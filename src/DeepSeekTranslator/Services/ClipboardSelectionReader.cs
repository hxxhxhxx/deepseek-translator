using System.Diagnostics;
using System.Runtime.InteropServices;
using DeepSeekTranslator.Interop;
using IDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace DeepSeekTranslator.Services;

public sealed record ClipboardReadResult(
    string? Text,
    bool ClipboardChanged,
    bool TimedOut,
    string? Diagnostic = null);

/// <summary>
/// 剪贴板回退：保存原剪贴板，发送 Ctrl+C，等待序列号变化，读取文本，再恢复。
/// 所有 OLE/剪贴板操作都在独立 STA 线程执行。
/// </summary>
public sealed class ClipboardSelectionReader
{
    public Task<ClipboardReadResult> TryCopySelectionAsync(
        CancellationToken cancellationToken,
        bool settleFocus = false)
    {
        return RunStaAsync(() => TryCopySelection(cancellationToken, settleFocus));
    }

    public Task<string?> ReadClipboardTextAsync(CancellationToken cancellationToken)
    {
        return RunStaAsync(() =>
        {
            try
            {
                return ReadClipboardText();
            }
            catch (Exception ex)
            {
                Logger.Error("读取剪贴板文本失败", ex);
                return null;
            }
        });
    }

    private static ClipboardReadResult TryCopySelection(
        CancellationToken cancellationToken,
        bool settleFocus)
    {
        IDataObject? snapshot = null;
        var hadSnapshot = false;
        var snapshotAvailable = false;
        var originalWasEmpty = false;

        try
        {
            try
            {
                var hr = NativeMethods.OleGetClipboard(out snapshot);
                snapshotAvailable = hr == 0;
                hadSnapshot = snapshotAvailable && snapshot is not null;
                originalWasEmpty = snapshotAvailable && snapshot is null;
            }
            catch (Exception ex)
            {
                Logger.Error("保存原剪贴板失败", ex);
            }

            if (!WaitForHotkeyModifiersReleased(cancellationToken))
            {
                return new ClipboardReadResult(
                    null,
                    false,
                    false,
                    "等待快捷键释放超时");
            }

            // 浏览器/PDF 会在选区旁弹出自己的小菜单；点我们的图标后它需要一点时间关闭、
            // 把焦点还给文档，太早发 Ctrl+C 会被吞掉（表现为“剪贴板没有变化”）。
            if (settleFocus)
            {
                Thread.Sleep(250);
            }

            var sequence = NativeMethods.GetClipboardSequenceNumber();
            if (!SendCopyShortcut(insert: false))
            {
                return new ClipboardReadResult(null, false, false, "SendInput Ctrl+C 失败");
            }

            var changed = WaitForSequenceChange(sequence, 700, cancellationToken);
            if (!changed)
            {
                // 第一次 Ctrl+C 可能落在浏览器的选区菜单上，重试一次。
                if (SendCopyShortcut(insert: false))
                {
                    changed = WaitForSequenceChange(sequence, 900, cancellationToken);
                }
            }

            if (!changed)
            {
                // 少数旧程序只响应 Ctrl+Insert。
                if (SendCopyShortcut(insert: true))
                {
                    changed = WaitForSequenceChange(sequence, 500, cancellationToken);
                }
            }

            if (!changed)
            {
                return new ClipboardReadResult(null, false, true, "剪贴板内容未变化");
            }

            var text = ReadClipboardText();
            return new ClipboardReadResult(text, true, false);
        }
        catch (Exception ex)
        {
            Logger.Error("剪贴板回退失败", ex);
            return new ClipboardReadResult(null, false, false, ex.GetType().Name);
        }
        finally
        {
            RestoreClipboard(snapshot, hadSnapshot, snapshotAvailable, originalWasEmpty);
        }
    }

