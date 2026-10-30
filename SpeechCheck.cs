using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using NAudio.Wave;

namespace ScreenGuide;

// Explicit CLI wiring check using the model publisher's public zh.wav fixture.
// This is not a microphone test or an estimate of accuracy on the user's voice.
internal static class SpeechCheck
{
    internal static async Task<int> Run(string wavPath, string? engine = null, bool autoEndpoint = false)
    {
        const string officialSha256 = "b77f1794fe374a0ba1ee1dc458bfaf9349496cbbfc32780c50ba3c5a7ad8e373";
        var output = Path.Combine(AppContext.BaseDirectory, autoEndpoint ? "speech-auto-endpoint-check.json" : "speech-check.json");
        object report;
        var passed = false;
        var results = new List<EngineResult>();
        try
        {
            SpeechEngine[] selected;
            if (string.IsNullOrWhiteSpace(engine)) selected = [SpeechEngine.SenseVoice];
            else if (engine.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)) selected = SpeechEngines.All.ToArray();
            else if (SpeechEngines.TryParse(engine, out var parsed)) selected = [parsed];
            else throw new InvalidOperationException("语音检查引擎须为 sensevoice、paraformer、zipformer 或 all。");
            var file = new FileInfo(wavPath);
            if (!file.Exists || file.Length > SpeechInput.SampleRate * SpeechInput.MaximumSeconds * 2 + 65536)
                throw new InvalidOperationException("测试音频不存在或超过 30 秒限制。");
            using (var fixture = file.OpenRead())
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(fixture));
                if (!hash.Equals(officialSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("此检查只接受 SHA256 匹配的官方中文测试音频。");
            }
            using var reader = new WaveFileReader(wavPath);
            var format = reader.WaveFormat;
            if (format.Encoding != WaveFormatEncoding.Pcm || format.Channels != 1 || format.BitsPerSample != 16
                || format.SampleRate != SpeechInput.SampleRate || reader.Length % 2 != 0)
                throw new InvalidOperationException("测试音频必须为 16 kHz、单声道、16 bit PCM WAV。");
            if (reader.Length > SpeechInput.SampleRate * SpeechInput.MaximumSeconds * 2)
                throw new InvalidOperationException("测试音频超过 30 秒限制。");

            var bytes = new byte[checked((int)reader.Length)];
            int offset = 0, read;
            while (offset < bytes.Length && (read = reader.Read(bytes, offset, bytes.Length - offset)) > 0) offset += read;
            if (offset != bytes.Length) throw new InvalidOperationException("测试音频未读取完整。");
            var samples = new float[bytes.Length / 2];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = (short)(bytes[i * 2] | bytes[i * 2 + 1] << 8) / 32768f;
            var originalInputSeconds = samples.Length / (double)SpeechInput.SampleRate;
            int? endpointSegments = null;
            if (autoEndpoint)
            {
                var endpoint = new SpeechEndpoint();
                var segments = new List<SpeechSegment>();
                // Replay the public fixture in microphone-sized buffers, then a final quiet pause.
                // No input device, desktop, or network is used by this check.
                for (int pos = 0; pos < samples.Length; pos += 1600)
                    if (endpoint.Push(samples.AsSpan(pos, Math.Min(1600, samples.Length - pos))) is { } segment) { segments.Add(segment); endpoint.Reset(); }
                for (int i = 0; i < 20; i++)
                    if (endpoint.Push(new float[1600]) is { } segment) { segments.Add(segment); endpoint.Reset(); }
                endpointSegments = segments.Count;
                if (segments.Count != 1) throw new InvalidOperationException($"公开语音样例应产生一个语段，实际为 {segments.Count}。");
                samples = segments[0].Samples;
            }
            // "all" is an explicit headless comparison. The interactive input always runs only the selected engine.
            foreach (var selectedEngine in selected)
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    using var input = new SpeechInput();
                    var text = await input.TranscribeAsync(samples, selectedEngine);
                    results.Add(new(SpeechEngines.Id(selectedEngine), SpeechEngines.Label(selectedEngine),
                        true, ExpectedTextMatches(text), text, watch.Elapsed.TotalSeconds, null, null));
                }
                catch (Exception error)
                {
                    results.Add(new(SpeechEngines.Id(selectedEngine), SpeechEngines.Label(selectedEngine),
                        false, false, null, watch.Elapsed.TotalSeconds, error.GetType().Name, error.Message));
                }
            }
            var pipelineSuccessCount = results.Count(result => result.PipelineSucceeded);
            var textMatchCount = results.Count(result => result.ExpectedTextMatched);
            passed = pipelineSuccessCount == selected.Length && textMatchCount == selected.Length;
            report = new
            {
                runtime = "sherpa-onnx / CPU", fixtureOnly = true,
                fixtureSha256 = officialSha256,
                autoEndpoint, endpointSegments, originalInputSeconds,
                microphoneTested = false, inputSeconds = samples.Length / (double)SpeechInput.SampleRate,
                expectedContains = "开放时间", pipelineSuccessCount, textMatchCount, engineCount = selected.Length,
                pipelineSucceeded = pipelineSuccessCount == selected.Length,
                expectedTextMatched = textMatchCount == selected.Length, passed, results,
                note = "同一官方样例分别检查本地推理接线与文字匹配；文字错误原样保留，不代表真人准确率。"
            };
        }
        catch (Exception error)
        {
            report = new
            {
                runtime = "sherpa-onnx / CPU", fixtureOnly = true, microphoneTested = false,
                pipelineSuccessCount = 0, textMatchCount = 0, pipelineSucceeded = false,
                expectedTextMatched = false, passed = false, results, error = error.GetType().Name, message = error.Message
            };
        }
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
        return passed ? 0 : 1;
    }

    internal static bool ExpectedTextMatches(string text) => text.Contains("开放时间", StringComparison.Ordinal);

    private sealed record EngineResult(string Engine, string Label, bool PipelineSucceeded, bool ExpectedTextMatched,
        string? Transcript, double ElapsedSeconds, string? Error, string? Message);
}
