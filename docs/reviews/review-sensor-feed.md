# Review: the sensor feed (M8, L10 and the sensor dispose nits)

- **Verdict:** merge after one fix at merge time (M1); the Low findings can follow. The design is sound: an age on every line, NaN mapped at the source and again at the pipe, one try per device, and a loop that owns and closes its own sensors.
- **Build/tests:** Release build 0 errors (`-p:SkipHost=true`), no new warning kinds; `dotnet test` 809/809 three times in a row (about 21 s each).
- **Proof:** checked in a scratch worktree at `c4d4a01` with the head's `SensorFeedTests.cs` (minus 3 tests that call APIs added by the fix: `Sequence`, `Parse`, `Dispose(TimeSpan)`).
  - Before: 13 of 15 failed, with the reasons the note gives (frozen NaN, "EC read failed", stalled feed taken for live, closed during `Update()`, sensors opened during Dispose never closed, hub loop dead after one failure). The 2 that passed are the guards (`M8_new_snapshots_keep_the_feed_live`, `M8_a_sensor_loop_whose_updates_fail_publishes_nothing_new`). Where the note quotes an `ArgumentException`/`InvalidOperationException`, xUnit reports it as `Assert.Null()` failing on the exception that `Record.Exception` caught: same cause.
  - Seam commit: the other 791 tests pass at `c4d4a01`; its diff only reroutes `IHardware` through `LhmNode` and adds internal constructors (the `log`/`nowMs` parameters are unused there). No behaviour change found.
  - After: 18/18 new tests, 809/809 total.

## Findings
- **Medium M1**: merging with `claude/fix-tray-deadlock-a14e66` gives text conflicts in `LocalSensorSource.cs`, `SensorHub.cs` and `SensorPipeServer.cs`. That branch also makes CA2007 an error in Core, so the new awaits here would fail the merged build (`LocalSensorSource.cs:53`, `SensorHub.cs:62,74,91`, `SensorPipeServer.cs:121`, plus the rewritten `115-116`) → resolve the conflicts by adding `.ConfigureAwait(false)` to every await in these files. This is not a deadlock today: every loop starts with `Task.Run`, so it has no synchronization context to capture.
- **Low L1**: a device that fails *once* now shows "no value" for a full update (`SensorService.cs:112`). LibreHardwareMonitor devices such as the EC, SMBus or NVMe fail now and then, so LCD text and graphs flicker or get gaps where before they only froze for a second → keep the last good value for a short grace period (for example up to 3 failed updates), then show no value.
- **Low L2**: an older tray app or editor against the new service never sees service sensors. Its `Deserialize<List<SensorInfo>>` throws on the object line, it reconnects every second and quietly falls back to the basic local sensors (`SensorPipeServer.cs` `Line()`). This only matters during an upgrade while an old process keeps running → say in the installer/upgrade notes that the tray app and editor must restart (or are closed) with the service. The reverse case (a new client and an old service) is handled and tested.
- **Low L3**: `ServiceRuntime.Stop` (`ServiceRuntime.cs:128`) can now spend up to 3 s more (`LocalSensorSource.Dispose` → `_loop.Wait(3 s)`, `LocalSensorSource.cs:85`) after the blank, past the 5000 ms stop wait hint, if an LHM update hangs. The blank still comes first, so the display is safe → use a shorter wait (about 1 s) for pre-shutdown/shutdown, or report a stop-pending checkpoint before the sensor dispose.
- **Low L4**: there is no hysteresis between the service feed and the local fallback (`SensorHub.cs:79`). A service update that takes 4 s or more, or the first second after resume, makes the hub open LibreHardwareMonitor in the user process and then close it again → require the feed to be stale for two hub ticks before opening local sensors (or use a longer stale time just for the switch).
- Nits:
  - `Seq + 1` is not atomic (`SensorPipeServer.cs:56`). It is harmless because staleness uses only the age, but `Start` and the loop can both publish.
  - `PipeSensorClient.Sequence` is used only by tests.
  - `LogLimiter` keys on the full message, which includes `ex.Message`, so a changing message gets past the once-a-minute limit.
  - `SensorHub.Dispose` blocks the UI thread up to 500 ms, and only when no service runs and the local loop is busy.
  - `M8_each_new_snapshot_..._at_once` gives only 500 ms, so it is the test most likely to flake on a loaded CI runner.

## Manual checks for the user
- Service installed from this branch: the editor status shows "Sensors: Glance service (n)" and the values change every second, as before.
- `%ProgramData%\Glance\service.log` shows "Sensors ready (n)" and, for a device LHM cannot read, at most one "Sensors: reading ... failed" line a minute.
- Watch a widget for a flaky device (EC fan, NVMe temperature) on the LCD for a minute: check whether it blinks to "no value" (L1).
- `sc.exe stop GlanceService` (one UAC prompt): within about 5 s the editor shows limited sensors, and the log ends with "Glance service stopped" a few seconds after the blank.
- Sleep and wake the PC with the editor open: the sensors come back from the service within a few seconds, without staying on the limited ones.
- Upgrade from a build without this fix while the tray app runs: confirm it is restarted, otherwise it stays on limited sensors (L2).
