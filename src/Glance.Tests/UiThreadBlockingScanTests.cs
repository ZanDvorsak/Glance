using System.Text;
using System.Text.RegularExpressions;

namespace Glance.Tests;

/// <summary>
/// Review finding M7 (and L6 of the auto-start review): process, pipe, USB, registry and large-file work must not run on the
/// editor's or the tray app's UI thread. Glance.App and Glance.Runner have no test project of their own, so this is an
/// analyzer-style check over their source: every call to a known blocking API must sit inside work that runs off the UI
/// thread (<c>Task.Run(...)</c>, <c>SerialWork.Run(...)</c>), inside a method marked <c>[WorkerThread]</c>, or on a line
/// that says why it may block (<c>// ui-thread-ok: reason</c>). A <c>[WorkerThread]</c> method may itself only be called from
/// such places. Where a blocking call must stay synchronous, the marker forces a reason into the code.
/// </summary>
public class UiThreadBlockingScanTests
{
    /// <summary>One blocking API, the projects it is checked in, and what it costs on the UI thread.</summary>
    public sealed record Rule(string Id, string[] Projects, string Pattern, string Why);

    private static readonly string[] Editor = { "Glance.App" };
    private static readonly string[] Tray = { "Glance.Runner" };
    private static readonly string[] Both = { "Glance.App", "Glance.Runner" };

    public static readonly Rule[] Rules =
    {
        new("display-open", Editor, @"_display\.TryOpen\(", "opens the service pipe (0.5 s connect + 10 s reply timeout) or the USB device"),
        new("display-release", Editor, @"_display\.Release\(", "waits for the display loop to stop: up to DisplayPusher.StopTimeout (18 s) with a stuck transfer"),
        new("display-restart", Editor, @"_display\.RestartIfDead\(", "stops a dead loop (2 s bound) and opens the display again"),
        new("tray-process-scan", Editor, @"RunnerClient\.IsRunning", "enumerates every process on the PC"),
        new("service-control", Editor, @"ServiceControl\.(Query|Start)\(", "asks the Service Control Manager"),
        new("autostart-schtasks", Editor, @"Autostart\.(IsInstalled|Remove|RunElevated)\b", "starts schtasks.exe or sc.exe and waits up to 5-60 s"),
        new("tray-pipe", Editor, @"RunnerClient\.(Send|SendLive)\(", "a control pipe round trip: 0.8-1 s connect + 0.8-5 s reply timeout"),
        new("service-pipe", Editor, @"DisplayConnector\.\w+\(", "the service's display pipe, or USB when there is no service"),
        new("tray-launch", Editor, @"RunnerClient\.Launch\(", "ShellExecute of another program"),
        new("package-import", Editor, @"ThemePackage\.(Import|Export)\(", "unzips or zips a .glance file of up to 1 GB"),
        new("aida-import", Editor, @"AidaImporter\.Import\(", "parses an AIDA64 theme and copies its pictures"),
        new("theme-save", Editor, @"[Tt]heme\.Save\(", "copies every picture and video into the theme folder and flushes the file"),
        new("network-copy", Editor, @"PickedFile\.Localize\(", "copies a file from a network share"),
        new("image-read", Editor, @"SKData\.Create\(", "reads a whole picture file"),
        new("ffmpeg-search", Editor, @"Ffmpeg\.Find\(", "searches PATH and the winget folders (every 5 s while FFmpeg is missing)"),
        new("folder-delete", Editor, @"Directory\.Delete\(", "deletes a whole folder tree"),
        // SetupWindow's _context is MainWindow.CurrentSetupContext ([WorkerThread]) handed over as a delegate, which the scanner cannot follow
        new("setup-context", Editor, @"\b_context\(\)","the setup context asks the Service Control Manager and lists every process"),

        new("tray-display-open", Tray, @"new DisplayPusher\(", "opens the service pipe or the USB device"),
        new("tray-display-stop", Tray, @"\.Stop\(\s*blank", "waits for the display loop to stop: up to 18 s with a stuck transfer"),
        new("tray-service-pipe", Tray, @"DisplayConnector\.\w+\(", "the service's display pipe, or USB when there is no service"),
        new("tray-file-read", Tray, @"(AppSettings\.Read|ThemeFile\.Load)\(", "reads files that can be locked (retries for up to a second)"),

        // after the review (docs/reviews/review-ui-thread.md)
        new("sync-over-async", Both, @"\.Wait\(|\.Result\b(?!\s*\.)|\bGetResult\(", "blocks on a task (L3); a bounded wait that must stay needs a ui-thread-ok reason"),
        new("tray-exe-search", Editor, @"AppPaths\.Find\w*\(", "searches the disk for the tray app, in a development build also the build output (nit)"),
        new("tray-gate-lock", Tray, @"\block\s*\(\s*_gate\s*\)", "waits for the display check, which holds _gate for up to 18 s while a transfer is stuck (L2)"),
    };


