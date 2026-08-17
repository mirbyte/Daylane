namespace Daylane.Models;

internal sealed record DaySnapshot(
    DateTime LocalDay,
    long KeyCount,
    long MouseClickCount,
    IReadOnlyList<ActivitySegment> Segments,
    IReadOnlyList<AppUsageSummary> AppUsage,
    IReadOnlyList<OpenAppSummary> OpenApps);
