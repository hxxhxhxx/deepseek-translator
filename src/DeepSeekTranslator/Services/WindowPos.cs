using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DeepSeekTranslator.Interop;

namespace DeepSeekTranslator.Services;

/// <summary>
/// 多显示器 / 混合 DPI 下的窗口定位工具。
/// WPF 的 Window.Left/Top 是 DIP，换算依赖窗口“当前”所在显示器的 DPI；
/// 直接把物理像素除目标屏缩放再赋给 Left/Top，在 100%+175% 这种组合下会落到错误的屏幕。
/// 因此这里统一用 SetWindowPos 以物理像素落位。
/// </summary>
public static class WindowPos
{
    /// <summary>窗口当前所在显示器的 DPI 缩放（未创建窗口时取系统 DPI）。</summary>
    public static double CurrentDpiScale(Window window)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            return dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    /// <summary>把窗口左上角移到屏幕物理像素 (x,y)，保持置顶且不激活。</summary>
    public static bool MoveToPixels(Window window, int x, int y)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return false;

        return NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HWND_TOPMOST,
            x,
            y,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE) != IntPtr.Zero;
    }

    /// <summary>
    /// 先按窗口当前 DPI 预算一个 DIP 位置（避免首次显示闪到左上角），
    /// 窗口显示后再用物理像素精确落位。
    /// </summary>
    public static void Place(Window window, int pixelX, int pixelY)
    {
        var scale = CurrentDpiScale(window);
        window.Left = pixelX / scale;
        window.Top = pixelY / scale;

        if (window.IsVisible)
        {
            MoveToPixels(window, pixelX, pixelY);
        }
    }
}
