# Code review: full codebase (v0.1.1, commit `ee16f8c`)

**Date:** 2026-11-26
**Scope:** the whole repository. That covers `Glance.Core`, `Glance.App`, `Glance.Runner`, the build and installer scripts, CI, and the gaps in the tests.
**Method:**
- Three independent reviewers read every line of their part of the code. One covered USB, display, rendering and media; one covered themes, sensors, import, presets, settings and setup; one covered the editor, runner, build, installer and tests.
- They changed no code. The test suite passed (253/253) during the review.
- The findings were then merged, duplicates removed, and the most severe ones checked again against the source.
- **No code was changed.** This document lists what needs to be done.

**How to read a finding:**
- **Confirmed**: the code path exists exactly as described.
- **Plausible**: the outcome depends on timing, the behaviour of a native library, or input that was not reproduced.
- **Checked**: I re-read the cited code myself after the reviewers reported it.

Nothing here was tested on the physical display.

---

## Summary

| Severity | Count | Themes |
|---|---|---|
| High | 6 | Assets lost on Save As, live preview broken for new themes, privilege escalation through ffmpeg and the autostart task, display loop dying silently or crashing |
| Medium | 13 | USB handle leaks, untrusted paths and values in themes, non-atomic saves, stale live theme, UI freezes, sensor feed staleness, memory growth from images and video |
| Low | 14 | Data-quality and robustness issues, installer polish, diagnostics |
| Nits | about 20 | Small leaks and hygiene |

**Fix first, in this order:**
1. H1, Save As loses assets. It causes user data loss and the fix is small.
2. H2, live preview for unsaved themes. It is a visible functional bug and the fix is one line plus a test.
3. H4 and H5, display-loop resilience. These are the freezes and crashes on the recovery path.
4. H3 and H6, privilege escalation. These matter more once the app is installed outside Program Files or shared widely.
5. M2 and M3, validating themes from other people.
6. M4, atomic writes.

---

## High

### H1. Save As leaves the theme's images, videos and fonts behind (confirmed, checked)
- **Where:** `src/Glance.Core/Themes/Theme.cs:70-88` (`Save` → `Adopt`); called from `src/Glance.App/MainWindow.cs:803`.
- **Problem:** `Adopt` only copies *absolute* paths. Paths that are already relative (`assets/bg.png`) are kept as they are, and then `BaseFolder` is switched to the new folder:
  ```csharp
  if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || !File.Exists(path)) return path;
  ...
  BaseFolder = dir;
  ```
- **Scenario:** the user saves `Themes\A\theme.json`, then uses Save As to `Themes\B\theme.json`. Folder B has no `assets` folder, but its JSON still says `assets/bg.png`. The image cache is cleared right after saving, so the backgrounds disappear at once. If folder A is ever deleted, the files are gone for good.
- **Remedial action:** in `Adopt`, resolve relative paths against the *old* `BaseFolder` (`ResolveAsset(path)`), and copy the file whenever the target folder differs from the old one. Add a test that saves into a second temp folder and checks that every referenced asset exists there.

### H2. The live preview of a new or template theme never reaches the runner (confirmed, checked)
- **Where:** `src/Glance.App/Services/RunnerClient.cs:34-36`, `src/Glance.Runner/RunnerContext.cs:218,239`.
- **Problem:** when `BaseFolder` is null, the folder argument becomes an empty string, so the line sent is `LIVE  <json>` with two spaces. The runner parses it with `Split(' ', 3, StringSplitOptions.RemoveEmptyEntries)`, which gives 2 parts. `case "LIVE" when parts.Length == 3` does not match, and the runner replies `ERR unknown command`. `BaseFolder` is null for every theme created from **New** (`SetTheme(theme, null)` at `MainWindow.cs:733`) and for the built-in starter theme.
- **Scenario:** with the runner running, the user picks a template and clicks **Send to display**. The status says the edits are being shown, but the display keeps showing the old saved theme. The `false` returned by `SendLive` is ignored.
- **Remedial action:**
  - Send a placeholder for "no folder" (for example `-`), or parse without `RemoveEmptyEntries`.
  - Move the command parsing and encoding into `Glance.Core` and add round-trip tests, including the empty folder.
  - Surface a failed `SendLive` in the status bar.

