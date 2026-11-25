using UsbProbe;

// Usage:
//   UsbProbe                  -> open display, backlight on, query size, draw colour bars
//   UsbProbe list             -> only enumerate libusb0 devices
//   UsbProbe fill R G B       -> fill screen with a colour (0-255 each)
//   UsbProbe backlight N      -> set backlight 0-7
const ushort Vid = 0x1908, Pid = 0x0102;

var devices = Libusb0.Enumerate();
Console.WriteLine($"libusb0 sees {devices.Count} device(s):");
foreach (var d in devices) Console.WriteLine($"  {d.Vid:X4}:{d.Pid:X4}  {d.Filename}");
if (args.Length > 0 && args[0] == "list") return 0;

var target = devices.FirstOrDefault(d => d.Vid == Vid && d.Pid == Pid);
if (target is null) { Console.WriteLine("Display 1908:0102 not found."); return 1; }

var h = Libusb0.usb_open(target.Device);
if (h == IntPtr.Zero) { Console.WriteLine("usb_open failed: " + Libusb0.LastError() + "\n(Is AIDA64 still running?)"); return 2; }

try
{
    Libusb0.usb_set_configuration(h, 1);
    var rc = Libusb0.usb_claim_interface(h, 0);
    if (rc < 0) { Console.WriteLine("claim_interface failed: " + Libusb0.LastError()); return 3; }

    var ax = new Ax206(h);

    if (args.Length >= 2 && args[0] == "backlight")
    {
        ax.SetBrightness(int.Parse(args[1]));
        return 0;
    }

    ax.SetBrightness(7);

    var dim = ax.GetDimensions();
    Console.WriteLine(dim is null ? "  size query failed, assuming 480x320" : $"  display reports {dim.Value.w}x{dim.Value.h}");
    var (w, hgt) = dim ?? (480, 320);

    if (args.Length >= 4 && args[0] == "fill")
        ax.Blit(0, 0, w, hgt, Ax206.Solid(w, hgt, byte.Parse(args[1]), byte.Parse(args[2]), byte.Parse(args[3])));
    else if (args.Length >= 1 && args[0] == "bands")
        ax.Blit(0, 0, w, hgt, Ax206.Bands(w, hgt));
    else
        ax.Blit(0, 0, w, hgt, Ax206.Bars(w, hgt));
    Console.WriteLine("Blit sent. Look at the screen: red, green, blue, white, black bars with a yellow border.");
}
finally
{
    Libusb0.usb_release_interface(h, 0);
    Libusb0.usb_close(h);
}
return 0;
