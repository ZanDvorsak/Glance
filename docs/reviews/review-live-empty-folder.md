# Code review: live preview of themes without a folder (H2)

**Date:** 2026-11-26
**Branch:** `claude/fix-live-empty-folder-5aa7c3`, one commit `e5182a4` ("Fix live preview of themes without a folder (H2)") on top of `main` (`0a9f10b`).
**Scope:** the full diff (`AGENTS.md`, `Glance.App/MainWindow.cs`, `Glance.App/Services/RunnerClient.cs`, new `Glance.Core/Control/ControlCommand.cs`,
`Glance.Runner/RunnerContext.cs`, new `Glance.Tests/ControlCommandTests.cs`) and the code around it, plus how it merges with the five sibling branches.
**Method:** read the diff and the surrounding code, built and tested the branch, ran mutation checks against the new parser (then restored the file
and confirmed `git status` was clean), and trial-merged every sibling branch with `git merge-tree` (no working tree was touched).
**No code was changed by this review.** The editor and runner were not started, and nothing was checked on the physical display.

---

## Verdict: merge after fixes

The H2 fix itself is correct and complete. A theme with no folder now goes over the pipe as `LIVE - <base64 json>`, the runner accepts it,
and the runner still accepts the old `LIVE  <json>` form (empty field) and the old form with a folder. Every other command behaves as before,
locking is unchanged, the tests are meaningful (they fail when the bug is put back), and the branch merges cleanly with all five siblings.

Two medium findings should be handled before merging, or recorded as an explicit follow-up:

- **M1:** the brief asked to move command *encoding* into Core, but seven editor call sites still build raw strings, and the new `AGENTS.md`
  sentence ("Build and parse them only with `ControlCommand`") is not true of the code it describes. Either finish the move or soften the sentence.
- **M2:** a failed live send is now reported, but it is never retried, so the display can stay on stale content until the next edit.

## Build and test results

| Check | Result |
|---|---|
| `dotnet build src/Glance.sln -c Release -p:SkipHost=true` (branch) | **Succeeded**, 0 errors, 30 warnings (all pre-existing: CA1416, CS0618, xUnit analyzers, CS9057, one CS8602 in `MainWindow.cs:1224`). None in the changed files. |
| `dotnet test src/Glance.Tests -c Release` (branch) | **295/295 passed** in about 2 s (42 of them are the new `ControlCommandTests`). Re-run after the mutation checks: 295/295. |
| Trial merge of `main` + all five sibling branches + this branch, in order | **Clean** at every step (`git merge-tree --write-tree`, exit 0). |
| Build and test of that merged tree (exported to a scratch folder) | **Succeeded**, 0 errors; **437/437 tests passed**. |

### Mutation checks (each applied to `ControlCommand.cs`, tests run, file restored with `git checkout`, `git status` clean afterwards)

| Mutation | Result |
|---|---|
| `Encode` sends `""` instead of `-` for no folder (the original bug) | 2 tests fail (`Live_without_a_folder_round_trips`, null and `""`) |
| `TryParse` splits LIVE with `RemoveEmptyEntries` (the original runner bug) | 1 test fails (`Live_from_an_older_editor_is_still_understood`) |
| `Live()` no longer turns `""` into `null` | 1 test fails |
| `folderField.Length > 0 &&` removed from the LIVE branch | **survives**, but it is an equivalent mutant: `""` decodes to `""`, which `Live()` turns into `null` anyway (see N4) |

---

## Findings

| ID | Severity | Summary |
|---|---|---|
| M1 | Medium | Seven editor call sites still send raw command strings; the new `AGENTS.md` rule says they must not |
| M2 | Medium | A failed live send is reported but never retried, and the warning is never cleared |
| L1 | Low | The warning can be a false alarm when the runner holds `_gate` for more than 2 s |
| L2 | Low | A new editor talking to an old, still-running runner still fails for themes without a folder, and the message does not say why |
| L3 | Low | Overlapping live sends can reach the runner out of order (pre-existing, not introduced) |
| L4 | Low | Test gaps: verb table completeness and the runner's dispatch are not covered |
| N1 | Nit | The namespace `Glance.Core.Control` reads like the UI type `Control` |
| N2 | Nit | The pipe name is still defined twice |
| N3 | Nit | `ToString()` returns the whole encoded line, including the base64 theme |
| N4 | Nit | `folderField.Length > 0` is redundant |

