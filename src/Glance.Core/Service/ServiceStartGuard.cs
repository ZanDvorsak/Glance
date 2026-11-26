using Glance.Core.Security;

namespace Glance.Core.Service;

/// <summary>
/// The service's check of its own program folder before it does anything as SYSTEM (review H1). The installer puts Glance in
/// Program Files and resets the folder's rules, but a folder whose rules were loosened later (or an install into a folder every
/// user can change) must not run as SYSTEM. It cannot stop a replaced exe (that never runs this check), only catch the rest.
/// </summary>
public static class ServiceStartGuard
{
    /// <param name="checkExecutable">The trust check: <c>FolderTrust.CheckExecutable(exe, TrustScope.FolderTree)</c> in the service.</param>
    /// <returns>Null when the service may run; otherwise why it refuses.</returns>
    public static string? Refusal(string? exePath, Func<string, TrustVerdict> checkExecutable)
    {
        if (string.IsNullOrEmpty(exePath)) return "The Glance service could not find its own program file, so it does not run.";
        var folder = Path.GetDirectoryName(exePath) ?? exePath;
        TrustVerdict verdict;
        try { verdict = checkExecutable(exePath); }
        catch (Exception ex) { verdict = TrustVerdict.No("its permissions could not be read: " + ex.Message); }
        if (verdict.Trusted) return null;
        return $"The Glance service does not run from {folder}, because that folder can be changed without administrator rights: " +
               $"{verdict.Reason} Reinstall Glance with the installer, which puts it in Program Files.";
    }
}
