using System.Windows;
using System.Windows.Media;

namespace DiskAnalyzer.App.Controls;

/// <summary>Lightweight horizontal bar (0..1). Drawn directly; cheap enough for thousands of virtualized rows.</summary>
public sealed class UsageBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(UsageBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(UsageBar), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(UsageBar), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(double), typeof(UsageBar), new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public Brush BarBrush { get => (Brush)GetValue(BarBrushProperty); set => SetValue(BarBrushProperty, value); }

    public Brush? TrackBrush { get => (Brush?)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(0, double.IsInfinity(availableSize.Height) ? 6 : 0);

    protected override void OnRender(DrawingContext drawingContext)
    {
        DrawingContext dc = drawingContext;
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        double r = Math.Min(Radius, h / 2);
        if (TrackBrush is not null)
        {
            dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, w, h), r, r);
        }

        double v = double.IsNaN(Value) ? 0 : Math.Clamp(Value, 0, 1);
        if (v > 0)
        {
            double fill = Math.Max(Math.Min(w, 2 * r), w * v);
            dc.DrawRoundedRectangle(BarBrush, null, new Rect(0, 0, fill, h), r, r);
        }
    }
}
