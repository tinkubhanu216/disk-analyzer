using System.Runtime.CompilerServices;

namespace DiskAnalyzer.Core.Models;

/// <summary>
/// Compact, struct-of-arrays representation of a scanned filesystem tree.
/// Nodes are addressed by <c>int</c> index; index 0 is the scan root.
/// Invariant: a node's parent always has a lower index than the node itself, which lets
/// folder totals be computed with one reverse linear pass (see <c>FolderAggregationService</c>).
/// Full paths are not stored; they are rebuilt from parent links on demand.
/// Single writer while scanning; read-only (and safe to read from many threads) afterwards.
/// </summary>
public sealed class ScanTree
{
    public const int None = -1;
    public const int RootIndex = 0;

    private readonly ChunkedArray<string> _name = new();
    private readonly ChunkedArray<int> _parent = new();
    private readonly ChunkedArray<int> _firstChild = new();
    private readonly ChunkedArray<int> _nextSibling = new();
    private readonly ChunkedArray<long> _size = new();
    private readonly ChunkedArray<long> _allocated = new();
    private readonly ChunkedArray<long> _lastWrite = new();
    private readonly ChunkedArray<int> _attributes = new();
    private readonly ChunkedArray<NodeFlags> _flags = new();
    private readonly ChunkedArray<int> _extension = new();
    private readonly ChunkedArray<int> _fileCount = new();
    private readonly ChunkedArray<int> _dirCount = new();

    private readonly List<string> _extensions = [string.Empty];
    private readonly Dictionary<string, int> _extensionIds = new(StringComparer.OrdinalIgnoreCase) { [string.Empty] = 0 };
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _extensionLookup;

    private int _count;

