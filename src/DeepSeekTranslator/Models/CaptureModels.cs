namespace DeepSeekTranslator.Models;

public enum CaptureMethod
{
    Uia,
    Clipboard,
    ManualClipboard,
    Ocr
}

public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);

    public int Height => Math.Max(0, Bottom - Top);

    public bool IsEmpty => Width <= 1 || Height <= 1;

    public int CenterX => Left + Width / 2;

    public int CenterY => Top + Height / 2;

    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public static PixelRect FromLtrb(double left, double top, double right, double bottom)
    {
        return new PixelRect(
            (int)Math.Round(left),
            (int)Math.Round(top),
            (int)Math.Round(right),
            (int)Math.Round(bottom));
    }
}

public sealed record CaptureResult(
    string Text,
    PixelRect? AnchorRect,
    CaptureMethod Method,
    string AppName);

public sealed record ValidationResult(
    bool IsValid,
    string NormalizedText,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static ValidationResult Ok(string text) => new(true, text, null, null);

    public static ValidationResult Fail(string code, string message, string text = "") =>
        new(false, text, code, message);
}

public sealed record TranslationResult(
    string Translation,
    string ModelLabel,
    string ModelVersion,
    string RequestedModel,
    string? ResponseModel,
    bool Truncated,
    int? TotalTokens,
    long LatencyMs);
