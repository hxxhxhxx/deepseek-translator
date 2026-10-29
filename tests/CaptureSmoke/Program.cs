using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using DeepSeekTranslator.Models;
using DeepSeekTranslator.Services;

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

                if (Win32Test.GetForegroundWindow() != handle)
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
