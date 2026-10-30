using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenGuide;

internal static class CaptureGeometryCheck
{
    // No screen/DC/window access: every frame and pixel below is synthetic.
    internal static IReadOnlyList<(string Label, bool Passed)> Checks()
    {
        var results = new List<(string Label, bool Passed)>();
        void Check(string label, Func<bool> test)
        {
            try { results.Add((label, test())); }
            catch { results.Add((label, false)); }
        }
        var desktop = new Rect(-1920, -200, 4480, 1640);
        var window = new Rect(-1600, 100, 1600, 900);
        Check("capture keeps both edges on a negative-origin monitor", () => CaptureGeometry.Crop(window, null, [], desktop) == window);
        Check("capture uses physical DWM frame only inside window", () => CaptureGeometry.Crop(window, new Rect(-1592, 100, 1584, 892), [], desktop) == new Rect(-1592, 100, 1584, 892));
        Check("mismatched DWM units cannot truncate physical window", () => CaptureGeometry.Crop(window, new Rect(0, 0, 1000, 500), [], desktop) == window);
        Check("popup extending right remains in capture", () => CaptureGeometry.Crop(window, null, [new Rect(-30, 400, 250, 300)], desktop) == new Rect(-1600, 100, 1820, 900));
        Check("desktop clipping trims only truly offscreen pixels", () => CaptureGeometry.Crop(new Rect(-2000, 0, 1600, 900), null, [], desktop) == new Rect(-1920, 0, 1520, 900));
        Check("fractional integral crop keeps all complete pixel columns", () => CaptureGeometry.Integral(new Rect(-2.7, 1.2, 12.5, 8.5)) == new Rect(-2, 2, 11, 7));

        long layered = CaptureGeometry.LayeredStyle;
        Check("confirmed zero-alpha layered window is not an opaque blocker", () => CaptureGeometry.IsProvenInvisible(layered, true, 0, 2));
        Check("unknown per-pixel alpha is conservatively retained", () => !CaptureGeometry.IsProvenInvisible(layered, false, 0, 2));
        Check("input-transparent style alone never implies invisible pixels", () => !CaptureGeometry.IsProvenInvisible(Native.WsExTransparent | layered, false, 0, 0));
        Check("partial alpha remains a real visual blocker", () => !CaptureGeometry.IsProvenInvisible(layered, true, 128, 2));
        Check("colour key without global zero alpha remains conservative", () => !CaptureGeometry.IsProvenInvisible(layered, true, 0, 1));
        Check("zero alpha value without layered style is insufficient", () => !CaptureGeometry.IsProvenInvisible(0, true, 0, 2));
        var crop = new Rect(0, 0, 1600, 900);
        var overlay = new Rect(580, 0, 1020, 900);
        Check("NULLREGION never erases the right side", () => CaptureGeometry.OccluderRegions(crop, overlay, null, [], false).Count == 0);
        Check("zero-alpha overlay never erases the right side", () => CaptureGeometry.OccluderRegions(crop, overlay, null, null, true).Count == 0);
        Check("window region limits a wide transparent host to actual panel", () => CaptureGeometry.OccluderRegions(crop, overlay, null, [new Rect(900, 100, 120, 200)], false).SequenceEqual([new Rect(1480, 100, 120, 200)]));
        Check("disjoint window regions retain their transparent gap", () => CaptureGeometry.OccluderRegions(crop, overlay, null, [new Rect(0, 0, 20, 20), new Rect(1000, 880, 20, 20)], false).SequenceEqual([new Rect(580, 0, 20, 20), new Rect(1580, 880, 20, 20)]));
        Check("unreadable region continues masking unrelated opaque app", () => CaptureGeometry.OccluderRegions(crop, overlay, null, null, false).SequenceEqual([overlay]));
        Check("DWM invisible frame border does not enlarge masks", () => CaptureGeometry.OccluderRegions(crop, new Rect(500, 0, 200, 400), new Rect(508, 8, 184, 384), null, false).SequenceEqual([new Rect(508, 8, 184, 384)]));
        Check("region coordinates translate from window not client origin", () => CaptureGeometry.OccluderRegions(window, new Rect(-1000, 300, 400, 400), null, [new Rect(20, 40, 50, 60)], false).SequenceEqual([new Rect(-980, 340, 50, 60)]));
        Check("a mostly hidden frame produces explicit retry rather than a misleading half image", () => CaptureGeometry.SubstantiallyObscured(crop, [overlay]));
        Check("small real popup mask is allowed and retained", () => !CaptureGeometry.SubstantiallyObscured(crop, [new Rect(1480, 100, 120, 200)]));

        byte[] Fixture(int width, int height)
        {
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int at = (y * width + x) * 4;
                pixels[at] = (byte)(x < width / 2 ? 0 : 255);
                pixels[at + 1] = 20; pixels[at + 2] = (byte)(x < width / 2 ? 255 : 0); pixels[at + 3] = 255;
            }
            return pixels;
        }
        Check("transparent host regression preserves left and right source pixels", () =>
        {
            var pixels = Fixture(1600, 900);
            foreach (var mask in CaptureGeometry.OccluderRegions(crop, overlay, null, [], false)) ScreenCapture.PaintMask(pixels, 1600, crop, mask);
            return pixels[0] == 0 && pixels[2] == 255 && pixels[(1599 * 4)] == 255 && pixels[(1599 * 4) + 2] == 0;
        });
        Check("real panel mask removes only panel while keeping rightmost source", () =>
        {
            var pixels = Fixture(1600, 900);
            foreach (var mask in CaptureGeometry.OccluderRegions(crop, overlay, null, [new Rect(900, 100, 100, 200)], false)) ScreenCapture.PaintMask(pixels, 1600, crop, mask);
            return pixels[(200 * 1600 + 1500) * 4] == 96 && pixels[(200 * 1600 + 1599) * 4] == 255 && pixels[(200 * 1600 + 1000) * 4] == 255;
        });
        Check("wide JPEG contains both full-width edge colours", () => EncodedEdges(1600, 900, Fixture(1600, 900)));
        var testCrop = new Rect(-40, 12, 128, 64);
        var suspect = new Rect(8, 12, 80, 64);
        var testPixels = Fixture(128, 64);
        var reference = new WindowPixelReference(testCrop, testPixels.ToArray());
        Check("transparent input overlay matching the current target preserves both screenshot edges", () =>
        {
            var pixels = testPixels.ToArray();
            var masks = CaptureOverlayResolver.Resolve(testCrop, pixels, suspect, [reference]);
            foreach (var mask in masks) ScreenCapture.PaintMask(pixels, 128, testCrop, mask);
            return masks.Count == 0 && pixels.SequenceEqual(testPixels);
        });
        Check("input transparency without a valid current window reference remains blocked", () =>
            CaptureOverlayResolver.Resolve(testCrop, testPixels, suspect, []).SequenceEqual([suspect]));
        Check("failed target rendering retains its entire possible overlay", () =>
            CaptureOverlayResolver.Resolve(testCrop, testPixels, suspect, [new(testCrop, null)]).SequenceEqual([suspect]));
        Check("visible content in a transparent host is masked without erasing its transparent background", () =>
        {
            var pixels = testPixels.ToArray(); pixels[(16 * 128 + 64) * 4 + 1] ^= 127;
            var masks = CaptureOverlayResolver.Resolve(testCrop, pixels, suspect, [reference]);
            return masks.Any(r => r.Contains(new Point(24, 28))) && masks.Sum(r => r.Width * r.Height) < suspect.Width * suspect.Height * .1;
        });
        Check("a single differing colour channel cannot be exposed as verified target pixels", () =>
        {
            var pixels = testPixels.ToArray(); pixels[(16 * 128 + 64) * 4] ^= 1;
            return CaptureOverlayResolver.Resolve(testCrop, pixels, suspect, [reference]).Any(r => r.Contains(new Point(24, 28)));
        });
        Check("opaque click-through overlay still triggers full-region protection", () =>
        {
            var pixels = testPixels.ToArray();
            for (int i = 0; i < pixels.Length; i += 4) pixels[i+1] ^= 127;
            return CaptureOverlayResolver.Resolve(testCrop, pixels, suspect, [reference]).SequenceEqual([suspect]);
        });
        Check("fragmented overlay differences cannot evade the substantial-occlusion threshold", () =>
        {
            var pixels = testPixels.ToArray();
            for (int y = 0; y < 64; y += 8) for (int x = 48; x < 128; x += 16) pixels[(y*128+x)*4+1] ^= 127;
            return CaptureOverlayResolver.Resolve(testCrop, pixels, suspect, [reference]).SequenceEqual([suspect]);
        });
        Check("unpainted sentinel data cannot authorize overlay removal", () =>
        {
            var sentinel = new byte[testPixels.Length];
            for (int i = 0; i < sentinel.Length; i+=4) { sentinel[i]=CaptureOverlayResolver.SentinelBlue; sentinel[i+1]=CaptureOverlayResolver.SentinelGreen; sentinel[i+2]=CaptureOverlayResolver.SentinelRed; }
            return CaptureOverlayResolver.Resolve(testCrop, sentinel, suspect, [new(testCrop, sentinel)]).SequenceEqual([suspect]);
        });
        Check("an unreadable associated popup cannot fall through to its parent reference", () =>
        {
            var popup = new Rect(16, 20, 16, 16);
            var masks = CaptureOverlayResolver.Resolve(testCrop, testPixels, suspect, [new(popup,null), reference]);
            return masks.Any(r=>r.Contains(new Point(20,24))) && masks.All(r=>r.Width*r.Height<suspect.Width*suspect.Height);
        });
        Check("real popup pixels can be validated before the independent parent reference", () =>
        {
            var popup = new Rect(16, 20, 16, 16);
            var popupPixels = new byte[16*16*4];
            for (int y=0;y<16;y++) Buffer.BlockCopy(testPixels, ((y+8)*128+56)*4,popupPixels,y*16*4,16*4);
            return CaptureOverlayResolver.Resolve(testCrop,testPixels,suspect,[new(popup,popupPixels),reference]).Count==0;
        });
        Check("tall JPEG contains both full-width edge colours", () => EncodedEdges(900, 1600, Fixture(900, 1600)));
        Check("square JPEG contains both full-width edge colours", () => EncodedEdges(1000, 1000, Fixture(1000, 1000)));
        Check("right-side image coordinate maps to right physical screen", () =>
        {
            var image = new PixelSnapshot(0, window, window, 1600, 900, [], []);
            var viewport = ScreenCapture.ImageViewport(1600, 900);
            var box = ScreenCapture.MapBox(image, [900, viewport.Top + 100, 980, viewport.Top + 150]);
            return !box.IsEmpty && box.Left >= -160 && box.Right <= 0;
        });
        return results;
    }

    private static bool EncodedEdges(int width, int height, byte[] pixels)
    {
        var image = new PixelSnapshot(0, new Rect(0, 0, width, height), new Rect(0, 0, width, height), width, height, pixels, []);
        using var stream = new MemoryStream(image.EncodeJpeg());
        var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgr32, null, 0);
        var viewport = ScreenCapture.ImageViewport(width, height);
        var left = new byte[4]; var right = new byte[4];
        int middle = (int)(viewport.Top + viewport.Height / 2);
        frame.CopyPixels(new Int32Rect((int)viewport.Left + 5, middle, 1, 1), left, 4, 0);
        frame.CopyPixels(new Int32Rect((int)viewport.Right - 6, middle, 1, 1), right, 4, 0);
        return left[2] > 200 && left[0] < 50 && right[0] > 200 && right[2] < 50;
    }
}
