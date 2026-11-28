# Review: tests fail when they run elevated (pipe owner = Administrators on GitHub CI)

- **Verdict:** merge. The fix is correct and production trust is unchanged, but expect CI to stay red on one timing test (M1)
- **Build/tests:** build 0 errors (194 warnings, all old); 993/993 passed in 3 runs in a row (~23 s each), normal user, this PC
- **Proof:**
  - Before: `ElevatedPipeOwnerTests` on `main` e64ba4c (scratch worktree, with `TestPipes` written the way `main` behaves: trust the user SID, squatter has no owner) gave **3 failed, 2 passed**: `Expected [S-1-5-32-544] Actual [S-1-5-21-…-1001]`, and the squatter's owner `Expected S-1-5-21-…-1001 Actual null`. Same as the author's note
  - After: 5/5 on the branch, and the full suite passes (above)
  - CI logs: run 46176951882 (tag) 45 failed, run 46176949017 (main) 46 failed. In each run, 44 failures are on test call sites this change edits, and all of them fail with "served by BUILTIN\Administrators" or as a result of it
  - **Not fixed by this change:** `UiThreadDeadlockTests.The_editors_tray_command_gets_its_reply_on_its_UI_thread` (both runs, reply null after ~820 ms) and `LiveLeaseTests.M5_a_closed_link_answers_at_once_with_no_reply` (main run, `LiveLeaseTests.cs:351`)
- **Kept:** `PipeServerCheck.ServiceOwners` is still only SYSTEM and Administrators (`PipeServerCheck.cs:15`); the `ServicePipeFactory` owner rule is unchanged (user or Administrators, `ServicePipeFactory.cs:35`); the tray client still uses `CurrentUserOnly` + Anonymous; `DisplayServer`'s default security is unchanged. No production code calls `TokenOwner`
- **Seam:** `WindowsIdentity.Owner` reads the token's `TokenOwner`, the same value .NET's `CurrentUserOnly` compares on both ends of a pipe. `TestPipes.Own` is exactly that one SID, so the tests do not trust "any owner". Squatters name the user SID as owner explicitly, so the tests that check "a squatter is refused" still go through the product's rule
- **UAC-off assessment:** correct. `TrayStart.Decide` returns `Run` for Default+elevated (`TrayStart.cs:22`). Tray and editor then both have the Administrators owner, so they accept each other. On CI (elevated), `ControlPipeClientTests.The_runners_reply_reaches_the_caller` and `LiveLeaseTests.M13_the_probe_asks_the_tray_over_its_pipe` passed. Service pipes are not widened: an administrator could already serve them before this change

## Findings
- **Medium M1** — The goal "green CI" will likely not be met. `The_editors_tray_command_gets_its_reply_on_its_UI_thread` failed in 2 of 2 CI runs: the reply is null after about the 800 ms reply timeout. The failure is not owner-related, because other `CurrentUserOnly` round trips passed elevated on CI (`UiThreadDeadlockTests.cs:248`, `ControlPipeClient.cs:16`) → fix it separately with a sync point instead of a race against the timeout, or a longer reply timeout in the test. `LiveLeaseTests.M5` needs the same. Do not call CI fixed until a run is green
- **Low L1** — The change is described as "tests only", but it adds a public `TokenOwner` class to the shipped `Glance.Core` assembly, and only the tests use it (`Glance.Core/Security/TokenOwner.cs:12`) → move it to `Glance.Tests`, or change the wording to "no production behaviour changed"
- **Low L2** — Proof tests 1–3 only check the helpers' own formula (`TokenOwner.Of` is `??`), so Windows behaviour plays no part in them. Only tests 4–5 use real pipes, and only CI runs the elevated branch (`ElevatedPipeOwnerTests.cs:22-46`) → keep them, but make CI print that it ran elevated (for example `whoami /groups`, or a test that writes `TokenOwner.Current()` to the output)
- **Low L3** — Nothing stops a new test from trusting the user SID again (`new[] { Me }`) → add a source-scan guard in the style of `ConfigureAwaitGuardTests` that flags a user SID passed as `trustedOwners` in the tests
- Nits: `TestPipes.UserOwned` changes the `PipeSecurity` passed to it (all current callers pass a new one). The note's "17 served by" count does not match the logs, which show 44 lines per run (output is duplicated)

## What only CI can confirm
- The 44 owner failures are gone when the tests really run elevated (`TokenOwner.Current()` = Administrators)
- Windows accepts the user SID as an explicit owner from an elevated token (`TestPipes.UserOwned` squatters)
- Whether M1's two timing tests fail again on the 4-vCPU runner
