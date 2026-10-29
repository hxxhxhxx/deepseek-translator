using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DeepSeekTranslator.Interop;
using DeepSeekTranslator.Models;
using DeepSeekTranslator.Services;
using DeepSeekTranslator.Views;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace DeepSeekTranslator;

public partial class App : Application
{
    private const string MutexName = @"Local\DeepSeekTranslator.SingleInstance";
    private const string ShowSettingsEventName = @"Local\DeepSeekTranslator.ShowSettings";
    private Mutex? _mutex;
    private EventWaitHandle? _showSettingsEvent;
    private AppController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 诊断模式不占用单实例 Mutex，便于在旧版本仍运行时验证新构建。
        if (e.Args.Any(arg => string.Equals(arg, "--ocr-selftest", StringComparison.OrdinalIgnoreCase)))
        {
            _ = RunOcrSelfTestAsync();
            return;
        }

        if (e.Args.Any(arg => string.Equals(arg, "--ui-preview", StringComparison.OrdinalIgnoreCase)))
        {
            _ = RunUiPreviewAsync();
            return;
        }

        if (e.Args.Any(arg => string.Equals(arg, "--check-update", StringComparison.OrdinalIgnoreCase)))
        {
            var withDownload = e.Args.Any(
                arg => string.Equals(arg, "--download-update", StringComparison.OrdinalIgnoreCase));
            _ = RunUpdateCheckAsync(withDownload);
            return;
        }

        if (e.Args.Length >= 2 &&
            string.Equals(e.Args[0], "--icon-at", StringComparison.OrdinalIgnoreCase))
        {
            _ = RunIconAtAsync(e.Args.Skip(1).ToArray());
            return;
        }

        if (e.Args.Length >= 2 &&
            string.Equals(e.Args[0], "--overlay-at", StringComparison.OrdinalIgnoreCase))
        {
            _ = RunOverlayAtAsync(e.Args.Skip(1).ToArray());
            return;
        }

