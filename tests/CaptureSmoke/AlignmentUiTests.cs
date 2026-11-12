using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeepSeekTranslator.Models;
using DeepSeekTranslator.Views;

namespace CaptureSmoke;

/// <summary>Dispatcher-driven synthetic selection tests. No API, clipboard or physical input.</summary>
public static class AlignmentUiTests
{
    private const string Target = "This matters in particular, fast and accurate.";
    private const TranslationDirection Direction = TranslationDirection.ChineseToEnglish;

    public static async Task RunAsync(OverlayWindow overlay, Action<bool, string> check)
    {
        overlay.Dispatcher.VerifyAccess();
        var original = Control<TextBox>(overlay, "OriginalText");
        var target = Control<TextBox>(overlay, "TranslationText");
        var toggle = Control<CheckBox>(overlay, "AlignmentToggle");
        var status = Control<TextBlock>(overlay, "AlignmentStatusText");
        var view = Control<Button>(overlay, "AlignmentViewButton");
        var count = Control<TextBlock>(overlay, "OriginalCountText");
        var sourceScroller = Control<ScrollViewer>(overlay, "OriginalScroller");
        var requests = new List<(AlignmentRequest Request, int Epoch)>();
        var cancellations = 0;
        void Requested(AlignmentRequest request, int epoch) => requests.Add((request, epoch));
        void Cancelled() => cancellations++;
        overlay.AlignmentRequested += Requested;
        overlay.AlignmentCancelled += Cancelled;

        var source = string.Join("\r\n", Enumerable.Range(1, 30)
            .Select(i => $"第 {i} 行：保留完整原文，不能截断定位依据。")) +
            "\r\nThis article is 尤其 important: fast and accurate.";
        var tail = source.IndexOf("尤其", StringComparison.Ordinal);
        var success = new AlignmentResult(new[] { new AlignmentSpan(tail, 2) }, true);
        var inParticular = Target.IndexOf("in particular", StringComparison.Ordinal);
        var fast = Target.IndexOf("fast", StringComparison.Ordinal);

        void Ready(string? from = null, string? to = null, bool enabled = true)
        {
            overlay.ShowLoading(from ?? source, new PixelRect(200, 200, 201, 201), Direction);
            overlay.ShowResult(new TranslationResult(to ?? Target, "mock model", "mock version", "mock", "mock", false, 1, 1), Direction);
            overlay.AlignmentEnabled = enabled;
            overlay.UpdateLayout();
        }

        (AlignmentRequest Request, int Epoch) Current() =>
            (new AlignmentRequest(original.Text, target.Text, target.SelectionStart, target.SelectionLength), overlay.CurrentAlignmentEpoch);

        void DeliverOld((AlignmentRequest Request, int Epoch) old)
        {
            overlay.ShowAlignmentResult(old.Request, old.Epoch, success);
            overlay.ShowAlignmentError(old.Request, old.Epoch, "STALE_ALIGNMENT_ERROR_MUST_NOT_APPEAR");
        }

        void CheckStale((AlignmentRequest Request, int Epoch) old, string lifecycle)
        {
            var beforeStatus = status.Text;
            DeliverOld(old);
            check(!overlay.IsCurrentAlignment(old.Request, old.Epoch) && overlay.HighlightedSourceSpans.Count == 0 &&
                  status.Text == beforeStatus && !status.Text.Contains("STALE_ALIGNMENT_ERROR", StringComparison.Ordinal),
                $"Alignment UI: {lifecycle} 拒绝旧高亮/旧错误");
        }

        try
        {
            check(!overlay.AlignmentEnabled && toggle.IsChecked != true, "Alignment UI: 默认关闭、未经授权不请求");
            check(status.Visibility == Visibility.Collapsed && count.Visibility == Visibility.Visible &&
                  toggle.Visibility == Visibility.Visible,
                "Alignment UI: 入口在标题栏，原文行不常驻提示");
            Ready(enabled: false);
            target.Select(inParticular, 13);
            await SettledAsync();
            check(requests.Count == 0 && overlay.HighlightedSourceSpans.Count == 0,
                "Alignment UI: 关闭状态选择不发送请求");
            overlay.AlignmentEnabled = true;
            await SettledAsync();
            check(requests.Count == 0, "Alignment UI: 仅开启授权不上传已有选区");

            target.Select(fast, 4);
            await Task.Delay(180);
            target.Select(inParticular, 2);
            target.Select(inParticular, 13);
            check(requests.Count == 0, "Alignment UI: 连续改变选区不立即请求");
            await SettledAsync();
            check(requests.Count == 1 && requests[0].Request.SelectedText == "in particular" &&
                  requests[0].Request.SelectionStart == inParticular,
                "Alignment UI: 650ms 防抖只请求最后稳定选区");
            check(status.Visibility == Visibility.Visible && count.Visibility == Visibility.Collapsed &&
                  status.Text.StartsWith("AI 估计：", StringComparison.Ordinal),
                "Alignment UI: 选词后原文行显示轻量状态并让出字数位置");
            var first = requests.Last();
            check(first.Request.Source == source && first.Request.Source.Length > 320 && original.Text == source,
                "Alignment UI: 超过 320 字符的完整原文进入请求");
            check(overlay.IsCurrentAlignment(first.Request, first.Epoch), "Alignment UI: 当前请求身份包含文本/选区/epoch");

            target.Focus();
            var focusBefore = Keyboard.FocusedElement;
            var logicalFocusBefore = FocusManager.GetFocusedElement(overlay);
            var selectionBefore = (target.SelectionStart, target.SelectionLength, target.SelectedText);
            var originalSelectionBefore = (original.SelectionStart, original.SelectionLength);
            overlay.ShowAlignmentResult(first.Request, first.Epoch, success);
            await overlay.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(50);
            check(overlay.HighlightedSourceSpans.SequenceEqual(success.SourceSpans) && tail > 320,
                "Alignment UI: 原文尾部可高亮且不依赖 320 字符预览");
            check(original.Text == source && target.Text == Target &&
                  (target.SelectionStart, target.SelectionLength, target.SelectedText) == selectionBefore &&
                  (original.SelectionStart, original.SelectionLength) == originalSelectionBefore,
                "Alignment UI: 高亮不修改源/译文和两侧原生选区");
            check(ReferenceEquals(Keyboard.FocusedElement, focusBefore) &&
                  ReferenceEquals(FocusManager.GetFocusedElement(overlay), logicalFocusBefore),
                "Alignment UI: 高亮不抢键盘/逻辑焦点");
            check(status.Text.Contains("估计", StringComparison.Ordinal) && view.Visibility == Visibility.Visible &&
                  count.Visibility == Visibility.Collapsed,
                "Alignment UI: 对照明确标注 AI 估计并显示查看按钮");
            check(sourceScroller.VerticalOffset > 0, "Alignment UI: 尾部对应词自动滚入原文视口");

            var multiSpans = new[]
            {
                new AlignmentSpan(source.IndexOf("fast", StringComparison.Ordinal), 4),
                new AlignmentSpan(source.IndexOf("accurate", StringComparison.Ordinal), 8)
            };
            overlay.ShowAlignmentResult(first.Request, first.Epoch, new AlignmentResult(multiSpans, true));
            check(overlay.HighlightedSourceSpans.SequenceEqual(multiSpans), "Alignment UI: 支持多个非连续原文高亮");
            overlay.ShowAlignmentResult(first.Request, first.Epoch, new AlignmentResult(Array.Empty<AlignmentSpan>(), false));
            check(overlay.HighlightedSourceSpans.Count == 0 && view.Visibility == Visibility.Collapsed &&
                  status.Visibility == Visibility.Visible && count.Visibility == Visibility.Collapsed,
                "Alignment UI: 无匹配清除所有高亮及查看按钮");
            overlay.ShowAlignmentResult(first.Request, first.Epoch, success);
            overlay.ShowAlignmentResult(first.Request, first.Epoch,
                new AlignmentResult(new[] { new AlignmentSpan(source.Length, 2) }, true));
            check(overlay.HighlightedSourceSpans.Count == 0 && view.Visibility == Visibility.Collapsed,
                "Alignment UI: 越界 span 不绘制且清除查看按钮");

            overlay.ShowAlignmentResult(first.Request, first.Epoch, success);
            var cancelledBefore = cancellations;
            target.Select(fast, 4);
            check(overlay.HighlightedSourceSpans.Count == 0 && view.Visibility == Visibility.Collapsed && cancellations > cancelledBefore,
                "Alignment UI: 新选区立即清除旧高亮并通知取消");
            CheckStale(first, "新选区");
            await SettledAsync();
            check(requests.Count == 2 && requests[^1].Request.SelectedText == "fast",
                "Alignment UI: 新选区独立产生新请求");
            var second = requests[^1];
            overlay.ShowAlignmentResult(second.Request, second.Epoch, success);
            overlay.AlignmentEnabled = false;
            CheckStale(second, "关闭功能");
            var countBefore = requests.Count;
            target.Select(inParticular, 13);
            await SettledAsync();
            check(requests.Count == countBefore && overlay.HighlightedSourceSpans.Count == 0,
                "Alignment UI: 关闭后不再请求且清理现有高亮");

            Ready();
            target.Select(inParticular, 13);
            var beforeHide = Current();
            countBefore = requests.Count;
            overlay.HideOverlay();
            CheckStale(beforeHide, "隐藏浮层");
            await SettledAsync();
            check(!overlay.IsVisible && requests.Count == countBefore &&
                  status.Visibility == Visibility.Collapsed && count.Visibility == Visibility.Visible,
                "Alignment UI: 隐藏取消尚未发出的防抖请求并恢复原文行");

            Ready();
            target.Select(inParticular, 13);
            var beforeLoading = Current();
            countBefore = requests.Count;
            overlay.ShowLoading(source, new PixelRect(200, 200, 201, 201), Direction);
            CheckStale(beforeLoading, "重新加载");
            await SettledAsync();
            check(!toggle.IsEnabled && requests.Count == countBefore, "Alignment UI: 加载中无请求且开关禁用");

            Ready();
            target.Select(inParticular, 13);
            var beforeStreaming = Current();
            countBefore = requests.Count;
            overlay.ShowStreaming(Target, Direction, overlay.CurrentStreamEpoch);
            target.Select(fast, 4);
            CheckStale(beforeStreaming, "流式输出");
            await SettledAsync();
            check(!toggle.IsEnabled && requests.Count == countBefore, "Alignment UI: 流式文本选词也不请求");

            Ready();
            target.Select(inParticular, 13);
            var beforeError = Current();
            countBefore = requests.Count;
            overlay.ShowError("Synthetic translation error", "MOCK_ERROR");
            CheckStale(beforeError, "翻译失败");
            await SettledAsync();
            check(!toggle.IsEnabled && requests.Count == countBefore, "Alignment UI: 错误状态取消请求并禁用对照");

            Ready();
            target.Select(inParticular, 13);
            var beforeReplace = Current();
            countBefore = requests.Count;
            overlay.ShowResult(new TranslationResult("Replacement translation", "mock", "mock", "mock", "mock", false, 1, 1), Direction);
            CheckStale(beforeReplace, "替换译文");
            await SettledAsync();
            check(requests.Count == countBefore, "Alignment UI: 结果替换不自动上传原选区");

            Ready();
            target.Select(inParticular, 13);
            var beforeClear = Current();
            countBefore = requests.Count;
            overlay.ClearOriginal();
            CheckStale(beforeClear, "清空原文");
            await SettledAsync();
            check(original.Text.Length == 0 && !toggle.IsEnabled && requests.Count == countBefore,
                "Alignment UI: 清空原文不保留可请求的旧状态");

            foreach (var (from, to, length, description) in new[]
            {
                (new string('中', 8001), Target, 13, "原文超过 8000"),
                (source, new string('a', 40001), 13, "译文超过 40000"),
                (source, new string('a', 401), 401, "选择超过 400"),
                (source, "   ", 3, "纯空白选择")
            })
            {
                Ready(from, to);
                countBefore = requests.Count;
                target.Select(0, length);
                await SettledAsync();
                check(requests.Count == countBefore && overlay.HighlightedSourceSpans.Count == 0,
                    $"Alignment UI: {description} 在 UI 校验阶段阻止请求");
            }

            Ready();
            target.Select(inParticular, 13);
            await SettledAsync();
            var beforeSourceClick = requests[^1];
            overlay.ShowAlignmentResult(beforeSourceClick.Request, beforeSourceClick.Epoch, success);
            original.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            });
            CheckStale(beforeSourceClick, "重新选原文");
            string? explainedWord = null;
            string? explainedContext = null;
            void Explained(string word, string? context)
            {
                explainedWord = word;
                explainedContext = context;
            }
            overlay.ExplainRequested += Explained;
            try
            {
                original.Select(source.IndexOf("article", StringComparison.Ordinal), 7);
                Control<Button>(overlay, "ExplainWordButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(explainedWord == "article" && explainedContext == source && original.Text == source,
                    "Alignment UI: 原文尾部单词详解仍使用完整上下文");
                overlay.ShowWordDetail("article", "Synthetic noun explanation; no API called.");
                check(Control<Border>(overlay, "WordPanel").Visibility == Visibility.Visible &&
                      Control<TextBox>(overlay, "WordDetailText").Text.StartsWith("Synthetic", StringComparison.Ordinal),
                    "Alignment UI: 原文单词详解面板不受对照功能影响");
            }
            finally
            {
                overlay.ExplainRequested -= Explained;
            }

