using System.Text;
using DeepSeekTranslator.Models;
using DeepSeekTranslator.Services;

Console.OutputEncoding = Encoding.UTF8;
if (args.Length != 1 || args[0] != "--live")
{
    Console.WriteLine("SKIP: 显式传入 --live 才会使用本机 Key 对公开示例发起一次收费 API 请求。");
    return 0;
}

string? key = null;
try
{
    key = new SecretStore().Load();
    if (string.IsNullOrWhiteSpace(key))
    {
        Console.WriteLine("SKIP: 本机尚未配置 Key，不创建或覆盖任何凭据。");
        return 2;
    }
    const string source = "This applies to all devices, in particular to low-power amplifiers.";
    const string translation = "这适用于所有器件，尤其适用于低功耗放大器。";
    var request = new AlignmentRequest(source, translation, translation.IndexOf("尤其", StringComparison.Ordinal), 2);
    using var client = new DeepSeekClient();
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var result = await client.AlignSelectionAsync(request, key, ModelCatalog.DefaultModel, cancellation.Token);
    var quotes = result.SourceSpans.Select(span => source.Substring(span.Start, span.Length)).ToArray();
    Console.WriteLine("LIVE_ALIGNMENT_QUOTES=" + string.Join(" | ", quotes));
    if (!result.HasMatch || !quotes.Contains("in particular", StringComparer.Ordinal))
    {
        Console.WriteLine("FAIL: 公开示例未得到预期对应，未伪造结果。");
        return 1;
    }
    Console.WriteLine("LIVE_ALIGNMENT_OK: 尤其 -> in particular");
    return 0;
}
catch (DeepSeekException ex)
{
    Console.WriteLine("LIVE_ALIGNMENT_ERROR=" + ex.Code);
    return 1;
}
catch (Exception ex)
{
    Console.WriteLine("LIVE_ALIGNMENT_ERROR_TYPE=" + ex.GetType().Name);
    return 1;
}
finally
{
    key = null;
}
