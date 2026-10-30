using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenGuide;

// Images live only in memory. Geometry always comes from this live desktop capture.
internal sealed record PixelSnapshot(nint Window, Rect WindowBounds, Rect ScreenBounds, int Width, int Height,
    byte[] Pixels, IReadOnlyList<Rect> Masks)
{
    internal byte[] EncodeJpeg()
    {
        // A 1000x1000 letterboxed image makes image pixels and 0..1000 coordinates
        // identical. Never heuristically reinterpret an ambiguous model coordinate unit.
        var viewport = ScreenCapture.ImageViewport(Width, Height);
        const int side = 1000; var canvas = new byte[side * side * 4];
        for (int i = 0; i < canvas.Length; i += 4) { canvas[i] = canvas[i + 1] = canvas[i + 2] = 96; canvas[i + 3] = 255; }
        int w = (int)viewport.Width, h = (int)viewport.Height;
        for (int y = 0; y < h; y++)
        {
            double sy = Math.Clamp((y + 0.5) * Height / h - 0.5, 0, Height - 1); int y0 = (int)sy, y1 = Math.Min(Height - 1, y0 + 1); double fy = sy - y0;
            for (int x = 0; x < w; x++)
            {
                double sx = Math.Clamp((x + 0.5) * Width / w - 0.5, 0, Width - 1); int x0 = (int)sx, x1 = Math.Min(Width - 1, x0 + 1); double fx = sx - x0;
                int dest = (((int)viewport.Y + y) * side + (int)viewport.X + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    double a = Pixels[(y0 * Width + x0) * 4 + c] * (1 - fx) + Pixels[(y0 * Width + x1) * 4 + c] * fx;
                    double b = Pixels[(y1 * Width + x0) * 4 + c] * (1 - fx) + Pixels[(y1 * Width + x1) * 4 + c] * fx;
                    canvas[dest + c] = (byte)Math.Round(a * (1 - fy) + b * fy);
                }
            }
        }
        var source = BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgr32, null, canvas, side * 4);
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
}
internal sealed record VisualProof(PixelSnapshot Snapshot, Rect Bounds);
internal sealed class ScreenCaptureBlockedException(bool unresolvedOverlay) : InvalidOperationException(
    unresolvedOverlay ? "检测到无法核实透明区域的上层浮窗，截图未发送。请收起或移开语音浮窗后重读，无需断开语音连接。" :
    "目标窗口的大部分被其他窗口遮挡，截图未发送；请露出目标软件后重读。")
{
    internal bool UnresolvedOverlay { get; } = unresolvedOverlay;
}

internal static class ScreenCapture
{
    internal static Rect ImageViewport(int width, int height)
    {
        double scale = Math.Min(1000.0 / width, 1000.0 / height);
        int w = Math.Clamp((int)Math.Round(width * scale, MidpointRounding.AwayFromZero), 1, 1000);
        int h = Math.Clamp((int)Math.Round(height * scale, MidpointRounding.AwayFromZero), 1, 1000);
        return new((1000 - w) / 2, (1000 - h) / 2, w, h);
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapHeader Header; public uint Color; }
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint GetTopWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(nint window, out uint colourKey, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(nint window, nint region);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int GetFrameBounds(nint window, int attribute, out Native.RECT value, int size);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dest, int x, int y, int width, int height, nint src, int sx, int sy, uint operation);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern uint GetRegionData(nint region, uint count, nint data);

