using System.IO;
using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace ScreenGuide;

// Real transport against isolated fake handlers, never the normal app pipe or desktop dispatcher.
internal static class PluginBridgeCheck
{
    private sealed class WaitFixture
    {
        internal readonly TaskCompletionSource<CancellationToken> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<object> Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal static async Task<int> Run()
    {
        var checks = new List<object>(); int passed = 0;
        void Check(string label, bool ok) { checks.Add(new { label, passed = ok }); if (ok) passed++; }
        var pipeName = "ScreenGuide.BridgeCheck." + Guid.NewGuid().ToString("N");
        var fixtures = new ConcurrentDictionary<string, WaitFixture>();
        var handlerCalls = 0;
        WaitFixture Fixture(string name) => fixtures.GetOrAdd(name, _ => new());
        using var bridge = new PluginBridge(async (request, cancel) =>
        {
            Interlocked.Increment(ref handlerCalls);
            if (request.Command is "status" or "clear") return new { ok = true, fixture = "isolated-transport-only", command = request.Command };
            if (request.Command == "codex_read") return new { ok = true, imageFixture = request.SessionId == "large-response" ? new string('a', 6 * 1024 * 1024) : "synthetic" };
            if (request.Command is "codex_point" or "codex_start") return new { ok = true, command = request.Command };
            var fixture = Fixture(request.SessionId ?? "legacy-guide");
            fixture.Entered.TrySetResult(cancel);
            try { return await fixture.Released.Task.WaitAsync(cancel); }
            catch (OperationCanceledException) { fixture.Cancelled.TrySetResult(); throw; }
        }, pipeName);
        bridge.Start();
        async Task<NamedPipeClientStream> Connect()
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.ConnectAsync(2000); return pipe; } catch { pipe.Dispose(); throw; }
        }
        async Task<JsonElement> Exchange(string request)
        {
            await using var pipe = await Connect();
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, true);
            await writer.WriteLineAsync(request);
            var response = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
            using var json = JsonDocument.Parse(response ?? "null"); return json.RootElement.Clone();
        }
        async Task Send(NamedPipeClientStream pipe, string request, bool newline = true)
        {
            var bytes = Encoding.UTF8.GetBytes(request + (newline ? "\n" : ""));
            await pipe.WriteAsync(bytes); await pipe.FlushAsync();
        }
        async Task<string?> Read(NamedPipeClientStream pipe, int seconds = 3)
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, true);
            return await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(seconds));
        }
        async Task<bool> Closed(string request)
        {
            await using var pipe = await Connect();
            try { await Send(pipe, request); return await Read(pipe) == null; }
            catch (IOException) { return true; }
        }
        async Task<bool> ReadClosed(NamedPipeClientStream pipe, int seconds = 3)
        { try { return await Read(pipe, seconds) == null; } catch (IOException) { return true; } }
        string WaitRequest(string session, int seconds = 25) => JsonSerializer.Serialize(new { command = "codex_wait", sessionId = session, afterEvent = 0, timeoutSeconds = seconds });
        try
        {
            var status = await Exchange("{\"command\":\"status\"}");
            Check("real host transport returns synthetic status", status.GetProperty("ok").GetBoolean());
            var invalid = await Exchange("{\"command\":\"click\"}");
            Check("real host transport rejects unsafe command", !invalid.GetProperty("ok").GetBoolean());
            var callsBeforeInvalid = handlerCalls;
            invalid = await Exchange("""{"command":"codex_wait","sessionId":"fake","afterEvent":0,"timeoutSeconds":26}""");
            Check("real host transport rejects illegal long-poll parameters before handler", !invalid.GetProperty("ok").GetBoolean() && handlerCalls == callsBeforeInvalid);
            invalid = await Exchange("""{"command":"codex_point","sessionId":"fake","snapshotId":"fake","action":"point","box":[1,2,3,1001],"confidence":1}""");
            Check("real host transport rejects illegal point parameters before handler", !invalid.GetProperty("ok").GetBoolean() && handlerCalls == callsBeforeInvalid);
            var exactHeader = "{\"command\":\"status\"}";
            status = await Exchange(exactHeader.PadRight(8192));
            Check("real host transport accepts its exact bounded header limit", status.GetProperty("ok").GetBoolean());
            var callsBeforeLong = handlerCalls;
            Check("real host transport closes oversized header without calling handler", await Closed(exactHeader.PadRight(8193)) && handlerCalls == callsBeforeLong);
            await using (var pipe = await Connect())
            {
                await Send(pipe, "{\"command\":", false);
                Check("incomplete header has a four-second deadline", await ReadClosed(pipe, 6));
            }
            await using (var pipe = await Connect())
            {
                await Send(pipe, WaitRequest("concurrent", 6));
                var fixture = Fixture("concurrent");
                await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                status = await Exchange("{\"command\":\"status\"}");
                Check("long wait does not block status connection", status.GetProperty("ok").GetBoolean());
                var clear = await Exchange("{\"command\":\"clear\"}");
                Check("long wait does not block clear connection", clear.GetProperty("ok").GetBoolean());
                await Task.Delay(4200);
                Check("long wait outlives legacy four-second deadline", !fixture.Entered.Task.Result.IsCancellationRequested);
                fixture.Released.TrySetResult(new { ok = true, eventName = "synthetic-click" });
                using var response = JsonDocument.Parse(await Read(pipe) ?? "null");
                Check("long wait returns synthetic click response", response.RootElement.GetProperty("eventName").GetString() == "synthetic-click");
            }
            await using (var pipe = await Connect())
            {
                await Send(pipe, "{\"command\":\"guide\",\"question\":\"Synthetic fixture\"}");
                await Fixture("legacy-guide").Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            }
            await Fixture("legacy-guide").Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check("disconnect cancels queued legacy handler before deadline", Fixture("legacy-guide").Entered.Task.Result.IsCancellationRequested);
            await using (var pipe = await Connect())
            {
                await Send(pipe, WaitRequest("extra-bytes"));
                await Fixture("extra-bytes").Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await Send(pipe, "x", false);
                await Fixture("extra-bytes").Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Check("extra bytes after one request cancel its pending handler", Fixture("extra-bytes").Entered.Task.Result.IsCancellationRequested);
            }
            await using (var pipe = await Connect())
            {
                await Send(pipe, WaitRequest("short-deadline", 1));
                await Fixture("short-deadline").Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Check("short long-poll deadline closes silent handler", await ReadClosed(pipe, 5));
                await Fixture("short-deadline").Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Check("short long-poll deadline propagates cancellation", Fixture("short-deadline").Entered.Task.Result.IsCancellationRequested);
            }
            var held = new List<NamedPipeClientStream>();
            try
            {
                for (var index = 0; index < 6; index++)
                {
                    var pipe = await Connect(); held.Add(pipe);
                    await Send(pipe, WaitRequest("capacity-" + index));
                    await Fixture("capacity-" + index).Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                }
                var full = await Exchange(WaitRequest("capacity-rejected"));
                Check("long waits reserve capacity by rejecting a seventh waiter", !full.GetProperty("ok").GetBoolean() && !Fixture("capacity-rejected").Entered.Task.IsCompleted);
                status = await Exchange("{\"command\":\"status\"}");
                Check("status remains available at maximum long-wait capacity", status.GetProperty("ok").GetBoolean());
                var clear = await Exchange("{\"command\":\"clear\"}");
                Check("clear remains available at maximum long-wait capacity", clear.GetProperty("ok").GetBoolean());
            }
            finally { foreach (var pipe in held) pipe.Dispose(); }
            await Task.WhenAll(Enumerable.Range(0, 6).Select(index => Fixture("capacity-" + index).Cancelled.Task)).WaitAsync(TimeSpan.FromSeconds(2));
            Check("all disconnected long waits release their handlers", Enumerable.Range(0, 6).All(index => Fixture("capacity-" + index).Entered.Task.Result.IsCancellationRequested));
            var large = await Exchange("""{"command":"codex_read","sessionId":"large-response"}""");
            Check("transport returns large synthetic image-sized response", large.GetProperty("imageFixture").GetString()?.Length == 6 * 1024 * 1024);
            status = await Exchange("{\"command\":\"status\"}");
            Check("host serves subsequent requests after cancellation and large response", status.GetProperty("ok").GetBoolean());
            await using (var first = await Connect())
            await using (var second = await Connect())
            await using (var partial = await Connect())
            {
                await Send(first, WaitRequest("close-first")); await Send(second, WaitRequest("close-second"));
                await Send(partial, "{", false);
                await Task.WhenAll(Fixture("close-first").Entered.Task, Fixture("close-second").Entered.Task).WaitAsync(TimeSpan.FromSeconds(2));
                bridge.Dispose();
                await Task.WhenAll(Fixture("close-first").Cancelled.Task, Fixture("close-second").Cancelled.Task).WaitAsync(TimeSpan.FromSeconds(2));
                await bridge.Completion.WaitAsync(TimeSpan.FromSeconds(3));
                Check("host disposal cancels all in-flight fake handlers", Fixture("close-first").Entered.Task.Result.IsCancellationRequested && Fixture("close-second").Entered.Task.Result.IsCancellationRequested);
                Check("host disposal closes incomplete request connection", await ReadClosed(partial));
                Check("host disposal awaits release of all connections", bridge.Completion.IsCompletedSuccessfully);
                bridge.Dispose(); bridge.Start();
                Check("host start and dispose remain idempotent after shutdown", bridge.Completion.IsCompletedSuccessfully);
            }
        }
        catch (Exception error) { Check("isolated transport error: " + error.GetType().Name, false); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "plugin-bridge-check.json"), JsonSerializer.Serialize(new
        { scope = "Actual concurrent host pipe against synthetic handlers with a random isolated name; no desktop, UIA, captured image, microphone, environment key or model request", passed, total = checks.Count, checks }, new JsonSerializerOptions { WriteIndented = true }));
        return passed == checks.Count ? 0 : 1;
    }
}
