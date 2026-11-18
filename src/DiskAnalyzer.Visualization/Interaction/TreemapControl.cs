using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DiskAnalyzer.Core.Algorithms;
using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;
using DiskAnalyzer.Visualization.Rendering;
using DiskAnalyzer.Visualization.Treemap;

namespace DiskAnalyzer.Visualization.Interaction;

public sealed class TreemapNodeEventArgs(RoutedEvent routedEvent, int treeIndex, TreemapNodeKind kind, int aggregateCount) : RoutedEventArgs(routedEvent)
{
    public int TreeIndex { get; } = treeIndex;

    public TreemapNodeKind Kind { get; } = kind;

    public int AggregateCount { get; } = aggregateCount;

    public bool IsFolderLike => Kind is TreemapNodeKind.Folder or TreemapNodeKind.FolderTile or TreemapNodeKind.Aggregate;
}

public sealed class TreemapZoomEventArgs(RoutedEvent routedEvent, bool zoomIn, int treeIndex) : RoutedEventArgs(routedEvent)
{
    public bool ZoomIn { get; } = zoomIn;

    /// <summary>Node under the mouse (or None).</summary>
    public int TreeIndex { get; } = treeIndex;
}

public sealed class TreemapRenderedEventArgs(RoutedEvent routedEvent, int nodeCount, TimeSpan layoutTime, TimeSpan renderTime) : RoutedEventArgs(routedEvent)
{
    public int NodeCount { get; } = nodeCount;

    public TimeSpan LayoutTime { get; } = layoutTime;

    public TimeSpan RenderTime { get; } = renderTime;
}

