# Elevated pipe owner: a walkthrough of the "tests fail on GitHub CI" fix

This walkthrough is for a mid-level developer. It explains why about 44 pipe tests passed on a developer's PC but failed on GitHub's
CI runner, and how a test-side change fixed that without loosening any production security check. It covers Windows object ownership,
the token's default owner (`TokenOwner`) under UAC, why CI behaved differently, the fix itself, and five ways the change could be better.

**The change:** branch `claude/fix-elevated-pipe-owner-809bf3`, commit `c94b768` on top of `main` `e64ba4c`.
**Files:** `src/Glance.Core/Security/TokenOwner.cs` (new), `src/Glance.Tests/TestPipes.cs` (new), `src/Glance.Tests/ElevatedPipeOwnerTests.cs` (new),
and call sites in `DisplayServiceTests`, `DisplayServiceReviewTests`, `SensorFeedTests`, `SensorFeedReviewTests`, `SensorPipeTests`, `UiThreadAuditTests` and `UiThreadDeadlockTests`.
**Author's note:** `docs/design/fix-elevated-pipe-owner.md`. **Independent review:** `docs/reviews/review-elevated-pipe-owner.md`.

---

## 1. The problem

CI runs 46176951882 (tag v0.2.0) and 46176949017 (`main`) failed 45 and 46 tests. Most of them failed with:

> The pipe is served by BUILTIN\Administrators, not by the Glance service, so it was not used.

That message comes from `PipeServerCheck.Verify`. Before a Glance client sends a frame or trusts a sensor feed, it checks **who owns the
server end of the pipe**. Glance trusts an owner the client cannot fake. The tests start their own servers in the test process, so they
passed a different trusted owner: the current user's SID (`new[] { Me }`). On CI, those test servers were owned by `BUILTIN\Administrators`
instead, so the clients refused them.

## 2. Windows object ownership and `TokenOwner`

- Every securable object (file, named pipe, registry key) has a security descriptor with an **owner** SID. The owner can always read and
  change the object's DACL, so "who owns this" is a strong identity signal.
- When a process creates an object **without naming an owner**, Windows uses the default owner from the process token. That is the
  `TokenOwner` field, which .NET exposes as `WindowsIdentity.Owner`. `WindowsIdentity.User` is a different field, `TokenUser`.
- You may name only two kinds of SID as owner: your own user SID, or a group in your token that has the `SE_GROUP_OWNER` attribute.
  Under UAC, a normal (filtered) token has Administrators only as a deny-only group, so **a normal user cannot make Administrators or SYSTEM
  the owner**. Glance's service-pipe check is built on this rule (`PipeServerCheck.ServiceOwners` = SYSTEM, Administrators).
- In an administrator's **elevated** token, `TokenOwner` is `BUILTIN\Administrators` by default. A policy ("Default owner for objects created by
  members of the Administrators group") can change it to "Object creator". This is why files you create from an elevated prompt belong
  to Administrators.

| Token | `TokenUser` | `TokenOwner` (owner of new pipes) |
|---|---|---|
| Normal user, or admin with UAC on (filtered) | you | you |
| Admin elevated ("Run as administrator", or UAC off) | you | `BUILTIN\Administrators` |
| LocalSystem (the Glance service) | SYSTEM | SYSTEM or Administrators |

## 3. Why CI differed from a developer's PC

- On a developer's PC, `dotnet test` runs with a normal token, so `TokenOwner` = user. The test servers' pipes were owned by the user, and
  `new[] { Me }` matched.
- GitHub's `windows-latest` runner runs the job as an elevated administrator, so `TokenOwner` = Administrators. Every test server pipe was
  owned by Administrators, `new[] { Me }` did not match, and the tests failed. Other tests failed as a knock-on effect: a feed that never
  arrives, the wrong exception type, or a fallback to USB.
- The "squatter" tests failed the other way round. Their fake squatter was meant to look like a normal user's program, but elevated it
  was owned by Administrators, which the product **rightly** trusts as the service. So "a squatter is refused" became "a squatter is accepted".
- A normal token cannot create an Administrators-owned pipe, so the elevated case cannot be reproduced on a developer's PC without elevation.

## 4. The fix

**One seam for "who owns what I create".** It lives in `Glance.Core/Security/TokenOwner.cs`:

