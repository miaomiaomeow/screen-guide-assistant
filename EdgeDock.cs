using System.Windows;

namespace ScreenGuide;

internal enum DockSide { Left, Right }

internal readonly record struct DockGeometry(Rect Orb, Rect Card);

// Coordinates are screen DIPs supplied by the caller, including negative
// monitor origins. This helper knows nothing about target applications or DPI.
internal static class EdgeDock
{
    internal const double OrbDiameter = 48;
    internal const double ExposedWidth = 24;
    internal const double NearMargin = 64;

    // anchorY is the orb's center, not its top edge.
    internal static double ClampAnchorY(Rect workArea, double anchorY)
    {
        ValidateWorkArea(workArea);
        RequireFinite(anchorY, nameof(anchorY));
        if (workArea.Height < OrbDiameter)
            return workArea.Top + workArea.Height / 2;
        return Math.Clamp(anchorY, workArea.Top + OrbDiameter / 2, workArea.Bottom - OrbDiameter / 2);
    }

    internal static Rect OrbRect(Rect workArea, double anchorY, DockSide side, bool hidden)
    {
        double centerY = ClampAnchorY(workArea, anchorY);
        double left = side switch
        {
            DockSide.Left => workArea.Left - (hidden ? OrbDiameter - ExposedWidth : 0),
            DockSide.Right => workArea.Right - (hidden ? ExposedWidth : OrbDiameter),
            _ => throw new ArgumentOutOfRangeException(nameof(side))
        };
        return new Rect(left, centerY - OrbDiameter / 2, OrbDiameter, OrbDiameter);
    }

    internal static DockSide NearestSide(Rect workArea, Point point)
    {
        ValidateWorkArea(workArea);
        ValidatePoint(point);
        return point.X <= workArea.Left + workArea.Width / 2 ? DockSide.Left : DockSide.Right;
    }

    // Cards open toward the work area's interior and fit even when the caller's
    // preferred card size exceeds this monitor's available space.
    internal static Rect CardRect(Rect workArea, Rect orbRect, Size cardSize, double gap = 8)
    {
        ValidateWorkArea(workArea);
        ValidateRect(orbRect, nameof(orbRect));
        if (cardSize.IsEmpty || !double.IsFinite(cardSize.Width) || !double.IsFinite(cardSize.Height)
            || cardSize.Width <= 0 || cardSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(cardSize));
        if (!double.IsFinite(gap) || gap < 0)
            throw new ArgumentOutOfRangeException(nameof(gap));

        double width = Math.Min(cardSize.Width, workArea.Width);
        double height = Math.Min(cardSize.Height, workArea.Height);
        var center = new Point(orbRect.Left + orbRect.Width / 2, orbRect.Top + orbRect.Height / 2);
        double left = NearestSide(workArea, center) == DockSide.Left
            ? orbRect.Right + gap
            : orbRect.Left - gap - width;
        double top = center.Y - height / 2;
        return new Rect(Math.Clamp(left, workArea.Left, workArea.Right - width),
            Math.Clamp(top, workArea.Top, workArea.Bottom - height), width, height);
    }

    internal static DockGeometry Layout(Rect workArea, double anchorY, DockSide side, bool hidden,
        Size cardSize, double gap = 8)
    {
        Rect orb = OrbRect(workArea, anchorY, side, hidden);
        return new DockGeometry(orb, CardRect(workArea, orb, cardSize, gap));
    }

    // Euclidean distance to the current orb rectangle, including corners.
    // This avoids an inflated rectangle accepting points farther than margin.
    internal static bool NearOrb(Point point, Rect orbRect, double margin = NearMargin)
    {
        ValidatePoint(point);
        ValidateRect(orbRect, nameof(orbRect));
        if (!double.IsFinite(margin) || margin < 0)
            throw new ArgumentOutOfRangeException(nameof(margin));
        double dx = Math.Max(Math.Max(orbRect.Left - point.X, point.X - orbRect.Right), 0);
        double dy = Math.Max(Math.Max(orbRect.Top - point.Y, point.Y - orbRect.Bottom), 0);
        return Math.Sqrt(dx * dx + dy * dy) <= margin;
    }

    private static void ValidateWorkArea(Rect workArea) => ValidateRect(workArea, nameof(workArea));

    private static void ValidateRect(Rect rect, string name)
    {
        if (rect.IsEmpty || !double.IsFinite(rect.Left) || !double.IsFinite(rect.Top)
            || !double.IsFinite(rect.Right) || !double.IsFinite(rect.Bottom)
            || rect.Width <= 0 || rect.Height <= 0)
            throw new ArgumentOutOfRangeException(name);
    }

    private static void ValidatePoint(Point point)
    {
        RequireFinite(point.X, nameof(point));
        RequireFinite(point.Y, nameof(point));
    }

    private static void RequireFinite(double value, string name)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(name);
    }
}

// Explicit timestamps make idle and bubble behavior testable without a timer,
// desktop, mouse hooks, or calls to the system clock.
internal sealed class EdgeDockIdlePolicy
{
    internal static readonly TimeSpan HideDelay = TimeSpan.FromSeconds(3);
    private DateTimeOffset? _idleSince;
    internal bool IsHidden { get; private set; }
    internal DateTimeOffset? BubbleExpiresAt { get; private set; }

    internal bool Update(DateTimeOffset now, bool nearOrb, bool inputOpen, bool controlsOpen, bool dragging = false)
    {
        if (nearOrb || inputOpen || controlsOpen || dragging)
        {
            _idleSince = null;
            IsHidden = false;
        }
        else
        {
            // A backward wall-clock adjustment restarts the quiet period.
            if (_idleSince is null || now < _idleSince.Value) _idleSince = now;
            IsHidden = now - _idleSince.Value >= HideDelay;
        }
        return IsHidden;
    }

    internal void ShowBubble(DateTimeOffset now, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > DateTimeOffset.MaxValue - now)
            throw new ArgumentOutOfRangeException(nameof(duration));
        BubbleExpiresAt = now + duration;
    }

    internal bool IsBubbleVisible(DateTimeOffset now) => BubbleExpiresAt is { } expiry && now < expiry;

    internal void DismissBubble() => BubbleExpiresAt = null;
}
