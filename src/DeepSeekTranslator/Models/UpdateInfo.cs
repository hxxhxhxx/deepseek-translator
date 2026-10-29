using System.Text.Json.Serialization;

namespace DeepSeekTranslator.Models;

/// <summary>
/// 更新清单 latest.json 的内容。
/// </summary>
public sealed class UpdateInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("mandatory")]
    public bool Mandatory { get; set; }

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = string.Empty;

    [JsonPropertyName("published")]
    public string Published { get; set; } = string.Empty;
}

public sealed record UpdateCheckResult(
    UpdateInfo? Update,
    string? Error,
    string CurrentVersion,
    bool IsLatest);
