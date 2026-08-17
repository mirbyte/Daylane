using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Media;
using Daylane.Controls;
using Daylane.Models;
using Daylane.Services;

namespace Daylane.ViewModels;

internal enum AppTab
{
    Day,
    Insights
}

internal enum InsightPeriod
{
    Week,
    Month
}

internal sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly TrackingService _tracking;
    private readonly ObservableCollection<AppUsageItemViewModel> _appUsage = [];
    private readonly ObservableCollection<AppUsageItemViewModel> _insightAppUsage = [];
    private readonly ObservableCollection<ActivitySegmentItemViewModel> _segmentItems = [];
    private readonly ObservableCollection<OpenAppItemViewModel> _openApps = [];
    private IReadOnlyList<ActivitySegment> _timelineSegments = [];
    private IReadOnlyList<double> _hourlyValues = new double[24];
    private IReadOnlyList<double> _insightDailyValues = [];
    private IReadOnlyList<string> _insightDailyLabels = [];
    private AppTab _selectedTab = AppTab.Day;
    private InsightPeriod _insightPeriod = InsightPeriod.Week;
    private DateTime _selectedDay = DateTime.Today;
    private DateTime _periodAnchor = DateTime.Today;
    private string _screenTimeText = "0m";
    private string _activeDurationText = "0m";
    private string _idleDurationText = "0m";
    private string _keyPressCount = "0";
    private string _mouseClickCount = "0";
    private string _peakHourText = "No peak yet";
    private string _insightScreenTimeText = "0m";
    private string _insightActiveDurationText = "0m";
    private string _insightIdleDurationText = "0m";
    private string _insightKeyPressCount = "0";
    private string _insightMouseClickCount = "0";
    private string _insightPeakDayText = "No peak yet";
    private string _insightAvgActiveText = "0m";
    private long? _selectedSegmentId;
    private string _focusLongestText = "—";
    private string _focusLongestDetailText = "";
    private string _focusAwayCountText = "0";
    private string _focusSessionCountText = "0";
    private string _focusLastAwayText = "Last Away: —";
    private string _selectedDetailTitle = "";
    private string _selectedDetailRangeText = "";
    private string _selectedDetailDurationText = "";
    private string _selectedDetailKeysText = "0";
    private string _selectedDetailClicksText = "0";
    private string _selectedDetailShareText = "";
    private IBrush? _selectedDetailColor;
    private Avalonia.Media.Imaging.Bitmap? _selectedDetailIcon;
    private bool _selectedDetailIsAway;
    private double _timelineZoom = 2;

    public MainWindowViewModel(TrackingService tracking)
    {
        _tracking = tracking;
        _tracking.PropertyChanged += OnTrackingPropertyChanged;
        OpenDataFolderCommand = new RelayCommand(() => OpenDataFolder(_tracking.DatabasePath));
        SelectDayCommand = new RelayCommand(() => SelectedTab = AppTab.Day);
        SelectInsightsCommand = new RelayCommand(() => SelectedTab = AppTab.Insights);
        SelectWeekPeriodCommand = new RelayCommand(() => InsightPeriodKind = InsightPeriod.Week);
        SelectMonthPeriodCommand = new RelayCommand(() => InsightPeriodKind = InsightPeriod.Month);
        PreviousDayCommand = new RelayCommand(() => SelectedDay = SelectedDay.AddDays(-1));
        NextDayCommand = new RelayCommand(() => SelectedDay = SelectedDay.AddDays(1));
        SelectTodayCommand = new RelayCommand(() => SelectedDay = DateTime.Today);
        PreviousPeriodCommand = new RelayCommand(GoPreviousPeriod);
        NextPeriodCommand = new RelayCommand(GoNextPeriod);
        SelectCurrentPeriodCommand = new RelayCommand(GoCurrentPeriod);
        ClearSelectionCommand = new RelayCommand(() => SelectedSegmentId = null);
        RefreshDay();
        RefreshInsights();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsRunning => _tracking.IsRunning;

    public string DatabasePath => _tracking.DatabasePath;

    public string CurrentAppName => _tracking.CurrentAppName;

    public DateTime SelectedDay
    {
        get => _selectedDay;
        set
        {
            DateTime day = value.Date;
            if (day > DateTime.Today)
            {
                day = DateTime.Today;
            }

            if (_selectedDay.Date == day)
            {
                return;
            }

            _selectedDay = day;
            SelectedSegmentId = null;
            TimelineZoom = day == DateTime.Today ? 2 : 1;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DayLabel));
            OnPropertyChanged(nameof(IsSelectedDayToday));
            OnPropertyChanged(nameof(CanGoNextDay));
            RefreshDay();
        }
    }

    public bool IsSelectedDayToday => SelectedDay.Date == DateTime.Today;

    public bool CanGoNextDay => SelectedDay.Date < DateTime.Today;

    public string DayLabel => SelectedDay.ToString("ddd, MMM d, yyyy");

    public string KeyPressCount => _keyPressCount;

    public string MouseClickCount => _mouseClickCount;

    public string ScreenTimeText => _screenTimeText;

    public string ActiveDurationText => _activeDurationText;

    public string IdleDurationText => _idleDurationText;

    public string PeakHourText => _peakHourText;

    public string FocusLongestText => _focusLongestText;

    public string FocusLongestDetailText => _focusLongestDetailText;

    public string FocusAwayCountText => _focusAwayCountText;

    public string FocusSessionCountText => _focusSessionCountText;

    public string FocusLastAwayText => _focusLastAwayText;

    public bool HasSelectedSegment => _selectedSegmentId is not null && FindSelectedSegment() is not null;

    public string SelectedDetailTitle => _selectedDetailTitle;

    public string SelectedDetailRangeText => _selectedDetailRangeText;

    public string SelectedDetailDurationText => _selectedDetailDurationText;

    public string SelectedDetailKeysText => _selectedDetailKeysText;

    public string SelectedDetailClicksText => _selectedDetailClicksText;

    public string SelectedDetailShareText => _selectedDetailShareText;

    public IBrush? SelectedDetailColor => _selectedDetailColor;

    public Avalonia.Media.Imaging.Bitmap? SelectedDetailIcon => _selectedDetailIcon;

    public bool HasSelectedDetailIcon => _selectedDetailIcon is not null;

    public bool SelectedDetailIsAway => _selectedDetailIsAway;

    public double TimelineZoom
    {
        get => _timelineZoom;
        set
        {
            double zoom = Math.Clamp(Math.Round(value * 4) / 4, 1, 8);
            if (Math.Abs(_timelineZoom - zoom) < 0.001)
            {
                return;
            }

            _timelineZoom = zoom;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<ActivitySegment> TimelineSegments => _timelineSegments;

    public ObservableCollection<AppUsageItemViewModel> AppUsage => _appUsage;

    public ObservableCollection<OpenAppItemViewModel> OpenApps => _openApps;

    public ObservableCollection<AppUsageItemViewModel> InsightAppUsage => _insightAppUsage;

    public ObservableCollection<ActivitySegmentItemViewModel> SegmentItems => _segmentItems;

    public long? SelectedSegmentId
    {
        get => _selectedSegmentId;
        set
        {
            if (_selectedSegmentId == value)
            {
                return;
            }

            _selectedSegmentId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedSegmentItem));
            RefreshSelectedDetail();
        }
    }

    public ActivitySegmentItemViewModel? SelectedSegmentItem
    {
        get
        {
            if (_selectedSegmentId is not long id)
            {
                return null;
            }

            foreach (var item in _segmentItems)
            {
                if (item.Id == id)
                {
                    return item;
                }
            }

            return null;
        }
        set => SelectedSegmentId = value?.Id;
    }

    public bool HasAppUsage => _appUsage.Count > 0;

    public bool HasOpenApps => _openApps.Count > 0;

    public bool HasSegments => _segmentItems.Count > 0;

    public bool HasDayActivity => _timelineSegments.Count > 0;

    public bool HasInsightAppUsage => _insightAppUsage.Count > 0;

    public IReadOnlyList<double> InsightDailyValues => _insightDailyValues;

    public IReadOnlyList<string> InsightDailyLabels => _insightDailyLabels;

    public string InsightScreenTimeText => _insightScreenTimeText;

    public string InsightActiveDurationText => _insightActiveDurationText;

    public string InsightIdleDurationText => _insightIdleDurationText;

    public string InsightKeyPressCount => _insightKeyPressCount;

    public string InsightMouseClickCount => _insightMouseClickCount;

    public string InsightPeakDayText => _insightPeakDayText;

    public string InsightAvgActiveText => _insightAvgActiveText;

    public string PeriodLabel
    {
        get
        {
            var (start, end) = GetPeriodBounds();
            if (InsightPeriodKind == InsightPeriod.Month)
            {
                return start.ToString("MMMM yyyy");
            }

            if (start.Year == end.Year)
            {
                return $"{start:MMM d} – {end:MMM d, yyyy}";
            }

            return $"{start:MMM d, yyyy} – {end:MMM d, yyyy}";
        }
    }

    public bool CanGoNextPeriod
    {
        get
        {
            var (_, end) = GetPeriodBounds();
            return end.Date < DateTime.Today;
        }
    }

    public AppTab SelectedTab
    {
        get => _selectedTab;
        private set
        {
            if (_selectedTab == value)
            {
                return;
            }

            _selectedTab = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDaySelected));
            OnPropertyChanged(nameof(IsInsightsSelected));
            if (value == AppTab.Insights)
            {
                RefreshInsights();
            }
        }
    }

    public InsightPeriod InsightPeriodKind
    {
        get => _insightPeriod;
        private set
        {
            if (_insightPeriod == value)
            {
                return;
            }

            _insightPeriod = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsWeekPeriod));
            OnPropertyChanged(nameof(IsMonthPeriod));
            OnPropertyChanged(nameof(PeriodLabel));
            OnPropertyChanged(nameof(CanGoNextPeriod));
            RefreshInsights();
        }
    }

    public bool IsDaySelected => SelectedTab == AppTab.Day;

    public bool IsInsightsSelected => SelectedTab == AppTab.Insights;

    public bool IsWeekPeriod => InsightPeriodKind == InsightPeriod.Week;

    public bool IsMonthPeriod => InsightPeriodKind == InsightPeriod.Month;

    public ICommand OpenDataFolderCommand { get; }

    public ICommand SelectDayCommand { get; }

    public ICommand SelectInsightsCommand { get; }

    public ICommand SelectWeekPeriodCommand { get; }

    public ICommand SelectMonthPeriodCommand { get; }

    public ICommand PreviousDayCommand { get; }

    public ICommand NextDayCommand { get; }

    public ICommand SelectTodayCommand { get; }

    public ICommand PreviousPeriodCommand { get; }

    public ICommand NextPeriodCommand { get; }

    public ICommand SelectCurrentPeriodCommand { get; }

    public ICommand ClearSelectionCommand { get; }

    private void OnTrackingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TrackingService.IsRunning):
                OnPropertyChanged(nameof(IsRunning));
                break;
            case nameof(TrackingService.CurrentAppName):
                OnPropertyChanged(nameof(CurrentAppName));
                break;
            case nameof(TrackingService.CurrentDateKey):
                OnDateRollover();
                break;
            case nameof(TrackingService.TodaySegments):
            case nameof(TrackingService.TodayAppUsage):
            case nameof(TrackingService.TodayOpenApps):
                if (IsSelectedDayToday)
                {
                    RefreshDay();
                }

                if (IsInsightsSelected && PeriodIncludesToday())
                {
                    RefreshInsights();
                }

                break;
        }
    }

    private void OnDateRollover()
    {
        DateTime today = DateTime.Today;

        // SelectedDay does not move with the clock; follow the live day across midnight
        // so the UI does not keep a stale "today" snapshot with an open-ended segment.
        if (_selectedDay.Date == today.AddDays(-1))
        {
            SelectedDay = today;
        }
        else
        {
            OnPropertyChanged(nameof(IsSelectedDayToday));
            OnPropertyChanged(nameof(CanGoNextDay));
            if (_selectedDay.Date == today)
            {
                RefreshDay();
            }
        }

        OnPropertyChanged(nameof(CanGoNextPeriod));
        if (IsInsightsSelected && PeriodIncludesToday())
        {
            RefreshInsights();
        }
    }

    private void GoPreviousPeriod()
    {
        _periodAnchor = InsightPeriodKind == InsightPeriod.Week
            ? _periodAnchor.AddDays(-7)
            : _periodAnchor.AddMonths(-1);
        NotifyPeriodChanged();
        RefreshInsights();
    }

    private void GoNextPeriod()
    {
        DateTime next = InsightPeriodKind == InsightPeriod.Week
            ? _periodAnchor.AddDays(7)
            : _periodAnchor.AddMonths(1);
        if (GetPeriodBoundsFor(next, InsightPeriodKind).Start > DateTime.Today)
        {
            return;
        }

        _periodAnchor = next;
        NotifyPeriodChanged();
        RefreshInsights();
    }

    private void GoCurrentPeriod()
    {
        _periodAnchor = DateTime.Today;
        NotifyPeriodChanged();
        RefreshInsights();
    }

    private void NotifyPeriodChanged()
    {
        OnPropertyChanged(nameof(PeriodLabel));
        OnPropertyChanged(nameof(CanGoNextPeriod));
    }

    private bool PeriodIncludesToday()
    {
        var (start, end) = GetPeriodBounds();
        DateTime today = DateTime.Today;
        return today >= start && today <= end;
    }

    private (DateTime Start, DateTime End) GetPeriodBounds() =>
        GetPeriodBoundsFor(_periodAnchor, InsightPeriodKind);

    private static (DateTime Start, DateTime End) GetPeriodBoundsFor(DateTime anchor, InsightPeriod period)
    {
        DateTime today = DateTime.Today;
        if (period == InsightPeriod.Week)
        {
            DateTime start = StartOfWeek(anchor);
            DateTime end = start.AddDays(6);
            if (end > today)
            {
                end = today;
            }

            return (start, end);
        }

        DateTime monthStart = new(anchor.Year, anchor.Month, 1);
        DateTime monthEnd = monthStart.AddMonths(1).AddDays(-1);
        if (monthEnd > today)
        {
            monthEnd = today;
        }

        return (monthStart, monthEnd);
    }

    private static DateTime StartOfWeek(DateTime day)
    {
        int diff = ((int)day.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return day.Date.AddDays(-diff);
    }

    private void RefreshDay()
    {
        var snapshot = _tracking.GetDaySnapshot(SelectedDay);
        _timelineSegments = snapshot.Segments;
        _keyPressCount = snapshot.KeyCount.ToString("N0");
        _mouseClickCount = snapshot.MouseClickCount.ToString("N0");

        TimeSpan active = TimeSpan.Zero;
        TimeSpan idle = TimeSpan.Zero;
        double totalDuration = snapshot.AppUsage.Sum(a => a.Duration.TotalSeconds);

        _appUsage.Clear();
        foreach (var item in snapshot.AppUsage)
        {
            if (item.IsIdle)
            {
                idle += item.Duration;
            }
            else
            {
                active += item.Duration;
            }

            _appUsage.Add(AppUsageItemViewModel.From(item, totalDuration));
        }

        _openApps.Clear();
        foreach (var item in snapshot.OpenApps)
        {
            _openApps.Add(OpenAppItemViewModel.From(item));
        }

        SyncSegmentItems(snapshot.Segments);

        _hourlyValues = BuildHourlyActiveMinutes(snapshot.Segments, SelectedDay);
        _peakHourText = BuildPeakHourText(_hourlyValues);
        _activeDurationText = FormatDuration(active);
        _idleDurationText = FormatDuration(idle);
        _screenTimeText = FormatDuration(active + idle);
        UpdateFocusSummary(snapshot.Segments);

        OnPropertyChanged(nameof(TimelineSegments));
        OnPropertyChanged(nameof(KeyPressCount));
        OnPropertyChanged(nameof(MouseClickCount));
        OnPropertyChanged(nameof(ScreenTimeText));
        OnPropertyChanged(nameof(ActiveDurationText));
        OnPropertyChanged(nameof(IdleDurationText));
        OnPropertyChanged(nameof(PeakHourText));
        OnPropertyChanged(nameof(FocusLongestText));
        OnPropertyChanged(nameof(FocusLongestDetailText));
        OnPropertyChanged(nameof(FocusAwayCountText));
        OnPropertyChanged(nameof(FocusSessionCountText));
        OnPropertyChanged(nameof(FocusLastAwayText));
        OnPropertyChanged(nameof(HasAppUsage));
        OnPropertyChanged(nameof(HasOpenApps));
        OnPropertyChanged(nameof(HasSegments));
        OnPropertyChanged(nameof(HasDayActivity));
        RefreshSelectedDetail();
    }

    private void RefreshInsights()
    {
        var (start, end) = GetPeriodBounds();
        var snapshot = _tracking.GetRangeSnapshot(start, end);

        TimeSpan active = TimeSpan.Zero;
        TimeSpan idle = TimeSpan.Zero;
        double totalDuration = snapshot.AppUsage.Sum(a => a.Duration.TotalSeconds);

        _insightAppUsage.Clear();
        foreach (var item in snapshot.AppUsage)
        {
            if (item.IsIdle)
            {
                idle += item.Duration;
            }
            else
            {
                active += item.Duration;
            }

            _insightAppUsage.Add(AppUsageItemViewModel.From(item, totalDuration));
        }

        _insightDailyValues = snapshot.DailyActive.Select(p => p.ActiveMinutes).ToArray();
        _insightDailyLabels = BuildInsightLabels(snapshot.DailyActive, InsightPeriodKind);
        _insightKeyPressCount = snapshot.KeyCount.ToString("N0");
        _insightMouseClickCount = snapshot.MouseClickCount.ToString("N0");
        _insightActiveDurationText = FormatDuration(active);
        _insightIdleDurationText = FormatDuration(idle);
        _insightScreenTimeText = FormatDuration(active + idle);
        _insightPeakDayText = BuildPeakDayText(snapshot.DailyActive);

        int dayCount = Math.Max(1, snapshot.DailyActive.Count);
        _insightAvgActiveText = FormatDurationShort(TimeSpan.FromMinutes(active.TotalMinutes / dayCount));

        OnPropertyChanged(nameof(InsightDailyValues));
        OnPropertyChanged(nameof(InsightDailyLabels));
        OnPropertyChanged(nameof(InsightKeyPressCount));
        OnPropertyChanged(nameof(InsightMouseClickCount));
        OnPropertyChanged(nameof(InsightScreenTimeText));
        OnPropertyChanged(nameof(InsightActiveDurationText));
        OnPropertyChanged(nameof(InsightIdleDurationText));
        OnPropertyChanged(nameof(InsightPeakDayText));
        OnPropertyChanged(nameof(InsightAvgActiveText));
        OnPropertyChanged(nameof(HasInsightAppUsage));
        OnPropertyChanged(nameof(PeriodLabel));
        OnPropertyChanged(nameof(CanGoNextPeriod));
    }

    private static IReadOnlyList<string> BuildInsightLabels(
        IReadOnlyList<DailyActivityPoint> points,
        InsightPeriod period)
    {
        if (points.Count == 0)
        {
            return [];
        }

        if (period == InsightPeriod.Week)
        {
            return points.Select(p => p.LocalDay.ToString("ddd")).ToArray();
        }

        int step = points.Count <= 10 ? 1 : points.Count <= 20 ? 2 : 5;
        var labels = new string[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            labels[i] = i % step == 0 || i == points.Count - 1
                ? points[i].LocalDay.Day.ToString()
                : "";
        }

        return labels;
    }

    private static string BuildPeakDayText(IReadOnlyList<DailyActivityPoint> points)
    {
        if (points.Count == 0 || points.All(p => p.ActiveMinutes <= 0))
        {
            return "No peak yet";
        }

        var peak = points.MaxBy(p => p.ActiveMinutes)!;
        return $"Peak {peak.LocalDay:ddd MMM d} · {FormatDurationShort(TimeSpan.FromMinutes(peak.ActiveMinutes))}";
    }

    // Newest-first. Avoid Clear+rebuild when only the open segment grows, or one new
    // segment was prepended (typical 5s refresh / app-switch paths).
    private void SyncSegmentItems(IReadOnlyList<ActivitySegment> segments)
    {
        var ordered = segments
            .OrderByDescending(s => s.StartUtc)
            .ToList();

        if (!TryPatchSegmentItems(ordered))
        {
            _segmentItems.Clear();
            foreach (var segment in ordered)
            {
                _segmentItems.Add(ActivitySegmentItemViewModel.From(segment));
            }
        }

        if (_selectedSegmentId is long id && SelectedSegmentItem is null)
        {
            SelectedSegmentId = null;
        }
        else if (_selectedSegmentId is not null)
        {
            OnPropertyChanged(nameof(SelectedSegmentItem));
            RefreshSelectedDetail();
        }

        OnPropertyChanged(nameof(HasSegments));
        OnPropertyChanged(nameof(HasDayActivity));
    }

    private void UpdateFocusSummary(IReadOnlyList<ActivitySegment> segments)
    {
        int awayCount = 0;
        int sessionCount = 0;
        TimeSpan bestFocus = TimeSpan.Zero;
        DateTime bestStart = default;
        DateTime bestEnd = default;
        string bestApp = "";
        ActivitySegment? lastAway = null;

        TimeSpan runDuration = TimeSpan.Zero;
        DateTime runStart = default;
        DateTime runEnd = default;
        var runApps = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);

        void FinalizeRun()
        {
            if (runDuration <= TimeSpan.Zero)
            {
                return;
            }

            if (runDuration > bestFocus)
            {
                bestFocus = runDuration;
                bestStart = runStart;
                bestEnd = runEnd;
                bestApp = runApps
                    .OrderByDescending(p => p.Value)
                    .Select(p => p.Key)
                    .FirstOrDefault() ?? "";
            }

            runDuration = TimeSpan.Zero;
            runApps.Clear();
        }

        foreach (var segment in segments.OrderBy(s => s.StartUtc))
        {
            if (segment.IsIdle)
            {
                awayCount++;
                lastAway = segment;
                FinalizeRun();
                continue;
            }

            sessionCount++;
            DateTime start = segment.StartUtc;
            DateTime end = segment.EffectiveEndUtc;
            if (runDuration <= TimeSpan.Zero)
            {
                runStart = start;
            }

            runEnd = end;
            runDuration += end - start;
            string name = string.IsNullOrWhiteSpace(segment.DisplayName) ? segment.ProcessName : segment.DisplayName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "App";
            }

            runApps[name] = runApps.TryGetValue(name, out TimeSpan existing)
                ? existing + (end - start)
                : end - start;
        }

        FinalizeRun();

        if (bestFocus > TimeSpan.Zero)
        {
            DateTime startLocal = bestStart.ToLocalTime();
            DateTime endLocal = bestEnd.ToLocalTime();
            _focusLongestText = FormatDuration(bestFocus);
            string appPart = string.IsNullOrWhiteSpace(bestApp) ? "" : $" · {bestApp}";
            _focusLongestDetailText = $"{startLocal:HH:mm}–{endLocal:HH:mm}{appPart}";
        }
        else
        {
            _focusLongestText = "—";
            _focusLongestDetailText = "No Active stretch yet";
        }

        _focusAwayCountText = awayCount.ToString("N0");
        _focusSessionCountText = sessionCount.ToString("N0");
        _focusLastAwayText = FormatLastAway(lastAway);
    }

    private static string FormatLastAway(ActivitySegment? away)
    {
        if (away is null)
        {
            return "Last Away: —";
        }

        DateTime startLocal = away.StartUtc.ToLocalTime();
        DateTime endLocal = away.EffectiveEndUtc.ToLocalTime();
        TimeSpan duration = endLocal - startLocal;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return $"Last Away: {startLocal:HH:mm} - {endLocal:HH:mm} ({FormatDurationShort(duration)})";
    }

    private ActivitySegment? FindSelectedSegment()
    {
        if (_selectedSegmentId is not long id)
        {
            return null;
        }

        foreach (var segment in _timelineSegments)
        {
            if (segment.Id == id)
            {
                return segment;
            }
        }

        return null;
    }

    private void RefreshSelectedDetail()
    {
        var segment = FindSelectedSegment();
        if (segment is null)
        {
            _selectedDetailTitle = "";
            _selectedDetailRangeText = "";
            _selectedDetailDurationText = "";
            _selectedDetailKeysText = "0";
            _selectedDetailClicksText = "0";
            _selectedDetailShareText = "";
            _selectedDetailColor = null;
            _selectedDetailIcon = null;
            _selectedDetailIsAway = false;
            NotifySelectedDetailChanged();
            return;
        }

        DateTime startLocal = segment.StartUtc.ToLocalTime();
        DateTime endLocal = segment.EffectiveEndUtc.ToLocalTime();
        TimeSpan duration = endLocal - startLocal;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        TimeSpan dayTracked = TimeSpan.Zero;
        foreach (var item in _timelineSegments)
        {
            TimeSpan d = item.EffectiveEndUtc - item.StartUtc;
            if (d > TimeSpan.Zero)
            {
                dayTracked += d;
            }
        }

        double share = dayTracked.TotalSeconds <= 0
            ? 0
            : duration.TotalSeconds / dayTracked.TotalSeconds * 100;

        _selectedDetailIsAway = segment.IsIdle;
        _selectedDetailTitle = segment.IsIdle
            ? "Away"
            : (string.IsNullOrWhiteSpace(segment.DisplayName) ? segment.ProcessName : segment.DisplayName);
        _selectedDetailRangeText = $"{startLocal:HH:mm:ss} – {endLocal:HH:mm:ss}";
        _selectedDetailDurationText = FormatDuration(duration);
        _selectedDetailKeysText = segment.KeyCount.ToString("N0");
        _selectedDetailClicksText = segment.MouseClickCount.ToString("N0");
        _selectedDetailShareText = $"{share:0.0}% of tracked";
        _selectedDetailIcon = segment.IsIdle ? null : AppIconLoader.Get(segment.ExePath);
        _selectedDetailColor = segment.IsIdle
            ? new SolidColorBrush(AwayHatch.Fill)
            : AppColor.For(segment.ExePath, segment.ProcessName);
        NotifySelectedDetailChanged();
    }

    private void NotifySelectedDetailChanged()
    {
        OnPropertyChanged(nameof(HasSelectedSegment));
        OnPropertyChanged(nameof(SelectedDetailTitle));
        OnPropertyChanged(nameof(SelectedDetailRangeText));
        OnPropertyChanged(nameof(SelectedDetailDurationText));
        OnPropertyChanged(nameof(SelectedDetailKeysText));
        OnPropertyChanged(nameof(SelectedDetailClicksText));
        OnPropertyChanged(nameof(SelectedDetailShareText));
        OnPropertyChanged(nameof(SelectedDetailColor));
        OnPropertyChanged(nameof(SelectedDetailIcon));
        OnPropertyChanged(nameof(HasSelectedDetailIcon));
        OnPropertyChanged(nameof(SelectedDetailIsAway));
    }

    private bool TryPatchSegmentItems(IReadOnlyList<ActivitySegment> ordered)
    {
        int existing = _segmentItems.Count;
        if (existing == 0)
        {
            return false;
        }

        if (ordered.Count == existing)
        {
            for (int i = 0; i < existing; i++)
            {
                if (_segmentItems[i].Id != ordered[i].Id)
                {
                    return false;
                }
            }

            for (int i = 0; i < existing; i++)
            {
                _segmentItems[i].UpdateTimes(ordered[i]);
            }

            return true;
        }

        // One new segment opened: [new, previous...,] vs [previous,...]
        if (ordered.Count == existing + 1)
        {
            for (int i = 0; i < existing; i++)
            {
                if (_segmentItems[i].Id != ordered[i + 1].Id)
                {
                    return false;
                }
            }

            _segmentItems.Insert(0, ActivitySegmentItemViewModel.From(ordered[0]));
            for (int i = 1; i < _segmentItems.Count; i++)
            {
                _segmentItems[i].UpdateTimes(ordered[i]);
            }

            return true;
        }

        return false;
    }

    private static double[] BuildHourlyActiveMinutes(IReadOnlyList<ActivitySegment> segments, DateTime localDay)
    {
        var hours = new double[24];
        DateTime dayStartLocal = localDay.Date;
        DateTime dayEndLocal = dayStartLocal.AddDays(1);
        DateTime dayStartUtc = dayStartLocal.ToUniversalTime();
        DateTime dayEndUtc = dayEndLocal.ToUniversalTime();
        DateTime nowUtc = DateTime.UtcNow;

        foreach (var segment in segments)
        {
            if (segment.IsIdle)
            {
                continue;
            }

            DateTime start = segment.StartUtc < dayStartUtc ? dayStartUtc : segment.StartUtc;
            DateTime end = segment.EffectiveEndUtc > dayEndUtc ? dayEndUtc : segment.EffectiveEndUtc;
            if (end > nowUtc)
            {
                end = nowUtc;
            }

            while (start < end)
            {
                DateTime local = start.ToLocalTime();
                int hour = local.Hour;
                DateTime hourEndLocal = local.Date.AddHours(hour + 1);
                DateTime sliceEnd = end < hourEndLocal.ToUniversalTime() ? end : hourEndLocal.ToUniversalTime();
                hours[hour] += (sliceEnd - start).TotalMinutes;
                start = sliceEnd;
            }
        }

        return hours;
    }

    private static string BuildPeakHourText(IReadOnlyList<double> hours)
    {
        if (hours.Count == 0 || hours.All(v => v <= 0))
        {
            return "No peak yet";
        }

        int peak = 0;
        for (int i = 1; i < hours.Count; i++)
        {
            if (hours[i] > hours[peak])
            {
                peak = i;
            }
        }

        string label = peak switch
        {
            0 => "12 am",
            12 => "12 pm",
            > 12 => $"{peak - 12} pm",
            _ => $"{peak} am"
        };

        return $"Peak {label} · {FormatDurationShort(TimeSpan.FromMinutes(hours[peak]))}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes:D2}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{duration.Minutes}m {duration.Seconds:D2}s";
        }

        return $"{Math.Max(0, (int)duration.TotalSeconds)}s";
    }

    private static string FormatDurationShort(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{duration.Minutes}m";
        }

        return $"{Math.Max(0, (int)duration.TotalSeconds)}s";
    }

    private static void OpenDataFolder(string databasePath)
    {
        string directory = Path.GetDirectoryName(databasePath) ?? AppContext.BaseDirectory;
        Process.Start(new ProcessStartInfo
        {
            FileName = directory,
            UseShellExecute = true
        });
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class RelayCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }
}

