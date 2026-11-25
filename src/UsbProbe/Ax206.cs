namespace UsbProbe;

/// <summary>AX206-style protocol: SCSI-like CBW over bulk endpoints, vendor opcode 0xCD.</summary>
internal sealed unsafe class Ax206
{
    private const int EpOut = 0x01, EpIn = 0x81, Timeout = 3000;
    private readonly IntPtr _h;

    /// <summary>Set backlight, 0 (off) to 7 (max).</summary>
    public void SetBrightness(int level)
    {
        var cdb = new byte[16];
        cdb[0] = 0xCD; cdb[5] = 6; cdb[6] = 0x01; cdb[7] = 0x01; cdb[8] = 0x00;
        cdb[9] = (byte)level; cdb[10] = 0;
        var cbw = Cbw(cdb, 0, read: false);
        var rc = Write(cbw, 0, cbw.Length);
        Console.WriteLine($"  set brightness {level}: write={rc}");
        var csw = new byte[13];
        var rc2 = Read(csw, 13);
        Console.WriteLine(rc2 >= 13 ? $"  CSW status={csw[12]}" : $"  CSW read: {rc2} {(rc2 < 0 ? Libusb0.LastError() : "")}");
    }

    public (int w, int h)? GetDimensions()
    {
        var cdb = new byte[16];
        cdb[0] = 0xCD; cdb[5] = 2;
        var d = Query(cdb, 5);
        if (d is { Length: >= 4 }) return (d[0] | (d[1] << 8), d[2] | (d[3] << 8));
        return null;
    }

    public Ax206(IntPtr handle) => _h = handle;

    // Values observed in a USBPcap capture of AIDA64 talking to this display.
    private byte[] Cbw(byte[] cdb, int dataLen, bool read)
    {
        var b = new byte[31];
        b[0] = (byte)'U'; b[1] = (byte)'S'; b[2] = (byte)'B'; b[3] = (byte)'C';
        b[4] = 0xDE; b[5] = 0xAD; b[6] = 0xBE; b[7] = 0xEF;   // fixed tag
        BitConverter.GetBytes((uint)dataLen).CopyTo(b, 8);
        b[12] = 0x00;   // direction flag is 0 even for reads on this device
        b[13] = 0;      // LUN
        b[14] = 16;     // CDB length
        Array.Copy(cdb, 0, b, 15, Math.Min(16, cdb.Length));
        return b;
    }

    private int Write(byte[] data, int offset, int count)
    {
        fixed (byte* p = &data[offset]) return Libusb0.usb_bulk_write(_h, EpOut, p, count, Timeout);
    }

    private int Read(byte[] buf, int count)
    {
        fixed (byte* p = buf) return Libusb0.usb_bulk_read(_h, EpIn, p, count, Timeout);
    }

    /// <summary>Send a read-style command; returns data bytes or null on failure.</summary>
    public byte[]? Query(byte[] cdb, int len)
    {
        var cbw = Cbw(cdb, len, read: true);
        var rc = Write(cbw, 0, cbw.Length);
        if (rc < 0) { Console.WriteLine($"  CBW write failed: {rc} {Libusb0.LastError()}"); return null; }
        var data = new byte[len];
        rc = Read(data, len);
        if (rc < 0) { Console.WriteLine($"  data read failed: {rc} {Libusb0.LastError()}"); return null; }
        Array.Resize(ref data, rc);
        var csw = new byte[13];
        var rc2 = Read(csw, 13);
        Console.WriteLine(rc2 >= 13
            ? $"  CSW status={csw[12]}"
            : $"  CSW read: {rc2} {(rc2 < 0 ? Libusb0.LastError() : "")}");
        return data;
    }

    public void Dump(byte[]? d)
    {
        if (d is null) { Console.WriteLine("  (no data)"); return; }
        Console.WriteLine("  hex:   " + BitConverter.ToString(d));
        Console.WriteLine("  ascii: " + new string(d.Select(c => c is >= 32 and < 127 ? (char)c : '.').ToArray()));
    }

    /// <summary>Blit an RGB565 (little-endian) rectangle.</summary>
    public void Blit(int x, int y, int w, int h, byte[] rgb565)
    {
        var x1 = x + w - 1; var y1 = y + h - 1;
        var cdb = new byte[16];
        cdb[0] = 0xCD; cdb[5] = 6; cdb[6] = 0x12;
        cdb[7] = (byte)x; cdb[8] = (byte)(x >> 8);
        cdb[9] = (byte)y; cdb[10] = (byte)(y >> 8);
        cdb[11] = (byte)x1; cdb[12] = (byte)(x1 >> 8);
        cdb[13] = (byte)y1; cdb[14] = (byte)(y1 >> 8);
        cdb[15] = 0;

        var cbw = Cbw(cdb, rgb565.Length, read: false);
        var rc = Write(cbw, 0, cbw.Length);
        if (rc < 0) { Console.WriteLine($"  blit CBW failed: {rc} {Libusb0.LastError()}"); return; }

        const int chunk = 16 * 1024;
        for (int off = 0; off < rgb565.Length; off += chunk)
        {
            var n = Math.Min(chunk, rgb565.Length - off);
            rc = Write(rgb565, off, n);
            if (rc < 0) { Console.WriteLine($"  blit data failed at {off}: {rc} {Libusb0.LastError()}"); return; }
        }
        var csw = new byte[13];
        var rc2 = Read(csw, 13);
        Console.WriteLine(rc2 >= 13 ? $"  blit CSW status={csw[12]}" : $"  blit CSW read: {rc2} {(rc2 < 0 ? Libusb0.LastError() : "")}");
    }

    private static ushort Rgb565(byte r, byte g, byte b) => (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));

    public static byte[] Solid(int w, int h, byte r, byte g, byte b)
    {
        var px = Rgb565(r, g, b);
        var buf = new byte[w * h * 2];
        for (int i = 0; i < w * h; i++) { buf[i * 2] = (byte)(px >> 8); buf[i * 2 + 1] = (byte)px; }
        return buf;
    }

    /// <summary>Four horizontal bands top to bottom: red, green, blue, white.</summary>
    public static byte[] Bands(int w, int h)
    {
        (byte, byte, byte)[] colors = { (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 255) };
        var buf = new byte[w * h * 2];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = colors[y * colors.Length / h];
                var px = Rgb565(r, g, b);
                var i = (y * w + x) * 2;
                buf[i] = (byte)(px >> 8); buf[i + 1] = (byte)px;   // big-endian on the wire
            }
        return buf;
    }

    public static byte[] Bars(int w, int h)
    {
        (byte, byte, byte)[] colors = { (255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 255), (0, 0, 0) };
        var buf = new byte[w * h * 2];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = colors[x * colors.Length / w];
                // thin white border so orientation/size problems are obvious
                if (x < 2 || y < 2 || x >= w - 2 || y >= h - 2) (r, g, b) = (255, 255, 0);
                var px = Rgb565(r, g, b);
                var i = (y * w + x) * 2;
                buf[i] = (byte)(px >> 8); buf[i + 1] = (byte)px;   // big-endian on the wire
            }
        return buf;
    }
}
