using Glance.Core.Service;

namespace Glance.Tests;

/// <summary>
/// The repository's shape after the move: no Glance program asks for administrator rights, the elevated helpers are gone,
/// and the installer creates the service with the tested rules. These read the source files, so they also catch a later
/// edit that quietly brings an elevated program back.
/// </summary>
public class ServiceLayoutTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (var d = AppContext.BaseDirectory; d is not null; d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, "src", "Glance.sln"))) return d;
        throw new InvalidOperationException("repository root not found");
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative));

    [Fact]
    public void No_Glance_program_asks_for_administrator_rights()
    {
        var manifests = Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.manifest", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .ToList();
        Assert.NotEmpty(manifests);
        foreach (var m in manifests)
        {
            var text = File.ReadAllText(m);
            Assert.DoesNotContain("requireAdministrator", text);
            Assert.DoesNotContain("highestAvailable", text);
        }
    }

    [Fact]
    public void The_elevated_helpers_are_gone_and_the_service_is_built()
    {
        var sln = Read(Path.Combine("src", "Glance.sln"));
        Assert.DoesNotContain("Glance.SensorHost", sln);
        Assert.False(Directory.Exists(Path.Combine(Root, "src", "Glance.SensorHost")));
        Assert.Contains("Glance.Service", sln);
        Assert.False(File.Exists(Path.Combine(Root, "src", "Glance.Runner", "AutostartInstaller.cs")));
        Assert.DoesNotContain("AutostartTask", Read(Path.Combine("src", "Glance.Runner", "Program.cs")));
        Assert.Contains("'Glance.Service'", Read(Path.Combine("build", "publish.ps1")));
    }

    [Fact]
    public void The_service_does_not_load_startup_hooks() =>
        Assert.Contains("<StartupHookSupport>false</StartupHookSupport>", Read(Path.Combine("src", "Glance.Service", "Glance.Service.csproj")));

    [Fact]
    public void The_installer_creates_the_service_with_the_tested_rules()
    {
        var iss = Read(Path.Combine("installer", "Glance.iss"));
        Assert.Contains("create " + ServiceNames.ServiceName, iss);
        Assert.Contains("start= auto", iss);
        Assert.Contains("sdset " + ServiceNames.ServiceName + " " + ServiceNames.ServiceSddl, iss);
        Assert.Contains("failure " + ServiceNames.ServiceName, iss);
        Assert.Contains("PreshutdownTimeout", iss);
        Assert.Contains(ServiceNames.PreshutdownTimeoutMs.ToString(), iss);
        Assert.Contains("start " + ServiceNames.ServiceName, iss);
        Assert.Contains("delete " + ServiceNames.ServiceName, iss);
    }

    [Fact]
    public void The_installer_retires_the_old_elevated_runner()
    {
        var iss = Read(Path.Combine("installer", "Glance.iss"));
        Assert.Contains("/Delete /TN \"\"Glance Runner\"\" /F", iss);   // the old logon task (Inno doubles the quotes)
        Assert.Contains(ServiceNames.LegacyRunnerPipe, iss);              // EXIT over its control pipe ...
        Assert.Contains("EXIT", iss);
        Assert.DoesNotContain("asks for administrator rights", iss);       // the tray no longer does
        Assert.DoesNotContain("/Create", iss);                            // and nothing creates a task any more
    }
}
