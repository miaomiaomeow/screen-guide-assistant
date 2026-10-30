using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ScreenGuide;

internal sealed partial class MainWindow : Window
{
    private readonly OverlayWindow _overlay = new();
    private readonly TextBox _question = new() { Width = 270, Height = 29, MaxLength = 500, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "输入本轮想找的功能；修改问题会清空本轮进度" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White, Margin = new Thickness(8, 4, 8, 7), FontSize = 12 };
    private readonly TextBlock _targetLabel = new() { Foreground = Brushes.LightSkyBlue, FontSize = 11, Margin = new Thickness(8, 3, 0, 0), MaxWidth = 105, TextTrimming = TextTrimming.CharacterEllipsis, Text = "Target: switch to your app" };
    private readonly TextBlock _mode = new() { Foreground = Brushes.LightGray, FontSize = 11, Margin = new Thickness(8, 3, 0, 0), MaxWidth = 125, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly CheckBox _manual = new() { Content = "Manual: exact control name", FontSize = 11, Foreground = Brushes.White, Focusable = false, VerticalAlignment = VerticalAlignment.Center, ToolTip = "明确的手动模式，输入当前控件完整名称；点击检测可调试，不调用模型" };
    private readonly ComboBox _imageMode = new() { Width = 146, Height = 23, FontSize = 11, Focusable = false, Foreground = Brushes.Black, Background = Brushes.White,
        Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, SelectedValuePath = "Tag",
        ToolTip = "混合（默认）：每步读取控件并发送当前截图。仅控件：不截图。直接截图：每步只发图。失败时截图：按已支持的控件失败原因转图片。图片含当前窗口可见内容，发给当前模型、不落盘；黄框来自控件，白框为图片估计。读取模式会保存。" };
    private readonly Button _speechButton = MakeButton("Voice", "使用旁边选中的本地语音方案；点击录音，再点结束转成文字，不自动指路，Esc 取消");
    private readonly ComboBox _speechEngine = new() { Width = 132, Height = 25, FontSize = 11, Focusable = false,
        Foreground = Brushes.Black, Background = Brushes.White, Margin = new Thickness(4, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center,
        SelectedValuePath = "Tag", ToolTip = "语音识别方案：每次只使用所选模型。切换会取消本次录音或转写，原文字保留；三个方案均在本地运行。" };
    private readonly SpeechInput _speech = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Native.WinEventProc _eventCallback;
    private readonly Native.KeyboardProc _keyboardCallback;
    private readonly Native.MouseProc _mouseCallback;
    private readonly PointerClickTracker _clicks = new();
    private IntentMapper _mapper;
    private VisionMapper _visionMapper;
    private bool _modelReady;
    private readonly bool _acceptance;
    private ModelConfig _activeModel = new();
    private GuideSession? _session;
    private GuidanceFrame? _frame, _pressedFrame;
    private Task? _uiaWork;
    private Task<bool>? _validation, _imageValidation;
    private CancellationTokenSource? _request;
    private CancellationTokenSource? _speechRequest;
    private SpeechEngine _activeSpeechEngine = SpeechEngine.Zipformer;
    private bool _transcribing;
    private nint _hwnd, _hook, _keyboardHook, _mouseHook, _lastTarget, _requestWindow;
    private int _version, _ticks, _frameToken, _hoverAttempts;
    private long _validatedAt, _hoverSince, _nextHover;
    private bool _collapsed, _busy, _closed, _hoverWork, _continuing, _capturing;

    internal MainWindow(bool acceptance = false, bool preview = false)
    {
        _shellPreview = preview;
        LoadSettings();
        _acceptance = acceptance;
        ModelConfig config;
        try
        {
            if (preview) config = new(); // Offscreen checks never read user profiles or secrets.
            else
            {
                _modelProfiles = _profileStore.Load();
                config = _profileStore.Resolve(_modelProfiles.Profiles.Single(p => p.Id == _modelProfiles.ActiveId));
            }
        }
        catch (Exception error)
        {
            _profileLoadError = error is InvalidOperationException ? error.Message : "模型方案读取失败，原文件保留。";
            config = new(); // A broken selected profile must not silently send to another provider.
            Log.Write("config", "profiles-load-disabled");
        }
        _activeModel = config;
        _modelReady = config.Ready; _mapper = new(config); _visionMapper = new(config);
        BuildShell(config.DisplayName);
        ApplySavedSettings();
        SetStatus(_modelReady ? "已恢复读取设置；混合模式每步读取控件和截图。点击目标后自动续步，Esc 暂停。" : "本体模型未配置；可连接 Codex / Claude Code 指路，或勾选手动模式输入控件原名调试。");
        if (_profileLoadError != null) SetStatus(_profileLoadError + " 请在控制面板的模型配置中检查并明确选择方案；当前未启用模型。");
        _eventCallback = OnWinEvent;
        _keyboardCallback = (code, message, data) =>
        {
            if (!_closed && code >= 0 && message.ToInt32() is 0x100 or 0x104 && Marshal.ReadInt32(data) == 0x1B)
                Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => { if (!_closed) { Pause("已清除高亮并停止语音，进度保留；Ctrl+Alt+G 可继续。", stopVoice: true); ShellEscape(); } }));
            return Native.CallNextHookEx(_keyboardHook, code, message, data);
        };
        _mouseCallback = OnMouse;
        if (!preview) SourceInitialized += InitializeNative;
        _timer.Tick += Tick; if (!preview) _timer.Start();
        Closed += (_, _) =>
        {
            PersistSettings();
            _closed = true; _pluginBridge?.Dispose(); ClearModelSecretInput(); CloseShell(); Reset(); _speech.Dispose(); _timer.Stop();
            if (_hook != 0) Native.UnhookWinEvent(_hook);
            if (_keyboardHook != 0) Native.UnhookWindowsHookEx(_keyboardHook);
            if (_mouseHook != 0) Native.UnhookWindowsHookEx(_mouseHook);
            if (_hwnd != 0) { Native.UnregisterHotKey(_hwnd, 1); Native.UnregisterHotKey(_hwnd, 2); Native.UnregisterHotKey(_hwnd, 3); }
            _overlay.Close(); Log.Write("exit", "continuous-closed");
        };
    }
    private static Button MakeButton(string title, string tip) { var button = ShellTheme.RoundedButton(title); button.ToolTip = tip; return button; }
    private void InitializeNative(object? sender, EventArgs args)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        var style = Native.GetWindowLongPtr(_hwnd, Native.GwlExStyle).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GwlExStyle, new nint(style | Native.WsExNoActivate | (_acceptance ? 0 : Native.WsExToolWindow)));
        HwndSource.FromHwnd(_hwnd).AddHook((nint h, int msg, nint w, nint l, ref bool handled) =>
        {
            if (msg == Native.WmMouseActivate)
            {
                Native.GetPhysicalCursorPos(out var pt);
                bool typing = EditableShellHit(new Point(pt.X, pt.Y));
                var ex = Native.GetWindowLongPtr(_hwnd, Native.GwlExStyle).ToInt64();
                Native.SetWindowLongPtr(_hwnd, Native.GwlExStyle, new nint(typing ? ex & ~Native.WsExNoActivate : ex | Native.WsExNoActivate));
                handled = true; return new nint(typing ? 1 : 3);
            }
            if (msg == 0x84 && ShellPassThrough(l)) { handled = true; return new nint(-1); }
            if (msg is 0x7e or 0x2e0 or 0x1a) ScheduleShellDisplayRefresh();
            if (msg == Native.WmHotkey && w.ToInt32() is 1 or 3) { handled = true; _ = Guide(); }
            if (msg == Native.WmHotkey && w.ToInt32() == 2) { handled = true; Close(); }
            return 0;
        });
        if (!Native.RegisterHotKey(_hwnd, 1, 0x4003, 0x47)) SetStatus("Ctrl+Alt+G 被占用，请使用指路按钮。");
        Native.RegisterHotKey(_hwnd, 2, 0x4003, 0x51);
        Native.RegisterHotKey(_hwnd, 3, 0x4006, 0x47); // Ctrl+Shift+G: Alt can dismiss native menus.
        _hook = Native.SetWinEventHook(3, 0x800B, 0, _eventCallback, 0, 0, 2);
        _keyboardHook = Native.SetWindowsHookEx(13, _keyboardCallback, Native.GetModuleHandle(null), 0);
        _mouseHook = Native.SetMouseHook(14, _mouseCallback, Native.GetModuleHandle(null), 0);
        if (_keyboardHook == 0) SetStatus("全局 Esc 监听不可用，请使用清除按钮。");
        if (_mouseHook == 0) SetStatus("点击监听不可用；请用 Ctrl+Alt+G 手动继续，鼠标无需移出菜单。");
        TrackForeground(Native.GetForegroundWindow());
        InitializePluginBridge();
        Log.Write("start", $"mode=continuous build=hybrid-settings-1 speechDefault={SpeechEngines.Id(SelectedSpeechEngine())} microphoneDefault=false screenMode={ModeId(SelectedMode())} os={Environment.OSVersion.Version} modelConfigured={_modelReady} mouseHook={_mouseHook != 0}");
    }
    private nint OnMouse(int code, nint message, nint data)
    {
        // Keep this hook short: only coordinates/native hit testing, never UIA, HTTP or file I/O.
        try
        {
            if (!_closed && code >= 0 && message.ToInt32() is 0x201 or 0x202)
            {
                var pt = Marshal.PtrToStructure<Native.POINT>(data); var point = new Point(pt.X, pt.Y);
                var now = Environment.TickCount64;
                if (message.ToInt32() == 0x201)
                {
                    _clicks.Reset(); _pressedFrame = null;
                    var frame = _frame;
                    if (frame != null && _session is { Paused: false, Complete: false } && !_busy && !_capturing &&
                        _validatedAt != 0 && now - _validatedAt <= 1500 && frame.Control.Bounds.Contains(point) &&
                        Native.GetWindowRect(frame.Control.Window, out var wr) && Native.SameRect(wr.ToRect(), frame.Control.WindowBounds) &&
                        Native.CanTarget(frame.Control.Window) && HitBelongs(frame, Native.WindowFromPoint(pt)))
                    {
                        _clicks.Down(frame.Token, frame.Control.Bounds, point, now); _pressedFrame = frame;
                        _continuing = true; // An owned dialog can gain focus during the user's press.
                    }
                }
                else
                {
                    var token = _clicks.Up(point, now); var frame = _pressedFrame; _pressedFrame = null;
                    if (frame != null && token == frame.Token)
                        Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => { if (!_closed) _ = Advance(frame, "clicked"); }));
                    else if (frame != null) _continuing = false;
                }
            }
        }
        catch { /* Do not block mouse input even if the target disappears in this callback. */ }
        return Native.CallNextHookEx(_mouseHook, code, message, data);
    }
    private void TrackForeground(nint foreground)
    {
        CheckVoiceTarget(foreground);
        if (Native.IsOwnWindow(foreground)) return;
        var target = Native.RootTarget(foreground);
        if (Native.CanTarget(target) && _lastTarget != target)
        {
            _lastTarget = target; _targetLabel.Text = "目标：" + Native.AppName(target);
        }
        if (_session is not { Paused: false, Complete: false } session || target == session.Window) return;
        // Only a newly opened owned dialog can continue the same guide across windows.
        if (_continuing && Native.CanTarget(target) && Native.BelongsTo(target, session.Window))
        {
            session.Window = target; _requestWindow = target; Log.Write("session", "owned-dialog"); return;
        }
        Pause("窗口已切换，高亮已清除，本轮进度保留；回到原窗口后按指路继续。");
    }
    private void OnWinEvent(nint hook, uint type, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (_closed) return;
        if (type == 3 || type is 0xA or 0x16 or 0x8001 or 0x8003 || (type == 0x800B && objectId == 0 && childId == 0))
            Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
            {
                if (_closed) return;
                if (type == 0x8001 && objectId == 0 && childId == 0) PluginWindowDestroyed(hwnd);
                if (type == 3) { TrackForeground(hwnd); return; }
                var target = _frame?.Control.Window ?? (_busy ? _requestWindow : _continuing ? _session?.Window ?? 0 : 0);
                if (target != 0 && hwnd == target)
                {
                    Native.GetWindowRect(hwnd, out var wr);
                    // Electron sends child hide/destroy events with the main HWND. A menu
                    // closing is not the root window disappearing; verify native visibility.
                    if (type is 0xA or 0x16 || (type is 0x8001 or 0x8003 && !Native.CanTarget(hwnd)) ||
                        (_frame != null && !Native.SameRect(wr.ToRect(), _frame.Control.WindowBounds)))
                        Pause("目标窗口已变化，高亮已清除，进度保留；请重新指路。");
                }
            }));
    }
    private nint ChooseWindow()
    {
        var foreground = Native.GetForegroundWindow(); TrackForeground(foreground);
        var target = Native.IsOwnWindow(foreground) ? _lastTarget : Native.RootTarget(foreground);
        if (!Native.CanTarget(target)) { SetStatus("请先切到要指路的软件，再点击指路或按 Ctrl+Alt+G。"); return 0; }
        return target;
    }
    private static bool IsCurrent(nint window) => Native.CanTarget(window) &&
        (Native.IsOwnWindow(Native.GetForegroundWindow()) || Native.IsTargetForeground(Native.GetForegroundWindow(), window));
    private async Task<T> ReadUi<T>(Func<T> work, int timeout, CancellationToken cancel)
    {
        if (_validation is { IsCompleted: false }) await _validation.WaitAsync(TimeSpan.FromMilliseconds(400), cancel);
        if (_uiaWork is { IsCompleted: false }) await _uiaWork.WaitAsync(TimeSpan.FromMilliseconds(1000), cancel);
        var task = Task.Run(work); _uiaWork = task;
        _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        return await task.WaitAsync(TimeSpan.FromMilliseconds(timeout), cancel);
    }
    private sealed class ControlReadException(ControlReadFailure failure, Exception cause) : Exception(cause.Message, cause)
    {
        internal ControlReadFailure Failure { get; } = failure;
    }
    private async Task<T> ReadControls<T>(Func<T> work, int timeout, CancellationToken cancel)
    {
        try { return await ReadUi(work, timeout, cancel); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
        catch (TimeoutException ex) { throw new ControlReadException(ControlReadFailure.ReadTimeout, ex); }
        catch (Exception ex) { throw new ControlReadException(ControlReadFailure.ReadError, ex); }
    }
    private async Task Guide(long? voiceGeneration = null, nint voiceTarget = default, nint requiredWindow = default)
    {
        if (_codex != null) Reset("本地指路已接管，当前 Agent 本轮已取消。");
        if (_speechRequest != null || _speech.IsBusy) { SetStatus(_voiceChat.Enabled ? "语音对话正在听或转写，停顿后会自动指路；Esc 停止。" : "正在录音或转写；结束后确认文字，再点击指路。Esc 可取消。"); return; }
        if (_busy || _continuing || _capturing || _hoverWork) { SetStatus("正在读取或选择下一步，Esc 可暂停。"); return; }
        var question = _question.Text.Trim();
        if (question.Length == 0) { SetStatus("请先输入想找的功能。"); return; }
        if (_manual.IsChecked != true && !_modelReady)
        { SetStatus("本体模型未配置；请在控制面板配置模型，或通过 当前 Agent 指路，也可使用手动控件调试。"); return; }
        var hwnd = ChooseWindow(); if (hwnd == 0) return;
        // ChooseWindow can observe a foreground switch and stop a voice conversation. Do not
        // submit that conversation's already decoded question against the newly selected app.
        if (voiceGeneration is { } generation && (!_voiceChat.Current(generation) || !Native.BelongsTo(hwnd, voiceTarget))) return;
        if (requiredWindow != 0 && hwnd != requiredWindow) return;
        var manual = _manual.IsChecked == true;
        var mode = SelectedMode();
        if (_session == null || _session.Question != question || _session.Manual != manual || _session.Mode != mode || _session.Window != hwnd || _session.Complete)
            _session = new(question, hwnd, manual, mode);
        _session.Paused = false;
        await RunGuide(_session);
    }
    private GuideMode SelectedMode() => _manual.IsChecked == true ? GuideMode.Controls : _imageMode.SelectedValue is GuideMode mode ? mode : PreferredGuideMode;
    private bool ImageRouteActive(GuideSession? session) => (_modelReady || (_codex is { } external && external.Guide == session && !external.State.Terminal)) && _session == session &&
        session is { Manual: false, Paused: false, Complete: false, Images: true } && SelectedMode() == session.Mode;
    private async Task RunGuide(GuideSession session, ControlSnapshot? prepared = null, bool automatic = false, IReadOnlySet<string>? previousEntries = null, PixelSnapshot? imagePrepared = null, ControlReadFailure? fallback = null)
    {
        CancelCurrent(); session.Paused = false;
        if (session.Count >= GuideSession.MaxInteractions) { Pause("本轮已到 12 次交互上限，已暂停；请重置后缩小问题范围。"); return; }
        var hwnd = session.Window; _requestWindow = hwnd; _busy = true;
        var version = _version; var request = new CancellationTokenSource(); _request = request;
        var initialBounds = Native.GetWindowRect(hwnd, out var initialRect) ? initialRect.ToRect() : Rect.Empty;
        bool Current() => !_closed && version == _version && _session == session && !session.Paused;
        try
        {
            if (session.Mode == GuideMode.Hybrid)
            {
                ControlSnapshot? controls = null;
                SetStatus($"第 {session.Count + 1} 步：正在读取控件和当前截图…");
                try { controls = await ReadControls(() => Locator.Capture(hwnd), 2500, request.Token); }
                catch (ControlReadException error) { Log.Write("hybrid-controls", "unavailable=" + error.Failure); }
                if (!Current()) return;
                // Always take a new picture, including when UIA exposes only the outer shell.
                await RunImageGuide(session, null, automatic, request.Token, Current, controls); return;
            }
            if (session.Images)
            {
                await RunImageGuide(session, imagePrepared, automatic, request.Token, Current); return;
            }
            if (fallback is { } reason)
            {
                if (!await TryImageFallback(session, reason, initialBounds, automatic, request.Token, Current))
                    if (Current()) Pause("菜单更新仍不可读，已暂停；请重新指路。");
                return;
            }
            SetStatus($"第 {session.Count + 1} 步：正在读取当前界面…");
            var snapshot = prepared?.Window == hwnd ? prepared : await ReadControls(() => Locator.Capture(hwnd), 2500, request.Token);
            if (!Current()) return;
            if (automatic && previousEntries != null && session.Memory.Interactions.LastOrDefault() is { Action: "open_entry", Event: "clicked" } parent &&
                (parent.Role is "MenuItem" or "TreeItem" || session.Mode == GuideMode.ImageOnControlFailure) &&
                !GuideSession.HasNewEntries(previousEntries, snapshot.Context))
            {
                // Native popup menus and UIA providers may settle after the first read. Poll
                // locally a finite number of times; never request a model on an unchanged menu.
                for (int retry = 0; retry < 3 && Current() && IsCurrent(hwnd); retry++)
                {
                    await Task.Delay(180, request.Token);
                    snapshot = await ReadControls(() => Locator.Capture(hwnd), 2500, request.Token);
                    if (GuideSession.HasNewEntries(previousEntries, snapshot.Context)) break;
                }
                if (Current() && !GuideSession.HasNewEntries(previousEntries, snapshot.Context))
                {
                    Log.Write("menu-wait", "no-new-readable-items");
                    if (!await TryImageFallback(session, ControlReadFailure.MenuNotUpdated, initialBounds, automatic, request.Token, Current))
                        if (Current()) Pause("检测到点击，但还没读到展开后的新菜单项；请保持菜单展开，再按 Ctrl+Shift+G 重读。");
                    return;
                }
            }
            if (!Current()) return;
            Log.Write("capture", $"app={snapshot.Context.App} count={snapshot.Candidates.Count} application={snapshot.ApplicationCandidateCount} chrome={snapshot.WindowChromeCandidateCount} partial={snapshot.Context.Partial} interactions={session.Count} popups={snapshot.PopupCount} nodes={snapshot.NodesRead} ms={snapshot.ElapsedMs} reason={snapshot.PartialReason}");
            Log.Write("uia-coverage", $"hwnd={hwnd} class={Native.ClassName(hwnd)} owner={Native.GetWindow(hwnd, 4)} rootOwner={Native.GetAncestor(hwnd, 3)} roles=" + string.Join(",", snapshot.RoleCounts.Select(p => p.Key + ":" + p.Value)) + " rejected=" + string.Join(",", snapshot.RejectedCounts.Select(p => p.Key + ":" + p.Value)));
            if (!IsCurrent(hwnd)) { Pause("窗口已切换，本轮已暂停，进度保留。"); return; }
            if (snapshot.Candidates.Count == 0)
            {
                if (!await TryImageFallback(session, ControlReadFailure.EmptyControls, initialBounds, automatic, request.Token, Current))
                    if (Current()) Pause("当前没有可读的可见入口，请自行展开目标区域后再指路。" + (snapshot.Context.Partial ? "（本次读取不完整）" : ""));
                return;
            }
            if (!session.Manual && snapshot.ApplicationCandidateCount == 0)
            {
                if (!await TryImageFallback(session, ControlReadFailure.NoApplicationEntries, initialBounds, automatic, request.Token, Current))
                    if (Current()) Pause("只读到窗口外壳控件，没有读到软件内的功能入口；可选择“控件失败时截图”后再试。");
                return;
            }
            SelectionDecision decision;
            if (session.Manual) decision = Locator.Manual(snapshot.Context, session.Question);
            else
            {
                SetStatus($"第 {session.Count + 1} 步：模型正在结合本轮 {session.Count} 次交互选择入口…");
                var watch = Stopwatch.StartNew();
                var applicationContext = snapshot.Context with { Controls = snapshot.Candidates.Where(c => !c.IsWindowChrome).Select(c => c.Option).ToArray() };
                decision = await _mapper.Select(session.Question, applicationContext, request.Token, session.Memory);
                Log.Write("selection", $"action={decision.Action} failure={decision.Failure} id={decision.Id ?? "none"} ms={watch.ElapsedMilliseconds} interactions={session.Count}");
            }
            if (!Current()) return;
            if (!IsCurrent(hwnd)) { Pause("窗口已切换，本轮已暂停，进度保留。"); return; }
            if (decision.Action == "done")
            {
                session.Complete = true; SetStatus("已检测到目标入口的点击，本轮指路结束。新问题会开始新一轮。");
                Log.Write("session", $"done interactions={session.Count}"); return;
            }
            if (!decision.CanPoint)
            {
                var message = decision.Message + (snapshot.Context.Partial ? "（本次只读到部分控件）" : "");
                if (!await TryOutcomeFallback(session, ImageFallbackPolicy.ForSelection(decision), message, initialBounds, automatic, request.Token, Current))
                    if (Current()) Pause(message);
                return;
            }
            var option = snapshot.Candidates.Single(c => c.Option.Id == decision.Id).Option;
            if (automatic && session.RepeatsLast(option))
            {
                const string message = "模型仍指向刚操作过的入口，无法确认下一步。";
                if (!await TryOutcomeFallback(session, ControlReadFailure.RepeatedEntry, message, initialBounds, automatic, request.Token, Current))
                    if (Current()) Pause(message + "已暂停，请检查界面后再指路。");
                return;
            }
            SetStatus("正在核实所选控件的实时边界…");
            var result = await ReadControls(() => Locator.Confirm(snapshot, decision), 1500, request.Token);
            if (!Current()) return;
            if (result.Control == null)
            {
                if (!(result.ReadFailed && await TryImageFallback(session, ControlReadFailure.ReadError, initialBounds, automatic, request.Token, Current)))
                    if (Current()) Pause(result.Message);
                return;
            }
            Native.GetWindowRect(hwnd, out var wr);
            if (!IsCurrent(hwnd) || !Native.SameRect(wr.ToRect(), result.Control.WindowBounds)) { Pause("窗口已移动或切换，请重新指路。"); return; }
            _frame = new(++_frameToken, result.Control, option, decision.Action, GuideSession.Entries(snapshot.Context));
            _validatedAt = Environment.TickCount64; _hoverSince = 0; _nextHover = 0; _hoverAttempts = 0;
            _overlay.Highlight(result.Control.Bounds);
            Log.Write("located", $"step={session.Count + 1} token={_frame.Token} app={snapshot.Context.App} role={option.Role} hwnd={hwnd} rect={result.Control.Bounds}");
            SetStatus($"第 {session.Count + 1} 步：" + (session.Manual ? $"手动指出「{option.Name}」；点击后结束调试。" : result.Message));
        }
        catch (OperationCanceledException) { }
        catch (ControlReadException ex)
        {
            Log.Write("read-error", $"reason={ex.Failure}");
            if (!await TryImageFallback(session, ex.Failure, initialBounds, automatic, request.Token, Current))
                if (Current()) Pause(ex.Failure == ControlReadFailure.ReadTimeout ? "控件读取超时，已暂停；请稍后再次指路。" : "控件读取失败，已暂停；请恢复目标软件再指路。");
        }
        catch (TimeoutException) { if (Current()) Pause(session.Images ? "图片核实超时，已暂停；请重新指路。" : "控件读取超时，已暂停；请稍后再次指路。"); Log.Write("read-error", "timeout"); }
        catch (InvalidOperationException ex) { if (Current()) Pause(ex.Message); Log.Write("uia-error", "invalid-state"); }
        catch (Exception ex) { Log.Write("read-error", ex.GetType().Name); if (Current()) Pause(session.Images ? "图片读取或定位失败，已暂停；请恢复目标软件再指路。" : "控件不可读或已消失，已暂停；请回到目标软件再指路。"); }
        finally
        {
            if (_request == request) { _busy = false; _requestWindow = 0; _request = null; _continuing = false; }
            request.Dispose();
        }
    }
    private static string FailureText(ControlReadFailure reason) => reason switch {
        ControlReadFailure.EmptyControls => "没有可读入口",
        ControlReadFailure.NoApplicationEntries => "只读到窗口外壳，没有业务入口",
        ControlReadFailure.ReadTimeout => "控件读取超时",
        ControlReadFailure.MenuNotUpdated => "未读到菜单更新",
        ControlReadFailure.SelectionNotFound => "控件路线未找到匹配入口",
        ControlReadFailure.SelectionAmbiguous => "控件路线存在歧义",
        ControlReadFailure.SelectionLowConfidence => "控件路线置信度不足",
        ControlReadFailure.RepeatedEntry => "控件路线重复同一入口",
        _ => "控件读取失败"
    };
    private async Task<bool> TryOutcomeFallback(GuideSession session, ControlReadFailure? reason, string message,
        Rect expectedBounds, bool automatic, CancellationToken cancel, Func<bool> current)
    {
        if (reason == null || session.Manual || !_modelReady || SelectedMode() != GuideMode.ImageOnControlFailure ||
            !current() || cancel.IsCancellationRequested || !IsCurrent(session.Window)) return false;
        // Show the actual control-route outcome before making the separately authorized image request.
        SetStatus(message + "\n将尝试当前截图定位…");
        Log.Write("control-outcome-fallback", $"reason={reason.Value} interactions={session.Count}");
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(350, cancel);
            if (!current() || cancel.IsCancellationRequested) return true;
            return await TryImageFallback(session, reason.Value, expectedBounds, automatic, cancel, current);
        }
        catch (OperationCanceledException) { return true; }
    }
    private async Task<bool> TryImageFallback(GuideSession session, ControlReadFailure reason, Rect expectedBounds, bool automatic, CancellationToken cancel, Func<bool> current)
    {
        // Only explicit read failures and the user's selected outcome categories reach this method.
        // Service/protocol errors, disabled entries, occlusion and window changes cannot authorize it.
        if (!current() || cancel.IsCancellationRequested || !_modelReady || SelectedMode() != GuideMode.ImageOnControlFailure ||
            !IsCurrent(session.Window) || expectedBounds.IsEmpty || !Native.GetWindowRect(session.Window, out var wr) ||
            !Native.SameRect(wr.ToRect(), expectedBounds) || !session.TryActivateImageFallback(reason)) return false;
        Log.Write("image-fallback", $"reason={reason} interactions={session.Count}");
        SetStatus(FailureText(reason) + "，本轮已转图片并保留进度；将发送当前窗口图像。");
        try { await RunImageGuide(session, null, automatic, cancel, current); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { if (current()) Pause("图片核实超时，已暂停；请重新指路。"); }
        catch (InvalidOperationException ex) { if (current()) Pause(ex.Message); Log.Write("fallback-error", "invalid-state"); }
        catch (Exception ex) { if (current()) Pause("图片读取或定位失败，已暂停；请恢复目标软件再指路。"); Log.Write("fallback-error", ex.GetType().Name); }
        return true;
    }
    private async Task<PixelSnapshot> CaptureImage(nint window, CancellationToken cancel)
    {
        if (_capturing) throw new InvalidOperationException("正在读取图片，请稍后指路。");
        if (!ImageRouteActive(_session)) throw new OperationCanceledException();
        if (!IsCurrent(window)) throw new InvalidOperationException("窗口已切换，未发送截图；请回到目标软件再指路。");
        _capturing = true;
        var restorePopups = SuppressShellPopups();
        var opacity = Opacity; var overlayOpacity = _overlay.Opacity;
        try
        {
            // Change only our own pixels, never activate, move or click the target window.
            // Keep the HWNDs in place so hovering menus retain their input state.
            Opacity = 0; _overlay.Opacity = 0;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(80, cancel);
            cancel.ThrowIfCancellationRequested();
            if (!ImageRouteActive(_session) || !IsCurrent(window)) throw new OperationCanceledException();
            var hidden = new HashSet<nint> { _hwnd, new WindowInteropHelper(_overlay).Handle };
            var snapshot = await Task.Run(() => ScreenCapture.Capture(window, hidden), cancel).WaitAsync(TimeSpan.FromSeconds(2), cancel);
            if (!ImageRouteActive(_session) || !IsCurrent(window)) throw new OperationCanceledException();
            return snapshot;
        }
        finally
        {
            if (!_closed) { Opacity = opacity; _overlay.Opacity = overlayOpacity; }
            restorePopups();
            _capturing = false;
        }
    }
    private async Task RunImageGuide(GuideSession session, PixelSnapshot? prepared, bool automatic, CancellationToken cancel, Func<bool> current, ControlSnapshot? controls = null)
    {
        var window = session.Window;
        if (!current() || !ImageRouteActive(session)) return;
        SetStatus($"第 {session.Count + 1} 步：" + (session.ImageFallbackReason is { } reason ? FailureText(reason) + "，本轮已转图片…" : "正在读取当前窗口图片…"));
        var image = prepared?.Window == window ? prepared : await CaptureImage(window, cancel);
        if (!current() || !ImageRouteActive(session)) return;
        if (controls != null && !Native.SameRect(controls.WindowBounds, image.WindowBounds))
        { Pause("控件和截图读取之间窗口已移动，请重新指路。"); return; }
        if (!IsCurrent(window)) { Pause("窗口已切换，未发送截图，本轮已暂停。"); return; }
        SetStatus($"第 {session.Count + 1} 步：图像模型正在读取当前图片、选择入口…");
        var context = session.Mode == GuideMode.Hybrid ? new SelectionContext(Native.AppName(window), controls?.Context.Partial ?? true,
            controls == null ? [] : IntentMapper.SelectableControls(controls.Candidates.Where(c => !c.IsWindowChrome).Select(c => c.Option).ToArray())) : null;
        var decision = await _visionMapper.Select(session.Question, Native.AppName(window), image, session.Memory, cancel, context);
        if (!current() || !ImageRouteActive(session)) return;
        if (!IsCurrent(window)) { Pause("窗口已切换，本轮图片指路已暂停。"); return; }
        if (decision.Action == "done")
        {
            session.Complete = true; SetStatus(decision.Message);
            Log.Write("session", $"image-done interactions={session.Count}"); return;
        }
        if (!decision.CanPoint) { Pause(decision.Message); return; }
        if (decision.CandidateId != null && controls != null)
        {
            var optionFromControls = controls.Candidates.Single(c => c.Option.Id == decision.CandidateId).Option;
            if (automatic && session.RepeatsLast(optionFromControls)) { Pause("仍指向刚操作过的控件，无法确认下一步。"); return; }
            var result = await ReadControls(() => Locator.Confirm(controls, new(decision.Action, decision.CandidateId, decision.Confidence, decision.Message)), 1500, cancel);
            if (!current() || !ImageRouteActive(session)) return;
            if (result.Control == null) { Pause(result.Message); return; }
            if (!IsCurrent(window) || !Native.GetWindowRect(window, out var latest) || !Native.SameRect(latest.ToRect(), result.Control.WindowBounds))
            { Pause("窗口已移动或切换，请重新指路。"); return; }
            _frame = new(++_frameToken, result.Control, optionFromControls, decision.Action, GuideSession.Entries(controls.Context)) { HoverImage = image };
            _validatedAt = Environment.TickCount64; _hoverSince = 0; _nextHover = 0; _hoverAttempts = 0;
            _overlay.Highlight(result.Control.Bounds);
            SetStatus($"第 {session.Count + 1} 步：已指出「{optionFromControls.Name}」的真实控件，请自行操作。"); return;
        }
        var bounds = ScreenCapture.MapBox(image, decision.Box!);
        if (bounds.IsEmpty) { Pause("图片估计范围无效、过大或被遮挡，未显示高亮；请露出入口后重试。"); return; }
        if (HybridGuidePolicy.OverlapsDisabledEntry(controls, bounds)) { Pause("图片框指向已读到的禁用控件，未显示高亮。"); return; }
        var option = new ControlOption("image-" + (_frameToken + 1), decision.Label!, "ImageEntry", "当前截图", true, decision.Action == "open_entry");
        if (automatic && session.RepeatsLast(option)) { Pause("模型仍指向刚操作过的入口，无法确认下一步；已暂停，请保持菜单展开后按 Ctrl+Shift+G 重试。"); return; }
        SetStatus("正在核实图片区域是否仍然可见…");
        var proof = new VisualProof(image, bounds);
        bool valid = await Task.Run(() => ScreenCapture.StillValid(proof), cancel).WaitAsync(TimeSpan.FromMilliseconds(1500), cancel);
        if (!current() || !ImageRouteActive(session)) return;
        if (!valid || !IsCurrent(window)) { Pause("图片对应区域已变化、被遮挡或窗口已移动，未画旧框；请重新指路（可先收起输入条）。"); return; }
        var located = new LocatedControl(window, image.WindowBounds, null, bounds, Native.PointWindow(bounds), [], option.Name, option.Role) { ImageProof = proof };
        _frame = new(++_frameToken, located, option, decision.Action, controls == null ? new HashSet<string>() : GuideSession.Entries(controls.Context));
        _validatedAt = Environment.TickCount64; _hoverSince = 0; _nextHover = 0; _hoverAttempts = 0;
        _overlay.Highlight(bounds, estimated: true);
        Log.Write("image-located", $"step={session.Count + 1} token={_frame.Token} rect={bounds}");
        SetStatus($"第 {session.Count + 1} 步：白框为「{option.Name}」的图片估计位置，请核对后自行点击。" +
            (decision.Action == "open_entry" ? "展开后会自动续步。" : ""));
    }
    private async Task Advance(GuidanceFrame frame, string interaction, ControlSnapshot? prepared = null, PixelSnapshot? imagePrepared = null)
    {
        var session = _session;
        if (_closed || _frame != frame || session == null || !session.Record(frame, interaction))
        { if (_frame == frame) _continuing = false; return; }
        CancelCurrent(); _continuing = true;
        var version = _version;
        Log.Write("interaction", $"event={interaction} token={frame.Token} count={session.Count}");
        if (session.Manual)
        {
            session.Complete = true; _continuing = false; SetStatus("已检测到高亮区域的点击；手动调试结束，没有调用模型。"); return;
        }
        SetStatus(interaction == "clicked" ? "已检测到点击，等待界面更新后自动指路…" : interaction == "hover_changed" ? "悬停时画面有变化，正在请求图片中的下一项…" : "已检测到悬停展开，正在读取下一项…");
        // Do not activate the input bar or move the pointer. The user stays on the real menu.
        if (interaction == "clicked") await Task.Delay(350);
        if (_closed || version != _version || _session != session || session.Paused) return;
        TrackForeground(Native.GetForegroundWindow());
        if (version != _version || !IsCurrent(session.Window)) { Pause("目标窗口已变化，本轮已暂停，进度保留。"); return; }
        if (_codex is { } external && external.Guide == session)
        {
            _continuing = false;
            if (session.Count >= GuideSession.MaxInteractions)
            { EndCodexAtLimit(external); return; }
            external.PreviousEntries = frame.ExpectsChild && interaction == "clicked" ? frame.VisibleEntries : null;
            InvalidateCodexView();
            external.State.Publish(interaction, "已检测到实际用户操作，请结合最新界面继续。");
            SetStatus("已检测到操作，等待 当前 Agent 读取最新界面并继续指路。");
            return;
        }
        await RunGuide(session, prepared, true, frame.VisibleEntries, imagePrepared);
    }
    private static bool HitBelongs(GuidanceFrame frame, nint hit) => frame.Control.ImageProof != null ?
        ScreenCapture.HitBelongs(hit, frame.Control.Window) : Native.BelongsTo(hit, frame.Control.Window);
    private static bool CanHover(GuidanceFrame frame) => frame.ExpectsChild || frame.Option.Role == "MenuItem";
    private static bool CursorInside(GuidanceFrame frame)
    {
        if (!Native.GetPhysicalCursorPos(out var point) || !frame.Control.Bounds.Contains(new Point(point.X, point.Y))) return false;
        return HitBelongs(frame, Native.WindowFromPoint(point));
    }
    private async Task<bool> TryHoverFallback(GuidanceFrame frame, ControlReadFailure reason)
    {
        var session = _session;
        if (_closed || _frame != frame || session is not { Mode: GuideMode.ImageOnControlFailure, Images: false, Paused: false, Manual: false } ||
            (reason == ControlReadFailure.MenuNotUpdated && !frame.ExpectsChild) ||
            !CursorInside(frame)) return false;
        if (!IsCurrent(frame.Control.Window) || !Native.GetWindowRect(frame.Control.Window, out var wr) ||
            !Native.SameRect(wr.ToRect(), frame.Control.WindowBounds))
        { Pause("目标窗口已变化，本轮已暂停，未发送图片。"); return true; }
        // We did not confirm an expansion or a click. Keep the same memory/step;
        // the current image must establish what is actually visible next.
        if (_codex is { } external && external.Guide == session)
        {
            if (!session.TryActivateImageFallback(reason)) return false;
            CancelCurrent(); InvalidateCodexView();
            external.State.Publish("view_changed", FailureText(reason));
            SetStatus(FailureText(reason) + "，等待 当前 Agent 读取当前图片。"); return true;
        }
        await RunGuide(session, automatic: true, fallback: reason);
        return true;
    }
    private async Task ProbeHover(GuidanceFrame frame, bool invalid = false)
    {
        if (_hoverWork || _busy || _clicks.Pressed || _session is not { Paused: false, Manual: false } || _frame != frame) return;
        _hoverWork = true; var version = _version; _hoverAttempts++; _nextHover = Environment.TickCount64 + 700;
        try
        {
            if ((frame.Control.ImageProof?.Snapshot ?? frame.HoverImage) is { } baseline)
            {
                var image = await CaptureImage(frame.Control.Window, CancellationToken.None);
                if (_closed || version != _version || _frame != frame || !CursorInside(frame)) return;
                bool changed = ScreenCapture.HasChanged(baseline, image, frame.Control.Bounds);
                Log.Write("image-hover", $"attempt={_hoverAttempts} changed={changed}");
                if (changed) await Advance(frame, "hover_changed", imagePrepared: image);
                else if (invalid) Pause("图片对应区域已变化，无法确认下一项；已清除，请保持菜单展开后按 Ctrl+Shift+G 重试。");
                return;
            }
            var snapshot = await ReadControls(() => Locator.Capture(frame.Control.Window), 2500, CancellationToken.None);
            Log.Write("hover-probe", $"attempt={_hoverAttempts} count={snapshot.Candidates.Count} newEntries={GuideSession.HasNewEntries(frame.VisibleEntries, snapshot.Context)}");
            if (_closed || version != _version || _frame != frame || !CursorInside(frame)) return;
            if (snapshot.Candidates.Any(c => c.RuntimeId.SequenceEqual(frame.Control.RuntimeId) &&
                Native.SameRect(c.Bounds, frame.Control.Bounds) && c.Option.Name == frame.Option.Name))
                _validatedAt = Environment.TickCount64;
            if (GuideSession.HasNewEntries(frame.VisibleEntries, snapshot.Context))
                await Advance(frame, "hover_revealed", snapshot);
            else if (invalid || _hoverAttempts >= 10)
            {
                if (!await TryHoverFallback(frame, ControlReadFailure.MenuNotUpdated) && invalid && _frame == frame)
                    Pause("入口已变化，未确认菜单展开，已清除并暂停；Ctrl+Shift+G 可重新指路。");
            }
        }
        catch (Exception ex)
        {
            if (version == _version && _frame == frame)
            {
                bool handled = ex is ControlReadException read && await TryHoverFallback(frame, read.Failure);
                if (!handled && _frame == frame) Pause("悬停后的读取失败，已暂停；可用 Ctrl+Shift+G 重试。");
            }
            Log.Write("hover-error", ex.GetType().Name);
        }
        finally { _hoverWork = false; }
    }
    private async void Tick(object? sender, EventArgs e)
    {
        TrackForeground(Native.GetForegroundWindow());
        TryStartVoiceTurn();
        TryStartPluginGuide();
        CodexTick();
        var frame = _frame; if (frame == null) return;
        var located = frame.Control;
        Native.GetWindowRect(located.Window, out var wr);
        if (!Native.CanTarget(located.Window) || !Native.SameRect(wr.ToRect(), located.WindowBounds)) { Pause("窗口已移动或隐藏，高亮已清除，进度保留。"); return; }
        if (_capturing) return;
        var now = Environment.TickCount64;
        bool hover = CanHover(frame) && CursorInside(frame);
        if (!hover) _hoverSince = 0;
        else if (_hoverSince == 0) _hoverSince = now;
        if (hover && _session is { Manual: false } && now - _hoverSince >= 450 && now >= _nextHover && _hoverAttempts < 10 && !_hoverWork && !_busy && !_clicks.Pressed)
        { _ = ProbeHover(frame); return; }
        bool imageMode = located.ImageProof != null;
        var pendingValidation = imageMode ? _imageValidation : _validation;
        if (++_ticks % 4 != 0 || _hoverWork || _busy || _clicks.Pressed || pendingValidation is { IsCompleted: false } ||
            (!imageMode && _uiaWork is { IsCompleted: false })) return;
        var validation = Task.Run(() => Locator.StillValid(located));
        if (imageMode) _imageValidation = validation; else _validation = validation;
        var completed = await Task.WhenAny(validation, Task.Delay(1000));
        if (_closed || _frame != frame || _clicks.Pressed || _capturing) return;
        if (completed == validation && await validation) { _validatedAt = Environment.TickCount64; return; }
        _validatedAt = 0; _overlay.Hide();
        if (completed == validation && hover && CanHover(frame) && _hoverAttempts < 10) _ = ProbeHover(frame, true);
        else Pause("入口已变化或不可见，已清除并暂停；Ctrl+Alt+G 可继续本轮引导。");
    }
    private void CancelCurrent()
    {
        _version++; _request?.Cancel(); _continuing = false;
        if (_frame != null) Log.Write("clear", "overlay-cleared");
        _frame = null; _pressedFrame = null; _clicks.Reset(); _overlay.Hide();
    }
    private void Pause(string? message = null, bool stopVoice = false)
    {
        // A guide refusal pauses the guide, while conversational input can accept a new question.
        // Explicit user cancellation/settings changes and unrelated windows also stop the microphone.
        if (stopVoice) CancelPluginGuide();
        if (stopVoice || !_voiceChat.Enabled) CancelSpeech();
        if (message != null) Log.Write("pause", PauseReason(message));
        CancelCurrent(); if (_session != null) _session.Paused = true;
        CodexPaused(message, stopVoice);
        if (message != null) SetStatus(message);
    }
    private static string PauseReason(string message) =>
        message.Contains("新菜单项") ? "no-new-menu-items" : message.Contains("刚操作") ? "repeated-entry" :
        message.Contains("切换") ? "foreground-change" : message.Contains("窗口") ? "window-change" :
        message.Contains("模型") || message.Contains("候选") || message.Contains("无法确定") ? "selection-refused" :
        message.Contains("超时") || message.Contains("读取") || message.Contains("不可读") ? "read-failed" :
        message.Contains("入口已变化") || message.Contains("控件已变化") || message.Contains("遮挡") ? "control-invalidated" : "user-or-state";
    private void Reset(string? message = null)
    {
        Pause(stopVoice: true); _session = null;
        if (message != null) SetStatus(message);
    }
    private void SetStatus(string message)
    {
        _status.Text = ShowcaseEnglish.Text(message); UpdateShellStatus();
        // Labels/questions/session memory stay out of the log. Only counts and event types are logged.
    }
    private void SetCollapsed(bool collapsed)
    {
        ApplyShellCollapsed(collapsed);
    }

    private void SpeechUi(Action action)
    {
        var request = _speechRequest;
        if (request == null || _closed) return;
        Dispatcher.BeginInvoke(new Action(() => {
            if (!_closed && _speechRequest == request) action();
        }));
    }

    private async Task ToggleSpeech()
    {
        if (_voiceChat.Enabled) { SetStatus("语音对话已开启；请先停止对话，再使用点按转写。"); return; }
        if (_speechRequest != null && !_transcribing) { await FinishSpeech(); return; }
        if (_transcribing || _speech.IsBusy) { SetStatus("正在完成本地转写，请稍等；Esc 可取消结果。"); return; }
        Pause();
        _speechRequest = new(); _question.IsReadOnly = true;
        try
        {
            _activeSpeechEngine = SelectedSpeechEngine();
            _speech.Start(_activeSpeechEngine); _speechButton.Content = "Stop";
            SetStatus($"{SpeechEngines.Label(_activeSpeechEngine)} · 录音已开始；说完点“结束”，Esc 取消。录音不上传。");
            Log.Write("speech", $"recording-start engine={SpeechEngines.Id(_activeSpeechEngine)}");
        }
        catch (Exception error)
        {
            CancelSpeech();
            SetStatus(error is InvalidOperationException ? error.Message : "麦克风无法开启，请检查默认输入设备和麦克风权限。");
            Log.Write("speech-error", error.GetType().Name);
        }
    }

    private async Task FinishSpeech()
    {
        if (_voiceChat.Enabled) { await FinishVoiceTurn(); return; }
        var request = _speechRequest;
        if (request == null || _transcribing || _closed) return;
        var engine = _activeSpeechEngine;
        _transcribing = true; _speechButton.IsEnabled = false; _speechButton.Content = "Transcribing";
        SetStatus($"{SpeechEngines.Label(engine)} · 正在本地转写…Esc 可取消，不会自动指路。");
        try
        {
            var text = await _speech.StopAndTranscribeAsync(request.Token);
            if (_closed || _speechRequest != request || request.IsCancellationRequested) return;
            // Release the audio request before TextChanged resets the guidance session.
            _speechRequest = null; _transcribing = false; _question.IsReadOnly = false;
            _speechButton.Content = "Voice"; _speechButton.IsEnabled = true;
            _question.Text = text; _question.CaretIndex = text.Length;
            SetStatus($"{SpeechEngines.Label(engine)} 转写已填入；请核对或修改，再点击“指路”。");
            Log.Write("speech", $"transcribed engine={SpeechEngines.Id(engine)} chars={text.Length}");
        }
        catch (OperationCanceledException) { /* Cancel/clear/collapse already updated the UI. */ }
        catch (Exception error)
        {
            if (!_closed && _speechRequest == request)
            {
                SetStatus(error is InvalidOperationException ? error.Message : "本地转写失败，原文字保留；可检查模型文件后重试。");
                Log.Write("speech-error", error.GetType().Name);
            }
        }
        finally
        {
            if (_speechRequest == request)
            {
                _speechRequest = null; _transcribing = false; _question.IsReadOnly = false;
                _speechButton.Content = "Voice"; _speechButton.IsEnabled = true;
            }
            request.Dispose();
        }
    }

    private void CancelSpeech()
    {
        StopVoiceChat();
        var request = _speechRequest; _speechRequest = null;
        request?.Cancel(); request?.Dispose(); _speech.Cancel();
        _transcribing = false; _question.IsReadOnly = false;
        _speechButton.Content = "Voice"; _speechButton.IsEnabled = true;
        UpdateVoiceChatUi();
        if (request != null) Log.Write("speech", "cancelled");
    }

    private SpeechEngine SelectedSpeechEngine() => _speechEngine.SelectedValue is SpeechEngine engine && SpeechEngines.All.Contains(engine)
        ? engine : throw new InvalidOperationException("请先选择一个有效的语音识别方案。");
}
