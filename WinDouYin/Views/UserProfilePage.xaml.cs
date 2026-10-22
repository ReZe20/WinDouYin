using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using WinDouYin.Contracts;
using WinDouYin.ViewModels;

namespace WinDouYin.Views;

/// <summary>
/// 导航到用户主页带的参数：要打开谁的 <c>sec_uid</c>、来路给的昵称
/// （资料接口还没回来之前头部靠它先占位），以及是不是底部「账号」的本人模式
/// （本人模式不带 sec_uid，进去以后现取自己的）。
/// </summary>
public sealed record UserProfileArgs(string SecUid, string Nickname = "", bool Self = false);

/// <summary>
/// 用户主页：头部一份资料 + 「作品 / 推荐 / 喜欢」（本人页多一档「观看历史」），作品下再分「作品 / 合集 / 短剧」。
/// 他人视角与底部「账号」（本人视角）共用同一份页面与取数：本人模式只是先补自己的 sec_uid，
/// 之后的资料/作品/合集/喜欢全走同一条通道（见 <see cref="UserProfileViewModel.LoadSelfAsync"/>），
/// 观看历史是账号侧 <c>history/read</c>，只有本人页摆。
/// </summary>
public sealed partial class UserProfilePage : Page
{
    /// <summary>
    /// 宿主页面（推荐页 / 分享视频页 / 搜索页）用它导航到某个作者的主页：
    /// 入口散在三处，判据与失败日志就收在这一枚里，免得各处各写一遍。
    /// </summary>
    public static void Open(Frame frame, string secUid, string nickname = "")
    {
        if (secUid.Length == 0)
        {
            return;
        }

        if (!frame.Navigate(typeof(UserProfilePage), new UserProfileArgs(secUid, nickname)))
        {
            MfLog.Write("app: 用户主页导航失败 " + secUid);
        }
    }

    /// <summary>底部「账号」：本人主页。自己的 sec_uid 由页面进去以后现取，入口这里不带身份。</summary>
    public static void OpenSelf(Frame frame)
    {
        if (!frame.Navigate(typeof(UserProfilePage), new UserProfileArgs(string.Empty, Self: true)))
        {
            MfLog.Write("app: 本人主页导航失败");
        }
    }

    public UserProfileViewModel ViewModel { get; } = new();

    /// <summary>本人主页闸门里的「登录」：页面不自己弹面板，交给宿主（主窗口）开统一那张登录框。</summary>
    public event Action? LoginRequested;

    /// <summary>当前页是不是底部「账号」的本人模式——主窗口用它避免重复导航与登录后刷新。</summary>
    public bool IsSelfProfile => ViewModel.IsSelfProfile;

    private string _secUid = string.Empty;
    private bool _selfLoaded;
    private bool _selfStale;
    private DispatcherQueueTimer? _tipTimer;
    private bool _openingMix;

    public UserProfilePage()
    {
        InitializeComponent();

        // 诊断裁剪链（"正文留前几块"与"热力图卡内留前几块"两档）2026-10-21 结案后拆干净了。
        // 那颗点页尾「账号」时的 0xC0000005 定死在"Viewbox 缩放宿主 + 第二台并排 ItemsRepeater"这棵树上：
        // 标签一枚都不喂、集合一次都不改也必崩 ⇒ 与数据与线程无关；摘掉任一样都确定不崩。
        // 现在整张热力图只剩一台 repeater（月份标签并回列里），外层换成定高横向 ScrollView。
        // 结论与那三步确定结果记在 XAML 上那段注释与仓库笔记里。

        // 热力图的底色全走 ThemeResource，换主题由系统自己重取，不必重建那一年几百个格子。
    }

