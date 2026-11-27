using System.Text.RegularExpressions;
using Glance.Core.Display;
using Glance.Core.Themes;
using Glance.Core.Threading;

namespace Glance.Tests;

/// <summary>Findings of docs/reviews/review-ui-thread.md (M1, L1, L2, L4 and the nits); the scanner's own (L3) are in <see cref="UiThreadBlockingScanTests"/>.</summary>
public class UiThreadReviewFixTests
{
    // ---- M1: the exit blank waits for display work already queued ----

    private static (PusherSlot Slot, DisplayPowerPolicy Power, FakeOpener Opener) Slot(int blitDelayMs = 0)
    {
        var power = new DisplayPowerPolicy();
        var opener = new FakeOpener { Make = () => new FakeDisplay { BlitDelayMs = blitDelayMs } };
        return (new PusherSlot(power, opener.Open), power, opener);
    }

    [Fact]
    public void M1_closing_while_a_release_is_still_running_waits_for_its_blank()
    {
        // Send to display was just unticked: its release is on the queue and the transfer it waits for takes 800 ms.
        var (slot, power, opener) = Slot(blitDelayMs: 800);
        Assert.True(slot.TryOpen(out _));
        Assert.True(DisplayPusherTests.WaitUntil(() => { lock (opener.Displays) return opener.Displays[0].BlitStarted.IsSet; }));
        var queue = new SerialWork();
        _ = queue.Run(() => slot.Release(blank: true, DisplayPusher.StopTimeout));
        Assert.True(DisplayPusherTests.WaitUntil(() => slot.Current is null));   // the release has taken the pusher

        power.Handle(PowerEvent.Quit);   // the window closes
        var exit = slot.ReleaseOnExit(queue);
        Assert.True(exit.Wait(PowerBlank.SessionEndBudget), "the exit blank did not finish within the budget");

        var device = opener.Displays[0];
        Assert.True(device.Blanks >= 1, "the process could end before the display was blanked");
        Assert.Equal(1, device.Disposes);
    }

    [Fact]
    public void M1_closing_while_an_open_is_still_running_waits_for_it_and_leaves_the_display_black()
    {
        // Send to display was just ticked: the open is on the queue and takes 500 ms (service pipe, USB).
        var power = new DisplayPowerPolicy();
        var opener = new FakeOpener();
        var slot = new PusherSlot(power, () => { Thread.Sleep(500); return opener.Open(); });
        var queue = new SerialWork();
        var started = new ManualResetEventSlim();
        _ = queue.Run(() => { started.Set(); return slot.TryOpen(out _); });
        Assert.True(started.Wait(2000));

        power.Handle(PowerEvent.Quit);
        var exit = slot.ReleaseOnExit(queue);
        Assert.True(exit.Wait(PowerBlank.SessionEndBudget), "the exit blank did not finish within the budget");

        Assert.Null(slot.Current);
        lock (opener.Displays)
        {
            Assert.Single(opener.Displays);
            Assert.True(opener.Displays[0].Blanks >= 1, "the display opened during the close was left lit");
            Assert.Equal(1, opener.Displays[0].Disposes);
        }
    }

    [Fact]
    public void M1_the_editor_hands_its_exit_blank_to_the_display_queue_and_disposes_the_hub_after_it()
    {
        var closing = MethodText(Source("Glance.App", "MainWindow.cs"), "Closing += ");
        Assert.Contains("ReleaseOnExit(_displayWork)", closing);
        Assert.DoesNotContain("_display.Current is not null", closing);
        Assert.Matches(new Regex(@"_displayWork\.Run\(\s*_hub\.Dispose\s*\)"), closing);   // after the blank, never under an open
    }

    // ---- L1: fire-and-forget work is observed ----

    [Fact]
    public async Task L1_an_observed_failure_reaches_the_handler_on_the_UI_thread()
    {
        using var ui = new TestUiThread();
        var seen = new TaskCompletionSource<(Exception, int)>();
        await ui.Invoke(() => TaskObserver.Observe(Task.Run(new Action(() => throw new IOException("USB gone"))),
            ex => seen.SetResult((ex, Environment.CurrentManagedThreadId))));
        var (error, thread) = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<IOException>(error);
        Assert.Equal(ui.ThreadId, thread);   // so the handler may untick Send to display and say why
    }

    [Fact]
    public async Task L1_a_task_that_succeeds_does_not_call_the_handler()
    {
        bool called = false;
        var t = Task.Run(() => { });
        TaskObserver.Observe(t, _ => called = true);
        await t;
        await Task.Delay(50);
        Assert.False(called);
    }

