using System.Security.Principal;
using Glance.Core.Display;
using Glance.Core.Usb;

namespace Glance.Core.Service;

/// <summary>
/// Opens "the display" for a user-side process (tray or editor): through the Glance service when it runs, otherwise the USB
/// device directly (portable use, or the service stopped). A user process never needs administrator rights for either.
/// </summary>
public static class DisplayConnector
{
    /// <summary>The service first, then USB. Used by <see cref="DisplayPusher"/>'s public constructor.</summary>
    internal static IFrameDisplay Open() => Open(ServiceNames.DisplayPipe, null, () => Ax206Display.Open(), () => ServiceControl.Query());

    /// <summary>
    /// Rules (review L3):
    /// <list type="bullet">
    /// <item>A service that answers is used.</item>
    /// <item>No answer, or a pipe not served by SYSTEM/Administrators: open USB directly only when the Glance service is not
    /// installed or is stopped. When Windows says it is running or starting, it is busy or not ready yet: throw, and the caller
    /// tries again, instead of taking the device away from the service.</item>
    /// <item>A service that refuses (another session is using the display) or fails: throw, never fall back to USB.</item>
    /// </list>
    /// </summary>
    internal static IFrameDisplay Open(string pipeName, IEnumerable<SecurityIdentifier>? trustedOwners, Func<IFrameDisplay> usb)
        => Open(pipeName, trustedOwners, usb, () => ServiceState.NotInstalled);

    internal static IFrameDisplay Open(string pipeName, IEnumerable<SecurityIdentifier>? trustedOwners, Func<IFrameDisplay> usb, Func<ServiceState> serviceState)
    {
        Exception noAnswer;
        try { return RemoteFrameDisplay.Connect(pipeName, trustedOwners); }
        catch (TimeoutException ex) { noAnswer = ex; }              // no pipe, every instance busy, or the service still starting
        catch (UnauthorizedAccessException ex) { noAnswer = ex; }    // not the service: never send it frames
        ServiceState state;
        try { state = serviceState(); } catch { state = ServiceState.Unknown; }
        if (state is ServiceState.Running or ServiceState.Starting)
            throw new DisplayServiceException("The Glance service is running but did not answer yet (" + noAnswer.Message.TrimEnd('.') + "); trying again.", denied: false);
        return usb();
    }

    /// <summary>Whether the Glance service answers on its display pipe (served by SYSTEM or Administrators).</summary>
    public static bool ServiceAnswers() => ServiceStatus() is not null;

    /// <summary>The service's one-line display status, or null when it does not run (or the pipe is not the service's).</summary>
    public static string? ServiceStatus() => RemoteFrameDisplay.QueryStatus(ServiceNames.DisplayPipe);

    /// <summary>Turns the display black right now: through the service when it runs, else directly. Returns an error message, or null.</summary>
    public static string? TryBlank()
    {
        try
        {
            using var remote = RemoteFrameDisplay.Connect(ServiceNames.DisplayPipe);
            remote.BlankOrThrow();
            return null;
        }
        catch (Exception ex) when (ex is TimeoutException or UnauthorizedAccessException)
        {
            // only take the device ourselves when no Glance service owns it
            return ServiceControl.Query() is ServiceState.Running or ServiceState.Starting
                ? "The Glance service did not answer; try again in a moment." : Ax206Display.TryBlank();
        }
        catch (Exception ex) { return ex.Message; }
    }
}
