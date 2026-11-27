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
    public int MaxConnections { get; init; } = 16;
    /// <summary>At most this many connections from one Windows session at a time.</summary>
    public int PerSessionLimit { get; init; } = 4;
    /// <summary>A connection that sends no complete request for this long is closed.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Whether a client came over the network (SMB), even through \localhost.</summary>
    public Func<NamedPipeServerStream, bool> IsRemote { get; init; } = NativeSessions.IsRemoteClient;
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

    /// <summary>One line for the editor's Background service window and the tray, as seen by the person at the console.</summary>
    public string StatusText => StatusFor(_o.ConsoleSession());

    /// <summary>
    /// The status as <paramref name="session"/> may see it. Other users only learn whether the display is in use and whether
    /// Windows is asleep; which session draws, the brightness and USB errors are for the person at the console (review N5).
    /// </summary>
    private string StatusFor(int? session)
    {
        string power = Power.Ended ? "stopping" : Power.Suspended ? "Windows is asleep" : "awake";
        int? driving; string? error; int brightness; bool open;
        lock (_gate) { driving = _driving; error = _lastError; brightness = _brightness; open = _device is not null; }
        if (session is null || session != _o.ConsoleSession()) return (open ? "display in use" : "display released") + "; " + power;
        var display = open ? $"showing session {driving}" : "display released";
        return $"{display}; brightness {brightness}; {power}" + (error is null ? "" : "; last error: " + error) + (_o.StatusNote is { } note ? "; " + note : "");
    }

    /// <summary>
    /// Creates the first pipe instance now, with the first-instance flag, so a name another program took is refused at once
    /// (review M1), and starts accepting clients.
    /// </summary>
    /// <exception cref="PipeNameTakenException">Another program serves the pipe name.</exception>
    public void Start()
    {
        _firstInstance = CreateInstance();
        _ = Task.Run(AcceptLoop);
    }

    private int _instances;   // our live pipe instances (waiting or connected); 0 means the name may have been lost

    private NamedPipeServerStream CreateInstance()
    {
        var security = _o.Security ?? ServicePipeSecurity.Build(WindowsIdentity.GetCurrent().User!);
        var server = ServicePipeFactory.Create(_o.PipeName, PipeDirection.InOut, _o.MaxConnections, security, first: Volatile.Read(ref _instances) == 0);
        Interlocked.Increment(ref _instances);
        return server;
    }

    private void Release(NamedPipeServerStream pipe)
    {
        try { pipe.Dispose(); } catch { /* best effort */ }
        Interlocked.Decrement(ref _instances);
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
                await next.WaitForConnectionAsync(token).ConfigureAwait(false);
                var connected = next;
                next = null;
                _ = Task.Run(() => Serve(connected, token));
            }
            catch (OperationCanceledException) { if (next is not null) Release(next); return; }
            catch (Exception ex)
            {
                if (next is not null) { Release(next); next = null; }
                Log("Display pipe error: " + ex.Message);
                try { await Task.Delay(1000, token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
        }
        if (next is not null) Release(next);
    }

    private readonly Dictionary<int, int> _perSession = new();

    private async Task Serve(NamedPipeServerStream pipe, CancellationToken token)
    {
        int? counted = null;
        try
        {
            // a client over the network path (SMB, also \localhost) gets a made-up session id from Windows: refuse it outright (L1)
            if (_o.IsRemote(pipe)) { Log("Refused a display client that came over the network"); return; }
            int? session = _o.ClientSession(pipe);
            int key = session ?? -1;
            lock (_perSession)
            {
                _perSession.TryGetValue(key, out var n);
                if (n >= _o.PerSessionLimit) { Log($"Session {key} has too many display connections open; closed one"); return; }   // M2
                _perSession[key] = n + 1;
                counted = key;
            }
            while (!token.IsCancellationRequested)
            {
                DisplayRequest? request;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    idle.CancelAfter(_o.IdleTimeout);   // M2: a connection that says nothing gives its pipe instance back
                    try { request = await DisplayWire.ReadRequestAsync(pipe, idle.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { return; }
                    catch (InvalidDataException ex)
                    {
                        await pipe.WriteAsync(DisplayWire.EncodeReply(ReplyStatus.Error, "Bad request: " + ex.Message), token).ConfigureAwait(false);
                        return;   // the stream is out of step now: close it
                    }
                }
                if (request is null) return;
                var (status, message) = Execute(request, session);
                await pipe.WriteAsync(DisplayWire.EncodeReply(status, message), token).ConfigureAwait(false);
                if (request.Op == DisplayOp.Hello && status != ReplyStatus.Ok) return;
            }
        }
        catch { /* the client left or the service is stopping */ }
        finally
        {
            if (counted is { } k) lock (_perSession) { if (_perSession.TryGetValue(k, out var n)) { if (n <= 1) _perSession.Remove(k); else _perSession[k] = n - 1; } }
            Release(pipe);
        }
    }

    private (ReplyStatus, string) Execute(DisplayRequest request, int? session)
    {
        if (DisplayAccess.Deny(request.Op, session, _o.ConsoleSession()) is { } why)
        {
            Log($"Refused {request.Op} from session {session?.ToString() ?? "unknown"}: {why}");   // rate-limited like every line here
            return (ReplyStatus.Denied, why);
        }
        switch (request.Op)
        {
            case DisplayOp.Status:
                return (ReplyStatus.Ok, StatusFor(session));

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
                        catch (Exception ex)
                        {
                            _lastError = ex.Message;
                            Log($"Could not open the display for session {session}: {ex.Message}");   // rate-limited: clients retry every few seconds
                            return (ReplyStatus.Error, ex.Message);
                        }
                    }
                    Exception? failed = null;
                    try { _device!.Blit(0, 0, DisplayWire.FrameWidth, DisplayWire.FrameHeight, request.Payload); }
                    catch (Exception ex) { failed = ex; }
                    // L2: a sleep or shutdown that came during this transfer could not take the device; blank it now, still within
                    // its budget, instead of leaving the owed blank to a tick that never comes before Windows sleeps
                    if (!Power.MayDrive)
                    {
                        _blankOwed = false;
                        CloseLocked(blank: failed is null);
                        Log("Blanked the display right after the transfer that held it (Windows is sleeping or shutting down)");
                        return (ReplyStatus.Error, Power.Ended ? "The display is off: Windows is shutting down." : "The display is off while Windows sleeps.");
                    }
                    if (failed is not null) return Failed(failed);
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

    private readonly LogLimiter _logLimiter = new(TimeSpan.FromMinutes(1));

    /// <summary>Repeated lines (a busy pipe, a client reconnecting in a loop) are written once a minute with a count (M2).</summary>
    private void Log(string message)
    {
        try { if (_logLimiter.Filter(message) is { } line) _o.Log?.Invoke(line); } catch { /* never */ }
    }
}