### M1. Encoding was only half moved into Core; `AGENTS.md` now overstates it (confirmed)

- **Where:** `src/Glance.App/MainWindow.cs:217` (`$"BRIGHTNESS {b}"`), `:426` (`$"PAGE {n}"`), `:1294` (`"RESUME"`, `$"BRIGHTNESS {b}"`, `$"PAGE {pg}"`),
  `:1304` (`"LIVE_CLEAR"`), `:1319` (`"PAUSE"`); `src/Glance.App/Views/ServiceWindow.cs:66` (`"STATUS"`), `:80` (`"EXIT"`);
  `src/Glance.App/Services/RunnerClient.cs:16` (`Send(string)` is still public) and `:32` (the new `Send(ControlCommand)` overload, used only by `SendLive`);
  `AGENTS.md:77`.
- **Evidence:** only `SendLive` builds its line with `ControlCommand`. Everything else still formats text by hand. `AGENTS.md` now says:
  "Build and parse them only with `Glance.Core.Control.ControlCommand` (shared by both sides and tested)."
- **Failure scenario:** the next person changes the wire format in `ControlCommand` (for example a versioned `PAGE` or a new argument) and the round-trip
  tests pass, but the hand-built strings in the editor no longer match, which is exactly the kind of drift H2 was. Today the strings happen to be identical
  to what `Encode()` produces, so there is no live bug.
- **Remedial action:**
  - Replace each raw string with the factory: `RunnerClient.Send(ControlCommand.Brightness(b))`, `ControlCommand.Page(n)`, `ControlCommand.Resume`,
    `ControlCommand.LiveClear`, `ControlCommand.Pause`, `ControlCommand.Status`, `ControlCommand.Exit`.
  - Then make `RunnerClient.Send(string, int)` `private` so the compiler enforces the `AGENTS.md` rule.
  - **Merge order matters:** `feature-shutdown-blank` edits `MainWindow.cs:425` and `:1314-1315`, right next to the calls at `:426` and `:1319`, and
    `fix-autostart-task` edits `ServiceWindow.cs` around lines 58-93 and 112, next to `:66` and `:80`. Doing this cleanup on this branch now would
    create small textual conflicts with them. Either do it after those branches are merged, or, if this branch must merge first, change the
    `AGENTS.md` sentence to "LIVE is built and parsed with `ControlCommand`; the other editor call sites will move to it" and open a follow-up.

### M2. A failed live send is never retried, and the warning outlives the failure (confirmed)

- **Where:** `src/Glance.App/MainWindow.cs:1335-1346` (`PushLive`).
- **Evidence:**

  ```csharp
  if (snap == _lastLive) return;
  _lastLive = snap;                                   // set before the send, success or not
  ...
  Task.Run(() => RunnerClient.SendLive(snap, folder)).ContinueWith(t =>
  {
      if (t.IsCompletedSuccessfully && t.Result) return;
      Dispatcher.UIThread.Post(() => { if (_liveToRunner && _lastLive == snap) _status.Text = "The background runner did not accept your edits, ..."; });
  });
  ```

  `_lastLive` already equals the failed snapshot, so the next 500 ms tick returns at `snap == _lastLive` and nothing is resent until the user edits
  the theme again. When a later send does succeed, nothing clears the warning, so it can stay in the status bar after the problem has gone.
- **Failure scenario:** the user clicks **Send to display** while the runner is busy reopening the display (`Supervise` holds `_gate` while it opens USB,
  and `DisposePusher` can hold it for up to 2 s in `DisplayPusher.Stop`). The first LIVE times out, the warning appears, and the display keeps the old
  theme until the user happens to change something. That is the same visible symptom H2 described, only rarer.
