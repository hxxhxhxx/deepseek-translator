using System.Net;
using System.Text;
using System.Text.Json;
using DeepSeekTranslator.Models;
using DeepSeekTranslator.Services;

Console.OutputEncoding = Encoding.UTF8;

var failures = new List<string>();

void Check(bool condition, string name)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {name}");
    if (!condition) failures.Add(name);
}

var empty = TextValidator.Validate("");
Check(!empty.IsValid && empty.ErrorCode == "EMPTY_SELECTION", "空文本被拒绝");

var longText = new string('a', 8001);
var tooLong = TextValidator.Validate(longText);
Check(!tooLong.IsValid && tooLong.ErrorCode == "TEXT_TOO_LONG", "超过 8000 字符被拒绝");

var symbols = TextValidator.Validate("12345 !!! 😀");
Check(!symbols.IsValid && symbols.ErrorCode == "NO_TRANSLATABLE_TEXT", "纯符号/数字被拒绝");

var okText = TextValidator.Validate("Hello DeepSeek");
Check(okText.IsValid && okText.NormalizedText == "Hello DeepSeek", "正常文本通过");

Check(MathText.Normalize(@"\(\boldsymbol{\Gamma}_{11}\)") == "Γ₁₁", "LaTeX 数学符号正常化");
Check(MathText.Normalize("The cost is $5.") == "The cost is $5.", "普通 $ 不被误删");
Check(MathText.Normalize(@"$f \times g \approx h$") == "f × g ≈ h", "LaTeX 运算符正常化");

var zhDetection = LanguageDetector.Detect("你好，世界。");
Check(zhDetection.Direction == TranslationDirection.ChineseToEnglish, "中文 → 英文方向检测");

Check(AppCompatibility.IsClipboardOnly("wps"), "WPS 走剪贴板");
Check(AppCompatibility.IsClipboardOnly("小绿鲸英文文献阅读器"), "小绿鲸走剪贴板");
Check(
    AppCompatibility.IsClipboardOnly("unknown", "论文.docx - WPS Office"),
    "窗口标题 WPS 兜底");
Check(
    AppCompatibility.IsClipboardOnly("unknown", "论文 - 小绿鲸英文文献阅读器"),
    "窗口标题小绿鲸兜底");
Check(
    AppCompatibility.IsClipboardOnly("myreader", null, "myreader\nfoo.exe"),
    "用户强制剪贴板列表");
Check(
    !AppCompatibility.IsClipboardOnly("notepad", "无标题 - 记事本"),
    "普通应用仍走 UIA");
Check(
    AppCompatibility.IsClipboardOnly("unknown", null, null, @"C:\Program Files\xljsci\小绿鲸英文文献阅读器.exe"),
    "可执行文件路径兜底 xljsci");
Check(
    AppCompatibility.Describe("wps", null, "").StartsWith("clipboard/process"),
    "诊断规则描述");
Check(AppCompatibility.IsBrowser("steamwebhelper"), "Steam 网页走浏览器模式");
Check(AppCompatibility.IsClipboardOnly("steam"), "Steam 主进程走剪贴板");
Check(AppCompatibility.IsClipboardOnly("gameoverlayui"), "Steam 游戏内覆盖层走剪贴板");
var enDetection = LanguageDetector.Detect("Hello world, this is English.");
Check(enDetection.Direction == TranslationDirection.EnglishToChinese, "英文 → 中文方向检测");
var mixedZh = LanguageDetector.Detect("使用 OpenAI API 调用 GPT-4 模型");
Check(mixedZh.Direction == TranslationDirection.ChineseToEnglish, "中文技术文本检测");
var mixedEn = LanguageDetector.Detect("Hello 中");
Check(mixedEn.Direction == TranslationDirection.EnglishToChinese, "英文中夹一个汉字检测");
var kana = LanguageDetector.Detect("こんにちは");
Check(kana.Direction == TranslationDirection.OtherToEnglish, "其他文字 → 英文兜底");

Check(HotkeyParser.TryParse("Alt+Ctrl+v", out var parsedHotkey) &&
      parsedHotkey.Display == "Ctrl+Alt+V", "快捷键解析 / 规范化");
Check(HotkeyParser.TryParse("Shift+Win+F5", out var parsedF5) &&
      parsedF5.Display == "Shift+Win+F5", "功能键快捷键解析");
