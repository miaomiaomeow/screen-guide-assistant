using System.Windows;

namespace ScreenGuide;

internal static class HybridGuidePolicyCheck
{
    internal static IEnumerable<(string Label, bool Passed)> Checks()
    {
        var hybrid = new GuideSession("fixture", 1, false, GuideMode.Hybrid);
        yield return ("hybrid always reads controls and images", hybrid.ReadsControls && hybrid.Images);
        yield return ("hybrid cannot activate a second image fallback", !hybrid.TryActivateImageFallback(ControlReadFailure.EmptyControls));
        var direct = new GuideSession("fixture", 1, false, GuideMode.DirectImage);
        yield return ("direct image does not read controls", direct.Images && !direct.ReadsControls);
        var conditional = new GuideSession("fixture", 1, false, GuideMode.ImageOnControlFailure);
        yield return ("conditional route initially reads only controls", conditional.ReadsControls && !conditional.Images);
        conditional.TryActivateImageFallback(ControlReadFailure.EmptyControls);
        yield return ("conditional route preserves image-only continuation", conditional.Images && !conditional.ReadsControls);
        var manual = new GuideSession("fixture", 1, true, GuideMode.Hybrid);
        yield return ("manual overrides hybrid without pictures", manual.Mode == GuideMode.Controls && !manual.Images);
        var disabled = new ControlCandidate(new("disabled", "Disabled", "MenuItem", "Menu", false, false), null!, new(100,100,200,30), 1, []);
        var snapshot = new ControlSnapshot(1, new(0,0,500,400), new("fixture",false,[disabled.Option]), [disabled]);
        yield return ("hybrid image cannot bypass known disabled menu item", HybridGuidePolicy.OverlapsDisabledEntry(snapshot, new(110,101,180,25)));
        yield return ("hybrid disabled item does not block other image entries", !HybridGuidePolicy.OverlapsDisabledEntry(snapshot, new(110,150,180,25)));
        yield return ("hybrid permits picture when controls unavailable", !HybridGuidePolicy.OverlapsDisabledEntry(null, new(110,101,180,25)));
        var enabled = disabled with { Option = disabled.Option with { Enabled = true } };
        yield return ("hybrid image does not reject enabled entries", !HybridGuidePolicy.OverlapsDisabledEntry(snapshot with { Candidates = [enabled] }, new(110,101,180,25)));
        yield return ("disabled window chrome does not override picture entries", !HybridGuidePolicy.OverlapsDisabledEntry(snapshot with { Candidates = [disabled with { IsWindowChrome = true }] }, new(110,101,180,25)));
        var baseline = new PixelSnapshot(1, snapshot.WindowBounds, snapshot.WindowBounds, 500, 400, [], []);
        var realControl = new LocatedControl(1, snapshot.WindowBounds, null!, disabled.Bounds, 1, [], "Menu", "MenuItem");
        var frame = new GuidanceFrame(1, realControl, enabled.Option, "open_entry", new HashSet<string>()) { HoverImage = baseline };
        yield return ("hybrid hover baseline preserves real-control highlight route", frame.HoverImage == baseline && frame.Control.ImageProof == null);
        yield return ("hybrid unexposed hover menu records only observed pixel change", hybrid.Record(frame, "hover_changed") && hybrid.Memory.Interactions[^1].Event == "hover_changed");
    }
}
