using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Glance.Tests;

/// <summary>
/// Guard for the Glance 0.2.0 deadlock (docs/design/fix-tray-deadlock.md): Glance.Core is called from the UI threads of the
/// tray app and the editor, often blocking on the result, so no await in Core may resume on the caller's context.
/// The build already fails on a missing ConfigureAwait (CA2007 is an error for Core); this test checks that the rule stays
/// switched on and scans the source itself, so neither guard can be dropped quietly.
/// </summary>
public sealed class ConfigureAwaitGuardTests
{
    private static string CoreFolder([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "Glance.Core"));

    /// <summary>Every await expression in <paramref name="source"/> (comments and strings removed) that lacks ConfigureAwait(false).</summary>
    internal static List<string> AwaitsWithoutConfigureAwait(string source)
    {
        var code = StripCommentsAndStrings(source);
        var missing = new List<string>();
        foreach (Match m in Regex.Matches(code, @"\bawait\b"))
        {
            // the awaited expression: up to the first ; , { } or an unmatched closing bracket at its own nesting level
            int depth = 0, i = m.Index + m.Length;
            for (; i < code.Length; i++)
            {
                char c = code[i];
                if (c is '(' or '[') depth++;
                else if (c is ')' or ']') { if (depth == 0) break; depth--; }
                else if (depth == 0 && c is ';' or ',' or '{' or '}') break;
            }
            var expr = code[m.Index..i];
            if (!Regex.IsMatch(expr, @"\.ConfigureAwait\(\s*false\s*\)"))
            {
                int line = code.Take(m.Index).Count(ch => ch == '\n') + 1;
                missing.Add($"line {line}: {Regex.Replace(expr, @"\s+", " ").Trim()}");
            }
        }
        return missing;
    }

    /// <summary>Blanks out comments and string/char literals (keeping line breaks), so neither can hide or fake an await.</summary>
    private static string StripCommentsAndStrings(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; if (i < s.Length) sb.Append('\n'); continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) { if (s[i] == '\n') sb.Append('\n'); i++; }
                i++; continue;
            }
            if (c == '"' || c == '\'')
            {
                bool verbatim = i > 0 && s[i - 1] == '@';
                sb.Append(c == '"' ? "\"\"" : "' '");
                i++;
                while (i < s.Length)
                {
                    if (verbatim && s[i] == '"' && i + 1 < s.Length && s[i + 1] == '"') { i += 2; continue; }
                    if (!verbatim && s[i] == '\\') { i += 2; continue; }
                    if (s[i] == c) break;
                    if (s[i] == '\n') sb.Append('\n');
                    i++;
                }
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    [Fact]
    public void Every_await_in_Glance_Core_says_ConfigureAwait_false()
    {
        var files = Directory.EnumerateFiles(CoreFolder(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.NotEmpty(files);
        int awaits = 0;
        var problems = new List<string>();
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            awaits += Regex.Matches(StripCommentsAndStrings(text), @"\bawait\b").Count;
            problems.AddRange(AwaitsWithoutConfigureAwait(text).Select(p => $"{Path.GetRelativePath(CoreFolder(), f)} {p}"));
        }
        Assert.True(awaits > 10, $"only {awaits} awaits found; is the scan looking in the right folder ({CoreFolder()})?");
        Assert.True(problems.Count == 0, "Awaits in Glance.Core without ConfigureAwait(false) (they deadlock a blocked UI thread):\n" + string.Join("\n", problems));
    }

    [Fact]
    public void The_build_rule_CA2007_stays_an_error_for_Glance_Core()
    {
        var csproj = File.ReadAllText(Path.Combine(CoreFolder(), "Glance.Core.csproj"));
        Assert.Contains("<GlobalAnalyzerConfigFiles Include=\"Core.globalconfig\"", csproj);
        var config = File.ReadAllText(Path.Combine(CoreFolder(), "Core.globalconfig"));
        Assert.Matches(@"(?m)^\s*is_global\s*=\s*true\s*$", config);
        Assert.Matches(@"(?m)^\s*dotnet_diagnostic\.CA2007\.severity\s*=\s*error\s*$", config);
    }

    [Theory]
    [InlineData("await Task.Delay(1);", 1)]
    [InlineData("await Task.Delay(1).ConfigureAwait(false);", 0)]
    [InlineData("await Task.Delay(1).ConfigureAwait(true);", 1)]
    [InlineData("while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line) { }", 0)]
    [InlineData("while (await reader.ReadLineAsync(ct) is { } line) { await x.ConfigureAwait(false); }", 1)]
    [InlineData("if (await ReadUpTo(s, Foo(a, b), ct) < 3) throw new X(\"await me\");", 1)]
    [InlineData("var t = Task.Run(async () => { await A().ConfigureAwait(false); return await B(); });", 1)]
    [InlineData("// await Task.Delay(1);\nint x = 1; /* await y; */ var s = \"await z;\";", 0)]
    public void The_scan_finds_awaits_without_ConfigureAwait_false(string code, int expected)
        => Assert.Equal(expected, AwaitsWithoutConfigureAwait(code).Count);
}
