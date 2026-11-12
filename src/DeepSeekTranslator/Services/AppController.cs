using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using DeepSeekTranslator.Interop;
using DeepSeekTranslator.Models;
using DeepSeekTranslator.Views;
using Application = System.Windows.Application;

namespace DeepSeekTranslator.Services;

public sealed partial class AppController : IDisposable
{
    private readonly SettingsStore _settingsStore = new();
    private readonly SecretStore _secretStore = new();
    private readonly DeepSeekClient _deepSeek = new();
    private readonly CaptureCoordinator _capture = new();
    private readonly TranslationCache _cache = new();
    private readonly OcrService _ocr = new();
    private readonly UiaSelectionReader _selectionReader = new();
    private readonly UpdateService _updates = new();
    private readonly CancellationTokenSource _lifetime = new();

    private AppSettings _settings = new();
    private TrayService? _tray;
    private HotkeyService? _hotkeys;
    private OverlayWindow? _overlay;
    private SettingsWindow? _settingsWindow;
    private DiagnosticsWindow? _diagnosticsWindow;
    private UpdateWindow? _updateWindow;
    private SelectionMonitor? _selectionMonitor;
    private SelectionToolbarWindow? _selectionToolbar;
    private PixelRect _toolbarRect;
    private int _selectionProbeVersion;
    private long _lastAccessibilityWarmUpTicks;
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
        _capture.ForceClipboardApps = _settings.ForceClipboardApps;

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
        _tray.DiagnosticsRequested += OpenDiagnostics;
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
                ShowLoading(MathText.Normalize(validation.NormalizedText), anchor, detection.Direction);
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
        ShowLoading(MathText.Normalize(validation.NormalizedText), anchor, detection.Direction);

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

        var glossaryEntries = Glossary.Parse(_settings.Glossary);
        var glossaryPrompt = Glossary.BuildPrompt(glossaryEntries);
        var cacheKey = TranslationCache.BuildKey(
            validation.NormalizedText,
            detection,
            _settings.Model,
            Glossary.Fingerprint(glossaryEntries));

        if (_settings.EnableCache && _cache.TryGet(cacheKey, out var cached))
        {
            Logger.Info("translate: cache hit");
            _lastTranslation = cached.Translation;
            _overlay?.ShowResult(cached, detection.Direction);
            return;
        }

