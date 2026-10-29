namespace DeepSeekTranslator.Services;

/// <summary>
/// 按应用区分取词方式。名单里的应用不走 UIA 选区，直接走
/// 「自动 Ctrl+C + 剪贴板」；划词图标也不再要求 UIA 探测到选区。
///
/// 三类原因：
/// 1. 微信 / Zotero / Foxit / UPDF / Acrobat / SumatraPDF：
///    界面没有可用的 UIA TextPattern，UIA 必然读不到选区。
/// 2. PowerPoint（POWERPNT）：
///    UIA 里有文本节点，但 GetSelection() 经常返回空范围或整个文本框，
///    读不到用户拖选的那一段，因此也按剪贴板处理。
/// 3. 浏览器（Edge / Chrome / Firefox 等）：
///    Chromium 的 UIA 树首次查询要 1~2 秒，图标经常来不及出现；
///    取词改走剪贴板，图标则用「鼠标是否 I 型光标」判断是否刚选中了文字。
/// </summary>
public static class AppCompatibility
{
    private static readonly string[] ClipboardApps =
    {
        "Weixin",
        "WeChat",
        "WeixinApp",
        "WeChatAppEx",
        "zotero",
        "FoxitPDFEditor",
        "FoxitPDFReader",
        "Foxit Reader",
        "UPDF",
        "Acrobat",
        "AcroRd32",
        "SumatraPDF",
        "POWERPNT"
    };

    private static readonly string[] BrowserApps =
    {
        "msedge",
        "chrome",
        "firefox",
        "brave",
        "vivaldi",
        "opera",
        "360se",
        "360chrome",
        "QQBrowser",
        "SogouExplorer",
        "iexplore"
    };

    /// <summary>该应用是否只能通过剪贴板取词（UIA 选区不可用）。</summary>
    public static bool IsClipboardOnly(string? appName)
    {
        return Matches(ClipboardApps, appName);
    }

    /// <summary>该应用是否是浏览器（取词走剪贴板，图标按光标形状判断）。</summary>
    public static bool IsBrowser(string? appName)
    {
        return Matches(BrowserApps, appName);
    }

    private static bool Matches(string[] candidates, string? appName)
    {
        if (string.IsNullOrWhiteSpace(appName)) return false;

        foreach (var candidate in candidates)
        {
            if (appName.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
