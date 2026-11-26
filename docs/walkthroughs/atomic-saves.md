# Saves that cannot be half done: a walkthrough of the M4 fix

This walkthrough is for a mid-level developer who wants to understand one change in Glance: how saving `theme.json`,
`settings.json` and `.glance` packages was made **all or nothing**, how the runner reads those files safely while the editor
writes them, and what happens to a settings file that turns out to be corrupt. It explains the Windows file-system behaviour the
change relies on, walks through the code, shows how the tests prove the bug and the fix, and ends with five ways the change could be better.

It assumes you know C# and .NET. You do not need to know the rest of Glance; the parts that matter are explained here. For the big
picture, read `docs/WALKTHROUGH.md` (sections 4.4 "The theme model", 4.7 "The runner and its control pipe" and 4.10 "Sharing themes
safely" are the background for this change).

**The change:** branch `claude/fix-atomic-saves-54bf6d`, three commits on `main` (`9bf03b0`):
`88db92c` (a seam, no behaviour change), `7383b55` (tests), `216ab94` (the fix). Findings M4 and the export part of L3 in
`docs/reviews/review-full-codebase.md`.
**Files:** `src/Glance.Core/Storage/AtomicFile.cs` (new), `src/Glance.Core/Settings/AppSettings.cs`, `src/Glance.Core/Themes/Theme.cs`,
`src/Glance.Core/Themes/ThemePackage.cs`, `src/Glance.Runner/RunnerContext.cs`, `src/Glance.Tests/AtomicSaveTests.cs` (new).
**Design note by the author:** `docs/design/fix-atomic-saves.md`. **Independent review:** `docs/reviews/review-atomic-saves.md`.

**Contents**

1. [The problem](#1-the-problem)
2. [Technologies involved](#2-technologies-involved)
3. [High-level walkthrough](#3-high-level-walkthrough)
4. [Detailed code review](#4-detailed-code-review)
5. [Self-review: five ways to improve the change](#5-self-review-five-ways-to-improve-the-change)

---

## 1. The problem

Glance keeps its state in a handful of small files under `%AppData%\Glance`:

```
%AppData%\Glance\
  settings.json              LastTheme, Brightness, PinDisplayToPage, SetupCheckShown
  Themes\<name>\theme.json   the theme (pages and widgets)
  Themes\<name>\assets\...   its images, videos and fonts
```

Two processes use them. The **editor** writes them. The **runner** (the elevated tray app that drives the display) watches the folder with a
`FileSystemWatcher` for `*.json`, and when something changes it re-reads `settings.json`, then the theme `LastTheme` points at.

Before this change, three things could go wrong:

| What | Old code | What it could cause |
|---|---|---|
| Saving settings | `File.WriteAllText(FilePath, json)` | `WriteAllText` opens with `FileMode.Create`, which **truncates the file first**, then writes. A crash, power cut or I/O error in between leaves an empty or half-written `settings.json`. |
| Loading settings | `try { Deserialize(...) } catch { return new(); }` | Every problem, from "the file is missing" to "it is corrupt" to "another process has it open", became the **defaults** (`LastTheme = null`, `Brightness = 7`), silently. The next save then wrote the defaults over whatever was left. |
| Runner reload | `AppSettings.Load()` once, no retry | If the runner read while the editor was writing, it got a sharing violation or a half file, switched to the defaults and showed the starter theme at full brightness. |
| Exporting `.glance` | `File.Delete(destination)`, then write the zip in place | A failed export (say an image locked by another program) destroyed the previous package and left a broken zip. |

`theme.json` was in slightly better shape: the H1 fix (Save As) already wrote `theme.json.saving` and renamed it over `theme.json`. But it did
not flush the data to the disk before the rename, and it kept no backup.

---

## 2. Technologies involved

| Area | Technology | What it does in this change |
|---|---|---|
| Atomic replace | **NTFS rename** via `File.Move(src, dst, overwrite: true)` (Win32 `MoveFileEx` with `MOVEFILE_REPLACE_EXISTING`) | Swaps a fully written temporary file in under the real name. On one NTFS volume a rename is a single journaled metadata operation: afterwards the name points at the old file or the new one, never at a mix. |
| Durability | **`FileStream.Flush(flushToDisk: true)`** (Win32 `FlushFileBuffers`) | Forces the temporary file's data out of the OS cache onto the disk **before** the rename, so after a power cut the new name cannot point at blocks that were never written. |
| The alternative | **`File.Replace`** (Win32 `ReplaceFile`) | Designed for "replace and keep a backup", and it preserves the old file's ACL and attributes. It was **not** used, because some of its failure codes leave no file under the target name (section 4.4). |
| Sharing | **`FileShare`** modes, sharing violations, byte-range locks (`FileStream.Lock`) | Why a rename or a read can fail for a moment while another process has the file open, and how the tests fake "a crash halfway through a write". |
| Change notification | **`FileSystemWatcher`** with a filter of `*.json` | The runner's trigger to reload. A temp-and-rename save produces a different stream of events than an in-place write (section 4.10). |
| Parsing | **System.Text.Json** `JsonException` | The signal for "this file is not (yet) valid": a half-written file, an empty file, `null`, zero bytes, or a value of the wrong type. |
| Tests | **xUnit**, a `TempDir` helper, real files and real locks | Every test works on real files in a temporary folder, with real sharing violations from a second handle. |

**A short primer on renaming over a file on Windows.** Three facts drive most of the design:

1. A rename inside one folder never copies data; it changes a directory entry. That is why the temporary file must live **in the same folder**
   as the target (same volume). `File.Move` would quietly fall back to copy-and-delete across volumes, which is not atomic.
2. Without the newer "POSIX semantics" flag (which .NET 8's `File.Move` does not use), Windows **refuses to replace a file that anyone has
   open**, whatever sharing flags they used. The rename fails with "access denied" (`UnauthorizedAccessException` in .NET). The review measured this:
   even a handle opened with `FileShare.ReadWrite | FileShare.Delete` blocks the swap.
3. `FlushFileBuffers` makes the file's **data** durable. The rename itself is journaled by NTFS, so it is consistent after a crash, but without
   `MOVEFILE_WRITE_THROUGH` it may not be durable yet: after a power cut you can wake up with the old file. That is fine; the old file is whole.

---

## 3. High-level walkthrough

### 3.1 One helper for every save

The change adds one small static class, `Glance.Core.Storage.AtomicFile`, and routes the three writers through it:

```
                     AtomicFile.Write(path, write, keepBackup)
 Theme.Save ─────┐   1. write to  <name>.<guid>.tmp   (same folder, CreateNew)
 AppSettings.Save├─► 2. Flush(true)                   (data on the disk)
 ThemePackage    │   3. [keepBackup] copy target -> <name>.bak.<guid>.tmp
   .Export ──────┘   4. File.Move(tmp, target, overwrite)   <- the only moment the name changes
                     5. [keepBackup] File.Move(bak tmp, <name>.bak, overwrite)   (best effort)
                     any failure before 4: delete the temp files, rethrow; the target is untouched
```

`theme.json` keeps one backup (`theme.json.bak`); settings and packages do not.

### 3.2 One helper for every read that can race a write

`AtomicFile.ReadWithRetry(Func<T>)` runs a read and, for about one second, tries again when the file is **busy** (a sharing violation,
"access denied") or **does not parse yet** (`JsonException`, in case some other program is writing it in place). A missing file fails at once.
The runner wraps its `Theme.Load` in it, and `AppSettings.TryLoad` uses it for settings.

### 3.3 Settings: three outcomes instead of one

`AppSettings.TryLoad(path, out settings, log)` replaces the old catch-all:

| The file is... | Returns | Settings | Side effects |
|---|---|---|---|
| missing | `true` | defaults | none (first run) |
| valid | `true` | the saved ones | none |
| corrupt, still after ~1 s | `true` | defaults | logged; renamed to `settings.json.corrupt-<yyyyMMdd-HHmmss>` so no save can overwrite it |
| unreadable (still locked after ~1 s) | `false` | defaults | logged; the file is left alone |

The runner uses the `false` case: on a reload it **keeps the settings it already has** instead of jumping to the defaults.

### 3.4 How the change was proven

The author used a three-step "seam, red, green" technique that is worth copying:

1. **Seam** (`88db92c`): add `AtomicFile` and the new `AppSettings` overloads, and route every caller through them, **with the old behaviour
   inside** (`WriteAllText` in place, delete-then-create, no retry, swallow everything). The suite stays at 542/542.
2. **Red** (`7383b55`): add `AtomicSaveTests` (18 tests). Against the seam, **13 fail**, each for the reason the finding describes.
3. **Green** (`216ab94`): replace the seam's insides with the real implementation. All 560 pass.

The seam is what makes step 2 possible: the tests can call `AppSettings.Save(path)` and `TryLoad(path, ...)` on a temporary folder
before the fix exists, instead of touching the real `%AppData%`.

---

## 4. Detailed code review

### 4.1 `AtomicFile.Write`

```csharp
public static void Write(string path, Action<Stream> write, bool keepBackup = false)
{
    var target = Path.GetFullPath(path);
    var dir = Path.GetDirectoryName(target)!;
    var name = Path.GetFileName(target);
    var temp = Path.Combine(dir, $"{name}.{Guid.NewGuid():N}.tmp");
    string? backupTemp = null;
    try
    {
        using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            write(fs);
            fs.Flush(flushToDisk: true);   // the content is on the disk before the name points at it
        }

        if (keepBackup && File.Exists(target))
        {
            // a copy, not a rename: the target keeps its whole old content until the final rename below
            backupTemp = Path.Combine(dir, $"{name}.bak.{Guid.NewGuid():N}.tmp");
            var copyFrom = target; var copyTo = backupTemp;
            Retry(() => File.Copy(copyFrom, copyTo));
            File.SetAttributes(backupTemp, FileAttributes.Normal);
        }

        Retry(() => File.Move(temp, target, overwrite: true));
    }
    catch
    {
        TryDelete(temp);
        if (backupTemp is not null) TryDelete(backupTemp);
        throw;
    }

    if (backupTemp is not null)
    {
        // the save itself has succeeded; failing to keep the backup is not a reason to report it as failed
        try { Retry(() => File.Move(backupTemp, BackupPath(target), overwrite: true)); }
        catch { TryDelete(backupTemp); }
    }
}
```

Read it line by line:

- **`Path.Combine(dir, ...)`**: the temporary file sits next to the target, so the final `File.Move` is a same-volume rename (fact 1 above).
- **`{name}.{guid}.tmp`**: a random part means two saves (two editor windows, or a retry after a crash) never collide, and `FileMode.CreateNew`
  makes sure we never write into somebody else's file. The name ends in `.tmp`, not `.json`, so the runner's `*.json` watcher does not react to it.
- **`FileShare.None`** on the temp file: nobody can read it while it is incomplete.
- **`write(fs)` then `fs.Flush(flushToDisk: true)` inside the `using`**: the callback's own writers (a `StreamWriter`, a `ZipArchive`) are disposed
  inside the callback, so their buffers are already in `fs`. `Flush(true)` then pushes `fs` to the disk. A plain `Flush()` (or `Dispose`) only
  hands the bytes to the OS cache.
- **The backup is a copy, not a rename.** If it were `File.Move(target, bak)`, there would be a moment with **no** `theme.json` at all, and the
  runner's `File.Exists(p)` check would pick the starter layout. Copying keeps the target whole until the one rename that matters.
- **`Retry(() => File.Move(temp, target, overwrite: true))`** is the last statement in the `try`. That matters for the caller: once it has
  returned, nothing in this method can throw, so `Theme.Save` never rolls back assets for a theme file that was actually written (4.9).
- **The `catch`** deletes both temporary files and rethrows. The target was never opened for writing, so it is exactly as it was.
- **After the `try`**: the backup rename is best effort. A failure is swallowed and the stray copy deleted. Note the asymmetry the review points out:
  failing to *create* the backup copy (line 59) still fails the whole save, because it is inside the `try`.

`WriteAllText` is a thin wrapper that writes UTF-8 **without** a byte-order mark, the same bytes `File.WriteAllText` produced before, so old
and new files are byte-compatible:

```csharp
public static void WriteAllText(string path, string text, bool keepBackup = false) =>
    Write(path, stream =>
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.Write(text);
    }, keepBackup);
```

`leaveOpen: true` is important: without it, disposing the writer would close `fs` before `Flush(true)` runs.

### 4.2 Why not just write the file and call `Flush(true)`?

Because the dangerous moment is the truncation, not the writing. `File.WriteAllText` opens with `FileMode.Create`, which sets the length to zero
first. Between that and the last byte, the file on disk is wrong, and no flush can fix a crash in that window. Writing elsewhere and renaming moves
the only change of state to one atomic step.

### 4.3 Why `Flush(true)` matters even with the rename

Without it, the order of what reaches the disk is up to the cache manager. NTFS journals metadata (the rename), not file data. After a power cut it is
possible to have the **new name** pointing at a file whose data blocks were never written: zeros or garbage, which is the exact failure M4 is about.
`FlushFileBuffers` closes that gap: data first, then the name. This cannot be unit-tested; it is the one line you have to get right by reading.

### 4.4 `File.Move(overwrite)` versus `File.Replace`

`File.Replace(source, target, backup)` looks made for this, and it has a real advantage: the result keeps the **old file's** ACL, attributes,
creation time and alternate data streams. The author chose `File.Move` because `ReplaceFile` has failure codes that are worse than failing:
`ERROR_UNABLE_TO_MOVE_REPLACEMENT` without a backup name can leave **no file** under the target name, and `ERROR_UNABLE_TO_MOVE_REPLACEMENT_2`
leaves the original under the backup name. The cost of `File.Move` is that the new file gets the folder's inherited permissions and default
attributes. In `%AppData%\Glance` that is what you want anyway; for an exported package in a user's folder it does not matter.

### 4.5 Retrying: `Retry` and `IsBusy`

```csharp
private static T Retry<T>(Func<T> operation, Func<Exception, bool> worthRetrying)
{
    var watch = Stopwatch.StartNew();
    while (true)
    {
        try { return operation(); }
        catch (Exception ex) when (worthRetrying(ex) && watch.Elapsed < RetryBudget) { Thread.Sleep(RetryDelay); }
    }
}

private static bool IsBusy(Exception ex) =>
    ex is UnauthorizedAccessException || (ex is IOException && ex is not FileNotFoundException && ex is not DirectoryNotFoundException);
```

Things to notice:

- **The exception filter (`when`)** decides without unwinding the stack. When the budget is spent, the filter is false and the *original* exception
  propagates with its own stack trace. There is no "retry count" to get wrong, and the loop cannot spin forever: the budget is 1 s, the delay 50 ms.
- **`UnauthorizedAccessException` counts as busy.** That looks odd, but it is what Windows reports when you rename over a file someone has open
  (fact 2 in section 2). The price is that a permanent "access denied" (a read-only `theme.json`, an ACL problem) also waits a full second before failing.
  The review measured 1,04 s for a read-only target.
- **Missing files fail at once.** `FileNotFoundException` and `DirectoryNotFoundException` derive from `IOException`, so they have to be excluded
  explicitly. A test (`A_theme_read_of_a_missing_file_fails_at_once`) pins this with a 500 ms limit.
- **`Thread.Sleep` blocks the caller.** The editor saves on its UI thread and the runner reloads on its UI thread while holding `_gate`. In the bad
  case, a save can now take up to about two seconds (backup copy retries plus rename retries) instead of failing at once.

`ReadWithRetry` reuses the same loop with one more retryable case:

```csharp
public static T ReadWithRetry<T>(Func<T> read) => Retry(read, ex => IsBusy(ex) || ex is JsonException);
```

The `JsonException` case is not needed for files Glance writes itself (they are never half-written any more). It is there for a file that **another
program** rewrites in place, for example the user saving `settings.json` from Notepad, while the runner reads it.

### 4.6 `AppSettings.TryLoad` and `KeepAside`

```csharp
public static bool TryLoad(string path, out AppSettings settings, Action<string>? log = null)
{
    settings = new();
    string? text = null;
    try
    {
        settings = AtomicFile.ReadWithRetry(() =>
        {
            text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(text) ?? throw new JsonException("The file holds no settings.");
        });
        return true;
    }
    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return true; }   // first run
    catch (JsonException ex)
    {
        KeepAside(path, ex.Message, log);
        return true;
    }
    catch (Exception ex)
    {
        log?.Invoke($"Could not read {path}: {ex.Message}");
        return false;
    }
}
```

- The literal `null` deserializes to a `null` object without an exception, so the lambda turns it into a `JsonException`. Empty files and
  zeroed blocks already throw. All four corrupt shapes therefore take the same path, and the theory test covers each one.
- The order of the `catch` blocks matters: `FileNotFoundException` before the general one, `JsonException` before the general one.
- `text` is assigned but not used afterwards; it is a leftover (harmless).
- `JsonException` also covers **a valid JSON file with a wrong type**, for example `"Brightness": "high"`. The review checked this: the file is kept
  aside as corrupt after a one-second wait. That is reasonable, and the original is kept, but be aware that it is not only "broken bytes".

`KeepAside` renames the bad file out of the way, with a copy as the fallback:

```csharp
var stem = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
var aside = stem;
for (var n = 2; File.Exists(aside); n++) aside = $"{stem}-{n}";
try
{
    try { File.Move(path, aside); }
    catch (FileNotFoundException) { return; }   // another process (the editor or the runner) moved it first
    catch { File.Copy(path, aside); }           // cannot rename it: at least keep a copy before the next save replaces it
    log?.Invoke($"The settings file {path} is corrupt ({reason}). It was kept as {Path.GetFileName(aside)} and the default settings are used.");
}
```

The name ends in a timestamp, not `.json`, so the watcher does not pick the copy up, and nothing will ever load it by accident. The editor, which has
no log of its own, gets a new `glance.log` through `DefaultLog`; the runner passes its own `Log` (to `runner.log`). The user is **not** told in the UI.

### 4.7 The runner

Two lines changed in `RunnerContext.cs`:

```csharp
_theme = p is not null && File.Exists(p) ? AtomicFile.ReadWithRetry(() => Theme.Load(p)) : Theme.CreateDefault(null);
```

```csharp
// a settings file that cannot be read right now keeps the settings already in use (never the defaults)
if (AppSettings.TryLoad(out var fresh, Log)) _settings = fresh;
```

The second line is the payoff of the three-outcome design: a reload that cannot read the file changes nothing, so the display does not flash the
starter theme at full brightness. At start-up there are no settings to keep yet, so the field initializer still uses `AppSettings.Load(Log)`, which
returns the defaults in the unreadable case.

### 4.8 The editor (unchanged, and why that matters)

The editor still does `private readonly AppSettings _settings = AppSettings.Load();` and later `_settings.Save()` (after a theme save, after an
import, after the setup check, and **always on close**). `Load()` returns the defaults when `TryLoad` says `false`, and nothing remembers that.
The review shows the consequence with a probe: if `settings.json` is locked for longer than a second when the editor starts, the editor's next
save writes the defaults over the good file. That is finding M1 in the review and suggestion 1 below.

### 4.9 Callers: `Theme.Save` and `ThemePackage.Export`

`Theme.Save` keeps the H1 order (copy assets, write the theme file, only then change the open theme). The only change is the write itself:

```csharp
// all or nothing, flushed to disk, and the replaced file is kept as theme.json.bak (see AtomicFile)
AtomicFile.WriteAllText(target, JsonSerializer.Serialize(copy, Json), keepBackup: true);
```

and the H1 `temp`/`.saving` handling is gone from the `catch`, because `AtomicFile` cleans up its own files. If `AtomicFile` throws, the H1
rollback deletes the assets this save copied. If it returns, the file is in place, and the backup step can no longer make it throw. The two
features compose without a gap.

`ThemePackage.Export` used to delete and recreate. Now the zip is written through the same helper:

```csharp
AtomicFile.Write(destination, stream =>
{
    using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
    WriteEntries(copy, zip);
});
```

`using var zip` disposes the archive (which writes the zip's central directory) at the end of the lambda, before `Flush(true)`. If
`WriteEntries` throws, the zip is disposed on the way out, the temp file is deleted, and the previous package is untouched. The temp file lives in the
export folder (for example the Desktop), so a crash in the middle can leave a `mine.glance.<guid>.tmp` there.

### 4.10 What the watcher sees

The review recorded the events a `FileSystemWatcher` configured like the runner's (`*.json`, subfolders, `LastWrite | FileName | Size`) receives:

```
settings save:  Deleted settings.json | Renamed settings.json.<guid>.tmp -> settings.json
theme save:     Deleted Themes\a\theme.json | Renamed Themes\a\theme.json.<guid>.tmp -> Themes\a\theme.json
keep aside:     Renamed settings.json -> settings.json.corrupt-20261126-213939
.glance export: Created mine.glance.<guid>.tmp | Renamed mine.glance.<guid>.tmp -> mine.glance   (unfiltered watcher)
```

Two lessons:

- .NET raises `Renamed` when **either** the old or the new name matches the filter, which is why the runner, which already handled `Renamed` since H1, still
  sees every save. The temp file's own create and write events are filtered out, so there is no storm: one reload per save, debounced.
- Replacing a file reports a `Deleted` for the target first. The runner does not subscribe to `Deleted`, which is good: a handler that reacted to it
  would see a "missing" file that is not really missing.

### 4.11 The tests

`AtomicSaveTests` has 18 cases. The interesting technique is how it fakes a crash:

```csharp
/// <summary>Opens the file the way an indexer or virus scanner might (sharing read and write) and locks its first byte.</summary>
private static FileStream LockFirstByte(string path)
{
    var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    fs.Lock(0, 1);
    return fs;
}
```

With the old code, `File.WriteAllText` can still open the file (the share mode allows it) and **truncate** it, and then fails when it writes into the
locked byte. The file is left empty, which is exactly what a crash after the truncation leaves. The proof table in the design note and the review's
re-run both show `Actual: ""` for the old code.

With the new code there is no truncation to observe, so the same test checks a different thing: that any failure leaves the old file. For settings the
failure comes from the rename (the target is open). For the theme, the review found it comes earlier, from the **backup copy** reading the locked byte.
Both are valid "failed save" paths, but neither is a crash during the temp-file write; that case is safe by construction rather than by test.

Other tests use a second thread that holds the file with `FileShare.None` for 300 ms (should be waited out) or 4 s (should be reported as unreadable),
or that finishes an in-place write after 250 ms (should be waited for, not called corrupt). The concurrency test does 300 saves while another thread
reads in a loop, and counts every read that fell back to the defaults; it must be zero.

---

## 5. Self-review: five ways to improve the change

### 1. Let the editor respect "could not read"

`TryLoad` already tells the caller that it could not read the file, but the editor throws that information away. Keep it, and do not overwrite
a file you never managed to read:

```csharp
private readonly AppSettings _settings;
private bool _settingsReadable;

// constructor
_settingsReadable = AppSettings.TryLoad(out _settings);
if (!_settingsReadable) _status.Text = "Settings could not be read; they will not be saved this session.";

// everywhere the editor saves
if (_settingsReadable) _settings.Save();
```

A gentler variant re-reads the file just before saving and only overwrites the fields the editor changed. Either one closes the last path by which
M4's "good settings replaced by the defaults" can still happen.

### 2. Tell the user when settings were kept aside

Today a corrupt `settings.json` produces a line in `glance.log` or `runner.log`, and the user only sees that the display went back to the starter theme.
Return the "kept aside" path from `TryLoad` (for example as an `out string? keptAside`, or a small result record) and show it once in the editor's status
bar: "Your settings file was damaged and was saved as settings.json.corrupt-...; Glance started with default settings." The runner could put the same
sentence in its tray tooltip.

### 3. Use the backup, and make it truly optional

`theme.json.bak` is written on every save but never read. Two small changes would make it worth its disk space:

- In the runner's `LoadTheme` (and the editor's open), when `theme.json` fails to parse, try `theme.json.bak`, log that you did, and show it.
- Move the backup **copy** out of the critical path: if `File.Copy` of the old file fails, log it and continue with the save instead of failing it.
  The save is what the user asked for; the backup is a bonus.

### 4. Clean up after crashes, and do not block the UI thread

A crash between steps 1 and 4 leaves `<name>.<guid>.tmp` (or `<name>.bak.<guid>.tmp`) behind forever. At the start of `Write`, delete files in the
same folder that match `name + ".*.tmp"` and are older than, say, an hour. Separately, run `Theme.Save` and `AppSettings.Save` off the UI thread
(`Task.Run`), since a save may now sleep for up to a couple of seconds while another program holds the file; the editor's status bar can show
"Saving..." meanwhile. On Windows 10 1709 and later, a rename with POSIX semantics (`SetFileInformationByHandle` with
`FILE_RENAME_FLAG_POSIX_SEMANTICS | FILE_RENAME_FLAG_REPLACE_IF_EXISTS`) would even succeed while a scanner holds the old file open, which removes most
of the waiting.

### 5. Make the timing injectable, and test the missing races

`RetryBudget` is a hard-coded `internal static readonly` one second, so the suite now spends about seven extra seconds sleeping (four corrupt cases at one
second each, plus a four-second lock test), and a slow CI machine can push the 250 ms and 300 ms "finish in time" tests close to the budget. Pass a budget
(or a small `RetryPolicy`) into `AtomicFile`, use 200 ms in tests, and add the tests that are missing today:

- a reader that loads `theme.json` in a loop while `Theme.Save` runs 300 times (the settings version exists; the theme version, with the backup step, does not);
- the watcher's event sequence for one save (exactly one `Renamed` that matches `*.json`, no `Changed` for the temp file);
- `KeepAside`'s `-2` suffix and copy fallback, and a `.bak` that cannot be replaced (the save must still succeed).
