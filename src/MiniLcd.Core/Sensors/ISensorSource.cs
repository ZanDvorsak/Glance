namespace MiniLcd.Core.Sensors;

public interface ISensorSource
{
    IReadOnlyList<SensorInfo> Snapshot { get; }

    /// <summary>Refresh the snapshot. For remote sources this may be a no-op (data arrives in the background).</summary>
    void Update();
}
