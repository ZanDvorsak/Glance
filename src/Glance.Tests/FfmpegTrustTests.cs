using Glance.Core.Media;
using Glance.Core.Setup;

namespace Glance.Tests;

/// <summary>
/// The rule that decides whether the elevated runner may start an ffmpeg.exe (review finding H3). The ACL facts below
/// mirror what Windows 11 really has on C:\ and C:\Program Files.
/// </summary>
public class FfmpegTrustTests
{
    private const string Exe = @"C:\Program Files\WinGet\Packages\Gyan.FFmpeg\bin\ffmpeg.exe";
    private const string Admins = FfmpegTrust.AdministratorsSid, System = FfmpegTrust.SystemSid, TrustedInstaller = FfmpegTrust.TrustedInstallerSid;
    private const string Users = "S-1-5-32-545", AuthUsers = "S-1-5-11", Everyone = "S-1-1-0", Interactive = "S-1-5-4";
    private const string Me = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string SomeDomainGroup = "S-1-5-21-4444444444-5555555555-6666666666-513";

    // rights masks as Windows reports them
    private const int FullControl = 0x1F01FF, Modify = 0x1301BF, ReadAndExecute = 0x1200A9;
    private const int WriteData = 0x2, AppendData = 0x4, DeleteChild = 0x40, Delete = 0x10000, WriteDac = 0x40000, WriteOwner = 0x80000;
    private const int GenericAll = 0x10000000, GenericWrite = 0x40000000;
    private const int GenericReadWriteExecuteDelete = unchecked((int)0xE0010000); // C:\'s inherit-only entry for Authenticated Users

    private static AceFact Allow(string sid, int rights, bool inheritOnly = false) => new(sid, true, rights, inheritOnly);

    /// <summary>The usual admin entries everywhere under Program Files.</summary>
    private static List<AceFact> AdminOnly() => new()
    {
        Allow(TrustedInstaller, FullControl), Allow(System, FullControl), Allow(Admins, FullControl),
        Allow(Users, ReadAndExecute), Allow("S-1-15-2-1", ReadAndExecute),   // ALL APPLICATION PACKAGES
        Allow("S-1-3-0", GenericAll, inheritOnly: true),                    // CREATOR OWNER
        Allow(Users, unchecked((int)0xA0000000), inheritOnly: true),         // generic read + execute for children
    };

    private static PathFact Root() => new(@"C:\", TrustedInstaller, new List<AceFact>
    {
        Allow(AuthUsers, AppendData),                                        // "create folders", this folder only
        Allow(AuthUsers, GenericReadWriteExecuteDelete, inheritOnly: true),
        Allow(System, FullControl), Allow(Admins, FullControl), Allow(Users, ReadAndExecute),
    });

    /// <summary>ffmpeg.exe, its bin folder, the package folder ... up to C:\, all admin-only.</summary>
    private static List<PathFact> GoodChain() => new()
    {
        new(Exe, Admins, AdminOnly()),
        new(@"C:\Program Files\WinGet\Packages\Gyan.FFmpeg\bin", Admins, AdminOnly()),
        new(@"C:\Program Files\WinGet\Packages\Gyan.FFmpeg", Admins, AdminOnly()),
        new(@"C:\Program Files\WinGet\Packages", Admins, AdminOnly()),
        new(@"C:\Program Files\WinGet", Admins, AdminOnly()),
        new(@"C:\Program Files", TrustedInstaller, AdminOnly()),
        Root(),
    };

    private static List<PathFact> With(int index, Func<PathFact, PathFact> change)
    {
        var chain = GoodChain();
        chain[index] = change(chain[index]);
        return chain;
    }

    private static PathFact AddAce(PathFact f, AceFact ace) => f with { Aces = f.Aces.Append(ace).ToList() };

    private static TrustVerdict Eval(List<PathFact> chain) => FfmpegTrust.Evaluate(chain[0].Path, chain);

    [Fact]
    public void Admin_only_install_under_Program_Files_is_trusted()
    {
        var v = Eval(GoodChain());
        Assert.True(v.Trusted, v.Reason);
    }

    [Fact]
    public void User_scope_winget_install_in_LocalAppData_is_rejected()
    {
        const string exe = @"C:\Users\me\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg\bin\ffmpeg.exe";
        List<AceFact> mine() => new() { Allow(System, FullControl), Allow(Admins, FullControl), Allow(Me, FullControl) };
        var chain = new List<PathFact> { new(exe, Me, mine()) };
        for (var d = Path.GetDirectoryName(exe); d is not null && d.Length > 3; d = Path.GetDirectoryName(d))
            chain.Add(new(d, d.Length > @"C:\Users".Length ? Me : TrustedInstaller, d.Length > @"C:\Users".Length ? mine() : AdminOnly()));
        chain.Add(Root());

        var v = FfmpegTrust.Evaluate(exe, chain);
        Assert.False(v.Trusted);
    }

    [Theory]
    [InlineData(Users, WriteData)]
    [InlineData(AuthUsers, AppendData)]
    [InlineData(Everyone, Delete)]
    [InlineData(Interactive, WriteDac)]
    [InlineData(Me, WriteOwner)]
    [InlineData(Users, GenericWrite)]
    [InlineData(Users, GenericAll)]
    [InlineData(Users, Modify)]
    [InlineData(SomeDomainGroup, WriteData)]   // allow-list: any non-admin principal counts, not just the well-known groups
    public void Any_non_admin_write_on_the_file_rejects_it(string sid, int rights)
    {
        var v = Eval(With(0, f => AddAce(f, Allow(sid, rights))));
        Assert.False(v.Trusted);
        Assert.Contains("the file", v.Reason);
    }

