using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeepSeekTranslator.Models;
using DeepSeekTranslator.Services;
using MessageBox = System.Windows.MessageBox;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;

namespace DeepSeekTranslator.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsStore _settingsStore;
    private readonly SecretStore _secretStore;
    private readonly DeepSeekClient _client;
    private readonly AppSettings _settings;
    private bool _busy;

    public SettingsWindow(
        SettingsStore settingsStore,
        SecretStore secretStore,
        DeepSeekClient client)
    {
        InitializeComponent();

        _settingsStore = settingsStore;
        _secretStore = secretStore;
        _client = client;
        _settings = _settingsStore.Load();

        ModelComboBox.ItemsSource = ModelCatalog.Models;
        ModelComboBox.SelectedValue = _settings.Model;
        SelectOcrLanguage(_settings.OcrLanguage);
        AutoStartCheckBox.IsChecked = _settings.AutoStart;
        SelectionIconCheckBox.IsChecked = _settings.ShowSelectionIcon;
        LoadHotkeys();

        foreach (var box in new[]
                 {
                     TranslateSelectionHotkeyBox,
                     TranslateClipboardHotkeyBox,
                     OcrHotkeyBox,
                     CopyLastHotkeyBox
                 })
        {
            box.RecordingStarted += () => HotkeyRecordingStarted?.Invoke();
            box.RecordingStopped += () => HotkeyRecordingStopped?.Invoke();
        }

        UpdateUsageHint();

        FooterText.Text = $"DeepSeek 划词翻译 {AppInfo.Version} · 内部使用";

        UpdateKeyStatus();
        UpdateModelDescription();

        if (!_secretStore.HasSecret())
        {
            ShowStatus("尚未配置 API Key。请填入 Key 后保存。", StatusKind.Warning);
        }
        else
        {
            ShowStatus("API Key 已加密保存在本机。可以直接开始翻译。", StatusKind.Success);
        }
    }

    private void UpdateUsageHint()
    {
        UsageHintText.Text =
            $"Word 优先使用 UI Automation 读取选区；浏览器、PowerPoint、微信、Zotero、PDF 走剪贴板（自动 Ctrl+C）。" +
            $"管理员窗口请手动 Ctrl+C 后按 {_settings.TranslateClipboardHotkey}。";
    }

    public event Action<AppSettings>? SettingsSaved;

    /// <summary>用户点进快捷键输入框，开始录制：此时应暂停全局热键。</summary>
    public event Action? HotkeyRecordingStarted;

    /// <summary>录制结束、焦点离开输入框：恢复全局热键。</summary>
    public event Action? HotkeyRecordingStopped;

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        Save(showStatus: true);
    }

    private bool Save(bool showStatus)
    {
        if (_busy) return false;

        var typedKey = ApiKeyBox.Password.Trim();
        if (typedKey.Length is > 0 and < 8)
        {
            ShowStatus("API Key 长度异常，请检查是否复制完整。", StatusKind.Error);
            ApiKeyBox.Focus();
            return false;
        }

        var selectedModel = ModelComboBox.SelectedValue as string ?? ModelCatalog.DefaultModel;
        var autoStart = AutoStartCheckBox.IsChecked == true;
        var ocrLanguage = GetSelectedOcrLanguage();

        if (!TryReadHotkeys(out var hotkeys))
        {
            return false;
        }

        SetBusy(true);
        try
        {
            if (typedKey.Length > 0)
            {
                _secretStore.Save(typedKey);
            }

            _settings.Model = ModelCatalog.IsSupported(selectedModel)
                ? selectedModel
                : ModelCatalog.DefaultModel;
            _settings.AutoStart = autoStart;
            _settings.ShowSelectionIcon = SelectionIconCheckBox.IsChecked == true;
            _settings.OcrLanguage = ocrLanguage;
            _settings.TranslateSelectionHotkey = hotkeys.Selection.Display;
            _settings.TranslateClipboardHotkey = hotkeys.Clipboard.Display;
            _settings.OcrHotkey = hotkeys.Ocr.Display;
            _settings.CopyLastHotkey = hotkeys.CopyLast.Display;

            // 托盘的「游戏模式」可能在设置窗口打开期间被改过，保存时不要覆盖它。
            _settings.GameMode = _settingsStore.Load().GameMode;

            _settingsStore.Save(_settings);
            UpdateUsageHint();

            try
            {
                AutoStartService.SetEnabled(autoStart);
            }
            catch (Exception ex)
            {
                Logger.Error("设置开机自启失败", ex);
                ShowStatus($"设置已保存，但开机自启设置失败：{ex.Message}", StatusKind.Warning);
                ApiKeyBox.Clear();
                UpdateKeyStatus();
                SettingsSaved?.Invoke(_settings);
                return true;
            }

            ApiKeyBox.Clear();
            UpdateKeyStatus();
            UpdateModelDescription();

            if (showStatus)
            {
                ShowStatus(
                    _secretStore.HasSecret()
                        ? $"设置已保存。现在可以在任意软件中选中文本，按 {_settings.TranslateSelectionHotkey} 翻译。"
                        : "设置已保存，但尚未配置 API Key；翻译前请先填入 Key。",
                    _secretStore.HasSecret() ? StatusKind.Success : StatusKind.Warning);
            }

            SettingsSaved?.Invoke(_settings);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("保存设置失败", ex);
            ShowStatus($"保存失败：{ex.Message}", StatusKind.Error);
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var typedKey = ApiKeyBox.Password.Trim();
        var key = typedKey.Length > 0 ? typedKey : _secretStore.Load();
        if (string.IsNullOrWhiteSpace(key))
        {
            ShowStatus("请先填写 DeepSeek API Key。", StatusKind.Error);
            ApiKeyBox.Focus();
            return;
        }

        SetBusy(true);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            ShowStatus("正在连接 DeepSeek…", StatusKind.Info);
            var models = await _client.TestConnectionAsync(key, CancellationToken.None);
            stopwatch.Stop();

            var detail = models.Count > 0
                ? $"当前账号可调用 {models.Count} 个模型。"
                : "API Key 有效。";
            var saveHint = typedKey.Length > 0 ? " 记得点击“保存设置”。" : string.Empty;
            ShowStatus(
                $"连接成功，耗时 {stopwatch.ElapsedMilliseconds} ms。{detail}{saveHint}",
                StatusKind.Success);
        }
        catch (DeepSeekException ex)
        {
            ShowStatus(ex.Message, StatusKind.Error);
        }
        catch (Exception ex)
        {
            Logger.Error("测试连接异常", ex);
            ShowStatus($"测试连接失败：{ex.Message}", StatusKind.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ClearKeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (!_secretStore.HasSecret() && ApiKeyBox.Password.Length == 0)
        {
            ShowStatus("当前没有已保存的 API Key。", StatusKind.Warning);
            return;
        }

        var result = MessageBox.Show(
            this,
            "确定要清除本机保存的 DeepSeek API Key 吗？清除后将无法翻译。",
            "清除 API Key",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            _secretStore.Clear();
            ApiKeyBox.Clear();
            UpdateKeyStatus();
            ShowStatus("API Key 已清除。", StatusKind.Warning);
        }
        catch (Exception ex)
        {
            Logger.Error("清除 Key 失败", ex);
            ShowStatus($"清除失败：{ex.Message}", StatusKind.Error);
        }
    }

    private void GetKeyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://platform.deepseek.com/api_keys",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Error("打开 API Key 页面失败", ex);
            ShowStatus("无法打开浏览器，请手动访问 https://platform.deepseek.com/api_keys", StatusKind.Warning);
        }
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.EnsureDirectories();
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{AppPaths.LogDirectory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Logger.Error("打开日志目录失败", ex);
            ShowStatus($"无法打开日志目录：{ex.Message}", StatusKind.Warning);
        }
    }

    private void ModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateModelDescription();
    }

    public void FocusHotkeySettings()
    {
        HotkeyCard.BringIntoView();
        TranslateSelectionHotkeyBox.Focus();
    }

    private void LoadHotkeys()
    {
        TranslateSelectionHotkeyBox.Text = _settings.TranslateSelectionHotkey;
        TranslateClipboardHotkeyBox.Text = _settings.TranslateClipboardHotkey;
        OcrHotkeyBox.Text = _settings.OcrHotkey;
        CopyLastHotkeyBox.Text = _settings.CopyLastHotkey;
    }

    private void ResetHotkeysButton_Click(object sender, RoutedEventArgs e)
    {
        TranslateSelectionHotkeyBox.Text = HotkeyParser.DefaultTranslateSelection;
        TranslateClipboardHotkeyBox.Text = HotkeyParser.DefaultTranslateClipboard;
        OcrHotkeyBox.Text = HotkeyParser.DefaultOcr;
        CopyLastHotkeyBox.Text = HotkeyParser.DefaultCopyLast;
        ShowStatus("快捷键已恢复默认，点击“保存设置”后生效。", StatusKind.Info);
    }

    private bool TryReadHotkeys(out HotkeySet hotkeys)
    {
        hotkeys = default;

        if (!HotkeyParser.TryParse(TranslateSelectionHotkeyBox.Text, out var selection) ||
            !HotkeyParser.TryParse(TranslateClipboardHotkeyBox.Text, out var clipboard) ||
            !HotkeyParser.TryParse(OcrHotkeyBox.Text, out var ocr) ||
            !HotkeyParser.TryParse(CopyLastHotkeyBox.Text, out var copyLast))
        {
            ShowStatus(
                "快捷键格式无效。请点击输入框后直接按组合键，至少包含 Ctrl/Alt/Shift/Win。",
                StatusKind.Error);
            return false;
        }

        var all = new[]
        {
            (Name: "翻译当前选区", Parsed: selection),
            (Name: "翻译剪贴板", Parsed: clipboard),
            (Name: "截图 OCR", Parsed: ocr),
            (Name: "复制最近译文", Parsed: copyLast)
        };

        for (var i = 0; i < all.Length; i++)
        {
            for (var j = i + 1; j < all.Length; j++)
            {
                if (all[i].Parsed.Modifiers == all[j].Parsed.Modifiers &&
                    all[i].Parsed.VirtualKey == all[j].Parsed.VirtualKey)
                {
                    ShowStatus(
                        $"{all[i].Name} 和 {all[j].Name} 不能使用同一个组合键。",
                        StatusKind.Error);
                    return false;
                }
            }
        }

        hotkeys = new HotkeySet(selection, clipboard, ocr, copyLast);
        return true;
    }

    private readonly record struct HotkeySet(
        ParsedHotkey Selection,
        ParsedHotkey Clipboard,
        ParsedHotkey Ocr,
        ParsedHotkey CopyLast);

    private void UpdateModelDescription()
    {
        if (ModelDescriptionText is null) return;

        var model = ModelCatalog.Get(ModelComboBox.SelectedValue as string);
        ModelDescriptionText.Text = $"{model.Description} 官方版本：{model.Version}。";
    }

    private void UpdateKeyStatus()
    {
        var masked = _secretStore.MaskedKey();
        if (string.IsNullOrWhiteSpace(masked))
        {
            KeyStatusText.Text = "当前未配置 API Key。";
            KeyStatusText.Foreground = BrushFrom("#B45309");
            HeaderStatusText.Text = "待配置";
            HeaderStatusPill.Background = BrushFrom("#FFFBEB");
            HeaderStatusPill.BorderBrush = BrushFrom("#FDE68A");
            HeaderStatusDot.Fill = BrushFrom("#D97706");
            HeaderStatusText.Foreground = BrushFrom("#92400E");
        }
        else
        {
            KeyStatusText.Text = $"已保存：{masked}（不会回显完整 Key）";
            KeyStatusText.Foreground = BrushFrom("#166534");
            HeaderStatusText.Text = "已配置";
            HeaderStatusPill.Background = BrushFrom("#ECFDF5");
            HeaderStatusPill.BorderBrush = BrushFrom("#BBF7D0");
            HeaderStatusDot.Fill = BrushFrom("#16A34A");
            HeaderStatusText.Foreground = BrushFrom("#166534");
        }
    }

    private void SelectOcrLanguage(string language)
    {
        foreach (var item in OcrLanguageComboBox.Items)
        {
            if (item is ComboBoxItem comboItem &&
                string.Equals(comboItem.Tag as string, language, StringComparison.OrdinalIgnoreCase))
            {
                OcrLanguageComboBox.SelectedItem = comboItem;
                return;
            }
        }

        OcrLanguageComboBox.SelectedIndex = 0;
    }

    private string GetSelectedOcrLanguage()
    {
        return OcrLanguageComboBox.SelectedItem is ComboBoxItem item
            ? item.Tag as string ?? string.Empty
            : string.Empty;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SaveButton.IsEnabled = !busy;
        TestButton.IsEnabled = !busy;
        ClearKeyButton.IsEnabled = !busy;
        SaveButton.Content = busy ? "处理中…" : "保存设置";
        TestButton.Content = busy ? "连接中…" : "测试连接";
    }

    private enum StatusKind
    {
        Info,
        Success,
        Warning,
        Error
    }

    private void ShowStatus(string message, StatusKind kind)
    {
        StatusBox.Visibility = Visibility.Visible;
        StatusText.Text = message;

        var (background, border, foreground) = kind switch
        {
            StatusKind.Success => ("#F0FDF4", "#BBF7D0", "#166534"),
            StatusKind.Warning => ("#FFFBEB", "#FDE68A", "#92400E"),
            StatusKind.Error => ("#FEF2F2", "#FECACA", "#991B1B"),
            _ => ("#EFF6FF", "#BFDBFE", "#1E40AF")
        };

        StatusBox.Background = BrushFrom(background);
        StatusBox.BorderBrush = BrushFrom(border);
        StatusText.Foreground = BrushFrom(foreground);
    }

    private static SolidColorBrush BrushFrom(string color)
    {
        return new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(color));
    }
}
