using System.Globalization;
using System.IO;
using System.Windows.Media;

namespace ScoutR;

/// <summary>Brushes used for item glyphs. Frozen so they can be shared.</summary>
public static class Palette
{
    private static SolidColorBrush Make(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    public static readonly Brush Folder = Make(0xF5, 0xC5, 0x42);
    public static readonly Brush Drive = Make(0x69, 0x79, 0x7E);
    public static readonly Brush Root = Make(0x00, 0x78, 0xD7);
    public static readonly Brush FileDefault = Make(0x69, 0x79, 0x7E);

    private static readonly Dictionary<string, Brush> ByExt = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = Make(0xD1, 0x34, 0x38),
        [".doc"] = Make(0x2B, 0x57, 0x9A), [".docx"] = Make(0x2B, 0x57, 0x9A),
        [".xls"] = Make(0x21, 0x73, 0x46), [".xlsx"] = Make(0x21, 0x73, 0x46), [".csv"] = Make(0x21, 0x73, 0x46),
        [".ppt"] = Make(0xC4, 0x3E, 0x1C), [".pptx"] = Make(0xC4, 0x3E, 0x1C),
        [".jpg"] = Make(0x87, 0x64, 0xB8), [".jpeg"] = Make(0x87, 0x64, 0xB8), [".png"] = Make(0x87, 0x64, 0xB8),
        [".gif"] = Make(0x87, 0x64, 0xB8), [".svg"] = Make(0x87, 0x64, 0xB8),
        [".zip"] = Make(0xB8, 0x86, 0x0B), [".7z"] = Make(0xB8, 0x86, 0x0B), [".rar"] = Make(0xB8, 0x86, 0x0B),
        [".mp3"] = Make(0xE3, 0x00, 0x8C), [".mp4"] = Make(0xE3, 0x00, 0x8C), [".mkv"] = Make(0xE3, 0x00, 0x8C),
        [".cs"] = Make(0x00, 0x78, 0xD7), [".xaml"] = Make(0x00, 0x78, 0xD7), [".csproj"] = Make(0x00, 0x78, 0xD7),
    };

    public static Brush ForExtension(string ext) =>
        ByExt.TryGetValue(ext, out var b) ? b : FileDefault;
}

/// <summary>One row in a folder pane.</summary>
public sealed class FsItem
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public bool IsDirectory { get; init; }
    public bool IsDrive { get; init; }
    public long? Size { get; init; }
    public DateTime? Modified { get; init; }

    public string SizeText => Size is long s ? FormatSize(s) : "";
    public string ModifiedText => Modified?.ToString("g", CultureInfo.CurrentCulture) ?? "";

    public bool IsFolderShape => IsDirectory;
    public bool IsFileShape => !IsDirectory;

    public string Glyph => IsDrive ? "" : IsDirectory ? "" : "";

    public Brush GlyphBrush =>
        IsDrive ? Palette.Drive :
        IsDirectory ? Palette.Folder :
        Palette.ForExtension(Path.GetExtension(Name));

    public static FsItem FromInfo(FileSystemInfo fi)
    {
        bool isDir = fi is DirectoryInfo;
        long? size = null;
        if (fi is FileInfo f)
        {
            try { size = f.Length; } catch { /* locked or vanished */ }
        }

        DateTime? modified = null;
        try { modified = fi.LastWriteTime; } catch { }

        return new FsItem
        {
            Name = fi.Name,
            FullPath = fi.FullName,
            IsDirectory = isDir,
            Size = size,
            Modified = modified,
        };
    }

    public static FsItem FromDrive(DriveInfo d)
    {
        string label = d.Name.TrimEnd('\\');
        long? total = null;
        try
        {
            if (d.IsReady)
            {
                total = d.TotalSize;
                if (!string.IsNullOrWhiteSpace(d.VolumeLabel)) label = $"{d.VolumeLabel} ({label})";
            }
        }
        catch { }

        return new FsItem
        {
            Name = label,
            FullPath = d.Name,
            IsDirectory = true,
            IsDrive = true,
            Size = total,
        };
    }

    public static string FormatSize(long s)
    {
        if (s < 1024) return s + " B";
        if (s < 1024L * 1024) return Math.Ceiling(s / 1024.0).ToString("N0", CultureInfo.CurrentCulture) + " KB";
        if (s < 1024L * 1024 * 1024) return (s / 1048576.0).ToString("N1", CultureInfo.CurrentCulture) + " MB";
        return (s / 1073741824.0).ToString("N1", CultureInfo.CurrentCulture) + " GB";
    }
}