- **Remedial action:** on failure, if `_lastLive == snap` still holds, set `_lastLive = null` so the next tick retries; keep one send in flight at a time
  (a `_liveInFlight` flag checked at the top of `PushLive`) so retries cannot stack 2 s timeouts; and on success restore the normal
  "Showing your edits on the display..." text if the warning is currently shown. A small pure helper in Core (for example `LiveSendState` with
  `ShouldSend(snapshot)`, `Completed(snapshot, ok)`) would make this testable.

### L1. The warning can be a false alarm (plausible)

- **Where:** `src/Glance.App/Services/RunnerClient.cs:16-29` (one `timeoutMs` for connect and reply), `src/Glance.Runner/RunnerContext.cs:246`
  (`lock (_gate) _live = t;`), `src/Glance.Core/Display/DisplayPusher.cs:119-125` (`Stop` waits up to 2 s, then blanks, while the caller holds `_gate`).
- **Evidence:** the runner applies the LIVE command after it gets the lock even if the editor has stopped waiting; the reply write then fails and is ignored.
- **Failure scenario:** during a runner reload, the editor waits 2 s, reports "did not accept your edits", but the runner shows the edits a moment later.
  Combined with M2 the message then stays even though the display is correct.
- **Remedial action:** give `SendLive` a longer reply timeout than its connect timeout (for example connect 1 s, reply 5 s), and word the message as
  "did not answer in time" when the reply was `null` versus "rejected your edits: <ERR text>" when it answered `ERR ...`. `SendLive` would need to return
  the reply (or a small result type) instead of a `bool`.

### L2. New editor with an old runner that is still running (confirmed by reading the old parser)

- **Where:** `src/Glance.Core/Control/ControlCommand.cs:71` (sends `-`), the old runner's `Convert.FromBase64String(parts[1])`.
- **Evidence:** an old runner splits `LIVE - <json>` into three parts and calls `Convert.FromBase64String("-")`, which throws `FormatException`;
  `Handle` replies `ERR The input is not a valid Base-64 string ...`. Themes *with* a folder still work.
- **Failure scenario:** someone runs a new editor from a zip or a dev build while an older runner (for example started by the autostart task from another
  folder) is still running. Sending a new theme fails exactly as before the fix. This is **not a regression** (it failed before too, silently) and the
  installer kills `Glance.Runner.exe` during an install (`installer/Glance.iss:54,63`), so installed users do not hit it.
- **Remedial action:** extend the status text for the `ERR` case with "If you just updated Glance, restart the background runner." Longer term, have
  `STATUS` report the runner's version so the editor can detect a mismatch.

### L3. Overlapping live sends can arrive out of order (pre-existing, not introduced)

- **Where:** `src/Glance.App/MainWindow.cs:1338`.
- **Evidence:** the live timer ticks every 500 ms and each changed snapshot starts a new `Task.Run`, while one send may take up to 2 s. The runner serves up
  to four pipe connections in parallel (`RunnerContext.cs:191`), so an older snapshot can take `_gate` after a newer one. The editor will not resend
  because `_lastLive` already holds the newer snapshot.
- **Failure scenario:** a fast series of edits while the runner is slow ends with the display showing the second-to-last version.
- **Remedial action:** the single-in-flight guard from M2 fixes this too: send the newest snapshot only when the previous send has finished.

### L4. Test gaps (confirmed)

- **Where:** `src/Glance.Tests/ControlCommandTests.cs:26-40`, `src/Glance.Runner/RunnerContext.cs:217-252`.
- **Evidence:** `AllCommands()` is a hand-written list. If someone adds a `ControlVerb` but forgets the `Words` table, `Encode()` throws
  `ArgumentOutOfRangeException` at run time and no test notices unless they also extend the list. The verb-to-action switch in `Execute` stays in the
  runner and is not tested (for example swapping the `Pause` and `Resume` cases would not fail any test).
