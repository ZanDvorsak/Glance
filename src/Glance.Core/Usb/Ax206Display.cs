namespace Glance.Core.Usb;

/// <summary>
/// Driver for the 1908:0102 "USB-Display" (AX206-style protocol over libusb-win32).
/// See docs/PROTOCOL.md. Frames are RGB565 big-endian.
/// </summary>
public sealed unsafe class Ax206Display : IDisposable
{
    public const ushort Vid = 0x1908, Pid = 0x0102;
    private const int EpOut = 0x01, EpIn = 0x81, TimeoutMs = 3000, ChunkSize = 64 * 1024;

    private IntPtr _handle;

    public int Width { get; private set; } = 480;
    public int Height { get; private set; } = 320;

    private Ax206Display(IntPtr handle) => _handle = handle;

    /// <summary>True if a 1908:0102 display is visible through the libusb-win32 driver. Throws DllNotFoundException if libusb0.dll is missing.</summary>
    public static bool IsPresent() => Libusb0.Enumerate().Any(d => d.Vid == Vid && d.Pid == Pid);

    /// <summary>Opens and immediately closes the display to see whether another program holds it. Returns the error, or null if it can be opened.</summary>
    public static string? TryProbeOpen()
    {
        var found = Libusb0.Enumerate().FirstOrDefault(d => d.Vid == Vid && d.Pid == Pid);
        if (found is null) return "not found";
        var h = Libusb0.usb_open(found.Device);
        if (h == IntPtr.Zero) return Libusb0.LastError();
        try
        {
            // opening can succeed even while another program owns the display; claiming the interface is what fails then
            if (Libusb0.usb_claim_interface(h, 0) < 0) return Libusb0.LastError();
            Libusb0.usb_release_interface(h, 0);
            return null;
        }
        finally { Libusb0.usb_close(h); }
    }

    /// <summary>Opens the first matching display. Throws if it is missing or used by another program.</summary>
    public static Ax206Display Open()
    {
        var found = Libusb0.Enumerate().FirstOrDefault(d => d.Vid == Vid && d.Pid == Pid)
            ?? throw new InvalidOperationException("Display 1908:0102 not found. Is it plugged in and using the libusb-win32 driver?");
        var h = Libusb0.usb_open(found.Device);
        if (h == IntPtr.Zero)
            throw new InvalidOperationException("Could not open the display: " + Libusb0.LastError() + " (is AIDA64 or another program using it?)");
        Libusb0.usb_set_configuration(h, 1);
        if (Libusb0.usb_claim_interface(h, 0) < 0)
        {
            var err = Libusb0.LastError();
            Libusb0.usb_close(h);
            throw new InvalidOperationException("Could not claim the display interface: " + err);
        }
        var d = new Ax206Display(h);
        d.ReadDimensions();
        return d;
    }

    private byte[] BuildCbw(byte[] cdb, int dataLen)
    {
        var b = new byte[31];
        b[0] = (byte)'U'; b[1] = (byte)'S'; b[2] = (byte)'B'; b[3] = (byte)'C';
        b[4] = 0xDE; b[5] = 0xAD; b[6] = 0xBE; b[7] = 0xEF;
        BitConverter.GetBytes((uint)dataLen).CopyTo(b, 8);
        b[14] = 16;
        Array.Copy(cdb, 0, b, 15, Math.Min(16, cdb.Length));
        return b;
    }

    private void Write(byte[] data, int offset, int count)
    {
        fixed (byte* p = &data[offset])
        {
            var rc = Libusb0.usb_bulk_write(_handle, EpOut, p, count, TimeoutMs);
            if (rc < 0) throw new IOException("USB write failed: " + Libusb0.LastError());
        }
    }

    private int Read(byte[] buf, int count)
    {
        fixed (byte* p = buf)
        {
            var rc = Libusb0.usb_bulk_read(_handle, EpIn, p, count, TimeoutMs);
            if (rc < 0) throw new IOException("USB read failed: " + Libusb0.LastError());
            return rc;
        }
    }

    private void ReadStatus() => Read(new byte[13], 13);

    private void ReadDimensions()
    {
        var cdb = new byte[16]; cdb[0] = 0xCD; cdb[5] = 2;
        var cbw = BuildCbw(cdb, 5);
        Write(cbw, 0, cbw.Length);
        var data = new byte[5];
        var n = Read(data, 5);
        ReadStatus();
        if (n >= 4)
        {
            var w = data[0] | (data[1] << 8); var h = data[2] | (data[3] << 8);
            if (w is > 0 and <= 4096 && h is > 0 and <= 4096) { Width = w; Height = h; }
        }
    }

    /// <summary>Backlight level, 0 (off) to 7 (full).</summary>
    public void SetBrightness(int level)
    {
        var cdb = new byte[16];
        cdb[0] = 0xCD; cdb[5] = 6; cdb[6] = 0x01; cdb[7] = 0x01; cdb[9] = (byte)Math.Clamp(level, 0, 7);
        var cbw = BuildCbw(cdb, 0);
        Write(cbw, 0, cbw.Length);
        ReadStatus();
    }

    /// <summary>
    /// Sends a rectangle of RGB565 big-endian pixels (w*h*2 bytes).
    /// NOTE: the 1908:0102 display only accepts full-screen updates (0,0,Width,Height). Smaller rectangles were measured on the
    /// real display: the transfer times out and nothing is drawn. See docs/PROTOCOL.md.
    /// </summary>
    public void Blit(int x, int y, int w, int h, byte[] rgb565Be)
    {
        if (rgb565Be.Length != w * h * 2) throw new ArgumentException("Buffer size does not match rectangle.");
        int x1 = x + w - 1, y1 = y + h - 1;
        var cdb = new byte[16];
        cdb[0] = 0xCD; cdb[5] = 6; cdb[6] = 0x12;
        cdb[7] = (byte)x; cdb[8] = (byte)(x >> 8);
        cdb[9] = (byte)y; cdb[10] = (byte)(y >> 8);
        cdb[11] = (byte)x1; cdb[12] = (byte)(x1 >> 8);
        cdb[13] = (byte)y1; cdb[14] = (byte)(y1 >> 8);
        var cbw = BuildCbw(cdb, rgb565Be.Length);
        Write(cbw, 0, cbw.Length);
        for (int off = 0; off < rgb565Be.Length; off += ChunkSize)
            Write(rgb565Be, off, Math.Min(ChunkSize, rgb565Be.Length - off));
        ReadStatus();
    }

    /// <summary>Black frame, then backlight off, so the screen does not keep showing its last picture.</summary>
    public void Blank()
    {
        try { Blit(0, 0, Width, Height, new byte[Width * Height * 2]); } catch { /* best effort */ }
        try { SetBrightness(0); } catch { /* best effort */ }
    }

    /// <summary>Opens the display just to turn it black. Returns an error message, or null on success.</summary>
    public static string? TryBlank()
    {
        try { using var d = Open(); d.Blank(); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        Libusb0.usb_release_interface(_handle, 0);
        Libusb0.usb_close(_handle);
        _handle = IntPtr.Zero;
    }
}
