using Glance.Core.Media;
using Glance.Core.Security;
using Glance.Core.Setup;

namespace Glance.Tests;

/// <summary>
/// The cases from the old FfmpegTrust tests (review finding H3) that FolderTrust's own tests did not already cover,
/// now aimed at <see cref="FolderTrust"/>, the one trust engine. The facts mirror what Windows 11 really has on C:\,
/// Program Files and a machine-scope winget package.
/// </summary>
public class FfmpegFolderTrustTests
{
    private const string Exe = @"C:\Program Files\WinGet\Packages\Gyan.FFmpeg_x\bin\ffmpeg.exe";
    private const string Admins = FolderTrust.Administrators, System = FolderTrust.LocalSystem, TrustedInstaller = FolderTrust.TrustedInstaller;
    private const string Users = "S-1-5-32-545", AuthUsers = "S-1-5-11", Interactive = "S-1-5-4";
    private const string SomeDomainGroup = "S-1-5-21-4444444444-5555555555-6666666666-513";

    private const int FullControl = 0x1F01FF, Modify = 0x1301BF, ReadAndExecute = 0x1200A9;

    private static AclEntry Allow(string sid, int rights, bool inheritOnly = false) => new(sid, true, rights, inheritOnly);

    private static List<AclEntry> AdminOnly() => new()
    {
        Allow(TrustedInstaller, FullControl), Allow(System, FullControl), Allow(Admins, FullControl),
        Allow(Users, ReadAndExecute), Allow("S-1-15-2-1", ReadAndExecute),
        Allow(FolderTrust.CreatorOwner, FolderTrust.GenericAll, inheritOnly: true),
        Allow(Users, unchecked((int)0xA0000000), inheritOnly: true),
    };

    /// <summary>C:\ as Windows 11 sets it up, including "Authenticated Users may create folders" and an inherit-only Modify.</summary>
    private static PathSecurity Root() => new(@"C:\", TrustRole.Ancestor, TrustedInstaller, new List<AclEntry>
    {
        Allow(AuthUsers, FolderTrust.AppendData),
        Allow(AuthUsers, unchecked((int)0xE0010000), inheritOnly: true),
        Allow(System, FullControl), Allow(Admins, FullControl), Allow(Users, ReadAndExecute),
    });

    /// <summary>ffmpeg.exe in a machine-scope winget package, its bin folder, and every folder up to C:\, all admin-only.</summary>
    private static List<PathSecurity> WingetMachineChain() => new()
    {
        new(Exe, TrustRole.File, Admins, AdminOnly()),
        new(@"C:\Program Files\WinGet\Packages\Gyan.FFmpeg_x\bin", TrustRole.Folder, Admins, AdminOnly()),
        new(@"C:\Program Files\WinGet\Packages\Gyan.FFmpeg_x", TrustRole.Ancestor, Admins, AdminOnly()),
        new(@"C:\Program Files\WinGet\Packages", TrustRole.Ancestor, Admins, AdminOnly()),
        new(@"C:\Program Files\WinGet", TrustRole.Ancestor, Admins, AdminOnly()),
        new(@"C:\Program Files", TrustRole.Ancestor, TrustedInstaller, AdminOnly()),
        Root(),
    };

    private static TrustVerdict Eval(List<PathSecurity> chain, string exe = Exe) => FolderTrust.Evaluate(exe, targetIsFile: true, onFixedDrive: true, chain);

    private static List<PathSecurity> With(int index, Func<PathSecurity, PathSecurity> change)
    {
        var chain = WingetMachineChain();
        chain[index] = change(chain[index]);
        return chain;
    }

    private static PathSecurity Add(PathSecurity o, AclEntry e) => o with { Entries = o.Entries!.Append(e).ToList() };

    [Fact]
    public void A_machine_scope_winget_package_under_the_real_C_root_is_trusted()
    {
        var v = Eval(WingetMachineChain());
        Assert.True(v.Trusted, v.Reason);
    }

