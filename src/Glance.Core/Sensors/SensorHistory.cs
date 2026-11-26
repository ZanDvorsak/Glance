namespace Glance.Core.Sensors;

/// <summary>Recent values of the sensors that graphs are showing, sampled about twice a second. Each list is kept in time order.</summary>
public sealed class SensorHistory
{
    public const int SampleMs = 500;
    private const int MaxSeconds = 900;

    private readonly Dictionary<string, List<(long ms, float value)>> _data = new();
    private readonly Dictionary<string, long> _lastSample = new();

    /// <summary>Adds a sample if enough time has passed since the last one for this sensor.</summary>
    public void Record(string sensorId, float value, long nowMs)
    {
        if (!_data.TryGetValue(sensorId, out var list)) _data[sensorId] = list = new();
        // _lastSample is always the newest sample's time (Seed raises it too), so this throttle also keeps the list
        // in time order: a time at or before the newest sample is less than SampleMs after it
        if (_lastSample.TryGetValue(sensorId, out var last) && nowMs - last < SampleMs) return;
        _lastSample[sensorId] = nowMs;
        list.Add((nowMs, value));
        var cutoff = nowMs - MaxSeconds * 1000L;
        int drop = 0;
        while (drop < list.Count && list[drop].ms < cutoff) drop++;
        if (drop > 0) list.RemoveRange(0, drop);
    }

    /// <summary>True when the sensor has at least one sample (real or seeded).</summary>
    public bool HasSamples(string sensorId) => _data.TryGetValue(sensorId, out var list) && list.Count > 0;

    /// <summary>Time of the sensor's oldest sample, or null when it has none.</summary>
    public long? Oldest(string sensorId) => _data.TryGetValue(sensorId, out var list) && list.Count > 0 ? list[0].ms : null;

    /// <summary>
    /// Inserts a sample in time order without the sampling throttle (a sample at a time that is already there is
    /// ignored). Used to pre-fill graphs and gallery thumbnails.
    /// </summary>
    public void Seed(string sensorId, long ms, float value)
    {
        if (!_data.TryGetValue(sensorId, out var list)) _data[sensorId] = list = new();
        int i = IndexOf(list, ms);
        if (i < list.Count && list[i].ms == ms) return;
        list.Insert(i, (ms, value));
        if (!_lastSample.TryGetValue(sensorId, out var last) || ms > last) _lastSample[sensorId] = ms;
    }

    /// <summary>A copy of the samples from the last <paramref name="seconds"/> seconds, oldest first.</summary>
    public IReadOnlyList<(long ms, float value)> Window(string sensorId, long nowMs, double seconds)
    {
        if (!_data.TryGetValue(sensorId, out var list)) return Array.Empty<(long, float)>();
        int start = IndexOf(list, nowMs - (long)(seconds * 1000));
        return list.GetRange(start, list.Count - start);
    }

    /// <summary>
    /// The same samples as <see cref="Window"/> without copying them, for drawing every frame. The view is only valid
    /// until the history is changed again (the next Record or Seed), so use it at once and do not keep it.
    /// </summary>
    internal ReadOnlySpan<(long ms, float value)> View(string sensorId, long nowMs, double seconds)
    {
        if (!_data.TryGetValue(sensorId, out var list)) return ReadOnlySpan<(long, float)>.Empty;
        int start = IndexOf(list, nowMs - (long)(seconds * 1000));
        return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list)[start..];
    }

    /// <summary>Index of the first sample at or after <paramref name="ms"/> (the list is sorted).</summary>
    private static int IndexOf(List<(long ms, float value)> list, long ms)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (list[mid].ms < ms) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    public void Clear() { _data.Clear(); _lastSample.Clear(); }
}
