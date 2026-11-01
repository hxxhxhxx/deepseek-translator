using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DeepSeekTranslator.Services;

/// <summary>
/// 写剪贴板。直接走 Win32（OpenClipboard / SetClipboardData），
/// 避开 WPF <c>Clipboard.SetText</c> 在剪贴板被占用时长达数秒的内部重试。
/// 在独立 STA 线程执行，最多重试约 0.9 秒；失败只返回 false，不阻塞 UI；
/// 失败时会记录占用剪贴板的进程，便于定位（例如截图/剪贴板管理工具）。
/// </summary>
public static class ClipboardWriter
{
    private const int MaxAttempts = 60;
    private const int RetryDelayMs = 15;
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public static Task<bool> TrySetTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return Task.FromResult(false);

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(TrySetText(text));
            }
            catch (Exception ex)
            {
                Logger.Error("写剪贴板失败", ex);
                completion.TrySetResult(false);
            }
        })
        {
            IsBackground = true,
            Name = "DeepSeekTranslator.Clipboard"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static bool TrySetText(string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text + "\0");

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                if (attempt == MaxAttempts - 1) LogOwner();
                Thread.Sleep(RetryDelayMs);
                continue;
            }

            try
            {
                if (!EmptyClipboard()) return false;

                var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
                if (handle == IntPtr.Zero) return false;

                var pointer = GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                {
                    GlobalFree(handle);
                    return false;
                }

                try
                {
                    Marshal.Copy(bytes, 0, pointer, bytes.Length);
                }
                finally
                {
                    GlobalUnlock(handle);
                }

                if (SetClipboardData(CF_UNICODETEXT, handle) == IntPtr.Zero)
                {
                    GlobalFree(handle);
                    return false;
                }

                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }

        return false;
    }

    private static void LogOwner()
    {
        try
        {
            var hwnd = GetClipboardOwner();
            if (hwnd == IntPtr.Zero)
            {
                Logger.Error("写剪贴板失败：剪贴板被占用（未找到所有者）");
                return;
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            var name = "?";
            try
            {
                name = Process.GetProcessById((int)pid).ProcessName;
            }
            catch
            {
                // 进程可能已退出，保留 pid 即可。
            }

            Logger.Error($"写剪贴板失败：剪贴板被 {name}(pid={pid}) 占用");
        }
        catch (Exception ex)
        {
            Logger.Error("读取剪贴板占用者失败", ex);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}