Check(!HotkeyParser.TryParse("V", out _), "无修饰键快捷键被拒绝");
Check(!HotkeyParser.TryParse("Ctrl+Alt", out _), "只有修饰键被拒绝");
Check(!HotkeyParser.TryParse("Ctrl+V+E", out _), "多个不同主键被拒绝");
Check(HotkeyParser.TryParse("Ctrl+C+C", out var chordHotkey) &&
      chordHotkey.Display == "Ctrl+C+C" &&
      chordHotkey.IsDoubleTap, "双击式快捷键 Ctrl+C+C 解析");
Check(HotkeyParser.TryParse("Alt+Ctrl+c+c", out var chordNormalized) &&
      chordNormalized.Display == "Ctrl+Alt+C+C", "双击式快捷键解析 / 规范化");
Check(HotkeyParser.TryParse("Ctrl+C", out var singleTap) &&
      !singleTap.IsDoubleTap, "普通单键标记为非双击式");
Check(!HotkeyParser.TryParse("Ctrl+C+D", out _), "双击式快捷键必须按同一个键");

var secretStore = new SecretStore();
if (secretStore.HasSecret())
{
    Console.WriteLine("SKIP  SecretStore: 已存在真实 Key，不覆盖");
}
else
{
    const string testKey = "sk-dpapi-smoke-test-123456";
    secretStore.Save(testKey);
    Check(secretStore.Load() == testKey, "DPAPI 保存/读取");
    Check(secretStore.MaskedKey()?.Contains('…') == true, "Key 掩码");
    secretStore.Clear();
    Check(!secretStore.HasSecret(), "DPAPI 清除");
}

string? capturedBody = null;
using (var client = new DeepSeekClient(new StubHandler(async request =>
{
    capturedBody = request.Content is null
        ? null
        : await request.Content.ReadAsStringAsync();
    return JsonResponse(HttpStatusCode.OK, """
        {
          "id": "chatcmpl-test",
          "model": "deepseek-flash",
          "choices": [
            { "index": 0, "finish_reason": "stop", "message": { "role": "assistant", "content": "Hello, world." } }
          ],
          "usage": { "total_tokens": 42 }
        }
        """);
})))
{
    var result = await client.TranslateAsync(
        "你好，世界。",
        "sk-test-key",
        "deepseek-flash",
        zhDetection,
        CancellationToken.None);

    Check(result.Translation == "Hello, world.", "DeepSeek 响应解析");
    Check(result.TotalTokens == 42, "usage total_tokens 解析");
    Check(result.Truncated == false, "finish_reason 解析");

    using var body = JsonDocument.Parse(capturedBody ?? "{}");
    Check(body.RootElement.GetProperty("model").GetString() == "deepseek-flash", "请求 model 正确");
    Check(body.RootElement.GetProperty("thinking").GetProperty("type").GetString() == "disabled", "思考模式关闭");
    Check(body.RootElement.GetProperty("messages").GetArrayLength() == 2, "system + user 两条消息");

    var userContent = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString() ?? "";
    Check(userContent.Contains("\"target_language\":\"English\""), "请求目标语言为英文");
    Check(userContent.Contains("\"source_language\":\"Chinese\""), "请求源语言为中文");
}

string? capturedEnglishBody = null;
using (var client = new DeepSeekClient(new StubHandler(async request =>
{
    capturedEnglishBody = request.Content is null
        ? null
        : await request.Content.ReadAsStringAsync();
    return JsonResponse(HttpStatusCode.OK, """
        {
          "id": "chatcmpl-test-en",
          "model": "deepseek-flash",
          "choices": [
            { "index": 0, "finish_reason": "stop", "message": { "role": "assistant", "content": "你好，世界。" } }
          ],
          "usage": { "total_tokens": 38 }
        }
        """);
})))
{
    var enResult = await client.TranslateAsync(
        "Hello world.",
        "sk-test-key",
        "deepseek-flash",
        enDetection,
        CancellationToken.None);

    Check(enResult.Translation == "你好，世界。", "英译中响应解析");
    using var enBody = JsonDocument.Parse(capturedEnglishBody ?? "{}");
    var enUserContent = enBody.RootElement.GetProperty("messages")[1]
        .GetProperty("content").GetString() ?? "";
    Check(enUserContent.Contains("\"target_language\":\"Chinese\""), "请求目标语言为中文");
    Check(enUserContent.Contains("\"source_language\":\"English\""), "请求源语言为英文");
}

