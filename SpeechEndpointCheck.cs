using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ScreenGuide;

// These are synthetic state-machine tests, not speech-recognition or microphone accuracy tests.
internal static class SpeechEndpointCheck
{
    internal static IReadOnlyList<(string Label, bool Passed)> Checks()
    {
        var results = new List<(string Label, bool Passed)>();
        void Check(string label, Func<bool> test)
        {
            try { results.Add((label, test())); }
            catch { results.Add((label, false)); }
        }

        Check("long silence keeps listening with bounded pre-buffer", () =>
        {
            var endpoint = new SpeechEndpoint(); var silence = new float[SpeechEndpoint.FrameSamples];
            for (int frame = 0; frame < 12 * 60 * 50; frame++)
                if (endpoint.Push(silence) != null || endpoint.BufferedSamples > SpeechEndpoint.PreRollSamples) return false;
            return !endpoint.IsCompleted && !endpoint.IsSpeechActive;
        });
        Check("loud steady tone is learned as background instead of requests", () =>
        {
            var endpoint = new SpeechEndpoint();
            return Feed(endpoint, Tone(65, .05), Silence(1.5)) == null && !endpoint.IsCompleted;
        });
        Check("continuous modulated sound cannot submit every 30 seconds", () =>
        {
            var endpoint = new SpeechEndpoint();
            return Feed(endpoint, Speech(65), Silence(1.5)) == null && !endpoint.IsCompleted
                && endpoint.BufferedSamples <= SpeechEndpoint.PreRollSamples;
        });
        Check("wideband stationary noise does not open an utterance", () =>
        {
            var random = new Random(4387); var noise = new float[SpeechEndpoint.SampleRate * 40];
            for (int i = 0; i < noise.Length; i++) noise[i] = (float)((random.NextDouble() * 2 - 1) * .08);
            var endpoint = new SpeechEndpoint();
            return Feed(endpoint, noise, Silence(1.5)) == null && !endpoint.IsCompleted;
        });
        Check("a 20 ms impact is rejected", () => Feed(new(), Silence(.5), Tone(.02, .7), Silence(2)) == null);
        Check("short voiced sound is rejected", () => Feed(new(), Speech(.4), Silence(2)) == null);
        Check("repeated short taps do not accumulate into a valid question", () =>
        {
            var endpoint = new SpeechEndpoint();
            for (int i = 0; i < 20; i++)
                if (Feed(endpoint, Tone(.08, .1), Silence(.1)) != null) return false;
            return Feed(endpoint, Silence(2)) == null;
        });
        Check("one second modulated question produces a pause endpoint", () =>
        {
            var endpoint = new SpeechEndpoint(); var segment = Feed(endpoint, Silence(.5), Speech(1), Silence(1.1));
            return segment?.Reason == SpeechEndpointReason.Pause && endpoint.IsCompleted && endpoint.VoicedSeconds >= .7;
        });
        Check("silence shorter than 1.1 seconds does not send", () =>
        {
            var endpoint = new SpeechEndpoint();
            return Feed(endpoint, Speech(1.2), Silence(1.08)) == null
                && Feed(endpoint, Silence(.02))?.Reason == SpeechEndpointReason.Pause;
        });
        Check("short within-question pause keeps both spoken parts", () =>
        {
            var segment = Feed(new(), Speech(.6), Silence(.5), Speech(.6), Silence(1.1));
            return segment?.Reason == SpeechEndpointReason.Pause && segment.Samples.Length >= SpeechEndpoint.SampleRate * 2.7;
        });
        Check("pre-buffer preserves the very first voiced samples", () =>
        {
            var spoken = Speech(1.2); var segment = Feed(new(), Silence(2), spoken, Silence(1.1));
            if (segment == null) return false;
            int beforeSpeech = SpeechEndpoint.PreRollSamples - SpeechEndpoint.FrameSamples * 3;
            for (int i = 0; i < SpeechEndpoint.FrameSamples * 3; i++)
                if (segment.Samples[beforeSpeech + i] != spoken[i]) return false;
            return true;
        });
        Check("speech immediately after enabling is retained from sample zero", () =>
        {
            var spoken = Speech(1.2); var segment = Feed(new(), spoken, Silence(1.1));
            return segment != null && spoken.Take(SpeechEndpoint.FrameSamples).SequenceEqual(segment.Samples.Take(SpeechEndpoint.FrameSamples));
        });
        Check("long speech is bounded to exactly 30 seconds", () =>
        {
            var endpoint = new SpeechEndpoint(); var words = new List<float[]> { Silence(.5) };
            for (int i = 0; i < 35; i++) { words.Add(Speech(.9)); words.Add(Silence(.1)); }
            var segment = Feed(endpoint, words.ToArray());
            return segment?.Reason == SpeechEndpointReason.MaximumLength && segment.Samples.Length == SpeechEndpoint.MaximumSamples
                && endpoint.BufferedSamples == SpeechEndpoint.MaximumSamples;
        });
        Check("completed segment cannot trigger a duplicate request", () =>
        {
            var endpoint = new SpeechEndpoint(); var first = Feed(endpoint, Speech(1.2), Silence(1.1));
            return first != null && Feed(endpoint, Speech(1.2), Silence(1.1)) == null;
        });
        Check("reset discards pending speech and can listen for a fresh question", () =>
        {
            var endpoint = new SpeechEndpoint(); Feed(endpoint, Speech(.8)); endpoint.Reset();
            return Feed(endpoint, Silence(2)) == null && Feed(endpoint, Speech(1.2), Silence(1.1)) != null;
        });
        Check("rejected short sound does not poison a later valid question", () =>
        {
            var endpoint = new SpeechEndpoint();
            return Feed(endpoint, Speech(.3), Silence(1.1)) == null && Feed(endpoint, Speech(1.2), Silence(1.1)) != null;
        });
        Check("speech after more than 30 seconds idle is still detected", () =>
            Feed(new(), Silence(40), Speech(1.2), Silence(1.1)) != null);
        Check("endpoint does not depend on microphone callback chunk sizes", () =>
        {
            var samples = Silence(.5).Concat(Speech(1.2)).Concat(Silence(1.1)).ToArray();
            var baseline = Feed(new(), samples); var split = new SpeechEndpoint(); SpeechSegment? fragmented = null;
            int offset = 0, iteration = 0; int[] chunks = [1, 17, 319, 643, 61, 1280];
            while (offset < samples.Length && fragmented == null)
            {
                int count = Math.Min(chunks[iteration++ % chunks.Length], samples.Length - offset);
                fragmented = split.Push(samples.AsSpan(offset, count)); offset += count;
            }
            return baseline != null && fragmented != null && baseline.Samples.SequenceEqual(fragmented.Samples)
                && baseline.Reason == fragmented.Reason;
        });
        Check("invalid samples are rejected before entering a speech segment", () =>
        {
            try { new SpeechEndpoint().Push([float.NaN]); return false; } catch (ArgumentException) { return true; }
        });
        return results;
    }

