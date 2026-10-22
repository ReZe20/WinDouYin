using Microsoft.UI.Xaml.Data;

namespace WinDouYin.Converters;

/// <summary>
/// 评论/回复那颗心：没赞过是空心（U+EB51），赞过是实心（U+EB52）。
/// VM 只吐"赞没赞过"，字形是视图的事。码位与弹幕那颗心相同，但两边各用一个转换器——
/// 将来动一处的视觉不会牵动另一处。
/// </summary>
public sealed partial class CommentLikeGlyphConverter : IValueConverter
{
    /// <summary>空心字形（没赞过）。</summary>
    public const string OutlineGlyph = "\uEB51";

    /// <summary>实心字形（赞过）。</summary>
    public const string SolidGlyph = "\uEB52";

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? SolidGlyph : OutlineGlyph;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new System.NotSupportedException();
}
