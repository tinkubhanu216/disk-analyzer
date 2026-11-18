using System.IO;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;
using DiskAnalyzer.Visualization.Rendering;
using DiskAnalyzer.Visualization.Treemap;

namespace DiskAnalyzer.Visualization.Tests;

public class TreemapTests
{
    [Fact]
    public void Squarify_AreasAreProportional_AndFillBounds()
    {
        double[] values = [600, 600, 400, 400, 300, 200, 100, 50, 25, 5];
        var bounds = new TreemapRect(10, 20, 600, 400);
        var rects = new TreemapRect[values.Length];
        Squarifier.Squarify(values, bounds, rects);

        double total = values.Sum();
        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i] / total * bounds.Area, rects[i].Area, 6);
            Assert.True(rects[i].X >= bounds.X - 1e-9 && rects[i].Right <= bounds.Right + 1e-9);
            Assert.True(rects[i].Y >= bounds.Y - 1e-9 && rects[i].Bottom <= bounds.Bottom + 1e-9);
        }

        Assert.Equal(bounds.Area, rects.Sum(r => r.Area), 6);
    }

    [Fact]
    public void Squarify_RectanglesDoNotOverlap()
    {
        double[] values = Enumerable.Range(1, 60).Select(i => 1000.0 / i).ToArray();
        var rects = new TreemapRect[values.Length];
        Squarifier.Squarify(values, new TreemapRect(0, 0, 1000, 700), rects);
        for (int i = 0; i < rects.Length; i++)
        {
            for (int j = i + 1; j < rects.Length; j++)
            {
                double overlapW = Math.Min(rects[i].Right, rects[j].Right) - Math.Max(rects[i].X, rects[j].X);
                double overlapH = Math.Min(rects[i].Bottom, rects[j].Bottom) - Math.Max(rects[i].Y, rects[j].Y);
                Assert.False(overlapW > 1e-6 && overlapH > 1e-6, $"{i} overlaps {j}");
            }
        }
    }

    [Fact]
    public void Squarify_ProducesReasonableAspectRatios()
    {
        double[] values = Enumerable.Repeat(1.0, 16).ToArray();
        var rects = new TreemapRect[values.Length];
        Squarifier.Squarify(values, new TreemapRect(0, 0, 400, 400), rects);
        foreach (TreemapRect r in rects)
        {
            double aspect = Math.Max(r.Width / r.Height, r.Height / r.Width);
            Assert.True(aspect < 2.0, $"aspect {aspect}");
        }
    }

    [Fact]
    public void Squarify_HandlesZeroAndEmpty()
    {
        var rects = new TreemapRect[2];
        Squarifier.Squarify([0, 0], new TreemapRect(0, 0, 100, 100), rects);
        Assert.All(rects, r => Assert.True(r.IsEmpty));
        Squarifier.Squarify([], new TreemapRect(0, 0, 100, 100), Span<TreemapRect>.Empty);
    }

    private static ScanTree SampleTree()
    {
        var tree = new ScanTree(@"C:\");
        int big = tree.AddDirectory(0, "big", 0, FileAttributes.Directory);
        int small = tree.AddDirectory(0, "small", 0, FileAttributes.Directory);
        tree.AddFile(big, "movie.mkv", 8_000_000, 0, 0, FileAttributes.Normal);
        tree.AddFile(big, "song.mp3", 1_000_000, 0, 0, FileAttributes.Normal);
        for (int i = 0; i < 5_000; i++)
        {
            tree.AddFile(small, $"tiny{i}.txt", 10, 0, 0, FileAttributes.Normal);
        }

        tree.AddFile(0, "photo.jpg", 950_000, 0, 0, FileAttributes.Normal);
        new FolderAggregationService().Aggregate(tree);
        return tree;
    }

    [Fact]
    public void Layout_TileAreasAreProportionalToSize()
    {
        ScanTree tree = SampleTree();
        var options = new TreemapLayoutOptions { FolderPadding = 0, FolderHeaderHeight = 0, FolderHeaderMinWidth = double.MaxValue };
        TreemapLayout layout = new SquarifiedTreemapLayout().Layout(tree, 0, new TreemapRect(0, 0, 1000, 1000), options);

        double total = tree.GetSize(0);
        foreach (TreemapNode child in layout.Root.Children!)
        {
            Assert.Equal(child.SizeBytes / total * 1_000_000, child.Bounds.Area, 3);
        }

        TreemapNode movie = layout.Nodes.Single(n => n.Kind == TreemapNodeKind.File && tree.GetName(n.TreeIndex) == "movie.mkv");
        TreemapNode photo = layout.Nodes.Single(n => n.Kind == TreemapNodeKind.File && tree.GetName(n.TreeIndex) == "photo.jpg");
        Assert.True(movie.Bounds.Area > photo.Bounds.Area * 8);
    }

    [Fact]
    public void Layout_MergesTinyItems_IntoAggregate()
    {
        ScanTree tree = SampleTree();
        TreemapLayout layout = new SquarifiedTreemapLayout().Layout(tree, 0, new TreemapRect(0, 0, 800, 600), new TreemapLayoutOptions());

        Assert.True(layout.Nodes.Count < 100, $"{layout.Nodes.Count} nodes — tiny files should be merged");
        TreemapNode? aggregate = layout.Nodes.FirstOrDefault(n => n.Kind == TreemapNodeKind.Aggregate);
        TreemapNode? smallTile = layout.Nodes.FirstOrDefault(n => n.TreeIndex == tree.FindByPath(@"C:\small"));
        Assert.True(aggregate is not null || smallTile?.Kind == TreemapNodeKind.FolderTile);
    }

    [Fact]
    public void Layout_MinSizeThreshold_HidesSmallItems()
    {
        ScanTree tree = SampleTree();
        TreemapLayout layout = new SquarifiedTreemapLayout().Layout(tree, 0, new TreemapRect(0, 0, 2000, 2000), new TreemapLayoutOptions { MinSizeBytes = 2_000_000 });
        Assert.DoesNotContain(layout.Nodes, n => n.Kind == TreemapNodeKind.File && n.SizeBytes < 2_000_000);
        Assert.Contains(layout.Nodes, n => n.Kind == TreemapNodeKind.Aggregate);
    }

    [Fact]
    public void Layout_ChildrenStayInsideParents_AndHitTestFindsDeepestNode()
    {
        ScanTree tree = SampleTree();
        TreemapLayout layout = new SquarifiedTreemapLayout().Layout(tree, 0, new TreemapRect(0, 0, 1200, 800), new TreemapLayoutOptions());
        foreach (TreemapNode node in layout.Nodes.Where(n => n.Parent is not null))
        {
            TreemapRect p = node.Parent!.Bounds;
            Assert.True(node.Bounds.X >= p.X - 1e-6 && node.Bounds.Right <= p.Right + 1e-6);
            Assert.True(node.Bounds.Y >= p.Y - 1e-6 && node.Bounds.Bottom <= p.Bottom + 1e-6);
        }

        TreemapNode movie = layout.Nodes.Single(n => n.Kind == TreemapNodeKind.File && tree.GetName(n.TreeIndex) == "movie.mkv");
        TreemapRect b = movie.Bounds;
        Assert.Same(movie, layout.HitTest(b.X + (b.Width / 2), b.Y + (b.Height / 2)));
        Assert.Null(layout.HitTest(-5, -5));
    }

    [Fact]
    public void Layout_ZoomedIntoFolder_UsesFolderAsRoot()
    {
        ScanTree tree = SampleTree();
        int big = tree.FindByPath(@"C:\big");
        TreemapLayout layout = new SquarifiedTreemapLayout().Layout(tree, big, new TreemapRect(0, 0, 500, 500), new TreemapLayoutOptions());
        Assert.Equal(big, layout.RootTreeIndex);
        Assert.Equal(2, layout.Root.Children!.Count);
        Assert.Equal(500 * 500, layout.Root.Children.Sum(c => c.Bounds.Area), 3);
    }

    [Fact]
    public void Renderer_FillsEveryLeafPixel()
    {
        ScanTree tree = SampleTree();
        TreemapLayout layout = new SquarifiedTreemapLayout().Layout(tree, 0, new TreemapRect(0, 0, 300, 200), new TreemapLayoutOptions());
        int[] pixels = new int[300 * 200];
        var renderer = new CushionTreemapRenderer { BackgroundColor = 0x000000 };
        renderer.Render(layout, pixels, 300, 200, n => n.Kind == TreemapNodeKind.Folder ? 0x404040u : 0xFF0000u);

        TreemapNode movie = layout.Nodes.Single(n => n.Kind == TreemapNodeKind.File && tree.GetName(n.TreeIndex) == "movie.mkv");
        int cx = (int)(movie.Bounds.X + (movie.Bounds.Width / 2));
        int cy = (int)(movie.Bounds.Y + (movie.Bounds.Height / 2));
        int center = pixels[(cy * 300) + cx];
        Assert.True(((center >> 16) & 0xFF) > 150, "cushion center should be bright red");
        Assert.Equal(0, center & 0xFFFF);
    }

    [Fact]
    public void ColorScheme_IsStablePerExtension()
    {
        Assert.Equal(TreemapColorScheme.GetExtensionColor(".mp4"), TreemapColorScheme.GetExtensionColor(".MP4"));
        Assert.NotEqual(TreemapColorScheme.GetExtensionColor(".mp4"), TreemapColorScheme.GetExtensionColor(".jpg"));
        Assert.Equal(TreemapColorScheme.GetCategoryColor(FileCategory.Other), TreemapColorScheme.GetExtensionColor(string.Empty));
        Assert.True(TreemapColorScheme.AgeHeat(DateTime.UtcNow.Ticks, DateTime.UtcNow) > 0.95);
        Assert.True(TreemapColorScheme.AgeHeat(DateTime.UtcNow.AddYears(-10).Ticks, DateTime.UtcNow) < 0.01);
    }
}
