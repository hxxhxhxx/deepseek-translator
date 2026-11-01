using System.Text;
using System.Text.RegularExpressions;

namespace DeepSeekTranslator.Services;

/// <summary>
/// 把 PDF/论文里夹带的 LaTeX 标记尽量转成可读的 Unicode：
/// <c>\(\boldsymbol{\Gamma}_{11}\)</c> → <c>Γ₁₁</c>。
/// 只在检测到 LaTeX 命令时处理，避免误伤价格里的 $ 或代码里的反斜杠。
/// </summary>
public static class MathText
{
    private static readonly Regex LatexCommand = new(@"\\[A-Za-z]+", RegexOptions.Compiled);

    private static readonly (string Name, string Symbol)[] Symbols =
    {
        ("Gamma", "Γ"), ("Delta", "Δ"), ("Theta", "Θ"), ("Lambda", "Λ"),
        ("Xi", "Ξ"), ("Pi", "Π"), ("Sigma", "Σ"), ("Phi", "Φ"),
        ("Psi", "Ψ"), ("Omega", "Ω"),
        ("alpha", "α"), ("beta", "β"), ("gamma", "γ"), ("delta", "δ"),
        ("epsilon", "ε"), ("varepsilon", "ε"), ("zeta", "ζ"), ("eta", "η"),
        ("theta", "θ"), ("iota", "ι"), ("kappa", "κ"), ("lambda", "λ"),
        ("mu", "μ"), ("nu", "ν"), ("xi", "ξ"), ("pi", "π"),
        ("rho", "ρ"), ("sigma", "σ"), ("tau", "τ"), ("upsilon", "υ"),
        ("phi", "φ"), ("varphi", "φ"), ("chi", "χ"), ("psi", "ψ"),
        ("omega", "ω"),
        ("times", "×"), ("cdot", "·"), ("pm", "±"), ("mp", "∓"),
        ("leq", "≤"), ("le", "≤"), ("geq", "≥"), ("ge", "≥"),
        ("neq", "≠"), ("ne", "≠"), ("approx", "≈"), ("equiv", "≡"),
        ("infty", "∞"), ("partial", "∂"), ("nabla", "∇"),
        ("sum", "∑"), ("prod", "∏"), ("int", "∫"),
        ("rightarrow", "→"), ("to", "→"), ("leftarrow", "←"),
        ("Rightarrow", "⇒"), ("Leftarrow", "⇐"),
        ("propto", "∝"), ("sim", "∼"), ("degree", "°"),
        ("circ", "∘"), ("ldots", "…"), ("cdots", "⋯")
    };

    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        if (!LatexCommand.IsMatch(text)) return text;

        var value = text;

        // 公式定界符
        value = Regex.Replace(value, @"\\[()\[\]]", string.Empty);
        value = Regex.Replace(value, @"\${1,2}", string.Empty);

        // 字体/文本包装：\boldsymbol{\Gamma} → \Gamma（嵌套时多剥几层）
        for (var i = 0; i < 3; i++)
        {
            var next = Regex.Replace(
                value,
                @"\\(?:boldsymbol|mathbf|mathrm|mathit|mathsf|text|mathcal|operatorname)\s*\{([^{}]*)\}",
                "$1");
            if (next == value) break;
            value = next;
        }

        value = Regex.Replace(value, @"\\(?:left|right)\b", string.Empty);
        value = Regex.Replace(value, @"\\[,;:! ]", " ");

        foreach (var (name, symbol) in Symbols)
        {
            value = value.Replace("\\" + name, symbol, StringComparison.Ordinal);
        }

        // 下标/上标：_{11} → ₁₁，^{2} → ²
        value = Regex.Replace(value, @"_\{?([0-9A-Za-z]+)\}?", match => Subscript(match.Groups[1].Value));
        value = Regex.Replace(value, @"\^\{?([0-9A-Za-z]+)\}?", match => Superscript(match.Groups[1].Value));

        // 剩余的花括号只是分组，去掉更易读
        value = value.Replace("{", string.Empty).Replace("}", string.Empty);

        return Regex.Replace(value, @"[ \t]{2,}", " ").Trim();
    }

    private static string Subscript(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(ch switch
            {
                '0' => '₀', '1' => '₁', '2' => '₂', '3' => '₃', '4' => '₄',
                '5' => '₅', '6' => '₆', '7' => '₇', '8' => '₈', '9' => '₉',
                'n' => 'ₙ', 'i' => 'ᵢ', 'j' => 'ⱼ', 'k' => 'ₖ', 'm' => 'ₘ',
                'x' => 'ₓ', 'a' => 'ₐ', 'e' => 'ₑ', 'o' => 'ₒ', 'p' => 'ₚ',
                _ => ch
            });
        }

        return builder.ToString();
    }

    private static string Superscript(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(ch switch
            {
                '0' => '⁰', '1' => '¹', '2' => '²', '3' => '³', '4' => '⁴',
                '5' => '⁵', '6' => '⁶', '7' => '⁷', '8' => '⁸', '9' => '⁹',
                'n' => 'ⁿ', 'i' => 'ⁱ',
                _ => ch
            });
        }

        return builder.ToString();
    }
}
