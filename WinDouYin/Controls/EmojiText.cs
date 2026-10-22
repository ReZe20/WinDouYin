
using WinDouYin.Contracts;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace WinDouYin.Controls;

/// <summary>
/// 给 RichTextBlock 挂一段"含官方表情码"的正文：把 <c>[微笑]</c> 这类码换成表情表里的图，
/// 图按行内元素插进去，其余文字原样排；换行留成 LineBreak。
/// 用附加属性而不是子类：RichTextBlock 是 sealed，且模板里一句 attached 就够。
/// 表情表没覆盖的码原样显示（不吞字）。
/// </summary>
public static class EmojiText
{
    /// <summary>行内表情图的边长与文字一样大（DIP）。</summary>
    private const int EmojiInlineSize = 18;

    /// <summary>码最长能有多长（中括号里的字符数）；超了就不当表情，避免扫到普通长括号句。</summary>
    private const int MaxCodeLength = 20;

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(EmojiText), new PropertyMetadata(string.Empty, OnTextChanged));

    public static string GetText(DependencyObject obj) => (string)obj.GetValue(TextProperty);

    public static void SetText(DependencyObject obj, string value) => obj.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RichTextBlock block)
        {
            Build(block, e.NewValue as string ?? string.Empty);
        }
    }

    private static void Build(RichTextBlock block, string text)
    {
        block.Blocks.Clear();
        if (text.Length == 0)
        {
            return;
        }

        var paragraph = new Paragraph();
        var run = new StringBuilder();

        void Flush()
        {
            if (run.Length > 0)
            {
                paragraph.Inlines.Add(new Run { Text = run.ToString() });
                run.Clear();
            }
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '[')
            {
                int close = text.IndexOf(']', i + 1);
                int length = close - i - 1;
                if (close > i + 1 && length <= MaxCodeLength)
                {
                    string code = text.Substring(i, close - i + 1);
                    if (EmojiCatalog.TryGetUrl(code, out string url))
                    {
                        Flush();
                        paragraph.Inlines.Add(EmojiInline(url, code));
                        i = close;
                        continue;
                    }
                }
            }

            if (c == '\n')
            {
                Flush();
                paragraph.Inlines.Add(new LineBreak());
                continue;
            }

            if (c == '\r')
            {
                continue;
            }

            run.Append(c);
        }

        Flush();
        if (paragraph.Inlines.Count > 0)
        {
            block.Blocks.Add(paragraph);
        }
    }

    private static InlineUIContainer EmojiInline(string url, string code)
    {
        var image = new Image
        {
            Width = EmojiInlineSize,
            Height = EmojiInlineSize,
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(url)),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(image, code);
        return new InlineUIContainer { Child = image };
    }
}
