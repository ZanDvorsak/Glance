using System.Collections;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Xml.Linq;
using Glance.Core.Security;
using Glance.Core.Setup;

namespace Glance.Tests;

/// <summary>Runs only on Windows and only when <paramref name="path"/> exists; otherwise the test is skipped with a reason.</summary>
public sealed class WindowsPathFactAttribute : FactAttribute
{
    public WindowsPathFactAttribute(Environment.SpecialFolder root, string relative)
    {
        if (!OperatingSystem.IsWindows()) { Skip = "Windows only."; return; }
        var path = Path.Combine(Environment.GetFolderPath(root), relative);
        if (!File.Exists(path) && !Directory.Exists(path)) Skip = $"{path} does not exist on this machine.";
    }
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Windows only."; }
}

/// <summary>Tests for the findings of docs/reviews/review-autostart-task.md, grouped by finding. The Windows-only ones skip elsewhere.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class AutostartReviewFixTests
{
    private const string Ana = "S-1-5-21-1-2-3-1001", Bob = "S-1-5-21-1-2-3-1002";
    private const string Users = "S-1-5-32-545", AuthUsers = "S-1-5-11";
    private const int FullControl = 0x1F01FF, Modify = 0x1301BF, ReadExecute = 0x1200A9;
    private const string Exe = @"C:\Program Files\Glance\Glance.Runner.exe";

    // ---------------------------------------------------------------- H1: the user's environment reaches the runner

    [Fact]
    public void H1_profiler_startup_hook_and_diagnostics_variables_are_reported()
    {
        var env = new Hashtable
        {
            ["CORECLR_ENABLE_PROFILING"] = "1", ["CORECLR_PROFILER_PATH"] = @"C:\x.dll", ["cor_enable_profiling"] = "1",
            ["DOTNET_STARTUP_HOOKS"] = @"C:\h.dll", ["DOTNET_DiagnosticPorts"] = "p", ["COMPlus_EnableEventPipe"] = "1",
            ["DOTNET_ROOT"] = @"C:\dotnet", ["DOTNET_ADDITIONAL_DEPS"] = "x",
            ["PATH"] = @"C:\Windows", ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_NOLOGO"] = "1", ["CORE_X"] = "1",
        };
        var risky = RunnerEnvironment.FindRiskyVariables(env);
        foreach (var name in new[] { "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER_PATH", "cor_enable_profiling", "DOTNET_STARTUP_HOOKS",
                                     "DOTNET_DiagnosticPorts", "COMPlus_EnableEventPipe", "DOTNET_ROOT", "DOTNET_ADDITIONAL_DEPS" })
            Assert.Contains(name, risky);
        foreach (var name in new[] { "PATH", "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_NOLOGO", "CORE_X" })
            Assert.DoesNotContain(name, risky);
    }

    [Theory]
    [InlineData(@"src\Glance.Runner\Glance.Runner.csproj")]
    [InlineData(@"src\Glance.Service\Glance.Service.csproj")]   // Glance.SensorHost was removed (runner-service); the service keeps the rule
    public void H1_elevated_programs_turn_startup_hooks_off(string project)
    {
        var doc = XDocument.Load(Path.Combine(RepoRoot(), project));
        Assert.Contains(doc.Descendants("StartupHookSupport"), e => e.Value.Trim() == "false");
    }

    private static string RepoRoot()
    {
        for (var d = AppContext.BaseDirectory; d is not null; d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, "src", "Glance.sln"))) return d;
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    // ---------------------------------------------------------------- M3: one trust engine with the FfmpegTrust rules

    private static List<AclEntry> AdminOnly() => new()
    {
        new(FolderTrust.TrustedInstaller, true, FullControl), new(FolderTrust.LocalSystem, true, FullControl),
        new(FolderTrust.Administrators, true, FullControl), new(Users, true, ReadExecute),
        new(FolderTrust.CreatorOwner, true, FolderTrust.GenericAll, InheritOnly: true),
    };

    private static List<AclEntry> CRoot() => new()
    {
        new(AuthUsers, true, FolderTrust.AppendData), new(AuthUsers, true, unchecked((int)0xE0010000), InheritOnly: true),
        new(FolderTrust.LocalSystem, true, FullControl), new(FolderTrust.Administrators, true, FullControl), new(Users, true, ReadExecute),
    };

    /// <summary>The runner, its folder and every folder up to the drive root, all admin-only.</summary>
    private static List<PathSecurity> GoodExeChain(string exe = Exe)
    {
        var dir = Path.GetDirectoryName(exe)!;
        var list = new List<PathSecurity>
        {
            new(exe, TrustRole.File, FolderTrust.Administrators, AdminOnly()),
            new(dir, TrustRole.Folder, FolderTrust.Administrators, AdminOnly()),
        };
        for (var d = Path.GetDirectoryName(dir); d is not null; d = Path.GetDirectoryName(d))
            list.Add(new(d, TrustRole.Ancestor, FolderTrust.TrustedInstaller, d.Length == 3 ? CRoot() : AdminOnly()));
        return list;
    }

    private static TrustVerdict EvalExe(List<PathSecurity> chain, string exe = Exe, bool fixedDrive = true) =>
        FolderTrust.Evaluate(exe, targetIsFile: true, onFixedDrive: fixedDrive, chain);

    [Fact]
    public void M3_a_good_chain_is_trusted() => Assert.True(EvalExe(GoodExeChain()).Trusted, EvalExe(GoodExeChain()).Reason);

    [Theory]
    [InlineData(0)] // the exe is a link
    [InlineData(1)] // its folder is a junction
    [InlineData(2)] // C:\Program Files is a junction
    public void M3_links_and_junctions_anywhere_are_refused(int index)
    {
        var chain = GoodExeChain();
        chain[index] = chain[index] with { IsLink = true };
        var v = EvalExe(chain);
        Assert.False(v.Trusted);
        Assert.Contains("link", v.Reason);
    }

    [Fact]
    public void M3_a_link_inside_the_program_folder_is_refused()
    {
        var chain = GoodExeChain();
        chain.Add(new(@"C:\Program Files\Glance\runtimes", TrustRole.Folder, FolderTrust.Administrators, AdminOnly(), IsLink: true));
        Assert.False(EvalExe(chain).Trusted);
    }

    [Fact]
    public void M3_a_network_or_removable_drive_is_refused() => Assert.False(EvalExe(GoodExeChain(), fixedDrive: false).Trusted);

    [Theory]
    [InlineData(@"\\server\share\Glance\Glance.Runner.exe")]
    [InlineData(@"Glance\Glance.Runner.exe")]
    [InlineData(@"\\?\C:\Program Files\Glance\Glance.Runner.exe")]
    [InlineData(@"C:\Program Files\Glance\..\Glance\Glance.Runner.exe")]
    [InlineData(@"C:Glance.Runner.exe")]
    public void M3_network_relative_and_dot_dot_paths_are_refused(string exe)
    {
        var v = EvalExe(GoodExeChain(), exe);
        Assert.False(v.Trusted);
        Assert.NotNull(FolderTrust.PathProblem(exe));
    }

    [Fact]
    public void M3_an_incomplete_chain_is_refused()
    {
        var noRoot = GoodExeChain(); noRoot.RemoveAt(noRoot.Count - 1);
        Assert.False(EvalExe(noRoot).Trusted);

        var noMiddle = GoodExeChain(); noMiddle.RemoveAt(2); // C:\Program Files missing
        Assert.False(EvalExe(noMiddle).Trusted);

        var noFile = GoodExeChain(); noFile.RemoveAt(0);
        Assert.False(EvalExe(noFile).Trusted);

        Assert.False(EvalExe(GoodExeChain(), @"C:\Program Files\Other\Glance.Runner.exe").Trusted); // facts for another file
    }

    [Fact]
    public void M3_a_folder_target_needs_its_own_facts_and_every_ancestor()
    {
        var folder = Path.GetDirectoryName(Exe)!;
        var chain = GoodExeChain().Skip(1).ToList(); // folder + ancestors
        Assert.True(FolderTrust.Evaluate(folder, targetIsFile: false, onFixedDrive: true, chain).Trusted);
        Assert.False(FolderTrust.Evaluate(folder, targetIsFile: false, onFixedDrive: true, chain.Take(2)).Trusted);
    }

    [Fact]
    public void M3_PathProblem_accepts_ordinary_local_paths()
    {
        Assert.Null(FolderTrust.PathProblem(Exe));
        Assert.Null(FolderTrust.PathProblem(@"D:\Apps\Glance"));
        Assert.Null(FolderTrust.PathProblem(@"C:\"));
    }

    // ---------------------------------------------------------------- L2: turning auto-start off never elevates the runner

    [Fact]
    public void L2_removal_runs_schtasks_with_a_fixed_command_line()
    {
        var psi = AutostartTask.RemoveStartInfo(); // it takes no runner path at all
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"), psi.FileName, ignoreCase: true);
        Assert.Equal("/Delete /TN \"Glance Runner\" /F", psi.Arguments);
        Assert.Equal("runas", psi.Verb);
        Assert.DoesNotContain("Glance.Runner", psi.FileName + psi.Arguments + psi.WorkingDirectory);
    }

    // ---------------------------------------------------------------- L3: a data drive's root

    /// <summary>D:\ as Windows formats it: Authenticated Users have Modify on the root itself (icacls D:\ on a real PC).</summary>
    private static List<AclEntry> DRoot() => new()
    {
        new(FolderTrust.Administrators, true, FullControl), new(FolderTrust.LocalSystem, true, FullControl),
        new(AuthUsers, true, Modify), new(AuthUsers, true, unchecked((int)0xE0010000), InheritOnly: true),
        new(Users, true, ReadExecute),
    };

    [Fact]
    public void L3_a_locked_down_folder_on_a_data_drive_is_trusted()
    {
        const string exe = @"D:\Program Files\Glance\Glance.Runner.exe";
        var chain = GoodExeChain(exe);
        chain[^1] = new(@"D:\", TrustRole.Ancestor, FolderTrust.Administrators, DRoot());
        var v = EvalExe(chain, exe);
        Assert.True(v.Trusted, v.Reason);
    }

    [Fact]
    public void L3_the_root_exception_is_only_for_delete_on_the_root_itself()
    {
        const string exe = @"D:\Program Files\Glance\Glance.Runner.exe";
        var deleteChild = GoodExeChain(exe);
        deleteChild[^1] = new(@"D:\", TrustRole.Ancestor, FolderTrust.Administrators, DRoot().Append(new(AuthUsers, true, FolderTrust.DeleteChild)).ToList());
        Assert.False(EvalExe(deleteChild, exe).Trusted);

        var writeDac = GoodExeChain(exe);
        writeDac[^1] = new(@"D:\", TrustRole.Ancestor, FolderTrust.Administrators, DRoot().Append(new(AuthUsers, true, FolderTrust.WriteDac)).ToList());
        Assert.False(EvalExe(writeDac, exe).Trusted);

        var deleteMiddle = GoodExeChain(exe); // D:\Program Files with Modify for Authenticated Users
        deleteMiddle[2] = deleteMiddle[2] with { Entries = AdminOnly().Append(new(AuthUsers, true, Modify)).ToList() };
        Assert.False(EvalExe(deleteMiddle, exe).Trusted);
    }

    // ---------------------------------------------------------------- test gaps: the real reader and command line

    [Theory]
    [InlineData("O:BAD:PAI(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1200a9;;;BU)", true)]
    [InlineData("O:BAD:PAI(A;;FA;;;SY)(A;;FA;;;BA)(A;OICIIO;FA;;;BU)", true)]     // inherit-only: not for the file itself
    [InlineData("O:BAD:(A;;FA;;;SY)(A;;0x1301bf;;;BU)", false)]                  // Users: Modify
    [InlineData("O:S-1-5-21-1-2-3-1001D:(A;;FA;;;SY)(A;;FA;;;BA)", false)]        // owned by a user
    [InlineData("O:BAD:(A;;FA;;;SY)(A;;FA;;;BA)(D;;FA;;;BU)(A;;0x2;;;BU)", false)] // a deny does not cancel an allow
    public void Reader_turns_a_security_descriptor_into_the_right_facts(string sddl, bool trusted)
    {
        var o = FolderTrust.FromDescriptor(Exe, TrustRole.File, new RawSecurityDescriptor(sddl));
        Assert.Equal(trusted, FolderTrust.Decide(new[] { o }).Trusted);
    }

    [WindowsPathFact(Environment.SpecialFolder.ProgramFiles, @"dotnet\host")]
    public void Reader_trusts_an_admin_only_folder_on_this_pc()
    {
        var v = FolderTrust.CheckFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"dotnet\host"));
        Assert.True(v.Trusted, v.Reason);
    }

    [WindowsPathFact(Environment.SpecialFolder.System, "cmd.exe")]
    public void Reader_trusts_a_system_exe_on_this_pc()
    {
        var v = FolderTrust.CheckExecutable(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), TrustScope.ExecutableOnly);
        Assert.True(v.Trusted, v.Reason);
    }

    [WindowsFact]
    public void Reader_marks_a_junction_as_a_link()
    {
        // A junction (mklink /J) needs no privilege, unlike a symbolic link.
        var dir = Directory.CreateTempSubdirectory("glance-link-").FullName;
        try
        {
            var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
            var link = Path.Combine(dir, "junction");
            using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), $"/d /c mklink /J \"{link}\" \"{target}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            { p.StandardOutput.ReadToEnd(); p.WaitForExit(); }
            Assert.True(Directory.Exists(link), "mklink /J failed");
            Assert.True(FolderTrust.ReadObject(link, TrustRole.Folder).IsLink);
            Assert.False(FolderTrust.ReadObject(target, TrustRole.Folder).IsLink);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
