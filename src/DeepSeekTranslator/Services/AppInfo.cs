using System.Reflection;

namespace DeepSeekTranslator.Services;

public static class AppInfo
{
    /// <summary>产品版本号，来自 csproj 的 Version（1.3.0）。</summary>
    public static string Version { get; } = Resolve();

    private static string Resolve()
    {
        try
        {
            var attribute = typeof(AppInfo).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            var value = attribute?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(value))
            {
                var plus = value.IndexOf('+');
                return plus > 0 ? value[..plus] : value;
            }
        }
        catch
        {
            // 读不到就退回默认值。
        }

        return "1.0.0";
    }
}