internal sealed class ActivitySegmentItemViewModel : INotifyPropertyChanged
{
    private string _endText = "";
    private string _durationText = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public long Id { get; init; }
    public required string DisplayName { get; init; }
    public required string StartText { get; init; }

    public string EndText
    {
        get => _endText;
        private set
        {
            if (_endText == value)
            {
                return;
            }

            _endText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EndText)));
        }
    }

    public string DurationText
    {
        get => _durationText;
        private set
        {
            if (_durationText == value)
            {
                return;
            }

            _durationText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DurationText)));
        }
    }

    public required IBrush Color { get; init; }
    public Avalonia.Media.Imaging.Bitmap? Icon { get; init; }
    public bool HasIcon => Icon is not null;
    public bool IsIdle { get; init; }

    public static ActivitySegmentItemViewModel From(ActivitySegment segment)
    {
        DateTime startLocal = segment.StartUtc.ToLocalTime();
        DateTime endLocal = segment.EffectiveEndUtc.ToLocalTime();
        TimeSpan duration = endLocal - startLocal;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return new ActivitySegmentItemViewModel
        {
            Id = segment.Id,
            DisplayName = segment.IsIdle ? "Away" : segment.DisplayName,
            StartText = startLocal.ToString("HH:mm:ss"),
            EndText = endLocal.ToString("HH:mm:ss"),
            DurationText = FormatClock(duration),
            IsIdle = segment.IsIdle,
            Icon = segment.IsIdle ? null : AppIconLoader.Get(segment.ExePath),
            Color = segment.IsIdle
                ? new SolidColorBrush(AwayHatch.Fill)
                : AppColor.For(segment.ExePath, segment.ProcessName)
        };
    }

    public void UpdateTimes(ActivitySegment segment)
    {
        DateTime endLocal = segment.EffectiveEndUtc.ToLocalTime();
        TimeSpan duration = endLocal - segment.StartUtc.ToLocalTime();
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        EndText = endLocal.ToString("HH:mm:ss");
        DurationText = FormatClock(duration);
    }

    private static string FormatClock(TimeSpan duration) =>
        $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
}

