# Code review: theme input from other people (fix for M2 and M3)

**Date:** 2026-11-26
**Branch:** `claude/fix-theme-input-dec5d4` (one commit, `ad03e38`, on top of `main` at `9bf03b0`)
**Scope:** `src/Glance.Core/Themes/{Theme,ThemePackage}.cs`, new `src/Glance.Core/Diagnostics/CrashLog.cs`,
`src/Glance.App/{Program,MainWindow}.cs`, new `src/Glance.Tests/ThemeInputTests.cs` (71 tests), and the proof in `docs/design/fix-theme-input.md`.
**Findings being fixed:** M2 and M3 in `docs/reviews/review-full-codebase.md`:
- M2: `.glance` import checked the zip's entry names but not the `ImagePath`/`FontFile` values inside `theme.json`.
- M3: `Theme.Normalize` did not clean nulls or clamp numbers, and the editor had no crash handler.

**Reviewer:** independent; did not write the change. No code in the branch was changed by this review.

Nothing here was run in the real editor, the runner or on the physical display (the brief said not to start them). Everything below
comes from reading the code, the test suite, a scratch worktree of `main` for the "before" run, temporary probe tests and mutations in
a scratch worktree of the branch, and one small throw-away Avalonia 12.2 console probe. All scratch copies were removed or live only in the
session scratchpad; `git status` in the branch worktree shows only the two new documents.

---

## Verdict: merge after fixes