- **Remedial action:** add `[Fact] Every_verb_has_a_wire_word()` that loops over `Enum.GetValues<ControlVerb>()` and checks that each one encodes and
  parses back (use `Live("{}", null)`, `Page(1)` and `Brightness(1)` for the verbs with arguments). The dispatch itself can stay untested for now; if it
  grows, move it behind an interface (`IRunnerControl` with `Pause()`, `Resume()`, `ShowLive(Theme)`...) so a fake can record the calls.

### N1. Namespace name `Glance.Core.Control`

- **Where:** `src/Glance.Core/Control/ControlCommand.cs:5`.
- **Evidence:** both UI frameworks in the solution have a type called `Control` (`RunnerContext.cs:24` uses WinForms `Control _ui`; Avalonia's `Control`
  is everywhere in `Glance.App`). It compiles today, because the namespace is only visible as a simple name inside `Glance.Core.*`, but it reads
  ambiguously and any future Core code that names a `Control` type will bind to the namespace.
- **Remedial action:** rename to `Glance.Core.Runner` or `Glance.Core.Ipc` (it sits next to the sensor pipe types in spirit).

### N2. Pipe name still duplicated

- **Where:** `src/Glance.App/Services/RunnerClient.cs:11`, `src/Glance.Runner/RunnerContext.cs:21`.
- **Remedial action:** move `"Glance.Runner.Control"` into Core next to the wire format (for example `ControlCommand.PipeName`) and reference it from both.

### N3. `ToString()` returns the full line

- **Where:** `src/Glance.Core/Control/ControlCommand.cs:76`.
- **Evidence:** for `LIVE` this is the whole base64 theme (often several KB). It shows up in debugger tooltips, xUnit display names and any future log line.
- **Remedial action:** return `Encode()` for short commands and `"LIVE <folder> <n bytes>"` for LIVE.

### N4. Redundant empty-folder check

- **Where:** `src/Glance.Core/Control/ControlCommand.cs:118`.
- **Evidence:** removing `folderField.Length > 0 &&` changes nothing (the mutation survives) because `Live()` already maps `""` to `null`.
- **Remedial action:** keep it (it skips a pointless decode and documents the old format), but add a short comment saying it is the old-editor case,
  so the next reader does not think it is the only thing handling it.

---

## Integration with the sibling branches

| Branch | Files shared with this branch | Textual merge | Semantic notes |
|---|---|---|---|
| `fix-saveas-assets-ee5527` | none | clean | none |
| `fix-ffmpeg-elevation-a3dfcc` | `RunnerContext.cs` (one line in the constructor), `MainWindow.cs` (FFmpeg install) | clean | none |
| `fix-display-loop-693991` | `RunnerContext.cs` (`Supervise`), `MainWindow.cs` (adds `RestartDeadPusher` directly after `PushLive`), `Theme.ApplySnapshot` | clean | `ApplySnapshot` now replaces pages in place; LIVE applies to a brand-new `Theme`, so it is unaffected. The new method lands right after the edited `PushLive`; reviewed in the merged tree, it reads correctly. |
| `fix-autostart-task-0011d2` | `ServiceWindow.cs` (not touched here) | clean | Touches the lines next to the raw `STATUS`/`EXIT` calls, so the M1 cleanup will conflict with it if done first. |
| `feature-shutdown-blank-60ded2` | `RunnerContext.cs` (fields, constructor, `Supervise`, new power section, `Quit`), `MainWindow.cs`, `AGENTS.md` | clean | `Execute` is untouched there. `RESUME` goes through `SetPaused` -> `Supervise`, which the power branch makes respect `MayDrive`, so a RESUME during sleep is correctly ignored. Its `MainWindow.cs` edits sit next to the raw `PAGE` and `PAUSE` calls (M1). |

All five merge cleanly in sequence, and the merged result builds and passes 437 tests. `RunnerContext.cs` is the file most branches touch, but this
branch only changes the `using` block and `Execute`, which no sibling edits.

---

## What was checked and found sound

