using System.IO;
using SherpaOnnx;

namespace ScreenGuide;

internal enum SpeechEngine { SenseVoice, Paraformer, Zipformer }

// These are three explicit local choices. A missing/failed engine never selects another engine.
internal static class SpeechEngines
{
    internal static IReadOnlyList<SpeechEngine> All { get; } = Array.AsReadOnly(new[]
        { SpeechEngine.SenseVoice, SpeechEngine.Paraformer, SpeechEngine.Zipformer });

    internal static string Id(SpeechEngine engine) => engine switch
    {
        SpeechEngine.SenseVoice => "sensevoice",
        SpeechEngine.Paraformer => "paraformer",
        SpeechEngine.Zipformer => "zipformer",
        _ => throw new ArgumentOutOfRangeException(nameof(engine), "不支持的语音引擎。")
    };

    internal static string Label(SpeechEngine engine) => engine switch
    {
        SpeechEngine.SenseVoice => "SenseVoice · English",
        SpeechEngine.Paraformer => "Paraformer · Chinese",
        SpeechEngine.Zipformer => "Zipformer · multilingual",
        _ => throw new ArgumentOutOfRangeException(nameof(engine), "不支持的语音引擎。")
    };

    internal static bool TryParse(string? value, out SpeechEngine engine)
    {
        engine = value?.Trim().ToLowerInvariant() switch
        {
            "sensevoice" => SpeechEngine.SenseVoice,
            "paraformer" => SpeechEngine.Paraformer,
            "zipformer" => SpeechEngine.Zipformer,
            _ => (SpeechEngine)(-1)
        };
        return All.Contains(engine);
    }

    internal static string ModelDirectory(SpeechEngine engine) =>
        Path.Combine(AppContext.BaseDirectory, "models", Id(engine));

    // Names are relative to ModelDirectory, shared with the download helper's deployment layout.
    internal static string[] RequiredFiles(SpeechEngine engine) => engine switch
    {
        SpeechEngine.SenseVoice or SpeechEngine.Paraformer => ["model.int8.onnx", "tokens.txt"],
        SpeechEngine.Zipformer => ["encoder.int8.onnx", "decoder.onnx", "joiner.int8.onnx", "tokens.txt"],
        _ => throw new ArgumentOutOfRangeException(nameof(engine), "不支持的语音引擎。")
    };

    internal static string[] MissingFiles(SpeechEngine engine)
    {
        var directory = ModelDirectory(engine);
        return RequiredFiles(engine).Where(name => !File.Exists(Path.Combine(directory, name))).ToArray();
    }

    internal static void EnsureModels(SpeechEngine engine)
    {
        var missing = MissingFiles(engine);
        if (missing.Length != 0)
            throw new InvalidOperationException($"缺少 {Label(engine)} 模型：{string.Join("、", missing)}。请运行 prepare-speech.ps1 -Engine {Id(engine)}。");
    }

    internal static OfflineRecognizerConfig CreateConfig(SpeechEngine engine)
    {
        var directory = ModelDirectory(engine); // Reject unknown enum before creating native configuration.
        var config = new OfflineRecognizerConfig();
        config.FeatConfig.SampleRate = SpeechInput.SampleRate;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Tokens = Path.Combine(directory, "tokens.txt");
        config.ModelConfig.NumThreads = 2;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";
        switch (engine)
        {
            case SpeechEngine.SenseVoice:
                config.ModelConfig.SenseVoice.Model = Path.Combine(directory, "model.int8.onnx");
                config.ModelConfig.SenseVoice.Language = "en";
                config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
                break;
            case SpeechEngine.Paraformer:
                config.ModelConfig.Paraformer.Model = Path.Combine(directory, "model.int8.onnx");
                break;
            case SpeechEngine.Zipformer:
                config.ModelConfig.Transducer.Encoder = Path.Combine(directory, "encoder.int8.onnx");
                config.ModelConfig.Transducer.Decoder = Path.Combine(directory, "decoder.onnx");
                config.ModelConfig.Transducer.Joiner = Path.Combine(directory, "joiner.int8.onnx");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(engine), "不支持的语音引擎。");
        }
        return config;
    }
}
