namespace Glance.Core.Diagnostics;

/// <summary>What the editor's crash handler should do with an exception it caught.</summary>
public enum CrashVerdict
{
    /// <summary>First time this error is seen: log it and tell the user (status bar).</summary>
    Notify,
    /// <summary>A repeat: log it, say nothing.</summary>
    Log,
    /// <summary>A repeat that was logged often enough already: do nothing.</summary>
    Quiet,
    /// <summary>The same error keeps coming (<see cref="CrashGuard.StormCount"/> in <see cref="CrashGuard.Window"/>): log that
    /// once and ask the user to save and close, since the editor is stuck in a failing loop. Given once per error.</summary>
    Storm,
}

/// <summary>
/// Decides how loudly to report exceptions the editor catches on its UI thread, so one failure is shown to the user once
/// and an error that repeats every frame neither floods the log nor goes unnoticed. Errors are told apart by type,
/// message and the top stack frame. Thread-safe.
/// </summary>
public sealed class CrashGuard
{
    public const int StormCount = 20;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    public const int MaxLoggedPerError = 5;

    private sealed class State { public readonly Queue<DateTime> Recent = new(); public int Seen, Logged; public bool Stormed; }
    private readonly Dictionary<string, State> _errors = new();
    private readonly object _gate = new();

    public CrashVerdict Note(Exception ex, DateTime now)
    {
        lock (_gate)
        {
            var key = Signature(ex);
            if (!_errors.TryGetValue(key, out var s)) _errors[key] = s = new State();
            s.Seen++;
            s.Recent.Enqueue(now);
            while (s.Recent.Count > 0 && now - s.Recent.Peek() > Window) s.Recent.Dequeue();

            if (!s.Stormed && s.Recent.Count >= StormCount) { s.Stormed = true; return CrashVerdict.Storm; }
            if (s.Logged >= MaxLoggedPerError) return CrashVerdict.Quiet;
            s.Logged++;
            return s.Seen == 1 ? CrashVerdict.Notify : CrashVerdict.Log;
        }
    }

    /// <summary>Type, message and the first line of the stack trace: the same failure in the same place.</summary>
    public static string Signature(Exception ex)
    {
        var top = ex.StackTrace?.Split('\n', 2)[0].Trim() ?? "";
        return $"{ex.GetType().FullName}|{ex.Message}|{top}";
    }
}
