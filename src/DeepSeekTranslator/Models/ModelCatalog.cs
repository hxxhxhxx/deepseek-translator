namespace DeepSeekTranslator.Models;

public sealed record ModelInfo(string Id, string Label, string Version, string Description);

public static class ModelCatalog
{
    public static readonly IReadOnlyList<ModelInfo> Models = new[]
    {
        new ModelInfo(
            "deepseek-flash",
            "DeepSeek V4.1 Flash",
            "DeepSeek-V4.1-Flash-1017",
            "最新、速度最快、价格最低，适合划词翻译（推荐）"),
        new ModelInfo(
            "deepseek-v4-pro",
            "DeepSeek V4 Pro",
            "DeepSeek-V4-Pro-0813",
            "质量更高、速度稍慢，适合长文或专业文本")
    };

    public const string DefaultModel = "deepseek-flash";

    public static bool IsSupported(string? id)
    {
        return Models.Any(model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public static ModelInfo Get(string? id)
    {
        return Models.FirstOrDefault(
                   model => string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase))
               ?? Models[0];
    }
}
