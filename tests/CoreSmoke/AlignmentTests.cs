using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DeepSeekTranslator.Models;
using DeepSeekTranslator.Services;

public static class AlignmentTests
{
    private const string FakeKey = "sk-alignment-mock-only";
    private const string Model = "deepseek-flash";
    private static readonly AlignmentRequest Basic = new("这点尤其重要。", "This matters in particular.", 13, 13);

    public static async Task RunAsync(Action<bool, string> check)
    {
        await TestPayloadAsync(check);
        await TestResolutionAsync(check);
        await TestValidationAsync(check);
        await TestFailuresAsync(check);
        await TestCacheAsync(check);
        await TestConcurrencyAsync(check);
    }

    private static async Task TestPayloadAsync(Action<bool, string> check)
    {
        string? sent = null;
        string? sentKey = null;
        using var client = new DeepSeekClient(new AlignmentHandler(async (message, ct) =>
        {
            sentKey = message.Headers.Authorization?.Parameter;
            sent = await message.Content!.ReadAsStringAsync(ct);
            return Reply(Schema("high", ("尤其", "点", "重要")));
        }));
        var source = "  这点尤其重要。\r\n";
        var translation = "  This matters in particular.\r\n";
        var start = translation.IndexOf("in particular", StringComparison.Ordinal);
        var request = new AlignmentRequest(source, translation, start, 13);
        var result = await client.AlignSelectionAsync(request, FakeKey, Model, CancellationToken.None);
        check(result.HasMatch && result.SourceSpans.SequenceEqual(new[] { new AlignmentSpan(4, 2) }),
            "Alignment: 尤其 -> in particular 精确原文偏移");
        check(request.SelectedText == "in particular", "Alignment: SelectedText 保留 UTF-16 选择");
        using var body = JsonDocument.Parse(sent!);
        var root = body.RootElement;
        check(root.GetProperty("stream").GetBoolean() == false &&
              root.GetProperty("thinking").GetProperty("type").GetString() == "disabled" &&
              root.GetProperty("response_format").GetProperty("type").GetString() == "json_object",
            "Alignment: 非流式 JSON + 禁用思考");
        using var data = JsonDocument.Parse(root.GetProperty("messages")[1].GetProperty("content").GetString()!);
        check(data.RootElement.GetProperty("source").GetString() == source &&
              data.RootElement.GetProperty("translation").GetString() == translation &&
              data.RootElement.GetProperty("selection_start_utf16").GetInt32() == start &&
              data.RootElement.GetProperty("selected_text").GetString() == "in particular" &&
              data.RootElement.GetProperty("context_after").GetString() == ".\r\n",
            "Alignment: 原文/译文/上下文不 trim、不规范化 CRLF");
        check(root.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("UNTRUSTED DATA") &&
              sentKey == FakeKey && !sent!.Contains(FakeKey, StringComparison.Ordinal),
            "Alignment: 不可信数据隔离且 Key 不放入提示词");
    }