    public ScanTree(string rootPath, long rootLastWriteUtcTicks = 0, FileAttributes rootAttributes = FileAttributes.Directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPath);
        _extensionLookup = _extensionIds.GetAlternateLookup<ReadOnlySpan<char>>();
        RootPath = rootPath;
        AddNodeCore(None, rootPath, NodeFlags.Directory, 0, 0, rootLastWriteUtcTicks, rootAttributes, 0);
    }

    /// <summary>Full path of the scan root, e.g. <c>C:\</c> or <c>D:\Projects</c>.</summary>
    public string RootPath { get; }

    public int Count => _count;

    /// <summary>True once folder sizes/counts have been computed.</summary>
    public bool IsAggregated { get; set; }

    public IReadOnlyList<string> Extensions => _extensions;

    // ---------------------------------------------------------------- writing

    public int AddDirectory(int parent, string name, long lastWriteUtcTicks, FileAttributes attributes, NodeFlags extraFlags = NodeFlags.None)
        => AddNodeCore(parent, name, NodeFlags.Directory | extraFlags, 0, 0, lastWriteUtcTicks, attributes, 0);

    public int AddFile(int parent, string name, long size, long allocated, long lastWriteUtcTicks, FileAttributes attributes, NodeFlags extraFlags = NodeFlags.None)
        => AddNodeCore(parent, name, extraFlags & ~NodeFlags.Directory, size, allocated, lastWriteUtcTicks, attributes, InternExtension(name));

    private int AddNodeCore(int parent, string name, NodeFlags flags, long size, long allocated, long lastWrite, FileAttributes attributes, int extensionId)
    {
        int index = _count;
        if (parent != None && (uint)parent >= (uint)index)
        {
            throw new ArgumentOutOfRangeException(nameof(parent), "Parent must already exist.");
        }

        if (parent != None && (_flags[parent] & NodeFlags.Directory) == 0)
        {
            throw new ArgumentException("Parent must be a directory.", nameof(parent));
        }

        EnsureCapacity(index + 1);
        _name[index] = name;
        _parent[index] = parent;
        _firstChild[index] = None;
        _size[index] = size;
        _allocated[index] = allocated;
        _lastWrite[index] = lastWrite;
        _attributes[index] = (int)attributes;
        _flags[index] = flags;
        _extension[index] = extensionId;
        _fileCount[index] = 0;
        _dirCount[index] = 0;

        if (parent != None)
        {
            _nextSibling[index] = _firstChild[parent];
            _firstChild[parent] = index;
        }
        else
        {
            _nextSibling[index] = None;
        }

        _count = index + 1;
        return index;
    }

    private void EnsureCapacity(int count)
    {
        if (count <= _name.Capacity)
        {
            return;
        }

        _name.EnsureCapacity(count);
        _parent.EnsureCapacity(count);
        _firstChild.EnsureCapacity(count);
        _nextSibling.EnsureCapacity(count);
        _size.EnsureCapacity(count);
        _allocated.EnsureCapacity(count);
        _lastWrite.EnsureCapacity(count);
        _attributes.EnsureCapacity(count);
        _flags.EnsureCapacity(count);
        _extension.EnsureCapacity(count);
        _fileCount.EnsureCapacity(count);
        _dirCount.EnsureCapacity(count);
    }

    private int InternExtension(string fileName)
    {
        ReadOnlySpan<char> ext = Path.GetExtension(fileName.AsSpan());
        if (ext.Length <= 1 || ext.Length > 16)
        {
            return 0;
        }

        if (_extensionLookup.TryGetValue(ext, out int id))
        {
            return id;
        }

        id = _extensions.Count;
        string value = ext.ToString().ToLowerInvariant();
        _extensions.Add(value);
        _extensionIds[value] = id;
        return id;
    }

    public void AddFlags(int index, NodeFlags flags) => _flags[index] |= flags;

    public void SetSize(int index, long size, long allocated)
    {
        _size[index] = size;
        _allocated[index] = allocated;
    }

    public void SetCounts(int index, int fileCount, int dirCount)
    {
        _fileCount[index] = fileCount;
        _dirCount[index] = dirCount;
    }

    // ---------------------------------------------------------------- reading

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string GetName(int index) => _name[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetParent(int index) => _parent[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetFirstChild(int index) => _firstChild[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetNextSibling(int index) => _nextSibling[index];

    /// <summary>Logical size in bytes (aggregated for folders after aggregation).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetSize(int index) => _size[index];

    /// <summary>Size on disk in bytes (aggregated for folders after aggregation).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetAllocated(int index) => _allocated[index];

    public long GetLastWriteUtcTicks(int index) => _lastWrite[index];

    public DateTime? GetLastWriteUtc(int index)
    {
        long ticks = _lastWrite[index];
        return ticks > 0 && ticks < DateTime.MaxValue.Ticks ? new DateTime(ticks, DateTimeKind.Utc) : null;
    }

    public FileAttributes GetAttributes(int index) => (FileAttributes)_attributes[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NodeFlags GetFlags(int index) => _flags[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsDirectory(int index) => (_flags[index] & NodeFlags.Directory) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetExtensionId(int index) => _extension[index];

    public string GetExtension(int index) => _extensions[_extension[index]];

    public int GetFileCount(int index) => _fileCount[index];

    public int GetDirectoryCount(int index) => _dirCount[index];

    /// <summary>Bytes that count towards totals: zero for hard-link duplicates.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetCountedSize(int index) => (_flags[index] & NodeFlags.HardLinkDuplicate) != 0 ? 0 : _size[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetCountedAllocated(int index) => (_flags[index] & NodeFlags.HardLinkDuplicate) != 0 ? 0 : _allocated[index];

    public ChildEnumerable GetChildren(int index) => new(this, index);

    public int GetChildCount(int index)
    {
        int n = 0;
        for (int c = _firstChild[index]; c != None; c = _nextSibling[c])
        {
            n++;
        }

        return n;
    }

    public int GetDepth(int index)
    {
        int depth = 0;
        for (int p = _parent[index]; p != None; p = _parent[p])
        {
            depth++;
        }

        return depth;
    }

    /// <summary>True when <paramref name="ancestor"/> is <paramref name="index"/> or one of its ancestors.</summary>
    public bool IsSelfOrAncestor(int ancestor, int index)
    {
        for (int p = index; p != None && p >= ancestor; p = _parent[p])
        {
            if (p == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the chain of nodes from the root down to <paramref name="index"/> (inclusive).</summary>
    public List<int> GetAncestry(int index)
    {
        var chain = new List<int>();
        for (int p = index; p != None; p = _parent[p])
        {
            chain.Add(p);
        }

        chain.Reverse();
        return chain;
    }

    /// <summary>Rebuilds the full path of a node by walking its parent links.</summary>
    public string GetFullPath(int index)
    {
        if (index == RootIndex)
        {
            return RootPath;
        }

        List<int> chain = GetAncestry(index);
        string root = RootPath;
        bool rootHasSeparator = root.EndsWith('\\') || root.EndsWith('/');
        int length = root.Length;
        for (int i = 1; i < chain.Count; i++)
        {
            length += _name[chain[i]].Length + 1;
        }

        if (rootHasSeparator)
        {
            length--;
        }

        return string.Create(length, (this, chain, rootHasSeparator), static (span, state) =>
        {
            var (tree, nodes, rootSep) = state;
            tree.RootPath.AsSpan().CopyTo(span);
            int pos = tree.RootPath.Length;
            for (int i = 1; i < nodes.Count; i++)
            {
                if (!(rootSep && i == 1))
                {
                    span[pos++] = '\\';
                }

                string name = tree._name[nodes[i]];
                name.AsSpan().CopyTo(span[pos..]);
                pos += name.Length;
            }
        });
    }

    /// <summary>Full path of the node's parent folder (empty for the root).</summary>
    public string GetParentPath(int index)
    {
        int parent = _parent[index];
        return parent == None ? string.Empty : GetFullPath(parent);
    }

    /// <summary>Finds a node by full path (case-insensitive), or <see cref="None"/>.</summary>
    public int FindByPath(string fullPath)
    {
        string root = RootPath.TrimEnd('\\', '/');
        string target = fullPath.TrimEnd('\\', '/');
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return None;
        }

        if (target.Length == root.Length)
        {
            return RootIndex;
        }

        if (target[root.Length] is not ('\\' or '/'))
        {
            return None;
        }

        int node = RootIndex;
        foreach (string part in target[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            int found = None;
            for (int c = _firstChild[node]; c != None; c = _nextSibling[c])
            {
                if (string.Equals(_name[c], part, StringComparison.OrdinalIgnoreCase))
                {
                    found = c;
                    break;
                }
            }

            if (found == None)
            {
                return None;
            }

            node = found;
        }

        return node;
    }

    /// <summary>Approximate managed memory used by the tree (columns + name strings).</summary>
    public long EstimateMemoryBytes()
    {
        long columns = _name.EstimatedBytes + _parent.EstimatedBytes + _firstChild.EstimatedBytes + _nextSibling.EstimatedBytes
            + _size.EstimatedBytes + _allocated.EstimatedBytes + _lastWrite.EstimatedBytes + _attributes.EstimatedBytes
            + _flags.EstimatedBytes + _extension.EstimatedBytes + _fileCount.EstimatedBytes + _dirCount.EstimatedBytes;
        long strings = 0;
        for (int i = 0; i < _count; i++)
        {
            strings += 22 + (2L * _name[i].Length);
        }

        return columns + strings;
    }

    public readonly struct ChildEnumerable(ScanTree tree, int parent)
    {
        public ChildEnumerator GetEnumerator() => new(tree, parent);
    }

    public struct ChildEnumerator(ScanTree tree, int parent)
    {
        private int _current = None;
        private bool _started;

        public readonly int Current => _current;

        public bool MoveNext()
        {
            _current = _started ? tree._nextSibling[_current] : tree._firstChild[parent];
            _started = true;
            return _current != None;
        }
    }
}
