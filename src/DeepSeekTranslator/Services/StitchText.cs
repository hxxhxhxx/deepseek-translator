using System.Text;

namespace DeepSeekTranslator.Services;

/// <summary>把跨页/多段选中文字拼成一段，处理页尾换行、连字符和中文标点。</summary>
public static class StitchText
{
    public static string Combine(IEnumerable<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var builder = new StringBuilder();
        foreach (var raw in segments)
        {
            var part = TextValidator.Normalize(raw);
            if (part.Length == 0) continue;
            if (builder.Length == 0)
            {
                builder.Append(part);
                continue;
            }

            var last = builder[^1];
            var first = part[0];

            // 论文页尾常把单词拆成 "inter-\nnational"。
            if (last == '-' && part.Length > 1 && char.IsLower(part[0]))
            {
                builder.Length--;
                builder.Append(part);
                continue;
            }

            if (char.IsWhiteSpace(last) || char.IsWhiteSpace(first))
            {
                builder.Append(part.TrimStart());
                continue;
            }

            // 中文之间不补空格；中文标点后接英文时补一个。
            if (IsCjk(last) || IsCjk(first))
            {
                builder.Append(part);
                continue;
            }

            if (last is '(' or '[' or '{' or '/' or '“' or '‘')
            {
                builder.Append(part);
                continue;
            }

            if (first is '.' or ',' or ';' or ':' or '?' or '!' or ')' or ']' or '}'
                or '，' or '。' or '；' or '：' or '？' or '！' or '、')
            {
                builder.Append(part);
                continue;
            }

            builder.Append(' ').Append(part);
        }

        return builder.ToString();
    }

    private static bool IsCjk(char value)
    {
        return value is >= '\u3400' and <= '\u9FFF'
            or >= '\uF900' and <= '\uFAFF';
    }
}
