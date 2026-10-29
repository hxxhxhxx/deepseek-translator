using DeepSeekTranslator.Interop;
using DeepSeekTranslator.Models;

namespace DeepSeekTranslator.Services;

/// <summary>
/// 取词编排：UIA 优先，剪贴板回退，管理员窗口直接拒绝自动复制。
/// </summary>
public sealed class CaptureCoordinator
{
    private readonly UiaSelectionReader _uia = new();
    private readonly ClipboardSelectionReader _clipboard = new();

    /// <summary>当前设置的快捷键文案，只用于错误提示（随设置变化）。</summary>
    public string SelectionHotkey { get; set; } = HotkeyParser.DefaultTranslateSelection;

    public string ClipboardHotkey { get; set; } = HotkeyParser.DefaultTranslateClipboard;

    public string OcrHotkey { get; set; } = HotkeyParser.DefaultOcr;

    public async Task<CaptureResult> CaptureSelectionAsync(CancellationToken cancellationToken)
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            throw new CaptureException("NO_FOREGROUND", "没有找到当前前台窗口。");
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        var appName = ElevationHelper.GetProcessName(processId);

        if (processId == (uint)Environment.ProcessId)
        {
            throw new CaptureException(
                "SELF_FOREGROUND",
                "当前焦点在翻译浮层或设置窗口上。请先回到要翻译的软件选中文字，再按快捷键。");
        }

        Logger.Info($"capture: app={appName}, uiaSkipped={ShouldSkipUia(appName)}");

        if (processId != 0 &&
            ElevationHelper.IsProcessElevated(processId) &&
            !ElevationHelper.IsCurrentProcessElevated())
        {
            throw new CaptureException(
                "ELEVATED_TARGET",
                $"当前窗口（{appName}）以管理员权限运行。请先手动 Ctrl+C 复制，再按 {ClipboardHotkey}；或以管理员身份重启本应用。");
        }

        UiaReadResult? uia = null;
        if (!ShouldSkipUia(appName))
        {
            uia = await _uia.TryReadAsync(hwnd, processId, cancellationToken)
                .ConfigureAwait(false);

            if (uia?.IsPassword == true)
            {
                throw new CaptureException("PASSWORD_FIELD", "当前输入框受保护，不读取其中的内容。");
            }

            if (uia is not null && !string.IsNullOrWhiteSpace(uia.Text))
            {
                return new CaptureResult(
                    uia.Text,
                    uia.Bounds ?? ScreenGeometry.GetCursorPoint(),
                    CaptureMethod.Uia,
                    appName);
            }
        }

        var clipboard = await _clipboard.TryCopySelectionAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(clipboard.Text))
        {
            return new CaptureResult(
                clipboard.Text,
                ScreenGeometry.GetCursorPoint(),
                CaptureMethod.Clipboard,
                appName);
        }

        if (clipboard.TimedOut)
        {
            Logger.Info($"capture failed: app={appName}, code=CLIPBOARD_TIMEOUT, detail={clipboard.Diagnostic}");
            throw new CaptureException(
                "CLIPBOARD_TIMEOUT",
                $"未能从 {appName} 读取选区：剪贴板没有变化。{HintFor(appName)}");
        }

        var detail = string.IsNullOrWhiteSpace(clipboard.Diagnostic)
            ? string.Empty
            : $"（{clipboard.Diagnostic}）";
        Logger.Info($"capture failed: app={appName}, code=NO_SELECTION, detail={clipboard.Diagnostic}");
        throw new CaptureException(
            "NO_SELECTION",
            $"未能从 {appName} 读取选中文本{detail}。{HintFor(appName)}");
    }

    private string HintFor(string appName)
    {
        if (appName.Equals("POWERPNT", StringComparison.OrdinalIgnoreCase))
        {
            return $"PowerPoint 请先双击文本框进入文字编辑（看到闪烁光标）再拖选；放映模式下没有文本选区，可改用 {OcrHotkey} 截图。";
        }

        if (AppCompatibility.IsBrowser(appName))
        {
            return $"请确认已选中可复制的文字；如果已经手动复制，请按 {ClipboardHotkey}（翻译剪贴板）。";
        }

        return $"请确认已选中可复制的文字；如果已经手动复制，请按 {ClipboardHotkey}（翻译剪贴板），不要按 {SelectionHotkey}。";
    }

    private static bool ShouldSkipUia(string appName)
    {
        // 这些应用没有可靠的 UIA 选区；直接走剪贴板，避免每次等待 UIA 超时。
        return AppCompatibility.IsClipboardOnly(appName) || AppCompatibility.IsBrowser(appName);
    }

    public async Task<CaptureResult> ReadClipboardAsync(CancellationToken cancellationToken)
    {
        var text = await _clipboard.ReadClipboardTextAsync(cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(text))
        {
            Logger.Info("capture failed: code=EMPTY_CLIPBOARD");
            throw new CaptureException(
                "EMPTY_CLIPBOARD",
                $"剪贴板里没有可翻译的文本。请先复制一段文字，再按 {ClipboardHotkey}。");
        }

        return new CaptureResult(
            text,
            ScreenGeometry.GetCursorPoint(),
            CaptureMethod.ManualClipboard,
            "剪贴板");
    }
}
