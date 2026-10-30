using System.Windows;
using System.Windows.Controls;

namespace ScreenGuide;

internal sealed partial class MainWindow
{
    private readonly VoiceChatState _voiceChat = new();
    private readonly Button _voiceChatButton = MakeButton("Voice chat", "开启一次，停顿后自动转写并指路；Esc 停止。录音不上传，转写文字会发送给当前指路模型。");
    private readonly Button _voiceChatControl = MakeButton("Start voice chat", "免按键输入；使用当前选中的本地识别模型。Esc 停止。");
    private readonly TextBlock _voiceHint = new() { Text = "Local · English", FontSize = 11, Foreground = ShellTheme.Muted, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _spokenText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = ShellTheme.Ink, LineHeight = 20 };
    private readonly Border _spokenBubble = new AngledBorder { Background = ShellTheme.SurfaceBlue, BorderBrush = ShellTheme.Line, BorderThickness = new Thickness(1), Padding = new Thickness(15, 8, 15, 8), Margin = new Thickness(0, 0, 0, 10), MaxHeight = 82, Visibility = Visibility.Collapsed };
    private FrameworkElement _typedInput = null!;
    private CancellationTokenSource? _voiceChatRequest;
    private bool _applyingVoiceTranscript;

    private void ToggleVoiceChat()
    {
        if (_voiceChat.Enabled)
        {
            Pause("语音对话已停止，进度与文字保留。", stopVoice: true); return;
        }
        if (_speech.IsBusy) { SetStatus("上一段语音仍在结束，请稍等后开启语音对话。"); return; }
        if (!_modelReady && _manual.IsChecked != true)
        { SetStatus("模型未配置，暂时不能自动指路；可用点按语音转写，或先配置模型。"); return; }
        var target = ChooseWindow(); if (target == 0) return;
        Pause(stopVoice: true);
        _voiceChat.Start(target); _voiceChatRequest = new();
        _spokenText.Text = "你：说一个想找的功能…";
        UpdateVoiceChatUi();
        SetStatus("语音对话已开启。说完停顿约1秒会自动指路；Esc 停止。每句作为新问题，目标点击仍自动续步。");
        Log.Write("voice-chat", $"enabled engine={SpeechEngines.Id(SelectedSpeechEngine())}");
        TryStartVoiceTurn();
    }

    private bool VoiceTargetCurrent()
    {
        if (!_voiceChat.Enabled || !Native.CanTarget(_voiceChat.Target)) return false;
        var foreground = Native.GetForegroundWindow();
        return Native.IsOwnWindow(foreground) || Native.BelongsTo(Native.RootTarget(foreground), _voiceChat.Target);
    }

    private void CheckVoiceTarget(nint foreground)
    {
        if (!_voiceChat.Enabled) return;
        if (!Native.CanTarget(_voiceChat.Target) || (!Native.IsOwnWindow(foreground) &&
            !Native.BelongsTo(Native.RootTarget(foreground), _voiceChat.Target)))
            Pause("目标已隐藏或切换，语音对话已停止，高亮已清除；回到软件后可重新开启。", stopVoice: true);
    }

    // Called on the UI timer. A canceled decoder/device is allowed to finish releasing before rearming.
    private void TryStartVoiceTurn()
    {
        if (_closed || !_voiceChat.Enabled || _voiceChat.Phase != VoiceChatPhase.Waiting ||
            _speech.IsBusy || _speechRequest != null || _busy || _continuing || _hoverWork || _capturing) return;
        if (!VoiceTargetCurrent()) { CheckVoiceTarget(Native.GetForegroundWindow()); return; }
        var generation = _voiceChat.Generation;
        var request = CancellationTokenSource.CreateLinkedTokenSource(_voiceChatRequest!.Token);
        _speechRequest = request; _activeSpeechEngine = SelectedSpeechEngine();
        try
        {
            if (!_voiceChat.Move(generation, VoiceChatPhase.Waiting, VoiceChatPhase.Listening)) return;
            _speech.Start(_activeSpeechEngine, autoEndpoint: true);
            UpdateVoiceChatUi();
            Log.Write("voice-chat", "listening");
        }
        catch (Exception error)
        {
            CancelSpeech();
            SetStatus((error is InvalidOperationException ? error.Message : "麦克风无法开启，请检查默认输入设备和权限。") + " 语音对话已停止。");
            Log.Write("speech-error", error.GetType().Name);
        }
    }

