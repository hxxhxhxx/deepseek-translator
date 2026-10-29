using System.Windows.Input;
using DeepSeekTranslator.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace DeepSeekTranslator.Controls;

/// <summary>
/// 点击后直接按下组合键即可录制，例如 Ctrl+Alt+V。
/// 同一个组合键在 600ms 内按两次会录成双击式快捷键，例如 Ctrl+C+C。
/// Backspace/Delete 清空；Tab 无修饰键时保留正常焦点切换。
/// </summary>
public sealed class HotkeyTextBox : TextBox
{
    private const int DoubleTapWindowMilliseconds = 600;

    private Key? _lastKey;
    private ModifierKeys _lastModifiers;
    private long _lastTicks;
    private bool _released = true;

    public HotkeyTextBox()
    {
        IsReadOnly = true;
        Cursor = System.Windows.Input.Cursors.Hand;
        TextAlignment = System.Windows.TextAlignment.Center;
        VerticalContentAlignment = System.Windows.VerticalAlignment.Center;
        FontFamily = new System.Windows.Media.FontFamily("Consolas");
        FontSize = 11;
    }

    public string Hotkey
    {
        get => Text;
        set => Text = value;
    }

    /// <summary>开始录制：调用方应暂停全局热键，否则组合键会被系统热键截获。</summary>
    public event Action? RecordingStarted;

    /// <summary>录制结束（焦点离开）：调用方恢复全局热键。</summary>
    public event Action? RecordingStopped;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        if (key == Key.Tab && modifiers == ModifierKeys.None)
        {
            return;
        }

        if (key is Key.Back or Key.Delete && modifiers == ModifierKeys.None)
        {
            Text = string.Empty;
            ClearRecording();
            e.Handled = true;
            return;
        }

        if (HotkeyParser.IsModifierKey(key))
        {
            e.Handled = true;
            return;
        }

        var single = HotkeyParser.Format(modifiers, key);
        if (string.IsNullOrWhiteSpace(single))
        {
            e.Handled = true;
            return;
        }

        var now = Environment.TickCount64;

        if (!_released && key == _lastKey)
        {
            // 按住不放的自动重复，不当作第二次按下。
            e.Handled = true;
            return;
        }

        var isDoubleTap =
            _lastKey == key &&
            _lastModifiers == modifiers &&
            _released &&
            now - _lastTicks <= DoubleTapWindowMilliseconds;

        Text = isDoubleTap
            ? $"{single}+{HotkeyParser.FormatKey(key)}"
            : single;

        _lastKey = key;
        _lastModifiers = modifiers;
        _lastTicks = now;
        _released = false;

        SelectAll();
        e.Handled = true;
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == _lastKey)
        {
            _released = true;
        }

        base.OnPreviewKeyUp(e);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        SelectAll();
        ClearRecording();
        RecordingStarted?.Invoke();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        ClearRecording();
        RecordingStopped?.Invoke();
    }

    private void ClearRecording()
    {
        _lastKey = null;
        _lastModifiers = ModifierKeys.None;
        _lastTicks = 0;
        _released = true;
    }
}
