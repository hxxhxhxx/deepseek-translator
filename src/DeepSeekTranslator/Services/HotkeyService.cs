using System.Windows.Interop;
using System.Windows.Threading;
using DeepSeekTranslator.Interop;
using DeepSeekTranslator.Models;

namespace DeepSeekTranslator.Services;

/// <summary>
/// 全局快捷键注册与分发。
/// 普通组合键走 RegisterHotKey；双击式组合（Ctrl+C+C）走低级键盘钩子，
/// 因为 RegisterHotKey 只能注册单次按键。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int DoubleTapWindowMilliseconds = 600;

    private static readonly HotkeyAction[] AllActions =
    {
        HotkeyAction.TranslateSelection,
        HotkeyAction.TranslateClipboard,
        HotkeyAction.OcrTranslate,
        HotkeyAction.CopyLastTranslation
    };

    private readonly HwndSource _source;
    private readonly Dispatcher _dispatcher;
    private readonly KeyboardHook _keyboardHook = new();
    private Dictionary<HotkeyAction, ParsedHotkey> _bindings = new();
    private Dictionary<HotkeyAction, ParsedHotkey> _chordBindings = new();
    private Dictionary<HotkeyAction, ParsedHotkey>? _suspendedPrevious;
    private HotkeyAction? _pendingChord;
    private long _pendingChordTicks;
    private bool _chordKeyReleased = true;
    private bool _suspended;
    private bool _enabled = true;
    private bool _disposed;

    public HotkeyService(AppSettings settings, bool enabled = true)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;

        var parameters = new HwndSourceParameters("DeepSeekTranslatorHotkeyWindow")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        _keyboardHook.KeyEvent += OnHookKeyEvent;

        _enabled = enabled;
        UpdateBindings(settings, out var failures);
        FailedHotkeys = failures;
    }

    public event Action<HotkeyAction>? HotkeyPressed;

    public IReadOnlyList<string> FailedHotkeys { get; private set; } = Array.Empty<string>();

    public bool IsEnabled => _enabled;

    /// <summary>快捷键总开关。关闭后不注册任何全局快捷键（包括双击式组合）。</summary>
    public void SetEnabled(bool enabled)
    {
        if (_disposed || _enabled == enabled) return;

        _enabled = enabled;

        if (!enabled)
        {
            UnregisterAll();
            _pendingChord = null;
            UpdateChordHook();
            FailedHotkeys = Array.Empty<string>();
            return;
        }

        UpdateChordHook();

        if (_suspended)
        {
            return;
        }

        var errors = RegisterAll(_bindings);
        if (errors.Count > 0)
        {
            UnregisterAll();
            if (_suspendedPrevious is not null)
            {
                RegisterAll(_suspendedPrevious);
                _bindings = _suspendedPrevious;
            }

            FailedHotkeys = errors;
            Logger.Error($"恢复快捷键失败：{string.Join("；", errors)}");
        }
        else
        {
            FailedHotkeys = Array.Empty<string>();
        }
    }

    public bool UpdateBindings(AppSettings settings, out IReadOnlyList<string> failures)
    {
        var candidate = new Dictionary<HotkeyAction, ParsedHotkey>();
        var chords = new Dictionary<HotkeyAction, ParsedHotkey>();
        var errors = new List<string>();
        var seen = new Dictionary<(uint Modifiers, uint VirtualKey, bool DoubleTap), HotkeyAction>();

        foreach (var action in AllActions)
        {
            var text = GetText(settings, action);
            if (!HotkeyParser.TryParse(text, out var parsed))
            {
                errors.Add($"{Label(action)}：格式无效（示例 Ctrl+Alt+V 或 Ctrl+C+C）");
                continue;
            }

            var key = (parsed.Modifiers, parsed.VirtualKey, parsed.IsDoubleTap);
            if (seen.TryGetValue(key, out var other))
            {
                errors.Add($"{Label(action)} 与 {Label(other)} 使用了相同的组合键 {parsed.Display}");
                continue;
            }

            seen[key] = action;

            if (parsed.IsDoubleTap)
            {
                chords[action] = parsed;
            }
            else
            {
                candidate[action] = parsed;
            }
        }

        if (errors.Count > 0)
        {
            failures = errors;
            FailedHotkeys = errors;
            return false;
        }

        // 设置窗口打开期间或总开关关闭时只记录绑定，不实际注册。
        if (_suspended || !_enabled)
        {
            _bindings = candidate;
            _chordBindings = chords;
            UpdateChordHook();
            FailedHotkeys = Array.Empty<string>();
            failures = Array.Empty<string>();
            return true;
        }

        var previous = new Dictionary<HotkeyAction, ParsedHotkey>(_bindings);
        var previousChords = new Dictionary<HotkeyAction, ParsedHotkey>(_chordBindings);
        UnregisterAll();
        _bindings.Clear();
        _chordBindings.Clear();

        var registrationErrors = RegisterAll(candidate);
        if (registrationErrors.Count > 0)
        {
            UnregisterAll();
            _bindings.Clear();
            _chordBindings.Clear();
            RegisterAll(previous);
            _bindings = previous;
            _chordBindings = previousChords;
            UpdateChordHook();
            failures = registrationErrors;
            FailedHotkeys = registrationErrors;
            return false;
        }

        _bindings = candidate;
        _chordBindings = chords;
        UpdateChordHook();
        FailedHotkeys = Array.Empty<string>();
        failures = Array.Empty<string>();
        return true;
    }

    public void Suspend()
    {
        if (_disposed || _suspended) return;
        _suspended = true;
        _suspendedPrevious = new Dictionary<HotkeyAction, ParsedHotkey>(_bindings);
        UnregisterAll();
        _keyboardHook.Enabled = false;
        _pendingChord = null;
    }

    public void Resume()
    {
        if (_disposed || !_suspended) return;
        _suspended = false;

        if (!_enabled)
        {
            return;
        }

        UpdateChordHook();

        var errors = RegisterAll(_bindings);
        if (errors.Count > 0)
        {
            // 新快捷键有冲突时，恢复设置窗口打开前的组合，避免用户完全失去热键。
            UnregisterAll();
            if (_suspendedPrevious is not null)
            {
                RegisterAll(_suspendedPrevious);
                _bindings = _suspendedPrevious;
            }

            FailedHotkeys = errors;
            Logger.Error($"恢复快捷键失败，已回退旧组合：{string.Join("；", errors)}");
        }
        else
        {
            FailedHotkeys = Array.Empty<string>();
        }

        _suspendedPrevious = null;
    }

    private void UpdateChordHook()
    {
        var needed = _chordBindings.Count > 0 && _enabled && !_suspended;

        if (needed)
        {
            if (!_keyboardHook.Install())
            {
                Logger.Error("双击式快捷键不可用：键盘钩子安装失败");
            }

            _keyboardHook.Enabled = true;
        }
        else
        {
            _keyboardHook.Enabled = false;
            _keyboardHook.Uninstall();
        }
    }

    private void OnHookKeyEvent(int virtualKey, bool isDown)
    {
        if (_disposed || !_enabled || _suspended || _chordBindings.Count == 0) return;

        var modifiers = ReadModifiers();
        var action = MatchChord((uint)virtualKey, modifiers);

        if (isDown)
        {
            if (action is null) return;

            if (!_chordKeyReleased)
            {
                // 按住不放产生的自动重复，不算第二次按下。
                return;
            }

            var now = Environment.TickCount64;
            if (_pendingChord == action && now - _pendingChordTicks <= DoubleTapWindowMilliseconds)
            {
                _pendingChord = null;
                _chordKeyReleased = false;
                Dispatch(action.Value);
                return;
            }

            _pendingChord = action;
            _pendingChordTicks = now;
            _chordKeyReleased = false;
        }
        else if (action is not null && _pendingChord == action)
        {
            _chordKeyReleased = true;
        }
    }

    private void Dispatch(HotkeyAction action)
    {
        _dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (_disposed || !_enabled || _suspended) return;

                try
                {
                    HotkeyPressed?.Invoke(action);
                }
                catch (Exception ex)
                {
                    Logger.Error("处理全局快捷键失败", ex);
                }
            }));
    }

    private HotkeyAction? MatchChord(uint virtualKey, uint modifiers)
    {
        foreach (var pair in _chordBindings)
        {
            if (pair.Value.VirtualKey == virtualKey && pair.Value.Modifiers == modifiers)
            {
                return pair.Key;
            }
        }

        return null;
    }

    private static uint ReadModifiers()
    {
        uint modifiers = 0;

        if (IsDown(NativeMethods.VK_CONTROL)) modifiers |= NativeMethods.MOD_CONTROL;
        if (IsDown(NativeMethods.VK_MENU)) modifiers |= NativeMethods.MOD_ALT;
        if (IsDown(NativeMethods.VK_SHIFT)) modifiers |= NativeMethods.MOD_SHIFT;
        if (IsDown(NativeMethods.VK_LWIN) || IsDown(NativeMethods.VK_RWIN))
        {
            modifiers |= NativeMethods.MOD_WIN;
        }

        return modifiers;
    }

    private static bool IsDown(int virtualKey)
    {
        return (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private List<string> RegisterAll(Dictionary<HotkeyAction, ParsedHotkey> bindings)
    {
        var errors = new List<string>();
        foreach (var action in AllActions)
        {
            if (!bindings.TryGetValue(action, out var parsed)) continue;
            if (!Register(action, parsed))
            {
                errors.Add($"{Label(action)}：{parsed.Display} 被其他程序占用");
            }
        }

        return errors;
    }

    private bool Register(HotkeyAction action, ParsedHotkey parsed)
    {
        var id = IdFor(action);
        var success = NativeMethods.RegisterHotKey(
            _source.Handle,
            id,
            parsed.Modifiers | NativeMethods.MOD_NOREPEAT,
            parsed.VirtualKey);

        if (!success)
        {
            Logger.Error($"注册全局快捷键失败：{Label(action)} -> {parsed.Display}");
        }

        return success;
    }

    private void UnregisterAll()
    {
        foreach (var action in AllActions)
        {
            NativeMethods.UnregisterHotKey(_source.Handle, IdFor(action));
        }
    }

    private static int IdFor(HotkeyAction action) => (int)action + 1;

    private static HotkeyAction? ActionForId(int id)
    {
        return id switch
        {
            1 => HotkeyAction.TranslateSelection,
            2 => HotkeyAction.TranslateClipboard,
            3 => HotkeyAction.OcrTranslate,
            4 => HotkeyAction.CopyLastTranslation,
            _ => null
        };
    }

    private static string Label(HotkeyAction action)
    {
        return action switch
        {
            HotkeyAction.TranslateSelection => "翻译当前选区",
            HotkeyAction.TranslateClipboard => "翻译剪贴板",
            HotkeyAction.OcrTranslate => "截图 OCR",
            HotkeyAction.CopyLastTranslation => "复制最近译文",
            _ => action.ToString()
        };
    }

    private static string GetText(AppSettings settings, HotkeyAction action)
    {
        return action switch
        {
            HotkeyAction.TranslateSelection => settings.TranslateSelectionHotkey,
            HotkeyAction.TranslateClipboard => settings.TranslateClipboardHotkey,
            HotkeyAction.OcrTranslate => settings.OcrHotkey,
            HotkeyAction.CopyLastTranslation => settings.CopyLastHotkey,
            _ => string.Empty
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY) return IntPtr.Zero;

        var action = ActionForId(wParam.ToInt32());
        if (action is not null)
        {
            handled = true;
            if (!_enabled || _suspended) return IntPtr.Zero;

            try
            {
                HotkeyPressed?.Invoke(action.Value);
            }
            catch (Exception ex)
            {
                Logger.Error("处理全局快捷键失败", ex);
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterAll();

        _keyboardHook.KeyEvent -= OnHookKeyEvent;
        _keyboardHook.Dispose();

        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
