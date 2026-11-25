using SkiaSharp;

namespace MiniLcd.Core.Rendering;

public static unsafe class FrameConverter
{
    /// <summary>Converts a Skia bitmap (BGRA8888 or RGBA8888) to RGB565 big-endian.</summary>
    public static byte[] ToRgb565Be(SKBitmap bmp)
    {
        if (bmp.ColorType != SKColorType.Bgra8888 && bmp.ColorType != SKColorType.Rgba8888)
            throw new ArgumentException("Bitmap must be Bgra8888 or Rgba8888.");
        bool bgra = bmp.ColorType == SKColorType.Bgra8888;
        int w = bmp.Width, h = bmp.Height;
        var outBuf = new byte[w * h * 2];
        var src = (byte*)bmp.GetPixels();
        int stride = bmp.RowBytes;
        fixed (byte* dst = outBuf)
        {
            for (int y = 0; y < h; y++)
            {
                var s = src + y * stride;
                var d = dst + y * w * 2;
                for (int x = 0; x < w; x++, s += 4, d += 2)
                {
                    int r = bgra ? s[2] : s[0];
                    int g = s[1];
                    int b = bgra ? s[0] : s[2];
                    int px = ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3);
                    d[0] = (byte)(px >> 8);
                    d[1] = (byte)px;
                }
            }
        }
        return outBuf;
    }
}