    internal static int Run()
    {
        var checks = Checks();
        var report = new
        {
            syntheticEndpointChecks = true, microphoneTested = false, recognitionAccuracyTested = false,
            passed = checks.Count(check => check.Passed), total = checks.Count,
            checks = checks.Select(check => new { check.Label, check.Passed })
        };
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "speech-endpoint-check.json"), JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return checks.All(check => check.Passed) ? 0 : 1;
    }

    private static SpeechSegment? Feed(SpeechEndpoint endpoint, params float[][] blocks)
    {
        foreach (var block in blocks)
            for (int offset = 0; offset < block.Length; offset += SpeechEndpoint.FrameSamples)
                if (endpoint.Push(block.AsSpan(offset, Math.Min(SpeechEndpoint.FrameSamples, block.Length - offset))) is { } ready) return ready;
        return null;
    }

    private static float[] Silence(double seconds) => new float[(int)Math.Round(seconds * SpeechEndpoint.SampleRate)];

    private static float[] Tone(double seconds, double amplitude)
    {
        var samples = Silence(seconds);
        for (int i = 0; i < samples.Length; i++) samples[i] = (float)(amplitude * Math.Sin(2 * Math.PI * 200 * i / SpeechEndpoint.SampleRate));
        return samples;
    }

    private static float[] Speech(double seconds)
    {
        var samples = Silence(seconds);
        for (int i = 0; i < samples.Length; i++)
        {
            double t = i / (double)SpeechEndpoint.SampleRate;
            samples[i] = (float)(.06 * (.65 + .35 * Math.Sin(2 * Math.PI * 3 * t)) * Math.Sin(2 * Math.PI * 220 * t));
        }
        return samples;
    }
}
