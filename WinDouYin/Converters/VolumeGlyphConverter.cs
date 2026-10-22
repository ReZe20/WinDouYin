using Microsoft.UI.Xaml.Data;

namespace WinDouYin.Converters;

/// <summary>
/// 音量图标：0（静音）与大于 0 用两枚不同的字形。
/// VM 只吐"音量是多少"这一个数，具体用哪个图标是视图的事，中间就这一层（跟 BoolToVisibility 一个道理）。
/// </summary>
public sealed partial class VolumeGlyphConverter : IValueConverter
{
    /// <summary>静音字形（用户 2026-10-14 点名用这枚）。</summary>
    public const string MuteGlyph = "\uE74F";

    /// <summary>有声字形。</summary>
    public const string SoundGlyph = "\uE767";

    public object Convert(object value, Type targetType, object parameter, string language) =>
        System.Convert.ToInt32(value) > 0 ? SoundGlyph : MuteGlyph;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new System.NotSupportedException();
}