- **H2 is fixed on both sides.** The editor never sends an empty field (`Encode` writes `-`), and the runner parses LIVE with `Split(' ', 2)`, which keeps
  empty fields, so even an old editor's `LIVE  <json>` works. `-` cannot be confused with a real folder: base64 uses only `A-Z a-z 0-9 + / =`, and a
  folder literally named `-` encodes to `LQ==` (tested).
- **Backwards compatibility.** Old editor + new runner: all commands work, including both LIVE forms. New editor + old runner: everything works except
  LIVE without a folder, which failed before as well (L2).
- **Every command matches the old `Execute`.** Same case-insensitive verb, same trimming, extra words after a verb ignored as before (`PAGE 2 extra`),
  same numbers (`PAGE 0` releases, `BRIGHTNESS` clamped to 0-7 in the runner, negative pages release), same `ERR unknown command` for unknown verbs.
  Bad arguments now reply `ERR <reason>` instead of `ERR unknown command`; the editor only checks `StartsWith("OK")`, so that is safe.
  Numbers are now parsed with the invariant culture instead of the current one, which is an improvement.
- **Locking and threading** in the runner are unchanged line for line: the same `lock (_gate)` scopes and the same `_ui.BeginInvoke` calls. `Execute` still
  runs on the pipe thread and exceptions (for example invalid JSON in `ApplySnapshot`) are still turned into `ERR <message>` by `Handle`.
- **UI-thread use** in `PushLive`: the continuation runs on the thread pool and only touches `_status`, `_liveToRunner` and `_lastLive` inside
  `Dispatcher.UIThread.Post`. `t.Result` is read only after `IsCompletedSuccessfully`. `SendLive` cannot throw (`Send` catches everything), so no
  exception is left unobserved. The `_lastLive == snap` check correctly ignores a stale failure that arrives after a newer edit.
- **Encoding edge cases.** Spaces, two spaces in a row and non-ASCII characters in the folder and the JSON round-trip (tested). Base64 output has no spaces
  or newlines, so the line format is safe for any theme size; the runner's `ReadLineAsync` has no length limit, so large themes are fine (the pipe is
  limited to the current user, so that is not an exposure). Malformed base64 is rejected with a clear reason instead of an exception message.
- **Test quality.** The tests describe behaviour, name H2, and fail when the original bug is put back (mutation table above). They use no pipes, so they
  cannot collide with a running runner.
- **Conventions.** New logic is in Core and tested, nullable annotations are right (`[NotNullWhen(true)]`), the record has a private constructor so
  commands can only be made through the factories, and the commit message says what and why.
- **Not verified:** the editor and runner were not started, so the end-to-end "Send to display" on a real display, and the new status message in the
  window, were not seen.

---

## Proof: the bug existed and is fixed

The parsing lives in `Glance.Runner` (a WinForms project the tests cannot reference), and the branch's tests use the new `ControlCommand` type, which does not exist on `main`. So the bug was reproduced on the **unfixed** `main` (`0a9f10b`) with a throwaway test. It copies `main`'s encoding line from `RunnerClient.SendLive` and its parsing line from `RunnerContext.Execute` word for word, and asserts that the runner handles the command:

```csharp
var folder = Convert.ToBase64String(Encoding.UTF8.GetBytes(baseFolder ?? ""));   // baseFolder = null (a New theme)
var line = $"LIVE {folder} {json}";
var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
Assert.True(parts[0] == "LIVE" && parts.Length == 3);                             // case "LIVE" when parts.Length == 3
```

| Run | Result |
|---|---|
| `main` (unfixed), the proof test above | **Failed**: `The runner would reply 'ERR unknown command': the line "LIVE  eyJOYW1lIjoiTmV3IHRoZW1lIn0=" split into 2 parts.` |
| Branch (`e5182a4`): `Live_without_a_folder_round_trips` (2 cases), `Live_from_an_older_editor_is_still_understood`, `A_live_theme_without_a_folder_can_be_applied_like_the_runner_does` | Passed, 4/4 (full suite 295/295) |

**Limit:** the proof copies two lines from `main` instead of running the real runner. A full end-to-end check needs you: with the runner running, click **New**, pick a template, click **Send to display**, and confirm the display shows the new theme.