    private async Task FinishVoiceTurn()
    {
        var request = _speechRequest;
        var conversation = _voiceChatRequest;
        var generation = _voiceChat.Generation;
        if (_closed || request == null || conversation == null ||
            !_voiceChat.Move(generation, VoiceChatPhase.Listening, VoiceChatPhase.Transcribing)) return;
        var cancel = conversation.Token;
        var engine = _activeSpeechEngine;
        _transcribing = true; UpdateVoiceChatUi();
        try
        {
            var text = await _speech.StopAndTranscribeAsync(request.Token);
            if (_closed || !_voiceChat.Current(generation) || _speechRequest != request || cancel.IsCancellationRequested) return;
            // StopAndTranscribeAsync has released the capture and native decode before returning.
            _speechRequest = null; _transcribing = false;
            if (!VoiceTargetCurrent()) { CheckVoiceTarget(Native.GetForegroundWindow()); return; }
            CancelCurrent(); _session = null;
            _applyingVoiceTranscript = true;
            try { _question.Text = text; _question.CaretIndex = text.Length; }
            finally { _applyingVoiceTranscript = false; }
            _spokenText.Text = "你：" + text;
            if (!_voiceChat.Move(generation, VoiceChatPhase.Transcribing, VoiceChatPhase.Guiding)) return;
            UpdateVoiceChatUi();
            SetStatus("已听到你的问题，正在指路…");
            Log.Write("voice-chat", $"submitted engine={SpeechEngines.Id(engine)} chars={text.Length}");
            // A click/hover guide can still be unwinding after cancellation. Never drop the new utterance
            // into Guide's busy guard or reuse its old UI snapshot.
            var waitingSince = Environment.TickCount64;
            while (_busy || _continuing || _hoverWork || _capturing || _speech.IsBusy)
            {
                if (Environment.TickCount64 - waitingSince >= 5000)
                    throw new InvalidOperationException("上一轮指路未能及时结束，请稍后重试。");
                await Task.Delay(50, cancel);
            }
            cancel.ThrowIfCancellationRequested();
            if (!_voiceChat.Current(generation) || _closed) return;
            if (!VoiceTargetCurrent()) { CheckVoiceTarget(Native.GetForegroundWindow()); return; }
            await Guide(generation, _voiceChat.Target);
            if (_voiceChat.Move(generation, VoiceChatPhase.Guiding, VoiceChatPhase.Waiting)) UpdateVoiceChatUi();
        }
        catch (OperationCanceledException) { /* User stop/window change owns the visible status. */ }
        catch (Exception error)
        {
            if (!_closed && _voiceChat.Current(generation))
            {
                CancelSpeech();
                SetStatus((error is InvalidOperationException ? error.Message : "本地转写失败，原文字保留。") + " 语音对话已暂停，可重新开启。");
                Log.Write("speech-error", error.GetType().Name);
            }
        }
        finally { request.Dispose(); }
    }

    private void StopVoiceChat()
    {
        bool enabled = _voiceChat.Enabled;
        _voiceChat.Stop();
        var request = _voiceChatRequest; _voiceChatRequest = null;
        request?.Cancel(); request?.Dispose();
        if (enabled) Log.Write("voice-chat", "stopped");
    }

    private void UpdateVoiceChatUi()
    {
        bool enabled = _voiceChat.Enabled;
        _question.IsReadOnly = enabled || _speechRequest != null;
        _speechButton.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        _speechButton.IsEnabled = !_transcribing;
        _voiceChatButton.Content = enabled ? "Stop chat" : "Voice chat";
        _voiceChatControl.Content = enabled ? "Stop voice chat" : "Start voice chat";
        _voiceHint.Text = _voiceChat.Phase switch
        {
            VoiceChatPhase.Listening => "Listening · pause to send",
            VoiceChatPhase.Transcribing => "正在转写 · 暂停收音",
            VoiceChatPhase.Guiding => "正在指路 · 暂停收音",
            VoiceChatPhase.Waiting => "等待继续听…",
            _ => "Local · English"
        };
        _spokenBubble.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (_typedInput != null) _typedInput.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        if (_orb != null) ShellTheme.SetOrbVoiceState(_orb, enabled, _voiceChat.Phase == VoiceChatPhase.Listening);
        ShowcaseEnglish.Apply(_shellCanvas);
        if (_shellReady && !_closed) RefreshShell(false);
    }
}
