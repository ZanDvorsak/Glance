namespace Glance.Core.Display;

/// <summary>
/// The operations <see cref="DisplayPusher"/> needs from a physical display. <see cref="Usb.Ax206Display"/> is the real one;
/// tests use a fake so the loop can be checked without hardware.
/// </summary>
internal interface IFrameDisplay : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>Sends RGB565 big-endian pixels (w*h*2 bytes). The real display only accepts whole frames.</summary>
    void Blit(int x, int y, int w, int h, byte[] rgb565Be);

    /// <summary>Backlight level, 0 (off) to 7 (full).</summary>
    void SetBrightness(int level);

    /// <summary>Black frame and backlight off. Best effort: never throws.</summary>
    void Blank();
}