    public static IEnumerable<object[]> RuleIds => Rules.Select(r => new object[] { r.Id });

    [Theory]
    [MemberData(nameof(RuleIds))]
    public void No_blocking_call_runs_on_a_UI_thread(string id)
    {
        var rule = Rules.Single(r => r.Id == id);
        var hits = rule.Projects.SelectMany(p => SourceFiles(p))
            .SelectMany(f => UiThreadScanner.Scan(Relative(f), File.ReadAllText(f), UiThreadScanner.Pattern(rule.Pattern)))
            .ToList();
        Assert.True(hits.Count == 0, $"{rule.Id}: {rule.Why}. On the UI thread at:\n" + string.Join("\n", hits));
    }

    [Theory]
    [InlineData("Glance.App")]
    [InlineData("Glance.Runner")]
    public void WorkerThread_methods_are_only_called_off_the_UI_thread(string project)
    {
        var files = SourceFiles(project).ToList();
        var names = files.SelectMany(f => UiThreadScanner.WorkerMethods(UiThreadScanner.Sanitize(File.ReadAllText(f)))).Distinct().ToList();
        var hits = files.SelectMany(f => UiThreadScanner.WorkerCallsOnUiThread(Relative(f), File.ReadAllText(f), names)).ToList();
        Assert.True(hits.Count == 0, "[WorkerThread] methods called on the UI thread:\n" + string.Join("\n", hits));
    }

    // ---- the scanner checks itself, so a green audit means something ----

    private const string Sample = """
        class C
        {
            void OnClick()
            {
                Slow();                                   // flagged: direct call
                var s = "Slow()";                         // a string, not a call
                // Slow(); in a comment
                Task.Run(() => Slow());                   // off the UI thread
                Task.Run(() =>
                {
                    Slow();                               // off the UI thread, several lines
                });
                _displayWork.Run(() => { Slow(); });      // SerialWork
                Slow(); // ui-thread-ok: measured, under 1 ms
                Helper();                                 // flagged: a [WorkerThread] method called on the UI thread
                Task.Run(Helper);                         // fine
            }

            [WorkerThread]
            private int Helper()
            {
                Slow();                                   // fine: inside a worker method
                return 1;
            }

            [WorkerThread]
            private int Arrow() => Slow();                // fine
        }
        """;

    [Fact]
    public void The_scanner_finds_direct_calls_and_skips_strings_comments_and_worker_code()
    {
        var hits = UiThreadScanner.Scan("Sample.cs", Sample, new Regex(@"Slow\("));
        Assert.Equal(new[] { 5 }, hits.Select(h => h.Line).ToArray());
    }

    [Fact]
    public void The_scanner_finds_worker_methods_called_on_the_UI_thread()
    {
        var clean = UiThreadScanner.Sanitize(Sample);
        Assert.Equal(new[] { "Helper", "Arrow" }, UiThreadScanner.WorkerMethods(clean).ToArray());
        var hits = UiThreadScanner.WorkerCallsOnUiThread("Sample.cs", Sample, new[] { "Helper", "Arrow" });
        Assert.Equal(new[] { 15 }, hits.Select(h => h.Line).ToArray());
    }

