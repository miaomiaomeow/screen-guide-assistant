using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ScreenGuide.Plugin;

// This check never connects to the running guide. Every pipe name is random and local to a test.
internal static class SelfTest
{
    private const string ModernVersion = "2026-07-28";
    private static readonly string[] ToolNames =
    [
        "screen_guide_status", "screen_guide_list_targets", "screen_guide_bind_target",
        "screen_guide_guide", "screen_guide_read_view", "screen_guide_point", "screen_guide_wait_event", "screen_guide_clear"
    ];

    internal static async Task<int> RunAsync()
    {
        var checks = new List<(string Label, bool Passed)>();
        void Check(string label, bool passed) => checks.Add((label, passed));
        async Task Scenario(string label, Func<Task> work)
        {
            try { await work(); }
            catch (Exception error) { Check(label + ": scenario completed (" + error.GetType().Name + ")", false); }
        }

        await Scenario("legacy", async () =>
        {
            var host = new FakeTransport();
            var messages = new List<string>
            {
                Request(1, "initialize", new
                {
                    protocolVersion = "2025-11-25", capabilities = new { },
                    clientInfo = new { name = "screen-guide-self-test", version = "1" }
                }),
                Notification("notifications/initialized"),
                Request(2, "tools/list", new { }),
                Call(3, "screen_guide_status", new { }),
                Call(4, "screen_guide_list_targets", new { }),
                Call(5, "screen_guide_bind_target", new { targetId = "fake-issued" }),
                Call(6, "screen_guide_guide", new { question = "find a setting" }),
                Call(7, "screen_guide_guide", new { question = "find a menu", mode = "image_on_failure" }),
                Call(8, "screen_guide_guide", new { question = "find another menu", mode = "direct_image" }),
                Call(9, "screen_guide_clear", new { }),
                Call(10, "screen_guide_guide", new { question = "test", mode = "unknown" }),
                Call(11, "screen_guide_guide", new { question = "test", unexpected = true }),
                Call(12, "screen_guide_guide", new { question = "   " }),
                Call(13, "screen_guide_guide", new { question = "test", hwnd = 12345 }),
                Request(14, "unsupported/method", new { }),
                Call(15, "unsupported_tool", new { }),
                Call(16, "screen_guide_guide", new { question = "host-denial" }),
                Call(17, "screen_guide_status", new { }),
                Call(18, "screen_guide_status", new { }),
                Call(19, "screen_guide_status", new { targetId = "unexpected" }),
                Call(24, "screen_guide_guide", new { question = new string(' ', 500) + "x" }),
                Call(25, "screen_guide_bind_target", new { }),
                "{broken-json",
                Notification("unknown/notification"),
                Notification("tools/call", new { name = "screen_guide_guide", arguments = new { question = "notification-must-not-guide" } })
            };
            var replies = await Exchange(host, messages);
            var initialized = Reply(replies, 1);
            Check("legacy initialize negotiates requested version", StringAt(initialized, "result", "protocolVersion") == "2025-11-25");
            Check("legacy declares exactly eight tools", DeclaresTools(Reply(replies, 2)));
            Check("legacy tools expose object input schemas", HasInputSchemas(Reply(replies, 2)));
            Check("status response has matching structured and text content", CoherentHostResult(Reply(replies, 3), "status", true));
            Check("list targets reaches host command", HostCall(host, "list_targets") is not null && CoherentHostResult(Reply(replies, 4), "list_targets", true));
            Check("binding forwards issued target ID", HostCall(host, "bind_target") is { } binding && StringAt(binding, "targetId") == "fake-issued");
            Check("guide preserves omitted mode for saved host preference", GuideCall(host, "find a setting") is { } first && At(first, "mode").ValueKind == JsonValueKind.Undefined);
            Check("guide forwards explicit conditional image mode", GuideCall(host, "find a menu") is { } fallback && StringAt(fallback, "mode") == "image_on_failure");
            Check("guide forwards explicit direct image mode", GuideCall(host, "find another menu") is { } image && StringAt(image, "mode") == "direct_image");
            Check("clear reaches host command", HostCall(host, "clear") is not null && CoherentHostResult(Reply(replies, 9), "clear", true));
            Check("unknown mode is a tool error", ToolError(Reply(replies, 10)));
            Check("extra guide argument is a tool error", ToolError(Reply(replies, 11)));
            Check("empty question is a tool error", ToolError(Reply(replies, 12)));
            Check("invented HWND argument is a tool error", ToolError(Reply(replies, 13)));
            Check("extra status argument is a tool error", ToolError(Reply(replies, 19)));
            Check("question max length applies before trimming", ToolError(Reply(replies, 24)));
            Check("binding requires an issued target ID", ToolError(Reply(replies, 25)));
            Check("unknown method uses method-not-found", RpcError(Reply(replies, 14), -32601));
            Check("unknown tool uses invalid-params", RpcError(Reply(replies, 15), -32602));
            Check("host denial is propagated as tool error", ToolError(Reply(replies, 16)) && CoherentHostResult(Reply(replies, 16), "codex_start", false));
            Check("bad JSON uses parse-error", replies.Any(r => RpcError(r, -32700) && At(r, "id").ValueKind == JsonValueKind.Null));
            Check("repeated requests retain their own response association", NumberAt(Reply(replies, 17), "result", "structuredContent", "callIndex") == 9 && NumberAt(Reply(replies, 18), "result", "structuredContent", "callIndex") == 10);
            Check("notifications produce no responses", replies.Count == 22 && replies.All(r => At(r, "id").ValueKind is JsonValueKind.Number or JsonValueKind.Null));
            Check("invalid arguments and notifications never reach host", host.Requests.Count == 10 && host.Requests.All(r => StringAt(r, "question") != "test" && StringAt(r, "question") != "notification-must-not-guide"));
        });

        await Scenario("saved mode and session overrides", async () =>
        {
            var host = new FakeTransport();
            var replies = await Exchange(host,
            [
                ModernCall(101, "screen_guide_guide", new { question = "inherit saved setting" }),
                ModernCall(102, "screen_guide_guide", new { question = "mixed controls and image", mode = "hybrid" }),
                ModernCall(103, "screen_guide_guide", new { question = "only real controls", mode = "controls" }),
                ModernCall(104, "screen_guide_guide", new { question = "conditional image", mode = "image_on_failure" }),
                ModernCall(105, "screen_guide_guide", new { question = "image only", mode = "direct_image" }),
                ModernCall(106, "screen_guide_guide", new { question = "bad mode", mode = "" }),
                ModernCall(107, "screen_guide_guide", new { question = "null mode", mode = (string?)null }),
                ModernRequest(108, "tools/list")
            ]);
            Check("omitted mode is absent rather than null or an explicit default", GuideCall(host, "inherit saved setting") is { } inherited && !inherited.TryGetProperty("mode", out _));
            Check("hybrid override is forwarded unchanged", GuideCall(host, "mixed controls and image") is { } hybrid && StringAt(hybrid, "mode") == "hybrid");
            Check("controls override is forwarded unchanged", GuideCall(host, "only real controls") is { } controls && StringAt(controls, "mode") == "controls");
            Check("both previous image modes are retained", GuideCall(host, "conditional image") is { } fallback && StringAt(fallback, "mode") == "image_on_failure" && GuideCall(host, "image only") is { } image && StringAt(image, "mode") == "direct_image");
            Check("empty and null modes cannot hide accidental client defaults", ToolError(Reply(replies, 106)) && ToolError(Reply(replies, 107)) && host.Requests.Count == 5);
            Check("session starts never add a settings-save field", host.Requests.All(r => r.EnumerateObject().All(p => p.Name is "command" or "question" or "mode")));
            var guideTool = At(Reply(replies, 108), "result", "tools").EnumerateArray().Single(t => StringAt(t, "name") == "screen_guide_guide");
            var modeSchema = At(guideTool, "inputSchema", "properties", "mode");
            Check("schema advertises all four modes without forcing a preference", At(modeSchema, "enum").EnumerateArray().Select(m => m.GetString()).Order().SequenceEqual(new[] { "controls", "direct_image", "hybrid", "image_on_failure" }) && At(modeSchema, "default").ValueKind == JsonValueKind.Undefined);
            Check("mode is optional in the public schema", !ContainsString(At(guideTool, "inputSchema", "required"), "mode"));
        });

        await Scenario("initialization guards", async () =>
        {
            var host = new FakeTransport();
            var replies = await Exchange(host,
            [
                Request(21, "tools/list", new { }),
                Request(22, "initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "self-test", version = "1" } }),
                Call(23, "screen_guide_clear", new { })
            ]);
            Check("tools rejected before initialize", RpcError(Reply(replies, 21), -32602));
            Check("tool calls rejected before initialized notification", RpcError(Reply(replies, 23), -32602));
            Check("incomplete initialization performs no host calls", host.Requests.Count == 0);
        });

        await Scenario("modern", async () =>
        {
            var host = new FakeTransport();
            var replies = await Exchange(host,
            [
                ModernRequest(31, "server/discover"),
                ModernRequest(32, "tools/list"),
                ModernCall(33, "screen_guide_status", new { }),
                ModernCall(34, "screen_guide_guide", new { question = "modern setting" }),
                ModernCall(35, "screen_guide_guide", new { question = "test", hwnd = 12345 }),
                Call(36, "screen_guide_status", new { }),
                ModernRequest(37, "tools/list", version: "unsupported-version"),
                ModernRequest(38, "unsupported/method"),
                Request(39, "tools/list", new { _meta = new Dictionary<string, object> { ["io.modelcontextprotocol/protocolVersion"] = ModernVersion } }),
                Request(40, "server/discover"),
                Notification("unknown/notification")
            ]);
            Check("modern discovery advertises supported version", ContainsString(At(Reply(replies, 31), "result", "supportedVersions"), ModernVersion));
            Check("modern discovery carries mandatory caching hints", NumberAt(Reply(replies, 31), "result", "ttlMs") >= 0 && StringAt(Reply(replies, 31), "result", "cacheScope") is "public" or "private");
            Check("modern lists eight tools without legacy handshake", DeclaresTools(Reply(replies, 32)) && StringAt(Reply(replies, 32), "result", "resultType") == "complete");
            Check("modern tool list carries mandatory caching hints", NumberAt(Reply(replies, 32), "result", "ttlMs") >= 0 && StringAt(Reply(replies, 32), "result", "cacheScope") is "public" or "private");
            Check("modern tool response is complete and coherent", StringAt(Reply(replies, 33), "result", "resultType") == "complete" && CoherentHostResult(Reply(replies, 33), "status", true));
            Check("modern guide preserves host preference by omitting mode", GuideCall(host, "modern setting") is { } guide && At(guide, "mode").ValueKind == JsonValueKind.Undefined && StringAt(Reply(replies, 34), "result", "resultType") == "complete");
            Check("modern schema rejection never reaches host", ToolError(Reply(replies, 35)) && StringAt(Reply(replies, 35), "result", "resultType") == "complete" && host.Requests.Count == 2);
            Check("modern missing request metadata rejected", RpcError(Reply(replies, 36), -32602));
            Check("modern unsupported version rejected", RpcError(Reply(replies, 37), -32022));
            Check("modern client capabilities metadata is required", RpcError(Reply(replies, 39), -32602));
            Check("modern discovery also validates required per-request metadata", RpcError(Reply(replies, 40), -32602));
            Check("modern unknown method rejected and notification silent", RpcError(Reply(replies, 38), -32601) && replies.Count == 10);
        });

        await Scenario("isolated IPC", async () =>
        {
            var pipeName = TestPipeName();
            using var server = NewTestPipe(pipeName);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var received = default(JsonElement);
            var serve = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync(timeout.Token);
                using var reader = new StreamReader(server, new UTF8Encoding(false, true), false, 1024, true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                var line = await reader.ReadLineAsync(timeout.Token);
                using var parsed = JsonDocument.Parse(line ?? throw new IOException("No isolated test request."));
                received = parsed.RootElement.Clone();
                await writer.WriteLineAsync("{\"ok\":true,\"test\":\"isolated-roundtrip\"}".AsMemory(), timeout.Token);
            }, timeout.Token);
            var request = ToolCatalog.Request("screen_guide_guide", JsonSerializer.SerializeToElement(new { question = "isolated test only" })).Request!.Value;
            var response = await new PipeGuideClient(pipeName).SendAsync(request, timeout.Token);
            await serve;
            Check("random user-only pipe preserves omitted mode and response", StringAt(received, "command") == "codex_start" && StringAt(received, "question") == "isolated test only" && At(received, "mode").ValueKind == JsonValueKind.Undefined && StringAt(response, "test") == "isolated-roundtrip" && At(response, "ok").ValueKind == JsonValueKind.True);
        });

        await Scenario("isolated IPC cancellation", async () =>
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            var cancelled = false;
            try { await new PipeGuideClient(TestPipeName()).SendAsync(JsonSerializer.SerializeToElement(new { command = "status" }), cancel.Token); }
            catch (OperationCanceledException) { cancelled = cancel.IsCancellationRequested; }
            Check("unconnected random pipe honors cancellation", cancelled);
        });

