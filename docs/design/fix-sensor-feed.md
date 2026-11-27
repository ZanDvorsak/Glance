# Fix: the sensor feed (M8, L10 and the sensor dispose nits)

Finding M8 and the nit "Dispose races" in `docs/reviews/review-full-codebase.md`, and L10 in `docs/reviews/review-runner-service.md`.
Branch `claude/fix-sensor-feed-07b0e8`, based on `main` at 8d2d13c (the service owns the sensors since then).

## What the redesign made obsolete

M8 was written against the elevated tray app that read the sensors and hosted the sensor pipe. On 8d2d13c the Glance service
(`ServiceRuntime`) reads them with `LocalSensorSource` and serves them with `SensorPipeServer`; the tray app and the editor read
them through `SensorHub` -> `PipeSensorClient`.

| Review item | Status on 8d2d13c | Evidence |
|---|---|---|
| M8 "the *first* `Publish` at `RunnerContext.cs:53` is not inside a try, so a NaN at startup would crash the runner" | **Obsolete as written.** The tray app no longer publishes sensors: `RunnerContext` only creates a `SensorHub` (line 66), and no file in `src/Glance.Runner` or `src/Glance.App` mentions `SensorPipeServer` or `Publish`. | `grep -rn "SensorPipeServer\|\.Publish(" src/Glance.Runner src/Glance.App` finds only build output. |
| ...the same first `Publish`, moved to the service | **Changed, not gone.** `ServiceRuntime.Start` (8d2d13c lines 49-56) calls it inside a try, so it no longer crashes, but a throw there skipped the next line, `_sensors.Updated += ...`: the pipe then served `[]` until the service restarted, and the log said "Sensors could not be read" although they could. Fixed here (Publish never throws, and the subscription now comes first). | `M8_publishing_a_NaN_never_throws_...` failed before with `System.ArgumentException: .NET number values such as positive and negative infinity cannot be written as valid JSON`. `ServiceRuntime` itself is not testable (needs an installed service). |
| L10 "the sensor pipe exists only after LibreHardwareMonitor has opened, and its errors are silent" | **Already fixed** by the runner-service branch. `ServiceRuntime.Start` creates the pipe (serving `[]`) right after the display pipe and before `new LocalSensorSource()`; `SensorPipeServer` takes the service's log and logs through `LogLimiter`. | 8d2d13c `ServiceRuntime.cs:40-44`; test `DisplayServiceReviewTests.M1_the_sensor_server_reports_a_squatted_name_and_serves_once_it_is_gone` passes before and after. |
| M13's sensor-pipe parts (machine-wide name, no current-user rule) | Already fixed by the runner-service branch (`ServicePipeFactory`, `ServicePipeSecurity`, `PipeServerCheck`). Not touched here. | `DisplayServiceTests.The_sensor_client_ignores_a_feed_that_is_not_served_by_the_service` passes before and after. |

Everything else in M8 and the dispose nit still existed, now in the service's path; each is proved below.

## How it was proved

All tests are in `src/Glance.Tests/SensorFeedTests.cs`. No real sensor is read: devices are `FakeHw` objects that return NaN,
throw, or block inside `Update()`, and every pipe has a random `glance-test-<guid>` name.

To compile the tests against the old code, commit c4d4a01 first added test seams with no change in behaviour:
`SensorService` reads hardware through a small `IHardwareNode` interface (LibreHardwareMonitor's `IHardware` in production,
wrapped by `LhmNode`), and `SensorService`, `LocalSensorSource` and `SensorHub` got internal constructors that take fake
hardware, a fake close, a fake drive list and a shorter first wait.

**Before** (c4d4a01, old behaviour plus seams): 13 of the 14 first tests failed; the 14th is a guard. **After**: all 18 pass
(4 tests were added with the fix: the line format, the immediate send, "a failing loop publishes nothing", and the service-level close guard). Full suite:
809 of 809 (791 before plus 18 new), about 21 s.

## Before and after

### M8: NaN and infinities

| Test | Before | After |
|---|---|---|
| `M8_a_NaN_or_infinite_reading_becomes_no_value` (`SensorService`) | fail: `Assert.Null() Failure: Value of type 'Nullable<float>' has a value. Actual: NaN` | pass |
| `M8_publishing_a_NaN_never_throws_and_the_client_gets_no_value_for_it` | fail: `System.ArgumentException: .NET number values such as positive and negative infinity cannot be written as valid JSON` | pass: NaN and +Inf arrive as null, 7 arrives as 7 |
| `M8_one_sensor_that_stays_NaN_does_not_freeze_the_others` | fail: `every sensor froze because one was NaN` | pass |

Fix: `SensorInfo.Finite` maps NaN and +/-Infinity to null. `SensorService` applies it to every reading (so the editor's local
sensors are clean too), and `SensorPipeServer.Publish` applies it again before serializing. `Publish` never throws: if it still
cannot serialize, it logs (rate-limited) and keeps the old snapshot, which then ages (next section).

### M8: a stalled sensor loop looked live