    [Fact]
    public void Sanitizing_keeps_lines_and_columns()
    {
        var src = "a(\"x//y\"); // c\n@\"q\"\"r\" + '\\'' /* d\ne */ b()";
        var clean = UiThreadScanner.Sanitize(src);
        Assert.Equal(src.Length, clean.Length);
        Assert.Equal(src.Count(c => c == '\n'), clean.Count(c => c == '\n'));
        Assert.DoesNotContain("//", clean);
        Assert.Contains("b()", clean);
        Assert.StartsWith("a(", clean);
    }

    // ---- L3 (review): the scanner's blind spots, each fooled the scanner before ----

    [Fact]
    public void L3_a_call_inside_an_interpolated_string_is_seen()
    {
        const string src = "void F()\n{\n    var t = $\"took {Slow()} ms, {(a ? \"x\" : \"y\")} {{not code}}\";\n}\n";
        Assert.Equal(new[] { 3 }, UiThreadScanner.Scan("S.cs", src, new Regex(@"Slow\(")).Select(h => h.Line).ToArray());
        Assert.Empty(UiThreadScanner.Scan("S.cs", src, new Regex(@"not code")));   // literal text stays blank
    }

    [Fact]
    public void L3_work_posted_back_to_the_UI_thread_from_a_worker_is_UI_code()
    {
        const string src = "void F()\n{\n    Task.Run(() =>\n    {\n        Dispatcher.UIThread.Post(() => Slow());\n        _ui.BeginInvoke(() => Slow());\n        Slow();\n    });\n}\n";
        Assert.Equal(new[] { 5, 6 }, UiThreadScanner.Scan("S.cs", src, new Regex(@"Slow\(")).Select(h => h.Line).ToArray());
    }

    [Fact]
    public void L3_whitespace_around_the_dot_does_not_hide_a_call()
    {
        const string src = "void F()\n{\n    ServiceControl .Query();\n    ServiceControl.\n        Query ();\n}\n";
        Assert.Equal(new[] { 3, 4 }, UiThreadScanner.Scan("S.cs", src, UiThreadScanner.Pattern(@"ServiceControl\.Query\(")).Select(h => h.Line).ToArray());
    }

    [Fact]
    public void L3_blocking_on_a_task_is_a_rule_and_a_ContinueWith_callback_is_not_UI_code()
    {
        var rule = UiThreadScanner.Pattern(Rules.Single(r => r.Id == "sync-over-async").Pattern);
        const string src = "void F()\n{\n    t.Wait();\n    var r = t.Result;\n    t.GetAwaiter().GetResult();\n    if (x == PowerBlank.Result.Busy) { }\n    t.ContinueWith(d => Use(d.Result));\n}\n";
        Assert.Equal(new[] { 3, 4, 5 }, UiThreadScanner.Scan("S.cs", src, rule).Select(h => h.Line).ToArray());
    }

    // What the scanner still cannot see (it reads text, it does not compile): a blocking call reached through a delegate or a
    // method group handed to another class, a call in another class that has no rule, a ContinueWith told to run on the UI
    // context (TaskScheduler.FromCurrentSynchronizationContext is treated as UI code, other schedulers are not), and
    // `.Result` followed by a member access (`t.Result.Length`), which is skipped so that enum values like `Result.Busy` pass.

    // ---- files ----

    private static string SourceRoot()
    {
        for (var d = AppContext.BaseDirectory; d is not null; d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, "Glance.sln"))) return d;
        throw new DirectoryNotFoundException("Glance.sln not found above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> SourceFiles(string project)
    {
        var dir = Path.Combine(SourceRoot(), project);
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"))
            .OrderBy(f => f, StringComparer.Ordinal);
    }

    private static string Relative(string file) => Path.GetRelativePath(SourceRoot(), file);
}