    [Fact]
    public void A_user_scope_winget_package_in_LocalAppData_is_refused()
    {
        const string exe = @"C:\Users\me\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_x\bin\ffmpeg.exe";
        const string me = "S-1-5-21-1111111111-2222222222-3333333333-1001";
        List<AclEntry> mine() => new() { Allow(System, FullControl), Allow(Admins, FullControl), Allow(me, FullControl) };
        var chain = new List<PathSecurity> { new(exe, TrustRole.File, me, mine()), new(Path.GetDirectoryName(exe)!, TrustRole.Folder, me, mine()) };
        for (var d = Path.GetDirectoryName(Path.GetDirectoryName(exe)); d is not null && d.Length > 3; d = Path.GetDirectoryName(d))
            chain.Add(d.Length > @"C:\Users".Length ? new(d, TrustRole.Ancestor, me, mine()) : new(d, TrustRole.Ancestor, TrustedInstaller, AdminOnly()));
        chain.Add(Root());
        var v = FolderTrust.Evaluate(exe, true, true, chain);
        Assert.False(v.Trusted);
        Assert.Equal(me, v.PrincipalSid);
    }

    [Theory]
    [InlineData(Interactive, FolderTrust.WriteData)]
    [InlineData(Users, FolderTrust.GenericAll)]
    [InlineData(Users, Modify)]
    [InlineData(SomeDomainGroup, FolderTrust.WriteData)] // allow-list: any non-admin principal counts
    public void Any_non_admin_write_on_ffmpeg_exe_refuses_it(string sid, int rights)
    {
        var v = Eval(With(0, o => Add(o, Allow(sid, rights))));
        Assert.False(v.Trusted);
        Assert.Equal(sid, v.PrincipalSid);
    }

    [Theory]
    [InlineData(FolderTrust.WriteDac)]
    [InlineData(FolderTrust.WriteOwner)]
    [InlineData(FolderTrust.GenericWrite)]
    public void Changing_the_bin_folder_refuses_ffmpeg(int rights)
    {
        var v = Eval(With(1, o => Add(o, Allow(Users, rights))));
        Assert.False(v.Trusted);
        Assert.Contains(@"\bin", v.Reason);
    }

    [Theory]
    [InlineData(FolderTrust.Delete)]
    [InlineData(FolderTrust.WriteDac)]
    [InlineData(FolderTrust.WriteOwner)]
    public void Replace_rights_on_a_folder_further_up_refuse_ffmpeg(int rights) =>
        Assert.False(Eval(With(4, o => Add(o, Allow(AuthUsers, rights)))).Trusted);

