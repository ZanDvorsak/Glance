# Building Glance: a developer walkthrough

This walkthrough is for a mid-level developer who wants to understand how Glance was built, why it is shaped the way it is,
and where it could be better. Glance is a Windows app that designs live hardware dashboards and runs them on a small USB LCD.
It replaces the LCD designer in AIDA64.

It assumes you know C# and .NET, and that you have used async/await, JSON serialization and a UI framework. You do not need to
know USB, Skia or Avalonia: each is explained where it first appears.

**Contents**

1. [The problem](#1-the-problem)
2. [Technologies at a glance](#2-technologies-at-a-glance)
3. [High-level walkthrough](#3-high-level-walkthrough)
4. [Detailed code review](#4-detailed-code-review)
   - 4.1 [Talking to the display over USB](#41-talking-to-the-display-over-usb)
   - 4.2 [From pixels to the wire: RGB565](#42-from-pixels-to-the-wire-rgb565)
   - 4.3 [The display loop](#43-the-display-loop-displaypusher)
   - 4.4 [The theme model](#44-the-theme-model)
   - 4.5 [The renderer](#45-the-renderer)
   - 4.6 [Sensors and the two-process split](#46-sensors-and-the-two-process-split)
   - 4.7 [The runner and its control pipe](#47-the-runner-and-its-control-pipe)
   - 4.8 [Undo and redo with snapshots](#48-undo-and-redo-with-snapshots)
   - 4.9 [Groups: moving and resizing many widgets as one](#49-groups-moving-and-resizing-many-widgets-as-one)
   - 4.10 [Sharing themes safely: the `.glance` package](#410-sharing-themes-safely-the-glance-package)
   - 4.11 [Importing AIDA64 themes](#411-importing-aida64-themes)
   - 4.12 [Presets and templates](#412-presets-and-templates)
   - 4.13 [The setup check](#413-the-setup-check)
   - 4.14 [Video backgrounds with FFmpeg](#414-video-backgrounds-with-ffmpeg)
   - 4.15 [Tests and CI](#415-tests-and-ci)
   - 4.16 [Packaging, signing and releasing](#416-packaging-signing-and-releasing)
5. [How the project evolved](#5-how-the-project-evolved)
6. [Self-review: five ways to improve the code](#6-self-review-five-ways-to-improve-the-code)

---

## 1. The problem

The display is a cheap 3.5" 480x320 USB screen (USB ID `1908:0102`) that is often sold to put inside a PC case. AIDA64 can drive it,
but its designer is dated and AIDA64 is paid software. The goal was a modern replacement with these features:

- **Live sensors**: CPU, GPU, motherboard, fans, drives and network, with no AIDA64 needed.
- **Rich visuals**: images, animated GIFs and video backgrounds, gauges, graphs and custom fonts.
- **A modern editor**: drag and drop, undo, multiple pages and premade widgets.
- **Keeps running**: the theme stays on the screen after the editor is closed.
- **Windows first.**

The hard constraints came from the hardware. There was no documentation, so the protocol had to be worked out from a USB capture.
The display turned out to accept only whole frames, at about 2.5 frames per second. That one measurement shaped many later decisions.

---

## 2. Technologies at a glance

| Area | Technology | Why it was chosen |
|---|---|---|
| Language and runtime | **C# 12 / .NET 8** | Mature, fast and good for both native interop and UI. `unsafe` code is allowed where pixel loops need it. |
| Editor UI | **Avalonia 12** (Fluent theme, ColorPicker) | Modern cross-platform XAML-family UI. The UI is **built in code**: there is no XAML except `App.axaml`. |
| Tray app (runner) | **WinForms** (`net8.0-windows`) | Its `NotifyIcon` and `ApplicationContext` are the simplest way to make a tray-only app. |
| 2D rendering | **SkiaSharp** | The same renderer draws the editor preview, the real display and the virtual display, and works without a window. |
| Hardware sensors | **LibreHardwareMonitorLib** + the **PawnIO** driver | Open-source access to CPU, GPU, motherboard, fan and drive sensors. Reading CPU and motherboard data needs admin rights. |
| USB | **libusb-win32** (`libusb0.dll`) through P/Invoke | The display enumerates as a vendor device. AIDA64 uses libusb-win32 with the same device, and Zadig installs it. |
| MVVM helpers | **CommunityToolkit.Mvvm** | `[ObservableProperty]` source-generates `INotifyPropertyChanged` boilerplate for the model. |
| Video | **FFmpeg** (child process) | Decodes any video format to raw BGRA frames on stdout. It is optional and found automatically. |
| Inter-process communication | **Named pipes** with ACLs | Sends sensors and commands between the elevated runner and the normal-user editor. |
| Tests | **xUnit** (253 tests) | Cover all hardware-free logic, including pixel-level rendering checks. |
| CI | **GitHub Actions** (`windows-latest`) | Builds and tests on every push. |
| Packaging | `dotnet publish` (self-contained, ReadyToRun), **Inno Setup 6**, `Set-AuthenticodeSignature` | Produces a zip, an installer and signed executables. |
| Releases | **GitHub CLI** (`gh release create`) | Publishes the zip and installer. |

---

## 3. High-level walkthrough

### 3.1 The solution layout

```
src/
  Glance.Core/        everything that is not UI: USB, renderer, theme model, sensors, presets, importer, setup checks
  Glance.App/         Avalonia editor (MainWindow.cs is the editor's brain)
  Glance.Runner/      elevated WinForms tray app that owns the display and serves sensors
  Glance.SensorHost/  older standalone sensor feed, replaced by the runner
  Glance.Tests/       xUnit tests for Glance.Core
  Glance.Demo/, UsbProbe/   small diagnostic tools that are not shipped
build/      publish.ps1, make-installer.ps1, sign.ps1
installer/  Inno Setup script
docs/       PROTOCOL.md, TUTORIAL.md, this file
```

The most important rule is that **logic goes in `Glance.Core`, not in the UI**. `Glance.App` has no automated tests, so anything that
can be decided without a window (page rotation, group scaling, setup diagnosis, import mapping) lives in Core, where it is tested.
You will see this pattern again and again below.

### 3.2 Two processes

Windows lets only administrators read CPU and motherboard sensors. An editor running as admin, however, causes trouble: drag and drop
from Explorer stops working, file dialogs run elevated, and it is bad practice in general. So Glance is split into two processes:

```
Glance.Runner (admin, tray)                          Glance.App (normal user, editor)
  LocalSensorSource -> SensorPipeServer  --pipe "Glance.Sensors" (1 JSON line/s)-->  PipeSensorClient -> SensorHub
  DisplayPusher (renders + sends frames)  <--pipe "Glance.Runner.Control"----------  RunnerClient
  watches %AppData%\Glance for theme/settings changes and reloads
```

- The **runner** reads sensors, owns the USB display, draws the saved theme and publishes sensor values on a pipe.
  It also accepts commands from the editor: reload, pause, change page, brightness, and "show this unsaved theme".
- The **editor** designs themes. It gets sensor values from the runner's pipe, or falls back to basic local sensors when the runner is not running.
  It can also drive the display itself when there is no runner.

### 3.3 The data flow for one frame

1. `DisplayPusher` (in the runner) asks for the current `Theme` and works out which page to show.
2. `ThemeRenderer` draws that page's widgets onto an `SKCanvas`, reading the latest sensor values.
3. `FrameConverter` turns the BGRA bitmap into RGB565 big-endian bytes.
4. If the bytes differ from the last frame sent, `Ax206Display.Blit` sends them over USB (about 400 ms).

The editor's preview and the virtual display window run steps 1 and 2 with the **same renderer**, which is why the preview is
exactly what the display shows.

### 3.4 The user's journey

1. **Setup check**: driver, display, PawnIO, runner and FFmpeg, each with a one-click fix.
2. **New theme** from a template wired to the sensors on *this* PC, or **Import AIDA64** to convert an `.axlcd` file.
3. **Edit**: drag, resize and group widgets, pick sensors, add premade widgets from the gallery, add pages and backgrounds.
4. **Send to display** for a live preview, **Save** (the runner reloads the file automatically).
5. **Background service** starts the runner, optionally at sign-in using a scheduled task.
6. **Export** to a `.glance` file to share the theme.

---

## 4. Detailed code review

### 4.1 Talking to the display over USB

**File:** `src/Glance.Core/Usb/Ax206Display.cs`, `Libusb0.cs`, `docs/PROTOCOL.md`

The protocol was worked out from a USBPcap capture of AIDA64. It turned out to be a *mass-storage-style wrapper*: every command is a
31-byte "Command Block Wrapper" (CBW) that holds a 16-byte command (CDB), then optional data, then a 13-byte status reply (CSW).

The P/Invoke layer is deliberately small: only the libusb-0.1 functions that are needed, all with `Cdecl`:

```csharp
internal static unsafe class Libusb0
{
    private const string Dll = "libusb0.dll";
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr usb_open(IntPtr dev);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int usb_claim_interface(IntPtr h, int iface);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int usb_bulk_write(IntPtr h, int ep, byte* data, int size, int timeoutMs);
    // ...
    // libusb-win32 x64 struct layouts (LIBUSB_PATH_MAX = 512)
    private const int BusDevicesOffset = 16 + PathMax;          // after next, prev, dirname
```

> **Review note:** device enumeration walks libusb's linked lists using hand-computed **x64 struct offsets** instead of
> `[StructLayout]` structs. It works and it is commented, but it ties the code to x64. That is acceptable because the package is
> `win-x64` only.

Building a command looks like this:

```csharp
private byte[] BuildCbw(byte[] cdb, int dataLen)
{
    var b = new byte[31];
    b[0] = (byte)'U'; b[1] = (byte)'S'; b[2] = (byte)'B'; b[3] = (byte)'C';
    b[4] = 0xDE; b[5] = 0xAD; b[6] = 0xBE; b[7] = 0xEF;       // tag, always DEADBEEF
    BitConverter.GetBytes((uint)dataLen).CopyTo(b, 8);       // data length, little-endian
    b[14] = 16;                                              // CDB length
    Array.Copy(cdb, 0, b, 15, Math.Min(16, cdb.Length));
    return b;
}
```

Sending a frame ("blit") is: write the CBW, write the pixel data in 64 KiB chunks, then read the status:

```csharp
public void Blit(int x, int y, int w, int h, byte[] rgb565Be)
{
    if (rgb565Be.Length != w * h * 2) throw new ArgumentException("Buffer size does not match rectangle.");
    int x1 = x + w - 1, y1 = y + h - 1;
    var cdb = new byte[16];
    cdb[0] = 0xCD; cdb[5] = 6; cdb[6] = 0x12;
    cdb[7] = (byte)x; cdb[8] = (byte)(x >> 8);
    // ... y, x1, y1 the same way
    var cbw = BuildCbw(cdb, rgb565Be.Length);
    Write(cbw, 0, cbw.Length);
    for (int off = 0; off < rgb565Be.Length; off += ChunkSize)
        Write(rgb565Be, off, Math.Min(ChunkSize, rgb565Be.Length - off));
    ReadStatus();
}
```

The API takes a rectangle because the protocol does, but **testing on the real display showed that only full-screen rectangles
work**. A partial blit never gets a status reply: it times out after 3 s and nothing is drawn. The doc comment and `PROTOCOL.md`
both record this, so nobody builds "dirty rectangle" optimizations on top of it. This is a good habit: **write down negative results**,
not only what works.

Two other details worth copying:

- **`TryProbeOpen`** checks whether another program (AIDA64) holds the device. Opening it is not enough, because `usb_open` succeeds
  even when the device is busy. What fails is *claiming the interface*:

  ```csharp
  // opening can succeed even while another program owns the display; claiming the interface is what fails then
  if (Libusb0.usb_claim_interface(h, 0) < 0) return Libusb0.LastError();
  ```

- **`Blank`** sends a black frame and then sets the backlight to 0. Otherwise the display keeps showing its last picture forever after the app exits.

### 4.2 From pixels to the wire: RGB565

**File:** `src/Glance.Core/Rendering/FrameConverter.cs`

Skia renders 32-bit BGRA. The display wants 16-bit RGB565 (5 bits red, 6 green, 5 blue) in **big-endian** order. The byte order was
confirmed by eye: with little-endian, red, green and blue came out as blue, pink and green.

```csharp
for (int y = 0; y < h; y++)
{
    var s = src + y * stride;            // respect RowBytes: rows can be padded
    var d = dst + y * w * 2;
    for (int x = 0; x < w; x++, s += 4, d += 2)
    {
        int r = bgra ? s[2] : s[0];
        int g = s[1];
        int b = bgra ? s[0] : s[2];
        int px = ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3);
        d[0] = (byte)(px >> 8);          // high byte first = big-endian
        d[1] = (byte)px;
    }
}
```

This is `unsafe` pointer code because it runs on 153,600 pixels several times a second. Note that it uses `stride` (`RowBytes`)
instead of `w * 4`: assuming the two are equal is a classic bitmap bug.

### 4.3 The display loop: `DisplayPusher`

**File:** `src/Glance.Core/Display/DisplayPusher.cs`

`DisplayPusher` owns a background task that renders and sends frames. Three ideas are worth studying.

**1. Page rotation is a pure static function.** It takes the current state and the time, and returns the next page. That makes it
trivial to test, and the virtual display window reuses the same function:

```csharp
public static int NextPageIndex(Theme theme, int current, ref long since, long nowMs, int pinned, int step)
{
    int n = theme.Pages.Count;
    if (n <= 1) { since = nowMs; return 0; }
    if (pinned >= 0) { since = nowMs; return Math.Min(pinned, n - 1); }
    if (current >= n) current = 0;                                     // pages were deleted under us
    if (step != 0) { since = nowMs; return ((current + step) % n + n) % n; }   // manual next/prev, wraps both ways
    long dwell = (long)(Math.Max(1, theme.Pages[current].DurationSeconds) * 1000);
    if (theme.AutoRotate && nowMs - since >= dwell) { since = nowMs; return (current + 1) % n; }
    return current;
}
```

Note `((current + step) % n + n) % n`: in C#, `%` can return a negative number, so this is the usual trick to wrap "previous" from page 0 to the last page.

**2. Skip identical frames.** A frame costs about 400 ms of USB time, so sending a clock that only changes once a second at "5 fps"
would waste most of the link. The loop compares the new frame with the last one sent and forces a refresh every 10 s, in case a frame
was ever lost:

```csharp
var rgb = FrameConverter.ToRgb565Be(bmp);
bool unchanged = lastSent is not null && rgb.AsSpan().SequenceEqual(lastSent);
if (!unchanged || frameStart - lastSentAt > ForcedRefreshMs)
{
    _lcd.Blit(0, 0, _lcd.Width, _lcd.Height, rgb);
    lastSent = rgb; lastSentAt = frameStart;
    frames++;
}
```

`Span.SequenceEqual` is vectorized, so comparing 300 KB costs microseconds, which is very little next to 400 ms of USB time.

**3. Cross-thread state without locks.** The UI thread changes brightness, the pinned page and next/prev, while the loop reads them.
These are `volatile int`s, and next/prev uses `Interlocked`, so a button press is consumed exactly once:

```csharp
public void NextPage() => Interlocked.Increment(ref _step);
// in the loop:
_pageIndex = NextPageIndex(theme, _pageIndex, ref pageSince, frameStart, _pinned, Interlocked.Exchange(ref _step, 0));
```

The theme itself is *not* thread-safe: the editor can change it in the middle of a render. The loop handles that by catching
`InvalidOperationException` ("collection was modified") and trying again on the next frame. It is a pragmatic choice; section 6
suggests something better.

The theme is also scaled to fit the panel while keeping its proportions, which is how "other screen sizes" work:

```csharp
float s = Math.Min((float)_lcd.Width / theme.Width, (float)_lcd.Height / theme.Height);
canvas.Translate((_lcd.Width - theme.Width * s) / 2, (_lcd.Height - theme.Height * s) / 2);
canvas.Scale(s, s);
```

`Stop(bool blank)` exists because "close the display" has two meanings. On exit or pause, the screen should go black. On reload or
error recovery, the display is reopened straight away, and blanking it would cause a visible flicker.
Windows shutdown, restart, sign-out and sleep also blank it: see `SessionPowerWatcher`, `DisplayPowerPolicy` and `PowerBlank` in
`src/Glance.Core/Display/`, and `docs/walkthroughs/shutdown-blank.md`.

### 4.4 The theme model

**Files:** `src/Glance.Core/Themes/{Theme,ThemePage,Widget}.cs`

A theme contains pages, and each page contains widgets. The key design decision is that **`Widget` is a single flat class** with a `Kind` enum,
not a class hierarchy:

```csharp
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WidgetKind { Text, Sensor, Clock, Bar, Gauge, Graph, Image, Panel }

public partial class Widget : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [ObservableProperty] private string _name = "Widget";
    [ObservableProperty] private WidgetKind _kind = WidgetKind.Text;
    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    [ObservableProperty] private string _sensorId = "";
    [ObservableProperty] private bool _italic;
    [ObservableProperty] private string _fontFile = "";
    [ObservableProperty] private string _group = "";
    // ... about 35 properties in total

    [JsonIgnore] public SKRect LastBounds { get; set; }   // filled by the renderer, used for hit-testing
}
```

The trade-off:

- **Pros:** JSON is trivial, with no polymorphic serialization. The property editor can be reflection-based. Changing a widget's kind
  needs no conversion code. One `Clone()` covers everything.
- **Cons:** every widget carries properties it does not use, and nothing stops you setting `Sweep` on a `Text` widget.

`[ObservableProperty]` comes from CommunityToolkit.Mvvm. You write a private field, and a source generator writes the public
`Italic` property, raising `PropertyChanged`. The editor relies on those events for undo and repainting.

**JSON compatibility matters**, because users have saved themes. Enums are stored as strings (so reordering an enum is safe), new
properties get defaults, and old files are migrated. Theme files written before pages existed had a top-level `Widgets` array:

```csharp
/// <summary>Only used to read theme files written before pages existed (their widgets sat at the top level).</summary>
[JsonPropertyName("Widgets")] public List<Widget>? LegacyWidgets { get; set; }

private void Normalize()
{
    if (LegacyWidgets is { Count: > 0 } legacy)
    {
        if (Pages.Count == 0) Pages.Add(new ThemePage { Name = "Page 1" });
        foreach (var w in legacy) Pages[0].Widgets.Add(w);
    }
    LegacyWidgets = null;                                    // never written back
    if (Pages.Count == 0) Pages.Add(new ThemePage { Name = "Page 1" });
    ActivePageIndex = Math.Clamp(ActivePageIndex, 0, Pages.Count - 1);
}
```

Meanwhile `Theme.Widgets` is a `[JsonIgnore]` alias for *the page being edited*, so most editor and import code did not have to change
when pages were added.

**Saving adopts assets.** If a widget points at `C:\Users\me\Pictures\bg.gif`, saving copies it into the theme's `assets/` folder
and stores a relative path, so the theme folder is self-contained. The same local function handles images and font files:

```csharp
string Adopt(string path)
{
    if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || !File.Exists(path)) return path;
    Directory.CreateDirectory(assets);
    var name = Path.GetFileName(path);
    var dest = Path.Combine(assets, name);
    var n = 1;
    while (File.Exists(dest) && new FileInfo(dest).Length != new FileInfo(path).Length)   // name clash with a different file
        dest = Path.Combine(assets, $"{Path.GetFileNameWithoutExtension(name)}_{n++}{Path.GetExtension(name)}");
    if (!File.Exists(dest)) File.Copy(path, dest);
    return "assets/" + Path.GetFileName(dest);
}
foreach (var w in Pages.SelectMany(p => p.Widgets))
{
    w.ImagePath = Adopt(w.ImagePath);
    w.FontFile = Adopt(w.FontFile);
}
```

> **Review note:** "same name and same length means same file" is a cheap heuristic. Two different files with the same name and
> size would be treated as one. A content hash would be exact; it was not worth it for user-picked images, but be aware of it.

### 4.5 The renderer

**File:** `src/Glance.Core/Themes/ThemeRenderer.cs`

One `ThemeRenderer` draws one page onto any `SKCanvas`. It does not know whether the canvas belongs to the editor, the USB display,
the virtual display or a unit test. That is the main reason the tests can check real pixels.

The main loop is a `switch` on `Kind`:

```csharp
foreach (var w in page.Widgets)
{
    if (!w.Visible) { w.LastBounds = default; continue; }
    switch (w.Kind)
    {
        case WidgetKind.Panel: DrawPanel(canvas, w); break;
        case WidgetKind.Bar: DrawBar(canvas, w); break;
        case WidgetKind.Gauge: DrawGauge(canvas, w); break;
        case WidgetKind.Graph: DrawGraph(canvas, w); break;
        case WidgetKind.Image: DrawImage(canvas, theme, w); break;
        case WidgetKind.Text: DrawText(canvas, w, w.Text); break;
        case WidgetKind.Clock: DrawText(canvas, w, SafeDate(w.Text)); break;
        case WidgetKind.Sensor: DrawText(canvas, w, w.Text + FormatValue(w), ColorFor(w, Value(w))); break;
    }
}
```

Every `Draw*` method writes `w.LastBounds`. The editor then uses those bounds for clicking, the selection outline and group outlines,
so hit-testing always matches what was drawn, including text, whose size depends on the font.

**Defensive formatting.** Users type format strings, so a bad one must never crash a frame:

```csharp
private string FormatNumber(Widget w)
{
    if (Value(w) is not { } v) return "--";
    try { return v.ToString(w.Format); } catch (FormatException) { return v.ToString("0"); }
}
```

**Thresholds** change colour as the value rises. This is used by sensor text, bars, gauges and graphs:

```csharp
private static SKColor ColorFor(Widget w, float? v)
{
    var normal = Parse(w.Color, SKColors.White);
    if (v is not { } x) return normal;
    if (w.CritAt > 0 && x >= w.CritAt) return Parse(w.CritColor, new SKColor(0xFF, 0x52, 0x52));
    if (w.WarnAt > 0 && x >= w.WarnAt) return Parse(w.WarnColor, new SKColor(0xFF, 0xC1, 0x07));
    return normal;
}
```

**Bars** clip to a rounded rectangle and then fill a plain rectangle, so a partly filled bar still has rounded ends:

```csharp
c.Save();
c.ClipRoundRect(new SKRoundRect(r, rad, rad), antialias: true);
c.DrawRect(w.Vertical ? new SKRect(r.Left, r.Bottom - r.Height * frac, r.Right, r.Bottom)
                      : new SKRect(r.Left, r.Top, r.Left + r.Width * frac, r.Bottom), fill);
c.Restore();
```

**Graphs** keep a per-renderer `SensorHistory` (throttled samples, capped at 15 minutes). They draw a line plus a gradient fill
under it, and auto-fit the Y range when `Max <= Min`. `SeedGraphHistory` fills a new graph with a gentle synthetic curve, so gallery
thumbnails do not look empty.

**Fonts and caches.** Typefaces are expensive to create, so they are cached per renderer, keyed by `(family, bold, italic)`. Custom
font files get their own cache. A font file holds only one style, so bold and italic are *synthesized* for it:

```csharp
private SKFont MakeFont(Widget w, double size, bool bold)
{
    var path = _theme?.ResolveAsset(w.FontFile);
    if (!string.IsNullOrEmpty(path) && FileFace(path) is { } custom)
        return new SKFont(custom, (float)size) { Edging = SKFontEdging.SubpixelAntialias, Embolden = bold, SkewX = w.Italic ? -0.25f : 0 };
    return new SKFont(Face(w.FontFamily, bold, w.Italic), (float)size) { Edging = SKFontEdging.SubpixelAntialias };
}
```

Because of these caches (images, GIF decoders, video processes, graph history, typefaces), **each consumer creates its own renderer and
disposes it**. Sharing one between the editor and the display thread would be a data race.

**Videos are swept.** A video widget starts an `ffmpeg` process. If a widget is deleted, resized or moved to another page, its
process must stop, so each render marks the videos it used, and anything not used for 3 s is killed:

```csharp
private void SweepVideos()
{
    var now = Environment.TickCount64;
    foreach (var k in _videos.Where(kv => now - kv.Value.LastUsedTicks > 3000).Select(kv => kv.Key).ToList())
    {
        _videos[k].Dispose();
        _videos.Remove(k);
    }
}
```

This is a "lease" pattern: the renderer never has to be told that a widget went away.

### 4.6 Sensors and the two-process split

**Files:** `Core/Sensors/{SensorService,SensorPipeServer,PipeSensorClient}.cs`, `App/Services/SensorHub.cs`

`SensorService` wraps LibreHardwareMonitor and flattens its hardware tree into one immutable record type:

```csharp
public sealed record SensorInfo(string Id, string Hardware, string Name, string Type, float? Value, string Unit);
```

The `Id` is LibreHardwareMonitor's identifier, for example `/amdcpu/0/temperature/2`. Widgets store that string, so a theme survives
restarts and the order of sensors does not matter.

**The server** (in the elevated runner) writes the latest snapshot as one JSON line per second to every connected client. Three
design points:

```csharp
var security = new PipeSecurity();
security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
var server = NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.Out, 16,
    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
```

1. **ACL:** only the current Windows user can connect. An elevated process that opens a pipe must think about who else can reach it.
2. **Each client is served on its own task**, and a client that stops reading for 5 s is dropped, so one stuck client cannot block the others.
3. The pipe **name is a constructor parameter**, so tests can use their own names and never collide with a real runner.

**The client** handles staleness, which was a real bug once: when the runner stopped, the editor kept showing the last values as if
they were live. Now `HasData` is only true while snapshots keep arriving:

```csharp
public bool HasData
{
    get { lock (_lock) return _snapshot.Count > 0 && Environment.TickCount64 - _lastUpdateMs < _staleAfterMs; }
}
```

**`SensorHub`** (in the editor) picks the best source. It uses the runner's feed when it is live, and otherwise starts a local
`SensorService`, which works without admin rights but gives fewer sensors. It stops the local one again when the runner comes back.
Drive-space sensors from `DriveSensors` are merged in every 5 s:

```csharp
private void Merge()
{
    var baseList = _pipe.HasData ? _pipe.Snapshot : _local?.Snapshot ?? Array.Empty<SensorInfo>();
    if (Environment.TickCount64 - _drivesAt > 5000) { _drives = DriveSensors.Read(); _drivesAt = Environment.TickCount64; }
    _merged = baseList.Concat(_drives).ToList();
}
```

Note that `_merged` is replaced wholesale and never mutated. The renderer checks `ReferenceEquals(snapshot, _indexedFor)` to decide
whether to rebuild its id-to-sensor dictionary, so an immutable snapshot doubles as a cheap "has anything changed?" check.

### 4.7 The runner and its control pipe

**File:** `src/Glance.Runner/RunnerContext.cs`

The runner is a WinForms `ApplicationContext` with no main window: a tray icon, a menu and some timers. Its jobs:

- **Supervise the display.** Every 2 s, open the display if it is not open yet. If the display keeps failing for 6 s, reopen it.
  This handles unplugging, plugging back in and AIDA64 taking the device:

  ```csharp
  else if (_pusher.LastError is not null)
  {
      _failingSince ??= DateTime.UtcNow;
      if (DateTime.UtcNow - _failingSince > TimeSpan.FromSeconds(6)) { Log("Display kept failing; reopening: " + _pusher.LastError); DisposePusher(blank: false); }
  }
  ```

- **Watch files.** A `FileSystemWatcher` on `%AppData%\Glance` reloads the theme when the editor saves. Editors often write a file in
  several steps, so events are **debounced** with a 700 ms timer:

  ```csharp
  FileSystemEventHandler h = (_, _) => _ui.BeginInvoke(() => { _reloadDebounce.Stop(); _reloadDebounce.Start(); });
  ```

- **Answer the control pipe.** This is a small line-based text protocol, which is easy to debug by hand:

  ```csharp
  switch (parts.Length > 0 ? parts[0].ToUpperInvariant() : "")
  {
      case "STATUS": ...
      case "RELOAD": _ui.BeginInvoke(Reload); return "OK";
      case "PAGE" when parts.Length > 1 && int.TryParse(parts[1], out var pg):   // PAGE 0 releases the pin
          lock (_gate) { _pinPage = pg > 0 ? pg - 1 : null; if (_pusher is not null) _pusher.PinnedPage = _pinPage; }
          return "OK";
      case "LIVE" when parts.Length == 3:
      {
          // LIVE <base64 folder> <base64 theme json>: show the editor's unsaved theme until LIVE_CLEAR
          var folder = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
          var json = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
          var t = new Theme { BaseFolder = string.IsNullOrEmpty(folder) ? null : folder };
          t.ApplySnapshot(json);
          lock (_gate) _live = t;
          return "OK";
      }
      // ...
  }
  ```

  Base64 makes the arguments safe in a space-separated, newline-terminated protocol. `LIVE` is how **Send to display** works while the
  runner owns the device: only one process can hold the USB display, so the editor sends its theme to the runner instead of opening it.
  On the editor side, `PushLive` only sends when the snapshot actually changed.

- **Blank on shutdown and sleep.** A `SessionPowerWatcher` (a hidden top-level window on its own thread) reports Windows shutdown,
  sign-out, sleep and wake. The runner blanks the display within a time limit, quits at a session end, and reopens the display after
  wake through `Supervise`. See `docs/walkthroughs/shutdown-blank.md`.

Two Windows details worth knowing:

- **Opening the editor from an elevated process without elevating it:** an elevated process starts the editor through `explorer.exe`,
  which runs it with the normal user token:

  ```csharp
  Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = true });
  ```

- **Marshalling onto the UI thread:** `_ = _ui.Handle;` forces a WinForms control handle to exist, so pipe and watcher threads can
  call `_ui.BeginInvoke(...)`.

### 4.8 Undo and redo with snapshots

**Files:** `src/Glance.Core/Editing/UndoManager.cs`, `MainWindow.cs` (`HookAll`, `OnModelChanged`)

Instead of the Command pattern (one undoable command class per kind of edit), Glance stores **whole-theme JSON snapshots**:

```csharp
public bool Commit(string snapshot)
{
    if (snapshot == _current) return false;   // no real change, no step
    _undo.Add(_current);
    if (_undo.Count > Limit) _undo.RemoveAt(0);
    _redo.Clear();
    _current = snapshot;
    return true;
}
```

Why this works well here: a theme is a few KB of JSON, so 200 snapshots are cheap. Every edit becomes undoable for free, including
page changes, imports and group scaling. Each new feature gets undo without extra work.

The editor subscribes to `PropertyChanged` on the theme, every page and every widget (`HookAll`), and **debounces** changes, so
typing "1234" into a box is one undo step, not four:

```csharp
private void OnModelChanged(object? s, PropertyChangedEventArgs e)
{
    if (_restoring || _dragging || _handle != Handle.None) return;  // drags commit on release
    _commitTimer.Stop(); _commitTimer.Start();
}
```

Notice the `_restoring` flag. Applying an undo snapshot raises `PropertyChanged` too, and without the guard, undoing would record a
new step. `ApplySnapshot` also keeps the same `Theme` *instance* (it copies fields and replaces pages), so references held by the
pusher and the renderer stay valid.

### 4.9 Groups: moving and resizing many widgets as one

**Files:** `src/Glance.Core/Themes/GroupOps.cs`, `Handles.cs`, `MainWindow.cs`

A premade "CPU card" is a panel, a label, a value and a bar. Users expect to drag and resize it as one object. The model is minimal:
widgets that share a `Group` string belong together. All the logic is in Core so it can be tested:

```csharp
public static List<Widget> Members(IEnumerable<Widget> page, Widget w)
{
    if (string.IsNullOrEmpty(w.Group)) return new List<Widget> { w };
    var list = page.Where(x => x.Group == w.Group).ToList();
    if (!list.Contains(w)) list.Add(w);
    return list;
}
```

**Resizing a group** uses `Scaler`. It takes a snapshot of every member when the drag starts, and each mouse move lays everything
out again *from that snapshot*:

```csharp
public void Apply(SKRect target)
{
    double sx = _box.Width > 0 ? target.Width / _box.Width : 1;
    double sy = _box.Height > 0 ? target.Height / _box.Height : 1;
    double s = Math.Min(sx, sy);   // text, line and corner sizes follow the smaller factor so nothing overflows

    foreach (var st in _start)
    {
        var w = st.W;
        w.X = Math.Round(target.Left + (st.X - _box.Left) * sx);
        w.Y = Math.Round(target.Top + (st.Y - _box.Top) * sy);
        if (Handles.IsFree(w))
        {
            w.Width = Math.Max(1, Math.Round(st.Width * sx));
            w.Height = Math.Max(1, Math.Round(st.Height * sy));
        }
        if (w.Kind is WidgetKind.Text or WidgetKind.Sensor or WidgetKind.Clock or WidgetKind.Gauge)
            w.FontSize = Math.Clamp(Math.Round(st.FontSize * s, 1), 4, 400);
        // ... gauge thickness, panel and bar corner radius
    }
}
```

**Why snapshot instead of applying small changes?** If each mouse move scaled the *current* values by a small factor, rounding
errors would add up, and dragging out and back would not return to the original layout. Recomputing from the start state makes the
operation *idempotent*. A test checks exactly this:

```csharp
[Fact]
public void Scaling_back_to_the_start_restores_the_original_layout()
{
    var (_, card, label, _) = Sample();
    var members = new[] { card, label };
    var box = GroupOps.Union(members);
    var scaler = new GroupOps.Scaler(members, box);
    scaler.Apply(new SKRect(box.Left, box.Top, box.Left + 333, box.Top + 77));
    scaler.Apply(box);
    Assert.Equal((20, 20, 100, 60, 10), (card.X, card.Y, card.Width, card.Height, card.CornerRadius));
    Assert.Equal((30, 30, 20), (label.X, label.Y, label.FontSize));
}
```

`DragRect` turns "which handle is being dragged, and where is the pointer" into a new rectangle, with an optional fixed aspect ratio
(Shift) and a minimum size. Both single widgets and groups use it, so they behave the same way.

The editor side stays thin. On pointer press it decides whether a handle was hit on the selection box (the group's outline, or the
widget's own bounds), then creates the scaler:

```csharp
var (box, free, members) = SelectionBox();
var h = Handles.HitTest(box, free, (float)p.X, (float)p.Y, ThemeRenderer.HandleSize, 3);
if (h != Handle.None)
{
    _handle = h; _startRect = box; _startFont = _selected.FontSize;
    _groupScaler = members.Count > 1 ? new GroupOps.Scaler(members, box) : null;
    e.Pointer.Capture(_preview);
    return;
}
```

**Group** and **Ungroup** use a multi-select layer list. The new name comes from `GroupOps.NewGroupName`, which fills the first free
`Group N` across *all* pages, so groups on different pages never merge by accident.

### 4.10 Sharing themes safely: the `.glance` package

**File:** `src/Glance.Core/Themes/ThemePackage.cs`

A `.glance` file is a zip containing `theme.json` and an `assets/` folder. **Export** works on a *copy* of the theme (made with
the snapshot functions), so the open theme keeps its own paths. It packs each referenced file once, renaming entries when names clash.

**Import** is the security-sensitive part, because the file may come from a stranger. A malicious zip can contain entries like
`../../AppData/Roaming/Microsoft/Windows/Start Menu/Programs/Startup/evil.bat` ("Zip Slip"). The importer validates every entry
**before writing anything**:

```csharp
var root = Path.GetFullPath(destFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
foreach (var entry in zip.Entries)
{
    var name = entry.FullName.Replace('\\', '/');
    if (name != ThemeEntry && !name.StartsWith("assets/", StringComparison.Ordinal))
        throw new InvalidDataException($"Unexpected file in theme: {name}");               // allow-list
    if (name.Contains("..") || Path.IsPathRooted(name) || name.Contains(':'))
        throw new InvalidDataException($"Unsafe path in theme: {name}");                   // cheap checks
    var target = Path.GetFullPath(Path.Combine(destFolder, name.Replace('/', Path.DirectorySeparatorChar)));
    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException($"Unsafe path in theme: {name}");                   // the real check
    total += entry.Length;   // declared size; also re-checked while copying, since it can lie
    if (total > MaxTotalBytes) throw new InvalidDataException("This theme file is too large.");
    plan.Add((entry, target));
}
```

Several layers of defence work together:

1. **Allow-list**: only `theme.json` and `assets/*` are accepted.
2. **Cheap rejections**: `..`, rooted paths and drive letters (`:`).
3. **The canonical check**: resolve the full path and make sure it is still inside the destination. Note the trailing separator on
   `root`, so `C:\Themes\abc` cannot match `C:\Themes\abcd-evil`.
4. **Zip-bomb limits**: at most 500 entries and 1 GB, checked against the declared sizes *and* again while copying, because the
   declared sizes can lie.
5. **Plan first, then write**, so a bad entry late in the archive leaves nothing half-extracted.
6. **Validate the result** by loading `theme.json` as a theme.

The tests try each attack, including both slash styles:

```csharp
[Theory]
[InlineData("assets/../../evil.txt")]
[InlineData("assets/..\\..\\evil.txt")]
[InlineData("../evil.txt")]
public void Path_traversal_is_refused_and_nothing_is_written(string entry) { ... }
```

### 4.11 Importing AIDA64 themes

**Files:** `src/Glance.Core/Import/{AidaImporter,AidaSensorMap}.cs`

An `.axlcd` file is Latin-1 text with one item per line, using tags like `<ITMX>12</ITMX><ID>TCPU</ID>`. Colours are BGR integers.
The importer:

- parses each line with a source-generated regex (`[GeneratedRegex]` compiles it at build time):

  ```csharp
  [GeneratedRegex("<([A-Z0-9]+)>(.*?)</\\1>", RegexOptions.Singleline)] private static partial Regex TagRegex();
  ```

- turns each `<LCDPAGEn>` section into a Glance page,
- maps AIDA item codes (`TCPU`, `SGPU1UTI`, ...) to the sensors on this PC through `AidaSensorMap`, including unit conversion
  multipliers,
- returns a result object with notes (`AidaImportResult`), so the UI can show a **preview of what was mapped and skipped before
  anything is imported**.

One honest touch: AIDA64's fonts are narrower than Segoe UI, so right-aligned values sometimes overlapped their labels after
import. `SeparateLabelsFromValues` measures the widest likely value for each unit (`"100"` for %, `"0000"` for MHz) and moves values to
the right just enough, without pushing them off screen. It then reports what it moved in the notes.

### 4.12 Presets and templates

**Files:** `src/Glance.Core/Presets/{WidgetPresets,ThemeTemplates}.cs`

A **preset** (in the Widget gallery) is a function from "this PC's sensors and a style" to a group of widgets laid out from (0, 0):

```csharp
public PresetResult Build(IReadOnlyList<SensorInfo> sensors, PresetOptions options) => _builder(new Ctx(sensors, options));
public sealed record PresetResult(List<Widget> Widgets, double Width, double Height, List<string> Missing);
```

The `Ctx` helper finds sensors by hardware, type and name, from the most to the least specific, and records what it could not find:

```csharp
var candidates = Sensors.Where(s => s.Type == type && idHas.Any(h => s.Id.Contains(h, StringComparison.OrdinalIgnoreCase))).ToList();
foreach (var n in names)
{
    hit = candidates.FirstOrDefault(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
          ?? candidates.FirstOrDefault(s => s.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
    if (hit is not null) break;
}
hit ??= candidates.FirstOrDefault();
if (hit is null) { Missing.Add(what); return ("", 1); }
```

This is why a template "just works" on both AMD and Intel PCs. When something is missing, the user is told which sensor to assign
instead of seeing a silent `--`. **Templates** put presets together into whole themes. Tests build *every* preset on a realistic
fake PC and on a PC with no sensors, so no preset can crash on unusual hardware.

### 4.13 The setup check

**File:** `src/Glance.Core/Setup/SetupChecks.cs`

The first-run experience is often where a hardware app fails. The setup check reads the system (it never changes it) and returns
`CheckResult` records, each with an optional one-click fix (`winget install`, open a URL, or open the service dialog).

The decision logic is a **pure function**, separate from the I/O that gathers the facts:

```csharp
/// <summary>Pure decision logic for the display row, separated so it can be tested without hardware.</summary>
public static CheckResult DiagnoseDisplay(bool dllPresent, bool found, bool runnerRunning, string? openError, bool registered, string? driverService)
```

The I/O part reads `libusb0.dll`, enumerates USB, probes for an open, and reads the driver service from
`HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_1908&PID_0102`. It then passes plain booleans and strings to the function above. That
split turns "the display is plugged in but uses the wrong driver" into a one-line unit test.

The editor runs the checks in `Task.Run`, because USB enumeration and registry reads must not block the UI thread.

### 4.14 Video backgrounds with FFmpeg

**File:** `src/Glance.Core/Media/VideoSource.cs`

Rather than binding a video library, Glance starts `ffmpeg`, asks it to scale and crop to the widget size, and reads raw BGRA frames from stdout:

```csharp
// -re reads the file at its native speed; without it ffmpeg decodes as fast as the CPU allows and the video plays far too fast
foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-stream_loop", "-1", "-re", "-i", path, "-an",
                          "-vf", $"{scale},fps={fps}", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" })
    psi.ArgumentList.Add(a);
```

Points to notice:

- **`ArgumentList`** instead of one argument string: the file path is passed exactly as it is, with no quoting or injection problems.
- **`-re`** was a real bug fix: without it the video played several times too fast.
- Fit modes are FFmpeg filter chains (`force_original_aspect_ratio=decrease,pad=...` for Contain, `increase,crop=...` for Cover), so
  the renderer draws the frame as it arrives.
- The reader loop fills a whole frame (`while (got < buf.Length)`) before it publishes one, because a pipe `Read` can return fewer bytes than asked for.
- `Kill(true)` kills the whole process tree when the video is disposed.
- **Which `ffmpeg.exe`** (`src/Glance.Core/Media/Ffmpeg.cs`, using `src/Glance.Core/Security/FolderTrust.cs`): the editor takes the first copy it finds
  (app folder, `PATH`, winget's machine and user folders). The runner is elevated, so it only takes a copy that no
  non-admin can replace or change: the file, its folder and every folder above it must be owned by an admin principal
  and grant no write, delete or permission rights to anyone else, and none may be a link or junction. Otherwise any
  program running as the user could plant an `ffmpeg.exe` and get it started as administrator. Rejected copies are
  logged to `runner.log`, the setup check explains a per-user-only install, and a video that had no usable FFmpeg is
  tried again every 5 s so a fresh install shows up without a reload.

### 4.15 Tests and CI

**Folder:** `src/Glance.Tests` (253 tests, about 2 seconds, no hardware needed)

The test strategy follows from the architecture: **everything that can be decided without hardware is in Core, and Core is tested.**

- **`Fake.Typical()`**: a realistic LibreHardwareMonitor-style sensor list (Ryzen with 8 cores, RTX GPU, NVMe, network, drives)
  with real ID formats. Presets, templates and the importer are tested against it.
- **Pixel tests**: `Fake.Render(theme, sensors)` renders into an `SKBitmap`, and the tests check real pixel colours
  (`Fake.Near(color, r, g, b)`). For example, a bar at 50% is coloured up to the middle, and a value over `CritAt` turns red.
- **Pure-function tests** for page rotation, setup diagnosis, undo, group scaling and drag rectangles. Their names read like specifications:

  ```csharp
  [Fact]
  public void Each_page_uses_its_own_duration_and_wraps_around()
  {
      var t = ThreePages(); long since = 0; int page = 0;
      page = Next(t, page, ref since, 2100); Assert.Equal(1, page);          // A (2 s) -> B
      page = Next(t, page, ref since, 4000); Assert.Equal(1, page);          // B needs 3 s: not yet
      page = Next(t, page, ref since, 5200); Assert.Equal(2, page);          // B -> C
      page = Next(t, page, ref since, 6300); Assert.Equal(0, page);          // C (1 s) -> back to A
  }
  ```

- **Security tests** for `.glance` import, as shown above.
- **Pipe tests** use private pipe names, so they never interfere with a running runner.

A useful habit used here is the **mutation check**: after writing a test, break the code on purpose and confirm the test fails. This
found a real gap: the rule that marks the sensor feed as stale after 5 s had no test.

CI (`.github/workflows/ci.yml`) is short: `windows-latest`, `setup-dotnet 8.0.x`, build, test. Anything involving the physical display,
real sensors, drivers or installers cannot run in CI. It was checked by hand, and is reported as unverified when it was not.

### 4.16 Packaging, signing and releasing

**Files:** `build/publish.ps1`, `build/make-installer.ps1`, `build/sign.ps1`, `installer/Glance.iss`

- **One version number**, in `src/Directory.Build.props`, read by every project and by the scripts:

  ```powershell
  [xml]$props = Get-Content (Join-Path $src 'Directory.Build.props')
  $version = $props.Project.PropertyGroup.Version
  ```

- **Editor and runner are published into the same folder**, so shared DLLs exist only once and each process can find the other next to itself.
  The default is self-contained with ReadyToRun (about 215 MB folder, about 90 MB zip, no .NET needed on the target PC).
- **The zip step retries**, because antivirus scanners often lock freshly written DLLs for a moment. That is a real-world detail that
  is easy to miss.
- **Signing** (`-Sign`) uses a self-signed code-signing certificate stored in `CurrentUser\My`, or a real certificate chosen by
  thumbprint through `$env:GLANCE_SIGN_THUMBPRINT`. It tries a timestamp server first and signs without one when offline:

  ```powershell
  try { $r = Set-AuthenticodeSignature -FilePath $file -Certificate $cert -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com' }
  catch { }
  if (-not $r -or -not $r.SignerCertificate) {
      # no network for the timestamp server: sign without a timestamp
      $r = Set-AuthenticodeSignature -FilePath $file -Certificate $cert -HashAlgorithm SHA256
  }
  ```

  **Be honest about what this buys you:** a self-signed signature proves the files were not changed after signing, but Windows does
  not trust the certificate, so **SmartScreen still warns**. Removing that warning needs a certificate from a trusted authority
  (Azure Trusted Signing, an OV or EV certificate, or SignPath for open-source projects).

- **Releases**: `git tag v0.1.1`, push, then `gh release create v0.1.1 <installer> <zip> --verify-tag`. After uploading, the SHA-256 of
  each asset was compared with the local file.

---

## 5. How the project evolved

The commit history shows the order in which things were built. Each step stood on the one before it:

| Commit | What it added | What drove it |
|---|---|---|
| `328bfb7` | Initial import (USB driver, renderer, editor, runner) | The core idea |
| `56a14f0` | Rename MiniLcd to Glance (with a migration of `%AppData%\MiniLcd`) | Product name; existing users' data had to move with it |
| `37537a4` | Packaging: icon, version, publish script, installer | Making it installable |
| `222eacf` | Fix video playback speed (`-re`) | Bug found by watching the display |
| `befa302` | Skip identical frames; document that partial updates do not work | A measurement on the real hardware |
| `5bcc832` | First-run setup check | Drivers are where most users get stuck |
| `35ea8d1` | Starter themes, more premade widgets, `.glance` sharing | An empty canvas is daunting |
| `8a63e18` | `AGENTS.md`, README | Making the project easy to pick up |
| `e72b4c8` | Test suite and CI | Locking in behaviour before more features |
| `a20bf70` | Tutorial with screenshots; small-window and sensor-filter fixes | User-facing polish |
| `617fb76` | Optional code signing | Release preparation |
| `9e5a3ee` | Custom font files and italic | A user request |
| `a9e65bc` | Scrollable panels; group resize; Group/Ungroup | User reports: "resizing only resizes one item" |
| `0fb5c05` | Version 0.1.1 | Release |

Two of the more instructive debugging stories from this work:

- **The properties panel would not scroll.** It was a `ScrollViewer` subclass. In Avalonia, a subclass does not automatically get the
  base control's template (styles are matched by exact type), so it rendered with no scrollbars and reported an extent of 0. The fix
  is one line: `protected override Type StyleKeyOverride => typeof(ScrollViewer);`. The diagnosis came from logging the extent in
  `LayoutUpdated`, rather than guessing.
- **The sensor filter showed stale or blank rows.** The first version hid and reused `ListBoxItem`s inside a virtualizing list, and
  the list did not reliably refresh them. The fix was to build fresh items for exactly the rows that pass the filter and replace
  `ItemsSource`. It is simpler, and with a few hundred rows it is fast enough. A comment in `SensorBrowser.cs` records why, so nobody
  "optimizes" it back.

---

## 6. Self-review: five ways to improve the code

These are the changes I would make next, roughly in order of value. Each names the place in the code and the reason.

### 1. Break up `MainWindow.cs` and move editor interaction logic into Core

`MainWindow.cs` is about 1,200 lines. It builds the layout and also handles selection, dragging, nudging, resizing, layer
reordering, grouping, undo wiring, live push and file I/O. The pure parts already moved out (`GroupOps.DragRect`, `GroupOps.Scaler`),
which shows the way, but the interaction *state machine* is still in the window:

```csharp
private void ResizeTo(Point p, bool keepAspect)
{
    var w = _selected!;
    ...
    bool left = _handle is Handle.TopLeft or Handle.Left or Handle.BottomLeft;   // computed, then unused on the group path
    bool top = _handle is Handle.TopLeft or Handle.Top or Handle.TopRight;
    if (_groupScaler is not null) { _groupScaler.Apply(GroupOps.DragRect(r, _handle, px, py, keepAspect, 8)); return; }
```

There are smaller signs of the same strain. The layer-list index mapping `_theme.Widgets[_theme.Widgets.Count - 1 - i]` is repeated
in `GroupSelected` and `UngroupSelected`. A leftover `<summary>` comment above `SelectionBox()` at `MainWindow.cs:1131` describes a
different method.

**Suggestion:** add a `Glance.Core.Editing.EditorSession` (or `CanvasInteraction`) class that owns the selection, the
`Press/Move/Release(point, modifiers)` transitions, the drag members and the scaler. The window would then only translate Avalonia
events into calls on it. Those transitions, the most bug-prone code in the editor, would become unit-testable: "Alt-drag moves one
member", "Shift-corner keeps the aspect ratio", "release commits one undo step".

### 2. Render from immutable snapshots instead of the live, mutable theme

`DisplayPusher` renders `_theme()`, the same object the editor is changing on the UI thread. Races are survived by catching
`InvalidOperationException` and skipping the frame:

```csharp
catch (InvalidOperationException) { /* theme edited mid-frame; try again */ }
```

This only catches the races that happen to throw. A widget whose `X` has been updated but whose `Width` has not yet will render a torn frame
with no exception. **Suggestion:** give the pusher an immutable snapshot. Either publish a deep copy whenever the theme changes, or
reuse the JSON snapshot the editor already builds for undo and `LIVE`. Swap it in with a single `Volatile.Write`. The runner's `LIVE`
path already works this way.

While in that loop, two smaller fixes:

- `FrameConverter.ToRgb565Be` allocates a new 300 KB array for every frame, which creates Large Object Heap garbage several times a
  second. Converting into one of two reusable buffers (current and last sent) would remove the allocation and keep the comparison.
- The loop uses `Thread.Sleep` inside `Task.Run`, which ties up a thread-pool thread forever. A dedicated `Thread`, or
  `await Task.Delay` with a `PeriodicTimer`, would say what it is doing more clearly.

### 3. Make `Widget.Clone()` and the reflection-based property editor safer

`Widget.Clone()` uses `MemberwiseClone()`:

```csharp
public Widget Clone()
{
    var c = (Widget)MemberwiseClone();
    c.Id = Guid.NewGuid().ToString("N");
    c.Name = Name + " copy";
    return c;
}
```

`Widget` derives from `ObservableObject`, whose `PropertyChanged` event is a field-like event. `MemberwiseClone` copies its backing
delegate, so **a copy probably starts out with the original's subscribers attached**. The editor's change hooks and any bindings
would then hear the copy's changes as if they came from the original. It also copies `LastBounds`. I have not confirmed a visible
symptom, but a test (clone a widget with a subscriber, change the copy, assert the subscriber is not called) would settle it. The fix
is to clone through the JSON serializer (which `ToSnapshot` already does for whole themes), or to copy the observable properties explicitly.

Related: `PropertiesPanel` refers to `Widget` properties **by name, as strings**, and finds them with reflection. A rename such as
`FontFile` to `FontPath` would compile and then quietly drop a row. **Suggestion:** use `nameof(Widget.FontFile)` everywhere, and add
a test that every row's property name exists on `Widget` with a type the panel can edit.

### 4. Stop swallowing errors silently; add one small logger shared by both processes

There are many bare `catch { }` blocks, which are reasonable at the edges but leave nothing to diagnose in the field:

```csharp
catch { /* client left or stalled */ }                 // SensorPipeServer.Serve
catch { /* process killed */ }                         // VideoSource.ReadLoop
catch { return null; }                                 // RunnerClient.Send: "runner not running" and "protocol error" look the same
try { Blit(...); } catch { /* best effort */ }         // Ax206Display.Blank
```

The runner has a `runner.log`, but the editor and Core have no logging at all. When a user reports "the display stays black",
there is nothing to ask them for. **Suggestion:** a tiny `Glance.Core.Diagnostics.Log` (a rolling file in `%AppData%\Glance\logs`,
with a level and a category). Keep the `catch` blocks that protect the app, but log what they catch at Debug or Warning level. Add a
"Copy diagnostics" button to the setup check that bundles the setup results with the last log lines. `RunnerClient.Send` should also
return a result type that tells "not reachable" apart from "replied ERR ...".

### 5. Version and harden the control-pipe protocol

The control protocol is a space-separated text line, and `LIVE` carries the **whole theme JSON as base64 on one line**:

```csharp
return Send($"LIVE {folder} {json}", 2000) is { } r && r.StartsWith("OK");
```

It is simple and it works, but:

- There is **no version handshake**. If the editor is updated and the runner is not (for example, it was started by the scheduled task from an
  older install), a new command or a changed theme schema fails as `ERR unknown command`, or as a JSON error, with no clear message.
- **No size limit**: `ReadLineAsync` reads a line of any length, so a big theme makes a big line, and a buggy client could make the
  runner allocate without bound. The pipe is limited to the current user, so this is robustness, not security, but it is still worth fixing.
- Each command opens a **new connection**, and `LIVE` re-sends the full theme every time something changes.

**Suggestion:** start each connection with a `HELLO <protocolVersion> <appVersion>` exchange, and have the editor say "please restart the
background runner" when versions differ. Cap the line length (for example 8 MB). Move to one JSON message per line
(`{"cmd":"live","folder":...,"theme":{...}}`), which removes the base64 layer and makes adding fields backwards-compatible. Longer
term, keep one connection open per editor session and send `LIVE` changes debounced, as undo already is.

---

*Further reading in this repository:* [`docs/PROTOCOL.md`](PROTOCOL.md) for the display protocol and measured limits,
[`docs/TUTORIAL.md`](TUTORIAL.md) for the user guide, and [`AGENTS.md`](../AGENTS.md) for build commands and conventions.