/// <summary>A small text scanner for C# source; good enough for this code base's own UI code (see <see cref="UiThreadBlockingScanTests"/>).</summary>
internal static class UiThreadScanner
{
    public sealed record Hit(string File, int Line, string Text)
    {
        public override string ToString() => $"  {File}:{Line}  {Text.Trim()}";
    }

    /// <summary>Calls that start work off the calling thread; everything inside their parentheses is off the UI thread.</summary>
    private static readonly Regex Offloads = new(@"(\b(Task\s*\.\s*Run|Task\s*\.\s*Factory\s*\.\s*StartNew|ThreadPool\s*\.\s*QueueUserWorkItem|\w*[Ww]ork\s*\.\s*Run)|\bnew\s+BackgroundProbe<[^>]*>|\w*[Oo]pener\s*\.\s*TryStart|\.\s*ContinueWith)\s*\(");
    /// <summary>Calls that run their lambda on the UI thread again, also from inside a worker (review L3).</summary>
    private static readonly Regex UiPosts = new(@"(\bDispatcher\s*\.\s*UIThread\s*\.\s*(Post|InvokeAsync|Invoke)|\.\s*BeginInvoke)\s*\(");
    private static readonly Regex WorkerAttribute = new(@"\[WorkerThread\]");
    private const string OkMarker = "ui-thread-ok:";

    /// <summary>A rule's pattern as a regex; whitespace is allowed around every '.' and before every '(' (review L3).</summary>
    public static Regex Pattern(string rule) => new(rule.Replace(@"\.", @"\s*\.\s*").Replace(@"\(", @"\s*\("));

    public static List<Hit> Scan(string file, string source, Regex pattern)
    {
        var clean = Sanitize(source);
        var regions = Regions(clean);
        var lines = source.Split('\n');
        var hits = new List<Hit>();
        foreach (Match m in pattern.Matches(clean))
        {
            if (OffUiThread(regions, m.Index)) continue;
            var line = LineOf(clean, m.Index);
            if (lines[line - 1].Contains(OkMarker)) continue;
            hits.Add(new Hit(file, line, lines[line - 1]));
        }
        return hits;
    }

    /// <summary>
    /// Ranges with the thread they run on: offloaded lambdas and [WorkerThread] bodies off the UI thread, lambdas posted back
    /// (<c>Dispatcher.UIThread.Post</c>, <c>BeginInvoke</c>, a <c>ContinueWith</c> on the UI scheduler) on it.
    /// </summary>
    public static List<(int Start, int End, bool Ui)> Regions(string clean)
    {
        var regions = new List<(int, int, bool)>();
        foreach (Match m in Offloads.Matches(clean))
        {
            var end = Match(clean, m.Index + m.Length - 1, '(', ')');
            var ui = m.Value.Contains("ContinueWith") && clean[m.Index..Math.Min(end + 1, clean.Length)].Contains("FromCurrentSynchronizationContext");
            regions.Add((m.Index, end, ui));
        }
        foreach (Match m in UiPosts.Matches(clean))
            regions.Add((m.Index, Match(clean, m.Index + m.Length - 1, '(', ')'), true));
        foreach (var (start, end) in WorkerMethodBodies(clean)) regions.Add((start, end, false));
        return regions;
    }

    /// <summary>Character ranges that run off the UI thread (for callers that only need those).</summary>
    public static List<(int Start, int End)> WorkerRegions(string clean) => Regions(clean).Where(r => !r.Ui).Select(r => (r.Start, r.End)).ToList();

    /// <summary>The innermost range around <paramref name="index"/> decides; outside every range is UI code.</summary>
    private static bool OffUiThread(List<(int Start, int End, bool Ui)> regions, int index)
    {
        (int Start, int End, bool Ui)? inner = null;
        foreach (var r in regions)
            if (index >= r.Start && index <= r.End && (inner is null || r.Start > inner.Value.Start)) inner = r;
        return inner is { Ui: false };
    }

