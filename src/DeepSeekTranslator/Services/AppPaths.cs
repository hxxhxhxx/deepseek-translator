using System.IO;

namespace DeepSeekTranslator.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeepSeekTranslator");

    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "settings.json");

    public static string SecretFile { get; } = Path.Combine(DataDirectory, "secret.dat");

    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");

    /// <summary>自动更新下载缓存。</summary>
    public static string UpdateDirectory { get; } = Path.Combine(DataDirectory, "updates");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
