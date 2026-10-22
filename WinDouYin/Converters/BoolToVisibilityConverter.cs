using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace WinDouYin.Converters;

/// <summary>x:Bind 不会自己把 bool 翻成 Visibility，而 VM 也不该直接吐 Visibility，所以中间就这一层。</summary>
/// <remarks>partial 是给 CsWinRT 投影器看的：XAML 里 <c>&lt;conv:BoolToVisibilityConverter/&gt;</c> 要在
/// 运行时把它当 WinRT 对象激活，非 partial 时投影代码不生成，Native AOT 下整页 XAML 加载直接抛
/// "Cannot create instance of type"（警告 CsWinRT1028）。所有被 XAML 实例化的 WinRT 类型同理。</remarks>
public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility.Visible;
}
