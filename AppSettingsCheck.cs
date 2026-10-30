using System.IO;
using System.Text.Json;
using System.Windows;

namespace ScreenGuide;

// Isolated preference files only: never opens the desktop, microphone or a model endpoint.
internal static class AppSettingsCheck
{
    internal static IReadOnlyList<(string Label, bool Passed)> Checks()
    {
        var results = new List<(string Label, bool Passed)>();
        void Check(string label, Func<bool> test)
        {
            try { results.Add((label, test())); }
            catch { results.Add((label, false)); }
        }
        bool Rejects(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
        var initial = AppSettings.Defaults;
        Check("showcase preferences default to hybrid and English SenseVoice", () => initial.ScreenMode == GuideMode.Hybrid && initial.SpeechEngine == SpeechEngine.SenseVoice);
        foreach (var mode in Enum.GetValues<GuideMode>())
        {
            var value = mode;
            Check("preference mode identifier round trip: " + mode, () => AppSettingsStore.TryMode(AppSettingsStore.ModeId(value), out var parsed) && parsed == value);
        }
        foreach (var text in new string?[] { null, "", "Hybrid", "controls ", "unknown", "2" })
        {
            var value = text;
            Check("unknown preference mode is rejected: " + results.Count, () => !AppSettingsStore.TryMode(value, out _));
        }
        foreach (var ratio in new[] { double.NaN, double.PositiveInfinity, -.1, 1.1 })
        {
            var value = ratio;
            Check("invalid dock ratio is rejected: " + results.Count, () => Rejects(() => AppSettingsStore.Validate(initial with { DockYRatio = value })));
        }
        Check("unknown screen mode is rejected", () => Rejects(() => AppSettingsStore.Validate(initial with { ScreenMode = (GuideMode)999 })));
        Check("unknown speech preference is rejected", () => Rejects(() => AppSettingsStore.Validate(initial with { SpeechEngine = (SpeechEngine)999 })));
        Check("unknown dock side is rejected", () => Rejects(() => AppSettingsStore.Validate(initial with { DockSide = (DockSide)999 })));
        var oldWork = new Rect(-1500, -180, 1400, 900); var newWork = new Rect(30, 40, 900, 600);
        foreach (double ratio in new[] { 0, .125, .5, .8, 1 })
        {
            double value = ratio;
            Check("dock ratio survives monitor resize: " + ratio, () =>
                Math.Abs(AppSettingsGeometry.RatioFromAnchor(oldWork, AppSettingsGeometry.AnchorFromRatio(oldWork, value)) - value) < .00001
                && Math.Abs(AppSettingsGeometry.RatioFromAnchor(newWork, AppSettingsGeometry.AnchorFromRatio(newWork, value)) - value) < .00001);
        }
        Check("dock restore handles work area shorter than the orb", () => AppSettingsGeometry.AnchorFromRatio(new Rect(0, 100, 50, 20), 1) == 110);
        Check("out-of-range dock position becomes an edge ratio", () => AppSettingsGeometry.RatioFromAnchor(newWork, -9999) == 0 && AppSettingsGeometry.RatioFromAnchor(newWork, 9999) == 1);
        string parent = Path.GetFullPath(AppContext.BaseDirectory);
        string directory = Path.Combine(parent, "settings-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "settings.local.json"), backup = path + ".bak";
        var store = new AppSettingsStore(directory);
        var changed = new AppSettings(GuideMode.Controls, SpeechEngine.Paraformer, DockSide.Left, .3125);
        try
        {
            Check("fresh preferences do not write a file", () => store.Load() == initial && !File.Exists(path));
            Check("all preference fields survive initial save and reload", () => { store.Save(changed); return new AppSettingsStore(directory).Load() == changed; });
            Check("preferences contain only the version and four safe fields", () => {
                using var file = JsonDocument.Parse(File.ReadAllText(path));
                return file.RootElement.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(
                    new[] { "version", "screenMode", "speechEngine", "dockSide", "dockYRatio" }.Order());
            });
            Check("invalid preference update leaves previous file intact", () => Rejects(() => store.Save(changed with { DockYRatio = 4 })) && store.Load() == changed);
            Check("atomic replacement preserves previous preferences in backup", () => {
                store.Save(initial);
                string backupDirectory = Path.Combine(directory, "backup-fixture"); Directory.CreateDirectory(backupDirectory);
                File.Copy(backup, Path.Combine(backupDirectory, "settings.local.json"));
                return store.Load() == initial && new AppSettingsStore(backupDirectory).Load() == changed;
            });
            Check("locked preference file reports failure without changing saved value", () => {
                using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return Rejects(() => store.Save(changed)) && store.Load() == initial;
            });
            Check("failed preference write leaves no temporary file", () => !Directory.EnumerateFiles(directory, ".settings.local.*.tmp").Any());
            Check("corrupt main preferences recover the previous valid backup", () => {
                File.WriteAllText(path, "{broken"); var recovered = store.Load();
                return recovered.RecoveredFromBackup && (recovered with { RecoveredFromBackup = false }) == changed;
            });
            Check("saving recovered preferences keeps good backup safe from corruption", () => {
                store.Save(initial); File.WriteAllText(path, "{broken-again"); var recovered = store.Load();
                return recovered.RecoveredFromBackup && (recovered with { RecoveredFromBackup = false }) == changed;
            });
            Check("missing main preferences recover backup", () => {
                File.Delete(path); return store.Load().RecoveredFromBackup;
            });
            Check("corrupt main and backup both fail visibly", () => {
                File.WriteAllText(path, "{bad"); File.WriteAllText(backup, "{bad"); return Rejects(() => store.Load());
            });
            File.Delete(backup);
            string original;
            store.Save(initial); original = File.ReadAllText(path); File.Delete(backup);
            foreach (var invalid in new[] {
                original.Replace("\"version\": 1", "\"version\": 999", StringComparison.Ordinal),
                original.Replace("\"hybrid\"", "\"unknown\"", StringComparison.Ordinal),
                original.Replace("\"sensevoice\"", "\"unknown\"", StringComparison.Ordinal),
                original.Replace("\"right\"", "\"unknown\"", StringComparison.Ordinal),
                "{}", "null", original.Replace("{", "{\"microphoneOn\":true,", StringComparison.Ordinal),
                new string(' ', 17_000) })
            {
                string fixture = invalid;
                Check("invalid preference schema is rejected: " + results.Count, () => { File.WriteAllText(path, fixture); return Rejects(() => store.Load()); });
            }
            Check("interrupted temporary file is ignored", () => {
                File.Delete(path); File.WriteAllText(Path.Combine(directory, ".settings.local.interrupted.tmp"), "{bad");
                return store.Load() == initial;
            });
        }
        finally
        {
            string resolved = Path.GetFullPath(directory);
            if (resolved.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(resolved) && (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(resolved, recursive: true);
        }
        return results;
    }
}
