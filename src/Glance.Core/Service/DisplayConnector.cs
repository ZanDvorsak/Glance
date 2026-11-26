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
    internal static IFrameDisplay Open() => Open(ServiceNames.DisplayPipe, null, () => Ax206Display.Open());

    /// <summary>
    /// Rules: a service that answers is used. No service pipe, or one that is not served by SYSTEM/Administrators (a squatter):
    /// open USB directly. A service that refuses (another session is using the display) or fails: throw, never fall back to
    /// USB, because the service holds the device.
    /// </summary>
    internal static IFrameDisplay Open(string pipeName, IEnumerable<SecurityIdentifier>? trustedOwners, Func<IFrameDisplay> usb)
    {
        try { return RemoteFrameDisplay.Connect(pipeName, trustedOwners); }
        catch (TimeoutException) { return usb(); }              // no service pipe: the service is not installed or not running
        catch (UnauthorizedAccessException) { return usb(); }   // not the service (or not ours to use): never send it frames
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
        catch (TimeoutException) { return Ax206Display.TryBlank(); }
        catch (UnauthorizedAccessException) { return Ax206Display.TryBlank(); }
        catch (Exception ex) { return ex.Message; }
    }
}
