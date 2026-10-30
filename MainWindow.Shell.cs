using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ScreenGuide;

internal sealed partial class MainWindow
{
    private enum ShellView { Conversation, Controls, ModelSettings, Docked, Reply }
    private readonly bool _shellPreview;
    private readonly Canvas _shellCanvas = new();
    private readonly AngledBorder _shellCard = new();
    private readonly StackPanel _shellCardStack = new();
    private readonly StackPanel _conversationView = new(), _controlsView = new();
    private readonly TextBlock _shellHeading = new(), _controlHint = new();
    private readonly ScrollViewer _replyScroll = new();
    private readonly Polygon _shellTail = new();
    private readonly TranslateTransform _orbShift = new();
    private readonly DispatcherTimer _shellTimer = new() { Interval = TimeSpan.FromMilliseconds(32) };
    private readonly EdgeDockIdlePolicy _dockIdle = new();
    private Grid _orb = null!;
    private Button _backToChat = null!, _foldCard = null!;
    private ShellView _shellView = ShellView.Conversation;
    private DockSide _dockSide = DockSide.Right;
    private Rect _shellWork, _orbScreen = Rect.Empty, _cardScreen = Rect.Empty, _shellBounds = Rect.Empty;
    private double _dockY, _shellScale = 1;
    private bool _shellReady, _orbHidden, _orbPressed, _orbDragging, _shellDisplayQueued, _mustLeaveOrbArea;
    private Point _pressPhysical, _pressOrbPhysical, _freeOrbPhysical;
    private string _lastShellStatus = "";

    private void BuildShell(string modelName)
    {
        Title = "Screen Guide · English"; Width = 470; Height = 230;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; ResizeMode = ResizeMode.NoResize;
        Topmost = true; ShowInTaskbar = _acceptance; ShowActivated = false;
        Background = Brushes.Transparent; Foreground = ShellTheme.Ink;
        Left = SystemParameters.WorkArea.Right - Width; Top = SystemParameters.WorkArea.Bottom - Height - 70;
        Content = _shellCanvas;

        _shellTail.Fill = ShellTheme.Surface; _shellTail.Stroke = ShellTheme.Line;
        _shellTail.StrokeThickness = 1; _shellTail.IsHitTestVisible = false;
        _shellCanvas.Children.Add(_shellTail);
        _shellCard.Width = 376; _shellCard.Padding = new Thickness(21, 13, 22, 15);
        _shellCard.Background = ShellTheme.Surface; _shellCard.ShadowBrush = ShellTheme.Accent; _shellCard.ReserveShadow = true;
        _shellCard.BorderBrush = ShellTheme.Ink; _shellCard.BorderThickness = new Thickness(1);
        var cardStack = _shellCardStack; _shellCard.Child = new ScrollViewer { Content = cardStack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 11) };
        var fold = ShellTheme.RoundedButton("×", quiet: true); fold.Padding = new Thickness(7, 0, 7, 0); fold.MinHeight = 25;
        _foldCard = fold;
        fold.ToolTip = "Dock · Esc clears the highlight"; fold.Click += (_, _) => SetCollapsed(true);
        DockPanel.SetDock(fold, Dock.Right); header.Children.Add(fold);
        _backToChat = ShellTheme.RoundedButton("Back", quiet: true); _backToChat.MinHeight = 25; _backToChat.Padding = new Thickness(8, 0, 8, 0);
        _backToChat.Click += (_, _) => { if (_shellView == ShellView.ModelSettings) ShowControls(); else ShowConversation(); }; DockPanel.SetDock(_backToChat, Dock.Right); header.Children.Add(_backToChat);
        _shellHeading.Text = "Screen Guide"; _shellHeading.MinWidth = 100; _shellHeading.Foreground = ShellTheme.Ink; _shellHeading.FontSize = 13;
        _shellHeading.FontWeight = FontWeights.SemiBold; _shellHeading.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(_shellHeading); cardStack.Children.Add(header);

