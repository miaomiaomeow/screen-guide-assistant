using System.Windows;
using System.Windows.Controls;

namespace ScreenGuide;

internal sealed partial class MainWindow
{
    private readonly AppSettingsStore _settingsStore = new();
    private readonly TextBlock _settingsNotice = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = ShellTheme.Muted, Margin = new Thickness(0, 5, 0, 0) };
    private AppSettings _savedSettings = AppSettings.Defaults;
    private int _settingsSuppressed;
    private bool _settingsApplied;
    private GuideMode PreferredGuideMode => _savedSettings.ScreenMode;

    // Call before BuildShell. Preview rendering must never read/write real user settings.
    private void LoadSettings()
    {
        if (_shellPreview) return;
        try
        {
            _savedSettings = _settingsStore.Load();
            _settingsNotice.Text = _savedSettings.RecoveredFromBackup ? "设置文件损坏，已恢复备份。" : "Settings save automatically. Microphone starts off.";
        }
        catch (Exception error)
        {
            _savedSettings = AppSettings.Defaults;
            _settingsNotice.Text = error is InvalidOperationException ? error.Message : "设置读取失败，已使用默认设置；原文件保留。";
            Log.Write("settings", "load-failed " + error.GetType().Name);
        }
    }

    // Call after BuildShell. Docking geometry is applied later, once the current monitor is known.
    private void ApplySavedSettings()
    {
        WithoutSettingsPersistence(() => {
            _imageMode.SelectedValue = _savedSettings.ScreenMode;
            _speechEngine.SelectedValue = _savedSettings.SpeechEngine;
            _dockSide = _savedSettings.DockSide;
        });
        _settingsApplied = true;
    }

    // Plugin requests may temporarily select a mode without overwriting the user's preference.
    private void WithoutSettingsPersistence(Action action)
    {
        _settingsSuppressed++;
        try { action(); }
        finally { _settingsSuppressed--; }
    }

    private void RememberScreenMode()
    {
        if (!_settingsApplied || _settingsSuppressed != 0 || _manual.IsChecked == true) return;
        if (_imageMode.SelectedValue is GuideMode mode && Enum.IsDefined(mode))
        { _savedSettings = _savedSettings with { ScreenMode = mode }; PersistSettings(); }
    }

    private void RememberSpeechEngine()
    {
        if (!_settingsApplied || _settingsSuppressed != 0) return;
        _savedSettings = _savedSettings with { SpeechEngine = SelectedSpeechEngine() }; PersistSettings();
    }

    // Immediate on preference changes and drag completion; also call before closing the shell.
    private void PersistSettings()
    {
        if (_shellPreview || !_settingsApplied || _settingsSuppressed != 0) return;
        if (_shellReady)
            _savedSettings = _savedSettings with { DockSide = _dockSide, DockYRatio = AppSettingsGeometry.RatioFromAnchor(_shellWork, _dockY) };
        try
        {
            _settingsStore.Save(_savedSettings);
            _settingsNotice.Text = "Settings saved · microphone starts off";
            Log.Write("settings", "saved mode=" + AppSettingsStore.ModeId(_savedSettings.ScreenMode));
        }
        catch (Exception error)
        {
            _settingsNotice.Text = error is InvalidOperationException ? error.Message : "设置保存失败，本次更改仅在当前运行有效。";
            Log.Write("settings", "save-failed " + error.GetType().Name);
        }
    }

    // Exercise real WPF selection/checkbox events offscreen. Preview mode short-circuits
    // preference disk access; sentinel state verifies these guards without inspecting
    // any real user's settings file. No Show, UIA, capture, model or audio operation.
    internal static IEnumerable<(string Label, bool Passed)> PreferenceUiChecks()
    {
        var results = new List<(string Label, bool Passed)>();
        void Check(string label, Func<bool> test)
        {
            try { results.Add((label, test())); }
            catch { results.Add((label, false)); }
        }
        MainWindow? window = null;
        try
        {
            window = new MainWindow(preview: true);
            Check("preference UI checks keep window offscreen and native timer stopped", () =>
                window._shellPreview && !window.IsVisible && window._hwnd == 0 && !window._timer.IsEnabled && !window._shellTimer.IsEnabled);
            Check("preference UI starts without microphone or active guide", () =>
                !window._speech.IsBusy && !window._voiceChat.Enabled && window._session == null && window._frame == null);

            var fixture = new AppSettings(GuideMode.DirectImage, SpeechEngine.SenseVoice, DockSide.Left, .27);
            window._savedSettings = fixture;
            window._settingsNotice.Text = "fixture-preference-io-guard";
            window.LoadSettings();
            Check("preview preference load preserves sentinel instead of reading user file", () =>
                window._savedSettings == fixture && window._settingsNotice.Text == "fixture-preference-io-guard");
            window._settingsApplied = false;
            window.ApplySavedSettings();
            Check("saved reading mode reaches real dropdown without initialization overwrite", () =>
                Equals(window._imageMode.SelectedValue, fixture.ScreenMode) && window.PreferredGuideMode == fixture.ScreenMode && window._savedSettings == fixture);
            Check("saved speech and dock side are restored by UI initialization", () =>
                Equals(window._speechEngine.SelectedValue, fixture.SpeechEngine) && window._dockSide == fixture.DockSide && window._settingsApplied);
            Check("initialization suppression unwinds after applying saved preferences", () => window._settingsSuppressed == 0);

            foreach (var mode in Enum.GetValues<GuideMode>())
            {
                var value = mode;
                Check("user dropdown updates remembered reading mode: " + value, () => {
                    window._imageMode.SelectedValue = value;
                    return window.PreferredGuideMode == value && window._savedSettings.ScreenMode == value;
                });
            }
            foreach (var engine in SpeechEngines.All)
            {
                var value = engine;
                Check("user dropdown updates remembered speech engine: " + value, () => {
                    window._speechEngine.SelectedValue = value;
                    return window._savedSettings.SpeechEngine == value && window.PreferredGuideMode == window._savedSettings.ScreenMode;
                });
            }

            window._imageMode.SelectedValue = GuideMode.Hybrid;
            window._speechEngine.SelectedValue = SpeechEngine.Paraformer;
            var preferred = window._savedSettings;
            window.WithoutSettingsPersistence(() => {
                window._imageMode.SelectedValue = GuideMode.DirectImage;
                window._speechEngine.SelectedValue = SpeechEngine.SenseVoice;
            });
            Check("temporary request selection changes UI without replacing saved preferences", () =>
                Equals(window._imageMode.SelectedValue, GuideMode.DirectImage) && window._savedSettings == preferred && window.PreferredGuideMode == GuideMode.Hybrid);
            Check("temporary speech selection does not replace saved engine", () =>
                Equals(window._speechEngine.SelectedValue, SpeechEngine.SenseVoice) && window._savedSettings.SpeechEngine == SpeechEngine.Paraformer);

            window._manual.IsChecked = true;
            Check("manual mode uses controls without overwriting hybrid preference", () =>
                window.SelectedMode() == GuideMode.Controls && !window._imageMode.IsEnabled && window.PreferredGuideMode == GuideMode.Hybrid && window._savedSettings == preferred);
            window._manual.IsChecked = false;
            Check("leaving manual mode restores remembered screen preference", () =>
                window.SelectedMode() == GuideMode.Hybrid && window._imageMode.IsEnabled && window._savedSettings == preferred);

            try
            {
                window.WithoutSettingsPersistence(() => window.WithoutSettingsPersistence(() =>
                    throw new InvalidOperationException("synthetic preference scope check")));
            }
            catch (InvalidOperationException) { }
            Check("nested preference suppression unwinds even if operation throws", () => window._settingsSuppressed == 0);
            window._imageMode.SelectedValue = GuideMode.ImageOnControlFailure;
            Check("user selection still persists in memory after exceptional temporary scope", () => window.PreferredGuideMode == GuideMode.ImageOnControlFailure);

            window._settingsNotice.Text = "fixture-preview-save-guard";
            var before = window._savedSettings;
            window.PersistSettings();
            Check("preview save skips disk and never falsely reports saved", () =>
                window._settingsNotice.Text == "fixture-preview-save-guard" && window._savedSettings == before);
            Check("preference event checks never start recording or voice conversation", () =>
                !window._speech.IsBusy && !window._voiceChat.Enabled && window._speechRequest == null && window._request == null);
        }
        catch
        {
            results.Add(("offscreen preference UI setup completed", false));
        }
        finally { window?.Close(); }
        return results;
    }
}
