namespace DeepSeekTranslator.Models;

public sealed class CaptureException : Exception
{
    public CaptureException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class DeepSeekException : Exception
{
    public DeepSeekException(string code, string message, int statusCode = 0, string? detail = null)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
        Detail = detail;
    }

    public string Code { get; }

    public int StatusCode { get; }

    public string? Detail { get; }
}