### H3. The elevated runner starts `ffmpeg.exe` from folders a normal process can write to (confirmed code path, checked)
- **Where:** `src/Glance.Core/Media/Ffmpeg.cs:24-34`, used by `VideoSource` inside the runner's `DisplayPusher`.
- **Problem:** the search order is: the app folder, then **every `PATH` entry** (the user PATH included), then `%LOCALAPPDATA%\Microsoft\WinGet\Links\ffmpeg.exe` and a recursive search of `%LOCALAPPDATA%\Microsoft\WinGet\Packages\*FFmpeg*`. The editor's own "Install FFmpeg" button (a user-scope winget install) puts ffmpeg in exactly those folders.
- **Scenario:** any program running as the user without elevation plants or replaces `ffmpeg.exe` in one of those folders. It then gets a video shown on the display, either by writing a theme the runner reloads or by sending `LIVE` over the control pipe. The runner, which runs as administrator and may be started by the autostart task, launches that binary as admin with no UAC prompt. That is a silent elevation of privilege. Separately, the elevated ffmpeg parses media files from themes shared by other people.
- **Remedial action:**
  - When the process is elevated, accept ffmpeg only from admin-only folders (the install folder under Program Files), or check its Authenticode signature or its folder's ACL.
  - Better, decode video in a non-elevated process: start ffmpeg with a de-elevated token, or let the editor decode.
  - At minimum, skip `PATH` and `%LOCALAPPDATA%` candidates when elevated.

### H4. The display loop can die silently and freeze the display (confirmed path, checked; the race is a small timing window)
- **Where:** `src/Glance.Core/Display/DisplayPusher.cs:76-106`, `src/Glance.Core/Themes/Theme.cs:100-101`, `MainWindow.cs:449,708,1349`.
- **Problem:** the loop catches only `IOException` and `InvalidOperationException`, and the task is never observed. The editor's own pusher renders the *same* `Theme` object that the UI thread changes:
  - `ApplySnapshot` (undo/redo) runs `Pages.Clear()` and then re-adds the pages. During that window `theme.Pages[Math.Min(0, -1)]` throws `ArgumentOutOfRangeException`.
  - Deleting a page (`Pages.RemoveAt`) can shrink the list between `n = Pages.Count` and `Pages[current]`.
  - A `null` in theme JSON (`"SensorId": null`, `"Pages": [null]`) throws `ArgumentNullException` or `NullReferenceException` (see M3).
- **Scenario:** the user presses Ctrl+Z while sending to the display. The loop thread ends and the display freezes on its last frame. `LastError` stays null, so the editor's dot stays green, and the runner's `Supervise` never reopens the display because it only looks at `LastError`.
- **Remedial action:**
  - Add a final `catch (Exception ex)` that sets `LastError`, backs off and continues.
  - Expose `IsAlive`/`Faulted`, and have the runner and the editor restart a dead pusher.
  - Render from an immutable snapshot (or at least `theme.Pages.ToArray()` with indexes clamped against that copy).
  - In `ApplySnapshot`, never leave `Pages` empty: replace pages by index, or add the new pages before removing the old ones.

### H5. Stopping the display can close the USB handle while a transfer is still running (plausible)
- **Where:** `DisplayPusher.cs:119-125`, `src/Glance.Core/Usb/Ax206Display.cs:70-87,152-158`, triggered from `RunnerContext.cs:154`.
- **Problem:** `Stop` waits only 2 s for the loop (`_task.Wait(2000)`), but one USB transfer can take up to 3 s (`TimeoutMs = 3000`) and a frame is several 64 KiB chunks. After the wait, `Blank()` sends from the calling thread, and `Dispose()` calls `usb_close` and sets `_handle = 0`, while the loop may still be inside `usb_bulk_write`.
- **Scenario:** the display stalls. After 6 s the runner decides "Display kept failing; reopening", the 2 s wait expires, and the handle is closed under the loop. libusb0 then works on a closed or NULL handle, which is an access violation .NET cannot catch, so the elevated runner crashes. This is exactly the error-recovery path.
- **Remedial action:**
  - Let the loop own the device: on exit, the loop itself blanks the display (if asked) and disposes it. `Stop` signals and waits for the loop to finish, ideally asynchronously so the runner's UI thread is not blocked (see L8).
  - Make `Ax206Display` defensive: a lock around transfers and `Dispose`, a disposed flag, and `ObjectDisposedException` instead of passing a zero handle to native code.

