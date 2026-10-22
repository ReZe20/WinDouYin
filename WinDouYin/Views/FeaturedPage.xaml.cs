
using WinDouYin.Contracts;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinDouYin.Controls;
using WinDouYin.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace WinDouYin.Views;

/// <summary>
/// 精选页（官方 <c>/jingxuan</c> 的形态，见 FeaturedPage.xaml 顶部注释）。
/// 页面只做这几件事：灌 17 枚分类、按内容宽切大卡/统一网格、把悬停预览与大卡预览挂到该挂的封面格上，
/// 以及记住每个分类的滚动位置。取数、分页、分类缓存与预取全在 <see cref="FeaturedViewModel"/>。
/// </summary>
public sealed partial class FeaturedPage : Page
{
    private readonly FeaturedPreviewControl _heroPreview = new() { IsHitTestVisible = false };
    private readonly Dictionary<string, double> _scrollOffsets = new(StringComparer.Ordinal);
    private readonly DispatcherQueueTimer _prefetchTimer;
    private FeaturedCategory? _prefetchWanted;
    private string _accountUid = string.Empty;
    private bool _suppressCategory;
    private bool _started;
    private bool _switching;
    private bool _restoringScroll;
    private bool _lastLoggedIn;
    private bool _subscribed;
    private int _autoFill;
    private int _switchVersion;
    private int _scrollVersion;

    public FeaturedPage()
    {
        InitializeComponent();

        // 鼠标在页签上停一下才预取：扫过去一排不该压出十几发 3MB 的请求
        _prefetchTimer = DispatcherQueue.CreateTimer();
        _prefetchTimer.Interval = TimeSpan.FromMilliseconds(320);
        _prefetchTimer.IsRepeating = false;
        _prefetchTimer.Tick += OnPrefetchTimerTick;

        foreach (FeaturedCategory category in ViewModel.Categories)
        {
            var item = new SelectorBarItem { Text = category.Label, Tag = category.Tag };
            item.PointerEntered += (_, _) => SchedulePrefetch(category);
            item.PointerExited += (_, _) => CancelPrefetch(category);
            item.GotFocus += (_, _) => ViewModel.Prefetch(category);
            CategoryBar.Items.Add(item);
        }

        Loaded += OnPageLoaded;
        SizeChanged += OnPageSizeChanged;
    }

    public FeaturedViewModel ViewModel => FeaturedViewModel.Shared;

    /// <summary>
    /// 账号换了而这一页正在前台：不靠 Frame 再导航一次（缓存页未必重新触发 Loaded），
    /// 直接把账面清到新号、回到「全部」重铺。主窗口在 AccountChanged 里调。
    /// </summary>
    public void ReloadForAccountChange()
    {
        string uid = Douyin.Feed.MyUid.Length > 0 ? Douyin.Feed.MyUid : Douyin.Feed.CurrentAccountUid;
        if (string.Equals(uid, _accountUid, StringComparison.Ordinal) && ViewModel.Current is not null)
        {
            return;   // 登录失败/加号没加成：号没变，账本留着
        }

        _accountUid = uid;
        ViewModel.ResetForAccount(uid);
        ResetTransientViewState();
        _started = true;
        _lastLoggedIn = Douyin.Feed.IsLoggedIn;
        SelectCategorySilently(0);
        _ = LoadFirstCategoryAsync();
    }

    /// <summary>
    /// 换号时把页面的瞬态一起作废：在飞的分类切换不许再恢复滚动，也不许把 _switching/_restoringScroll 留在 true。
    /// </summary>
    private void ResetTransientViewState()
    {
        _switchVersion++;
        _scrollVersion++;
        _switching = false;
        _restoringScroll = false;
        _autoFill = 0;
        _scrollOffsets.Clear();
        _prefetchTimer.Stop();
        _prefetchWanted = null;
    }

    private async Task LoadFirstCategoryAsync()
    {
        int version = ++_switchVersion;
        await ViewModel.SelectAsync(ViewModel.Categories[0]);
        if (version == _switchVersion)
        {
            RestoreScroll(0);
        }
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            // 账面是进程内单例，页面卸载时要退订，免得旧实例被事件拖着不放
            ViewModel.ItemsChanged += OnItemsChanged;
            _subscribed = true;
        }

