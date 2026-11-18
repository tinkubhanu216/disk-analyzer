namespace DiskAnalyzer.Core.Models;

[Flags]
public enum NodeFlags : byte
{
    None = 0,
    Directory = 1,

    /// <summary>The entry is a reparse point (junction, symlink, mount point, cloud placeholder…). Directory reparse points are not descended by default.</summary>
    ReparsePoint = 2,

    /// <summary>The folder could not be enumerated (access denied, I/O error, vanished during scan).</summary>
    Inaccessible = 4,

    /// <summary>Additional name of a hard-linked file whose bytes were already counted under another name.</summary>
    HardLinkDuplicate = 8,

    /// <summary>The folder's contents are incomplete (scan cancelled, or small files not stored in history).</summary>
    Partial = 16,

    /// <summary>Synthetic node not backed by a single filesystem entry.</summary>
    Synthetic = 32,
}
