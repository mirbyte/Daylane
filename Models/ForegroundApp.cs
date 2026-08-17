namespace Daylane.Models;

internal readonly record struct ForegroundApp(
    string ProcessName,
    string ExePath,
    string DisplayName,
    bool IsIdle)
{
    public static ForegroundApp Idle { get; } = new("Idle", "", "Away", true);

    public static ForegroundApp Unknown { get; } = new("Unknown", "", "Unknown", false);

    public bool SameIdentity(ForegroundApp other) =>
        IsIdle == other.IsIdle
        && string.Equals(ExePath, other.ExePath, StringComparison.OrdinalIgnoreCase)
        && (ExePath.Length > 0
            || string.Equals(ProcessName, other.ProcessName, StringComparison.OrdinalIgnoreCase));
}
