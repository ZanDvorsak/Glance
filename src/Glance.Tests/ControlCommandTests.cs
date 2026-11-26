using System.Text;
using Glance.Core.Control;
using Glance.Core.Themes;

namespace Glance.Tests;

/// <summary>The runner's control-pipe wire format (finding H2: a theme without a folder never reached the runner).</summary>
public class ControlCommandTests
{
    private static ControlCommand Parse(string line)
    {
        Assert.True(ControlCommand.TryParse(line, out var cmd, out var error), $"'{line}' should parse, got: {error}");
        return cmd!;
    }

    private static string Fails(string? line)
    {
        Assert.False(ControlCommand.TryParse(line, out var cmd, out var error), $"'{line}' should not parse");
        Assert.Null(cmd);
        Assert.False(string.IsNullOrWhiteSpace(error));
        return error;
    }

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    public static IEnumerable<object[]> AllCommands() => new[]
    {
        new object[] { ControlCommand.Status, "STATUS" },
        new object[] { ControlCommand.Reload, "RELOAD" },
        new object[] { ControlCommand.Pause, "PAUSE" },
        new object[] { ControlCommand.Resume, "RESUME" },
        new object[] { ControlCommand.Exit, "EXIT" },
        new object[] { ControlCommand.Next, "NEXT" },
        new object[] { ControlCommand.Prev, "PREV" },
        new object[] { ControlCommand.LiveClear, "LIVE_CLEAR" },
        new object[] { ControlCommand.Page(3), "PAGE 3" },
        new object[] { ControlCommand.Page(0), "PAGE 0" },
        new object[] { ControlCommand.Brightness(7), "BRIGHTNESS 7" },
        new object[] { ControlCommand.Brightness(0), "BRIGHTNESS 0" },
    };

    [Theory]
    [MemberData(nameof(AllCommands))]
    public void Every_command_encodes_to_its_wire_form_and_parses_back(ControlCommand command, string wire)
    {
        Assert.Equal(wire, command.Encode());
        Assert.Equal(command, Parse(command.Encode()));
    }

    [Fact]
    public void Live_with_a_folder_round_trips()
    {
        var cmd = Parse(ControlCommand.Live("{\"Name\":\"A\"}", @"C:\Themes\A").Encode());
        Assert.Equal(ControlVerb.Live, cmd.Verb);
        Assert.Equal(@"C:\Themes\A", cmd.Folder);
        Assert.Equal("{\"Name\":\"A\"}", cmd.ThemeJson);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Live_without_a_folder_round_trips(string? folder)
    {
        // the H2 bug: a new theme has no folder, the line had two spaces in a row and the runner rejected it
        var line = ControlCommand.Live("{}", folder).Encode();
        Assert.DoesNotContain("  ", line);
        Assert.Equal(3, line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        var cmd = Parse(line);
        Assert.Equal(ControlVerb.Live, cmd.Verb);
        Assert.Null(cmd.Folder);
        Assert.Equal("{}", cmd.ThemeJson);
    }

    [Fact]
    public void Live_keeps_spaces_and_non_ascii_characters_in_the_folder_and_theme()
    {
        const string folder = @"C:\Users\Žan Dvoršak\Mój folder\テーマ 1";
        const string json = "{\"Name\":\"Temperatura °C — čšž\",\"Note\":\"two  spaces\"}";
        var line = ControlCommand.Live(json, folder).Encode();
        Assert.Equal(3, line.Split(' ').Length);   // nothing in the payload adds separators
        var cmd = Parse(line);
        Assert.Equal(folder, cmd.Folder);
        Assert.Equal(json, cmd.ThemeJson);
    }

    [Fact]
    public void A_folder_named_like_the_placeholder_is_not_mistaken_for_no_folder()
    {
        var cmd = Parse(ControlCommand.Live("{}", ControlCommand.NoFolder).Encode());
        Assert.Equal(ControlCommand.NoFolder, cmd.Folder);
    }

    [Fact]
    public void Live_from_an_older_editor_is_still_understood()
    {
        // older editors sent "LIVE <b64 folder> <b64 json>", and an empty folder as an empty field
        var withFolder = Parse($"LIVE {B64(@"D:\My Themes\B")} {B64("{\"x\":1}")}");
        Assert.Equal(@"D:\My Themes\B", withFolder.Folder);
        Assert.Equal("{\"x\":1}", withFolder.ThemeJson);

        var noFolder = Parse($"LIVE {B64("")} {B64("{}")}");   // "LIVE  e30="
        Assert.Null(noFolder.Folder);
        Assert.Equal("{}", noFolder.ThemeJson);
    }

    [Fact]
    public void A_live_theme_without_a_folder_can_be_applied_like_the_runner_does()
    {
        var theme = new Theme { Name = "Fresh" };
        var cmd = Parse(ControlCommand.Live(theme.ToSnapshot(), theme.BaseFolder).Encode());
        var shown = new Theme { BaseFolder = cmd.Folder };
        shown.ApplySnapshot(cmd.ThemeJson!);
        Assert.Null(shown.BaseFolder);
        Assert.Equal("Fresh", shown.Name);
    }

    [Theory]
    [InlineData("status", ControlVerb.Status)]
    [InlineData("  Next  ", ControlVerb.Next)]
    [InlineData("live_clear", ControlVerb.LiveClear)]
    public void Command_words_ignore_case_and_surrounding_spaces(string line, ControlVerb verb) =>
        Assert.Equal(verb, Parse(line).Verb);

    [Theory]
    [InlineData("PAGE   2", 2)]
    [InlineData("page 2 extra", 2)]
    [InlineData("BRIGHTNESS -3", -3)]   // the runner clamps it
    [InlineData("BRIGHTNESS 12", 12)]
    public void Numbers_are_read_like_before(string line, int number) => Assert.Equal(number, Parse(line).Number);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("HELLO")]
    [InlineData("LIVECLEAR")]
    [InlineData("STATUSX")]
    public void Unknown_commands_are_rejected(string? line) => Assert.Equal("unknown command", Fails(line));

    [Theory]
    [InlineData("PAGE")]
    [InlineData("PAGE x")]
    [InlineData("PAGE 1.5")]
    [InlineData("BRIGHTNESS")]
    [InlineData("BRIGHTNESS high")]
    [InlineData("LIVE")]
    [InlineData("LIVE e30=")]
    [InlineData("LIVE - ")]
    [InlineData("LIVE - not*base64")]
    [InlineData("LIVE not*base64 e30=")]
    public void Bad_arguments_are_rejected(string line) => Fails(line);
}
