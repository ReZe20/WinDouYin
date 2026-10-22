using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace WinDouYin.Converters;

/// <summary>
/// 评论点赞那颗心的颜色：赞过用**系统强调色**，没赞过用三级文字色。
/// 评论面板按用户要求走 WinUI 3 设计语言，不掺抖音红（弹幕列表那枚红心是另一套）。
/// </summary>
public sealed partial class CommentLikeBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        string key = value is true ? "AccentTextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush";
        return Application.Current.Resources[key] as Brush
            ?? new SolidColorBrush(value is true ? Microsoft.UI.Colors.Red : Microsoft.UI.Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new System.NotSupportedException();
}
