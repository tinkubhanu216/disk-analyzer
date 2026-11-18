using System.Collections.Frozen;

namespace DiskAnalyzer.Core.Models;

public enum FileCategory
{
    Documents,
    Images,
    Videos,
    Audio,
    Applications,
    Archives,
    Development,
    System,
    Other,
}

/// <summary>Maps file extensions to broad categories.</summary>
public static class FileCategoryClassifier
{
    private static readonly FrozenDictionary<string, FileCategory> Map = Build();

    public static IReadOnlyList<FileCategory> All { get; } = Enum.GetValues<FileCategory>();

    /// <summary>Classifies an extension such as <c>.mp4</c> (case-insensitive, leading dot optional).</summary>
    public static FileCategory Classify(string? extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return FileCategory.Other;
        }

        if (extension[0] != '.')
        {
            extension = "." + extension;
        }

        return Map.TryGetValue(extension, out FileCategory category) ? category : FileCategory.Other;
    }

    private static FrozenDictionary<string, FileCategory> Build()
    {
        var d = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory c, string list)
        {
            foreach (string e in list.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                d["." + e] = c;
            }
        }

        Add(FileCategory.Documents, "pdf doc docx docm dot dotx xls xlsx xlsm xlsb ppt pptx pptm odt ods odp rtf txt md markdown csv tsv epub mobi azw3 one onepkg pages numbers key tex log msg eml pst ost vsd vsdx xps oxps chm");
        Add(FileCategory.Images, "jpg jpeg png gif bmp tif tiff webp heic heif avif raw cr2 cr3 nef arw dng orf rw2 psd psb ai svg ico eps xcf jxl tga dds exr hdr");
        Add(FileCategory.Videos, "mp4 m4v mkv avi mov wmv flv webm mpg mpeg m2ts mts ts vob 3gp ogv rm rmvb divx f4v braw r3d prproj");
        Add(FileCategory.Audio, "mp3 wav flac aac m4a ogg oga opus wma aiff aif alac ape mid midi amr dsf dff");
        Add(FileCategory.Applications, "exe msi msix msixbundle appx appxbundle com scr bat cmd ps1 vbs jar apk xapk ipa app dmg deb rpm");
        Add(FileCategory.Archives, "zip rar 7z tar gz tgz bz2 tbz2 xz txz zst lz lzma cab iso img vhd vhdx vmdk vdi qcow2 wim esd swm arc");
        Add(FileCategory.Development, "cs csproj sln slnx vb fs c h cpp hpp cc cxx java kt kts py pyc pyd ipynb js mjs cjs ts tsx jsx json yaml yml xml xaml toml ini config props targets go rs rb php swift m mm sql sh psm1 psd1 gradle cmake make mk html htm css scss sass less vue svelte lock pdb obj o a lib ilk class wasm map nupkg snupkg whl gem");
        Add(FileCategory.System, "dll sys drv cpl ocx mui cat inf pnf manifest efi msp mum etl evtx dmp mdmp hiberfil pagefile swapfile bin dat db db-wal db-shm sqlite edb jrs chk regtrans-ms blf tmp temp cache lnk");
        return d.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}
