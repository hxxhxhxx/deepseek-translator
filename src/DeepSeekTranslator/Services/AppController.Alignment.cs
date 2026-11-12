using DeepSeekTranslator.Models;

namespace DeepSeekTranslator.Services;

public sealed partial class AppController
{
    private TextAlignmentService? _alignmentService;
    private CancellationTokenSource? _alignmentCts;

    private void CancelAlignment()
    {
        var previous = _alignmentCts;
        _alignmentCts = null;
        // CTS 由创建它的异步调用 finally 释放，避免旧请求的 token 被提前 Dispose。
        previous?.Cancel();
    }

    private async Task AlignTranslationSelectionAsync(AlignmentRequest request, int epoch)
    {
        CancelAlignment();
        var overlay = _overlay;
        if (_disposed || overlay is null || !overlay.IsCurrentAlignment(request, epoch)) return;
        string? key = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _alignmentCts = cancellation;
        try
        {
            key = _secretStore.Load();
            if (string.IsNullOrWhiteSpace(key))
            {
                overlay.ShowAlignmentError(request, epoch, "尚未配置 API Key，请在设置中填写后重新选词。");
                return;
            }
            _alignmentService ??= new TextAlignmentService(_deepSeek);
            var result = await _alignmentService.AlignAsync(request, key, _settings.Model, cancellation.Token);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(_overlay, overlay))
                overlay.ShowAlignmentResult(request, epoch, result);
        }
        catch (OperationCanceledException)
        {
            // 换选区、关闭、重新翻译、退出均正常取消，不把旧错误覆盖到新结果。
        }
        catch (DeepSeekException ex)
        {
            overlay.ShowAlignmentError(request, epoch, ex.Message);
        }
        catch (Exception ex)
        {
            // 不记录原文/译文/选区，避免模型或服务端回显导致内容进入日志。
            Logger.Info($"alignment failed: {ex.GetType().Name}");
            overlay.ShowAlignmentError(request, epoch, "对照暂时失败，请稍后重新选中词组重试。");
        }
        finally
        {
            key = null;
            if (ReferenceEquals(_alignmentCts, cancellation)) _alignmentCts = null;
        }
    }
}
