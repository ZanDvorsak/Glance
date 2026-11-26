# Moving the elevated work into a Windows service: a walkthrough

This walkthrough is for a mid-level developer who wants to understand one change in Glance. Before it, an **elevated tray app**
drove the USB display and read the sensors. After it, a **Windows service** running as LocalSystem does that work, and the tray app
runs with normal rights. The walkthrough covers the Windows mechanisms involved, how the change is put together, and where it could
be better.

It assumes you know C# and .NET async code, and have heard of UAC. You do not need to know Windows services, named pipe security or
Inno Setup: they are explained where they first appear. For the rest of Glance, read `docs/WALKTHROUGH.md` first. Section 4.3 (the
display loop) and section 4.7 (the runner) are the background for this change, and so is `docs/walkthroughs/shutdown-blank.md`
(power events).

The change is 6 commits on `claude/feature-runner-service-8fa395` (`2f8164e`..`5261090`, base `main` at `9bf03b0`). The design note is
`docs/design/runner-service.md`, and a security review is in `docs/reviews/review-runner-service.md`.

**Contents**

1. [The problem](#1-the-problem)
2. [Technologies at a glance](#2-technologies-at-a-glance)
3. [High-level walkthrough](#3-high-level-walkthrough)
4. [Detailed code review](#4-detailed-code-review)
   - 4.1 [What crosses the boundary: `DisplayWire`](#41-what-crosses-the-boundary-displaywire)
   - 4.2 [Who may connect: `ServicePipeSecurity`](#42-who-may-connect-servicepipesecurity)
   - 4.3 [Who may draw: `DisplayAccess` and `NativeSessions`](#43-who-may-draw-displayaccess-and-nativesessions)
   - 4.4 [Is this really the service? `PipeServerCheck` and the anonymous level](#44-is-this-really-the-service-pipeservercheck-and-the-anonymous-level)
   - 4.5 [The service side: `DisplayServer`](#45-the-service-side-displayserver)
   - 4.6 [The user side: `RemoteFrameDisplay` and `DisplayConnector`](#46-the-user-side-remoteframedisplay-and-displayconnector)
   - 4.7 [Talking to the Service Control Manager: `NativeService` and `ServiceControlMap`](#47-talking-to-the-service-control-manager-nativeservice-and-servicecontrolmap)
   - 4.8 [Who may start and stop the service: the service DACL](#48-who-may-start-and-stop-the-service-the-service-dacl)
   - 4.9 [Where SYSTEM writes: `ServiceLog`](#49-where-system-writes-servicelog)
   - 4.10 [The installer](#410-the-installer)
   - 4.11 [The tray app and the editor](#411-the-tray-app-and-the-editor)
   - 4.12 [Tests](#412-tests)
5. [Self-review: five ways to improve the change](#5-self-review-five-ways-to-improve-the-change)

---

## 1. The problem

CPU temperatures, fans and voltages come from LibreHardwareMonitor, which needs administrator rights and the PawnIO driver. So in Glance
0.1 the tray app (`Glance.Runner.exe`) had a `requireAdministrator` manifest. To start it at sign-in without a UAC prompt every time,
the editor registered a **scheduled task** set to "run with highest privileges".

A review (H6/H1, `docs/reviews/review-autostart-task.md`) proved that this is a **silent UAC bypass**:

- A task with "highest privileges" and the interactive token starts the program **elevated, with the user's own environment variables**.
- Any normal program can write environment variables to `HKCU\Environment`, with no prompt.
- .NET reads some variables before your `Main` runs. `CORECLR_ENABLE_PROFILING` + `CORECLR_PROFILER_PATH` load a "profiler" DLL into
  the process, `DOTNET_DiagnosticPorts` opens a diagnostics channel, and more `DOTNET_`/`COMPlus_` knobs exist.
- So malware running as the user, *not* elevated, writes three registry values and waits for the next sign-in. Its DLL then runs
  inside an administrator process, and nobody sees a prompt.

The runner cannot defend itself: the code loads before `Main`. The only real fix is that **no Glance program that has more rights than
the user is ever started with the user's environment**. The design note adds a second goal: **no privileged process parses files the
user controls** (themes, images, fonts, videos), because each parser (Skia, FFmpeg) is attack surface.

---

## 2. Technologies at a glance

| Area | Technology | What you need to know |
|---|---|---|
| Background programs | **Windows services**, the **Service Control Manager (SCM)** | A service is an exe the SCM starts (at boot for `start= auto`), under an account chosen at install time. It talks to the SCM through a *dispatcher* (`StartServiceCtrlDispatcher`), a `ServiceMain` callback, a *control handler* and `SetServiceStatus`. |
| The most powerful account | **LocalSystem** (`NT AUTHORITY\SYSTEM`, `S-1-5-18`) | More rights than an administrator. Its environment comes from the SCM (the system environment), **never from a signed-in user**. That property is what fixes H1. |
| Separation | **Session 0 isolation** | Services run in session 0, with no desktop. Each signed-in user gets their own session (1, 2, ...). The **console session** is the one attached to the physical screen and keyboard (`WTSGetActiveConsoleSessionId`). RDP sessions are not the console. |
| Talking across sessions | **Named pipes** (`\\.\pipe\<name>`) | The pipe namespace is **global**, not per session or per user, so a name can be "squatted" by whoever creates it first. A pipe has a security descriptor (owner + DACL), like a file. |
| Pipe rights | `FILE_CREATE_PIPE_INSTANCE` (`PipeAccessRights.CreateNewInstance`), `READ_CONTROL`, `WRITE_DAC` | Whoever may create an instance can serve the pipe name too. A client needs `READ_CONTROL` to read the server's owner. |
| Who is in the token | Well-known SIDs: **INTERACTIVE** (`S-1-5-4`), **NETWORK** (`S-1-5-2`), Administrators (`S-1-5-32-544`) | People signed in at this PC (console or RDP) have INTERACTIVE. Remote SMB logons have NETWORK. |
| Server acting as client | **Impersonation levels** (`SecurityAnonymous` ... `SecurityImpersonation`) | A pipe client chooses how far the server may act as it. .NET clients default to *Impersonation*. *Anonymous* means the server learns nothing and can act as nobody. |
| Service rights | **Service DACL** (`sc sdset`, SDDL) | Who may query, start, stop, reconfigure or delete a service. Written as SDDL, for example `(A;;CCLCSWRPLOCRRC;;;IU)`: Allow these rights (`RP` = start) to Interactive Users. |
| Shutdown and power | `SERVICE_CONTROL_PRESHUTDOWN`, `SERVICE_CONTROL_POWEREVENT`, `SERVICE_CONTROL_SESSIONCHANGE` | Pre-shutdown arrives *before* the normal shutdown notice and has its own timeout (`PreshutdownTimeout`). .NET's `ServiceBase` cannot accept it, so this change calls the Win32 service API directly. |
| Installer | **Inno Setup 6**, Pascal Script `[Code]`, `sc.exe` | The setup runs elevated. `[Run]` entries with `runasoriginaluser` start a program with the *un*elevated token. `sc.exe create/config/failure/sdset/start` configure the service. |
| Trust checks | `FolderTrust` (Core) | Glance's single engine for "only administrators can change this file or folder" (owner, ACL, ancestors, no links, fixed drive). |
| Tests | **xUnit**, real pipes with random names, `FakeDisplay` | The display server and client run over real named pipes in the test process, with fake session lookups and a fake clock. |

---

## 3. High-level walkthrough

The old design had one privileged process that did everything. The new one splits by **who must have the rights** and **who owns the data**:

```
            Session 0  (SYSTEM; environment from the Service Control Manager)
 +-----------------------------------------------------------------------------------+
 |  Glance.Service.exe                                                               |
 |    NativeService  <-- SCM: STOP, PRESHUTDOWN, POWEREVENT, SESSIONCHANGE          |
 |       | ServiceControlMap (pure) -> PowerEvent / SessionLeft                      |
 |       v                                                                           |
 |    DisplayServer --- Ax206Display (USB, libusb0)        LocalSensorSource (LHM)   |
 |       ^  pipe "Glance.Service.Display"                         |                  |
 |       |  binary: HELLO, FRAME(307200 B), BRIGHTNESS, BLANK,     v                 |
 |       |  STATUS.  DACL: deny NETWORK; SYSTEM full;      SensorPipeServer          |
 |       |  Admins + INTERACTIVE read/write only           pipe "Glance.Sensors"     |
 +-------|-------------------------------------------------------|-------------------+
         | frames (already rendered)                             | JSON line / s
 +-------|-------------------------------------------------------|-------------------+
 |       |   Session N  (the signed-in user, normal rights)      v                   |
 |  RemoteFrameDisplay <- DisplayPusher <- ThemeRenderer     SensorHub               |
 |   (owner check: SYSTEM/Admins; Anonymous level)   ^  reads %AppData%\Glance:      |
 |                                                   |  theme, images, fonts,        |
 |  Glance.Runner (tray, asInvoker) -----------------+  FFmpeg runs here, as user    |
 |      ^ pipe "Glance.Runner.Control.<N>" (this user only): LIVE, PAGE, PAUSE, ...  |
 |  Glance.App (editor, asInvoker)                                                   |
 +-----------------------------------------------------------------------------------+
```

There are four user stories.

**Normal use.**
1. The service starts at boot and creates both pipes.
2. You sign in. The tray app starts from the common Startup folder, as you, not elevated.
3. The tray reads your theme from `%AppData%\Glance`, renders frames with Skia and sends each finished 480×320 RGB565 frame to the service.
4. The service checks that your session is the console session and pushes the frame over USB.
5. Sensors flow the other way, as JSON, once per second.

**The editor.**
- When the tray runs, the editor sends it the live (unsaved) theme over the tray's own control pipe, as before.
- When no tray runs, the editor renders and streams frames to the service itself.
- Either way, nothing of the user's reaches SYSTEM except finished pixels.

**Shutdown, sleep, sign-out.** The service gets pre-shutdown, power events and session changes from the SCM and blanks the display.
The tray app and the editor keep their own watcher (`SessionPowerWatcher`) as a second line: they stop their pusher with a blank,
which reaches the service as `BLANK`.

**No service (the portable zip).** `DisplayConnector` finds no service pipe and opens USB directly, as the user. libusb-win32 does not
need admin. Sensors fall back to the basic ones a normal user can read. There is no elevated mode any more.

**The key design choice: the user side renders.** The first idea was a service that also runs the display loop. But then the service
needs the theme and its assets, and every way of getting them there makes SYSTEM parse user files: reading the profile (junctions,
`..`, `C:\Windows\...` asset paths), receiving a `.glance` package (Skia and FFmpeg still run as SYSTEM), or a shared `%ProgramData%`
folder (either users cannot save, or it is a shared writable folder). Streaming frames removes the whole class. The only data SYSTEM
accepts is a fixed-size byte array, one brightness byte and five opcodes. The cost is small: about 2.5 frames/s × 300 KiB = 750 KiB/s
over a local pipe.

---

## 4. Detailed code review

### 4.1 What crosses the boundary: `DisplayWire`

`src/Glance.Core/Service/DisplayWire.cs` is the entire language SYSTEM understands. A request is 1 byte op + 4 bytes length (little
endian) + payload; a reply is 1 byte status + 2 bytes length + UTF-8 text.

```csharp
public static int? PayloadLength(DisplayOp op) => op switch
{
    DisplayOp.Hello => 4,
    DisplayOp.Frame => FrameBytes,          // 480 * 320 * 2 = 307 200
    DisplayOp.Brightness => 1,
    DisplayOp.Blank or DisplayOp.Status => 0,
    _ => null,
};
```

```csharp
var op = (DisplayOp)header[0];
int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
if (PayloadLength(op) is not { } expected) throw new InvalidDataException($"Unknown request {header[0]}.");
if (length != expected) throw new InvalidDataException($"{op} must carry {expected} bytes, not {length}.");
var payload = new byte[length];
```

Two details matter for security:

- **The length is checked against the op before the buffer is allocated.** A client that announces a 1 GiB "frame" gets an error, and
  the service never allocates it (`A_wrong_length_is_refused_before_the_payload_is_read` checks that the stream position is still 5).
- **A bad request closes the connection** (`DisplayServer.Serve`), because after a framing error the stream is out of step.

Why binary and not the line-based text protocol the tray's control pipe uses? Text needs a line-length limit, parsing and escaping.
Fixed lengths make the parser about 15 lines long and easy to reason about.

### 4.2 Who may connect: `ServicePipeSecurity`

```csharp
public const PipeAccessRights ClientRights = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;

public static IReadOnlyList<Rule> Rules(SecurityIdentifier server) => new[]
{
    new Rule(Network, PipeAccessRights.FullControl, AccessControlType.Deny),
    new Rule(server, PipeAccessRights.FullControl, AccessControlType.Allow),
    new Rule(Administrators, ClientRights, AccessControlType.Allow),
    new Rule(Interactive, ClientRights, AccessControlType.Allow),
};
```

Read it as four sentences:

1. **Deny NETWORK everything.** A remote SMB logon carries the NETWORK SID, so the kernel refuses it before the service sees a byte.
   Deny entries are sorted first, so they win.
2. **The server's own account gets full control.** It needs `CreateNewInstance` to add more instances of its own pipe.
3. **Administrators and INTERACTIVE may read and write.** `ReadWrite` includes `ReadPermissions`, which clients need to check the
   owner (4.4).
4. **Nothing else.** There is no Everyone, Users, Anonymous or ALL APPLICATION PACKAGES entry, and no explicit integrity label, so
   Low-integrity and AppContainer processes are kept out by the default medium label.

The important *absence* is `CreateNewInstance` for clients. Without it, no normal user can add an instance to the service's pipe
while the service runs, so no one can sit between the tray and the service.

`CurrentUserOnly()` builds the tray control pipe's rules: the current user, full control.

### 4.3 Who may draw: `DisplayAccess` and `NativeSessions`

The pipe ACL lets every INTERACTIVE user connect, which includes a second user on fast user switching and RDP users. Who may *draw* is
decided per request:

```csharp
public static string? Deny(DisplayOp op, int? clientSession, int? consoleSession)
{
    if (op == DisplayOp.Status) return null;
    if (op is not (DisplayOp.Hello or DisplayOp.Frame or DisplayOp.Brightness or DisplayOp.Blank)) return "Unknown request.";
    if (clientSession is not { } client) return "Glance could not tell which Windows session this program runs in, ...";
    if (client == 0) return "Programs in session 0 (services) may not draw on the display; ...";
    if (consoleSession is not { } console) return "Nobody is signed in at the console right now, ...";
    if (client != console) return $"Another user is at the console. ...";
    return null;
}
```

The inputs come from two Win32 calls in `NativeSessions`:

```csharp
GetNamedPipeClientSessionId(pipe.SafePipeHandle.DangerousGetHandle(), out var id)   // which session the client runs in
WTSGetActiveConsoleSessionId()                                                       // which session owns the screen
```

Notice that `consoleSession` is read **for every request**, not once per connection. When a second user switches in, the first user's
next frame is refused at once, even before the `WTS_CONSOLE_DISCONNECT` notification arrives.

The pure function is easy to test (`Only_the_console_session_may_drive_the_display`), and the server takes the two lookups as
`Func<>` options so tests can fake them.

> **Review note.** The code treats "session unknown" as "remote, refuse". The review measured that a client coming over the SMB path
> gets a *made-up* session number (9 or 13 in a session-1 process), not null. Today the NETWORK deny ACE is what really keeps remote
> clients out (review L1).

### 4.4 Is this really the service? `PipeServerCheck` and the anonymous level

Named pipes have a classic weakness: **squatting**. The namespace is global, so while the service is stopped any normal program can
create `\\.\pipe\Glance.Service.Display`. The tray would then send frames to the squatter, and worse, believe its sensor values.

The client side therefore checks the owner of the server end right after connecting, before sending anything:

```csharp
public static IReadOnlyList<SecurityIdentifier> ServiceOwners { get; } = new[] { ServicePipeSecurity.LocalSystem, ServicePipeSecurity.Administrators };

public static void Verify(PipeStream pipe, IEnumerable<SecurityIdentifier>? trusted = null)
{
    var owner = OwnerOf(pipe);   // pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)); null if unreadable
    if (!IsTrustedOwner(owner, trusted ?? ServiceOwners))
        throw new UnauthorizedAccessException($"The pipe is served by {Describe(owner)}, not by the Glance service, so it was not used.");
}
```

Why the owner works as an identity check: Windows only lets a process make itself, or a group marked "may be owner" in its token, the
owner of a new object. A non-elevated process has neither SYSTEM nor Administrators available, so it **cannot** produce such an
owner. An unreadable owner counts as untrusted (fail closed).

The second protection is the **impersonation level**:

```csharp
// Anonymous: the server can never act as this user (ImpersonateNamedPipeClient), even if it is a squatter
var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
```

By default a .NET pipe client lets the server *impersonate* it. The service never needs that: it gets the client's session from
`GetNamedPipeClientSessionId`, not from the token. So every Glance client (display, sensors, tray control, and even the installer's
`CreateFileW` with `SECURITY_SQOS_PRESENT`) connects as Anonymous. The tray-control client also uses `PipeOptions.CurrentUserOnly`,
which makes .NET check that the server is owned by the same user.

> **Review note.** The owner check is sound, but the *server* side has a gap: without `FILE_FLAG_FIRST_PIPE_INSTANCE`, a service that
> starts after a squatter **joins** the squatter's pipe instead of failing (review M1, proven). Clients still refuse it, so it is a
> denial of service, not an elevation.

### 4.5 The service side: `DisplayServer`

`src/Glance.Core/Service/DisplayServer.cs` owns the USB device. Its rules:

- **Open lazily** on the first `FRAME`, and apply the last brightness at once.
- **Release on `BLANK`**, after 20 s without a frame (clients refresh every 10 s), and on sign-out or console disconnect of the driving
  session. This is what lets AIDA64 use the display when no Glance client draws.
- **A USB error** is returned to the client and closes the device, so the next frame reopens it fresh.
- **One lock (`_gate`)** guards the device. Reading a request happens *outside* the lock, and only the USB transfer happens inside it.

The frame path:

```csharp
case DisplayOp.Frame:
    lock (_gate)
    {
        if (!Power.MayDrive)
        {
            PayOwedBlankLocked();
            return (ReplyStatus.Error, Power.Ended ? "The display is off: Windows is shutting down." : "The display is off while Windows sleeps.");
        }
        if (_device is null) { /* _open(), SetBrightness(_brightness), log "Display opened for session N" */ }
        try { _device!.Blit(0, 0, DisplayWire.FrameWidth, DisplayWire.FrameHeight, request.Payload); }
        catch (Exception ex) { return Failed(ex); }
        _lastFrameMs = _o.NowMs();
        _driving = session;
        _lastError = null;
        return (ReplyStatus.Ok, "");
    }
```

Power events come from the service's control handler, which Windows waits for. So they must be **bounded**:

```csharp
public PowerBlank.Result HandlePower(PowerEvent e)
{
    var action = Power.Handle(e);   // from now on no frame is drawn (checked under the lock)
    if (action is not (PowerAction.BlankAndStop or PowerAction.BlankAndHold)) return PowerBlank.Result.NothingOpen;
    if (!Monitor.TryEnter(_gate, PowerBlank.OwnerBudgetFor(e) / 3))
    {
        _blankOwed = true;   // a transfer holds the device; the next request or tick blanks it
        return PowerBlank.Result.Busy;
    }
    try { return BlankAndCloseLocked() ? PowerBlank.Result.Blanked : PowerBlank.Result.NothingOpen; }
    finally { Monitor.Exit(_gate); }
}
```

Two ideas carry over from the shutdown-blank change (`docs/walkthroughs/shutdown-blank.md`):

- **Update the policy first, then try to blank.** `DisplayPowerPolicy` has its own lock, so `MayDrive` turns false at once, even if the
  device lock is busy. No later frame can light the display again.
- **Bounded wait, then an "owed" blank.** If a transfer holds the device, the blank is remembered and paid later instead of blocking Windows.

> **Review note.** For a suspend the wait is only about 400 ms, and the frame that holds the lock does not pay the owed blank itself.
> The user side's `BLANK` covers it (review L2). Also, `Serve` has no idle timeout and the pipe allows 8 instances, so 8 idle
> connections from any interactive user lock the console user out, and SYSTEM logs one line per second (review M2, proven).

### 4.6 The user side: `RemoteFrameDisplay` and `DisplayConnector`

The nicest part of the design is how little the user side changed. `DisplayPusher` (the display loop: page rotation, skipping identical
frames, forced refresh, error handling) talks to an `IFrameDisplay`. `RemoteFrameDisplay` is just another one:

```csharp
public void Blit(int x, int y, int w, int h, byte[] rgb565Be)
{
    if (x != 0 || y != 0 || w != Width || h != Height || rgb565Be.Length != DisplayWire.FrameBytes)
        throw new ArgumentException($"The display only takes whole {Width}x{Height} frames.");
    Request(DisplayOp.Frame, rgb565Be);   // returns after the service has done the USB transfer
}
```

Because `Request` waits for the reply, which comes after the USB transfer, the pusher's timing (about 400 ms per frame) and its USB-error
handling (`LastError`, `ShouldReopen`) behave exactly as with a local display. A broken connection or a 10 s timeout marks the object
unusable and throws `IOException`, which the pusher treats like a USB failure and reopens.

`DisplayConnector` chooses between the service and direct USB:

```csharp
internal static IFrameDisplay Open(string pipeName, IEnumerable<SecurityIdentifier>? trustedOwners, Func<IFrameDisplay> usb)
{
    try { return RemoteFrameDisplay.Connect(pipeName, trustedOwners); }
    catch (TimeoutException) { return usb(); }              // no service pipe: the service is not installed or not running
    catch (UnauthorizedAccessException) { return usb(); }   // not the service (or not ours to use): never send it frames
}
```

Note what is *not* caught: a `DisplayServiceException` (the service refused, for example "another user is at the console") propagates,
because the service holds the device and going around it would fight over USB (`The_connector_does_not_go_around_a_service_that_refuses`).

`DisplayPusher`'s public constructor now calls `DisplayConnector.Open()` instead of `Ax206Display.Open()`, so the tray app and the editor
picked up the service without other changes.

> **Review note.** `TimeoutException` also means "all instances busy" or "service still starting at sign-in"; the tray then holds USB as
> the user and never moves back to the service (review L3).

### 4.7 Talking to the Service Control Manager: `NativeService` and `ServiceControlMap`

.NET's `ServiceBase` does not let you accept `SERVICE_CONTROL_PRESHUTDOWN`, so `src/Glance.Service/NativeService.cs` uses the Win32 API
directly. The skeleton is the classic one:

```csharp
// kept in static fields so the garbage collector never frees a callback Windows still holds
private static readonly ServiceMainProc MainProc = ServiceMain;
private static readonly HandlerEx Handler = HandleControl;

public static bool Run(Func<IServiceBody> create)
{
    var table = new[]
    {
        new ServiceTableEntry { Name = ServiceNames.ServiceName, Proc = Marshal.GetFunctionPointerForDelegate(MainProc) },
        new ServiceTableEntry { Name = null, Proc = IntPtr.Zero },
    };
    return StartServiceCtrlDispatcherW(table);   // returns when the service has stopped
}
```

- `StartServiceCtrlDispatcherW` connects the process to the SCM. It returns false at once when the exe was started by hand, which
  `Program.Main` uses to print a hint.
- `ServiceMain` registers the handler (`RegisterServiceCtrlHandlerExW`), reports `START_PENDING` with a 30 s hint (LibreHardwareMonitor
  can be slow), starts the body, and reports `RUNNING` with `ControlsAccepted = STOP | PRESHUTDOWN | POWEREVENT | SESSIONCHANGE`.
- The handler does two different things, depending on what Windows waits for:

```csharp
case ServiceControlMap.Stop:
case ServiceControlMap.Preshutdown:
case ServiceControlMap.Shutdown:
    lock (_statusLock) { if (_state is StopPending or Stopped) return NoError; Report(StopPending, 5000); }
    // answer the control manager at once; the blank runs on another thread and reports "stopped" when done
    Task.Run(() => { try { body.Stop(signal); } catch (...) { ... } Report(Stopped, 0); });
    return NoError;

case ServiceControlMap.PowerEventControl:
case ServiceControlMap.SessionChange:
    // Windows waits for this answer before it sleeps, so the blank happens here (bounded inside)
    try { body.OnSignal(signal); } catch (...) { ... }
    return NoError;
```

The decision of *what* a control code means lives in Core, pure and tested:

```csharp
case Preshutdown:
case Shutdown: return new ServiceSignal(ServiceSignalKind.Power, PowerEvent.SessionEnding);
case Stop: return new ServiceSignal(ServiceSignalKind.Power, PowerEvent.Quit);
case PowerEventControl: /* PBT_APMSUSPEND -> Suspend; PBT_APMRESUMEAUTOMATIC/RESUMESUSPEND -> Resume */
case SessionChange:
    return eventType is WtsSessionLogoff or WtsConsoleDisconnect
        ? new ServiceSignal(ServiceSignalKind.SessionLeft, SessionId: sessionId)
        : ServiceSignal.None;
```

One subtle detail is reading the session id out of `eventData`: `WTSSESSION_NOTIFICATION` is `{ DWORD cbSize; DWORD dwSessionId; }`,
so `Marshal.ReadInt32(eventData, 4)` is right. Another is that `AcceptShutdown` is deliberately *not* set: a service that accepts
pre-shutdown gets it instead, earlier, and with its own timeout.

### 4.8 Who may start and stop the service: the service DACL

A service has its own security descriptor, separate from the exe file's. The installer sets it with `sc sdset`:

```csharp
public const string ServiceSddl =
    "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";
```

Decoded, using the service meanings of the SDDL letters: `CC` query config, `LC` query status, `SW` enumerate dependents, `RP`
**start**, `WP` **stop**, `DT` pause/continue, `LO` interrogate, `CR` user-defined control, `RC` read the DACL, `DC` change config,
`SD` delete, `WD`/`WO` change the DACL or owner.

- `SY` (SYSTEM) and `BA` (Administrators): the Windows defaults.
- `IU` (Interactive Users): the default **plus `RP`**. Any signed-in user may start the service, so the editor's "Start" button needs no prompt.
- `SU` (service logons): read only.

Stopping needs an administrator, because a stop would turn off the display and sensors for whoever is at the console. The editor
runs Windows' own `sc.exe stop` through one UAC prompt:

```csharp
new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), "stop " + name)
    { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Environment.SystemDirectory }
```

That is a native System32 tool, so the user's `CORECLR_`/`DOTNET_` variables have nothing to hook into.

`CheckServiceSddl` parses an SDDL and lists anything beyond "read + start for IU". The tests feed it unsafe variants (IU may stop, a
`GA` grant for Everyone, IU may change config), and `ServiceLayoutTests` checks that the installer text uses exactly this constant.

### 4.9 Where SYSTEM writes: `ServiceLog`

A SYSTEM process that writes into a folder a user controls can be turned into an "overwrite any file" primitive with a junction or a
hard link. `%ProgramData%` is a trap here: normal users may create folders in it and become their owner. So:

```csharp
if (!Directory.Exists(folder))
{
    var rules = new DirectorySecurity();
    rules.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
    // SYSTEM and Administrators full control, Users read and execute, inherited by files
    rules.CreateDirectory(folder);
}
return new ServiceLog(folder, FolderTrust.CheckFolder);   // writes nothing unless only admins can change the folder
```

- If the folder already exists, whoever created it, the log is only written when `FolderTrust` says only administrators can change it.
- Otherwise `DisabledReason` explains why, and it is added to the display `STATUS`.
- The uninstaller deletes only `service.log` and then the folder if it is empty, never a tree (the folder could be a planted link).

### 4.10 The installer

`installer/Glance.iss` does three new things. All of them run elevated, so the script follows one rule: **only Windows tools are
started while elevated**, always by full path:

```pascal
function Sc(const Params: String): Integer;
begin
  Result := RunHidden(ExpandConstant('{sys}\sc.exe'), Params);
end;

procedure InstallService();
var
  Bin: String;
begin
  Bin := 'binPath= "\"' + ExpandConstant('{app}\Glance.Service.exe') + '\"" start= auto obj= LocalSystem DisplayName= "Glance display service"';
  if Sc('query GlanceService') = ERROR_SERVICE_DOES_NOT_EXIST then Sc('create GlanceService ' + Bin) else Sc('config GlanceService ' + Bin);
  Sc('failure GlanceService reset= 86400 actions= restart/5000/restart/10000/restart/60000');
  Sc('sdset GlanceService D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)');
  RegWriteDWordValue(HKLM, 'SYSTEM\CurrentControlSet\Services\GlanceService', 'PreshutdownTimeout', 5000);
  Sc('start GlanceService');
end;
```

- **The `binPath` is quoted** (`"\"...\""`). An unquoted `C:\Program Files\Glance\Glance.Service.exe` would let Windows try
  `C:\Program.exe` first: the classic "unquoted service path" bug.
- **Upgrade from 0.1.1** happens in `PrepareToInstall`, before any file is replaced:
  1. Ask the old elevated runner to `EXIT` over its pipe, so it blanks the display.
  2. Delete the "Glance Runner" task.
  3. `net stop` a newer service.
  4. `taskkill` whatever is left.
- **The `EXIT` uses `CreateFileW`, not PowerShell.** The author's first draft used a PowerShell one-liner. PowerShell 5.1 is a .NET
  Framework program, so `COR_ENABLE_PROFILING` from the user's environment would have loaded a profiler into it while elevated: the
  H1 hole again, inside the fix for H1. The design note records this.
- **The tray and the editor are started with `runasoriginaluser`**, and the sign-in shortcut lives in the common Startup folder, which
  only admins can change. The tray is `asInvoker`, so at sign-in it gets the user's normal token. The user's environment is then
  harmless: it can only affect a process that already runs as the user.

> **Review note.** The directory page is still enabled. An admin who installs to `C:\Glance` gets a folder every user can modify
> (inherited from `C:\`), and SYSTEM then runs whatever is in it (review H1). Lock the folder to Program Files and reset its ACL.

### 4.11 The tray app and the editor

The tray app (`Glance.Runner`) lost its admin manifest, its sensor pipe server and its auto-start installer mode. It gained:

- `SensorHub`, moved from the editor into Core, so the tray and the editor share it. It prefers the service's feed and falls back
  to local basic sensors.
- A per-session control pipe name, `Glance.Runner.Control.<session>`. Two signed-in users no longer collide, and the editor finds its
  own tray (`RunnerClient.IsRunning` now filters by session).
- A "Glance service: ..." menu line, filled off the UI thread from `DisplayConnector.ServiceStatus()` and `ServiceControl.Query()`.

```xml
<!-- The tray app runs with the user's normal rights; the Glance service (LocalSystem) reads the sensors and owns USB. -->
<requestedExecutionLevel level="asInvoker" uiAccess="false" />
```

The editor's Background service window became a status panel. It shows the service state and the display status line, has
Start (no prompt) and Stop (UAC for `sc.exe`) buttons and tray start/stop, and offers to remove a leftover 0.1 task. FFmpeg is now
installed per user, because video is decoded only in user processes.

### 4.12 Tests

The change adds 82 tests (624 in total) in four files:

- **`RunnerServiceTests.cs` (`ServiceRulesTests`):** pure rules.
  - The pipe DACL: NETWORK denied, no take-over rights for clients, no broad groups.
  - The owner rule, the console-session rule, the control-code mapping and the accepted controls.
  - The service SDDL, including unsafe variants.
  - The wire format: exact lengths, refusal before allocation, bounded replies.
  - The log-folder trust. One test reads this PC's real `C:\ProgramData` ACL.
- **`DisplayServiceTests.cs`:** the server and the client over **real named pipes** with a `FakeDisplay`.
  - Frames and brightness, refusals with reasons, wrong sizes, idle release, USB errors, sleep and shutdown, sign-out.
  - A power event against a stuck transfer.
  - The pusher end to end.
  - The connector's three outcomes.
  - Two tests that **play the squatter**: they serve the pipe, read the client's bytes, call `RunAsClient`, and assert they could not
    act as the user.
- **`ServiceChecksTests.cs`:** the setup-check rows and texts.
- **`ServiceLayoutTests.cs`:** the repository's shape.
  - No manifest asks for admin, SensorHost and the auto-start installer are gone, and the service does not load startup hooks.
  - The installer uses the tested SDDL and retires the old runner.

A pattern worth copying: the server takes its pipe name, security, session lookups and clock as options, so the tests never touch the
real pipe names. A Glance service running on the developer's PC is never disturbed.

---

## 5. Self-review: five ways to improve the change

1. **Refuse a squatted pipe instead of joining it.**
   - The design says a squatter makes the service "fail to create its pipe". In fact `NamedPipeServerStreamAcl.Create` with 8 or 16
     instances silently joins a squatter's pipe, under the squatter's rules, because the default DACL of a user-created pipe grants
     SYSTEM full control.
   - Pass `PipeOptions.FirstPipeInstance` for the first instance (it exists in .NET 8), and check the owner of your own instance after
     creating it. Do this in `SensorPipeServer` too, and give it a log callback.
   - Better still, create the pipes through `CreateNamedPipeW` with `PIPE_REJECT_REMOTE_CLIENTS` as well, so the NETWORK ACE is no
     longer the only barrier against remote clients.
2. **Make the service's own folder part of the trust model.**
   - The service *executes* everything in `{app}`, but nothing checks that folder. 0.1.1 did, for its elevated task.
   - Set `DisableDirPage=yes` (or refuse folders outside `{autopf}`), reset the folder's ACL to SYSTEM/Administrators full and Users
     read in the installer, and have `ServiceMain` run `FolderTrust.CheckExecutable(Environment.ProcessPath, TrustScope.FolderTree)`
     and refuse to start when it fails.
   - Also call `SetDefaultDllDirectories(System32 | ApplicationDir)` first thing in `Main`, so neither `libusb0.dll` nor
     LibreHardwareMonitor's vendor DLLs can come from `PATH`.
3. **Bound what one client can cost SYSTEM.**
   - Add a read timeout per connection, at most two connections per session (with one kept for the console session), rate-limited
     log lines, and a size cap with rotation for `service.log`.
   - Today 8 idle connections from another user lock the console user out, and the service logs a line every second until it restarts.
4. **Make "never elevated" a runtime rule, not just a manifest.**
   - A leftover 0.1 task (run level Highest) starts the new tray elevated with the user's environment, and nothing notices.
   - Let the tray and the editor check `TokenElevationType` at start, log `RunnerEnvironment.Warning`, and refuse to render elevated.
   - Let the service delete the 0.1 task when it starts (SYSTEM may), and have the installer verify the deletion.
5. **Close the remaining timing and robustness gaps, and test them.**
   - After a frame, pay an owed power blank at once (`if (!Power.MayDrive) BlankAndCloseLocked();`), so a suspend that arrives during
     a transfer is not left to the user side.
   - Make `DisplayConnector` fall back to USB only when the service is not installed or stopped, not when it is busy or still starting.
   - Check every `sc.exe` exit code in the installer. Disable the recovery actions before the upgrade `taskkill`, and set the
     pre-shutdown timeout with `ChangeServiceConfig2W`.
   - Add tests for the three mutations that survive today: no `CurrentUserOnly` on the tray client, a refused HELLO that keeps the
     connection open, and an unclamped brightness.
