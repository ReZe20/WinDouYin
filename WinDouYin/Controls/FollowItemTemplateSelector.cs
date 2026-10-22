
using WinDouYin.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinDouYin.ViewModels;

namespace WinDouYin.Controls;

/// <summary>
/// 关注页左栏是"小节头 + 用户行"混排：ListView 用这个选择器一格一格换模板。
/// 用平铺列表而不是 CollectionViewSource 分组——分组视图对"先建空组、后填行"的刷新不稳，
/// 平铺这一条在 WinUI 3 上更好控（也方便搜索时整栏重建）。
/// </summary>
public sealed partial class FollowItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? SectionTemplate { get; set; }

    public DataTemplate? RowTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is FollowSectionRow ? SectionTemplate : RowTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