    private static bool WaitForHotkeyModifiersReleased(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < 900)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsKeyDown(NativeMethods.VK_CONTROL) &&
                !IsKeyDown(NativeMethods.VK_MENU) &&
                !IsKeyDown(NativeMethods.VK_SHIFT) &&
                !IsKeyDown(NativeMethods.VK_LWIN) &&
                !IsKeyDown(NativeMethods.VK_RWIN))
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }

    private static bool IsKeyDown(int virtualKey)
    {
        return (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private static bool SendCopyShortcut(bool insert)
    {
        const ushort c = 0x43;
        const ushort insertKey = 0x2D;

        var key = insert ? insertKey : c;
        var inputs = new[]
        {
            KeyboardInput(NativeMethods.VK_CONTROL, keyUp: false),
            KeyboardInput(key, keyUp: false),
            KeyboardInput(key, keyUp: true),
            KeyboardInput(NativeMethods.VK_CONTROL, keyUp: true)
        };

        var inputSize = Marshal.SizeOf<NativeMethods.INPUT>();
        if (inputSize != 40)
        {
            Logger.Error($"INPUT 结构大小异常：{inputSize}，预期 40。");
            return false;
        }

        var sent = NativeMethods.SendInput(
            (uint)inputs.Length,
            inputs,
            inputSize);
        if (sent != (uint)inputs.Length)
        {
            Logger.Error($"SendInput 失败：sent={sent}/{inputs.Length}, size={inputSize}, error={Marshal.GetLastWin32Error()}");
            return false;
        }

        return true;
    }

    private static NativeMethods.INPUT KeyboardInput(int virtualKey, bool keyUp)
    {
        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = (ushort)virtualKey,
                    dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0
                }
            }
        };
    }

    private static bool WaitForSequenceChange(
        uint original,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (NativeMethods.GetClipboardSequenceNumber() != original)
            {
                return true;
            }

            Thread.Sleep(35);
        }

        return false;
    }

    private static string? ReadClipboardText()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            // 不在后台 STA 线程使用 System.Windows.Clipboard：WPF 剪贴板会在这类临时
            // 线程上创建隐藏窗口/Dispatcher，线程退出时窗口过程可能抛 NRE 并直接
            // FailFast（表现为 HwndSubclass.SubclassWndProc 崩溃）。这里只走 Win32。
            if (TryReadClipboardTextWin32(out var text) &&
                !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            Thread.Sleep(35);
        }

        return null;
    }

    private static bool TryReadClipboardTextWin32(out string? text)
    {
        text = null;
        if (!OpenClipboard(IntPtr.Zero))
        {
            return false;
        }

        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            var ansi = false;
            if (handle == IntPtr.Zero)
            {
                handle = GetClipboardData(CF_TEXT);
                ansi = true;
            }

            if (handle == IntPtr.Zero)
            {
                return false;
            }

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                text = ansi
                    ? Marshal.PtrToStringAnsi(pointer)
                    : Marshal.PtrToStringUni(pointer);
                return true;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static void ClearClipboardWin32()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(30);
                continue;
            }

            try
            {
                if (EmptyClipboard())
                {
                    return;
                }
            }
            finally
            {
                CloseClipboard();
            }

            Thread.Sleep(30);
        }
    }

    private static void RestoreClipboard(
        IDataObject? snapshot,
        bool hadSnapshot,
        bool snapshotAvailable,
        bool originalWasEmpty)
    {
        // 如果连原剪贴板都没能读取，不要清空用户的剪贴板。
        if (!snapshotAvailable) return;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (hadSnapshot && snapshot is not null)
                {
                    var hr = NativeMethods.OleSetClipboard(snapshot);
                    if (hr == 0)
                    {
                        NativeMethods.OleFlushClipboard();
                        return;
                    }
                }
                else if (originalWasEmpty)
                {
                    ClearClipboardWin32();
                    return;
                }
                else
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("恢复剪贴板失败", ex);
            }

            Thread.Sleep(30);
        }
    }

    private static Task<T> RunStaAsync<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var oleInitialized = NativeMethods.OleInitialize(IntPtr.Zero) >= 0;
            try
            {
                tcs.TrySetResult(action());
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                if (oleInitialized)
                {
                    NativeMethods.OleUninitialize();
                }
            }
        })
        {
            IsBackground = true,
            Name = "DeepSeekTranslator.STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private const uint CF_TEXT = 1;
    private const uint CF_UNICODETEXT = 13;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();
}
