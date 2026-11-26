# Save As keeps the theme's files: a walkthrough of the H1 fix

This walkthrough is for a mid-level developer who wants to understand one small but important change in Glance: how **Save As** was
fixed so that a theme saved into a new folder takes its images, videos and fonts with it. It covers how a theme refers to its files,
why the old code lost them, what the new code does line by line, how the tests prove it, and what could still be better.

It assumes you know C# and .NET. You do not need to know the rest of Glance; the parts that matter are explained here. For the big
picture, read `docs/WALKTHROUGH.md` (section 4.4, "The theme model", is the background for this change).

**The change:** branch `claude/fix-saveas-assets-ee5527`, commit `736133d`, finding H1 in `docs/reviews/review-full-codebase.md`.
**Files:** `src/Glance.Core/Themes/Theme.cs`, `src/Glance.Tests/ThemeTests.cs`.
**Independent review of this change:** `docs/reviews/review-saveas-assets.md`.

**Contents**

1. [The problem](#1-the-problem)
2. [Technologies involved](#2-technologies-involved)
3. [High-level walkthrough](#3-high-level-walkthrough)
4. [Detailed code review](#4-detailed-code-review)
5. [Self-review: five ways to improve the change](#5-self-review-five-ways-to-improve-the-change)

---

## 1. The problem

A Glance theme is a folder: a `theme.json` plus an `assets` folder holding the pictures, videos and font files the widgets use.
Inside `theme.json`, a widget refers to its file with a path **relative to the theme folder**:

```json
{ "Kind": "Image", "ImagePath": "assets/bg.png", "FontFile": "" }
```

Relative paths are what make a theme portable. The folder can be moved, zipped into a `.glance` package or copied to another PC,
and it still works.

The bug: open `Themes\A\theme.json`, choose **Save As**, and save it as `Themes\B\theme.json`. Folder B got a `theme.json` that still said
`assets/bg.png`, but **no `assets` folder**. The editor clears its image cache right after saving, so the background vanished at
once. If the user later deleted folder A, which looks like a safe clean-up because "B is the copy", the files were gone for good. That is
why the full-codebase review rated it High: it loses user data, quietly.

---

## 2. Technologies involved

| Area | Technology | What it does in this change |
|---|---|---|
| Language and runtime | **C# 12 / .NET 8** | Local functions, pattern matching (`is not null`) and nullable reference types are used in `Theme.Save`. |
| File system | **`System.IO`**: `Path`, `File`, `FileInfo`, `Directory` | Resolving paths, comparing folders, copying files and picking free names. The details of `Path.IsPathRooted`, `Path.GetFullPath` and `Path.Combine` matter here (section 4). |
| Serialization | **System.Text.Json** | `Theme` is serialized as is. `BaseFolder` is `[JsonIgnore]`, so the folder a theme lives in is runtime state, not file content. |
| Model change notification | **CommunityToolkit.Mvvm** `[ObservableProperty]` | `Widget.ImagePath` and `Widget.FontFile` raise `PropertyChanged` when `Save` rewrites them. The editor listens for undo and repaint (see 4.6). |
| Editor UI | **Avalonia 12** `StorageProvider.SaveFilePickerAsync` | Shows the Save As dialog and returns the path that `Theme.Save` receives. |
| Tests | **xUnit** with a `TempDir` helper | Each test builds real folders and files in a temporary directory and checks bytes on disk. |

---

## 3. High-level walkthrough

### 3.1 How a theme finds its files

Two pieces of state decide where a widget's file is:

- **`Widget.ImagePath` / `Widget.FontFile`**: either an absolute path (`C:\Users\me\Pictures\bg.gif`, usually just picked with
  *Browse...*) or a relative one (`assets/bg.png`, the normal saved form).
- **`Theme.BaseFolder`**: the folder `theme.json` was loaded from or last saved to. It is not saved in the JSON. `Theme.Load` sets it, and
  `Theme.Save` updates it.

`ResolveAsset` puts them together, and every consumer (the renderer, font loading, export) goes through it:

```csharp
public string? ResolveAsset(string path)
{
    if (string.IsNullOrEmpty(path)) return null;
    if (Path.IsPathRooted(path)) return path;
    return BaseFolder is null ? null : Path.Combine(BaseFolder, path);
}
```

### 3.2 What Save does

`Theme.Save(path)` writes `theme.json` and first **adopts** the files the theme uses: it copies them into `<folder>\assets` and
rewrites the widget paths to `assets/<name>`, so the folder is self-contained. Then it sets `BaseFolder` to the new folder.

### 3.3 Why Save As lost the files

The old adoption rule was "copy a file only when its path is absolute". On a Save As, a relative path such as `assets/bg.png` was kept
as it was, and then `BaseFolder` switched from A to B. From that moment `ResolveAsset("assets/bg.png")` pointed at `B\assets\bg.png`,
which nobody had created:

```
before Save As:  BaseFolder = A   "assets/bg.png" -> A\assets\bg.png   (exists)
after  Save As:  BaseFolder = B   "assets/bg.png" -> B\assets\bg.png   (missing)
```

### 3.4 The fix in one paragraph

Relative paths are now resolved against the **old** `BaseFolder`, and the file is copied when the theme is being saved to a **different**
folder. A save to the same folder (plain Ctrl+S) leaves relative paths alone, so there is no copying and the theme's own subfolders are kept.
Missing files are left as they are, and the name-clash handling is unchanged. That is all: about 20 changed lines in one method, plus four tests.

---

## 4. Detailed code review

### 4.1 `Adopt`, before and after

**Before** (`main`):

```csharp
// returns the relative path to store, or the original when it is not an existing absolute file
string Adopt(string path)
{
    if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || !File.Exists(path)) return path;
    Directory.CreateDirectory(assets);
    var name = Path.GetFileName(path);
    var dest = Path.Combine(assets, name);
    var n = 1;
    while (File.Exists(dest) && new FileInfo(dest).Length != new FileInfo(path).Length)
        dest = Path.Combine(assets, $"{Path.GetFileNameWithoutExtension(name)}_{n++}{Path.GetExtension(name)}");
    if (!File.Exists(dest)) File.Copy(path, dest);
    return "assets/" + Path.GetFileName(dest);
}
```

The guard `!Path.IsPathRooted(path)` is the bug: every relative path leaves at the first line.

**After** (this branch):

```csharp
var sameFolder = BaseFolder is not null && SameFolder(BaseFolder, dir);

// returns the relative path to store, or the original when the file cannot be found
string Adopt(string path)
{
    if (string.IsNullOrEmpty(path)) return path;
    var rooted = Path.IsPathRooted(path);
    if (!rooted && sameFolder) return path;          // already next to the theme
    var source = rooted ? path : ResolveAsset(path); // relative paths belong to the old folder
    if (source is null || !File.Exists(source)) return path;
    Directory.CreateDirectory(assets);
    var name = Path.GetFileName(source);
    var dest = Path.Combine(assets, name);
    var n = 1;
    while (File.Exists(dest) && new FileInfo(dest).Length != new FileInfo(source).Length)
        dest = Path.Combine(assets, $"{Path.GetFileNameWithoutExtension(name)}_{n++}{Path.GetExtension(name)}");
    if (!File.Exists(dest)) File.Copy(source, dest);
    return "assets/" + Path.GetFileName(dest);
}
```

The new code separates two ideas the old code mixed up: **what is stored** in the widget (`path`) and **where the bytes are**
(`source`). Walk through the cases:

| Widget path | Save target | What happens |
|---|---|---|
| empty | any | returned unchanged |
| relative, e.g. `assets/bg.png` | same folder | returned unchanged, no I/O (the Ctrl+S case) |
| relative | different folder | `source = ResolveAsset(path)`, i.e. `<old folder>\assets\bg.png`, copied into the new `assets` |
| absolute | any | `source = path`, copied into the new `assets`, as before |
| anything whose file is missing | any | returned unchanged, so the theme still saves and the reference is not lost |

`ResolveAsset` returns `null` when `BaseFolder` is `null` (a theme that has never been saved). The `source is null` check covers that, so
a relative path in an unsaved theme is left as it is rather than crashing.

### 4.2 Order matters: resolve first, then move `BaseFolder`

`Adopt` is a **local function**. It captures `this`, so `ResolveAsset` inside it reads the *current* `BaseFolder`. The method relies on
the order of these lines:

```csharp
foreach (var w in Pages.SelectMany(p => p.Widgets))
{
    w.ImagePath = Adopt(w.ImagePath);   // ResolveAsset still sees the OLD BaseFolder here
    w.FontFile = Adopt(w.FontFile);
}
BaseFolder = dir;                        // only now does the theme "move"
File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
```

If someone moved `BaseFolder = dir;` above the loop, every relative path would resolve against the new folder, find nothing, and be
left as it was. That is the original bug again. The commit message says this exact mutation ("resolving against the new folder") was tried and a
test failed. When you touch this method, keep that line last.

Also notice `Pages.SelectMany(p => p.Widgets)`. `Theme.Widgets` is only the *active page* (see `WALKTHROUGH.md` 4.4), so walking `Pages`
is what makes every page's files come along. The main test puts a video and an outside image on page two to prove it.

### 4.3 Deciding "same folder"

```csharp
private static bool SameFolder(string a, string b)
{
    static string Norm(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);   // Windows paths are case-insensitive
}
```

Three details:

- `Path.GetFullPath` collapses `.` and `..` segments and turns `/` into `\`, so `C:\Themes\A\..\A` equals `C:\Themes\A`.
- `TrimEnd` removes a trailing separator, so `C:\Themes\A\` equals `C:\Themes\A`. The test deliberately sets `BaseFolder` with a trailing slash.
- `OrdinalIgnoreCase`, because NTFS folder names are case-insensitive by default.

It compares *strings*, not folders, so a junction or a `subst` drive would look like a different folder. That fails safe: the code takes the
copy path, and for files directly under `assets\` the destination is the source itself (`File.Exists(dest)` with equal size), so nothing
is copied.

**Why have a same-folder short-cut at all?** Without it, a normal Ctrl+S would run every relative file through the copy path. Top-level
files would be no-ops, but `assets/fonts/digits.ttf` would be copied to `assets/digits.ttf` and the widget rewritten. The theme's own layout
would change on every save. The third test pins this down.

### 4.4 The name-clash loop (unchanged, but now used much more)

```csharp
while (File.Exists(dest) && new FileInfo(dest).Length != new FileInfo(source).Length)
    dest = Path.Combine(assets, $"{Path.GetFileNameWithoutExtension(name)}_{n++}{Path.GetExtension(name)}");
if (!File.Exists(dest)) File.Copy(source, dest);
```

It reads as: "if a file with this name is already there and has a *different size*, try `name_1`, `name_2`, and so on. If it has the *same size*,
assume it is the same file and reuse it." The only change here is `path` → `source`.

That size-only rule was already flagged (L2 in the full review, and the "Review note" in `WALKTHROUGH.md` 4.4). Before this change it applied only to
files the user had just browsed for. Now it applies to **every relative asset on every Save As**, so its weakness matters more. The review of this
change (M2) shows a same-size BMP in the destination being reused in place of the theme's own picture.

### 4.5 Where it is called from

The editor's save handler is unchanged (`src/Glance.App/MainWindow.cs`):

```csharp
try
{
    _theme.Save(path);
    _undo.Rebase(_theme.ToSnapshot());   // saving rewrites image paths; that is not an undo step
    _themePath = path; _settings.LastTheme = path; _settings.Save();
    _renderer.ClearImageCache();
    _status.Text = "Saved " + path;
}
catch (Exception ex) { _status.Text = "Save failed: " + ex.Message; }
```

Things to know when you read this together with the fix:

- `Save` runs on the **UI thread**. Copying a large video now happens on every Save As to a new folder, so the editor can pause (review L2).
- `Rebase` keeps the undo history. Older snapshots hold paths that were relative to the *old* folder (review L1).
- If `Save` throws halfway, the `catch` shows a message, but the widgets already processed have their new paths while `BaseFolder` is
  still old (review M1). Section 5 shows how to make that all-or-nothing.

Other callers are unaffected. The AIDA64 import saves into the same folder it imported into, so it is a same-folder save. A `.glance` import loads from the folder
it extracted to. `ThemePackage.Export` does not call `Save`; it has its own `Pack` function built on the same `ResolveAsset` idea.

### 4.6 The tests

Four tests were added to `ThemeModelTests`, with a small helper that lists the files in a folder (an empty array if the folder does not exist):

```csharp
private static string[] FilesIn(string folder) =>
    Directory.Exists(folder) ? Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).OrderBy(f => f).ToArray() : Array.Empty<string>();
```

**1. `Save_as_to_another_folder_takes_every_image_video_and_font_along`.** This is the H1 regression test. It builds folder A with an image, a video and a
font, plus an absolute file elsewhere, spread over **two pages**, with one image used twice. It then saves to B and checks:

```csharp
Assert.Equal(new[] { "bg.png", "clip.mp4", "digits.ttf", "photo.jpg" }, FilesIn(dir.Combine("B", "assets")));
...
Assert.All(used, p =>
{
    Assert.False(Path.IsPathRooted(p));
    var file = Path.GetFullPath(back.ResolveAsset(p)!);
    Assert.StartsWith(dir.Combine("B") + Path.DirectorySeparatorChar, file);   // in the new folder, not the old one
    Assert.True(File.Exists(file), file);
});
Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(back.ResolveAsset(back.Pages[0].Widgets[0].ImagePath)!));
```

Note the `StartsWith(B + separator)` check. Without it the test could pass if the paths still resolved into A (where the files do exist).
Comparing **bytes**, not just names, proves the right file landed under each name. The test also reloads the theme from disk with `Theme.Load`, so
it tests the JSON as well as the in-memory object, and it checks that folder A is left untouched.

**2. `Save_as_keeps_two_different_files_with_the_same_name_apart`.** A relative `assets/pic.png` and an absolute `elsewhere\pic.png` must end up as two files.
The two files have different sizes (1 and 2 bytes). That is what the size-only rule can tell apart; two same-size files would not be.

**3. `Saving_again_to_the_same_folder_copies_nothing_extra`.** This one saves to the theme's own folder twice (the second time after a reload). It uses a trailing slash on
`BaseFolder` and a font in a subfolder, and checks that the folder contents and the stored paths are unchanged.

**4. `Save_as_leaves_missing_files_as_they_are`.** Missing relative and absolute files keep their paths, the save still succeeds, and no empty `assets` folder is created.

**Mutation check.** AGENTS.md recommends breaking the code on purpose to see a test fail. The author reports three mutations. The independent review
repeated it with four:

| Mutation | Fails |
|---|---|
| `if (!rooted) return path;` (the old behaviour) | tests 1 and 2 |
| same-folder short-cut removed | test 3 |
| `FontFile` not adopted | test 1 and an existing `FontTests` test |
| size check removed from the clash loop | test 2 |

---

## 5. Self-review: five ways to improve the change

These are listed roughly in order of value. The first two match the medium findings in `docs/reviews/review-saveas-assets.md`.

### 1. Make `Save` all-or-nothing for the in-memory theme

The loop rewrites each widget as soon as its file is copied, and `BaseFolder` moves only at the end. One failed `File.Copy` in the middle
leaves a mix: some widgets have paths relative to the new folder, while `BaseFolder` is still the old one. A later Ctrl+S then writes those
broken paths into the *original* `theme.json`. A two-phase version is barely longer:

```csharp
// phase 1: work out every new path and copy every file; touch nothing on the theme yet
var map = new Dictionary<string, string>();               // stored value -> new stored value
foreach (var value in Pages.SelectMany(pg => pg.Widgets).SelectMany(w => new[] { w.ImagePath, w.FontFile }))
    if (!map.ContainsKey(value)) map[value] = Adopt(value);   // may throw: the theme is still untouched

// phase 2: nothing can fail any more except the final write
foreach (var w in Pages.SelectMany(pg => pg.Widgets)) { w.ImagePath = map[w.ImagePath]; w.FontFile = map[w.FontFile]; }
BaseFolder = dir;
```

Even better, write `theme.json` to a temporary file and move it into place (M4 in the full review), and only then commit phase 2.
Add a test that locks one source with `FileShare.None` and checks that nothing on the theme changed.

### 2. Compare contents, not just sizes, before reusing a file

Replace "same name and same size means same file" with "same size **and** same bytes". A streamed byte compare runs only when the sizes
match, so it costs almost nothing in the common case:

```csharp
static bool SameContent(string a, string b)
{
    var fa = new FileInfo(a); var fb = new FileInfo(b);
    if (fa.Length != fb.Length) return false;
    using var sa = fa.OpenRead(); using var sb = fb.OpenRead();
    // compare in 64 KB blocks ...
}
```

Also keep a `source -> dest` dictionary for the duration of one `Save` (`ThemePackage.Export` already does this with `used`). Then
`assets/x/p.png` and `assets/y/p.png` can never both become `assets/p.png`.

### 3. Do the copying off the UI thread

`SaveAsync` calls `_theme.Save(path)` on the UI thread, and Save As now copies whole videos. Combined with suggestion 1, `Save` could work on a
clone (as `ThemePackage.Export` does with `ApplySnapshot(ToSnapshot())`) inside `Task.Run`. The UI thread then applies the resulting paths and
`BaseFolder` when the task completes. The editor stays responsive, and the display pusher never sees a half-rewritten theme.

### 4. Reset undo history when the folder changes

`_undo.Rebase` keeps snapshots whose relative paths meant "in folder A". After Save As they silently mean "in folder B". Have `Save` report
whether the folder changed (for example, return a small result record), and let `SaveAsync` call `_undo.Reset(...)` in that case. Or adopt the
files referenced by older snapshots too. Resetting is simpler and easy to explain in the status bar.

### 5. Decide and document the rules for odd paths

The change copies whatever a relative path resolves to, including `../..` paths that leave the theme folder. It flattens
`assets/sub/file` to `assets/file` on Save As but not on Ctrl+S. And it leaves relative paths alone when the theme has never been saved. Each of
these is defensible, but none is written down or tested. Write them into the `Save` doc comment, add one test each, and consider refusing (or
just not following) relative paths that escape the old folder. That lines up with M2 in the full review, which already asks for widget paths to be
validated on import.