using (var client = new DeepSeekClient(new StubHandler(_ =>
{
    var sse = string.Join("\n", new[]
    {
        "data: {\"model\":\"deepseek-flash\",\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}",
        "",
        "data: {\"choices\":[{\"delta\":{\"content\":\", world.\"}}]}",
        "",
        "data: [DONE]",
        ""
    });

    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
    });
})))
{
    var partials = new List<string>();
    var streamResult = await client.TranslateStreamAsync(
        "你好，世界。",
        "sk-test-key",
        "deepseek-flash",
        zhDetection,
        null,
        partial => partials.Add(partial),
        CancellationToken.None);

    Check(streamResult.Translation == "Hello, world.", "流式响应拼接");
    Check(partials.Count >= 1 && partials[^1] == "Hello, world.", "流式增量回调");
}

string? capturedGlossaryBody = null;
using (var client = new DeepSeekClient(new StubHandler(async request =>
{
    capturedGlossaryBody = request.Content is null
        ? null
        : await request.Content.ReadAsStringAsync();
    return JsonResponse(HttpStatusCode.OK, """
        {
          "id": "chatcmpl-glossary",
          "model": "deepseek-flash",
          "choices": [
            { "index": 0, "finish_reason": "stop", "message": { "role": "assistant", "content": "LMBA test" } }
          ]
        }
        """);
})))
{
    await client.TranslateAsync(
        "LMBA 测试",
        "sk-test-key",
        "deepseek-flash",
        zhDetection,
        CancellationToken.None,
        "Terminology:\n- \"LMBA\" -> \"LMBA\"\n- \"PA\" -> \"功率放大器\"");

    using var glossaryBody = JsonDocument.Parse(capturedGlossaryBody ?? "{}");
    var systemContent = glossaryBody.RootElement.GetProperty("messages")[0]
        .GetProperty("content").GetString() ?? "";
    Check(systemContent.Contains("LMBA") && systemContent.Contains("功率放大器"), "术语表注入 system prompt");
}

string? capturedWordBody = null;
using (var client = new DeepSeekClient(new StubHandler(async request =>
{
    capturedWordBody = request.Content is null
        ? null
        : await request.Content.ReadAsStringAsync();
    return JsonResponse(HttpStatusCode.OK, """
        {
          "id": "chatcmpl-word",
          "model": "deepseek-flash",
          "choices": [
            { "index": 0, "finish_reason": "stop", "message": { "role": "assistant", "content": "音标：/ˈɑːtɪkl/\n词性：n. / v." } }
          ],
          "usage": { "total_tokens": 120 }
        }
        """);
})))
{
    var wordResult = await client.ExplainWordAsync(
        "article",
        "In this article",
        "sk-test-key",
        "deepseek-flash",
        CancellationToken.None);

    Check(wordResult.Translation.Contains("音标"), "单词详解返回内容");
    using var wordBody = JsonDocument.Parse(capturedWordBody ?? "{}");
    var wordUser = wordBody.RootElement.GetProperty("messages")[1]
        .GetProperty("content").GetString() ?? "";
    Check(
        wordUser.Contains("article") && wordUser.Contains("In this article"),
        "单词详解携带单词与上下文");
}

using (var client = new DeepSeekClient(new StubHandler(_ =>
{
    return Task.FromResult(JsonResponse(
        HttpStatusCode.Unauthorized,
        """{"error":{"message":"Authentication Fails"}}"""));
})))
{
    try
    {
        await client.TranslateAsync(
            "Hello",
            "bad-key",
            "deepseek-flash",
            enDetection,
            CancellationToken.None);
        Check(false, "401 应抛出异常");
    }
    catch (DeepSeekException ex)
    {
        Check(ex.Code == "INVALID_API_KEY", "401 映射为 INVALID_API_KEY");
    }
}

if (failures.Count > 0)
{
    Console.WriteLine($"FAILED: {string.Join(", ", failures)}");
    return 1;
}

Console.WriteLine("ALL CORE SMOKE TESTS PASSED");
return 0;

static HttpResponseMessage JsonResponse(HttpStatusCode status, string json)
{
    return new HttpResponseMessage(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}

internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

    public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        return _handler(request);
    }
}
