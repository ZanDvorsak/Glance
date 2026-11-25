using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using MiniLcd.Core.Sensors;
using MiniLcd.Core.Themes;
using SkiaSharp;

namespace MiniLcd.Core.Import;

public sealed record AidaImportResult(Theme Theme, List<string> Notes, int Imported, int Unmapped, int Skipped);

/// <summary>Converts an AIDA64 .axlcd LCD theme into a MiniLcd theme (page 1 only).</summary>
public static partial class AidaImporter
{
    private static readonly Regex TagRx = TagRegex();
    [GeneratedRegex("<([A-Z0-9]+)>(.*?)</\\1>", RegexOptions.Singleline)] private static partial Regex TagRegex();

    public static AidaImportResult Import(string axlcdPath, string themeFolder, IReadOnlyList<SensorInfo> sensors, int width = 480, int height = 320)
    {
        Directory.CreateDirectory(themeFolder);
        var assets = Path.Combine(themeFolder, "assets");
        var map = new AidaSensorMap(sensors);
        var notes = new List<string>();
        int imported = 0, unmapped = 0, skipped = 0;

        var theme = new Theme
        {
            Name = Path.GetFileNameWithoutExtension(axlcdPath), Width = width, Height = height, BaseFolder = themeFolder,
        };

        // The file is ANSI (Latin-1 covers the degree sign etc.), one item per line.
        var text = File.ReadAllText(axlcdPath, Encoding.Latin1);
        var lines = text.Split('\n').Select(l => l.Trim('\r', ' ')).Where(l => l.Length > 0).ToList();

        var bg = Tag(string.Join("", lines.Where(l => l.StartsWith("<LCDBGCOLOR>"))), "LCDBGCOLOR");
        if (int.TryParse(bg, out var bgc)) theme.BackgroundColor = Color(bgc);

        // every <LCDPAGEn> section becomes a page; items land on the page being read (theme.Widgets = the active page)
        bool inPage = false;
        int pageCount = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("<LCDPAGE", StringComparison.Ordinal))
            {
                pageCount++;
                if (pageCount > 1) theme.Pages.Add(new ThemePage { Name = $"Page {pageCount}" });
                theme.ActivePageIndex = pageCount - 1;
                inPage = true;
                continue;
            }
            if (line.StartsWith("</LCDPAGE", StringComparison.Ordinal)) { inPage = false; continue; }
            if (!inPage || !line.StartsWith('<')) continue;

            var t = ParseLine(line);
            var id = (t.GetValueOrDefault("ID") ?? "").Trim();
            var code = id.TrimStart('-').Replace("[SIMPLE]", "", StringComparison.OrdinalIgnoreCase);
            double x = Num(t, "ITMX"), y = Num(t, "ITMY");

