using Microsoft.UI.Xaml.Data;

namespace WinDouYin.Converters;

/// <summary>
/// 清晰度菜单里那一行的可用性：这条视频真有的档位表（Key 列表）里含 ConverterParameter 给的键，
/// 这一档就亮着，否则置灰（"列出所有档位、没有的档置灰"就是这个转换）。
/// VM 只吐"这条视频有哪些档"，哪一行亮是视图的事，中间就这一层（与 VolumeGlyphConverter 一个道理）。
/// </summary>
public sealed partial class QualityEnabledConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is IReadOnlyList<string> keys && parameter is string key && keys.Contains(key);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