    [Theory]
    [InlineData(FolderTrust.WriteData)]
    [InlineData(FolderTrust.AppendData)]
    [InlineData(FolderTrust.GenericWrite)]
    public void Creating_new_entries_further_up_is_harmless(int rights)
    {
        var v = Eval(With(3, o => Add(o, Allow(AuthUsers, rights))));
        Assert.True(v.Trusted, v.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void A_non_admin_owner_anywhere_on_the_path_refuses_ffmpeg(int index) =>
        Assert.False(Eval(With(index, o => o with { OwnerSid = "S-1-5-21-1-2-3-1001" })).Trusted);

    // N2 from docs/reviews/review-ffmpeg-elevation.md, now for the one engine
    [Theory]
    [InlineData(FolderTrust.CreatorOwner)]
    [InlineData(FolderTrust.OwnerRights)]
    public void Placeholder_principals_are_not_accepted_as_owners(string owner) =>
        Assert.False(Eval(With(0, o => o with { OwnerSid = owner })).Trusted);

    [Fact]
    public void The_winget_Links_symlink_is_refused_but_its_target_folder_is_not_affected()
    {
        // Links\ffmpeg.exe is a symlink: refused, so the lookup goes on to the real file under Packages
        const string link = @"C:\Program Files\WinGet\Links\ffmpeg.exe";
        var chain = new List<PathSecurity>
        {
            new(link, TrustRole.File, Admins, AdminOnly(), IsLink: true),
            new(@"C:\Program Files\WinGet\Links", TrustRole.Folder, Admins, AdminOnly()),
            new(@"C:\Program Files\WinGet", TrustRole.Ancestor, Admins, AdminOnly()),
            new(@"C:\Program Files", TrustRole.Ancestor, TrustedInstaller, AdminOnly()),
            Root(),
        };
        Assert.False(FolderTrust.Evaluate(link, true, true, chain).Trusted);
        Assert.True(Eval(WingetMachineChain()).Trusted);
    }

    // ---- the real reader, through the exact check Ffmpeg uses ------------------------------------------------------

    [StockSystemDriveFact]
    public void Ffmpeg_trust_accepts_an_admin_only_program_on_this_pc()
    {
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (!File.Exists(dotnet)) return; // no machine-wide .NET here: nothing to compare with
        var v = Ffmpeg.Trust(dotnet);
        Assert.True(v.Trusted, v.Reason);
    }

    [Fact]
    public void Ffmpeg_trust_refuses_an_exe_in_the_users_temp_folder_on_this_pc()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("glance-ffmpeg-trust-");
        try
        {
            var exe = Path.Combine(dir.FullName, "ffmpeg.exe");
            File.WriteAllBytes(exe, new byte[] { 0x4D, 0x5A });
            var v = Ffmpeg.Trust(exe);
            Assert.False(v.Trusted);
            Assert.False(string.IsNullOrEmpty(v.Reason));
        }
        finally { dir.Delete(true); }
    }
}

public class FfmpegSetupCheckTests
{
    [Fact]
    public void Ffmpeg_the_runner_can_use_is_ok()
    {
        var r = SetupChecks.DiagnoseFfmpeg(@"C:\Program Files\x\ffmpeg.exe", @"C:\Program Files\x\ffmpeg.exe");
        Assert.Equal(CheckStatus.Ok, r.Status);
        Assert.Equal(FixKind.None, r.Fix);
    }

    [Fact]
    public void Ffmpeg_only_in_a_user_folder_warns_that_the_runner_will_not_use_it()
    {
        const string path = @"C:\Users\me\AppData\Local\Microsoft\WinGet\Links\ffmpeg.exe";
        var r = SetupChecks.DiagnoseFfmpeg(path, null);
        Assert.Equal(CheckStatus.Warning, r.Status);
        Assert.Contains(path, r.Detail);
        Assert.Contains("runner", r.Detail);
        Assert.Equal((FixKind.WingetInstallMachine, SetupChecks.FfmpegId), (r.Fix, r.FixArg));
        Assert.False(SetupChecks.NeedsAttention(new[] { r })); // video is optional: never a blocking problem
    }

    [Fact]
    public void Missing_ffmpeg_offers_an_all_users_install()
    {
        var r = SetupChecks.DiagnoseFfmpeg(null, null);
        Assert.Equal(CheckStatus.Info, r.Status);
        Assert.Equal((FixKind.WingetInstallMachine, SetupChecks.FfmpegId), (r.Fix, r.FixArg));
    }

    [Fact]
    public void Machine_scope_install_runs_winget_elevated_for_all_users()
    {
        // the script itself is checked in FfmpegReviewSetupTests
        var psi = SetupChecks.WingetInstallStartInfo(SetupChecks.FfmpegId, machineScope: true);
        Assert.EndsWith(@"\powershell.exe", psi.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-EncodedCommand", psi.Arguments);
        Assert.Equal("runas", psi.Verb);
        Assert.True(psi.UseShellExecute); // runas only works through the shell
        Assert.Contains("--scope machine", SetupChecks.MachineInstallScript(SetupChecks.FfmpegId));
        Assert.Throws<ArgumentException>(() => SetupChecks.MachineInstallScript("x'; Remove-Item C:\\ #"));
    }

    [Fact]
    public void User_scope_install_is_unchanged()
    {
        var psi = SetupChecks.WingetInstallStartInfo(SetupChecks.ZadigId, machineScope: false);
        Assert.DoesNotContain("--scope", psi.Arguments);
        Assert.False(psi.UseShellExecute);
        Assert.True(string.IsNullOrEmpty(psi.Verb));
    }
}

/// <summary>
/// A fact that reads real permissions on the system drive. It is skipped, with the reason, on a machine whose drive
/// root is set up unusually (for example a build image that lets ordinary users change C:\), because then "an admin-only
/// program is trusted" is not expected to hold and a failure would say nothing about Glance (review L6).
/// </summary>
public sealed class StockSystemDriveFactAttribute : FactAttribute
{
    public StockSystemDriveFactAttribute()
    {
        if (RealAcl.SkipReason() is { } why) Skip = why;
    }
}

internal static class RealAcl
{
    public static string? SkipReason()
    {
        if (!OperatingSystem.IsWindows()) return "Needs Windows: it reads NTFS permissions.";
        string root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? @"C:\";
        try { return SkipReason(FolderTrust.ReadObject(root, TrustRole.Ancestor)); }
        catch (Exception ex) { return $"Could not read the permissions of {root}: {ex.Message}"; }
    }

