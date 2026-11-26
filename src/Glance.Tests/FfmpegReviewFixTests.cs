using System.Text;
using Glance.Core.Media;
using Glance.Core.Security;
using Glance.Core.Setup;
using Glance.Core.Themes;

namespace Glance.Tests;

/// <summary>
/// Tests for the findings in docs/reviews/review-ffmpeg-elevation.md. Each was run against the code before its fix
/// (and failed) and after it (and passed); the review's "Fixes after review" section has the output.
/// </summary>
public class FfmpegLookupTests
{
    private const string UserCopy = @"C:\Users\me\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_x\bin\ffmpeg.exe";
    private const string MachineLink = @"C:\Program Files\WinGet\Links\ffmpeg.exe";
    private const string MachineCopy = @"C:\Program Files\WinGet\Packages\Gyan.FFmpeg_x\bin\ffmpeg.exe";

    private static readonly TrustVerdict Trusted = new(true, "only administrators can change it");

    /// <summary>A finder over fake files: every candidate exists, and <paramref name="trusted"/> decides the verdict.</summary>
    private static FfmpegFinder Finder(bool elevated, IEnumerable<string> candidates, Func<string, bool> trusted, List<string>? checkedPaths = null) =>
        new(() => elevated, () => candidates, _ => true, p =>
        {
            checkedPaths?.Add(p);
            return trusted(p) ? Trusted : new TrustVerdict(false, p == MachineLink ? "the file is a link or junction" : "the file is owned by me");
        });

    // ---- M2: the switch that turns the admin-only rule on ---------------------------------------------------------

    [Fact]
    public void Elevated_lookup_skips_a_user_writable_copy_and_uses_the_admin_only_one()
    {
        var f = Finder(elevated: true, new[] { UserCopy, MachineCopy }, p => p == MachineCopy);
        Assert.Equal(MachineCopy, f.Find());
    }

    [Fact]
    public void Elevated_lookup_finds_nothing_when_no_copy_is_admin_only_and_reports_each()
    {
        var f = Finder(elevated: true, new[] { UserCopy, MachineLink }, _ => false);
        var log = new List<string>();
        f.Rejected += log.Add;
        Assert.Null(f.Find());
        Assert.Equal(2, log.Count);
        Assert.Contains(log, m => m.Contains(UserCopy));
        Assert.NotNull(f.LastRejection);
    }

    [Fact]
    public void Normal_lookup_returns_the_first_copy_without_reading_permissions()
    {
        var f = new FfmpegFinder(() => false, () => new[] { UserCopy, MachineCopy }, _ => true,
            _ => throw new InvalidOperationException("a non-elevated lookup must not check permissions"));
        Assert.Equal(UserCopy, f.Find());
    }

    [Fact]
    public void Winget_package_folders_are_searched_for_ffmpeg()
    {
        var root = Directory.CreateTempSubdirectory("glance-winget-");
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe", "ffmpeg-9.1-full_build", "bin"));
            File.WriteAllBytes(Path.Combine(bin.FullName, "ffmpeg.exe"), new byte[] { 0x4D, 0x5A });
            var other = Directory.CreateDirectory(Path.Combine(root.FullName, "Some.Other_x"));
            File.WriteAllBytes(Path.Combine(other.FullName, "ffmpeg.exe"), new byte[] { 0x4D, 0x5A });

            Assert.Equal(new[] { Path.Combine(bin.FullName, "ffmpeg.exe") }, Ffmpeg.WingetPackageFiles(root.FullName));
        }
        finally { root.Delete(true); }
        Assert.Contains(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinGet", "Links", "ffmpeg.exe"), Ffmpeg.Candidates());
    }

    // ---- L1: no misleading log line when a usable copy is found ----------------------------------------------------

    [Fact]
    public void A_rejected_Links_symlink_before_a_usable_copy_logs_nothing_and_leaves_no_rejection()
    {
        var f = Finder(elevated: true, new[] { MachineLink, MachineCopy }, p => p == MachineCopy);
        var log = new List<string>();
        f.Rejected += log.Add;
        Assert.Equal(MachineCopy, f.Find());
        Assert.Empty(log);
        Assert.Null(f.LastRejection);
    }

    // ---- N5: handlers run outside the lock ---------------------------------------------------------------------------

    [Fact]
    public void Rejected_handlers_run_outside_the_lookup_lock()
    {
        var f = Finder(elevated: true, new[] { UserCopy }, _ => false);
        bool otherThreadGotIn = false;
        f.Rejected += _ => otherThreadGotIn = Task.Run(f.Reset).Wait(TimeSpan.FromSeconds(2)); // would deadlock on a UI thread
        f.Find();
        Assert.True(otherThreadGotIn, "Reset() on another thread was blocked while Rejected was being raised");
    }

    // ---- L5: start exactly the path that was checked ------------------------------------------------------------------

    [Theory]
    [InlineData(@"tools\ffmpeg.exe")]
    [InlineData(@".\ffmpeg.exe")]
    [InlineData(@"C:tools\ffmpeg.exe")]
    public void Elevated_lookup_never_returns_a_relative_PATH_entry(string candidate)
    {
        var f = Finder(elevated: true, new[] { candidate }, _ => true);
        Assert.Null(f.Find());
    }

