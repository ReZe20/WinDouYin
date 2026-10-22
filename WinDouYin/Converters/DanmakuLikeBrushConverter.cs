using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace WinDouYin.Converters;

/// <summary>
/// 弹幕列表那颗心的颜色：点过是抖音红，没点过回到正文色（主题里的那枚）。
/// VM 只吐"点没点过"，颜色是视图的事——跟字形各管一头（见 <see cref="DanmakuLikeGlyphConverter"/>）。
/// </summary>
public sealed partial class DanmakuLikeBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Accent = new(Color.FromArgb(0xFF, 0xFE, 0x2C, 0x55));

    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true
            ? Accent
            : Application.Current.Resources["TextFillColorPrimaryBrush"] as Brush ?? new SolidColorBrush(Microsoft.UI.Colors.White);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new System.NotSupportedException();
}
