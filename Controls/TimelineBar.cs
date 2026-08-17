using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Daylane.Models;
using Daylane.Services;

namespace Daylane.Controls;

internal enum TimelineLaneMode
{
    Applications,
    Computer,
    Activity
}

internal sealed class TimelineLane : Control
{
    public static readonly StyledProperty<IReadOnlyList<ActivitySegment>?> SegmentsProperty =
        AvaloniaProperty.Register<TimelineLane, IReadOnlyList<ActivitySegment>?>(nameof(Segments));

    public static readonly StyledProperty<DateTime> DayStartLocalProperty =
        AvaloniaProperty.Register<TimelineLane, DateTime>(nameof(DayStartLocal));

    public static readonly StyledProperty<TimelineLaneMode> ModeProperty =
        AvaloniaProperty.Register<TimelineLane, TimelineLaneMode>(nameof(Mode));

    public static readonly StyledProperty<long?> SelectedSegmentIdProperty =
        AvaloniaProperty.Register<TimelineLane, long?>(nameof(SelectedSegmentId));

    private long? _tipSegmentId;

    static TimelineLane()
    {
        AffectsRender<TimelineLane>(
            SegmentsProperty,
            DayStartLocalProperty,
            ModeProperty,
            SelectedSegmentIdProperty);
    }

    public TimelineLane()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public IReadOnlyList<ActivitySegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public DateTime DayStartLocal
    {
        get => GetValue(DayStartLocalProperty);
        set => SetValue(DayStartLocalProperty, value);
    }

