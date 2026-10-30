using System.Windows;

namespace ScreenGuide;

internal static class ShellCheck
{
    internal static IEnumerable<(string Label, bool Passed)> Run()
    {
        var area = new Rect(100, 50, 800, 600);
        var left = EdgeDock.OrbRect(area, 300, DockSide.Left, hidden: false);
        var right = EdgeDock.OrbRect(area, 300, DockSide.Right, hidden: false);
        yield return ("悬浮球完全露出时贴住左右工作区边缘", left == new Rect(100, 276, 48, 48)
            && right == new Rect(852, 276, 48, 48));

        var hiddenLeft = EdgeDock.OrbRect(area, 300, DockSide.Left, hidden: true);
        yield return ("左侧半藏只在工作区露出24 DIP", Equal(Rect.Intersect(area, hiddenLeft).Width, 24)
            && Equal(hiddenLeft.Left, 76) && Equal(hiddenLeft.Width, 48));
        var hiddenRight = EdgeDock.OrbRect(area, 300, DockSide.Right, hidden: true);
        yield return ("右侧半藏只在工作区露出24 DIP", Equal(Rect.Intersect(area, hiddenRight).Width, 24)
            && Equal(hiddenRight.Left, 876) && Equal(hiddenRight.Width, 48));

        var negativeArea = new Rect(-1920, -200, 1920, 1080);
        yield return ("负坐标屏幕保持自身原点", EdgeDock.OrbRect(negativeArea, 100, DockSide.Left, true)
            == new Rect(-1944, 76, 48, 48)
            && EdgeDock.OrbRect(negativeArea, 100, DockSide.Right, true) == new Rect(-24, 76, 48, 48));
        yield return ("球心超出顶部时夹紧到工作区内", Equal(EdgeDock.ClampAnchorY(negativeArea, -10000), -176)
            && Equal(EdgeDock.OrbRect(negativeArea, -10000, DockSide.Left, false).Top, -200));
        yield return ("球心超出底部时夹紧到工作区内", Equal(EdgeDock.ClampAnchorY(negativeArea, 10000), 856)
            && Equal(EdgeDock.OrbRect(negativeArea, 10000, DockSide.Right, false).Bottom, 880));
        yield return ("松开位置选择最近侧且中心平局选左", EdgeDock.NearestSide(area, new Point(-100, 200)) == DockSide.Left
            && EdgeDock.NearestSide(area, new Point(1500, 200)) == DockSide.Right
            && EdgeDock.NearestSide(area, new Point(500, 200)) == DockSide.Left);

        var leftCard = EdgeDock.CardRect(area, left, new Size(300, 180), gap: 8);
        yield return ("左侧卡片向内展开并保留间距", area.Contains(leftCard) && Equal(leftCard.Left, left.Right + 8));
        var rightCard = EdgeDock.CardRect(area, right, new Size(300, 180), gap: 8);
        yield return ("右侧卡片向内展开并保留间距", area.Contains(rightCard) && Equal(rightCard.Right, right.Left - 8));
        var topCard = EdgeDock.CardRect(area, EdgeDock.OrbRect(area, -1000, DockSide.Left, false), new Size(300, 200));
        var bottomCard = EdgeDock.CardRect(area, EdgeDock.OrbRect(area, 1000, DockSide.Right, false), new Size(300, 200));
        yield return ("卡片靠近上下边缘仍完整在工作区内", area.Contains(topCard) && area.Contains(bottomCard)
            && Equal(topCard.Top, area.Top) && Equal(bottomCard.Bottom, area.Bottom));
        yield return ("过大的卡片缩到当前工作区尺寸", EdgeDock.CardRect(area, hiddenLeft, new Size(2000, 2000)) == area);
        var tinyArea = new Rect(-60, -20, 64, 40);
        var tinyOrb = EdgeDock.OrbRect(tinyArea, 100, DockSide.Left, false);
        yield return ("极矮工作区保持48 DIP球居中且卡片收缩", tinyOrb == new Rect(-60, -24, 48, 48)
            && EdgeDock.CardRect(tinyArea, tinyOrb, new Size(300, 200)) == tinyArea);

        yield return ("近球64 DIP边界包含而边界外排除", EdgeDock.NearOrb(new Point(left.Right + 64, 300), left)
            && !EdgeDock.NearOrb(new Point(left.Right + 64.001, 300), left));
        yield return ("近球角落按距离判断而非方框扩张", EdgeDock.NearOrb(new Point(left.Right + 40, left.Bottom + 40), left)
            && !EdgeDock.NearOrb(new Point(left.Right + 64, left.Bottom + 64), left));

        var start = new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
        var idle = new EdgeDockIdlePolicy();
        bool initiallyVisible = !idle.Update(start, false, false, false);
        bool beforeDeadline = !idle.Update(start.AddMilliseconds(2999), false, false, false);
        bool atDeadline = idle.Update(start.AddSeconds(3), false, false, false);
        yield return ("离开后不足3秒不藏且3秒整半藏", initiallyVisible && beforeDeadline && atDeadline && idle.IsHidden);
        yield return ("靠近已半藏球立即露出", !idle.Update(start.AddSeconds(4), true, false, false) && !idle.IsHidden);
        bool restarted = !idle.Update(start.AddSeconds(5), false, false, false);
        bool restartedBefore = !idle.Update(start.AddMilliseconds(7999), false, false, false);
        bool restartedAfter = idle.Update(start.AddSeconds(8), false, false, false);
        yield return ("靠近后再次离开重新计算完整3秒", restarted && restartedBefore && restartedAfter);
        yield return ("输入框打开时露出并阻止半藏", KeepsVisible(start, inputOpen: true));
        yield return ("控制面板打开时露出并阻止半藏", KeepsVisible(start, controlsOpen: true));
        yield return ("拖动期间露出并阻止半藏", KeepsVisible(start, dragging: true));
        var clockChange = new EdgeDockIdlePolicy();
        clockChange.Update(start, false, false, false);
        bool hiddenBeforeClockChange = clockChange.Update(start.AddSeconds(3), false, false, false);
        bool visibleAfterClockChange = !clockChange.Update(start.AddSeconds(-5), false, false, false);
        bool hiddenAfterNewDelay = clockChange.Update(start.AddSeconds(-2), false, false, false);
        yield return ("时钟回拨时重新等待而非沿用旧隐藏", hiddenBeforeClockChange && visibleAfterClockChange && hiddenAfterNewDelay);

        var bubble = new EdgeDockIdlePolicy();
        bubble.ShowBubble(start, TimeSpan.FromSeconds(8));
        yield return ("提示气泡8秒前可见且到期立即消失", bubble.IsBubbleVisible(start)
            && bubble.IsBubbleVisible(start.AddMilliseconds(7999)) && !bubble.IsBubbleVisible(start.AddSeconds(8)));
        bubble.ShowBubble(start.AddSeconds(10), TimeSpan.FromSeconds(8));
        bubble.DismissBubble();
        yield return ("清除气泡立即取消显示及到期状态", !bubble.IsBubbleVisible(start.AddSeconds(11)) && bubble.BubbleExpiresAt is null);
        yield return ("几何拒绝空工作区及无效尺寸坐标", Rejects(() => EdgeDock.OrbRect(Rect.Empty, 100, DockSide.Left, false))
            && Rejects(() => EdgeDock.OrbRect(area, double.NaN, DockSide.Left, false))
            && Rejects(() => EdgeDock.OrbRect(area, 100, (DockSide)100, false))
            && Rejects(() => EdgeDock.CardRect(area, left, new Size(0, 100)))
            && Rejects(() => EdgeDock.CardRect(area, left, new Size(100, 100), -1))
            && Rejects(() => EdgeDock.NearOrb(new Point(double.PositiveInfinity, 0), left))
            && Rejects(() => EdgeDock.NearOrb(new Point(100, 100), left, -1)));
        yield return ("气泡拒绝零负时长及日期溢出", Rejects(() => bubble.ShowBubble(start, TimeSpan.Zero))
            && Rejects(() => bubble.ShowBubble(start, TimeSpan.FromSeconds(-1)))
            && Rejects(() => bubble.ShowBubble(DateTimeOffset.MaxValue, TimeSpan.FromSeconds(1))));
    }

    private static bool KeepsVisible(DateTimeOffset start, bool inputOpen = false, bool controlsOpen = false, bool dragging = false)
    {
        var policy = new EdgeDockIdlePolicy();
        policy.Update(start, false, false, false);
        bool wasHidden = policy.Update(start.AddSeconds(3), false, false, false);
        bool opensVisible = !policy.Update(start.AddSeconds(10), false, inputOpen, controlsOpen, dragging);
        bool staysVisible = !policy.Update(start.AddSeconds(20), false, inputOpen, controlsOpen, dragging);
        return wasHidden && opensVisible && staysVisible && !policy.IsHidden;
    }

    private static bool Equal(double actual, double expected) => Math.Abs(actual - expected) < 0.000001;

    private static bool Rejects(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return true; }
        catch { return false; }
        return false;
    }
}
