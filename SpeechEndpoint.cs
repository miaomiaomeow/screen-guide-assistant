namespace ScreenGuide;

internal enum SpeechEndpointReason { Pause, MaximumLength }
internal sealed record SpeechSegment(float[] Samples, SpeechEndpointReason Reason);

// A small energy endpoint, not a speech-recognition model. It keeps a short rolling pre-buffer
// during silence and one bounded utterance after onset. TV/other people's speech is still speech.
internal sealed class SpeechEndpoint
{
    internal const int SampleRate = 16000;
    internal const int FrameSamples = 320; // 20 ms, independent of the microphone callback size.
    internal const int PreRollSamples = SampleRate * 2 / 5;
    internal const int MaximumSamples = SampleRate * 30;
    private const int StartFrames = 3;
    private const int EndPauseFrames = 55; // 1.1 seconds.
    private const int MinimumVoiceFrames = 35; // 0.7 s of effective voiced frames.
    private const int MinimumSpanFrames = 45; // 0.9 s from onset to last voiced frame.
    private const int MinimumVoiceRunFrames = 6;
    private readonly float[] _frame = new float[FrameSamples];
    private readonly float[] _preRoll = new float[PreRollSamples];
    private readonly float[] _utterance = new float[MaximumSamples];
    private int _frameCount, _preRollCount, _preRollWrite, _sampleCount;
    private int _startRun, _voiceFrames, _voiceRun, _longestVoiceRun, _spanFrames, _lastVoiceFrame, _quietFrames;
    private double _noiseRms = .0008, _minVoiceRms, _maxVoiceRms, _voiceRmsSum;
    private bool _active, _completed, _hadInternalPause;

    internal bool IsSpeechActive => _active;
    internal bool IsCompleted => _completed;
    internal int BufferedSamples => _active || _completed ? _sampleCount : _preRollCount;
    internal double SpeechSeconds => _lastVoiceFrame * FrameSamples / (double)SampleRate;
    internal double VoicedSeconds => _voiceFrames * FrameSamples / (double)SampleRate;