    public TimelineLaneMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public long? SelectedSegmentId
    {
        get => GetValue(SelectedSegmentIdProperty);
        set => SetValue(SelectedSegmentIdProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 54 : availableSize.Height;
        return new Size(width, height);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        UpdateHover(HitTest(e.GetPosition(this).X));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ClearHover();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        ActivitySegment? segment = HitTest(e.GetPosition(this).X);
        SelectedSegmentId = segment?.Id;
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.White, bounds);

        if (!TryGetDayRange(out DateTime dayStartLocal, out DateTime dayStartUtc, out DateTime dayEndUtc, out double dayMs))
        {
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        DrawGrid(context, bounds);

        var segments = Segments;
        long? selectedId = SelectedSegmentId;
        Rect? selectedRect = null;

        if (segments is { Count: > 0 })
        {
            foreach (var segment in segments)
            {
                if (!TryMapSegment(segment, dayStartUtc, dayEndUtc, nowUtc, dayMs, bounds.Width,
                        out double x, out double w))
                {
                    continue;
                }

                switch (Mode)
                {
                    case TimelineLaneMode.Applications:
                        DrawApplicationSegment(context, segment, x, w, bounds.Height);
                        break;
                    case TimelineLaneMode.Computer:
                        DrawComputerSegment(context, segment, x, w, bounds.Height);
                        break;
                    case TimelineLaneMode.Activity:
                        DrawActivitySegment(context, segment, x, w, bounds.Height);
                        break;
                }

                if (selectedId == segment.Id)
                {
                    selectedRect = GetSegmentRect(Mode, x, w, bounds.Height);
                }
            }
        }

        if (selectedRect is Rect highlight)
        {
            context.DrawRectangle(
                null,
                new Pen(new SolidColorBrush(Color.Parse("#111827")), 1.5),
                highlight,
                2,
                2);
        }

        if (dayStartLocal.Date == DateTime.Today)
        {
            double nowX = bounds.Width * ((nowUtc - dayStartUtc).TotalMilliseconds / dayMs);
            if (nowX >= 0 && nowX <= bounds.Width)
            {
                context.DrawLine(
                    new Pen(new SolidColorBrush(Color.Parse("#111827")), 1),
                    new Point(nowX, 0),
                    new Point(nowX, bounds.Height));
            }
        }
    }

    private ActivitySegment? HitTest(double pointerX)
    {
        if (!TryGetDayRange(out _, out DateTime dayStartUtc, out DateTime dayEndUtc, out double dayMs))
        {
            return null;
        }

        var segments = Segments;
        if (segments is null || segments.Count == 0)
        {
            return null;
        }

        DateTime nowUtc = DateTime.UtcNow;
        double width = Bounds.Width;

        // Prefer the topmost / latest match when widths are clamped.
        for (int i = segments.Count - 1; i >= 0; i--)
        {
            var segment = segments[i];
            if (!TryMapSegment(segment, dayStartUtc, dayEndUtc, nowUtc, dayMs, width, out double x, out double w))
            {
                continue;
            }

            if (pointerX >= x && pointerX <= x + w)
            {
                return segment;
            }
        }

        return null;
    }

    private void UpdateHover(ActivitySegment? segment)
    {
        long? id = segment?.Id;
        if (id == _tipSegmentId)
        {
            return;
        }

        _tipSegmentId = id;
        if (segment is null)
        {
            ToolTip.SetTip(this, null);
            Cursor = Cursor.Default;
            return;
        }

        ToolTip.SetTip(this, FormatTip(segment, Mode));
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    private void ClearHover()
    {
        _tipSegmentId = null;
        ToolTip.SetTip(this, null);
        Cursor = Cursor.Default;
    }

    private static string FormatTip(ActivitySegment segment, TimelineLaneMode mode)
    {
        string title = mode switch
        {
            TimelineLaneMode.Computer => segment.IsIdle ? "Away" : "Active",
            _ => segment.IsIdle ? "Away" : (string.IsNullOrWhiteSpace(segment.DisplayName)
                ? segment.ProcessName
                : segment.DisplayName)
        };

        DateTime startLocal = segment.StartUtc.ToLocalTime();
        DateTime endLocal = segment.EffectiveEndUtc.ToLocalTime();
        TimeSpan duration = endLocal - startLocal;
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        string range =
            $"{startLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} – {endLocal.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} · {FormatDuration(duration)}";

        if (segment.IsIdle || mode == TimelineLaneMode.Computer)
        {
            return $"{title}\n{range}";
        }

        return
            $"{title}\n{range}\nKeys {segment.KeyCount.ToString("N0", CultureInfo.InvariantCulture)} · Clicks {segment.MouseClickCount.ToString("N0", CultureInfo.InvariantCulture)}";
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

    private static Rect GetSegmentRect(TimelineLaneMode mode, double x, double w, double height) =>
        mode switch
        {
            TimelineLaneMode.Applications => new Rect(x, 5, w, Math.Max(0, height - 10)),
            _ => new Rect(x, 6, w, Math.Max(0, height - 12))
        };

    private bool TryGetDayRange(
        out DateTime dayStartLocal,
        out DateTime dayStartUtc,
        out DateTime dayEndUtc,
        out double dayMs)
    {
        dayStartLocal = DayStartLocal == default ? DateTime.Today : DayStartLocal.Date;
        dayStartUtc = dayStartLocal.ToUniversalTime();
        dayEndUtc = dayStartLocal.AddDays(1).ToUniversalTime();
        dayMs = (dayEndUtc - dayStartUtc).TotalMilliseconds;
        return dayMs > 0 && Bounds.Width > 0;
    }

    private static bool TryMapSegment(
        ActivitySegment segment,
        DateTime dayStartUtc,
        DateTime dayEndUtc,
        DateTime nowUtc,
        double dayMs,
        double width,
        out double x,
        out double w)
    {
        DateTime start = segment.StartUtc < dayStartUtc ? dayStartUtc : segment.StartUtc;
        DateTime end = segment.EffectiveEndUtc > dayEndUtc ? dayEndUtc : segment.EffectiveEndUtc;
        if (end > nowUtc)
        {
            end = nowUtc;
        }

        x = 0;
        w = 0;
        if (end <= start)
        {
            return false;
        }

        x = width * ((start - dayStartUtc).TotalMilliseconds / dayMs);
        w = width * ((end - start).TotalMilliseconds / dayMs);
        if (w < 1.5)
        {
            w = 1.5;
        }

        return true;
    }

    private static void DrawApplicationSegment(
        DrawingContext context,
        ActivitySegment segment,
        double x,
        double w,
        double height)
    {
        var rect = new Rect(x, 5, w, Math.Max(0, height - 10));

        if (segment.IsIdle)
        {
            AwayHatch.Draw(context, rect, soft: true, cornerRadius: 2);
            return;
        }

        context.FillRectangle(AppColor.For(segment.ExePath, segment.ProcessName), rect, 2);

        const double minWidthForIcon = 32;
        const double iconSize = 22;
        if (w < minWidthForIcon)
        {
            return;
        }

        var icon = AppIconLoader.Get(segment.ExePath);
        if (icon is null)
        {
            return;
        }

        double size = Math.Min(iconSize, Math.Max(0, Math.Min(rect.Height - 6, w - 8)));
        if (size < 12)
        {
            return;
        }

        var dest = new Rect(
            x + (w - size) / 2,
            rect.Y + (rect.Height - size) / 2,
            size,
            size);
        using (context.PushClip(rect))
        using (context.PushOpacity(0.28))
        {
            context.DrawImage(icon, dest);
        }
    }

    private static void DrawComputerSegment(
        DrawingContext context,
        ActivitySegment segment,
        double x,
        double w,
        double height)
    {
        var rect = new Rect(x, 6, w, Math.Max(0, height - 12));
        if (segment.IsIdle)
        {
            AwayHatch.Draw(context, rect, soft: false, cornerRadius: 2);
            return;
        }

        context.FillRectangle(new SolidColorBrush(Color.Parse("#2F9E6B")), rect, 2);
    }

    private static void DrawActivitySegment(
        DrawingContext context,
        ActivitySegment segment,
        double x,
        double w,
        double height)
    {
        var rect = new Rect(x, 6, w, Math.Max(0, height - 12));
        if (segment.IsIdle)
        {
            AwayHatch.Draw(context, rect, soft: true, cornerRadius: 2);
            return;
        }

        double minutes = Math.Max(0.25, segment.Duration.TotalMinutes);
        double rate = (segment.KeyCount + segment.MouseClickCount) / minutes;
        byte alpha = (byte)Math.Clamp(50 + (rate * 14), 50, 220);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, 47, 158, 107));
        context.FillRectangle(brush, rect, 2);
    }

    private static void DrawGrid(DrawingContext context, Rect bounds)
    {
        var minor = new Pen(new SolidColorBrush(Color.Parse("#F3F4F6")), 1);
        var major = new Pen(new SolidColorBrush(Color.Parse("#E5E7EB")), 1);

        for (int hour = 1; hour < 24; hour++)
        {
            double x = bounds.Width * (hour / 24.0);
            context.DrawLine(hour % 3 == 0 ? major : minor, new Point(x, 0), new Point(x, bounds.Height));
        }

        context.DrawLine(
            new Pen(new SolidColorBrush(Color.Parse("#E5E7EB")), 1),
            new Point(0, bounds.Height - 0.5),
            new Point(bounds.Width, bounds.Height - 0.5));
    }
}

internal sealed class TimelineRuler : Control
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        return new Size(width, 26);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.White, bounds);

        var textBrush = new SolidColorBrush(Color.Parse("#6B7280"));
        var typeface = new Typeface("Segoe UI");
        double pxPerHour = bounds.Width / 24.0;
        int step = pxPerHour >= 56 ? 1 : pxPerHour >= 28 ? 2 : 3;

        for (int hour = 0; hour <= 24; hour += step)
        {
            double x = bounds.Width * (hour / 24.0);
            string label = hour == 24 ? "24:00" : $"{hour:00}:00";
            var formatted = new FormattedText(
                label,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                10,
                textBrush);

            double textX = hour switch
            {
                0 => 0,
                24 => bounds.Width - formatted.Width,
                _ => x - (formatted.Width / 2)
            };

            context.DrawText(formatted, new Point(textX, 6));
        }
    }
}

