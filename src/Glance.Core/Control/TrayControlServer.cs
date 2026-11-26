using System.IO.Pipes;
using System.Text;
using Glance.Core.Service;

namespace Glance.Core.Control;

/// <summary>
/// The server end of the tray app's control pipe (<see cref="ServiceNames.TrayControlPipe"/>), moved out of the tray app so it
/// can be tested over real pipes. One line per command, one line per reply; a connection may carry many commands, which are
/// handled one after another in the order they arrive.
/// <list type="bullet">
/// <item>The pipe only admits its own user and denies the network (<see cref="ServicePipeSecurity.CurrentUserOnly"/>).</item>
/// <item>The name is claimed with the first-instance flag, so a name another account took first is refused, never joined (review L8).</item>
/// <item>A client that came through the network redirector (also <c>\\localhost</c>) is closed before a byte is read (review L1).</item>
/// <item>Each connection gets a number; <c>closed</c> is called with it when the client hangs up or goes away (review M5: the
/// live theme belongs to the connection that sent it).</item>
/// </list>
/// </summary>
public sealed class TrayControlServer : IDisposable
{
    /// <summary>At most this many connections at once (the editor keeps one open; short ones come and go).</summary>
    public const int MaxInstances = 4;

    private readonly string _name;
    private readonly Func<string, long, string> _execute;
    private readonly Action<long>? _closed;
    private readonly Action<string> _log;
    private readonly PipeSecurity _security;
    private readonly CancellationTokenSource _cts = new();
    private readonly LogLimiter _logLimiter = new(TimeSpan.FromMinutes(1));
    private int _instances;        // live instances of the pipe; 0 means the name must be claimed (first instance) again
    private long _nextConnection;

    /// <param name="execute">Runs one command line for the connection with the given number and returns the reply line.</param>
    /// <param name="closed">Called once per connection when it ends (on a pipe thread).</param>
    /// <param name="security">Tests pass their own rules; the default admits only the current user.</param>
    public TrayControlServer(string pipeName, Func<string, long, string> execute, Action<long>? closed, Action<string> log, PipeSecurity? security = null)
    {
        _name = pipeName;
        _execute = execute;
        _closed = closed;
        _log = log;
        _security = security ?? ServicePipeSecurity.CurrentUserOnly();
        _ = Task.Run(Loop);
    }

    public void Dispose() => _cts.Cancel();

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = ServicePipeFactory.Create(_name, PipeDirection.InOut, MaxInstances, _security, first: Volatile.Read(ref _instances) == 0);
                Interlocked.Increment(ref _instances);
                await server.WaitForConnectionAsync(_cts.Token);
                var connected = server;
                server = null;
                _ = Task.Run(() => Handle(connected));
            }
            catch (OperationCanceledException) { Release(server); return; }
            catch (Exception ex)
            {
                Release(server);
                if (_logLimiter.Filter("Control pipe error: " + ex.Message) is { } line) _log(line);   // not once a second for ever
                try { await Task.Delay(2000, _cts.Token); } catch { return; }
            }
        }
    }

    private void Release(NamedPipeServerStream? pipe)
    {
        if (pipe is null) return;
        try { pipe.Dispose(); } catch { /* best effort */ }
        Interlocked.Decrement(ref _instances);
    }

    private async Task Handle(NamedPipeServerStream pipe)
    {
        long id = Interlocked.Increment(ref _nextConnection);
        try
        {
            if (NativeSessions.IsRemoteClient(pipe)) return;   // L1: never from another PC, not even through \localhost
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            while (await reader.ReadLineAsync(_cts.Token) is { } line)
            {
                string reply;
                try { reply = _execute(line.Trim(), id); } catch (Exception ex) { reply = "ERR " + ex.Message; }
                await writer.WriteLineAsync(reply);
            }
        }
        catch { /* client left */ }
        finally
        {
            Release(pipe);
            try { _closed?.Invoke(id); } catch (Exception ex) { _log("Control pipe: " + ex.Message); }
        }
    }
}
