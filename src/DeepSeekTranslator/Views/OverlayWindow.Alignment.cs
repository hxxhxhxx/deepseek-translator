using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using DeepSeekTranslator.Models;

namespace DeepSeekTranslator.Views;

public partial class OverlayWindow
{
    private DispatcherTimer? _alignmentTimer;
    private AlignmentHighlightAdorner? _alignmentAdorner;
    private AdornerLayer? _alignmentLayer;
    private AlignmentRequest? _pendingAlignment;
    private bool _alignmentInitialized;
    private bool _alignmentReady;
    private int _alignmentEpoch;
    private IReadOnlyList<AlignmentSpan> _highlightedSpans = Array.Empty<AlignmentSpan>();

    public event Action<AlignmentRequest, int>? AlignmentRequested;
    public event Action? AlignmentCancelled;
    public event Action<bool>? AlignmentEnabledChanged;

    /// <summary>默认关闭；用户明确开启后，稳定的译文选区才会发送额外请求。</summary>
    public bool AlignmentEnabled
    {
        get => AlignmentToggle.IsChecked == true;
        set => AlignmentToggle.IsChecked = value;
    }

    public int CurrentAlignmentEpoch => _alignmentEpoch;
    public IReadOnlyList<AlignmentSpan> HighlightedSourceSpans => _highlightedSpans;

    private void InitializeAlignment()
    {
        _alignmentTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _alignmentTimer.Tick += AlignmentTimer_Tick;
        OriginalText.SizeChanged += (_, _) => _alignmentAdorner?.InvalidateVisual();
        OriginalScroller.ScrollChanged += (_, _) => _alignmentAdorner?.InvalidateVisual();
        _alignmentInitialized = true;
        ResetAlignment();
    }

    private void ResetAlignment(bool ready = false)
    {
        if (!_alignmentInitialized) return;
        _alignmentTimer?.Stop();
        _pendingAlignment = null;
        _alignmentEpoch++;
        _alignmentReady = ready;
        AlignmentCancelled?.Invoke();
        ClearAlignmentHighlight();
        AlignmentToggle.IsEnabled = ready;
        AlignmentViewButton.Visibility = Visibility.Collapsed;
        SetAlignmentIdleText();
    }

    private void SetAlignmentIdleText()
    {
        // 不做常驻提示：只有真的选词/查询时才占用原文标题行。
        AlignmentStatusText.Text = string.Empty;
        AlignmentStatusText.ToolTip = null;
        AlignmentStatusText.Visibility = Visibility.Collapsed;
        AlignmentViewButton.Visibility = Visibility.Collapsed;
        OriginalCountText.Visibility = Visibility.Visible;
    }

    private void ShowAlignmentFeedback(string text, bool showView, string? toolTip = null)
    {
        AlignmentStatusText.Text = text;
        AlignmentStatusText.ToolTip = toolTip ?? text;
        AlignmentStatusText.Visibility = Visibility.Visible;
        AlignmentViewButton.Visibility = showView ? Visibility.Visible : Visibility.Collapsed;
        OriginalCountText.Visibility = Visibility.Collapsed;
    }

