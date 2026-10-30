using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using NAudio.Wave;
using SherpaOnnx;

namespace ScreenGuide;

// Audio stays in a bounded memory buffer. Nothing here sends a request or guides/clicks the UI.
internal sealed class SpeechInput : IDisposable
{
    internal const int SampleRate = 16000;
    internal const int MaximumSeconds = 30;
    private const int MaximumSamples = SampleRate * MaximumSeconds;
    private readonly object _gate = new();
    private readonly SynchronizationContext? _eventContext = SynchronizationContext.Current;
    private Operation? _operation;
    private long _generation;
    private bool _disposed;

    internal static string ModelDirectory => SpeechEngines.ModelDirectory(SpeechEngine.SenseVoice);
    internal static bool ModelsAvailable => SpeechEngines.MissingFiles(SpeechEngine.SenseVoice).Length == 0;

    internal bool IsRecording { get { lock (_gate) return _operation?.Capture is { StopRequested: false }; } }
    internal bool IsBusy { get { lock (_gate) return _operation != null; } }

    // Events are delivered on the creating UI context, when there is one. Old sessions are suppressed.
    internal event Action<double, double>? RecordingProgress;
    internal event Action? RecordingLimitReached;
    internal event Action? SpeechSegmentReady;
    internal event Action<Exception>? RecordingFailed;

    internal void Start(SpeechEngine engine = SpeechEngine.SenseVoice, bool autoEndpoint = false)
    {
        SpeechEngines.EnsureModels(engine);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_operation != null) throw new InvalidOperationException("上一段录音仍在结束或转写，请稍候。");
            if (WaveInEvent.DeviceCount == 0) throw new InvalidOperationException("没有可用的麦克风，请检查 Windows 输入设备。");

