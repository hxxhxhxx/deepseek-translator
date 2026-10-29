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
