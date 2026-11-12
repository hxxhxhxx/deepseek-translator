using System.Windows;
using System.Windows.Controls;
using DeepSeekTranslator.Models;
using DeepSeekTranslator.Services;
using DeepSeekTranslator.Views;

namespace CaptureSmoke;

/// <summary>固定模式的无网络、无物理输入回归测试。</summary>
public static class PinOverlayUiTests
{
    public static async Task RunAsync(OverlayWindow overlay, Action<bool, string> check)
    {
        var translation = Control<TextBox>(overlay, "TranslationText");
        var updating = Control<TextBlock>(overlay, "UpdatingHint");
        var pinButton = Control<CheckBox>(overlay, "PinButton");
        var changed = new List<bool>();
        void PinChanged(bool value) => changed.Add(value);
        overlay.PinOverlayChanged += PinChanged;
        var direction = TranslationDirection.EnglishToChinese;
        var first = new TranslationResult("第一段固定译文", "mock", "mock", "mock", "mock", false, 1, 1);
        var second = new TranslationResult("第二段固定译文", "mock", "mock", "mock", "mock", false, 1, 1);
        const string sourceA = "First pinned source.";
        const string sourceB = "Second pinned source.";

        try
        {
            check(pinButton.IsChecked != true && !overlay.PinOverlay,
                "固定模式：标题栏图钉默认未选中");
            pinButton.IsChecked = true;
            check(overlay.PinOverlay && changed.Count > 0,
                "固定模式：标题栏图钉可直接开启并通知保存");
            overlay.PinOverlay = false;
            check(pinButton.IsChecked != true,
                "固定模式：设置状态变化会同步回图钉");
            pinButton.IsChecked = true;

            overlay.ShowLoading(sourceA, new PixelRect(420, 300, 446, 326), direction);
            overlay.ShowResult(first, direction);
            overlay.UpdateLayout();
            await Task.Delay(80);
            var pinnedLeft = overlay.Left;
            var pinnedTop = overlay.Top;

            overlay.ShowLoading(sourceB, new PixelRect(900, 520, 926, 546), direction);
            overlay.UpdateLayout();
            check(Math.Abs(overlay.Left - pinnedLeft) < 0.5 && Math.Abs(overlay.Top - pinnedTop) < 0.5,
                "固定模式：新翻译不移动到新选区旁");
            check(translation.Text == first.Translation &&
                  translation.Visibility == Visibility.Visible &&
                  updating.Visibility == Visibility.Visible,
                "固定模式：新请求期间保留上一段译文并显示正在更新");

            overlay.ShowResult(second, direction);
            overlay.UpdateLayout();
            await Task.Delay(80);
            check(translation.Text == second.Translation &&
                  updating.Visibility == Visibility.Collapsed,
                "固定模式：新结果替换旧译文并收起更新提示");
            check(Math.Abs(overlay.Left - pinnedLeft) < 0.5 && Math.Abs(overlay.Top - pinnedTop) < 0.5,
                "固定模式：结果落地后位置仍不变");

            // 明确关闭后，下一次翻译应回到新选区旁，而不是旧固定位置。
            overlay.HideOverlay();
            var monitor = ScreenGeometry.GetMonitorForPoint(420, 300);
            overlay.ShowLoading(sourceB, new PixelRect(
                monitor.WorkArea.Left + 520, monitor.WorkArea.Top + 260,
                monitor.WorkArea.Left + 548, monitor.WorkArea.Top + 286), direction);
            overlay.ShowResult(second, direction);
            overlay.UpdateLayout();
            await Task.Delay(80);
            check(Math.Abs(overlay.Left - pinnedLeft) > 5 || Math.Abs(overlay.Top - pinnedTop) > 5,
                "固定模式：点 × 后下一次按新选区重新落位");

            // 显式设定的物理位置应被恢复，并在显示前收敛到工作区。
            overlay.HideOverlay();
            var targetX = monitor.WorkArea.Left + 120;
            var targetY = monitor.WorkArea.Top + 120;
            overlay.PreferredPinnedPosition = (targetX, targetY);
            overlay.ShowLoading(sourceA, new PixelRect(
                monitor.WorkArea.Left + 700, monitor.WorkArea.Top + 400,
                monitor.WorkArea.Left + 728, monitor.WorkArea.Top + 426), direction);
            overlay.ShowResult(first, direction);
            overlay.UpdateLayout();
            await Task.Delay(80);
            var dpi = WindowPos.CurrentDpiScale(overlay);
            check(Math.Abs(overlay.Left - targetX / dpi) < 3 && Math.Abs(overlay.Top - targetY / dpi) < 3,
                "固定模式：记住并恢复上次位置");
        }
        finally
        {
            overlay.PinOverlayChanged -= PinChanged;
            overlay.PinOverlay = false;
            overlay.HideOverlay();
        }
    }

    private static T Control<T>(OverlayWindow overlay, string name) where T : FrameworkElement
        => overlay.FindName(name) as T ?? throw new InvalidOperationException("Missing pin test control: " + name);
}
