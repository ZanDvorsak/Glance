using Glance.Core.Display;

namespace Glance.Core.Service;

public enum ServiceSignalKind
{
    /// <summary>Nothing for the display to do.</summary>
    None,
    /// <summary>A <see cref="PowerEvent"/> for <see cref="DisplayPowerPolicy"/>.</summary>
    Power,
    /// <summary>A Windows session signed out or left the console; blank if it was the one drawing.</summary>
    SessionLeft,
}

public readonly record struct ServiceSignal(ServiceSignalKind Kind, PowerEvent Power = default, int SessionId = 0)
{
    public static readonly ServiceSignal None = new(ServiceSignalKind.None);
}

/// <summary>
/// Turns the control requests Windows sends a service (<c>HandlerEx</c>) into display events. Pure, so the mapping is tested
/// without installing a service.
/// </summary>
public static class ServiceControlMap
{
    // control codes (winsvc.h)
    public const int Stop = 0x1, Interrogate = 0x4, Shutdown = 0x5, PowerEventControl = 0xD, SessionChange = 0xE, Preshutdown = 0xF;
    // accepted-controls bits
    public const int AcceptStop = 0x1, AcceptShutdown = 0x4, AcceptPowerEvent = 0x40, AcceptSessionChange = 0x80, AcceptPreshutdown = 0x100;
    // WM_WTSSESSION_CHANGE event types
    public const int WtsConsoleConnect = 0x1, WtsConsoleDisconnect = 0x2, WtsRemoteConnect = 0x3, WtsRemoteDisconnect = 0x4,
        WtsSessionLogon = 0x5, WtsSessionLogoff = 0x6, WtsSessionLock = 0x7, WtsSessionUnlock = 0x8;

    /// <summary>What the service tells Windows it handles. Pre-shutdown instead of shutdown: it comes earlier and has its own timeout.</summary>
    public const int AcceptedControls = AcceptStop | AcceptPreshutdown | AcceptPowerEvent | AcceptSessionChange;

    /// <param name="control">The control code.</param>
    /// <param name="eventType">For power events the PBT_ code, for session changes the WTS_ code.</param>
    /// <param name="sessionId">For session changes: the session it is about.</param>
    public static ServiceSignal Map(int control, int eventType, int sessionId)
    {
        switch (control)
        {
            // the PC shuts down or restarts: for the service this is the end, never reopen
            case Preshutdown:
            case Shutdown: return new ServiceSignal(ServiceSignalKind.Power, PowerEvent.SessionEnding);
            case Stop: return new ServiceSignal(ServiceSignalKind.Power, PowerEvent.Quit);
            case PowerEventControl:
                return eventType switch
                {
                    PowerMessages.PBT_APMSUSPEND => new ServiceSignal(ServiceSignalKind.Power, PowerEvent.Suspend),
                    PowerMessages.PBT_APMRESUMEAUTOMATIC or PowerMessages.PBT_APMRESUMESUSPEND => new ServiceSignal(ServiceSignalKind.Power, PowerEvent.Resume),
                    _ => ServiceSignal.None,
                };
            case SessionChange:
                // a user signing out is not the PC shutting down: blank for that session, keep serving the next one
                return eventType is WtsSessionLogoff or WtsConsoleDisconnect
                    ? new ServiceSignal(ServiceSignalKind.SessionLeft, SessionId: sessionId)
                    : ServiceSignal.None;
            default: return ServiceSignal.None;
        }
    }
}
