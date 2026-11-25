namespace MiniLcd.Core.Sensors;

/// <summary>Drive space sensors from Windows itself (LibreHardwareMonitor only reports drive load percentages).</summary>
public static class DriveSensors
{
    private const double GB = 1024.0 * 1024 * 1024;

    public static List<SensorInfo> Read()
    {
        var list = new List<SensorInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                var letter = d.Name.TrimEnd('\\', ':').ToLowerInvariant();
                var hw = $"Drive {d.Name.TrimEnd('\\')}";
                var label = d.Name.TrimEnd('\\');
                double total = d.TotalSize / GB, free = d.AvailableFreeSpace / GB, used = total - free;
                list.Add(new SensorInfo($"/drive/{letter}/data/0", hw, $"{label} Used Space", "Data", (float)used, "GB"));
                list.Add(new SensorInfo($"/drive/{letter}/data/1", hw, $"{label} Free Space", "Data", (float)free, "GB"));
                list.Add(new SensorInfo($"/drive/{letter}/data/2", hw, $"{label} Total Size", "Data", (float)total, "GB"));
                list.Add(new SensorInfo($"/drive/{letter}/load/0", hw, $"{label} Utilization", "Load", total > 0 ? (float)(used / total * 100) : 0, "%"));
            }
            catch { /* drive vanished or not accessible */ }
        }
        return list;
    }
}
