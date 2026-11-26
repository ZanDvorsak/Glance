# Fixing the live preview of themes without a folder: a walkthrough

This walkthrough is for a mid-level developer who wants to understand one small but instructive change in Glance: the fix for
finding **H2** in `docs/reviews/review-full-codebase.md`. The bug was a single `StringSplitOptions` flag. The fix is worth studying
because of what came with it: a tiny text protocol moved out of a UI process into a tested library, kept compatible with older
programs on the other end of the pipe, and a failure that used to be silent now shows up in the editor.

It assumes you know C# and .NET, and that you have used async/await and a UI framework. Named pipes and the editor/runner split are
explained where they first appear. For the bigger picture, read `docs/WALKTHROUGH.md` (section 4.7 covers the runner and its pipe).

**The change:** branch `claude/fix-live-empty-folder-5aa7c3`, commit `e5182a4`.

**Contents**

1. [The problem](#1-the-problem)
2. [Technologies at a glance](#2-technologies-at-a-glance)
3. [High-level walkthrough](#3-high-level-walkthrough)
4. [Detailed code review](#4-detailed-code-review)
   - 4.1 [The bug, line by line](#41-the-bug-line-by-line)
   - 4.2 [`ControlCommand`: one place for the wire format](#42-controlcommand-one-place-for-the-wire-format)
   - 4.3 [Parsing without losing empty fields](#43-parsing-without-losing-empty-fields)
   - 4.4 [The runner: from string cases to verbs](#44-the-runner-from-string-cases-to-verbs)
   - 4.5 [The editor: sending, and noticing failure](#45-the-editor-sending-and-noticing-failure)
   - 4.6 [The tests, and how to know they test the bug](#46-the-tests-and-how-to-know-they-test-the-bug)
5. [Self-review: five ways to improve the change](#5-self-review-five-ways-to-improve-the-change)

---

## 1. The problem

Glance runs as two processes. The **runner** is an elevated tray app that owns the USB display. The **editor** is a normal-user Avalonia app
where you design themes. While the runner is running, only it can talk to the display, so when you click **Send to display** in the editor,
the editor streams your unsaved theme to the runner over a named pipe with a `LIVE` command, about twice a second when something changed.

`LIVE` carries two values: the theme's folder (so the runner can find images like `assets/bg.png`) and the theme JSON. A theme you have just
created with **New** or from a template has **no folder yet**, because it has never been saved. For those themes the editor sent a line
with an empty folder field, the runner misread it, replied `ERR unknown command`, and the editor ignored the reply. The status bar said your
edits were on the display while the display kept showing the old saved theme.

The review asked for three things:

- send a placeholder for "no folder", or parse without dropping empty fields;
- move command parsing and encoding into `Glance.Core` and test it, including the empty folder;
- show a failed live send in the status bar.

---

## 2. Technologies at a glance

| Area | Technology | Role in this change |
|---|---|---|
| Inter-process communication | **Windows named pipes** (`NamedPipeServerStream`, `NamedPipeClientStream`) | The editor connects to `Glance.Runner.Control`, writes one line, reads one reply line, and disconnects. An ACL limits the pipe to the current Windows user. |
| Protocol | A **line-based text protocol** | `VERB arg arg\n`, reply `OK ...` or `ERR ...`. Easy to debug by hand, but every separator rule matters. |
| Payload encoding | **UTF-8 + Base64** | Turns any folder path or JSON (spaces, newlines, non-ASCII) into characters from `A-Z a-z 0-9 + / =`, which never contain the space separator. |
| String splitting | `string.Split(char, int count, StringSplitOptions)` | The bug and the fix both live in the options passed here. |
| Modelling | **C# records**, `[NotNullWhen(true)]` | `ControlCommand` is an immutable `sealed record` with value equality (handy in tests); the `Try` pattern tells the nullable analysis when `out` is set. |
| Editor UI | **Avalonia 12**, `Dispatcher.UIThread.Post` | Background work must hop back to the UI thread before touching controls such as the status text. |
| Runner UI | **WinForms** `Control.BeginInvoke` | Pipe threads marshal UI work (reload, pause, quit) onto the tray app's UI thread. Unchanged here. |
| Tests | **xUnit** `[Theory]`, `[InlineData]`, `[MemberData]` | Table-driven tests for every command, plus a *mutation check* to prove the tests catch the bug. |

---

## 3. High-level walkthrough

### 3.1 The data flow before and after

```
Editor (PushLive, every 500 ms if the theme changed)            Runner (control pipe thread)
  snapshot JSON + BaseFolder                                       Execute(line)
        |                                                                |
  before: "LIVE " + b64(folder ?? "") + " " + b64(json)            Split(' ', 3, RemoveEmptyEntries)
          folder null -> "LIVE  e30="  (two spaces)  ------------> ["LIVE", "e30="]  -> 2 parts, case needs 3
                                                                   -> "ERR unknown command"  (editor ignored it)

  after:  ControlCommand.Live(json, folder).Encode()              ControlCommand.TryParse(line)
          folder null -> "LIVE - e30="  ------------------------> verb LIVE, folder "-" -> null, json "{}"
                                                                   -> new Theme { BaseFolder = null }.ApplySnapshot(json) -> "OK"
          on failure -> status bar message
```

### 3.2 The wire format, written down once

The new class documents the whole protocol in one place:

| Line | Meaning |
|---|---|
| `STATUS`, `RELOAD`, `PAUSE`, `RESUME`, `EXIT`, `NEXT`, `PREV`, `LIVE_CLEAR` | No arguments |
| `PAGE <n>` | Pin page n (1-based); `0` releases the pin |
| `BRIGHTNESS <0-7>` | Backlight level, clamped by the runner |
| `LIVE <b64 folder> <b64 json>` | Show an unsaved theme until `LIVE_CLEAR`. **New:** `-` in the folder field means "no folder". Still accepted: an empty folder field (old editors). |

Why `-`? Base64 output only uses letters, digits, `+`, `/` and `=`. A single `-` can therefore never be the encoding of a real folder,
so it is a safe sentinel. A folder that is literally named `-` is encoded as `LQ==` like any other folder, and there is a test for that.

### 3.3 Compatibility between versions

The editor and the runner are separate executables, and the runner is long-lived (it may be started at sign-in), so for a while an old
runner can meet a new editor, or the other way round:

| Editor | Runner | Theme with a folder | Theme without a folder |
|---|---|---|---|
| old | old | works | **broken (H2)** |
| old | new | works | **works**: the new parser keeps the empty field |
| new | new | works | works |
| new | old | works | still fails (the old runner cannot decode `-`), but the editor now **tells you** |

The installer kills a running `Glance.Runner.exe` before it copies files, so installed users normally only see the "new/new" row.

---

## 4. Detailed code review

### 4.1 The bug, line by line

**Before**, in `src/Glance.App/Services/RunnerClient.cs`:

```csharp
public static bool SendLive(string snapshotJson, string? baseFolder)
{
    var folder = Convert.ToBase64String(Encoding.UTF8.GetBytes(baseFolder ?? ""));
    var json = Convert.ToBase64String(Encoding.UTF8.GetBytes(snapshotJson));
    return Send($"LIVE {folder} {json}", 2000) is { } r && r.StartsWith("OK");
}
```

`Convert.ToBase64String` of zero bytes is the empty string, so a null folder produced `"LIVE " + "" + " " + json`: two spaces in a row.

**Before**, in `src/Glance.Runner/RunnerContext.cs`:

```csharp
var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
switch (parts.Length > 0 ? parts[0].ToUpperInvariant() : "")
{
    ...
    case "LIVE" when parts.Length == 3:
    {
        var folder = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
        ...
    }
    ...
    default: return "ERR unknown command";
}
```

`RemoveEmptyEntries` threw away the empty field between the two spaces, so `parts` had 2 entries, the `when parts.Length == 3` guard failed,
and the switch fell through to `default`. Two lessons hide here:

- **`RemoveEmptyEntries` and positional fields do not mix.** It is the right choice for "words separated by any amount of space", and the wrong one
  for "field 1, field 2, field 3" where a field may legitimately be empty.
- **A `when` guard that fails falls through silently.** The reply said "unknown command", which pointed at the verb rather than the arguments and
  made the bug look like something else.

On top of that, the editor discarded the `bool` that `SendLive` returned:

```csharp
Task.Run(() => RunnerClient.SendLive(snap, folder));
```

### 4.2 `ControlCommand`: one place for the wire format

**File:** `src/Glance.Core/Control/ControlCommand.cs` (new)

The fix moves the protocol into `Glance.Core`, which both processes reference and which has tests. The class is a `sealed record` with a
private constructor, so the only ways to get one are the factories and the parser (abridged: doc comments shortened, some members elided):

```csharp
public enum ControlVerb { Status, Reload, Pause, Resume, Exit, Next, Prev, Page, Brightness, Live, LiveClear }

public sealed record ControlCommand
{
    public const string NoFolder = "-";

    private ControlCommand(ControlVerb verb) { Verb = verb; }

    public ControlVerb Verb { get; }
    public int Number { get; private init; }          // PAGE or BRIGHTNESS
    public string? Folder { get; private init; }      // LIVE, null when the theme has none
    public string? ThemeJson { get; private init; }   // LIVE

    public static ControlCommand Status { get; } = new(ControlVerb.Status);
    // ... Reload, Pause, Resume, Exit, Next, Prev, LiveClear
    public static ControlCommand Page(int page) => new(ControlVerb.Page) { Number = page };
    public static ControlCommand Brightness(int level) => new(ControlVerb.Brightness) { Number = level };

    public static ControlCommand Live(string themeJson, string? folder) =>
        new(ControlVerb.Live) { ThemeJson = themeJson ?? throw new ArgumentNullException(nameof(themeJson)),
                                Folder = string.IsNullOrEmpty(folder) ? null : folder };
```

A few design points worth noticing:

- **Normalise at the edge.** `Live()` turns an empty folder into `null`, so "no folder" has exactly one representation inside the program. The old runner
  did the same normalisation, but only after decoding; now both sides share it.
- **`private init`** lets the factories use object initialisers while keeping the record immutable from outside.
- **Records give value equality for free.** The tests compare a command with its parsed copy using `Assert.Equal(command, parsed)`.

Encoding is a single switch expression:

```csharp
public string Encode()
{
    var word = WordOf(Verb);
    return Verb switch
    {
        ControlVerb.Page or ControlVerb.Brightness => word + " " + Number.ToString(CultureInfo.InvariantCulture),
        ControlVerb.Live => word + " " + (Folder is null ? NoFolder : ToBase64(Folder)) + " " + ToBase64(ThemeJson ?? ""),
        _ => word,
    };
}
```

`InvariantCulture` is a small but real improvement: the old code used `$"PAGE {n}"`, which formats with the current culture. For small positive
integers that makes no difference, but protocol text should never depend on the user's regional settings.

The verb words live in one table, `Words`, that both `Encode` (through `WordOf`) and `TryParse` read, so the spelling of `LIVE_CLEAR` cannot differ
between the two directions.

### 4.3 Parsing without losing empty fields

`TryParse` follows the familiar `TryXxx` pattern, with an extra `error` string that the runner sends back as `ERR <error>`:

```csharp
public static bool TryParse(string? line, [NotNullWhen(true)] out ControlCommand? command, out string error)
{
    command = null; error = "";
    line = line?.Trim() ?? "";
    var space = line.IndexOf(' ');
    var word = (space < 0 ? line : line[..space]).ToUpperInvariant();
    var rest = space < 0 ? "" : line[(space + 1)..];
    ...
```

The verb is everything up to the **first** space; `rest` is everything after it, untouched. Then each verb reads `rest` the way it needs to:

```csharp
case ControlVerb.Page:
case ControlVerb.Brightness:
{
    var arg = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
    {
        error = word + " needs a number";
        return false;
    }
    command = new ControlCommand(found) { Number = n };
    return true;
}
case ControlVerb.Live:
{
    // Split on single spaces without dropping empty fields: an older editor sent an empty folder as "LIVE  <json>".
    var fields = rest.Split(' ', 2);
    if (fields.Length != 2) { error = "LIVE needs a folder and a theme"; return false; }
    var folderField = fields[0];
    var jsonField = fields[1].Trim();
    if (jsonField.Length == 0) { error = "LIVE needs a theme"; return false; }
    if (!TryFromBase64(jsonField, out var json)) { error = "LIVE theme is not valid base64"; return false; }
    string? folder = null;
    if (folderField.Length > 0 && folderField != NoFolder)
    {
        if (!TryFromBase64(folderField, out folder)) { error = "LIVE folder is not valid base64"; return false; }
    }
    command = Live(json, folder);
    return true;
}
```

This is the heart of the fix. Compare the two splits on the old editor's line `"LIVE  e30="` (after the verb is cut off, `rest` is `" e30="`):

| Call | Result |
|---|---|
| `" e30=".Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)` | `["e30="]`: the empty folder field is gone |
| `" e30=".Split(' ', 2)` | `["", "e30="]`: folder is empty, JSON is in its place |

Note that `PAGE` and `BRIGHTNESS` deliberately *keep* `RemoveEmptyEntries`: there, "a number somewhere after the verb" is the right reading, and it
keeps `PAGE   2` working as before. Choosing the split per field layout, not once for the whole line, is what the old code got wrong.

Base64 decoding errors are caught and turned into a clear reason:

```csharp
private static bool TryFromBase64(string text, out string value)
{
    value = "";
    try { value = Encoding.UTF8.GetString(Convert.FromBase64String(text)); return true; }
    catch (FormatException) { return false; }
}
```

(.NET 8 also has `Convert.TryFromBase64String`, which avoids the exception but needs a buffer of the right size; for a command that arrives a
couple of times a second, the try/catch is fine.)

### 4.4 The runner: from string cases to verbs

**File:** `src/Glance.Runner/RunnerContext.cs`

The runner's `Execute` keeps its shape. Only the parsing moved out, and the `case` labels became enum values:

```csharp
// before
case "PAGE" when parts.Length > 1 && int.TryParse(parts[1], out var pg):
    lock (_gate) { _pinPage = pg > 0 ? pg - 1 : null; if (_pusher is not null) _pusher.PinnedPage = _pinPage; }
    return "OK";
```

```csharp
// after
if (!ControlCommand.TryParse(line, out var cmd, out var error)) return "ERR " + error;
switch (cmd.Verb)
{
    ...
    case ControlVerb.Page:   // PAGE <n> pins the display to page n (1-based); PAGE 0 releases it
        lock (_gate) { _pinPage = cmd.Number > 0 ? cmd.Number - 1 : null; if (_pusher is not null) _pusher.PinnedPage = _pinPage; }
        return "OK";
    ...
    case ControlVerb.Live:
    {
        var t = new Theme { BaseFolder = cmd.Folder };
        t.ApplySnapshot(cmd.ThemeJson!);
        lock (_gate) _live = t;
        return "OK";
    }
```

What a reviewer should check in a refactor like this, and what holds here:

- **The lock scopes are identical.** Every `lock (_gate)` and every `_ui.BeginInvoke(...)` is the same as before, so the threading model (pipe thread,
  WinForms UI thread, display loop) did not change.
- **The theme is built outside the lock.** `ApplySnapshot` runs on a fresh `Theme` that nothing else can see yet; only the reference swap
  `_live = t` happens under `_gate`. That keeps the lock short and means the display loop never sees a half-applied theme.
- **Error replies changed only in wording.** `PAGE x` used to fall through to `ERR unknown command`; now it answers `ERR PAGE needs a number`.
  Callers only check `StartsWith("OK")`, so that is safe, and it is much easier to debug.
- **Exceptions are still caught one level up.** `Handle` wraps `Execute` in `try { ... } catch (Exception ex) { reply = "ERR " + ex.Message; }`,
  so invalid JSON inside valid base64 still produces an error reply instead of killing the pipe thread.

### 4.5 The editor: sending, and noticing failure

**File:** `src/Glance.App/Services/RunnerClient.cs`

```csharp
public static string? Send(ControlCommand command, int timeoutMs = 800) => Send(command.Encode(), timeoutMs);

public static bool SendLive(string snapshotJson, string? baseFolder) =>
    Send(ControlCommand.Live(snapshotJson, baseFolder), 2000) is { } r && r.StartsWith("OK");
```

`SendLive` no longer knows anything about base64 or separators. The other commands (`PAGE`, `BRIGHTNESS`, `RESUME`, `PAUSE`, `LIVE_CLEAR`,
`STATUS`, `EXIT`) are still sent as hand-written strings from `MainWindow.cs` and `ServiceWindow.cs`. They happen to match what `Encode()` produces
today; section 5 comes back to this.

**File:** `src/Glance.App/MainWindow.cs`, `PushLive`

```csharp
// before
Task.Run(() => RunnerClient.SendLive(snap, folder));
```

```csharp
// after
Task.Run(() => RunnerClient.SendLive(snap, folder)).ContinueWith(t =>
{
    if (t.IsCompletedSuccessfully && t.Result) return;
    Dispatcher.UIThread.Post(() =>
    {
        if (_liveToRunner && _lastLive == snap)   // only for the latest edit, and only while still sending
            _status.Text = "The background runner did not accept your edits, so the display may still show the old theme.";
    });
});
```

Three things make this correct:

1. **Pipe work stays off the UI thread.** `SendLive` can block for up to 2 s (connect plus reply), so it runs in `Task.Run`, as `AGENTS.md` requires.
2. **UI state is only touched on the UI thread.** The continuation runs on a thread-pool thread, so it posts to `Dispatcher.UIThread` before reading
   `_liveToRunner` and `_lastLive` or writing `_status.Text`.
3. **Stale failures are ignored.** Sends can overlap (the timer ticks every 500 ms), so by the time a failure arrives the user may have made another edit
   that succeeded, or stopped sending. Comparing with `_lastLive` and `_liveToRunner` on the UI thread drops those late reports.

The same `Task.Run(...).ContinueWith(t => Dispatcher.UIThread.Post(...))` shape is already used in `TurnDisplayOff`, so the change follows an existing
pattern in the file.

### 4.6 The tests, and how to know they test the bug

**File:** `src/Glance.Tests/ControlCommandTests.cs` (new, 42 test cases)

The tests are table-driven. One `[Theory]` covers the round trip of every simple command:

```csharp
public static IEnumerable<object[]> AllCommands() => new[]
{
    new object[] { ControlCommand.Status, "STATUS" },
    ...
    new object[] { ControlCommand.Page(0), "PAGE 0" },
    new object[] { ControlCommand.Brightness(7), "BRIGHTNESS 7" },
};

[Theory]
[MemberData(nameof(AllCommands))]
public void Every_command_encodes_to_its_wire_form_and_parses_back(ControlCommand command, string wire)
{
    Assert.Equal(wire, command.Encode());
    Assert.Equal(command, Parse(command.Encode()));
}
```

Asserting the exact wire text (`"PAGE 0"`) as well as the round trip matters: a round trip alone would still pass if both sides changed the format in the
same wrong way, and an old runner would then stop understanding a new editor.

The H2 test checks the line itself, not only the round trip:

```csharp
[Theory]
[InlineData(null)]
[InlineData("")]
public void Live_without_a_folder_round_trips(string? folder)
{
    // the H2 bug: a new theme has no folder, the line had two spaces in a row and the runner rejected it
    var line = ControlCommand.Live("{}", folder).Encode();
    Assert.DoesNotContain("  ", line);
    Assert.Equal(3, line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
    var cmd = Parse(line);
    Assert.Equal(ControlVerb.Live, cmd.Verb);
    Assert.Null(cmd.Folder);
    Assert.Equal("{}", cmd.ThemeJson);
}
```

The `Split(..., RemoveEmptyEntries).Length == 3` assertion is a nice touch: it checks the line with the *old* runner's own splitting rule.

And a separate test pins down backwards compatibility with the exact text an older editor sent:

```csharp
var noFolder = Parse($"LIVE {B64("")} {B64("{}")}");   // "LIVE  e30="
Assert.Null(noFolder.Folder);
```

Other tests cover spaces and non-ASCII characters in both the folder and the JSON, a folder literally named `-`, applying the parsed command to a `Theme`
the way the runner does, case-insensitive verbs, numbers read as before (`PAGE   2`, `page 2 extra`, `BRIGHTNESS -3`), unknown verbs
(`LIVECLEAR`, `STATUSX`) and bad arguments (`PAGE 1.5`, `LIVE e30=`, invalid base64).

**How do you know the tests would have caught the bug?** Put the bug back and run them. This is the *mutation check* that `AGENTS.md` recommends:

| Mutation in `ControlCommand.cs` | Tests that fail |
|---|---|
| Send `""` instead of `-` for no folder (the editor half of H2) | `Live_without_a_folder_round_trips` (both cases) |
| Split LIVE with `RemoveEmptyEntries` (the runner half of H2) | `Live_from_an_older_editor_is_still_understood` |
| Stop turning `""` into `null` in `Live()` | `Live_without_a_folder_round_trips("")` |

One mutation survives: removing the `folderField.Length > 0 &&` check. That is fine. It is an *equivalent mutant*: `""` decodes to `""`, which `Live()`
already turns into `null`, so the behaviour cannot change. Recognising equivalent mutants is part of reading mutation results honestly.

What the tests cannot cover: the verb-to-action `switch` stays in `Glance.Runner`, which has no test project, and nothing here runs the editor or a real
display.

---

## 5. Self-review: five ways to improve the change

These are the changes I would make next, roughly in order of value.

### 1. Finish moving the encoding into Core, then let the compiler enforce it

`AGENTS.md` now says commands are built "only" with `ControlCommand`, but seven call sites still format strings by hand:

```csharp
Task.Run(() => { RunnerClient.Send("RESUME"); RunnerClient.Send($"BRIGHTNESS {b}"); RunnerClient.Send($"PAGE {pg}"); });
```

**Suggestion:** use the factories everywhere (`RunnerClient.Send(ControlCommand.Resume)`, `ControlCommand.Brightness(b)`, `ControlCommand.Page(pg)`,
and so on in `MainWindow.cs` and `ServiceWindow.cs`), then make `RunnerClient.Send(string, int)` private. After that, a hand-built command line no longer
compiles, which is a stronger guarantee than a sentence in `AGENTS.md`. Do it after the shutdown and autostart branches merge, because they edit the
lines right next to these calls.

### 2. Retry a failed live send instead of only reporting it

`PushLive` stores the snapshot in `_lastLive` *before* sending, so after a failure the next tick sees "nothing changed" and never resends. The warning
also stays on screen after a later send succeeds.

**Suggestion:** keep one send in flight at a time, clear `_lastLive` when the latest send fails so the next tick retries, and restore the normal status
text on success. Put that small state machine in Core (for example `LiveSendState.ShouldSend(snapshot)` and `Completed(snapshot, ok)`) so it can be
tested like `ControlCommand`. The single-in-flight rule also stops overlapping sends from reaching the runner out of order.

### 3. Tell the user *why* a live send failed

`SendLive` returns a `bool`, so the editor cannot tell "the runner did not answer within 2 s" (it may still apply the edit, because the runner can be
waiting on its display lock) from "the runner answered `ERR LIVE folder is not valid base64`" (for example an older runner that does not know `-`).

**Suggestion:** return the reply or a small result (`Ok`, `NoAnswer`, `Rejected(reason)`), use a longer reply timeout than connect timeout, and word the
message to match: "did not answer in time" versus "rejected your edits (...). If you just updated Glance, restart the background runner."

### 4. Make the verb table impossible to get out of sync

`ControlVerb` and the `Words` table are maintained by hand, and the round-trip test lists commands by hand too. Adding a verb to the enum but not to the
table would compile and then throw `ArgumentOutOfRangeException` from `Encode()` at run time.

**Suggestion:** add a test that loops over `Enum.GetValues<ControlVerb>()` and round-trips each verb. Optionally move the pipe name
`"Glance.Runner.Control"`, now defined in both `RunnerClient` and `RunnerContext`, next to the wire format in Core.

### 5. Small API polish

- Rename the namespace `Glance.Core.Control`. Both UI frameworks in the solution have a type called `Control` (the runner uses WinForms
  `Control _ui` in the same file that now imports this namespace), so `Glance.Core.Runner` or `Glance.Core.Ipc` would read more clearly.
- `ToString()` returns `Encode()`, which for `LIVE` is the whole base64 theme. A short form such as `LIVE <folder> <1234 bytes>` is kinder in the
  debugger and in test output.
- Add a one-line comment on `folderField.Length > 0` saying it is the old-editor case, so the next reader knows `Live()` also normalises it.
