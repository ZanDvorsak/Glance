# Fix: theme input from other people (M2, M3)

Branch `claude/fix-theme-input-dec5d4`. Findings M2 and M3 in `docs/reviews/review-full-codebase.md`.
This file records the proof (before and after). The independent review is written separately.

All new tests are in `src/Glance.Tests/ThemeInputTests.cs`:

| Class | Tests | What |
|---|---|---|
| `ImportedThemePathTests` | 36 | M2: bad widget paths inside `theme.json` are removed on import; good ones kept; `ResolveAsset` refuses network/device paths |
| `ThemeNormalizeTests` | 19 | M3: null lists/pages/widgets/strings, sizes, extreme numbers, enums, the live/undo snapshot path, valid themes unchanged |
| `ThemeInputApiTests` | 13 | New API: the import report, NaN/Infinity through the internal `Normalize`, `IsNetworkOrDevicePath` |
| `CrashLogTests` | 3 | The editor's crash log writer |

## How "before" was captured

The 55 tests in the first two classes use only the API that existed before. They were run against the old
`Glance.Core` (the fix temporarily reverted with `git checkout -- src/Glance.Core`, then re-applied from a patch):

```
Failed!  - Failed: 45, Passed: 10, Skipped: 0, Total: 55
```

The 10 that passed on the old code are the guards that must keep passing: good import paths stay, local paths still
resolve, and 8 valid themes (the starter, every template, the edges of every editor range) load unchanged.

The 16 tests in `ThemeInputApiTests` and `CrashLogTests` use the new API (`Import(..., out removed)`, internal
`Normalize`, `CrashLog`), so on the old code they do not compile; that is their "before".

## M2: paths inside a shared theme

### Before (old code)

| Test | Result on old code |
|---|---|
| `A_bad_image_path_inside_theme_json_is_removed_on_import` (14 paths) | 14 failed: `Expected: ""`, actual e.g. `"\\attacker\share\x.png"`, `"C:\Windows\win.ini"`, `"assets\..\..\secret.png"` kept as they were |
| `A_bad_font_path_inside_theme_json_is_removed_on_import` (same 14) | 14 failed, same way |
| `The_renderer_never_resolves_network_or_device_paths` (6 paths) | 6 failed: `Assert.Null() Failure: Value is not null` (the UNC/device path came back unchanged) |

The 14 paths: `\\attacker\share\x.png`, `//attacker/share/x.png`, `\\?\UNC\attacker\...`, `\\.\PhysicalDrive0`,
`\\?\C:\Windows\win.ini`, `C:\Windows\win.ini`, `\Windows\win.ini`, `C:secret.png`, `assets\..\..\secret.png`,
`../secret.png`, `assets/../theme.json`, `theme.json`, `assets/a.png:hidden`, `assets`.

No test touched the network: the old import never opens widget paths, and `ResolveAsset` only builds a string.

### Fix

- `ThemePackage.Import(package, dest, out removed)` (the old two-argument `Import` calls it): after extracting and
  loading, every widget `ImagePath` and `FontFile` is cleared unless it is a relative path that resolves inside
  `<dest>\assets\`. Refused: network or device paths, rooted paths, any `:` (drive-relative paths, alternate data
  streams), a `..` segment, and anything that resolves outside `assets\`. A path to a missing file inside `assets\`
  is harmless and kept. If anything was cleared, the cleaned theme is written back to `theme.json` (directly, not with
  `Theme.Save`, which is being changed on another branch and has nothing to do here). `removed` lists page, widget,
  property, the path and the reason.
- Editor: `ImportPackage` keeps the list; after opening the theme (Open, or a `.glance` on the command line) the status
  bar says how many paths were removed and a `ReportWindow` lists them.
- `Theme.ResolveAsset` returns null for network and device paths (`Theme.IsNetworkOrDevicePath`: two leading slashes in
  either direction, which covers `\\server`, `//server`, `\\?\`, `\\.\`, plus `\??\`).

### Decision: should the renderer ever open UNC or device paths?

No, for themes from any source, not only imports. Reasons:
- Opening `\\host\...` makes Windows authenticate to that host with the user's credentials (NTLM), and the runner does it
  elevated, and parses whatever image or font comes back. A theme can reach the runner without passing through `.glance` import: a
  `theme.json` copied into the themes folder by hand, or one shared as a plain folder.
