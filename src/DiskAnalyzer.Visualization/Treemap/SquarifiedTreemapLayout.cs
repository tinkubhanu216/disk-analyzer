using System.Diagnostics;
using DiskAnalyzer.Core.Interfaces;
using DiskAnalyzer.Core.Models;

namespace DiskAnalyzer.Visualization.Treemap;

/// <summary>
/// Builds a nested squarified treemap for a subtree of a <see cref="ScanTree"/>.
/// Folders are framed (with a header strip when large enough) and subdivided recursively;
/// items too small to see are merged into one "smaller items" tile per folder, so the number of
/// layout nodes is bounded by pixel area rather than file count. Also computes cushion-shading
/// coefficients for each tile.
/// </summary>
public sealed class SquarifiedTreemapLayout : ITreemapLayoutEngine
{
    public TreemapLayout Layout(ScanTree tree, int rootIndex, TreemapRect bounds, TreemapLayoutOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(options);
        var stopwatch = Stopwatch.StartNew();
        var nodes = new List<TreemapNode>(4096);
        var builder = new Builder(tree, options, nodes, cancellationToken);

        TreemapNode root;
        if (tree.IsDirectory(rootIndex))
        {
            root = new TreemapNode
            {
                TreeIndex = rootIndex,
                Kind = TreemapNodeKind.Folder,
                Bounds = bounds,
                ContentBounds = bounds,
                SizeBytes = tree.GetSize(rootIndex),
                Depth = 0,
            };
            nodes.Add(root);
            builder.LayoutChildren(root, bounds, 0, 0, 0, 0, 0);
        }
        else
        {
            root = new TreemapNode
            {
                TreeIndex = rootIndex,
                Kind = TreemapNodeKind.File,
                Bounds = bounds,
                SizeBytes = tree.GetSize(rootIndex),
                ExtensionId = tree.GetExtensionId(rootIndex),
            };
            Builder.ApplyCushion(root, 0, 0, 0, 0, options.CushionHeight);
            nodes.Add(root);
        }

        return new TreemapLayout { Root = root, Nodes = nodes, Bounds = bounds, RootTreeIndex = rootIndex, Elapsed = stopwatch.Elapsed };
    }

    private sealed class Builder(ScanTree tree, TreemapLayoutOptions options, List<TreemapNode> nodes, CancellationToken ct)
    {
        private readonly double _minArea = options.MinTilePixels * options.MinTilePixels;