        _question.Width = double.NaN; _question.Height = 34; _question.FontSize = 13;
        _question.Background = Brushes.Transparent; _question.Foreground = ShellTheme.Ink; _question.BorderThickness = new Thickness(0);
        _question.Padding = new Thickness(8, 2, 8, 2);
        var questionGrid = new Grid();
        var prompt = new TextBlock { Text = "What would you like to find?", Foreground = ShellTheme.Muted, FontSize = 13, Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        questionGrid.Children.Add(prompt); questionGrid.Children.Add(_question);
        void RefreshPrompt() => prompt.Visibility = _question.Text.Length == 0 && !_question.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Collapsed;
        _question.TextChanged += (_, _) => { RefreshPrompt(); if (!_applyingVoiceTranscript) Reset("新问题：点击指路开始，点击高亮目标后会自动继续。"); };
        _question.GotKeyboardFocus += (_, _) => RefreshPrompt(); _question.LostKeyboardFocus += (_, _) => RefreshPrompt();
        _question.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await Guide(); } };
        var questionBorder = new AngledBorder { Background = ShellTheme.SurfaceBlue, BorderBrush = ShellTheme.Line, BorderThickness = new Thickness(1), Padding = new Thickness(7, 1, 7, 1), Child = questionGrid };
        var inputRow = new Grid(); inputRow.ColumnDefinitions.Add(new ColumnDefinition()); inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputRow.Children.Add(questionBorder);
        var guide = ShellTheme.RoundedButton("Guide", primary: true); guide.Margin = new Thickness(8, 0, 0, 0);
        guide.ToolTip = "Ctrl+Shift+G / Ctrl+Alt+G · keep the pointer in place"; guide.Click += async (_, _) => await Guide(); Grid.SetColumn(guide, 1); inputRow.Children.Add(guide);
        _typedInput = inputRow; _conversationView.Children.Add(inputRow);
        _spokenBubble.Child = new ScrollViewer { Content = _spokenText, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _conversationView.Children.Add(_spokenBubble);
        var voiceRow = new DockPanel { Margin = new Thickness(0, 9, 0, 11) };
        _speechButton.Width = double.NaN; _speechButton.Margin = new Thickness(0); _speechButton.MinHeight = 29;
        _speechButton.Padding = new Thickness(10, 3, 10, 3); _speechButton.Click += async (_, _) => await ToggleSpeech();
        DockPanel.SetDock(_speechButton, Dock.Left); voiceRow.Children.Add(_speechButton);
        _voiceChatButton.Margin = new Thickness(6, 0, 10, 0); _voiceChatButton.MinHeight = 29;
        _voiceChatButton.Padding = new Thickness(10, 3, 10, 3); _voiceChatButton.Click += (_, _) => ToggleVoiceChat();
        DockPanel.SetDock(_voiceChatButton, Dock.Left); voiceRow.Children.Add(_voiceChatButton);
        voiceRow.Children.Add(_voiceHint);
        _conversationView.Children.Add(voiceRow); cardStack.Children.Add(_conversationView);

        _imageMode.Width = _speechEngine.Width = double.NaN;
        _imageMode.Height = _speechEngine.Height = 31;
        _imageMode.Margin = _speechEngine.Margin = new Thickness(0, 5, 0, 13);
        _imageMode.FontSize = _speechEngine.FontSize = 12;
        _controlsView.Children.Add(ShellLabel("Speech recognition", "Local audio · English"));
        foreach (var engine in SpeechEngines.All) _speechEngine.Items.Add(new ComboBoxItem { Content = SpeechEngines.Label(engine), Tag = engine });
        _speechEngine.SelectedValue = SpeechEngine.SenseVoice;
        _speechEngine.SelectionChanged += (_, _) => {
            bool cancelled = _speechRequest != null; CancelSpeech();
            var engine = SelectedSpeechEngine();
            SetStatus($"语音已选 {SpeechEngines.Label(engine)}；" + (cancelled ? "上段已取消，原文字保留。" : "返回对话后点击语音开始。"));
            Log.Write("speech-engine", $"selected={SpeechEngines.Id(engine)} cancelled={cancelled}");
            RememberSpeechEngine();
        };
        _controlsView.Children.Add(_speechEngine);
        _voiceChatControl.HorizontalAlignment = HorizontalAlignment.Stretch;
        _voiceChatControl.Margin = new Thickness(0, 0, 0, 13); _voiceChatControl.Click += (_, _) => ToggleVoiceChat();
        _controlsView.Children.Add(_voiceChatControl);
        _controlsView.Children.Add(ShellLabel("Screen input", "Choose what to share"));
        _imageMode.Items.Add(new ComboBoxItem { Content = "Hybrid: controls + image", Tag = GuideMode.Hybrid });
        _imageMode.Items.Add(new ComboBoxItem { Content = "Controls only", Tag = GuideMode.Controls });
        _imageMode.Items.Add(new ComboBoxItem { Content = "Image every step", Tag = GuideMode.DirectImage });
        _imageMode.Items.Add(new ComboBoxItem { Content = "Image on control failure", Tag = GuideMode.ImageOnControlFailure });
        _imageMode.SelectedValue = GuideMode.Hybrid;
        _imageMode.SelectionChanged += (_, _) => {
            Reset(SelectedMode() switch {
                GuideMode.Hybrid => "混合：每步同时读取当前控件和截图；截图发送给当前指路模型。",
                GuideMode.DirectImage => "每步发送当前窗口图片，白框是估计位置。",
                GuideMode.ImageOnControlFailure => "先读取控件，失败后本轮转为发送图片并保留进度。",
                _ => "仅控件：不截图、不发图，旧请求与高亮已取消。"
            });
            RememberScreenMode();
        };
        _controlsView.Children.Add(_imageMode);
        _manual.Foreground = ShellTheme.Ink; _manual.FontSize = 12; _manual.Margin = new Thickness(0, 0, 0, 12);
        _manual.Checked += (_, _) => { WithoutSettingsPersistence(() => _imageMode.SelectedValue = GuideMode.Controls); _imageMode.IsEnabled = false; Reset("手动调试：输入完整控件名称，不调用模型。"); _mode.Text = "Manual · no model calls"; };
        _manual.Unchecked += (_, _) => { _imageMode.IsEnabled = true; WithoutSettingsPersistence(() => _imageMode.SelectedValue = PreferredGuideMode); Reset("模型模式：结合问题、当前界面和本轮进度选择入口。"); _mode.Text = _modelReady ? _activeModel.DisplayName : "Connection not configured"; };
        foreach (var label in new[] { _targetLabel, _mode }) { label.MaxWidth = 320; label.Margin = new Thickness(0, 0, 0, 5); label.Foreground = ShellTheme.Muted; _controlsView.Children.Add(label); }
        _mode.Text = _modelReady ? modelName : "Connection not configured";
        var modelSettings = ShellTheme.RoundedButton("Connection settings"); modelSettings.Margin = new Thickness(0, 6, 0, 4);
        modelSettings.Click += (_, _) => ShowModelSettings(); _controlsView.Children.Add(modelSettings);
        var actions = new UniformGrid { Columns = 2, Margin = new Thickness(-3, 8, -3, 0) };
        var clear = ShellTheme.RoundedButton("Clear highlight"); clear.Click += (_, _) => Pause("已清除高亮并停止语音，进度保留；指路可继续。", stopVoice: true);
        var reset = ShellTheme.RoundedButton("Reset guide"); reset.Click += (_, _) => Reset("本轮记忆已清空。");
        var dock = ShellTheme.RoundedButton("Dock", quiet: true); dock.Click += (_, _) => SetCollapsed(true);
        var exit = ShellTheme.RoundedButton("Quit", quiet: true); exit.Foreground = new SolidColorBrush(Color.FromRgb(255, 212, 59)); exit.Click += (_, _) => Close();
        foreach (var button in new[] { clear, reset, dock, exit }) { button.Margin = new Thickness(3); actions.Children.Add(button); }
        _controlsView.Children.Add(actions);
        _controlHint.Foreground = ShellTheme.Muted; _controlHint.FontSize = 11; _controlHint.TextWrapping = TextWrapping.Wrap;
        _controlHint.MaxHeight = 42; _controlHint.Margin = new Thickness(0, 10, 0, 0); _controlsView.Children.Add(_controlHint);
        _controlsView.Children.Add(_settingsNotice);
        cardStack.Children.Add(_controlsView);
        BuildModelSettings(); cardStack.Children.Add(_modelSettingsView);
        _status.Foreground = ShellTheme.Ink; _status.FontSize = 12; _status.Margin = new Thickness(0, 1, 0, 0); _status.LineHeight = 19;
        _replyScroll.Content = _status; _replyScroll.MaxHeight = 112; _replyScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; _replyScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        cardStack.Children.Add(_replyScroll); _shellCanvas.Children.Add(_shellCard);
        _orb = ShellTheme.MakeOrb(); _orb.RenderTransform = _orbShift; _shellCanvas.Children.Add(_orb);
        _orb.MouseLeftButtonDown += OrbDown; _orb.MouseMove += OrbMove; _orb.MouseLeftButtonUp += OrbUp;
        _orb.LostMouseCapture += (_, _) => { if (_orbPressed) FinishOrbDrag(); };

        _speech.RecordingProgress += (seconds, level) => SpeechUi(() => {
            if (_voiceChat.Enabled) return; // Listening must not overwrite a reply or reopen its bubble every 100ms.
            if (_speech.IsRecording) SetStatus($"录音中 {seconds:0.0}s · 输入强度 {Math.Min(100, level * 100):0}%\n说完点击“结束”，Esc 取消，最长30秒。");
        });
        _speech.SpeechSegmentReady += () => SpeechUi(() => { _ = FinishVoiceTurn(); });
        _speech.RecordingLimitReached += () => SpeechUi(() => { _ = FinishSpeech(); });
        _speech.RecordingFailed += error => SpeechUi(() => { CancelSpeech(); SetStatus("麦克风录音失败，语音已停止；请检查默认输入设备和麦克风权限。"); Log.Write("speech-error", error.GetType().Name); });
        _shellTimer.Tick += ShellTick;
        if (!_shellPreview) Loaded += (_, _) => InitializeShell();
        UpdateCardMode();
        ShowcaseEnglish.Apply(_shellCanvas);
    }

    private static FrameworkElement ShellLabel(string title, string hint)
    {
        var panel = new DockPanel();
        var help = new TextBlock { Text = hint, Foreground = ShellTheme.Muted, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(help, Dock.Right); panel.Children.Add(help);
        panel.Children.Add(new TextBlock { Text = title, Foreground = ShellTheme.Ink, FontSize = 12, FontWeight = FontWeights.SemiBold });
        return panel;
    }

    private void InitializeShell()
    {
        Native.GetWindowRect(_hwnd, out var rect);
        ReadShellMonitor(new Point(rect.Right - 25, rect.Bottom - 50));
        if (_shellWork.Width <= 0 || _shellWork.Height <= 0) _shellWork = SystemParameters.WorkArea;
        _dockY = AppSettingsGeometry.AnchorFromRatio(_shellWork, _savedSettings.DockYRatio);
        _shellReady = true; RefreshShell(false); _shellTimer.Start();
    }

    private void ReadShellMonitor(Point physical)
    {
        var monitor = ShellMonitorApi.MonitorFromPoint(new Native.POINT { X = (int)physical.X, Y = (int)physical.Y }, 2);
        var info = new ShellMonitorApi.Info { Size = Marshal.SizeOf<ShellMonitorApi.Info>() };
        if (monitor == 0 || !ShellMonitorApi.GetMonitorInfo(monitor, ref info)) return;
        double scale = 1;
        if (ShellMonitorApi.GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0) scale = dpi / 96.0;
        _shellScale = scale;
        _shellWork = new Rect(info.Work.Left / scale, info.Work.Top / scale,
            (info.Work.Right - info.Work.Left) / scale, (info.Work.Bottom - info.Work.Top) / scale);
    }

    private void ShellTick(object? sender, EventArgs args)
    {
        if (_closed || !_shellReady || _capturing || _orbPressed) return;
        ShowcaseEnglish.Apply(_shellCanvas);
        if (!Native.GetPhysicalCursorPos(out var cursor)) return;
        var point = new Point(cursor.X / _shellScale, cursor.Y / _shellScale);
        var now = DateTimeOffset.UtcNow;
        bool nearOrb = EdgeDock.NearOrb(point, _orbScreen);
        if (_mustLeaveOrbArea && !nearOrb) _mustLeaveOrbArea = false;
        bool overCard = !_cardScreen.IsEmpty && _cardScreen.Contains(point);
        bool near = !_mustLeaveOrbArea && (nearOrb || (_shellView is ShellView.Conversation or ShellView.Controls or ShellView.ModelSettings && overCard));
        bool inputOpen = (!_voiceChat.Enabled && ((IsActive && _question.IsKeyboardFocusWithin) || _speechRequest != null || _speech.IsBusy)) ||
            (IsActive && _modelSettingsView.IsKeyboardFocusWithin) || _profilePicker.IsDropDownOpen || _imageMode.IsDropDownOpen || _speechEngine.IsDropDownOpen;
        bool hidden = _dockIdle.Update(now, near, inputOpen, _shellView == ShellView.Controls && overCard);
        if (hidden && _shellView is ShellView.Conversation or ShellView.Controls or ShellView.ModelSettings)
        {
            ClearModelSecretInput();
            _shellView = ShellView.Docked; _orbHidden = true; RefreshShell(true);
            Log.Write("shell", "idle-half-hidden");
        }
        if (_shellView == ShellView.Reply && !_dockIdle.IsBubbleVisible(now))
        {
            if (overCard) { _dockIdle.ShowBubble(now, TimeSpan.FromSeconds(2)); return; }
            _shellView = ShellView.Docked; RefreshShell(false);
        }
    }

    private void ShowConversation()
    {
        ClearModelSecretInput();
        _shellView = ShellView.Conversation; _orbHidden = false; _mustLeaveOrbArea = false; _dockIdle.DismissBubble();
        _dockIdle.Update(DateTimeOffset.UtcNow, true, false, false);
        if (_shellReady) RefreshShell(true); else UpdateCardMode();
    }

    private void UpdateShellStatus()
    {
        ShowcaseEnglish.Apply(_shellCanvas);
        _controlHint.Text = _status.Text;
        bool changed = _lastShellStatus != _status.Text; _lastShellStatus = _status.Text;
        if (!_shellReady || _closed) return;
        if (changed && _shellView is ShellView.Docked or ShellView.Reply)
        {
            _shellView = ShellView.Reply;
            _dockIdle.ShowBubble(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(8));
            RefreshShell(true);
        }
        else RefreshShell(false);
    }

    private void ApplyShellCollapsed(bool collapsed)
    {
        if (!collapsed) { ShowConversation(); return; }
        if (!_voiceChat.Enabled && _speechRequest != null) { CancelSpeech(); SetStatus("语音已取消，原文字保留。"); }
        Keyboard.ClearFocus(); _imageMode.IsDropDownOpen = _speechEngine.IsDropDownOpen = false;
        _profilePicker.IsDropDownOpen = false; ClearModelSecretInput();
        _shellView = ShellView.Docked; _orbHidden = true; _dockIdle.DismissBubble();
        _mustLeaveOrbArea = true;
        if (_shellReady) RefreshShell(true); else UpdateCardMode();
        Log.Write("shell", "manually-half-hidden");
    }

    private void ShellEscape() { if (_shellView is ShellView.Controls or ShellView.ModelSettings) SetCollapsed(true); }

    private void ScheduleShellDisplayRefresh()
    {
        if (_shellDisplayQueued || !_shellReady || _closed || _shellPreview) return;
        _shellDisplayQueued = true;
        Dispatcher.BeginInvoke(new Action(() => {
            _shellDisplayQueued = false;
            if (_closed || _capturing || _orbPressed) return;
            double dockRatio = AppSettingsGeometry.RatioFromAnchor(_shellWork, _dockY);
            var center = new Point((_orbScreen.X + 24) * _shellScale, (_orbScreen.Y + 24) * _shellScale);
            ReadShellMonitor(center); _dockY = AppSettingsGeometry.AnchorFromRatio(_shellWork, dockRatio);
            RefreshShell(false);
        }));
    }

    private void UpdateCardMode()
    {
        ShowcaseEnglish.Apply(_shellCanvas);
        _collapsed = _shellView is ShellView.Docked or ShellView.Reply;
        _shellCard.Visibility = _shellTail.Visibility = _shellView == ShellView.Docked ? Visibility.Collapsed : Visibility.Visible;
        _conversationView.Visibility = _shellView == ShellView.Conversation ? Visibility.Visible : Visibility.Collapsed;
        _controlsView.Visibility = _shellView == ShellView.Controls ? Visibility.Visible : Visibility.Collapsed;
        _modelSettingsView.Visibility = _shellView == ShellView.ModelSettings ? Visibility.Visible : Visibility.Collapsed;
        _replyScroll.Visibility = _shellView is ShellView.Controls or ShellView.ModelSettings ? Visibility.Collapsed : Visibility.Visible;
        _backToChat.Visibility = _shellView is ShellView.Controls or ShellView.ModelSettings ? Visibility.Visible : Visibility.Collapsed;
        _backToChat.Content = _shellView == ShellView.ModelSettings ? "Back" : "Back";
        _foldCard.Visibility = _shellView == ShellView.Reply ? Visibility.Collapsed : Visibility.Visible;
        _shellHeading.Text = _shellView == ShellView.ModelSettings ? "Connection settings" : _shellView == ShellView.Controls ? "Control panel" : _shellView == ShellView.Reply ? "Guide reply" : "Screen Guide";
        _backToChat.Content = "Back";
        _mode.Visibility = Visibility.Visible;
    }

    private void RefreshShell(bool animate)
    {
        if (!_shellReady || _closed) return;
        UpdateCardMode();
        _shellCard.Width = Math.Min(376, _shellWork.Width - 16);
        _shellCard.MaxHeight = Math.Max(64, _shellWork.Height - 16);
        // Measure the natural content rather than reusing a ScrollViewer's previous viewport.
        // This matters when switching from the short chat card to the taller control panel.
        _shellCardStack.InvalidateMeasure();
        _shellCardStack.Measure(new Size(Math.Max(1, _shellCard.Width - 36), double.PositiveInfinity));
        _shellCard.Height = Math.Clamp(_shellCardStack.DesiredSize.Height + 30, 64, _shellCard.MaxHeight);
        var size = new Size(_shellCard.Width, _shellCard.Height);
        _shellCard.Measure(size);
        var orb = _orbDragging ? new Rect(_freeOrbPhysical.X / _shellScale - 24, _freeOrbPhysical.Y / _shellScale - 24, 48, 48)
            : EdgeDock.OrbRect(_shellWork, _dockY, _dockSide, _orbHidden);
        var card = _shellView == ShellView.Docked ? Rect.Empty : EdgeDock.CardRect(_shellWork, orb, size, 12);
        // Our own reply must not cover the real control being highlighted.
        if (_frame != null && !card.IsEmpty)
        {
            var b = _frame.Control.Bounds; var blocked = new Rect(b.X / _shellScale, b.Y / _shellScale, b.Width / _shellScale, b.Height / _shellScale);
            if (card.IntersectsWith(blocked))
            {
                foreach (double y in new[] { blocked.Top - card.Height - 16, blocked.Bottom + 16, _shellWork.Top + 8, _shellWork.Bottom - card.Height - 8 })
                {
                    var alternative = new Rect(card.X, Math.Clamp(y, _shellWork.Top, _shellWork.Bottom - card.Height), card.Width, card.Height);
                    if (!alternative.IntersectsWith(blocked)) { card = alternative; break; }
                }
                if (card.IntersectsWith(blocked) && _shellView == ShellView.Reply)
                {
                    card = Rect.Empty; _shellCard.Visibility = _shellTail.Visibility = Visibility.Collapsed;
                }
            }
        }
        var previous = _orbScreen;
        if (!previous.IsEmpty) previous.Offset(_orbShift.X, _orbShift.Y);
        var bounds = orb; if (!card.IsEmpty) bounds.Union(card);
        if (animate && !previous.IsEmpty) bounds.Union(previous);
        bounds.Inflate(12, 12);
        _orbScreen = orb; _cardScreen = card; _shellBounds = bounds;
        Width = bounds.Width; Height = bounds.Height;
        Canvas.SetLeft(_orb, orb.X - bounds.X); Canvas.SetTop(_orb, orb.Y - bounds.Y);
        if (!card.IsEmpty)
        {
            Canvas.SetLeft(_shellCard, card.X - bounds.X); Canvas.SetTop(_shellCard, card.Y - bounds.Y);
            double tailY = Math.Clamp(orb.Y + 24, card.Top + 20, card.Bottom - 20) - bounds.Y;
            double x = _dockSide == DockSide.Right ? card.Right - bounds.X - 1 : card.Left - bounds.X + 1;
            double direction = _dockSide == DockSide.Right ? 1 : -1;
            _shellTail.Points = new PointCollection { new(x, tailY - 7), new(x + direction * 11, tailY), new(x, tailY + 7) };
        }
        var clip = Rect.Intersect(bounds, _shellWork); clip.Offset(-bounds.X, -bounds.Y);
        _shellCanvas.Clip = new RectangleGeometry(clip);
        if (animate && !previous.IsEmpty)
        {
            _orbShift.BeginAnimation(TranslateTransform.XProperty, null); _orbShift.X = 0;
            _orbShift.BeginAnimation(TranslateTransform.YProperty, null); _orbShift.Y = 0;
            var duration = TimeSpan.FromMilliseconds(220);
            _orbShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(previous.X - orb.X, 0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
            _orbShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(previous.Y - orb.Y, 0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
            if (!card.IsEmpty) _shellCard.BeginAnimation(OpacityProperty, new DoubleAnimation(.45, 1, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.Stop });
        }
        if (!_shellPreview && _hwnd != 0)
            Native.SetWindowPos(_hwnd, Native.HwndTopmost, (int)Math.Floor(bounds.X * _shellScale), (int)Math.Floor(bounds.Y * _shellScale),
                (int)Math.Ceiling(bounds.Width * _shellScale), (int)Math.Ceiling(bounds.Height * _shellScale), Native.SwpNoActivate);
    }

    private void OrbDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !Native.GetPhysicalCursorPos(out var cursor)) return;
        _pressPhysical = new Point(cursor.X, cursor.Y);
        _pressOrbPhysical = new Point((_orbScreen.X + 24 + _orbShift.X) * _shellScale, (_orbScreen.Y + 24 + _orbShift.Y) * _shellScale);
        _orbPressed = true; _orbDragging = false;
        if (!_orb.CaptureMouse()) { _orbPressed = false; return; }
        e.Handled = true;
    }

    private void OrbMove(object sender, MouseEventArgs e)
    {
        if (!_orbPressed || e.LeftButton != MouseButtonState.Pressed || !Native.GetPhysicalCursorPos(out var cursor)) return;
        var delta = new Point(cursor.X, cursor.Y) - _pressPhysical;
        if (!_orbDragging && delta.Length < 5 * _shellScale) return;
        _orbDragging = true; _orbHidden = false; _shellView = ShellView.Docked; _dockIdle.DismissBubble();
        _freeOrbPhysical = _pressOrbPhysical + delta; ReadShellMonitor(new Point(cursor.X, cursor.Y));
        _dockSide = EdgeDock.NearestSide(_shellWork, new Point(cursor.X / _shellScale, cursor.Y / _shellScale));
        _dockY = EdgeDock.ClampAnchorY(_shellWork, _freeOrbPhysical.Y / _shellScale);
        _orbShift.BeginAnimation(TranslateTransform.XProperty, null); _orbShift.BeginAnimation(TranslateTransform.YProperty, null);
        _orbShift.X = _orbShift.Y = 0; RefreshShell(false); e.Handled = true;
    }

    private void OrbUp(object sender, MouseButtonEventArgs e)
    {
        if (!_orbPressed || e.ChangedButton != MouseButton.Left) return;
        bool dragged = _orbDragging; FinishOrbDrag();
        if (!dragged)
        {
            if (_shellView is ShellView.Docked or ShellView.Reply) { ShowConversation(); e.Handled = true; return; }
            ClearModelSecretInput();
            _shellView = _shellView == ShellView.Controls ? ShellView.Conversation : ShellView.Controls;
            _orbHidden = false; _mustLeaveOrbArea = false; _dockIdle.DismissBubble(); _dockIdle.Update(DateTimeOffset.UtcNow, true, false, _shellView == ShellView.Controls);
            RefreshShell(true);
        }
        e.Handled = true;
    }

    private void FinishOrbDrag()
    {
        bool dragged = _orbDragging; _orbPressed = _orbDragging = false; _orb.ReleaseMouseCapture();
        if (dragged) { ShowConversation(); PersistSettings(); Log.Write("shell", "orb-dragged-and-docked side=" + _dockSide); }
    }

    private bool ShellPassThrough(nint lparam)
    {
        if (!_shellReady || _orbPressed) return false;
        long value = lparam.ToInt64(); var point = new Point((short)(value & 0xffff) / _shellScale, (short)((value >> 16) & 0xffff) / _shellScale);
        var orb = _orbScreen; orb.Offset(_orbShift.X, _orbShift.Y);
        if (orb.Contains(point)) return false;
        return _cardScreen.IsEmpty || !_cardScreen.Contains(point) || _shellView == ShellView.Reply;
    }

    private Action SuppressShellPopups()
    {
        _imageMode.IsDropDownOpen = _speechEngine.IsDropDownOpen = _profilePicker.IsDropDownOpen = false;
        var saved = new List<(DependencyObject Element, bool Enabled)>();
        void Walk(DependencyObject element)
        {
            saved.Add((element, ToolTipService.GetIsEnabled(element))); ToolTipService.SetIsEnabled(element, false);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) Walk(VisualTreeHelper.GetChild(element, i));
        }
        Walk(_shellCanvas);
        // WPF tooltips/popups need not use the native menu class. Hide every other
        // visible helper HWND from THIS process, so no own pixels enter the capture.
        var overlay = new WindowInteropHelper(_overlay).Handle;
        Native.EnumWindows((handle, _) => {
            if (handle != _hwnd && handle != overlay && Native.IsOwnWindow(handle) && Native.IsWindowVisible(handle))
                Native.ShowWindow(handle, 0);
            return true;
        }, 0);
        return () => {
            if (_closed) return;
            // Do not resurrect HWNDs whose WPF IsOpen state has already closed.
            // Tooltips can appear naturally on the next genuine hover.
            foreach (var (element, enabled) in saved) ToolTipService.SetIsEnabled(element, enabled);
        };
    }

    private void CloseShell()
    {
        _shellTimer.Stop(); _orbPressed = false; _orb?.ReleaseMouseCapture();
        _orbShift.BeginAnimation(TranslateTransform.XProperty, null); _orbShift.BeginAnimation(TranslateTransform.YProperty, null);
        _shellCard.BeginAnimation(OpacityProperty, null);
    }

    internal static int RenderShellPreviews()
    {
        var window = new MainWindow(preview: true);
        try
        {
            window._shellWork = new Rect(0, 0, 900, 600); window._shellScale = 1;
            window._dockY = 300; window._shellReady = true;
            window._status.Text = "这里显示助手的回复。收到新消息时，气泡会在圆球旁展开。";
            var directory = System.IO.Path.Combine(AppContext.BaseDirectory, "ui-previews"); Directory.CreateDirectory(directory);
            void Save(string name)
            {
                window.RefreshShell(false);
                window._shellCanvas.Measure(new Size(window.Width, window.Height));
                window._shellCanvas.Arrange(new Rect(0, 0, window.Width, window.Height)); window._shellCanvas.UpdateLayout(); ShowcaseEnglish.Apply(window._shellCanvas); window._shellCanvas.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.Width), (int)Math.Ceiling(window.Height), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window._shellCanvas);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(System.IO.Path.Combine(directory, name + ".png")); encoder.Save(output);
            }
            foreach (var view in new[] { ShellView.Conversation, ShellView.Controls, ShellView.Reply, ShellView.Docked })
            {
                window._shellView = view; window._orbHidden = view is ShellView.Reply or ShellView.Docked;
                Save(view.ToString().ToLowerInvariant());
            }
            window._shellWork = new Rect(0, 0, 620, 290); window._dockY = 145;
            window._shellView = ShellView.Controls; window._orbHidden = false; Save("controls-small");
            window._shellWork = new Rect(-900, -100, 900, 600); window._dockSide = DockSide.Left; window._dockY = 200;
            window._shellView = ShellView.Conversation; Save("conversation-left");
            window._shellWork = new Rect(0, 0, 900, 600); window._dockSide = DockSide.Right; window._dockY = 300;
            var voiceGeneration = window._voiceChat.Start(1); // State-only preview: never opens an input device.
            window._voiceChat.Move(voiceGeneration, VoiceChatPhase.Waiting, VoiceChatPhase.Listening);
            window._spokenText.Text = "你：主题颜色在哪改？";
            window._status.Text = "这里显示助手的指路回复，你仍然自己操作软件。";
            window.UpdateVoiceChatUi(); Save("voice-listening");
            window._voiceChat.Move(voiceGeneration, VoiceChatPhase.Listening, VoiceChatPhase.Transcribing);
            window.UpdateVoiceChatUi(); Save("voice-transcribing");
            window._voiceChat.Stop(); window.UpdateVoiceChatUi();
            window._shellView = ShellView.ModelSettings; Save("model-settings");
            return 0;
        }
        finally { window.Close(); }
    }

    private static class ShellMonitorApi
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Info { internal int Size; internal Native.RECT Monitor, Work; internal uint Flags; }
        [DllImport("user32.dll")] internal static extern nint MonitorFromPoint(Native.POINT point, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] internal static extern bool GetMonitorInfo(nint monitor, ref Info info);
        [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    }
}
