using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using DeepSeekTranslator.Models;

namespace DeepSeekTranslator.Services;

public sealed class TrayService : IDisposable
{
    private const string ProductName = "DeepSeek 划词翻译";

    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ToolStripMenuItem _gameModeItem;
    private readonly ToolStripMenuItem _translateSelectionItem;
    private readonly ToolStripMenuItem _translateClipboardItem;
    private readonly ToolStripMenuItem _ocrItem;
    private readonly ToolStripMenuItem _copyLastItem;

    public TrayService()
    {
        _icon = LoadIcon();
        _menu = new ContextMenuStrip();
        _autoStartItem = new ToolStripMenuItem("开机自启") { CheckOnClick = true };

        _translateSelectionItem = new ToolStripMenuItem("翻译当前选区")
        {
            ShortcutKeyDisplayString = "Ctrl+Alt+E"
        };
        _translateSelectionItem.Click += (_, _) => TranslateSelectionRequested?.Invoke();

        _translateClipboardItem = new ToolStripMenuItem("翻译剪贴板")
        {
            ShortcutKeyDisplayString = "Ctrl+Alt+V"
        };
        _translateClipboardItem.Click += (_, _) => TranslateClipboardRequested?.Invoke();

        _ocrItem = new ToolStripMenuItem("截图 OCR 翻译")
        {
            ShortcutKeyDisplayString = "Ctrl+Alt+O"
        };
        _ocrItem.Click += (_, _) => OcrRequested?.Invoke();

        _copyLastItem = new ToolStripMenuItem("复制最近译文")
        {
            ShortcutKeyDisplayString = "Ctrl+Alt+C"
        };
        _copyLastItem.Click += (_, _) => CopyLastRequested?.Invoke();

        _gameModeItem = new ToolStripMenuItem("游戏模式（暂停快捷键和划词图标）")
        {
            CheckOnClick = true,
            ToolTipText = "打游戏或录屏时勾选：全局快捷键和划词图标都会暂停，托盘菜单仍可翻译。"
        };
        _gameModeItem.Click += (_, _) =>
            GameModeChanged?.Invoke(_gameModeItem.Checked);

        var settings = new ToolStripMenuItem("设置");
        settings.Click += (_, _) => SettingsRequested?.Invoke();

        var hotkeySettings = new ToolStripMenuItem("快捷键设置…");
        hotkeySettings.Click += (_, _) => HotkeySettingsRequested?.Invoke();

        var checkUpdate = new ToolStripMenuItem("检查更新…");
        checkUpdate.Click += (_, _) => UpdateCheckRequested?.Invoke();

        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        _autoStartItem.Click += (_, _) => AutoStartChanged?.Invoke(_autoStartItem.Checked);

        _menu.Items.Add(_translateSelectionItem);
        _menu.Items.Add(_translateClipboardItem);
        _menu.Items.Add(_ocrItem);
        _menu.Items.Add(_copyLastItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_gameModeItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(settings);
        _menu.Items.Add(hotkeySettings);
        _menu.Items.Add(_autoStartItem);
        _menu.Items.Add(checkUpdate);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exit);

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = ProductName,
            Visible = true,
            ContextMenuStrip = _menu
        };
        _notifyIcon.DoubleClick += (_, _) => SettingsRequested?.Invoke();
    }

    public event Action? TranslateSelectionRequested;

    public event Action? TranslateClipboardRequested;

    public event Action? OcrRequested;

    public event Action? CopyLastRequested;

    public event Action? SettingsRequested;

    public event Action? HotkeySettingsRequested;

    public event Action? UpdateCheckRequested;

    public event Action? ExitRequested;

    public event Action<bool>? AutoStartChanged;

    public event Action<bool>? GameModeChanged;

    public void SetAutoStartChecked(bool value)
    {
        _autoStartItem.Checked = value;
    }

    public void SetGameMode(bool enabled)
    {
        _gameModeItem.Checked = enabled;
        _notifyIcon.Text = enabled ? $"{ProductName}（游戏模式）" : ProductName;
    }

    public void SetHotkeyLabels(AppSettings settings, bool gameMode = false)
    {
        var suffix = gameMode ? "（游戏模式）" : string.Empty;
        _translateSelectionItem.ShortcutKeyDisplayString = settings.TranslateSelectionHotkey + suffix;
        _translateClipboardItem.ShortcutKeyDisplayString = settings.TranslateClipboardHotkey + suffix;
        _ocrItem.ShortcutKeyDisplayString = settings.OcrHotkey + suffix;
        _copyLastItem.ShortcutKeyDisplayString = settings.CopyLastHotkey + suffix;
    }

    public void ShowBalloon(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        void Show()
        {
            try
            {
                _notifyIcon.BalloonTipTitle = title;
                _notifyIcon.BalloonTipText = message;
                _notifyIcon.BalloonTipIcon = icon;
                _notifyIcon.ShowBalloonTip(3500);
            }
            catch
            {
                // 通知失败不影响主流程。
            }
        }

        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher &&
            !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(Show);
        }
        else
        {
            Show();
        }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/app.ico"));
            if (resource?.Stream is not null)
            {
                return new Icon(resource.Stream);
            }
        }
        catch
        {
            // 退回到系统图标。
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon.Dispose();
    }
}