    private static async Task TestResolutionAsync(Action<bool, string> check)
    {
        var repeated = new AlignmentRequest("第一处尤其重要；第二处尤其明显。", "First in particular; second in particular.", 28, 13);
        check(!(await Resolve(repeated, Schema("high", ("尤其", "", "")))).HasMatch,
            "Alignment: 重复原词缺少锚点不猜第一处");
        var anchored = await Resolve(repeated, Schema("high", ("尤其", "第二处", "明显")));
        check(anchored.HasMatch && anchored.SourceSpans[0].Start == repeated.Source.LastIndexOf("尤其", StringComparison.Ordinal),
            "Alignment: 立即相邻锚点唯一定位第二处");
        check(!(await Resolve(repeated, Schema("high", ("尤其", "不存在", "明显")))).HasMatch,
            "Alignment: 错误锚点拒绝");
        var identical = new AlignmentRequest("A term B A term B", "one term", 4, 4);
        check(!(await Resolve(identical, Schema("high", ("term", "A ", " B")))).HasMatch,
            "Alignment: 锚点也重复时仍不高亮");
        check(!(await Resolve(new("aaa", "two", 0, 3), Schema("high", ("aa", "", "")))).HasMatch,
            "Alignment: 重叠重复 quote 不猜第一处");

        var unicode = new AlignmentRequest("前😀后\r\n尤其", "A😀B\r\nin particular", 6, 13);
        var emoji = await Resolve(unicode, Schema("high", ("😀", "前", "后")));
        check(emoji.HasMatch && emoji.SourceSpans[0] == new AlignmentSpan(1, 2),
            "Alignment: emoji 长度为两个 UTF-16 单元");
        var crlf = await Resolve(unicode, Schema("high", ("后\r\n尤其", "😀", "")));
        check(crlf.HasMatch && crlf.SourceSpans[0] == new AlignmentSpan(3, 5),
            "Alignment: 多行 quote 精确保留 CRLF 偏移");
        check(!(await Resolve(unicode, Schema("high", ("\n尤其", "", "")))).HasMatch,
            "Alignment: 原文 span 不能拆分 CRLF");
        var combining = new AlignmentRequest("cafe\u0301", "cafe", 0, 4);
        check(!(await Resolve(combining, Schema("high", ("e", "", "")))).HasMatch &&
              (await Resolve(combining, Schema("high", ("e\u0301", "caf", "")))).HasMatch,
            "Alignment: 组合字符不能拆分、完整组合字符可匹配");
        check(!(await Resolve(new("a👩‍💻b", "developer", 0, 9), Schema("high", ("👩", "", "")))).HasMatch,
            "Alignment: emoji ZWJ 序列不拆分");
        await ExpectRejectedAsync(() => Resolve(unicode,
            """{"confidence":"high","spans":[{"quote":"\uD83D","before":"","after":""}]}"""),
            check, "Alignment: 模型孤立 surrogate 被拒绝");

        var multi = await Resolve(new("既快又准", "fast and accurate", 0, 17),
            Schema("high", ("准", "又", ""), ("快", "既", "又")));
        check(multi.HasMatch && multi.SourceSpans.SequenceEqual(new[] { new AlignmentSpan(1, 1), new AlignmentSpan(3, 1) }),
            "Alignment: 非连续多 span 返回原文顺序");
        check(!(await Resolve(Basic, Schema("high", ("尤其", "", ""), ("尤其重要", "", "")))).HasMatch,
            "Alignment: 重复/重叠 span 整体拒绝");
        check(!(await Resolve(Basic, Schema("high", ("完全不存在", "", "")))).HasMatch,
            "Alignment: 幻觉 quote 不高亮");
        check(!(await Resolve(Basic, Schema("high", ("尤其", "", ""), ("幻觉", "", "")))).HasMatch,
            "Alignment: 部分幻觉不能部分成功");
        check(!(await Resolve(Basic, Schema("low", ("尤其", "", "")))).HasMatch &&
              !(await Resolve(Basic, Schema("none"))).HasMatch,
            "Alignment: 低置信/无匹配不高亮");
        check(!(await Resolve(new(new string('甲', 100), "a longer translation", 0, 1),
            Schema("high", (new string('甲', 100), "", "")))).HasMatch,
            "Alignment: 小选择对应过宽原文拒绝");
    }

    private static async Task TestValidationAsync(Action<bool, string> check)
    {
        var sent = 0;
        using var client = new DeepSeekClient(new AlignmentHandler((_, _) =>
        {
            sent++;
            return Task.FromResult(Reply(Schema("none")));
        }));
        var invalid = new AlignmentRequest[]
        {
            new("source", "text", -1, 1), new("source", "text", 0, 0),
            new("source", "text", 3, 2), new("source", "text", int.MaxValue, int.MaxValue),
            new("source", " \r\n ", 0, 4), new(" ", "text", 0, 4),
            new(new string('a', 8001), "text", 0, 4),
            new("source", new string('a', 40001), 0, 4),
            new("source", new string('a', 401), 0, 401),
            new("source", "a😀b", 1, 1), new("source", "a😀b", 2, 1),
            new("source", "a\r\nb", 0, 2), new("source", "e\u0301", 0, 1),
            new("bad\ud83d", "text", 0, 4), new("source", "bad\udc00", 0, 3),
            new(null!, "text", 0, 4), new("source", null!, 0, 1)
        };
        foreach (var request in invalid)
        {
            await ExpectRejectedAsync(() => client.AlignSelectionAsync(request, FakeKey, Model, CancellationToken.None),
                check, $"Alignment: 无效/超限选择校验 #{Array.IndexOf(invalid, request) + 1}");
        }

        check(sent == 0, "Alignment: 无效选择不发送网络请求");
        var limit = new AlignmentRequest(new string('中', 8000), new string('a', 40000), 39600, 400);
        limit.Validate();
        check(limit.SelectedText.Length == 400, "Alignment: 长度上限及末端 UTF-16 offset 有效");
        var exact = new AlignmentRequest(" source ", " text ", 0, 6);
        check(exact.SelectedText == " text ", "Alignment: 合法选择两侧空格原样保留");
    }

