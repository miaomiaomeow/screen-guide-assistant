using System.IO;
using System.Text;
using System.Text.Json;

namespace ScreenGuide;

// Only synthetic secrets, injected environment readers and a fake protector are used here.
// The temporary store is isolated from models.local.json/config.local.json in the running app.
internal static class ModelProfilesCheck
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
        var a = new ModelProfile("one", "方案一", "https://model.example.invalid/v1", "fixture-model", "FIXTURE_API_KEY");
        var b = new ModelProfile("two", "方案二", "https://other.example.invalid", "fixture-other");
        const string syntheticSecret = "synthetic-test-secret-never-a-real-key";

        Check("HTTPS URL and model metadata are normalized", () =>
        {
            var valid = ModelProfileStore.NormalizeProfile(a with { Name = "  方案一  ", BaseUrl = a.BaseUrl + "/", Model = " fixture-model " });
            return valid.Name == "方案一" && valid.BaseUrl == a.BaseUrl && valid.Model == a.Model;
        });
        foreach (var address in new[] { "http://model.example.invalid", "https://user:pass@model.example.invalid", "https://model.example.invalid?key=fixture", "https://model.example.invalid#fragment", "relative/path" })
        {
            var invalid = address;
            Check("invalid base address is rejected: " + results.Count, () => Rejects(() => ModelProfileStore.NormalizeProfile(a with { BaseUrl = invalid })));
        }
        Check("empty model is rejected", () => Rejects(() => ModelProfileStore.NormalizeProfile(a with { Model = " " })));
        Check("control characters in model are rejected", () => Rejects(() => ModelProfileStore.NormalizeProfile(a with { Model = "model\nname" })));
        Check("empty profile name is rejected", () => Rejects(() => ModelProfileStore.NormalizeProfile(a with { Name = " " })));
        foreach (var name in new[] { "1_KEY", "KEY-NAME", "A B", "$env:KEY", "KEY=VALUE" })
        {
            var invalid = name;
            Check("invalid environment name is rejected: " + results.Count, () => Rejects(() => ModelProfileStore.NormalizeProfile(a with { ApiKeyEnv = invalid })));
        }
        Check("empty environment name is allowed for stored keys", () => ModelProfileStore.NormalizeProfile(b).ApiKeyEnv.Length == 0);
        Check("invalid stored ciphertext is rejected", () => Rejects(() => ModelProfileStore.NormalizeProfile(a with { ProtectedApiKey = "not base64!" })));
        Check("duplicate identifiers are rejected", () => Rejects(() => ModelProfileStore.Normalize(new(a.Id, [a, b with { Id = "ONE" }]))));
        Check("duplicate names are rejected", () => Rejects(() => ModelProfileStore.Normalize(new(a.Id, [a, b with { Name = a.Name }]))));
        Check("implicit legacy name cannot collide", () => Rejects(() => ModelProfileStore.Normalize(new(a.Id, [a with { Name = ModelProfileStore.LegacyProfile.Name }]))));
        Check("missing active identifier is rejected", () => Rejects(() => ModelProfileStore.Normalize(new("absent", [a]))));
        Check("legacy identifier cannot disguise a new profile", () => Rejects(() => ModelProfileStore.NormalizeProfile(a with { Id = "legacy" })));
        Check("legacy bridge cannot contain secret metadata", () => Rejects(() => ModelProfileStore.NormalizeProfile(ModelProfileStore.LegacyProfile with { ApiKeyEnv = "FIXTURE_KEY" })));
        Check("model config string representation redacts the key", () => !new ModelConfig(syntheticSecret, "fixture-model").ToString().Contains(syntheticSecret, StringComparison.Ordinal));

        string parent = Path.GetFullPath(AppContext.BaseDirectory);
        string directory = Path.Combine(parent, "profile-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var protector = new FakeProtector();
            var environment = new Dictionary<string, string?>(); var environmentReads = new List<string>();
            string? Read(string name) { environmentReads.Add(name); return environment.GetValueOrDefault(name); }
            var legacy = new ModelConfig("synthetic-legacy-secret", "legacy-model", "https://legacy.example.invalid/v1");
            var store = new ModelProfileStore(directory, protector, Read, () => legacy);
            var path = Path.Combine(directory, "models.local.json");
            Check("fresh load defaults to legacy without writing a file", () => store.Load().ActiveId == ModelProfileStore.LegacyId && !File.Exists(path));
            Check("legacy resolution keeps original config behavior", () => store.LoadActiveOrLegacy() == legacy);
            Check("unknown provider is not mislabeled as NEBIUS default", () => !store.Load().Profiles.Any(profile => profile.Id == "nebius"));
            environment["DEEPSEEK_MODEL"] = "fixture-deepseek"; environment["NEBIUS_MODEL"] = "fixture-nebius";
            Check("known environment models seed both named templates", () =>
            {
                var defaults = store.Load();
                return defaults.Profiles.Any(profile => profile.Id == "deepseek" && profile.ApiKeyEnv == "DEEPSEEK_API_KEY")
                    && defaults.Profiles.Any(profile => profile.Id == "nebius" && profile.ApiKeyEnv == "NEBIUS_API_KEY");
            });
            Check("listing metadata does not read key environment values", () =>
            {
                environmentReads.Clear(); _ = store.Load();
                return environmentReads.All(name => !name.EndsWith("API_KEY", StringComparison.Ordinal));
            });
            var encrypted = store.WithSecret(a, syntheticSecret);
            Check("password field is protected before entering profile data", () => encrypted.ProtectedApiKey.Length > 0 && !encrypted.ProtectedApiKey.Contains(syntheticSecret, StringComparison.Ordinal));
            Check("profile string representation contains only its name", () => encrypted.ToString() == encrypted.Name);
            Check("empty password entry keeps previous encrypted key", () => store.WithSecret(encrypted, "").ProtectedApiKey == encrypted.ProtectedApiKey);
            Check("explicit key removal clears stored ciphertext", () => store.WithSecret(encrypted, null, clearStoredSecret: true).ProtectedApiKey.Length == 0);
            Check("replace and remove key cannot be combined", () => Rejects(() => store.WithSecret(encrypted, syntheticSecret, clearStoredSecret: true)));
            Check("legacy key cannot be migrated implicitly", () => Rejects(() => store.WithSecret(ModelProfileStore.LegacyProfile, syntheticSecret)));
            environment[a.ApiKeyEnv] = "synthetic-environment-priority";
            Check("environment value wins without even decrypting stored key", () =>
            {
                int before = protector.UnprotectCalls;
                return store.Resolve(encrypted).ApiKey == environment[a.ApiKeyEnv] && protector.UnprotectCalls == before;
            });
            environment[a.ApiKeyEnv] = " ";
            Check("missing environment value falls back to protected key", () => store.Resolve(encrypted).ApiKey == syntheticSecret);
            Check("no available key is reported by Ready=false", () => !store.Resolve(a).Ready);
            var first = new ModelProfilesSnapshot(a.Id, [encrypted, b]);
            Check("first save persists selection and encrypted metadata", () =>
            {
                store.Save(first); var loaded = store.Load();
                return loaded.ActiveId == a.Id && loaded.Profiles.Single(profile => profile.Id == a.Id).ProtectedApiKey == encrypted.ProtectedApiKey;
            });
            Check("saved file never contains plaintext synthetic secrets", () =>
            {
                var contents = File.ReadAllText(path);
                return !contents.Contains(syntheticSecret, StringComparison.Ordinal) && !contents.Contains(legacy.ApiKey, StringComparison.Ordinal)
                    && !contents.Contains("synthetic-environment-priority", StringComparison.Ordinal);
            });
            Check("active profile resolves after reload", () => store.LoadActiveOrLegacy().ApiKey == syntheticSecret);
            Check("unreadable protected key fails without leaking protector details", () =>
            {
                var invalidProtected = a with { ProtectedApiKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("wrong-purpose-fixture")) };
                try { store.Resolve(invalidProtected); return false; }
                catch (InvalidOperationException error)
                { return error.InnerException == null && !error.Message.Contains("Invalid fixture", StringComparison.Ordinal); }
            });
            Check("invalid save leaves the old active profile intact", () =>
                Rejects(() => store.Save(first with { ActiveId = "absent" })) && store.Load().ActiveId == a.Id);
            Check("successful replacement keeps the previous snapshot backup", () =>
            {
                store.Save(first with { ActiveId = b.Id });
                using var backup = JsonDocument.Parse(File.ReadAllText(path + ".bak"));
                return store.Load().ActiveId == b.Id && backup.RootElement.GetProperty("activeId").GetString() == a.Id;
            });
            Check("locked destination cannot partially replace saved configuration", () =>
            {
                using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return Rejects(() => store.Save(first)) && store.Load().ActiveId == b.Id;
            });
            Check("failed save cleans its temporary file", () => !Directory.EnumerateFiles(directory, ".models.local.*.tmp").Any());
            Check("unknown plaintext key fields are rejected instead of being imported", () =>
            {
                var original = File.ReadAllText(path);
                // No backup is available here: an accidental plaintext field must fail closed.
                var backup = File.ReadAllText(path + ".bak"); File.Delete(path + ".bak");
                try
                {
                    File.WriteAllText(path, original.Replace("\"protectedApiKey\":", "\"apiKey\": \"synthetic-forbidden-plaintext\", \"protectedApiKey\":", StringComparison.Ordinal));
                    return Rejects(() => store.Load());
                }
                finally { File.WriteAllText(path, original); File.WriteAllText(path + ".bak", backup); }
            });
            Check("corrupt main file can recover the previous backup", () =>
            {
                File.WriteAllText(path, "{corrupt");
                var recovered = store.Load(); return recovered.RecoveredFromBackup && recovered.ActiveId == a.Id;
            });
            Check("saving recovered data does not overwrite valid backup with corruption", () =>
            {
                store.Save(first with { ActiveId = b.Id }); File.WriteAllText(path, "{corrupt-again");
                var recovered = store.Load(); return recovered.RecoveredFromBackup && recovered.ActiveId == a.Id;
            });
            Check("legacy remains a selectable non-migrating bridge after save", () =>
            {
                store.Save(first with { ActiveId = ModelProfileStore.LegacyId });
                return store.LoadActiveOrLegacy() == legacy && !File.ReadAllText(path).Contains(legacy.ApiKey, StringComparison.Ordinal);
            });
        }
        finally
        {
            // Delete only the newly created, non-reparse child of this check's own output folder.
            string resolved = Path.GetFullPath(directory);
            if (resolved.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(resolved) && (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(resolved, recursive: true);
        }
        return results;
    }

    // Optional explicit Windows-runtime check. It never reads environment variables or real keys.
    internal static bool DpapiRoundTrip()
    {
        const string synthetic = "synthetic-dpapi-roundtrip-only";
        var protector = new WindowsDpapiSecretProtector();
        var encrypted = protector.Protect(synthetic);
        if (encrypted.Contains(synthetic, StringComparison.Ordinal) || protector.Unprotect(encrypted) != synthetic) return false;
        var bytes = Convert.FromBase64String(encrypted); bytes[^1] ^= 0x40;
        try { protector.Unprotect(Convert.ToBase64String(bytes)); return false; }
        catch (InvalidOperationException) { return true; }
    }

    private sealed class FakeProtector : IModelSecretProtector
    {
        internal int UnprotectCalls;
        public string Protect(string plainText) => Convert.ToBase64String(Encoding.UTF8.GetBytes("fake-test-only|" + plainText));
        public string Unprotect(string protectedText)
        {
            UnprotectCalls++;
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(protectedText));
            if (!text.StartsWith("fake-test-only|", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid fixture.");
            return text[15..];
        }
    }
}
