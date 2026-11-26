using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Glance.Core.Security;
using Glance.Core.Service;
using Glance.Core.Setup;

namespace Glance.Tests;

/// <summary>
/// Fixes after the security review of the service (docs/reviews/review-runner-service.md), apart from the pipe tests in
/// DisplayServiceReviewTests.cs. Each test is named after its finding. Installer behaviour cannot run here (it would install a
/// service), so for the installer these tests read installer/Glance.iss: they are the "probe" that the script does the right thing.
/// </summary>
public class ReviewFixTests
{
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (var d = AppContext.BaseDirectory; d is not null; d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, "src", "Glance.sln"))) return d;
        throw new InvalidOperationException("repository root not found");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Root }.Concat(parts).ToArray()));
    private static string Iss => Read("installer", "Glance.iss");

    // ---- H1: the service's own folder ------------------------------------------------------------------------------

    [Fact]
    public void H1_the_service_refuses_to_run_from_a_folder_users_can_change()
    {
        var dir = Path.Combine(Path.GetTempPath(), "glance-svc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "Glance.Service.exe");
            File.WriteAllText(exe, "not a real program");
            var why = ServiceStartGuard.Refusal(exe, e => FolderTrust.CheckExecutable(e, TrustScope.FolderTree));
            Assert.NotNull(why);
            Assert.Contains(dir, why);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void H1_the_service_runs_from_a_trusted_folder_and_refuses_an_unknown_path()
    {
        Assert.Null(ServiceStartGuard.Refusal(@"C:\Program Files\Glance\Glance.Service.exe", _ => TrustVerdict.Yes));
        Assert.NotNull(ServiceStartGuard.Refusal(null, _ => TrustVerdict.Yes));
        Assert.NotNull(ServiceStartGuard.Refusal(@"C:\x\Glance.Service.exe", _ => throw new UnauthorizedAccessException("cannot read")));
    }

    [Fact]
    public void H1_the_service_checks_its_folder_before_it_does_anything_else()
    {
        var native = Read("src", "Glance.Service", "NativeService.cs");
        Assert.Contains("ServiceStartGuard.Refusal", native);
        Assert.True(native.IndexOf("ServiceStartGuard.Refusal", StringComparison.Ordinal) < native.IndexOf("_body.Start()", StringComparison.Ordinal),
            "the folder check must run before the service starts its work");
    }

    [Fact]
    public void H1_the_installer_always_installs_into_Program_Files_and_resets_the_folder_rules()
    {
        var iss = Iss;
        Assert.Contains("DisableDirPage=yes", iss);
        Assert.Contains("UsePreviousAppDir=no", iss);
        Assert.Contains("icacls.exe", iss);
        Assert.Contains("/inheritance:r", iss);
        Assert.Contains("*S-1-5-32-545:(OI)(CI)RX", iss);   // users: read and run only
    }

    // ---- M3: a leftover 0.1 task --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(TokenElevation.Full, true, false, TrayStartAction.RelaunchUnelevated)]   // started elevated (old task): restart as the user
    [InlineData(TokenElevation.Full, true, true, TrayStartAction.Refuse)]                // still elevated after that: refuse
    [InlineData(TokenElevation.Limited, false, false, TrayStartAction.Run)]              // the normal case
    [InlineData(TokenElevation.Default, false, false, TrayStartAction.Run)]              // a standard user
    [InlineData(TokenElevation.Default, true, false, TrayStartAction.Run)]               // UAC off: every program of this user is elevated anyway
    public void M3_the_tray_does_not_run_elevated_when_it_could_run_as_the_user(TokenElevation type, bool elevated, bool relaunched, TrayStartAction expected) =>
        Assert.Equal(expected, TrayStart.Decide(type, elevated, relaunched));

    [Fact]
    public void M3_the_tray_checks_its_token_before_anything_else()
    {
        var program = Read("src", "Glance.Runner", "Program.cs");
        Assert.Contains("TrayStart.Decide", program);
        Assert.Contains("RunnerEnvironment.Warning", program);
    }

    [Fact]
    public void M3_the_service_deletes_the_old_task_without_a_prompt()
    {
        var psi = LegacyTask.ServiceDeleteStartInfo();
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), psi.FileName, ignoreCase: true);
        Assert.Equal("/Delete /TN \"Glance Runner\" /F", psi.Arguments);
        Assert.False(psi.UseShellExecute);
        Assert.True(string.IsNullOrEmpty(psi.Verb));
        Assert.Contains("LegacyTask.ServiceDeleteStartInfo", Read("src", "Glance.Service", "ServiceRuntime.cs"));
    }

    [Fact]
    public void M3_the_installer_checks_that_the_old_task_is_gone()
    {
        var iss = Iss;
        Assert.Contains("/Query /TN \"Glance Runner\"", iss);
        Assert.Contains("SuppressibleMsgBox", iss);
    }

    // ---- M4: DLL search order -----------------------------------------------------------------------------------------

    [Fact]
    public void M4_the_service_limits_the_dll_search_before_anything_loads()
    {
        var program = Read("src", "Glance.Service", "Program.cs");
        var main = program.IndexOf("static int Main", StringComparison.Ordinal);
        Assert.True(main >= 0);
        var body = program[(program.IndexOf('{', main) + 1)..];
        var firstStatement = body.TrimStart().Split('\n')[0];
        Assert.Contains("SetDefaultDllDirectories", firstStatement);
    }

    [Fact]
    public void M4_libusb_is_only_loaded_from_System32()
    {
        var type = typeof(Glance.Core.Usb.Ax206Display).Assembly.GetType("Glance.Core.Usb.Libusb0", throwOnError: true)!;
        var imports = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.GetCustomAttribute<DllImportAttribute>() is { Value: "libusb0.dll" }).ToList();
        Assert.NotEmpty(imports);
        foreach (var m in imports)
        {
            // on the method, or for the whole assembly (DefaultDllImportSearchPaths cannot be put on a class)
            var paths = m.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>() ?? type.Assembly.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>();
            Assert.True(paths is not null && paths.Paths == DllImportSearchPath.System32, $"{m.Name} may load libusb0.dll from outside System32");
        }
    }

    // ---- L1: the tray's control pipe and the network -----------------------------------------------------------------

    [Fact]
    public void L1_the_tray_control_pipe_denies_the_network()
    {
        var rules = ServicePipeSecurity.CurrentUserOnly().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>();
        Assert.Contains(rules, r => r.IdentityReference == ServicePipeSecurity.Network && r.AccessControlType == AccessControlType.Deny);
    }

    // ---- M2: the log ------------------------------------------------------------------------------------------------------

    [Fact]
    public void M2_a_repeated_line_is_written_once_per_window_with_a_count()
    {
        long now = 0;
        var limiter = new LogLimiter(TimeSpan.FromSeconds(60), () => now);
        Assert.NotNull(limiter.Filter("busy"));
        for (int i = 0; i < 99; i++) { now += 500; Assert.Null(limiter.Filter("busy")); }
        Assert.NotNull(limiter.Filter("something else"));   // other lines are not held back
        now += 61_000;
        var again = limiter.Filter("busy");
        Assert.NotNull(again);
        Assert.Contains("99", again);   // says how many were left out
    }

    [Fact]
    public void M2_the_service_log_rolls_over_at_its_size_limit()
    {
        var dir = Path.Combine(Path.GetTempPath(), "glance-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var log = new ServiceLog(dir, _ => TrustVerdict.Yes, maxBytes: 2000);
            for (int i = 0; i < 200; i++) log.Write($"line {i} " + new string('x', 40));
            Assert.True(new FileInfo(Path.Combine(dir, "service.log")).Length <= 2100, "the log grew past its limit");
            Assert.True(File.Exists(Path.Combine(dir, "service.old.log")));
            Assert.True(new FileInfo(Path.Combine(dir, "service.old.log")).Length <= 2100);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- L4: installer robustness ---------------------------------------------------------------------------------------

    [Fact]
    public void L4_the_installer_turns_off_the_restart_before_stopping_and_checks_results()
    {
        var iss = Iss;
        var noRestart = iss.IndexOf("failure GlanceService reset= 0 actions= \"\"", StringComparison.Ordinal);
        var stop = iss.IndexOf("'stop GlanceService'", StringComparison.Ordinal);
        Assert.True(noRestart >= 0 && stop > noRestart, "the recovery restart must be off before the service is stopped");
        Assert.Contains("WaitForServiceStopped", iss);
        Assert.Contains("ServiceStepFailed", iss);   // every sc.exe result that matters is checked
    }

    // ---- L5: pre-shutdown timeout ---------------------------------------------------------------------------------------

    [Fact]
    public void L5_the_preshutdown_timeout_is_read_through_the_service_api()
    {
        // any installed service will do for reading; the Glance service sets its own value at start
        Assert.NotNull(ServiceControl.QueryPreshutdownTimeout("EventLog"));
        Assert.DoesNotContain("RegWriteDWordValue", Iss);
        Assert.Contains("ServiceControl.SetPreshutdownTimeout", Read("src", "Glance.Service", "ServiceRuntime.cs"));
    }

    // ---- L6: who opens the service log --------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void L6_only_the_service_or_an_elevated_console_opens_the_log(bool isSystem, bool elevatedConsole, bool expected) =>
        Assert.Equal(expected, ServiceLog.ShouldOpen(isSystem, elevatedConsole));

    // ---- L7: privileges ---------------------------------------------------------------------------------------------------

    [Fact]
    public void L7_the_service_keeps_only_the_privileges_it_needs()
    {
        var privs = ServiceNames.RequiredPrivileges.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("SeChangeNotifyPrivilege", privs);
        foreach (var dangerous in new[] { "SeDebugPrivilege", "SeTcbPrivilege", "SeTakeOwnershipPrivilege", "SeImpersonatePrivilege",
                                          "SeBackupPrivilege", "SeRestorePrivilege", "SeAssignPrimaryTokenPrivilege", "SeCreateTokenPrivilege" })
            Assert.DoesNotContain(dangerous, privs);
        var iss = Iss;
        Assert.Contains("privs GlanceService " + ServiceNames.RequiredPrivileges, iss);
        Assert.Contains("sidtype GlanceService unrestricted", iss);
    }

    // ---- L8: the tray's pipe name -----------------------------------------------------------------------------------------

    [Fact]
    public void L8_the_tray_pipe_name_carries_the_session_and_the_user()
    {
        var name = ServiceNames.TrayControlPipe(3, Me.Value);
        Assert.Contains(".3.", name);
        Assert.EndsWith(Me.Value, name);
    }

    // ---- L9: looking for a sibling program --------------------------------------------------------------------------------

    private static readonly string[] FakeFiles =
    {
        @"C:\Glance.Runner\bin\x\Glance.Runner.exe",                                // planted by another user
        @"D:\repo\src\Glance.sln",
        @"D:\repo\src\Glance.Runner\bin\Release\net8.0-windows\Glance.Runner.exe",  // a development build
    };

    private static string? Find(string baseDir) => DevPaths.FindSibling(baseDir, "Glance.Runner.exe", "Glance.Runner",
        f => FakeFiles.Contains(f, StringComparer.OrdinalIgnoreCase),
        d => FakeFiles.Any(f => f.StartsWith(d.TrimEnd('\\') + @"\", StringComparison.OrdinalIgnoreCase)),
        d => FakeFiles.Where(f => f.StartsWith(d.TrimEnd('\\') + @"\", StringComparison.OrdinalIgnoreCase) && f.EndsWith("Glance.Runner.exe")));

    [Fact]
    public void L9_an_installed_copy_never_searches_other_folders()
    {
        Assert.Null(Find(@"C:\Program Files\Glance\"));
        Assert.Null(Find(@"C:\Users\me\Downloads\Glance\"));
    }

    [Fact]
    public void L9_a_development_build_only_searches_inside_its_repository()
    {
        Assert.Equal(FakeFiles[2], Find(@"D:\repo\src\Glance.App\bin\Release\net8.0\"));
        Assert.Null(Find(@"C:\other\Glance.App\bin\Release\net8.0\"));   // a bin folder without a Glance repository above it
        Assert.Null(Find(@"D:\repo\src\Glance.App\"));                      // inside a repository, but not a build output
    }

    // ---- N2, N4, N6, T1 ---------------------------------------------------------------------------------------------------

    [Fact]
    public void N2_the_console_mode_is_only_in_debug_builds()
    {
        var program = Read("src", "Glance.Service", "Program.cs");
        var console = program.IndexOf("args.Contains(\"--console\")", StringComparison.Ordinal);   // the code, not the doc comment
        Assert.True(console >= 0);
        Assert.True(program.LastIndexOf("#if DEBUG", console, StringComparison.Ordinal) >= 0, "--console is not inside #if DEBUG");
    }

    [Fact]
    public void N4_files_from_older_versions_are_removed_on_upgrade()
    {
        var iss = Iss;
        Assert.Contains("[InstallDelete]", iss);
        Assert.Contains("{app}\\Glance.SensorHost.*", iss);
    }

    [Fact]
    public void N6_the_uninstaller_does_not_delete_through_a_link()
    {
        var iss = Iss;
        Assert.DoesNotContain("[UninstallDelete]", iss);
        Assert.Contains("FILE_ATTRIBUTE_REPARSE_POINT", iss);
    }

    [Fact]
    public void T1_the_editor_only_talks_to_a_tray_app_of_its_own_user()
    {
        Assert.True(Glance.Core.Control.ControlPipeClient.Options.HasFlag(PipeOptions.CurrentUserOnly));
        Assert.Contains("Options, System.Security.Principal.TokenImpersonationLevel.Anonymous", Read("src", "Glance.Core", "Control", "ControlPipeClient.cs"));
    }
}
