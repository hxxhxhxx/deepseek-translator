using System.Text;
using DeepSeekTranslator.Models;

namespace DeepSeekTranslator.Services;

public static class TextValidator
{
    public const int MaxChars = 8000;

    public static ValidationResult Validate(string? raw)
    {
        var text = Normalize(raw);
        if (text.Length == 0)
        {
            return ValidationResult.Fail("EMPTY_SELECTION", "未检测到选中文本，请重新选择。");
        }

        if (text.Length > MaxChars)
        {
            return ValidationResult.Fail(
                "TEXT_TOO_LONG",
                $"选中内容共 {text.Length:N0} 个字符，超过 {MaxChars:N0} 字符上限，请分段翻译。",
                text);
        }

        if (!ContainsLetter(text))
        {
            return ValidationResult.Fail(
                "NO_TRANSLATABLE_TEXT",
                "选中的内容里没有可翻译的文字（例如只有数字、标点或表情符号）。",
                text);
        }

        return ValidationResult.Ok(text);
    }

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (ch == '\0') continue;
            builder.Append(ch);
        }

        return builder.ToString().Trim();
    }

    private static bool ContainsLetter(string text)
    {
        foreach (var ch in text)
        {
            if (char.IsLetter(ch)) return true;
        }

        return false;
    }
}
