using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ScoutR;

/// <summary>Type names ("Text Document") and shell verbs (Properties, Open with) from Windows.</summary>
public static class ShellInfo
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    private const uint ShgfiTypeName = 0x400;
    private const uint ShgfiUseFileAttributes = 0x10;
    private const uint AttrDirectory = 0x10;
    private const uint AttrNormal = 0x80;

    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static string Query(string path, uint attributes, uint flags)
    {
        try
        {
            var info = new ShFileInfo();
            SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiTypeName | flags);
            return info.szTypeName ?? "";
        }
        catch
        {
            return "";
        }
    }

    public static string TypeName(FsItem item)
    {
        if (item.IsDrive) return Cache.GetOrAdd("drive:" + item.FullPath, _ => Query(item.FullPath, 0, 0));
        if (item.IsDirectory) return Cache.GetOrAdd("<dir>", _ => Query("folder", AttrDirectory, ShgfiUseFileAttributes));

        string ext = Path.GetExtension(item.Name);
        // .exe/.lnk etc. are described per file by Windows, but the generic name per extension is enough here.
        return Cache.GetOrAdd(ext.Length == 0 ? "<none>" : ext,
            _ => Query("file" + ext, AttrNormal, ShgfiUseFileAttributes));
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiSmallIcon = 0x1;

    private static readonly ConcurrentDictionary<string, BitmapSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    // These carry their own icon per file, so they are read from the real file.
    private static readonly HashSet<string> OwnIcon = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".ico", ".lnk", ".url", ".msc", ".cpl", ".scr" };

    private static BitmapSource? LoadIcon(string path, uint attributes, uint flags)
    {
        try
        {
            var info = new ShFileInfo();
            SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(),
                ShgfiIcon | ShgfiSmallIcon | flags);
            if (info.hIcon == IntPtr.Zero) return null;
            try
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                return bmp;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The icon Explorer shows for this item (16 px), or null to use the drawn one.</summary>
    public static BitmapSource? Icon(FsItem item)
    {
        if (item.IsDrive) return ShellIcons.ForDrive(item.FullPath);
        if (item.IsDirectory) return ShellIcons.Folder;

        string ext = Path.GetExtension(item.Name);
        if (OwnIcon.Contains(ext))
            return IconCache.GetOrAdd(item.FullPath, p => LoadIcon(p, 0, 0));
        return IconCache.GetOrAdd(ext.Length == 0 ? "<none>" : ext,
            _ => LoadIcon("file" + ext, AttrNormal, ShgfiUseFileAttributes));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string? lpVerb;
        public string? lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);

    private const uint SeeMaskInvokeIdList = 0x0C;

    /// <summary>Runs a shell verb such as "properties" or "openas" on a file or folder.</summary>
    public static void Verb(string path, string verb, IntPtr owner)
    {
        var info = new ShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<ShellExecuteInfo>(),
            fMask = SeeMaskInvokeIdList,
            hwnd = owner,
            lpVerb = verb,
            lpFile = path,
            nShow = 1,
        };
        if (!ShellExecuteEx(ref info)) throw new System.ComponentModel.Win32Exception();
    }
}
