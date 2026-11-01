using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeepSeekTranslator.Models;

namespace DeepSeekTranslator.Services;

/// <summary>
/// 翻译结果缓存：同一个（文本 + 方向 + 模型 + 术语表）直接返回上次结果。
/// 内存 LRU 最多 200 条，并落盘到数据目录，重启后仍然有效。
/// </summary>
public sealed class TranslationCache : IDisposable
{
    private const int MaxEntries = 200;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private readonly object _sync = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly string _path;
    private readonly System.Threading.Timer _saveTimer;
    private bool _dirty;
    private bool _disposed;

    public TranslationCache()
    {
        AppPaths.EnsureDirectories();
        _path = Path.Combine(AppPaths.DataDirectory, "translation-cache.json");
        _saveTimer = new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        Load();
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    public static string BuildKey(
        string text,
        LanguageDetection detection,
        string model,
        string glossaryFingerprint)
    {
        var raw = string.Join(
            '\u0001',
            model,
            detection.SourceLanguage,
            detection.TargetLanguage,
            glossaryFingerprint,
            text);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash);
    }

    public bool TryGet(string key, out TranslationResult result)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (DateTimeOffset.UtcNow - entry.SavedAt <= MaxAge)
                {
                    entry.LastUsed = DateTimeOffset.UtcNow;
                    result = entry.Result with { LatencyMs = 0 };
                    return true;
                }

                _entries.Remove(key);
                _dirty = true;
            }
        }

        result = null!;
        return false;
    }

    public void Set(string key, TranslationResult result)
    {
        if (_disposed) return;

        lock (_sync)
        {
            _entries[key] = new CacheEntry
            {
                Result = result with { LatencyMs = 0 },
                SavedAt = DateTimeOffset.UtcNow,
                LastUsed = DateTimeOffset.UtcNow
            };

            while (_entries.Count > MaxEntries)
            {
                var oldestKey = _entries
                    .OrderBy(pair => pair.Value.LastUsed)
                    .Select(pair => pair.Key)
                    .FirstOrDefault();
                if (oldestKey is null) break;
                _entries.Remove(oldestKey);
            }

            _dirty = true;
        }

        _saveTimer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _dirty = true;
        }

        Flush();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;

            var json = File.ReadAllText(_path, Encoding.UTF8);
            var items = JsonSerializer.Deserialize<List<CacheEntry>>(json);
            if (items is null) return;

            var now = DateTimeOffset.UtcNow;
            lock (_sync)
            {
                foreach (var item in items)
                {
                    if (string.IsNullOrEmpty(item.Key) || item.Result is null) continue;
                    if (now - item.SavedAt > MaxAge) continue;
                    if (_entries.Count >= MaxEntries) break;
                    _entries[item.Key] = item;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("读取翻译缓存失败", ex);
        }
    }

    private void Flush()
    {
        if (!_dirty) return;

        CacheEntry[] snapshot;
        lock (_sync)
        {
            _dirty = false;
            snapshot = _entries.Values
                .OrderByDescending(entry => entry.LastUsed)
                .ToArray();
        }

        try
        {
            AppPaths.EnsureDirectories();
            var json = JsonSerializer.Serialize(snapshot);
            File.WriteAllText(_path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            Logger.Error("保存翻译缓存失败", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _saveTimer.Dispose();
        Flush();
    }

    private sealed class CacheEntry
    {
        public string Key { get; set; } = "";
        public TranslationResult Result { get; set; } = null!;
        public DateTimeOffset SavedAt { get; set; }
        public DateTimeOffset LastUsed { get; set; }
    }
}
