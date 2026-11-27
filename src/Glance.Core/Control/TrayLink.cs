using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading.Channels;

namespace Glance.Core.Control;

/// <summary>
/// The editor's channel to its tray app's control pipe (review M5).
/// <list type="bullet">
/// <item><b>One connection, one queue:</b> commands are sent one after another, each after the reply to the one before, so a
/// <c>LIVE_CLEAR</c> can never overtake the <c>LIVE</c> before it, and <c>PAGE</c>/<c>BRIGHTNESS</c> arrive in the order the
/// user made them. (Before, every command went on its own connection from its own task.)</item>
/// <item><b>The connection is the editor:</b> the tray leases the live theme to it (<see cref="TrayLiveState"/>). When the editor
/// exits, crashes or is killed, Windows closes the pipe and the tray shows the saved theme again.</item>
/// <item><b>Bounded:</b> no answer within the reply timeout counts as no answer (null), and the connection is dropped so a late
/// reply can never be taken for the next command's. The next command connects again.</item>
/// <item>Same protection as <see cref="ControlPipeClient"/>: only a pipe this user serves (<c>CurrentUserOnly</c>), and the
/// server can never act as the editor (<c>Anonymous</c>).</item>
/// </list>
/// Thread safe. The pipe work runs on one background worker; <see cref="Send"/> and <see cref="Close"/> block, the rest does not.
/// </summary>
public sealed class TrayLink : IDisposable
{
    /// <summary>
    /// The tray can hold its lock for about 2 s while it reopens the display, and it still applies the command afterwards;
    /// giving up earlier would drop the connection (and with it the live theme) for nothing.
    /// </summary>
    public static readonly TimeSpan DefaultReplyTimeout = TimeSpan.FromMilliseconds(ControlPipeClient.LiveReplyTimeoutMs);

    private sealed record Item(ControlCommand Command, TaskCompletionSource<string?> Reply);

    private readonly string _pipeName;
    private readonly TimeSpan _replyTimeout;
    private readonly int _connectTimeoutMs;
    private readonly Channel<Item> _queue = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _worker;
    private NamedPipeClientStream? _pipe;   // only the worker touches these two
    private StreamReader? _reader;

    public TrayLink(string pipeName, TimeSpan? replyTimeout = null, int? connectTimeoutMs = null)
    {
        _pipeName = pipeName;
        _replyTimeout = replyTimeout ?? DefaultReplyTimeout;
        _connectTimeoutMs = connectTimeoutMs ?? ControlPipeClient.LiveConnectTimeoutMs;
        _worker = Task.Run(Work);
    }

    /// <summary>Queues a command behind the ones before it; the task gives the reply, or null when the tray app did not answer.</summary>
    public Task<string?> SendAsync(ControlCommand command)
    {
        var item = new Item(command, new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_queue.Writer.TryWrite(item)) item.Reply.TrySetResult(null);   // closed: nothing more goes out
        return item.Reply.Task;
    }

    /// <summary>Queues a command without waiting for the reply.</summary>
    public void Post(ControlCommand command) => _ = SendAsync(command);

    /// <summary>How long <see cref="Send"/> waits at most: one connect and one reply (review M3: never an unbounded wait).</summary>
    public TimeSpan SendTimeout => _replyTimeout + TimeSpan.FromMilliseconds(_connectTimeoutMs);

    /// <summary>
    /// Sends a command (in order) and waits for the reply, at most <see cref="SendTimeout"/>, also when commands queued before it
    /// are slow; then null (the command may still go out later). Blocks: never call this on the UI thread.
    /// </summary>
    public string? Send(ControlCommand command)
    {
        var reply = SendAsync(command);
        return reply.Wait(SendTimeout) ? reply.Result : null;   // sync-wait: Result only after the bounded Wait said it finished
    }

    /// <summary>
    /// Takes no more commands, delivers what is still queued within <paramref name="timeout"/>, then hangs up (which also
    /// ends any live theme this connection holds). Returns true when everything queued got an answer or a definite failure in time.
    /// </summary>
    public bool Close(TimeSpan timeout)
    {
        _queue.Writer.TryComplete();
        try { return _worker.Wait(timeout); }
        catch { return true; }   // the worker failed; there is nothing left to deliver
    }

    public void Dispose() => Close(TimeSpan.Zero);

    private async Task Work()
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                string? reply = null;
                try { reply = Exchange(item.Command); }
                finally { item.Reply.TrySetResult(reply); }
            }
        }
        finally { Drop(); }
    }

    /// <summary>One command, one reply line, on the open connection (or a new one).</summary>
    private string? Exchange(ControlCommand command)
    {
        var line = new UTF8Encoding(false).GetBytes(command.Encode() + "\n");
        for (int attempt = 0; attempt < 2; attempt++)
        {
            bool reused = _pipe is not null;
            try
            {
                if (_pipe is null) Connect();
                _pipe!.Write(line);
                using var giveUp = new CancellationTokenSource();
                var read = _reader!.ReadLineAsync(giveUp.Token).AsTask();
                if (!read.Wait(_replyTimeout))
                {
                    // L3: cancel the read first; while it is pending the handle cannot really close, and the tray would keep
                    // this connection (one of its four) until it answers
                    giveUp.Cancel();
                    Observe(read);
                    Drop();          // a late reply must never be read as the next command's
                    return null;
                }
                if (read.Result is { } reply) return reply;
                Drop();              // the tray closed this connection (it exited or restarted)
            }
            catch (Exception)
            {
                Drop();
            }
            // an old connection that broke since its last use: try once more on a new one; a new one that fails is the answer
            if (!reused) return null;
        }
        return null;
    }

    private void Connect()
    {
        // CurrentUserOnly: .NET checks that this user owns the server end, so another account that took the name gets nothing.
        // Anonymous: whoever serves the pipe can never act as this user (ImpersonateNamedPipeClient).
        // Asynchronous (L3): only then can a pending read be cancelled, so a dropped connection really closes at once.
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, ControlPipeClient.Options | PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
        try { pipe.Connect(_connectTimeoutMs); }
        catch { pipe.Dispose(); throw; }
        _pipe = pipe;
        _reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
    }

    private void Drop()
    {
        try { _reader?.Dispose(); } catch { /* best effort */ }
        try { _pipe?.Dispose(); } catch { /* best effort */ }
        _reader = null;
        _pipe = null;
    }

    /// <summary>A read given up on still finishes (with an error once the pipe is closed); observe it so it is not reported as unobserved.</summary>
    private static void Observe(Task task) => task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
}