    private static async Task TestFailuresAsync(Action<bool, string> check)
    {
        foreach (var invalid in new[]
        {
            "not JSON", "```json\n{}\n```", "{}", "[]",
            """{"confidence":true,"spans":[]}""",
            """{"confidence":"high","confidence":"none","spans":[]}""",
            """{"confidence":"high","spans":[{"quote":"尤其","before":"","after":"","start":3}]}""",
            """{"confidence":"high","spans":[{"quote":3,"before":"","after":""}]}""",
            Schema("high", Enumerable.Repeat(("尤其", "", ""), 6).ToArray())
        })
        {
            await ExpectCodeAsync(() => Resolve(Basic, invalid), "BAD_RESPONSE", check,
                "Alignment: 严格 JSON/schema 拒绝非法/偏移/过多 span");
        }

        foreach (var body in new[]
        {
            "{}", "[]", "{", """{"choices":[]}""", """{"choices":[null]}""",
            """{"error":{"message":"untrusted data"}}""",
            """{"choices":[{"finish_reason":"stop","message":{"content":""}}]}""",
            new string(' ', 65537)
        })
        {
            using var client = new DeepSeekClient(new AlignmentHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, body))));
            await ExpectCodeAsync(() => client.AlignSelectionAsync(Basic, FakeKey, Model, CancellationToken.None),
                "BAD_RESPONSE", check, "Alignment: 空 choices/坏外层 JSON/超大响应受控报错");
        }

        using (var client = new DeepSeekClient(new AlignmentHandler((_, _) => Task.FromResult(Reply(Schema("high", ("尤其", "", "")), "length")))))
        {
            check(!(await client.AlignSelectionAsync(Basic, FakeKey, Model, CancellationToken.None)).HasMatch,
                "Alignment: 截断响应不高亮");
        }

        foreach (var (status, code) in new[]
        {
            (HttpStatusCode.Unauthorized, "INVALID_API_KEY"), (HttpStatusCode.TooManyRequests, "RATE_LIMITED"),
            (HttpStatusCode.ServiceUnavailable, "SERVICE_UNAVAILABLE")
        })
        {
            using var client = new DeepSeekClient(new AlignmentHandler((_, _) => Task.FromResult(Json(status,
                """{"error":{"message":"must not reflect raw source or key"}}"""))));
            await ExpectCodeAsync(() => client.AlignSelectionAsync(Basic, FakeKey, Model, CancellationToken.None),
                code, check, $"Alignment: HTTP {(int)status} 映射且不回传原始 detail");
        }

        using (var client = new DeepSeekClient(new AlignmentHandler((_, _) => throw new HttpRequestException("mock transport detail"))))
        {
            await ExpectCodeAsync(() => client.AlignSelectionAsync(Basic, FakeKey, Model, CancellationToken.None),
                "NETWORK_ERROR", check, "Alignment: 网络错误不泄漏 transport detail");
        }
        using (var client = new DeepSeekClient(new AlignmentHandler((_, _) => throw new OperationCanceledException())))
        {
            await ExpectCodeAsync(() => client.AlignSelectionAsync(Basic, FakeKey, Model, CancellationToken.None),
                "TIMEOUT", check, "Alignment: 非用户取消映射 TIMEOUT");
        }

        var calls = 0;
        var started = Signal();
        using (var client = new DeepSeekClient(new AlignmentHandler(async (_, ct) =>
        {
            calls++;
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Reply(Schema("none"));
        })))
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await ExpectCancelledAsync(() => client.AlignSelectionAsync(Basic, FakeKey, Model, cancelled.Token),
                check, "Alignment: 预取消传播");
            check(calls == 0, "Alignment: 预取消不请求网络");
            using var active = new CancellationTokenSource();
            var task = client.AlignSelectionAsync(Basic, FakeKey, Model, active.Token);
            await started.Task;
            active.Cancel();
            await ExpectCancelledAsync(() => task, check, "Alignment: HTTP 中途取消传播");
        }
    }

    private static async Task TestCacheAsync(Action<bool, string> check)
    {
        var calls = 0;
        using var client = new DeepSeekClient(new AlignmentHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(Reply(Schema("high", ("尤其", "", ""))));
        }));
        var service = new TextAlignmentService(client);
        await service.AlignAsync(Basic, FakeKey, Model, CancellationToken.None);
        await service.AlignAsync(Basic with { }, FakeKey, Model, CancellationToken.None);
        check(calls == 1, "Alignment cache: 完全相同内容和位置复用");
        var repeated = new AlignmentRequest("尤其", "in particular / in particular", 0, 13);
        await service.AlignAsync(repeated, FakeKey, Model, CancellationToken.None);
        await service.AlignAsync(repeated with { SelectionStart = 16 }, FakeKey, Model, CancellationToken.None);
        check(calls == 3, "Alignment cache: 相同目标词不同重复位置不误复用");
        await service.AlignAsync(Basic with { Source = "尤其有用。" }, FakeKey, Model, CancellationToken.None);
        await service.AlignAsync(Basic with { Translation = Basic.Translation + "!" }, FakeKey, Model, CancellationToken.None);
        await service.AlignAsync(Basic, FakeKey, "deepseek-v4-pro", CancellationToken.None);
        check(calls == 6, "Alignment cache: 原文/译文/model 改变失效");
        await ExpectCodeAsync(() => service.AlignAsync(Basic, "", Model, CancellationToken.None),
            "NO_API_KEY", check, "Alignment cache: 命中也校验缺失 Key");
        service.Clear();
        await service.AlignAsync(Basic, FakeKey, Model, CancellationToken.None);
        check(calls == 7, "Alignment cache: Clear 清除旧数据且不 dispose client");

        var boundedCalls = 0;
        using var boundedClient = new DeepSeekClient(new AlignmentHandler((_, _) =>
        {
            boundedCalls++;
            return Task.FromResult(Reply(Schema("none")));
        }));
        var bounded = new TextAlignmentService(boundedClient);
        for (var i = 0; i < 65; i++)
            await bounded.AlignAsync(Basic with { Source = "尤其" + i }, FakeKey, Model, CancellationToken.None);
        await bounded.AlignAsync(Basic with { Source = "尤其64" }, FakeKey, Model, CancellationToken.None);
        check(boundedCalls == 65, "Alignment cache: 无匹配结果可缓存");
        await bounded.AlignAsync(Basic with { Source = "尤其0" }, FakeKey, Model, CancellationToken.None);
        check(boundedCalls == 66, "Alignment cache: 64 条有界内存 LRU 淘汰");

        var errorCalls = 0;
        using var errorClient = new DeepSeekClient(new AlignmentHandler((_, _) => Task.FromResult(
            ++errorCalls == 1 ? Json(HttpStatusCode.TooManyRequests, "{}") : Reply(Schema("none")))));
        var errors = new TextAlignmentService(errorClient);
        await ExpectCodeAsync(() => errors.AlignAsync(Basic, FakeKey, Model, CancellationToken.None),
            "RATE_LIMITED", check, "Alignment cache: 错误原样传播");
        await errors.AlignAsync(Basic, FakeKey, Model, CancellationToken.None);
        check(errorCalls == 2, "Alignment cache: 错误不缓存");
    }

    private static async Task TestConcurrencyAsync(Action<bool, string> check)
    {
        var calls = 0;
        var entered = Signal();
        var release = Signal();
        using (var client = new DeepSeekClient(new AlignmentHandler(async (_, ct) =>
        {
            calls++;
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Reply(Schema("high", ("尤其", "", "")));
        })))
        {
            var service = new TextAlignmentService(client);
            var first = service.AlignAsync(Basic, FakeKey, Model, CancellationToken.None);
            await entered.Task;
            var duplicate = service.AlignAsync(Basic, FakeKey, Model, CancellationToken.None);
            using var queuedCts = new CancellationTokenSource();
            var queued = service.AlignAsync(Basic with { Source = "尤其有用" }, FakeKey, Model, queuedCts.Token);
            queuedCts.Cancel();
            await ExpectCancelledAsync(() => queued, check, "Alignment concurrency: 排队取消传播");
            check(calls == 1, "Alignment concurrency: 等待期间串行避免 API 风暴");
            release.TrySetResult();
            var results = await Task.WhenAll(first, duplicate);
            check(calls == 1 && results.All(r => r.HasMatch), "Alignment concurrency: 并发相同请求等待后复用");
        }

        var staleCalls = 0;
        var staleEntered = Signal();
        var staleRelease = Signal();
        using (var client = new DeepSeekClient(new AlignmentHandler(async (_, ct) =>
        {
            if (++staleCalls == 1)
            {
                staleEntered.TrySetResult();
                await staleRelease.Task.WaitAsync(ct);
            }
            return Reply(Schema("high", ("尤其", "", "")));
        })))
        {
            var service = new TextAlignmentService(client);
            var old = service.AlignAsync(Basic, FakeKey, Model, CancellationToken.None);
            await staleEntered.Task;
            var queuedOld = service.AlignAsync(Basic with { Source = "尤其有用" }, FakeKey, Model, CancellationToken.None);
            service.Clear();
            staleRelease.TrySetResult();
            var stale = await Task.WhenAll(old, queuedOld);
            check(staleCalls == 1 && stale.All(r => !r.HasMatch),
                "Alignment concurrency: Clear 抑制旧响应并跳过旧排队请求");
            await service.AlignAsync(Basic, FakeKey, Model, CancellationToken.None);
            check(staleCalls == 2, "Alignment concurrency: 旧 revision 结果不缓存");
        }

        var cancelCalls = 0;
        var cancelEntered = Signal();
        using (var client = new DeepSeekClient(new AlignmentHandler(async (_, ct) =>
        {
            if (++cancelCalls == 1)
            {
                cancelEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return Reply(Schema("high", ("尤其", "", "")));
        })))
        {
            var service = new TextAlignmentService(client);
            using var cts = new CancellationTokenSource();
            var pending = service.AlignAsync(Basic, FakeKey, Model, cts.Token);
            await cancelEntered.Task;
            cts.Cancel();
            await ExpectCancelledAsync(() => pending, check, "Alignment concurrency: 活动取消不变成无匹配");
            var retried = await service.AlignAsync(Basic, FakeKey, Model, CancellationToken.None);
            check(cancelCalls == 2 && retried.HasMatch, "Alignment concurrency: 取消不缓存且释放串行门闩");
        }
    }

    private static async Task<AlignmentResult> Resolve(AlignmentRequest request, string json)
    {
        using var client = new DeepSeekClient(new AlignmentHandler((_, _) => Task.FromResult(Reply(json))));
        return await client.AlignSelectionAsync(request, FakeKey, Model, CancellationToken.None);
    }

    private static string Schema(string confidence, params (string Quote, string Before, string After)[] spans) =>
        JsonSerializer.Serialize(new { confidence, spans = spans.Select(s => new { quote = s.Quote, before = s.Before, after = s.After }) });

    private static HttpResponseMessage Reply(string json, string finish = "stop") => Json(HttpStatusCode.OK,
        JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = finish, message = new { content = json } } } }));

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task ExpectRejectedAsync(Func<Task> action, Action<bool, string> check, string name)
    {
        try
        {
            await action();
            check(false, name);
        }
        catch (Exception ex) when (ex is ArgumentException or DeepSeekException)
        {
            check(true, name);
        }
    }

    private static async Task ExpectCodeAsync(Func<Task> action, string code, Action<bool, string> check, string name)
    {
        try
        {
            await action();
            check(false, name);
        }
        catch (DeepSeekException ex)
        {
            check(ex.Code == code && string.IsNullOrEmpty(ex.Detail), name);
        }
    }

    private static async Task ExpectCancelledAsync(Func<Task> action, Action<bool, string> check, string name)
    {
        try
        {
            await action();
            check(false, name);
        }
        catch (OperationCanceledException)
        {
            check(true, name);
        }
    }

    private sealed class AlignmentHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handler(request, ct);
    }
}
