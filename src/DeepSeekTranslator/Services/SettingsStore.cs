using System.IO;
using System.Text.Json;
using DeepSeekTranslator.Models;

namespace DeepSeekTranslator.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(AppPaths.SettingsFile);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            MigrateLegacy(json, settings);
            Normalize(settings);
            return settings;
        }
        catch (Exception ex)
        {
            Logger.Error("读取设置失败，使用默认设置", ex);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Normalize(settings);
        AppPaths.EnsureDirectories();

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var temp = AppPaths.SettingsFile + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, AppPaths.SettingsFile, overwrite: true);
    }

    private static void MigrateLegacy(string json, AppSettings settings)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;

            // 1.3.0 之前只有「禁用全局快捷键」，现在合并为「游戏模式」。
            if (document.RootElement.TryGetProperty("HotkeysDisabled", out var legacy) &&
                legacy.ValueKind == JsonValueKind.True)
            {
                settings.GameMode = true;
            }
        }
        catch
        {
            // 旧配置解析失败时忽略迁移。
        }
    }

    private static void Normalize(AppSettings settings)
    {
        if (settings.Model is not ("deepseek-flash" or "deepseek-v4-pro"))
        {
            settings.Model = "deepseek-flash";
        }

        if (string.IsNullOrWhiteSpace(settings.UpdateFeed))
        {
            settings.UpdateFeed = AppSettings.DefaultUpdateFeed;
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        settings.TranslateSelectionHotkey = NormalizeHotkey(
            settings.TranslateSelectionHotkey,
            HotkeyParser.DefaultTranslateSelection,
            used);
        settings.TranslateClipboardHotkey = NormalizeHotkey(
            settings.TranslateClipboardHotkey,
            HotkeyParser.DefaultTranslateClipboard,
            used);
        settings.OcrHotkey = NormalizeHotkey(
            settings.OcrHotkey,
            HotkeyParser.DefaultOcr,
            used);
        settings.CopyLastHotkey = NormalizeHotkey(
            settings.CopyLastHotkey,
            HotkeyParser.DefaultCopyLast,
            used);
    }

    private static string NormalizeHotkey(
        string? value,
        string fallback,
        HashSet<string> used)
    {
        if (HotkeyParser.TryParse(value, out var parsed) && used.Add(parsed.Display))
        {
            return parsed.Display;
        }

        var candidates = new[]
        {
            fallback,
            HotkeyParser.DefaultTranslateSelection,
            HotkeyParser.DefaultTranslateClipboard,
            HotkeyParser.DefaultOcr,
            HotkeyParser.DefaultCopyLast
        };

        foreach (var candidate in candidates)
        {
            if (!HotkeyParser.TryParse(candidate, out var candidateParsed)) continue;
            if (used.Add(candidateParsed.Display)) return candidateParsed.Display;
        }

        // 四个默认键都已被占用时保持原值，交给设置页/热键服务给出冲突提示。
        return fallback;
    }

}