        ViewModel.SetPageVisible(true);

        bool loggedIn = Douyin.Feed.IsLoggedIn;
        string uid = Douyin.Feed.MyUid.Length > 0 ? Douyin.Feed.MyUid : Douyin.Feed.CurrentAccountUid;

        // 换号（或首次登录）后整本账作废：缓存按号分，导出的旧内容不许铺到新号上。
        if (!string.Equals(uid, _accountUid, StringComparison.Ordinal))
        {
            _accountUid = uid;
            ViewModel.ResetForAccount(uid);
            ResetTransientViewState();
            _started = false;
        }

        // 账面被账号回调清空（Current 没了）时，哪怕 uid 没变也得重新铺一次
        if (!_started || ViewModel.Current is null)
        {
            _started = true;
            _lastLoggedIn = loggedIn;
            if (ViewModel.Current is null)
            {
                _scrollOffsets.Clear();
            }

            SelectCategorySilently(0);
            int version = ++_switchVersion;
            await ViewModel.SelectAsync(ViewModel.Categories[0]);
            if (version == _switchVersion)
            {
                RestoreScroll(0);
            }

            return;
        }

        // 上次进来还没登录、这次登录了：首屏那句"登录后才会出现精选内容"要自己让位
        if (loggedIn != _lastLoggedIn)
        {
            _lastLoggedIn = loggedIn;
            if (ViewModel.Current is { } current)
            {
                int version = ++_switchVersion;
                await ViewModel.SelectAsync(current);
                if (version == _switchVersion)
                {
                    RestoreScroll(_scrollOffsets.TryGetValue(current.Tag, out double offset) ? offset : 0);
                }

                return;
            }
        }

        _lastLoggedIn = loggedIn;

        // 页面在 Frame 里缓存：回来时把大卡预览重新挂上，缓存内容不重取；
        // 滚动位置顺手对一次，窗口重排后 ScrollViewer 自己不一定还停在原处。
        if (ViewModel.Current is { } shown)
        {
            // 首屏是在页面不在树上时取回来的（或上一次切页被卸载打断）：账上有内容但屏幕还是空的，
            // 这里补一次 Show；已经铺好的缓存不走这支，不重建列表。
            if (ViewModel.CurrentNeedsSelect)
            {
                int version = ++_switchVersion;
                await ViewModel.SelectAsync(shown);
                if (version == _switchVersion)
                {
                    RestoreScroll(_scrollOffsets.TryGetValue(shown.Tag, out double retryOffset) ? retryOffset : 0);
                }

                return;
            }

            AttachHeroPlayer();
            if (_scrollOffsets.TryGetValue(shown.Tag, out double saved))
            {
                RestoreScroll(saved);
            }

            return;
        }

        AttachHeroPlayer();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.SetPageVisible(false);

        if (_subscribed)
        {
            ViewModel.ItemsChanged -= OnItemsChanged;
            _subscribed = false;
        }

        _prefetchTimer.Stop();
        _prefetchWanted = null;

        // 页已经不在树上：在飞的切页/恢复不许再把滚动拉走，也不许把标志留在 true
        _switchVersion++;
        _switching = false;
        _restoringScroll = false;

        if (ViewModel.Current is { } current && ViewModel.Items.Count > 0)
        {
            _scrollOffsets[current.Tag] = FeedScroll.VerticalOffset;
        }

