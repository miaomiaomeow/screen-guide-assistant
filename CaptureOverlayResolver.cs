using System.Windows;

namespace ScreenGuide;

// These are window-rendered references, not outgoing screenshots. The outgoing
// pixels always come from the current desktop. A missing/unsupported reference
// never authorizes the overlay's rectangular bounds to be silently ignored.
internal sealed record WindowPixelReference(Rect Bounds, byte[]? Pixels);

internal static class CaptureOverlayResolver
{
    internal const byte SentinelBlue = 0xA6, SentinelGreen = 0x57, SentinelRed = 0xE1;

    internal static IReadOnlyList<Rect> Resolve(Rect crop, byte[] desktop, Rect suspected,
        IReadOnlyList<WindowPixelReference> references)
    {
        var area = CaptureGeometry.Integral(Rect.Intersect(crop, suspected));
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0) return [];
        if (desktop.Length != crop.Width * crop.Height * 4 || references.Count == 0) return [area];
        const int tile = 8;
        var masks = new List<Rect>();
        for (int y = (int)area.Top; y < area.Bottom; y += tile)
        {
            int bottom = (int)Math.Min(area.Bottom, y + tile), start = -1;
            for (int x = (int)area.Left; x < area.Right; x += tile)
            {
                int right = (int)Math.Min(area.Right, x + tile);
                bool matches = true;
                for (int py = y; matches && py < bottom; py++)
                for (int px = x; matches && px < right; px++)
                    matches = Matches(crop, desktop, px, py, references);
                if (!matches && start < 0) start = x;
                if (start >= 0 && (matches || right == area.Right))
                {
                    int end = matches ? x : right;
                    // Join identical runs on adjacent rows, bounding mask growth.
                    int previous = masks.FindLastIndex(r => r.X == start && r.Right == end && r.Bottom == y);
                    if (previous >= 0) { var r = masks[previous]; masks[previous] = new(r.X, r.Y, r.Width, bottom - r.Y); }
                    else masks.Add(new(start, y, end - start, bottom - y));
                    start = -1;
                }
            }
            if (masks.Count > 4096) return [area];
        }
        // A genuinely covering overlay, animation, or unsupported print renderer
        // remains a full blocker; fragmented tiles must not evade the 35% policy.
        if (masks.Sum(r => r.Width * r.Height) >= area.Width * area.Height * .35) return [area];
        return masks;
    }

    private static bool Matches(Rect crop, byte[] desktop, int x, int y, IReadOnlyList<WindowPixelReference> references)
    {
        foreach (var reference in references)
        {
            var b = reference.Bounds;
            if (x < b.Left || x >= b.Right || y < b.Top || y >= b.Bottom) continue;
            // Topmost associated popup wins, including a failed popup render.
            // Never fall through it to the parent window's pixels underneath.
            var pixels = reference.Pixels;
            if (pixels == null || pixels.Length != b.Width * b.Height * 4) return false;
            int i = ((y - (int)b.Top) * (int)b.Width + x - (int)b.Left) * 4;
            int at = ((y - (int)crop.Top) * (int)crop.Width + x - (int)crop.Left) * 4;
            if (pixels[i] == SentinelBlue && pixels[i + 1] == SentinelGreen && pixels[i + 2] == SentinelRed) return false;
            return pixels[i] == desktop[at] && pixels[i + 1] == desktop[at + 1] && pixels[i + 2] == desktop[at + 2];
        }
        return false;
    }
}