    internal SpeechSegment? Push(ReadOnlySpan<float> samples)
    {
        if (_completed) return null;
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample) || Math.Abs(sample) > 1.001f)
                throw new ArgumentException("端点输入必须为有限的归一化音频样本。", nameof(samples));
            _frame[_frameCount++] = sample;
            if (_frameCount != FrameSamples) continue;
            _frameCount = 0;
            if (ProcessFrame() is { } segment) return segment;
        }
        return null;
    }

    internal void Reset()
    {
        Array.Clear(_frame); Array.Clear(_preRoll); Array.Clear(_utterance);
        _frameCount = _preRollCount = _preRollWrite = _sampleCount = 0;
        _noiseRms = .0008; _completed = false;
        ClearCandidate();
    }

    private SpeechSegment? ProcessFrame()
    {
        double squareSum = 0, peak = 0;
        int crossings = 0;
        for (int i = 0; i < FrameSamples; i++)
        {
            var sample = _frame[i]; squareSum += sample * (double)sample;
            peak = Math.Max(peak, Math.Abs(sample));
            if (i > 0 && (sample >= 0) != (_frame[i - 1] >= 0)) crossings++;
        }
        double rms = Math.Sqrt(squareSum / FrameSamples);
        double threshold = Math.Max(.004, _noiseRms * 2.2);
        // High-frequency wideband noise has frequent crossings. Unvoiced consonants are kept
        // in the pre-buffer/utterance but do not by themselves open or validate an utterance.
        bool voiced = rms >= threshold && peak >= .006 && crossings / (double)FrameSamples < .35;

        if (!_active)
        {
            RememberFrame();
            if (!voiced)
            {
                ClearCandidate();
                _noiseRms = _noiseRms * .98 + rms * .02;
                return null;
            }
            if (_startRun == 0) _minVoiceRms = _maxVoiceRms = rms;
            _startRun++; _minVoiceRms = Math.Min(_minVoiceRms, rms); _maxVoiceRms = Math.Max(_maxVoiceRms, rms);
            _voiceRmsSum += rms;
            if (_startRun < StartFrames) return null;
            _active = true;
            _voiceFrames = _voiceRun = _longestVoiceRun = _spanFrames = _lastVoiceFrame = _startRun;
            CopyPreRoll();
            return null;
        }

        int append = Math.Min(FrameSamples, MaximumSamples - _sampleCount);
        Array.Copy(_frame, 0, _utterance, _sampleCount, append); _sampleCount += append;
        _spanFrames++;
        if (voiced)
        {
            if (_quietFrames >= 3) _hadInternalPause = true;
            _voiceFrames++; _voiceRun++; _lastVoiceFrame = _spanFrames; _quietFrames = 0;
            _longestVoiceRun = Math.Max(_longestVoiceRun, _voiceRun);
            _minVoiceRms = Math.Min(_minVoiceRms, rms); _maxVoiceRms = Math.Max(_maxVoiceRms, rms);
            _voiceRmsSum += rms;
        }
        else { _quietFrames++; _voiceRun = 0; }

        // A steady hum/tone must not yield a new request every 30 seconds. Learn it as the
        // current background after 2 s, provided there were no pauses or meaningful modulation.
        bool modulated = _maxVoiceRms >= _minVoiceRms * 1.3;
        if (_spanFrames >= 100 && _quietFrames == 0 && _voiceFrames == _spanFrames && !modulated)
        {
            double background = _voiceRmsSum / _voiceFrames;
            RejectCandidate(); _noiseRms = Math.Max(_noiseRms, background);
            return null;
        }

        bool eligible = _voiceFrames >= MinimumVoiceFrames && _lastVoiceFrame >= MinimumSpanFrames
            && _longestVoiceRun >= MinimumVoiceRunFrames && modulated;
        if (_quietFrames >= EndPauseFrames)
        {
            if (eligible) return Complete(SpeechEndpointReason.Pause);
            RejectCandidate();
        }
        else if (_sampleCount == MaximumSamples)
        {
            // A continuous, modulated machine sound must not submit every 30 s. Natural long
            // questions contain word/breath pauses; otherwise drop this buffer locally instead.
            if (eligible && _hadInternalPause) return Complete(SpeechEndpointReason.MaximumLength);
            double background = _voiceFrames == 0 ? _noiseRms : _voiceRmsSum / _voiceFrames;
            bool continuous = !_hadInternalPause;
            RejectCandidate();
            if (continuous) _noiseRms = Math.Max(_noiseRms, background);
        }
        return null;
    }

    private void RememberFrame()
    {
        foreach (var sample in _frame)
        {
            _preRoll[_preRollWrite] = sample;
            _preRollWrite = (_preRollWrite + 1) % PreRollSamples;
            _preRollCount = Math.Min(PreRollSamples, _preRollCount + 1);
        }
    }

    private void CopyPreRoll()
    {
        int first = (_preRollWrite - _preRollCount + PreRollSamples) % PreRollSamples;
        for (int i = 0; i < _preRollCount; i++) _utterance[i] = _preRoll[(first + i) % PreRollSamples];
        _sampleCount = _preRollCount;
    }

    private SpeechSegment Complete(SpeechEndpointReason reason)
    {
        _completed = true; _active = false;
        return new(_utterance[.._sampleCount], reason);
    }

    private void RejectCandidate()
    {
        Array.Clear(_utterance, 0, _sampleCount); _sampleCount = 0;
        // Do not carry the rejected noise into the next phrase's pre-buffer.
        Array.Clear(_preRoll); _preRollCount = _preRollWrite = 0;
        ClearCandidate();
    }

    private void ClearCandidate()
    {
        _active = false;
        _hadInternalPause = false;
        _startRun = _voiceFrames = _voiceRun = _longestVoiceRun = _spanFrames = _lastVoiceFrame = _quietFrames = 0;
        _minVoiceRms = _maxVoiceRms = _voiceRmsSum = 0;
    }
}
