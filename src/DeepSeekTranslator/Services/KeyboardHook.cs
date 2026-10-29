using System.Runtime.InteropServices;
using DeepSeekTranslator.Interop;

namespace DeepSeekTranslator.Services;

/// <summary>
/// 低级键盘钩子：只“观察”按键，不拦截，用于双击式快捷键（例如 Ctrl+C+C）。
/// 普通组合键仍然走 RegisterHotKey，不占用额外钩子。
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private IntPtr _hook;
    private bool _disposed;

    public KeyboardHook()
    {
        // 必须用字段持有委托，否则 GC 回收后钩子回调会崩溃。
        _proc = Callback;
    }

    /// <summary>(virtualKey, isDown)。在安装钩子的线程（UI 线程）上触发。</summary>
    public event Action<int, bool>? KeyEvent;

    public bool Enabled { get; set; } = true;

    public bool Installed => _hook != IntPtr.Zero;

    public bool Install()
    {
        if (_disposed) return false;
        if (_hook != IntPtr.Zero) return true;

        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _proc,
            NativeMethods.GetModuleHandle(null),
            0);

        if (_hook == IntPtr.Zero)
        {
            Logger.Error($"安装键盘钩子失败：{Marshal.GetLastWin32Error()}");
            return false;
        }

        return true;
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;

        if (!NativeMethods.UnhookWindowsHookEx(_hook))
        {
            Logger.Error($"卸载键盘钩子失败：{Marshal.GetLastWin32Error()}");
        }

        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled && KeyEvent is not null)
        {
            var message = wParam.ToInt32();
            var isDown = message == NativeMethods.WM_KEYDOWN ||
                         message == NativeMethods.WM_SYSKEYDOWN;
            var isUp = message == NativeMethods.WM_KEYUP ||
                       message == NativeMethods.WM_SYSKEYUP;

            if (isDown || isUp)
            {
                try
                {
                    var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                    KeyEvent.Invoke((int)info.vkCode, isDown);
                }
                catch (Exception ex)
                {
                    // 钩子回调里绝不能抛异常，否则会拖慢整个系统的键盘输入。
                    Logger.Error("键盘钩子回调异常", ex);
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        KeyEvent = null;
        Uninstall();
    }
}
