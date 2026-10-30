using System.Windows;

namespace ScreenGuide;

// All rectangles here use physical desktop pixels, including negative monitor origins.
internal static class CaptureGeometry
{
    internal const long LayeredStyle = 0x00080000;
    internal const uint AlphaFlag = 0x00000002;

    internal static bool IsProvenInvisible(long style, bool attributesKnown, byte alpha, uint flags) =>
        (style & LayeredStyle) != 0 && attributesKnown && (flags & AlphaFlag) != 0 && alpha == 0;

    internal static Rect VisibleFrame(Rect bounds, Rect? frame) =>
        frame is { IsEmpty: false, Width: > 0, Height: > 0 } visible && bounds.Contains(visible) ? visible : bounds;

    internal static Rect Crop(Rect windowBounds, Rect? frame, IEnumerable<Rect> popups, Rect desktop)
    {
        var crop = VisibleFrame(windowBounds, frame);
        foreach (var popup in popups) if (!popup.IsEmpty && popup.Width > 0 && popup.Height > 0) crop.Union(popup);
        crop.Intersect(desktop);
        return Integral(crop);
    }

    // null means no readable Win32 region: conservatively use the visible frame.
    // An empty list means a successfully read NULLREGION: no pixels can be drawn.
    internal static IReadOnlyList<Rect> OccluderRegions(Rect crop, Rect bounds, Rect? frame,
        IReadOnlyList<Rect>? relativeRegions, bool invisible)
    {
        if (invisible) return [];
        var visible = VisibleFrame(bounds, frame);
        if (relativeRegions == null)
        {
            var overlap = Rect.Intersect(crop, visible);
            return overlap.IsEmpty || overlap.Width <= 0 || overlap.Height <= 0 ? [] : [overlap];
        }
        var result = new List<Rect>();
        foreach (var local in relativeRegions)
        {
            if (local.IsEmpty || local.Width <= 0 || local.Height <= 0) continue;
            var screen = new Rect(bounds.Left + local.Left, bounds.Top + local.Top, local.Width, local.Height);
            screen.Intersect(visible); screen.Intersect(crop);
            if (!screen.IsEmpty && screen.Width > 0 && screen.Height > 0) result.Add(screen);
        }
        return result;
    }

    internal static bool SubstantiallyObscured(Rect crop, IEnumerable<Rect> masks) =>
        !crop.IsEmpty && masks.Any(mask => !mask.IsEmpty &&
            mask.Width * mask.Height >= crop.Width * crop.Height * 0.35);

    internal static Rect Integral(Rect rect) => rect.IsEmpty ? Rect.Empty :
        new(Math.Ceiling(rect.Left), Math.Ceiling(rect.Top), Math.Max(0, Math.Floor(rect.Right) - Math.Ceiling(rect.Left)), Math.Max(0, Math.Floor(rect.Bottom) - Math.Ceiling(rect.Top)));
}