            // Opt-in export of this test's synthetic content only; never capture the desktop or real user text.
            var screenshot = Environment.GetEnvironmentVariable("DSH_ALIGNMENT_SCREENSHOT");
            if (!string.IsNullOrWhiteSpace(screenshot))
            {
                const string demoSource = "This applies to all devices, in particular to low-power amplifiers.";
                const string demoTarget = "这适用于所有器件，尤其适用于低功耗放大器。";
                overlay.PreferredWidth = 480;
                overlay.PreferredMaxContentHeight = 500;
                overlay.ShowLoading(demoSource, new PixelRect(200, 200, 201, 201), TranslationDirection.EnglishToChinese);
                overlay.ShowResult(new TranslationResult(demoTarget, "DeepSeek V4.1 Flash", "synthetic example", "mock", "mock", false, 1, 100),
                    TranslationDirection.EnglishToChinese);
                overlay.AlignmentEnabled = true;
                target.Select(demoTarget.IndexOf("尤其", StringComparison.Ordinal), 2);
                var demo = Current();
                overlay.ShowAlignmentResult(demo.Request, demo.Epoch,
                    new AlignmentResult(new[] { new AlignmentSpan(demoSource.IndexOf("in particular", StringComparison.Ordinal), 13) }, true));
                await Task.Delay(100);
                overlay.UpdateLayout();
                SaveSyntheticRender(overlay, screenshot);
                check(true, "Alignment UI: 显式环境变量启用的合成示例渲染完成");

                // Inspect only OriginalText's image region: a spans-state assertion alone cannot prove visible paint.
                using var imageStream = new FileStream(screenshot, FileMode.Open, FileAccess.Read, FileShare.Read);
                var decoded = new PngBitmapDecoder(imageStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var bitmap = new FormatConvertedBitmap(decoded.Frames[0], PixelFormats.Bgra32, null, 0);
                var dpi = VisualTreeHelper.GetDpi(overlay);
                var sourceBounds = original.TransformToAncestor(overlay)
                    .TransformBounds(new Rect(0, 0, original.ActualWidth, original.ActualHeight));
                var left = Math.Clamp((int)Math.Floor(sourceBounds.Left * dpi.DpiScaleX), 0, bitmap.PixelWidth - 1);
                var top = Math.Clamp((int)Math.Floor(sourceBounds.Top * dpi.DpiScaleY), 0, bitmap.PixelHeight - 1);
                var right = Math.Clamp((int)Math.Ceiling(sourceBounds.Right * dpi.DpiScaleX), left + 1, bitmap.PixelWidth);
                var bottom = Math.Clamp((int)Math.Ceiling(sourceBounds.Bottom * dpi.DpiScaleY), top + 1, bitmap.PixelHeight);
                var stride = (right - left) * 4;
                var pixels = new byte[stride * (bottom - top)];
                bitmap.CopyPixels(new Int32Rect(left, top, right - left, bottom - top), pixels, stride, 0);
                var amberPixels = 0;
                for (var i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i + 3] > 240 && pixels[i + 2] > 220 && pixels[i + 1] is > 170 and < 245 &&
                        pixels[i] < 210 && pixels[i + 2] - pixels[i] > 40) amberPixels++;
                }
                check(amberPixels >= 10, "Alignment UI: 导出图原文区域实际绘制琥珀色高亮");
            }
        }
        finally
        {
            overlay.AlignmentRequested -= Requested;
            overlay.AlignmentCancelled -= Cancelled;
            overlay.AlignmentEnabled = false;
            overlay.HideOverlay();
        }
    }

    private static void SaveSyntheticRender(OverlayWindow overlay, string destination)
    {
        var path = Path.GetFullPath(destination);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var dpi = VisualTreeHelper.GetDpi(overlay);
        var image = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(overlay.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(overlay.ActualHeight * dpi.DpiScaleY)),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(overlay);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        // The path is explicitly supplied by the caller for this generated test artifact.
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(file);
    }

    // An awaited delay returns through the WPF SynchronizationContext and pumps the real DispatcherTimer.
    private static Task SettledAsync() => Task.Delay(850);

    private static T Control<T>(OverlayWindow overlay, string name) where T : FrameworkElement =>
        overlay.FindName(name) as T ?? throw new InvalidOperationException($"Missing alignment test control: {name}");
}