    [Fact]
    public void Elevated_lookup_returns_the_same_full_path_it_checked()
    {
        var seen = new List<string>();
        var f = Finder(elevated: true, new[] { @"C:\Program Files\x\..\WinGet\ffmpeg.exe" }, _ => true, seen);
        var found = f.Find();
        Assert.Equal(@"C:\Program Files\WinGet\ffmpeg.exe", found);
        Assert.Equal(new[] { found! }, seen);
    }
}

public class FfmpegReviewSetupTests
{
    // ---- L2: the warning says correctly where video still plays ------------------------------------------------------

    [Fact]
    public void User_folder_warning_says_video_plays_in_the_preview_and_only_without_the_runner()
    {
        var r = SetupChecks.DiagnoseFfmpeg(@"C:\Users\me\ffmpeg.exe", null);
        Assert.DoesNotContain("while the editor is sending", r.Detail);
        Assert.Contains("preview", r.Detail);
        Assert.Contains("without the background runner", r.Detail);
    }

    // ---- L3: "ready" only when the runner can use it --------------------------------------------------------------

    [Fact]
    public void Install_outcome_is_not_ready_when_only_a_user_writable_copy_exists()
    {
        var msg = SetupChecks.FfmpegInstallOutcome(usableByRunner: null, usableByEditor: @"C:\Users\me\ffmpeg.exe", exitCode: 0);
        Assert.DoesNotContain("ready", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("runner", msg);
    }

    [Fact]
    public void Install_outcome_is_ready_when_the_runner_can_use_the_copy()
    {
        const string p = @"C:\Program Files\WinGet\Packages\Gyan.FFmpeg_x\bin\ffmpeg.exe";
        Assert.Contains("ready", SetupChecks.FfmpegInstallOutcome(p, p, 0));
    }

    // ---- M1: the all-users install ends with a copy only admins can change ---------------------------------------------

    private static string Script(System.Diagnostics.ProcessStartInfo psi)
    {
        var arg = psi.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int i = Array.FindIndex(arg, a => a.Equals("-EncodedCommand", StringComparison.OrdinalIgnoreCase));
        Assert.True(i >= 0 && i + 1 < arg.Length, "expected an encoded PowerShell script: " + psi.Arguments);
        return Encoding.Unicode.GetString(Convert.FromBase64String(arg[i + 1]));
    }

    [Fact]
    public void All_users_install_resets_the_package_folder_to_admin_only_permissions()
    {
        var psi = SetupChecks.WingetInstallStartInfo(SetupChecks.FfmpegId, machineScope: true);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            psi.FileName, ignoreCase: true);
        Assert.Equal("runas", psi.Verb);
        Assert.True(psi.UseShellExecute);

        var script = Script(psi);
        Assert.Contains("install --id Gyan.FFmpeg --exact", script);
        Assert.Contains("--scope machine", script);
        Assert.Contains("'Gyan.FFmpeg_*'", script);               // only this package's folders
        Assert.Contains("/reset /T", script);                     // drop the user's ACEs winget's temp folder left behind
        Assert.Contains("/setowner *S-1-5-32-544 /T", script);     // Administrators, by SID (any Windows language)
        Assert.Contains("exit $code", script);                    // winget's exit code still comes back
        // no cmdlet may run before module loading is limited to the system folder (the user's Documents comes first)
        var lines = script.Split('\n').Select(l => l.Trim()).ToList();
        int reset = lines.FindIndex(l => l.StartsWith("$env:PSModulePath", StringComparison.Ordinal));
        Assert.True(reset >= 0 && lines.Take(reset + 1).All(l => !System.Text.RegularExpressions.Regex.IsMatch(l, @"\b[A-Z][a-z]+-[A-Z][A-Za-z]+\b")),
            "a cmdlet runs before PSModulePath is reset");
    }
}

public class VideoRetryTests
{
    private static (ThemeRenderer renderer, Func<int> created, Action<long> setNow) Renderer()
    {
        long now = 0; int count = 0;
        var r = new ThemeRenderer
        {
            Now = () => now,
            VideoFactory = (p, w, h, fit) =>
            {
                count++;
                var v = new VideoSource(p, w, h, fit, 15, () => null, () => null);
                // the source looks for ffmpeg on its own thread (review M9); wait for the answer so the test is deterministic
                Assert.True(SpinWait.SpinUntil(() => v.FfmpegMissing, 5000));
                return v;
            },
        };
        return (r, () => count, n => now = n);
    }

    private static Theme VideoTheme()
    {
        var t = Fake.Canvas();
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, X = 0, Y = 0, Width = 100, Height = 100, ImagePath = @"C:\nowhere\clip.mp4" });
        return t;
    }

    // ---- L4: a video that had no FFmpeg is tried again -------------------------------------------------------------

    [Fact]
    public void A_video_without_ffmpeg_is_tried_again_after_a_few_seconds()
    {
        var (r, created, setNow) = Renderer();
        using (r)
        {
            var t = VideoTheme();
            Fake.Render(t, null, renderer: r).Dispose();
            setNow(6_000);
            Fake.Render(t, null, renderer: r).Dispose();
            Assert.Equal(2, created());
        }
    }

    [Fact]
    public void A_video_without_ffmpeg_is_not_retried_every_frame()
    {
        var (r, created, setNow) = Renderer();
        using (r)
        {
            var t = VideoTheme();
            Fake.Render(t, null, renderer: r).Dispose();
            setNow(1_000);
            Fake.Render(t, null, renderer: r).Dispose();
            Assert.Equal(1, created());
        }
    }
}
