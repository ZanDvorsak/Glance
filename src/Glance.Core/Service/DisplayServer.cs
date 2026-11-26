using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using Glance.Core.Display;
using Glance.Core.Usb;

namespace Glance.Core.Service;

/// <summary>Settings for <see cref="DisplayServer"/>. Tests replace the pipe name, the rules, the session lookups and the clock.</summary>
public sealed class DisplayServerOptions
{
    public string PipeName { get; init; } = ServiceNames.DisplayPipe;
    /// <summary>The pipe's rules; null builds <see cref="ServicePipeSecurity.Build"/> for the account this process runs as.</summary>
    public PipeSecurity? Security { get; init; }
    public Func<NamedPipeServerStream, int?> ClientSession { get; init; } = NativeSessions.ClientSessionOf;
    public Func<int?> ConsoleSession { get; init; } = NativeSessions.ActiveConsoleSession;
    public Action<string>? Log { get; init; }
    /// <summary>No frame for this long: the client is gone, so blank and release the device. Clients refresh at least every 10 s.</summary>
    public TimeSpan IdleBlankAfter { get; init; } = TimeSpan.FromSeconds(20);
    public Func<long> NowMs { get; init; } = () => Environment.TickCount64;
    /// <summary>How many clients may be connected at once (tray, editor, status queries).</summary>
    public int MaxConnections { get; init; } = 8;
    /// <summary>Added to <see cref="DisplayServer.StatusText"/>, for example why the service log is not written.</summary>
    public string? StatusNote { get; init; }
}

/// <summary>
/// The service side of the display: owns the USB device, takes finished frames from the user's tray or editor over
/// <see cref="ServiceNames.DisplayPipe"/>, and blanks the display for shutdown, sleep, sign-out and idle clients.
/// It never reads a theme or any file a user chose; the only input is fixed-size frames and a few small commands.
/// <para>
/// One lock (<c>_gate</c>) guards the device; requests are served one at a time. A power event waits for that lock only within
/// its budget; if a transfer holds it longer, the blank is owed and paid by the next request or <see cref="Tick"/>.
/// </para>
/// </summary>
public sealed class DisplayServer : IDisposable
{
    private readonly Func<IFrameDisplay> _open;
    private readonly DisplayServerOptions _o;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private IFrameDisplay? _device;
    private int _brightness = 7;
    private long _lastFrameMs;
    private int? _driving;
    private string? _lastError;
    private volatile bool _blankOwed;
    private int _disposed;
    private NamedPipeServerStream? _firstInstance;

    /// <summary>The real display over USB.</summary>
    public static DisplayServer ForUsb(DisplayServerOptions options) => new(() => Ax206Display.Open(), options);

    internal DisplayServer(Func<IFrameDisplay> open, DisplayServerOptions options) { _open = open; _o = options; }

    public DisplayPowerPolicy Power { get; } = new();

    public bool DeviceOpen => Volatile.Read(ref _device) is not null;

    /// <summary>The session whose frames are on the display, or null.</summary>
    public int? DrivingSession { get { lock (_gate) return _driving; } }

    /// <summary>One line for the editor's Background service window and the tray.</summary>
    public string StatusText
    {
        get
        {
            string power = Power.Ended ? "stopping" : Power.Suspended ? "Windows is asleep" : "awake";
            int? driving; string? error; int brightness; bool open;
            lock (_gate) { driving = _driving; error = _lastError; brightness = _brightness; open = _device is not null; }
            var display = open ? $"showing session {driving}" : "display released";
            return $"{display}; brightness {brightness}; {power}" + (error is null ? "" : "; last error: " + error) + (_o.StatusNote is { } note ? "; " + note : "");
        }
    }

    /// <summary>
    /// Creates the first pipe instance now (so a squatted name is noticed at once) and starts accepting clients.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">Another process already owns the pipe name.</exception>
    public void Start()
    {
        _firstInstance = CreateInstance();
        _ = Task.Run(AcceptLoop);
    }

