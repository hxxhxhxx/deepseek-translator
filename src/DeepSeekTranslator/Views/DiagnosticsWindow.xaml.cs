using System.Windows;
using System.Windows.Input;
using DeepSeekTranslator.Models;
using DeepSeekTranslator.Services;
using MessageBox = System.Windows.MessageBox;

namespace DeepSeekTranslator.Views;

public partial class DiagnosticsWindow : Window
{
    private readonly Func<string> _buildReport;
    private readonly Func<Task<(bool Ok, string Message)>> _testCapture;
    private readonly Action _openLogs;
    private readonly Action _clearCache;

    public DiagnosticsWindow(
        Func<string> buildReport,
        Func<Task<(bool Ok, string Message)>> testCapture,
        Action openLogs,
        Action clearCache)
    {
        InitializeComponent();

        _buildReport = buildReport;
        _testCapture = testCapture;
        _openLogs = openLogs;
        _clearCache = clearCache;

        RefreshReport();
    }

    private void RefreshReport()
    {
        try
        {
            ReportBox.Text = _buildReport();
        }
        catch (Exception ex)
        {
            ReportBox.Text = $"生成诊断报告失败：{ex}";
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshReport();
        StatusText.Text = "已刷新。";
    }

    private async void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var ok = await ClipboardWriter.TrySetTextAsync(ReportBox.Text);
        StatusText.Text = ok
            ? "诊断报告已复制到剪贴板。"
            : "复制失败：剪贴板被其他程序占用，请稍后重试。";
    }

    private async void TestCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        TestCaptureButton.IsEnabled = false;
        StatusText.Text = "正在读取当前选区（请先在其他软件里选中文字）…";

        try
        {
            var (ok, message) = await _testCapture();
            StatusText.Text = message;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"测试失败：{ex.Message}";
        }
        finally
        {
            TestCaptureButton.IsEnabled = true;
            RefreshReport();
        }
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _openLogs();
            StatusText.Text = "已打开日志目录。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"打开日志目录失败：{ex.Message}";
        }
    }

    private void ClearCacheButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            "确定清空本地翻译缓存吗？之后的翻译会重新请求 DeepSeek。",
            "清空缓存",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            _clearCache();
            StatusText.Text = "缓存已清空。";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"清空缓存失败：{ex.Message}";
        }

        RefreshReport();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
