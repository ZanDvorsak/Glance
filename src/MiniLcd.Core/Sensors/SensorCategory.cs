namespace MiniLcd.Core.Sensors;

/// <summary>Groups sensors by the kind of hardware they belong to, derived from the LibreHardwareMonitor id.</summary>
public static class SensorCategory
{
    /// <summary>Display order of the groups.</summary>
    public static readonly string[] Order = { "CPU", "GPU", "Motherboard", "Memory", "Storage", "Drives", "Network", "Other" };

    /// <summary>Display order of the sensor types; anything else sorts after these.</summary>
    public static readonly string[] TypeOrder = { "Temperature", "Load", "Clock", "Fan", "Voltage", "Power", "Data", "Throughput", "Control", "Level" };

    public static string Of(SensorInfo s)
    {
        var id = s.Id.ToLowerInvariant();
        if (id.StartsWith("/amdcpu/") || id.StartsWith("/intelcpu/") || id.StartsWith("/cpu/")) return "CPU";
        if (id.StartsWith("/gpu-") || id.StartsWith("/gpu/")) return "GPU";
        if (id.StartsWith("/lpc/") || id.StartsWith("/embeddedcontroller/") || id.StartsWith("/motherboard/")) return "Motherboard";
        if (id.StartsWith("/ram/") || id.StartsWith("/memory/") || id.StartsWith("/vram/")) return "Memory";
        if (id.StartsWith("/nvme/") || id.StartsWith("/hdd/") || id.StartsWith("/ssd/") || id.StartsWith("/storage/")) return "Storage";
        if (id.StartsWith("/drive/")) return "Drives";
        if (id.StartsWith("/nic/")) return "Network";
        return "Other";
    }

    public static int CategoryRank(string category) { var i = Array.IndexOf(Order, category); return i < 0 ? Order.Length : i; }
    public static int TypeRank(string type) { var i = Array.IndexOf(TypeOrder, type); return i < 0 ? TypeOrder.Length : i; }
}
