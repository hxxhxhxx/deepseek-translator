using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using DeepSeekTranslator.Interop;
using DeepSeekTranslator.Models;
using DeepSeekTranslator.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace DeepSeekTranslator.Views;

public partial class OverlayWindow : Window
{
    /// <summary>浮层刚出现时忽略输入的宽限时间，避免触发它的那次点击/按键立刻把它关掉。</summary>
    private const int DismissGraceMilliseconds = 450;

    private readonly DispatcherTimer _copyResetTimer;
    private PixelRect? _anchor;
    private TranslationDirection _direction = TranslationDirection.Unknown;
    private bool _allowClose;
    private bool _userPositioned;
    private InputWatcher? _inputWatcher;
    private long _shownAtTicks;

    public OverlayWindow()
    {
        InitializeComponent();

        _copyResetTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1600)
        };
        _copyResetTimer.Tick += (_, _) =>
        {
            _copyResetTimer.Stop();
            CopyButton.Content = "复制全文";
        };

        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) =>
        {
            if (IsVisible) RepositionAfterLayout();
        };
    }

    public event Action? RetryRequested;

    public event Action? SettingsRequested;

    public void ShowLoading(
        string original,
        PixelRect? anchor,
        TranslationDirection direction)
    {
        // 新一轮翻译重新跟随选区定位；用户手动拖动的状态在这里清除。
        _userPositioned = false;
        _direction = direction;
        ApplyDirection(direction);
        _anchor = anchor;
        OriginalText.Text = TruncateForDisplay(original, 320);
        OriginalCountText.Text = $"{original.Length:N0} 字符";
        SetOriginalVisible(true);

        LoadingPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ErrorSettingsButton.Visibility = Visibility.Collapsed;
        TranslationText.Visibility = Visibility.Collapsed;
        TranslationText.Text = string.Empty;
        WarningPanel.Visibility = Visibility.Collapsed;
        CopyButton.Visibility = Visibility.Collapsed;
        CopyButton.Content = "复制全文";
        RetryButton.Visibility = Visibility.Collapsed;
        MetaText.Text = "正在请求 DeepSeek…";
        ErrorText.Text = string.Empty;
        BodyScroller.ScrollToTop();

        ShowPanel();
    }

    public void ShowResult(TranslationResult result, TranslationDirection direction)
    {
        _direction = direction;
        ApplyDirection(direction);
        SetOriginalVisible(true);
        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ErrorSettingsButton.Visibility = Visibility.Collapsed;
        TranslationText.Text = result.Translation;
        TranslationText.Visibility = Visibility.Visible;
        WarningPanel.Visibility = result.Truncated ? Visibility.Visible : Visibility.Collapsed;
        CopyButton.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Visible;
        ErrorText.Text = string.Empty;

        var seconds = (result.LatencyMs / 1000d).ToString("0.0");
        var tokens = result.TotalTokens is int count ? $" · {count} tokens" : string.Empty;
        MetaText.Text = $"{result.ModelLabel} · {seconds}s{tokens}";
        MetaText.ToolTip = string.IsNullOrWhiteSpace(result.ModelVersion)
            ? result.RequestedModel
            : $"{result.ModelVersion}（请求模型：{result.RequestedModel}）";

        BodyScroller.ScrollToTop();
        RepositionAfterLayout();
    }

    public void ShowError(
        string message,
        string? code = null,
        bool showSettings = false,
        bool canRetry = true)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        TranslationText.Visibility = Visibility.Collapsed;
        TranslationText.Text = string.Empty;
        WarningPanel.Visibility = Visibility.Collapsed;
        CopyButton.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorText.Text = message;
        ErrorSettingsButton.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        MetaText.Text = string.IsNullOrWhiteSpace(code) ? string.Empty : $"错误代码：{code}";
        MetaText.ToolTip = null;
        ApplyDirection(_direction);
        SetOriginalVisible(!string.IsNullOrWhiteSpace(OriginalText.Text));

        if (!IsVisible)
        {
            _anchor ??= ScreenGeometry.GetCursorPoint();
            ShowPanel();
        }
        else
        {
            RepositionAfterLayout();
        }
    }

    public void HideOverlay()
    {
        _copyResetTimer.Stop();
        StopDismissWatcher();
        Hide();
    }

    public void ClearOriginal()
    {
        OriginalText.Text = string.Empty;
        OriginalCountText.Text = string.Empty;
        SetOriginalVisible(false);
    }

    public void ResetDirection()
    {
        _direction = TranslationDirection.Unknown;
        ApplyDirection(_direction);
    }

    private void ApplyDirection(TranslationDirection direction)
    {
        TranslationLabel.Text = direction switch
        {
            TranslationDirection.ChineseToEnglish => "中 → 英 · 英文译文",
            TranslationDirection.EnglishToChinese => "英 → 中 · 中文译文",
            TranslationDirection.OtherToEnglish => "→ 英 · 英文译文",
            _ => "译文"
        };
    }

    private void SetOriginalVisible(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        OriginalHeaderRow.Visibility = visibility;
        OriginalCard.Visibility = visibility;
        DividerLine.Visibility = visibility;
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    private void ShowPanel()
    {
        if (!IsVisible)
        {
            // 先按估算高度定位，避免在高 DPI 屏幕上先闪到左上角。
            if (!_userPositioned)
            {
                PositionNearAnchor(estimatedHeight: 360);
            }

            Show();
        }

        // 显示后（句柄已创建）再按物理像素精确落位，跨屏/混合 DPI 时才会落在正确的显示器上。
        if (!_userPositioned)
        {
            RepositionAfterLayout();
            StartDismissWatcher();
        }

        Topmost = true;
    }

    private void RepositionAfterLayout()
    {
        // 用户拖动过之后，不再自动吸附回选区旁，避免“拖不动/弹回去”。
        if (_userPositioned) return;

        UpdateLayout();
        PositionNearAnchor(estimatedHeight: Math.Max(220, ActualHeight));
    }

    private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        _userPositioned = true;
        StopDismissWatcher();

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已经松开时 DragMove 会抛异常，忽略即可。
        }

        e.Handled = true;
    }

    private void PositionNearAnchor(double estimatedHeight)
    {
        var anchor = _anchor ?? ScreenGeometry.GetCursorPoint();
        var monitor = ScreenGeometry.GetMonitorForPoint(anchor.CenterX, anchor.CenterY);
        var scale = Math.Max(0.5, monitor.Scale);
        var width = Width > 0 ? Width : 440;
        var height = estimatedHeight > 0 ? estimatedHeight : 360;
        var margin = 12 * scale;

        var x = anchor.Right - width * scale / 2;
        var y = anchor.Bottom + 10 * scale;

        if (y + height * scale > monitor.WorkArea.Bottom - margin)
        {
            y = anchor.Top - height * scale - 10 * scale;
        }

        x = Math.Clamp(
            x,
            monitor.WorkArea.Left + margin,
            Math.Max(monitor.WorkArea.Left + margin, monitor.WorkArea.Right - width * scale - margin));
        y = Math.Clamp(
            y,
            monitor.WorkArea.Top + margin,
            Math.Max(monitor.WorkArea.Top + margin, monitor.WorkArea.Bottom - height * scale - margin));

        var left = (int)Math.Round(x);
        var top = (int)Math.Round(y);

        // WPF 的 Left/Top 是 DIP，换算依赖窗口“当前”显示器 DPI；
        // 在 100% + 175% 这种混合 DPI 环境下直接用目标屏缩放会算错屏幕。
        // 这里先按当前 DPI 预算 DIP，再用物理像素精确落位。
        var dpiScale = WindowPos.CurrentDpiScale(this);
        Left = left / dpiScale;
        Top = top / dpiScale;

        if (IsVisible)
        {
            WindowPos.MoveToPixels(this, left, top);

            if (Logger.IsVerbose)
            {
                NativeMethods.GetWindowRect(
                    new WindowInteropHelper(this).Handle,
                    out var actual);
                Logger.Info(
                    $"overlay pos: anchor=({anchor.Left},{anchor.Top},{anchor.Right},{anchor.Bottom}) monitor={monitor.Bounds.Left},{monitor.Bounds.Top} scale={scale:0.##} dpi={dpiScale:0.##} target=({left},{top}) actual=({actual.Left},{actual.Top}) user={_userPositioned}");
            }
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
        // 取词已经在显示浮层前完成；这里不强制 NOACTIVATE，避免个别系统上按钮无法点击。
        style |= NativeMethods.WS_EX_TOOLWINDOW;
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

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var text = TranslationText.Text;
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            System.Windows.Clipboard.SetText(text);
            CopyButton.Content = "已复制";
            _copyResetTimer.Stop();
            _copyResetTimer.Start();
        }
        catch (Exception ex)
        {
            Logger.Error("复制译文失败", ex);
            CopyButton.Content = "复制失败";
        }
    }

    /// <summary>右键菜单：只复制用户选中的那一段译文。</summary>
    private void CopySelectionMenu_Click(object sender, RoutedEventArgs e)
    {
        var selected = TranslationText.SelectedText;
        if (string.IsNullOrEmpty(selected)) return;

        try
        {
            System.Windows.Clipboard.SetText(selected);
            CopyButton.Content = "已复制选中";
            _copyResetTimer.Stop();
            _copyResetTimer.Start();
        }
        catch (Exception ex)
        {
            Logger.Error("复制选中译文失败", ex);
            CopyButton.Content = "复制失败";
        }
    }

    private void SelectAllMenu_Click(object sender, RoutedEventArgs e)
    {
        TranslationText.Focus();
        TranslationText.SelectAll();
    }

    /// <summary>
    /// 只读 TextBox 自带滚动条容器，会吞掉滚轮；这里把滚轮转发给外层 ScrollViewer，
    /// 保证鼠标停在长译文上也能滚动浮层。
    /// </summary>
    private void TranslationText_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        BodyScroller.ScrollToVerticalOffset(BodyScroller.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        RetryRequested?.Invoke();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        HideOverlay();
        SettingsRequested?.Invoke();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        HideOverlay();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideOverlay();
            e.Handled = true;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideOverlay();
            return;
        }

        StopDismissWatcher();
        _inputWatcher?.Dispose();
        _inputWatcher = null;

        base.OnClosing(e);
    }

    /// <summary>
    /// 浮层显示期间监听全局输入：点别处、滚轮、敲键盘都关闭浮层。
    /// 但浮层自己被激活时（用户正在选中/复制译文），键盘输入不关闭它。
    /// 用户拖动过浮层后（_userPositioned）不安装监听，浮层一直保留到点 ×。
    /// </summary>
    private void StartDismissWatcher()
    {
        if (_userPositioned) return;

        if (_inputWatcher is null)
        {
            var watcher = new InputWatcher();
            watcher.PointerPressed += OnGlobalPointerPressed;
            watcher.WheelScrolled += OnGlobalWheelScrolled;
            watcher.KeyPressed += OnGlobalKeyPressed;
            _inputWatcher = watcher;
        }

        _shownAtTicks = Environment.TickCount64;
        _inputWatcher.Start();
    }

    private void StopDismissWatcher()
    {
        _inputWatcher?.Stop();
    }

    private bool IsWithinGracePeriod()
    {
        return Environment.TickCount64 - _shownAtTicks < DismissGraceMilliseconds;
    }

    private bool IsOutsideOverlay(int x, int y)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return true;
        if (!NativeMethods.GetWindowRect(handle, out var rect)) return true;

        return x < rect.Left || x >= rect.Right || y < rect.Top || y >= rect.Bottom;
    }

    private void OnGlobalPointerPressed(int x, int y)
    {
        if (!IsVisible || IsWithinGracePeriod()) return;

        // 点浮层内部（复制、重试、关闭、拖动标题）不关闭。
        if (IsOutsideOverlay(x, y))
        {
            HideOverlay();
        }
    }

    private void OnGlobalWheelScrolled(int x, int y)
    {
        if (!IsVisible || IsWithinGracePeriod()) return;

        // 在浮层里滚动长译文时不关闭，滚别处才关闭。
        if (IsOutsideOverlay(x, y))
        {
            HideOverlay();
        }
    }

    private void OnGlobalKeyPressed(int virtualKey)
    {
        if (!IsVisible || IsWithinGracePeriod()) return;

        // 浮层处于激活状态（用户点了浮层、正在选中或复制译文）时，键盘输入不关闭浮层，
        // 否则 Ctrl+C 会把浮层自己关掉。
        if (IsActive) return;

        // 单独按 Ctrl/Alt/Shift/Win 不算“输入”，避免组合键的后半段误关。
        if (IsModifierKey(virtualKey)) return;

        HideOverlay();
    }

    private static bool IsModifierKey(int virtualKey)
    {
        return virtualKey is 0x10 or 0x11 or 0x12      // Shift / Ctrl / Alt
            or 0x5B or 0x5C                            // 左/右 Win
            or >= 0xA0 and <= 0xA5;                    // 左右 Shift/Ctrl/Alt
    }

    private static string TruncateForDisplay(string text, int limit)
    {
        return text.Length <= limit
            ? text
            : $"{text[..limit]}\n…（已截断，共 {text.Length:N0} 个字符）";
    }
}
