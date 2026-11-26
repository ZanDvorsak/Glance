namespace Glance.Core.Settings;

/// <summary>What reading settings.json found.</summary>
public enum SettingsFileState
{
    /// <summary>The saved settings were read.</summary>
    Loaded,
    /// <summary>There is no settings file yet (first run): the defaults.</summary>
    Missing,
    /// <summary>The file is not valid JSON (cut off, empty, zeroed). It was kept aside; the defaults are used.</summary>
    Corrupt,
    /// <summary>The file is valid JSON but not Glance settings (wrong types, not an object). It was kept aside; the defaults are used.</summary>
    WrongContent,
    /// <summary>The file exists but another program kept it locked. The defaults are returned, and they must not be saved over the file.</summary>
    Unreadable,
}

/// <param name="Settings">The settings to use.</param>
/// <param name="State">What was found.</param>
/// <param name="KeptAs">For <see cref="SettingsFileState.Corrupt"/> and <see cref="SettingsFileState.WrongContent"/>: where the damaged file was kept.</param>
/// <param name="Problem">A sentence for the user when something went wrong, otherwise null.</param>
public sealed record SettingsRead(AppSettings Settings, SettingsFileState State, string? KeptAs, string? Problem);