    /// <summary>A discarded task (<c>_ = X()</c>) or a SerialWork item whose result is dropped: its failure vanishes unless observed.</summary>
    [Theory]
    [InlineData("Glance.App")]
    [InlineData("Glance.Runner")]
    public void L1_fire_and_forget_tasks_are_observed(string project)
    {
        var discard = new Regex(@"(?<![\w.])_\s*=\s*(?!TaskObserver\b|Observe\b)[\w.]*(Async|[Ww]ork\s*\.\s*Run|\.Request)\s*\(");
        var queueCall = new Regex(@"(?<![\w.])(_work|_displayWork)\s*\.\s*Run\s*\(");
        var hits = new List<string>();
        foreach (var file in SourceFiles(project))
        {
            var lines = UiThreadScanner.Sanitize(File.ReadAllText(file)).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (discard.IsMatch(lines[i])) hits.Add($"{Path.GetFileName(file)}:{i + 1}  {lines[i].Trim()}");
                foreach (Match m in queueCall.Matches(lines[i]))
                {
                    var before = lines[i][..m.Index].TrimEnd();
                    // used: awaited, returned, assigned, passed on (Observe(...), WhenAll(...)), or a deconstruction
                    bool used = before.EndsWith("await") || before.EndsWith("return") || before.EndsWith("(") || before.EndsWith(",")
                                || (before.EndsWith("=") && !before.EndsWith("=>") && !Regex.IsMatch(before, @"(?<![\w.])_\s*=$"));
                    if (!used) hits.Add($"{Path.GetFileName(file)}:{i + 1}  {lines[i].Trim()}");
                }
            }
        }
        Assert.True(hits.Count == 0, "Unobserved fire-and-forget work (use TaskObserver.Observe):\n" + string.Join("\n", hits));
    }

    // ---- L4: Export is file work and exports a detached copy ----

    [Fact]
    public void L4_every_file_operation_in_the_editor_runs_under_BeginFileWork()
    {
        var source = UiThreadScanner.Sanitize(Source("Glance.App", "MainWindow.cs"));
        var fileWork = new Regex(@"ThemePackage\s*\.\s*(Export|Import)\s*\(|ThemeSaveJob\s*\.|AidaImporter\s*\.\s*Import\s*\(|(?<![\w.])ImportPackage\s*\(");
        var hits = new List<string>();
        foreach (Match m in fileWork.Matches(source))
        {
            var member = UiThreadScanner.EnclosingMember(source, m.Index);
            Assert.NotNull(member);
            var text = source[member!.Value.Start..member.Value.End];
            var header = text[..text.IndexOf('{')];
            if (header.Contains("[WorkerThread]")) continue;   // a helper such as ImportPackage; the calls to it are checked
            if (!text.Contains("BeginFileWork(")) hits.Add("MainWindow.cs: " + source[m.Index..].Split('\n')[0].Trim());
        }
        Assert.True(hits.Count == 0, "File work outside BeginFileWork (Save could race it, closing is not refused):\n" + string.Join("\n", hits));
    }

    [Fact]
    public void L4_a_detached_copy_does_not_change_with_the_live_theme()
    {
        var live = Theme.CreateDefault(null);
        live.BaseFolder = @"C:\themes\a";
        var copy = live.Detach();
        var before = copy.ToSnapshot();
        live.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = "assets/x.png" });
        live.Name = "changed";
        Assert.Equal(before, copy.ToSnapshot());
        Assert.Equal(@"C:\themes\a", copy.BaseFolder);
    }

    // ---- nits ----

    [Fact]
    public void Nit_the_tray_menus_page_commands_do_not_wait_behind_the_display_check()
    {
        var menu = Source("Glance.Runner", "RunnerContext.cs").Split('\n').Where(l => l.Contains("\"Next page\"") || l.Contains("\"Previous page\"")).ToList();
        Assert.Equal(2, menu.Count);
        foreach (var line in menu)
        {
            Assert.DoesNotContain("_work.Run", line);   // queued behind a stuck stop, the page changed seconds later
            Assert.DoesNotContain("lock (_gate)", line); // NextPage/PrevPage are one Interlocked step: no lock needed
        }
    }

    [Fact]
    public async Task Nit_repeated_requests_collapse_into_one_run()
    {
        var queue = new SerialWork();
        var gate = new ManualResetEventSlim();
        _ = queue.Run(() => gate.Wait(5000));   // the worker is busy (a stuck transfer)
        int runs = 0;
        var reload = new CoalescedWork(queue, () => Interlocked.Increment(ref runs));
        var first = reload.Request();
        for (var i = 0; i < 4; i++) Assert.Null(reload.Request());   // "Reload theme" clicked four more times
        gate.Set();
        await first!;
        Assert.Equal(1, runs);
        await (reload.Request() ?? Task.CompletedTask);   // a later request runs again
        Assert.Equal(2, runs);
    }

    [Fact]
    public void Nit_the_tray_queues_reloads_and_checks_through_CoalescedWork()
    {
        var source = UiThreadScanner.Sanitize(Source("Glance.Runner", "RunnerContext.cs"));
        Assert.DoesNotMatch(new Regex(@"_work\s*\.\s*Run\s*\(\s*ReloadNow\s*\)"), source);
        Assert.DoesNotContain("_superviseQueued", source);
    }

    // ---- files ----

    internal static string SourceRoot()
    {
        for (var d = AppContext.BaseDirectory; d is not null; d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, "Glance.sln"))) return d;
        throw new DirectoryNotFoundException("Glance.sln not found above " + AppContext.BaseDirectory);
    }

    private static string Source(string project, string file) =>
        File.ReadAllText(Directory.EnumerateFiles(Path.Combine(SourceRoot(), project), file, SearchOption.AllDirectories).First(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")));

    private static IEnumerable<string> SourceFiles(string project)
    {
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(Path.Combine(SourceRoot(), project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"));
    }

    /// <summary>The text from <paramref name="start"/> to the end of the block it opens.</summary>
    private static string MethodText(string source, string start)
    {
        var clean = UiThreadScanner.Sanitize(source);
        var at = clean.IndexOf(start, StringComparison.Ordinal);
        Assert.True(at >= 0, start + " not found");
        var brace = clean.IndexOf('{', at);
        int depth = 0, i = brace;
        for (; i < clean.Length; i++) { if (clean[i] == '{') depth++; else if (clean[i] == '}' && --depth == 0) break; }
        return clean[at..(i + 1)];
    }
}
