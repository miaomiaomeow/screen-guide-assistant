using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ScreenGuide;

internal sealed record ModelProfile(string Id, string Name, string BaseUrl, string Model,
    string ApiKeyEnv = "", string ProtectedApiKey = "", bool UseLegacy = false)
{
    public override string ToString() => Name;
}

internal sealed record ModelProfilesSnapshot(string? ActiveId, IReadOnlyList<ModelProfile> Profiles)
{
    internal bool RecoveredFromBackup { get; init; }
}

internal interface IModelSecretProtector
{
    string Protect(string plainText);
    string Unprotect(string protectedText);
}

// Profile metadata may be listed without resolving any secret. Resolve chooses only this
// profile's named environment variable, then its DPAPI ciphertext; legacy remains a separate choice.
internal sealed class ModelProfileStore
{
    internal const string LegacyId = "legacy";
    internal static ModelProfile LegacyProfile => new(LegacyId, "Environment / local configuration", "", "", UseLegacy: true);
    private const int MaximumFileBytes = 1_048_576;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _directory, _path, _backup;
    private readonly IModelSecretProtector _protector;
    private readonly Func<string, string?> _environmentReader;
    private readonly Func<ModelConfig> _legacyLoader;

    internal ModelProfileStore(string? directory = null, IModelSecretProtector? protector = null,
        Func<string, string?>? environmentReader = null, Func<ModelConfig>? legacyLoader = null)
    {
        _directory = Path.GetFullPath(directory ?? AppContext.BaseDirectory);
        _path = Path.Combine(_directory, "models.local.json"); _backup = _path + ".bak";
        _protector = protector ?? new WindowsDpapiSecretProtector();
        _environmentReader = environmentReader ?? ReadEnvironment;
        _legacyLoader = legacyLoader ?? ModelConfig.Load;
    }

    internal ModelProfilesSnapshot Load()
    {
        if (File.Exists(_path))
        {
            try { return ReadSnapshot(_path); }
            catch
            {
                if (File.Exists(_backup))
                {
                    try { return ReadSnapshot(_backup) with { RecoveredFromBackup = true }; } catch { }
                }
                throw new InvalidOperationException("模型方案文件无法读取，原文件已保留；请检查 models.local.json。");
            }
        }
        if (File.Exists(_backup))
        {
            try { return ReadSnapshot(_backup) with { RecoveredFromBackup = true }; }
            catch { throw new InvalidOperationException("模型方案备份无法读取，原文件已保留。"); }
        }
        return Defaults();
    }