internal sealed class AppUsageItemViewModel
{
    private const double BarTrackWidth = 84;

    public required string DisplayName { get; init; }
    public required string DurationText { get; init; }
    public required string PercentageText { get; init; }
    public required string KeyText { get; init; }
    public required string ClickText { get; init; }
    public required IBrush Color { get; init; }
    public required double BarWidth { get; init; }
    public Avalonia.Media.Imaging.Bitmap? Icon { get; init; }
    public bool HasIcon => Icon is not null;
    public bool IsIdle { get; init; }

    public static AppUsageItemViewModel From(AppUsageSummary summary, double totalDurationSeconds)
    {
        double percent = totalDurationSeconds <= 0
            ? 0
            : summary.Duration.TotalSeconds / totalDurationSeconds * 100;
        double barRatio = Math.Clamp(percent / 100.0, 0, 1);
        if (barRatio > 0 && barRatio < 0.03)
        {
            barRatio = 0.03;
        }

        return new AppUsageItemViewModel
        {
            DisplayName = summary.IsIdle ? "Away" : summary.DisplayName,
            DurationText = FormatClock(summary.Duration),
            PercentageText = $"{percent:0.0}%",
            KeyText = summary.KeyCount.ToString("N0"),
            ClickText = summary.MouseClickCount.ToString("N0"),
            IsIdle = summary.IsIdle,
            BarWidth = BarTrackWidth * barRatio,
            Icon = summary.IsIdle ? null : AppIconLoader.Get(summary.ExePath),
            Color = summary.IsIdle
                ? new SolidColorBrush(AwayHatch.Fill)
                : AppColor.For(summary.ExePath, summary.ProcessName)
        };
    }

    private static string FormatClock(TimeSpan duration) =>
        $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}";
}

internal sealed class OpenAppItemViewModel
{
    public required string DisplayName { get; init; }
    public required string OpenDurationText { get; init; }
    public required IBrush Color { get; init; }
    public Avalonia.Media.Imaging.Bitmap? Icon { get; init; }
    public bool HasIcon => Icon is not null;

    public static OpenAppItemViewModel From(OpenAppSummary summary) =>
        new()
        {
            DisplayName = summary.DisplayName,
            OpenDurationText = FormatClock(summary.OpenDuration),
            Icon = AppIconLoader.Get(summary.ExePath),
            Color = AppColor.For(summary.ExePath, summary.ProcessName)
        };

    private static string FormatClock(TimeSpan duration) =>
        $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}";
}