### H6. The autostart task can be hijacked (confirmed code path, checked)
- **Where:** `src/Glance.App/Services/Autostart.cs:34-38`, `SensorHub.FindExe`.
- **Problem A:** the task XML is written to a **fixed name in `%TEMP%`** (`GlanceRunnerTask.xml`) and then read by an elevated `schtasks.exe` after the UAC prompt. Any process running as the same user can rewrite it while the prompt is open. The user approves a Microsoft-signed `schtasks.exe`, and a "run with highest privileges" logon task gets registered with an attacker-chosen command.
- **Problem B:** the task runs whatever `FindRunnerExe()` found. That can be the folder extracted from the zip or a dev `bin` folder, both writable by the user. Replacing `Glance.Runner.exe` or a DLL next to it gives silent admin at every logon.
- **Remedial action:**
  - Have the elevated runner register the task itself (for example `Glance.Runner.exe --install-autostart`), building the XML in memory from its own path. If a file must be used, give it a random name in a folder only admins can write, and check the registered task afterwards.
  - Refuse, or warn clearly about, autostart when the runner's folder is writable by the user, i.e. not under Program Files.

---

## Medium

### M1. A failed open leaks a claimed USB handle (confirmed)
- **Where:** `Ax206Display.cs:54-56`, `DisplayPusher.cs:38-39`.
- **Problem:** `new Ax206Display(h); d.ReadDimensions();` and `_lcd = Open(); _lcd.SetBrightness(...)` have no cleanup if the second call throws.
- **Scenario:** right after the display is plugged in, `ReadDimensions` times out. Interface 0 stays claimed by this process, so every later open fails with "busy" until the runner restarts.
- **Remedial action:** wrap both in `try { ... } catch { d.Dispose(); throw; }`.

### M2. `.glance` import checks the zip's file names but not the paths inside `theme.json` (confirmed design gap; the credential leak is plausible)
- **Where:** `src/Glance.Core/Themes/ThemePackage.cs:59-105`, `Theme.ResolveAsset` (`Theme.cs:105-110`).
- **Problem:** after extracting, only `Theme.Load` runs. A widget's `ImagePath` or `FontFile` can be a UNC path (`\\attacker\share\x.png`), an absolute local path, or a path containing `..`, and `ResolveAsset` returns rooted paths unchanged.
- **Scenarios:**
  - Opening a shared theme makes the editor, and later the elevated runner, open a remote file. That sends the user's NTLM credentials to the attacker, and parses a font file from an untrusted share.
  - Saving or re-exporting copies whatever local file the path points at into the theme, which can leak it when the user shares the theme again.
