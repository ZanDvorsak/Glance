# The sensor feed: a walkthrough of the M8 fix

This walkthrough is for a mid-level developer who wants to understand how Glance learned to tell a live sensor feed from a dead one,
and how it stopped one bad reading or one bad device from freezing every sensor. The change covers:

- **M8, stale feed:** a sensor loop that hung still looked live, because the pipe kept re-sending its last snapshot.
- **M8, NaN:** a `NaN` or infinite reading could not be written as JSON, so nothing new was published again.
- **M8, one device:** one device that threw from `Update()` froze all the others.
- **Dispose nits:** LibreHardwareMonitor could be closed while an update was still running on another thread.

It assumes you know C# and .NET, async/await and `System.Text.Json`. For the bigger picture, read `docs/WALKTHROUGH.md`
(section 4.6 covers sensors) and `docs/design/runner-service.md` (the service that now owns the sensors).

**The change:** branch `claude/fix-sensor-feed-07b0e8`, commits `c4d4a01` (test seams, no behaviour change), `845813f` (the fix),
`87ae3ad` (one more test and the write-up). Based on `main` at `8d2d13c`.
**Files:**
- `src/Glance.Core/Sensors/SensorService.cs`: reading LibreHardwareMonitor, one device at a time
- `src/Glance.Core/Sensors/LocalSensorSource.cs`: the service's 1 s update loop
- `src/Glance.Core/Sensors/SensorPipeServer.cs`: the `Glance.Sensors` pipe and its new line format
- `src/Glance.Core/Sensors/PipeSensorClient.cs`: the tray/editor side of that pipe
- `src/Glance.Core/Sensors/SensorHub.cs`: uses the service feed, or local sensors when the feed is gone
- `src/Glance.Service/ServiceRuntime.cs`: wiring
- `src/Glance.Tests/SensorFeedTests.cs` (new, 18 tests)

**Proof (author):** `docs/design/fix-sensor-feed.md`. **Independent review:** `docs/reviews/review-sensor-feed.md`.

**Contents**

