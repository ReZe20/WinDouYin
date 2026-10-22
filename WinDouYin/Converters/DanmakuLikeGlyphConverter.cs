using Microsoft.UI.Xaml.Data;

namespace WinDouYin.Converters;

/// <summary>
/// 弹幕列表里那颗心：没点过是空心（U+EB51），点过是实心（U+EB52）。
/// VM 只吐"点没点过"，具体用哪个字形是视图的事，中间就这一层（跟 VolumeGlyphConverter 一个道理）。
/// </summary>
public sealed partial class DanmakuLikeGlyphConverter : IValueConverter
{
    /// <summary>空心字形（没点过）。</summary>
    public const string OutlineGlyph = "\uEB51";

    /// <summary>实心字形（点过）。</summary>
    public const string SolidGlyph = "\uEB52";

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? SolidGlyph : OutlineGlyph;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new System.NotSupportedException();
}
