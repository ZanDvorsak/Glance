using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Glance.Core.Control;

/// <summary>The commands the runner accepts on its control pipe.</summary>
public enum ControlVerb { Status, Reload, Pause, Resume, Exit, Next, Prev, Page, Brightness, Live, LiveClear }

/// <summary>
/// One command on the runner's control pipe (<c>Glance.Runner.Control</c>, one line per command).
/// The editor builds commands with the factory methods and <see cref="Encode"/>; the runner reads them with <see cref="TryParse"/>.
/// <para>
/// Wire format: <c>STATUS</c>, <c>RELOAD</c>, <c>PAUSE</c>, <c>RESUME</c>, <c>EXIT</c>, <c>NEXT</c>, <c>PREV</c>, <c>LIVE_CLEAR</c>,
/// <c>PAGE &lt;n&gt;</c> (1-based, 0 releases the pin), <c>BRIGHTNESS &lt;0-7&gt;</c> and
/// <c>LIVE &lt;base64 UTF-8 folder&gt; &lt;base64 UTF-8 theme json&gt;</c>. A theme without a folder is sent with
/// <see cref="NoFolder"/> in place of the folder; base64 never contains that character, so it cannot clash with a real folder.
/// Older editors sent an empty folder field (two spaces in a row); that is still understood.
/// </para>
/// </summary>
public sealed record ControlCommand
{
    /// <summary>Placeholder sent in the folder field of <c>LIVE</c> when the theme has no folder.</summary>
    public const string NoFolder = "-";

    private static readonly (ControlVerb Verb, string Word)[] Words =
    {
        (ControlVerb.Status, "STATUS"), (ControlVerb.Reload, "RELOAD"), (ControlVerb.Pause, "PAUSE"), (ControlVerb.Resume, "RESUME"),
        (ControlVerb.Exit, "EXIT"), (ControlVerb.Next, "NEXT"), (ControlVerb.Prev, "PREV"), (ControlVerb.Page, "PAGE"),
        (ControlVerb.Brightness, "BRIGHTNESS"), (ControlVerb.Live, "LIVE"), (ControlVerb.LiveClear, "LIVE_CLEAR"),
    };

    private ControlCommand(ControlVerb verb) { Verb = verb; }

    public ControlVerb Verb { get; }

    /// <summary>The page (PAGE, 1-based, 0 releases the pin) or brightness level (BRIGHTNESS, not clamped here).</summary>
    public int Number { get; private init; }

    /// <summary>LIVE: the theme's folder, or null when it has none.</summary>
    public string? Folder { get; private init; }

    /// <summary>LIVE: the theme snapshot JSON.</summary>
    public string? ThemeJson { get; private init; }

    public static ControlCommand Status { get; } = new(ControlVerb.Status);
    public static ControlCommand Reload { get; } = new(ControlVerb.Reload);
    public static ControlCommand Pause { get; } = new(ControlVerb.Pause);
    public static ControlCommand Resume { get; } = new(ControlVerb.Resume);
    public static ControlCommand Exit { get; } = new(ControlVerb.Exit);
    public static ControlCommand Next { get; } = new(ControlVerb.Next);
    public static ControlCommand Prev { get; } = new(ControlVerb.Prev);
    public static ControlCommand LiveClear { get; } = new(ControlVerb.LiveClear);

    /// <summary>Pins the display to page <paramref name="page"/> (1-based); 0 releases the pin.</summary>
    public static ControlCommand Page(int page) => new(ControlVerb.Page) { Number = page };

    public static ControlCommand Brightness(int level) => new(ControlVerb.Brightness) { Number = level };

    /// <summary>Shows an (unsaved) theme until <c>LIVE_CLEAR</c>. An empty folder counts as no folder.</summary>
    public static ControlCommand Live(string themeJson, string? folder) =>
        new(ControlVerb.Live) { ThemeJson = themeJson ?? throw new ArgumentNullException(nameof(themeJson)), Folder = string.IsNullOrEmpty(folder) ? null : folder };

    /// <summary>The single line sent over the pipe (without the line break).</summary>
    public string Encode()
    {
        var word = WordOf(Verb);
        return Verb switch
        {
            ControlVerb.Page or ControlVerb.Brightness => word + " " + Number.ToString(CultureInfo.InvariantCulture),
            ControlVerb.Live => word + " " + (Folder is null ? NoFolder : ToBase64(Folder)) + " " + ToBase64(ThemeJson ?? ""),
            _ => word,
        };
    }

    public override string ToString() => Encode();

    /// <summary>
    /// Reads one line from the pipe. Returns false with a short reason (sent back as <c>ERR &lt;reason&gt;</c>)
    /// for an unknown command or bad arguments. The command word is not case sensitive and surrounding spaces are ignored.
    /// </summary>
    public static bool TryParse(string? line, [NotNullWhen(true)] out ControlCommand? command, out string error)
    {
        command = null; error = "";
        line = line?.Trim() ?? "";
        var space = line.IndexOf(' ');
        var word = (space < 0 ? line : line[..space]).ToUpperInvariant();
        var rest = space < 0 ? "" : line[(space + 1)..];

        ControlVerb? verb = null;
        foreach (var (v, w) in Words) if (w == word) { verb = v; break; }
        if (verb is not { } found) { error = "unknown command"; return false; }

        switch (found)
        {
            case ControlVerb.Page:
            case ControlVerb.Brightness:
            {
                var arg = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    error = word + " needs a number";
                    return false;
                }
                command = new ControlCommand(found) { Number = n };
                return true;
            }
            case ControlVerb.Live:
            {
                // Split on single spaces without dropping empty fields: an older editor sent an empty folder as "LIVE  <json>".
                var fields = rest.Split(' ', 2);
                if (fields.Length != 2) { error = "LIVE needs a folder and a theme"; return false; }
                var folderField = fields[0];
                var jsonField = fields[1].Trim();
                if (jsonField.Length == 0) { error = "LIVE needs a theme"; return false; }
                if (!TryFromBase64(jsonField, out var json)) { error = "LIVE theme is not valid base64"; return false; }
                string? folder = null;
                // An empty field is how editors before the "-" placeholder sent "no folder" (two spaces in a row).
                // Live() would also turn a decoded "" into null; this check just says so and skips the decode.
                if (folderField.Length > 0 && folderField != NoFolder)
                {
                    if (!TryFromBase64(folderField, out folder)) { error = "LIVE folder is not valid base64"; return false; }
                }
                command = Live(json, folder);
                return true;
            }
            default:
                command = new ControlCommand(found);
                return true;
        }
    }

    private static string WordOf(ControlVerb verb)
    {
        foreach (var (v, w) in Words) if (v == verb) return w;
        throw new ArgumentOutOfRangeException(nameof(verb));
    }

    private static string ToBase64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static bool TryFromBase64(string text, out string value)
    {
        value = "";
        try { value = Encoding.UTF8.GetString(Convert.FromBase64String(text)); return true; }
        catch (FormatException) { return false; }
    }
}