        _mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // 第二次启动：通知已运行的实例打开设置，而不是只弹“已在运行”。
            try
            {
                using var showEvent = EventWaitHandle.OpenExisting(ShowSettingsEventName);
                showEvent.Set();
            }
            catch
            {
                MessageBox.Show(
                    "DeepSeek 划词翻译已经在运行，请查看系统托盘，或从任务管理器结束旧进程后重试。",
                    "DeepSeek 划词翻译",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _showSettingsEvent = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            ShowSettingsEventName);

        try
        {
            _controller = new AppController();
            _controller.Initialize();
            StartShowSettingsListener();

            var isAutoStart = e.Args.Any(
                arg => string.Equals(arg, "--autostart", StringComparison.OrdinalIgnoreCase));
            var wantsDemo = e.Args.Any(
                arg => string.Equals(arg, "--demo-overlay", StringComparison.OrdinalIgnoreCase));

            if (wantsDemo)
            {
                _controller.ShowDemoOverlay();
            }
            else if (!isAutoStart)
            {
                // 手动启动时直接打开设置，避免用户以为“程序没打开”。
                _controller.ShowSettings();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("程序启动失败", ex);
            MessageBox.Show(
                $"程序启动失败：{ex.Message}\n\n日志：{AppPaths.LogDirectory}",
                "DeepSeek 划词翻译",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("UI 线程未处理异常", e.Exception);
        MessageBox.Show(
            $"发生未处理异常：{e.Exception.Message}\n\n详情已写入日志。",
            "DeepSeek 划词翻译",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.Error("后台任务未处理异常", e.Exception);
        e.SetObserved();
    }

    private async Task RunOcrSelfTestAsync()
    {
        try
        {
            await OcrSelfTest.RunAsync();
        }
        finally
        {
            Shutdown();
        }
    }

    private async Task RunUiPreviewAsync()
    {
        try
        {
            await UiPreview.RunAsync();
        }
        catch (Exception ex)
        {
            Logger.Error("生成 UI 预览失败", ex);
        }
        finally
        {
            Shutdown();
        }
    }

    /// <summary>诊断：检查更新（可加 --download-update 一并测试下载与校验），结果写日志后退出。</summary>
    private async Task RunUpdateCheckAsync(bool withDownload)
    {
        try
        {
            var settings = new SettingsStore().Load();
            var service = new UpdateService();

            Logger.Info($"update-check: feed={settings.UpdateFeed}");
            var result = await service.CheckAsync(settings.UpdateFeed, CancellationToken.None);
            Logger.Info(
                $"update-check: current={result.CurrentVersion} latest={result.Update?.Version ?? "-"} isLatest={result.IsLatest} error={result.Error ?? "-"}");

            if (withDownload && result.Update is not null)
            {
                var file = await service.DownloadAsync(result.Update, null, CancellationToken.None);
                Logger.Info($"update-check: downloaded+verified {file} ({new FileInfo(file).Length} bytes)");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("update-check 诊断失败", ex);
        }
        finally
        {
            Shutdown();
        }
    }

    /// <summary>诊断：把浮层定位到指定选区坐标并记录实际落点（--overlay-at x,y x2,y2 ...）。</summary>
    private async Task RunOverlayAtAsync(string[] arguments)
    {
        try
        {
            var overlay = new OverlayWindow();

            foreach (var argument in arguments)
            {
                var parts = argument.Split(',');
                var x = int.Parse(parts[0]);
                var y = int.Parse(parts[1]);
                var anchor = new PixelRect(x, y, x + 300, y + 20);

                var monitor = ScreenGeometry.GetMonitorForPoint(x, y);
                Logger.Info($"overlay-at: point=({x},{y}) monitor={monitor.Bounds} scale={monitor.Scale}");

                overlay.ShowLoading("Diagnostic sample text.", anchor, TranslationDirection.EnglishToChinese);

                await Task.Delay(500);
                var handle = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
                NativeMethods.GetWindowRect(handle, out var actual1);
                Logger.Info(
                    $"overlay-at: after loading wpf=({overlay.Left:0.#},{overlay.Top:0.#}) dpi={WindowPos.CurrentDpiScale(overlay):0.##} actual=({actual1.Left},{actual1.Top},{actual1.Right},{actual1.Bottom})");

                overlay.ShowResult(
                    new TranslationResult("诊断译文。", "DeepSeek V4.1 Flash", "v", "deepseek-flash", "deepseek-flash", false, 10, 100),
                    TranslationDirection.EnglishToChinese);

                await Task.Delay(700);
                NativeMethods.GetWindowRect(handle, out var actual2);
                Logger.Info(
                    $"overlay-at: after result wpf=({overlay.Left:0.#},{overlay.Top:0.#}) dpi={WindowPos.CurrentDpiScale(overlay):0.#} actual=({actual2.Left},{actual2.Top},{actual2.Right},{actual2.Bottom})");

                // 不隐藏：模拟“上一次翻译的浮层还开着，接着在另一块屏上翻译”的真实场景。
                await Task.Delay(400);
            }

            overlay.ForceClose();
        }
        catch (Exception ex)
        {
            Logger.Error("overlay-at 诊断失败", ex);
        }
        finally
        {
            Shutdown();
        }
    }
    private async Task RunIconAtAsync(string[] arguments)
    {
        try
        {
            var window = new SelectionToolbarWindow();

            foreach (var argument in arguments)
            {
                var parts = argument.Split(',');
                var x = int.Parse(parts[0]);
                var y = int.Parse(parts[1]);

                var monitor = ScreenGeometry.GetMonitorForPoint(x, y);
                Logger.Info(
                    $"icon-at: point=({x},{y}) monitor={monitor.Bounds} work={monitor.WorkArea} scale={monitor.Scale}");

                var rect = window.ShowAt(x, y);
                await Task.Delay(600);

                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                NativeMethods.GetWindowRect(handle, out var actual);
                Logger.Info(
                    $"icon-at: returned={rect} wpf=({window.Left:0.#},{window.Top:0.#}) actual=({actual.Left},{actual.Top},{actual.Right},{actual.Bottom})");

                window.HideToolbar();
                await Task.Delay(400);
            }

            window.CloseToolbar();
        }
        catch (Exception ex)
        {
            Logger.Error("icon-at 诊断失败", ex);
        }
        finally
        {
            Shutdown();
        }
    }

    private void StartShowSettingsListener()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (_showSettingsEvent is null || !_showSettingsEvent.WaitOne()) return;
                    Dispatcher.BeginInvoke(() => _controller?.ShowSettings());
                }
                catch
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "DeepSeekTranslator.ShowSettings"
        };
        thread.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _controller?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Error("程序退出清理失败", ex);
        }

        try
        {
            _showSettingsEvent?.Dispose();
        }
        catch
        {
            // ignore
        }

        try
        {
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // Mutex 可能未持有。
        }

        _mutex?.Dispose();
        base.OnExit(e);
    }
}
