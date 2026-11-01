using System.Text;

namespace DeepSeekTranslator.Services;

/// <summary>一条术语：<paramref name="Target"/> 为空表示保留原文。</summary>
public sealed record GlossaryEntry(string Source, string Target);

/// <summary>
/// 术语表的解析与提示词拼装。格式：每行 <c>原文=译文</c>，
/// 以 <c>#</c> 开头的行视为注释，译文留空表示“保留原文不翻译”。
/// </summary>
public static class Glossary
{
    public const int MaxEntries = 200;

    public static IReadOnlyList<GlossaryEntry> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<GlossaryEntry>();

        var entries = new List<GlossaryEntry>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            var source = line[..separator].Trim();
            var target = line[(separator + 1)..].Trim();
            if (source.Length == 0) continue;

            entries.Add(new GlossaryEntry(source, target));
            if (entries.Count >= MaxEntries) break;
        }

        return entries;
    }

    /// <summary>生成追加到 system prompt 的术语约束；没有术语时返回空串。</summary>
    public static string BuildPrompt(IReadOnlyList<GlossaryEntry> entries)
    {
        if (entries.Count == 0) return string.Empty;

        var builder = new StringBuilder();
        builder.AppendLine("Terminology (apply exactly, keep the original casing):");
        foreach (var entry in entries)
        {
            builder.AppendLine(entry.Target.Length == 0
                ? $"- \"{entry.Source}\" -> keep as \"{entry.Source}\""
                : $"- \"{entry.Source}\" -> \"{entry.Target}\"");
        }

        return builder.ToString();
    }

    /// <summary>用于缓存键：术语表变了，缓存必须失效。</summary>
    public static string Fingerprint(IReadOnlyList<GlossaryEntry> entries)
    {
        return string.Join("\n", entries.Select(entry => $"{entry.Source}={entry.Target}"));
    }
}
