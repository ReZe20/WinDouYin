
using WinDouYin.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinDouYin.ViewModels;

namespace WinDouYin.Views;

/// <summary>导航参数：看关注还是粉丝（<c>relation/list</c> 两档，读链只对自己的账号成立）。</summary>
public sealed record UserRelationArgs(bool Followers, string OwnerNickname = "");

/// <summary>
/// 关注 / 粉丝列表页。从自己的主页点头部那两个数进来，点一行去对方主页，返回回自己的主页。
/// </summary>
public sealed partial class UserRelationPage : Page
{
    public UserRelationViewModel ViewModel { get; } = new();

    private bool _started;

    public UserRelationPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (_started || e.Parameter is not UserRelationArgs args)
        {
            return;
        }

        _started = true;
        _ = ViewModel.LoadAsync(args.Followers);
    }

    /// <summary>滚到底续页（<c>max_time</c> 游标）；留 400px 提前量。</summary>
    private void OnScrollChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        double room = ListScroll.ScrollableHeight - ListScroll.VerticalOffset - ListScroll.ViewportHeight;
        if (room < 400)
        {
            _ = ViewModel.LoadMoreAsync();
        }
    }

    private void OnRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string secUid } && secUid.Length > 0)
        {
            UserProfilePage.Open(Frame, secUid);
        }
    }
}