- **Remedial action:** after import, clear or reject every widget path that is rooted, contains `..`, or does not resolve inside `<dest>\assets\`, and save the cleaned theme. Add tests for UNC, drive-letter and `..` widget paths.

### M3. Theme values are not validated, so a bad file can crash the editor or the runner (confirmed chain)
- **Where:** `Theme.Normalize` (`Theme.cs:42-52`), `MainWindow.cs:1204-1231`, `PropertiesPanel.cs:159`.
- **Problem:** `Normalize` only guarantees one page. JSON `null` is accepted for non-nullable strings and collections. `Width`/`Height` of 0, negative or huge are accepted. There is no global crash handler in the editor.
- **Scenarios:**
  - `"SensorId": null` causes an `ArgumentNullException` on every render.
  - `"Pages": null` causes a NullReferenceException in `Normalize`.
  - `Width: 0` gives an Infinity scale in `DisplayPusher`, and a crash when `SKBitmap`/`WriteableBitmap` are allocated.
  - A huge size makes the app allocate gigabytes.
  - An extreme double overflows a `decimal` in the property panel.
- **Remedial action:**
  - In `Normalize`: replace null collections, drop null pages and widgets, coerce null strings to their defaults, clamp sizes (for example 1–4096) and clamp other numbers.
  - Add a top-level unhandled-exception handler that logs.
  - Add tests for each case.

### M4. Theme and settings saves are not atomic, and corrupt settings are silently reset (confirmed, checked)
- **Where:** `Theme.cs:88` (`File.WriteAllText`), `src/Glance.Core/Settings/AppSettings.cs:53-63`.
- **Scenarios:**
  - A crash or power cut during a save truncates `theme.json`, and the theme is lost.
  - The runner's file watcher reads a half-written `settings.json`, falls back to the defaults (`LastTheme = null`, `Brightness = 7`), and briefly shows the starter theme at full brightness.
  - A corrupt `settings.json` is silently overwritten with the defaults on the next save.
- **Remedial action:**
  - Write to `path + ".tmp"`, flush, then `File.Move(tmp, path, overwrite: true)` (or `File.Replace` with a `.bak` copy).
  - Make `Load` tell "missing" apart from "corrupt", log a corrupt file, and never overwrite it automatically.

### M5. The runner can stay stuck on an old unsaved theme after the editor closes (plausible)
- **Where:** `RunnerContext.cs:97-106,246,249`; `MainWindow.cs:159,1299-1305,1338`.
- **Problem:** `LIVE_CLEAR` is sent fire-and-forget on a background task when the editor closes. Each command uses its own connection, so the order is not guaranteed. `_live` and `_pinPage` never expire, and `Reload()` does not clear them.
- **Scenario:** the editor exits before the background send completes, or crashes, or is killed by the installer. The display keeps showing the unsaved, pinned theme until the runner restarts, and saving does not help.
- **Remedial action:**
  - Send `LIVE_CLEAR` synchronously (with a short timeout) in `Closing`.
  - Send all control commands through one ordered queue.
  - Give the live theme a lease: the runner drops `_live`/`_pinPage` after a few seconds without a heartbeat, or when the connection that set it closes.

### M6. A failed live send is never retried (confirmed)
- **Where:** `MainWindow.cs:1330-1339`.
- **Problem:** `_lastLive = snap` is set *before* the send, and the result of `SendLive` is ignored.
- **Scenario:** the runner is busy (see L8) or the pipe's 4 instances are taken, so the send times out. The display then shows an older state until the next edit.
- **Remedial action:** update `_lastLive` only after an `OK` reply. Better, use one background sender that always sends the latest snapshot.

### M7. Process, pipe, USB and large file work runs on the UI thread (confirmed)
- **Where and what:**
  - `ServiceWindow.Sync()` runs **every second** (`ServiceWindow.cs:61-73`). It calls `Autostart.IsInstalled()`, which starts `schtasks.exe` and waits up to 5 s, and `RunnerClient.Send("STATUS")`, which can block for 1.6 s.
  - `SetupWindow.RefreshAsync` and `MainWindow.CurrentSetupContext` also call `Autostart.IsInstalled()` on the UI thread.
  - `ToggleSend` and `TurnDisplayOff` open and close the USB display on the UI thread (3 s timeouts, plus a 2 s wait in `Stop`).
  - `ImportPackage` extracts up to 1 GB, the AIDA importer runs twice, and `Theme.Save` copies large video files, all on the UI thread.
- **Scenario:** the editor freezes for up to 5 s per tick while the Background service or Setup window is open, and for long periods during big imports and saves. AGENTS.md forbids this.
- **Remedial action:** move all of this to `Task.Run`/await. Query `IsInstalled` once when the window opens and after a change, not on a 1 s timer.

### M8. The sensor feed can look live when it has stopped, and one NaN can break it (plausible; the startup crash path is checked)
- **Where:** `SensorPipeServer.cs:23,51-57`, `LocalSensorSource.cs:32`, `RunnerContext.cs:53-54`, `SensorService.cs:37`.
- **Problems:**
  - The server re-sends the last snapshot every second whether or not it is new, so the editor's 5 s staleness rule never fires while the pipe is connected.
  - `JsonSerializer.Serialize` throws on `float.NaN` or `Infinity`. In the update loop this is swallowed, so updates stop. The *first* `Publish` at `RunnerContext.cs:53` is not inside a try, so a NaN at startup would crash the runner.
  - `SensorService.Update` has no per-hardware try. One failing device discards the whole new list, so every sensor freezes.
- **Remedial action:**
  - Map non-finite values to null before serializing.
  - Add a sequence number or timestamp to each snapshot, and have the client treat "no new snapshot for 5 s" as stale.
  - Wrap the initial `Publish`.
  - Put a try/catch around each `Collect(hw, ...)`.

### M9. Video: frames are never disposed, and resizing a video starts a new ffmpeg for every size (confirmed)
- **Where:** `src/Glance.Core/Media/VideoSource.cs:63,69-74`, `ThemeRenderer.cs:298,349-355`.
- **Problems:**
  - `_latest = SKImage.FromPixelCopy(...)` replaces the previous image without disposing it, and `Dispose` never frees `_latest`. A 480x320 video at 15 fps leaves about 9 MB/s of native memory for the finalizer, which matters in a runner that runs for days.
  - Videos are cached by `path|WxH|fit`. Dragging a video widget's resize handle creates a new ffmpeg process for every pixel size, on the UI thread (including `Ffmpeg.Find()`), and each one lives about 3 s.
- **Remedial action:**
  - Copy frames into one reused bitmap under a lock, or hand out reference-counted frames, and dispose `_latest` in `Dispose`.
  - While resizing, keep the existing source and scale its frames; restart ffmpeg after the size has been stable for about 500 ms, off the UI thread.

### M10. Renderer caches: typefaces leak, images use too much memory, and the display never sees updated files (confirmed, the cap was checked)
- **Where:** `ThemeRenderer.cs:381-389,411-436`, `src/Glance.Core/Media/AnimatedImage.cs:34,46`.
- **Problems:**
  - `ClearImageCache` clears `_fileFaces` without disposing the typefaces, and `_faces` is never disposed, not even in `Dispose`. Undo and redo call `ClearImageCache` on every step, so they leak and also decode every image again on the UI thread.
  - Images are decoded at full resolution, with a 600 MB cap *per image, per renderer*. The editor can hold three renderers (preview, pusher and virtual display), so one large GIF can cost about 1.8 GB.
  - The editor pusher's cache is never invalidated, so an image replaced on disk keeps showing the old picture, and "missing file" results are cached forever.
- **Remedial action:**
  - Dispose typefaces when clearing, and dispose `_faces` in `Dispose` (not `SKTypeface.Default`).
  - Don't clear every cache on undo; invalidate by file modified time instead.
  - Decode to the widget size and use a much lower total cap.
  - Give the pusher a way to invalidate its cache.

### M11. Seeding a graph's history writes samples out of time order (confirmed, checked)
- **Where:** `src/Glance.Core/Sensors/SensorHistory.cs:26-31`, `ThemeRenderer.cs:79-94`, `MainWindow.cs:951`.
- **Problem:** `Seed` appends without checking order, while `Window` and the pruning assume the list is sorted. Adding a preset seeds the whole active page again.
- **Scenario:** a CPU graph with 60 s of real history gets 120 synthetic samples appended, and draws a visible zigzag back to the left edge. Two graphs on one page that share a sensor are seeded twice.
- **Remedial action:** seed only sensors whose history is empty, or insert samples in order. Add a test.

### M12. The USB status reply is not checked, and there is no recovery after a transfer fails partway (plausible)
- **Where:** `Ax206Display.cs:89,133-135`.
- **Problem:** `ReadStatus` ignores the `USBS` signature, the tag and the status byte. If a chunk write throws partway, the status reply is never read, and `usb_clear_halt` is declared but never used.
- **Scenario:** after a timeout in the middle of a frame, the device may treat the next command as data, which can garble or reject frames until it resyncs. The editor's pusher never reopens the display, so it can stay broken.
- **Remedial action:** validate the status reply. After a failure partway through a transfer, clear the halt on both endpoints and drain the pending status (or reset or reopen the device) before the next frame.

### M13. Pipes are machine-wide, and "runner running" is judged by process name (plausible)
- **Where:** `RunnerContext.cs:184-190`, `SensorPipeServer.cs:27-36`, `RunnerClient.cs:13,20`.
- **Problems:**
  - The pipe names are global and neither side uses `PipeOptions.CurrentUserOnly`.
  - `IsRunning` looks at process names in every session.
  - If the runner is elevated with *a different admin account*, it uses that account's SID and `%AppData%`. The editor then thinks the runner is running, but every send fails silently.
  - A second signed-in user's runner cannot create the pipe and logs an error every second forever (see L7).
  - Another local account that creates the pipe name first receives the editor's theme and paths.
- **Remedial action:**
  - Use `PipeOptions.CurrentUserOnly` on both ends.
  - Put the user SID or session id in the pipe names.
  - Decide "runner available" with a `STATUS` round trip instead of by process name.
  - Show a clear message when the runner belongs to another account.

---

## Low

| ID | Finding | Where | Remedial action |
|---|---|---|---|
| L1 | `Widget.Clone()` uses `MemberwiseClone`, which copies the original's `PropertyChanged` subscribers. Changes to a duplicate fire the editor's handler twice and a stale property-panel handler, and keep objects alive. | `Widget.cs:87-93` | Clone through a JSON round trip or explicit property copies. Add a test that a clone has no subscribers. |
| L2 | Assets are de-duplicated by name and size only, so two different same-size files with the same name are treated as one. The AIDA importer overwrites when the lengths match. | `Theme.cs:77-79`, `AidaImporter.cs:140-142` | Compare content (a hash, or bytes when sizes match), otherwise pick a new name. |
| L3 | Package edge cases: `theme.json` can be up to 1 GB and is read into one string; Windows reserved names (`CON`, `COM1`, ...) are not rejected; Export deletes the old `.glance` before writing the new one. | `ThemePackage.cs:25,61-104` | Cap `theme.json` at a few MB, reject reserved names and names ending in a dot or space, export to a temp file and then move it. |
| L4 | The AIDA importer aborts the whole import on one bad image: bad hex, an empty `<IMGFIL>`, or an unsafe extension. | `AidaImporter.cs:133-142` | Wrap each item in a try that adds a note and continues; default empty names; use the cleaned extension. |
| L5 | The setup check says "runner is using the display" even when AIDA64 holds it, and the AIDA64 warning is hidden while the runner runs. | `SetupChecks.cs:34,98` | Always warn about AIDA64; ask the runner's `STATUS` whether it actually holds the display. |
| L6 | Preset sensor lookups fall back to *any* sensor of that type without saying so, so a card can show the wrong quantity, for example "Used Space" as disk activity. | `WidgetPresets.cs:60` | Report the fallback in `Missing`/notes, or drop it for specific sensors. |
| L7 | The elevated runner appends `runner.log` in user-writable `%AppData%`, a junction-redirect risk. The log is never rotated, and the pipe-error loop in M13 can grow it without limit. | `RunnerContext.cs:309-317` | Log to `%ProgramData%\Glance` with a restricted ACL, or refuse reparse points; add size-based rotation. |
| L8 | The runner holds `_gate` during slow USB open and close on its UI thread, which stalls pipe commands and the tray menu and feeds M6. | `RunnerContext.cs:143-165` | Open and close outside the lock and swap the reference under it; stop asynchronously. |
| L9 | The "display not found" error is overwritten at once by "Stopped sending." because unchecking the toggle re-enters `ToggleSend`. | `MainWindow.cs:1345-1356` | Uncheck under a guard flag, or set the message after unchecking. |
| L10 | Unsaved work is lost: the editor has no unsaved-changes prompt on close, and the installer runs `taskkill /F` on the editor and the runner without asking. | `MainWindow.cs:159`, `installer/Glance.iss:58-65` | Add a dirty check on close; in the installer, ask first and stop the runner with `EXIT` before killing anything. |
| L11 | `make-installer.ps1` does not check what it is packaging: it can package a framework-dependent build, or one without `Glance.Runner.exe`. | `build/make-installer.ps1:9-11` | Write a flavor marker in `publish.ps1` and check it; check that the runner exe exists. |
| L12 | Theme-folder watchers pile up when `LastTheme` moves between external folders, and the "inside AppData" check is a prefix match without a separator. | `RunnerContext.cs:117-124` | Dispose the old watcher; compare with a trailing separator. |
| L13 | `Autostart.RunElevated` reads `ExitCode` after a 30 s wait that may have expired, which shows a confusing error if the UAC prompt stays open. | `Autostart.cs:56-57` | Check `HasExited`, or wait asynchronously without a hard limit. |
| L14 | Display loop details: | `DisplayPusher.cs`, `FrameConverter.cs`, `ThemeRenderer.cs` | One fix per item, below. |
| | • about 300 KB is allocated on the large object heap 5 times a second | | Convert into two reused buffers. |
| | • a brightness change is lost when the USB call fails | | Set `applied` only after the call succeeds. |
| | • `catch (InvalidOperationException)` also hides `ObjectDisposedException` | | Count consecutive failures into `LastError`. |
| | • background renderers write `Widget.LastBounds`, which the UI thread uses for hit-testing | | Record bounds only in the editor's preview renderer. |

## Nits

- **Undisposed objects:** `WriteableBitmap`s replaced in `RenderPreview`, `VirtualDisplayWindow` and `PreviewSurface`; an `HICON` from `MakeIcon`; `Process[]` arrays from `IsRunning`/`SetupChecks`; `CancellationTokenSource`s in `DisplayPusher` and `VideoSource`; frames already created when `AnimatedImage.Load` fails partway.
- **Dispose races:** `SensorHub.Dispose` and `LocalSensorSource.Dispose` can close LibreHardwareMonitor while their loop is inside `Update()`. Cancel, wait, then dispose.
- **Unbounded reads:** the control pipe's `ReadLineAsync` and the sensor pipe client have no line-length limit. An idle client holds one of the 4 control pipe instances forever.
- `Autostart.IsInstalled` reads stdout and then stderr one after the other, a classic deadlock pattern.
- `SensorHistory.Window` returns the live internal list.
- `Libusb0.LastError()` and `Enumerate` use libusb-0.1 global state. Serialize them with a static lock, since setup checks can enumerate while the display opens.
- `FrameConverter` ignores alpha and truncates rather than rounds, so translucent backgrounds look darker on the device than in the preview, and gradients band.
- `GroupOps.DragRect` with Shift held and a zero-width start box gives a zero ratio.
- A video whose ffmpeg exits without writing to stderr shows "Loading video..." forever.
- **Culture:** a `StartsWith` without `StringComparison.Ordinal` in `AidaImporter:36`, and an `int.Parse` without invariant culture in `CpuCoreLoads`.
- An unknown enum value in theme JSON (a theme from a newer version) fails the whole load. Consider a lenient converter.
- `publish.ps1` assigns to PowerShell's automatic `$args` variable.
- CI has no `permissions:` block, and its actions are pinned by tag rather than commit SHA.

---

## Test coverage gaps

These are worth adding, ideally together with the fixes above:

1. **Control-pipe command parsing and encoding.** It is untested and lives in `Glance.Runner`; a test would have caught H2. Move it into Core and add round-trip tests: empty folder, unknown command, ordering.
2. **Save As into a new folder** keeps every asset (H1).
3. **`ThemePackage.Import`** with UNC, absolute and `..` paths *inside* `theme.json` (M2), and with reserved names (L3).
4. **`Theme.Normalize`**: null pages, widgets and strings, zero, negative and huge sizes (M3).
5. **`DisplayPusher` loop resilience:** a non-IO exception and `Stop` during a transfer (H4, H5). This needs an `IDisplay` interface over `Ax206Display` so it can be tested without hardware.
6. **`Autostart.BuildXml`**: escaping of `&`, quotes and `DOMAIN\user`.
7. **Sensor feed:** a NaN value, and staleness when the server keeps re-sending an old snapshot (M8).
8. **`SensorHistory.Seed`** ordering (M11) and **`Widget.Clone`** subscribers (L1).
9. `SensorHub` fallback and merge, and `RunnerClient`, live in `Glance.App`, which has no tests. Moving their logic into Core would make them testable.

---

## What was checked and found sound

- **USB layer:** the libusb-win32 x64 struct offsets, the P/Invoke signatures and calling convention, the command-block layout and the blit command (matching `docs/PROTOCOL.md`), the 64 KiB chunking and the pointer bounds.
- **Frames and pages:** the RGB565 big-endian conversion with row stride; page rotation (`NextPageIndex`); the skip-unchanged-frame logic and the 10 s forced refresh.
- **Editor geometry:** the handle layout and hit-testing; group scaling, which recomputes from the start state so there is no drift; `Union`; `NewGroupName`.
- **Media:** GIF frame compositing in `AnimatedImage`; ffmpeg arguments passed through `ArgumentList` (no injection), the frame-read framing, `-re`, and killing the process tree on dispose.
- **`.glance` zip entry validation:** an allow-list, rejection of `..`, rooted paths and drive letters, the full-path containment check, entry and size caps re-checked while copying, and no possibility of creating symlinks.
- **Theme files:** old-file migration (top-level `Widgets` moved to page 1, at least one page guaranteed), the MiniLcd to Glance folder migration, and the undo manager.
- **Pipes:** both pipes are restricted to the current user's SID, and the sensor pipe is send-only. Bad base64 or JSON in `LIVE` is caught and answered with `ERR`; `PAGE` and `BRIGHTNESS` are clamped.
- **Elevation:** the editor is de-elevated through `explorer.exe` and the installer's `runasoriginaluser`; the manifests are `asInvoker` for the editor and `requireAdministrator` for the runner.
- **Editor plumbing:** `HookAll` unsubscribes removed pages and widgets; `PropertiesPanel` unsubscribes its previous target; the virtual display and import preview windows dispose their renderers.
- **Culture:** AIDA numbers are parsed with the invariant culture, and theme JSON is culture-independent.
