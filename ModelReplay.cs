using System.Diagnostics;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ScreenGuide;

// Replays explicit, filtered metadata fixtures only. Never captures or operates the desktop.
internal sealed record MappingCase(string Label, string Question, SelectionContext Context, GuideMemory? Memory,
    string[] AllowedIds, string[] AllowedActions);
internal static class ModelReplay
{
    internal static async Task<int> Run(string path)
    {
        var config = new ModelProfileStore().LoadActiveOrLegacy(); if (!config.Ready) return 2;
        var json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var cases = JsonSerializer.Deserialize<MappingCase[]>(await File.ReadAllTextAsync(path), json) ?? [];
        var mapper = new IntentMapper(config); var results = new List<object>(); int passed = 0;
        foreach (var item in cases)
        {
            var watch = Stopwatch.StartNew();
            var result = await mapper.Select(item.Question, item.Context, CancellationToken.None, item.Memory);
            bool ok = item.AllowedActions.Contains(result.Action) && (result.Id == null ? item.AllowedIds.Length == 0 : item.AllowedIds.Contains(result.Id));
            if (ok) passed++;
            results.Add(new { item.Label, item.Question, action = result.Action, id = result.Id,
                result.Confidence, result.Message, passed = ok, elapsedMs = watch.ElapsedMilliseconds });
        }
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "mapping-replay-check.json"),
            JsonSerializer.Serialize(new { date = DateTimeOffset.Now, config.Model, scope = "Filtered metadata replay only; no desktop test", passed, total = cases.Length, results }, json));
        return passed == cases.Length ? 0 : 1;
    }
}
