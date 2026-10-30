using System.Text.Json;

namespace ScreenGuide;

// Pure protocol checks. No model call, desktop capture or live AutomationElement.
internal static class HybridDecisionCheck
{
    internal static IReadOnlyList<(string Label, bool Passed)> Checks()
    {
        var results = new List<(string Label, bool Passed)>();
        void Check(string label, Func<bool> test)
        {
            try { results.Add((label, test())); }
            catch { results.Add((label, false)); }
        }
        ControlOption[] controls = [
            new("c-current-settings", "设置", "Button", "Toolbar", true, false),
            new("c-current-file", "文件", "MenuItem", "MenuBar", true, true),
            new("c-disabled", "查找", "MenuItem", "Menu", false, false),
            new("c-duplicate-a", "选项", "Button", "Toolbar", true, false),
            new("c-duplicate-b", "选项", "Button", "Toolbar", true, false),
            new("c-check", "自动换行", "CheckBox", "Toolbar", true, false),
            new("c-category", "视频效果", "TreeItem", "Library", true, true),
            new("c-tree-leaf", "效果项", "TreeItem", "Library", true, false)
        ];
        string Id(string? id, string action = "point", double confidence = .98, object? box = null, object? label = null) =>
            JsonSerializer.Serialize(new { action, label, box, candidateId = id, confidence });
        string Image(string action = "point", double confidence = .98, object? candidateId = null) =>
            JsonSerializer.Serialize(new { action, label = "查找", box = new[] { 100, 200, 180, 230 }, candidateId, confidence });
        var point = VisionMapper.ParseHybrid(Id("c-current-settings"), controls);
        Check("hybrid candidate decision uses real ID and current metadata", () => point.CanPoint && point.CandidateId == controls[0].Id && point.Label == "设置" && point.Box == null);
        Check("hybrid prefers real ID without requiring model geometry", () => VisionMapper.ParseHybrid(Id("c-current-settings", label: "untrusted model label"), controls).Label == "设置");
        Check("hybrid ID final entry retains controls confidence threshold", () => VisionMapper.ParseHybrid(Id("c-current-settings", confidence: .85), controls).CanPoint);
        Check("hybrid ID final below controls threshold is refused", () => !VisionMapper.ParseHybrid(Id("c-current-settings", confidence: .84), controls).CanPoint);
        Check("hybrid parent retains tentative confidence threshold", () => VisionMapper.ParseHybrid(Id("c-current-file", "open_entry", .60), controls).CanPoint);
        Check("hybrid low-confidence parent is refused", () => !VisionMapper.ParseHybrid(Id("c-current-file", "open_entry", .59), controls).CanPoint);
        Check("hybrid expandable menu cannot masquerade as final entry", () => VisionMapper.ParseHybrid(Id("c-current-file"), controls).Action == "open_entry");
        Check("hybrid expandable tree cannot masquerade as final entry", () => VisionMapper.ParseHybrid(Id("c-category"), controls).Action == "open_entry");
        Check("hybrid non-expandable tree cannot become parent", () => !VisionMapper.ParseHybrid(Id("c-tree-leaf", "open_entry"), controls).CanPoint);
        Check("hybrid checkbox cannot become parent", () => !VisionMapper.ParseHybrid(Id("c-check", "open_entry"), controls).CanPoint);
        Check("hybrid stale ID never points", () => !VisionMapper.ParseHybrid(Id("c-old-settings"), controls).CanPoint);
        Check("hybrid ID must be unique in the current list", () => !VisionMapper.ParseHybrid(Id("c-current-settings"), [controls[0], controls[0]]).CanPoint);
        Check("hybrid identical candidate metadata is ambiguous", () => VisionMapper.ParseHybrid(Id("c-duplicate-a"), controls) is { CanPoint: false, Action: "ambiguous" });
        Check("hybrid disabled real candidate is refused", () => !VisionMapper.ParseHybrid(Id("c-disabled"), controls).CanPoint);
        Check("hybrid ID and image box cannot both be supplied", () => !VisionMapper.ParseHybrid(Id("c-current-settings", box: new[] { 100, 200, 180, 230 }), controls).CanPoint);
        Check("hybrid invalid ID cannot be rescued by an image box", () => !VisionMapper.ParseHybrid(Image(candidateId: "old"), controls).CanPoint);
        Check("hybrid disabled ID cannot be rescued by an image box", () => !VisionMapper.ParseHybrid(Image(candidateId: "c-disabled"), controls).CanPoint);
        Check("hybrid image selection remains available with real controls", () => VisionMapper.ParseHybrid(Image(), controls) is { CanPoint: true, CandidateId: null, Box.Length: 4 });
        Check("hybrid empty controls still accept five-field image choice", () => VisionMapper.ParseHybrid(Image(), []) is { CanPoint: true, CandidateId: null });
        Check("hybrid empty controls cannot invent an ID", () => !VisionMapper.ParseHybrid(Id("c-invented"), []).CanPoint);
        Check("hybrid image confidence remains stricter than control ID", () => !VisionMapper.ParseHybrid(Image(confidence: .89), controls).CanPoint);
        Check("hybrid image accepts minimum image confidence", () => VisionMapper.ParseHybrid(Image(confidence: .90), controls).CanPoint);
        Check("hybrid image whole-window estimate is refused", () => !VisionMapper.ParseHybrid(Id(null, box: new[] { 0, 0, 1000, 1000 }, label: "window"), controls).CanPoint);
        Check("hybrid image out-of-range estimate is refused", () => !VisionMapper.ParseHybrid(Id(null, box: new[] { 10, 20, 1001, 30 }, label: "entry"), controls).CanPoint);
        Check("hybrid image reversed estimate is refused", () => !VisionMapper.ParseHybrid(Id(null, box: new[] { 100, 200, 90, 230 }, label: "entry"), controls).CanPoint);
        Check("hybrid image cannot omit visible label", () => !VisionMapper.ParseHybrid(Id(null, box: new[] { 100, 200, 180, 230 }), controls).CanPoint);
        foreach (string action in new[] { "click", "type", "point_and_click", "unknown" })
        {
            string value = action;
            Check("hybrid rejects unsupported action: " + value, () => !VisionMapper.ParseHybrid(Id("c-current-settings", value), controls).CanPoint);
        }
        Check("hybrid empty ID is refused", () => !VisionMapper.ParseHybrid(Id(""), controls).CanPoint);
        Check("hybrid null target cannot point", () => !VisionMapper.ParseHybrid(Id(null), controls).CanPoint);
        Check("hybrid malformed JSON is refused", () => !VisionMapper.ParseHybrid("```json\n{}\n```", controls).CanPoint);
        Check("hybrid array instead of object is refused", () => !VisionMapper.ParseHybrid("[]", controls).CanPoint);
        Check("hybrid candidate ID must be a string", () => !VisionMapper.ParseHybrid(Image(candidateId: 3), controls).CanPoint);
        Check("hybrid ID metadata label cannot carry control characters", () => !VisionMapper.ParseHybrid(Id("c-current-settings", label: "fake\nname"), controls).CanPoint);
        foreach (double score in new[] { -.01, 1.01 })
        {
            double value = score;
            Check("hybrid invalid confidence is refused: " + value, () => !VisionMapper.ParseHybrid(Id("c-current-settings", confidence: value), controls).CanPoint);
        }
        string good = Id("c-current-settings");
        Check("hybrid extra field is refused", () => !VisionMapper.ParseHybrid(good.Replace("{", "{\"x\":42,", StringComparison.Ordinal), controls).CanPoint);
        Check("hybrid duplicate field is refused", () => !VisionMapper.ParseHybrid(good.Replace("{", "{\"action\":\"point\",", StringComparison.Ordinal), controls).CanPoint);
        const string four = "{\"action\":\"point\",\"label\":\"Find\",\"box\":[100,200,180,230],\"confidence\":0.98}";
        Check("pure image four-field schema remains compatible", () => VisionMapper.Parse(four).CanPoint);
        Check("hybrid does not silently downgrade to four fields", () => !VisionMapper.ParseHybrid(four, controls).CanPoint);
        Check("pure image parser does not silently accept candidate fields", () => !VisionMapper.Parse(Image()).CanPoint);
        foreach (string action in new[] { "not_found", "ambiguous" })
        {
            string value = action;
            Check("hybrid explicit refusal is preserved: " + action, () => VisionMapper.ParseHybrid(Id(null, value), controls).Action == value);
            Check("hybrid refusal cannot carry candidate: " + action, () => VisionMapper.ParseHybrid(Id("c-current-settings", value), controls).Action == "not_found");
        }
        var finalMemory = new GuideMemory([new(1, "设置", "Button", "Toolbar", "point", "clicked")]);
        var parentMemory = new GuideMemory([new(1, "文件", "MenuItem", "MenuBar", "open_entry", "clicked")]);
        Check("hybrid done requires actual final click evidence", () => VisionMapper.ParseHybrid(Id(null, "done"), controls).Action != "done");
        Check("hybrid done cannot follow a parent click", () => VisionMapper.ParseHybrid(Id(null, "done"), controls, parentMemory).Action != "done");
        Check("hybrid done can follow a final click", () => VisionMapper.ParseHybrid(Id(null, "done"), controls, finalMemory).Action == "done");
        Check("hybrid done cannot carry an ID", () => VisionMapper.ParseHybrid(Id("c-current-settings", "done"), controls, finalMemory).Action != "done");
        var context = new SelectionContext("fixture-app", true, controls);
        Check("hybrid request describes both real controls and image dimensions", () => {
            using var data = JsonDocument.Parse(VisionMapper.DescribeInput("找设置", "fixture-app", new([]), context));
            var root = data.RootElement;
            return root.GetProperty("mode").GetString() == "hybrid" && root.GetProperty("partial").GetBoolean()
                && root.GetProperty("imageWidth").GetInt32() == 1000 && root.GetProperty("imageHeight").GetInt32() == 1000
                && root.GetProperty("controls").GetArrayLength() == controls.Length
                && root.GetProperty("controls")[0].GetProperty("id").GetString() == controls[0].Id;
        });
        Check("empty control read still builds hybrid request with explicit empty list", () => {
            using var data = JsonDocument.Parse(VisionMapper.DescribeInput("找设置", "fixture-app", new([]), new("fixture-app", true, [])));
            return data.RootElement.GetProperty("mode").GetString() == "hybrid" && data.RootElement.GetProperty("controls").GetArrayLength() == 0;
        });
        Check("pure image request remains independent of control metadata", () => {
            using var data = JsonDocument.Parse(VisionMapper.DescribeInput("找设置", "fixture-app", new([]), null));
            return !data.RootElement.TryGetProperty("mode", out _) && !data.RootElement.TryGetProperty("controls", out _);
        });
        return results;
    }
}
