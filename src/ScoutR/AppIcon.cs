using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScoutR;

/// <summary>
/// Window/taskbar icon: the Explorer icon of this PC, recoloured from blue to green at runtime.
/// Nothing from Windows is shipped with ScoutR; if reading it fails, the bundled icon stays.
/// </summary>
public static class AppIcon
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy,
        IntPtr[] icons, IntPtr[]? ids, uint count, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static ImageSource? Load()
    {
        try
        {
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            var handles = new IntPtr[1];
            if (PrivateExtractIcons(explorer, 0, 256, 256, handles, null, 1, 0) == 0 || handles[0] == IntPtr.Zero)
                return null;

            BitmapSource src;
            try
            {
                src = Imaging.CreateBitmapSourceFromHIcon(handles[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                DestroyIcon(handles[0]);
            }

            var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            bgra.CopyPixels(px, stride, 0);

            // Blue pixels (the pocket) become green: swap the blue and green channels. Yellow stays yellow.
            for (int i = 0; i < px.Length; i += 4)
            {
                byte b = px[i], g = px[i + 1], r = px[i + 2];
                if (b > r + 25 && b > g)
                {
                    px[i] = g;
                    px[i + 1] = b;
                }
            }

            var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
            result.Freeze();
            return result;
        }
        catch
        {
            return null;
        }
    }
}
