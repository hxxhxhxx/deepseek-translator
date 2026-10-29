using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DeepSeekTranslator.Models;

namespace DeepSeekTranslator.Services;

/// <summary>
/// 自动更新：读取 latest.json → 比较版本 → 下载安装包 → 校验 SHA256 + 签名 → 交给安装器。
/// 支持 https、http、file:// 以及本地/共享目录路径（方便先用共享文件夹测试）。
/// </summary>
public sealed class UpdateService
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<UpdateCheckResult> CheckAsync(string feed, CancellationToken cancellationToken)
    {
        var current = AppInfo.Version;

        if (string.IsNullOrWhiteSpace(feed))
        {
            return new UpdateCheckResult(null, "未配置更新地址", current, false);
        }

        try
        {
            var json = await ReadFeedAsync(feed, cancellationToken).ConfigureAwait(false);
            var info = JsonSerializer.Deserialize<UpdateInfo>(json, JsonOptions);

            if (info is null || string.IsNullOrWhiteSpace(info.Version))
            {
                return new UpdateCheckResult(null, "更新清单格式不正确", current, false);
            }

            if (!Version.TryParse(info.Version, out var remoteVersion))
            {
                return new UpdateCheckResult(null, $"更新清单里的版本号无效：{info.Version}", current, false);
            }

            if (!Version.TryParse(current, out var localVersion))
            {
                return new UpdateCheckResult(null, $"当前版本号无效：{current}", current, false);
            }

            if (remoteVersion <= localVersion)
            {
                return new UpdateCheckResult(null, null, current, true);
            }

            return new UpdateCheckResult(info, null, current, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error("检查更新失败", ex);
            return new UpdateCheckResult(null, ex.Message, current, false);
        }
    }

    /// <summary>下载安装包到本机缓存目录，返回文件路径。</summary>
    public async Task<string> DownloadAsync(
        UpdateInfo info,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(info.Url))
        {
            throw new InvalidOperationException("更新清单缺少下载地址。");
        }

        var fileName = GetFileName(info.Url);
        var directory = AppPaths.UpdateDirectory;
        Directory.CreateDirectory(directory);

        var target = Path.Combine(directory, fileName);
        var temp = target + ".part";

        if (File.Exists(temp)) File.Delete(temp);
        if (File.Exists(target)) File.Delete(target);

        if (IsRemote(info.Url))
        {
            using var response = await Client
                .GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? info.Size;
            await using var source = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var destination = File.Create(temp);

            var buffer = new byte[81920];
            long copied = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                copied += read;

                if (total > 0)
                {
                    progress?.Report(Math.Min(1d, (double)copied / total));
                }
            }
        }
        else
        {
            var sourcePath = ToLocalPath(info.Url);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"找不到更新文件：{sourcePath}");
            }

            await using var source = File.OpenRead(sourcePath);
            await using var destination = File.Create(temp);

            var total = source.Length;
            var buffer = new byte[81920];
            long copied = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                copied += read;

                if (total > 0)
                {
                    progress?.Report(Math.Min(1d, (double)copied / total));
                }
            }
        }

        File.Move(temp, target, overwrite: true);
        progress?.Report(1d);

        Verify(target, info);
        return target;
    }

    /// <summary>校验下载结果：大小、SHA256、以及“签名者必须与当前程序一致”。</summary>
    public void Verify(string file, UpdateInfo info)
    {
        var size = new FileInfo(file).Length;
        if (info.Size > 0 && size != info.Size)
        {
            throw new InvalidOperationException($"安装包大小不符（期望 {info.Size}，实际 {size}）。");
        }

        if (!string.IsNullOrWhiteSpace(info.Sha256))
        {
            using var stream = File.OpenRead(file);
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actual, info.Sha256.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("安装包校验失败（SHA256 不一致），已停止更新。");
            }
        }

        VerifySigner(file);
    }

    /// <summary>
    /// 把更新包的签名者与当前 exe 的签名者比对，防止有人把链接换成自己的安装包。
    /// 当前程序未签名（开发版）时跳过这一步。
    /// </summary>
    private static void VerifySigner(string file)
    {
        var running = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(running)) return;

        X509Certificate2? self = TryGetSigner(running);
        if (self is null) return;

        var signer = TryGetSigner(file);
        if (signer is null || !string.Equals(signer.Thumbprint, self.Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("更新包签名与当前程序不一致，已拒绝安装。");
        }
    }

    private static X509Certificate2? TryGetSigner(string path)
    {
        try
        {
            var certificate = X509Certificate.CreateFromSignedFile(path);
            return new X509Certificate2(certificate);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>静默运行安装包；安装完成后由安装器重新拉起程序（/RESTARTAPP=1）。</summary>
    public static void RunInstaller(string installerPath)
    {
        var startInfo = new ProcessStartInfo(installerPath)
        {
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTAPP=1",
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? string.Empty,
            UseShellExecute = true
        };

        Process.Start(startInfo);
    }

    private static async Task<string> ReadFeedAsync(string feed, CancellationToken cancellationToken)
    {
        if (IsRemote(feed))
        {
            var separator = feed.Contains('?') ? '&' : '?';
            var url = $"{feed}{separator}t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

            using var response = await Client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        var path = ToLocalPath(feed);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到更新清单：{path}");
        }

        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsRemote(string location)
    {
        return location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToLocalPath(string location)
    {
        if (location.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(location).LocalPath;
        }

        return location;
    }

    private static string GetFileName(string url)
    {
        var name = IsRemote(url)
            ? Path.GetFileName(new Uri(url).AbsolutePath)
            : Path.GetFileName(ToLocalPath(url));

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "DeepSeekTranslator-update.exe";
        }

        // 只允许文件名，避免路径穿越
        return Path.GetFileName(name);
    }
}
