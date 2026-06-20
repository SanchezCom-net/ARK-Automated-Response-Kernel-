using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ARK.UI.Core.Services;

internal static class ProcessIconHelper
{
    private const uint ShgfiUseFileAttributes = 0x000000010;
    private const uint FileAttributeNormal    = 0x000000080;

    private static readonly Lazy<BitmapSource?> _fallback = new(LoadFallback);

    internal static BitmapSource? FallbackIcon => _fallback.Value;

    internal static BitmapSource? GetIcon(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return null;

        var info   = new Win32Api.SHFILEINFO();
        var result = Win32Api.SHGetFileInfoW(
            filePath, 0, ref info,
            (uint)Marshal.SizeOf<Win32Api.SHFILEINFO>(),
            Win32Api.SHGFI_ICON | Win32Api.SHGFI_SMALLICON);

        return result != IntPtr.Zero ? ExtractIcon(ref info) : null;
    }

    private static BitmapSource? LoadFallback()
    {
        var info   = new Win32Api.SHFILEINFO();
        var result = Win32Api.SHGetFileInfoW(
            ".exe", FileAttributeNormal, ref info,
            (uint)Marshal.SizeOf<Win32Api.SHFILEINFO>(),
            Win32Api.SHGFI_ICON | Win32Api.SHGFI_SMALLICON | ShgfiUseFileAttributes);

        return result != IntPtr.Zero ? ExtractIcon(ref info) : null;
    }

    private static BitmapSource? ExtractIcon(ref Win32Api.SHFILEINFO info)
    {
        if (info.hIcon == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
        finally
        {
            Win32Api.DestroyIcon(info.hIcon);
        }
    }
}
