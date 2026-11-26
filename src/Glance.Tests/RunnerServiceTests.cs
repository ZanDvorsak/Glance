using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Glance.Core.Display;
using Glance.Core.Security;
using Glance.Core.Service;

namespace Glance.Tests;

/// <summary>
/// The pure rules of the Glance service (docs/design/runner-service.md): pipe rules, owner check, who may drive the display,
/// service control codes, the service's own rules, the wire format and the service log.
/// </summary>
public class ServiceRulesTests
{
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);
    private static readonly SecurityIdentifier AuthenticatedUsers = new(WellKnownSidType.AuthenticatedUserSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Anonymous = new(WellKnownSidType.AnonymousSid, null);

    // ---- pipe rules ------------------------------------------------------------------------------------------------

    [Fact]
    public void Service_pipes_deny_the_network_and_let_users_only_read_and_write()
    {
        var rules = ServicePipeSecurity.Rules(ServicePipeSecurity.LocalSystem);

        Assert.Contains(rules, r => r.Sid == ServicePipeSecurity.Network && r.Type == AccessControlType.Deny && r.Rights == PipeAccessRights.FullControl);
        Assert.Contains(rules, r => r.Sid == ServicePipeSecurity.LocalSystem && r.Type == AccessControlType.Allow && r.Rights == PipeAccessRights.FullControl);
        Assert.Contains(rules, r => r.Sid == ServicePipeSecurity.Interactive && r.Type == AccessControlType.Allow && r.Rights == ServicePipeSecurity.ClientRights);
        Assert.Contains(rules, r => r.Sid == ServicePipeSecurity.Administrators && r.Type == AccessControlType.Allow && r.Rights == ServicePipeSecurity.ClientRights);

        const PipeAccessRights takeover = PipeAccessRights.CreateNewInstance | PipeAccessRights.ChangePermissions | PipeAccessRights.TakeOwnership | PipeAccessRights.Delete;
        foreach (var r in rules.Where(r => r.Type == AccessControlType.Allow && r.Sid != ServicePipeSecurity.LocalSystem))
            Assert.True((r.Rights & takeover) == 0, $"{r.Sid} may create instances, change the rules or take the pipe over");
        foreach (var broad in new[] { Everyone, AuthenticatedUsers, Users, Anonymous })
            Assert.DoesNotContain(rules, r => r.Sid == broad);
        Assert.Equal(4, rules.Count);
    }

    [Fact]
    public void The_built_pipe_security_has_exactly_those_rules()
    {
        var security = ServicePipeSecurity.Build(ServicePipeSecurity.LocalSystem);
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        Assert.Equal(4, rules.Count);
        Assert.Contains(rules, r => r.IdentityReference == ServicePipeSecurity.Network && r.AccessControlType == AccessControlType.Deny);
        Assert.DoesNotContain(rules, r => r.IdentityReference == Me && ServicePipeSecurity.LocalSystem != Me);
    }

    [Fact]
    public void Only_system_or_administrators_count_as_the_service()
    {
        Assert.True(PipeServerCheck.IsTrustedOwner(ServicePipeSecurity.LocalSystem, PipeServerCheck.ServiceOwners));
        Assert.True(PipeServerCheck.IsTrustedOwner(ServicePipeSecurity.Administrators, PipeServerCheck.ServiceOwners));
        Assert.False(PipeServerCheck.IsTrustedOwner(Me, PipeServerCheck.ServiceOwners));
        Assert.False(PipeServerCheck.IsTrustedOwner(Users, PipeServerCheck.ServiceOwners));
        Assert.False(PipeServerCheck.IsTrustedOwner(null, PipeServerCheck.ServiceOwners));
        Assert.True(PipeServerCheck.IsTrustedOwner(Me, new[] { Me }));
    }

    // ---- who may drive the display ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(DisplayOp.Hello)]
    [InlineData(DisplayOp.Frame)]
    [InlineData(DisplayOp.Brightness)]
    [InlineData(DisplayOp.Blank)]
    public void Only_the_console_session_may_drive_the_display(DisplayOp op)
    {
        Assert.Null(DisplayAccess.Deny(op, clientSession: 2, consoleSession: 2));
        Assert.Contains("console", DisplayAccess.Deny(op, clientSession: 3, consoleSession: 2));
        Assert.NotNull(DisplayAccess.Deny(op, clientSession: null, consoleSession: 2));   // unknown (for example remote) client
        Assert.NotNull(DisplayAccess.Deny(op, clientSession: 2, consoleSession: null));   // nobody at the console
        Assert.NotNull(DisplayAccess.Deny(op, clientSession: 0, consoleSession: 0));      // session 0 is services, never a person
    }

    [Fact]
    public void Anyone_connected_may_ask_for_the_status()
    {
        Assert.Null(DisplayAccess.Deny(DisplayOp.Status, clientSession: 3, consoleSession: 2));
        Assert.Null(DisplayAccess.Deny(DisplayOp.Status, clientSession: null, consoleSession: null));
    }

    [Fact]
    public void Unknown_requests_are_refused() =>
        Assert.NotNull(DisplayAccess.Deny((DisplayOp)99, clientSession: 2, consoleSession: 2));

    // ---- service control codes -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ServiceControlMap.Preshutdown, 0, PowerEvent.SessionEnding)]
    [InlineData(ServiceControlMap.Shutdown, 0, PowerEvent.SessionEnding)]
    [InlineData(ServiceControlMap.Stop, 0, PowerEvent.Quit)]
    [InlineData(ServiceControlMap.PowerEventControl, PowerMessages.PBT_APMSUSPEND, PowerEvent.Suspend)]
    [InlineData(ServiceControlMap.PowerEventControl, PowerMessages.PBT_APMRESUMEAUTOMATIC, PowerEvent.Resume)]
    [InlineData(ServiceControlMap.PowerEventControl, PowerMessages.PBT_APMRESUMESUSPEND, PowerEvent.Resume)]
    public void Control_codes_become_power_events(int control, int eventType, PowerEvent expected)
    {
        var s = ServiceControlMap.Map(control, eventType, 0);
        Assert.Equal(ServiceSignalKind.Power, s.Kind);
        Assert.Equal(expected, s.Power);
    }

    [Theory]
    [InlineData(ServiceControlMap.WtsSessionLogoff, 3)]
    [InlineData(ServiceControlMap.WtsConsoleDisconnect, 4)]
    public void Leaving_the_console_or_signing_out_is_reported_with_the_session(int wts, int session)
    {
        var s = ServiceControlMap.Map(ServiceControlMap.SessionChange, wts, session);
        Assert.Equal(ServiceSignalKind.SessionLeft, s.Kind);
        Assert.Equal(session, s.SessionId);
    }

    [Theory]
    [InlineData(ServiceControlMap.SessionChange, ServiceControlMap.WtsSessionLock)]
    [InlineData(ServiceControlMap.SessionChange, ServiceControlMap.WtsSessionLogon)]
    [InlineData(ServiceControlMap.PowerEventControl, 0xA)]   // PBT_APMPOWERSTATUSCHANGE (battery)
    [InlineData(ServiceControlMap.Interrogate, 0)]
    public void Other_controls_do_nothing(int control, int eventType) =>
        Assert.Equal(ServiceSignalKind.None, ServiceControlMap.Map(control, eventType, 1).Kind);

    [Fact]
    public void The_service_accepts_preshutdown_power_and_session_events()
    {
        var a = ServiceControlMap.AcceptedControls;
        Assert.Equal(ServiceControlMap.AcceptStop, a & ServiceControlMap.AcceptStop);
        Assert.Equal(ServiceControlMap.AcceptPreshutdown, a & ServiceControlMap.AcceptPreshutdown);
        Assert.Equal(ServiceControlMap.AcceptPowerEvent, a & ServiceControlMap.AcceptPowerEvent);
        Assert.Equal(ServiceControlMap.AcceptSessionChange, a & ServiceControlMap.AcceptSessionChange);
        Assert.Equal(0, a & ServiceControlMap.AcceptShutdown);   // with pre-shutdown accepted, plain shutdown would only come later
    }

    // ---- the service's own rules (who may start or stop it) ---------------------------------------------------------

    [Fact]
    public void Interactive_users_may_start_the_service_but_not_stop_change_or_delete_it()
    {
        var sd = new RawSecurityDescriptor(ServiceNames.ServiceSddl);
        var iu = sd.DiscretionaryAcl!.Cast<CommonAce>().Where(a => a.SecurityIdentifier == ServicePipeSecurity.Interactive && a.AceQualifier == AceQualifier.AccessAllowed)
            .Aggregate(0, (m, a) => m | a.AccessMask);
        Assert.Equal(ServiceNames.ServiceStart, iu & ServiceNames.ServiceStart);
        Assert.Equal(ServiceNames.ServiceQueryStatus, iu & ServiceNames.ServiceQueryStatus);
        Assert.Equal(0, iu & (ServiceNames.ServiceStop | ServiceNames.ServiceChangeConfig | ServiceNames.Delete | ServiceNames.WriteDac | ServiceNames.WriteOwner | ServiceNames.ServicePauseContinue));
        Assert.Empty(ServiceNames.CheckServiceSddl(ServiceNames.ServiceSddl));
    }

    [Theory]
    [InlineData("D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPWPLOCRRC;;;IU)", "stop")]
    [InlineData("D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPLOCRRC;;;IU)(A;;GA;;;WD)", "S-1-1-0")]
    [InlineData("D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)", "start")]
    [InlineData("D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCDCLCSWRPLOCRRC;;;IU)", "change")]
    public void Unsafe_service_rules_are_reported(string sddl, string expectedWord) =>
        Assert.Contains(ServiceNames.CheckServiceSddl(sddl), p => p.Contains(expectedWord, StringComparison.OrdinalIgnoreCase));

    // ---- wire format ----------------------------------------------------------------------------------------------

    [Fact]
    public void Every_request_has_one_exact_length()
    {
        Assert.Equal(4, DisplayWire.PayloadLength(DisplayOp.Hello));
        Assert.Equal(480 * 320 * 2, DisplayWire.PayloadLength(DisplayOp.Frame));
        Assert.Equal(1, DisplayWire.PayloadLength(DisplayOp.Brightness));
        Assert.Equal(0, DisplayWire.PayloadLength(DisplayOp.Blank));
        Assert.Equal(0, DisplayWire.PayloadLength(DisplayOp.Status));
        Assert.Null(DisplayWire.PayloadLength((DisplayOp)99));
        Assert.Throws<ArgumentException>(() => DisplayWire.EncodeRequest(DisplayOp.Frame, new byte[10]));
    }

    [Fact]
    public async Task Requests_and_replies_survive_the_round_trip()
    {
        var ms = new MemoryStream();
        ms.Write(DisplayWire.EncodeRequest(DisplayOp.Brightness, new byte[] { 5 }));
        ms.Write(DisplayWire.EncodeRequest(DisplayOp.Status, ReadOnlySpan<byte>.Empty));
        ms.Write(DisplayWire.EncodeReply(ReplyStatus.Denied, "nope ü"));
        ms.Position = 0;

        var a = await DisplayWire.ReadRequestAsync(ms, default);
        Assert.Equal(DisplayOp.Brightness, a!.Op);
        Assert.Equal(new byte[] { 5 }, a.Payload);
        Assert.Equal(DisplayOp.Status, (await DisplayWire.ReadRequestAsync(ms, default))!.Op);
        Assert.Equal((ReplyStatus.Denied, "nope ü"), await DisplayWire.ReadReplyAsync(ms, default));
        Assert.Null(await DisplayWire.ReadRequestAsync(ms, default));   // clean end
    }

    [Fact]
    public async Task A_wrong_length_is_refused_before_the_payload_is_read()
    {
        var header = new byte[] { (byte)DisplayOp.Frame, 0x00, 0x00, 0x00, 0x40 };   // 1 GiB "frame"
        var ms = new MemoryStream(header.Concat(new byte[64]).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => DisplayWire.ReadRequestAsync(ms, default));
        Assert.Equal(DisplayWire.HeaderBytes, ms.Position);
    }

    [Fact]
    public async Task Unknown_ops_and_short_requests_are_refused()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => DisplayWire.ReadRequestAsync(new MemoryStream(new byte[] { 99, 0, 0, 0, 0 }), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => DisplayWire.ReadRequestAsync(new MemoryStream(new byte[] { (byte)DisplayOp.Hello, 4, 0, 0, 0, 1 }), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => DisplayWire.ReadRequestAsync(new MemoryStream(new byte[] { (byte)DisplayOp.Hello, 4 }), default));
    }

    [Fact]
    public async Task Replies_are_bounded()
    {
        var reply = DisplayWire.EncodeReply(ReplyStatus.Error, new string('x', 5000));
        Assert.True(reply.Length <= 3 + DisplayWire.MaxMessageBytes);
        var (_, text) = await DisplayWire.ReadReplyAsync(new MemoryStream(reply), default);
        Assert.Equal(DisplayWire.MaxMessageBytes, Encoding.UTF8.GetByteCount(text));

        var tooLong = new byte[] { 1, 0xFF, 0xFF }.Concat(new byte[0xFFFF]).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => DisplayWire.ReadReplyAsync(new MemoryStream(tooLong), default));
    }

    // ---- service log ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_service_log_is_not_written_into_a_folder_a_user_can_change()
    {
        // a folder in the user's temp folder: the user owns it, exactly like a folder planted in %ProgramData%
        var dir = Path.Combine(Path.GetTempPath(), "glance-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var log = new ServiceLog(dir, FolderTrust.CheckFolder);
            log.Write("hello");
            Assert.NotNull(log.DisabledReason);
            Assert.False(File.Exists(Path.Combine(dir, "service.log")));

            var trusted = new ServiceLog(dir, _ => TrustVerdict.Yes);
            trusted.Write("hello");
            Assert.Null(trusted.DisabledReason);
            Assert.Contains("hello", File.ReadAllText(Path.Combine(dir, "service.log")));
        }
        finally { Directory.Delete(dir, true); }
    }
}

