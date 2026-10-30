using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using NAudio.Wave;

namespace ScreenGuide;

internal static class EnglishSpeechCheck
{
    internal static async Task<int> Run(string path)
    {
        const string expectedHash = "EB1EB008904465B74C304AAD8342E8C7D3C6E61FFE9F66ADCACA9CF0F76A93F4";
        if (new FileInfo(path).Length > 1_000_000 || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != expectedHash) return 2;
        using var wav = new WaveFileReader(path);
        if (wav.WaveFormat.SampleRate != 16000 || wav.WaveFormat.Channels != 1 || wav.WaveFormat.BitsPerSample != 16) return 2;
        var bytes = new byte[(int)wav.Length]; wav.ReadExactly(bytes);
        var samples = new float[bytes.Length/2];
        for(int i=0;i<samples.Length;i++) samples[i]=(short)(bytes[i*2] | bytes[i*2+1]<<8)/32768f;
        using var input = new SpeechInput();
        var transcript = await input.TranscribeAsync(samples,SpeechEngine.SenseVoice);
        bool english = transcript.Count(char.IsAsciiLetter)>10 && !transcript.Any(c=>c>='\u4e00'&&c<='\u9fff');
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"english-speech-check.json"), JsonSerializer.Serialize(new {
            passed=english, language="en", model="SenseVoice", fixtureSha256=expectedHash, transcript,
            microphoneTested=false, accuracyMeasured=false,
            note="Official public English sample only; verifies the local pipeline and English output, not human speech accuracy."
        },new JsonSerializerOptions{WriteIndented=true}));
        return english?0:1;
    }
}
