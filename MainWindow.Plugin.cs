using System.Diagnostics;
using System.Windows.Threading;

namespace ScreenGuide;

internal sealed partial class MainWindow
{
    private sealed record PluginTarget(string Id, nint Handle, uint ProcessId, long ProcessStartedAt, string ClassName, string App);
    private sealed record PendingPluginGuide(PluginTarget Target, string Question, GuideMode Mode, long ExpiresAt);
    private readonly Dictionary<string, PluginTarget> _pluginTargets = new(StringComparer.Ordinal);
    private PluginBridge? _pluginBridge;
    private PluginTarget? _pluginBound;
    private PendingPluginGuide? _pendingPluginGuide;

    private void InitializePluginBridge()
    {
        try
        {
            _pluginBridge = new((request, cancel) => Dispatcher.InvokeAsync(async () =>
            {
                cancel.ThrowIfCancellationRequested();
                return request.Command.StartsWith("codex_", StringComparison.Ordinal)
                    ? await HandleCodexRequest(request, cancel) : HandlePluginRequest(request);
            }, DispatcherPriority.Normal, cancel).Task.Unwrap());
            _pluginBridge.Start(); Log.Write("plugin-bridge", "ready");
        }
        catch (Exception error) { Log.Write("plugin-bridge", "unavailable=" + error.GetType().Name); }
    }

    private object HandlePluginRequest(PluginRequest request)
    {
        if (_closed) return new { ok = false, error = "助手已退出。" };
        switch (request.Command)
        {
            case "status":
                return new { ok = true, version = "hybrid-ui-capture-2", instancePid = Environment.ProcessId, modelConfigured = _modelReady, model = _activeModel.DisplayName,
                    usesDesktopConfiguredModel = _codex == null, pluginDecisionProvider = "codex_session", screenshotMode = ModeId(SelectedMode()), microphoneOn = _speech.IsRecording,
                    preferredMode = ModeId(PreferredGuideMode), settings = _settingsNotice.Text,
                    busy = _busy || _capturing || _continuing, pending = _pendingPluginGuide != null,
                    boundTarget = _pluginBound == null ? null : new { targetId = _pluginBound.Id, app = _pluginBound.App },
                    codexSession = _codex == null ? null : new { sessionId = _codex.State.Id, cursor = _codex.State.Cursor, terminal = _codex.State.Terminal,
                        step = _codex.Guide.Count, imageRoute = _codex.Guide.Images }, message = _status.Text };
            case "list_targets":
                var targets = new List<object>(); _pluginTargets.Clear();
                Native.EnumWindows((handle, _) =>
                {
                    if (targets.Count >= 48 || !Native.CanTarget(handle) || Native.IsMenuPopup(handle)) return true;
                    if (!Native.GetWindowRect(handle, out var bounds) || bounds.Right - bounds.Left < 120 || bounds.Bottom - bounds.Top < 80) return true;
                    Native.GetWindowThreadProcessId(handle, out var process);
                    var started = ProcessStartedAt(process); if (started == 0) return true;
                    var target = new PluginTarget(Guid.NewGuid().ToString("N")[..16], handle, process, started, Native.ClassName(handle), Native.AppName(handle));
                    _pluginTargets.Add(target.Id, target);
                    targets.Add(new { targetId = target.Id, app = target.App, windowClass = target.ClassName,
                        width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top, foreground = Native.RootTarget(Native.GetForegroundWindow()) == handle });
                    return true;
                }, 0);
                return new { ok = true, targets, note = "按用户指定软件选择 targetId 后绑定。不会读取文档标题、控件或截图。" };
            case "bind_target":
                if (PluginWorkBusy) return new { ok = false, error = "助手已有指路或语音任务，请先在本体清除或停止后重试。" };
                if (request.TargetId == null || !_pluginTargets.TryGetValue(request.TargetId, out var bind) || !PluginTargetValid(bind))
                    return new { ok = false, error = "目标不存在或已变化，请重新列出窗口并绑定。" };
                Reset(); _pluginBound = bind;
                SetStatus("当前 Agent 插件已绑定：" + bind.App + "。切回该软件后可执行指路。");
                return new { ok = true, targetId = bind.Id, app = bind.App };
            case "guide":
                if (PluginWorkBusy) return new { ok = false, error = "助手已有指路或语音任务，请先在本体清除或停止后重试。" };
                if (_pluginBound == null || !PluginTargetValid(_pluginBound))
                    return new { ok = false, error = "请先列出窗口并明确绑定目标软件。" };
                var question = request.Question?.Trim() ?? "";
                if (!PluginGuidePolicy.ValidQuestion(question))
                    return new { ok = false, error = "问题需为1–500个字符，且不含控制字符。" };
                if (!PluginGuidePolicy.TryMode(request.Mode, out var mode)) return new { ok = false, error = "不支持的屏幕读取模式。" };
                mode = PluginGuidePolicy.ResolveMode(request.Mode, PreferredGuideMode);
                if (!_modelReady) return new { ok = false, error = "请先在本体配置可用模型。" };
                Reset();
                _pendingPluginGuide = new(_pluginBound, question, mode, Environment.TickCount64 + 60000);
                SetStatus("当前 Agent 指路请求已等待。请切回「" + _pluginBound.App + "」；60秒内执行，Esc 取消。");
                Log.Write("plugin-guide", $"queued mode={ModeId(mode)}");
                return new { ok = true, queued = true, app = _pluginBound.App, mode = ModeId(mode), expiresInSeconds = 60,
                    message = "请自行切回目标软件。助手不会激活或点击窗口；当前模型与费用来源于本体配置。" };
            case "clear":
                Pause("当前 Agent 插件已清除高亮、停止语音并取消待执行请求。", stopVoice: true);
                return new { ok = true };
            default: return new { ok = false, error = "未知插件操作。" };
        }
    }

