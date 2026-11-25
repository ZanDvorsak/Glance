namespace Glance.Core.Sensors;

/// <summary>Recent values of the sensors that graphs are showing, sampled about twice a second.</summary>
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
        if (_lastSample.TryGetValue(sensorId, out var last) && nowMs - last < SampleMs) return;
        _lastSample[sensorId] = nowMs;
        list.Add((nowMs, value));
        var cutoff = nowMs - MaxSeconds * 1000L;
        int drop = 0;
        while (drop < list.Count && list[drop].ms < cutoff) drop++;
        if (drop > 0) list.RemoveRange(0, drop);
    }

    /// <summary>Appends a sample without the sampling throttle. Used to pre-fill gallery thumbnails.</summary>
    public void Seed(string sensorId, long ms, float value)
    {
        if (!_data.TryGetValue(sensorId, out var list)) _data[sensorId] = list = new();
        list.Add((ms, value));
        _lastSample[sensorId] = ms;
    }

    /// <summary>Samples from the last <paramref name="seconds"/> seconds, oldest first.</summary>
    public IReadOnlyList<(long ms, float value)> Window(string sensorId, long nowMs, double seconds)
    {
        if (!_data.TryGetValue(sensorId, out var list)) return Array.Empty<(long, float)>();
        var from = nowMs - (long)(seconds * 1000);
        int start = 0;
        while (start < list.Count && list[start].ms < from) start++;
        return start == 0 ? list : list.GetRange(start, list.Count - start);
    }

    public void Clear() { _data.Clear(); _lastSample.Clear(); }
}