    private static IEnumerable<(int Start, int End)> WorkerMethodBodies(string clean)
    {
        foreach (Match m in WorkerAttribute.Matches(clean))
        {
            if (Declaration(clean, m.Index + m.Length) is not { } d) continue;
            var paramsClose = Match(clean, d.ParamsOpen, '(', ')');
            var i = paramsClose + 1;
            while (i < clean.Length && char.IsWhiteSpace(clean[i])) i++;
            int end;
            if (i + 1 < clean.Length && clean[i] == '=' && clean[i + 1] == '>') end = StatementEnd(clean, i);
            else
            {
                var brace = clean.IndexOf('{', i);
                end = brace < 0 ? clean.Length : Match(clean, brace, '{', '}');
            }
            yield return (m.Index, end);
        }
    }

    /// <summary>
    /// Blanks out comments and the text of string and character literals; keeps every line break and column, and keeps the code
    /// inside interpolation holes (<c>$"{Slow()}"</c>, review L3), nested strings included.
    /// </summary>
    public static string Sanitize(string src)
    {
        var sb = new StringBuilder(src);
        ScanCode(src, sb, 0, inHole: false);
        return sb.ToString();
    }

    private static void Blank(StringBuilder sb, int from, int to)
    {
        for (var k = from; k < to && k < sb.Length; k++) if (sb[k] != '\n' && sb[k] != '\r') sb[k] = ' ';
    }

    /// <summary>Code from <paramref name="i"/>; inside a hole, stops at its closing '}' and returns its index.</summary>
    private static int ScanCode(string src, StringBuilder sb, int i, bool inHole)
    {
        var depth = 0;
        while (i < src.Length)
        {
            var ch = src[i];
            var next = i + 1 < src.Length ? src[i + 1] : '\0';
            if (inHole && ch == '{') depth++;
            else if (inHole && ch == '}') { if (depth == 0) return i; depth--; }

            if (ch == '/' && next == '/')
            {
                var end = src.IndexOf('\n', i);
                if (end < 0) end = src.Length;
                Blank(sb, i, end); i = end;
            }
            else if (ch == '/' && next == '*')
            {
                var end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? src.Length : end + 2;
                Blank(sb, i, end); i = end;
            }
            else if (ch == '"' || (ch is '@' or '$' && next == '"') || (ch is '@' or '$' && next is '@' or '$' && i + 2 < src.Length && src[i + 2] == '"'))
                i = ScanString(src, sb, i);
            else if (ch == '\'')
            {
                var j = i + 1;
                if (j < src.Length && src[j] == '\\') j += 2; else j++;
                while (j < src.Length && src[j] != '\'' && src[j] != '\n') j++;
                Blank(sb, i + 1, j); i = j + 1;
            }
            else i++;
        }
        return i;
    }

    /// <summary>A string literal starting at <paramref name="i"/> (its prefix included); returns the index after it.</summary>
    private static int ScanString(string src, StringBuilder sb, int i)
    {
        var start = src.IndexOf('"', i);
        var prefix = src.AsSpan(i, start - i);
        bool verbatim = prefix.Contains('@'), interpolated = prefix.Contains('$');
        if (start + 2 < src.Length && src[start + 1] == '"' && src[start + 2] == '"')
        {
            var end = src.IndexOf("\"\"\"", start + 3, StringComparison.Ordinal);   // raw string literal (no holes in this code base's raw strings)
            end = end < 0 ? src.Length : end + 3;
            Blank(sb, start + 1, end - 1);
            return end;
        }
        var j = start + 1;
        while (j < src.Length)
        {
            var c = src[j];
            var n = j + 1 < src.Length ? src[j + 1] : '\0';
            if (verbatim && c == '"' && n == '"') { Blank(sb, j, j + 2); j += 2; continue; }
            if (!verbatim && c == '\\') { Blank(sb, j, j + 2); j += 2; continue; }
            if (c == '"') break;
            if (interpolated && c == '{' && n == '{') { Blank(sb, j, j + 2); j += 2; continue; }
            if (interpolated && c == '}' && n == '}') { Blank(sb, j, j + 2); j += 2; continue; }
            if (interpolated && c == '{') { j = ScanCode(src, sb, j + 1, inHole: true) + 1; continue; }   // the hole is code
            Blank(sb, j, j + 1);
            j++;
        }
        return j + 1;
    }