The change does what M2 and M3 asked for, and the proof holds up: I reproduced the author's "before" run exactly (45 of 55 fail on `main`,
with the same errors), and all 71 new tests pass on the branch. The import cleaner is strict in the right way. It allow-lists "relative and
inside `assets\`" instead of block-listing bad forms, and its final check compares fully resolved paths. Every dangerous path form I tried
was either removed on import or made the import fail. `Normalize` is careful about JSON compatibility: every limit is wider than the editor's
own ranges, and eight real themes round-trip byte for byte.

One medium issue should be fixed on this branch before merging:

- **M1.** `Theme.Save` still opens UNC and device paths. The design doc says the renderer refuses network paths "for themes from any source",
  but `Save` reads rooted paths with `File.Exists` and `File.Copy` without going through `ResolveAsset`. I proved it with a probe:
  Ctrl+S on a theme holding `\\localhost\C$\Windows\win.ini` opens the share and copies `win.ini` into `assets\`.
  The design doc lists this as "left open" for the atomic-saves branch, but that branch (`216ab94`) does not touch `Adopt`.
  So after both merge, the gap is still there.

The other findings are low or nits and can be follow-ups.

## Build and test results

Run in the branch worktree:

| Command | Result |
|---|---|
| `dotnet build src/Glance.sln -c Release -p:SkipHost=true` | **Succeeded**, 0 errors, 29 warnings. A `--no-incremental` rebuild of `Glance.App` (which builds Core) showed no warning in any changed file. |
| `dotnet test src/Glance.Tests -c Release` | **Passed 613 / 613**, about 8 s (542 on `main` + 71 new) |

**Merge check.** `git merge-tree --write-tree HEAD <branch>` against `fix-atomic-saves`, `fix-renderer-memory`, `fix-usb-recovery` and
`feature-runner-service` found no textual conflicts. `fix-atomic-saves` edits `Theme.Save` and `ThemePackage.Export`. It does not change
`Adopt` or `Import`, so it neither conflicts with this change nor closes M1.

---

## Proof verification

The user's rule: every bug must be shown to exist on the old code, then shown fixed.

**Before (old code).** I created a scratch worktree at `main` (`git worktree add --detach ... 9bf03b0`) and copied in
`ThemeInputTests.cs`. The classes `ThemeInputApiTests` and `CrashLogTests` do not compile against `main`: they use
`Import(..., out removed)`, `RemovedAssetPath`, the now-internal `Normalize()` and `CrashLog`, none of which exist there. I deleted those
two classes from the scratch copy, so nothing was stubbed. The other two classes compiled unchanged.

```
dotnet test src/Glance.Tests -c Release --filter "ImportedThemePathTests|ThemeNormalizeTests"
Failed!  - Failed: 45, Passed: 10, Skipped: 0, Total: 55
```

This matches the author's claim exactly. The failure messages (taken from the .trx) match the table in the design doc:

| Test | Failure on `main` |
|---|---|
| `A_bad_image_path_inside_theme_json_is_removed_on_import` x14, `A_bad_font_path_...` x14 | path kept as it was |
| `The_renderer_never_resolves_network_or_device_paths` x6 | `Assert.Null() Failure: Value is not null` |
| `Null_pages_list_gives_one_empty_page`, `Null_pages_widget_lists_and_widgets_are_dropped_or_replaced`, `A_live_or_undo_snapshot_is_normalized_too` | `NullReferenceException` |
| `A_null_sensor_id_no_longer_breaks_rendering` | `ArgumentNullException: Value cannot be null. (Parameter 'key')` (from rendering) |
| `Extreme_numbers_are_clamped_so_the_property_panel_and_renderer_cope` | `OverflowException: Value was either too large or too small for a Decimal` |
| `Screen_size_is_clamped_to_1_to_4096` x4 | `Assert.InRange() Failure` |
| `Unknown_enum_numbers_fall_back_to_the_defaults` | `Expected: Text` (got 99) |
| `Null_strings_become_their_defaults_and_every_kind_still_renders` | `Expected: "Untitled"` (got null) |

The 10 that passed on `main` are the guards that must keep passing: `Good_paths_are_kept...`, `Local_absolute_and_relative_paths_still_resolve`
and the 8 `Valid_themes_load_unchanged` cases.

One honest caveat on the "before" side: `Screen_size_is_clamped` and `Unknown_enum_numbers` show the *missing validation*, not a crash. They
fail on an assertion, before the render that would have crashed. M3's crash chains are shown by the NullReference, ArgumentNull and
decimal-overflow failures above.

**After (branch).** All 55 pass, plus the 16 new-API tests (`ThemeInputApiTests` 13, `CrashLogTests` 3): 71 of 71, and 613 of 613 overall.

**Mutation check (my own, in a scratch worktree of the branch, restored byte for byte afterwards):**

| Mutation | Tests that fail |
|---|---|
| `ResolveAsset` without `IsNetworkOrDevicePath` | 6 (`The_renderer_never_resolves_...`) |
| `WhyForeign` without the `..` segment rule | 1 (`Import_reports_each_removed_path...`, by its reason text only; see N2) |
| Import does not write the cleaned theme back | 28 (every `A_bad_..._path` case) |
| `Bound` without the NaN rule | 1 (`NaN_and_infinity_become_the_default_or_the_nearest_limit`) |

**Crash handler, outside the editor.** The editor was not run. To check that the Avalonia wiring in `Program.Main` actually works, I built
a throw-away console app against Avalonia 12.2.0 (`scratchpad\avprobe`). It reads `Dispatcher.UIThread` before `AppBuilder` runs, as
`Program.Main` now does, then starts a desktop lifetime and posts an action that throws:

```
--- early (handler subscribed before AppBuilder, like Program.Main):
early impl: _impl=Avalonia.Controls.Platform.ManagedDispatcherImpl
init impl: _impl=Avalonia.Win32.Win32DispatcherImpl
handler: boom from Post
exited normally; handler count=1; same dispatcher=True
--- late (no handler):
START FAILED: InvalidOperationException: boom from Post
```

So in 12.2, touching `Dispatcher.UIThread` early is safe: the same object is kept and the Win32 implementation is swapped in. The handler
catches posted exceptions. Without a handler, such an exception ends the app. This proves the mechanism, not the editor itself.

---

## Findings

### Medium

#### M1. `Theme.Save` still opens UNC and device paths from any theme that was not imported as a `.glance`
- **Where:** `src/Glance.Core/Themes/Theme.cs:165-181` (`Adopt`). Line 170 is `var source = rooted ? stored : ResolveAsset(stored);` and line 171 is
  `if (source is null || !File.Exists(source)) return stored;`. Then `File.Copy(source, dest)` runs at line 180.
- **Evidence:** a probe test on the branch put each path below in a widget's `ImagePath` and called `Save`. Output:
  ```
  \\?\C:\Windows\win.ini:          File.Exists=True; after Save ImagePath=assets/win.ini; resolve=null
  \\localhost\C$\Windows\win.ini:  File.Exists=True; after Save ImagePath=assets/win.ini; resolve=null
  \\127.0.0.1\C$\Windows\win.ini:  File.Exists=True; after Save ImagePath=assets/win.ini; resolve=null
  ```
  `ResolveAsset` refuses the path (`resolve=null`), but `Save` still opens the share and copies the file in.
- **Failure scenario:** someone shares a theme as a plain folder or a bare `theme.json` (a forum zip, a git repo, a cloud folder),
  or the user imported a `.glance` with a build older than this fix. Its `ImagePath` is `\\attacker.example\s\bg.png`. The preview shows
  nothing, so the user "fixes" a colour and presses Ctrl+S. Windows connects to `attacker.example` over SMB and sends the user's NTLM
  challenge-response: M2's leak, one keypress later. If the share answers, the attacker's file is also copied into the theme, and the next
  Export ships it.
- **Why it is not trivial:** the doc keeps `Save` open on purpose, so that a picture picked from a NAS (`\\nas\pics\bg.png`) still gets
  copied in. The fix has to keep that case working.
- **Remedial action:** in `Adopt`, refuse network and device paths unless the user picked that exact path in this session:
  ```csharp
  if (rooted && IsNetworkOrDevicePath(stored) && !UserPickedPaths.Contains(stored)) return stored;   // never touch a foreign share
  ```
  `UserPickedPaths` would be a `HashSet<string>` (`[JsonIgnore]`, case-insensitive) on `Theme`. `MainWindow`'s image, video and font
  pickers add to it. A simpler alternative: when a picker returns a UNC path, copy the file into `assets\` right away. Then neither the
  preview nor `Save` ever needs to open a share. Add a test: a theme loaded from JSON with `\\localhost\C$\Windows\win.ini` keeps its path
  after `Save`, and no `assets\win.ini` appears. Update the "Left open" note in the design doc.

### Low

#### L1. An unreadable path makes the whole import fail, with a raw .NET message
- **Where:** `src/Glance.Core/Themes/ThemePackage.cs:141-142` (`Path.GetFullPath` in `WhyForeign`).
- **Evidence:** probes. `ImagePath = "assets/a\0b.png"` makes `Import` throw `ArgumentException: Null character in path. (Parameter 'path')`.
  A 40,000-character `assets/aaa...png` makes `WhyForeign` throw `PathTooLongException`.
- **Failure scenario:** a theme with one corrupt path cannot be opened at all. The status bar says
  "Could not open theme: Null character in path." It fails closed, so this is safe, but the user cannot see why.
- **Remedial action:** in `WhyForeign`, wrap the two `GetFullPath` calls in `try { ... } catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException) { return "not a valid path"; }`.
  That removes the one path and keeps the theme. Add both inputs to `BadPaths`.

#### L2. Rooted but not fully qualified paths, and the base folder, are not checked for network targets in `ResolveAsset`
- **Where:** `src/Glance.Core/Themes/Theme.cs:272-274`.
- **Evidence:** the probe shows `ResolveAsset(@"\x.png")` returns `\x.png` and `ResolveAsset("C:x.png")` returns `C:x.png` unchanged. Windows
  resolves these against the process's current drive and current directory. `Path.Combine(BaseFolder, relative)` is not re-checked either.
- **Failure scenario:** these are edge cases, all unverified because they need a share:
  - The process's current directory is on a share (Glance started from `\\nas\tools\Glance`). Then `\x.png` opens `\\nas\tools\x.png`.
  - A theme is opened from a UNC folder, or the editor sends `LIVE` with a UNC folder (`RunnerContext.cs:358`). Then every relative path
    becomes a network path, and the elevated runner opens it.
  In both cases the server is one the user chose, so the risk is small. Still, the doc comment says "never resolved, for themes from any source", and that is not quite true.
- **Remedial action:** use `Path.IsPathFullyQualified(path)` for the "local absolute" branch and return null for other rooted forms (`\x`,
  `C:x`). Then either check the combined result with `IsNetworkOrDevicePath`, or reword the comment to say a theme *stored* on a share
  may read its own folder. Add the two inputs to `The_renderer_never_resolves_network_or_device_paths`.

#### L3. The UI crash handler swallows every UI exception silently and without a limit
- **Where:** `src/Glance.App/Program.cs:26-30`.
- **Evidence:** `e.Handled = true` with no user-visible signal. The probe confirms the handler sees `Dispatcher.Post` exceptions, which
  includes `async void` event handlers and timer ticks.
- **Failure scenario:**
  - An exception during a click (say, in a dialog's continuation) is logged to `editor.log`, and the user sees nothing happen. They do not
    know their action failed and may assume it worked (for example a Save that never wrote).
  - If the exception comes from something that runs every frame and is not already wrapped (a layout pass, a timer other than
    `RenderPreview`), it repeats forever. The editor keeps running half-broken and rewrites the 1 MB log every few seconds.
- **Remedial action:**
  - On the first handled exception, also set `_status.Text = "Something went wrong; details in editor.log"`. Expose a static event from
    `Program` that `MainWindow` listens to.
  - Count exceptions in a sliding window (for example more than 20 in 10 s). When the limit is hit, show one dialog that offers to save
    and close.
  - Optionally skip repeated identical stacks in `CrashLog`.

#### L4. The new size limits still let a video widget ask for 256 MiB frames in the elevated runner
- **Where:** `Theme.cs:113-114` (widget `Width`/`Height` up to `2 * MaxScreenSide` = 8192), `src/Glance.Core/Media/VideoSource.cs:32,64,77`.
- **Evidence (by reading; not run, no ffmpeg in the tests):** `VideoSource` allocates `Width * Height * 4` bytes and calls
  `SKImage.FromPixelCopy` for every frame. At 8192 x 8192 that is 268,435,456 bytes per frame at 15 fps.
- **Failure scenario:** a shared theme with an 8192 x 8192 video widget makes the elevated runner allocate gigabytes per second. Memory
  thrashes, and a smaller PC runs out of memory. M3 asked for "a huge size makes the app allocate gigabytes" to be fixed, and this is
  the one allocation that still scales with widget size.
- **Remedial action:** cap the decode size in `ThemeRenderer.DrawImage` or `VideoSource`. For example, limit width and height to the theme
  size, or scale down so `w * h <= 4096 * 4096`, and let Skia stretch the result. Add a renderer test that a 8192 x 8192 video widget
  requests at most the capped size from `VideoFactory`.

#### L5. A picked or typed network image now shows a blank box with no explanation
- **Where:** `Theme.cs:272`, editor image and font rows (`PropertiesPanel.cs:202` and the image path row).
- **Failure scenario:** a user picks `\\nas\photos\bg.png` or types it. The preview stays empty until they save, and nothing says why.
  It looks like a broken file.
- **Remedial action:** when a widget's path is refused by `IsNetworkOrDevicePath`, show a hint under the path box ("Network files are
  shown after you save; Glance copies them into the theme"). Or copy at pick time, as in M1's alternative, which removes the problem.

### Nits

- **N1. Duplicate widget ids survive `Normalize`.** A probe loaded two widgets with `"Id": "a"` and both kept it. Selection after undo
  (`MainWindow.cs:724`) picks the first. Fix: in `NormalizePage`, keep a `HashSet<string>` and give a repeated id a new GUID.
- **N2. One test comment overstates what it proves.** In `ThemeInputTests.cs:44`, `"assets/../theme.json"` is labelled
  "'..' that stays inside the folder is refused as well". But it resolves to `theme.json`, which is outside `assets\`, so the final
  containment check removes it anyway. The `..` rule is only covered by the reason string in one test (mutation B above). Add
  `"assets/sub/../a.png"` (which stays inside `assets\`) to `BadPaths`.
- **N3. AIDA64 imports skip `Normalize`.** `AidaImporter.Import` builds a `Theme` with `new` (`AidaImporter.cs:27`) and the editor uses it
  straight away. The values are ints, so there is no NaN or decimal overflow, but `FontSize` 0 or a negative width is possible until the
  first save and reload. Fix: call `theme.Normalize()` at the end of `Import` (it is internal and in the same assembly).
- **N4. `CrashLog` rotation can lose an entry under concurrency.** The UI thread and the finalizer thread (`UnobservedTaskException`) can
  both reach `File.Move`/`AppendAllText`. One gets an `IOException`, returns false, and its entry is lost. This is acceptable for a crash
  log; a `lock` on a static object would make it exact.
- **N5. `RemovedAssetPath.ToString` lowercases the property name** ("font file"), while the dialog text uses "pictures or fonts". This is cosmetic.

### Residual risk outside this change (for the record)

- **Paths inside media files.** A video file inside `assets\` can itself refer to other files. For example, an HLS playlist renamed to
  `.mp4` can list segments at `\\server\share\a.ts`. Recent FFmpeg builds refuse HLS with a non-standard extension, and the concat demuxer
  defaults to `safe=1`, but an old FFmpeg might follow such a reference. This was not tested (no FFmpeg run). Pinning the input format, or
  passing `-protocol_whitelist file,pipe` (which does not stop UNC, because UNC is "file"), would only partly help. Worth a line in the
  FFmpeg notes.
- **Reserved device names** (`assets/CON`, `assets/NUL.png`) are kept by the import cleaner. On this Windows 11 build `GetFullPath` leaves
  them as ordinary names. On older Windows, `GetFullPathName` maps them to `\\.\CON`, which then fails the "inside assets" check and is
  removed (fails safe). This is the separate finding L3 in the full review.

---

## Test gaps

- **No test that `Save` refuses foreign network paths (M1).** The current tests only show that `ResolveAsset` refuses them.
- **No test for paths `GetFullPath` cannot handle** (null character, very long), so the "whole import fails" behaviour is untested (L1).
- **No test for `\x.png` / `C:x.png` in `ResolveAsset`** (L2), or for `/\server` (only `\/server` is tested; the code is symmetric).
- **The `..` rule is covered by one reason-string assertion only** (N2).
- **The editor parts were not run**: the cleaned-theme dialog, the status line and the crash handler. The Avalonia probe covers the
  dispatcher mechanism only. A `PrintWindow` capture after opening a crafted `.glance` would close this gap.
- **Nothing checks that a theme imported by an older build is still dangerous**, that is, that `Load` does not clean foreign paths.
  This is by design, but it is the reason M1 matters.

## What was checked and found sound

**Import cleaning (`WhyForeign`)** is an allow-list. A path is kept only if it is relative, has no `:` and no `..` segment, and its full
path starts with `<dest>\assets\`. A probe of 49 forms through `IsNetworkOrDevicePath`, `WhyForeign` and `ResolveAsset` gave these results:

- **Removed on import and refused by `ResolveAsset`:**
  - `\\server\share`, `//server/share`, `/\server`, `\/server`
  - `\\?\UNC\server\share`, `\\?\GLOBALROOT\Device\Mup\...`, `\\.\PhysicalDrive0`, `\\;LanmanRedirector\server\share`
  - `\??\UNC\...`, `\??\C:\...`
  - the same with a leading space, tab, no-break space or ideographic space