            if (code == "IMG") { if (ImportImage(theme, t, assets, x, y, notes)) imported++; else skipped++; }
            else if (code == "LBL") { ImportLabel(theme, t, x, y, sensors); imported++; }
            else if (AidaSensorMap.ClockFormat(code) is { } fmt) { ImportClock(theme, t, fmt, x, y, code); imported++; }
            else if (code.Length > 1 && char.IsLetter(code[0]) && code.All(c => char.IsLetterOrDigit(c)))
            {
                var res = ImportSensorItem(theme, t, code, x, y, map, notes);
                imported++;
                if (!res) unmapped++;
            }
            else { skipped++; notes.Add($"Skipped unsupported item type '{id}'."); }
        }
        if (pageCount > 1) notes.Add($"The theme has {pageCount} pages; each was imported as a page and they rotate every {theme.Pages[0].DurationSeconds:0} seconds.");
        int nudged = 0;
        for (int i = 0; i < theme.Pages.Count; i++) { theme.ActivePageIndex = i; nudged += SeparateLabelsFromValues(theme); }
        theme.ActivePageIndex = 0;
        if (nudged > 0) notes.Add($"Layout: moved {nudged} value(s) slightly right so they don't collide with their labels.");
        return new AidaImportResult(theme, notes, imported, unmapped, skipped);
    }

    // ---- overlap handling ---------------------------------------------------------------------------------------

    /// <summary>
    /// AIDA64 positions static labels and right-aligned values independently, and its fonts are a little narrower than ours.
    /// For every value on the same row as a label to its left, make sure the widest likely value still clears the label.
    /// </summary>
    private static int SeparateLabelsFromValues(Theme theme)
    {
        int moved = 0;
        var labels = theme.Widgets.Where(w => w.Kind == WidgetKind.Text && w.Align == TextAlign.Left && !w.Name.EndsWith(" unit")).ToList();
        foreach (var value in theme.Widgets.Where(w => w.Kind == WidgetKind.Sensor && w.Align == TextAlign.Right).ToList())
        {
            var unit = theme.Widgets.FirstOrDefault(w => w.Kind == WidgetKind.Text && w.Name == value.Name + " unit" && Math.Abs(w.X - value.X) < 0.5 && Math.Abs(w.Y - value.Y) < 0.5);
            double valueW = Measure(value, SampleValue(unit?.Text ?? value.Unit));
            double left = value.X - valueW;
            double need = 0;
            foreach (var l in labels)
            {
                if (Math.Abs(l.Y - value.Y) > 3 || l.X >= value.X) continue;           // same row, label to the left
                double labelRight = l.X + Measure(l, l.Text);
                if (labelRight + 4 > left) need = Math.Max(need, labelRight + 4 - left);
            }
            if (need <= 0.5) continue;
            double unitW = unit is null ? 0 : Measure(unit, unit.Text);
            need = Math.Min(need, Math.Max(0, theme.Width - 1 - (value.X + unitW)));   // stay on screen
            if (need <= 0.5) continue;
            value.X += need;
            if (unit is not null) unit.X += need;
            moved++;
        }
        return moved;
    }

    /// <summary>The widest value we expect for a unit, used to reserve room.</summary>
    private static string SampleValue(string unit) => unit.Trim().ToUpperInvariant() switch
    {
        "%" => "100",
        "MHZ" or "RPM" => "0000",
        "MB" => "00000",
        "°C" => "100",
        _ => "000",
    };

    private static double Measure(Widget w, string text)
    {
        using var face = SKTypeface.FromFamilyName(w.FontFamily, w.Bold ? SKFontStyle.Bold : SKFontStyle.Normal);
        using var font = new SKFont(face ?? SKTypeface.Default, (float)w.FontSize);
        return font.MeasureText(text);
    }

    // ---- items --------------------------------------------------------------------------------------------------

    private static bool ImportImage(Theme theme, Dictionary<string, string> t, string assetsDir, double x, double y, List<string> notes)
    {
        var name = t.GetValueOrDefault("IMGFIL") ?? "image.png";
        var hex = t.GetValueOrDefault("IMGDAT");
        if (string.IsNullOrWhiteSpace(hex)) { notes.Add($"Image '{name}' has no embedded data; skipped."); return false; }
        Directory.CreateDirectory(assetsDir);
        var bytes = Convert.FromHexString(hex.Trim());
        var file = Path.Combine(assetsDir, SafeFile(name));
        // several pages can embed different pictures under the same name: keep both
        for (int n = 2; File.Exists(file) && new FileInfo(file).Length != bytes.Length; n++)
            file = Path.Combine(assetsDir, $"{Path.GetFileNameWithoutExtension(SafeFile(name))}_{n}{Path.GetExtension(name)}");
        File.WriteAllBytes(file, bytes);
        double w = 100, h = 100;
        using (var data = SKData.Create(file))
        using (var codec = data is null ? null : SKCodec.Create(data))
            if (codec is not null) { w = codec.Info.Width; h = codec.Info.Height; }
        theme.Widgets.Add(new Widget
        {
            Kind = WidgetKind.Image, Name = Path.GetFileNameWithoutExtension(name), X = x, Y = y, Width = w, Height = h,
            ImagePath = "assets/" + Path.GetFileName(file),
        });
        return true;
    }

    private static void ImportLabel(Theme theme, Dictionary<string, string> t, double x, double y, IReadOnlyList<SensorInfo> sensors)
    {
        var label = ResolveVariables(t.GetValueOrDefault("LBL") ?? "", sensors);
        theme.Widgets.Add(new Widget
        {
            Kind = WidgetKind.Text, Name = Trim(label), X = x, Y = y, Text = label,
            FontFamily = FontName(t.GetValueOrDefault("FNTNAM")), FontSize = Px(t, "TXTSIZ", 12), Bold = IsBold(t.GetValueOrDefault("TXTBIR") ?? t.GetValueOrDefault("LBLBIS")),
            Color = Color(Int(t, "LBLCOL", 0xFFFFFF)),
        });
    }

    private static void ImportClock(Theme theme, Dictionary<string, string> t, string format, double x, double y, string code)
    {
        theme.Widgets.Add(new Widget
        {
            Kind = WidgetKind.Clock, Name = code == "SDATE" ? "Date" : code == "STIME" ? "Time" : "Clock", X = x, Y = y, Text = format,
            FontFamily = FontName(t.GetValueOrDefault("FNTNAM")), FontSize = Px(t, "TXTSIZ", 12), Bold = IsBold(t.GetValueOrDefault("TXTBIR")),
            Color = Color(Int(t, "TXTCOL", Int(t, "VALCOL", 0xFFFFFF))),
        });
    }

    /// <summary>One AIDA value item becomes: optional label text, a value, and optionally a bar.</summary>
    private static bool ImportSensorItem(Theme theme, Dictionary<string, string> t, string code, double x, double y, AidaSensorMap map, List<string> notes)
    {
        bool showLabel = Flag(t, "SHWLBL"), showValue = !t.ContainsKey("SHWVAL") || Flag(t, "SHWVAL"), showUnit = Flag(t, "SHWUNT"), showBar = Flag(t, "SHWBAR");
        // [SIMPLE] items carry label/value colours under different tag names
        bool simple = (t.GetValueOrDefault("ID") ?? "").StartsWith("[SIMPLE]", StringComparison.OrdinalIgnoreCase);
        if (simple) { showValue = true; showLabel = Flag(t, "SHWLBL"); }

        var aidaUnit = showUnit ? (t.GetValueOrDefault("UNT") ?? "") : "";
        var label = t.GetValueOrDefault("LBL") ?? code;
        var mapping = map.Resolve(code, t.GetValueOrDefault("UNT") ?? "");
        var family = FontName(t.GetValueOrDefault("FNTNAM"));
        var size = Px(t, "TXTSIZ", 12);
        bool bold = IsBold(t.GetValueOrDefault("TXTBIR") ?? t.GetValueOrDefault("VALBIS"));
        var valColor = Color(Int(t, simple ? "TXTCOL" : "VALCOL", 0xFFFFFF));

        if (mapping is null)
            notes.Add($"No matching sensor for AIDA item '{code}' ({label}) - imported as an empty Sensor widget; assign one with the sensor browser.");
        else
            notes.Add($"{code} -> {mapping.Sensor!.Hardware} / {mapping.Sensor.Name}" + (mapping.Multiplier != 1 ? $" (x{mapping.Multiplier:0.####})" : ""));

        // AIDA64 lays a boxed item out as: label at the left edge, value right-aligned, then a fixed-width unit field
        // (WID = box width, UNTWID = unit field width). [SIMPLE] items have no box: value and unit follow the anchor.
        double wid = simple ? 0 : Num(t, "WID", 0);
        bool boxed = wid > 0;

        // The bar goes first so the item's label and value are drawn on top of it (the designer overlays text on bars).
        if (showBar)
        {
            double bw = Num(t, "BARWID", 100), bh = Num(t, "BARHEI", 10);
            double by = showValue && !boxed ? y + size * 1.25 + 2 : y;
            double min = TryNum(t, "BARMIN") ?? 0;
            double max = TryNum(t, "BARMAX") ?? DefaultMax(mapping?.Sensor);
            theme.Widgets.Add(new Widget
            {
                Kind = WidgetKind.Bar, Name = label + " bar", X = x, Y = by, Width = bw, Height = bh, CornerRadius = Math.Min(4, bh / 2),
                SensorId = mapping?.Sensor?.Id ?? "", Multiplier = mapping?.Multiplier ?? 1, Min = min, Max = max,
                Color = Color(Int(t, "BARMINFGC", 0x40C4FF)), BackColor = WithAlpha(Color(Int(t, "BARMINBGC", 0x444444)), 0xAA),
            });
        }
        double untWid = showUnit && boxed ? Num(t, "UNTWID", 40) : 0;
        double cursorX = x;
        if (showLabel)
        {
            var lbl = new Widget
            {
                Kind = WidgetKind.Text, Name = label, X = x, Y = y, Text = label, FontFamily = family, FontSize = size,
                Color = Color(Int(t, "LBLCOL", 0xFFFFFF)),
            };
            theme.Widgets.Add(lbl);
            using var face = SKTypeface.FromFamilyName(family);
            using var font = new SKFont(face ?? SKTypeface.Default, (float)size);
            cursorX = x + font.MeasureText(label) + 6;
        }

        if (showValue && boxed)
        {
            double right = x + wid - untWid;
            theme.Widgets.Add(new Widget
            {
                Kind = WidgetKind.Sensor, Name = label, X = right, Y = y, Text = "", SensorId = mapping?.Sensor?.Id ?? "", Align = TextAlign.Right,
                Multiplier = mapping?.Multiplier ?? 1, Unit = "", Format = "0", FontFamily = family, FontSize = size, Bold = bold, Color = valColor,
            });
            if (showUnit && aidaUnit.Trim().Length > 0)
                theme.Widgets.Add(new Widget
                {
                    Kind = WidgetKind.Text, Name = label + " unit", X = right, Y = y, Text = aidaUnit, FontFamily = family, FontSize = size,
                    Color = Color(Int(t, "UNTCOL", Int(t, "VALCOL", 0xFFFFFF))),
                });
        }
        else if (showValue)
        {
            theme.Widgets.Add(new Widget
            {
                Kind = WidgetKind.Sensor, Name = label, X = cursorX, Y = y, Text = "", SensorId = mapping?.Sensor?.Id ?? "",
                Multiplier = mapping?.Multiplier ?? 1, Unit = aidaUnit, Format = "0", FontFamily = family, FontSize = size, Bold = bold, Color = valColor,
            });
        }

        return mapping is not null;
    }

    private static double DefaultMax(SensorInfo? s) => s?.Type switch { "Fan" => 3000, "Clock" => 6000, "Voltage" => 15, "Power" => 300, _ => 100 };

    // ---- text variables -----------------------------------------------------------------------------------------

    /// <summary>AIDA variables like $CPUMODEL are replaced with this PC's values at import time.</summary>
    private static string ResolveVariables(string s, IReadOnlyList<SensorInfo> sensors)
    {
        if (!s.Contains('$')) return s;
        return Regex.Replace(s, @"\$[A-Z0-9]+", m => m.Value.ToUpperInvariant() switch
        {
            "$CPUMODEL" => Registry("HKEY_LOCAL_MACHINE\\HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0", "ProcessorNameString")?.Trim() ?? m.Value,
            "$MOBOMODEL" => (Registry("HKEY_LOCAL_MACHINE\\HARDWARE\\DESCRIPTION\\System\\BIOS", "BaseBoardProduct") is { } p
                ? p : null) ?? m.Value,
            "$GPU1MODEL" => sensors.FirstOrDefault(x => x.Id.Contains("/gpu-", StringComparison.OrdinalIgnoreCase))?.Hardware ?? m.Value,
            _ => m.Value,
        });
    }

    private static string? Registry(string key, string value)
    {
        try { return Microsoft.Win32.Registry.GetValue(key, value, null) as string; } catch { return null; }
    }

    // ---- parsing helpers ----------------------------------------------------------------------------------------

    private static Dictionary<string, string> ParseLine(string line)
    {
        var d = new Dictionary<string, string>();
        foreach (Match m in TagRx.Matches(line)) d[m.Groups[1].Value] = m.Groups[2].Value; // later tags win
        return d;
    }

    private static string? Tag(string s, string name) => TagRx.Matches(s).FirstOrDefault(m => m.Groups[1].Value == name)?.Groups[2].Value;
    private static bool Flag(Dictionary<string, string> t, string k) => t.TryGetValue(k, out var v) && v.Trim() == "1";
    private static double Num(Dictionary<string, string> t, string k, double def = 0) => TryNum(t, k) ?? def;
    private static double? TryNum(Dictionary<string, string> t, string k) =>
        t.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    private static int Int(Dictionary<string, string> t, string k, int def) => t.TryGetValue(k, out var v) && int.TryParse(v, out var i) ? i : def;
    /// <summary>AIDA sizes are in points at 96 dpi.</summary>
    private static double Px(Dictionary<string, string> t, string k, double defPt) => Math.Round(Num(t, k, defPt) * 96 / 72, 1);
    private static bool IsBold(string? bir) => bir is { Length: > 0 } && bir[0] == '1';
    private static string FontName(string? f) => string.IsNullOrWhiteSpace(f) ? "Arial" : f.Trim();
    private static string Trim(string s) => s.Length > 24 ? s[..24] : s;
    private static string SafeFile(string n) => string.Concat(n.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>AIDA stores colours as BGR integers; returns #AARRGGBB.</summary>
    private static string Color(int bgr) => $"#FF{bgr & 0xFF:X2}{(bgr >> 8) & 0xFF:X2}{(bgr >> 16) & 0xFF:X2}";
    private static string WithAlpha(string argb, byte a) => $"#{a:X2}{argb[3..]}";
}
