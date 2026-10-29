using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeepSeekTranslator.Models;
using DeepSeekTranslator.Views;

namespace DeepSeekTranslator.Services;

/// <summary>
/// 开发诊断：把浮层和划词图标渲染成 PNG（--ui-preview）。
/// 只在诊断参数下运行，不影响正常启动。
/// </summary>
internal static class UiPreview
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DeepSeekTranslator-ui-preview");
        Directory.CreateDirectory(directory);

        var anchor = new PixelRect(80, 80, 81, 81);

        var overlay = new OverlayWindow();
        overlay.ShowLoading(
            "Fig. 4(e) compares this implementation with a transmission-line phase shifter while keeping the remaining PA circuit parameters unchanged.",
            anchor,
            TranslationDirection.EnglishToChinese);
        overlay.ShowResult(
            new TranslationResult(
                "图4(e)在保持其余 PA 电路参数和偏置条件不变的情况下，将该实现与传输线移相器进行了比较，展示了其对宽带回退效率的益处。",
                "DeepSeek V4.1 Flash",
                "DeepSeek-V4.1-Flash-1017",
                "deepseek-flash",
                "deepseek-flash",
                false,
                345,
                1000),
            TranslationDirection.EnglishToChinese);

        await Task.Delay(400);
        SavePng(overlay, Path.Combine(directory, "overlay.png"));
        overlay.ForceClose();

        var toolbar = new SelectionToolbarWindow();
        toolbar.ShowAt(120, 120);
        await Task.Delay(300);
        SavePng(toolbar, Path.Combine(directory, "selection-icon.png"));
        toolbar.CloseToolbar();

        Logger.Info($"UI 预览已生成：{directory}");
    }

    private static void SavePng(Window window, string path)
    {
        window.UpdateLayout();

        var dpi = VisualTreeHelper.GetDpi(window);
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY));

        var bitmap = new RenderTargetBitmap(
            width,
            height,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