internal sealed class HourlyChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<HourlyChart, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IReadOnlyList<string>?> LabelsProperty =
        AvaloniaProperty.Register<HourlyChart, IReadOnlyList<string>?>(nameof(Labels));

    static HourlyChart()
    {
        AffectsRender<HourlyChart>(ValuesProperty, LabelsProperty);
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IReadOnlyList<string>? Labels
    {
        get => GetValue(LabelsProperty);
        set => SetValue(LabelsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 140 : availableSize.Height;
        return new Size(width, height);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var values = Values;
        if (values is null || values.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        double max = values.Max();
        if (max <= 0)
        {
            max = 1;
        }

        int count = values.Count;
        double gap = count > 20 ? 1 : 2;
        double barWidth = Math.Max(2, (bounds.Width - (gap * (count - 1))) / count);
        double chartHeight = bounds.Height - 18;

        for (int i = 0; i < count; i++)
        {
            double ratio = values[i] / max;
            double h = Math.Max(values[i] > 0 ? 3 : 0, chartHeight * ratio);
            double x = i * (barWidth + gap);
            double y = chartHeight - h;

            var color = Color.Parse("#2F9E6B");
            byte alpha = (byte)Math.Clamp(90 + (ratio * 140), 90, 230);
            context.FillRectangle(
                new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)),
                new Rect(x, y, barWidth, h),
                2);
        }

        var textBrush = new SolidColorBrush(Color.Parse("#6B7280"));
        var typeface = new Typeface("Segoe UI");
        var labels = Labels;
        if (labels is { Count: > 0 })
        {
            int labelCount = Math.Min(labels.Count, count);
            for (int i = 0; i < labelCount; i++)
            {
                string label = labels[i];
                if (string.IsNullOrEmpty(label))
                {
                    continue;
                }

                double x = i * (barWidth + gap);
                var ft = new FormattedText(
                    label,
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    10,
                    textBrush);
                context.DrawText(ft, new Point(x, chartHeight + 4));
            }
        }
        else if (count == 24)
        {
            for (int hour = 0; hour < 24; hour += 3)
            {
                double x = hour * (barWidth + gap);
                var ft = new FormattedText(
                    $"{hour:00}",
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    10,
                    textBrush);
                context.DrawText(ft, new Point(x, chartHeight + 4));
            }
        }
    }
}