    internal void Save(ModelProfilesSnapshot snapshot)
    {
        var valid = Normalize(snapshot);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new DiskSnapshot(1, valid.ActiveId, valid.Profiles.ToArray()), JsonOptions);
        if (bytes.Length > MaximumFileBytes) throw new InvalidOperationException("模型方案文件过大，未保存。");
        var temporary = Path.Combine(_directory, $".models.local.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(_directory);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            { output.Write(bytes); output.Flush(flushToDisk: true); }
            // Replacement is atomic on the same volume. Never delete the old destination first.
            if (File.Exists(_path))
            {
                string? backup = _backup;
                try { _ = ReadSnapshot(_path); }
                catch
                {
                    // A recovered backup must not be overwritten by the corrupt main file.
                    try { _ = ReadSnapshot(_backup); backup = null; } catch { }
                }
                File.Replace(temporary, _path, backup, ignoreMetadataErrors: true);
            }
            else File.Move(temporary, _path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("模型方案保存失败，请检查目录空间与写入权限；原配置仍保留。"); }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    internal ModelConfig LoadActiveOrLegacy()
    {
        var snapshot = Load();
        if (snapshot.ActiveId == null || snapshot.ActiveId == LegacyId) return _legacyLoader();
        return Resolve(snapshot.Profiles.Single(profile => profile.Id == snapshot.ActiveId));
    }

    internal ModelConfig Resolve(ModelProfile profile)
    {
        var valid = NormalizeProfile(profile);
        if (valid.UseLegacy) return _legacyLoader();
        var key = valid.ApiKeyEnv.Length == 0 ? null : _environmentReader(valid.ApiKeyEnv);
        if (string.IsNullOrWhiteSpace(key))
        {
            try { key = valid.ProtectedApiKey.Length == 0 ? "" : _protector.Unprotect(valid.ProtectedApiKey); }
            catch { throw new InvalidOperationException("无法在当前 Windows 用户下读取保存的密钥，请重新输入。"); }
        }
        key = key?.Trim() ?? "";
        if (key.Any(char.IsControl)) throw new InvalidOperationException("密钥格式无效，请检查环境变量或重新输入。");
        return new(key, valid.Model, valid.BaseUrl);
    }

    // An empty password field means keep the stored key. Clearing is a separate, explicit action.
    internal ModelProfile WithSecret(ModelProfile profile, string? newSecret, bool clearStoredSecret = false)
    {
        var valid = NormalizeProfile(profile);
        if (valid.UseLegacy) throw new InvalidOperationException("当前旧配置仅作兼容入口，请新建方案后输入密钥。");
        if (clearStoredSecret && !string.IsNullOrWhiteSpace(newSecret))
            throw new InvalidOperationException("不能同时清除并替换密钥。");
        if (clearStoredSecret) return valid with { ProtectedApiKey = "" };
        if (string.IsNullOrWhiteSpace(newSecret)) return valid;
        var secret = newSecret.Trim();
        if (secret.Length > 8192 || secret.Any(char.IsControl)) throw new InvalidOperationException("密钥格式无效或过长。");
        string protectedKey;
        try { protectedKey = _protector.Protect(secret); }
        catch { throw new InvalidOperationException("密钥保护失败，请检查当前 Windows 用户后重试。"); }
        return NormalizeProfile(valid with { ProtectedApiKey = protectedKey });
    }

    internal static ModelProfilesSnapshot Normalize(ModelProfilesSnapshot snapshot)
    {
        if (snapshot.Profiles == null || snapshot.Profiles.Count > 64) throw new InvalidOperationException("模型方案数量无效。");
        var profiles = snapshot.Profiles.Select(NormalizeProfile).ToList();
        if (!profiles.Any(profile => profile.UseLegacy)) profiles.Insert(0, LegacyProfile);
        if (profiles.Count > 64) throw new InvalidOperationException("模型方案最多 64 个（含旧配置兼容入口）。");
        if (profiles.Select(profile => profile.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profiles.Count
            || profiles.Select(profile => profile.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profiles.Count)
            throw new InvalidOperationException("模型方案的名称或标识重复。");
        var active = string.IsNullOrWhiteSpace(snapshot.ActiveId) ? LegacyId : snapshot.ActiveId.Trim();
        var selected = profiles.SingleOrDefault(profile => profile.Id.Equals(active, StringComparison.OrdinalIgnoreCase));
        if (selected == null) throw new InvalidOperationException("当前模型方案不存在，未保存。");
        return new(selected.Id, profiles.ToArray());
    }

    internal static ModelProfile NormalizeProfile(ModelProfile profile)
    {
        if (profile == null || string.IsNullOrWhiteSpace(profile.Id)
            || !Regex.IsMatch(profile.Id, @"\A[A-Za-z0-9_-]{1,64}\z"))
            throw new InvalidOperationException("模型方案标识无效。");
        var name = profile.Name?.Trim() ?? "";
        if (name.Length == 0 || name.Length > 60 || name.Any(char.IsControl))
            throw new InvalidOperationException("方案名称需为 1–60 个可显示字符。");
        if (profile.UseLegacy)
        {
            if (profile.Id != LegacyId || !string.IsNullOrEmpty(profile.BaseUrl) || !string.IsNullOrEmpty(profile.Model)
                || !string.IsNullOrEmpty(profile.ApiKeyEnv) || !string.IsNullOrEmpty(profile.ProtectedApiKey))
                throw new InvalidOperationException("旧配置兼容入口不能存放模型参数或密钥。");
            return profile with { Name = name };
        }
        if (profile.Id.Equals(LegacyId, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("此方案标识为旧配置保留。");
        var address = profile.BaseUrl?.Trim() ?? "";
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host.Length == 0
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("接口地址必须为 HTTPS 基础地址，不能包含账号、查询参数或片段。");
        var model = profile.Model?.Trim() ?? "";
        if (model.Length == 0 || model.Length > 200 || model.Any(char.IsControl))
            throw new InvalidOperationException("请填写有效模型名称（1–200 字符）。");
        var environment = profile.ApiKeyEnv?.Trim() ?? "";
        if (environment.Length != 0 && !Regex.IsMatch(environment, @"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z"))
            throw new InvalidOperationException("密钥环境变量名只能包含字母、数字及下划线，且不能以数字开头。");
        var protectedKey = profile.ProtectedApiKey ?? "";
        if (protectedKey.Length != 0)
        {
            if (protectedKey.Length > 131072 || protectedKey.Any(char.IsWhiteSpace))
                throw new InvalidOperationException("保存的密钥密文格式无效。");
            try { if (Convert.FromBase64String(protectedKey).Length > 65536) throw new FormatException(); }
            catch { throw new InvalidOperationException("保存的密钥密文格式无效。"); }
        }
        return profile with { Name = name, BaseUrl = uri.AbsoluteUri.TrimEnd('/'), Model = model, ApiKeyEnv = environment, ProtectedApiKey = protectedKey };
    }

    private ModelProfilesSnapshot ReadSnapshot(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > MaximumFileBytes) throw new InvalidOperationException("模型方案文件过大。");
        var disk = JsonSerializer.Deserialize<DiskSnapshot>(input, JsonOptions);
        if (disk == null || disk.Version != 1 || disk.Profiles == null) throw new InvalidOperationException("模型方案版本或格式无效。");
        return Normalize(new(disk.ActiveId, disk.Profiles));
    }

    private ModelProfilesSnapshot Defaults()
    {
        var profiles = new List<ModelProfile> { LegacyProfile };
        ModelConfig? legacy = null; try { legacy = _legacyLoader(); } catch { }
        bool legacyNebius = Uri.TryCreate(legacy?.BaseUrl, UriKind.Absolute, out var legacyUri)
            && legacyUri.Host.Equals("api.tokenfactory.nebius.com", StringComparison.OrdinalIgnoreCase);
        void Add(string id, string name, string url, string? model, string keyEnvironment)
        {
            if (string.IsNullOrWhiteSpace(model)) return;
            try { profiles.Add(NormalizeProfile(new(id, name, url, model, keyEnvironment))); } catch (InvalidOperationException) { }
        }
        Add("deepseek", "DeepSeek（环境变量）", _environmentReader("DEEPSEEK_BASE_URL")
            ?? (legacy?.IsDeepSeek == true ? legacy.BaseUrl : "https://api.deepseek.com"),
            _environmentReader("DEEPSEEK_MODEL") ?? (legacy?.IsDeepSeek == true ? legacy.Model : null), "DEEPSEEK_API_KEY");
        Add("nebius", "NEBIUS（环境变量）", _environmentReader("NEBIUS_BASE_URL")
            ?? (legacyNebius ? legacy!.BaseUrl : "https://api.tokenfactory.nebius.com/v1"),
            _environmentReader("NEBIUS_MODEL") ?? (legacyNebius ? legacy!.Model : null), "NEBIUS_API_KEY");
        return new(LegacyId, profiles.ToArray());
    }

    private static string? ReadEnvironment(string name)
    {
        foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            try { var value = Environment.GetEnvironmentVariable(name, scope); if (!string.IsNullOrWhiteSpace(value)) return value.Trim(); }
            catch (System.Security.SecurityException) { }
        }
        return null;
    }

    private sealed record DiskSnapshot(int Version, string? ActiveId, ModelProfile[] Profiles);
}

internal sealed class WindowsDpapiSecretProtector : IModelSecretProtector
{
    private const string Purpose = "ScreenGuide.ModelProfile.v1";
    private const uint NoPrompt = 1;
    [StructLayout(LayoutKind.Sequential)] private struct Blob { internal int Size; internal nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, out nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);

    public string Protect(string plainText)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("密钥保护仅支持当前 Windows 用户。");
        if (string.IsNullOrEmpty(plainText) || plainText.Length > 8192)
            throw new InvalidOperationException("密钥内容为空或过长。");
        byte[] bytes = Encoding.UTF8.GetBytes(plainText);
        Blob input = default, output = default;
        try
        {
            input = CopyInput(bytes);
            if (!CryptProtectData(ref input, Purpose, 0, 0, 0, NoPrompt, out output))
                throw new InvalidOperationException("无法在当前 Windows 用户下保护密钥，请检查用户配置。");
            return Convert.ToBase64String(CopyOutput(output));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); FreeInput(input); FreeOutput(output); }
    }

    public string Unprotect(string protectedText)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("密钥保护仅支持当前 Windows 用户。");
        if (string.IsNullOrEmpty(protectedText) || protectedText.Length > 131072)
            throw new InvalidOperationException("保存的密钥密文格式无效，请重新输入密钥。");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(protectedText); }
        catch { throw new InvalidOperationException("保存的密钥密文格式无效，请重新输入密钥。"); }
        Blob input = default, output = default; nint description = 0;
        byte[]? clear = null;
        try
        {
            input = CopyInput(bytes);
            if (!CryptUnprotectData(ref input, out description, 0, 0, 0, NoPrompt, out output)
                || Marshal.PtrToStringUni(description) != Purpose)
                throw new InvalidOperationException("无法在当前 Windows 用户下解密密钥，请重新输入。");
            clear = CopyOutput(output);
            return new UTF8Encoding(false, true).GetString(clear);
        }
        catch (DecoderFallbackException) { throw new InvalidOperationException("保存的密钥内容无效，请重新输入。"); }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (clear != null) CryptographicOperations.ZeroMemory(clear);
            FreeInput(input); FreeOutput(output);
            if (description != 0) LocalFree(description);
        }
    }

    private static Blob CopyInput(byte[] bytes)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try { Marshal.Copy(bytes, 0, input.Data, bytes.Length); return input; }
        catch { FreeInput(input); throw; }
    }
    private static byte[] CopyOutput(Blob output)
    {
        if (output.Size < 0 || output.Size > 65536 || output.Data == 0) throw new InvalidOperationException("密钥保护返回格式无效。");
        var bytes = new byte[output.Size]; Marshal.Copy(output.Data, bytes, 0, bytes.Length); return bytes;
    }
    private static void FreeInput(Blob input)
    {
        if (input.Data == 0) return;
        for (int i = 0; i < input.Size; i++) Marshal.WriteByte(input.Data, i, 0);
        Marshal.FreeHGlobal(input.Data);
    }
    private static void FreeOutput(Blob output)
    {
        if (output.Data == 0) return;
        if (output.Size is >= 0 and <= 65536)
            for (int i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
        LocalFree(output.Data);
    }
}
