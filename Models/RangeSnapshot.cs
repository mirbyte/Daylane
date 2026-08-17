namespace Daylane.Models;

internal sealed record DailyActivityPoint(DateTime LocalDay, double ActiveMinutes);

internal sealed record RangeSnapshot(
    DateTime StartLocal,
    DateTime EndLocal,
    long KeyCount,
    long MouseClickCount,
    IReadOnlyList<AppUsageSummary> AppUsage,
    IReadOnlyList<DailyActivityPoint> DailyActive);