/// <summary>
/// Interactive cushion treemap. The tiles are rendered off the UI thread into a single bitmap; labels,
/// highlights, selection and hover are drawn in lightweight <see cref="DrawingVisual"/> overlays.
/// No WPF element is created per rectangle, so 100k+ tiles stay responsive.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    // ------------------------------------------------------------------ dependency properties

    public static readonly DependencyProperty TreeProperty = Register(nameof(Tree), typeof(ScanTree), null, relayout: true);
    public static readonly DependencyProperty RootNodeProperty = Register(nameof(RootNode), typeof(int), ScanTree.RootIndex, relayout: true);
    public static readonly DependencyProperty ColorModeProperty = Register(nameof(ColorMode), typeof(TreemapColorMode), TreemapColorMode.FileType, relayout: true);
    public static readonly DependencyProperty MinSizeBytesProperty = Register(nameof(MinSizeBytes), typeof(long), 0L, relayout: true);
    public static readonly DependencyProperty IsDarkProperty = Register(nameof(IsDark), typeof(bool), true, relayout: true);
    public static readonly DependencyProperty ShowLabelsProperty = Register(nameof(ShowLabels), typeof(bool), true, relayout: false);
    public static readonly DependencyProperty HighlightLargestFilesProperty = Register(nameof(HighlightLargestFiles), typeof(bool), false, relayout: false);
    public static readonly DependencyProperty HighlightLargestFoldersProperty = Register(nameof(HighlightLargestFolders), typeof(bool), false, relayout: false);
    public static readonly DependencyProperty HighlightCountProperty = Register(nameof(HighlightCount), typeof(int), 10, relayout: false);
    public static readonly DependencyProperty SearchTextProperty = Register(nameof(SearchText), typeof(string), null, relayout: false);
    public static readonly DependencyProperty DriveCapacityBytesProperty = Register(nameof(DriveCapacityBytes), typeof(long), 0L, relayout: false);

    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.Register(
        nameof(SelectedNode), typeof(int), typeof(TreemapControl),
        new FrameworkPropertyMetadata(ScanTree.None, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((TreemapControl)d).RedrawDecorations()));

    private static readonly DependencyPropertyKey SearchMatchCountPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(SearchMatchCount), typeof(int), typeof(TreemapControl), new PropertyMetadata(0));

    public static readonly DependencyProperty SearchMatchCountProperty = SearchMatchCountPropertyKey.DependencyProperty;

    public static readonly RoutedEvent NodeClickEvent = EventManager.RegisterRoutedEvent(nameof(NodeClick), RoutingStrategy.Bubble, typeof(EventHandler<TreemapNodeEventArgs>), typeof(TreemapControl));
    public static readonly RoutedEvent NodeRightClickEvent = EventManager.RegisterRoutedEvent(nameof(NodeRightClick), RoutingStrategy.Bubble, typeof(EventHandler<TreemapNodeEventArgs>), typeof(TreemapControl));
    public static readonly RoutedEvent ZoomRequestedEvent = EventManager.RegisterRoutedEvent(nameof(ZoomRequested), RoutingStrategy.Bubble, typeof(EventHandler<TreemapZoomEventArgs>), typeof(TreemapControl));
    public static readonly RoutedEvent RenderedEvent = EventManager.RegisterRoutedEvent(nameof(Rendered), RoutingStrategy.Bubble, typeof(EventHandler<TreemapRenderedEventArgs>), typeof(TreemapControl));

    private static readonly Typeface LabelTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface LabelBoldTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Pen SelectionPen = Freeze(new Pen(Brushes.White, 2.5));
    private static readonly Pen SelectionInnerPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 1));
    private static readonly Pen HoverPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)), 2));
    private static readonly Pen LargestFilePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB9, 0x00)), 2.5));
    private static readonly Pen LargestFolderPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF)), 2.5));
    private static readonly Pen SearchPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x3D, 0xD6)), 2));
    private static readonly Brush SearchFill = Freeze(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromArgb(240, 255, 255, 255)));
    private static readonly Brush LabelShadowBrush = Freeze(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)));

    private readonly VisualCollection _visuals;
    private readonly DrawingVisual _bitmapVisual = new();
    private readonly DrawingVisual _decorVisual = new();
    private readonly DrawingVisual _hoverVisual = new();
    private readonly Popup _tooltip;
    private readonly StackPanel _tooltipPanel;
    private readonly Border _tooltipBorder;

    private CancellationTokenSource? _renderCts;
    private bool _renderScheduled;
    private TreemapLayout? _layout;
    private ScanTree? _layoutTree;
    private WriteableBitmap? _bitmap;
    private double _scaleX = 1, _scaleY = 1;
    private TreemapNode? _hover;
    private HashSet<int> _largestFiles = [];
    private HashSet<int> _largestFolders = [];

    public TreemapControl()
    {
        _visuals = new VisualCollection(this) { _bitmapVisual, _decorVisual, _hoverVisual };
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;

        _tooltipPanel = new StackPanel { MaxWidth = 520 };
        _tooltipBorder = new Border
        {
            Child = _tooltipPanel,
            Padding = new Thickness(12, 9, 12, 10),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
        };
        _tooltip = new Popup
        {
            Child = _tooltipBorder,
            AllowsTransparency = true,
            Placement = PlacementMode.Relative,
            PlacementTarget = this,
            IsHitTestVisible = false,
            StaysOpen = true,
        };

        Unloaded += (_, _) =>
        {
            _tooltip.IsOpen = false;
            _renderCts?.Cancel();
        };
        Loaded += (_, _) => ScheduleRender();
    }

    public ScanTree? Tree { get => (ScanTree?)GetValue(TreeProperty); set => SetValue(TreeProperty, value); }

    public int RootNode { get => (int)GetValue(RootNodeProperty); set => SetValue(RootNodeProperty, value); }

    public TreemapColorMode ColorMode { get => (TreemapColorMode)GetValue(ColorModeProperty); set => SetValue(ColorModeProperty, value); }

    public long MinSizeBytes { get => (long)GetValue(MinSizeBytesProperty); set => SetValue(MinSizeBytesProperty, value); }

    public bool IsDark { get => (bool)GetValue(IsDarkProperty); set => SetValue(IsDarkProperty, value); }

    public bool ShowLabels { get => (bool)GetValue(ShowLabelsProperty); set => SetValue(ShowLabelsProperty, value); }

    public bool HighlightLargestFiles { get => (bool)GetValue(HighlightLargestFilesProperty); set => SetValue(HighlightLargestFilesProperty, value); }

    public bool HighlightLargestFolders { get => (bool)GetValue(HighlightLargestFoldersProperty); set => SetValue(HighlightLargestFoldersProperty, value); }

    public int HighlightCount { get => (int)GetValue(HighlightCountProperty); set => SetValue(HighlightCountProperty, value); }

    public string? SearchText { get => (string?)GetValue(SearchTextProperty); set => SetValue(SearchTextProperty, value); }

    public long DriveCapacityBytes { get => (long)GetValue(DriveCapacityBytesProperty); set => SetValue(DriveCapacityBytesProperty, value); }

    public int SelectedNode { get => (int)GetValue(SelectedNodeProperty); set => SetValue(SelectedNodeProperty, value); }

    public int SearchMatchCount => (int)GetValue(SearchMatchCountProperty);

    public ITreemapLayoutEngine LayoutEngine { get; set; } = new SquarifiedTreemapLayout();

    public ITreemapRenderer Renderer { get; set; } = new CushionTreemapRenderer();

    public TreemapLayoutOptions LayoutOptions { get; set; } = new();

    /// <summary>The most recent layout (for tests and diagnostics).</summary>
    public TreemapLayout? CurrentLayout => _layout;

    public event EventHandler<TreemapNodeEventArgs> NodeClick { add => AddHandler(NodeClickEvent, value); remove => RemoveHandler(NodeClickEvent, value); }

    public event EventHandler<TreemapNodeEventArgs> NodeRightClick { add => AddHandler(NodeRightClickEvent, value); remove => RemoveHandler(NodeRightClickEvent, value); }

    public event EventHandler<TreemapZoomEventArgs> ZoomRequested { add => AddHandler(ZoomRequestedEvent, value); remove => RemoveHandler(ZoomRequestedEvent, value); }

    public event EventHandler<TreemapRenderedEventArgs> Rendered { add => AddHandler(RenderedEvent, value); remove => RemoveHandler(RenderedEvent, value); }

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height);

    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters) => new PointHitTestResult(this, hitTestParameters.HitPoint);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ScheduleRender();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ScheduleRender();
    }

    /// <summary>Forces a new layout and render (e.g. after the tree content changed).</summary>
    public void Refresh() => ScheduleRender();

    private static DependencyProperty Register(string name, Type type, object? defaultValue, bool relayout) =>
        DependencyProperty.Register(name, type, typeof(TreemapControl), new FrameworkPropertyMetadata(defaultValue, (d, _) =>
        {
            var control = (TreemapControl)d;
            if (relayout)
            {
                control.ScheduleRender();
            }
            else
            {
                control.UpdateHighlightSets();
                control.RedrawDecorations();
            }
        }));

    private static T Freeze<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    // ------------------------------------------------------------------ layout + render pipeline

    private void ScheduleRender()
    {
        if (_renderScheduled)
        {
            return;
        }

        _renderScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _renderScheduled = false;
            _ = RenderAsync();
        }));
    }

    private async Task RenderAsync()
    {
        _renderCts?.Cancel();
        var cts = new CancellationTokenSource();
        _renderCts = cts;

        ScanTree? tree = Tree;
        int root = RootNode;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Floor(ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Floor(ActualHeight * dpi.DpiScaleY);
        if (tree is null || width < 8 || height < 8 || root < 0 || root >= tree.Count || !IsLoaded)
        {
            _layout = null;
            _layoutTree = null;
            ClearVisuals();
            return;
        }

        // Layout runs in device pixels; scale DIP-based sizes so headers/padding look the same at any DPI.
        double s = dpi.DpiScaleY;
        TreemapLayoutOptions options = LayoutOptions with
        {
            MinSizeBytes = MinSizeBytes,
            MinTilePixels = LayoutOptions.MinTilePixels * s,
            MinFolderPixels = LayoutOptions.MinFolderPixels * s,
            FolderHeaderHeight = LayoutOptions.FolderHeaderHeight * s,
            FolderHeaderMinWidth = LayoutOptions.FolderHeaderMinWidth * s,
            FolderPadding = LayoutOptions.FolderPadding * s,
        };
        TreemapColorMode mode = ColorMode;
        bool dark = IsDark;
        var bounds = new TreemapRect(0, 0, width, height);

        try
        {
            var (layout, pixels, layoutTime, renderTime) = await Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                TreemapLayout l = LayoutEngine.Layout(tree, root, bounds, options, cts.Token);
                TimeSpan lt = sw.Elapsed;
                sw.Restart();
                int[] buffer = new int[width * height];
                if (Renderer is CushionTreemapRenderer cushion)
                {
                    cushion.BackgroundColor = dark ? 0x1C1C1Cu : 0xF3F3F3u;
                }

                Renderer.Render(l, buffer, width, height, CreateColorSelector(tree, l, mode, dark), cts.Token);
                return (l, buffer, lt, sw.Elapsed);
            }, cts.Token).ConfigureAwait(true);

            if (cts.IsCancellationRequested)
            {
                return;
            }

            _scaleX = dpi.DpiScaleX;
            _scaleY = dpi.DpiScaleY;
            if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height || Math.Abs(_bitmap.DpiX - (96 * _scaleX)) > 0.01)
            {
                _bitmap = new WriteableBitmap(width, height, 96 * _scaleX, 96 * _scaleY, PixelFormats.Bgr32, null);
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
            _layout = layout;
            _layoutTree = tree;
            _hover = null;

            using (DrawingContext dc = _bitmapVisual.RenderOpen())
            {
                dc.DrawImage(_bitmap, new Rect(0, 0, width / _scaleX, height / _scaleY));
            }

            UpdateHighlightSets();
            RedrawDecorations();
            DrawHover();
            RaiseEvent(new TreemapRenderedEventArgs(RenderedEvent, layout.Nodes.Count, layoutTime, renderTime));
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer render
        }
    }

    private void ClearVisuals()
    {
        using (_bitmapVisual.RenderOpen())
        {
        }

        using (_decorVisual.RenderOpen())
        {
        }

        using (_hoverVisual.RenderOpen())
        {
        }

        _tooltip.IsOpen = false;
    }

    private static Func<TreemapNode, uint> CreateColorSelector(ScanTree tree, TreemapLayout layout, TreemapColorMode mode, bool dark)
    {
        uint[] extensionColors = tree.Extensions.Select(TreemapColorScheme.GetExtensionColor).ToArray();
        DateTime now = DateTime.UtcNow;
        double maxLog = 1;
        if (mode == TreemapColorMode.Size)
        {
            long max = 1;
            foreach (TreemapNode n in layout.Nodes)
            {
                if (n.IsLeaf && n.SizeBytes > max)
                {
                    max = n.SizeBytes;
                }
            }

            maxLog = Math.Log(max + 1);
        }

        return node =>
        {
            if (node.Kind == TreemapNodeKind.Folder)
            {
                return TreemapColorScheme.FolderFrameColor(dark, node.Depth);
            }

            if (node.Kind == TreemapNodeKind.Aggregate)
            {
                return TreemapColorScheme.AggregateColor;
            }

            return mode switch
            {
                TreemapColorMode.Age => TreemapColorScheme.Heat(TreemapColorScheme.AgeHeat(tree.GetLastWriteUtcTicks(node.TreeIndex), now)),
                TreemapColorMode.Size => TreemapColorScheme.Heat(Math.Log(node.SizeBytes + 1) / maxLog),
                _ => node.Kind == TreemapNodeKind.FolderTile ? TreemapColorScheme.FolderTileColor : extensionColors[node.ExtensionId],
            };
        };
    }

    private void UpdateHighlightSets()
    {
        ScanTree? tree = _layoutTree;
        if (tree is null || _layout is null)
        {
            _largestFiles = [];
            _largestFolders = [];
            return;
        }

        int root = _layout.RootTreeIndex;
        int count = Math.Clamp(HighlightCount, 1, 200);
        _largestFiles = HighlightLargestFiles
            ? [.. TreeAlgorithms.TopN(tree, root, count, i => !tree.IsDirectory(i) && (tree.GetFlags(i) & NodeFlags.HardLinkDuplicate) == 0, tree.GetSize)]
            : [];
        _largestFolders = HighlightLargestFolders
            ? [.. TreeAlgorithms.TopN(tree, root, count, i => i != root && tree.IsDirectory(i), tree.GetSize)]
            : [];
    }

    // ------------------------------------------------------------------ decorations

    private Rect ToDip(TreemapRect r) => new(r.X / _scaleX, r.Y / _scaleY, r.Width / _scaleX, r.Height / _scaleY);

    private void RedrawDecorations()
    {
        TreemapLayout? layout = _layout;
        ScanTree? tree = _layoutTree;
        using DrawingContext dc = _decorVisual.RenderOpen();
        if (layout is null || tree is null)
        {
            return;
        }

        double pixelsPerDip = _scaleY;

        if (ShowLabels)
        {
            DrawLabels(dc, layout, tree, pixelsPerDip);
        }

        // Search matches among visible tiles.
        int matches = 0;
        string? search = SearchText;
        if (!string.IsNullOrWhiteSpace(search))
        {
            NameMatcher matcher = NameMatcher.Create(search);
            foreach (TreemapNode node in layout.Nodes)
            {
                if (node == layout.Root || node.Kind == TreemapNodeKind.Aggregate || !matcher.IsMatch(tree.GetName(node.TreeIndex)))
                {
                    continue;
                }

                matches++;
                Rect r = ToDip(node.Bounds);
                if (r.Width >= 1 && r.Height >= 1)
                {
                    dc.DrawRectangle(SearchFill, SearchPen, Inset(r, 1));
                }
            }
        }

        SetValue(SearchMatchCountPropertyKey, matches);

        if (_largestFolders.Count > 0 || _largestFiles.Count > 0)
        {
            foreach (TreemapNode node in layout.Nodes)
            {
                if (node.Kind == TreemapNodeKind.Aggregate)
                {
                    continue;
                }

                if (_largestFolders.Contains(node.TreeIndex) && node.IsDirectoryLike)
                {
                    dc.DrawRectangle(null, LargestFolderPen, Inset(ToDip(node.Bounds), 1.25));
                }
                else if (_largestFiles.Contains(node.TreeIndex) && node.Kind == TreemapNodeKind.File)
                {
                    dc.DrawRectangle(null, LargestFilePen, Inset(ToDip(node.Bounds), 1.25));
                }
            }
        }

        int selected = SelectedNode;
        if (selected >= 0)
        {
            TreemapNode? node = layout.FindByTreeIndex(selected);
            if (node is not null && node != layout.Root)
            {
                Rect r = Inset(ToDip(node.Bounds), 1.25);
                dc.DrawRectangle(null, SelectionPen, r);
                dc.DrawRectangle(null, SelectionInnerPen, Inset(r, 1.75));
            }
        }
    }

    private void DrawLabels(DrawingContext dc, TreemapLayout layout, ScanTree tree, double pixelsPerDip)
    {
        const int MaxLabels = 2500;
        int drawn = 0;
        Brush headerText = IsDark ? Brushes.WhiteSmoke : Brushes.Black;
        foreach (TreemapNode node in layout.Nodes)
        {
            if (drawn >= MaxLabels)
            {
                break;
            }

            if (node == layout.Root)
            {
                continue;
            }

            Rect r = ToDip(node.Bounds);
            if (node.Kind == TreemapNodeKind.Folder)
            {
                double headerHeight = (node.ContentBounds.Y - node.Bounds.Y) / _scaleY;
                if (headerHeight >= 12 && r.Width >= 50)
                {
                    string text = $"{tree.GetName(node.TreeIndex)}  {SizeFormatter.Format(node.SizeBytes)}";
                    DrawText(dc, text, new Rect(r.X + 3, r.Y, r.Width - 6, headerHeight), 10.5, LabelBoldTypeface, headerText, null, pixelsPerDip);
                    drawn++;
                }

                continue;
            }

            if (r.Width < 44 || r.Height < 16)
            {
                continue;
            }

            string name = node.Kind == TreemapNodeKind.Aggregate ? $"{node.AggregateCount:N0} smaller items" : tree.GetName(node.TreeIndex);
            var area = new Rect(r.X + 3, r.Y + 2, r.Width - 6, r.Height - 4);
            double fontSize = Math.Clamp(Math.Min(r.Height / 3.2, r.Width / 9), 10, 15);
            DrawText(dc, name, area, fontSize, LabelTypeface, LabelBrush, LabelShadowBrush, pixelsPerDip);
            if (r.Height >= 34)
            {
                DrawText(dc, SizeFormatter.Format(node.SizeBytes), new Rect(area.X, area.Y + fontSize + 3, area.Width, area.Height - fontSize - 3), fontSize - 1, LabelTypeface, LabelBrush, LabelShadowBrush, pixelsPerDip);
            }

            drawn++;
        }
    }

    private static void DrawText(DrawingContext dc, string text, Rect area, double size, Typeface typeface, Brush brush, Brush? shadow, double pixelsPerDip)
    {
        if (area.Width < 8 || area.Height < size)
        {
            return;
        }

        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush, pixelsPerDip)
        {
            MaxTextWidth = area.Width,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.PushClip(new RectangleGeometry(area));
        double y = area.Y + Math.Max(0, (Math.Min(area.Height, formatted.Height + 2) - formatted.Height) / 2);
        if (shadow is not null)
        {
            formatted.SetForegroundBrush(shadow);
            dc.DrawText(formatted, new Point(area.X + 0.8, y + 0.8));
            formatted.SetForegroundBrush(brush);
        }

        dc.DrawText(formatted, new Point(area.X, y));
        dc.Pop();
    }

    private static Rect Inset(Rect r, double amount) =>
        r.Width > 2 * amount && r.Height > 2 * amount ? new Rect(r.X + amount, r.Y + amount, r.Width - (2 * amount), r.Height - (2 * amount)) : r;

    // ------------------------------------------------------------------ hover + tooltip

    private void DrawHover()
    {
        using DrawingContext dc = _hoverVisual.RenderOpen();
        if (_hover is not null && _layout is not null && _hover != _layout.Root)
        {
            dc.DrawRectangle(null, HoverPen, Inset(ToDip(_hover.Bounds), 1));
        }
    }

    private TreemapNode? HitTestNode(Point dip) => _layout?.HitTest(dip.X * _scaleX, dip.Y * _scaleY);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point position = e.GetPosition(this);
        TreemapNode? node = HitTestNode(position);
        if (node == _layout?.Root)
        {
            node = null;
        }

        if (!ReferenceEquals(node, _hover))
        {
            _hover = node;
            DrawHover();
            UpdateTooltipContent();
            Cursor = node is { IsDirectoryLike: true } or { Kind: TreemapNodeKind.Aggregate } ? Cursors.Hand : null;
        }

        if (_hover is null)
        {
            _tooltip.IsOpen = false;
            return;
        }

        // Keep the tooltip near the cursor but inside the control's area.
        _tooltipBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = _tooltipBorder.DesiredSize;
        double x = position.X + 18;
        double y = position.Y + 20;
        if (x + size.Width > ActualWidth)
        {
            x = Math.Max(0, position.X - size.Width - 12);
        }

        if (y + size.Height > ActualHeight)
        {
            y = Math.Max(0, position.Y - size.Height - 12);
        }

        _tooltip.HorizontalOffset = x;
        _tooltip.VerticalOffset = y;
        _tooltip.IsOpen = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        DrawHover();
        _tooltip.IsOpen = false;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        Focus();
        TreemapNode? node = HitTestNode(e.GetPosition(this));
        if (node is not null && node != _layout?.Root)
        {
            RaiseEvent(new TreemapNodeEventArgs(NodeClickEvent, node.TreeIndex, node.Kind, node.AggregateCount));
            e.Handled = true;
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        TreemapNode? node = HitTestNode(e.GetPosition(this));
        if (node is not null && node != _layout?.Root)
        {
            _tooltip.IsOpen = false;
            RaiseEvent(new TreemapNodeEventArgs(NodeRightClickEvent, node.TreeIndex, node.Kind, node.AggregateCount));
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        TreemapNode? node = HitTestNode(e.GetPosition(this));
        RaiseEvent(new TreemapZoomEventArgs(ZoomRequestedEvent, e.Delta > 0, node?.TreeIndex ?? ScanTree.None));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Back or Key.OemMinus or Key.Subtract)
        {
            RaiseEvent(new TreemapZoomEventArgs(ZoomRequestedEvent, false, ScanTree.None));
            e.Handled = true;
        }
    }

    private void UpdateTooltipContent()
    {
        _tooltipPanel.Children.Clear();
        TreemapNode? node = _hover;
        ScanTree? tree = _layoutTree;
        if (node is null || tree is null)
        {
            return;
        }

        bool dark = IsDark;
        _tooltipBorder.Background = new SolidColorBrush(dark ? Color.FromArgb(0xF2, 0x2B, 0x2B, 0x2B) : Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF));
        _tooltipBorder.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x40, 0, 0, 0));
        Brush primary = dark ? Brushes.White : Brushes.Black;
        Brush secondary = new SolidColorBrush(dark ? Color.FromRgb(0xC8, 0xC8, 0xC8) : Color.FromRgb(0x5A, 0x5A, 0x5A));

        void Line(string text, Brush brush, double size = 12, FontWeight? weight = null, bool wrap = false) =>
            _tooltipPanel.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = brush,
                FontSize = size,
                FontWeight = weight ?? FontWeights.Normal,
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 0, 1),
            });

        int index = node.TreeIndex;
        long size = node.SizeBytes;
        int parent = node.Kind == TreemapNodeKind.Aggregate ? index : tree.GetParent(index);
        long parentSize = parent >= 0 ? tree.GetSize(parent) : 0;
        long capacity = DriveCapacityBytes;

        if (node.Kind == TreemapNodeKind.Aggregate)
        {
            Line($"{node.AggregateCount:N0} smaller items", primary, 13, FontWeights.SemiBold);
            Line($"in {tree.GetFullPath(index)}", secondary, 11.5, wrap: true);
            Line($"Size: {SizeFormatter.Format(size)}", primary);
            Line($"Of folder: {SizeFormatter.FormatPercent(SizeFormatter.Percent(size, tree.GetSize(index)))}", primary);
            Line("Click to open this folder", secondary, 11);
            return;
        }

        bool isDir = tree.IsDirectory(index);
        Line(tree.GetName(index), primary, 13, FontWeights.SemiBold);
        Line(tree.GetFullPath(index), secondary, 11.5, wrap: true);
        Line($"Size: {SizeFormatter.Format(size)}   (on disk: {SizeFormatter.Format(tree.GetAllocated(index))})", primary);
        if (capacity > 0)
        {
            Line($"Of drive: {SizeFormatter.FormatPercent(SizeFormatter.Percent(size, capacity))}", primary);
        }

        if (parentSize > 0)
        {
            Line($"Of parent folder: {SizeFormatter.FormatPercent(SizeFormatter.Percent(size, parentSize))}", primary);
        }

        if (tree.GetLastWriteUtc(index) is { } modified)
        {
            Line($"Modified: {modified.ToLocalTime():g}", primary);
        }

        if (isDir)
        {
            Line($"{tree.GetFileCount(index):N0} files, {tree.GetDirectoryCount(index):N0} folders", primary);
        }
        else
        {
            string ext = tree.GetExtension(index);
            Line($"Type: {(string.IsNullOrEmpty(ext) ? "(none)" : ext)} · {FileCategoryClassifier.Classify(ext)}", primary);
        }

        NodeFlags flags = tree.GetFlags(index);
        if ((flags & NodeFlags.HardLinkDuplicate) != 0)
        {
            Line("Hard link — bytes counted under another name", secondary, 11);
        }

        if ((flags & NodeFlags.ReparsePoint) != 0)
        {
            Line(isDir ? "Junction / symbolic link — not followed" : "Reparse point (link or cloud file)", secondary, 11);
        }

        if ((flags & NodeFlags.Inaccessible) != 0)
        {
            Line("Access denied — contents not counted", secondary, 11);
        }
    }
}
