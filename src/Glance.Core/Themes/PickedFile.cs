namespace Glance.Core.Themes;

/// <summary>
/// Turns a file the user just picked in a file dialog into a path a theme may keep. Local files are used where they are
/// (Save copies them into <c>assets/</c> later). A file on a network or device path is copied right away, while the user's
/// own choice is fresh, so neither the renderer nor <see cref="Theme.Save"/> ever has to open a network path later
/// (they refuse to: <see cref="Theme.ResolveAsset"/>).
/// </summary>
public static class PickedFile
{
    /// <summary>
    /// Returns the path to store in the widget. For a network or device path the file is copied: into the theme's own
    /// <c>assets</c> folder when the theme has been saved (the result is <c>assets/name</c>), otherwise into a new folder
    /// under <paramref name="stagingRoot"/> (the result is that local full path, which Save adopts as usual).
    /// Throws if the copy fails; the caller shows the message.
    /// </summary>
    public static string Localize(string picked, Theme theme, string stagingRoot)
    {
        if (!Theme.IsNetworkOrDevicePath(picked)) return picked;
        var name = Path.GetFileName(picked);
        if (string.IsNullOrEmpty(name)) throw new IOException("Not a file: " + picked);

        if (theme.BaseFolder is { } baseFolder && !Theme.IsNetworkOrDevicePath(baseFolder))
        {
            var assets = Path.Combine(baseFolder, "assets");
            Directory.CreateDirectory(assets);
            var dest = Path.Combine(assets, name);
            for (var n = 1; File.Exists(dest); n++)   // never overwrite a file another widget may use
                dest = Path.Combine(assets, $"{Path.GetFileNameWithoutExtension(name)}_{n}{Path.GetExtension(name)}");
            File.Copy(picked, dest);
            return "assets/" + Path.GetFileName(dest);
        }

        var folder = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var staged = Path.Combine(folder, name);
        File.Copy(picked, staged);
        return staged;
    }
}
