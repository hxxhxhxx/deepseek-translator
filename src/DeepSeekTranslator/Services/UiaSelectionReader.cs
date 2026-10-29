using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using DeepSeekTranslator.Models;

namespace DeepSeekTranslator.Services;

public sealed record UiaReadResult(string Text, PixelRect? Bounds, bool IsPassword);

/// <summary>
/// 通过 UI Automation 读取当前前台窗口的文本选区。
/// Word / PowerPoint 通常暴露 TextPattern；微信 Qt 界面通常没有。
/// </summary>
public sealed class UiaSelectionReader
{
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromMilliseconds(2600);

    /// <param name="timeout">UIA 探测超时；划词图标使用更短的超时。</param>
    /// <param name="maxDescendants">最多遍历多少个可文本节点（0 表示只看焦点元素和根节点）。</param>
    public async Task<UiaReadResult?> TryReadAsync(
        IntPtr foregroundWindow,
        uint processId,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        int maxDescendants = 120)
    {
        if (foregroundWindow == IntPtr.Zero) return null;

        var task = Task.Run(
            () => TryRead(foregroundWindow, processId, maxDescendants),
            cancellationToken);
        var completed = await Task.WhenAny(
                task,
                Task.Delay(timeout ?? DefaultReadTimeout, cancellationToken))
            .ConfigureAwait(false);

        if (completed != task)
        {
            // UIA 对无响应应用可能永久阻塞；放弃本次探测，让调用方走剪贴板回退。
            _ = task.ContinueWith(
                t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return null;
        }

        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error("UIA 读取选区失败", ex);
            return null;
        }
    }

    /// <summary>
    /// 按屏幕坐标读取选区：浏览器（Chromium）和 Office 里，鼠标位置下方的文本元素
    /// 往往比「当前焦点元素」更可靠（浏览器焦点可能停在 Pane 上）。
    /// </summary>
    public async Task<UiaReadResult?> TryReadFromPointAsync(
        int x,
        int y,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var task = Task.Run(() => TryReadFromPoint(x, y), cancellationToken);
        var completed = await Task.WhenAny(
                task,
                Task.Delay(timeout ?? DefaultReadTimeout, cancellationToken))
            .ConfigureAwait(false);

        if (completed != task)
        {
            _ = task.ContinueWith(
                t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return null;
        }

        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error("UIA 按坐标读取选区失败", ex);
            return null;
        }
    }

    private static UiaReadResult? TryReadFromPoint(int x, int y)
    {
        AutomationElement? element;
        try
        {
            element = AutomationElement.FromPoint(new System.Windows.Point(x, y));
        }
        catch (Exception ex)
        {
            Logger.Error("UIA FromPoint 失败", ex);
            return null;
        }

        var walker = TreeWalker.ControlViewWalker;

        // 从命中元素向上找最近的可文本节点；浏览器里通常是 Document / Text，
        // Word 里是文档节点本身。
        for (var depth = 0; depth < 10 && element is not null; depth++)
        {
            try
            {
                if (element.Current.IsPassword)
                {
                    return new UiaReadResult(string.Empty, null, true);
                }
            }
            catch
            {
                // 元素失效时继续向上。
            }

            var result = Extract(element);
            if (result is not null) return result;

            try
            {
                element = walker.GetParent(element);
            }
            catch
            {
                break;
            }
        }

        return null;
    }

    private static UiaReadResult? TryRead(IntPtr hwnd, uint processId, int maxDescendants)
    {
        AutomationElement root;
        try
        {
            root = AutomationElement.FromHandle(hwnd);
        }
        catch (Exception ex)
        {
            Logger.Error("UIA 获取窗口元素失败", ex);
            return null;
        }

        if (root is null) return null;

        try
        {
            if (root.Current.IsPassword)
            {
                return new UiaReadResult(string.Empty, null, true);
            }
        }
        catch
        {
            // 某些窗口不支持 Current 属性，继续尝试 TextPattern。
        }

        // 大多数编辑器会把焦点放在文档/文本框本身，优先读取聚焦元素。
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is not null &&
                focused.Current.ProcessId == (int)processId)
            {
                if (focused.Current.IsPassword)
                {
                    return new UiaReadResult(string.Empty, null, true);
                }

                var focusedResult = Extract(focused);
                if (focusedResult is not null) return focusedResult;
            }
        }
        catch
        {
            // 焦点元素可能属于其他进程或已失效。
        }

        var rootResult = Extract(root);
        if (rootResult is not null) return rootResult;

        if (maxDescendants <= 0) return null;

        try
        {
            var condition = new PropertyCondition(
                AutomationElement.IsTextPatternAvailableProperty,
                true);
            var descendants = root.FindAll(TreeScope.Descendants, condition);
            var count = Math.Min(descendants.Count, maxDescendants);

            for (var i = 0; i < count; i++)
            {
                var element = descendants[i];
                try
                {
                    if (element.Current.IsPassword)
                    {
                        return new UiaReadResult(string.Empty, null, true);
                    }
                }
                catch
                {
                    continue;
                }

                var result = Extract(element);
                if (result is not null) return result;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("UIA 遍历 TextPattern 节点失败", ex);
        }

        return null;
    }

    private static UiaReadResult? Extract(AutomationElement element)
    {
        try
        {
            object available;
            try
            {
                available = element.GetCurrentPropertyValue(
                    AutomationElement.IsTextPatternAvailableProperty,
                    ignoreDefaultValue: true);
            }
            catch
            {
                return null;
            }

            if (available is not true) return null;

            if (element.GetCurrentPattern(TextPattern.Pattern) is not TextPattern pattern)
            {
                return null;
            }

            var ranges = pattern.GetSelection();
            if (ranges is null || ranges.Length == 0) return null;

            var parts = new List<string>(ranges.Length);
            foreach (var range in ranges)
            {
                try
                {
                    var text = range.GetText(-1);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        parts.Add(text);
                    }
                }
                catch
                {
                    // 某个 range 失效时继续处理其他 range。
                }
            }

            var selected = string.Join("\n", parts).Trim();
            if (selected.Length == 0) return null;

            var bounds = GetBounds(ranges);
            return new UiaReadResult(selected, bounds, false);
        }
        catch (Exception ex) when (
            ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error("UIA 提取选区文本失败", ex);
            return null;
        }
    }

    private static PixelRect? GetBounds(TextPatternRange[] ranges)
    {
        double? left = null;
        double? top = null;
        double? right = null;
        double? bottom = null;

        foreach (var range in ranges)
        {
            try
            {
                var rectangles = range.GetBoundingRectangles();
                if (rectangles is null) continue;

                foreach (var rect in rectangles)
                {
                    if (rect.Width <= 0 || rect.Height <= 0) continue;

                    left = left is null ? rect.Left : Math.Min(left.Value, rect.Left);
                    top = top is null ? rect.Top : Math.Min(top.Value, rect.Top);
                    right = right is null ? rect.Right : Math.Max(right.Value, rect.Right);
                    bottom = bottom is null ? rect.Bottom : Math.Max(bottom.Value, rect.Bottom);
                }
            }
            catch
            {
                // 忽略单个 range 的坐标失败。
            }
        }

        if (left is null || top is null || right is null || bottom is null) return null;
        return PixelRect.FromLtrb(left.Value, top.Value, right.Value, bottom.Value);
    }
}
