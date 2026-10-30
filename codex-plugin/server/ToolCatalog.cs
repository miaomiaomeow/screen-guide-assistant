using System.Text.Json;

namespace ScreenGuide.Plugin;

internal static class ToolCatalog
{
    internal static readonly JsonElement Tools = Load();
    private static JsonElement Load()
    {
        using var stream = typeof(ToolCatalog).Assembly.GetManifestResourceStream("ScreenGuide.Plugin.tools.json")
            ?? throw new InvalidOperationException("Embedded tool definitions are missing.");
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.GetProperty("tools").Clone();
    }
    internal static bool Known(string tool) => Tools.EnumerateArray().Any(entry => entry.GetProperty("name").GetString() == tool);

    internal static (JsonElement? Request, string? Error) Request(string tool, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !JsonLines.UniqueProperties(arguments)) return (null, "工具参数必须是字段不重复的 JSON 对象。");
        var command = tool switch {
            "screen_guide_status" => "status",
            "screen_guide_list_targets" => "list_targets",
            "screen_guide_bind_target" => "bind_target",
            "screen_guide_guide" => "codex_start",
            "screen_guide_read_view" => "codex_read",
            "screen_guide_point" => "codex_point",
            "screen_guide_wait_event" => "codex_wait",
            "screen_guide_clear" => "clear",
            _ => ""
        };
        if (command.Length == 0) return (null, "未知工具。");
        string[] fields = command switch {
            "bind_target" => ["targetId"],
            "codex_start" => ["question", "mode"],
            "codex_read" => ["sessionId"],
            "codex_point" => ["sessionId", "snapshotId", "action", "candidateId", "box", "label", "confidence"],
            "codex_wait" => ["sessionId", "afterEvent", "timeoutSeconds"],
            _ => []
        };
        if (arguments.EnumerateObject().Any(field => !fields.Contains(field.Name))) return (null, "工具包含不支持的参数；只接受工具所列字段，不接受 HWND、脚本或任意路径。");
        var request = new Dictionary<string, object> { ["command"] = command };
        if (command == "bind_target")
        {
            if (!Id(arguments, "targetId", out var target)) return (null, "请传入 list_targets 返回的有效 targetId，不要编造窗口编号。");
            request["targetId"] = target;
        }
        if (command == "codex_start")
        {
            if (!Text(arguments, "question", 500, out var question)) return (null, "请输入 1–500 字的功能位置问题，不含控制字符。");
            request["question"] = question;
            if (arguments.TryGetProperty("mode", out var selected))
            {
                if (selected.ValueKind != JsonValueKind.String || selected.GetString() is not ("hybrid" or "controls" or "image_on_failure" or "direct_image"))
                    return (null, "mode 仅支持 hybrid、controls、image_on_failure 或 direct_image。省略时使用本体已保存模式，首次为控件加截图的 hybrid。");
                request["mode"] = selected.GetString()!;
            }
        }
        if (command is "codex_read" or "codex_point" or "codex_wait")
        {
            if (!Id(arguments, "sessionId", out var session)) return (null, "请传入 guide 返回的有效 sessionId。");
            request["sessionId"] = session;
        }
        if (command == "codex_point")
        {
            if (!Id(arguments, "snapshotId", out var snapshot)) return (null, "请传入最新读取结果的 snapshotId。");
            if (!arguments.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String ||
                action.GetString() is not ("point" or "open_entry" or "done" or "not_found" or "ambiguous"))
                return (null, "action 仅支持 point、open_entry、done、not_found 或 ambiguous。");
            if (!arguments.TryGetProperty("confidence", out var confidence) || confidence.ValueKind != JsonValueKind.Number ||
                !confidence.TryGetDouble(out var value) || !double.IsFinite(value) || value is < 0 or > 1)
                return (null, "confidence 必须是 0–1 的有限数字。");
            request["snapshotId"] = snapshot; request["action"] = action.GetString()!; request["confidence"] = value;
            var hasCandidate = arguments.TryGetProperty("candidateId", out _);
            var hasBox = arguments.TryGetProperty("box", out var box);
            if (action.GetString() is "point" or "open_entry")
            {
                if (hasCandidate == hasBox) return (null, "指出入口时须选择一个最新 candidateId，或图片模式的 box，不能同时提供或全部省略。");
                if (hasCandidate)
                {
                    if (!Id(arguments, "candidateId", out var candidate)) return (null, "candidateId 必须来自最新界面候选。");
                    request["candidateId"] = candidate;
                }
                else
                {
                    if (box.ValueKind != JsonValueKind.Array || box.GetArrayLength() != 4) return (null, "box 必须是补边截图上的四个 0–1000 数字 [左,上,右,下]。");
                    var coordinates = new double[4]; int index = 0;
                    foreach (var coordinate in box.EnumerateArray())
                    {
                        if (coordinate.ValueKind != JsonValueKind.Number || !coordinate.TryGetDouble(out var number) ||
                            !double.IsFinite(number) || number is < 0 or > 1000)
                            return (null, "box 坐标必须是 0–1000 的有限数字。");
                        coordinates[index++] = number;
                    }
                    if (coordinates[2] <= coordinates[0] || coordinates[3] <= coordinates[1]) return (null, "box 必须具有正宽高。");
                    request["box"] = coordinates;
                }
            }
            else if (hasCandidate || hasBox) return (null, "done、not_found 和 ambiguous 不接受候选或坐标。");
            if (arguments.TryGetProperty("label", out _))
            {
                if (!Text(arguments, "label", 240, out var label)) return (null, "label 须为 1–240 字的简短引导文字，不含控制字符。");
                request["label"] = label;
            }
        }
        if (command == "codex_wait")
        {
            if (!arguments.TryGetProperty("afterEvent", out var after) || after.ValueKind != JsonValueKind.Number ||
                !after.TryGetInt32(out var cursor) || cursor < 0) return (null, "afterEvent 必须是上次返回的非负整数事件编号。");
            var seconds = 20;
            if (arguments.TryGetProperty("timeoutSeconds", out var duration) &&
                (duration.ValueKind != JsonValueKind.Number || !duration.TryGetInt32(out seconds) || seconds is < 1 or > 25))
                return (null, "timeoutSeconds 必须是 1–25 的整数。");
            request["afterEvent"] = cursor; request["timeoutSeconds"] = seconds;
        }
        return (JsonSerializer.SerializeToElement(request), null);
    }

    private static bool Id(JsonElement arguments, string name, out string value)
    {
        value = "";
        if (!arguments.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String) return false;
        value = item.GetString()!;
        return value.Length is >= 1 and <= 96 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    }
    private static bool Text(JsonElement arguments, string name, int maximum, out string value)
    {
        value = "";
        if (!arguments.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String) return false;
        var original = item.GetString()!;
        if (original.Length > maximum || original.Any(char.IsControl)) return false;
        value = original.Trim(); return value.Length > 0;
    }
}