        try
        {
            var result = _settings.EnableStreaming
                ? await TranslateStreamingAsync(validation.NormalizedText, key, detection, glossaryPrompt, anchor)
                : await _deepSeek.TranslateAsync(
                    validation.NormalizedText,
                    key,
                    _settings.Model,
                    detection,
                    _lifetime.Token,
                    glossaryPrompt);

            // LaTeX 公式转成可读的 Unicode（Γ₁₁ 这类），避免译文里出现 \boldsymbol 等命令。
            result = result with { Translation = MathText.Normalize(result.Translation) };

            _lastTranslation = result.Translation;
            _overlay?.ShowResult(result, detection.Direction);

            if (_settings.EnableCache)
            {
                _cache.Set(cacheKey, result);
            }

            // 独占全屏可能遮挡置顶窗；把译文放到剪贴板作为兜底。
            if (_currentWasFullscreen)
            {
                var copied = await ClipboardWriter.TrySetTextAsync(result.Translation);
                if (copied)
                {
                    _tray?.ShowBalloon(
                        "全屏模式提示",
                        "浮层可能被全屏应用遮挡，译文已复制到剪贴板。",
                        ToolTipIcon.Info);
                }
                else
                {
                    Logger.Error("全屏模式复制译文失败（剪贴板被占用）");
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

    /// <summary>
    /// 流式翻译；如果流式通道不可用（网络中断、SSE 解析失败等），自动回退为整段请求。
    /// </summary>
    private async Task<TranslationResult> TranslateStreamingAsync(
        string text,
        string apiKey,
        LanguageDetection detection,
        string glossaryPrompt,
        PixelRect? anchor)
    {
        var dispatcher = Application.Current?.Dispatcher;
        var overlay = _overlay;
        var streamEpoch = overlay?.CurrentStreamEpoch ?? 0;
        var lastTick = 0L;

        try
        {
            return await _deepSeek.TranslateStreamAsync(
                text,
                apiKey,
                _settings.Model,
                detection,
                glossaryPrompt,
                partial =>
                {
                    var now = Environment.TickCount64;
                    if (now - lastTick < 90) return;
                    lastTick = now;

                    dispatcher?.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                    {
                        if (overlay is { IsVisible: true })
                        {
                            overlay.ShowStreaming(
                                MathText.Normalize(partial),
                                detection.Direction,
                                streamEpoch);
                        }
                    }));
                },
                _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DeepSeekException ex) when (ex.Code is "NETWORK_ERROR" or "BAD_RESPONSE" or "TIMEOUT")
        {
            Logger.Error($"流式翻译失败（{ex.Code}），回退为整段请求");
        }
        catch (Exception ex) when (ex is not DeepSeekException)
        {
            Logger.Error("流式翻译异常，回退为整段请求", ex);
        }

        ShowLoading(text, anchor, detection.Direction);
        return await _deepSeek.TranslateAsync(
            text,
            apiKey,
            _settings.Model,
            detection,
            _lifetime.Token,
            glossaryPrompt);
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

    private void SaveOverlaySettings()
    {
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            Logger.Error("保存浮层设置失败", ex);
        }
    }

    private void EnsureOverlay()
    {
        if (_overlay is not null) return;

        (int X, int Y)? pinnedPosition = null;
        if (_settings.OverlayPinnedLeft is int pinnedLeft && _settings.OverlayPinnedTop is int pinnedTop)
        {
            pinnedPosition = (pinnedLeft, pinnedTop);
        }

        _overlay = new OverlayWindow
        {
            PreferredWidth = _settings.OverlayWidth > 0 ? _settings.OverlayWidth : 440,
            PreferredFontSize = _settings.OverlayFontSize,
            PreferredMaxContentHeight = _settings.OverlayMaxContentHeight,
            PreferredSpeechRate = _settings.SpeechRate,
            AlignmentEnabled = _settings.EnableAlignment,
            PreferredPinnedPosition = pinnedPosition,
            PinOverlay = _settings.PinOverlay
        };
        _overlay.WidthChanged += width =>
        {
            _settings.OverlayWidth = width;
            SaveOverlaySettings();
        };
        _overlay.FontSizeChanged += size =>
        {
            _settings.OverlayFontSize = size;
            SaveOverlaySettings();
        };
        _overlay.MaxContentHeightChanged += height =>
        {
            _settings.OverlayMaxContentHeight = height;
            SaveOverlaySettings();
        };
        _overlay.RetryRequested += () =>
        {
            var action = _retryAction;
            if (action is not null)
            {
                RunGuarded(action);
            }
        };
        _overlay.SettingsRequested += OpenSettings;
        _overlay.ExplainRequested += (word, context) => RunGuarded(() => ExplainWordAsync(word, context));
        _overlay.AlignmentRequested += (request, epoch) => _ = AlignTranslationSelectionAsync(request, epoch);
        _overlay.AlignmentCancelled += CancelAlignment;
        _overlay.AlignmentEnabledChanged += enabled =>
        {
            _settings.EnableAlignment = enabled;
            if (!enabled) _alignmentService?.Clear();
            SaveOverlaySettings();
        };
        _overlay.PinnedPositionChanged += (left, top) =>
        {
            _settings.OverlayPinnedLeft = left;
            _settings.OverlayPinnedTop = top;
            SaveOverlaySettings();
        };
        _overlay.PinOverlayChanged += enabled =>
        {
            _settings.PinOverlay = enabled;
            SaveOverlaySettings();
        };
    }

    /// <summary>单词详解：音标、词性、释义、搭配和本句含义。</summary>
    private async Task ExplainWordAsync(string word, string? context)
    {
        var key = _secretStore.Load();
        if (string.IsNullOrWhiteSpace(key))
        {
            _overlay?.ShowWordDetailError(word, "尚未配置 API Key。请打开设置填入 Key 后再试。");
            return;
        }

        try
        {
            var result = await _deepSeek.ExplainWordAsync(
                word,
                context,
                key,
                _settings.Model,
                _lifetime.Token);
            _overlay?.ShowWordDetail(word, result.Translation);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 程序退出中。
        }
        catch (DeepSeekException ex)
        {
            _overlay?.ShowWordDetailError(word, ex.Message);
        }
        catch (Exception ex)
        {
            Logger.Error("单词详解失败", ex);
            _overlay?.ShowWordDetailError(word, "查询失败，请重试。");
        }
        finally
        {
            key = null;
        }
    }

    private void CopyLastTranslation()
    {
        _ = CopyLastTranslationAsync();
    }

    private async Task CopyLastTranslationAsync()
    {
        if (string.IsNullOrWhiteSpace(_lastTranslation))
        {
            _tray?.ShowBalloon("还没有译文", $"先选中文字按 {_settings.TranslateSelectionHotkey} 翻译。", ToolTipIcon.Info);
            return;
        }

        var ok = await ClipboardWriter.TrySetTextAsync(_lastTranslation);
        _tray?.ShowBalloon(
            ok ? "已复制" : "复制失败",
            ok ? "最近一条译文已复制到剪贴板。" : "剪贴板暂时被其他程序占用，请稍后重试。",
            ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
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
        // 从托盘/浮层进入设置时保留固定位置，避免下一次翻译跳回选区。
        _overlay?.HideOverlay(releasePinnedPosition: false);
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
            if (_overlay is not null)
            {
                _overlay.PreferredSpeechRate = settings.SpeechRate;
                _overlay.AlignmentEnabled = settings.EnableAlignment;
                _overlay.PinOverlay = settings.PinOverlay;
            }
            _capture.SelectionHotkey = settings.TranslateSelectionHotkey;
            _capture.ClipboardHotkey = settings.TranslateClipboardHotkey;
            _capture.OcrHotkey = settings.OcrHotkey;
            _capture.ForceClipboardApps = settings.ForceClipboardApps;
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

    private void OpenDiagnostics()
    {
        if (_diagnosticsWindow is not null)
        {
            if (_diagnosticsWindow.IsVisible)
            {
                _diagnosticsWindow.Activate();
                return;
            }

            _diagnosticsWindow = null;
        }

        _diagnosticsWindow = new DiagnosticsWindow(
            BuildDiagnosticsReport,
            TestCaptureAsync,
            OpenLogDirectory,
            ClearTranslationCache);
        _diagnosticsWindow.Closed += (_, _) => _diagnosticsWindow = null;
        _diagnosticsWindow.Show();
        _diagnosticsWindow.Activate();
    }

    private string BuildDiagnosticsReport()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"DeepSeek 划词翻译 {AppInfo.Version}");
        builder.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"系统：{RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}");
        builder.AppendLine($"运行时：.NET {Environment.Version}");
        builder.AppendLine(
            $"进程：{Environment.ProcessId}，管理员：{(ElevationHelper.IsCurrentProcessElevated() ? "是" : "否")}");
        builder.AppendLine($"数据目录：{AppPaths.DataDirectory}");
        builder.AppendLine(
            $"设置文件：{AppPaths.SettingsFile}（{(File.Exists(AppPaths.SettingsFile) ? "存在" : "缺失")}）");
        builder.AppendLine($"API Key：{(_secretStore.HasSecret() ? _secretStore.MaskedKey() : "未配置")}");
        builder.AppendLine($"模型：{_settings.Model}");
        builder.AppendLine($"浮层宽度：{_settings.OverlayWidth:0} DIP，字号 {_settings.OverlayFontSize:0}，内容最大高度 {_settings.OverlayMaxContentHeight:0} DIP");
        builder.AppendLine(
            $"强制剪贴板：{AppCompatibility.ParseUserApps(_settings.ForceClipboardApps).Count} 个（{_settings.ForceClipboardApps.Replace("\r", " ").Replace("\n", " ")}）");
        builder.AppendLine(
            $"翻译缓存：{_cache.Count} 条（{(_settings.EnableCache ? "启用" : "关闭")}）");
        builder.AppendLine($"流式输出：{(_settings.EnableStreaming ? "启用" : "关闭")}");
        builder.AppendLine($"术语表：{Glossary.Parse(_settings.Glossary).Count} 条");
        builder.AppendLine($"游戏模式：{(_settings.GameMode ? "开" : "关")}");
        builder.AppendLine();

        builder.AppendLine("快捷键：");
        AppendHotkeyLine(builder, "翻译当前选区", _settings.TranslateSelectionHotkey);
        AppendHotkeyLine(builder, "翻译剪贴板", _settings.TranslateClipboardHotkey);
        AppendHotkeyLine(builder, "截图 OCR", _settings.OcrHotkey);
        AppendHotkeyLine(builder, "复制最近译文", _settings.CopyLastHotkey);
        if (_hotkeys?.FailedHotkeys is { Count: > 0 } failures)
        {
            builder.AppendLine($"  注册失败：{string.Join("；", failures)}");
        }

        builder.AppendLine();

        var hwnd = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(hwnd, out var foregroundPid);
        var foregroundName = foregroundPid == 0
            ? "未知"
            : ElevationHelper.GetProcessName(foregroundPid);
        var foregroundElevated = foregroundPid != 0 &&
                                 ElevationHelper.IsProcessElevated(foregroundPid);
        var foregroundTitle = NativeMethods.GetWindowTitle(hwnd);
        var foregroundPath = foregroundPid == 0
            ? ""
            : ElevationHelper.GetProcessPath(foregroundPid);
        builder.AppendLine($"前台窗口：{foregroundName}{(foregroundElevated ? "（管理员）" : "")}");
        builder.AppendLine($"窗口标题：{foregroundTitle}");
        builder.AppendLine($"可执行文件：{foregroundPath}");
        builder.AppendLine(
            $"取词规则：{AppCompatibility.Describe(foregroundName, foregroundTitle, _settings.ForceClipboardApps, foregroundPath)}");
        builder.AppendLine($"剪贴板：{DescribeClipboard()}");
        builder.AppendLine(
            $"OCR 语言：{(OcrService.GetAvailableLanguages() is { Count: > 0 } languages ? string.Join(", ", languages) : "未安装")}");
        builder.AppendLine();

        builder.AppendLine("最近日志：");
        foreach (var line in ReadLogTail(12))
        {
            builder.AppendLine("  " + line);
        }

        return builder.ToString();
    }

    private void AppendHotkeyLine(StringBuilder builder, string label, string hotkey)
    {
        var failed = _hotkeys?.FailedHotkeys
            .Any(item => item.StartsWith(label, StringComparison.Ordinal)) == true;
        builder.AppendLine($"  {label}：{hotkey}{(failed ? "（注册失败）" : "")}");
    }

    private static string DescribeClipboard()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText())
            {
                return "没有文本";
            }

