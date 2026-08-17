using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Daylane.Models;

namespace Daylane.Services;

internal sealed class TrackingService : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan UiRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly DailyStatsStore _store;
    private readonly InputHook _hook;
    private readonly ForegroundTracker _foreground;
    private readonly OpenAppTracker _openApps;
    private readonly Timer _midnightCheckTimer;
    private readonly Timer _activityRefreshTimer;
    private readonly object _rolloverLock = new();
    private readonly object _segmentStateLock = new();
    private readonly object _openAppsLock = new();
    private readonly Dictionary<string, OpenAppState> _openAppStates = new(StringComparer.OrdinalIgnoreCase);
    private string _currentDateKey;
    private long _keyPressCount;
    private long _mouseClickCount;
    private OpenSegmentState? _openSegment;
    private string _currentAppName = "Starting…";
    private volatile bool _uiVisible = true;
    private bool _disposed;

    public TrackingService()
    {
        _store = new DailyStatsStore();
        _store.CloseOrphanOpenSegments(DateTime.UtcNow);
        _currentDateKey = TodayKey();
        (long keys, long clicks) = _store.GetTodayTotals();
        _keyPressCount = keys;
        _mouseClickCount = clicks;
        _hook = new InputHook(OnInputCaptured);
        _foreground = new ForegroundTracker();
        _foreground.Changed += OnForegroundChanged;
        _openApps = new OpenAppTracker();
        _openApps.Changed += OnOpenAppsChanged;
        _midnightCheckTimer = new Timer(_ => CheckDateRollover(), null, 60000, 60000);
        _activityRefreshTimer = new Timer(
            _ => PublishActivity(),
            null,
            UiRefreshInterval,
            UiRefreshInterval);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DatabasePath => _store.DatabasePath;

    public long KeyPressCount => _keyPressCount;

    public long MouseClickCount => _mouseClickCount;

    public bool IsRunning { get; private set; }

    public string CurrentAppName => _currentAppName;

    public string CurrentDateKey => _currentDateKey;

    public IReadOnlyList<ActivitySegment> TodaySegments { get; private set; } = [];

    public IReadOnlyList<AppUsageSummary> TodayAppUsage { get; private set; } = [];

    public IReadOnlyList<OpenAppSummary> TodayOpenApps { get; private set; } = [];

    public DaySnapshot GetDaySnapshot(DateTime localDay)
    {
        DateTime day = localDay.Date;
        string dateKey = day.ToString("yyyy-MM-dd");
        bool isToday = dateKey == TodayKey();

        long keys;
        long clicks;
        if (isToday)
        {
            keys = _keyPressCount;
            clicks = _mouseClickCount;
            FlushOpenSegmentCounts();
        }
        else
        {
            (keys, clicks) = _store.GetTotalsForDate(dateKey);
        }

        var segments = _store.GetSegmentsForLocalDay(day);
        return new DaySnapshot(
            day,
            keys,
            clicks,
            segments,
            _store.AggregateAppUsage(segments, day),
            _store.GetOpenAppsForLocalDay(day));
    }

    public RangeSnapshot GetRangeSnapshot(DateTime startLocalInclusive, DateTime endLocalInclusive)
    {
        DateTime start = startLocalInclusive.Date;
        DateTime end = endLocalInclusive.Date;
        DateTime today = DateTime.Today;
        if (end > today)
        {
            end = today;
        }

        if (end < start)
        {
            end = start;
        }

        FlushOpenSegmentCounts();

        (long keys, long clicks) = _store.GetTotalsForDateRange(start, end);
        if (start <= today && today <= end)
        {
            (long storeTodayKeys, long storeTodayClicks) = _store.GetTotalsForDate(TodayKey());
            keys = keys - storeTodayKeys + _keyPressCount;
            clicks = clicks - storeTodayClicks + _mouseClickCount;
        }

        DateTime endExclusive = end.AddDays(1);
        var segments = _store.GetSegmentsForLocalRange(start, endExclusive);
        var appUsage = _store.AggregateAppUsage(segments, start, endExclusive);
        var dailyActive = BuildDailyActiveMinutes(segments, start, end);

        return new RangeSnapshot(start, end, keys, clicks, appUsage, dailyActive);
    }

    private void FlushOpenSegmentCounts()
    {
        lock (_segmentStateLock)
        {
            if (_openSegment is { } open)
            {
                _store.UpdateOpenSegmentCounts(open.Id, open.KeyCount, open.MouseClickCount);
            }
        }
    }

    private static IReadOnlyList<DailyActivityPoint> BuildDailyActiveMinutes(
        IReadOnlyList<ActivitySegment> segments,
        DateTime startLocal,
        DateTime endLocalInclusive)
    {
        DateTime nowUtc = DateTime.UtcNow;
        int dayCount = (endLocalInclusive - startLocal).Days + 1;
        var minutes = new double[dayCount];

        foreach (var segment in segments)
        {
            if (segment.IsIdle)
            {
                continue;
            }

            DateTime rangeStartUtc = startLocal.ToUniversalTime();
            DateTime rangeEndUtc = endLocalInclusive.AddDays(1).ToUniversalTime();
            DateTime segStart = segment.StartUtc < rangeStartUtc ? rangeStartUtc : segment.StartUtc;
            DateTime segEnd = segment.EffectiveEndUtc > rangeEndUtc ? rangeEndUtc : segment.EffectiveEndUtc;
            if (segEnd > nowUtc)
            {
                segEnd = nowUtc;
            }

            while (segStart < segEnd)
            {
                DateTime local = segStart.ToLocalTime();
                DateTime dayStartLocal = local.Date;
                DateTime dayEndLocal = dayStartLocal.AddDays(1);
                DateTime sliceEnd = segEnd < dayEndLocal.ToUniversalTime()
                    ? segEnd
                    : dayEndLocal.ToUniversalTime();
                int index = (dayStartLocal - startLocal).Days;
                if (index >= 0 && index < dayCount)
                {
                    minutes[index] += (sliceEnd - segStart).TotalMinutes;
                }

                segStart = sliceEnd;
            }
        }

        var points = new DailyActivityPoint[dayCount];
        for (int i = 0; i < dayCount; i++)
        {
            points[i] = new DailyActivityPoint(startLocal.AddDays(i), minutes[i]);
        }

        return points;
    }

    public void SetUiVisible(bool visible)
    {
        if (_uiVisible == visible)
        {
            return;
        }

        _uiVisible = visible;
        if (visible)
        {
            PublishActivity(force: true);
        }
    }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _hook.Install();
        _foreground.Start();
        _openApps.Start();
        IsRunning = true;
        OnPropertyChanged(nameof(IsRunning));
        PublishActivity();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _midnightCheckTimer.Dispose();
        _activityRefreshTimer.Dispose();
        _foreground.Changed -= OnForegroundChanged;
        _foreground.Dispose();
        _openApps.Changed -= OnOpenAppsChanged;
        _openApps.Dispose();

        CloseOpenSegment(DateTime.UtcNow);
        CloseAllOpenApps(DateTime.UtcNow);

        if (IsRunning)
        {
            _hook.Dispose();
            IsRunning = false;
        }

        _store.Dispose();
    }

    private void OnForegroundChanged(ForegroundApp app)
    {
        if (_disposed)
        {
            return;
        }

        CheckDateRollover();

        // Entering Away: backdate to last input so the inactive span is fully visible.
        // Leaving Away / app switch: boundary is now (same as Inactivity Timer wake).
        DateTime boundaryUtc = app.IsIdle
            ? IdleMonitor.LastInputUtc()
            : DateTime.UtcNow;

        SwitchSegment(app, boundaryUtc);
        PublishActivity();
    }

    private void OnOpenAppsChanged(IReadOnlyList<ForegroundApp> apps)
    {
        if (_disposed)
        {
            return;
        }

        CheckDateRollover();
        SyncOpenApps(apps, DateTime.UtcNow);
        PublishActivity();
    }

    private void SyncOpenApps(IReadOnlyList<ForegroundApp> apps, DateTime boundaryUtc)
    {
        lock (_openAppsLock)
        {
            var sampledKeys = new HashSet<string>(
                apps.Select(OpenAppTracker.IdentityKey),
                StringComparer.OrdinalIgnoreCase);

            foreach (var key in _openAppStates.Keys.ToList())
            {
                if (sampledKeys.Contains(key))
                {
                    continue;
                }

                if (_openAppStates.Remove(key, out OpenAppState? state))
                {
                    DateTime closeAt = boundaryUtc < state.StartedUtc ? state.StartedUtc : boundaryUtc;
                    _store.CloseOpenAppSegment(state.Id, closeAt);
                }
            }

            foreach (ForegroundApp app in apps)
            {
                string key = OpenAppTracker.IdentityKey(app);
                if (_openAppStates.ContainsKey(key))
                {
                    continue;
                }

                long id = _store.OpenOpenAppSegment(app, boundaryUtc);
                _openAppStates[key] = new OpenAppState(id, app, boundaryUtc);
            }
        }
    }

    private void CloseAllOpenApps(DateTime utcNow)
    {
        lock (_openAppsLock)
        {
            foreach (OpenAppState state in _openAppStates.Values)
            {
                _store.CloseOpenAppSegment(state.Id, utcNow);
            }

            _openAppStates.Clear();
        }
    }

    // Called from the input-hook thread. Must stay lock-free and allocation-light.
    private void OnInputCaptured(InputEvent inputEvent)
    {
        _store.Enqueue(inputEvent);

        if (inputEvent.EventType == "Key")
        {
            Interlocked.Increment(ref _keyPressCount);
            Volatile.Read(ref _openSegment)?.AddKey();
        }
        else if (inputEvent.EventType == "Mouse")
        {
            Interlocked.Increment(ref _mouseClickCount);
            Volatile.Read(ref _openSegment)?.AddClick();
        }
    }

    private void SwitchSegment(ForegroundApp app, DateTime boundaryUtc)
    {
        lock (_segmentStateLock)
        {
            if (_openSegment is { } open && open.App.SameIdentity(app))
            {
                return;
            }

            if (_openSegment is { } current)
            {
                DateTime closeAt = boundaryUtc;
                if (closeAt < current.StartedUtc)
                {
                    closeAt = current.StartedUtc;
                }

                if (closeAt > DateTime.UtcNow)
                {
                    closeAt = DateTime.UtcNow;
                }

                _store.CloseSegment(current.Id, closeAt, current.KeyCount, current.MouseClickCount);
                Volatile.Write(ref _openSegment, null);

                // Keep Away contiguous with the closed app segment.
                if (app.IsIdle && boundaryUtc < closeAt)
                {
                    boundaryUtc = closeAt;
                }
            }

            DateTime openAt = boundaryUtc;
            if (openAt > DateTime.UtcNow)
            {
                openAt = DateTime.UtcNow;
            }

            long id = _store.OpenSegment(app, openAt);
            Volatile.Write(ref _openSegment, new OpenSegmentState(id, app, openAt));
            _currentAppName = app.DisplayName;
        }

        Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(CurrentAppName)));
    }

    private void CloseOpenSegment(DateTime utcNow)
    {
        lock (_segmentStateLock)
        {
            if (_openSegment is not { } current)
            {
                return;
            }

            _store.CloseSegment(current.Id, utcNow, current.KeyCount, current.MouseClickCount);
            Volatile.Write(ref _openSegment, null);
        }
    }

    private void PublishActivity(bool force = false)
    {
        if (_disposed)
        {
            return;
        }

        if (!force && !_uiVisible)
        {
            return;
        }

        lock (_segmentStateLock)
        {
            if (_openSegment is { } open)
            {
                _store.UpdateOpenSegmentCounts(open.Id, open.KeyCount, open.MouseClickCount);
            }
        }

        DateTime today = DateTime.Now;
        var segments = _store.GetSegmentsForLocalDay(today);
        var usage = _store.AggregateAppUsage(segments, today);
        var openApps = _store.GetOpenAppsForLocalDay(today);

        Dispatcher.UIThread.Post(() =>
        {
            TodaySegments = segments;
            TodayAppUsage = usage;
            TodayOpenApps = openApps;
            OnPropertyChanged(nameof(TodaySegments));
            OnPropertyChanged(nameof(TodayAppUsage));
            OnPropertyChanged(nameof(TodayOpenApps));
            OnPropertyChanged(nameof(KeyPressCount));
            OnPropertyChanged(nameof(MouseClickCount));
        });
    }

    private void CheckDateRollover()
    {
        string today = TodayKey();
        if (today == _currentDateKey)
        {
            return;
        }

        lock (_rolloverLock)
        {
            today = TodayKey();
            if (today == _currentDateKey)
            {
                return;
            }

            DateTime utcNow = DateTime.UtcNow;
            ForegroundApp? resumeApp = null;

            lock (_segmentStateLock)
            {
                if (_openSegment is { } open)
                {
                    resumeApp = open.App;
                    _store.CloseSegment(open.Id, utcNow, open.KeyCount, open.MouseClickCount);
                    Volatile.Write(ref _openSegment, null);
                }
            }

            _currentDateKey = today;
            _store.Flush();
            (long keys, long clicks) = _store.GetTodayTotals();
            _keyPressCount = keys;
            _mouseClickCount = clicks;

            if (resumeApp is { } app)
            {
                SwitchSegment(app, utcNow);
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(CurrentDateKey));
            OnPropertyChanged(nameof(KeyPressCount));
            OnPropertyChanged(nameof(MouseClickCount));
        });

        PublishActivity(force: true);
    }

    private static string TodayKey() => DateTime.Now.ToString("yyyy-MM-dd");

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class OpenSegmentState(long id, ForegroundApp app, DateTime startedUtc)
    {
        private long _keyCount;
        private long _mouseClickCount;

        public long Id { get; } = id;
        public ForegroundApp App { get; } = app;
        public DateTime StartedUtc { get; } = startedUtc;
        public long KeyCount => Interlocked.Read(ref _keyCount);
        public long MouseClickCount => Interlocked.Read(ref _mouseClickCount);

        public void AddKey() => Interlocked.Increment(ref _keyCount);
        public void AddClick() => Interlocked.Increment(ref _mouseClickCount);
    }

    private sealed class OpenAppState(long id, ForegroundApp app, DateTime startedUtc)
    {
        public long Id { get; } = id;
        public ForegroundApp App { get; } = app;
        public DateTime StartedUtc { get; } = startedUtc;
    }
}