    private void AlignmentToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_alignmentInitialized) return;
        ResetAlignment(_alignmentReady);
        AlignmentEnabledChanged?.Invoke(AlignmentEnabled);
        // 开启只表示授权后续选词，不立即上传已经存在的选区。
    }

    private void TranslationText_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!_alignmentInitialized) return;
        _alignmentTimer?.Stop();
        _pendingAlignment = null;
        _alignmentEpoch++;
        AlignmentCancelled?.Invoke();
        ClearAlignmentHighlight();
        AlignmentViewButton.Visibility = Visibility.Collapsed;
        if (!_alignmentReady || !AlignmentEnabled || !IsVisible || TranslationText.SelectionLength == 0)
        {
            SetAlignmentIdleText();
            return;
        }

        var request = new AlignmentRequest(OriginalText.Text, TranslationText.Text,
            TranslationText.SelectionStart, TranslationText.SelectionLength);
        try
        {
            request.Validate();
        }
        catch (Exception ex) when (ex is DeepSeekException or ArgumentException)
        {
            ShowAlignmentFeedback("AI 估计：" + ex.Message, false, ex.Message);
            return;
        }
        _pendingAlignment = request;
        ShowAlignmentFeedback("AI 估计：等待选区稳定…", false);
        _alignmentTimer?.Start();
    }

    private void AlignmentTimer_Tick(object? sender, EventArgs e)
    {
        _alignmentTimer?.Stop();
        var request = _pendingAlignment;
        if (request is null || !IsCurrentAlignment(request, _alignmentEpoch)) return;
        // 拖动中、Shift 扩选中不请求；不以每次 SelectionChanged 发出网络调用。
        if (Mouse.LeftButton == MouseButtonState.Pressed || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _alignmentTimer?.Start();
            return;
        }
        _pendingAlignment = null;
        ShowAlignmentFeedback("AI 估计：定位中…", false);
        if (AlignmentRequested is null)
        {
            ShowAlignmentFeedback("AI 估计：服务尚未连接", false);
            return;
        }
        AlignmentRequested.Invoke(request, _alignmentEpoch);
    }

    public bool IsCurrentAlignment(AlignmentRequest request, int epoch)
    {
        return _alignmentReady && AlignmentEnabled && IsVisible && epoch == _alignmentEpoch
            && OriginalText.Text == request.Source && TranslationText.Text == request.Translation
            && TranslationText.SelectionStart == request.SelectionStart
            && TranslationText.SelectionLength == request.SelectionLength;
    }

    public void ShowAlignmentResult(AlignmentRequest request, int epoch, AlignmentResult result)
    {
        if (!IsCurrentAlignment(request, epoch)) return;
        ClearAlignmentHighlight();
        if (!result.HasMatch || result.SourceSpans.Any(span => span.Start < 0 || span.Length <= 0
            || span.Start > OriginalText.Text.Length || span.Length > OriginalText.Text.Length - span.Start))
        {
            ShowAlignmentFeedback("AI 估计：未找到可靠对应", false,
                "可改选完整词组再试；意译可能没有逐词对应。");
            return;
        }
        _highlightedSpans = result.SourceSpans.ToArray();
        _alignmentLayer ??= AdornerLayer.GetAdornerLayer(OriginalText);
        if (_alignmentLayer is null)
        {
            _highlightedSpans = Array.Empty<AlignmentSpan>();
            ShowAlignmentFeedback("AI 估计：高亮暂不可用", false, "请重新打开浮层后再试。");
            return;
        }
        if (_alignmentAdorner is null)
        {
            _alignmentAdorner = new AlignmentHighlightAdorner(OriginalText);
            _alignmentLayer.Add(_alignmentAdorner);
        }
        _alignmentAdorner.SetSpans(_highlightedSpans);
        var sourceQuotes = _highlightedSpans.Select(span => OriginalText.Text.Substring(span.Start, span.Length));
        ShowAlignmentFeedback(
            $"AI 估计：{AlignmentPreview(request.SelectedText, 24)} → {AlignmentPreview(string.Join(" / ", sourceQuotes), 72)}",
            true,
            "AI 估计，仅供参考；琥珀色为对应原文。");
        OriginalText.UpdateLayout();
        var location = OriginalText.GetRectFromCharacterIndex(_highlightedSpans[0].Start);
        if (!location.IsEmpty) OriginalScroller.ScrollToVerticalOffset(Math.Max(0, location.Top - 8));
        // 不调用 Focus/Select，不改变用户的译文选区与 Ctrl+C 行为。
    }

    public void ShowAlignmentError(AlignmentRequest request, int epoch, string message)
    {
        if (!IsCurrentAlignment(request, epoch)) return;
        ClearAlignmentHighlight();
        ShowAlignmentFeedback("AI 估计失败：" + message, false, message);
    }

    private static string AlignmentPreview(string text, int limit)
    {
        var singleLine = text.Replace('\r', ' ').Replace('\n', ' ');
        if (singleLine.Length <= limit) return singleLine;
        if (char.IsHighSurrogate(singleLine[limit - 1])) limit--;
        return singleLine[..limit] + "…";
    }

    private void ClearAlignmentHighlight()
    {
        _highlightedSpans = Array.Empty<AlignmentSpan>();
        _alignmentAdorner?.SetSpans(_highlightedSpans);
    }

    private void AlignmentViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_highlightedSpans.Count == 0) return;
        OriginalCard.BringIntoView();
    }

    private void OriginalText_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_alignmentInitialized) return;
        _alignmentTimer?.Stop();
        _pendingAlignment = null;
        _alignmentEpoch++;
        AlignmentCancelled?.Invoke();
        ClearAlignmentHighlight();
        AlignmentViewButton.Visibility = Visibility.Collapsed;
        SetAlignmentIdleText();
    }

    private void OriginalText_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (OriginalScroller.ScrollableHeight > 0 &&
            (e.Delta > 0 && OriginalScroller.VerticalOffset > 0 ||
             e.Delta < 0 && OriginalScroller.VerticalOffset < OriginalScroller.ScrollableHeight))
        {
            OriginalScroller.ScrollToVerticalOffset(OriginalScroller.VerticalOffset - e.Delta);
            e.Handled = true;
            return;
        }
        TranslationText_PreviewMouseWheel(sender, e);
    }

    private void DisposeAlignment()
    {
        ResetAlignment();
        if (_alignmentAdorner is not null) _alignmentLayer?.Remove(_alignmentAdorner);
        _alignmentAdorner = null;
        _alignmentLayer = null;
    }
}
