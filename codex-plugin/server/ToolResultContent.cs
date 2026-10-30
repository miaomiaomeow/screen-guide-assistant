using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenGuide.Plugin;

// Pixels belong in MCP's native image block, never in model-visible JSON text.
internal static class ToolResultContent
{
    internal static JsonObject Create(JsonElement response, bool includeStructured)
    {
        if (response.ValueKind != JsonValueKind.Object)
            response = PipeGuideClient.Failure("app_protocol_error", "本体响应格式无效。");
        JsonObject? image = null;
        if (response.TryGetProperty("image", out var given) && given.ValueKind != JsonValueKind.Null)
        {
            if (given.ValueKind != JsonValueKind.Object ||
                !given.TryGetProperty("mimeType", out var mime) || mime.ValueKind != JsonValueKind.String || mime.GetString() != "image/jpeg" ||
                !given.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.String || !ValidBase64(data.GetString()!))
                response = PipeGuideClient.Failure("app_protocol_error", "本体图片响应格式无效；未转发图片数据。");
            else image = new JsonObject { ["type"] = "image", ["data"] = data.GetString(), ["mimeType"] = "image/jpeg" };
        }
        var safe = JsonNode.Parse(response.GetRawText())!.AsObject();
        RemoveImagePayloads(safe);
        if (image != null) safe["imageProvidedAsContent"] = true;
        bool failed = !safe.TryGetPropertyValue("ok", out var ok) || ok is not JsonValue value ||
            !value.TryGetValue<bool>(out var success) || !success;
        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = safe.ToJsonString() });
        if (image != null) content.Add(image);
        var result = new JsonObject { ["content"] = content, ["isError"] = failed };
        if (includeStructured) result["structuredContent"] = safe;
        return result;
    }

    private static bool ValidBase64(string data)
    {
        if (data.Length == 0 || data.Length > PipeGuideClient.MaximumResponseCharacters - 65536 || data.Length % 4 != 0) return false;
        var bytes = new byte[data.Length / 4 * 3];
        return Convert.TryFromBase64String(data, bytes, out var count) && count > 0;
    }

    private static void RemoveImagePayloads(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            // The protocol only supplies a top-level image. Strip any nested image too.
            obj.Remove("image");
            foreach (var property in obj) RemoveImagePayloads(property.Value);
        }
        else if (node is JsonArray array)
            foreach (var child in array) RemoveImagePayloads(child);
    }
}
