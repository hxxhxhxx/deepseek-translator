using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using DeepSeekTranslator.Models;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace DeepSeekTranslator.Services;

/// <summary>
/// 使用 Windows 自带 OCR 引擎识别用户框选的屏幕区域。
/// 截图只在本地内存/临时文件中处理，只把识别后的文字发送给 DeepSeek。
/// </summary>
public sealed class OcrService
{
    public async Task<string> RecognizeAsync(
        Bitmap bitmap,
        string preferredLanguage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var maxDimension = OcrEngine.MaxImageDimension;
        using var working = ResizeIfNeeded(bitmap, maxDimension);
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"deepseek-ocr-{Guid.NewGuid():N}.png");

        try
        {
            working.Save(tempPath, ImageFormat.Png);

            var file = await StorageFile.GetFileFromPathAsync(tempPath);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            using var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

            var engine = CreateEngine(preferredLanguage);
            if (engine is null)
            {
                throw new CaptureException(
                    "OCR_UNAVAILABLE",
                    "当前 Windows 没有可用的 OCR 语言包。请在系统设置中安装中文或英文 OCR 语言。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var result = await engine.RecognizeAsync(softwareBitmap);
            return (result.Text ?? string.Empty).Trim();
        }
        catch (CaptureException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error("Windows OCR 识别失败", ex);
            throw new CaptureException("OCR_FAILED", "OCR 识别失败，请重试或换一个更清晰的区域。");
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch
            {
                // 临时文件清理失败不影响结果。
            }
        }
    }

    private static OcrEngine? CreateEngine(string preferredLanguage)
    {
        if (!string.IsNullOrWhiteSpace(preferredLanguage))
        {
            try
            {
                var engine = OcrEngine.TryCreateFromLanguage(
                    new Windows.Globalization.Language(preferredLanguage));
                if (engine is not null) return engine;
            }
            catch
            {
                // 语言标签无效时继续使用系统语言。
            }
        }

        var fromProfile = OcrEngine.TryCreateFromUserProfileLanguages();
        if (fromProfile is not null) return fromProfile;

        foreach (var tag in new[] { "zh-Hans", "en-US" })
        {
            try
            {
                var engine = OcrEngine.TryCreateFromLanguage(
                    new Windows.Globalization.Language(tag));
                if (engine is not null) return engine;
            }
            catch
            {
                // 继续尝试下一个语言。
            }
        }

        return null;
    }

    private static Bitmap ResizeIfNeeded(Bitmap source, uint maxDimension)
    {
        if (source.Width <= maxDimension && source.Height <= maxDimension)
        {
            return new Bitmap(source);
        }

        var scale = Math.Min(
            maxDimension / (double)source.Width,
            maxDimension / (double)source.Height);
        var width = Math.Max(1, (int)(source.Width * scale));
        var height = Math.Max(1, (int)(source.Height * scale));
        var resized = new Bitmap(width, height);

        using var graphics = Graphics.FromImage(resized);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, width, height);
        return resized;
    }
}
