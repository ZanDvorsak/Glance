namespace Glance.Core.Service;

/// <summary>Requests on the service's display pipe.</summary>
public enum DisplayOp : byte
{
    /// <summary>Payload: the client's protocol version (4 bytes, little endian). Checks access before the first frame.</summary>
    Hello = 1,
    /// <summary>Payload: one whole frame, RGB565 big endian, exactly <see cref="DisplayWire.FrameBytes"/> bytes.</summary>
    Frame = 2,
    /// <summary>Payload: one byte, 0-7.</summary>
    Brightness = 3,
    /// <summary>Black frame, backlight off, and release the device. No payload.</summary>
    Blank = 4,
    /// <summary>A one-line description of the service's display state. No payload.</summary>
    Status = 5,
}

/// <summary>
/// Who may do what on the display pipe. The display shows the screen of whoever sits at it: only a client in the active
/// console session may draw, change the brightness or blank it. Anyone allowed by the pipe's rules may ask for the status.
/// </summary>
public static class DisplayAccess
{
    /// <returns>Null when allowed, otherwise the reason sent back to the client.</returns>
    /// <param name="clientSession">The client's Windows session, or null if it could not be found out (then it is refused).</param>
    /// <param name="consoleSession">The active console session, or null when there is none (for example while it switches).</param>
    public static string? Deny(DisplayOp op, int? clientSession, int? consoleSession)
    {
        if (op == DisplayOp.Status) return null;
        if (op is not (DisplayOp.Hello or DisplayOp.Frame or DisplayOp.Brightness or DisplayOp.Blank)) return "Unknown request.";
        if (clientSession is not { } client) return "Glance could not tell which Windows session this program runs in, so it may not use the display.";
        if (client == 0) return "Programs in session 0 (services) may not draw on the display; only the user at the console may.";
        if (consoleSession is not { } console) return "Nobody is signed in at the console right now, so the display stays off.";
        if (client != console) return $"Another user is at the console. Only the session at the console (session {console}) may use the display, not session {client}.";
        return null;
    }
}
