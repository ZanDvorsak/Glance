using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Glance.Core.Service;

/// <summary>
/// The access rules of the service's pipes. One place, tested, used for both the display pipe and the sensor pipe.
/// </summary>
public static class ServicePipeSecurity
{
    /// <summary>One access rule.</summary>
    public sealed record Rule(SecurityIdentifier Sid, PipeAccessRights Rights, AccessControlType Type);

    public static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    public static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    public static readonly SecurityIdentifier Interactive = new(WellKnownSidType.InteractiveSid, null);
    public static readonly SecurityIdentifier Network = new(WellKnownSidType.NetworkSid, null);

    /// <summary>What a client may do: read, write and wait on the pipe. Not create instances, change rules or take ownership.</summary>
    public const PipeAccessRights ClientRights = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;

    /// <param name="server">The account that serves the pipe (SYSTEM for the service; the test user in tests). It needs full
    /// control to create further instances of its own pipe.</param>
    public static IReadOnlyList<Rule> Rules(SecurityIdentifier server) => new[]
    {
        // remote clients arrive with the NETWORK group in their token: refused by Windows before the service sees them
        new Rule(Network, PipeAccessRights.FullControl, AccessControlType.Deny),
        new Rule(server, PipeAccessRights.FullControl, AccessControlType.Allow),
        new Rule(Administrators, ClientRights, AccessControlType.Allow),
        // anyone signed in at this PC (console or remote desktop); which of them may draw is decided per request (DisplayAccess)
        new Rule(Interactive, ClientRights, AccessControlType.Allow),
    };

    public static PipeSecurity Build(SecurityIdentifier server)
    {
        var security = new PipeSecurity();
        foreach (var r in Rules(server)) security.AddAccessRule(new PipeAccessRule(r.Sid, r.Rights, r.Type));
        return security;
    }

    /// <summary>The rules for a pipe that only the current user may use (the tray's control pipe).</summary>
    public static PipeSecurity CurrentUserOnly()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(Network, PipeAccessRights.FullControl, AccessControlType.Deny));   // L1: no logons from other PCs
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
