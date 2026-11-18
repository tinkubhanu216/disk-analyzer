using System.Windows.Media;
using DiskAnalyzer.App.Converters;
using DiskAnalyzer.Core.Models;
using DiskAnalyzer.Core.Services;

namespace DiskAnalyzer.App.ViewModels;

/// <summary>
/// Row wrapper over a <see cref="ScanTree"/> node. Created only for rows that are listed; text such as the
/// full path is computed lazily so virtualized lists stay cheap.
/// </summary>
public sealed class NodeItemViewModel
{
    private static readonly SolidColorBrush FolderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3D)));
    private string? _fullPath;
    private string? _parentPath;

    public NodeItemViewModel(ScanTree tree, int index, long driveCapacityBytes, long scopeBytes = 0)
    {
        Tree = tree;
        Index = index;
        Name = index == ScanTree.RootIndex ? tree.RootPath : tree.GetName(index);
        IsDirectory = tree.IsDirectory(index);
        SizeBytes = tree.GetSize(index);
        AllocatedBytes = tree.GetAllocated(index);
        Flags = tree.GetFlags(index);
        int parent = tree.GetParent(index);
        long parentSize = parent >= 0 ? tree.GetSize(parent) : SizeBytes;
        PercentOfParent = SizeFormatter.Percent(tree.GetCountedSize(index), parentSize);
        PercentOfDrive = SizeFormatter.Percent(tree.GetCountedSize(index), driveCapacityBytes);
        PercentOfScope = SizeFormatter.Percent(tree.GetCountedSize(index), scopeBytes > 0 ? scopeBytes : tree.GetSize(ScanTree.RootIndex));
        Extension = IsDirectory ? string.Empty : tree.GetExtension(index);
        Category = IsDirectory ? FileCategory.Other : FileCategoryClassifier.Classify(Extension);
    }

    public ScanTree Tree { get; }

    public int Index { get; }

    public string Name { get; }

    public bool IsDirectory { get; }

    public long SizeBytes { get; }

    public long AllocatedBytes { get; }

    public NodeFlags Flags { get; }

    public double PercentOfParent { get; }

    public double PercentOfDrive { get; }

    public double PercentOfScope { get; }

    public double ParentRatio => PercentOfParent / 100;

    public double ScopeRatio => PercentOfScope / 100;

    public string Extension { get; }

    public FileCategory Category { get; }

    public int FileCount => IsDirectory ? Tree.GetFileCount(Index) : 0;

    public int FolderCount => IsDirectory ? Tree.GetDirectoryCount(Index) : 0;

    public string FileCountText => IsDirectory ? SizeFormatter.FormatCount(FileCount) : string.Empty;

    public string FolderCountText => IsDirectory ? SizeFormatter.FormatCount(FolderCount) : string.Empty;

    public DateTime? LastModifiedUtc => Tree.GetLastWriteUtc(Index);

    public long LastModifiedTicks => Tree.GetLastWriteUtcTicks(Index);

    public string ModifiedText => LastModifiedUtc?.ToLocalTime().ToString("g") ?? string.Empty;

    public string SizeText => SizeFormatter.Format(SizeBytes);

    public string AllocatedText => SizeFormatter.Format(AllocatedBytes);

    public string PercentOfParentText => SizeFormatter.FormatPercent(PercentOfParent);

    public string PercentOfDriveText => SizeFormatter.FormatPercent(PercentOfDrive);

    public string FullPath => _fullPath ??= Tree.GetFullPath(Index);

    public string ParentPath => _parentPath ??= Tree.GetParentPath(Index);

    public string TypeText => IsDirectory ? "Folder" : string.IsNullOrEmpty(Extension) ? "File" : Extension.TrimStart('.').ToUpperInvariant() + " file";

    public string CategoryText => IsDirectory ? "Folder" : Category.ToString();

    public string Glyph =>
        (Flags & NodeFlags.Inaccessible) != 0 ? ""
        : (Flags & NodeFlags.ReparsePoint) != 0 && IsDirectory ? ""
        : IsDirectory ? ""
        : Category switch
        {
            FileCategory.Documents => "",
            FileCategory.Images => "",
            FileCategory.Videos => "",
            FileCategory.Audio => "",
            FileCategory.Applications => "",
            FileCategory.Archives => "",
            FileCategory.Development => "",
            FileCategory.System => "",
            _ => "",
        };

    public Brush IconBrush => IsDirectory ? FolderBrush : CategoryBrushConverter.GetBrush(Category);

    public Brush BarBrush => IsDirectory ? FolderBrush : CategoryBrushConverter.GetBrush(Category);

    /// <summary>Short note for special entries.</summary>
    public string Note =>
        (Flags & NodeFlags.Inaccessible) != 0 ? "Access denied"
        : (Flags & NodeFlags.HardLinkDuplicate) != 0 ? "Hard link (counted once)"
        : (Flags & NodeFlags.ReparsePoint) != 0 ? (IsDirectory ? "Junction/link (not followed)" : "Link or cloud file")
        : (Flags & NodeFlags.Synthetic) != 0 ? "Metadata"
        : string.Empty;

    public bool HasChildren => IsDirectory && Tree.GetFirstChild(Index) != ScanTree.None;

    private static T Freeze<T>(T f)
        where T : System.Windows.Freezable
    {
        f.Freeze();
        return f;
    }
}
