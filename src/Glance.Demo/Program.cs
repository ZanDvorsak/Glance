using Glance.Core.Rendering;
using Glance.Core.Sensors;
using Glance.Core.Usb;
using SkiaSharp;

// Usage:
//   Glance.Demo sensors   -> list every sensor found, then exit (no display needed)
//   Glance.Demo [bg.png]  -> live demo on the display (Ctrl+C to stop)
// Prefer the Glance service's sensor feed (full CPU/motherboard sensors); fall back to local, non-admin reading.
using var pipe = new PipeSensorClient();
SensorService? local = null;
for (int i = 0; i < 25 && !pipe.HasData; i++) await Task.Delay(100);
ISensorSource sensors;
if (pipe.HasData) { sensors = pipe; Console.WriteLine("Using the Glance service's sensors."); }
else { local = new SensorService(); sensors = local; Console.WriteLine("Glance service not running: using local sensors (no admin => limited)."); }
using var _local = local;
sensors.Update();

if (args.Length > 0 && args[0] == "sensors")
{
    foreach (var g in sensors.Snapshot.GroupBy(s => s.Hardware))
    {
        Console.WriteLine($"== {g.Key}");
        foreach (var s in g) Console.WriteLine($"   [{s.Type}] {s.Name} = {(s.Value is null ? "n/a" : s.Value.Value.ToString("0.#"))} {s.Unit}   ({s.Id})");
    }
    Console.WriteLine($"\n{sensors.Snapshot.Count} sensors.");
    return 0;
}

var bgPath = args.Length > 0 ? args[0] : FindDefaultBackground();
SKBitmap? bg = bgPath is not null && File.Exists(bgPath) ? SKBitmap.Decode(bgPath) : null;

using var lcd = Ax206Display.Open();
lcd.SetBrightness(7);
Console.WriteLine($"Display {lcd.Width}x{lcd.Height}. Ctrl+C to stop.");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

using var bmp = new SKBitmap(new SKImageInfo(lcd.Width, lcd.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
using var canvas = new SKCanvas(bmp);
using var big = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 44);
using var small = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 16);
using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White };
using var panel = new SKPaint { Color = new SKColor(0, 0, 0, 120) };

var frames = 0; var sw = System.Diagnostics.Stopwatch.StartNew();
while (!cts.IsCancellationRequested)
{
    sensors.Update();
    var cpu = Pick(sensors, "Cpu", "Temperature", "Package", "Tctl", "Tdie", "Core");
    var gpu = Pick(sensors, "Gpu", "Temperature", "Core");
    var load = Pick(sensors, "Cpu", "Load", "Total");

    canvas.Clear(SKColors.Black);
    if (bg is not null) canvas.DrawBitmap(bg, -35, 0);
    canvas.DrawRoundRect(10, 10, 230, 140, 10, 10, panel);
    Text(canvas, "CPU", 24, 40, small, paint);
    Text(canvas, Fmt(cpu, "°C"), 24, 90, big, paint);
    Text(canvas, "Load " + Fmt(load, "%"), 24, 126, small, paint);
    canvas.DrawRoundRect(10, 160, 230, 140, 10, 10, panel);
    Text(canvas, "GPU", 24, 190, small, paint);
    Text(canvas, Fmt(gpu, "°C"), 24, 240, big, paint);
    Text(canvas, DateTime.Now.ToString("HH:mm:ss"), 330, 40, small, paint);

    lcd.Blit(0, 0, lcd.Width, lcd.Height, FrameConverter.ToRgb565Be(bmp));
    frames++;
    if (frames % 10 == 0) Console.WriteLine($"{frames} frames, {frames / sw.Elapsed.TotalSeconds:0.0} fps (sensor-limited)");
    try { await Task.Delay(500, cts.Token); } catch (OperationCanceledException) { }
}
return 0;

static void Text(SKCanvas c, string t, float x, float y, SKFont f, SKPaint p) => c.DrawText(t, x, y, SKTextAlign.Left, f, p);
static string Fmt(SensorInfo? s, string unit) => s?.Value is { } v ? $"{v:0}{unit}" : "--";

static SensorInfo? Pick(ISensorSource svc, string hwType, string type, params string[] nameHints)
{
    var cands = svc.Snapshot.Where(s => s.Id.Contains(hwType, StringComparison.OrdinalIgnoreCase)
                                        && s.Type == type && s.Value is not null).ToList();
    foreach (var h in nameHints)
    {
        var m = cands.FirstOrDefault(s => s.Name.Contains(h, StringComparison.OrdinalIgnoreCase));
        if (m is not null) return m;
    }
    return cands.FirstOrDefault();
}

static string? FindDefaultBackground()
{
    var dir = AppContext.BaseDirectory;
    for (int i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
    {
        var p = Path.Combine(dir, "Themes", "autumn (Aida).png");
        if (File.Exists(p)) return p;
    }
    return null;
}
