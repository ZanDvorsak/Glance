using System.Xml.Linq;
using Glance.Core.Security;
using Glance.Core.Setup;

namespace Glance.Tests;

public class AutostartTaskTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private const string Exe = @"C:\Program Files\Glance\Glance.Runner.exe";

    private static XDocument Xml(string exe = Exe, string user = @"PC\Ana") => XDocument.Parse(AutostartTask.BuildXml(exe, user));

    [Fact]
    public void Xml_escapes_ampersands_and_quotes_in_the_path()
    {
        var exe = @"C:\Program Files\R&D ""Tools"" 'x'\Glance.Runner.exe";
        var raw = AutostartTask.BuildXml(exe, @"PC\Ana");
        Assert.Contains("R&amp;D &quot;Tools&quot; &apos;x&apos;", raw);
        var doc = XDocument.Parse(raw); // well-formed
        Assert.Equal(exe, doc.Descendants(Ns + "Command").Single().Value);
        Assert.Equal(Path.GetDirectoryName(exe), doc.Descendants(Ns + "WorkingDirectory").Single().Value);
    }

    [Fact]
    public void Xml_keeps_a_domain_backslash_user_and_escapes_an_ampersand_in_it()
    {
        var doc = Xml(user: @"R&D-PC\Ana Marija");
        Assert.All(doc.Descendants(Ns + "UserId"), u => Assert.Equal(@"R&D-PC\Ana Marija", u.Value));
        Assert.Equal(2, doc.Descendants(Ns + "UserId").Count()); // trigger and principal
    }

    [Fact]
    public void Xml_keeps_the_settings_existing_installs_rely_on()
    {
        var doc = Xml();
        Assert.Equal("HighestAvailable", doc.Descendants(Ns + "RunLevel").Single().Value);
        Assert.Equal("InteractiveToken", doc.Descendants(Ns + "LogonType").Single().Value);
        Assert.Single(doc.Descendants(Ns + "LogonTrigger"));
        Assert.Equal("PT0S", doc.Descendants(Ns + "ExecutionTimeLimit").Single().Value);
        Assert.Equal("false", doc.Descendants(Ns + "DisallowStartIfOnBatteries").Single().Value);
        Assert.Equal("false", doc.Descendants(Ns + "StopIfGoingOnBatteries").Single().Value);
        Assert.Equal(Exe, doc.Descendants(Ns + "Command").Single().Value);
        Assert.Empty(doc.Descendants(Ns + "Arguments"));
    }

    [Fact]
    public void Xml_refuses_relative_paths_and_bad_user_names()
    {
        Assert.Throws<ArgumentException>(() => AutostartTask.BuildXml(@"Glance.Runner.exe", @"PC\Ana"));
        Assert.Throws<ArgumentException>(() => AutostartTask.BuildXml(Exe, "PC\\Ana<x>"));
        Assert.Throws<ArgumentException>(() => AutostartTask.BuildXml(Exe, ""));
    }

    [Theory]
    [InlineData(@"PC\Ana")]
    [InlineData(@"AzureAD\AnaNovak")]
    [InlineData("ana")]
    [InlineData(@"CORP\Ana Marija")]
    [InlineData(@"R&D\ana.novak-2")]
    public void Valid_user_names_are_accepted(string user) => Assert.Null(AutostartTask.ValidateUserName(user));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"PC\")]
    [InlineData(@"\Ana")]
    [InlineData(@"A\B\C")]
    [InlineData("PC\\Ana\"")]
    [InlineData("PC\\Ana\" --remove-autostart \"x")]
    [InlineData(" PC\\Ana")]
    [InlineData("PC\\Ana\n")]
    [InlineData("PC\\a:b")]
    public void Invalid_user_names_are_rejected(string? user) => Assert.NotNull(AutostartTask.ValidateUserName(user));

    [Fact]
    public void No_autostart_switch_means_a_normal_start()
    {
        Assert.Equal(AutostartMode.None, AutostartTask.ParseArgs(Array.Empty<string>()).Mode);
        Assert.Equal(AutostartMode.None, AutostartTask.ParseArgs(new[] { "--something-else" }).Mode);
    }

    [Fact]
    public void Install_arguments_round_trip_through_the_parser()
    {
        // What Windows hands Main for the editor's command line "--install-autostart --user "PC\Ana Marija"".
        Assert.Equal(@"--install-autostart --user ""PC\Ana Marija""", AutostartTask.InstallArguments(@"PC\Ana Marija"));
        var r = AutostartTask.ParseArgs(new[] { "--install-autostart", "--user", @"PC\Ana Marija" });
        Assert.Equal(AutostartMode.Install, r.Mode);
        Assert.Equal(@"PC\Ana Marija", r.User);

        var swapped = AutostartTask.ParseArgs(new[] { "--USER", @"PC\Ana", "--Install-Autostart" });
        Assert.Equal(AutostartMode.Install, swapped.Mode);
        Assert.Equal(@"PC\Ana", swapped.User);
    }

    [Fact]
    public void Remove_takes_no_other_arguments()
    {
        Assert.Equal(AutostartMode.Remove, AutostartTask.ParseArgs(new[] { AutostartTask.RemoveArguments() }).Mode);
        Assert.Equal(AutostartMode.Invalid, AutostartTask.ParseArgs(new[] { "--remove-autostart", "--user", @"PC\Ana" }).Mode);
    }

    [Theory]
    [InlineData("--install-autostart")]                                      // no user
    [InlineData("--install-autostart", "--user")]                            // user without value
    [InlineData("--install-autostart", "--user", @"PC\Ana", "--user", "B")] // user twice
    [InlineData("--install-autostart", "--install-autostart", "--user", "A")]
    [InlineData("--install-autostart", "--user", @"PC\Ana", "extra")]        // unknown argument
    [InlineData("--install-autostart", "--user", "PC\\<Ana>")]               // invalid user
    [InlineData("--install-autostart", "--remove-autostart", "--user", "A")] // both modes
    public void Malformed_autostart_arguments_are_invalid(params string[] args)
    {
        var r = AutostartTask.ParseArgs(args);
        Assert.Equal(AutostartMode.Invalid, r.Mode);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void Install_arguments_refuse_a_name_that_could_break_out_of_its_quotes()
    {
        Assert.Throws<ArgumentException>(() => AutostartTask.InstallArguments("PC\\Ana\" --x \""));
        Assert.Throws<ArgumentException>(() => AutostartTask.InstallArguments(@"PC\")); // trailing backslash would escape the quote
    }

    [Fact]
    public void Every_exit_code_but_success_has_a_message()
    {
        Assert.Null(AutostartTask.DescribeExit(AutostartTask.Exit.Ok));
        foreach (var code in new[] { 10, 11, 12, 13, 14, 15, 16, 17, 1, -5 })
            Assert.False(string.IsNullOrWhiteSpace(AutostartTask.DescribeExit(code)));
        Assert.Contains("Program Files", AutostartTask.DescribeExit(AutostartTask.Exit.FolderNotTrusted));
    }

    [Fact]
    public void Read_back_accepts_the_task_as_built()
    {
        Assert.Null(AutostartTask.VerifyRegistered(AutostartTask.BuildXml(Exe, @"PC\Ana"), Exe));
        Assert.Null(AutostartTask.VerifyRegistered(AutostartTask.BuildXml(Exe, @"PC\Ana"), Exe.ToUpperInvariant()));
    }

    [Fact]
    public void Read_back_rejects_another_command_extra_actions_or_arguments()
    {
        var other = AutostartTask.BuildXml(@"C:\Users\Ana\Downloads\evil.exe", @"PC\Ana");
        Assert.NotNull(AutostartTask.VerifyRegistered(other, Exe));

        var doc = Xml();
        var exec = doc.Descendants(Ns + "Exec").Single();
        exec.Add(new XElement(Ns + "Arguments", "--evil"));
        Assert.NotNull(AutostartTask.VerifyRegistered(doc.ToString(), Exe));

        doc = Xml();
        doc.Descendants(Ns + "Actions").Single().Add(new XElement(Ns + "Exec", new XElement(Ns + "Command", @"C:\evil.exe")));
        Assert.NotNull(AutostartTask.VerifyRegistered(doc.ToString(), Exe));

        Assert.NotNull(AutostartTask.VerifyRegistered(null, Exe));
        Assert.NotNull(AutostartTask.VerifyRegistered("<not xml", Exe));
    }

    [Fact]
    public void Same_executable_ignores_case_and_quotes_but_not_the_folder()
    {
        Assert.True(AutostartTask.SameExecutable(Exe, "\"" + Exe.ToLowerInvariant() + "\""));
        Assert.False(AutostartTask.SameExecutable(Exe, @"C:\Program Files\Glance2\Glance.Runner.exe"));
        Assert.False(AutostartTask.SameExecutable(Exe, "Glance.Runner.exe"));
        Assert.False(AutostartTask.SameExecutable(null, Exe));
    }
}

public class FolderTrustTests
{
    private const string Users = "S-1-5-32-545", AuthenticatedUsers = "S-1-5-11", Everyone = "S-1-1-0",
        Ana = "S-1-5-21-1-2-3-1001", AppPackages = "S-1-15-2-1";
    private const int ReadExecute = 0x1200A9, FullControl = 0x1F01FF, Modify = 0x1301BF;

    private static AclEntry Allow(string sid, int rights, bool inheritOnly = false) => new(sid, true, rights, inheritOnly);
    private static AclEntry Deny(string sid, int rights) => new(sid, false, rights);

    /// <summary>The usual permissions under Program Files.</summary>
    private static List<AclEntry> ProgramFiles() => new()
    {
        Allow(FolderTrust.TrustedInstaller, FullControl),
        Allow(FolderTrust.LocalSystem, FullControl),
        Allow(FolderTrust.Administrators, FullControl),
        Allow(Users, ReadExecute),
        Allow(AppPackages, ReadExecute),
        Allow(FolderTrust.CreatorOwner, unchecked((int)0x10000000), inheritOnly: true),
    };

    private static ObjectSecurity Obj(TrustRole role, IReadOnlyList<AclEntry>? entries, string owner = FolderTrust.Administrators, string path = @"C:\Program Files\Glance") =>
        new(path, role, owner, entries);

    private static TrustVerdict Decide(params ObjectSecurity[] objects) => FolderTrust.Decide(objects);

    [Fact]
    public void An_installed_folder_under_Program_Files_is_trusted()
    {
        var v = Decide(
            Obj(TrustRole.Folder, ProgramFiles()),
            Obj(TrustRole.Ancestor, ProgramFiles(), FolderTrust.TrustedInstaller, @"C:\Program Files"),
            Obj(TrustRole.File, ProgramFiles(), path: @"C:\Program Files\Glance\Glance.Runner.exe"));
        Assert.True(v.Trusted, v.Reason);
    }

    [Fact]
    public void A_folder_owned_by_the_user_is_not_trusted()
    {
        // A zip extracted under Downloads: the user owns it (and could rewrite its permissions).
        var v = Decide(Obj(TrustRole.Folder, ProgramFiles(), owner: Ana, path: @"C:\Users\Ana\Downloads\Glance"));
        Assert.False(v.Trusted);
        Assert.Equal(Ana, v.PrincipalSid);
        Assert.Contains(@"Downloads\Glance", v.Reason);
    }

    [Theory]
    [InlineData(TrustRole.Folder, 0x2)]       // add a file (plant a DLL)
    [InlineData(TrustRole.Folder, 0x4)]       // add a subfolder
    [InlineData(TrustRole.Folder, 0x40)]      // delete files in it
    [InlineData(TrustRole.Folder, 0x10000)]   // delete it
    [InlineData(TrustRole.File, 0x2)]         // overwrite the exe
    [InlineData(TrustRole.File, 0x4)]
    [InlineData(TrustRole.File, 0x10000)]     // delete and replace it
    [InlineData(TrustRole.File, 0x40000)]     // rewrite its permissions
    [InlineData(TrustRole.File, 0x80000)]     // take ownership
    [InlineData(TrustRole.File, 0x40000000)]  // GENERIC_WRITE
    [InlineData(TrustRole.Ancestor, 0x40)]    // delete or rename the program folder
    [InlineData(TrustRole.Ancestor, 0x10000000)] // GENERIC_ALL
    public void A_dangerous_right_for_ordinary_users_is_not_trusted(TrustRole role, int right)
    {
        var acl = ProgramFiles();
        acl.Add(Allow(Users, right));
        var v = Decide(Obj(role, acl));
        Assert.False(v.Trusted);
        Assert.Equal(Users, v.PrincipalSid);
    }

    [Theory]
    [InlineData(AuthenticatedUsers, Modify)]
    [InlineData(Everyone, FullControl)]
    [InlineData(Ana, FullControl)]
    public void Modify_or_full_control_for_anyone_else_is_not_trusted(string sid, int rights)
    {
        var acl = ProgramFiles();
        acl.Add(Allow(sid, rights));
        Assert.False(Decide(Obj(TrustRole.Folder, acl)).Trusted);
    }

    [Fact]
    public void Creating_folders_in_an_ancestor_is_fine()
    {
        // C:\ lets Authenticated Users create folders; that does not let them touch C:\Program Files\Glance.
        var root = ProgramFiles();
        root.Add(Allow(AuthenticatedUsers, 0x4 | ReadExecute));
        root.Add(Allow(AuthenticatedUsers, Modify, inheritOnly: true));
        Assert.True(Decide(Obj(TrustRole.Ancestor, root, FolderTrust.TrustedInstaller, @"C:\")).Trusted);

        // The same right on the program folder itself is not fine.
        Assert.False(Decide(Obj(TrustRole.Folder, root)).Trusted);
    }

    [Fact]
    public void Inherit_only_entries_do_not_count_for_the_object_itself()
    {
        var acl = ProgramFiles();
        acl.Add(Allow(Users, FullControl, inheritOnly: true));
        Assert.True(Decide(Obj(TrustRole.Folder, acl)).Trusted);
    }

    [Fact]
    public void Deny_entries_are_ignored_both_ways()
    {
        // Conservative: a deny does not cancel an allow in this check.
        var acl = ProgramFiles();
        acl.Add(Deny(Users, FullControl));
        Assert.True(Decide(Obj(TrustRole.Folder, acl)).Trusted);
        acl.Add(Allow(Users, Modify));
        Assert.False(Decide(Obj(TrustRole.Folder, acl)).Trusted);
    }

    [Fact]
    public void Read_and_execute_for_users_is_fine()
    {
        var acl = new List<AclEntry> { Allow(FolderTrust.Administrators, FullControl), Allow(Users, ReadExecute), Allow(Everyone, ReadExecute) };
        Assert.True(Decide(Obj(TrustRole.Folder, acl), Obj(TrustRole.File, acl)).Trusted);
    }

    [Fact]
    public void A_null_access_list_or_unknown_entry_is_not_trusted()
    {
        Assert.False(Decide(Obj(TrustRole.Folder, null)).Trusted);
        var acl = ProgramFiles();
        acl.Add(new AclEntry("unknown", true, -1));
        Assert.False(Decide(Obj(TrustRole.File, acl)).Trusted);
    }

    [Fact]
    public void One_writable_file_among_many_is_enough_to_refuse()
    {
        var writable = ProgramFiles();
        writable.Add(Allow(Users, Modify));
        var v = Decide(
            Obj(TrustRole.Folder, ProgramFiles()),
            Obj(TrustRole.File, ProgramFiles(), path: @"C:\Program Files\Glance\a.dll"),
            Obj(TrustRole.File, writable, path: @"C:\Program Files\Glance\b.dll"),
            Obj(TrustRole.File, ProgramFiles(), path: @"C:\Program Files\Glance\c.dll"));
        Assert.False(v.Trusted);
        Assert.Contains("b.dll", v.Reason);
    }

    [Fact]
    public void Owner_rights_and_creator_owner_entries_follow_the_owner()
    {
        var acl = ProgramFiles();
        acl.Add(Allow(FolderTrust.OwnerRights, FullControl));
        acl.Add(Allow(FolderTrust.CreatorOwner, FullControl));
        Assert.True(Decide(Obj(TrustRole.Folder, acl)).Trusted);
        Assert.False(Decide(Obj(TrustRole.Folder, acl, owner: Ana)).Trusted);
    }

    [Fact]
    public void Nothing_to_check_is_not_trusted() => Assert.False(FolderTrust.Decide(Array.Empty<ObjectSecurity>()).Trusted);

    [Fact]
    public void A_missing_folder_is_not_trusted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var v = FolderTrust.Check(Path.Combine(Path.GetTempPath(), "glance-missing-" + Guid.NewGuid().ToString("N")));
        Assert.False(v.Trusted);
    }

    [Fact]
    public void A_folder_the_test_user_created_is_not_trusted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("glance-trust-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "Glance.Runner.exe"), "x");
            var v = FolderTrust.Check(dir);
            Assert.False(v.Trusted); // owned by (and writable for) the current user
            Assert.NotNull(v.Reason);
        }
        finally { Directory.Delete(dir, true); }
    }
}
