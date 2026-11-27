namespace Glance.Core.Service;

/// <summary>
/// Keeps a repeated log line from being written over and over (for example "All pipe instances are busy" once a second):
/// the first one is written, repeats inside the window are counted, and the next one after the window says how many were
/// left out (review M2). Thread-safe.
/// </summary>
public sealed class LogLimiter
{
    private readonly long _windowMs;
    private readonly Func<long> _now;
    private readonly Dictionary<string, (long Since, int Suppressed)> _seen = new();

    public LogLimiter(TimeSpan window, Func<long>? nowMs = null)
    {
        _windowMs = (long)window.TotalMilliseconds;
        _now = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>The text to write for <paramref name="message"/>, or null when it is a repeat inside the window.</summary>
    public string? Filter(string message) => Filter(message, message);

    /// <summary>
    /// As <see cref="Filter(string)"/>, but lines with the same <paramref name="key"/> count as repeats even when their text
    /// differs (an error message that names a changing address or time); the text written is the newest one.
    /// </summary>
    public string? Filter(string key, string message)
    {
        lock (_seen)
        {
            long now = _now();
            if (_seen.TryGetValue(key, out var seen) && now - seen.Since < _windowMs)
            {
                _seen[key] = (seen.Since, seen.Suppressed + 1);
                return null;
            }
            if (_seen.Count > 200) _seen.Clear();   // many different lines: forget, never grow without limit
            _seen[key] = (now, 0);
            return seen.Suppressed > 0 ? $"{message} (repeated {seen.Suppressed} more times before this)" : message;
        }
    }
}
