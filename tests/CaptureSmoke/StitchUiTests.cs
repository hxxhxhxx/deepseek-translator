using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeepSeekTranslator.Views;

namespace CaptureSmoke;

/// <summary>拼接篮的纯 UI 回归；不取词、不访问网络。</summary>
public static class StitchUiTests
{
    public static Task RunAsync(Action<bool, string> check)
    {
        var toolbar = new SelectionToolbarWindow();
        var addClicks = 0;
        toolbar.AddClicked += () => addClicks++;
        try
        {
            toolbar.ShowAt(120, 120);
            var addButton = Control<Border>(toolbar, "AddButton");
            addButton.RaiseEvent(new MouseButtonEventArgs(
                Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent
            });
            addButton.RaiseEvent(new MouseButtonEventArgs(
                Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent
            });
            check(addClicks == 1, "划词图标：+ 按钮触发暂存事件");
        }
        finally
        {
            toolbar.CloseToolbar();
        }

        var basket = new StitchBasketWindow();
        var translated = 0;
        var cleared = 0;
        var undone = 0;
        basket.TranslateRequested += () => translated++;
        basket.ClearRequested += () => cleared++;
        basket.UndoRequested += () => undone++;
        try
        {
            basket.ShowBasket(2, 128, "最近一段：上一页最后一句");
            basket.UpdateLayout();
            var count = Control<TextBlock>(basket, "CountText");
            var preview = Control<TextBlock>(basket, "PreviewText");
            check(count.Text.Contains("2 段", StringComparison.Ordinal) &&
                  count.Text.Contains("128", StringComparison.Ordinal),
                "拼接篮：显示段数与总字数");
            check(preview.Text.Contains("上一页最后一句", StringComparison.Ordinal),
                "拼接篮：显示最近一段预览");

            Control<Button>(basket, "TranslateButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Control<Button>(basket, "UndoButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Control<Button>(basket, "ClearButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(translated == 1 && undone == 1 && cleared == 1,
                "拼接篮：翻译、撤销、清空按钮事件可用");

            basket.SetBusy(true);
            check(!Control<Button>(basket, "TranslateButton").IsEnabled &&
                  Control<Button>(basket, "UndoButton").IsEnabled == false,
                "拼接篮：翻译中禁用操作按钮");
        }
        finally
        {
            basket.HideBasket();
            basket.Close();
        }

        return Task.CompletedTask;
    }

    private static T Control<T>(Window window, string name) where T : FrameworkElement
        => window.FindName(name) as T ?? throw new InvalidOperationException("Missing stitch control: " + name);
}
