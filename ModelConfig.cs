using System.IO;
using System.Text.Json;

namespace ScreenGuide;

internal sealed record ModelConfig(string ApiKey = "", string Model = "", string BaseUrl = "https://api.tokenfactory.nebius.com/v1/")
{
    internal bool Ready => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(Model);
    internal bool IsDeepSeek => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) && uri.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase);
    internal string DisplayName => (IsDeepSeek ? "DeepSeek" :
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var endpoint) && endpoint.Host.Equals("api.tokenfactory.nebius.com", StringComparison.OrdinalIgnoreCase)
            ? "Nebius" : "Compatible API") + " · " + Model;
    public override string ToString() => $"ModelConfig {{ Model = {Model}, ApiKey = [redacted] }}";
    internal static ModelConfig Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config.local.json");
        var c = File.Exists(path) ? JsonSerializer.Deserialize<ModelConfig>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new() : new();
        var deepseekKey = ReadEnvironment("DEEPSEEK_API_KEY");
        if (deepseekKey != null)
            return new(deepseekKey, ReadEnvironment("DEEPSEEK_MODEL") ?? c.Model, ReadEnvironment("DEEPSEEK_BASE_URL") ?? "https://api.deepseek.com");
        return new(ReadEnvironment("NEBIUS_API_KEY") ?? c.ApiKey, ReadEnvironment("NEBIUS_MODEL") ?? c.Model, ReadEnvironment("NEBIUS_BASE_URL") ?? c.BaseUrl);
    }
    private static string? ReadEnvironment(string name)
    {
        foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            try { var value = Environment.GetEnvironmentVariable(name, scope); if (!string.IsNullOrWhiteSpace(value)) return value.Trim(); }
            catch (System.Security.SecurityException) { }
        }
        return null;
    }
}
