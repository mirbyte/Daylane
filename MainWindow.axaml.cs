using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Daylane.ViewModels;

namespace Daylane;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _boundVm;
    private bool _panning;
    private bool _panArmed;
    private bool _pendingDefaultTimelineView;
    private bool _userAdjustedTimeline;
    private bool _layoutRetryAttached;
    private bool _applyingTimelineView;
    private int _timelineViewStablePasses;
    private Point _panPointerStart;
    private Vector _panOffsetStart;
    private double _periodThumbX = double.NaN;
    private double _periodThumbW;
    private double _periodThumbH;

    public MainWindow()
    {
        InitializeComponent();
        Title = AppVersion.WindowTitle;
        DataContextChanged += OnDataContextChanged;
        Opened += (_, _) => ScrollTimelineToNow();
        TimelineScroll.SizeChanged += OnTimelineScrollSizeChanged;
        PeriodSegmentGroup.LayoutUpdated += (_, _) => SyncPeriodThumb();

        // Attach to content only so the ScrollViewer scrollbar keeps working.
        TimelineZoomSurface.AddHandler(PointerWheelChangedEvent, OnTimelinePointerWheel, handledEventsToo: true);
        TimelineZoomSurface.AddHandler(PointerPressedEvent, OnTimelinePointerPressed, handledEventsToo: true);
        TimelineZoomSurface.AddHandler(PointerMovedEvent, OnTimelinePointerMoved, handledEventsToo: true);
        TimelineZoomSurface.AddHandler(PointerReleasedEvent, OnTimelinePointerReleased, handledEventsToo: true);
        TimelineZoomSurface.AddHandler(PointerCaptureLostEvent, OnTimelinePointerCaptureLost, handledEventsToo: true);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Application.Current is App { IsExiting: false })
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            if (IsVisible)
            {
                ScrollTimelineToNow();
            }
            else
            {
                DetachLayoutRetry();
            }
        }
    }

    internal void ScrollTimelineToNow()
    {
        _userAdjustedTimeline = false;
        RequestDefaultTimelineView();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_boundVm is not null)
        {
            _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _boundVm = DataContext as MainWindowViewModel;
        if (_boundVm is not null)
        {
            _boundVm.PropertyChanged += OnViewModelPropertyChanged;
        }

        RequestDefaultTimelineView();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.HasDayActivity))
        {
            Dispatcher.UIThread.Post(() =>
            {
                SyncTimelineViewport();
                TimelineZoomSurface.InvalidateMeasure();
                if (_pendingDefaultTimelineView)
                {
                    ApplyDefaultTimelineView();
                }
            }, DispatcherPriority.Loaded);
        }
        else if (e.PropertyName is nameof(MainWindowViewModel.SelectedDay))
        {
            ScrollTimelineToNow();
        }
        else if (e.PropertyName is nameof(MainWindowViewModel.InsightPeriodKind)
                 or nameof(MainWindowViewModel.IsInsightsSelected))
        {
            Dispatcher.UIThread.Post(SyncPeriodThumb, DispatcherPriority.Loaded);
        }
    }

    private void SyncPeriodThumb()
    {
        if (!PeriodSegmentGroup.IsVisible)
        {
            return;
        }

        Button? target = _boundVm?.IsMonthPeriod == true ? MonthPeriodButton : WeekPeriodButton;
        if (target is null || target.Bounds.Width <= 0 || target.Bounds.Height <= 0)
        {
            return;
        }

        if (PeriodThumb.Parent is not Visual host)
        {
            return;
        }

        Point origin = target.TranslatePoint(default, host) ?? default;
        double x = origin.X;
        double w = target.Bounds.Width;
        double h = target.Bounds.Height;

        if (!double.IsNaN(_periodThumbX)
            && Math.Abs(_periodThumbX - x) < 0.5
            && Math.Abs(_periodThumbW - w) < 0.5
            && Math.Abs(_periodThumbH - h) < 0.5)
        {
            return;
        }

        _periodThumbX = x;
        _periodThumbW = w;
        _periodThumbH = h;
        PeriodThumb.Width = w;
        PeriodThumb.Height = h;
        PeriodThumb.RenderTransform = TransformOperations.Parse(
            string.Create(CultureInfo.InvariantCulture, $"translate({x}px, {origin.Y}px)"));
    }

    private void OnTimelinePointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_boundVm is null)
        {
            return;
        }

        double delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
        if (Math.Abs(delta) < 0.01)
        {
            return;
        }

        ZoomTimelineAtPointer(delta > 0 ? 0.25 : -0.25, e.GetPosition(TimelineScroll).X);
        e.Handled = true;
    }

    private void OnTimelinePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(TimelineZoomSurface);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsMiddleButtonPressed)
        {
            return;
        }

        _panArmed = true;
        _panning = false;
        _panPointerStart = e.GetPosition(TimelineScroll);
        _panOffsetStart = TimelineScroll.Offset;
    }

    private void OnTimelinePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_panArmed || _boundVm is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(TimelineZoomSurface);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsMiddleButtonPressed)
        {
            return;
        }

        Point pos = e.GetPosition(TimelineScroll);
        double dx = pos.X - _panPointerStart.X;
        if (!_panning)
        {
            if (Math.Abs(dx) < 5)
            {
                return;
            }

            _panning = true;
            MarkTimelineUserAdjusted();
            e.Pointer.Capture(TimelineZoomSurface);
        }

        double viewport = GetTimelineViewportWidth();
        double extent = viewport * Math.Max(1, _boundVm.TimelineZoom);
        double maxOffset = Math.Max(0, extent - viewport);
        TimelineScroll.Offset = new Vector(Math.Clamp(_panOffsetStart.X - dx, 0, maxOffset), 0);
        e.Handled = true;
    }

    private void OnTimelinePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_panArmed)
        {
            return;
        }

        bool wasPanning = _panning;
        StopPanning(e.Pointer);
        if (wasPanning)
        {
            e.Handled = true;
        }
    }

    private void OnTimelinePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) =>
        StopPanning(null);

    private void StopPanning(IPointer? pointer)
    {
        if (_panning)
        {
            pointer?.Capture(null);
        }

        _panning = false;
        _panArmed = false;
    }

    private void ZoomTimelineAtPointer(double zoomDelta, double pointerX)
    {
        if (_boundVm is null)
        {
            return;
        }

        double oldZoom = _boundVm.TimelineZoom;
        double viewport = GetTimelineViewportWidth();
        if (viewport <= 1)
        {
            _boundVm.TimelineZoom = oldZoom + zoomDelta;
            return;
        }

        // Content X under the cursor, as a fraction of the full day width.
        double oldExtent = TimelineZoomSurface.Bounds.Width > 1
            ? TimelineZoomSurface.Bounds.Width
            : viewport * oldZoom;
        double anchor = (TimelineScroll.Offset.X + pointerX) / oldExtent;

        _boundVm.TimelineZoom = oldZoom + zoomDelta;
        double newZoom = _boundVm.TimelineZoom;
        if (Math.Abs(newZoom - oldZoom) < 0.001)
        {
            return;
        }

        MarkTimelineUserAdjusted();

        // Apply Zoom on the control immediately; binding can lag behind layout.
        TimelineZoomSurface.Zoom = newZoom;
        SyncTimelineViewport();
        TimelineZoomSurface.InvalidateMeasure();
        TimelineZoomSurface.UpdateLayout();

        double newExtent = TimelineZoomSurface.Bounds.Width > 1
            ? TimelineZoomSurface.Bounds.Width
            : viewport * newZoom;
        double maxOffset = Math.Max(0, newExtent - viewport);
        TimelineScroll.Offset = new Vector(Math.Clamp((anchor * newExtent) - pointerX, 0, maxOffset), 0);
    }

    private void OnTimelineScrollSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        SyncTimelineViewport();
        if (!_userAdjustedTimeline)
        {
            ApplyDefaultTimelineView();
        }
    }

    private void OnTimelineLayoutUpdated(object? sender, EventArgs e)
    {
        if (_pendingDefaultTimelineView && !_userAdjustedTimeline)
        {
            ApplyDefaultTimelineView(fromLayout: true);
        }
    }

    private void MarkTimelineUserAdjusted()
    {
        _userAdjustedTimeline = true;
        _pendingDefaultTimelineView = false;
        DetachLayoutRetry();
    }

    private void SyncTimelineViewport()
    {
        if (TimelineZoomSurface is null || TimelineScroll is null)
        {
            return;
        }

        double viewport = GetTimelineViewportWidth();
        if (viewport <= 1)
        {
            return;
        }

        if (Math.Abs(TimelineZoomSurface.ViewportWidth - viewport) > 0.5)
        {
            TimelineZoomSurface.ViewportWidth = viewport;
        }
    }

    private void RequestDefaultTimelineView()
    {
        _pendingDefaultTimelineView = true;
        _timelineViewStablePasses = 0;
        AttachLayoutRetry();
        Dispatcher.UIThread.Post(() => ApplyDefaultTimelineView(), DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(() => ApplyDefaultTimelineView(), DispatcherPriority.Render);
    }

    private void AttachLayoutRetry()
    {
        if (_layoutRetryAttached || TimelineScroll is null)
        {
            return;
        }

        TimelineScroll.LayoutUpdated += OnTimelineLayoutUpdated;
        _layoutRetryAttached = true;
    }

    private void DetachLayoutRetry()
    {
        if (!_layoutRetryAttached || TimelineScroll is null)
        {
            return;
        }

        TimelineScroll.LayoutUpdated -= OnTimelineLayoutUpdated;
        _layoutRetryAttached = false;
    }

    private void ApplyDefaultTimelineView(bool fromLayout = false)
    {
        if (_applyingTimelineView
            || _userAdjustedTimeline
            || _boundVm is null
            || TimelineZoomSurface is null
            || TimelineScroll is null)
        {
            return;
        }

        _applyingTimelineView = true;
        try
        {
            SyncTimelineViewport();

            double zoom = Math.Max(1, _boundVm.TimelineZoom);
            TimelineZoomSurface.Zoom = zoom;
            TimelineZoomSurface.InvalidateMeasure();
            TimelineScroll.InvalidateMeasure();
            if (!fromLayout)
            {
                TimelineScroll.UpdateLayout();
            }

            double viewport = GetTimelineViewportWidth();
            if (viewport <= 1)
            {
                return;
            }

            double extent = TimelineZoomSurface.Bounds.Width > 1
                ? TimelineZoomSurface.Bounds.Width
                : Math.Max(TimelineScroll.Extent.Width, viewport * zoom);
            if (extent + 1 < viewport * zoom)
            {
                return;
            }

            double maxOffset = Math.Max(0, extent - viewport);
            double offset = 0;
            double nowX = 0;
            bool keepNowVisible = _boundVm.IsSelectedDayToday;
            if (keepNowVisible)
            {
                double nowFraction = Math.Clamp((DateTime.Now - DateTime.Today).TotalDays, 0, 1);
                nowX = nowFraction * extent;
                offset = Math.Clamp(nowX - (viewport * 0.5), 0, maxOffset);
                if (nowX > offset + viewport)
                {
                    offset = Math.Clamp(nowX - viewport, 0, maxOffset);
                }
                else if (nowX < offset)
                {
                    offset = Math.Clamp(nowX, 0, maxOffset);
                }
            }

            if (Math.Abs(TimelineScroll.Offset.X - offset) > 1)
            {
                _timelineViewStablePasses = 0;
                TimelineScroll.Offset = new Vector(offset, 0);
                return;
            }

            if (keepNowVisible)
            {
                double viewStart = TimelineScroll.Offset.X;
                double viewEnd = viewStart + viewport;
                if (nowX < viewStart - 1 || nowX > viewEnd + 1)
                {
                    _timelineViewStablePasses = 0;
                    TimelineScroll.Offset = new Vector(offset, 0);
                    return;
                }
            }

            _timelineViewStablePasses++;
            if (_timelineViewStablePasses >= 2)
            {
                _pendingDefaultTimelineView = false;
                DetachLayoutRetry();
            }
        }
        finally
        {
            _applyingTimelineView = false;
        }
    }

    private double GetTimelineViewportWidth()
    {
        if (TimelineScroll.Viewport.Width > 1)
        {
            return TimelineScroll.Viewport.Width;
        }

        return Math.Max(0, TimelineScroll.Bounds.Width);
    }

    private void OnDayCalendarSelectedDatesChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Ignore the initial SelectedDate sync when the flyout opens.
        if (e.RemovedItems.Count == 0)
        {
            return;
        }

        if (DayPickerButton.Flyout is Flyout flyout)
        {
            flyout.Hide();
        }
    }

    private void OnSegmentSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SegmentList.SelectedItem is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (SegmentList.SelectedItem is { } item)
            {
                SegmentList.ScrollIntoView(item);
            }
        });
    }
}
