using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace ScreenGuide;

// Preferences only: never persist microphone state, questions, windows or screen images.
internal sealed record AppSettings(GuideMode ScreenMode, SpeechEngine SpeechEngine, DockSide DockSide, double DockYRatio)
{
    internal static AppSettings Defaults => new(GuideMode.Hybrid, SpeechEngine.SenseVoice, DockSide.Right, .75);
    internal bool RecoveredFromBackup { get; init; }
}

internal sealed class AppSettingsStore
{
    private const int MaximumBytes = 16_384;
    private readonly string _directory, _path, _backup;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    internal AppSettingsStore(string? directory = null)
    {
        _directory = Path.GetFullPath(directory ?? AppContext.BaseDirectory);
        _path = Path.Combine(_directory, "settings.local.json"); _backup = _path + ".bak";
    }

    internal AppSettings Load()
    {
        if (File.Exists(_path))
        {
            try { return Read(_path); }
            catch
            {
                try { return Read(_backup) with { RecoveredFromBackup = true }; }
                catch { throw new InvalidOperationException("设置文件无法读取，已使用默认设置；原文件保留。"); }
            }
        }
        if (File.Exists(_backup))
        {
            try { return Read(_backup) with { RecoveredFromBackup = true }; }
            catch { throw new InvalidOperationException("设置备份无法读取，已使用默认设置；原文件保留。"); }
        }
        return AppSettings.Defaults;
    }

    internal void Save(AppSettings settings)
    {
        var valid = Validate(settings);
        var data = new DiskSettings(1, ModeId(valid.ScreenMode), SpeechEngines.Id(valid.SpeechEngine),
            valid.DockSide == DockSide.Left ? "left" : "right", valid.DockYRatio);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
        string temporary = Path.Combine(_directory, $".settings.local.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(_directory);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(bytes); output.Flush(flushToDisk: true); }
            if (File.Exists(_path))
            {
                string? backup = _backup;
                try { _ = Read(_path); }
                catch { try { _ = Read(_backup); backup = null; } catch { } }
                File.Replace(temporary, _path, backup, ignoreMetadataErrors: true);
            }
            else File.Move(temporary, _path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("设置保存失败，请检查目录空间与写入权限；本次更改仅在当前运行有效。"); }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    private static AppSettings Read(string path)
    {
        if (new FileInfo(path).Length > MaximumBytes) throw new InvalidOperationException("设置文件过大。");
        var disk = JsonSerializer.Deserialize<DiskSettings>(File.ReadAllBytes(path), JsonOptions)
            ?? throw new InvalidOperationException("设置文件为空。");
        if (disk.Version != 1 || !TryMode(disk.ScreenMode, out var mode)
            || !TrySpeech(disk.SpeechEngine, out var speech) || disk.DockSide is not ("left" or "right") || disk.DockYRatio is null)
            throw new InvalidOperationException("设置文件格式无效。");
        return Validate(new(mode, speech, disk.DockSide == "left" ? DockSide.Left : DockSide.Right, disk.DockYRatio.Value));
    }

    internal static AppSettings Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(settings.ScreenMode) || !SpeechEngines.All.Contains(settings.SpeechEngine)
            || !Enum.IsDefined(settings.DockSide) || !double.IsFinite(settings.DockYRatio) || settings.DockYRatio is < 0 or > 1)
            throw new InvalidOperationException("设置内容无效，未保存。");
        return settings with { RecoveredFromBackup = false };
    }

    internal static string ModeId(GuideMode mode) => mode switch
    {
        GuideMode.Controls => "controls", GuideMode.DirectImage => "direct_image",
        GuideMode.ImageOnControlFailure => "image_on_failure", GuideMode.Hybrid => "hybrid",
        _ => throw new InvalidOperationException("屏幕读取模式无效。")
    };

    internal static bool TryMode(string? value, out GuideMode mode)
    {
        mode = value switch
        {
            "controls" => GuideMode.Controls, "direct_image" => GuideMode.DirectImage,
            "image_on_failure" => GuideMode.ImageOnControlFailure, "hybrid" => GuideMode.Hybrid,
            _ => (GuideMode)(-1)
        };
        return Enum.IsDefined(mode);
    }

    private static bool TrySpeech(string? value, out SpeechEngine speech)
    {
        speech = SpeechEngines.All.FirstOrDefault(engine => SpeechEngines.Id(engine) == value, (SpeechEngine)(-1));
        return SpeechEngines.All.Contains(speech);
    }

    private sealed record DiskSettings(int Version, string? ScreenMode, string? SpeechEngine, string? DockSide, double? DockYRatio);
}

internal static class AppSettingsGeometry
{
    internal static double AnchorFromRatio(Rect workArea, double ratio)
    {
        if (!double.IsFinite(ratio) || ratio is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(ratio));
        return EdgeDock.ClampAnchorY(workArea, workArea.Top + EdgeDock.OrbDiameter / 2
            + Math.Max(0, workArea.Height - EdgeDock.OrbDiameter) * ratio);
    }

    internal static double RatioFromAnchor(Rect workArea, double anchor)
    {
        double bounded = EdgeDock.ClampAnchorY(workArea, anchor);
        return workArea.Height <= EdgeDock.OrbDiameter ? .5
            : Math.Clamp((bounded - workArea.Top - EdgeDock.OrbDiameter / 2) / (workArea.Height - EdgeDock.OrbDiameter), 0, 1);
    }
}
