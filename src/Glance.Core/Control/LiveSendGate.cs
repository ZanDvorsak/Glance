namespace Glance.Core.Control;

/// <summary>One live send handed out by <see cref="LiveSendGate.TryBegin"/>; pass it back to <see cref="LiveSendGate.Complete"/>.</summary>
public sealed record LiveSend(string Snapshot, int Generation);

/// <summary>
/// Decides when the editor sends its live theme to the runner and what the status bar says about it.
/// <list type="bullet">
/// <item>Only one send is in flight at a time, and the next one always carries the newest snapshot, so an older edit can never arrive last.</item>
/// <item>A snapshot counts as sent only when the runner answered <c>OK</c>; until then every tick retries it.</item>
/// <item>"No answer" and "rejected" get different messages; after a failure, the next success restores the normal status.</item>
/// </list>
/// Not thread safe: call it from one thread (the editor's UI thread).
/// </summary>
public sealed class LiveSendGate
{
    public const string ShowingText = "Showing your edits on the display through the background runner.";
    public const string NoAnswerText = "The background runner did not answer in time, so the display may still show the old theme. Glance keeps trying.";
    public const string RejectedPrefix = "The background runner rejected your edits";
    public const string RestartHint = "If you just updated Glance, restart the background runner.";

    private string? _sent;          // the last snapshot the runner accepted
    private LiveSend? _inFlight;
    private string? _warning;       // the warning currently shown, if any
    private int _generation;

    /// <summary>Forgets everything; call when live sending starts or stops. A send still on its way is then ignored.</summary>
    public void Reset()
    {
        _sent = null; _inFlight = null; _warning = null;
        _generation++;
    }

    /// <summary>Returns the send to start now, or null when the snapshot is already on the display or another send is still on its way.</summary>
    public LiveSend? TryBegin(string snapshot)
    {
        if (_inFlight is not null || snapshot == _sent) return null;
        return _inFlight = new LiveSend(snapshot, _generation);
    }

    /// <summary>
    /// Records the runner's reply (null when it could not be reached or did not answer in time).
    /// Returns the new status text, or null to leave the status bar alone.
    /// </summary>
    public string? Complete(LiveSend send, string? reply)
    {
        if (send.Generation != _generation || !ReferenceEquals(send, _inFlight)) return null;   // from before a Reset
        _inFlight = null;

        if (reply is { } r && r.StartsWith("OK", StringComparison.Ordinal))
        {
            _sent = send.Snapshot;
            if (_warning is null) return null;
            _warning = null;
            return ShowingText;
        }

        // _sent is left alone, so the next TryBegin sends the (newest) snapshot again
        var text = reply is null ? NoAnswerText : RejectedText(reply);
        if (text == _warning) return null;      // same failure again: do not keep rewriting the status bar
        _warning = text;
        return text;
    }

    private static string RejectedText(string reply)
    {
        var reason = reply.StartsWith("ERR ", StringComparison.Ordinal) ? reply[4..].Trim() : reply.Trim();
        return $"{RejectedPrefix} ({reason}). {RestartHint}";
    }
}
