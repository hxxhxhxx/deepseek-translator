using System.Windows;
using System.Windows.Forms;
using DeepSeekTranslator.Interop;
using DeepSeekTranslator.Models;
using DeepSeekTranslator.Views;
using Application = System.Windows.Application;

namespace DeepSeekTranslator.Services;

public sealed class AppController : IDisposable
{
    private readonly SettingsStore _settingsStore = new();
    private readonly SecretStore _secretStore = new();
    private readonly DeepSeekClient _deepSeek = new();
    private readonly CaptureCoordinator _capture = new();
    private readonly OcrService _ocr = new();
    private readonly UiaSelectionReader _selectionReader = new();
    private readonly UpdateService _updates = new();
    private readonly CancellationTokenSource _lifetime = new();

    private AppSettings _settings = new();
    private TrayService? _tray;
    private HotkeyService? _hotkeys;
    private OverlayWindow? _overlay;
    private SettingsWindow? _settingsWindow;
    private UpdateWindow? _updateWindow;
    private SelectionMonitor? _selectionMonitor;
    private SelectionToolbarWindow? _selectionToolbar;
    private PixelRect _toolbarRect;
    private int _selectionProbeVersion;
    private string? _lastTranslation;
    private string? _currentText;
    private PixelRect? _currentAnchor;
    private bool _currentWasFullscreen;
    private Func<Task>? _retryAction;
    private bool _busy;
    private bool _disposed;

    public void Initialize()
    {
        AppPaths.EnsureDirectories();
        _settings = _settingsStore.Load();
        _capture.SelectionHotkey = _settings.TranslateSelectionHotkey;
        _capture.ClipboardHotkey = _settings.TranslateClipboardHotkey;
        _capture.OcrHotkey = _settings.OcrHotkey;

        if (_settings.AutoStart)
        {
            try
            {
                AutoStartService.SetEnabled(true);
            }
            catch (Exception ex)
            {
                Logger.Error("初始化开机自启失败", ex);
            }
        }

        _tray = new TrayService();
        _tray.TranslateSelectionRequested += () => RunGuarded(TranslateSelectionAsync);
        _tray.TranslateClipboardRequested += () => RunGuarded(TranslateClipboardAsync);
        _tray.OcrRequested += () => RunGuarded(StartOcrAsync);
        _tray.CopyLastRequested += CopyLastTranslation;
        _tray.SettingsRequested += OpenSettings;
        _tray.HotkeySettingsRequested += OpenHotkeySettings;
        _tray.AutoStartChanged += ToggleAutoStart;
        _tray.GameModeChanged += ToggleGameMode;
        _tray.UpdateCheckRequested += () => RunGuarded(() => CheckForUpdatesAsync(silent: false));
        _tray.ExitRequested += Exit;
        _tray.SetAutoStartChecked(_settings.AutoStart);
        _tray.SetGameMode(_settings.GameMode);
        _tray.SetHotkeyLabels(_settings, _settings.GameMode);

        _hotkeys = new HotkeyService(_settings, enabled: !_settings.GameMode);
        _hotkeys.HotkeyPressed += OnHotkeyPressed;

        if (_hotkeys.FailedHotkeys.Count > 0)
        {
            _tray.ShowBalloon(
                "快捷键注册失败",
                $"以下快捷键被其他程序占用：{string.Join("、", _hotkeys.FailedHotkeys)}。可以通过托盘菜单操作。",
                ToolTipIcon.Warning);
        }

        ApplySelectionIconSetting();

        if (_settings.AutoCheckUpdate)
        {
            _ = RunStartupUpdateCheckAsync();
        }

        if (!_secretStore.HasSecret())
        {
            _tray.ShowBalloon(
                "还需要配置 API Key",
                "右键托盘图标选择“设置”，填入 DeepSeek API Key 后即可开始翻译。",
                ToolTipIcon.Info);
        }
    }

    private void OnHotkeyPressed(HotkeyAction action)
    {
        Logger.Info($"hotkey: {action}, foreground={GetForegroundProcessName()}");

        switch (action)
        {
            case HotkeyAction.TranslateSelection:
                RunGuarded(TranslateSelectionAsync);
                break;
            case HotkeyAction.TranslateClipboard:
                RunGuarded(TranslateClipboardAsync);
                break;
            case HotkeyAction.OcrTranslate:
                RunGuarded(StartOcrAsync);
                break;
            case HotkeyAction.CopyLastTranslation:
                CopyLastTranslation();
                break;
        }
    }