        await Scenario("isolated IPC malformed reply", async () =>
        {
            var pipeName = TestPipeName();
            using var server = NewTestPipe(pipeName);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var serve = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync(timeout.Token);
                using var reader = new StreamReader(server, new UTF8Encoding(false, true), false, 1024, true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                _ = await reader.ReadLineAsync(timeout.Token);
                await writer.WriteLineAsync("{broken-test-reply".AsMemory(), timeout.Token);
            }, timeout.Token);
            var rejected = false;
            try
            {
                var reply = await new PipeGuideClient(pipeName).SendAsync(JsonSerializer.SerializeToElement(new { command = "status" }), timeout.Token);
                rejected = At(reply, "ok").ValueKind == JsonValueKind.False && StringAt(reply, "error") == "app_protocol_error";
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or IOException or InvalidOperationException) { rejected = true; }
            await serve;
            Check("malformed isolated pipe response is rejected", rejected);
        });

        await Scenario("Codex decision loop", async () =>
        {
            var host = new FakeTransport();
            var replies = await Exchange(host,
            [
                ModernCall(51, "screen_guide_read_view", new { sessionId = "session-1" }),
                ModernCall(52, "screen_guide_point", new { sessionId = "session-1", snapshotId = "view-2", action = "open_entry", candidateId = "c1-001", label = "展开文件菜单", confidence = .93 }),
                ModernCall(53, "screen_guide_wait_event", new { sessionId = "session-1", afterEvent = 2 }),
                ModernCall(54, "screen_guide_point", new { sessionId = "session-1", snapshotId = "view-3", action = "point", box = new[] { 10.5, 20, 100, 200 }, confidence = .89 }),
                ModernCall(55, "screen_guide_point", new { sessionId = "session-1", snapshotId = "view-3", action = "not_found", label = "当前界面没有可确定的入口", confidence = .1 }),
                ModernCall(56, "screen_guide_wait_event", new { sessionId = "session-1", afterEvent = 3, timeoutSeconds = 25 })
            ]);
            Check("read_view forwards session without adding model request", HostCall(host, "codex_read") is { } read && StringAt(read, "sessionId") == "session-1");
            Check("point forwards snapshot and opaque string candidate", HostCall(host, "codex_point") is { } point && StringAt(point, "snapshotId") == "view-2" && StringAt(point, "candidateId") == "c1-001" && StringAt(point, "action") == "open_entry");
            Check("wait defaults to twenty seconds and forwards cursor", HostCall(host, "codex_wait") is { } wait && NumberAt(wait, "afterEvent") == 2 && NumberAt(wait, "timeoutSeconds") == 20);
            Check("image decision retains fractional normalized box", host.Requests.Any(r => At(r, "box").ValueKind == JsonValueKind.Array && At(r, "box")[0].GetDouble() == 10.5));
            Check("failure decision is forwarded without inventing a candidate", host.Requests.Any(r => StringAt(r, "action") == "not_found" && At(r, "candidateId").ValueKind == JsonValueKind.Undefined));
            Check("new tools use matching structured and text results", CoherentHostResult(Reply(replies, 51), "codex_read", true) && CoherentHostResult(Reply(replies, 52), "codex_point", true) && CoherentHostResult(Reply(replies, 53), "codex_wait", true));
            Check("wait accepts upper bounded timeout", host.Requests.Any(r => StringAt(r, "command") == "codex_wait" && NumberAt(r, "timeoutSeconds") == 25));
        });

        await Scenario("decision boundary validation", () =>
        {
            bool Valid(string tool, object args) => ToolCatalog.Request(tool, JsonSerializer.SerializeToElement(args)).Error == null;
            Check("session IDs reject whitespace and punctuation", !Valid("screen_guide_read_view", new { sessionId = "fake session" }) && !Valid("screen_guide_read_view", new { sessionId = "../other" }));
            Check("session IDs reject excessive length", !Valid("screen_guide_read_view", new { sessionId = new string('a', 97) }));
            Check("point requires a fresh snapshot identifier", !Valid("screen_guide_point", new { sessionId = "s", action = "point", candidateId = "c", confidence = .9 }));
            Check("candidate IDs must be strings", !Valid("screen_guide_point", new { sessionId = "s", snapshotId = "v", action = "point", candidateId = 1, confidence = .9 }));
            Check("point rejects two location sources", !Valid("screen_guide_point", new { sessionId = "s", snapshotId = "v", action = "point", candidateId = "c", box = new[] { 1, 2, 3, 4 }, confidence = .9 }));
            Check("point rejects absent location", !Valid("screen_guide_point", new { sessionId = "s", snapshotId = "v", action = "point", confidence = .9 }));
            Check("point rejects inverted and out-of-range boxes", !Valid("screen_guide_point", new { sessionId = "s", snapshotId = "v", action = "point", box = new[] { 10, 20, 9, 30 }, confidence = .9 }) && !Valid("screen_guide_point", new { sessionId = "s", snapshotId = "v", action = "point", box = new[] { 10, 20, 90, 1001 }, confidence = .9 }));
            Check("point requires confidence within zero and one", !Valid("screen_guide_point", new { sessionId = "s", snapshotId = "v", action = "point", candidateId = "c", confidence = 2 }) && !Valid("screen_guide_point", new { sessionId = "s", snapshotId = "v", action = "point", candidateId = "c" }));
            Check("unknown decision action is rejected", !Valid("screen_guide_point", new { sessionId = "s", snapshotId = "v", action = "click", candidateId = "c", confidence = .9 }));
            Check("done does not accept a location", !Valid("screen_guide_point", new { sessionId = "s", snapshotId = "v", action = "done", candidateId = "c", confidence = .9 }));
            Check("terminal decisions accept no location", Valid("screen_guide_point", new { sessionId = "s", snapshotId = "v", action = "done", confidence = 1 }) && Valid("screen_guide_point", new { sessionId = "s", snapshotId = "v", action = "ambiguous", confidence = .5 }));
            Check("wait requires non-negative integer cursor", !Valid("screen_guide_wait_event", new { sessionId = "s", afterEvent = -1 }) && !Valid("screen_guide_wait_event", new { sessionId = "s", afterEvent = 1.5 }));
            Check("wait timeout is bounded", !Valid("screen_guide_wait_event", new { sessionId = "s", afterEvent = 0, timeoutSeconds = 0 }) && !Valid("screen_guide_wait_event", new { sessionId = "s", afterEvent = 0, timeoutSeconds = 26 }));
            Check("new read tool refuses arbitrary coordinates", !Valid("screen_guide_read_view", new { sessionId = "s", x = 100 }));
            Check("questions and labels reject control characters", !Valid("screen_guide_guide", new { question = "find\nmenu" }) && !Valid("screen_guide_point", new { sessionId = "s", snapshotId = "v", action = "done", label = "a\nb", confidence = 1 }));
            return Task.CompletedTask;
        });

        await Scenario("native MCP image", () =>
        {
            var encoded = Convert.ToBase64String([255, 216, 255, 0, 255, 217]);
            var data = JsonSerializer.SerializeToElement(new { ok = true, view = new { snapshotId = "image-view", image = new { data = encoded } }, image = new { data = encoded, mimeType = "image/jpeg" } });
            using var rendered = JsonDocument.Parse(ToolResultContent.Create(data, true).ToJsonString());
            var root = rendered.RootElement;
            var content = At(root, "content");
            Check("image emitted as a native MCP block", content.GetArrayLength() == 2 && StringAt(content[1], "type") == "image" && StringAt(content[1], "data") == encoded && StringAt(content[1], "mimeType") == "image/jpeg");
            Check("structured result never repeats base64 image payload", !At(root, "structuredContent").GetRawText().Contains(encoded, StringComparison.Ordinal) && At(root, "structuredContent", "image").ValueKind == JsonValueKind.Undefined);
            Check("text result never repeats nested or top-level base64", !StringAt(content[0], "text")!.Contains(encoded, StringComparison.Ordinal) && At(root, "structuredContent", "view", "snapshotId").GetString() == "image-view");
            Check("image availability retained without payload", At(root, "structuredContent", "imageProvidedAsContent").ValueKind == JsonValueKind.True);
            using var invalid = JsonDocument.Parse(ToolResultContent.Create(JsonSerializer.SerializeToElement(new { ok = true, image = new { data = "invalid-base64!", mimeType = "image/jpeg" } }), true).ToJsonString());
            Check("invalid image cannot leak into text or be marked successful", At(invalid.RootElement, "isError").ValueKind == JsonValueKind.True && At(invalid.RootElement, "content").GetArrayLength() == 1 && !invalid.RootElement.GetRawText().Contains("invalid-base64!", StringComparison.Ordinal));
            using var legacy = JsonDocument.Parse(ToolResultContent.Create(data, false).ToJsonString());
            Check("old MCP still receives native image without structured field", At(legacy.RootElement, "structuredContent").ValueKind == JsonValueKind.Undefined && At(legacy.RootElement, "content").GetArrayLength() == 2);
            return Task.CompletedTask;
        });

        await Scenario("wait concurrency and MCP cancellation", async () =>
        {
            var host = new BlockingWaitTransport();
            using var input = new LiveInput();
            using var output = new RecordingWriter();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var server = new McpServer(host).RunAsync(input, output, timeout.Token);
            input.Add(ModernCall(71, "screen_guide_wait_event", new { sessionId = "session-1", afterEvent = 0 }));
            await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            input.Add(ModernCall(72, "screen_guide_status", new { }));
            input.Add(ModernCall(73, "screen_guide_clear", new { }));
            var status = await output.Read(72).WaitAsync(TimeSpan.FromSeconds(2));
            var clear = await output.Read(73).WaitAsync(TimeSpan.FromSeconds(2));
            Check("active wait does not block status or clear", CoherentHostResult(status, "status", true) && CoherentHostResult(clear, "clear", true) && !host.Released.Task.IsCompleted);
            input.Add(Notification("notifications/cancelled", new { requestId = 71, reason = "self-test" }));
            var cancelled = await output.Read(71).WaitAsync(TimeSpan.FromSeconds(2));
            await host.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check("MCP cancellation propagates into an active wait", ToolError(cancelled) && StringAt(cancelled, "result", "structuredContent", "error") == "cancelled");
            input.Add(ModernCall(74, "screen_guide_status", new { }));
            Check("cancelled wait leaves later tools usable", CoherentHostResult(await output.Read(74).WaitAsync(TimeSpan.FromSeconds(2)), "status", true));
            input.Complete(); await server;
        });

        await Scenario("command-specific IPC budget", () =>
        {
            int Budget(string command) => PipeGuideClient.TimeoutSeconds(JsonSerializer.SerializeToElement(new { command }));
            Check("only wait receives extended IPC budget", Budget("codex_wait") == 33 && Budget("status") == 8 && Budget("clear") == 8 && Budget("list_targets") == 8);
            Check("wait budget reserves fresh-view time within thirty-eight seconds", PipeGuideClient.TimeoutSeconds(JsonSerializer.SerializeToElement(new { command = "codex_wait", timeoutSeconds = 25 })) == 38 && PipeGuideClient.TimeoutSeconds(JsonSerializer.SerializeToElement(new { command = "codex_wait", timeoutSeconds = 1 })) == 14);
            Check("UIA read and point receive ten-second IPC budget", Budget("codex_start") == 10 && Budget("codex_read") == 10 && Budget("codex_point") == 10);
            Check("image reply transport has bounded six-MiB character budget", PipeGuideClient.MaximumResponseCharacters == 6 * 1024 * 1024);
            return Task.CompletedTask;
        });

        await Scenario("isolated image IPC", async () =>
        {
            var pipeName = TestPipeName();
            using var server = NewTestPipe(pipeName);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var bytes = new byte[128 * 1024];
            bytes[0] = 255; bytes[1] = 216; bytes[2] = 255;
            var encoded = Convert.ToBase64String(bytes);
            var serve = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync(timeout.Token);
                using var reader = new StreamReader(server, new UTF8Encoding(false, true), false, 1024, true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                _ = await reader.ReadLineAsync(timeout.Token);
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok = true, snapshotId = "large-image", image = new { data = encoded, mimeType = "image/jpeg" } }).AsMemory(), timeout.Token);
            }, timeout.Token);
            var reply = await new PipeGuideClient(pipeName).SendAsync(JsonSerializer.SerializeToElement(new { command = "codex_read", sessionId = "test" }), timeout.Token);
            await serve;
            using var projected = JsonDocument.Parse(ToolResultContent.Create(reply, true).ToJsonString());
            Check("isolated IPC accepts image larger than old 64-KiB limit", At(reply, "ok").ValueKind == JsonValueKind.True && StringAt(reply, "image", "data") == encoded && encoded.Length > 65536);
            Check("large IPC image is projected once without base64 text duplication", At(projected.RootElement, "content").GetArrayLength() == 2 && !At(projected.RootElement, "structuredContent").GetRawText().Contains(encoded, StringComparison.Ordinal));
        });

        await Scenario("image through full MCP response", async () =>
        {
            var encoded = Convert.ToBase64String([255, 216, 255, 0, 255, 217]);
            var host = new StaticTransport(JsonSerializer.SerializeToElement(new { ok = true, source = "image", snapshotId = "view-image", cursor = 2, image = new { data = encoded, mimeType = "image/jpeg" } }));
            var modern = Reply(await Exchange(host, [ModernCall(81, "screen_guide_read_view", new { sessionId = "test" })]), 81);
            var blocks = At(modern, "result", "content");
            Check("full modern MCP call delivers image content and current snapshot", blocks.GetArrayLength() == 2 && StringAt(blocks[1], "data") == encoded && StringAt(modern, "result", "structuredContent", "snapshotId") == "view-image");
            Check("full modern MCP response advertises English package version", StringAt(modern, "result", "_meta", "io.modelcontextprotocol/serverInfo", "version") == "0.3.1");
            var legacy = Reply(await Exchange(host,
            [
                Request(82, "initialize", new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "test", version = "1" } }),
                Notification("notifications/initialized"),
                Call(83, "screen_guide_read_view", new { sessionId = "test" })
            ]), 83);
            Check("full oldest MCP call delivers image without unsupported structured field", At(legacy, "result", "content").GetArrayLength() == 2 && At(legacy, "result", "structuredContent").ValueKind == JsonValueKind.Undefined);
        });

        await Scenario("hybrid native image and real control metadata", async () =>
        {
            var encoded = Convert.ToBase64String([255, 216, 255, 0, 255, 217]);
            var controls = new[] {
                new { id = "c4-001", name = "Settings", kind = "MenuItem", ancestry = "File" },
                new { id = "c4-002", name = "Settings", kind = "MenuItem", ancestry = "Edit" }
            };
            var host = new StaticTransport(JsonSerializer.SerializeToElement(new {
                ok = true, source = "hybrid", snapshotId = "hybrid-view", controls,
                image = new { data = encoded, mimeType = "image/jpeg" }
            }));
            var reply = Reply(await Exchange(host, [ModernCall(111, "screen_guide_read_view", new { sessionId = "test" })]), 111);
            var structured = At(reply, "result", "structuredContent");
            var content = At(reply, "result", "content");
            Check("hybrid preserves real candidate IDs even with duplicate labels", StringAt(structured, "source") == "hybrid" && At(structured, "controls").GetArrayLength() == 2 && StringAt(At(structured, "controls")[1], "id") == "c4-002" && StringAt(At(structured, "controls")[1], "ancestry") == "Edit");
            Check("hybrid image is delivered natively beside controls without base64 JSON", content.GetArrayLength() == 2 && StringAt(content[1], "type") == "image" && StringAt(content[1], "data") == encoded && !structured.GetRawText().Contains(encoded, StringComparison.Ordinal) && !StringAt(content[0], "text")!.Contains(encoded, StringComparison.Ordinal));
            var noControls = new StaticTransport(JsonSerializer.SerializeToElement(new {
                ok = true, source = "hybrid", snapshotId = "image-without-uia", controls = Array.Empty<object>(),
                image = new { data = encoded, mimeType = "image/jpeg" }
            }));
            var imageOnly = Reply(await Exchange(noControls, [ModernCall(112, "screen_guide_read_view", new { sessionId = "test" })]), 112);
            Check("hybrid remains successful when an inaccessible submenu exposes no controls", At(imageOnly, "result", "isError").ValueKind == JsonValueKind.False && At(imageOnly, "result", "structuredContent", "controls").GetArrayLength() == 0 && At(imageOnly, "result", "content").GetArrayLength() == 2);
            using var jsonText = JsonDocument.Parse(StringAt(At(imageOnly, "result", "content")[0], "text")!);
            Check("empty-controls hybrid text retains image availability and current snapshot", StringAt(jsonText.RootElement, "source") == "hybrid" && StringAt(jsonText.RootElement, "snapshotId") == "image-without-uia" && At(jsonText.RootElement, "imageProvidedAsContent").ValueKind == JsonValueKind.True);
        });

        await Scenario("bounded JSON line recovery", async () =>
        {
            using var input = new StringReader("oversized-line\n{}\n");
            var rejected = false;
            try { await JsonLines.ReadAsync(input, 4, CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
            Check("overlong line is rejected without corrupting following request", rejected && await JsonLines.ReadAsync(input, 4, CancellationToken.None) == "{}");
            using var duplicate = JsonDocument.Parse("{\"sessionId\":\"s\",\"sessionId\":\"other\"}");
            Check("duplicate tool identifiers cannot override validation", ToolCatalog.Request("screen_guide_read_view", duplicate.RootElement).Error != null);
        });

        await Scenario("isolated IPC concurrent wait and clear", async () =>
        {
            var pipeName = TestPipeName();
            using var first = NewTestPipe(pipeName, 2);
            using var second = NewTestPipe(pipeName, 2);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Serve(NamedPipeServerStream pipe)
            {
                await pipe.WaitForConnectionAsync(timeout.Token);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false, true), false, 1024, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                using var request = JsonDocument.Parse(await reader.ReadLineAsync(timeout.Token) ?? throw new IOException());
                var command = StringAt(request.RootElement, "command");
                if (command == "codex_wait") { waiting.TrySetResult(); await cleared.Task.WaitAsync(timeout.Token); }
                else if (command == "clear") cleared.TrySetResult();
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok = true, command }).AsMemory(), timeout.Token);
            }
            var serving = Task.WhenAll(Serve(first), Serve(second));
            var client = new PipeGuideClient(pipeName);
            var wait = client.SendAsync(JsonSerializer.SerializeToElement(new { command = "codex_wait", sessionId = "test", afterEvent = 0, timeoutSeconds = 20 }), timeout.Token);
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var clear = await client.SendAsync(JsonSerializer.SerializeToElement(new { command = "clear" }), timeout.Token).WaitAsync(TimeSpan.FromSeconds(2));
            var finished = await wait;
            await serving;
            Check("same IPC client can clear while another connection waits", At(clear, "ok").ValueKind == JsonValueKind.True && StringAt(clear, "command") == "clear" && StringAt(finished, "command") == "codex_wait");
        });

        await Scenario("isolated connected IPC cancellation", async () =>
        {
            var pipeName = TestPipeName();
            using var server = NewTestPipe(pipeName);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var callerCancel = new CancellationTokenSource();
            var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var serve = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync(timeout.Token);
                using var reader = new StreamReader(server, new UTF8Encoding(false, true), false, 1024, true);
                _ = await reader.ReadLineAsync(timeout.Token); accepted.TrySetResult();
                try { if (await reader.ReadLineAsync(timeout.Token) == null) disconnected.TrySetResult(); }
                catch (IOException) { disconnected.TrySetResult(); }
            }, timeout.Token);
            var request = new PipeGuideClient(pipeName).SendAsync(JsonSerializer.SerializeToElement(new { command = "codex_wait", sessionId = "test", afterEvent = 0 }), callerCancel.Token);
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            callerCancel.Cancel();
            var cancelled = false;
            try { await request; } catch (OperationCanceledException) { cancelled = true; }
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2)); await serve;
            Check("cancelling a connected wait closes its pipe for host cancellation", cancelled && disconnected.Task.IsCompletedSuccessfully);
        });

        var failures = checks.Where(c => !c.Passed).Select(c => c.Label).ToArray();
        Console.WriteLine($"Plugin self-test: {checks.Count - failures.Length}/{checks.Count} passed.");
        foreach (var failure in failures) Console.WriteLine("FAIL: " + failure);
        return failures.Length == 0 ? 0 : 1;
    }

    private static string TestPipeName() => "ScreenGuide.Plugin.SelfTest." + Guid.NewGuid().ToString("N");
    private static NamedPipeServerStream NewTestPipe(string name, int instances = 1) => new(name, PipeDirection.InOut, instances,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static async Task<List<JsonElement>> Exchange(IGuideTransport host, IEnumerable<string> messages)
    {
        using var input = new StringReader(string.Join('\n', messages) + "\n");
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await new McpServer(host).RunAsync(input, output, cancel.Token);
        var replies = new List<JsonElement>();
        foreach (var line in output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line);
            replies.Add(document.RootElement.Clone());
        }
        return replies;
    }

    private static string Request(int id, string method, object? parameters = null) => parameters == null
        ? JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method })
        : JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
    private static string Notification(string method, object? parameters = null) => parameters == null
        ? JsonSerializer.Serialize(new { jsonrpc = "2.0", method })
        : JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters });
    private static string Call(int id, string name, object arguments) => Request(id, "tools/call", new { name, arguments });
    private static Dictionary<string, object> Meta(string version) => new()
    {
        ["io.modelcontextprotocol/protocolVersion"] = version,
        ["io.modelcontextprotocol/clientCapabilities"] = new { }
    };
    private static string ModernRequest(int id, string method, string version = ModernVersion) => Request(id, method, new { _meta = Meta(version) });
    private static string ModernCall(int id, string name, object arguments) => Request(id, "tools/call", new { name, arguments, _meta = Meta(ModernVersion) });

    private static JsonElement Reply(IEnumerable<JsonElement> replies, int id) => replies.FirstOrDefault(r => NumberAt(r, "id") == id);
    private static JsonElement At(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var next)) return default;
            element = next;
        }
        return element;
    }
    private static string? StringAt(JsonElement element, params string[] path)
    {
        var value = At(element, path);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
    private static int? NumberAt(JsonElement element, params string[] path)
    {
        var value = At(element, path);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    }
    private static bool RpcError(JsonElement reply, int code) => NumberAt(reply, "error", "code") == code;
    private static bool ToolError(JsonElement reply) => At(reply, "error").ValueKind == JsonValueKind.Undefined && At(reply, "result", "isError").ValueKind == JsonValueKind.True;
    private static bool ContainsString(JsonElement value, string expected) => value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == expected);
    private static bool DeclaresTools(JsonElement reply)
    {
        var tools = At(reply, "result", "tools");
        return tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() == ToolNames.Length &&
            tools.EnumerateArray().Select(t => StringAt(t, "name")).Order(StringComparer.Ordinal).SequenceEqual(ToolNames.Order(StringComparer.Ordinal));
    }
    private static bool HasInputSchemas(JsonElement reply)
    {
        var tools = At(reply, "result", "tools");
        return tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() == ToolNames.Length &&
            tools.EnumerateArray().All(t => StringAt(t, "inputSchema", "type") == "object" && At(t, "inputSchema", "additionalProperties").ValueKind == JsonValueKind.False);
    }
    private static JsonElement? HostCall(FakeTransport host, string command) => host.Requests.Where(r => StringAt(r, "command") == command).Select(r => (JsonElement?)r).FirstOrDefault();
    private static JsonElement? GuideCall(FakeTransport host, string question) => host.Requests.Where(r => StringAt(r, "command") == "codex_start" && StringAt(r, "question") == question).Select(r => (JsonElement?)r).FirstOrDefault();
    private static bool CoherentHostResult(JsonElement reply, string command, bool ok)
    {
        var structured = At(reply, "result", "structuredContent");
        var content = At(reply, "result", "content");
        if (StringAt(structured, "command") != command || At(structured, "ok").ValueKind != (ok ? JsonValueKind.True : JsonValueKind.False) || content.ValueKind != JsonValueKind.Array) return false;
        foreach (var block in content.EnumerateArray())
        {
            if (StringAt(block, "type") != "text" || StringAt(block, "text") is not { } text) continue;
            try
            {
                using var document = JsonDocument.Parse(text);
                if (StringAt(document.RootElement, "command") == command && At(document.RootElement, "ok").ValueKind == (ok ? JsonValueKind.True : JsonValueKind.False) && NumberAt(document.RootElement, "callIndex") == NumberAt(structured, "callIndex")) return true;
            }
            catch (JsonException) { }
        }
        return false;
    }

    private sealed class FakeTransport : IGuideTransport
    {
        internal List<JsonElement> Requests { get; } = [];
        public Task<JsonElement> SendAsync(JsonElement request, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            Requests.Add(request.Clone());
            var command = StringAt(request, "command");
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                ok = StringAt(request, "question") != "host-denial", command,
                callIndex = Requests.Count, status = "fake-host-only"
            }));
        }
    }

    private sealed class BlockingWaitTransport : IGuideTransport
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<JsonElement> SendAsync(JsonElement request, CancellationToken cancel)
        {
            var command = StringAt(request, "command");
            if (command == "codex_wait")
            {
                Started.TrySetResult();
                try { await Released.Task.WaitAsync(cancel); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            return JsonSerializer.SerializeToElement(new { ok = true, command, callIndex = 0 });
        }
    }

    private sealed class StaticTransport(JsonElement response) : IGuideTransport
    {
        public Task<JsonElement> SendAsync(JsonElement request, CancellationToken cancel)
        { cancel.ThrowIfCancellationRequested(); return Task.FromResult(response.Clone()); }
    }

    private sealed class LiveInput : TextReader
    {
        private readonly Channel<char> _input = Channel.CreateUnbounded<char>();
        internal void Add(string line) { foreach (var character in line + "\n") _input.Writer.TryWrite(character); }
        internal void Complete() => _input.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (!await _input.Reader.WaitToReadAsync(cancellationToken)) return 0;
            if (_input.Reader.TryRead(out var character)) { buffer.Span[0] = character; return 1; }
            return await ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class RecordingWriter : TextWriter
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _replies = [];
        public override Encoding Encoding => Encoding.UTF8;
        internal Task<JsonElement> Read(int id) => _replies.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            using var document = JsonDocument.Parse(buffer.ToString());
            var reply = document.RootElement.Clone();
            if (NumberAt(reply, "id") is { } id) _replies.GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(reply);
            return Task.CompletedTask;
        }
    }
}