The server re-sent the last snapshot every second and the client dated each line by its arrival, so `HasData` stayed true
while the service's sensor loop was stuck.

| Test | Before | After |
|---|---|---|
| `M8_a_feed_that_stops_changing_is_stale_while_the_pipe_stays_connected` (stale after 1.5 s, waits 3.5 s, snapshot still there) | fail: `the old snapshot kept looking live although the sensors stopped` | pass |
| `M8_a_client_that_connects_after_the_feed_stalled_never_sees_it_as_live` | fail: `a new client took an old snapshot for live values` | pass |
| `M8_the_hub_falls_back_to_its_own_sensors_when_the_service_feed_stalls` (end to end: server -> client -> `SensorHub`) | fail: `the hub kept showing the stalled service feed as live` | pass: the hub switches to its local sensors, `FromService` false |
| `M8_new_snapshots_keep_the_feed_live` (guard: a feed that keeps changing is never stale) | pass | pass (it caught a margin problem during the fix, see below) |
| `M8_each_new_snapshot_has_the_next_number_and_reaches_the_client_at_once` (new) | | pass |
| `M8_the_client_reads_the_numbered_line_and_the_older_bare_list` (new) | | pass |
| `M8_a_sensor_loop_whose_updates_fail_publishes_nothing_new` (new) | | pass |

Fix, the line format: each line is now `{"Seq":n,"AgeMs":ms,"Sensors":[...]}`. `Seq` grows by one with every `Publish`;
`AgeMs` is how long ago, on the service's clock, that snapshot was published. The client sets "last update" to
`now - AgeMs`, so `HasData` turns false once the *snapshot* is older than the stale time (5 s by default, as before), whether
or not the pipe is still connected and re-sending. The client still reads the old bare-array line (as "just read") so a newer
editor against an older service degrades to the old behaviour instead of failing. `PipeSensorClient.Sequence` exposes `Seq`.

`LocalSensorSource` raises `Updated` (which publishes) only after an update that read the hardware list, so a loop whose
updates throw or hang publishes nothing new and its clients see the snapshot age.

The guard failed in the first version of the fix: the server sent the line once a second, so a client only learned a
snapshot's age at the next send, and the age it saw could reach update interval + 1 s (about 2.5 s on a PC with a slow
LibreHardwareMonitor update, against the 5 s limit). Now `Publish` wakes every client's send loop, so a new snapshot goes out
at once; the 1 s re-send remains as a heartbeat (it also finds clients that stopped reading, as before).

### M8: one failing device froze every sensor

| Test | Before | After |
|---|---|---|
| `M8_one_failing_device_does_not_freeze_the_others` (a failing device and a failing sub-device) | fail: `System.InvalidOperationException: EC read failed` from `Update()` | pass: the others update, the failing ones stay listed with no value |
| `M8_a_device_that_fails_at_start_does_not_stop_the_service_sensors` | fail: `System.InvalidOperationException: EC read failed` from the `LocalSensorSource` constructor (in the service: "Sensors could not be read", no sensors at all) | pass |
| `M8_a_failing_device_is_logged_once_a_minute_not_every_second` (fake clock, 70 updates) | fail: `System.InvalidOperationException: EC read failed` | pass: one line, then one "(repeated 59 more times before this)" line |

Fix: `SensorService.Collect` reads each device and sub-device in its own try. A device whose `Update()` throws keeps its
sensors in the list: with their last good value for up to 3 failed updates in a row (`SensorService.FailedUpdatesKept`, review L1:
EC, SMBus and NVMe reads fail now and then, and the display should not blink), then with no value (widgets show "no value", not
old numbers that look live); if even listing its sensors
throws, it is left out. Failures go to the service log through `LogLimiter` (the same line at most once a minute, with the
count; the limit keys on the device and error type, not the error text, which can change every time). Only a failure to list the hardware at all still throws, and then nothing new is published (previous section).

### Dispose races (nit) and the hub's local loop

