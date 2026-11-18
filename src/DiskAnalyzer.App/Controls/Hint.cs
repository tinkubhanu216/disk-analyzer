using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace DiskAnalyzer.App.Controls;

/// <summary>Attached placeholder text for TextBox, drawn in an adorner while the box is empty.</summary>
public static class Hint
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Hint), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);

    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
        {
            return;
        }

        box.Loaded -= Refresh;
        box.TextChanged -= Refresh;
        box.Loaded += Refresh;
        box.TextChanged += Refresh;
        Refresh(box, EventArgs.Empty);
    }

    private static void Refresh(object sender, EventArgs e)
    {
        var box = (TextBox)sender;
        AdornerLayer? layer = AdornerLayer.GetAdornerLayer(box);
        if (layer is null)
        {
            return;
        }

        HintAdorner? existing = layer.GetAdorners(box)?.OfType<HintAdorner>().FirstOrDefault();
        bool show = string.IsNullOrEmpty(box.Text) && !string.IsNullOrEmpty(GetText(box));
        if (show && existing is null)
        {
            layer.Add(new HintAdorner(box));
        }
        else if (!show && existing is not null)
        {
            layer.Remove(existing);
        }
        else
        {
            existing?.InvalidateVisual();
        }
    }

    private sealed class HintAdorner : Adorner
    {
        public HintAdorner(TextBox box)
            : base(box) => IsHitTestVisible = false;

        protected override void OnRender(DrawingContext drawingContext)
        {
            var box = (TextBox)AdornedElement;
            string text = GetText(box) ?? string.Empty;
            Brush brush = (box.TryFindResource("App.TextTertiary") as Brush) ?? Brushes.Gray;
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(box.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), box.FontSize, brush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, box.ActualWidth - box.Padding.Left - box.Padding.Right - 8),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            double left = box.Padding.Left + box.BorderThickness.Left + 3;
            double top = (box.ActualHeight - formatted.Height) / 2;
            drawingContext.DrawText(formatted, new Point(left, top));
        }
    }
}
