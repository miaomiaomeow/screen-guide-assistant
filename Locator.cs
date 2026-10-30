using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;

namespace ScreenGuide;

internal static class Locator
{
    internal static ControlSnapshot Capture(nint hwnd, List<object>? diagnostics = null)
    {
        if (!Native.CanTarget(hwnd)) throw new InvalidOperationException("目标窗口不可见，请先恢复窗口。");
        Native.GetWindowRect(hwnd, out var windowRect);
        var options = new List<ControlCandidate>();
        var roleCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var rejectedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        static void Count(Dictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
        void Reject(string reason) => Count(rejectedCounts, reason);
        var seen = new HashSet<string>();
        var prefix = "c" + Guid.NewGuid().ToString("N")[..6];
        var watch = Stopwatch.StartNew();
        var walker = TreeWalker.ControlViewWalker;
        int nodes = 0, metadataCharacters = 0; bool partial = false; string partialReason = "none";
        // Breadth-first traversal reaches toolbars before large document/file-list subtrees.
        var pending = new Queue<(AutomationElement Element, int Depth, string Context, LocatorScope Scope)>();
        var popups = Native.OwnedPopups(hwnd);
        foreach (var popup in popups)
        {
            try { pending.Enqueue((AutomationElement.FromHandle(popup), 0, "Menu", default)); }
            catch { partial = true; partialReason = "popup-unreadable"; Reject("provider-error"); }
        }
        pending.Enqueue((AutomationElement.FromHandle(hwnd), 0, "", default));
        while (pending.TryDequeue(out var node))
        {
            var (element, depth, context, scope) = node;
            if (++nodes > 3000 || options.Count >= 220 || watch.ElapsedMilliseconds > 1600)
            { partial = true; partialReason = nodes > 3000 ? "node-budget" : options.Count >= 220 ? "candidate-budget" : "time-budget"; break; }
            ControlType type;
            string nextContext = context;
            try
            {
                type = element.Current.ControlType;
                string role = Role(type);
                Count(roleCounts, role);
                scope = LocatorPolicy.Enter(scope, role);
                if (element.Current.IsPassword) { Reject("password"); continue; }
                // Never read Name/Value/TextPattern on editing or document/text nodes.
                if (type == ControlType.Edit) { Reject("edit-subtree"); continue; }
                if (diagnostics != null && type == ControlType.Menu && diagnostics.Count < 80)
                {
                    var owner = walker.GetParent(element); string expansion = "unavailable";
                    if (owner != null && owner.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ownerPattern))
                        expansion = ((ExpandCollapsePattern)ownerPattern).Current.ExpandCollapseState.ToString();
                    diagnostics.Add(new { menu = SafeLabel(element.Current.Name), offscreen = element.Current.IsOffscreen,
                        bounds = element.Current.BoundingRectangle.ToString(System.Globalization.CultureInfo.InvariantCulture), expansion,
                        ownerRole = owner?.Current.ControlType.ProgrammaticName,
                        ownerBounds = owner?.Current.BoundingRectangle.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                }
                if (LocatorPolicy.IsCandidateRole(role))
                {
                    bool expandable = false;
                    // Reject document trees before reading their labels or patterns. Ordinary
                    // entry roles keep the old cheap visibility checks before pattern reads.
                    string? roleRejection = LocatorPolicy.CandidateRejection(role, scope, expandable: true);
                    if (roleRejection == null && role == "TreeItem")
                    {
                        try { expandable = CanExpand(element); }
                        catch { Reject("expand-pattern-error"); }
                        roleRejection = LocatorPolicy.CandidateRejection(role, scope, expandable);
                    }
                    if (roleRejection != null) Reject(roleRejection);
                    else
                    {
                        var bounds = element.Current.BoundingRectangle;
                        var name = SafeLabel(element.Current.Name);
                        if (diagnostics != null && type == ControlType.MenuItem && diagnostics.Count < 80)
                            diagnostics.Add(new { name, offscreen = element.Current.IsOffscreen,
                                bounds = bounds.ToString(System.Globalization.CultureInfo.InvariantCulture), visibleInOpenMenu = OpenMenuContains(element, bounds) });
                        if (name.Length == 0) Reject("empty-label");
                        else if (!Valid(bounds)) Reject("invalid-bounds");
                        else if (!Native.PointIsTarget(bounds, hwnd)) Reject("occluded");
                        else if (!Visible(element, bounds)) Reject("offscreen");
                        else
                        {
                            var runtimeId = element.GetRuntimeId();
                            if (seen.Add(string.Join(".", runtimeId)))
                            {
                                if (role != "TreeItem")
                                {
                                    try { expandable = CanExpand(element); }
                                    catch { Reject("expand-pattern-error"); }
                                }
                                if (metadataCharacters + name.Length + context.Length + 120 > 24000) { partial = true; partialReason = "metadata-budget"; break; }
                                metadataCharacters += name.Length + context.Length + 120;
                                var option = new ControlOption($"{prefix}-{options.Count + 1:D3}", name, role, context,
                                    element.Current.IsEnabled, expandable);
                                options.Add(new(option, element, bounds, Native.PointWindow(bounds), runtimeId)
                                    { IsWindowChrome = scope.InTitleBar });
                            }
                            else Reject("duplicate");
                        }
                    }
                }
                else Reject("non-entry-role");
                // Structural labels help distinguish repeated names. Window/document titles are excluded.
                if (type == ControlType.MenuBar || type == ControlType.Menu || type == ControlType.ToolBar || type == ControlType.Group)
                {
                    var label = SafeLabel(element.Current.Name);
                    nextContext = ShortContext(context, Role(type) + (label.Length > 0 ? ":" + label : ""));
                }
            }
            catch { partial = true; if (partialReason == "none") partialReason = "provider-error"; Reject("provider-error"); continue; }

            if (depth >= 32) { partial = true; partialReason = "depth-budget"; continue; }
            AutomationElement? child;
            try { child = walker.GetFirstChild(element); }
            catch { partial = true; if (partialReason == "none") partialReason = "provider-error"; Reject("provider-error"); continue; }
            int siblings = 0;
            while (child != null)
            {
                if (++siblings > 128 || pending.Count >= 4000 || watch.ElapsedMilliseconds > 1600)
                { partial = true; partialReason = siblings > 128 ? "branch-budget" : pending.Count >= 4000 ? "queue-budget" : "time-budget"; break; }
                pending.Enqueue((child, depth + 1, nextContext, scope));
                try { child = walker.GetNextSibling(child); }
                catch { partial = true; if (partialReason == "none") partialReason = "provider-error"; Reject("provider-error"); break; }
            }
        }
        Native.GetWindowRect(hwnd, out var after);
        if (!Native.CanTarget(hwnd) || !Native.SameRect(windowRect.ToRect(), after.ToRect()))
            throw new InvalidOperationException("窗口已移动或隐藏，请重新指路。");
        var modelContext = new SelectionContext(Native.AppName(hwnd), partial, options.Select(c => c.Option).ToArray());
        return new(hwnd, windowRect.ToRect(), modelContext, options)
        {
            PopupCount = popups.Count, NodesRead = nodes, ElapsedMs = (int)watch.ElapsedMilliseconds, PartialReason = partialReason,
            RoleCounts = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(roleCounts),
            RejectedCounts = new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(rejectedCounts)
        };
    }

    internal static SelectionDecision Manual(SelectionContext context, string name)
    {
        var matches = context.Controls.Where(c => c.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) return SelectionDecision.Refuse("没有同名的可见控件；手动模式需要输入控件原名。", SelectionFailure.NotFound);
        if (matches.Length > 1) return SelectionDecision.Refuse("存在多个同名控件，手动模式无法唯一确定；请改用模型或先展开目标区域。", SelectionFailure.Ambiguous);
        if (!matches[0].Enabled) return SelectionDecision.Refuse("这个控件当前已禁用。", SelectionFailure.Disabled);
        return new("point", matches[0].Id, 1, "手动定位，不调用模型。");
    }

    internal static LocateResult Confirm(ControlSnapshot snapshot, SelectionDecision decision)
    {
        if (!decision.CanPoint) return new(null, decision.Message);
        var matches = snapshot.Candidates.Where(c => c.Option.Id == decision.Id).ToArray();
        if (matches.Length != 1) return new(null, "候选编号无效或不唯一，未显示高亮。");
        var c = matches[0];
        try
        {
            Native.GetWindowRect(snapshot.Window, out var wr);
            if (!Native.CanTarget(snapshot.Window) || !Native.SameRect(wr.ToRect(), snapshot.WindowBounds))
                return new(null, "目标窗口已变化，请重新指路。");
            var bounds = c.Element.Current.BoundingRectangle;
            if (!Visible(c.Element, bounds) || c.Element.Current.IsPassword || !c.Element.Current.IsEnabled ||
                SafeLabel(c.Element.Current.Name) != c.Option.Name || Role(c.Element.Current.ControlType) != c.Option.Role ||
                !c.Element.GetRuntimeId().SequenceEqual(c.RuntimeId) || !Valid(bounds) || !Native.SameRect(bounds, c.Bounds) ||
                (c.Option.Role == "TreeItem" && !CanExpand(c.Element)))
                return new(null, "所选控件已变化、消失或不可用，请重新指路。");
            if (!Native.PointIsTarget(bounds, snapshot.Window) || Native.PointWindow(bounds) != c.HitWindow)
                return new(null, "所选控件被遮挡或菜单已收起，请重新指路。");
            var located = new LocatedControl(snapshot.Window, wr.ToRect(), c.Element, bounds, c.HitWindow,
                c.RuntimeId, c.Option.Name, c.Option.Role);
            var message = decision.Action == "open_entry"
                ? decision.Confidence < 0.85
                    ? $"可先查看「{c.Option.Name}」，下一层尚未确认；打开后自动核实。"
                    : $"请打开「{c.Option.Name}」；点击或悬停展开后自动读取下一步。"
                : $"已指出「{c.Option.Name}」；请自己点击，随后自动核对下一步。";
            if (snapshot.Context.Partial) message += "（本次只读到部分控件）";
            return new(located, message);
        }
        catch { return new(null, "所选控件已不可读，请重新指路。") { ReadFailed = true }; }
    }

    internal static bool StillValid(LocatedControl c)
    {
        if (c.ImageProof is { } proof) return ScreenCapture.StillValid(proof);
        if (c.Element == null) return false;
        try { return Native.CanTarget(c.Window) && Visible(c.Element, c.Bounds) && c.Element.Current.IsEnabled &&
            !c.Element.Current.IsPassword && SafeLabel(c.Element.Current.Name) == c.Name &&
            Role(c.Element.Current.ControlType) == c.Role &&
            c.Element.GetRuntimeId().SequenceEqual(c.RuntimeId) && Native.SameRect(c.Bounds, c.Element.Current.BoundingRectangle) &&
            Native.PointWindow(c.Bounds) == c.HitWindow && (c.Role != "TreeItem" || CanExpand(c.Element)); }
        catch { return false; }
    }
    internal static string SafeLabel(string? name) => string.IsNullOrWhiteSpace(name) || name.Length > 160 || name.Contains('\n') || name.Contains('\r') ? "" : name.Trim();
    private static bool CanExpand(AutomationElement element) =>
        element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern) &&
        ((ExpandCollapsePattern)pattern).Current.ExpandCollapseState is ExpandCollapseState.Collapsed or
            ExpandCollapseState.Expanded or ExpandCollapseState.PartiallyExpanded;
    private static bool Visible(AutomationElement element, Rect bounds) =>
        !element.Current.IsOffscreen || OpenMenuContains(element, bounds) || HitMatches(element, bounds);
    private static bool OpenMenuContains(AutomationElement element, Rect bounds)
    {
        try
        {
            // Overflow menus can extend beyond their menu-bar entry. Some UIA providers clip
            // IsOffscreen to that entry, even though the live Menu container is visible.
            // Require the whole real row inside that visible Menu; never infer a hidden item.
            if (!Valid(bounds) || element.Current.ControlType != ControlType.MenuItem) return false;
            var walker = TreeWalker.ControlViewWalker;
            var menu = walker.GetParent(element);
            if (menu == null || menu.Current.ControlType != ControlType.Menu || !MenuVisible(menu, 0)) return false;
            var menuBounds = menu.Current.BoundingRectangle;
            if (!Valid(menuBounds) || !menuBounds.Contains(bounds)) return false;
            return Native.PointWindow(menuBounds) == Native.PointWindow(bounds);
        }
        catch { return false; }
    }
    private static bool MenuVisible(AutomationElement menu, int depth)
    {
        if (depth > 4) return false;
        if (!menu.Current.IsOffscreen) return true;
        var bounds = menu.Current.BoundingRectangle;
        if (!Valid(bounds)) return false;
        var parent = TreeWalker.ControlViewWalker.GetParent(menu);
        if (parent == null) return false;
        if (parent.Current.ControlType == ControlType.MenuItem &&
            parent.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var pattern) &&
            ((ExpandCollapsePattern)pattern).Current.ExpandCollapseState == ExpandCollapseState.Expanded) return true;
        if (parent.Current.ControlType != ControlType.Menu || !MenuVisible(parent, depth + 1)) return false;
        // Chromium can expose a shown submenu as a child of the containing Menu, not its
        // initiating row, and clip its offscreen flag to that parent. Require adjacent,
        // overflowing real menu rectangles and the same actual native hit window.
        var parentBounds = parent.Current.BoundingRectangle;
        bool adjacent = Math.Abs(bounds.Left - parentBounds.Right) <= 4 || Math.Abs(bounds.Right - parentBounds.Left) <= 4;
        bool overlapsHeight = bounds.Top < parentBounds.Bottom && bounds.Bottom > parentBounds.Top;
        return Valid(parentBounds) && adjacent && overlapsHeight && !parentBounds.Contains(bounds) &&
            Native.PointWindow(bounds) == Native.PointWindow(parentBounds);
    }
    private static bool HitMatches(AutomationElement element, Rect bounds)
    {
        if (!Valid(bounds)) return false;
        try
        {
            // Some providers misreport menu rows as offscreen. A live UIA hit must resolve to
            // THIS element (or its child), not just to a window at the same coordinates.
            var identity = element.GetRuntimeId();
            var hit = AutomationElement.FromPoint(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
            for (int depth = 0; hit != null && depth < 5; depth++, hit = TreeWalker.RawViewWalker.GetParent(hit))
                if (hit.GetRuntimeId().SequenceEqual(identity)) return true;
        }
        catch { }
        return false;
    }
    private static string Role(ControlType type) => type.ProgrammaticName.Replace("ControlType.", "");
    private static string ShortContext(string previous, string next)
    {
        var combined = previous.Length > 0 ? previous + " > " + next : next;
        return combined.Length <= 180 ? combined : next;
    }
    private static bool Valid(Rect r) => !r.IsEmpty && r.Width > 2 && r.Height > 2 &&
        double.IsFinite(r.X) && double.IsFinite(r.Y) && double.IsFinite(r.Width) && double.IsFinite(r.Height);
}
