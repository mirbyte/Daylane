namespace Daylane.Models;

internal sealed class OpenAppSummary
{
    public required string DisplayName { get; init; }
    public required string ProcessName { get; init; }
    public required string ExePath { get; init; }
    public TimeSpan OpenDuration { get; init; }
    public bool IsCurrentlyOpen { get; init; }
}
