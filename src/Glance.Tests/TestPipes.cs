using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Glance.Core.Security;

namespace Glance.Tests;

/// <summary>
/// Who owns the pipes a test serves itself, so the tests pass both as a normal user (on a developer's PC) and as an elevated
/// administrator (GitHub's CI runner), where Windows makes <c>BUILTIN\Administrators</c> the owner of new pipes
/// (docs/design/fix-elevated-pipe-owner.md).
/// </summary>
internal static class TestPipes
{
    /// <summary>The account the tests run as (for ACL rules and "who did the server act as" checks, not for owner checks).</summary>
    public static readonly SecurityIdentifier User = WindowsIdentity.GetCurrent().User!;

    /// <summary>What a test client trusts for a server the test runs with default security: the owner Windows gives it.</summary>
    public static IReadOnlyList<SecurityIdentifier> Own { get; } = TrustedFor(User, TokenOwner.Current());

    /// <summary>The owners a test trusts for its own pipes, for a token with this user and this default owner (TokenOwner).</summary>
    public static SecurityIdentifier[] TrustedFor(SecurityIdentifier user, SecurityIdentifier tokenOwner) => new[] { TokenOwner.Of(user, tokenOwner) };

    /// <summary>
    /// Makes a pipe the test serves look like a normal user's (a squatter for the product's SYSTEM/Administrators rule) also
    /// when the test runs elevated: owned by the user SID, which every token may name as owner.
    /// </summary>
    public static PipeSecurity UserOwned(PipeSecurity security)
    {
        security.SetOwner(User);
        return security;
    }
}
