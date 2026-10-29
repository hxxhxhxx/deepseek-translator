using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using DeepSeekTranslator.Interop;
using DeepSeekTranslator.Models;
using DeepSeekTranslator.Services;

namespace DeepSeekTranslator.Views;

/// <summary>
/// 划词小图标：选中文字后出现在鼠标旁，点一下直接翻译。
/// 使用 WS_EX_NOACTIVATE 显示，点击时不会抢走目标窗口焦点，
/// 因此 UI Automation 仍能读到原来的选区。
/// </summary>
public partial class SelectionToolbarWindow : Window
{
    private readonly DispatcherTimer _autoHideTimer;
    private bool _allowClose;

    public SelectionToolbarWindow()
    {
        InitializeComponent();

        _autoHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(6)
        };
        _autoHideTimer.Tick += (_, _) => HideToolbar();

        SourceInitialized += OnSourceInitialized;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
    }

    public event Action? Clicked;

    public bool IsShown => IsVisible;

    /// <summary>在屏幕坐标附近显示图标，返回它占用的屏幕区域（像素）。</summary>
    public PixelRect ShowAt(int pixelX, int pixelY)
    {
        var monitor = ScreenGeometry.GetMonitorForPoint(pixelX, pixelY);
        var scale = Math.Max(0.5, monitor.Scale);
        var size = Width * scale;
        var gap = 14 * scale;

        var x = pixelX + gap;
        var y = pixelY + gap;

        if (x + size > monitor.WorkArea.Right - 4 * scale)
        {
            x = pixelX - size - gap / 2;
        }

        if (y + size > monitor.WorkArea.Bottom - 4 * scale)
        {
            y = pixelY - size - gap / 2;
        }

        x = Math.Clamp(x, monitor.WorkArea.Left, Math.Max(monitor.WorkArea.Left, monitor.WorkArea.Right - size));
        y = Math.Clamp(y, monitor.WorkArea.Top, Math.Max(monitor.WorkArea.Top, monitor.WorkArea.Bottom - size));

        var left = (int)Math.Round(x);
        var top = (int)Math.Round(y);

        // 用窗口“当前”DPI 估算 DIP 位置（首次显示不闪），显示后再用物理像素精确落位。
        WindowPos.Place(this, left, top);

        if (!IsVisible)
        {
            Show();
        }

        Topmost = true;

        // 关键：跨屏/混合 DPI 时 Left/Top 的 DIP 换算不可靠，统一用物理像素再压一次。
        WindowPos.MoveToPixels(this, left, top);

        if (Logger.IsVerbose)
        {
            var handle = new WindowInteropHelper(this).Handle;
            NativeMethods.GetWindowRect(handle, out var debugRect);
            Logger.Info(
                $"icon pos: target=({left},{top}) actual=({debugRect.Left},{debugRect.Top}) dpi={WindowPos.CurrentDpiScale(this):0.##} w={debugRect.Right - debugRect.Left} h={debugRect.Bottom - debugRect.Top}");
        }

        _autoHideTimer.Stop();
        _autoHideTimer.Start();

        return new PixelRect(
            left,
            top,
            (int)Math.Round(left + size),
            (int)Math.Round(top + size));
    }

    public void HideToolbar()
    {
        _autoHideTimer.Stop();
        if (IsVisible)
        {
            Hide();
        }
    }

    public void CloseToolbar()
    {
        _allowClose = true;
        _autoHideTimer.Stop();
        Close();
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        HideToolbar();
        Clicked?.Invoke();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
        style |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE, new IntPtr(style));

        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideToolbar();
            return;
        }

        base.OnClosing(e);
    }
}