    private NamedPipeServerStream CreateInstance()
    {
        var security = _o.Security ?? ServicePipeSecurity.Build(WindowsIdentity.GetCurrent().User!);
        return NamedPipeServerStreamAcl.Create(_o.PipeName, PipeDirection.InOut, _o.MaxConnections, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
    }

    private async Task AcceptLoop()
    {
        var token = _cts.Token;
        var next = _firstInstance;
        while (!token.IsCancellationRequested)
        {
            try
            {
                next ??= CreateInstance();
                await next.WaitForConnectionAsync(token);
                var connected = next;
                next = null;
                _ = Task.Run(() => Serve(connected, token));
            }
            catch (OperationCanceledException) { next?.Dispose(); return; }
            catch (Exception ex)
            {
                next?.Dispose(); next = null;
                Log("Display pipe error: " + ex.Message);
                try { await Task.Delay(1000, token); } catch (OperationCanceledException) { return; }
            }
        }
        next?.Dispose();
    }

    private async Task Serve(NamedPipeServerStream pipe, CancellationToken token)
    {
        try
        {
            int? session = _o.ClientSession(pipe);
            while (!token.IsCancellationRequested)
            {
                DisplayRequest? request;
                try { request = await DisplayWire.ReadRequestAsync(pipe, token); }
                catch (InvalidDataException ex)
                {
                    await pipe.WriteAsync(DisplayWire.EncodeReply(ReplyStatus.Error, "Bad request: " + ex.Message), token);
                    return;   // the stream is out of step now: close it
                }
                if (request is null) return;
                var (status, message) = Execute(request, session);
                await pipe.WriteAsync(DisplayWire.EncodeReply(status, message), token);
                if (request.Op == DisplayOp.Hello && status != ReplyStatus.Ok) return;
            }
        }
        catch { /* the client left or the service is stopping */ }
        finally { pipe.Dispose(); }
    }

    private (ReplyStatus, string) Execute(DisplayRequest request, int? session)
    {
        if (DisplayAccess.Deny(request.Op, session, _o.ConsoleSession()) is { } why) return (ReplyStatus.Denied, why);
        switch (request.Op)
        {
            case DisplayOp.Status:
                return (ReplyStatus.Ok, StatusText);

            case DisplayOp.Hello:
                int version = BinaryPrimitives.ReadInt32LittleEndian(request.Payload);
                return version == DisplayWire.Version
                    ? (ReplyStatus.Ok, $"{DisplayWire.FrameWidth}x{DisplayWire.FrameHeight}")
                    : (ReplyStatus.Error, $"This Glance service speaks version {DisplayWire.Version}, the app version {version}. Install the same Glance version for both.");

            case DisplayOp.Brightness:
                lock (_gate)
                {
                    _brightness = Math.Clamp((int)request.Payload[0], 0, 7);
                    if (_device is null || !Power.MayDrive) return (ReplyStatus.Ok, "");   // applied when the display opens
                    try { _device.SetBrightness(_brightness); return (ReplyStatus.Ok, ""); }
                    catch (Exception ex) { return Failed(ex); }
                }

            case DisplayOp.Blank:
                lock (_gate)
                {
                    if (BlankAndCloseLocked()) Log($"Session {session} turned the display off");
                    return (ReplyStatus.Ok, "");
                }

            case DisplayOp.Frame:
                lock (_gate)
                {
                    if (!Power.MayDrive)
                    {
                        PayOwedBlankLocked();
                        return (ReplyStatus.Error, Power.Ended ? "The display is off: Windows is shutting down." : "The display is off while Windows sleeps.");
                    }
                    if (_device is null)
                    {
                        try
                        {
                            var d = _open();
                            try { d.SetBrightness(_brightness); }
                            catch { d.Dispose(); throw; }
                            Volatile.Write(ref _device, d);
                            Log($"Display opened for session {session}");
                        }
                        catch (Exception ex) { _lastError = ex.Message; return (ReplyStatus.Error, ex.Message); }
                    }
                    try { _device!.Blit(0, 0, DisplayWire.FrameWidth, DisplayWire.FrameHeight, request.Payload); }
                    catch (Exception ex) { return Failed(ex); }
                    _lastFrameMs = _o.NowMs();
                    _driving = session;
                    _lastError = null;
                    return (ReplyStatus.Ok, "");
                }

            default:
                return (ReplyStatus.Error, "Unknown request.");
        }
    }

    /// <summary>Under the lock: a USB transfer failed. Close the device (a stalled one cannot be blanked) so the next frame opens it fresh.</summary>
    private (ReplyStatus, string) Failed(Exception ex)
    {
        _lastError = ex.Message;
        Log("Display transfer failed; closing it: " + ex.Message);
        CloseLocked(blank: false);
        return (ReplyStatus.Error, ex.Message);
    }

    /// <summary>Under the lock. Returns true if a display was open.</summary>
    private bool BlankAndCloseLocked() => CloseLocked(blank: true);

    private bool CloseLocked(bool blank)
    {
        var d = _device;
        if (d is null) return false;
        if (blank) { try { d.Blank(); } catch { /* best effort */ } }
        try { d.Dispose(); } catch { /* best effort */ }
        Volatile.Write(ref _device, null);
        _driving = null;
        return true;
    }

    private void PayOwedBlankLocked()
    {
        if (!_blankOwed) return;
        _blankOwed = false;
        if (BlankAndCloseLocked()) Log("Blanked the display after the transfer that held it finished");
    }

    /// <summary>A Windows power event (from the service's control handler). Bounded by <see cref="PowerBlank.OwnerBudgetFor"/>.</summary>
    public PowerBlank.Result HandlePower(PowerEvent e)
    {
        var action = Power.Handle(e);   // from now on no frame is drawn (checked under the lock)
        Log($"Windows {e}: {action}");
        if (action is not (PowerAction.BlankAndStop or PowerAction.BlankAndHold)) return PowerBlank.Result.NothingOpen;
        if (!Monitor.TryEnter(_gate, PowerBlank.OwnerBudgetFor(e) / 3))
        {
            _blankOwed = true;   // a transfer holds the device; the next request or tick blanks it
            Log($"Windows {e}: the display is busy; it is blanked as soon as the transfer finishes");
            return PowerBlank.Result.Busy;
        }
        try { return BlankAndCloseLocked() ? PowerBlank.Result.Blanked : PowerBlank.Result.NothingOpen; }
        finally { Monitor.Exit(_gate); }
    }

    /// <summary>A session signed out or left the console: blank now if its frames are on the display.</summary>
    public void SessionLeft(int sessionId)
    {
        if (!Monitor.TryEnter(_gate, PowerBlank.SleepBudget))
        {
            _blankOwed = true;
            return;
        }
        try
        {
            if (_driving == sessionId && BlankAndCloseLocked()) Log($"Session {sessionId} left the console; display off");
        }
        finally { Monitor.Exit(_gate); }
    }

    /// <summary>The service's 2 s supervisor tick: idle blank, a blank a power event could not do in time, a missed wake.</summary>
    public void Tick()
    {
        if (Monitor.TryEnter(_gate, 100))
        {
            try
            {
                PayOwedBlankLocked();
                if (_device is not null && _o.NowMs() - _lastFrameMs > (long)_o.IdleBlankAfter.TotalMilliseconds)
                {
                    Log($"No frame from session {_driving} for {_o.IdleBlankAfter.TotalSeconds:0} s; display off");
                    BlankAndCloseLocked();
                }
            }
            finally { Monitor.Exit(_gate); }
        }
        if (Power.NoteAwakeTick() == PowerAction.Reopen) Log("No wake-up notice from Windows after 30 s awake; frames are accepted again");
    }

    /// <summary>Stops serving and blanks the display once (the service is stopping).</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _cts.Cancel(); } catch { /* already */ }
        if (HandlePower(PowerEvent.Quit) == PowerBlank.Result.Busy && Monitor.TryEnter(_gate, PowerBlank.SessionEndBudget))
        {
            try { PayOwedBlankLocked(); } finally { Monitor.Exit(_gate); }
        }
    }

    private void Log(string message) { try { _o.Log?.Invoke(message); } catch { /* never */ } }
}
