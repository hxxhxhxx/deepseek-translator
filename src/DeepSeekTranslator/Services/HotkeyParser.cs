using System.Windows.Input;
using DeepSeekTranslator.Interop;
using DeepSeekTranslator.Models;

namespace DeepSeekTranslator.Services;

public sealed record ParsedHotkey(
    uint Modifiers,
    uint VirtualKey,
    string Display,
    bool IsDoubleTap = false);

public static class HotkeyParser
{
    public const string DefaultTranslateSelection = "Ctrl+Alt+E";
    public const string DefaultTranslateClipboard = "Ctrl+Alt+V";
    public const string DefaultOcr = "Ctrl+Alt+O";
    public const string DefaultCopyLast = "Ctrl+Alt+C";

    public static bool TryParse(string? text, out ParsedHotkey parsed)
    {
        parsed = default!;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var tokens = text.Split(
            '+',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2) return false;

        uint modifiers = 0;
        var keyTokens = new List<string>(2);

        foreach (var token in tokens)
        {
            switch (token.Trim().ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= NativeMethods.MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= NativeMethods.MOD_ALT;
                    break;
                case "shift":
                    modifiers |= NativeMethods.MOD_SHIFT;
                    break;
                case "win":
                case "windows":
                    modifiers |= NativeMethods.MOD_WIN;
                    break;
                default:
                    keyTokens.Add(token);
                    break;
            }
        }

        if (modifiers == 0 || keyTokens.Count == 0 || keyTokens.Count > 2) return false;
        if (!TryParseKey(keyTokens[0], out var key) || IsModifierKey(key)) return false;

        // Ctrl+C+C：同一个主键按两次。两个不同的主键（Ctrl+V+E）仍然视为无效。
        var isDoubleTap = false;
        if (keyTokens.Count == 2)
        {
            if (!TryParseKey(keyTokens[1], out var secondKey) ||
                IsModifierKey(secondKey) ||
                secondKey != key)
            {
                return false;
            }

            isDoubleTap = true;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey <= 0) return false;

        var modifierKeys = ToModifierKeys(modifiers);
        var display = Format(modifierKeys, key);
        if (isDoubleTap)
        {
            display = $"{display}+{FormatKey(key)}";
        }

        parsed = new ParsedHotkey(
            modifiers,
            (uint)virtualKey,
            display,
            isDoubleTap);
        return true;
    }

    public static bool IsValid(string? text) => TryParse(text, out _);

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        if (parts.Count == 0 || IsModifierKey(key)) return string.Empty;
        parts.Add(FormatKey(key));
        return string.Join("+", parts);
    }

    public static string FormatKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
        {
            return ((char)('A' + ((int)key - (int)Key.A))).ToString();
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            return ((char)('0' + ((int)key - (int)Key.D0))).ToString();
        }

        return key switch
        {
            Key.Space => "Space",
            Key.Escape => "Esc",
            Key.Enter => "Enter",
            Key.Tab => "Tab",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Up => "Up",
            Key.Down => "Down",
            _ => key.ToString()
        };
    }

    public static bool IsModifierKey(Key key)
    {
        return key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin
            or Key.System;
    }

    public static string DefaultFor(HotkeyAction action)
    {
        return action switch
        {
            HotkeyAction.TranslateSelection => DefaultTranslateSelection,
            HotkeyAction.TranslateClipboard => DefaultTranslateClipboard,
            HotkeyAction.OcrTranslate => DefaultOcr,
            HotkeyAction.CopyLastTranslation => DefaultCopyLast,
            _ => DefaultTranslateSelection
        };
    }

    private static bool TryParseKey(string token, out Key key)
    {
        key = Key.None;
        var normalized = token.Trim();

        if (normalized.Length == 1)
        {
            if (normalized[0] is >= '0' and <= '9')
            {
                return Enum.TryParse($"D{normalized}", ignoreCase: true, out key);
            }

            return Enum.TryParse(normalized, ignoreCase: true, out key);
        }

        var alias = normalized.ToLowerInvariant() switch
        {
            "esc" => Key.Escape,
            "return" => Key.Enter,
            "del" => Key.Delete,
            "ins" => Key.Insert,
            "pgup" => Key.PageUp,
            "pgdn" => Key.PageDown,
            "spacebar" => Key.Space,
            _ => Key.None
        };

        if (alias != Key.None)
        {
            key = alias;
            return true;
        }

        return Enum.TryParse(normalized, ignoreCase: true, out key) &&
               key != Key.None;
    }

    private static ModifierKeys ToModifierKeys(uint modifiers)
    {
        var result = ModifierKeys.None;
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0) result |= ModifierKeys.Control;
        if ((modifiers & NativeMethods.MOD_ALT) != 0) result |= ModifierKeys.Alt;
        if ((modifiers & NativeMethods.MOD_SHIFT) != 0) result |= ModifierKeys.Shift;
        if ((modifiers & NativeMethods.MOD_WIN) != 0) result |= ModifierKeys.Windows;
        return result;
    }
}