    [Theory]
    [InlineData(WriteData)]    // create files: plant a DLL next to ffmpeg.exe
    [InlineData(AppendData)]   // create folders
    [InlineData(DeleteChild)]  // delete or rename ffmpeg.exe
    [InlineData(Delete)]
    [InlineData(WriteDac)]
    [InlineData(GenericWrite)]
    public void Writes_in_the_folder_holding_ffmpeg_reject_it(int rights)
    {
        var v = Eval(With(1, f => AddAce(f, Allow(Users, rights))));
        Assert.False(v.Trusted);
        Assert.Contains(@"\bin", v.Reason);
    }

    [Theory]
    [InlineData(DeleteChild)]
    [InlineData(Delete)]
    [InlineData(WriteDac)]
    [InlineData(WriteOwner)]
    [InlineData(GenericAll)]
    public void Replace_rights_on_a_folder_further_up_reject_it(int rights)
    {
        var v = Eval(With(4, f => AddAce(f, Allow(AuthUsers, rights))));
        Assert.False(v.Trusted);
    }

    [Theory]
    [InlineData(WriteData)]
    [InlineData(AppendData)]
    [InlineData(GenericWrite)]
    public void Creating_new_entries_further_up_is_harmless(int rights)
    {
        // C:\ itself lets Authenticated Users create folders; that cannot replace anything that already exists
        var v = Eval(With(3, f => AddAce(f, Allow(AuthUsers, rights))));
        Assert.True(v.Trusted, v.Reason);
    }

    [Fact]
    public void Inherit_only_entries_do_not_apply_to_the_object_itself()
    {
        var v = Eval(With(0, f => AddAce(f, Allow(Users, FullControl, inheritOnly: true))));
        Assert.True(v.Trusted, v.Reason);
    }

    [Fact]
    public void Deny_entries_never_make_a_writable_file_trusted()
    {
        var chain = With(0, f => f with { Aces = f.Aces.Append(new AceFact(Users, false, FullControl)).Append(Allow(Users, WriteData)).ToList() });
        Assert.False(Eval(chain).Trusted);
    }

    [Fact]
    public void Admin_principals_may_have_full_control()
    {
        var chain = With(0, f => AddAce(AddAce(f, Allow(TrustedInstaller, FullControl)), Allow(System, GenericAll)));
        Assert.True(Eval(chain).Trusted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    public void A_non_admin_owner_anywhere_on_the_path_rejects_it(int index)
    {
        var v = Eval(With(index, f => f with { OwnerSid = Me }));
        Assert.False(v.Trusted);
        Assert.Contains("owned", v.Reason);
    }

    [Fact]
    public void An_unknown_owner_rejects_it() => Assert.False(Eval(With(0, f => f with { OwnerSid = null })).Trusted);

    [Theory]
    [InlineData(0)]   // the Links\ffmpeg.exe symlink: only the real file under Packages is accepted
    [InlineData(2)]   // a junction on the way could be pointed somewhere else
    public void Links_and_junctions_on_the_path_reject_it(int index)
    {
        var v = Eval(With(index, f => f with { IsReparsePoint = true }));
        Assert.False(v.Trusted);
        Assert.Contains("link", v.Reason);
    }

    [Theory]
    [InlineData(@"\\server\share\ffmpeg.exe")]
    [InlineData(@"ffmpeg\ffmpeg.exe")]
    [InlineData(@"\\?\C:\Program Files\ffmpeg.exe")]
    public void Network_and_relative_paths_are_rejected(string path)
    {
        var chain = GoodChain();
        chain[0] = chain[0] with { Path = path };
        Assert.False(FfmpegTrust.Evaluate(path, chain).Trusted);
    }

    [Fact]
    public void An_incomplete_chain_is_rejected()
    {
        var chain = GoodChain();
        chain.RemoveAt(chain.Count - 1); // C:\ missing: the caller did not read every folder
        Assert.False(Eval(chain).Trusted);
        Assert.False(FfmpegTrust.Evaluate(Exe, new List<PathFact> { GoodChain()[0] }).Trusted);
        Assert.False(FfmpegTrust.Evaluate(@"C:\other\ffmpeg.exe", GoodChain()).Trusted); // chain is for a different file
    }

    // ---- the real reader, against folders every Windows PC has --------------------------------------------------

    [Fact]
    public void A_system_exe_is_trusted_on_this_pc()
    {
        if (!OperatingSystem.IsWindows()) return;
        var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var v = FfmpegTrust.Check(cmd);
        Assert.True(v.Trusted, v.Reason);
    }

    [Fact]
    public void A_file_in_the_users_temp_folder_is_rejected_on_this_pc()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("glance-ffmpeg-trust-");
        try
        {
            var exe = Path.Combine(dir.FullName, "ffmpeg.exe");
            File.WriteAllBytes(exe, new byte[] { 0x4D, 0x5A });
            var v = FfmpegTrust.Check(exe);
            Assert.False(v.Trusted);
            Assert.NotEmpty(v.Reason);
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
        var psi = SetupChecks.WingetInstallStartInfo(SetupChecks.FfmpegId, machineScope: true);
        Assert.Equal("winget", psi.FileName);
        Assert.Contains("--id Gyan.FFmpeg", psi.Arguments);
        Assert.Contains("--scope machine", psi.Arguments);
        Assert.Equal("runas", psi.Verb);
        Assert.True(psi.UseShellExecute); // runas only works through the shell
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
