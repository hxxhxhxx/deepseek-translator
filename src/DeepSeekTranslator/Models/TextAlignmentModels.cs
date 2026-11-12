using System.Globalization;

namespace DeepSeekTranslator.Models;

/// <summary>Exact displayed strings and UTF-16 selection offsets; never normalized or trimmed.</summary>
public sealed record AlignmentRequest(string Source, string Translation, int SelectionStart, int SelectionLength)
{
    public const int MaxSourceLength = 8000;
    public const int MaxTranslationLength = 40000;
    public const int MaxSelectionLength = 400;

    public string SelectedText
    {
        get
        {
            Validate();
            return Translation.Substring(SelectionStart, SelectionLength);
        }
    }

    public void Validate() => Validate(this);

    public static void Validate(AlignmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);
        ArgumentNullException.ThrowIfNull(request.Translation);

        if (request.Source.Length > MaxSourceLength || request.Translation.Length > MaxTranslationLength)
        {
            throw new DeepSeekException("ALIGNMENT_TEXT_TOO_LONG", "对照文本过长：原文最多 8000 字符，译文最多 40000 字符。");
        }

        if (string.IsNullOrWhiteSpace(request.Source))
        {
            throw new DeepSeekException("EMPTY_SOURCE", "没有可对照的原文。");
        }

        if (request.SelectionStart < 0 || request.SelectionStart > request.Translation.Length ||
            request.SelectionLength <= 0 ||
            request.SelectionLength > request.Translation.Length - request.SelectionStart)
        {
            throw new ArgumentOutOfRangeException(nameof(SelectionStart), "译文选择范围无效。");
        }

        if (request.SelectionLength > MaxSelectionLength)
        {
            throw new DeepSeekException("ALIGNMENT_SELECTION_TOO_LONG", "对照选择最多 400 字符，请缩小选择范围。");
        }

        if (!IsWellFormedUtf16(request.Source) || !IsWellFormedUtf16(request.Translation))
        {
            throw new ArgumentException("对照文本包含不完整的 Unicode 字符。", nameof(request));
        }

        var boundaries = TextBoundaries(request.Translation);
        if (!boundaries.Contains(request.SelectionStart) ||
            !boundaries.Contains(request.SelectionStart + request.SelectionLength))
        {
            throw new ArgumentException("选择范围不能拆分 Unicode 字符或 CRLF 换行。", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Translation.Substring(request.SelectionStart, request.SelectionLength)))
        {
            throw new DeepSeekException("EMPTY_SELECTION", "请在译文中选择非空白文本。");
        }
    }

    internal static bool IsWellFormedUtf16(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false;
            }
            else if (char.IsLowSurrogate(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    // Text elements also protect combining marks, emoji ZWJ sequences and CRLF pairs.
    internal static HashSet<int> TextBoundaries(string text)
    {
        var result = new HashSet<int>(StringInfo.ParseCombiningCharacters(text)) { text.Length };
        return result;
    }
}

public sealed record AlignmentSpan(int Start, int Length);

/// <summary>An AI estimate, not a guarantee of semantic equivalence.</summary>
public sealed record AlignmentResult(IReadOnlyList<AlignmentSpan> SourceSpans, bool IsConfident, string? Message = null)
{
    public bool HasMatch => IsConfident && SourceSpans is { Count: > 0 };
}
