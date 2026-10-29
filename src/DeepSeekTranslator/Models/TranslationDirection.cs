namespace DeepSeekTranslator.Models;

public enum TranslationDirection
{
    ChineseToEnglish,
    EnglishToChinese,
    OtherToEnglish,
    Unknown
}

public sealed record LanguageDetection(
    TranslationDirection Direction,
    string SourceLanguage,
    string TargetLanguage,
    string Badge,
    string ResultLabel)
{
    public static LanguageDetection ChineseToEnglish { get; } = new(
        TranslationDirection.ChineseToEnglish,
        "Chinese",
        "English",
        "中 → 英",
        "英文译文");

    public static LanguageDetection EnglishToChinese { get; } = new(
        TranslationDirection.EnglishToChinese,
        "English",
        "Chinese",
        "英 → 中",
        "中文译文");

    public static LanguageDetection OtherToEnglish { get; } = new(
        TranslationDirection.OtherToEnglish,
        "Other",
        "English",
        "→ 英",
        "英文译文");

    public static LanguageDetection Unknown { get; } = new(
        TranslationDirection.Unknown,
        "Unknown",
        "English",
        "自动",
        "译文");
}
