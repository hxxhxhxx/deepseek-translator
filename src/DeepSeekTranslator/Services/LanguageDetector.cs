using DeepSeekTranslator.Models;

namespace DeepSeekTranslator.Services;

/// <summary>
/// 轻量的中英方向检测：
/// - 中文为主 → 英文
/// - 英文为主 → 中文
/// - 日文/韩文等其他文字 → 英文
/// 这样不需要额外调用模型就能决定目标语言；模型请求里仍会显式带上 target_language。
/// </summary>
public static class LanguageDetector
{
    public static LanguageDetection Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return LanguageDetection.Unknown;
        }

        var cjk = 0;
        var latin = 0;
        var kana = 0;
        var hangul = 0;
        var otherLetters = 0;

        foreach (var ch in text)
        {
            if (IsCjk(ch))
            {
                cjk++;
            }
            else if (IsKana(ch))
            {
                kana++;
            }
            else if (IsHangul(ch))
            {
                hangul++;
            }
            else if (IsLatin(ch))
            {
                latin++;
            }
            else if (char.IsLetter(ch))
            {
                otherLetters++;
            }
        }

        if (kana > 0 || hangul > 0)
        {
            return LanguageDetection.OtherToEnglish;
        }

        if (cjk > 0)
        {
            // 中文技术文本里常混有英文缩写；中文占比达到一定阈值就判为中文。
            if (cjk >= 2 || latin == 0)
            {
                var threshold = Math.Max(2, latin * 0.15);
                if (cjk >= threshold)
                {
                    return LanguageDetection.ChineseToEnglish;
                }
            }
        }

        if (latin > 0)
        {
            return LanguageDetection.EnglishToChinese;
        }

        if (cjk > 0)
        {
            return LanguageDetection.ChineseToEnglish;
        }

        if (otherLetters > 0)
        {
            return LanguageDetection.OtherToEnglish;
        }

        return LanguageDetection.Unknown;
    }

    private static bool IsCjk(char ch)
    {
        return (ch >= '\u3400' && ch <= '\u4DBF') ||
               (ch >= '\u4E00' && ch <= '\u9FFF') ||
               (ch >= '\uF900' && ch <= '\uFAFF');
    }

    private static bool IsKana(char ch)
    {
        return (ch >= '\u3040' && ch <= '\u30FF');
    }

    private static bool IsHangul(char ch)
    {
        return (ch >= '\uAC00' && ch <= '\uD7AF') ||
               (ch >= '\u1100' && ch <= '\u11FF');
    }

    private static bool IsLatin(char ch)
    {
        return (ch >= 'A' && ch <= 'Z') ||
               (ch >= 'a' && ch <= 'z') ||
               (ch >= '\u00C0' && ch <= '\u024F');
    }
}
