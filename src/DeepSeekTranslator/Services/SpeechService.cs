using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace DeepSeekTranslator.Services;

/// <summary>
/// 本地语音朗读：使用 Windows OneCore 语音（Windows.Media.SpeechSynthesis），
/// 不联网、不额外收费；只朗读传入的文本。
/// </summary>
public sealed class SpeechService : IDisposable
{
    private readonly SpeechSynthesizer _synthesizer = new();
    private readonly MediaPlayer _player = new();
    private SpeechSynthesisStream? _stream;
    private MediaSource? _source;
    private CancellationTokenSource? _cancellation;
    private bool _disposed;

    /// <summary>朗读开始/结束状态变化（可能在非 UI 线程触发）。</summary>
    public event Action<bool>? SpeakingChanged;

    public bool IsSpeaking { get; private set; }

    public SpeechService()
    {
        _player.MediaEnded += (_, _) => FinishPlayback();
        _player.MediaFailed += (_, args) =>
        {
            Logger.Error($"朗读播放失败：{args.Error} {args.ErrorMessage}");
            FinishPlayback();
        };
    }

    /// <summary>
    /// 朗读文本。language 用 BCP-47（如 zh-CN / en-US），rate 0.5~2.0。
    /// </summary>
    public async Task SpeakAsync(
        string text,
        string language,
        double rate,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var voice = SelectVoice(language)
                    ?? throw new InvalidOperationException("系统没有可用的语音，请先在 Windows 设置里安装语音包。");

        _synthesizer.Voice = voice;
        _synthesizer.Options.SpeakingRate = Math.Clamp(rate, 0.5, 2.0);

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cancellation.Token;

        var stream = await _synthesizer.SynthesizeTextToStreamAsync(text);
        if (token.IsCancellationRequested || _disposed)
        {
            stream.Dispose();
            return;
        }

        _stream = stream;
        _source = MediaSource.CreateFromStream(stream, stream.ContentType);
        _player.Source = _source;
        SetSpeaking(true);
        _player.Play();
    }

    /// <summary>停止朗读并释放当前音频流。</summary>
    public void Stop()
    {
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;

        try
        {
            _player.Pause();
            _player.Source = null;
        }
        catch (Exception ex)
        {
            Logger.Error("停止朗读失败", ex);
        }

        ReleaseStream();
        SetSpeaking(false);
    }

    private void FinishPlayback()
    {
        try
        {
            _player.Source = null;
        }
        catch (Exception ex)
        {
            Logger.Error("释放朗读音源失败", ex);
        }

        ReleaseStream();
        SetSpeaking(false);
    }

    private void ReleaseStream()
    {
        _stream?.Dispose();
        _stream = null;
        _source?.Dispose();
        _source = null;
    }

    private void SetSpeaking(bool value)
    {
        if (IsSpeaking == value)
        {
            return;
        }

        IsSpeaking = value;
        try
        {
            SpeakingChanged?.Invoke(value);
        }
        catch (Exception ex)
        {
            Logger.Error("朗读状态回调失败", ex);
        }
    }

    private static VoiceInformation? SelectVoice(string language)
    {
        var voices = SpeechSynthesizer.AllVoices;
        if (voices.Count == 0)
        {
            return null;
        }

        var isChinese = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var prefix = isChinese ? "zh" : "en";

        // 优先本地语音；Windows 11 的 “Online/Natural” 语音可能需要联网，避免误用。
        var local = voices
            .Where(v => !v.DisplayName.Contains("Online", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return local.FirstOrDefault(v => v.Language.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
               ?? voices.FirstOrDefault(v => v.Language.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
               ?? local.FirstOrDefault()
               ?? voices.FirstOrDefault();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _player.Dispose();
        _synthesizer.Dispose();
    }
}
