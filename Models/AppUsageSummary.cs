namespace Daylane.Models;

internal sealed class AppUsageSummary
{
    public required string DisplayName { get; init; }
    public required string ProcessName { get; init; }
    public required string ExePath { get; init; }
    public TimeSpan Duration { get; init; }
    public long KeyCount { get; init; }
    public long MouseClickCount { get; init; }
    public bool IsIdle { get; init; }
}
