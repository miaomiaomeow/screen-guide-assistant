using System.Windows;

namespace ScreenGuide;

internal static class ImageFallbackPolicyCheck
{
    internal static IReadOnlyList<(string Label, bool Passed)> Checks()
    {
        var checks = new List<(string Label, bool Passed)>();
        void Check(string label, bool passed) => checks.Add((label, passed));
        foreach (var failure in Enum.GetValues<SelectionFailure>())
        {
            var decision = SelectionDecision.Refuse("fixture", failure);
            var reason = ImageFallbackPolicy.ForSelection(decision);
            var allowed = failure is SelectionFailure.NotFound or SelectionFailure.Ambiguous or SelectionFailure.LowConfidence;
            Check("only selected outcome categories can authorize image: " + failure, (reason != null) == allowed);
            if (reason is { } selectedReason)
            {
                var route = new GuideSession("fixture", 17, false, GuideMode.ImageOnControlFailure);
                Check("selected outcome keeps the existing conditional session: " + failure,
                    route.TryActivateImageFallback(selectedReason) && route.Question == "fixture" && route.Window == 17 && route.Count == 0);
            }
        }
        Check("a valid control point never authorizes an outcome image", ImageFallbackPolicy.ForSelection(
            new("point", "p1", 0.98, "fixture") { Failure = SelectionFailure.NotFound }) == null);
        Check("done never authorizes an outcome image", ImageFallbackPolicy.ForSelection(
            new("done", null, 0.98, "fixture") { Failure = SelectionFailure.Ambiguous }) == null);
        var entry = new ControlOption("p1", "设置", "Button", "ToolBar", true, false);
        var chrome = new ControlCandidate(entry, null!, new Rect(0, 0, 20, 20), 1, [1]) { IsWindowChrome = true };
        var shellOnly = new ControlSnapshot(17, new Rect(0, 0, 300, 200), new("fixture", false, [entry]), [chrome]);
        Check("nonempty title bar is not a readable application entry", shellOnly.Candidates.Count == 1 &&
            shellOnly.ApplicationCandidateCount == 0 && shellOnly.WindowChromeCandidateCount == 1);
        var business = chrome with { IsWindowChrome = false };
        Check("one real application entry remains usable even in a small tree", (shellOnly with { Candidates = [business] }).ApplicationCandidateCount == 1);
        Check("chrome plus application controls are counted separately", (shellOnly with { Candidates = [chrome, business] }) is
            { ApplicationCandidateCount: 1, WindowChromeCandidateCount: 1 });
        var controls = new[] { entry };
        Check("real parser not-found selects the authorized fallback", ImageFallbackPolicy.ForSelection(IntentMapper.Parse(
            "{\"action\":\"not_found\",\"id\":null,\"confidence\":0.9}", controls)) == ControlReadFailure.SelectionNotFound);
        Check("real parser ambiguity selects the authorized fallback", ImageFallbackPolicy.ForSelection(IntentMapper.Parse(
            "{\"action\":\"ambiguous\",\"id\":null,\"confidence\":0.9}", controls)) == ControlReadFailure.SelectionAmbiguous);
        Check("real parser low confidence selects the authorized fallback", ImageFallbackPolicy.ForSelection(IntentMapper.Parse(
            "{\"action\":\"point\",\"id\":\"p1\",\"confidence\":0.5}", controls)) == ControlReadFailure.SelectionLowConfidence);
        Check("malformed response cannot disguise itself as a no-match image request", ImageFallbackPolicy.ForSelection(IntentMapper.Parse(
            "not-json", controls)) == null);
        Check("invented control cannot authorize an image request", ImageFallbackPolicy.ForSelection(IntentMapper.Parse(
            "{\"action\":\"point\",\"id\":\"fake\",\"confidence\":0.9}", controls)) == null);
        Check("disabled control cannot authorize an image request", ImageFallbackPolicy.ForSelection(IntentMapper.Parse(
            "{\"action\":\"point\",\"id\":\"p1\",\"confidence\":0.9}", [entry with { Enabled = false }])) == null);
        Check("low score does not let an invented ID authorize images", ImageFallbackPolicy.ForSelection(IntentMapper.Parse(
            "{\"action\":\"point\",\"id\":\"fake\",\"confidence\":0.1}", controls)) == null);
        Check("low score does not let a disabled control authorize images", ImageFallbackPolicy.ForSelection(IntentMapper.Parse(
            "{\"action\":\"point\",\"id\":\"p1\",\"confidence\":0.1}", [entry with { Enabled = false }])) == null);
        Check("low score cannot turn an unsupported done claim into an image request", ImageFallbackPolicy.ForSelection(IntentMapper.Parse(
            "{\"action\":\"done\",\"id\":null,\"confidence\":0.1}", controls)) == null);
        var retained = new GuideSession("fixture", 17, false, GuideMode.ImageOnControlFailure);
        var frame = new GuidanceFrame(1, new(17, new Rect(0, 0, 300, 200), null, new Rect(0, 0, 20, 20), 17, [1], "设置", "Button"),
            entry, "open_entry", new HashSet<string>());
        retained.Record(frame, "clicked");
        Check("repeat fallback preserves actual progress instead of inventing a click", retained.TryActivateImageFallback(ControlReadFailure.RepeatedEntry)
            && retained.Count == 1 && retained.Memory.Interactions.Single().Event == "clicked");
        Check("an image failure cannot repeatedly reactivate the fallback", !retained.TryActivateImageFallback(ControlReadFailure.SelectionNotFound));
        return checks;
    }
}
