namespace DiskAnalyzer.Core.Models;

public readonly record struct TreemapRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double Area => Width * Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(double px, double py) => px >= X && py >= Y && px < X + Width && py < Y + Height;

    public TreemapRect Deflate(double left, double top, double right, double bottom) =>
        new(X + left, Y + top, Math.Max(0, Width - left - right), Math.Max(0, Height - top - bottom));
}

public enum TreemapNodeKind : byte
{
    /// <summary>A single file.</summary>
    File,

    /// <summary>A folder whose children are laid out inside it (drawn as a frame with a header).</summary>
    Folder,

    /// <summary>A folder too small to subdivide; drawn as one tile.</summary>
    FolderTile,

    /// <summary>Several items too small to show individually, merged into one tile.</summary>
    Aggregate,
}

/// <summary>
/// One rectangle of a computed treemap. Created only for items large enough to be visible,
/// so the count is bounded by the pixel area, not by the number of files scanned.
/// </summary>
public sealed class TreemapNode
{
    public required int TreeIndex { get; init; }

    public required TreemapNodeKind Kind { get; init; }

    public required TreemapRect Bounds { get; init; }

    /// <summary>Area children were laid out in (folders only).</summary>
    public TreemapRect ContentBounds { get; set; }

    public long SizeBytes { get; init; }

    public int Depth { get; init; }

    /// <summary>Number of merged items (aggregates only).</summary>
    public int AggregateCount { get; init; }

    public TreemapNode? Parent { get; init; }

    public List<TreemapNode>? Children { get; set; }

    /// <summary>Extension id from <see cref="ScanTree"/> (files) used for coloring.</summary>
    public int ExtensionId { get; init; }

    // Cushion surface coefficients: z = S1·x² + S2·x + S3·y² + S4·y (van Wijk & van de Wetering).
    public double S1 { get; set; }

    public double S2 { get; set; }

    public double S3 { get; set; }

    public double S4 { get; set; }

    public bool IsLeaf => Kind != TreemapNodeKind.Folder;

    public bool IsDirectoryLike => Kind is TreemapNodeKind.Folder or TreemapNodeKind.FolderTile;
}

public sealed record TreemapLayoutOptions
{
    /// <summary>Tiles whose shorter side would be below this many pixels are merged into an aggregate tile.</summary>
    public double MinTilePixels { get; init; } = 3;

    /// <summary>Items smaller than this many bytes are merged into an aggregate tile.</summary>
    public long MinSizeBytes { get; init; }

    /// <summary>Folders smaller than this (either side) are drawn as one tile rather than subdivided.</summary>
    public double MinFolderPixels { get; init; } = 12;

    public double FolderHeaderHeight { get; init; } = 15;

    /// <summary>Minimum folder size before a header strip is drawn.</summary>
    public double FolderHeaderMinWidth { get; init; } = 60;

    public double FolderPadding { get; init; } = 1.5;

    public int MaxDepth { get; init; } = 64;

    /// <summary>Cushion ridge height at the top level.</summary>
    public double CushionHeight { get; init; } = 0.5;

    /// <summary>Cushion height falloff per nesting level.</summary>
    public double CushionFalloff { get; init; } = 0.75;
}

/// <summary>Result of a treemap layout: a node tree plus a flat pre-order list for rendering.</summary>
public sealed class TreemapLayout
{
    public required TreemapNode Root { get; init; }

    public required IReadOnlyList<TreemapNode> Nodes { get; init; }

    public required TreemapRect Bounds { get; init; }

    public int RootTreeIndex { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>Deepest node containing the point, or null.</summary>
    public TreemapNode? HitTest(double x, double y)
    {
        if (!Root.Bounds.Contains(x, y))
        {
            return null;
        }

        TreemapNode current = Root;
        while (current.Children is { Count: > 0 } children)
        {
            TreemapNode? next = null;
            foreach (TreemapNode child in children)
            {
                if (child.Bounds.Contains(x, y))
                {
                    next = child;
                    break;
                }
            }

            if (next is null)
            {
                break; // on the folder's frame/header
            }

            current = next;
        }

        return current;
    }

    public TreemapNode? FindByTreeIndex(int treeIndex)
    {
        foreach (TreemapNode node in Nodes)
        {
            if (node.TreeIndex == treeIndex && node.Kind != TreemapNodeKind.Aggregate)
            {
                return node;
            }
        }

        return null;
    }
}
