using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Glance.Core.Service;

/// <summary>
/// Checks who serves a pipe before a client trusts it. A normal user can create a pipe with the service's name while the
/// service is stopped ("squatting"). The owner of the server end tells them apart: a non-elevated process cannot make
/// SYSTEM or Administrators the owner of an object it creates.
/// </summary>
public static class PipeServerCheck
{
    /// <summary>The owners a service pipe may have: SYSTEM, or Administrators (the default owner in elevated tokens).</summary>
    public static IReadOnlyList<SecurityIdentifier> ServiceOwners { get; } = new[] { ServicePipeSecurity.LocalSystem, ServicePipeSecurity.Administrators };

    public static bool IsTrustedOwner(SecurityIdentifier? owner, IEnumerable<SecurityIdentifier> trusted) =>
        owner is not null && trusted.Contains(owner);

    /// <summary>The owner of the server end of a connected pipe, or null if it cannot be read.</summary>
    public static SecurityIdentifier? OwnerOf(PipeStream pipe)
    {
        try { return pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier; }
        catch { return null; }
    }

    /// <summary>
    /// Throws <see cref="UnauthorizedAccessException"/> when the server end of <paramref name="pipe"/> is not owned by one of
    /// <paramref name="trusted"/> (default <see cref="ServiceOwners"/>).
    /// </summary>
    public static void Verify(PipeStream pipe, IEnumerable<SecurityIdentifier>? trusted = null)
    {
        var owner = OwnerOf(pipe);
        if (!IsTrustedOwner(owner, trusted ?? ServiceOwners))
            throw new UnauthorizedAccessException($"The pipe is served by {Describe(owner)}, not by the Glance service, so it was not used.");
    }

    private static string Describe(SecurityIdentifier? sid)
    {
        if (sid is null) return "an unknown account";
        try { return sid.Translate(typeof(NTAccount)).Value; } catch { return sid.Value; }
    }
}
