using System.Runtime.InteropServices;
using System.Windows.Threading;
using DeepSeekTranslator.Interop;
using DeepSeekTranslator.Models;

namespace DeepSeekTranslator.Services;

/// <summary>
/// 低级鼠标钩子：检测“按住左键拖选文字后松开”，为划词小图标提供触发点。
/// 只观察位置和时间，不拦截鼠标事件。
/// </summary>
public sealed class SelectionMonitor : IDisposable
{
    private readonly NativeMethods.LowLevelMouseProc _proc;
    private readonly Dispatcher _dispatcher;
    private IntPtr _hook;
    private int _downX;
    private int _downY;
    private long _downTicks;
    private bool _tracking;
    private long _lastRaisedTicks;
    private long _lastClickTicks;
    private int _lastClickX;
    private int _lastClickY;
    private bool _disposed;

    public SelectionMonitor()
    {
        _proc = Callback;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>鼠标左键/右键按下时的屏幕坐标（用于隐藏已经显示的小图标）。</summary>
    public event Action<PixelRect>? PointerDown;

    /// <summary>拖选结束（松开左键）时的屏幕坐标。</summary>
    public event Action<PixelRect>? DragCompleted;

    /// <summary>双击（例如双击选词、三击选段）时的屏幕坐标。</summary>
    public event Action<PixelRect>? DoubleClicked;

    /// <summary>小于该距离视为普通点击，不触发划词图标。</summary>
    public int MinDragPixels { get; set; } = 8;

    /// <summary>拖选的最短持续时间，过滤误触。</summary>
    public int MinDragMilliseconds { get; set; } = 40;

    /// <summary>两次触发之间的最小间隔。</summary>
    public int CooldownMilliseconds { get; set; } = 250;

    public bool Enabled { get; set; } = true;

    public bool Installed => _hook != IntPtr.Zero;

    public bool Install()
    {
        if (_disposed) return false;
        if (_hook != IntPtr.Zero) return true;

        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _proc,
            NativeMethods.GetModuleHandle(null),
            0);

        if (_hook == IntPtr.Zero)
        {
            Logger.Error($"安装鼠标钩子失败：{Marshal.GetLastWin32Error()}");
            return false;
        }

        return true;
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;

        if (!NativeMethods.UnhookWindowsHookEx(_hook))
        {
            Logger.Error($"卸载鼠标钩子失败：{Marshal.GetLastWin32Error()}");
        }

        _hook = IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled)
        {
            try
            {
                HandleMessage(wParam.ToInt32(), lParam);
            }
            catch (Exception ex)
            {
                Logger.Error("鼠标钩子回调异常", ex);
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void HandleMessage(int message, IntPtr lParam)
    {
        switch (message)
        {
            case NativeMethods.WM_LBUTTONDOWN:
            case NativeMethods.WM_RBUTTONDOWN:
            {
                var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var point = new PixelRect(info.pt.X, info.pt.Y, info.pt.X + 1, info.pt.Y + 1);

                if (message == NativeMethods.WM_LBUTTONDOWN)
                {
                    _downX = info.pt.X;
                    _downY = info.pt.Y;
                    _downTicks = Environment.TickCount64;
                    _tracking = true;
                }
                else
                {
                    _tracking = false;
                }

                Raise(PointerDown, point);
                break;
            }

            case NativeMethods.WM_LBUTTONUP:
            {
                if (!_tracking)
                {
                    return;
                }

                _tracking = false;
                var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var elapsed = Environment.TickCount64 - _downTicks;
                var dx = info.pt.X - _downX;
                var dy = info.pt.Y - _downY;
                var distance = Math.Abs(dx) + Math.Abs(dy);

                if (distance < MinDragPixels || elapsed < MinDragMilliseconds)
                {
                    HandleClick(info.pt);
                    return;
                }

                if (Environment.TickCount64 - _lastRaisedTicks < CooldownMilliseconds)
                {
                    return;
                }

                _lastRaisedTicks = Environment.TickCount64;
                _lastClickTicks = 0;
                Raise(
                    DragCompleted,
                    new PixelRect(info.pt.X, info.pt.Y, info.pt.X + 1, info.pt.Y + 1));
                break;
            }
        }
    }

    /// <summary>
    /// 没有拖动的点击：连续两次落在同一点视为双击，用于「双击选词 / 三击选段」后显示图标。
    /// </summary>
    private void HandleClick(NativeMethods.POINT point)
    {
        var now = Environment.TickCount64;
        var isDoubleClick =
            now - _lastClickTicks <= 400 &&
            Math.Abs(point.X - _lastClickX) <= 4 &&
            Math.Abs(point.Y - _lastClickY) <= 4;

        if (isDoubleClick)
        {
            _lastClickTicks = 0;
            Raise(
                DoubleClicked,
                new PixelRect(point.X, point.Y, point.X + 1, point.Y + 1));
            return;
        }

        _lastClickTicks = now;
        _lastClickX = point.X;
        _lastClickY = point.Y;
    }

    private void Raise(Action<PixelRect>? handler, PixelRect point)
    {
        if (handler is null) return;

        // 钩子回调必须尽快返回，把业务处理丢回 UI 线程。
        _dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (!_disposed)
                {
                    handler.Invoke(point);
                }
            }));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        PointerDown = null;
        DragCompleted = null;
        Uninstall();
    }
}
