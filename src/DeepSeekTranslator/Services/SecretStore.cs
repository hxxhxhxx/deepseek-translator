using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DeepSeekTranslator.Services;

/// <summary>
/// 使用 Windows DPAPI（CurrentUser）保存 API Key。
/// 密文只能由当前 Windows 用户解密，不依赖配置文件、日志或前端状态。
/// </summary>
public sealed class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DeepSeekTranslator.ApiKey.v1");

    public bool HasSecret()
    {
        return !string.IsNullOrWhiteSpace(Load());
    }

    public string? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SecretFile)) return null;

            var protectedBytes = File.ReadAllBytes(AppPaths.SecretFile);
            var plainBytes = ProtectedData.Unprotect(
                protectedBytes,
                Entropy,
                DataProtectionScope.CurrentUser);
            var key = Encoding.UTF8.GetString(plainBytes).Trim();
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch (Exception ex)
        {
            Logger.Error("读取 API Key 失败", ex);
            return null;
        }
    }

    public void Save(string apiKey)
    {
        var key = apiKey.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("API Key 不能为空。", nameof(apiKey));
        }

        AppPaths.EnsureDirectories();
        var plainBytes = Encoding.UTF8.GetBytes(key);
        var protectedBytes = ProtectedData.Protect(
            plainBytes,
            Entropy,
            DataProtectionScope.CurrentUser);
        File.WriteAllBytes(AppPaths.SecretFile, protectedBytes);
        Array.Clear(plainBytes);
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(AppPaths.SecretFile))
            {
                File.Delete(AppPaths.SecretFile);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("清除 API Key 失败", ex);
            throw;
        }
    }

    public string? MaskedKey()
    {
        var key = Load();
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (key.Length <= 10) return "••••••";
        return $"{key[..4]}…{key[^4..]}";
    }
}
