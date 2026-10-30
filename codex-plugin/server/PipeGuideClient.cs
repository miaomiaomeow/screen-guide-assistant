using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenGuide.Plugin;

internal interface IGuideTransport
{
    Task<JsonElement> SendAsync(JsonElement request, CancellationToken cancel);
}

internal static class PipeIdentity
{
    internal static string CurrentUserName()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(sid)) throw new InvalidOperationException("Current Windows user is unavailable.");
        return "ScreenGuideDemo.Plugin.v1." + Regex.Replace(sid, "[^A-Za-z0-9]", "_");
    }
}

internal sealed class PipeGuideClient(string pipeName) : IGuideTransport
{
    internal const int MaximumResponseCharacters = 6 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public async Task<JsonElement> SendAsync(JsonElement request, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds(request)));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(1500, timeout.Token);
            using var writer = new StreamWriter(pipe, Utf8, 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            using var reader = new StreamReader(pipe, Utf8, detectEncodingFromByteOrderMarks: false, 1024, leaveOpen: true);
            var line = request.GetRawText();
            if (line.Length > 8192) throw new InvalidDataException("Request is too large.");
            await writer.WriteLineAsync(line.AsMemory(), timeout.Token);
            var response = await JsonLines.ReadAsync(reader, MaximumResponseCharacters, timeout.Token);
            if (response == null) throw new InvalidDataException("The app closed without a response.");
            using var json = JsonDocument.Parse(response, new JsonDocumentOptions { MaxDepth = 32 });
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !JsonLines.UniqueProperties(json.RootElement))
                throw new InvalidDataException("The app returned an invalid response.");
            return json.RootElement.Clone();
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        { return Failure("app_timeout", "本体响应超时；请求是否已接收尚不能确认，请先查询状态，避免重复提交。"); }
        catch (TimeoutException)
        { return Failure("app_unavailable", "未连接到本地屏幕指路助手。请先启动本体并开启插件连接。"); }
        catch (UnauthorizedAccessException)
        { return Failure("app_access_denied", "无法访问本体管道。插件与本体需由同一 Windows 用户运行。"); }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException)
        { return Failure("app_protocol_error", "本体连接中断或响应格式无效；请先查询状态，避免重复提交。"); }
    }

    internal static JsonElement Failure(string code, string message) =>
        JsonSerializer.SerializeToElement(new { ok = false, error = code, message });

    internal static int TimeoutSeconds(JsonElement request)
    {
        if (!request.TryGetProperty("command", out var command)) return 8;
        if (command.GetString() != "codex_wait")
            return command.GetString() is "codex_start" or "codex_read" or "codex_point" ? 10 : 8;
        var seconds = 20;
        if (request.TryGetProperty("timeoutSeconds", out var duration) && duration.TryGetInt32(out var requested) && requested is >= 1 and <= 25)
            seconds = requested;
        // The bounded event wait may be followed by a fresh UIA/image view from the host.
        return seconds + 13;
    }
}

internal static class JsonLines
{
    internal static async Task<string?> ReadAsync(TextReader input, int limit, CancellationToken cancel)
    {
        var text = new StringBuilder(Math.Min(1024, limit));
        var character = new char[1];
        bool overflow = false;
        while (true)
        {
            var read = await input.ReadAsync(character.AsMemory(), cancel);
            if (read == 0 || character[0] == '\n')
            {
                if (overflow) throw new InvalidDataException("JSON line exceeds the transport limit.");
                if (read == 0 && text.Length == 0) return null;
                if (text.Length > 0 && text[^1] == '\r') text.Length--;
                return text.ToString();
            }
            if (text.Length >= limit) overflow = true;
            else if (!overflow) text.Append(character[0]);
        }
    }

    internal static bool UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || !UniqueProperties(property.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (!UniqueProperties(item)) return false;
        return true;
    }
}
