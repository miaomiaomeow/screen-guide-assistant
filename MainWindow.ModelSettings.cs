using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ScreenGuide;

internal sealed partial class MainWindow
{
    private readonly ModelProfileStore _profileStore = new();
    private ModelProfilesSnapshot _modelProfiles = new(ModelProfileStore.LegacyId, [ModelProfileStore.LegacyProfile]);
    private readonly StackPanel _modelSettingsView = new();
    private readonly ComboBox _profilePicker = new() { Height = 31, Focusable = false, DisplayMemberPath = "Name", SelectedValuePath = "Id" };
    private readonly TextBox _profileName = ProfileText(60), _profileUrl = ProfileText(500), _profileModel = ProfileText(200), _profileEnvironment = ProfileText(128);
    private readonly PasswordBox _profileSecret = new() { Height = 31, MaxLength = 8192, Padding = new Thickness(7, 3, 7, 3) };
    private readonly CheckBox _clearProfileSecret = new() { Content = "Clear stored key", Focusable = false, FontSize = 11, Foreground = ShellTheme.Muted };
    private readonly TextBlock _profileNotice = new() { FontSize = 11, Foreground = ShellTheme.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private ModelProfile? _editingProfile;
    private bool _fillingProfile;
    private string? _profileLoadError;

    private static TextBox ProfileText(int maxLength) => new()
    {
        Height = 31, MaxLength = maxLength, FontSize = 12, Foreground = ShellTheme.Ink,
        Padding = new Thickness(7, 3, 7, 3), VerticalContentAlignment = VerticalAlignment.Center
    };

    private void BuildModelSettings()
    {
        _modelSettingsView.Children.Add(ShellLabel("Saved connections", "Applied immediately"));
        _profilePicker.Margin = new Thickness(0, 5, 0, 7);
        _profilePicker.SelectionChanged += (_, _) => { if (!_fillingProfile && _profilePicker.SelectedItem is ModelProfile profile) FillProfile(profile); };
        _modelSettingsView.Children.Add(_profilePicker);
        var row = new UniformGrid { Columns = 3, Margin = new Thickness(-3, 0, -3, 7) };
        var create = ShellTheme.RoundedButton("New"); create.Click += (_, _) =>
        {
            _fillingProfile = true; _profilePicker.SelectedIndex = -1; _fillingProfile = false;
            FillProfile(new(Guid.NewGuid().ToString("N"), "新模型方案", "https://api.deepseek.com", ""));
        };
        var use = ShellTheme.RoundedButton("Use selected"); use.Click += (_, _) => UseSelectedProfile();
        var remove = ShellTheme.RoundedButton("Delete", quiet: true); remove.Click += (_, _) => DeleteSelectedProfile();
        foreach (var button in new[] { create, use, remove }) { button.Margin = new Thickness(3); row.Children.Add(button); }
        _modelSettingsView.Children.Add(row);
        void Field(string label, Control input)
        {
            _modelSettingsView.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = ShellTheme.Muted, Margin = new Thickness(0, 5, 0, 4) });
            _modelSettingsView.Children.Add(input);
        }
        Field("Profile name", _profileName); Field("API base URL (HTTPS)", _profileUrl);
        Field("Actual model ID", _profileModel); Field("Key environment variable (optional)", _profileEnvironment);
        Field("New key (optional; encrypted for this Windows user)", _profileSecret);
        _profileSecret.PasswordChanged += (_, _) => { if (_profileSecret.Password.Length > 0) _clearProfileSecret.IsChecked = false; };
        _clearProfileSecret.Margin = new Thickness(0, 9, 0, 6); _modelSettingsView.Children.Add(_clearProfileSecret);
        var save = ShellTheme.RoundedButton("Save and use", primary: true); save.Margin = new Thickness(0, 5, 0, 0);
        save.Click += (_, _) => SaveProfile(); _modelSettingsView.Children.Add(save);
        _modelSettingsView.Children.Add(_profileNotice);
        RefreshProfilePicker(_modelProfiles.ActiveId);
    }

    private void RefreshProfilePicker(string? selected)
    {
        _fillingProfile = true;
        try { _profilePicker.ItemsSource = _modelProfiles.Profiles; _profilePicker.SelectedValue = selected; }
        finally { _fillingProfile = false; }
        if (_profilePicker.SelectedItem is ModelProfile profile) FillProfile(profile);
    }

    private void FillProfile(ModelProfile profile)
    {
        _editingProfile = profile; _profileSecret.Clear(); _clearProfileSecret.IsChecked = false;
        _profileName.Text = profile.Name; _profileUrl.Text = profile.BaseUrl; _profileModel.Text = profile.Model;
        _profileEnvironment.Text = profile.ApiKeyEnv;
        bool editable = !profile.UseLegacy;
        foreach (var input in ModelEditableInputs()) input.IsEnabled = editable;
        _clearProfileSecret.IsEnabled = editable;
        _profileNotice.Text = _profileLoadError ?? (profile.UseLegacy
            ? "兼容原有环境变量与 config.local.json，不显示旧密钥。可直接使用；修改参数请新建方案。"
            : "输入新密钥时改用本地加密保存，并清空环境变量名；留空则沿用原来源。" +
              (profile.ProtectedApiKey.Length > 0 ? " 此方案已有加密密钥。" : ""));
        if (_modelProfiles.RecoveredFromBackup) _profileNotice.Text += " 模型方案已从本地备份恢复读取。";
        if (_shellReady && !_closed) RefreshShell(false);
    }

    private IEnumerable<FrameworkElement> ModelEditableInputs() => [_profileName, _profileUrl, _profileModel, _profileEnvironment, _profileSecret];

    private void SaveProfile()
    {
        var editing = _editingProfile;
        if (editing == null || editing.UseLegacy) { _profileNotice.Text = "请先新建方案；当前旧配置继续兼容保留。"; return; }
        try
        {
            var password = _profileSecret.Password;
            var profile = editing with { Name = _profileName.Text, BaseUrl = _profileUrl.Text, Model = _profileModel.Text,
                ApiKeyEnv = string.IsNullOrWhiteSpace(password) ? _profileEnvironment.Text : "" };
            profile = _profileStore.WithSecret(profile, password, _clearProfileSecret.IsChecked == true);
            var config = _profileStore.Resolve(profile);
            if (!config.Ready) throw new InvalidOperationException("此方案尚无可用密钥，请检查环境变量或输入新密钥。");
            var profiles = _modelProfiles.Profiles.Where(p => p.Id != profile.Id).Append(profile).ToArray();
            var next = ModelProfileStore.Normalize(new(profile.Id, profiles));
            _profileStore.Save(next); // Only commit in-memory state after a durable write.
            _modelProfiles = next; _profileLoadError = null; _profileSecret.Clear();
            ApplyModelProfile(config, profile.Name); RefreshProfilePicker(profile.Id);
            _profileNotice.Text = "已保存并使用，无需重启。图像能力取决于所选模型；本次未发送测试请求。";
        }
        catch (Exception error) { ProfileError(error); }
    }

    private void UseSelectedProfile()
    {
        if (_profilePicker.SelectedItem is not ModelProfile profile) { _profileNotice.Text = "请先选择已保存的方案。"; return; }
        try
        {
            var config = _profileStore.Resolve(profile);
            if (!config.Ready) throw new InvalidOperationException("此方案尚无可用密钥，请先完成配置。");
            var next = _modelProfiles with { ActiveId = profile.Id };
            _profileStore.Save(next); _modelProfiles = next; _profileSecret.Clear();
            ApplyModelProfile(config, profile.Name); _profileNotice.Text = "已切换，无需重启；旧请求与高亮已取消。";
        }
        catch (Exception error) { ProfileError(error); }
    }

    private void DeleteSelectedProfile()
    {
        if (_profilePicker.SelectedItem is not ModelProfile profile || profile.UseLegacy)
        { _profileNotice.Text = "当前旧配置兼容入口保留。"; return; }
        try
        {
            bool active = _modelProfiles.ActiveId == profile.Id;
            var next = new ModelProfilesSnapshot(active ? ModelProfileStore.LegacyId : _modelProfiles.ActiveId,
                _modelProfiles.Profiles.Where(p => p.Id != profile.Id).ToArray());
            var replacement = active ? _profileStore.Resolve(ModelProfileStore.LegacyProfile) : null;
            _profileStore.Save(next); _modelProfiles = next;
            if (replacement != null) ApplyModelProfile(replacement, ModelProfileStore.LegacyProfile.Name);
            RefreshProfilePicker(next.ActiveId); _profileNotice.Text = "方案已删除。";
        }
        catch (Exception error) { ProfileError(error); }
    }

    private void ApplyModelProfile(ModelConfig config, string name)
    {
        Reset(); _activeModel = config; _mapper = new(config); _visionMapper = new(config); _modelReady = config.Ready;
        _mode.Text = _manual.IsChecked == true ? "Manual · no model calls" : config.Ready ? config.DisplayName : "Connection not configured";
        _imageMode.IsEnabled = _manual.IsChecked != true;
        SetStatus("已使用模型方案：" + name + "。本轮已重置。");
        Log.Write("model-profile", "applied ready=" + config.Ready);
    }

    private void ProfileError(Exception error)
    {
        _profileNotice.Text = error is InvalidOperationException ? error.Message : "模型方案操作失败，原配置保持使用。";
        Log.Write("model-profile-error", error.GetType().Name);
    }

    private void ShowModelSettings()
    {
        Reset(); RefreshProfilePicker(_modelProfiles.ActiveId);
        _shellView = ShellView.ModelSettings; _orbHidden = false;
        if (_shellReady) RefreshShell(true); else UpdateCardMode();
    }

    private void ShowControls()
    {
        ClearModelSecretInput(); Keyboard.ClearFocus();
        _shellView = ShellView.Controls; _orbHidden = false;
        if (_shellReady) RefreshShell(true); else UpdateCardMode();
    }

    private void ClearModelSecretInput() => _profileSecret.Clear();

    private bool EditableShellHit(Point physical)
    {
        IEnumerable<FrameworkElement> inputs = [_question, .. ModelEditableInputs()];
        foreach (var input in inputs)
        {
            if (!input.IsVisible || !input.IsEnabled || input is TextBox { IsReadOnly: true }) continue;
            var point = input.PointFromScreen(physical);
            if (point.X >= 0 && point.X <= input.ActualWidth && point.Y >= 0 && point.Y <= input.ActualHeight) return true;
        }
        return false;
    }
}
