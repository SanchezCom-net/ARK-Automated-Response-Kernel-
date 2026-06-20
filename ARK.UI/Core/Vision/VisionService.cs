using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ARK.UI.Core.Interfaces;
using ARK.UI.Core.Models;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace ARK.UI.Core.Vision;

public sealed partial class VisionService : IVisionService, IDisposable
{
    private readonly SemaphoreSlim _captureLock = new(1, 1);
    private bool _disposed;

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint hWnd, nint hDC);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint hDC);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleBitmap(nint hDC, int cx, int cy);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint hDC, nint h);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(
        nint hDC, int x, int y, int cx, int cy,
        nint hdcSrc, int x1, int y1, uint rop);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint ho);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint hDC);

    [LibraryImport("gdi32.dll")]
    private static unsafe partial int GetDIBits(
        nint hDC, nint hBitmap, uint start, uint cLines,
        byte* lpvBits, BITMAPINFOHEADER* pbmi, uint usage);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint   biSize;
        public int    biWidth;
        public int    biHeight;     // negative = top-down
        public ushort biPlanes;
        public ushort biBitCount;
        public uint   biCompression;
        public uint   biSizeImage;
        public int    biXPelsPerMeter;
        public int    biYPelsPerMeter;
        public uint   biClrUsed;
        public uint   biClrImportant;
    }

    private const uint SRCCOPY        = 0x00CC0020;
    private const uint DIB_RGB_COLORS = 0;
    private const int  ColorTolerance = 15;

    // ── Захват экрана ────────────────────────────────────────────────────────

    public async Task<byte[]> CapturePrimaryScreenAsync(CancellationToken cancellationToken = default)
    {
        var area = new SearchArea(0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
        var bgra = await CaptureScreenAsync(area, cancellationToken).ConfigureAwait(false);
        int width = area.Width, height = area.Height;
        return await Task.Run(() =>
        {
            var bitmap = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
            bitmap.Freeze();
            var encoder = new JpegBitmapEncoder { QualityLevel = 75 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> CaptureScreenAsync(SearchArea area, CancellationToken ct)
    {
        await _captureLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => CaptureInternal(area), ct).ConfigureAwait(false);
        }
        finally
        {
            _captureLock.Release();
        }
    }

    private unsafe byte[] CaptureInternal(SearchArea area)
    {
        nint hdcScreen = GetDC(nint.Zero);
        nint hdcMem    = CreateCompatibleDC(hdcScreen);
        nint hBitmap   = CreateCompatibleBitmap(hdcScreen, area.Width, area.Height);
        nint hOld      = SelectObject(hdcMem, hBitmap);
        try
        {
            BitBlt(hdcMem, 0, 0, area.Width, area.Height, hdcScreen, area.X, area.Y, SRCCOPY);

            var bi = new BITMAPINFOHEADER
            {
                biSize        = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth       = area.Width,
                biHeight      = -area.Height,
                biPlanes      = 1,
                biBitCount    = 32,
                biCompression = 0
            };

            var pixels = new byte[area.Width * area.Height * 4];
            fixed (byte* pPixels = pixels)
                GetDIBits(hdcMem, hBitmap, 0, (uint)area.Height, pPixels, &bi, DIB_RGB_COLORS);

            return pixels;
        }
        finally
        {
            SelectObject(hdcMem, hOld);
            DeleteObject(hBitmap);
            DeleteDC(hdcMem);
            ReleaseDC(nint.Zero, hdcScreen);
        }
    }

    // ── Поиск цвета ──────────────────────────────────────────────────────────

    public async Task<WpfPoint?> FindColorAsync(
        WpfColor targetColor, SearchArea area, CancellationToken cancellationToken = default)
    {
        byte[] pixels = await CaptureScreenAsync(area, cancellationToken).ConfigureAwait(false);

        return await Task.Run(() =>
        {
            ReadOnlySpan<byte> span   = pixels;
            int                stride = area.Width * 4;

            for (int y = 0; y < area.Height; y++)
            {
                for (int x = 0; x < area.Width; x++)
                {
                    int idx = y * stride + x * 4; // BGRA layout
                    if (Math.Abs(span[idx]     - targetColor.B) <= ColorTolerance &&
                        Math.Abs(span[idx + 1] - targetColor.G) <= ColorTolerance &&
                        Math.Abs(span[idx + 2] - targetColor.R) <= ColorTolerance)
                    {
                        return (WpfPoint?)new WpfPoint(area.X + x, area.Y + y);
                    }
                }
            }
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    // ── Template Matching ────────────────────────────────────────────────────

    public async Task<WpfPoint?> FindTemplateAsync(
        byte[] templateBgra, int templateWidth, int templateHeight,
        SearchArea area, double tolerance = 0.1, CancellationToken cancellationToken = default)
    {
        byte[] pixels = await CaptureScreenAsync(area, cancellationToken).ConfigureAwait(false);

        return await Task.Run(() =>
        {
            ReadOnlySpan<byte> screen         = pixels;
            ReadOnlySpan<byte> template       = templateBgra;
            int                screenStride   = area.Width * 4;
            int                templateStride = templateWidth * 4;
            int                toleranceByte  = (int)(255 * tolerance);
            // Adaptive step: sample every N-th pixel inside template — faster on large templates
            int step = Math.Max(1, Math.Min(templateWidth, templateHeight) / 8);

            for (int sy = 0; sy <= area.Height - templateHeight; sy++)
            {
                for (int sx = 0; sx <= area.Width - templateWidth; sx++)
                {
                    if (MatchesTemplate(screen, screenStride, sx, sy,
                                        template, templateWidth, templateHeight,
                                        templateStride, toleranceByte, step))
                    {
                        return (WpfPoint?)new WpfPoint(
                            area.X + sx + templateWidth  / 2,
                            area.Y + sy + templateHeight / 2);
                    }
                }
            }
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static bool MatchesTemplate(
        ReadOnlySpan<byte> screen, int screenStride, int startX, int startY,
        ReadOnlySpan<byte> template, int templateWidth, int templateHeight,
        int templateStride, int toleranceByte, int step)
    {
        for (int ty = 0; ty < templateHeight; ty += step)
        {
            for (int tx = 0; tx < templateWidth; tx += step)
            {
                int si = (startY + ty) * screenStride   + (startX + tx) * 4;
                int ti =         ty    * templateStride +          tx    * 4;
                if (Math.Abs(screen[si]     - template[ti])     > toleranceByte ||
                    Math.Abs(screen[si + 1] - template[ti + 1]) > toleranceByte ||
                    Math.Abs(screen[si + 2] - template[ti + 2]) > toleranceByte)
                    return false;
            }
        }
        return true;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _captureLock.Dispose();
    }
}
