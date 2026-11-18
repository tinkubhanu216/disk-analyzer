using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DiskAnalyzer.Scanner.Tests;

/// <summary>A temporary directory tree on disk, deleted on dispose.</summary>
internal sealed partial class TempTree : IDisposable
{
    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "da-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string relative, long size)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        stream.SetLength(size);
        return path;
    }

    public string Dir(string relative)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Creates a directory junction (no elevation required).</summary>
    public void Junction(string relativeLink, string targetFullPath)
    {
        string link = Path.Combine(Root, relativeLink);
        using Process process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{targetFullPath}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("mklink failed: " + process.StandardError.ReadToEnd());
        }
    }

    public void HardLink(string relativeLink, string existingFullPath)
    {
        if (!CreateHardLink(Path.Combine(Root, relativeLink), existingFullPath, IntPtr.Zero))
        {
            throw new InvalidOperationException("CreateHardLink failed: " + Marshal.GetLastPInvokeError());
        }
    }

    public static int RunIcacls(string arguments)
    {
        using Process process = Process.Start(new ProcessStartInfo("icacls.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        return process.ExitCode;
    }

    public void Dispose()
    {
        try
        {
            // Junctions must be removed without following them.
            foreach (string dir in Directory.EnumerateDirectories(Root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            {
                if ((System.IO.File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(dir);
                }
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
