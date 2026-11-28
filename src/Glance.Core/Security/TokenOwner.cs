using System.Security.Principal;

namespace Glance.Core.Security;

/// <summary>
/// Who owns the objects (pipes, files) this process creates without naming an owner: its token's default owner
/// (<c>TokenOwner</c>). That is the user in a normal token, but <c>BUILTIN\Administrators</c> in an administrator's elevated
/// token (UAC off, "Run as administrator", or GitHub's CI runner), unless the "default owner for objects created by members
/// of the Administrators group" policy says "object creator". .NET's <c>PipeOptions.CurrentUserOnly</c> compares against the
/// same value (<see cref="WindowsIdentity.Owner"/>) on both ends of a pipe. See <c>docs/design/fix-elevated-pipe-owner.md</c>.
/// </summary>
public static class TokenOwner
{
    /// <summary>The default owner of this process's token.</summary>
    public static SecurityIdentifier Current()
    {
        using var me = WindowsIdentity.GetCurrent();
        return Of(me.User!, me.Owner);
    }

    /// <summary>Pure: the owner a token with this user and this <c>TokenOwner</c> gives new objects (the user if none is reported).</summary>
    public static SecurityIdentifier Of(SecurityIdentifier user, SecurityIdentifier? tokenOwner) => tokenOwner ?? user;
}