- **Removed on import:**
  - drive-relative `C:x.png`, absolute `C:\x.png`, rooted `\x.png`
  - alternate data streams `assets/a.png:stream` and `::$DATA`
  - `file://` and `http://` (they contain `:`)
  - `assets\..\x`, `assets/../../x`, mixed `assets/..\..\x`, `assets/..`, `assets/.`
  - the bare `assets`, `CON` at the theme root, `theme.json`
  - URL-encoded `%5C%5Cserver` and Unicode look-alike slashes (U+FF3C, U+2215, U+29F5). Windows does not treat these as separators, so
    they are plain file names, outside `assets\`.
- **Kept, and harmless:**
  - `assets/a.png.`, `assets/a.png ` and `assets/a.png...`, which Windows trims to `assets\a.png`
  - `assets/.. /x.png` and `assets/.../x.png`, which are literal folder names inside `assets\` (`GetFullPath` keeps them)
  - `assets//a.png`, `./assets/a.png`
  - names with `|` or `*`, which cannot be opened
  - `\Device\Mup\server\share\x.png`, which Win32 reads as `C:\Device\Mup\...` (a local path), so it is not a network path

**The cleaned theme is written back**, so the raw paths never reach `Save`, `Export`, the runner's watcher or `LIVE`. Mutation C fails 28
tests. The write happens only when something was removed, so a clean package's `theme.json` stays byte-identical (tested). JSON duplicate
keys (last one wins) cannot split the check from the use, because both read the same deserialized model.