| Test | Before | After |
|---|---|---|
| `Nit_the_sensor_service_itself_never_closes_during_an_update` (new, after the mutation check) | | pass |
| `Nit_the_local_source_never_closes_the_sensors_while_its_loop_is_updating` | fail: `LibreHardwareMonitor was closed while the loop was inside Update()` | pass |
| `Nit_the_hub_never_closes_its_local_sensors_while_its_loop_is_updating` | fail: `LibreHardwareMonitor was closed while the hub's loop was inside Update()` | pass |
| `Nit_local_sensors_the_hub_opens_while_it_is_disposed_are_closed` | fail: `sensors opened during Dispose were never closed (the driver stays open)` | pass |
| `The_hub_tries_its_local_sensors_again_after_they_fail_to_open` (found while re-assessing: one exception ended the hub's local loop for good) | fail: `one failure to open the local sensors stopped them for good` | pass |

Fix, the shutdown order is now cancel, wait for the loop, then close:
- `SensorService` never closes LibreHardwareMonitor during an update: `Update` and the close share a gate. `Dispose` waits up to
  3 s for an update in progress; if it is still running then, the update closes it as it ends. After `Dispose`, `Update` does
  nothing. `Dispose(TimeSpan)` lets callers choose the wait.
- `LocalSensorSource` (service): its loop owns the `SensorService` and closes it in a `finally`. `Dispose` cancels, waits up to
  1 s for the loop (`LocalSensorSource.StopWait`, review L3: inside the service's stop budget), and only if the loop is still busy
  marks the service closed without waiting again; the hung update then closes it when it ends. `ServiceRuntime.Stop`
  still blanks the display first.
- `SensorHub` (tray app, editor): the same pattern. Its local loop has a try per pass (a failed open or update is tried again a
  second later), checks for cancellation right after opening, and closes its sensors in a `finally`, so sensors opened while the
  hub was disposed are closed. `Dispose` only cancels and returns at once (review nit), because the editor and the tray app call it on their
  UI threads (M7); the loop closes the sensors off the UI thread. `SensorHub.Stopped` ends when it has. The hub changes between the
  service's feed and its own sensors only after 2 checks in a row disagree (`SensorHub.SwitchAfterChecks`, review L4), so a feed
  that is stale for a moment (after a resume, one slow update) never opens LibreHardwareMonitor in the user's process.

### Service wiring (`ServiceRuntime.Start`, not testable here)

`LocalSensorSource` gets the service log; the `Updated` subscription comes before the first `Publish`, so nothing can leave
the pipe serving `[]` for good. The pipe's creation order and security are unchanged (`ServicePipeSecurity.Build(system)`,
first-instance flag, owner checks, anonymous impersonation on the client).

## Mutation check

Each mutation was applied alone to the fixed code, `SensorFeedTests` run, and the file restored.

| Mutation | Caught by |
|---|---|
| R1 `SensorService` keeps NaN (`Finite` removed) | `M8_a_NaN_or_infinite_reading_becomes_no_value` |
| R2 `Publish` serializes NaN as is | `M8_publishing_a_NaN_never_throws_...`, `M8_one_sensor_that_stays_NaN_...` |
| R3 client dates a line by its arrival (ignores `AgeMs`) | the three stall tests (`..._stale_while_the_pipe_stays_connected`, `..._connects_after_the_feed_stalled_...`, `M8_the_hub_falls_back_...`) |
| R4 server sends only once a second (no immediate send) | `M8_each_new_snapshot_..._at_once`, `M8_new_snapshots_keep_the_feed_live` |
| R5 no per-device try (rethrow) | `M8_one_failing_device_...`, `M8_a_device_that_fails_at_start_...`, `M8_a_failing_device_is_logged_...` |
| R6 `SensorService.Dispose` closes without waiting for the update | first run: **survived** (the owning loops already wait). Added `Nit_the_sensor_service_itself_never_closes_during_an_update`; it now fails |
| R7 the hub's local loop ends at the first exception | `The_hub_tries_its_local_sensors_again_...` |
| R8 the hub's loop does not close its sensors on the way out | `Nit_local_sensors_the_hub_opens_while_it_is_disposed_are_closed`, `Nit_the_hub_never_closes_..._while_its_loop_is_updating` |
| R9 no `LogLimiter` in `SensorService` | `M8_a_failing_device_is_logged_once_a_minute_...` |
| R10 `Updated` raised after a failed update | `M8_a_sensor_loop_whose_updates_fail_publishes_nothing_new` |

## Not changed

- The sensor pipe client still has no line-length limit (nit "Unbounded reads"). The server must be SYSTEM or Administrators
  (`PipeServerCheck`), so only an administrator could send an endless line. Left as a follow-up.
- AGENTS.md's sensor paragraph ("the feed counts as gone after 5 s without data") is still true; it now means 5 s without a
  *new* snapshot.

## Manual checks for the user

Need an installed service (built from this branch) and the real hardware; not verified by an agent:
1. Sensors arrive as before: the editor's status shows "Sensors: Glance service (n)", values change every second.
2. `%ProgramData%\Glance\service.log` shows "Sensors ready (n)" and, on a PC with a device LibreHardwareMonitor cannot read,
   one "Sensors: reading ... failed" line per minute at most, not one per second.
3. Stop the service (`sc.exe stop GlanceService`, one UAC prompt): within about 5 s the editor shows
   "Sensors: limited, service not running", and the service stops in a few seconds (the log ends with "Glance service stopped").
4. Close the editor and the tray app with the service stopped (local sensors in use): they close at once, as before.

## Older tray app or editor against this service (review L2)

An older tray app or editor reads each line as a bare list, so the new `{"Seq","AgeMs","Sensors"}` line fails to parse: it
reconnects every second and shows only its basic local sensors. Its log says nothing (that code is already shipped). The
installer closes the editor and the tray app before it upgrades (`taskkill` in `PrepareToInstall`) and starts the tray app again
afterwards, so an installed upgrade never mixes them. Only an older portable copy started by hand meets the new service: restart
it from the new version. The reverse (a new client and an older service) works: the client still reads the bare list.
