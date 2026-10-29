using System.Windows;
using System.Windows.Threading;
using DeepSeekTranslator.Models;
using DeepSeekTranslator.Services;

namespace DeepSeekTranslator.Views;

/// <summary>
/// 发现新版本时的提示窗口：显示版本/更新说明，下载并校验安装包，
/// 然后交给安装器（/VERYSILENT /RESTARTAPP=1），由安装器在安装完成后重新拉起程序。
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _info;
    private readonly UpdateService _service;
    private bool _busy;
    private bool _allowClose;

    public UpdateWindow(UpdateInfo info, string currentVersion, UpdateService service)
    {
        InitializeComponent();

        _info = info;
        _service = service;

        TitleText.Text = $"发现新版本 {info.Version}";
        SubtitleText.Text = $"当前版本 {currentVersion}" +
            (string.IsNullOrWhiteSpace(info.Published) ? string.Empty : $" · 发布于 {FormatPublished(info.Published)}");

        NotesText.Text = string.IsNullOrWhiteSpace(info.Notes)
            ? "本次更新包含问题修复与体验改进。"
            : info.Notes;

        MandatoryText.Visibility = info.Mandatory ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = info.Mandatory ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>用户选择“稍后”时回传版本号，用于自动检查时不再重复打扰。</summary>
    public event Action<string>? Skipped;

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        SetBusy(true);
        StatusText.Foreground = System.Windows.Media.Brushes.Gray;
        ProgressBar.Visibility = Visibility.Visible;

        var progress = new Progress<double>(value =>
        {
            ProgressBar.Value = value * 100;
            StatusText.Text = $"正在下载… {value:P0}";
        });

        try
        {
            var file = await _service.DownloadAsync(_info, progress, CancellationToken.None);

            ProgressBar.Value = 100;
            StatusText.Text = "下载完成，正在校验并重启安装…";

            await Task.Delay(400);

            UpdateService.RunInstaller(file);

            _allowClose = true;
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Logger.Error("更新失败", ex);
            StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
            StatusText.Text = $"更新失败：{ex.Message}";
            ProgressBar.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        Skipped?.Invoke(_info.Version);
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_busy)
        {
            // 下载/安装过程中不允许关掉窗口
            e.Cancel = true;
            return;
        }

        if (!_allowClose)
        {
            Skipped?.Invoke(_info.Version);
        }

        base.OnClosing(e);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateButton.IsEnabled = !busy;
        SkipButton.IsEnabled = !busy;
        UpdateButton.Content = busy ? "更新中…" : "立即更新";
        if (busy)
        {
            StatusText.Text = "准备下载…";
        }
    }

    private static string FormatPublished(string value)
    {
        return DateTimeOffset.TryParse(value, out var time)
            ? time.ToLocalTime().ToString("yyyy-MM-dd")
            : value;
    }
}