    private static string GetForegroundProcessName()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return "none";

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        return processId == 0 ? "unknown" : ElevationHelper.GetProcessName(processId);
    }

    private async Task TranslateSelectionAsync()
    {
        if (_busy)
        {
            Logger.Info("translate selection ignored: busy");
            return;
        }

        _busy = true;

        try
        {
            _lastTranslation = null;
            _retryAction = TranslateSelectionAsync;
            var capture = await _capture
                .CaptureSelectionAsync(_lifetime.Token)
                .WaitAsync(TimeSpan.FromSeconds(12), _lifetime.Token);
            Logger.Info($"capture ok: app={capture.AppName}, method={capture.Method}, chars={capture.Text.Length}");
            await TranslateCapturedAsync(capture);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 程序正在退出。
        }
        catch (TimeoutException)
        {
            Logger.Error("读取选区超时（12 秒）");
            ShowError("读取选区超时。请重试；如果一直这样，请把日志发给开发者。", "CAPTURE_TIMEOUT", canRetry: true);
        }
        catch (CaptureException ex)
        {
            ShowCaptureError(ex);
        }
        catch (Exception ex)
        {
            Logger.Error("翻译当前选区失败", ex);
            ShowError("读取选区失败，请重试。", "UNKNOWN", canRetry: true);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task TranslateClipboardAsync()
    {
        if (_busy)
        {
            Logger.Info("translate clipboard ignored: busy");
            return;
        }

        _busy = true;

        try
        {
            _lastTranslation = null;
            _retryAction = TranslateClipboardAsync;
            var capture = await _capture
                .ReadClipboardAsync(_lifetime.Token)
                .WaitAsync(TimeSpan.FromSeconds(5), _lifetime.Token);
            Logger.Info($"clipboard ok: chars={capture.Text.Length}");
            await TranslateCapturedAsync(capture);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 程序正在退出。
        }
        catch (TimeoutException)
        {
            Logger.Error("读取剪贴板超时（5 秒）");
            ShowError("读取剪贴板超时。请重试；如果一直这样，请把日志发给开发者。", "CLIPBOARD_READ_TIMEOUT", canRetry: true);
        }
        catch (CaptureException ex)
        {
            ShowCaptureError(ex);
        }
        catch (Exception ex)
        {
            Logger.Error("翻译剪贴板失败", ex);
            ShowError("读取剪贴板失败，请重试。", "UNKNOWN", canRetry: true);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task StartOcrAsync()
    {
        if (_busy) return;
        _busy = true;

        try
        {
            _retryAction = StartOcrAsync;
            _overlay?.HideOverlay();

            var picker = new ScreenshotOverlayWindow();
            var accepted = picker.ShowDialog() == true;
            if (!accepted || picker.SelectedBitmap is null)
            {
                return;
            }

            using var bitmap = picker.SelectedBitmap;
            _currentAnchor = ScreenGeometry.GetCursorPoint();
            ShowLoading("（截图 OCR）", _currentAnchor, TranslationDirection.Unknown);

            var text = await _ocr.RecognizeAsync(
                bitmap,
                _settings.OcrLanguage,
                _lifetime.Token);

            if (string.IsNullOrWhiteSpace(text))
            {
                ShowError(
                    "没有从截图中识别出文字。请选择更清晰、包含文字的区域后重试。",
                    "OCR_EMPTY",
                    canRetry: false);
                return;
            }

            _currentText = text;
            _retryAction = StartOcrAsync;
            await TranslateTextCoreAsync(text, _currentAnchor);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 程序正在退出。
        }
        catch (CaptureException ex)
        {
            ShowError(ex.Message, ex.Code, canRetry: false);
        }
        catch (Exception ex)
        {
            Logger.Error("截图 OCR 失败", ex);
            ShowError("截图 OCR 失败，请重试。", "OCR_FAILED", canRetry: true);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task TranslateCapturedAsync(CaptureResult capture)
    {
        _currentText = capture.Text;
        _currentAnchor = capture.AnchorRect;
        await TranslateTextCoreAsync(capture.Text, capture.AnchorRect);
    }

    private async Task TranslateTextCoreAsync(string text, PixelRect? anchor)
    {
        var detection = LanguageDetector.Detect(text);
        var validation = TextValidator.Validate(text);
        if (!validation.IsValid)
        {
            if (!string.IsNullOrWhiteSpace(validation.NormalizedText))
            {
                ShowLoading(validation.NormalizedText, anchor, detection.Direction);
            }
            else
            {
                EnsureOverlay();
                _overlay!.ClearOriginal();
                _overlay.ResetDirection();
            }

            ShowError(
                validation.ErrorMessage ?? "没有可翻译的文本。",
                validation.ErrorCode,
                canRetry: false);
            return;
        }

        _currentText = validation.NormalizedText;
        _currentAnchor = anchor;
        _currentWasFullscreen = ScreenGeometry.IsForegroundFullscreen();
        ShowLoading(validation.NormalizedText, anchor, detection.Direction);

        var key = _secretStore.Load();
        if (string.IsNullOrWhiteSpace(key))
        {
            ShowError(
                "尚未配置 DeepSeek API Key。请打开设置填入 Key 后再试。",
                "NO_API_KEY",
                showSettings: true,
                canRetry: false);
            return;
        }

        try
        {
            var result = await _deepSeek.TranslateAsync(
                validation.NormalizedText,
                key,
                _settings.Model,
                detection,
                _lifetime.Token);

            _lastTranslation = result.Translation;
            _overlay?.ShowResult(result, detection.Direction);

            // 独占全屏可能遮挡置顶窗；把译文放到剪贴板作为兜底。
            if (_currentWasFullscreen)
            {
                try
                {
                    System.Windows.Clipboard.SetText(result.Translation);
                    _tray?.ShowBalloon(
                        "全屏模式提示",
                        "浮层可能被全屏应用遮挡，译文已复制到剪贴板。",
                        ToolTipIcon.Info);
                }
                catch (Exception ex)
                {
                    Logger.Error("全屏模式复制译文失败", ex);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            _overlay?.HideOverlay();
        }
        catch (DeepSeekException ex)
        {
            var canRetry = ex.Code is not ("NO_API_KEY" or "INVALID_API_KEY" or "CONTENT_FILTER");
            var showSettings = ex.Code is "NO_API_KEY" or "INVALID_API_KEY";
            ShowError(ex.Message, ex.Code, showSettings, canRetry);
        }
        catch (Exception ex)
        {
            Logger.Error("DeepSeek 翻译失败", ex);
            ShowError("翻译失败，请重试。", "UNKNOWN", canRetry: true);
        }
        finally
        {
            // Key 字符串不保留在控制器字段中。
            key = null;
        }
    }

    private void ShowCaptureError(CaptureException ex)
    {
        EnsureOverlay();
        _overlay!.ClearOriginal();
        _overlay.ResetDirection();
        var canRetry = ex.Code is not "PASSWORD_FIELD";
        ShowError(ex.Message, ex.Code, canRetry: canRetry);
    }

    private void ShowLoading(
        string original,
        PixelRect? anchor,
        TranslationDirection direction)
    {
        HideSelectionToolbar();
        EnsureOverlay();
        _overlay!.ShowLoading(original, anchor, direction);
    }

    private void ShowError(
        string message,
        string? code,
        bool showSettings = false,
        bool canRetry = true)
    {
        EnsureOverlay();
        _overlay!.ShowError(message, code, showSettings, canRetry);
    }

    private void EnsureOverlay()
    {
        if (_overlay is not null) return;

        _overlay = new OverlayWindow();
        _overlay.RetryRequested += () =>
        {
            var action = _retryAction;
            if (action is not null)
            {
                RunGuarded(action);
            }
        };
        _overlay.SettingsRequested += OpenSettings;
    }

    private void CopyLastTranslation()
    {
        if (string.IsNullOrWhiteSpace(_lastTranslation))
        {
            _tray?.ShowBalloon("还没有译文", $"先选中文字按 {_settings.TranslateSelectionHotkey} 翻译。", ToolTipIcon.Info);
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(_lastTranslation);
            _tray?.ShowBalloon("已复制", "最近一条译文已复制到剪贴板。", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Logger.Error("复制最近译文失败", ex);
            _tray?.ShowBalloon("复制失败", "剪贴板暂时不可用，请稍后重试。", ToolTipIcon.Warning);
        }
    }

    public void ShowSettings()
    {
        OpenSettings();
    }

    /// <summary>
    /// 诊断用：不调用 API，展示一次成功结果浮层。可通过 --demo-overlay 启动。
    /// </summary>
    public void ShowDemoOverlay()
    {
        EnsureOverlay();
        var sample = "DeepSeek 的最新模型为开发者提供了更强的推理能力和更低的调用成本。";
        var detection = LanguageDetector.Detect(sample);
        _overlay!.ShowLoading(sample, ScreenGeometry.GetCursorPoint(), detection.Direction);

        _ = Task.Run(async () =>
        {
            await Task.Delay(700);
            _ = Application.Current.Dispatcher.BeginInvoke(() =>
            {
                _overlay?.ShowResult(new TranslationResult(
                    "DeepSeek’s latest model gives developers stronger reasoning capabilities and lower calling costs.",
                    "DeepSeek V4.1 Flash",
                    "DeepSeek-V4.1-Flash-1017",
                    "deepseek-flash",
                    "deepseek-flash",
                    false,
                    168,
                    842),
                    detection.Direction);
            });
        });
    }

    private void OpenSettings()
    {
        _overlay?.HideOverlay();
        HideSelectionToolbar();

        if (_settingsWindow is not null)
        {
            if (_settingsWindow.IsVisible)
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = null;
        }

        _settingsWindow = new SettingsWindow(_settingsStore, _secretStore, _deepSeek);

        // 只有正在录制快捷键时才暂停全局热键；否则设置窗口开着也能用快捷键。
        _settingsWindow.HotkeyRecordingStarted += () => _hotkeys?.Suspend();
        _settingsWindow.HotkeyRecordingStopped += () => _hotkeys?.Resume();

        _settingsWindow.SettingsSaved += settings =>
        {
            _settings = settings;
            _capture.SelectionHotkey = settings.TranslateSelectionHotkey;
            _capture.ClipboardHotkey = settings.TranslateClipboardHotkey;
            _capture.OcrHotkey = settings.OcrHotkey;
            _tray?.SetAutoStartChecked(settings.AutoStart);
            ApplySelectionIconSetting();

            if (_hotkeys is not null &&
                !_hotkeys.UpdateBindings(settings, out var failures))
            {
                _tray?.ShowBalloon(
                    "快捷键未更新",
                    $"{string.Join("；", failures)}。旧快捷键仍然有效，请重新设置。",
                    ToolTipIcon.Warning);
                _tray?.SetHotkeyLabels(settings, _settings.GameMode);
                return;
            }

            _tray?.SetHotkeyLabels(settings, _settings.GameMode);
        };
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            _hotkeys?.Resume();

            if (_hotkeys is not null && _hotkeys.FailedHotkeys.Count > 0)
            {
                _tray?.ShowBalloon(
                    "快捷键注册失败",
                    $"{string.Join("；", _hotkeys.FailedHotkeys)}。请重新打开设置修改。",
                    ToolTipIcon.Warning);
            }
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OpenHotkeySettings()
    {
        OpenSettings();
        _settingsWindow?.FocusHotkeySettings();
    }

    private void ToggleAutoStart(bool enabled)
    {
        try
        {
            AutoStartService.SetEnabled(enabled);
            _settings.AutoStart = enabled;
            _settingsStore.Save(_settings);
            _tray?.SetAutoStartChecked(enabled);
            _tray?.ShowBalloon(
                enabled ? "已开启开机自启" : "已关闭开机自启",
                enabled ? "下次登录 Windows 时会自动在托盘运行。" : "下次登录 Windows 时不会自动启动。",
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Logger.Error("切换开机自启失败", ex);
            _tray?.SetAutoStartChecked(!enabled);
            _tray?.ShowBalloon("设置失败", $"无法修改开机自启：{ex.Message}", ToolTipIcon.Warning);
        }
    }

    private void ToggleGameMode(bool enabled)
    {
        _settings.GameMode = enabled;

        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            Logger.Error("保存游戏模式失败", ex);
        }

        _hotkeys?.SetEnabled(!enabled);
        ApplySelectionIconSetting();
        _tray?.SetGameMode(enabled);
        _tray?.SetHotkeyLabels(_settings, enabled);

        _tray?.ShowBalloon(
            enabled ? "已进入游戏模式" : "已退出游戏模式",
            enabled
                ? "全局快捷键和划词图标都已暂停，托盘菜单仍可翻译。打游戏时不会再冲突。"
                : "全局快捷键已恢复，划词图标按设置重新启用。",
            enabled ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    private void ApplySelectionIconSetting()
    {
        if (_settings.GameMode)
        {
            StopSelectionMonitor();
        }
        else if (_settings.ShowSelectionIcon)
        {
            StartSelectionMonitor();
        }
        else
        {
            StopSelectionMonitor();
        }
    }

    private void StartSelectionMonitor()
    {
        if (_selectionMonitor is not null) return;

        var monitor = new SelectionMonitor();
        monitor.PointerDown += OnGlobalPointerDown;
        monitor.DragCompleted += OnSelectionDragCompleted;
        monitor.DoubleClicked += OnSelectionDoubleClicked;

        if (!monitor.Install())
        {
            monitor.PointerDown -= OnGlobalPointerDown;
            monitor.DragCompleted -= OnSelectionDragCompleted;
            monitor.DoubleClicked -= OnSelectionDoubleClicked;
            monitor.Dispose();
            _tray?.ShowBalloon(
                "划词图标不可用",
                "无法安装鼠标钩子，选中文字后不会出现小图标。仍可用快捷键翻译。",
                ToolTipIcon.Warning);
            return;
        }

        _selectionMonitor = monitor;
    }

    private void StopSelectionMonitor()
    {
        if (_selectionMonitor is null) return;

        _selectionMonitor.PointerDown -= OnGlobalPointerDown;
        _selectionMonitor.DragCompleted -= OnSelectionDragCompleted;
        _selectionMonitor.DoubleClicked -= OnSelectionDoubleClicked;
        _selectionMonitor.Dispose();
        _selectionMonitor = null;

        HideSelectionToolbar();
    }

    private void OnGlobalPointerDown(PixelRect point)
    {
        if (_selectionToolbar is null || !_selectionToolbar.IsShown) return;

        if (!_toolbarRect.Contains(point.CenterX, point.CenterY))
        {
            HideSelectionToolbar();
        }
    }

    private void OnSelectionDragCompleted(PixelRect point)
    {
        _ = ProbeSelectionAsync(point, allowClipboardOnly: true);
    }

    private void OnSelectionDoubleClicked(PixelRect point)
    {
        _ = ProbeSelectionAsync(point, allowClipboardOnly: false);
    }

    /// <summary>
    /// 鼠标拖选 / 双击选词后判断是否存在文本选区，决定是否显示「译」图标。
    /// 浏览器（Edge/Chrome）里焦点元素常常只是 Pane，所以优先用鼠标坐标命中文本节点。
    /// </summary>
    private async Task ProbeSelectionAsync(PixelRect point, bool allowClipboardOnly)
    {
        var version = ++_selectionProbeVersion;

        try
        {
            if (_settings.GameMode || !_settings.ShowSelectionIcon) return;

            var hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;

            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0 || processId == (uint)Environment.ProcessId) return;

            if (ScreenGeometry.IsForegroundFullscreen()) return;

            var appName = ElevationHelper.GetProcessName(processId);

            // 微信 / Zotero / 部分 PDF 阅读器没有 UIA 文本选区，拖选后直接给图标，
            // 点击时再走「自动 Ctrl+C + 剪贴板」回退。
            if (allowClipboardOnly && AppCompatibility.IsClipboardOnly(appName))
            {
                ShowSelectionToolbar(point);
                return;
            }

            // 浏览器：Chromium 的 UIA 树首次查询要 1~2 秒，图标经常来不及出现。
            // 拖选文字后鼠标仍是 I 型光标，用它即时判断；拖动链接/图片/标签页则是手型或箭头。
            if (allowClipboardOnly &&
                AppCompatibility.IsBrowser(appName) &&
                ScreenGeometry.IsTextCursor())
            {
                ShowSelectionToolbar(point);
                return;
            }

            var timeout = TimeSpan.FromMilliseconds(1200);
            var result = await _selectionReader.TryReadFromPointAsync(
                point.CenterX,
                point.CenterY,
                _lifetime.Token,
                timeout);

            if (version != _selectionProbeVersion) return;

            if (result is null || string.IsNullOrWhiteSpace(result.Text))
            {
                result = await _selectionReader.TryReadAsync(
                    hwnd,
                    processId,
                    _lifetime.Token,
                    timeout,
                    maxDescendants: 60);
            }

            if (version != _selectionProbeVersion) return;
            if (result is null || string.IsNullOrWhiteSpace(result.Text)) return;
            if (_settings.GameMode || !_settings.ShowSelectionIcon) return;

            ShowSelectionToolbar(point);
        }
        catch (OperationCanceledException)
        {
            // 程序退出或新的探测已经接管。
        }
        catch (Exception ex)
        {
            Logger.Error("划词图标探测失败", ex);
        }
    }

    private void ShowSelectionToolbar(PixelRect point)
    {
        if (_settings.GameMode) return;

        if (_selectionToolbar is null)
        {
            var toolbar = new SelectionToolbarWindow();
            toolbar.Clicked += () =>
            {
                HideSelectionToolbar();
                RunGuarded(TranslateSelectionAsync);
            };
            _selectionToolbar = toolbar;
        }

        _toolbarRect = _selectionToolbar.ShowAt(point.CenterX, point.CenterY);
    }

    private void HideSelectionToolbar()
    {
        _selectionToolbar?.HideToolbar();
    }

    private async Task RunStartupUpdateCheckAsync()
    {
        try
        {
            // 启动后等一会儿再查，避免和启动动画/快捷键注册抢资源
            await Task.Delay(TimeSpan.FromSeconds(25), _lifetime.Token);
            await CheckForUpdatesAsync(silent: true);
        }
        catch (OperationCanceledException)
        {
            // 程序退出中
        }
        catch (Exception ex)
        {
            Logger.Error("启动时检查更新失败", ex);
        }
    }

    private async Task CheckForUpdatesAsync(bool silent)
    {
        if (_updateWindow is not null)
        {
            _updateWindow.Activate();
            return;
        }

        var result = await _updates.CheckAsync(_settings.UpdateFeed, _lifetime.Token);

        if (result.Update is null)
        {
            if (silent)
            {
                if (result.Error is not null)
                {
                    Logger.Error($"自动检查更新失败：{result.Error}");
                }

                return;
            }

            _tray?.ShowBalloon(
                result.Error is null ? "已是最新版本" : "检查更新失败",
                result.Error ?? $"当前版本 {result.CurrentVersion} 已是最新。",
                result.Error is null ? ToolTipIcon.Info : ToolTipIcon.Warning);
            return;
        }

        // 用户点过“稍后”的版本，自动检查时不再提示（强制更新除外）
        if (silent &&
            !result.Update.Mandatory &&
            string.Equals(result.Update.Version, _settings.SkippedVersion, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ShowUpdateWindow(result);
    }

    private void ShowUpdateWindow(UpdateCheckResult result)
    {
        if (result.Update is null) return;

        _overlay?.HideOverlay();
        HideSelectionToolbar();

        var window = new UpdateWindow(result.Update, result.CurrentVersion, _updates);
        window.Skipped += version =>
        {
            _settings.SkippedVersion = version;
            try
            {
                _settingsStore.Save(_settings);
            }
            catch (Exception ex)
            {
                Logger.Error("保存“稍后更新”的版本失败", ex);
            }
        };
        window.Closed += (_, _) => _updateWindow = null;

        _updateWindow = window;
        window.Show();
        window.Activate();
    }

    private void RunGuarded(Func<Task> action)
    {
        _ = RunGuardedAsync(action);
    }

    private async Task RunGuardedAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 退出中。
        }
        catch (Exception ex)
        {
            Logger.Error("后台任务异常", ex);
            _tray?.ShowBalloon("操作失败", "发生未预期错误，请查看日志。", ToolTipIcon.Error);
        }
    }

    private void Exit()
    {
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _lifetime.Cancel();
        }
        catch
        {
            // ignore
        }

        try
        {
            _hotkeys?.Dispose();
            _hotkeys = null;
        }
        catch (Exception ex)
        {
            Logger.Error("释放快捷键失败", ex);
        }

        try
        {
            StopSelectionMonitor();
            _selectionToolbar?.CloseToolbar();
            _selectionToolbar = null;
        }
        catch (Exception ex)
        {
            Logger.Error("释放划词图标失败", ex);
        }

        try
        {
            _updateWindow?.Close();
            _updateWindow = null;
        }
        catch
        {
            // ignore
        }

        try
        {
            _settingsWindow?.Close();
            _overlay?.ForceClose();
        }
        catch
        {
            // ignore
        }

        try
        {
            _tray?.Dispose();
            _tray = null;
        }
        catch (Exception ex)
        {
            Logger.Error("释放托盘失败", ex);
        }

        _deepSeek.Dispose();
        _lifetime.Dispose();
    }
}
