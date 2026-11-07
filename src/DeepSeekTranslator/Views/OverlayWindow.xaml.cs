using System.Windows;
using System.Windows.Controls.Primitives;
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

    /// <summary>浮层可调宽度的范围（DIP）。</summary>
    public const double MinWidthDip = 320;

    public const double MaxWidthDip = 960;

    /// <summary>正文字号范围与默认值。</summary>
    public const double MinFontSize = 12;

    public const double MaxFontSize = 24;

    public const double DefaultFontSize = 15;

    public const double LargeFontSize = 20;

    /// <summary>内容区最大高度的范围与默认值（DIP）。</summary>
    public const double MinContentHeight = 240;

    public const double MaxContentHeightLimit = 900;

    public const double DefaultContentHeight = 500;

    private readonly DispatcherTimer _copyResetTimer;
    private readonly System.Windows.Media.Effects.Effect? _cardShadow;
    private PixelRect? _anchor;
    private TranslationDirection _direction = TranslationDirection.Unknown;
    private bool _allowClose;
    private bool _userPositioned;
    private bool _resizing;
    private bool _dragging;
    private bool _streaming;
    private int _streamEpoch;
    private double _fontSize = DefaultFontSize;
    private double _speechRate = 1.0;
    private SpeechService? _speech;
    private InputWatcher? _inputWatcher;
    private long _shownAtTicks;

    public OverlayWindow()
    {
        InitializeComponent();

        _cardShadow = CardBorder.Effect;
        MaxHeight = Math.Max(360, Math.Min(1000, SystemParameters.WorkArea.Height - 24));
        ApplyFontSize(_fontSize);
        ApplyMaxContentHeight(DefaultContentHeight);

        try
        {
            _speech = new SpeechService();
            _speech.SpeakingChanged += OnSpeechSpeakingChanged;
        }
        catch (Exception ex)
        {
            Logger.Error("初始化本地语音失败", ex);
            SpeechButton.IsEnabled = false;
            SpeechButton.ToolTip = "系统没有可用的语音包";
        }

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
            // 流式输出时文字持续变长，不移动窗口，避免抖动；结果到达后再统一落位。
            if (!IsVisible || _resizing || _streaming) return;
            RepositionAfterLayout();
        };
    }

    public event Action? RetryRequested;

    public event Action? SettingsRequested;

    /// <summary>用户请求详解原文中选中的单词；参数为单词和原文上下文。</summary>
    public event Action<string, string?>? ExplainRequested;

    /// <summary>用户拖动边缘调整宽度后触发，参数为新的宽度（DIP）。</summary>
    public event Action<double>? WidthChanged;

    /// <summary>用户调整字号后触发，参数为新的字号。</summary>
    public event Action<double>? FontSizeChanged;

    /// <summary>用户上下拖动内容区后触发，参数为新的最大高度（DIP）。</summary>
    public event Action<double>? MaxContentHeightChanged;

    /// <summary>恢复上次使用的宽度；非法值回落到默认 440。</summary>
    public double PreferredWidth
    {
        get => Width;
        set => Width = Math.Clamp(value, MinWidthDip, MaxWidthDip);
    }

    /// <summary>恢复上次使用的字号；非法值回落到 15。</summary>
    public double PreferredFontSize
    {
        get => _fontSize;
        set => ApplyFontSize(double.IsFinite(value) ? value : DefaultFontSize);
    }

    /// <summary>恢复上次使用的内容区最大高度；非法值回落到 500。</summary>
    public double PreferredMaxContentHeight
    {
        get => BodyScroller.MaxHeight;
        set => ApplyMaxContentHeight(double.IsFinite(value) ? value : DefaultContentHeight);
    }

    public double CurrentFontSize => _fontSize;

    public double CurrentMaxContentHeight => BodyScroller.MaxHeight;

    /// <summary>朗读语速（0.5~2.0，默认 1.0）。</summary>
    public double PreferredSpeechRate
    {
        get => _speechRate;
        set => _speechRate = Math.Clamp(double.IsFinite(value) ? value : 1.0, 0.5, 2.0);
    }

    public void ShowLoading(
        string original,
        PixelRect? anchor,
        TranslationDirection direction)
    {
        // 新一轮翻译重新跟随选区定位；用户手动拖动的状态在这里清除。
        _userPositioned = false;
        _streaming = false;
        _streamEpoch++;
        StopSpeech();
        SpeechButton.IsEnabled = false;WordPanel.Visibility = Visibility.Collapsed;
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
        _streaming = false;
        _streamEpoch++;
        _direction = direction;
        ApplyDirection(direction);
        SetOriginalVisible(true);
        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ErrorSettingsButton.Visibility = Visibility.Collapsed;
        TranslationText.Text = result.Translation;
        TranslationText.Visibility = Visibility.Visible;
        SpeechButton.IsEnabled = true;
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

    /// <summary>当前流式请求代次；结果落地后旧代次的增量会被丢弃。</summary>
    public int CurrentStreamEpoch => _streamEpoch;

    /// <summary>流式输出中：显示已经生成的部分，不显示复制/重试按钮。</summary>
    public void ShowStreaming(string partial, TranslationDirection direction, int epoch)
    {
        // 拖动时 UI 线程被占住，增量会积压；翻译完成的回调可能先执行，
        // 随后积压的旧增量就会把结果覆盖回“正在输出…”。这里按代次直接丢弃过期增量。
        if (epoch != _streamEpoch) return;

        _streaming = true;
        _direction = direction;
        StopSpeech();
        SpeechButton.IsEnabled = false;
        ApplyDirection(direction);
        SetOriginalVisible(true);
        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ErrorSettingsButton.Visibility = Visibility.Collapsed;
        WarningPanel.Visibility = Visibility.Collapsed;
        TranslationText.Text = partial;
        TranslationText.Visibility = Visibility.Visible;
        CopyButton.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        ErrorText.Text = string.Empty;
        MetaText.Text = $"正在输出… {partial.Length:N0} 字符";
        MetaText.ToolTip = null;

        if (!IsVisible)
        {
            _anchor ??= ScreenGeometry.GetCursorPoint();
            ShowPanel();
        }
    }

    public void ShowError(
        string message,
        string? code = null,
        bool showSettings = false,
        bool canRetry = true)
    {
        _streaming = false;
        _streamEpoch++;
        StopSpeech();
        SpeechButton.IsEnabled = false;
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
        StopSpeech();
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
        _speech?.Dispose();
        _speech = null;
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

        var startLeft = Left;
        var startTop = Top;
        var wasPositioned = _userPositioned;

        // 拖动会进入嵌套消息循环：翻译恰好完成时不能让浮层重定位，否则会从鼠标下跳走。
        _userPositioned = true;
        _dragging = true;

        // 关键：先停掉全局输入监听并让它标记为“已停止”。
        // 按下鼠标时钩子已经排了一个事件到 UI 队列；等它执行时窗口已经跟着鼠标移开，
        // 旧坐标会被判成“点在浮层外”，于是拖到一半浮层就消失了。
        StopDismissWatcher();

        // 拖动时临时去掉投影：分层窗口每移动一像素都会重绘阴影，这是拖动卡顿的主因。
        CardBorder.Effect = null;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已经松开时 DragMove 会抛异常，忽略即可。
        }
        finally
        {
            _dragging = false;
            CardBorder.Effect = _cardShadow;
        }

        var moved = Math.Abs(Left - startLeft) > 0.5 || Math.Abs(Top - startTop) > 0.5;
        if (moved)
        {
            // 真正拖动过：不再自动吸附回选区旁，也不再自动消失（只有点 × 关闭）。
            e.Handled = true;
            return;
        }

        // 只是点了一下标题栏，没移动：恢复原来的自动消失规则。
        _userPositioned = wasPositioned;
        if (!wasPositioned)
        {
            StartDismissWatcher();
        }

        e.Handled = true;
    }

    private void ResizeThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        _resizing = true;
        _dragging = true;

        // 与拖动标题栏同理：让按下时已排队的全局事件失效，避免调宽时浮层被关掉。
        StopDismissWatcher();

        // 调宽时同样去掉投影，减少重绘卡顿。
        CardBorder.Effect = null;
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var draggingLeftEdge = ReferenceEquals(sender, LeftResizeThumb);
        var delta = draggingLeftEdge ? -e.HorizontalChange : e.HorizontalChange;
        var newWidth = Math.Clamp(Width + delta, MinWidthDip, MaxWidthDip);
        var applied = newWidth - Width;
        if (Math.Abs(applied) < 0.5) return;

        // 拖左边时保持右边缘不动。
        if (draggingLeftEdge) Left -= applied;
        Width = newWidth;
    }

    private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _resizing = false;
        _dragging = false;
        CardBorder.Effect = _cardShadow;

        // 调宽不算“拖动过浮层”，自动消失规则保持不变。
        if (!_userPositioned)
        {
            StartDismissWatcher();
        }

        WidthChanged?.Invoke(Width);
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

    private void ExplainWordButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = (OriginalText.SelectedText ?? string.Empty).Trim();
        if (selected.Length == 0)
        {
            ShowWordDetailMessage("请先在原文中选中一个单词（双击选词或拖选），再点「详解」。");
            return;
        }

        if (selected.Length > 60) selected = selected[..60];

        WordTitle.Text = $"单词详解 · {selected}";
        WordDetailText.Text = "正在查询…";
        WordPanel.Visibility = Visibility.Visible;
        ExplainRequested?.Invoke(selected, OriginalText.Text);
    }

    public void ShowWordDetail(string word, string detail)
    {
        WordTitle.Text = $"单词详解 · {word}";
        WordDetailText.Text = detail;
        WordPanel.Visibility = Visibility.Visible;
    }

    public void ShowWordDetailError(string word, string message)
    {
        WordTitle.Text = $"单词详解 · {word}";
        WordDetailText.Text = message;
        WordPanel.Visibility = Visibility.Visible;
    }

    private void ShowWordDetailMessage(string message)
    {
        WordTitle.Text = "单词详解";
        WordDetailText.Text = message;
        WordPanel.Visibility = Visibility.Visible;
    }

    private void WordCloseButton_Click(object sender, RoutedEventArgs e)
    {
        WordPanel.Visibility = Visibility.Collapsed;
    }

    private async void CopyOriginalSelection_Click(object sender, RoutedEventArgs e)
    {
        var selected = OriginalText.SelectedText;
        if (string.IsNullOrEmpty(selected)) return;

        await ClipboardWriter.TrySetTextAsync(selected);
    }

    private void SelectAllOriginal_Click(object sender, RoutedEventArgs e)
    {
        OriginalText.Focus();
        OriginalText.SelectAll();
    }

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var text = TranslationText.Text;
        if (string.IsNullOrWhiteSpace(text)) return;

        // 写剪贴板放到后台 STA 线程：剪贴板被占用时 WPF 会在 UI 线程反复重试，
        // 把消息循环（以及鼠标钩子）一起卡住，表现为“复制失败 + 鼠标乱跳”。
        CopyButton.IsEnabled = false;
        try
        {
            var ok = await ClipboardWriter.TrySetTextAsync(text);
            CopyButton.Content = ok ? "已复制" : "复制失败";
            _copyResetTimer.Stop();
            _copyResetTimer.Start();
        }
        finally
        {
            CopyButton.IsEnabled = true;
        }
    }

    /// <summary>右键菜单：只复制用户选中的那一段译文。</summary>
    private async void CopySelectionMenu_Click(object sender, RoutedEventArgs e)
    {
        var selected = TranslationText.SelectedText;
        if (string.IsNullOrEmpty(selected)) return;

        var ok = await ClipboardWriter.TrySetTextAsync(selected);
        CopyButton.Content = ok ? "已复制选中" : "复制失败";
        if (ok)
        {
            _copyResetTimer.Stop();
            _copyResetTimer.Start();
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

    private async void SpeechButton_Click(object sender, RoutedEventArgs e)
    {
        if (_speech is null)
        {
            return;
        }

        if (_speech.IsSpeaking)
        {
            _speech.Stop();
            return;
        }

        if (_streaming)
        {
            return;
        }

        var text = TranslationText.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        SpeechButton.IsEnabled = false;
        try
        {
            await _speech.SpeakAsync(text, TargetSpeechLanguage(), _speechRate);
        }
        catch (Exception ex)
        {
            Logger.Error("朗读失败", ex);
            SpeechButton.ToolTip = "朗读失败：" + ex.Message;
            System.Windows.MessageBox.Show(
                this,
                "朗读失败：" + ex.Message,
                "朗读",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            if (!_streaming)
            {
                SpeechButton.IsEnabled = true;
            }
        }
    }

    private void OnSpeechSpeakingChanged(bool speaking)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnSpeechSpeakingChanged(speaking)));
            return;
        }

        SpeechButton.Content = speaking ? "停止" : "朗读";
        SpeechButton.ToolTip = speaking ? "停止朗读" : "朗读译文";
    }

    private string TargetSpeechLanguage()
    {
        var direction = _direction;
        if (direction == TranslationDirection.Unknown)
        {
            direction = LanguageDetector.Detect(TranslationText.Text).Direction;
        }

        return direction is TranslationDirection.ChineseToEnglish
            or TranslationDirection.OtherToEnglish
            ? "en-US"
            : "zh-CN";
    }

    private void StopSpeech()
    {
        try
        {
            _speech?.Stop();
        }
        catch (Exception ex)
        {
            Logger.Error("停止朗读失败", ex);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        HideOverlay();
        SettingsRequested?.Invoke();
    }

    private void FontDecreaseButton_Click(object sender, RoutedEventArgs e) => ChangeFontSize(-1);

    private void FontIncreaseButton_Click(object sender, RoutedEventArgs e) => ChangeFontSize(1);

    private void LargeFontButton_Click(object sender, RoutedEventArgs e)
    {
        SetFontSize(_fontSize >= LargeFontSize ? DefaultFontSize : LargeFontSize);
    }

    private void ChangeFontSize(double delta) => SetFontSize(_fontSize + delta);

    private void SetFontSize(double value)
    {
        var target = Math.Clamp(value, MinFontSize, MaxFontSize);
        if (Math.Abs(target - _fontSize) < 0.1) return;

        ApplyFontSize(target);
        FontSizeChanged?.Invoke(_fontSize);

        if (!_userPositioned)
        {
            RepositionAfterLayout();
        }
    }

    private void ApplyFontSize(double value)
    {
        _fontSize = Math.Clamp(value, MinFontSize, MaxFontSize);
        TranslationText.FontSize = _fontSize;
        OriginalText.FontSize = Math.Max(11, _fontSize - 2);
        WordDetailText.FontSize = Math.Max(11, _fontSize - 3);
        FontSizeText.Text = _fontSize.ToString("0");
        UpdateLargeFontButton();
    }

    private void UpdateLargeFontButton()
    {
        LargeFontButton.Content = _fontSize >= LargeFontSize ? "默认" : "大字";
        LargeFontButton.ToolTip = _fontSize >= LargeFontSize
            ? "恢复默认字号"
            : "切换到 20 号大字";
    }

    private void ApplyMaxContentHeight(double value)
    {
        BodyScroller.MaxHeight = Math.Clamp(value, MinContentHeight, GetContentHeightLimit());
    }

    private static double GetContentHeightLimit()
    {
        return Math.Min(
            MaxContentHeightLimit,
            Math.Max(MinContentHeight, SystemParameters.WorkArea.Height - 180));
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        ChangeFontSize(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void BottomResizeThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        _resizing = true;
        _dragging = true;
        StopDismissWatcher();
        CardBorder.Effect = null;
    }

    private void BottomResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var target = Math.Clamp(
            BodyScroller.MaxHeight + e.VerticalChange,
            MinContentHeight,
            GetContentHeightLimit());
        if (Math.Abs(target - BodyScroller.MaxHeight) < 0.5) return;

        BodyScroller.MaxHeight = target;
    }

    private void BottomResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _resizing = false;
        _dragging = false;
        CardBorder.Effect = _cardShadow;

        if (!_userPositioned)
        {
            StartDismissWatcher();
        }

        MaxContentHeightChanged?.Invoke(BodyScroller.MaxHeight);
    }

    private void BottomResizeThumb_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ApplyMaxContentHeight(DefaultContentHeight);
        MaxContentHeightChanged?.Invoke(BodyScroller.MaxHeight);
        e.Handled = true;
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
        // 拖动过程中不响应全局监听：排队的事件会按拖动后的窗口位置重新判断，
        // 容易把“按在标题栏上”误判成“点在浮层外”，导致拖动时浮层消失。
        if (_dragging) return;
        if (!IsVisible || IsWithinGracePeriod()) return;

        // 点浮层内部（复制、重试、关闭、拖动标题）不关闭。
        if (IsOutsideOverlay(x, y))
        {
            HideOverlay();
        }
    }

    private void OnGlobalWheelScrolled(int x, int y)
    {
        if (_dragging) return;
        if (!IsVisible || IsWithinGracePeriod()) return;

        // 在浮层里滚动长译文时不关闭，滚别处才关闭。
        if (IsOutsideOverlay(x, y))
        {
            HideOverlay();
        }
    }

    private void OnGlobalKeyPressed(int virtualKey)
    {
        if (_dragging) return;
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
