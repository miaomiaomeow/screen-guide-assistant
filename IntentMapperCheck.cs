using System.Net.Http;
using System.Text.Json;

namespace ScreenGuide;

// Parser/filter checks plus preflight checks. Every preflight fixture uses an invalid base URL:
// no real key is loaded and there is no path from these checks to an HTTP request.
internal static class IntentMapperCheck
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
            new("ready", "默认命令", "Button", "ToolBar", true, false),
            new("folder", "分类目录", "TreeItem", "Tree", true, true),
            new("disabled", "不可用命令", "Button", "ToolBar", false, false),
            new("check", "选项", "CheckBox", "Menu", true, false),
            new("menu", "菜单", "MenuItem", "MenuBar", true, true),
            new("leaf", "叶子", "TreeItem", "Tree", true, false)
        ];
        string Json(string action, object? id, double confidence = .99) => JsonSerializer.Serialize(new { action, id, confidence });
        bool Failure(string json, SelectionFailure failure, IReadOnlyList<ControlOption>? allowed = null, GuideMemory? memory = null)
        {
            var result = IntentMapper.Parse(json, allowed ?? controls, memory);
            return !result.CanPoint && result.Failure == failure;
        }
        var finalClick = new GuideMemory([new(1, "默认命令", "Button", "ToolBar", "point", "clicked")]);

        Check("enabled selection succeeds with no failure", () =>
        {
            var result = IntentMapper.Parse(Json("point", "ready"), controls);
            return result.CanPoint && result.Failure == SelectionFailure.None && result.Id == "ready";
        });
        Check("explicit not_found remains distinct from malformed output", () => Failure(Json("not_found", null), SelectionFailure.NotFound));
        Check("explicit ambiguity is typed", () => Failure(Json("ambiguous", null), SelectionFailure.Ambiguous));
        Check("low confidence on a real candidate is typed", () => Failure(Json("point", "ready", .5), SelectionFailure.LowConfidence));
        Check("open_entry confidence threshold stays at 0.60", () => IntentMapper.Parse(Json("open_entry", "menu", .60), controls).CanPoint);
        Check("below parent threshold is low confidence", () => Failure(Json("open_entry", "menu", .59), SelectionFailure.LowConfidence));
        Check("non-JSON is InvalidResponse", () => Failure("not-json", SelectionFailure.InvalidResponse));
        Check("non-object JSON is InvalidResponse", () => Failure("[]", SelectionFailure.InvalidResponse));
        Check("unknown action with low score is not LowConfidence", () => Failure(Json("click", "ready", .1), SelectionFailure.InvalidResponse));
        Check("non-string action is InvalidResponse", () => Failure("{\"action\":42,\"id\":\"ready\",\"confidence\":0.99}", SelectionFailure.InvalidResponse));
        Check("missing field is InvalidResponse", () => Failure("{\"action\":\"point\",\"id\":\"ready\"}", SelectionFailure.InvalidResponse));
        Check("duplicate field is InvalidResponse", () => Failure("{\"action\":\"point\",\"id\":\"ready\",\"id\":\"ready\",\"confidence\":0.99}", SelectionFailure.InvalidResponse));
        Check("extra coordinates are InvalidResponse", () => Failure("{\"action\":\"point\",\"id\":\"ready\",\"confidence\":0.99,\"x\":5}", SelectionFailure.InvalidResponse));
        Check("out of range score is not LowConfidence", () => Failure(Json("point", "ready", -1), SelectionFailure.InvalidResponse));
        Check("non-finite score is InvalidResponse", () => Failure("{\"action\":\"point\",\"id\":\"ready\",\"confidence\":1e999}", SelectionFailure.InvalidResponse));
        Check("null point ID is InvalidResponse even with low score", () => Failure(Json("point", null, .1), SelectionFailure.InvalidResponse));
        Check("array point ID is InvalidResponse", () => Failure(Json("point", new[] { "ready" }), SelectionFailure.InvalidResponse));
        Check("unknown ID is InvalidCandidate even with low score", () => Failure(Json("point", "invented", .1), SelectionFailure.InvalidCandidate));
        Check("duplicate ID is InvalidCandidate", () => Failure(Json("point", "ready"), SelectionFailure.InvalidCandidate, [controls[0], controls[0]]));
        Check("local parser keeps disabled defense", () => Failure(Json("point", "disabled"), SelectionFailure.Disabled));
        Check("disabled low score remains a disabled selection", () => Failure(Json("point", "disabled", .1), SelectionFailure.Disabled));
        Check("request allowlist excludes disabled entries", () =>
        {
            var selectable = IntentMapper.SelectableControls(controls);
            return selectable.Length == 5 && selectable.All(control => control.Enabled)
                && selectable.Select(control => control.Id).SequenceEqual(new[] { "ready", "folder", "check", "menu", "leaf" });
        });
        Check("filtered disabled ID is InvalidCandidate", () => Failure(Json("point", "disabled"), SelectionFailure.InvalidCandidate,
            IntentMapper.SelectableControls(controls)));
        Check("filter does not mutate source candidates", () =>
        {
            _ = IntentMapper.SelectableControls(controls);
            return controls.Length == 6 && !controls[2].Enabled;
        });
        Check("identical enabled metadata is Ambiguous", () => Failure(Json("point", "ready"), SelectionFailure.Ambiguous,
            [controls[0], controls[0] with { Id = "other" }]));
        Check("disabled metadata duplicate does not make valid selection ambiguous", () =>
            IntentMapper.Parse(Json("point", "ready"), [controls[0], controls[0] with { Id = "disabled-copy", Enabled = false }]).CanPoint);
        Check("wrong parent role is InvalidEntry", () => Failure(Json("open_entry", "check"), SelectionFailure.InvalidEntry));
        Check("expandable tree parent can be opened", () => IntentMapper.Parse(Json("open_entry", "folder"), controls).CanPoint);
        Check("expandable tree point is normalized to parent entry", () => IntentMapper.Parse(Json("point", "folder"), controls).Action == "open_entry");
        Check("expandable menu point remains parent entry", () => IntentMapper.Parse(Json("point", "menu"), controls).Action == "open_entry");
        Check("nonexpandable tree leaf cannot be opened as category", () => Failure(Json("open_entry", "leaf"), SelectionFailure.InvalidEntry));
        Check("done without final click is UnconfirmedDone", () => Failure(Json("done", null), SelectionFailure.UnconfirmedDone));
        Check("low score done without final click stays UnconfirmedDone", () => Failure(Json("done", null, .4), SelectionFailure.UnconfirmedDone));
        Check("done after hover is UnconfirmedDone", () => Failure(Json("done", null), SelectionFailure.UnconfirmedDone, memory:
            new GuideMemory([new(1, "菜单", "MenuItem", "MenuBar", "open_entry", "hover_revealed")])));
        Check("done with ID is InvalidResponse", () => Failure(Json("done", "ready"), SelectionFailure.InvalidResponse, memory: finalClick));
        Check("done with low score is LowConfidence", () => Failure(Json("done", null, .4), SelectionFailure.LowConfidence, memory: finalClick));
        Check("valid done keeps legacy action and no failure", () =>
        {
            var result = IntentMapper.Parse(Json("done", null), controls, finalClick);
            return result.Action == "done" && result.Failure == SelectionFailure.None;
        });
        Check("not_found with an ID is InvalidResponse", () => Failure(Json("not_found", "ready"), SelectionFailure.InvalidResponse));
        Check("successful HTTP envelope preserves valid selection", () =>
        {
            var envelope = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = Json("point", "ready") } } } });
            return IntentMapper.ParseResponse(envelope, controls).CanPoint;
        });
        foreach (var envelope in new[] { "not-json", "[]", "{}", "{\"choices\":[]}", "{\"choices\":{}}",
                     "{\"choices\":[null]}", "{\"choices\":[{\"message\":{}}]}", "{\"choices\":[{\"message\":{\"content\":null}}]}",
                     "{\"choices\":[{\"message\":{\"content\":{}}}]}" })
        {
            var fixture = envelope;
            Check("bad HTTP envelope is InvalidResponse: " + results.Count, () => IntentMapper.ParseResponse(fixture, controls).Failure == SelectionFailure.InvalidResponse);
        }
        Check("transport exception is ServiceError", () => IntentMapper.RequestFailure(new HttpRequestException(), false) == SelectionFailure.ServiceError);
        Check("uncancelled request timeout is RequestTimeout", () => IntentMapper.RequestFailure(new TaskCanceledException(), false) == SelectionFailure.RequestTimeout);
        Check("explicit cancellation is Cancelled", () => IntentMapper.RequestFailure(new OperationCanceledException(), true) == SelectionFailure.Cancelled);
        var noNetwork = new ModelConfig("test-only", "test-only", "invalid-base-url");
        Check("all disabled preflight returns NoEnabledEntries without HTTP", () =>
            new IntentMapper(noNetwork).Select("功能在哪", new("fixture", false, [controls[2]]), CancellationToken.None).GetAwaiter().GetResult().Failure == SelectionFailure.NoEnabledEntries);
        Check("empty candidates preflight returns NoEnabledEntries without HTTP", () =>
            new IntentMapper(noNetwork).Select("功能在哪", new("fixture", false, []), CancellationToken.None).GetAwaiter().GetResult().Failure == SelectionFailure.NoEnabledEntries);
        Check("invalid question preflight is InvalidQuestion", () =>
            new IntentMapper(noNetwork).Select(" ", new("fixture", false, controls), CancellationToken.None).GetAwaiter().GetResult().Failure == SelectionFailure.InvalidQuestion);
        Check("invalid configuration preflight is Configuration", () =>
            new IntentMapper(noNetwork).Select("功能在哪", new("fixture", false, controls), CancellationToken.None).GetAwaiter().GetResult().Failure == SelectionFailure.Configuration);
        Check("pre-cancelled request never begins HTTP", () =>
            new IntentMapper(noNetwork).Select("功能在哪", new("fixture", false, controls), new CancellationToken(true)).GetAwaiter().GetResult().Failure == SelectionFailure.Cancelled);
        return results;
    }
}
