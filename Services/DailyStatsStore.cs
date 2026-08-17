using System.Collections.Concurrent;
using System.Diagnostics;
using Daylane.Models;
using Microsoft.Data.Sqlite;

namespace Daylane.Services;

internal sealed class DailyStatsStore : IDisposable
{
    private const int FlushRetryCount = 3;
    private const int SchemaVersion = 1;

    private readonly ConcurrentQueue<InputEvent> _buffer = new();
    private readonly string _connectionString;
    private readonly object _flushLock = new();
    private readonly object _dbWriteLock = new();
    private readonly Timer _flushTimer;
    private bool _disposed;

    public DailyStatsStore()
    {
        DatabasePath = ResolveDatabasePath();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5
        }.ConnectionString;

        InitializeDatabase();
        _flushTimer = new Timer(_ => Flush(), null, 5000, 5000);
    }

    public string DatabasePath { get; }

    public (long KeyCount, long MouseClickCount) GetTodayTotals() =>
        GetTotalsForDate(TodayKey());

    public (long KeyCount, long MouseClickCount) GetTotalsForDate(string dateKey)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT KeyCount, MouseClickCount
            FROM DailyInput
            WHERE LogDate = $date;
            """;
        command.Parameters.AddWithValue("$date", dateKey);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return (0, 0);
        }

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    public long OpenSegment(ForegroundApp app, DateTime startUtc)
    {
        lock (_dbWriteLock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ActivitySegment (
                    StartUtc, EndUtc, ProcessName, ExePath, DisplayName, IsIdle, KeyCount, MouseClickCount)
                VALUES ($start, NULL, $process, $exe, $display, $idle, 0, 0);
                """;
            command.Parameters.AddWithValue("$start", ToUtcText(startUtc));
            command.Parameters.AddWithValue("$process", app.ProcessName);
            command.Parameters.AddWithValue("$exe", app.ExePath);
            command.Parameters.AddWithValue("$display", app.DisplayName);
            command.Parameters.AddWithValue("$idle", app.IsIdle ? 1 : 0);
            command.ExecuteNonQuery();

            using var idCommand = connection.CreateCommand();
            idCommand.CommandText = "SELECT last_insert_rowid();";
            return (long)(idCommand.ExecuteScalar() ?? 0L);
        }
    }

    public void CloseSegment(long segmentId, DateTime endUtc, long keyCount, long mouseClickCount)
    {
        lock (_dbWriteLock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE ActivitySegment
                SET EndUtc = $end,
                    KeyCount = $keys,
                    MouseClickCount = $clicks
                WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$end", ToUtcText(endUtc));
            command.Parameters.AddWithValue("$keys", keyCount);
            command.Parameters.AddWithValue("$clicks", mouseClickCount);
            command.Parameters.AddWithValue("$id", segmentId);
            command.ExecuteNonQuery();
        }
    }

    public void UpdateOpenSegmentCounts(long segmentId, long keyCount, long mouseClickCount)
    {
        lock (_dbWriteLock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE ActivitySegment
                SET KeyCount = $keys,
                    MouseClickCount = $clicks
                WHERE Id = $id AND EndUtc IS NULL;
                """;
            command.Parameters.AddWithValue("$keys", keyCount);
            command.Parameters.AddWithValue("$clicks", mouseClickCount);
            command.Parameters.AddWithValue("$id", segmentId);
            command.ExecuteNonQuery();
        }
    }

    public void CloseOrphanOpenSegments(DateTime endUtc)
    {
        lock (_dbWriteLock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE ActivitySegment
                SET EndUtc = $end
                WHERE EndUtc IS NULL;
                """;
            command.Parameters.AddWithValue("$end", ToUtcText(endUtc));
            command.ExecuteNonQuery();

            using var openApps = connection.CreateCommand();
            openApps.CommandText = """
                UPDATE OpenAppSegment
                SET EndUtc = $end
                WHERE EndUtc IS NULL;
                """;
            openApps.Parameters.AddWithValue("$end", ToUtcText(endUtc));
            openApps.ExecuteNonQuery();
        }
    }

    public long OpenOpenAppSegment(ForegroundApp app, DateTime startUtc)
    {
        lock (_dbWriteLock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO OpenAppSegment (StartUtc, EndUtc, ProcessName, ExePath, DisplayName)
                VALUES ($start, NULL, $process, $exe, $display);
                """;
            command.Parameters.AddWithValue("$start", ToUtcText(startUtc));
            command.Parameters.AddWithValue("$process", app.ProcessName);
            command.Parameters.AddWithValue("$exe", app.ExePath);
            command.Parameters.AddWithValue("$display", app.DisplayName);
            command.ExecuteNonQuery();

            using var idCommand = connection.CreateCommand();
            idCommand.CommandText = "SELECT last_insert_rowid();";
            return (long)(idCommand.ExecuteScalar() ?? 0L);
        }
    }

    public void CloseOpenAppSegment(long segmentId, DateTime endUtc)
    {
        lock (_dbWriteLock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE OpenAppSegment
                SET EndUtc = $end
                WHERE Id = $id;
                """;
            command.Parameters.AddWithValue("$end", ToUtcText(endUtc));
            command.Parameters.AddWithValue("$id", segmentId);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<OpenAppSummary> GetOpenAppsForLocalDay(DateTime localDay) =>
        AggregateOpenApps(localDay.Date, localDay.Date.AddDays(1));

    public IReadOnlyList<OpenAppSummary> AggregateOpenApps(
        DateTime rangeStartLocal,
        DateTime rangeEndExclusiveLocal)
    {
        DateTime rangeStartUtc = rangeStartLocal.ToUniversalTime();
        DateTime rangeEndUtc = rangeEndExclusiveLocal.ToUniversalTime();
        DateTime nowUtc = DateTime.UtcNow;
        string nowText = ToUtcText(nowUtc);

        lock (_dbWriteLock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT StartUtc, EndUtc, ProcessName, ExePath, DisplayName
                FROM OpenAppSegment
                WHERE StartUtc < $rangeEnd
                  AND COALESCE(EndUtc, $now) > $rangeStart;
                """;
            command.Parameters.AddWithValue("$rangeStart", ToUtcText(rangeStartUtc));
            command.Parameters.AddWithValue("$rangeEnd", ToUtcText(rangeEndUtc));
            command.Parameters.AddWithValue("$now", nowText);

            var totals = new Dictionary<string, OpenAccumulator>(StringComparer.OrdinalIgnoreCase);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                DateTime start = ParseUtc(reader.GetString(0));
                DateTime? endNullable = reader.IsDBNull(1) ? null : ParseUtc(reader.GetString(1));
                DateTime end = endNullable ?? nowUtc;
                bool currentlyOpen = endNullable is null
                    && nowUtc >= rangeStartUtc
                    && nowUtc < rangeEndUtc;

                if (start < rangeStartUtc)
                {
                    start = rangeStartUtc;
                }

                if (end > rangeEndUtc)
                {
                    end = rangeEndUtc;
                }

                if (end > nowUtc)
                {
                    end = nowUtc;
                }

                if (end <= start)
                {
                    continue;
                }

                string processName = reader.GetString(2);
                string exePath = reader.GetString(3);
                string displayName = reader.GetString(4);
                string key = string.IsNullOrEmpty(exePath) ? processName : exePath;

                if (!totals.TryGetValue(key, out var acc))
                {
                    acc = new OpenAccumulator
                    {
                        DisplayName = displayName,
                        ProcessName = processName,
                        ExePath = exePath
                    };
                    totals[key] = acc;
                }

                acc.OpenDuration += end - start;
                if (currentlyOpen)
                {
                    acc.IsCurrentlyOpen = true;
                }
            }

            return totals.Values
                .Select(a => new OpenAppSummary
                {
                    DisplayName = a.DisplayName,
                    ProcessName = a.ProcessName,
                    ExePath = a.ExePath,
                    OpenDuration = a.OpenDuration,
                    IsCurrentlyOpen = a.IsCurrentlyOpen
                })
                .OrderByDescending(a => a.IsCurrentlyOpen)
                .ThenByDescending(a => a.OpenDuration)
                .ToList();
        }
    }

    public (long KeyCount, long MouseClickCount) GetTotalsForDateRange(
        DateTime startLocalInclusive,
        DateTime endLocalInclusive)
    {
        string startKey = startLocalInclusive.Date.ToString("yyyy-MM-dd");
        string endKey = endLocalInclusive.Date.ToString("yyyy-MM-dd");

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(KeyCount), 0), COALESCE(SUM(MouseClickCount), 0)
            FROM DailyInput
            WHERE LogDate >= $start AND LogDate <= $end;
            """;
        command.Parameters.AddWithValue("$start", startKey);
        command.Parameters.AddWithValue("$end", endKey);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return (0, 0);
        }

        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    public IReadOnlyList<ActivitySegment> GetSegmentsForLocalDay(DateTime localDay) =>
        GetSegmentsForLocalRange(localDay.Date, localDay.Date.AddDays(1));

    public IReadOnlyList<ActivitySegment> GetSegmentsForLocalRange(
        DateTime rangeStartLocal,
        DateTime rangeEndExclusiveLocal)
    {
        DateTime rangeStartUtc = rangeStartLocal.ToUniversalTime();
        DateTime rangeEndUtc = rangeEndExclusiveLocal.ToUniversalTime();
        string nowUtc = ToUtcText(DateTime.UtcNow);

        lock (_dbWriteLock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Id, StartUtc, EndUtc, ProcessName, ExePath, DisplayName, IsIdle, KeyCount, MouseClickCount
                FROM ActivitySegment
                WHERE StartUtc < $rangeEnd
                  AND COALESCE(EndUtc, $now) > $rangeStart
                ORDER BY StartUtc;
                """;
            command.Parameters.AddWithValue("$rangeStart", ToUtcText(rangeStartUtc));
            command.Parameters.AddWithValue("$rangeEnd", ToUtcText(rangeEndUtc));
            command.Parameters.AddWithValue("$now", nowUtc);

            var segments = new List<ActivitySegment>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                segments.Add(ReadSegment(reader));
            }

            return segments;
        }
    }

    public IReadOnlyList<AppUsageSummary> GetAppUsageForLocalDay(DateTime localDay) =>
        AggregateAppUsage(GetSegmentsForLocalDay(localDay), localDay);

    public IReadOnlyList<AppUsageSummary> AggregateAppUsage(
        IReadOnlyList<ActivitySegment> segments,
        DateTime localDay) =>
        AggregateAppUsage(segments, localDay.Date, localDay.Date.AddDays(1));

    public IReadOnlyList<AppUsageSummary> AggregateAppUsage(
        IReadOnlyList<ActivitySegment> segments,
        DateTime rangeStartLocal,
        DateTime rangeEndExclusiveLocal)
    {
        DateTime rangeStartUtc = rangeStartLocal.ToUniversalTime();
        DateTime rangeEndUtc = rangeEndExclusiveLocal.ToUniversalTime();
        DateTime nowUtc = DateTime.UtcNow;

        var totals = new Dictionary<string, Accumulator>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in segments)
        {
            DateTime start = segment.StartUtc < rangeStartUtc ? rangeStartUtc : segment.StartUtc;
            DateTime end = segment.EffectiveEndUtc > rangeEndUtc ? rangeEndUtc : segment.EffectiveEndUtc;
            if (end > nowUtc)
            {
                end = nowUtc;
            }

            if (end <= start)
            {
                continue;
            }

            string key = segment.IsIdle
                ? "__idle__"
                : (string.IsNullOrEmpty(segment.ExePath) ? segment.ProcessName : segment.ExePath);

            if (!totals.TryGetValue(key, out var acc))
            {
                acc = new Accumulator
                {
                    DisplayName = segment.DisplayName,
                    ProcessName = segment.ProcessName,
                    ExePath = segment.ExePath,
                    IsIdle = segment.IsIdle
                };
                totals[key] = acc;
            }

            acc.Duration += end - start;
            acc.KeyCount += segment.KeyCount;
            acc.MouseClickCount += segment.MouseClickCount;
        }

        return totals.Values
            .Select(a => new AppUsageSummary
            {
                DisplayName = a.DisplayName,
                ProcessName = a.ProcessName,
                ExePath = a.ExePath,
                Duration = a.Duration,
                KeyCount = a.KeyCount,
                MouseClickCount = a.MouseClickCount,
                IsIdle = a.IsIdle
            })
            .OrderByDescending(a => a.Duration)
            .ToList();
    }

    public void Enqueue(InputEvent inputEvent) => _buffer.Enqueue(inputEvent);

    public void Flush()
    {
        lock (_flushLock)
        {
            if (_buffer.IsEmpty)
            {
                return;
            }

            var batch = new List<InputEvent>();
            while (_buffer.TryDequeue(out var inputEvent))
            {
                batch.Add(inputEvent);
            }

            lock (_dbWriteLock)
            {
                if (!TryWriteBatch(_connectionString, batch))
                {
                    foreach (var inputEvent in batch)
                    {
                        _buffer.Enqueue(inputEvent);
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _flushTimer.Dispose();
        Flush();
    }

    private static string ResolveDatabasePath() =>
        Path.Combine(AppContext.BaseDirectory, "daylane.db");

    private static bool TryWriteBatch(string connectionString, List<InputEvent> batch)
    {
        var keyCountsByDate = new Dictionary<string, int>();
        var mouseCountsByDate = new Dictionary<string, int>();

        foreach (var inputEvent in batch)
        {
            string dateKey = ToLocalDateKey(inputEvent.TimestampUtc);

            if (inputEvent.EventType == "Key")
            {
                keyCountsByDate[dateKey] = keyCountsByDate.GetValueOrDefault(dateKey) + 1;
            }
            else if (inputEvent.EventType == "Mouse")
            {
                mouseCountsByDate[dateKey] = mouseCountsByDate.GetValueOrDefault(dateKey) + 1;
            }
        }

        var allDates = keyCountsByDate.Keys.Union(mouseCountsByDate.Keys);
        if (!allDates.Any())
        {
            return true;
        }

        for (int attempt = 0; attempt < FlushRetryCount; attempt++)
        {
            try
            {
                using var connection = new SqliteConnection(connectionString);
                connection.Open();
                using var transaction = connection.BeginTransaction();

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO DailyInput (LogDate, KeyCount, MouseClickCount)
                    VALUES ($date, $keys, $clicks)
                    ON CONFLICT(LogDate) DO UPDATE SET
                        KeyCount = KeyCount + excluded.KeyCount,
                        MouseClickCount = MouseClickCount + excluded.MouseClickCount;
                    """;

                var dateParam = command.Parameters.Add("$date", SqliteType.Text);
                var keysParam = command.Parameters.Add("$keys", SqliteType.Integer);
                var clicksParam = command.Parameters.Add("$clicks", SqliteType.Integer);

                foreach (string date in allDates)
                {
                    dateParam.Value = date;
                    keysParam.Value = keyCountsByDate.GetValueOrDefault(date);
                    clicksParam.Value = mouseCountsByDate.GetValueOrDefault(date);
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
                return true;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 5 && attempt < FlushRetryCount - 1)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (SqliteException ex)
            {
                Debug.WriteLine($"Failed to flush input stats: {ex.Message}");
                return false;
            }
        }

        return false;
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA busy_timeout=5000;
                """;
            pragma.ExecuteNonQuery();
        }

        int version;
        using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt32(versionCommand.ExecuteScalar());
        }

        if (version < 1)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS DailyInput (
                    LogDate TEXT PRIMARY KEY,
                    KeyCount INTEGER NOT NULL DEFAULT 0,
                    MouseClickCount INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS ActivitySegment (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    StartUtc TEXT NOT NULL,
                    EndUtc TEXT NULL,
                    ProcessName TEXT NOT NULL,
                    ExePath TEXT NOT NULL,
                    DisplayName TEXT NOT NULL,
                    IsIdle INTEGER NOT NULL DEFAULT 0,
                    KeyCount INTEGER NOT NULL DEFAULT 0,
                    MouseClickCount INTEGER NOT NULL DEFAULT 0
                );

                CREATE INDEX IF NOT EXISTS IX_ActivitySegment_StartEnd
                    ON ActivitySegment (StartUtc, EndUtc);

                CREATE INDEX IF NOT EXISTS IX_ActivitySegment_ExePath_Start
                    ON ActivitySegment (ExePath, StartUtc);

                CREATE TABLE IF NOT EXISTS OpenAppSegment (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    StartUtc TEXT NOT NULL,
                    EndUtc TEXT NULL,
                    ProcessName TEXT NOT NULL,
                    ExePath TEXT NOT NULL,
                    DisplayName TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_OpenAppSegment_StartEnd
                    ON OpenAppSegment (StartUtc, EndUtc);

                CREATE INDEX IF NOT EXISTS IX_OpenAppSegment_ExePath_Start
                    ON OpenAppSegment (ExePath, StartUtc);
                """;
            command.ExecuteNonQuery();
        }

        if (version < SchemaVersion)
        {
            using var setVersion = connection.CreateCommand();
            setVersion.CommandText = $"PRAGMA user_version = {SchemaVersion};";
            setVersion.ExecuteNonQuery();
        }
    }

    private static ActivitySegment ReadSegment(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetInt64(0),
            StartUtc = ParseUtc(reader.GetString(1)),
            EndUtc = reader.IsDBNull(2) ? null : ParseUtc(reader.GetString(2)),
            ProcessName = reader.GetString(3),
            ExePath = reader.GetString(4),
            DisplayName = reader.GetString(5),
            IsIdle = reader.GetInt64(6) != 0,
            KeyCount = reader.GetInt64(7),
            MouseClickCount = reader.GetInt64(8)
        };

    private static string TodayKey() => DateTime.Now.ToString("yyyy-MM-dd");

    private static string ToLocalDateKey(DateTime timestampUtc) =>
        timestampUtc.ToLocalTime().ToString("yyyy-MM-dd");

    private static string ToUtcText(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O");

    private static DateTime ParseUtc(string text) =>
        DateTime.Parse(text, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    private sealed class Accumulator
    {
        public required string DisplayName { get; init; }
        public required string ProcessName { get; init; }
        public required string ExePath { get; init; }
        public bool IsIdle { get; init; }
        public TimeSpan Duration { get; set; }
        public long KeyCount { get; set; }
        public long MouseClickCount { get; set; }
    }

    private sealed class OpenAccumulator
    {
        public required string DisplayName { get; init; }
        public required string ProcessName { get; init; }
        public required string ExePath { get; init; }
        public TimeSpan OpenDuration { get; set; }
        public bool IsCurrentlyOpen { get; set; }
    }
}