        HideHoverPreview();
        DetachHeroPlayer();
    }

    private void SelectCategorySilently(int index)
    {
        _suppressCategory = true;
        try
        {
            CategoryBar.SelectedItem = CategoryBar.Items[index];
        }
        finally
        {
            _suppressCategory = false;
        }
    }

    private async void OnCategoryChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_suppressCategory || sender.SelectedItem is not SelectorBarItem item || item.Tag is not string tag)
        {
            return;
        }

        FeaturedCategory? category = ViewModel.Categories.FirstOrDefault(c => c.Tag == tag);
        if (category is null || ReferenceEquals(category, ViewModel.Current))
        {
            return;
        }

        _prefetchTimer.Stop();
        _prefetchWanted = null;

        // 每个分类各记各的滚动位置：来回切的时候回到上次看到的那一屏。
        // 上一页还在取数（Items 已清空）时不记——那会儿的偏移还是更早那一页留下的。
        if (ViewModel.Current is { } previous && ViewModel.Items.Count > 0)
        {
            _scrollOffsets[previous.Tag] = FeedScroll.VerticalOffset;
        }

        HideHoverPreview();
        DetachHeroPlayer();
        _autoFill = 0;
        _switching = true;
        int version = ++_switchVersion;
        try
        {
            await ViewModel.SelectAsync(category);
            if (version == _switchVersion)
            {
                RestoreScroll(_scrollOffsets.TryGetValue(category.Tag, out double offset) ? offset : 0);
            }
        }
        finally
        {
            if (version == _switchVersion)
            {
                _switching = false;
            }
        }
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = FeaturedRepeater.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        bool hero = FeaturedGridLayout.HeroFor(width);
        if (hero == ViewModel.HeroMode)
        {
            return;
        }

        ViewModel.SetHeroMode(hero);
        FeaturedRepeater.InvalidateMeasure();
        DispatcherQueue.TryEnqueue(AttachHeroPlayer);
    }

    private void OnItemsChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            AttachHeroPlayer();

            // MaybeFillViewport 读的是 ExtentHeight；不先量一遍，刚铺的 20 张卡会被当成"没铺满"，
            // 白花一次自动补页的机会
            FeedScroll.UpdateLayout();
            MaybeFillViewport();
        });
    }

    private void SchedulePrefetch(FeaturedCategory category)
    {
        if (ReferenceEquals(category, ViewModel.Current))
        {
            return;
        }

        _prefetchWanted = category;
        _prefetchTimer.Stop();
        _prefetchTimer.Start();
    }

    private void CancelPrefetch(FeaturedCategory category)
    {
        if (!ReferenceEquals(_prefetchWanted, category))
        {
            return;
        }

        _prefetchTimer.Stop();
        _prefetchWanted = null;
    }

    private void OnPrefetchTimerTick(DispatcherQueueTimer sender, object args)
    {
        FeaturedCategory? category = _prefetchWanted;
        _prefetchWanted = null;
        if (category is not null)
        {
            ViewModel.Prefetch(category);
        }
    }

    /// <summary>
    /// 把视图挪回该分类上次的位置。ItemsRepeater 不是虚拟化的，先 UpdateLayout 把新网格量出来，
    /// 再 ChangeView；ChangeView 自己会按 ExtentHeight 夹住超出范围的值。
    /// 位置当场落下去（不等下一拍），否则紧接着再点一枚页签时，存下来的还是上一页的偏移。
    /// </summary>
    private void RestoreScroll(double offset)
    {
        int version = ++_scrollVersion;
        _restoringScroll = true;
        FeedScroll.UpdateLayout();
        FeedScroll.ChangeView(null, Math.Max(0, offset), null, true);

        // 只把"恢复中"的标记留到下一拍，压住 ChangeView 自己引发的触底事件
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (version == _scrollVersion)
            {
                _restoringScroll = false;
            }
        });
    }

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Index == 0)
        {
            DispatcherQueue.TryEnqueue(AttachHeroPlayer);
        }
    }

    /// <summary>大卡预览：把共享的预览控件塞进首卡的 HeroMediaHost（封面之上、文字之下），只此一枚。</summary>
    private void AttachHeroPlayer()
    {
        if (!ViewModel.HasHero || ViewModel.Items.Count == 0)
        {
            DetachHeroPlayer();
            return;
        }

        if (FeaturedRepeater.TryGetElement(0) is not FrameworkElement container
            || FindByName(container, "HeroMediaHost") is not Grid host)
        {
            return;
        }

        if (!ReferenceEquals(_heroPreview.Parent, host))
        {
            if (_heroPreview.Parent is Panel old)
            {
                old.Children.Remove(_heroPreview);
            }

            host.Children.Add(_heroPreview);
        }

        string play = ViewModel.Items[0].Play;
        if (play.Length > 0 && !string.Equals(_heroPreview.Source, play, StringComparison.Ordinal))
        {
            _heroPreview.Source = play;
        }
    }

    private void DetachHeroPlayer()
    {
        _heroPreview.Source = string.Empty;
        if (_heroPreview.Parent is Panel parent)
        {
            parent.Children.Remove(_heroPreview);
        }
    }

    private void OnFeedScrollChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        HideHoverPreview();
        if (_switching || _restoringScroll || sender is not ScrollViewer viewer)
        {
            return;
        }

        if (viewer.VerticalOffset + viewer.ViewportHeight >= viewer.ExtentHeight - 600)
        {
            _ = ViewModel.LoadMoreAsync();
        }
    }

    /// <summary>窗口太高、一页没填满时自动再要一页（最多 3 次，防止空转）。</summary>
    private void MaybeFillViewport()
    {
        if (_autoFill >= 3 || !ViewModel.CanLoadMore)
        {
            return;
        }

        if (FeedScroll.ExtentHeight > FeedScroll.ViewportHeight + 240)
        {
            return;
        }

        _autoFill++;
        _ = ViewModel.LoadMoreAsync();
    }

    private void OnCardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement root || root.DataContext is not FeaturedRow row || row.IsHero)
        {
            return;
        }

        row.Hovered = true;
        if (row.Play.Length == 0 || FindByName(root, "CoverBox") is not FrameworkElement cover)
        {
            return;
        }

        Rect bounds = cover.TransformToVisual(PreviewHost)
            .TransformBounds(new Rect(0, 0, cover.ActualWidth, cover.ActualHeight));
        if (bounds.Width < 40 || bounds.Y < 0 || bounds.Y + bounds.Height > PreviewHost.ActualHeight + 0.5)
        {
            return;   // 卡片露不全就不画预览，免得盖到页签条或页外
        }

        HoverPreview.Width = bounds.Width;
        HoverPreview.Height = bounds.Height;
        HoverPreview.Margin = new Thickness(bounds.X, bounds.Y, 0, 0);
        HoverPreview.Visibility = Visibility.Visible;
        if (!string.Equals(HoverPreview.Source, row.Play, StringComparison.Ordinal))
        {
            HoverPreview.Source = row.Play;
        }
    }

    private void OnCardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement root && root.DataContext is FeaturedRow row)
        {
            row.Hovered = false;
        }

        HideHoverPreview();
    }

    private void OnMoreFlyoutOpening(object? sender, object e)
    {
        if (sender is MenuFlyout { Target: FrameworkElement { DataContext: FeaturedRow row } })
        {
            row.MenuOpen = true;
        }
    }

    private void OnMoreFlyoutClosed(object? sender, object e)
    {
        if (sender is MenuFlyout { Target: FrameworkElement { DataContext: FeaturedRow row } })
        {
            row.MenuOpen = false;
        }
    }

    private void HideHoverPreview()
    {
        HoverPreview.Source = string.Empty;
        HoverPreview.Visibility = Visibility.Collapsed;
    }

    private void OnCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || id.Length == 0)
        {
            return;
        }

        FeaturedRow? row = ViewModel.Items.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
        if (row is null || Frame is null)
        {
            return;
        }

        List<SharedVideoRef> context = ViewModel.Items
            .Where(r => r.Id.Length > 0)
            .Select(r => new SharedVideoRef(r.Id, null))
            .ToList();
        Frame.Navigate(typeof(SharedVideoPage), new SharedVideoArgs(VideoEntry.From(row.Card), null, context));
    }

    private void OnCopyVideoLink(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } && id.Length > 0)
        {
            CopyText("https://www.douyin.com/video/" + id);
        }
    }

    private async void OnOpenVideoInBrowser(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } && id.Length > 0)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://www.douyin.com/video/" + id));
        }
    }

    private void OnCopyVideoId(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } && id.Length > 0)
        {
            CopyText(id);
        }
    }

    private static void CopyText(string text)
    {
        DataPackage package = new();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private static FrameworkElement? FindByName(DependencyObject root, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && element.Name == name)
            {
                return element;
            }

            if (FindByName(child, name) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
