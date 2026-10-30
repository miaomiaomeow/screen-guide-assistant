using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenGuide;

internal sealed record PluginRequest(string Command, string? TargetId = null, string? Question = null, string? Mode = null,
    string? SessionId = null, string? SnapshotId = null, string? Action = null, string? CandidateId = null,
    double[]? Box = null, string? Label = null, double? Confidence = null, long? AfterEvent = null, int? TimeoutSeconds = null);

// The local desktop host is transport-only here. All native-window decisions run on the UI thread.
internal sealed class PluginBridge(Func<PluginRequest, CancellationToken, Task<object>> handle, string? pipeName = null) : IDisposable
{
    private const int MaxClients = 8;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(MaxClients);
    private readonly object _gate = new();
    private readonly HashSet<Connection> _connections = [];
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _started, _disposed;
    private int _waiting;
    private sealed class Connection(NamedPipeServerStream pipe, CancellationTokenSource cancellation)
    {
        internal readonly NamedPipeServerStream Pipe = pipe;
        internal readonly CancellationTokenSource Cancellation = cancellation;
        internal readonly TaskCompletionSource Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal static string PipeName => "ScreenGuideDemo.Plugin.v1." + Regex.Replace(
        WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("无法确定当前 Windows 用户。"), "[^A-Za-z0-9]", "_");

    internal Task Completion => _completed.Task;

    internal void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
            _ = Task.Run(Listen);
        }
    }

    private async Task Listen()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _slots.WaitAsync(_stop.Token);
                Connection? connection = null;
                var transferred = false;
                try
                {
                    var pipe = new NamedPipeServerStream(pipeName ?? PipeName, PipeDirection.InOut, MaxClients, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    connection = new(pipe, CancellationTokenSource.CreateLinkedTokenSource(_stop.Token));
                    lock (_gate)
                    {
                        if (_disposed) throw new OperationCanceledException();
                        _connections.Add(connection);
                    }
                    await pipe.WaitForConnectionAsync(connection.Cancellation.Token);
                    var accepted = connection;
                    _ = Task.Run(() => ProcessConnection(accepted));
                    transferred = true;
                    connection = null; // The worker now owns this connection and semaphore slot.
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { WriteTransportError(error); }
                finally
                {
                    if (connection != null) FinishConnection(connection);
                    else if (!transferred) _slots.Release();
                }
                if (!transferred && !_stop.IsCancellationRequested) await Task.Delay(100, _stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { WriteTransportError(error); }
        finally
        {
            Connection[] remaining;
            lock (_gate) remaining = _connections.ToArray();
            foreach (var connection in remaining) CancelAndClose(connection);
            await Task.WhenAll(remaining.Select(c => c.Completed.Task));
            _stop.Dispose(); _slots.Dispose(); _completed.TrySetResult();
        }
    }

    private async Task ProcessConnection(Connection connection)
    {
        var client = connection.Cancellation;
        var countedWait = false;
        try
        {
            client.CancelAfter(TimeSpan.FromSeconds(4)); // An incomplete request header never reserves a long wait.
            using var reader = new StreamReader(connection.Pipe, new UTF8Encoding(false, true), false, 1024, true);
            using var writer = new StreamWriter(connection.Pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            var line = await ReadLineBounded(reader, client.Token);
            var disconnected = ObserveDisconnect(reader, client);
            try
            {
                object response;
                try
                {
                    var request = PluginGuidePolicy.Parse(line);
                    client.CancelAfter(PluginGuidePolicy.RequestTimeout(request));
                    // Reserve capacity for status and clear while several Codex calls wait for clicks.
                    if (request.Command == "codex_wait")
                    {
                        countedWait = true;
                        if (Interlocked.Increment(ref _waiting) > MaxClients - 2)
                        {
                            response = new { ok = false, error = "同时等待的请求过多，请结束已有等待后重试。" };
                            await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions).AsMemory(), client.Token);
                            return;
                        }
                    }
                    client.Token.ThrowIfCancellationRequested();
                    var work = handle(request, client.Token);
                    // A cancelled client may stop awaiting before an uncooperative handler eventually faults.
                    _ = work.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    response = await work.WaitAsync(client.Token);
                }
                catch (JsonException) { response = new { ok = false, error = "请求格式或参数无效。" }; }
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions).AsMemory(), client.Token);
            }
            finally { TryCancel(client); await disconnected; }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { WriteTransportError(error); }
        finally
        {
            if (countedWait) Interlocked.Decrement(ref _waiting);
            FinishConnection(connection);
        }
    }

    private void FinishConnection(Connection connection)
    {
        CancelAndClose(connection);
        connection.Cancellation.Dispose();
        lock (_gate)
        {
            _slots.Release();
            connection.Completed.TrySetResult();
            _connections.Remove(connection);
        }
    }

    private void WriteTransportError(Exception error)
    {
        if (!_stop.IsCancellationRequested) Log.Write("plugin-bridge", "transport=" + error.GetType().Name);
    }

    private static void TryCancel(CancellationTokenSource cancellation)
    { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { } }

    private static void CancelAndClose(Connection connection)
    {
        TryCancel(connection.Cancellation);
        try { connection.Pipe.Dispose(); } catch (IOException) { }
    }

    private static async Task ObserveDisconnect(StreamReader reader, CancellationTokenSource client)
    {
        try
        {
            // This connection accepts exactly one request. EOF or extra data cancels queued work.
            await reader.ReadAsync(new char[1].AsMemory(), client.Token);
            TryCancel(client);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or ObjectDisposedException or DecoderFallbackException)
        { TryCancel(client); }
    }

    private static async Task<string> ReadLineBounded(StreamReader reader, CancellationToken cancel)
    {
        var text = new StringBuilder(); var one = new char[1];
        while (await reader.ReadAsync(one.AsMemory(), cancel) != 0)
        {
            if (one[0] == '\n') return text.ToString().TrimEnd('\r');
            // The optional CR in a CRLF delimiter does not consume the JSON header budget.
            if (text.Length >= 8192 && !(text.Length == 8192 && one[0] == '\r')) throw new IOException("插件请求过长。");
            text.Append(one[0]);
        }
        throw new IOException("插件连接提前结束。");
    }

    public void Dispose()
    {
        Connection[] connections; bool started;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; started = _started; connections = _connections.ToArray();
        }
        TryCancel(_stop);
        foreach (var connection in connections) CancelAndClose(connection);
        if (!started) { _stop.Dispose(); _slots.Dispose(); _completed.TrySetResult(); }
    }
}
