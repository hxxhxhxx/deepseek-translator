using DeepSeekTranslator.Models;

namespace DeepSeekTranslator.Services;

/// <summary>
/// Optional selection-alignment coordinator. Memory only; no keys, logs or disk persistence.
/// The caller owns the injected client and must gate calls on user opt-in and finished selections.
/// </summary>
public sealed class TextAlignmentService
{
    private const int CacheCapacity = 64;
    private readonly DeepSeekClient _client;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> _cache = new();
    private readonly LinkedList<CacheEntry> _lru = new();
    private long _revision;

    public TextAlignmentService(DeepSeekClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public async Task<AlignmentResult> AlignAsync(
        AlignmentRequest request,
        string apiKey,
        string model,
        CancellationToken ct)
    {
        AlignmentRequest.Validate(request);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new DeepSeekException("NO_API_KEY", "尚未配置 DeepSeek API Key。请打开设置填入 Key。");
        }

        var key = new CacheKey(request.Source, request.Translation, request.SelectionStart,
            request.SelectionLength, ModelCatalog.Get(model).Id);
        long revision;
        lock (_sync)
        {
            ct.ThrowIfCancellationRequested();
            revision = _revision;
            if (TryCached(key, out var cached)) return cached;
        }

        // No background task or shared CancellationTokenSource: queued and active callers use their own token.
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                ct.ThrowIfCancellationRequested();
                if (_revision != revision) return StaleResult();
                if (TryCached(key, out var cached)) return cached;
            }

            var result = await _client.AlignSelectionAsync(request, apiKey, key.Model, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            lock (_sync)
            {
                ct.ThrowIfCancellationRequested();
                // Clear during HTTP work invalidates that result as well as the old memory cache.
                if (_revision != revision) return StaleResult();
                var node = _lru.AddFirst(new CacheEntry(key, result));
                _cache.Add(key, node);
                if (_cache.Count > CacheCapacity)
                {
                    var last = _lru.Last!;
                    _cache.Remove(last.Value.Key);
                    _lru.RemoveLast();
                }
            }

            return result;
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>
    /// Clears memory and suppresses results/queued work started before this call. Does not dispose the
    /// injected client or cancel an HTTP request; the UI should also cancel its own token on disable/revision.
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            _revision++;
            _cache.Clear();
            _lru.Clear();
        }
    }

    // Called only while holding _sync. Client-produced span lists are read-only, safe to share from cache.
    private bool TryCached(CacheKey key, out AlignmentResult result)
    {
        if (_cache.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            result = node.Value.Result;
            return true;
        }

        result = null!;
        return false;
    }

    private static AlignmentResult StaleResult() =>
        new(Array.Empty<AlignmentSpan>(), false, "对照内容已更新，已忽略旧结果。");

    private sealed record CacheKey(string Source, string Translation, int Start, int Length, string Model);
    private sealed record CacheEntry(CacheKey Key, AlignmentResult Result);
}
