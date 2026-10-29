using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DeepSeekTranslator.Interop;
using DeepSeekTranslator.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Window = System.Windows.Window;

namespace DeepSeekTranslator.Views;

public partial class ScreenshotOverlayWindow : Window
{
    private readonly Bitmap _screenBitmap;
    private Point _start;
    private bool _dragging;

    public ScreenshotOverlayWindow()
    {
        InitializeComponent();

        var virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;

        _screenBitmap = new Bitmap(
            Math.Max(1, virtualScreen.Width),
            Math.Max(1, virtualScreen.Height),
            PixelFormat.Format32bppArgb);

        using (var graphics = Graphics.FromImage(_screenBitmap))
        {
            graphics.CopyFromScreen(
                virtualScreen.Left,
                virtualScreen.Top,
                0,
                0,
                _screenBitmap.Size,
                CopyPixelOperation.SourceCopy);
        }

        ScreenshotImage.Source = ToBitmapSource(_screenBitmap);

        Left = virtualScreen.Left;
        Top = virtualScreen.Top;
        Width = Math.Max(1, virtualScreen.Width);
        Height = Math.Max(1, virtualScreen.Height);

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            NativeMethods.SetWindowPos(
                handle,
                NativeMethods.HWND_TOPMOST,
                virtualScreen.Left,
                virtualScreen.Top,
                virtualScreen.Width,
                virtualScreen.Height,
                NativeMethods.SWP_SHOWWINDOW);
        };
    }

    public Bitmap? SelectedBitmap { get; private set; }

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(SelectionCanvas);
        _dragging = true;
        SelectionBorder.Visibility = System.Windows.Visibility.Visible;
        UpdateSelection(_start);
        SelectionCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        UpdateSelection(e.GetPosition(SelectionCanvas));
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        SelectionCanvas.ReleaseMouseCapture();

        var end = e.GetPosition(SelectionCanvas);
        var selection = Normalize(_start, end);
        if (selection.Width < 5 || selection.Height < 5)
        {
            SelectionBorder.Visibility = System.Windows.Visibility.Collapsed;
            return;
        }

        var scaleX = _screenBitmap.Width / Math.Max(1.0, SelectionCanvas.ActualWidth);
        var scaleY = _screenBitmap.Height / Math.Max(1.0, SelectionCanvas.ActualHeight);

        var left = (int)Math.Floor(selection.Left * scaleX);
        var top = (int)Math.Floor(selection.Top * scaleY);
        var right = (int)Math.Ceiling(selection.Right * scaleX);
        var bottom = (int)Math.Ceiling(selection.Bottom * scaleY);

        left = Math.Clamp(left, 0, _screenBitmap.Width - 1);
        top = Math.Clamp(top, 0, _screenBitmap.Height - 1);
        right = Math.Clamp(right, left + 1, _screenBitmap.Width);
        bottom = Math.Clamp(bottom, top + 1, _screenBitmap.Height);

        SelectedBitmap = _screenBitmap.Clone(
            new Rectangle(left, top, right - left, bottom - top),
            PixelFormat.Format32bppArgb);

        DialogResult = true;
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
        }
    }

    private void UpdateSelection(Point current)
    {
        var rect = Normalize(_start, current);
        System.Windows.Controls.Canvas.SetLeft(SelectionBorder, rect.Left);
        System.Windows.Controls.Canvas.SetTop(SelectionBorder, rect.Top);
        SelectionBorder.Width = rect.Width;
        SelectionBorder.Height = rect.Height;
    }

    private static Rect Normalize(Point start, Point end)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        var right = Math.Max(start.X, end.X);
        var bottom = Math.Max(start.Y, end.Y);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _screenBitmap.Dispose();
    }
}