            return $"有文本（{System.Windows.Clipboard.GetText().Length} 字符）";
        }
        catch (Exception ex)
        {
            return $"读取失败（{ex.GetType().Name}）";
        }
    }

    private static IReadOnlyList<string> ReadLogTail(int count)
    {
        try
        {
            var path = Path.Combine(AppPaths.LogDirectory, "app.log");
            if (!File.Exists(path)) return Array.Empty<string>();
            return File.ReadLines(path).TakeLast(count).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void OpenLogDirectory()
    {
        AppPaths.EnsureDirectories();
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{AppPaths.LogDirectory}\"",
            UseShellExecute = true
        });
    }

    private void ClearTranslationCache()
    {
        _cache.Clear();
        _alignmentService?.Clear();
        CancelAlignment();
        Logger.Info("translate: cache cleared");
    }

    private async Task<(bool Ok, string Message)> TestCaptureAsync()
    {
        try
        {
            var capture = await _capture
                .CaptureSelectionAsync(_lifetime.Token)
                .WaitAsync(TimeSpan.FromSeconds(15), _lifetime.Token);

            return (true,
                $"取词成功：{capture.AppName} / {capture.Method} / {capture.Text.Length} 字符。");
        }
        catch (CaptureException ex)
        {
            return (false, $"取词失败：{ex.Message}");
        }
        catch (TimeoutException)
        {
            return (false, "取词超时（15 秒），请重试。");
        }
        catch (Exception ex)
        {
            Logger.Error("诊断取词失败", ex);
            return (false, $"取词异常：{ex.Message}");
        }
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
        WarmUpForegroundAccessibility();

        if (_selectionToolbar is null || !_selectionToolbar.IsShown) return;

        if (!_toolbarRect.Contains(point.CenterX, point.CenterY))
        {
            HideSelectionToolbar();
        }
    }

    /// <summary>
    /// 浏览器（尤其 Edge PDF）的 UIA 树是按需构建的，第一次查询要 1~3 秒。
    /// 鼠标按下时先在后台预热一次，松手后的探测就能及时命中。
    /// </summary>
    private void WarmUpForegroundAccessibility()
    {
        if (_settings.GameMode || !_settings.ShowSelectionIcon) return;
        if (Environment.TickCount64 - _lastAccessibilityWarmUpTicks < 1500) return;

        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0 || processId == (uint)Environment.ProcessId) return;

        var appName = ElevationHelper.GetProcessName(processId);
        if (!AppCompatibility.IsBrowser(appName)) return;

        _lastAccessibilityWarmUpTicks = Environment.TickCount64;
        _ = Task.Run(() => _selectionReader.WarmUp(hwnd));
    }

    private void OnSelectionDragCompleted(PixelRect point)
    {
        _ = ProbeSelectionAsync(
            point,
            allowClipboardOnly: true,
            startedOnText: _selectionMonitor?.LastPointerDownWasTextCursor ?? false);
    }

    private void OnSelectionDoubleClicked(PixelRect point)
    {
        _ = ProbeSelectionAsync(point, allowClipboardOnly: false, startedOnText: false);
    }

    /// <summary>
    /// 鼠标拖选 / 双击选词后判断是否存在文本选区，决定是否显示「译」图标。
    /// 浏览器（Edge/Chrome）里焦点元素常常只是 Pane，所以优先用鼠标坐标命中文本节点。
    /// </summary>
    private async Task ProbeSelectionAsync(
        PixelRect point,
        bool allowClipboardOnly,
        bool startedOnText = false)
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
            var windowTitle = NativeMethods.GetWindowTitle(hwnd);
            var processPath = ElevationHelper.GetProcessPath(processId);

            // 微信 / Zotero / WPS / 小绿鲸 / Visio / 部分 PDF 阅读器没有可靠的 UIA 文本选区，
            // 拖选后直接给图标，点击时再走「自动 Ctrl+C + 剪贴板」回退。
            if (allowClipboardOnly &&
                AppCompatibility.IsClipboardOnly(appName, windowTitle, _settings.ForceClipboardApps, processPath))
            {
                ShowSelectionToolbar(point);
                return;
            }

            // 浏览器：Chromium 的 UIA 树首次查询要 1~2 秒，图标经常来不及出现。
            // 拖选/双击选词后鼠标仍是 I 型光标，用它即时判断；拖动链接/图片/标签页则是手型或箭头。
            // 再加“按下时在文字上”的补充：从前往后拖选时松手可能落在行尾空白。
            if (AppCompatibility.IsBrowser(appName) &&
                (ScreenGeometry.IsTextCursor() || startedOnText))
            {
                ShowSelectionToolbar(point);
                return;
            }

            var timeout = TimeSpan.FromMilliseconds(
                AppCompatibility.IsBrowser(appName) ? 2600 : 1200);
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
            _diagnosticsWindow?.Close();
            _diagnosticsWindow = null;
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
        _cache.Dispose();
        _lifetime.Dispose();
    }
}