    private static bool PluginTargetValid(PluginTarget target)
    {
        if (!Native.CanTarget(target.Handle) || Native.ClassName(target.Handle) != target.ClassName) return false;
        Native.GetWindowThreadProcessId(target.Handle, out var process);
        return process == target.ProcessId && ProcessStartedAt(process) == target.ProcessStartedAt;
    }

    private static long ProcessStartedAt(uint id)
    {
        try { using var process = Process.GetProcessById(checked((int)id)); return process.StartTime.ToUniversalTime().Ticks; }
        catch { return 0; }
    }

    private void PluginWindowDestroyed(nint handle)
    {
        // Destroy notification invalidates even an HWND that the same process quickly reuses.
        foreach (var target in _pluginTargets.Values.Where(t => t.Handle == handle).ToArray()) _pluginTargets.Remove(target.Id);
        if (_pluginBound?.Handle != handle) return;
        _pluginBound = null; CancelPluginGuide(); CancelCurrent(); FinishCodexGuide("target_closed", "绑定窗口已关闭。");
        SetStatus("当前 Agent 绑定窗口已关闭，请重新列出并绑定目标软件。");
    }

    private static string ModeId(GuideMode mode) => mode switch
    {
        GuideMode.Hybrid => "hybrid", GuideMode.DirectImage => "direct_image", GuideMode.ImageOnControlFailure => "image_on_failure", _ => "controls"
    };

    private bool PluginWorkBusy => _codex is { State.Terminal: false } || _pendingPluginGuide != null || _busy || _continuing || _capturing || _hoverWork ||
        _speechRequest != null || _speech.IsBusy || _voiceChat.Enabled || _session is { Paused: false, Complete: false };

    private void CancelPluginGuide()
    {
        if (_pendingPluginGuide != null) Log.Write("plugin-guide", "cancelled");
        _pendingPluginGuide = null;
    }

    private void TryStartPluginGuide()
    {
        var pending = _pendingPluginGuide; if (pending == null || _closed) return;
        if (PluginGuidePolicy.Expired(Environment.TickCount64, pending.ExpiresAt) || !PluginTargetValid(pending.Target))
        { _pendingPluginGuide = null; SetStatus("当前 Agent 待执行请求已过期或目标不可见，请重新绑定／指路。"); return; }
        if (_busy || _continuing || _capturing || _hoverWork || _speech.IsBusy ||
            Native.RootTarget(Native.GetForegroundWindow()) != pending.Target.Handle) return;
        _pendingPluginGuide = null;
        _lastTarget = pending.Target.Handle; _targetLabel.Text = "目标：" + pending.Target.App;
        WithoutSettingsPersistence(() => { _manual.IsChecked = false; _imageMode.SelectedValue = pending.Mode; });
        _question.Text = pending.Question;
        Log.Write("plugin-guide", "started mode=" + ModeId(pending.Mode));
        _ = Guide(requiredWindow: pending.Target.Handle);
    }
}
