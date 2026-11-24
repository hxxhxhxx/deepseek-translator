using System.Windows;
using System.Windows.Interop;
using DeepSeekTranslator.Interop;
using DeepSeekTranslator.Models;
using DeepSeekTranslator.Services;

namespace DeepSeekTranslator.Views;

/// <summary>跨页/多段选词的临时拼接篮；只保存文本，不在这里访问网络。</summary>
public partial class StitchBasketWindow : Window
{
    private bool _positioned;

    public StitchBasketWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    public event Action? TranslateRequested;
    public event Action? UndoRequested;
    public event Action? ClearRequested;
    public event Action? CloseRequested;

    public void ShowBasket(int segmentCount, int characterCount, string lastPreview)
    {
        CountText.Text = $"{segmentCount} 段 · {characterCount:N0} 字";
        PreviewText.Text = lastPreview;
        PreviewText.ToolTip = lastPreview;
        StatusText.Visibility = Visibility.Collapsed;
        StatusText.Text = string.Empty;
        SetBusy(false);

        if (!IsVisible)
        {
            Show();
            _positioned = false;
        }

        UpdateLayout();
        if (!_positioned)
        {
            PositionAtBottomRight();
            _positioned = true;
        }
    }

    public void SetBusy(bool busy)
    {
        UndoButton.IsEnabled = !busy;
        ClearButton.IsEnabled = !busy;
        TranslateButton.IsEnabled = !busy;
        TranslateButton.Content = busy ? "翻译中…" : "翻译拼接";
    }

    public void ShowMessage(string message)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
    }

    public void HideBasket()
    {
        Hide();
        _positioned = false;
    }

    private void PositionAtBottomRight()
    {
        var cursor = ScreenGeometry.GetCursorPoint();
        var monitor = ScreenGeometry.GetMonitorForPoint(cursor.CenterX, cursor.CenterY);
        var scale = Math.Max(0.5, monitor.Scale);
        var width = (int)Math.Ceiling(Width * scale);
        var height = (int)Math.Ceiling(ActualHeight * scale);
        var margin = (int)Math.Ceiling(20 * scale);

        var x = Math.Max(monitor.WorkArea.Left,
            monitor.WorkArea.Right - width - margin);
        var y = Math.Max(monitor.WorkArea.Top,
            monitor.WorkArea.Bottom - height - margin);

        WindowPos.Place(this, x, y);
        if (IsVisible)
        {
            WindowPos.MoveToPixels(this, x, y);
        }
    }

    private void TranslateButton_Click(object sender, RoutedEventArgs e)
    {
        TranslateRequested?.Invoke();
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        UndoRequested?.Invoke();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        ClearRequested?.Invoke();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke();
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
}
