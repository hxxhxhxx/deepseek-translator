using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;

namespace DeepSeekTranslator.Services;

/// <summary>
/// 诊断用 OCR 自检：生成一张本地图片，调用 Windows OCR，不访问网络。
/// 通过 --ocr-selftest 启动。
/// </summary>
public static class OcrSelfTest
{
    public static async Task RunAsync()
    {
        AppPaths.EnsureDirectories();
        var output = Path.Combine(AppPaths.DataDirectory, "ocr-selftest.txt");

        try
        {
            using var bitmap = new Bitmap(1000, 260);
            using (var graphics = Graphics.FromImage(bitmap))
            using (var background = new SolidBrush(Color.White))
            using (var foreground = new SolidBrush(Color.Black))
            using (var font = new Font("Microsoft YaHei UI", 34, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.White);
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.DrawString("截图 OCR 测试：DeepSeek 桌面翻译", font, foreground, 26, 48);
                graphics.DrawString("The quick brown fox jumps over the lazy dog.", font, foreground, 26, 130);
            }

            var text = await new OcrService().RecognizeAsync(bitmap, "zh-Hans", CancellationToken.None);
            await File.WriteAllTextAsync(output, text);
            Logger.Info($"OCR 自检完成，识别字符数：{text.Length}");
        }
        catch (Exception ex)
        {
            Logger.Error("OCR 自检失败", ex);
            await File.WriteAllTextAsync(output, $"ERROR: {ex}");
        }
    }
}
