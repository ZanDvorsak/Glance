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
    };

    public static IEnumerable<object[]> RuleIds => Rules.Select(r => new object[] { r.Id });

    [Theory]
    [MemberData(nameof(RuleIds))]
    public void No_blocking_call_runs_on_a_UI_thread(string id)
    {
        var rule = Rules.Single(r => r.Id == id);
        var hits = rule.Projects.SelectMany(p => SourceFiles(p))
            .SelectMany(f => UiThreadScanner.Scan(Relative(f), File.ReadAllText(f), new Regex(rule.Pattern)))
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
    private static readonly Regex Offloads = new(@"(\b(Task\.Run|Task\.Factory\.StartNew|ThreadPool\.QueueUserWorkItem|\w*[Ww]ork\.Run)|\bnew BackgroundProbe<[^>]*>|\w*[Oo]pener\.TryStart)\s*\(");
    private static readonly Regex WorkerAttribute = new(@"\[WorkerThread\]");
    private const string OkMarker = "ui-thread-ok:";

    public static List<Hit> Scan(string file, string source, Regex pattern)
    {
        var clean = Sanitize(source);
        var regions = WorkerRegions(clean);
        var lines = source.Split('\n');
        var hits = new List<Hit>();
        foreach (Match m in pattern.Matches(clean))
        {
            if (Inside(regions, m.Index)) continue;
            var line = LineOf(clean, m.Index);
            if (lines[line - 1].Contains(OkMarker)) continue;
            hits.Add(new Hit(file, line, lines[line - 1]));
        }
        return hits;
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

    /// <summary>Character ranges that run off the UI thread: offloaded lambdas and the bodies of [WorkerThread] methods.</summary>
    public static List<(int Start, int End)> WorkerRegions(string clean)
    {
        var regions = new List<(int, int)>();
        foreach (Match m in Offloads.Matches(clean))
        {
            var open = m.Index + m.Length - 1;
            regions.Add((m.Index, Match(clean, open, '(', ')')));
        }
        foreach (Match m in WorkerAttribute.Matches(clean))
        {
            if (Declaration(clean, m.Index + m.Length) is not { } d) continue;
            var paramsOpen = d.ParamsOpen;
            var paramsClose = Match(clean, paramsOpen, '(', ')');
            var i = paramsClose + 1;
            while (i < clean.Length && char.IsWhiteSpace(clean[i])) i++;
            int end;
            if (i + 1 < clean.Length && clean[i] == '=' && clean[i + 1] == '>') end = StatementEnd(clean, i);
            else
            {
                var brace = clean.IndexOf('{', i);
                end = brace < 0 ? clean.Length : Match(clean, brace, '{', '}');
            }
            regions.Add((m.Index, end));
        }
        return regions;
    }

    private static bool Inside(List<(int Start, int End)> regions, int index) => regions.Any(r => index >= r.Start && index <= r.End);

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

    /// <summary>Blanks out comments and the contents of string and character literals; keeps every line break and column.</summary>
    public static string Sanitize(string src)
    {
        var sb = new StringBuilder(src);
        var i = 0;
        void Blank(int from, int to) { for (var k = from; k < to && k < sb.Length; k++) if (sb[k] != '\n' && sb[k] != '\r') sb[k] = ' '; }
        while (i < src.Length)
        {
            var ch = src[i];
            var next = i + 1 < src.Length ? src[i + 1] : '\0';
            if (ch == '/' && next == '/')
            {
                var end = src.IndexOf('\n', i);
                if (end < 0) end = src.Length;
                Blank(i, end); i = end;
            }
            else if (ch == '/' && next == '*')
            {
                var end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? src.Length : end + 2;
                Blank(i, end); i = end;
            }
            else if (ch == '"' && next == '"' && i + 2 < src.Length && src[i + 2] == '"')
            {
                var end = src.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);   // raw string literal (no nested quotes in this code base)
                end = end < 0 ? src.Length : end + 3;
                Blank(i + 1, end - 1); i = end;
            }
            else if (ch == '"' || (ch is '@' or '$' && next == '"') || (ch is '@' or '$' && next is '@' or '$' && i + 2 < src.Length && src[i + 2] == '"'))
            {
                var start = src.IndexOf('"', i);
                var verbatim = src.AsSpan(i, start - i).Contains('@');
                var j = start + 1;
                while (j < src.Length)
                {
                    if (verbatim && src[j] == '"' && j + 1 < src.Length && src[j + 1] == '"') { j += 2; continue; }
                    if (!verbatim && src[j] == '\\') { j += 2; continue; }
                    if (src[j] == '"') break;
                    j++;
                }
                Blank(start + 1, j); i = j + 1;
            }
            else if (ch == '\'')
            {
                var j = i + 1;
                if (j < src.Length && src[j] == '\\') j += 2; else j++;
                while (j < src.Length && src[j] != '\'' && src[j] != '\n') j++;
                Blank(i + 1, j); i = j + 1;
            }
            else i++;
        }
        return sb.ToString();
    }
}
