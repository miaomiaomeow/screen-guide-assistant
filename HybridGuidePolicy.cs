using System.Windows;

namespace ScreenGuide;

internal static class HybridGuidePolicy
{
    internal static bool OverlapsDisabledEntry(ControlSnapshot? controls, Rect bounds) =>
        controls != null && !bounds.IsEmpty && controls.Candidates.Any(c => !c.IsWindowChrome && !c.Option.Enabled &&
            !c.Bounds.IsEmpty && c.Bounds.Contains(new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2)) &&
            IntersectionArea(c.Bounds, bounds) >= bounds.Width * bounds.Height * 0.5);

    private static double IntersectionArea(Rect a, Rect b)
    {
        var overlap = Rect.Intersect(a, b);
        return overlap.IsEmpty ? 0 : overlap.Width * overlap.Height;
    }
}
