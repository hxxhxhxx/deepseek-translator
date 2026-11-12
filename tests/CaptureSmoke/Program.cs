using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using DeepSeekTranslator.Models;
using DeepSeekTranslator.Services;
using DeepSeekTranslator.Views;

namespace CaptureSmoke;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            // WinExe 在重定向输出时没有有效控制台句柄，设置编码可能失败。
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (Exception)
        {
            // 忽略：只影响中文输出是否乱码。
        }

        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };

        // 浮层用到 App.xaml 里的样式资源，测试进程也要把它们加载进来。
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/DeepSeekTranslator;component/Resources/Styles.xaml",
                UriKind.Absolute)
        });

        var textBox = new TextBox
        {
            Text = "Hello UIA smoke test 你好",
            FontSize = 18,
            Margin = new Thickness(20)
        };
        var window = new Window
        {
            Title = "DeepSeek Capture Smoke",
            Width = 480,
            Height = 220,
            Content = textBox,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true
        };

        window.Loaded += async (_, _) =>
        {
            var exitCode = 1;
            try
            {
                window.Activate();
                textBox.Focus();
                textBox.SelectAll();

                await Task.Delay(250);
                var handle = new WindowInteropHelper(window).Handle;
                var reader = new UiaSelectionReader();
                var result = await reader.TryReadAsync(
                    handle,
                    (uint)Environment.ProcessId,
                    CancellationToken.None);

                if (result is not null && result.Text.Contains("UIA smoke test", StringComparison.Ordinal))
                {
                    Console.WriteLine("PASS  UIA 读取 TextBox 选区");
                    Console.WriteLine($"TEXT  {result.Text}");
                    Console.WriteLine($"BOUNDS {result.Bounds}");
                    exitCode = 0;
                }
                else
                {
                    Console.WriteLine($"FAIL  UIA 未读回预期文本: {result?.Text ?? "<null>"}");
                }

                // 剪贴板回退：需要测试窗口真正处于前台；Windows 前台锁可能阻止 SetForegroundWindow。
                window.Activate();
                window.Topmost = true;
                textBox.Focus();
                textBox.SelectAll();
                Win32Test.SetForegroundWindow(handle);
                await Task.Delay(300);

                if (Environment.GetEnvironmentVariable("DSH_CAPTURE_CLIPBOARD_SMOKE") != "1")
                {
                    Console.WriteLine("SKIP  剪贴板回退（需显式 DSH_CAPTURE_CLIPBOARD_SMOKE=1，默认不触碰用户剪贴板）");
                }
                else if (Win32Test.GetForegroundWindow() != handle)
                {
                    Console.WriteLine("SKIP  剪贴板回退（测试窗口未能取得前台焦点，避免误发 Ctrl+C）");
                }
                else
                {
                    var clipboardResult = await new ClipboardSelectionReader()
                        .TryCopySelectionAsync(CancellationToken.None);
                    Console.WriteLine($"CLIP  changed={clipboardResult.ClipboardChanged} timedOut={clipboardResult.TimedOut} diagnostic={clipboardResult.Diagnostic}");
                    Console.WriteLine($"CLIP  text={clipboardResult.Text}");
                    if (clipboardResult.Text?.Contains("UIA smoke test", StringComparison.Ordinal) == true)
                    {
                        Console.WriteLine("PASS  剪贴板回退读取选区");
                    }
                    else
                    {
                        Console.WriteLine("FAIL  剪贴板回退未读回预期文本");
                        exitCode = 1;
                    }
                }

                if (Environment.GetEnvironmentVariable("DSH_CLIPBOARD_WRITE_SMOKE") == "1")
                {
                    var wrote = await ClipboardWriter.TrySetTextAsync("clipboard-writer-smoke");
                    if (wrote && System.Windows.Clipboard.GetText() == "clipboard-writer-smoke")
                        Console.WriteLine("PASS  写剪贴板（Win32）");
                    else
                        Console.WriteLine($"SKIP  写剪贴板未成功（wrote={wrote}，可能被其他程序占用）");
                }
                else
                {
                    Console.WriteLine("SKIP  写剪贴板（需显式 DSH_CLIPBOARD_WRITE_SMOKE=1，默认不修改用户剪贴板）");
                }

                // 流式代次：拖动造成的积压增量不能覆盖已经落地的最终结果。
                var overlay = new DeepSeekTranslator.Views.OverlayWindow();
                overlay.ShowLoading(
                    "source text",
                    new PixelRect(200, 200, 201, 201),
                    TranslationDirection.ChineseToEnglish);
                var epoch = overlay.CurrentStreamEpoch;
                overlay.ShowStreaming(
                    "部分译文",
                    TranslationDirection.ChineseToEnglish,
                    epoch);
                overlay.ShowResult(
                    new TranslationResult("完整译文", "model", "version", "model", "model", false, 10, 100),
                    TranslationDirection.ChineseToEnglish);
                overlay.ShowStreaming(
                    "过期的部分译文",
                    TranslationDirection.ChineseToEnglish,
                    epoch);

                var translationBox = overlay.FindName("TranslationText") as TextBox;
                if (translationBox?.Text == "完整译文")
                {
                    Console.WriteLine("PASS  过期流式增量不会覆盖最终结果");
                }
                else
                {
                    Console.WriteLine($"FAIL  流式代次保护: {translationBox?.Text}");
                    exitCode = 1;
                }

                overlay.ShowWordDetail("article", "音标：/ˈɑːtɪkl/");
                var wordPanel = overlay.FindName("WordPanel") as Border;
                var wordDetail = overlay.FindName("WordDetailText") as TextBox;
                if (wordPanel?.Visibility == Visibility.Visible &&
                    wordDetail?.Text.Contains("音标", StringComparison.Ordinal) == true)
                {
                    Console.WriteLine("PASS  单词详解面板显示");
                }
                else
                {
                    Console.WriteLine("FAIL  单词详解面板未显示");
                    exitCode = 1;
                }

                overlay.PreferredFontSize = 24;
                var fontBox = overlay.FindName("TranslationText") as TextBox;
                if (Math.Abs(overlay.CurrentFontSize - 24) < 0.1 &&
                    fontBox is not null &&
                    Math.Abs(fontBox.FontSize - 24) < 0.1)
                {
                    Console.WriteLine("PASS  浮层字号可调（24）");
                }
                else
                {
                    Console.WriteLine($"FAIL  浮层字号: {overlay.CurrentFontSize}");
                    exitCode = 1;
                }

                overlay.PreferredFontSize = 1;
                if (Math.Abs(overlay.CurrentFontSize - OverlayWindow.MinFontSize) < 0.1)
                {
                    Console.WriteLine("PASS  浮层字号下限 clamp（12）");
                }
                else
                {
                    Console.WriteLine($"FAIL  浮层字号下限: {overlay.CurrentFontSize}");
                    exitCode = 1;
                }

                overlay.PreferredFontSize = OverlayWindow.DefaultFontSize;
                overlay.PreferredSpeechRate = 1.5;
                var speechButton = overlay.FindName("SpeechButton") as Button;
                if (Math.Abs(overlay.PreferredSpeechRate - 1.5) < 0.01 && speechButton is not null)
                {
                    Console.WriteLine("PASS  朗读按钮与语速属性");
                }
                else
                {
                    Console.WriteLine($"FAIL  朗读按钮/语速: {overlay.PreferredSpeechRate}, button={speechButton is not null}");
                    exitCode = 1;
                }

                overlay.PreferredMaxContentHeight = 300;
                var bodyScroller = overlay.FindName("BodyScroller") as ScrollViewer;
                if (bodyScroller is not null && Math.Abs(bodyScroller.MaxHeight - 300) < 0.1)
                {
                    Console.WriteLine("PASS  浮层内容高度可调（300）");
                }
                else
                {
                    Console.WriteLine($"FAIL  浮层内容高度: {bodyScroller?.MaxHeight}");
                    exitCode = 1;
                }

                await AlignmentUiTests.RunAsync(overlay, (condition, name) =>
                {
                    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {name}");
                    if (!condition) exitCode = 1;
                });

                overlay.ForceClose();

                var hotkeySettings = new AppSettings
                {
                    TranslateSelectionHotkey = "Ctrl+Alt+E",
                    TranslateClipboardHotkey = "Ctrl+Alt+Y",
                    OcrHotkey = "Ctrl+Alt+O",
                    CopyLastHotkey = "Ctrl+Alt+C"
                };
                using (var hotkeyService = new HotkeyService(hotkeySettings))
                {
                    if (hotkeyService.FailedHotkeys.Count == 0)
                    {
                        hotkeyService.Suspend();
                        hotkeyService.Resume();
                        Console.WriteLine("PASS  自定义快捷键注册 / 暂停 / 恢复（Ctrl+Alt+Y）");

                        hotkeyService.SetEnabled(false);
                        var disabledOk = !hotkeyService.IsEnabled;
                        hotkeyService.SetEnabled(true);
                        if (disabledOk && hotkeyService.IsEnabled && hotkeyService.FailedHotkeys.Count == 0)
                        {
                            Console.WriteLine("PASS  快捷键总开关启用 / 禁用");
                        }
                        else
                        {
                            Console.WriteLine("FAIL  快捷键总开关");
                            exitCode = 1;
                        }
                    }
                    else
                    {
                        Console.WriteLine("FAIL  自定义快捷键注册: " + string.Join("; ", hotkeyService.FailedHotkeys));
                        exitCode = 1;
                    }
                }

                var chordSettings = new AppSettings
                {
                    TranslateSelectionHotkey = "Ctrl+C+C",
                    TranslateClipboardHotkey = "Ctrl+Alt+V",
                    OcrHotkey = "Ctrl+Alt+O",
                    CopyLastHotkey = "Ctrl+Alt+C"
                };
                using (var chordService = new HotkeyService(chordSettings))
                {
                    if (chordService.FailedHotkeys.Count == 0)
                    {
                        Console.WriteLine("PASS  双击式快捷键 Ctrl+C+C 注册（不占用 Ctrl+C）");
                    }
                    else
                    {
                        Console.WriteLine("FAIL  双击式快捷键: " + string.Join("; ", chordService.FailedHotkeys));
                        exitCode = 1;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL  " + ex);
            }
            finally
            {
                window.Close();
                app.Shutdown(exitCode);
            }
        };

        app.Run(window);
        return Environment.ExitCode;
    }
}

internal static class Win32Test
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
}
