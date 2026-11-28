# Fix: tests fail when they run elevated (GitHub CI, 0.2.0)

## Cause (confirmed from the CI logs)

- CI runs `windows-latest` as an elevated administrator. Windows makes the token's default owner (`TokenOwner`) the owner of
  every pipe a process creates without naming one. In an elevated admin token that is `BUILTIN\Administrators`, not the user.
- The tests trusted only the user SID for their own test servers, so the client check refused them:
  "The pipe is served by BUILTIN\Administrators, not by the Glance service, so it was not used."
- Test "squatters" (test servers that play a normal user's program) also got the Administrators owner, which the product
  rightly trusts as the service, so "a squatter is refused" tests failed the other way round.
- Run 46176949017 (main): 46 failed. Run 46176951882 (tag v0.2.0): 45 failed. 44 in each are this owner problem, directly
  (17 "served by BUILTIN\Administrators") or as a result (feeds that never arrive, wrong exception type, USB fallback).
- Not owner-related: `UiThreadDeadlockTests.The_editors_tray_command_gets_its_reply_on_its_UI_thread` (both runs) and
  `LiveLeaseTests.M5_a_closed_link_answers_at_once_with_no_reply` (main run only). Both are timing-sensitive tests; see "Still open".

## Proof

- A normal token cannot make Administrators the owner (no group in it may be owner), so the elevated case cannot be
  reproduced for real here. The seam is `TokenOwner` (Core): `Of(user, tokenOwner)` (pure) and `Current()` (reads `WindowsIdentity.Owner`,
  which is `TokenOwner`, the same value .NET's `PipeOptions.CurrentUserOnly` compares on both ends).
- New tests, `ElevatedPipeOwnerTests`:
  - fake elevated token (`TokenOwner` = Administrators): the tests must trust that owner for their own pipes;
  - normal token: the tests trust only the user; elevated: exactly Administrators, nothing more;
  - real pipe: a pipe this process creates is owned by `TokenOwner.Current()` (on CI this is the elevated case);
  - real pipe: a test squatter names the user as owner explicitly, and the product's SYSTEM/Administrators rule refuses it.
- Before (old behaviour behind the seam: trust the user, squatter without owner): 3 failed, 990 passed.
  - "a test client refuses the test's own server when the tests run elevated"
  - `Expected: [S-1-5-32-544]  Actual: [S-1-5-21-...-1001]`
  - squatter owner `Expected: S-1-5-21-...-1001  Actual: null`
- After: 993 of 993 passed, 3 runs in a row (normal user, this PC).

## Fix (tests only)

- `TestPipes.Own` = `TokenOwner.Current()`: what test clients trust for servers the test runs itself (was the user SID, 34 places).
- `TestPipes.UserOwned(security)` sets the user SID as owner, for the 3 tests whose server plays a squatter:
  `A_squatted_display_pipe_is_not_used`, `The_connector_opens_usb_directly_without_a_service_or_with_a_squatter`,
  `The_sensor_client_ignores_a_feed_that_is_not_served_by_the_service`.
- No production check changed. `PipeServerCheck.ServiceOwners` stays SYSTEM and Administrators.

## Elevated tray or editor (UAC off, "Run as administrator")

- UAC off: `TrayStart.Decide(Default, elevated: true)` returns `Run`; the tray runs elevated (there is no normal-rights token to
  fall back to). The editor is elevated too. Both have `TokenOwner` = Administrators:
  - the tray's `ServicePipeFactory` accepts its own Administrators-owned instance (user or Administrators, unchanged);
  - the editor's `CurrentUserOnly` compares the pipe owner with its own `TokenOwner` (Administrators): accepted;
  - the pipe DACL allows the user SID, which both tokens carry. So tray and editor talk. CI shows it: the tray-pipe tests
    (`ControlPipeClientTests.The_runners_reply_reaches_the_caller`, `LiveLeaseTests.M13_the_probe_asks_the_tray_over_its_pipe`
    and the other `TrayControlServer` tests) passed elevated.
- UAC on, editor "Run as administrator", normal tray: owners differ (Administrators vs the user), so the editor refuses the tray's
  pipe and reports it as not answering. Kept on purpose: an elevated process should not trust a pipe any normal-rights process of the
  user could serve. The editor is not meant to run elevated (the installer starts it with `runasoriginaluser`).
- UAC on, elevated tray (old 0.1 task): the tray relaunches with normal rights or refuses (`TrayStart`), as before.
- Service display and sensor pipes: unchanged. With UAC off any administrator's program could serve an Administrators-owned pipe,
  but such a program can already do anything on the PC; standard users still cannot.

## Covered here vs. only on CI

- Covered here: the trust choice for an elevated token (fake), that `TokenOwner` is the real owner of new pipes, and that
  squatters carry an explicit user owner.
- Only CI can confirm: the 44 owner failures are gone when the tests really run elevated, and Windows accepts the user SID as an
  explicit owner from an elevated token (it should: the token's user may always be named owner).

## Still open (not fixed here)

- Timing-sensitive tests fail on 4 cores, also without elevation and with this fix. Reproduce with `$env:DOTNET_PROCESSOR_COUNT = "4"`
  (`windows-latest` has 4 vCPUs). 7 full runs failed 1 to 3 tests each, a different mix each time, all passing alone:
  `SensorPipeTests.A_client_receives_what_the_server_publishes_and_drops_it_when_the_server_stops`,
  `SensorFeedTests.M8_each_new_snapshot_has_the_next_number_and_reaches_the_client_at_once`, `SensorFeedTests.M8_new_snapshots_keep_the_feed_live`,
  `TrayDisplayOpenerTests.A_failed_open_is_retried_within_a_second_not_on_the_next_2_s_tick`, `LiveLeaseTests.M5_a_closed_link_answers_at_once_with_no_reply`,
  `UiThreadDeadlockTests.The_editors_tray_command_gets_its_reply_on_its_UI_thread`, `ControlPipeClientTests.The_runners_reply_reaches_the_caller`.
  Not thread-pool starvation at the moment of failure (14 threads, 0 queued). CI may still go red on one or two of these.
