using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Glance.Core.Security;
using Glance.Core.Service;

namespace Glance.Tests;

/// <summary>
/// Bug: 0.2.0's CI run failed 46 tests that pass on a developer's PC. GitHub's runner runs the tests as an elevated
/// administrator, and Windows then makes BUILTIN\Administrators (the token's default owner, TokenOwner) the owner of every
/// pipe the test servers create, while the tests trusted only the user SID ("The pipe is served by BUILTIN\Administrators,
/// not by the Glance service"). Test squatters, in turn, got the Administrators owner that the product rightly trusts.
/// A normal token cannot name Administrators as owner, so these tests fake the elevated token where they must, and check
/// with real pipes that TokenOwner is what Windows gives (docs/design/fix-elevated-pipe-owner.md).
/// </summary>
public sealed class ElevatedPipeOwnerTests
{
    private static readonly SecurityIdentifier Admins = ServicePipeSecurity.Administrators;
    private static string Name() => "glance-test-owner-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void Under_an_elevated_token_the_tests_trust_the_Administrators_owner_their_own_pipes_get()
    {
        // GitHub's runner: user runneradmin, TokenOwner BUILTIN\Administrators
        var pipeOwner = TokenOwner.Of(TestPipes.User, Admins);   // what Windows gives a pipe the test server creates
        Assert.Equal(Admins, pipeOwner);
        Assert.True(PipeServerCheck.IsTrustedOwner(pipeOwner, TestPipes.TrustedFor(TestPipes.User, Admins)),
            "a test client refuses the test's own server when the tests run elevated");
    }

    [Fact]
    public void Under_a_normal_token_the_tests_trust_only_the_user()
    {
        var trusted = TestPipes.TrustedFor(TestPipes.User, TestPipes.User);
        Assert.Equal(new[] { TestPipes.User }, trusted);
        Assert.DoesNotContain(Admins, trusted);
        Assert.Equal(new[] { Admins }, TestPipes.TrustedFor(TestPipes.User, Admins));   // elevated: exactly that owner, nothing more
    }

    [Fact]
    public void Token_owner_falls_back_to_the_user_only_when_the_token_names_none()
    {
        Assert.Equal(TestPipes.User, TokenOwner.Of(TestPipes.User, null));
        Assert.Equal(Admins, TokenOwner.Of(TestPipes.User, Admins));
    }

    [Fact]
    public void A_pipe_this_process_creates_is_owned_by_its_token_owner_and_the_tests_trust_it()
    {
        // locally TokenOwner is the user; on an elevated runner it is Administrators: either way it must match the real pipe
        using var plain = new NamedPipeServerStream(Name(), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Assert.Equal(TokenOwner.Current(), PipeServerCheck.OwnerOf(plain));

        using var glance = ServicePipeFactory.Create(Name(), PipeDirection.InOut, 1, ServicePipeSecurity.Build(TestPipes.User), first: true);
        var owner = PipeServerCheck.OwnerOf(glance);
        Assert.Equal(TokenOwner.Current(), owner);
        Assert.True(PipeServerCheck.IsTrustedOwner(owner, TestPipes.Own));
    }

    [Fact]
    public void A_test_squatter_names_the_user_as_owner_so_an_elevated_run_cannot_make_it_look_like_the_service()
    {
        var security = TestPipes.UserOwned(ServicePipeSecurity.Build(TestPipes.User));
        Assert.Equal(TestPipes.User, security.GetOwner(typeof(SecurityIdentifier)));   // explicit, not the token's default owner

        using var squatter = NamedPipeServerStreamAcl.Create(Name(), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        var owner = PipeServerCheck.OwnerOf(squatter);
        Assert.Equal(TestPipes.User, owner);
        Assert.False(PipeServerCheck.IsTrustedOwner(owner, PipeServerCheck.ServiceOwners));   // the product's rule still refuses it
    }
}
