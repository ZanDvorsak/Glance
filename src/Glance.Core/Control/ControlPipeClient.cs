using System.IO.Pipes;
using System.Text;

namespace Glance.Core.Control;

/// <summary>Sends one <see cref="ControlCommand"/> over the tray app's control pipe and waits for the one-line reply.</summary>
public static class ControlPipeClient
{
    /// <summary>How long to wait for the runner to accept the connection.</summary>
    public const int ConnectTimeoutMs = 800;

    /// <summary>How long to wait for the reply to a short command (the editor calls some of these from the UI thread).</summary>
    public const int ReplyTimeoutMs = 800;

    /// <summary>
    /// LIVE gets a longer reply timeout than its connect timeout: the runner may hold its lock for about 2 s while it
    /// reopens the display, and it still applies the theme afterwards. Giving up earlier reported a failure that was not one.
    /// </summary>
    public const int LiveConnectTimeoutMs = 1000, LiveReplyTimeoutMs = 5000;

    /// <summary>Returns the reply, or null if the runner could not be reached or did not answer in time.</summary>
    public static string? Send(string pipeName, ControlCommand command) =>
        command.Verb == ControlVerb.Live
            ? Send(pipeName, command.Encode(), LiveConnectTimeoutMs, LiveReplyTimeoutMs)
            : Send(pipeName, command.Encode(), ConnectTimeoutMs, ReplyTimeoutMs);

    private static string? Send(string pipeName, string line, int connectTimeoutMs, int replyTimeoutMs)
    {
        try
        {
            // CurrentUserOnly: .NET checks that this user owns the server end, so another account that took the name gets nothing.
            // Anonymous: whoever serves the pipe can never act as this user (ImpersonateNamedPipeClient).
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly, System.Security.Principal.TokenImpersonationLevel.Anonymous);
            pipe.Connect(connectTimeoutMs);
            // No StreamWriter, and the reader leaves the pipe open: disposing a StreamWriter flushes the pipe, which throws once
            // the pipe is closed (by the reader) or broken (by the runner), and the catch below then turned every reply into null.
            pipe.Write(new UTF8Encoding(false).GetBytes(line + "\n"));
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            var task = reader.ReadLineAsync();
            return task.Wait(replyTimeoutMs) ? task.Result : null;
        }
        catch { return null; }
    }
}