    /// <summary>Null when the drive root only lets administrators replace what is under it (stock Windows), else why not.</summary>
    public static string? SkipReason(PathSecurity root)
    {
        var v = FolderTrust.Decide(new[] { root with { Role = TrustRole.Ancestor } });
        return v.Trusted ? null : $"The drive root {root.Path} is set up unusually ({v.Reason}), so admin-only programs are not expected to pass here.";
    }
}

public class RealAclSkipTests
{
    private static PathSecurity Root(params AclEntry[] extra) => new(@"C:\", TrustRole.Ancestor, FolderTrust.TrustedInstaller, new List<AclEntry>
    {
        new("S-1-5-11", true, FolderTrust.AppendData),                  // Authenticated Users: create folders (stock)
        new("S-1-5-11", true, unchecked((int)0xE0010000), true),       // inherit-only modify (stock)
        new(FolderTrust.LocalSystem, true, 0x1F01FF), new(FolderTrust.Administrators, true, 0x1F01FF),
        new("S-1-5-32-545", true, 0x1200A9),
    }.Concat(extra).ToList());

    [Fact]
    public void A_stock_drive_root_runs_the_real_acl_tests() => Assert.Null(RealAcl.SkipReason(Root()));

    [Fact]
    public void A_drive_root_users_can_change_skips_them_with_a_reason()
    {
        var why = RealAcl.SkipReason(Root(new AclEntry("S-1-5-11", true, FolderTrust.WriteDac))); // Authenticated Users: change permissions
        Assert.NotNull(why);
        Assert.Contains("unusual", why);
    }

    [Fact]
    public void The_default_finder_follows_this_process_elevation() =>
        Assert.Equal(Environment.IsPrivilegedProcess, Ffmpeg.Default.IsElevated);
}

/// <summary>The H3 scenario end to end, with the real candidate list and the real FolderTrust check (Windows only).</summary>
public class FfmpegH3RegressionTests
{
    [Fact]
    public void A_planted_ffmpeg_ahead_of_everything_is_refused_when_elevated_and_still_used_by_the_editor()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("glance-h3-");
        try
        {
            // any program running as the user can do this
            var planted = Path.Combine(dir.FullName, "ffmpeg.exe");
            File.WriteAllBytes(planted, new byte[] { 0x4D, 0x5A });
            IEnumerable<string> candidates() => new[] { planted }.Concat(Ffmpeg.Candidates());

            var runner = new FfmpegFinder(() => true, candidates, File.Exists, Ffmpeg.Trust);
            var log = new List<string>();
            runner.Rejected += log.Add;
            var used = runner.Find();
            Assert.NotEqual(planted, used, StringComparer.OrdinalIgnoreCase);
            if (used is null) Assert.Contains(log, m => m.Contains(planted, StringComparison.OrdinalIgnoreCase));

            var editor = new FfmpegFinder(() => false, candidates, File.Exists, _ => throw new InvalidOperationException("not elevated: no check"));
            Assert.Equal(planted, editor.Find());
        }
        finally { dir.Delete(true); }
    }
}
