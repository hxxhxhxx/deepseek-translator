using System.Runtime.InteropServices;
using System.Windows.Threading;
using DeepSeekTranslator.Interop;

namespace DeepSeekTranslator.Services;

/// <summary>
/// 只“观察”全局鼠标和键盘输入（不拦截），用于浮层的「点一下 / 滚一下 / 敲键盘就消失」。
/// 只在浮层显示期间安装，浮层隐藏即卸载。
/// </summary>
internal sealed class InputWatcher : IDisposable
{
    private readonly KeyboardHook _keyboard = new();
    private readonly NativeMethods.LowLevelMouseProc _mouseProc;
    private readonly Dispatcher _dispatcher;
    private IntPtr _mouseHook;
    private bool _disposed;

    public InputWatcher()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _mouseProc = MouseCallback;
        _keyboard.KeyEvent += OnKeyEvent;
    }

    /// <summary>鼠标按下（左/右/中/侧键），参数为屏幕像素坐标。</summary>
    public event Action<int, int>? PointerPressed;

    /// <summary>滚轮滚动，参数为屏幕像素坐标。</summary>
    public event Action<int, int>? WheelScrolled;

    /// <summary>键盘按下（含自动重复），参数为虚拟键码。</summary>
    public event Action<int>? KeyPressed;

    public bool IsRunning => _mouseHook != IntPtr.Zero;

    public bool Start()
    {
        if (_disposed) return false;
        if (_mouseHook != IntPtr.Zero) return true;

        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _mouseProc,
            NativeMethods.GetModuleHandle(null),
            0);

        if (_mouseHook == IntPtr.Zero)
        {
            Logger.Error($"安装浮层鼠标监听失败：{Marshal.GetLastWin32Error()}");
            return false;
        }

        if (_keyboard.Install())
        {
            _keyboard.Enabled = true;
        }

        return true;
    }

    public void Stop()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        _keyboard.Enabled = false;
        _keyboard.Uninstall();
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var message = wParam.ToInt32();
                if (message is NativeMethods.WM_LBUTTONDOWN
                    or NativeMethods.WM_RBUTTONDOWN
                    or NativeMethods.WM_MBUTTONDOWN
                    or NativeMethods.WM_XBUTTONDOWN)
                {
                    var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    Dispatch(PointerPressed, info.pt.X, info.pt.Y);
                }
                else if (message == NativeMethods.WM_MOUSEWHEEL)
                {
                    var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    Dispatch(WheelScrolled, info.pt.X, info.pt.Y);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("浮层输入监听异常", ex);
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void OnKeyEvent(int virtualKey, bool isDown)
    {
        if (!isDown) return;

        _dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (!_disposed) KeyPressed?.Invoke(virtualKey);
            }));
    }

    private void Dispatch(Action<int, int>? handler, int x, int y)
    {
        if (handler is null) return;

        // 钩子回调要尽快返回，业务处理丢回 UI 线程。
        _dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (!_disposed) handler.Invoke(x, y);
            }));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        _keyboard.KeyEvent -= OnKeyEvent;
        _keyboard.Dispose();

        PointerPressed = null;
        WheelScrolled = null;
        KeyPressed = null;
    }
}
