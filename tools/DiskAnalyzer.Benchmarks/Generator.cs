using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskAnalyzer.Benchmarks;

/// <summary>Creates synthetic directory trees for benchmarking. Only ever writes inside the given directory.</summary>
internal static partial class Generator
{
    private static readonly string[] Extensions = [".txt", ".jpg", ".mp4", ".dll", ".cs", ".json", ".zip", ".pdf", ".png", ".log", ""];

    /// <summary>~20 files per folder, ~10 subfolders per folder (realistic project-like shape).</summary>
    public static void Balanced(string root, int fileCount)
    {
        if (Exists(root, fileCount))
        {
            return;
        }

        Directory.CreateDirectory(root);
        var folders = new List<string> { root };
        int created = 0;
        int folderIndex = 0;
        while (created < fileCount)
        {
            string folder = folders[folderIndex % folders.Count];
            if (folders.Count < (fileCount / 20) + 1)
            {
                for (int s = 0; s < 10 && folders.Count < (fileCount / 20) + 1; s++)
                {
                    string sub = Path.Combine(folder, $"dir{folders.Count}");
                    Directory.CreateDirectory(sub);
                    folders.Add(sub);
                }
            }

            int n = Math.Min(20, fileCount - created);
            Parallel.For(0, n, i => WriteFile(Path.Combine(folder, $"f{created + i}{Extensions[(created + i) % Extensions.Length]}"), Random.Shared.Next(0, 4096)));
            created += n;
            folderIndex++;
        }

        MarkComplete(root, fileCount);
    }

    public static void Deep(string root, int depth, int filesPerLevel)
    {
        if (Exists(root, depth))
        {
            return;
        }

        string current = root;
        for (int d = 0; d < depth; d++)
        {
            current = Path.Combine(current, $"d{d}");
            Directory.CreateDirectory(current);
            for (int f = 0; f < filesPerLevel; f++)
            {
                WriteFile(Path.Combine(current, $"f{f}.dat"), 512);
            }
        }

        MarkComplete(root, depth);
    }

    public static void Wide(string root, int fileCount)
    {
        if (Exists(root, fileCount))
        {
            return;
        }

        string folder = Path.Combine(root, "huge");
        Directory.CreateDirectory(folder);
        Parallel.For(0, fileCount, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => WriteFile(Path.Combine(folder, $"item{i:D7}.tmp"), i % 1024));
        MarkComplete(root, fileCount);
    }

    /// <summary>Large files created as sparse so they take almost no disk space.</summary>
    public static void LargeSparse(string root, int count, long sizeBytes)
    {
        if (Exists(root, count))
        {
            return;
        }

        Directory.CreateDirectory(root);
        for (int i = 0; i < count; i++)
        {
            using var stream = new FileStream(Path.Combine(root, $"large{i}.vhdx"), FileMode.Create, FileAccess.Write);
            MakeSparse(stream.SafeFileHandle);
            stream.SetLength(sizeBytes);
        }

        MarkComplete(root, count);
    }

    public static void DeleteTree(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteFile(string path, int size)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1);
        if (size > 0)
        {
            stream.SetLength(size);
        }
    }

    private static bool Exists(string root, int marker) => File.Exists(Path.Combine(root, $".complete-{marker}"));

    private static void MarkComplete(string root, int marker) => File.WriteAllText(Path.Combine(root, $".complete-{marker}"), string.Empty);

    private static unsafe void MakeSparse(SafeFileHandle handle)
    {
        const uint FsctlSetSparse = 0x000900C4;
        DeviceIoControl(handle, FsctlSetSparse, null, 0, null, 0, out _, IntPtr.Zero);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint code, void* inBuffer, int inSize, void* outBuffer, int outSize, out int returned, IntPtr overlapped);
}
