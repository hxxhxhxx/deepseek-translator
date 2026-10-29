using System.Runtime.InteropServices;
using DeepSeekTranslator.Interop;
using DeepSeekTranslator.Models;

namespace DeepSeekTranslator.Services;

public sealed record MonitorGeometry(PixelRect Bounds, PixelRect WorkArea, uint Dpi)
{
    public double Scale => Dpi / 96.0;
}

public static class ScreenGeometry
{
    public static MonitorGeometry GetMonitorForPoint(int x, int y)
    {
        var point = new NativeMethods.POINT(x, y);
        var monitor = NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };

        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y));
            var bounds = screen.Bounds;
            return new MonitorGeometry(
                new PixelRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
                new PixelRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
                96);
        }

        var dpi = 96u;
        try
        {
            if (NativeMethods.GetDpiForMonitor(
                    monitor,
                    NativeMethods.MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI,
                    out var dpiX,
                    out _) == 0 && dpiX > 0)
            {
                dpi = dpiX;
            }
        }
        catch
        {
            // 旧系统或 Shcore 不可用时退回 96 DPI。
        }

        return new MonitorGeometry(
            new PixelRect(
                info.rcMonitor.Left,
                info.rcMonitor.Top,
                info.rcMonitor.Right,
                info.rcMonitor.Bottom),
            new PixelRect(
                info.rcWork.Left,
                info.rcWork.Top,
                info.rcWork.Right,
                info.rcWork.Bottom),
            dpi);
    }

    public static PixelRect GetCursorPoint()
    {
        if (NativeMethods.GetCursorPos(out var point))
        {
            return new PixelRect(point.X, point.Y, point.X + 1, point.Y + 1);
        }

        return new PixelRect(0, 0, 1, 1);
    }

    /// <summary>
    /// 当前鼠标指针是否是文本输入光标（I 型）。
    /// 浏览器里拖选文字后指针仍是 I 型，拖动链接/图片/标签页则是手型或箭头，
    /// 用这个信号决定要不要立刻显示「译」图标。
    /// </summary>
    public static bool IsTextCursor()
    {
        var info = new NativeMethods.CURSORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.CURSORINFO>()
        };

        if (!NativeMethods.GetCursorInfo(ref info)) return false;
        if ((info.flags & NativeMethods.CURSOR_SHOWING) == 0) return false;

        var ibeam = NativeMethods.LoadCursor(IntPtr.Zero, new IntPtr(NativeMethods.IDC_IBEAM));
        return ibeam != IntPtr.Zero && info.hCursor == ibeam;
    }

    /// <summary>
    /// 判断前台窗口是否几乎完全覆盖一块显示器（用于独占全屏的复制降级）。
    /// </summary>
    public static bool IsForegroundFullscreen()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;

        NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
        if (processId == (uint)Environment.ProcessId) return false;

        if (!NativeMethods.GetWindowRect(foreground, out var rect)) return false;
        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top) return false;

        var monitor = GetMonitorForPoint(
            rect.Left + (rect.Right - rect.Left) / 2,
            rect.Top + (rect.Bottom - rect.Top) / 2);
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;

        return width >= monitor.Bounds.Width * 0.98 &&
               height >= monitor.Bounds.Height * 0.98;
    }
}
