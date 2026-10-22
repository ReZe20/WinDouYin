using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using WinDouYin.Contracts;
using WinDouYin.ViewModels;

namespace WinDouYin.Views;

/// <summary>
/// 顶部搜索框的落地页：Pivot 页签（综合/视频/用户）+ 视图切换 + 筛选 + 相关词 ToggleButton，
/// 任一维度都重取第一页，滚到底按同一组条件续页；点一张卡进双舞台视频页，
/// 上下文就是当前已取到的结果（滚轮前后切）。页面缓存：点开视频再回来时结果和滚动位置都还在。
/// 选中/悬停/按下/焦点视觉全部由系统控件自带状态出，代码不再手动上色。
/// </summary>
public sealed partial class SearchPage : Page
{
    private static readonly string[] SortLabels = ["综合排序", "最多点赞", "最新发布"];
    private static readonly int[] SortValues = [0, 1, 2];
    private static readonly string[] TimeLabels = ["不限", "一天内", "一周内", "半年内"];
    private static readonly int[] TimeValues = [0, 1, 7, 180];

    private string _keyword = string.Empty;
    private bool _syncingFilter;

    public SearchPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SearchViewModel.StatusText))
            {
                DispatcherQueue.TryEnqueue(FillViewport);
            }
        };

        // Pivot / RadioButtons / Flyout 这几家的 SelectionChanged 委托形状都不一样，
        // 用 lambda 挂，省得在 XAML 里对签名
        Tabs.SelectionChanged += (_, _) => SwitchTab(Tabs.SelectedIndex);
        FilterFlyout.Opening += (_, _) => SyncFilterSelection();
        SortGroup.SelectionChanged += (_, _) => OnFilterChanged();
        TimeGroup.SelectionChanged += (_, _) => OnFilterChanged();
    }

    public SearchViewModel ViewModel { get; } = new();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        string keyword = (e.Parameter as string ?? string.Empty).Trim();
        if (keyword.Length == 0
            || (string.Equals(keyword, _keyword, StringComparison.Ordinal) && ViewModel.Results.Count > 0))
        {
            return;   // 从结果里点开视频再回来：保留已取到的结果与滚动位置
        }

        Apply(keyword);
    }

    /// <summary>顶部搜索框在当前页再次回车：同一个关键词也重取第一页。</summary>
    public void Search(string keyword) => Apply(keyword.Trim());

    private void Apply(string keyword)
    {
        if (keyword.Length == 0)
        {
            return;
        }

        _keyword = keyword;
        TitleText.Text = keyword;
        ResultScroll.ChangeView(null, 0, null, true);
        _ = ViewModel.SearchAsync(keyword);
    }

    private void SwitchTab(int index)
    {
        SearchKind kind = index switch
        {
            1 => SearchKind.Video,
            2 => SearchKind.User,
            _ => SearchKind.General,
        };

        if (kind == ViewModel.Kind)
        {
            return;   // Pivot 装载时也报一次当前页签；账面没变就别重取
        }

        ResultScroll.ChangeView(null, 0, null, true);
        _ = ViewModel.SetKindAsync(kind);
    }

    /// <summary>主按钮=在当前档位上取反。SplitButton.Click 的委托形状与普通按钮不同，
    /// 也不等 Checked/Unchecked（ToggleSplitButton 那两个事件在 WinUI 3 不暴露），取反完直接把 IsChecked 拨到账面值。</summary>
    private void OnColumnPrimaryClick(SplitButton sender, SplitButtonClickEventArgs args)
    {
        ViewModel.SetMultiColumn(ViewModel.SingleColumn);
        if (sender is ToggleSplitButton toggle)
        {
            toggle.IsChecked = ViewModel.SingleColumn;
        }
    }

    private void OnViewModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            ViewModel.SetMultiColumn(tag != "single");
        }
    }

    private void OnFilterChanged()
    {
        if (_syncingFilter)
        {
            return;
        }

        int sort = ValueOf(SortLabels, SortValues, SortGroup.SelectedItem as string);
        int time = ValueOf(TimeLabels, TimeValues, TimeGroup.SelectedItem as string);
        ResultScroll.ChangeView(null, 0, null, true);
        _ = ViewModel.SetFilterAsync(sort, time);
    }

    /// <summary>弹层每次打开都把单选钮拨回账面值：不然用户关窗后再开，看到的还是上一次点的。</summary>
    private void SyncFilterSelection()
    {
        _syncingFilter = true;
        SortGroup.SelectedIndex = IndexOfValue(SortValues, ViewModel.SortType);
        TimeGroup.SelectedIndex = IndexOfValue(TimeValues, ViewModel.PublishTime);
        _syncingFilter = false;
    }

    private void OnChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string word } chip)
        {
            return;
        }

        ResultScroll.ChangeView(null, 0, null, true);
        _ = ViewModel.SetRefineAsync(word);

        // 再点已选中的那枚只会把它自己取消勾选，账面没有"反选"这一档：把视觉拨回账面
        chip.IsChecked = ViewModel.IsChipSelected(word);
    }

    private static int ValueOf(string[] labels, int[] values, string? label)
    {
        int at = Array.IndexOf(labels, label);
        return at < 0 ? values[0] : values[at];
    }

    private static int IndexOfValue(int[] values, int value)
    {
        int at = Array.IndexOf(values, value);
        return at < 0 ? 0 : at;
    }

    private void OnResultScrollChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!ViewModel.HasMore || ViewModel.IsLoading)
        {
            return;
        }

        double room = ResultScroll.ScrollableHeight - ResultScroll.VerticalOffset - ResultScroll.ViewportHeight;
        if (room < 600)
        {
            _ = ViewModel.LoadMoreAsync();
        }
    }

    /// <summary>窗口很大、一页结果没撑满视口时不会触发 ViewChanged：取完一页主动补到能滚动/到底为止。</summary>
    private void FillViewport()
    {
        if (ViewModel.HasMore && !ViewModel.IsLoading
            && ResultScroll.ScrollableHeight <= ResultScroll.ViewportHeight + 1)
        {
            _ = ViewModel.LoadMoreAsync();
        }
    }

    /// <summary>结果集合加进来后布局才落定；高度一变就重新看一眼视口有没有被撑满。</summary>
    private void OnRepeaterSizeChanged(object sender, SizeChangedEventArgs e) => FillViewport();

    private void OnCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || id.Length == 0)
        {
            return;
        }

        SearchResultRow? row = ViewModel.Results.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
        if (row is null)
        {
            return;
        }

        // 上下文 = 已经取到的结果；详情页按需补齐，滚轮就能在结果里前后切
        List<SharedVideoRef> context = ViewModel.Results
            .Where(r => r.Id.Length > 0)
            .Select(r => new SharedVideoRef(r.Id, null))
            .ToList();

        var args = new SharedVideoArgs(VideoEntry.From(row.Card), null, context) { OpenComments = false };
        if (!Frame.Navigate(typeof(SharedVideoPage), args))
        {
            MfLog.Write("app: 搜索结果点开后导航失败 " + id);
        }
    }

    /// <summary>点用户页签的整宽行：进那位作者的主页（第五十四轮留的那条「用户行点不开主页」）。</summary>
    private void OnUserClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string secUid } || secUid.Length == 0)
        {
            return;
        }

        string nickname = ViewModel.UserResults.FirstOrDefault(r => string.Equals(r.SecUid, secUid, StringComparison.Ordinal))?.Name ?? string.Empty;
        UserProfilePage.Open(Frame, secUid, nickname);
    }
}