```csharp
public static SecurityIdentifier Current()
{
    using var me = WindowsIdentity.GetCurrent();
    return Of(me.User!, me.Owner);
}
public static SecurityIdentifier Of(SecurityIdentifier user, SecurityIdentifier? tokenOwner) => tokenOwner ?? user;
```

`Of` is a pure function, so a test can feed it a fake elevated token. `Current()` reads the real token.

**Tests trust exactly the owner their own pipes get.** From `Glance.Tests/TestPipes.cs`:

```csharp
public static IReadOnlyList<SecurityIdentifier> Own { get; } = TrustedFor(User, TokenOwner.Current());
public static SecurityIdentifier[] TrustedFor(SecurityIdentifier user, SecurityIdentifier tokenOwner) => new[] { TokenOwner.Of(user, tokenOwner) };
```

About 34 call sites changed from `new[] { Me }` to `TestPipes.Own`, for example:

```csharp
-        var c = RemoteFrameDisplay.Connect(_pipe, new[] { Me }, connectTimeoutMs: 3000);
+        var c = RemoteFrameDisplay.Connect(_pipe, TestPipes.Own, connectTimeoutMs: 3000);
```

`Own` holds one SID, never a list of "anything that might be right". Locally it is the user. On CI it is Administrators.

**Squatters name the user as owner explicitly**, so they look like a normal user's program even when the tests run elevated:

```csharp
public static PipeSecurity UserOwned(PipeSecurity security) { security.SetOwner(User); return security; }
// DisplayServiceTests.StartServer
Security = asSquatter ? TestPipes.UserOwned(security) : security,
```

Naming your own user SID as owner is always allowed. The three squatter tests then still check the **product's** rule
(`trustedOwners: null` → SYSTEM/Administrators) and expect a refusal.

**Proof** (`ElevatedPipeOwnerTests`): under a fake elevated token the tests trust exactly Administrators. Under a normal token they trust
only the user. A real pipe created by this process is owned by `TokenOwner.Current()`. A `UserOwned` squatter is owned by the user and
is refused by `PipeServerCheck.ServiceOwners`. On `main`'s behaviour, 3 of these 5 tests fail. On the branch, all 5 pass.

**Production is unchanged.** `PipeServerCheck.ServiceOwners`, `ServicePipeFactory` (accepts the user or Administrators as its own owner)
and the tray client's `CurrentUserOnly` are untouched.

**UAC off (side note).** With UAC off, the tray and the editor both run elevated, and both have `TokenOwner` = Administrators. .NET's
`CurrentUserOnly` compares the pipe owner with the caller's own `TokenOwner`, so they still accept each other. CI confirms this: the
tray-pipe tests passed elevated. An editor started with "Run as administrator" while UAC is on refuses a normal tray, because the owners
differ. That is deliberate: an elevated process should not trust a pipe that a normal-rights process can serve.

## 5. Self-review: five ways to improve the change

1. **Keep test helpers out of the product.** `TokenOwner` is public in the shipped `Glance.Core`, but only the tests use it. Move it into
   `Glance.Tests`, or describe the change as "no production behaviour changed" instead of "tests only".
2. **Prove that CI really runs the elevated path.** Proof tests 1–3 check a `??` and pass anywhere. Add a CI step (`whoami /groups`) or a
   test output line with `TokenOwner.Current()` so a green run is known to have covered Administrators.
3. **Guard against regressions.** Add a source-scan test, like `ConfigureAwaitGuardTests`, that fails when a test passes the user SID
   (`Me`, `WindowsIdentity.GetCurrent().User`) as `trustedOwners`. The next new pipe test would otherwise break CI again.
4. **Finish the job "make CI green".** `UiThreadDeadlockTests.The_editors_tray_command_gets_its_reply_on_its_UI_thread` failed in both CI
   runs. It races a 200 ms server delay against an 800 ms reply timeout on a 4-vCPU runner and is not owner-related.
   `LiveLeaseTests.M5_a_closed_link_answers_at_once_with_no_reply` is similar. Give both sync points in a separate change.
5. **Write down the accepted risk.** With UAC off, any program the administrator runs gets the Administrators owner and so passes the
   service-pipe check. That is acceptable, because such a program can already do anything on the PC. Add one line about it next to
   `PipeServerCheck.ServiceOwners`, and give a clear message when an elevated editor refuses a normal tray. Today it is only reported as
   "not answering".