- Device paths (`\\.\PhysicalDrive0`, `\\?\GLOBALROOT\...`) would let a theme make the elevated runner read raw devices.
- Glance never writes such paths itself: the file picker's result is copied into `assets\` on Save, so saved themes hold
  relative paths. The only loss: a picture picked straight from a `\\nas\...` share shows nothing in the preview until the
  theme is saved (Save copies it into `assets\` because its copy step does not go through `ResolveAsset`, see "Left open").
  Mapped drive letters (`Z:\`) still work: they are local absolute paths to a server the user chose.
- Local absolute paths (`C:\...`) are still resolved, because the editor needs them for files picked and not yet saved.
  For imports they are removed anyway (they could leak a local file into a re-export).

### After

```
ImportedThemePathTests: Passed! - Failed: 0, Passed: 36, Total: 36
```

Mutation check: making the "inside assets\" rule always pass (`true ? null : ...`) fails 4 tests (`theme.json` and
`assets` as image and font paths); the source was restored and rebuilt before the final run.

## M3: theme values are not validated

### Before (old code)

| Test | Result on old code |
|---|---|
| `Null_pages_list_gives_one_empty_page` (`"Pages": null`) | `NullReferenceException` in `Theme.Normalize()` (Theme.cs line 45) |
| `Null_pages_widget_lists_and_widgets_are_dropped_or_replaced` | `NullReferenceException` in `Theme.Normalize()` (line 49, legacy `"Widgets": [null]`) |
| `A_live_or_undo_snapshot_is_normalized_too` (`ApplySnapshot`, used by undo and the runner's `LIVE`) | `NullReferenceException` in `Theme.Normalize()` |
| `A_null_sensor_id_no_longer_breaks_rendering` | `ArgumentNullException: Value cannot be null. (Parameter 'key')` from `Dictionary.FindValue` while rendering, as the review said |
| `Null_strings_become_their_defaults_and_every_kind_still_renders` | `Expected: "Untitled"`, `Actual: null` |
| `Screen_size_is_clamped_to_1_to_4096` (0x0, -5x-1, 100000x320, 480x2147483647) | 4 failed: values outside 1 to 4096 kept |
| `Extreme_numbers_are_clamped_so_the_property_panel_and_renderer_cope` | `OverflowException: Value was either too large or too small for a Decimal` at `Convert.ToDecimal`, which is what `PropertiesPanel.Num` calls |
| `Unknown_enum_numbers_fall_back_to_the_defaults` (`"Kind": 99`) | `Expected: Text`, `Actual: 99` |
| `Valid_themes_load_unchanged` (8 themes) | passed (guard) |

### Fix (`Theme.Normalize`, now `internal`, still run by `Load` and `ApplySnapshot`)

- Theme: null `Name`/`BackgroundColor` get their defaults; `Width`/`Height` are clamped to 1 to 4096 (`Theme.MaxScreenSide`);
  null `Pages` becomes a new list; null pages are dropped (as before); null legacy widgets are skipped.
- Page: null `Name` becomes `Page N`; null `Widgets` becomes an empty list; null widgets are dropped;
  `DurationSeconds` is clamped to 1 to 86400 (the pusher already waits at least 1 s, so 0.5 behaved as 1 anyway).
- Widget: an empty or null `Id` gets a new one; every null string gets the default of a new `Widget`, except `Text`, which
  becomes `""` (null means no text, not the word "Text"); unknown enum numbers (`Kind`, `Align`, `Fit`) get the default.
- Widget numbers: NaN becomes the default, Infinity and large values the nearest limit.
  `X`,`Y`,`StartAngle` +/-100000; `Width`,`Height` 0 to 8192; `FontSize` 1 to 1000; `Thickness` 0 to 1000;
  `CornerRadius` 0 to 4096; `Opacity` 0 to 100; `HistorySeconds` 1 to 86400; `Sweep` -360 to 360;
  `Multiplier`,`Min`,`Max`,`WarnAt`,`CritAt` +/-1e12 (safe for `decimal`, which overflows at 7.9e28).
- JSON compatibility: every limit is wider than what the editor lets you enter (`PropertiesPanel`, page duration box),
  so a theme the editor wrote is not changed. Proven by `Valid_themes_load_unchanged`: the starter, every template,
  and a theme at the edges of every editor range give byte-identical JSON after `Load`.
- The display loop's `StablePages` copy (already on `main`) stays; it protects against concurrent edits, this protects
  against bad files. They do not overlap.

Unhandled exceptions in the editor: there was no handler. `Program.Main` now logs through `Glance.Core.Diagnostics.CrashLog`
to `%AppData%\Glance\editor.log`:
- `AppDomain.UnhandledException` (logged, the process still ends),
- `TaskScheduler.UnobservedTaskException` (logged, marked observed),
- `Dispatcher.UIThread.UnhandledException` (logged, marked handled so the editor keeps running and unsaved work is not lost).
The log is capped at 1 MB (`CrashLog.MaxBytes`) and rotated to `editor.log.old`, so an error that repeats every frame
cannot fill the disk. `CrashLog.Write` never throws.

### After

```
ThemeNormalizeTests: Passed! - Failed: 0, Passed: 19, Total: 19
ThemeInputApiTests:  Passed! - Failed: 0, Passed: 13, Total: 13
CrashLogTests:       Passed! - Failed: 0, Passed: 3,  Total: 3
```

## Whole suite

```
dotnet build src/Glance.sln -c Release -p:SkipHost=true   -> 0 errors (warnings are the existing ones)
dotnet test src/Glance.Tests -c Release                    -> Passed! Failed: 0, Passed: 613, Total: 613
```

613 = the 542 tests on `main` plus the 71 new ones.

## Not verified / left open

- The editor's new dialog and crash handler were built but not run by hand (no UI automation here). The dialog reuses the
  existing `ReportWindow`.
- `Theme.Save` still checks a stored *rooted* path with `File.Exists` before copying it (it does not go through
  `ResolveAsset`), so saving a hand-made theme that holds a UNC path would still touch that host once. Imported `.glance`
  themes are cleaned before they can reach Save. `Save` is being changed on another branch (atomic saves); the one-line
  follow-up there is to skip `Theme.IsNetworkOrDevicePath` paths in `Adopt` (or decide to keep them for user-picked NAS files).
- Colour strings are not validated: the renderer already falls back on unparsable colours.
- Reserved device names inside `assets\` (`assets/CON`) are the separate finding L3.