    internal static bool HitBelongs(nint hit, nint target)
    {
        if (Native.BelongsTo(hit, target)) return true;
        // Unexposed native menus may have no normal owner. This relaxation is only used
        // INSIDE an image crop; switching to an independent window still pauses the guide.
        Native.GetWindowThreadProcessId(hit, out var hitPid);
        Native.GetWindowThreadProcessId(target, out var pid);
        return pid != 0 && pid == hitPid;
    }
    internal static PixelSnapshot Capture(nint window, IReadOnlySet<nint>? hidden = null)
    {
        using var dpi = CaptureDpiScope.Enter();
        if (!Native.CanTarget(window) || !Native.GetWindowRect(window, out var wr))
            throw new InvalidOperationException("目标窗口不可见，不能截图；请恢复窗口再指路。");
        var windowBounds = wr.ToRect();
        var popups = Native.OwnedPopups(window).Select(popup => Native.GetWindowRect(popup, out var pr) ? pr.ToRect() : Rect.Empty).Where(r => !r.IsEmpty).ToArray();
        var desktop = new Rect(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
        var crop = CaptureGeometry.Crop(windowBounds, FrameBounds(window), popups, desktop);
        if (crop.IsEmpty || crop.Width < 20 || crop.Height < 20 || crop.Width > 8192 || crop.Height > 8192 || crop.Width * crop.Height > 16_000_000)
            throw new InvalidOperationException("窗口可见范围太小或过大，请调整窗口后再指路。");

        Log.Write("capture-geometry", FormattableString.Invariant($"window={windowBounds} crop={crop} desktop={desktop} popups={popups.Length} physicalPixels=true"));
        var uncertain = new List<Rect>();
        var occluders = OcclusionMasks(window, crop, hidden, uncertain);
        var masks = new List<Rect>(occluders);
        // A popup extending outside the root must not reveal the desktop/another app around it.
        if (!windowBounds.Contains(crop))
        {
            var allowed = new List<Rect> { windowBounds };
            allowed.AddRange(popups);
            foreach (var strip in Outside(crop, windowBounds))
            {
                var remaining = new List<Rect> { strip };
                foreach (var region in allowed.Skip(1)) remaining = remaining.SelectMany(r => Outside(r, region)).ToList();
                masks.AddRange(remaining);
            }
        }
        var pixels = CapturePixels(crop);
        // Include both before/after occluder bounds if another window moves during capture.
        var afterOccluders = OcclusionMasks(window, crop, hidden, uncertain);
        occluders.AddRange(afterOccluders); masks.AddRange(afterOccluders);
        bool unresolvedOverlay = false;
        if (uncertain.Count > 0)
        {
            var references = WindowReferenceCapture.TryCapture(window, windowBounds);
            foreach (var area in uncertain.Distinct())
            {
                var resolved = CaptureOverlayResolver.Resolve(crop, pixels, area, references);
                occluders.AddRange(resolved); masks.AddRange(resolved);
                bool unresolved = resolved.Any(r => r == area);
                unresolvedOverlay |= unresolved;
                Log.Write("capture-overlay-check", FormattableString.Invariant($"region={area} references={references.Count} masks={resolved.Count} unresolved={unresolved}"));
            }
        }
        masks = masks.Distinct().ToList();
        Log.Write("capture-masks", FormattableString.Invariant($"count={masks.Count} maxAreaFraction={masks.Select(m => m.Width * m.Height / (crop.Width * crop.Height)).DefaultIfEmpty().Max():F3} sample={string.Join(";", masks.Take(12).Select(r => r.ToString(System.Globalization.CultureInfo.InvariantCulture)))}"));
        if (masks.Any(m => m.Contains(crop))) throw new ScreenCaptureBlockedException(unresolvedOverlay);
        if (CaptureGeometry.SubstantiallyObscured(crop, occluders))
            throw new ScreenCaptureBlockedException(unresolvedOverlay);
        foreach (var mask in masks) PaintMask(pixels, (int)crop.Width, crop, mask);
        if (!Native.GetWindowRect(window, out var after) || !Native.SameRect(after.ToRect(), windowBounds))
            throw new InvalidOperationException("截图期间窗口已移动，请重新指路。");
        return new(window, windowBounds, crop, (int)crop.Width, (int)crop.Height, pixels, masks);
    }
    private static List<Rect> OcclusionMasks(nint window, Rect crop, IReadOnlySet<nint>? hidden, List<Rect> uncertain)
    {
        var masks = new List<Rect>(); var seen = new HashSet<nint>(); bool found = false;
        int hiddenCount = 0, zeroAlphaCount = 0, emptyRegionCount = 0, regionCount = 0, rectangleCount = 0, unknownLayeredCount = 0, clickThroughCount = 0;
        for (var h = GetTopWindow(0); h != 0 && seen.Count < 1024 && seen.Add(h); h = Native.GetWindow(h, 2))
        {
            if (h == window) { found = true; break; }
            if (hidden?.Contains(h) == true) { hiddenCount++; continue; }
            if (!Native.Usable(h) || HitBelongs(h, window)) continue;
            if (Native.GetWindowRect(h, out var hr))
            {
                if (!crop.IntersectsWith(hr.ToRect())) continue;
                long style = Native.GetWindowLongPtr(h, Native.GwlExStyle).ToInt64();
                byte alpha = 255; uint flags = 0;
                bool layered = (style & CaptureGeometry.LayeredStyle) != 0;
                bool attributesKnown = layered && GetLayeredWindowAttributes(h, out _, out alpha, out flags);
                if (CaptureGeometry.IsProvenInvisible(style, attributesKnown, alpha, flags)) { zeroAlphaCount++; continue; }
                // Input transparency does not imply visual transparency. Unknown per-pixel
                // alpha must retain conservative masking, rather than expose another app.
                if (layered && !attributesKnown) unknownLayeredCount++;
                if ((style & Native.WsExTransparent) != 0) clickThroughCount++;
                var regions = WindowRegions(h);
                if (regions is { Count: 0 }) emptyRegionCount++;
                else if (regions != null) regionCount++;
                else rectangleCount++;
                var areas = CaptureGeometry.OccluderRegions(crop, hr.ToRect(), FrameBounds(h), regions, false);
                if ((style & Native.WsExTransparent) != 0 && regions == null)
                {
                    uncertain.AddRange(areas);
                    Log.Write("capture-overlay-host", FormattableString.Invariant($"app={Native.AppName(h)} class={Native.ClassName(h)} style=0x{style:X} alphaKnown={attributesKnown} alpha={alpha} flags={flags} rect={hr.ToRect()}"));
                }
                else masks.AddRange(areas);
            }
        }
        Log.Write("capture-occluders", $"hidden={hiddenCount} zeroAlpha={zeroAlphaCount} emptyRegion={emptyRegionCount} shaped={regionCount} conservativeRect={rectangleCount} unknownLayered={unknownLayeredCount} inputTransparent={clickThroughCount} masks={masks.Count}");
        if (!found) throw new InvalidOperationException("无法核实截图窗口层级；请将目标软件放到前台后重试。");
        return masks;
    }
    private static Rect? FrameBounds(nint window) => GetFrameBounds(window, 9, out var frame, Marshal.SizeOf<Native.RECT>()) == 0 ? frame.ToRect() : null;
    private static IReadOnlyList<Rect>? WindowRegions(nint window)
    {
        nint region = CreateRectRgn(0, 0, 0, 0), data = 0;
        if (region == 0) return null;
        try
        {
            int kind = GetWindowRgn(window, region);
            if (kind == 1) return [];
            if (kind is not (2 or 3)) return null;
            uint bytes = GetRegionData(region, 0, 0);
            if (bytes < 32 || bytes > 32 + 4096 * 16) return null;
            data = Marshal.AllocHGlobal((int)bytes);
            if (GetRegionData(region, bytes, data) != bytes) return null;
            int size = Marshal.ReadInt32(data), type = Marshal.ReadInt32(data, 4), count = Marshal.ReadInt32(data, 8);
            if (size != 32 || type != 1 || count < 0 || count > 4096 || 32L + count * 16L > bytes) return null;
            var result = new List<Rect>(count);
            for (int i = 0; i < count; i++) result.Add(Marshal.PtrToStructure<Native.RECT>(data + 32 + i * 16).ToRect());
            return result;
        }
        finally { if (data != 0) Marshal.FreeHGlobal(data); DeleteObject(region); }
    }
    private static Rect Integral(Rect rect) => CaptureGeometry.Integral(rect);

    internal static IEnumerable<Rect> Outside(Rect area, Rect cut)
    {
        var overlap = Rect.Intersect(area, cut);
        if (overlap.IsEmpty || overlap.Width <= 0 || overlap.Height <= 0) { yield return area; yield break; }
        if (overlap.Top > area.Top) yield return new(area.Left, area.Top, area.Width, overlap.Top - area.Top);
        if (overlap.Bottom < area.Bottom) yield return new(area.Left, overlap.Bottom, area.Width, area.Bottom - overlap.Bottom);
        if (overlap.Left > area.Left) yield return new(area.Left, overlap.Top, overlap.Left - area.Left, overlap.Height);
        if (overlap.Right < area.Right) yield return new(overlap.Right, overlap.Top, area.Right - overlap.Right, overlap.Height);
    }
    internal static byte[] CapturePixels(Rect bounds)
    {
        using var dpi = CaptureDpiScope.Enter();
        int width = (int)bounds.Width, height = (int)bounds.Height;
        if (width <= 0 || height <= 0 || (long)width * height > 16_000_000) throw new InvalidOperationException("截图范围无效。");
        nint src = 0, dest = 0, bitmap = 0, previous = 0;
        try
        {
            src = GetDC(0); dest = CreateCompatibleDC(src);
            var info = new BitmapInfo { Header = new() { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 } };
            bitmap = CreateDIBSection(src, ref info, 0, out var bits, 0, 0);
            if (src == 0 || dest == 0 || bitmap == 0 || bits == 0) throw new InvalidOperationException("无法读取桌面图像；请确认桌面未锁定。");
            previous = SelectObject(dest, bitmap);
            if (previous == 0 || previous == new nint(-1) || !BitBlt(dest, 0, 0, width, height, src, (int)bounds.X, (int)bounds.Y, 0x40CC0020))
                throw new InvalidOperationException("桌面图像读取失败，未发送图片。");
            var result = new byte[checked(width * height * 4)]; Marshal.Copy(bits, result, 0, result.Length); return result;
        }
        finally
        {
            if (previous != 0 && previous != new nint(-1)) SelectObject(dest, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (dest != 0) DeleteDC(dest);
            if (src != 0) ReleaseDC(0, src);
        }
    }
    internal static Rect MapBox(PixelSnapshot image, double[] box)
    {
        if (box.Length != 4 || box.Any(v => !double.IsFinite(v) || v < 0 || v > 1000) || box[2] <= box[0] || box[3] <= box[1]) return Rect.Empty;
        var viewport = ImageViewport(image.Width, image.Height);
        if (!viewport.Contains(new Rect(box[0], box[1], box[2] - box[0], box[3] - box[1]))) return Rect.Empty;
        var rect = Integral(new(image.ScreenBounds.X + (box[0] - viewport.X) * image.Width / viewport.Width,
            image.ScreenBounds.Y + (box[1] - viewport.Y) * image.Height / viewport.Height,
            (box[2] - box[0]) * image.Width / viewport.Width, (box[3] - box[1]) * image.Height / viewport.Height));
        if (rect.IsEmpty || rect.Width < 8 || rect.Height < 8 || rect.Width * rect.Height > image.Width * (double)image.Height * 0.25 ||
            !image.ScreenBounds.Contains(rect) || image.Masks.Any(r => r.IntersectsWith(rect))) return Rect.Empty;
        return rect;
    }
    internal static bool StillValid(VisualProof proof)
    {
        try
        {
            using var dpi = CaptureDpiScope.Enter();
            var image = proof.Snapshot;
            if (!Native.CanTarget(image.Window) || !Native.GetWindowRect(image.Window, out var wr) || !Native.SameRect(wr.ToRect(), image.WindowBounds)) return false;
            foreach (var pt in SamplePoints(proof.Bounds))
                if (!HitBelongs(Native.WindowFromPoint(pt), image.Window)) return false;
            var crop = Integral(proof.Bounds);
            return PatchMatches(Extract(image, crop), CapturePixels(crop), (int)crop.Width, (int)crop.Height);
        }
        catch { return false; }
    }
    private static IEnumerable<Native.POINT> SamplePoints(Rect r)
    {
        yield return new() { X = (int)(r.X + r.Width / 2), Y = (int)(r.Y + r.Height / 2) };
        foreach (double x in new[] { r.Left + 2, r.Right - 2 })
            foreach (double y in new[] { r.Top + 2, r.Bottom - 2 }) yield return new() { X = (int)x, Y = (int)y };
    }
    internal static byte[] Extract(PixelSnapshot image, Rect region)
    {
        int x = (int)(region.X - image.ScreenBounds.X), y = (int)(region.Y - image.ScreenBounds.Y), width = (int)region.Width, height = (int)region.Height;
        if (x < 0 || y < 0 || x + width > image.Width || y + height > image.Height) throw new InvalidOperationException("图像范围已变化。");
        var result = new byte[width * height * 4];
        for (int row = 0; row < height; row++) Buffer.BlockCopy(image.Pixels, ((y + row) * image.Width + x) * 4, result, row * width * 4, width * 4);
        return result;
    }
    // Compare absolute edges rather than background colour: normal menu hover/inversion
    // must not erase a valid frame. This is a conservative heuristic, not UIA identity proof.
    internal static bool PatchMatches(byte[] before, byte[] after, int width, int height)
    {
        if (before.Length != after.Length || before.Length != width * height * 4 || width < 4 || height < 4) return false;
        int oldEdges = 0, newEdges = 0, common = 0;
        for (int y = 1; y < height - 1; y++) for (int x = 1; x < width - 1; x++)
        {
            int i = (y * width + x) * 4;
            bool a = Edge(before, i, width), b = Edge(after, i, width);
            if (a) oldEdges++; if (b) newEdges++; if (a && b) common++;
        }
        return oldEdges >= 6 && newEdges >= 6 && 2.0 * common / (oldEdges + newEdges) >= 0.70;
    }
    private static bool Edge(byte[] pixels, int i, int width)
    {
        int Bright(int n) => (pixels[n] + pixels[n + 1] * 2 + pixels[n + 2]) / 4;
        return Math.Abs(Bright(i) - Bright(i + 4)) + Math.Abs(Bright(i) - Bright(i + width * 4)) >= 24;
    }
    internal static bool HasChanged(PixelSnapshot before, PixelSnapshot after, Rect ignored)
    {
        if (!Native.SameRect(before.ScreenBounds, after.ScreenBounds) || !Native.SameRect(before.WindowBounds, after.WindowBounds)) return true;
        int changed = 0, total = 0; ignored.Inflate(8, 8);
        for (int y = 4; y < before.Height; y += 8) for (int x = 4; x < before.Width; x += 8)
        {
            var point = new Point(before.ScreenBounds.X + x, before.ScreenBounds.Y + y);
            if (ignored.Contains(point) || before.Masks.Any(r => r.Contains(point)) || after.Masks.Any(r => r.Contains(point))) continue;
            int i = (y * before.Width + x) * 4; total++;
            if (Math.Abs(before.Pixels[i] - after.Pixels[i]) + Math.Abs(before.Pixels[i + 1] - after.Pixels[i + 1]) + Math.Abs(before.Pixels[i + 2] - after.Pixels[i + 2]) > 100) changed++;
        }
        return changed >= 20 && changed > total * 0.008;
    }
    internal static void PaintMask(byte[] pixels, int width, Rect crop, Rect mask)
    {
        int left = Math.Max(0, (int)Math.Floor(mask.Left - crop.Left)), top = Math.Max(0, (int)Math.Floor(mask.Top - crop.Top));
        int right = Math.Min(width, (int)Math.Ceiling(mask.Right - crop.Left)), bottom = Math.Min(pixels.Length / 4 / width, (int)Math.Ceiling(mask.Bottom - crop.Top));
        for (int y = top; y < bottom; y++) for (int x = left; x < right; x++)
        { int i = (y * width + x) * 4; pixels[i] = pixels[i + 1] = pixels[i + 2] = 96; pixels[i + 3] = 255; }
    }
}
