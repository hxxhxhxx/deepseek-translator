using System.Diagnostics;
using System.Runtime.InteropServices;
using DeepSeekTranslator.Interop;
using Clipboard = System.Windows.Clipboard;
using IDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using TextDataFormat = System.Windows.TextDataFormat;

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
    private const int MaxWaitMs = 1100;

    public Task<ClipboardReadResult> TryCopySelectionAsync(CancellationToken cancellationToken)
    {
        return RunStaAsync(() => TryCopySelection(cancellationToken));
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

    private static ClipboardReadResult TryCopySelection(CancellationToken cancellationToken)
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

            var sequence = NativeMethods.GetClipboardSequenceNumber();
            if (!SendCopyShortcut(insert: false))
            {
                return new ClipboardReadResult(null, false, false, "SendInput Ctrl+C 失败");
            }

            var changed = WaitForSequenceChange(sequence, MaxWaitMs, cancellationToken);
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
            try
            {
                if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
                {
                    var text = Clipboard.GetText(TextDataFormat.UnicodeText);
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
            catch (COMException)
            {
                // 剪贴板被其他进程短暂占用，重试。
            }
            catch (Exception)
            {
                // 某些应用写入延迟，继续重试。
            }

            Thread.Sleep(35);
        }

        return null;
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
                    Clipboard.Clear();
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
}