    /// <summary>
    /// 热力图右边那颗年份选项。走事件而不是 <c>x:Bind Mode=TwoWay</c> 顶 <c>SelectedItem</c>：
    /// 年份集合刷新（换号、账本跨年）会让选项被程序改选一次，两条路并存就是一场自转——
    /// <c>SelectYear</c> 那头也按"与当前相同就当没这回事"再收一道。
    /// sender 必须写 <c>object</c>：ComboBox.SelectionChanged 是老式委托
    /// <c>SelectionChangedEventHandler(object, SelectionChangedEventArgs)</c>，第一个参数不认具体类型
    /// （写成 ComboBox 就是实测那条 CS0123）；上面 <see cref="OnTabChanged"/> 能写 SelectorBar，
    /// 是因为那个事件走的是 TypedEventHandler&lt;TSender,TArgs&gt;。
    /// </summary>
    private void OnHeatmapYearSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: int year })
        {
            ViewModel.Heatmap.SelectYear(year);
        }
    }

    /// <summary>
    /// 热力图那张卡量到宽了：把可用宽交给 <see cref="UsageHeatmapViewModel.SetAvailableWidth"/> 换成"往左画几周"。
    /// 这是"界面尺寸的出处"唯一进热力图 ViewModel 的一道口子——格子固定 16 DIP 不缩放，
    /// 宽度只决定画几天（2026-10-22 口述"左边空缺的继续用过去的事格子填充"），
    /// 纯 XAML 那头表达不了"缺出来的宽度用日子填"，只能报个数进去；列数没变时 VM 那头发空转，
    /// 拖窗沿不会重排格子，也不会顶出横向滚动条。
    /// 事件是老式 <c>SizeChangedEventHandler(object, SizeChangedEventArgs)</c>，sender 只能写 object。
    /// </summary>
    private void OnHeatmapSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ViewModel.Heatmap.SetAvailableWidth(e.NewSize.Width);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is UserProfileArgs { Self: true })
        {
            if (_selfLoaded && !_selfStale)
            {
                // 同一趟本人主页重复进来：保住已翻到的页与滚动，只把热力图对一遍最新账本，
                // 再向服务端补一发增量（资料 + 当前档第一页里没见过的条目）
                ViewModel.RefreshUsageHeatmap();
                _ = ViewModel.RefreshAsync();
                return;
            }

            _selfLoaded = true;
            _selfStale = false;
            _secUid = string.Empty;
            _ = ViewModel.LoadSelfAsync();
            return;
        }

        _selfLoaded = false;
        string secUid = e.Parameter switch
        {
            UserProfileArgs a => a.SecUid,
            string s => s,
            _ => string.Empty,
        };

        if (secUid.Length == 0)
        {
            return;
        }

        if (string.Equals(secUid, _secUid, StringComparison.Ordinal))
        {
            // 同一个人重复进来：已翻到的页与滚动都保住，只做增量（缓存已在内存里，新条目补到最前）
            _ = ViewModel.RefreshAsync();
            return;
        }

        _secUid = secUid;
        _ = ViewModel.LoadAsync(secUid);
    }

    /// <summary>
    /// 登录/退出后由主窗口喊一声：本人主页要按新会话重取，不能还挂着上一个号的资料。
    /// 页面不在前台时只记脏，等下次导航回来自动重载（保住"看视频看到一半退出登录"那条路）。
    /// </summary>
    public void MarkSelfProfileStale() => _selfStale = true;

    public void ReloadSelfProfile()
    {
        if (_selfLoaded)
        {
            _selfStale = false;
            _ = ViewModel.LoadSelfAsync();
        }
    }

    private void OnLoginRequested(object sender, RoutedEventArgs e) => LoginRequested?.Invoke();

    /// <summary>滚到底（留 600px 提前量，照搜索页那档）就续当前这一页签的下一页。</summary>
    private void OnScrollChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        double room = PageScroll.ScrollableHeight - PageScroll.VerticalOffset - PageScroll.ViewportHeight;
        if (room < 600)
        {
            _ = ViewModel.LoadMoreAsync();
        }
    }

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        UserProfileTab tab = ReferenceEquals(sender.SelectedItem, HistoryTabItem)
            ? UserProfileTab.History
            : ReferenceEquals(sender.SelectedItem, FavoriteTabItem)
                ? UserProfileTab.Favorite
                : ReferenceEquals(sender.SelectedItem, LikesTabItem)
                    ? UserProfileTab.Likes
                    : ReferenceEquals(sender.SelectedItem, RecommendTabItem)
                        ? UserProfileTab.Recommend
                        : UserProfileTab.Works;
        _ = ViewModel.SelectTabAsync(tab);
    }

    private void OnSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        UserProfileSection section = ReferenceEquals(sender.SelectedItem, SeriesSectionItem)
            ? UserProfileSection.Series
            : ReferenceEquals(sender.SelectedItem, MixSectionItem)
                ? UserProfileSection.Mix
                : UserProfileSection.Posts;
        _ = ViewModel.SelectSectionAsync(section);
    }

    private void OnFavoriteSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        UserFavoriteSection section = ReferenceEquals(sender.SelectedItem, FavoriteVideosSectionItem)
            ? UserFavoriteSection.Videos
            : ReferenceEquals(sender.SelectedItem, FavoriteMusicSectionItem)
                ? UserFavoriteSection.Music
                : ReferenceEquals(sender.SelectedItem, FavoriteCompilationSectionItem)
                    ? UserFavoriteSection.Compilation
                    : ReferenceEquals(sender.SelectedItem, FavoritePlayletSectionItem)
                        ? UserFavoriteSection.Playlet
                        : UserFavoriteSection.Folders;
        _ = ViewModel.SelectFavoriteSectionAsync(section);
    }

    /// <summary>点收藏夹卡：换到那一夹的夹内列表（同一张卡模板，游标从头）。</summary>
    private void OnFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || id.Length == 0)
        {
            return;
        }

        UserProfileCollectFolderRow? row = ViewModel.CollectFolders.FirstOrDefault(
            r => string.Equals(r.Id, id, StringComparison.Ordinal));
        if (row is not null)
        {
            _ = ViewModel.OpenFolderAsync(row);
        }
    }

    /// <summary>夹内列表退回收藏夹格子：账面还在，不重取。</summary>
    private void OnFolderBackClick(object sender, RoutedEventArgs e) => ViewModel.CloseFolder();

    /// <summary>点作品卡进视频页：上下文就用主页已经翻到的这批，滚轮能在里头前后切（照搜索页那条）。</summary>
    private void OnPostClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || id.Length == 0)
        {
            return;
        }

        UserProfilePostRow? row = ViewModel.Posts.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
        if (row is null)
        {
            return;
        }

        List<SharedVideoRef> context = ViewModel.Posts
            .Where(r => r.Id.Length > 0)
            .Select(r => new SharedVideoRef(r.Id, null))
            .ToList();

        if (!Frame.Navigate(typeof(SharedVideoPage), new SharedVideoArgs(VideoEntry.From(row.Card), null, context)))
        {
            MfLog.Write("app: 主页点开后导航失败 " + id);
        }
    }

    /// <summary>简介「更多 / 收起」：MaxLines 3 ↔ 不限（0）。</summary>
    private void OnSignatureToggle(object sender, RoutedEventArgs e) => ViewModel.ToggleSignature();

    /// <summary>自己的主页：关注数 → 关注列表。</summary>
    private void OnFollowingListClick(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(UserRelationPage), new UserRelationArgs(Followers: false));

    /// <summary>自己的主页：粉丝数 → 粉丝列表。</summary>
    private void OnFollowersListClick(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(UserRelationPage), new UserRelationArgs(Followers: true));

    /// <summary>
    /// 关注 / 取关：写链形状实测过（commit/follow/user · POST form type=1|0），
    /// 成功后账面会重取资料，以读回的 follow_status 为准（见 ViewModel）。
    /// </summary>
    private async void OnFollowClick(object sender, RoutedEventArgs e) => await ViewModel.ToggleFollowAsync();

    /// <summary>
    /// 分享主页：优先走 Windows 系统分享面板（<see cref="Service.SystemShare"/> 绑窗口句柄）；
    /// 拿不到窗口句柄就回退复制链接。链接是本页实测能直接开的
    /// <c>douyin.com/user/&lt;sec_uid&gt;</c>——视频那条 web_shorten 短链只对作品有效，不掺到这里。
    /// </summary>
    private void OnShareClick(object sender, RoutedEventArgs e)
    {
        string link = ViewModel.ShareUrl;
        if (link.Length == 0)
        {
            ShowTip("主页地址还没取到");
            return;
        }

        try
        {
            Microsoft.UI.Windowing.AppWindow window = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(
                XamlRoot.ContentIslandEnvironment.AppWindowId);
            IntPtr hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(window.Id);
            Service.SystemShare.Show(hwnd, ViewModel.Nickname + " 的抖音主页", link, "抖音用户主页");
            return;
        }
        catch (Exception ex)
        {
            MfLog.Write("app: 系统分享主页失败，回退复制：" + ex.GetType().Name + " · " + ex.Message);
        }

        CopyText(link);
    }

    /// <summary>作品卡右键：复制长链（浏览器实测可直接开）。</summary>
    private void OnCopyVideoLink(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } && id.Length > 0)
        {
            CopyText("https://www.douyin.com/video/" + id);
        }
    }

    /// <summary>作品卡右键：交给系统浏览器打开。</summary>
    private async void OnOpenVideoInBrowser(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } && id.Length > 0)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://www.douyin.com/video/" + id));
        }
    }

    /// <summary>作品卡右键：复制作品 ID（调试/对账用）。</summary>
    private void OnCopyVideoId(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id } && id.Length > 0)
        {
            CopyText(id);
        }
    }

    private void CopyText(string text)
    {
        DataPackage pkg = new();
        pkg.SetText(text);
        Clipboard.SetContent(pkg);
        ShowTip("已复制：" + text);
    }

    /// <summary>
    /// 点合集卡：官方 /collection/{id}/1 实测就是 302 到 /video/{第一集 id}，所以这里先取逐集列表，
    /// 再把第一集交给标准视频页，上下文带本页集数——滚轮/连播能在合集里前后走。
    /// 短剧档的内容端点还没验，点了只给说明，不假装能开。
    /// </summary>
    private async void OnMixClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || id.Length == 0 || _openingMix)
        {
            return;
        }

        UserProfileMixRow? row = ViewModel.Mixes.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal));
        if (row is null)
        {
            return;
        }

        if (!row.IsMix)
        {
            ShowTip("短剧内容端点还没验，暂不能打开");
            return;
        }

        _openingMix = true;
        try
        {
            ShowTip("合集读取中…");
            MixEpisodePage page = await Task.Run(() => Douyin.Feed.MixEpisodesAsync(id, 0, 20, CancellationToken.None));
            if (page.Items.Count == 0)
            {
                ShowTip("这个合集还没取到内容");
                return;
            }

            List<SharedVideoRef> context = page.Items
                .Where(static x => x.Card.Id.Length > 0)
                .Select(static x => new SharedVideoRef(x.Card.Id, null))
                .ToList();
            MixEpisode first = page.Items[0];
            if (!Frame.Navigate(typeof(SharedVideoPage), new SharedVideoArgs(VideoEntry.From(first.Card), null, context)))
            {
                ShowTip("合集打开失败");
                MfLog.Write("app: 合集点开后导航失败 " + id);
            }
        }
        catch (Exception ex)
        {
            ShowTip("合集没打开：" + ex.Message);
            MfLog.Write("app: 合集打开失败 " + ex.GetType().Name + " · " + ex.Message);
        }
        finally
        {
            _openingMix = false;
        }
    }

    /// <summary>页内轻提示：没有现成 Toast 帮手，就一枚自己收的文本（三秒后撤）。</summary>
    private void ShowTip(string text)
    {
        TipText.Text = text;
        TipText.Visibility = Visibility.Visible;
        _tipTimer ??= DispatcherQueue.GetForCurrentThread().CreateTimer();
        _tipTimer.Interval = TimeSpan.FromSeconds(3);   // DispatcherQueueTimer 默认不重复，没有 IsOneShot 这枚属性
        _tipTimer.Stop();
        _tipTimer.Tick -= OnTipExpired;
        _tipTimer.Tick += OnTipExpired;
        _tipTimer.Start();
    }

    private void OnTipExpired(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) =>
        TipText.Visibility = Visibility.Collapsed;
}
