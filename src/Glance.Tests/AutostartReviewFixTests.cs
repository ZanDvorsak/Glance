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

    private static readonly Func<string, string?> ToSid = n => n switch
    {
        @"PC\Ana" => Ana, @"PC\Bob" => Bob,
        _ when n.StartsWith("S-1-", StringComparison.Ordinal) => n,
        _ => null,
    };

    /// <summary>The task as Task Scheduler returns it (schtasks /Query /XML ONE on a real PC, account anonymised).</summary>
    private const string RealFormat = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Shows your Glance theme on the USB display and provides hardware sensors.</Description>
            <URI>\Glance Runner</URI>
          </RegistrationInfo>
          <Principals>
            <Principal id="Author">
              <UserId>S-1-5-21-1-2-3-1001</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <StartWhenAvailable>true</StartWhenAvailable>
            <IdleSettings>
              <StopOnIdleEnd>true</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
          </Settings>
          <Triggers>
            <LogonTrigger>
              <UserId>PC\Ana</UserId>
            </LogonTrigger>
          </Triggers>
          <Actions Context="Author">
            <Exec>
              <Command>C:\Program Files\Glance\Glance.Runner.exe</Command>
              <WorkingDirectory>C:\Program Files\Glance</WorkingDirectory>
            </Exec>
          </Actions>
        </Task>
        """;

    private static string Real(string from, string to) => RealFormat.Replace(from, to);

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

    // ---------------------------------------------------------------- M1: the existing task is checked

    private static TrustVerdict TrustProgramFiles(string exe) =>
        exe.StartsWith(@"C:\Program Files\", StringComparison.OrdinalIgnoreCase) ? TrustVerdict.Yes : TrustVerdict.No($"{exe} is writable.");

    [Fact]
    public void M1_a_task_pointing_at_a_writable_copy_is_flagged()
    {
        var old = Real(@"C:\Program Files\Glance", @"C:\Users\Ana\Downloads\Glance");
        var s = AutostartTask.Assess(true, old, Exe, TrustProgramFiles);
        Assert.Equal(AutostartState.Untrusted, s.State);
        Assert.Equal(@"C:\Users\Ana\Downloads\Glance\Glance.Runner.exe", s.Command);
        Assert.Contains(@"Downloads\Glance", s.Message);
    }

    [Fact]
    public void M1_a_task_pointing_at_another_trusted_copy_is_flagged()
    {
        var other = Real(@"C:\Program Files\Glance", @"C:\Program Files\Glance Old");
        var s = AutostartTask.Assess(true, other, Exe, TrustProgramFiles);
        Assert.Equal(AutostartState.OtherCopy, s.State);
        Assert.Contains("Glance Old", s.Message);
    }

    [Fact]
    public void M1_a_task_that_is_not_a_plain_runner_start_is_flagged()
    {
        var withArgs = RealFormat.Replace("</WorkingDirectory>", "</WorkingDirectory><Arguments>--x</Arguments>");
        Assert.Equal(AutostartState.NotGlance, AutostartTask.Assess(true, withArgs, Exe, TrustProgramFiles).State);
        Assert.Equal(AutostartState.NotGlance, AutostartTask.Assess(true, "<garbage", Exe, TrustProgramFiles).State);
        Assert.Equal(AutostartState.NotGlance, AutostartTask.Assess(true, null, Exe, TrustProgramFiles).State);
    }

    [Fact]
    public void M1_this_install_in_Task_Scheduler_format_is_fine_and_no_task_is_not_installed()
    {
        Assert.Equal(AutostartState.Ok, AutostartTask.Assess(true, RealFormat, Exe, TrustProgramFiles).State);
        Assert.Equal(AutostartState.NotInstalled, AutostartTask.Assess(false, null, Exe, TrustProgramFiles).State);
    }

    // ---------------------------------------------------------------- M2: a standard user would get a task that never starts

    [Fact]
    public void M2_a_standard_user_elevated_by_another_admin_is_refused()
    {
        Assert.Equal(AutostartTask.Exit.UserNotAdmin, AutostartTask.CheckInstallUser(Ana, Ana, Bob, requestedIsAdmin: false));
        Assert.Equal(AutostartTask.Exit.UserNotAdmin, AutostartTask.CheckInstallUser(Ana, Ana, Bob, requestedIsAdmin: null));
        Assert.Equal(AutostartTask.Exit.Ok, AutostartTask.CheckInstallUser(Ana, Ana, Bob, requestedIsAdmin: true));
        Assert.Equal(AutostartTask.Exit.Ok, AutostartTask.CheckInstallUser(Ana, Ana, Ana, requestedIsAdmin: null)); // split-token admin
        Assert.Equal(AutostartTask.Exit.NotSessionUser, AutostartTask.CheckInstallUser(Ana, Bob, Ana, requestedIsAdmin: true));
        Assert.Equal(AutostartTask.Exit.NotSessionUser, AutostartTask.CheckInstallUser(Ana, null, Ana, requestedIsAdmin: true));
    }

    [Fact]
    public void M2_only_an_administrator_can_use_autostart()
    {
        Assert.True(AutostartTask.UserCanAutostart(TokenElevation.Limited, inAdministratorsRole: false)); // UAC split token
        Assert.True(AutostartTask.UserCanAutostart(TokenElevation.Full, inAdministratorsRole: true));     // already elevated
        Assert.True(AutostartTask.UserCanAutostart(TokenElevation.Default, inAdministratorsRole: true));  // UAC turned off
        Assert.False(AutostartTask.UserCanAutostart(TokenElevation.Default, inAdministratorsRole: false)); // standard user
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

    // ---------------------------------------------------------------- L1: "already removed" is not a failure

    [Fact]
    public void L1_task_not_found_counts_as_removed()
    {
        Assert.Equal(AutostartTask.Exit.Ok, AutostartTask.ClassifyRemoveError(new FileNotFoundException("gone")));
        Assert.Equal(AutostartTask.Exit.Ok, AutostartTask.ClassifyRemoveError(new COMException("gone", unchecked((int)0x80070002))));
        Assert.Equal(AutostartTask.Exit.RemoveFailed, AutostartTask.ClassifyRemoveError(new UnauthorizedAccessException()));
        Assert.Equal(AutostartTask.Exit.RemoveFailed, AutostartTask.ClassifyRemoveError(new COMException("other", unchecked((int)0x80004005))));
    }

    [WindowsFact]
    public void L1_what_Task_Scheduler_really_throws_for_a_missing_task_counts_as_removed()
    {
        // Read-only: asks for a task that does not exist; nothing is created, changed or deleted.
        var type = Type.GetTypeFromProgID("Schedule.Service");
        Assert.NotNull(type);
        dynamic service = Activator.CreateInstance(type!)!;
        service.Connect();
        dynamic root = service.GetFolder("\\");
        Exception? thrown = null;
        try { root.GetTask("Glance test missing " + Guid.NewGuid().ToString("N")); }
        catch (Exception ex) { thrown = ex; }
        Assert.NotNull(thrown);
        Assert.Equal(AutostartTask.Exit.Ok, AutostartTask.ClassifyRemoveError(thrown!));
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

    // ---------------------------------------------------------------- L5: read-back checks more than the action

    [Fact]
    public void L5_the_Task_Scheduler_format_is_accepted() =>
        Assert.Null(AutostartTask.VerifyRegistered(RealFormat, Exe, Ana, ToSid));

    [Theory]
    [InlineData(@"<WorkingDirectory>C:\Program Files\Glance</WorkingDirectory>", @"<WorkingDirectory>C:\Users\Ana</WorkingDirectory>")]
    [InlineData("<UserId>S-1-5-21-1-2-3-1001</UserId>", "<UserId>S-1-5-21-1-2-3-1002</UserId>")]
    [InlineData(@"<UserId>PC\Ana</UserId>", @"<UserId>PC\Bob</UserId>")]
    [InlineData("<LogonType>InteractiveToken</LogonType>", "<LogonType>Password</LogonType>")]
    [InlineData("<RunLevel>HighestAvailable</RunLevel>", "<RunLevel>LeastPrivilege</RunLevel>")]
    [InlineData("</LogonTrigger>", "</LogonTrigger><TimeTrigger><StartBoundary>2026-01-01T00:00:00</StartBoundary></TimeTrigger>")]
    [InlineData("<LogonTrigger>", "<BootTrigger/><LogonTrigger>")]
    public void L5_a_changed_working_folder_principal_or_trigger_is_rejected(string from, string to) =>
        Assert.NotNull(AutostartTask.VerifyRegistered(Real(from, to), Exe, Ana, ToSid));

    [Fact]
    public void L5_a_task_built_by_this_code_passes_its_own_read_back() =>
        Assert.Null(AutostartTask.VerifyRegistered(AutostartTask.BuildXml(Exe, @"PC\Ana"), Exe, Ana, ToSid));

    // ---------------------------------------------------------------- nits

    [Fact]
    public void N3_a_switch_is_not_taken_as_the_user_name()
    {
        Assert.Equal(AutostartMode.Invalid, AutostartTask.ParseArgs(new[] { "--user", "--install-autostart" }).Mode);
        Assert.Equal(AutostartMode.Invalid, AutostartTask.ParseArgs(new[] { "--install-autostart", "--user", "--remove-autostart" }).Mode);
    }

    [Fact]
    public void N2_each_user_problem_has_its_own_exit_code_and_message()
    {
        var codes = new[] { AutostartTask.Exit.InvalidUser, AutostartTask.Exit.UserLookupFailed, AutostartTask.Exit.NotSessionUser, AutostartTask.Exit.UserNotAdmin };
        Assert.Equal(codes.Length, codes.Distinct().Count());
        var messages = codes.Select(AutostartTask.DescribeExit).ToList();
        Assert.Equal(codes.Length, messages.Distinct().Count());
        Assert.All(messages, m => Assert.DoesNotContain("exited with code", m));
        Assert.Contains("administrator", AutostartTask.DescribeExit(AutostartTask.Exit.UserNotAdmin));
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string cmdLine, out int argc);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private static string[] SplitLikeWindows(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var argc);
        try { return Enumerable.Range(0, argc).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(argv); }
    }

    [WindowsFact]
    public void Install_arguments_survive_Windows_argument_splitting()
    {
        foreach (var user in new[] { @"PC\Ana", @"PC\Ana Marija", @"R&D\ana", @"PC\o'neil", @"PC\100%", @"CORP\ana@x", @"PC\Žan Čuk", "ana", @"AzureAD\AnaNovak" })
        {
            var args = SplitLikeWindows("\"C:\\Program Files\\Glance\\Glance.Runner.exe\" " + AutostartTask.InstallArguments(user)).Skip(1).ToArray();
            var r = AutostartTask.ParseArgs(args);
            Assert.Equal(AutostartMode.Install, r.Mode);
            Assert.Equal(user, r.User);
        }
        Assert.Equal(AutostartMode.Remove, AutostartTask.ParseArgs(SplitLikeWindows("x.exe " + AutostartTask.RemoveArguments()).Skip(1).ToArray()).Mode);
    }

    [Theory]
    [InlineData(false, false, false, true, true)]
    [InlineData(false, false, false, null, true)]   // trust not known yet
    [InlineData(false, false, false, false, false)] // untrusted copy cannot turn it on
    [InlineData(false, false, true, false, true)]   // ... but can turn it off
    [InlineData(true, false, true, true, false)]    // busy
    [InlineData(false, true, true, true, false)]    // no runner
    public void Window_rule_for_the_checkbox(bool busy, bool noRunner, bool installed, bool? trusted, bool enabled) =>
        Assert.Equal(enabled, AutostartTask.CheckboxEnabled(!noRunner, busy, installed, trusted));
}
