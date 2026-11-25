using System.Runtime.InteropServices;

namespace MiniLcd.Core.Usb;

/// <summary>Minimal P/Invoke wrapper over libusb-win32 (libusb0.dll, libusb-0.1 API). x64 only.</summary>
internal static unsafe class Libusb0
{
    private const string Dll = "libusb0.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void usb_init();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int usb_find_busses();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int usb_find_devices();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr usb_get_busses();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr usb_open(IntPtr dev);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int usb_close(IntPtr h);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int usb_set_configuration(IntPtr h, int cfg);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int usb_claim_interface(IntPtr h, int iface);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int usb_release_interface(IntPtr h, int iface);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int usb_clear_halt(IntPtr h, uint ep);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int usb_bulk_write(IntPtr h, int ep, byte* data, int size, int timeoutMs);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int usb_bulk_read(IntPtr h, int ep, byte* data, int size, int timeoutMs);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr usb_strerror();

    public static string LastError() => Marshal.PtrToStringAnsi(usb_strerror()) ?? "";

    // libusb-win32 x64 struct layouts (LIBUSB_PATH_MAX = 512)
    private const int PathMax = 512;
    private const int BusDevicesOffset = 16 + PathMax;          // after next, prev, dirname
    private const int DevFilenameOffset = 16;
    private const int DevDescriptorOffset = 16 + PathMax + 8;   // after bus pointer

    public record Found(IntPtr Device, ushort Vid, ushort Pid, string Filename);

    public static List<Found> Enumerate()
    {
        var list = new List<Found>();
        usb_init();
        usb_find_busses();
        usb_find_devices();
        for (var bus = usb_get_busses(); bus != IntPtr.Zero; bus = Marshal.ReadIntPtr(bus, 0))
        {
            for (var dev = Marshal.ReadIntPtr(bus, BusDevicesOffset); dev != IntPtr.Zero; dev = Marshal.ReadIntPtr(dev, 0))
            {
                var vid = (ushort)Marshal.ReadInt16(dev, DevDescriptorOffset + 8);
                var pid = (ushort)Marshal.ReadInt16(dev, DevDescriptorOffset + 10);
                var name = Marshal.PtrToStringAnsi(dev + DevFilenameOffset) ?? "";
                list.Add(new Found(dev, vid, pid, name));
            }
        }
        return list;
    }

    /// <summary>Best-effort dump of the first interface's endpoints (layout assumptions may not hold).</summary>
    public static void DumpEndpoints(IntPtr dev)
    {
        try
        {
            var cfg = Marshal.ReadIntPtr(dev, DevDescriptorOffset + 24);  // descriptor(18)+pad -> config ptr
            if (cfg == IntPtr.Zero) { Console.WriteLine("  (no config descriptor cached)"); return; }
            var nIf = Marshal.ReadByte(cfg, 4);
            var ifaces = Marshal.ReadIntPtr(cfg, 16);
            Console.WriteLine($"  config: interfaces={nIf}");
            for (int i = 0; i < nIf; i++)
            {
                var alt = Marshal.ReadIntPtr(ifaces, i * 16);
                var cls = Marshal.ReadByte(alt, 5);
                var sub = Marshal.ReadByte(alt, 6);
                var proto = Marshal.ReadByte(alt, 7);
                var nEp = Marshal.ReadByte(alt, 4);
                var eps = Marshal.ReadIntPtr(alt, 16);
                Console.WriteLine($"  interface {i}: class={cls:X2} sub={sub:X2} proto={proto:X2} endpoints={nEp}");
                for (int e = 0; e < nEp && e < 8; e++)
                {
                    var addr = Marshal.ReadByte(eps, e * 32 + 2);
                    var attr = Marshal.ReadByte(eps, e * 32 + 3);
                    var mps = (ushort)Marshal.ReadInt16(eps, e * 32 + 4);
                    Console.WriteLine($"    ep 0x{addr:X2} attr=0x{attr:X2} maxpacket={mps}");
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("  (endpoint dump failed: " + ex.Message + ")"); }
    }
}

