using System.Runtime.InteropServices;
using System.Windows;

namespace ScreenGuide;

internal static class WindowReferenceCapture
{
    private static readonly SemaphoreSlim Slot = new(1, 1);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapHeader info, uint usage, out nint bits, nint section, uint offset);
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
        public uint Color;
    }

    internal static IReadOnlyList<WindowPixelReference> TryCapture(nint target, Rect expectedBounds)
    {
        // PrintWindow is synchronous and some apps never return. At most one
        // worker can exist; timeout does not reuse old data or free its live DC.
        if (!Slot.Wait(0)) return [];
        var task = Task.Run<IReadOnlyList<WindowPixelReference>>(() =>
        {
            try
            {
                using var dpi = CaptureDpiScope.Enter();
                if (!Native.CanTarget(target) || !Native.GetWindowRect(target, out var before) || !Native.SameRect(before.ToRect(), expectedBounds)) return [];
                var windows = Native.OwnedPopups(target).ToList();
                if (windows.Count > 5) return [];
                windows.Add(target); // EnumWindows already supplies top-to-bottom order.
                var result = new List<WindowPixelReference>();
                long pixels = 0;
                foreach (var window in windows)
                {
                    if (!Native.GetWindowRect(window, out var wr)) return [];
                    var bounds = wr.ToRect(); pixels += (long)bounds.Width * (long)bounds.Height;
                    if (pixels > 24_000_000) return [];
                    var data = Render(window, bounds);
                    if (!Native.Usable(window) || !Native.GetWindowRect(window, out var after) || !Native.SameRect(bounds, after.ToRect())) return [];
                    result.Add(new(bounds, data));
                }
                if (!Native.CanTarget(target) || !Native.GetWindowRect(target, out var last) || !Native.SameRect(last.ToRect(), expectedBounds)) return [];
                return result;
            }
            catch { return []; }
            finally { Slot.Release(); }
        });
        return task.Wait(500) ? task.Result : [];
    }

    private static byte[]? Render(nint window, Rect bounds)
    {
        int width = (int)bounds.Width, height = (int)bounds.Height;
        if (width < 1 || height < 1 || width > 8192 || height > 8192 || (long)width * height > 16_000_000) return null;
        nint screen = 0, dc = 0, bitmap = 0, previous = 0;
        try
        {
            screen = GetDC(0); dc = CreateCompatibleDC(screen);
            var header = new BitmapHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(screen, ref header, 0, out var bits, 0, 0);
            if (screen == 0 || dc == 0 || bitmap == 0 || bits == 0) return null;
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            { pixels[i] = CaptureOverlayResolver.SentinelBlue; pixels[i+1] = CaptureOverlayResolver.SentinelGreen; pixels[i+2] = CaptureOverlayResolver.SentinelRed; }
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            previous = SelectObject(dc, bitmap);
            if (previous == 0 || previous == new nint(-1) || !PrintWindow(window, dc, 2)) return null;
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            if (previous != 0 && previous != new nint(-1)) SelectObject(dc, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (dc != 0) DeleteDC(dc);
            if (screen != 0) ReleaseDC(0, screen);
        }
    }
}
