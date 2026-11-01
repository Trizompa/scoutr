using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ScoutR;

/// <summary>The standard Windows folder and drive icons, read from the system (same as Explorer).</summary>
public static class ShellIcons
{
    private const int SiidFolder = 3;
    private const int SiidFolderOpen = 4;
    private const int SiidDriveRemovable = 7;
    private const int SiidDriveFixed = 8;
    private const int SiidDriveNet = 9;
    private const int SiidDriveCd = 11;

    private const uint ShgsiIcon = 0x100; // large icon (32px); WPF scales it down smoothly

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StockIconInfo
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysImageIndex;
        public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szPath;
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetStockIconInfo(int siid, uint flags, ref StockIconInfo info);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static BitmapSource? Load(int id)
    {
        try
        {
            var info = new StockIconInfo { cbSize = (uint)Marshal.SizeOf<StockIconInfo>() };
            if (SHGetStockIconInfo(id, ShgsiIcon, ref info) != 0 || info.hIcon == IntPtr.Zero) return null;

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
            return null; // fall back to the drawn icons
        }
    }

    public static BitmapSource? Folder { get; } = Load(SiidFolder);
    public static BitmapSource? FolderOpen { get; } = Load(SiidFolderOpen) ?? Folder;
    private static BitmapSource? DriveFixed { get; } = Load(SiidDriveFixed);
    private static BitmapSource? DriveRemovable { get; } = Load(SiidDriveRemovable) ?? DriveFixed;
    private static BitmapSource? DriveNet { get; } = Load(SiidDriveNet) ?? DriveFixed;
    private static BitmapSource? DriveCd { get; } = Load(SiidDriveCd) ?? DriveFixed;

    /// <summary>False if Windows would not give us the icons; the UI then uses the drawn ones.</summary>
    public static bool Available => Folder is not null;

    public static BitmapSource? ForDrive(string path)
    {
        try
        {
            return new DriveInfo(path).DriveType switch
            {
                DriveType.Removable => DriveRemovable,
                DriveType.Network => DriveNet,
                DriveType.CDRom => DriveCd,
                _ => DriveFixed,
            };
        }
        catch
        {
            return DriveFixed;
        }
    }
}
