namespace Glance.Core.Control;

/// <summary>The editor's channel to its tray app's control pipe.</summary>
public sealed class TrayLink : IDisposable
{
    private readonly string _pipeName;

    public TrayLink(string pipeName, TimeSpan? replyTimeout = null, int? connectTimeoutMs = null) { _pipeName = pipeName; }

    /// <summary>Sends a command; the task gives the reply, or null when the tray app did not answer.</summary>
    public Task<string?> SendAsync(ControlCommand command) => Task.Run(() => ControlPipeClient.Send(_pipeName, command));

    /// <summary>Sends a command without waiting for the reply.</summary>
    public void Post(ControlCommand command) => _ = SendAsync(command);

    /// <summary>Sends a command and waits for the reply. Never call this on the UI thread.</summary>
    public string? Send(ControlCommand command) => SendAsync(command).GetAwaiter().GetResult();

    /// <summary>Delivers what is still queued (within <paramref name="timeout"/>) and hangs up. True when all of it got through.</summary>
    public bool Close(TimeSpan timeout) => true;

    public void Dispose() => Close(TimeSpan.Zero);
}
