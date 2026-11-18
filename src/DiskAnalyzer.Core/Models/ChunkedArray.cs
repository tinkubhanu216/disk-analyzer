using System.Runtime.CompilerServices;

namespace DiskAnalyzer.Core.Models;

/// <summary>
/// Growable array split into fixed 64K-element chunks. Growing never copies existing
/// elements, so a multi-million element column never triggers large-object-heap reallocation.
/// Not thread-safe for writes: <see cref="ScanTree"/> has a single writer.
/// </summary>
public sealed class ChunkedArray<T>
{
    internal const int Shift = 16;
    internal const int ChunkSize = 1 << Shift;
    private const int Mask = ChunkSize - 1;

    private T[][] _chunks = new T[4][];
    private int _chunkCount;

    public int Capacity => _chunkCount * ChunkSize;

    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _chunks[index >> Shift][index & Mask];
    }

    public void EnsureCapacity(int count)
    {
        int needed = (count + Mask) >> Shift;
        while (_chunkCount < needed)
        {
            if (_chunkCount == _chunks.Length)
            {
                Array.Resize(ref _chunks, _chunks.Length * 2);
            }

            _chunks[_chunkCount++] = new T[ChunkSize];
        }
    }

    /// <summary>Estimated bytes held by allocated chunks (excluding referenced objects).</summary>
    public long EstimatedBytes =>
        (long)_chunkCount * ChunkSize * (RuntimeHelpers.IsReferenceOrContainsReferences<T>() ? IntPtr.Size : Unsafe.SizeOf<T>());
}
