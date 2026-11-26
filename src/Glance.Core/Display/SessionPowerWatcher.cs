using System.Runtime.InteropServices;

namespace Glance.Core.Display;

/// <summary>Maps the Windows messages about shutdown, sign-out, sleep and wake to <see cref="PowerEvent"/>s. Pure, for testing.</summary>
public static class PowerMessages
{
    public const uint WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016, WM_POWERBROADCAST = 0x0218;
    public const int PBT_APMSUSPEND = 0x4, PBT_APMRESUMECRITICAL = 0x6, PBT_APMRESUMESUSPEND = 0x7, PBT_APMRESUMEAUTOMATIC = 0x12;

    public static PowerEvent? Translate(uint msg, nint wParam)
    {
        switch (msg)
        {
            // WM_ENDSESSION with TRUE: the session really ends (shutdown, restart, sign-out, or an installer closing the app).
            // FALSE means a shutdown was cancelled after WM_QUERYENDSESSION; nothing was done then, so nothing to undo.
            case WM_ENDSESSION: return wParam != 0 ? PowerEvent.SessionEnding : null;
            case WM_POWERBROADCAST:
                return (int)wParam switch
                {
                    PBT_APMSUSPEND => PowerEvent.Suspend,
                    PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND or PBT_APMRESUMECRITICAL => PowerEvent.Resume,
                    _ => null,
                };
            default: return null;
        }
    }
}

/// <summary>
/// Tells its owner when Windows shuts down, restarts, signs out, sleeps or wakes, early enough to turn the display off.
/// <para>
/// It creates a hidden top-level window on its own thread with its own message loop. That works the same for the runner
/// (a WinForms tray <c>ApplicationContext</c> with no window of its own) and the Avalonia editor, and it does not depend on the
/// UI thread being free. A top-level window is required: message-only windows do not receive these broadcasts.
/// </para>
/// <para>
/// The handler runs synchronously on the watcher's thread, before the window answers the message. Windows does not end the
/// process until WM_ENDSESSION has been answered (and gives sleep about two seconds after PBT_APMSUSPEND), so the display can be
/// blanked inside the handler. During a session end a shutdown block reason is set while the handler runs, so if blanking is
/// slow Windows shows "Turning off the display" instead of killing the process mid-transfer; it is removed right after.
/// </para>
/// </summary>
public sealed class SessionPowerWatcher : IDisposable
{
    private readonly Action<PowerEvent> _handler;
    private readonly Action<string>? _log;
    private readonly string _reason;
    private readonly string _className = "Glance.PowerWatcher." + Guid.NewGuid().ToString("N");
    private readonly WndProcDelegate _wndProc;   // kept in a field so the GC cannot collect it while Windows calls it
    private readonly Thread _thread;
    private volatile IntPtr _hwnd;

    /// <summary>The hidden window (for tests and diagnostics).</summary>
    public IntPtr WindowHandle => _hwnd;

    /// <param name="handler">Called on the watcher's thread; Windows waits for it to return. Must not wait on the UI thread.</param>
    /// <param name="reason">Shown by Windows if the session end has to wait for the handler.</param>
    /// <param name="log">Optional diagnostics.</param>
    public SessionPowerWatcher(Action<PowerEvent> handler, string reason = "Turning off the Glance display", Action<string>? log = null)
    {
        _handler = handler; _reason = reason; _log = log;
        _wndProc = WndProc;
        using var ready = new ManualResetEventSlim();
        Exception? failure = null;
        _thread = new Thread(() =>
        {
            try { _hwnd = CreateHiddenWindow(); }
            catch (Exception ex) { failure = ex; }
            finally { ready.Set(); }
            if (failure is null) Pump();
        }) { IsBackground = true, Name = "Glance session/power watcher" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        if (failure is not null) throw failure;
    }

    private IntPtr CreateHiddenWindow()
    {
        var hInstance = GetModuleHandleW(null);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = _className,
        };
        if (RegisterClassExW(ref wc) == 0) throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastPInvokeError());
        // No parent and no WS_VISIBLE: a hidden top-level window (WS_EX_TOOLWINDOW keeps it out of Alt+Tab even if shown).
        var hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, _className, "Glance session watcher", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastPInvokeError());
        return hwnd;
    }

    private void Pump()
    {
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
        try { UnregisterClassW(_className, GetModuleHandleW(null)); } catch { /* best effort */ }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case PowerMessages.WM_QUERYENDSESSION:
                    return 1; // never block shutdown; the work happens in WM_ENDSESSION when the end is certain
                case PowerMessages.WM_ENDSESSION:
                case PowerMessages.WM_POWERBROADCAST:
                    if (PowerMessages.Translate(msg, wParam) is { } e) Raise(hwnd, e);
                    return msg == PowerMessages.WM_POWERBROADCAST ? 1 : 0;
                case WM_CLOSE:
                    DestroyWindow(hwnd);
                    return 0;
                case WM_DESTROY:
                    _hwnd = IntPtr.Zero;
                    PostQuitMessage(0);
                    return 0;
            }
        }
        catch (Exception ex) { _log?.Invoke("Session watcher error: " + ex.Message); } // an exception must never escape into Windows
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void Raise(IntPtr hwnd, PowerEvent e)
    {
        bool blocking = e == PowerEvent.SessionEnding && ShutdownBlockReasonCreate(hwnd, _reason);
        try { _handler(e); }
        catch (Exception ex) { _log?.Invoke($"Handling {e} failed: {ex.Message}"); }
        finally { if (blocking) ShutdownBlockReasonDestroy(hwnd); }
    }

    public void Dispose()
    {
        var hwnd = _hwnd;
        if (hwnd != IntPtr.Zero) PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != _thread) _thread.Join(1000);
    }

    // ---- Win32 ----------------------------------------------------------------------------------------------------

    private const uint WM_CLOSE = 0x0010, WM_DESTROY = 0x0002, WS_EX_TOOLWINDOW = 0x00000080;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; public uint lPrivate; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string className, IntPtr hInstance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool ShutdownBlockReasonCreate(IntPtr hwnd, string reason);
    [DllImport("user32.dll")] private static extern bool ShutdownBlockReasonDestroy(IntPtr hwnd);
}