    /// <summary>The class member (method, constructor, nested type) whose body contains <paramref name="index"/>: its header start and body end.</summary>
    public static (int Start, int End)? EnclosingMember(string clean, int index)
    {
        // the class body is the first '{' after "class "; members open their body one level deeper
        var cls = Regex.Match(clean, @"\bclass\s+\w+[^{]*\{");
        if (!cls.Success) return null;
        int classDepth = 0;
        for (var i = 0; i < cls.Index + cls.Length; i++) { if (clean[i] == '{') classDepth++; else if (clean[i] == '}') classDepth--; }
        int depth = classDepth, headerStart = cls.Index + cls.Length, bodyStart = -1;
        for (var i = cls.Index + cls.Length; i < clean.Length; i++)
        {
            var ch = clean[i];
            if (ch == '{') { if (depth == classDepth) bodyStart = i; depth++; }
            else if (ch == '}')
            {
                depth--;
                if (depth == classDepth && bodyStart >= 0)
                {
                    if (index >= headerStart && index <= i) return (headerStart, i + 1);   // the header too (a declaration)
                    headerStart = i + 1; bodyStart = -1;
                }
                if (depth < classDepth) break;
            }
            else if (ch == ';' && depth == classDepth) headerStart = i + 1;
        }
        return null;
    }

    public static List<Hit> WorkerCallsOnUiThread(string file, string source, IEnumerable<string> workerMethods)
    {
        var hits = new List<Hit>();
        foreach (var name in workerMethods)
            hits.AddRange(Scan(file, source, new Regex($@"(?<![\w.])(this\.)?{Regex.Escape(name)}\s*\(")));
        return hits.OrderBy(h => h.Line).ToList();
    }

    public static List<string> WorkerMethods(string clean)
    {
        var names = new List<string>();
        foreach (Match m in WorkerAttribute.Matches(clean))
            if (Declaration(clean, m.Index + m.Length) is { } d) names.Add(d.Name);
        return names;
    }

    private static readonly HashSet<string> Modifiers = new() { "private", "public", "internal", "protected", "static", "async", "unsafe", "new", "override", "virtual", "sealed", "extern" };

    /// <summary>The method declared after an attribute: its name and where its parameter list opens (a tuple return type is skipped).</summary>
    private static (string Name, int ParamsOpen)? Declaration(string clean, int from)
    {
        foreach (Match n in new Regex(@"(?<![\w.])(\w+)\s*(<[^<>()]*>)?\s*\(").Matches(clean, from))
        {
            if (Modifiers.Contains(n.Groups[1].Value)) continue;
            return (n.Groups[1].Value, n.Index + n.Length - 1);
        }
        return null;
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++) if (text[i] == '\n') line++;
        return line;
    }

    private static int Match(string clean, int open, char o, char c)
    {
        var depth = 0;
        for (var i = open; i < clean.Length; i++)
        {
            if (clean[i] == o) depth++;
            else if (clean[i] == c && --depth == 0) return i;
        }
        return clean.Length;
    }

    private static int StatementEnd(string clean, int from)
    {
        var depth = 0;
        for (var i = from; i < clean.Length; i++)
        {
            var ch = clean[i];
            if (ch is '(' or '{' or '[') depth++;
            else if (ch is ')' or '}' or ']') depth--;
            else if (ch == ';' && depth <= 0) return i;
        }
        return clean.Length;
    }
}
