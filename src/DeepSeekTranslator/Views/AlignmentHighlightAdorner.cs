using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using DeepSeekTranslator.Models;
using TextBox = System.Windows.Controls.TextBox;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace DeepSeekTranslator.Views;

/// <summary>只绘制，不修改 TextBox 的内容、焦点或用户选区；支持不连续短语。</summary>
internal sealed class AlignmentHighlightAdorner : Adorner
{
    private readonly TextBox _textBox;
    private IReadOnlyList<AlignmentSpan> _spans = Array.Empty<AlignmentSpan>();
    private static readonly Brush HighlightBrush = CreateBrush();

    public AlignmentHighlightAdorner(TextBox textBox) : base(textBox)
    {
        _textBox = textBox;
        IsHitTestVisible = false;
    }

    public void SetSpans(IReadOnlyList<AlignmentSpan> spans)
    {
        _spans = spans;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_spans.Count == 0 || !_textBox.IsVisible || _textBox.ActualWidth <= 0) return;
        drawingContext.PushClip(new RectangleGeometry(new Rect(_textBox.RenderSize)));
        foreach (var span in _spans)
        {
            Rect? line = null;
            for (var index = span.Start; index < span.Start + span.Length; index++)
            {
                if (index >= _textBox.Text.Length) break;
                if (char.IsLowSurrogate(_textBox.Text[index]) || _textBox.Text[index] is '\r' or '\n') continue;
                var leading = _textBox.GetRectFromCharacterIndex(index, trailingEdge: false);
                var trailing = _textBox.GetRectFromCharacterIndex(index, trailingEdge: true);
                if (leading.IsEmpty || trailing.IsEmpty || Math.Abs(leading.Top - trailing.Top) > 1) continue;
                var glyph = new Rect(Math.Min(leading.Left, trailing.Left), leading.Top,
                    Math.Max(1, Math.Abs(trailing.Left - leading.Left)), Math.Max(1, leading.Height));
                if (line is Rect previous && Math.Abs(previous.Top - glyph.Top) < 1)
                {
                    previous.Union(glyph);
                    line = previous;
                }
                else
                {
                    if (line is Rect completed) drawingContext.DrawRoundedRectangle(HighlightBrush, null, completed, 2, 2);
                    line = glyph;
                }
            }
            if (line is Rect last) drawingContext.DrawRoundedRectangle(HighlightBrush, null, last, 2, 2);
        }
        drawingContext.Pop();
    }

    private static Brush CreateBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(100, 251, 191, 36));
        brush.Freeze();
        return brush;
    }
}
