namespace DeepSeekTranslator.Models;

public sealed class AppSettings
{
    public const string DefaultUpdateFeed =
        "https://gitee.com/hexin1225/deepseek-translator/raw/master/latest.json";

    public string Model { get; set; } = "deepseek-flash";

    public bool AutoStart { get; set; } = true;

    public bool ShowOriginal { get; set; } = true;

    /// <summary>
    /// 选中文字后，在鼠标旁显示一个小翻译图标（托盘可随时关闭）。
    /// </summary>
    public bool ShowSelectionIcon { get; set; } = true;

    /// <summary>
    /// 游戏模式：同时暂停全局快捷键和划词图标，避免打游戏/录屏时冲突。
    /// </summary>
    public bool GameMode { get; set; }

    /// <summary>浮层宽度（DIP，320~960）；用户拖动边缘后记住。</summary>
    public double OverlayWidth { get; set; } = 440;

    /// <summary>浮层字号（12~24，默认 15）。</summary>
    public double OverlayFontSize { get; set; } = 15;

    /// <summary>朗读语速（0.5~2.0，默认 1.0）。</summary>
    public double SpeechRate { get; set; } = 1.0;

    /// <summary>浮层内容区最大高度（DIP，240~900，默认 500）；用户上下拖动后记住。</summary>
    public double OverlayMaxContentHeight { get; set; } = 500;

    /// <summary>
    /// 用户强制走剪贴板取词的应用（进程名，每行/逗号分隔，可带 .exe）。
    /// 用于 WPS、小绿鲸等 UIA 不可靠或未内置支持的软件。
    /// </summary>
    public string ForceClipboardApps { get; set; } = "";

    /// <summary>
    /// 术语表：每行一条 <c>原文=译文</c>；译文留空表示保留原文。
    /// </summary>
    public string Glossary { get; set; } = "";

    /// <summary>相同文本再次翻译时直接命中本地缓存。</summary>
    public bool EnableCache { get; set; } = true;

    /// <summary>边生成边显示译文；关闭后等完整结果再显示。</summary>
    public bool EnableStreaming { get; set; } = true;

    /// <summary>
    /// 固定翻译框：新翻译在原位置刷新，滚动/点击外部不自动关闭。
    /// 关闭后下一次翻译回到新选区旁。
    /// </summary>
    public bool PinOverlay { get; set; }

    /// <summary>上次固定的窗口左上角（屏幕物理像素）；跨重启恢复。</summary>
    public int? OverlayPinnedLeft { get; set; }

    public int? OverlayPinnedTop { get; set; }

    /// <summary>用户明确开启后，译文选词触发 AI 原文对照；默认关闭以避免额外计费。</summary>
    public bool EnableAlignment { get; set; }

    public string OcrLanguage { get; set; } = "";

    public string TranslateSelectionHotkey { get; set; } = "Ctrl+Alt+E";

    public string TranslateClipboardHotkey { get; set; } = "Ctrl+Alt+V";

    public string OcrHotkey { get; set; } = "Ctrl+Alt+O";

    public string CopyLastHotkey { get; set; } = "Ctrl+Alt+C";

    /// <summary>
    /// 更新清单地址：支持 https、http、file:// 和本地/共享目录路径。
    /// </summary>
    public string UpdateFeed { get; set; } = DefaultUpdateFeed;

    /// <summary>启动后是否自动检查更新。</summary>
    public bool AutoCheckUpdate { get; set; } = true;

    /// <summary>用户点了“稍后”的版本号，自动检查时不再打扰（强制更新除外）。</summary>
    public string SkippedVersion { get; set; } = "";
}