**`ResolveAsset` is the single gate for every reader of widget paths** in the renderer (`ThemeRenderer.cs:300` images and videos, `:416`
fonts) and in Export (`ThemePackage.cs:34`). A grep found no other code that opens `ImagePath` or `FontFile`, apart from `Save` (M1).
FFmpeg gets its arguments through `ArgumentList` (`VideoSource.cs:52-53`), so a crafted path cannot inject options.

**`Normalize`:**
- **Nulls:** null `Pages`, null pages, null `Widgets`, null widgets and legacy `[null]` are all handled. Every string property gets the
  default of a fresh `Widget`, except `Text`, which becomes `""` (that choice is right). A top-level JSON `null` gives a valid empty theme.
- **Enums:** an out-of-range number (`-1`, `7`, `99`) falls back to the default. An unknown *name* (`"Kind": "Teleporter"`) still throws
  `JsonException` and the load fails cleanly, as before.
- **Numbers:** NaN becomes the default and Infinity the nearest limit. `1e300` is clamped. Every `double` on `Widget` is covered: the test
  reflects over all of them and converts each to `decimal`.
- **JSON compatibility:**
  - Every limit is at least as wide as `PropertiesPanel`. I checked each `Num(...)` range: X/Y -2000..4000, size 1..4000, font 4..400,
    thickness 0.5..200, sweep 10..360, history 5..900, multiplier ±1e6, thresholds 0..1e6, page duration 1..3600.
  - The starter, all templates and an "edges" theme round-trip byte for byte.
  - A probe also loaded and rendered themes with bad colours, a bad format string, `Min > Max`, a 200,000-character text and a
    4096 x 4096 screen without errors.
- **`ApplySnapshot`** (undo, the runner's `LIVE`) normalizes too, which is tested.

**Editor wiring:**
- `_importRemoved` is reset on every failed open (`MainWindow.cs:628`, `:817`) and cleared after it is shown, so a stale list cannot
  appear later.
- The dialog reuses `ReportWindow`. Widget and page names from the untrusted theme are shown as plain text.

**`CrashLog`:**
- It never throws (tested with a folder in place of the file).
- It is capped at about 2 MB in total (log plus `.old`).
- Writing in the `AppDomain` handler cannot loop, because `Write` swallows its own errors.
- The `UnobservedTaskException` handler marks the exception observed, which is harmless on .NET 8.
