using Glance.Core.Security;

namespace Glance.Tests;

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

    private static PathSecurity Obj(TrustRole role, IReadOnlyList<AclEntry>? entries, string owner = FolderTrust.Administrators, string path = @"C:\Program Files\Glance") =>
        new(path, role, owner, entries);

    private static TrustVerdict Decide(params PathSecurity[] objects) => FolderTrust.Decide(objects);

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
    public void Nothing_to_check_is_not_trusted() => Assert.False(FolderTrust.Decide(Array.Empty<PathSecurity>()).Trusted);

    [Fact]
    public void A_missing_folder_is_not_trusted()
    {
        if (!OperatingSystem.IsWindows()) return;
        var v = FolderTrust.CheckFolder(Path.Combine(Path.GetTempPath(), "glance-missing-" + Guid.NewGuid().ToString("N")));
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
            var v = FolderTrust.CheckFolder(dir);
            Assert.False(v.Trusted); // owned by (and writable for) the current user
            Assert.NotNull(v.Reason);
        }
        finally { Directory.Delete(dir, true); }
    }
}