        public void LayoutChildren(TreemapNode folder, TreemapRect content, int depth, double s1, double s2, double s3, double s4)
        {
            if (nodes.Count % 2048 == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            long folderBytes = 0;
            var visible = new List<(long Size, int Index)>();
            foreach (int child in tree.GetChildren(folder.TreeIndex))
            {
                long size = tree.GetCountedSize(child);
                if (size > 0)
                {
                    visible.Add((size, child));
                    folderBytes += size;
                }
            }

            if (folderBytes <= 0 || content.IsEmpty)
            {
                folder.Children = [];
                return;
            }

            visible.Sort((a, b) => b.Size.CompareTo(a.Size));

            // Items whose tile would be smaller than the minimum (or below the byte threshold) are merged.
            double areaPerByte = content.Area / folderBytes;
            int keep = 0;
            while (keep < visible.Count && visible[keep].Size * areaPerByte >= _minArea && visible[keep].Size >= options.MinSizeBytes)
            {
                keep++;
            }

            long mergedBytes = 0;
            for (int i = keep; i < visible.Count; i++)
            {
                mergedBytes += visible[i].Size;
            }

            int mergedCount = visible.Count - keep;
            int itemCount = keep + (mergedCount > 0 ? 1 : 0);
            var entries = new (long Size, int Index)[itemCount];
            for (int i = 0; i < keep; i++)
            {
                entries[i] = visible[i];
            }

            if (mergedCount > 0)
            {
                entries[keep] = (mergedBytes, ScanTree.None);
                Array.Sort(entries, (a, b) => b.Size.CompareTo(a.Size));
            }

            double[] values = new double[itemCount];
            for (int i = 0; i < itemCount; i++)
            {
                values[i] = entries[i].Size;
            }

            var rects = new TreemapRect[itemCount];
            Squarifier.Squarify(values, content, rects);

            folder.Children = new List<TreemapNode>(itemCount);
            double height = options.CushionHeight * Math.Pow(options.CushionFalloff, depth + 1);
            for (int i = 0; i < itemCount; i++)
            {
                TreemapRect rect = rects[i];
                (long size, int index) = entries[i];
                if (index == ScanTree.None)
                {
                    var aggregate = new TreemapNode
                    {
                        TreeIndex = folder.TreeIndex,
                        Kind = TreemapNodeKind.Aggregate,
                        Bounds = rect,
                        SizeBytes = size,
                        Depth = depth + 1,
                        AggregateCount = mergedCount,
                        Parent = folder,
                    };
                    ApplyCushion(aggregate, s1, s2, s3, s4, height);
                    folder.Children.Add(aggregate);
                    nodes.Add(aggregate);
                    continue;
                }

                if (tree.IsDirectory(index))
                {
                    LayoutFolder(folder, index, rect, size, depth + 1, s1, s2, s3, s4, height);
                }
                else
                {
                    var file = new TreemapNode
                    {
                        TreeIndex = index,
                        Kind = TreemapNodeKind.File,
                        Bounds = rect,
                        SizeBytes = size,
                        Depth = depth + 1,
                        Parent = folder,
                        ExtensionId = tree.GetExtensionId(index),
                    };
                    ApplyCushion(file, s1, s2, s3, s4, height);
                    folder.Children.Add(file);
                    nodes.Add(file);
                }
            }
        }

        private void LayoutFolder(TreemapNode parent, int index, TreemapRect rect, long size, int depth, double s1, double s2, double s3, double s4, double height)
        {
            double pad = options.FolderPadding;
            bool hasHeader = rect.Width >= options.FolderHeaderMinWidth && rect.Height >= options.FolderHeaderHeight * 2.5;
            TreemapRect content = hasHeader
                ? rect.Deflate(pad, options.FolderHeaderHeight, pad, pad)
                : rect.Deflate(pad, pad, pad, pad);

            bool subdivide = depth < options.MaxDepth
                && content.Width >= options.MinFolderPixels
                && content.Height >= options.MinFolderPixels
                && tree.GetFirstChild(index) != ScanTree.None;

            var node = new TreemapNode
            {
                TreeIndex = index,
                Kind = subdivide ? TreemapNodeKind.Folder : TreemapNodeKind.FolderTile,
                Bounds = rect,
                ContentBounds = subdivide ? content : rect,
                SizeBytes = size,
                Depth = depth,
                Parent = parent,
            };

            parent.Children!.Add(node);
            nodes.Add(node);

            // The folder's ridge is added to the surface its children inherit.
            ApplyCushion(node, s1, s2, s3, s4, height);
            if (subdivide)
            {
                LayoutChildren(node, content, depth, node.S1, node.S2, node.S3, node.S4);
            }
        }

        /// <summary>Adds a parabolic ridge over the node's rectangle to the inherited cushion surface.</summary>
        public static void ApplyCushion(TreemapNode node, double s1, double s2, double s3, double s4, double height)
        {
            TreemapRect r = node.Bounds;
            if (r.Width > 0)
            {
                s2 += 4 * height * (r.Right + r.X) / r.Width;
                s1 -= 4 * height / r.Width;
            }

            if (r.Height > 0)
            {
                s4 += 4 * height * (r.Bottom + r.Y) / r.Height;
                s3 -= 4 * height / r.Height;
            }

            node.S1 = s1;
            node.S2 = s2;
            node.S3 = s3;
            node.S4 = s4;
        }
    }
}