internal sealed class TimelineZoomSurface : Panel
{
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<TimelineZoomSurface, double>(nameof(Zoom), 1);

    public static readonly StyledProperty<double> ViewportWidthProperty =
        AvaloniaProperty.Register<TimelineZoomSurface, double>(nameof(ViewportWidth));

    static TimelineZoomSurface()
    {
        AffectsMeasure<TimelineZoomSurface>(ZoomProperty, ViewportWidthProperty);
        AffectsArrange<TimelineZoomSurface>(ZoomProperty, ViewportWidthProperty);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public double ViewportWidth
    {
        get => GetValue(ViewportWidthProperty);
        set => SetValue(ViewportWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double viewport = ViewportWidth > 1
            ? ViewportWidth
            : double.IsInfinity(availableSize.Width) ? 800 : Math.Max(1, availableSize.Width);
        double zoom = Math.Clamp(Zoom, 1, 8);
        double width = viewport * zoom;
        double height = 0;

        var constraint = new Size(width, availableSize.Height);
        foreach (Control child in Children)
        {
            child.Measure(constraint);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        if (height <= 0)
        {
            height = double.IsInfinity(availableSize.Height) ? 224 : availableSize.Height;
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rect = new Rect(finalSize);
        foreach (Control child in Children)
        {
            child.Arrange(rect);
        }

        return finalSize;
    }
}

internal static class AppColor
{
    public static IBrush For(string exePath, string processName) =>
        new SolidColorBrush(AppIconLoader.GetAccent(exePath, processName));
}

internal static class AwayHatch
{
    public static readonly Color Fill = Color.Parse("#AEB4BE");
    public static readonly Color Stripe = Color.Parse("#A0A7B1");
    public static readonly Color SoftFill = Color.Parse("#EEF0F3");
    public static readonly Color SoftStripe = Color.Parse("#E4E7EB");

    public static void Draw(DrawingContext context, Rect rect, bool soft, double cornerRadius = 0)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        Color fill = soft ? SoftFill : Fill;
        Color stripe = soft ? SoftStripe : Stripe;
        context.FillRectangle(new SolidColorBrush(fill), rect, (float)cornerRadius);

        var pen = new Pen(new SolidColorBrush(stripe), 1);
        const double spacing = 6;

        using (context.PushClip(rect))
        {
            double start = -rect.Height;
            double end = rect.Width + rect.Height;
            for (double offset = start; offset < end; offset += spacing)
            {
                context.DrawLine(
                    pen,
                    new Point(rect.X + offset, rect.Y),
                    new Point(rect.X + offset + rect.Height, rect.Y + rect.Height));
            }
        }
    }
}

internal sealed class AwayFill : Control
{
    public static readonly StyledProperty<bool> SoftProperty =
        AvaloniaProperty.Register<AwayFill, bool>(nameof(Soft));

    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<AwayFill, double>(nameof(CornerRadius), 2);

    static AwayFill()
    {
        AffectsRender<AwayFill>(SoftProperty, CornerRadiusProperty);
    }

    public AwayFill()
    {
        ClipToBounds = true;
    }

    public bool Soft
    {
        get => GetValue(SoftProperty);
        set => SetValue(SoftProperty, value);
    }

    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 16 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 16 : availableSize.Height;
        return new Size(width, height);
    }

    public override void Render(DrawingContext context)
    {
        AwayHatch.Draw(context, new Rect(Bounds.Size), Soft, CornerRadius);
    }
}
