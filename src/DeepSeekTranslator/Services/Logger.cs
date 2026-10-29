using System.IO;
using System.Text;

namespace DeepSeekTranslator.Services;

/// <summary>
/// 极简文件日志。只记录错误类型和上下文，不记录 API Key、请求头和选区正文。
/// </summary>
public static class Logger
{
    private static readonly object Sync = new();

    /// <summary>设置环境变量 DEEPSEEK_TRANSLATOR_DEBUG=1 时输出窗口定位等调试日志。</summary>
    public static bool IsVerbose { get; } =
        Environment.GetEnvironmentVariable("DEEPSEEK_TRANSLATOR_DEBUG") == "1";

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Error(string message, Exception? exception = null)
    {
        var detail = exception is null
            ? message
            : $"{message} | {exception.GetType().Name}: {exception.Message}";
        Write("ERROR", detail);
    }

    private static void Write(string level, string message)
    {
        try
        {
            AppPaths.EnsureDirectories();
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            lock (Sync)
            {
                File.AppendAllText(
                    Path.Combine(AppPaths.LogDirectory, "app.log"),
                    line,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // 日志失败不能影响主流程。
        }
    }
}
