namespace DeepSeekTranslator.Services;

/// <summary>
/// 按应用区分取词方式。名单里的应用不走 UIA 选区，直接走
/// 「自动 Ctrl+C + 剪贴板」；划词图标也不再要求 UIA 探测到选区。
///
/// 三类原因：
/// 1. 微信 / Zotero / Foxit / UPDF / Acrobat / SumatraPDF / WPS：
///    界面没有可用的 UIA TextPattern，或 UIA 选区不可靠，UIA 必然读不到选区。
/// 2. PowerPoint（POWERPNT）：
///    UIA 里有文本节点，但 GetSelection() 经常返回空范围或整个文本框，
///    读不到用户拖选的那一段，因此也按剪贴板处理。
/// 3. 浏览器 / 小绿鲸（Electron）：
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
        "POWERPNT",

        // WPS Office：文字 / 表格 / 演示 / PDF / 云服务
        "wps",
        "et",
        "wpp",
        "wpspdf",
        "wpscloudsvr",

        // 小绿鲸英文文献阅读器（Electron，主程序为中文 exe 名）
        "小绿鲸英文文献阅读器",
        "xljsci",
        "xiaolvjing",
        "xlj"
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

    /// <summary>
    /// 窗口标题关键字兜底：进程名对不上时（尤其是 Electron 应用换了 exe 名），
    /// 标题里出现这些词也按剪贴板应用处理。
    /// </summary>
    private static readonly string[] ClipboardTitleKeywords =
    {
        "WPS",
        "小绿鲸",
        "XLJSCI",
        "英文文献阅读器"
    };

    /// <summary>
    /// 可执行文件路径关键字兜底：小绿鲸的安装包把中文 exe 名写成了乱码，
    /// 进程名匹配不到，但路径里的 xljsci 是稳定的 ASCII。
    /// </summary>
    private static readonly string[] ClipboardPathKeywords =
    {
        "xljsci",
        "kingsoft\\wps",
        "kingsoft/wps"
    };

    /// <summary>该应用是否只能通过剪贴板取词（UIA 选区不可用）。</summary>
    /// <param name="appName">前台进程名（不带 .exe）。</param>
    /// <param name="windowTitle">前台窗口标题，用于关键字兜底。</param>
    /// <param name="userList">设置里用户自定义的强制剪贴板进程名列表。</param>
    /// <param name="processPath">前台进程可执行文件路径，用于路径兜底。</param>
    public static bool IsClipboardOnly(
        string? appName,
        string? windowTitle = null,
        string? userList = null,
        string? processPath = null)
    {
        return Matches(ClipboardApps, appName)
               || MatchesPath(processPath)
               || MatchesTitle(windowTitle)
               || MatchesUserList(userList, appName, processPath);
    }

    /// <summary>该应用是否是浏览器（取词走剪贴板，图标按光标形状判断）。</summary>
    public static bool IsBrowser(string? appName)
    {
        return Matches(BrowserApps, appName);
    }

    /// <summary>诊断用：说明当前前台窗口为什么走剪贴板 / 浏览器 / UIA。</summary>
    public static string Describe(
        string? appName,
        string? windowTitle,
        string? userList,
        string? processPath = null)
    {
        if (Matches(ClipboardApps, appName))
        {
            return $"clipboard/process ({appName})";
        }

        if (MatchesPath(processPath))
        {
            return $"clipboard/path ({processPath})";
        }

        if (TryMatchTitle(windowTitle, out var keyword))
        {
            return $"clipboard/title ({keyword})";
        }

        if (MatchesUserList(userList, appName, processPath))
        {
            return $"clipboard/user ({appName})";
        }

        if (IsBrowser(appName))
        {
            return $"browser ({appName})";
        }

        return "uia";
    }

    /// <summary>解析用户自定义列表：换行、逗号、分号、竖线分隔，允许带 .exe。</summary>
    public static IReadOnlyList<string> ParseUserApps(string? userList)
    {
        if (string.IsNullOrWhiteSpace(userList))
        {
            return Array.Empty<string>();
        }

        return userList
            .Split(new[] { '\r', '\n', ',', ';', '，', '；', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Select(item => item.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? item[..^4]
                : item)
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool MatchesUserList(string? userList, string? appName, string? processPath)
    {
        if (string.IsNullOrWhiteSpace(appName) && string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        foreach (var item in ParseUserApps(userList))
        {
            if (!string.IsNullOrWhiteSpace(appName) &&
                appName.Equals(item, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 用户填的是路径关键字（例如 xljsci）时也让它命中。
            if (!string.IsNullOrWhiteSpace(processPath) &&
                processPath.Contains(item, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesPath(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        foreach (var candidate in ClipboardPathKeywords)
        {
            if (processPath.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesTitle(string? windowTitle)
    {
        return TryMatchTitle(windowTitle, out _);
    }

    private static bool TryMatchTitle(string? windowTitle, out string keyword)
    {
        keyword = "";
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            return false;
        }

        foreach (var candidate in ClipboardTitleKeywords)
        {
            if (windowTitle.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                keyword = candidate;
                return true;
            }
        }

        return false;
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