1. [The problem](#1-the-problem)
2. [Technologies involved](#2-technologies-involved)
3. [High-level walkthrough](#3-high-level-walkthrough)
4. [Detailed code review](#4-detailed-code-review)
5. [Self-review: five ways to improve the change](#5-self-review-five-ways-to-improve-the-change)

---

## 1. The problem

Since the service redesign, sensors flow like this:

```
Glance.Service (LocalSystem)                                   tray app / editor (normal user)
  SensorService --Update() every 1 s--> LocalSensorSource          SensorHub
        |                                     | Updated                |  prefers the service, else local sensors
        |                                     v                        |
        |                              SensorPipeServer --"Glance.Sensors"--> PipeSensorClient
```

`PipeSensorClient.HasData` decides whether the hub trusts the service. Before this change it meant "a line arrived in the last 5 s".
The server wrote `_latest` to every client once a second *whether or not it had changed*. So if the service's sensor loop hung (a stuck
driver call inside LibreHardwareMonitor), the pipe kept delivering the same old snapshot, `HasData` stayed true, and every widget on the
LCD showed frozen numbers as if they were live. Nobody could tell.

The other two problems made a hang more likely. `JsonSerializer.Serialize` throws on `float.NaN` and the infinities, and some sensors do
report NaN (a fan header with nothing on it, a GPU while it is waking up). The loop caught the exception and moved on, so it never
published anything new, and the feed looked live with old values. In the same way, `SensorService.Update()` walked every device in one
`foreach`, so the first device whose `Update()` threw (an embedded controller that is busy, an SMBus read that times out) ended the walk
for all of them.

---

## 2. Technologies involved

### 2.1 Named pipes and JSON lines

The sensor pipe is a one-way named pipe (`PipeDirection.Out` on the server). Each message is one line of JSON, read with
`StreamReader.ReadLineAsync`. A line-based format is simple, but it has no version field. That matters when a new server talks to an old
client (section 4.4).

### 2.2 NaN and JSON

JSON has no literal for NaN or infinity. By default `System.Text.Json` refuses them and throws
`ArgumentException: .NET number values such as positive and negative infinity cannot be written as valid JSON`.
(`JsonNumberHandling.AllowNamedFloatingPointLiterals` would write `"NaN"` as a string, but then every reader must opt in as well.)
Glance already has a value for "no reading": `float? Value = null`, which widgets draw as "no value". The fix maps NaN to that.

### 2.3 Monotonic clocks: `Environment.TickCount64`

`TickCount64` is the number of milliseconds since boot. It never jumps when the user changes the wall clock, and on Windows it keeps
counting during sleep. The service and its clients run on the same PC, so they share this clock. The fix still sends an *age*
("published 1200 ms ago") rather than a timestamp. An age keeps working if the clients ever read a clock with a different origin, and it
is easy to check in a test.

### 2.4 `Monitor.TryEnter` as a gate, and `TaskCompletionSource` as a signal

`lock` is `Monitor.Enter` + `Monitor.Exit`. `Monitor.TryEnter(obj, timeout)` waits at most `timeout`, which turns "close, but not while
an update runs" into a **bounded** wait. A `TaskCompletionSource` (TCS) is a task you complete by hand. Swapping in a fresh TCS on every
publish and completing the old one wakes everyone who was waiting for "the next snapshot". `RunContinuationsAsynchronously` makes sure
those continuations do not run inline on the publisher's thread.

### 2.5 LibreHardwareMonitor

LHM's `Computer` exposes `Hardware` (a tree with `SubHardware`), each with `Update()` and `Sensors`. `Open()` loads drivers and can take
seconds. `Close()` unloads them, and it must not run in the middle of an `Update()` on another thread. `IHardware.Update()` can throw
for a single device.

---

## 3. High-level walkthrough

1. **Test seams first (`c4d4a01`).** `SensorService` reads hardware through a small `IHardwareNode` interface, so tests can pass
   fake devices that return NaN, throw or block. Without that, the bugs could not be shown failing on the old code.
2. **Clean the values at the source and at the wire.** `SensorInfo.Finite` turns NaN and ±Infinity into `null`. `SensorService` applies
   it to every reading, and `SensorPipeServer.Publish` applies it again and never throws.
3. **Give every line an age.** The line becomes `{"Seq":n,"AgeMs":ms,"Sensors":[...]}`. The client dates the snapshot by when the
   service *published* it, not by when the line arrived. A re-sent old snapshot now looks old.
4. **Publish only real updates.** `LocalSensorSource` raises `Updated` only after an update that succeeded. A hung or failing loop
   publishes nothing, and its snapshot ages.
5. **Send new snapshots at once.** Otherwise the age a client sees could be up to one extra second old (see 4.3).
6. **One try per device.** A failing device keeps its sensors with no value, and the others keep updating. Failures are logged at most
   once a minute per line.
7. **Shutdown order: cancel → wait (bounded) → close.** The loop that updates the sensors is the one that closes them.

---

## 4. Detailed code review

### 4.1 The seam: `IHardwareNode`

```csharp
public interface IHardwareNode
{
    string Name { get; }
    void Update();
    IEnumerable<SensorReading> Readings { get; }
    IEnumerable<IHardwareNode> Children { get; }
}
```

Production wraps LHM's `IHardware` in a private `LhmNode`, and tests use a `FakeHw`. The seam commit changes no behaviour on purpose,
so you can run the new tests against it to see the old bugs (the review did: 13 of 15 failed). The fix commit adds `.ToList()` to
`Readings` and `Children`. The lists are then built inside `Collect`'s `try`, so an exception from LHM's enumeration is caught there and
not later.

### 4.2 Finite values

```csharp
public sealed record SensorInfo(string Id, string Hardware, string Name, string Type, float? Value, string Unit)
{
    public static float? Finite(float? value) => value is { } v && float.IsFinite(v) ? v : null;
}
```

It is applied twice. The first time is in `SensorService.Collect`, so the editor's local sensors are clean too and `ThemeRenderer` never
sees NaN: its graphs skip `null` (`s.Value is { } raw`), but NaN would pass that pattern. The second time is in the publisher:

```csharp
public void Publish(IReadOnlyList<SensorInfo> snapshot)
{
    try
    {
        var clean = snapshot.Select(s => s with { Value = SensorInfo.Finite(s.Value) }).ToList();
        var json = JsonSerializer.Serialize(clean);
        var previous = _latest;
        _latest = new Published(previous.Seq + 1, Environment.TickCount64, json);
        Interlocked.Exchange(ref _changed, NewSignal()).TrySetResult();
    }
    catch (Exception ex) { Log("Sensor snapshot could not be published: " + ex.Message); }
}
```

Note the "never throws" contract. If serializing still fails, the old snapshot stays and **ages**, so the failure becomes visible to
clients as staleness. It is not hidden as frozen-but-live data.

### 4.3 The line format and the age

The server builds each line when it sends it, so the age is always current:

```csharp
internal string Line()
{
    var p = _latest;
    long age = Math.Max(0, Environment.TickCount64 - p.AtMs);
    return string.Create(CultureInfo.InvariantCulture, $"{{\"Seq\":{p.Seq},\"AgeMs\":{age},\"Sensors\":{p.Json}}}");
}
```

The client turns the age back into a time on its own clock:

```csharp
if (Parse(line) is not { } feed) continue;
lock (_lock)
{
    _snapshot = feed.Sensors;
    _sequence = feed.Seq;
    _lastUpdateMs = Environment.TickCount64 - feed.AgeMs;   // when the service read it, not when it was re-sent
}
```

`HasData` is unchanged (`now - _lastUpdateMs < 5 s`). Only the meaning of `_lastUpdateMs` changed, which is why this is a small,
safe diff on the client.

Now the send loop. The author's first version kept "send once a second", and a guard test (`M8_new_snapshots_keep_the_feed_live`)
failed against it. The reason: a client learns a snapshot's age only at the next send, so the age it sees can reach
update interval + 1 s. With a slow LHM update that comes close to the 5 s limit. The final loop waits for *either* a new snapshot or
the 1 s heartbeat:

```csharp
var changed = Volatile.Read(ref _changed).Task;   // before the line: a snapshot published after it is sent at once
await writer.WriteLineAsync(Line().AsMemory(), timeout.Token);
await writer.FlushAsync(timeout.Token);
using (var wait = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
{
    await Task.WhenAny(changed, Task.Delay(1000, wait.Token));
    wait.Cancel();
}
```

Two details are worth copying. First, the signal is read **before** the line is built. If `Publish` runs between `Line()` and the
wait, the old TCS is already completed and the loop goes round again at once, so no update is lost. Second, `wait.Cancel()` stops the
`Task.Delay` timer when the signal wins, so one second-long timer per client per publish does not pile up.

### 4.4 Wire compatibility

```csharp
internal static (long Seq, long AgeMs, IReadOnlyList<SensorInfo> Sensors)? Parse(string line)
{
    if (line.TrimStart().StartsWith('['))
        return JsonSerializer.Deserialize<List<SensorInfo>>(line) is { } legacy ? (0, 0, legacy) : null;
    if (JsonSerializer.Deserialize<FeedLine>(line) is not { Sensors: { } sensors } feed) return null;
    return (feed.Seq, Math.Clamp(feed.AgeMs, 0, TimeSpan.FromDays(1).Ticks / TimeSpan.TicksPerMillisecond), sensors);
}
```

A **new client against an old service** reads the bare array as "just read". That is the old behaviour, so it degrades without
failing. The clamp protects against a negative or absurd age. The other direction is not covered: an **old client against a new
service** tries to read the object as a list, throws, reconnects every second and falls back to local sensors. The review lists this
as L2. It only matters while an old tray app or editor runs during an upgrade.

### 4.5 Publish only real updates

```csharp
bool fresh = false;
try { _service.Update(); fresh = true; }
catch (Exception ex) { Log("Sensors: update failed: " + ex.Message); }
if (_cts.IsCancellationRequested) return;
try { Merge(); } catch (Exception ex) { Log("Sensors: reading drive space failed: " + ex.Message); }
if (fresh)
{
    try { Updated?.Invoke(); } catch (Exception ex) { Log("Sensors: publishing failed: " + ex.Message); }
}
```

Before this change, `Update(); Merge(); Updated?.Invoke();` sat inside one `try { } catch { }`. Every error was swallowed in silence,
and there was no way to tell "published" from "skipped". Now each step has its own try and its own log line, and `fresh` carries the
one fact that matters downstream. In `ServiceRuntime.Start`, the subscription also moved before the first `Publish`. A throw there can
then no longer leave the pipe serving `[]` until the next restart.

### 4.6 One try per device

```csharp
private void Collect(IHardwareNode hw, List<SensorInfo> list)
{
    string name = "?";
    bool failed = false;
    try { name = hw.Name; hw.Update(); }
    catch (Exception ex) { failed = true; Log($"Sensors: reading \"{name}\" failed: {ex.GetType().Name}: {ex.Message}"); }
    try
    {
        foreach (var s in hw.Readings)
            list.Add(new SensorInfo(s.Id, name, s.Name, s.Type.ToString(), failed ? null : SensorInfo.Finite(s.Value), UnitFor(s.Type)));
        foreach (var sub in hw.Children) Collect(sub, list);
    }
    catch (Exception ex) { Log($"Sensors: listing \"{name}\" failed: {ex.GetType().Name}: {ex.Message}"); }
}
```

There are two separate tries: one for "read the device" and one for "list what it has". A device that fails to update keeps its sensor
**ids** in the list with no value. Widgets and presets still find the sensor and show "no value" instead of numbers that look live.
Its children are still visited, each with its own try. `Log` goes through `LogLimiter` (the same text at most once a minute, then a
"(repeated N more times before this)" line), so a device that fails every second does not fill the service log.

The trade-off: one *transient* failure now blanks that device's values for one update (review L1).

### 4.7 Shutdown: cancel, wait, close

`SensorService` has two locks with different jobs: `_lock` guards the snapshot reference, and `_gate` covers "an update or the close".

```csharp
public bool Dispose(TimeSpan wait)
{
    _disposed = true;
    if (!Monitor.TryEnter(_gate, wait)) return false;   // the running update closes when it is over
    try { CloseOnce(); return true; }
    finally { Monitor.Exit(_gate); }
}
```

and inside `Update`:

```csharp
lock (_gate)
{
    try
    {
        if (_disposed) return;
        ...
    }
    finally
    {
        if (_disposed) CloseOnce();   // Dispose gave up waiting for this update: it closes now that it is over
    }
}
```

So the close always happens exactly once, never during an update, and `Dispose` is bounded even when a driver call hangs. The owners
follow the same pattern. `LocalSensorSource`'s loop closes the service in its `finally`, and `Dispose` cancels, waits up to 3 s
(`_loop.Wait(SensorService.DisposeWait)`) and otherwise marks it closed with `Dispose(TimeSpan.Zero)`. `SensorHub` does the same but
waits only 500 ms, because the editor and the tray app dispose it on their UI threads. Its local loop also checks for cancellation
right after the slow `_openLocal()`. Sensors opened while the hub was being disposed are then closed too.

**About blocking:** `Task.WaitAll` on the UI thread is safe here because both loops were started with `Task.Run`. Their awaits have no
UI `SynchronizationContext` to come back to, so they cannot deadlock against the waiting UI thread. A sibling branch
(`fix-tray-deadlock`) makes `ConfigureAwait(false)` mandatory in Core (CA2007 as an error). When the two branches are merged, every
await in these files needs it (review M1).

### 4.8 The tests

`SensorFeedTests` reads no real sensor and uses random pipe names (`glance-test-<guid>`). Three patterns are worth reusing:

- **A blocking fake device** (`Blocking()`) whose `Update()` waits on a `ManualResetEventSlim`, and whose close records
  `hw.Inside`. That turns "closed during an update" into a plain boolean assertion.
- **A fake clock for `LogLimiter`** (`nowMs: () => now`). Seventy simulated seconds run in milliseconds.
- **Guards next to proofs.** `M8_new_snapshots_keep_the_feed_live` passed before the fix and must keep passing. It caught the
  one-extra-second problem in 4.3.

The author also mutation-tested the fix: each part was removed on its own (R1-R10 in the design note), and one test was added when a
mutation survived.

---

## 5. Self-review: five ways to improve the change

### 1. Keep the last good value for a short grace period

A device whose `Update()` throws once should not blank its widgets for a second. `SensorService` could keep a per-device counter and
the previous readings, reuse them for up to N failed updates (say 3), and only then report `null`. The "stale is never shown as live"
promise still holds, because a feed that stops altogether still ages out in the client.

### 2. Version the line and keep old clients working

A `"V":2` field, or a hello line, would let a future format change be detected rather than inferred from `[` versus `{`. For the
upgrade window, the server could notice a client that keeps reconnecting and log it. Or the installer could restart the tray app
together with the service, so an old client never meets the new format.

### 3. Add hysteresis to the hub's fallback

Opening LibreHardwareMonitor in the user process is expensive. Make the hub switch to local sensors only after the feed has been stale
for two consecutive ticks, and switch back as soon as a fresh snapshot arrives. That removes the open/close churn after resume and
with slow service updates, without hiding a real stall for long.

### 4. Budget the service stop explicitly

`ServiceRuntime.Stop` blanks the display, then disposes the sensors, which can take 3 s. Pass the remaining stop budget into
`LocalSensorSource.Dispose(TimeSpan)` (shorter for pre-shutdown), or report a stop-pending checkpoint before it. The SCM's wait hint
then always matches what the service is doing.

### 5. Make the rate limiter key stable, and test timing with a fake clock

`LogLimiter` keys on the whole message, so an `ex.Message` that contains a changing number gets past it. Key on device + exception
type and append the message only to the line that is written. Likewise, the pipe tests depend on real time (a 500 ms "at once"
window). Injecting the tick source into `SensorPipeServer`/`PipeSensorClient`, as `SensorService` already accepts `nowMs`, would make
the age logic testable without sleeps and remove the most likely CI flake.