            var operation = new Operation(++_generation, engine);
            var wave = new WaveInEvent
            {
                DeviceNumber = -1, // WAVE_MAPPER: use the Windows default recording device.
                WaveFormat = new WaveFormat(SampleRate, 16, 1),
                BufferMilliseconds = 100,
                NumberOfBuffers = 3
            };
            var capture = new Capture(wave, autoEndpoint);
            operation.Capture = capture;
            _operation = operation;
            wave.DataAvailable += (_, e) => Receive(operation, capture, e);
            wave.RecordingStopped += (_, e) => FinishCapture(operation, capture, e.Exception);
            try
            {
                wave.StartRecording();
                // Auto mode can listen silently for as long as the user leaves it enabled.
                // SpeechEndpoint bounds each utterance to 30 s and idle audio to its pre-buffer.
                if (!autoEndpoint)
                    capture.LimitTimer = new Timer(_ => ReachLimit(operation, capture), null,
                        TimeSpan.FromSeconds(MaximumSeconds), Timeout.InfiniteTimeSpan);
            }
            catch (Exception error)
            {
                _operation = null;
                capture.StopRequested = true;
                capture.Finished = true;
                try { wave.Dispose(); } catch { }
                operation.Cancellation.Dispose();
                throw new InvalidOperationException("麦克风启动失败，请检查设备和麦克风权限。", error);
            }
        }
    }

    internal async Task<string> StopAndTranscribeAsync(CancellationToken cancellationToken = default)
    {
        Operation operation;
        Capture capture;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            operation = _operation ?? throw new InvalidOperationException("请先开始录音。");
            capture = operation.Capture ?? throw new InvalidOperationException("正在转写，请稍候。");
            if (operation.Claimed) throw new InvalidOperationException("正在转写，请稍候。");
            operation.Claimed = true;
            capture.StopRequested = true;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operation.Cancellation.Token);
        using var registration = linked.Token.Register(() => RequestStop(operation, capture));
        try
        {
            RequestStop(operation, capture);
            // RecordingStopped means the device has returned its final available buffers.
            var samples = await capture.Completion.Task.ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return await DecodeAsync(samples, operation.Engine, linked.Token).ConfigureAwait(false);
        }
        finally { CompleteOperation(operation); }
    }

    // For explicit, headless fixture checks; this does not open an input device.
    internal Task<string> TranscribeAsync(float[] samples, CancellationToken cancellationToken = default) =>
        TranscribeAsync(samples, SpeechEngine.SenseVoice, cancellationToken);

    internal async Task<string> TranscribeAsync(float[] samples, SpeechEngine engine, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ValidateClip(samples);
        SpeechEngines.EnsureModels(engine);
        Operation operation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_operation != null) throw new InvalidOperationException("正在录音或转写，请稍候。");
            operation = new Operation(++_generation, engine) { Claimed = true };
            _operation = operation;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operation.Cancellation.Token);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            return await DecodeAsync((float[])samples.Clone(), operation.Engine, linked.Token).ConfigureAwait(false);
        }
        finally { CompleteOperation(operation); }
    }

    internal void Cancel()
    {
        Operation? operation;
        bool alreadyStopped;
        lock (_gate)
        {
            ++_generation; // Suppress already queued progress/limit/failure events as well as old results.
            operation = _operation;
            if (operation?.Capture is { } capture) capture.StopRequested = true;
            operation?.Cancellation.Cancel();
            alreadyStopped = operation?.Capture is { Released: true } && !operation.Claimed;
        }
        if (operation?.Capture is { } active) RequestStop(operation, active);
        if (alreadyStopped && operation != null) CompleteOperation(operation, onlyIfUnclaimed: true);
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        Cancel(); // The capture callback releases the device; a running native decode owns its own disposal.
    }

    internal static void ValidateClip(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length < SampleRate * 0.35) throw new InvalidOperationException("录音太短，请说完一句后再停止。");
        if (samples.Length > MaximumSamples) throw new InvalidOperationException("录音超过 30 秒，请分段输入。");
        double sum = 0, peak = 0;
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample) || Math.Abs(sample) > 1.001f)
                throw new InvalidOperationException("录音数据无效，请重新录音。");
            sum += sample * (double)sample;
            peak = Math.Max(peak, Math.Abs(sample));
        }
        if (peak < 0.002 && Math.Sqrt(sum / samples.Length) < 0.0005)
            throw new InvalidOperationException("没有录到清晰声音，请检查麦克风和输入音量。");
    }

    internal static string CleanTranscript(string? raw)
    {
        // SenseVoice may return language/emotion/event tags. Only remove these metadata tokens.
        var text = Regex.Replace(raw ?? "", @"<\|[^<>]*\|>", "", RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length == 0) throw new InvalidOperationException("没有识别到文字，请重试或直接输入。");
        if (text.Length > 500 || text.Any(c => char.IsControl(c)))
            throw new InvalidOperationException("转写结果无效，请分段输入或重新录音。");
        return text;
    }

    private static Task<string> DecodeAsync(float[] samples, SpeechEngine engine, CancellationToken cancellationToken)
    {
        ValidateClip(samples);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            double squareSum = 0, peak = 0;
            foreach (var sample in samples)
            {
                squareSum += sample * (double)sample;
                peak = Math.Max(peak, Math.Abs(sample));
            }
            Log.Write("speech-clip", FormattableString.Invariant($"engine={SpeechEngines.Id(engine)} samples={samples.Length} seconds={samples.Length / (double)SampleRate:F2} rms={Math.Sqrt(squareSum / samples.Length):F6} peak={peak:F6} rate=16000 channels=1 bits=16"));
            try
            {
                var config = SpeechEngines.CreateConfig(engine);
                // Keep native ownership on this worker. Cancel only discards its result, never disposes it mid-decode.
                using var recognizer = new OfflineRecognizer(config);
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = recognizer.CreateStream();
                if (stream.Handle == IntPtr.Zero) throw new InvalidOperationException($"{SpeechEngines.Label(engine)} 模型无法加载，请重新准备模型文件。");
                stream.AcceptWaveform(SampleRate, samples);
                recognizer.Decode(stream);
                cancellationToken.ThrowIfCancellationRequested();
                return CleanTranscript(stream.Result.Text);
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidOperationException) { throw; }
            catch (Exception error)
            {
                throw new InvalidOperationException($"{SpeechEngines.Label(engine)} 转写失败，请检查模型文件后重试。", error);
            }
        }, cancellationToken);
    }

    private void Receive(Operation operation, Capture capture, WaveInEventArgs buffer)
    {
        double seconds, level;
        bool progress, limit, segmentReady = false;
        lock (_gate)
        {
            if (_operation != operation || capture.Finished || operation.Cancellation.IsCancellationRequested) return;
            var count = capture.Endpoint == null ? Math.Min(buffer.BytesRecorded / 2, MaximumSamples - capture.Count) : buffer.BytesRecorded / 2;
            var incoming = capture.Endpoint == null ? null : new float[count];
            double sum = 0;
            for (var i = 0; i < count; i++)
            {
                var sample = (short)(buffer.Buffer[i * 2] | buffer.Buffer[i * 2 + 1] << 8) / 32768f;
                if (incoming == null) capture.Samples[capture.Count++] = sample;
                else incoming[i] = sample;
                sum += sample * (double)sample;
            }
            if (incoming != null && capture.Endpoint!.Push(incoming) is { } segment)
            {
                Array.Copy(segment.Samples, capture.Samples, segment.Samples.Length);
                capture.Count = segment.Samples.Length;
                Array.Clear(segment.Samples);
                capture.StopRequested = true;
                segmentReady = true;
                Log.Write("speech-endpoint", $"reason={segment.Reason} samples={capture.Count} voicedSeconds={capture.Endpoint.VoicedSeconds:F2}");
            }
            seconds = capture.Endpoint?.SpeechSeconds ?? capture.Count / (double)SampleRate;
            level = count == 0 ? 0 : Math.Min(1, Math.Sqrt(sum / count));
            var now = Stopwatch.GetTimestamp();
            progress = now - capture.LastProgressTick >= Stopwatch.Frequency / 10;
            if (progress) capture.LastProgressTick = now;
            limit = capture.Endpoint == null && capture.Count == MaximumSamples && !capture.StopRequested;
        }
        if (progress) Deliver(operation.Generation, () => RecordingProgress?.Invoke(seconds, level));
        if (segmentReady)
        {
            RequestStop(operation, capture);
            Deliver(operation.Generation, () => SpeechSegmentReady?.Invoke());
        }
        if (limit) ReachLimit(operation, capture);
    }

    private void ReachLimit(Operation operation, Capture capture)
    {
        lock (_gate)
        {
            if (_operation != operation || capture.Finished || capture.StopRequested || operation.Cancellation.IsCancellationRequested) return;
            capture.StopRequested = true;
        }
        RequestStop(operation, capture);
        Deliver(operation.Generation, () => RecordingLimitReached?.Invoke());
    }

    private void RequestStop(Operation operation, Capture capture)
    {
        if (Interlocked.Exchange(ref capture.StopIssued, 1) != 0) return;
        capture.LimitTimer?.Dispose();
        try { capture.Wave.StopRecording(); }
        catch (Exception error) { FinishCapture(operation, capture, new InvalidOperationException("麦克风停止失败，请重新录音。", error)); }
    }

    private void FinishCapture(Operation operation, Capture capture, Exception? error)
    {
        float[] samples;
        lock (_gate)
        {
            if (capture.Finished) return;
            if (capture.Endpoint != null && !capture.StopRequested && error == null && !operation.Cancellation.IsCancellationRequested)
                error = new InvalidOperationException("麦克风监听意外结束，请重新开启语音对话。");
            capture.Finished = true;
            capture.StopRequested = true;
            samples = capture.Samples[..capture.Count];
        }
        capture.LimitTimer?.Dispose();
        try { capture.Wave.Dispose(); } catch { }
        Array.Clear(capture.Samples);
        capture.Endpoint?.Reset();
        bool cancelled, reportFailure, completeOperation;
        lock (_gate)
        {
            // Finished prevents duplicate callbacks. Released is set only after disposal: cancel must
            // not clear busy while the previous microphone handle is still being closed.
            capture.Released = true;
            cancelled = operation.Cancellation.IsCancellationRequested;
            reportFailure = error != null && _operation == operation && !operation.Claimed && !cancelled;
            completeOperation = !operation.Claimed && (error != null || cancelled);
        }
        if (cancelled) capture.Completion.TrySetCanceled();
        else if (error != null) capture.Completion.TrySetException(new InvalidOperationException("麦克风录音失败，请检查设备后重试。", error));
        else capture.Completion.TrySetResult(samples);
        if (completeOperation) CompleteOperation(operation, onlyIfUnclaimed: true);
        if (reportFailure) Deliver(operation.Generation, () => RecordingFailed?.Invoke(
            new InvalidOperationException("麦克风录音失败，请检查设备后重试。", error)));
    }

    private void CompleteOperation(Operation operation, bool onlyIfUnclaimed = false)
    {
        lock (_gate)
        {
            if (_operation != operation) return;
            if (onlyIfUnclaimed && operation.Claimed) return;
            _operation = null;
            operation.Cancellation.Dispose();
        }
    }

    private void Deliver(long generation, Action callback)
    {
        void Guarded()
        {
            lock (_gate)
            {
                if (_disposed || _generation != generation) return;
                try { callback(); } catch { } // UI callbacks must not terminate the capture worker.
            }
        }
        if (_eventContext != null) _eventContext.Post(_ => Guarded(), null);
        else ThreadPool.QueueUserWorkItem(_ => Guarded());
    }

    private sealed class Operation(long generation, SpeechEngine engine)
    {
        internal readonly long Generation = generation;
        internal readonly SpeechEngine Engine = engine;
        internal readonly CancellationTokenSource Cancellation = new();
        internal Capture? Capture;
        internal bool Claimed;
    }

    private sealed class Capture(WaveInEvent wave, bool autoEndpoint)
    {
        internal readonly WaveInEvent Wave = wave;
        internal readonly SpeechEndpoint? Endpoint = autoEndpoint ? new SpeechEndpoint() : null;
        internal readonly float[] Samples = new float[MaximumSamples];
        internal readonly TaskCompletionSource<float[]> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Timer? LimitTimer;
        internal int Count, StopIssued;
        internal long LastProgressTick;
        internal bool StopRequested, Finished, Released;
    }
}
