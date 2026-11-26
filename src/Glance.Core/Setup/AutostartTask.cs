namespace Glance.Core.Setup;

/// <summary>
/// The "Glance Runner" logon task of Glance 0.1. It started the elevated runner at sign-in with highest privileges and the
/// user's environment (review H6/H1). Glance no longer creates it (the Glance service replaced it); what is left here only
/// removes a leftover one: the installer, the service (<see cref="Service.LegacyTask"/>) and the editor's Background service
/// window. The 0.1 helpers that built and checked the task were removed after the runner-service review (N3).
/// </summary>
public static class AutostartTask
{
    public const string TaskName = "Glance Runner";

    /// <summary>The fixed arguments for removing the task with Windows' own schtasks.exe.</summary>
    public const string SchtasksDeleteArguments = "/Delete /TN \"" + TaskName + "\" /F";

    /// <summary>
    /// How the editor removes the task: Windows' own <c>schtasks.exe</c> from System32 with a fixed command line, elevated
    /// through the prompt. It reads no file, so removing never elevates a Glance copy. A task that is already gone makes
    /// schtasks fail, so callers judge success by querying afterwards.
    /// </summary>
    public static System.Diagnostics.ProcessStartInfo RemoveStartInfo() =>
        new(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), SchtasksDeleteArguments)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.SystemDirectory,
        };
}

/// <summary>The token types Windows reports through TokenElevationType.</summary>
public enum TokenElevation { Default = 1, Full = 2, Limited = 3 }
