using System.Globalization;
using System.Text.Json;

namespace ScreenGuide;

// Validate the local IPC boundary before anything reaches the window dispatcher.
internal static class PluginGuidePolicy
{
    private static readonly HashSet<string> KnownFields = new(StringComparer.Ordinal)
    {
        "command", "targetId", "question", "mode", "sessionId", "snapshotId", "action",
        "candidateId", "box", "label", "confidence", "afterEvent", "timeoutSeconds"
    };

    internal static PluginRequest Parse(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!KnownFields.Contains(property.Name) || !fields.TryAdd(property.Name, property.Value)) throw new JsonException();

        string? Text(string key)
        {
            if (!fields.TryGetValue(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind != JsonValueKind.String) throw new JsonException();
            return value.GetString();
        }
        long? Integer(string key)
        {
            if (!fields.TryGetValue(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number)) throw new JsonException();
            return number;
        }
        double? Number(string key)
        {
            if (!fields.TryGetValue(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)) throw new JsonException();
            return number;
        }
        void Only(bool legacy, params string[] allowed)
        {
            foreach (var field in fields)
            {
                if (field.Key == "command" || allowed.Contains(field.Key)) continue;
                // The original typed client serialized these optional members as null.
                if (legacy && (field.Key is "targetId" or "question" or "mode") && field.Value.ValueKind == JsonValueKind.Null) continue;
                throw new JsonException();
            }
        }

        var command = Text("command") ?? throw new JsonException();
        switch (command)
        {
            case "status" or "list_targets" or "clear":
                Only(true); break;
            case "bind_target":
                Only(true, "targetId");
                var id = Text("targetId");
                if (id == null || id.Length != 16 || id.Any(c => !Uri.IsHexDigit(c))) throw new JsonException();
                break;
            case "guide" or "codex_start":
                Only(command == "guide", "question", "mode");
                if (!ValidQuestion(Text("question")) || !TryMode(Text("mode"), out _)) throw new JsonException();
                break;
            case "codex_read":
                Only(false, "sessionId");
                if (!ValidToken(Text("sessionId"))) throw new JsonException();
                break;
            case "codex_point":
                Only(false, "sessionId", "snapshotId", "action", "candidateId", "box", "label", "confidence");
                if (!ValidToken(Text("sessionId")) || !ValidToken(Text("snapshotId"))) throw new JsonException();
                var action = Text("action");
                if (action is not ("point" or "open_entry" or "done" or "not_found" or "ambiguous")) throw new JsonException();
                var candidate = Text("candidateId");
                if (candidate != null && !ValidToken(candidate)) throw new JsonException();
                var label = Text("label");
                if (label != null && !ValidLabel(label)) throw new JsonException();
                var confidence = Number("confidence");
                if (confidence is not (>= 0 and <= 1)) throw new JsonException();
                var box = ReadBox(fields);
                if (action is "point" or "open_entry")
                {
                    if ((candidate != null) == (box != null)) throw new JsonException();
                }
                else if (candidate != null || box != null) throw new JsonException();
                break;
            case "codex_wait":
                Only(false, "sessionId", "afterEvent", "timeoutSeconds");
                if (!ValidToken(Text("sessionId")) || Integer("afterEvent") is not (>= 0)) throw new JsonException();
                var timeout = Integer("timeoutSeconds");
                if (timeout != null && timeout is not (>= 1 and <= 25)) throw new JsonException();
                break;
            default: throw new JsonException();
        }

        return new(command, Text("targetId"), Text("question"), Text("mode"), Text("sessionId"), Text("snapshotId"),
            Text("action"), Text("candidateId"), ReadBox(fields), Text("label"), Number("confidence"),
            Integer("afterEvent"), Integer("timeoutSeconds") is long seconds ? checked((int)seconds) : null);
    }

    private static double[]? ReadBox(IReadOnlyDictionary<string, JsonElement> fields)
    {
        if (!fields.TryGetValue("box", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 4) throw new JsonException();
        var box = new double[4]; var index = 0;
        foreach (var part in value.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Number || !part.TryGetDouble(out var number) ||
                !double.IsFinite(number) || number is < 0 or > 1000) throw new JsonException();
            box[index++] = number;
        }
        return box;
    }

    internal static bool ValidToken(string? value) => value is { Length: > 0 and <= 96 } &&
        value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');
    internal static bool ValidLabel(string? value) => value != null && value.Trim().Length is > 0 and <= 160 &&
        value.Length <= 160 && !value.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format);
    internal static bool ValidQuestion(string? question) => question != null && question.Trim().Length is > 0 and <= 500 &&
        question.Length <= 500 && !question.Any(char.IsControl);
    internal static bool Expired(long now, long expiresAt) => now >= expiresAt;
    internal static TimeSpan RequestTimeout(PluginRequest request) => request.Command switch
    {
        "codex_wait" => TimeSpan.FromSeconds((request.TimeoutSeconds ?? 25) + 2),
        "codex_start" or "codex_read" or "codex_point" => TimeSpan.FromSeconds(9),
        _ => TimeSpan.FromSeconds(4)
    };
    internal static bool TryMode(string? value, out GuideMode mode)
    {
        mode = value switch { null or "hybrid" => GuideMode.Hybrid, "direct_image" => GuideMode.DirectImage, "image_on_failure" => GuideMode.ImageOnControlFailure, _ => GuideMode.Controls };
        return value is null or "hybrid" or "controls" or "direct_image" or "image_on_failure";
    }
    internal static GuideMode ResolveMode(string? value, GuideMode preferred) =>
        value == null ? preferred : TryMode(value, out var mode) ? mode : throw new ArgumentException("Invalid screen mode");
}
