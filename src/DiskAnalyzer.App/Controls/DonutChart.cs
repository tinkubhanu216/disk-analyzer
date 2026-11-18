using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DiskAnalyzer.App.Controls;

public sealed record ChartSegment(string Label, double Value, Brush Brush);

/// <summary>Donut chart drawn with StreamGeometry arcs; segments come from <see cref="Segments"/>.</summary>
public sealed class DonutChart : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IEnumerable), typeof(DonutChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingThicknessProperty = DependencyProperty.Register(
        nameof(RingThickness), typeof(double), typeof(DonutChart), new FrameworkPropertyMetadata(26.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CenterTitleProperty = DependencyProperty.Register(
        nameof(CenterTitle), typeof(string), typeof(DonutChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CenterSubtitleProperty = DependencyProperty.Register(
        nameof(CenterSubtitle), typeof(string), typeof(DonutChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(DonutChart), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Segments { get => (IEnumerable?)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }

    public double RingThickness { get => (double)GetValue(RingThicknessProperty); set => SetValue(RingThicknessProperty, value); }

    public string? CenterTitle { get => (string?)GetValue(CenterTitleProperty); set => SetValue(CenterTitleProperty, value); }

    public string? CenterSubtitle { get => (string?)GetValue(CenterSubtitleProperty); set => SetValue(CenterSubtitleProperty, value); }

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        double size = Math.Min(double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 200 : availableSize.Height);
        return new Size(size, size);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        DrawingContext dc = drawingContext;
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double outer = (size / 2) - 2;
        double inner = Math.Max(4, outer - RingThickness);
        List<ChartSegment> segments = Segments?.OfType<ChartSegment>().Where(s => s.Value > 0).ToList() ?? [];
        double total = segments.Sum(s => s.Value);

        if (total <= 0)
        {
            dc.DrawEllipse(null, new Pen(Foreground, 1) { DashStyle = DashStyles.Dash }, center, (outer + inner) / 2, (outer + inner) / 2);
        }
        else
        {
            double angle = -90;
            foreach (ChartSegment s in segments)
            {
                double sweep = s.Value / total * 360;
                if (sweep >= 359.99)
                {
                    var ring = new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(center, outer, outer), new EllipseGeometry(center, inner, inner));
                    dc.DrawGeometry(s.Brush, null, ring);
                }
                else if (sweep > 0.2)
                {
                    dc.DrawGeometry(s.Brush, null, Arc(center, inner, outer, angle, Math.Max(0.2, sweep - 0.6)));
                }

                angle += sweep;
            }
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (!string.IsNullOrEmpty(CenterTitle))
        {
            var title = new FormattedText(CenterTitle, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), Math.Max(12, inner / 3.4), Foreground, dpi)
            { MaxTextWidth = inner * 1.8, TextAlignment = TextAlignment.Center };
            dc.DrawText(title, new Point(center.X - (inner * 0.9), center.Y - title.Height + (string.IsNullOrEmpty(CenterSubtitle) ? title.Height / 2 : 2)));
        }

        if (!string.IsNullOrEmpty(CenterSubtitle))
        {
            var subtitle = new FormattedText(CenterSubtitle, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), Math.Max(10, inner / 7.5), Foreground, dpi)
            { MaxTextWidth = inner * 1.8, TextAlignment = TextAlignment.Center };
            subtitle.SetForegroundBrush(Foreground.CloneCurrentValue() is SolidColorBrush b ? new SolidColorBrush(b.Color) { Opacity = 0.7 } : Foreground);
            dc.DrawText(subtitle, new Point(center.X - (inner * 0.9), center.Y + 4));
        }
    }

    private static StreamGeometry Arc(Point c, double inner, double outer, double startDeg, double sweepDeg)
    {
        double a0 = startDeg * Math.PI / 180, a1 = (startDeg + sweepDeg) * Math.PI / 180;
        Point P(double r, double a) => new(c.X + (r * Math.Cos(a)), c.Y + (r * Math.Sin(a)));
        bool large = sweepDeg > 180;
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(P(outer, a0), true, true);
            ctx.ArcTo(P(outer, a1), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true, false);
            ctx.LineTo(P(inner, a1), true, false);
            ctx.ArcTo(P(inner, a0), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }
}
