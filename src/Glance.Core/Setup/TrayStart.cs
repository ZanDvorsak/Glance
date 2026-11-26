namespace Glance.Core.Setup;

public enum TrayStartAction { Run, RelaunchUnelevated, Refuse }

/// <summary>
/// Whether the tray app may run with the token it was started with (review M3). Glance 0.1's "Glance Runner" logon task starts
/// Glance.Runner.exe with highest privileges whatever its manifest says, and then the tray would render user files elevated with
/// the user's environment: the H1 hole again. Pure, for testing.
/// </summary>
public static class TrayStart
{
    /// <summary>Marks the copy started again with the user's normal rights (a named object, since explorer passes no arguments).</summary>
    public const string RelaunchMarker = @"Local\Glance.Runner.Relaunch";

    /// <param name="elevationType">TokenElevationType: Full means an administrator's elevated half of a split token.</param>
    /// <param name="isElevated">TokenElevation.TokenIsElevated.</param>
    /// <param name="alreadyRelaunched">This copy was started by the relaunch (and is still elevated).</param>
    public static TrayStartAction Decide(TokenElevation elevationType, bool isElevated, bool alreadyRelaunched)
    {
        // Default with elevation means UAC is off (or the built-in Administrator): every program of this user is elevated
        // anyway, so there is no normal-rights copy to fall back to and no boundary to protect.
        if (!isElevated || elevationType != TokenElevation.Full) return TrayStartAction.Run;
        return alreadyRelaunched ? TrayStartAction.Refuse : TrayStartAction.RelaunchUnelevated;
    }
}
