using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using WinDouYin.Controls;
using WinDouYin.Contracts;
using WinDouYin.Service;
using WinDouYin.ViewModels;
using WinDouYin.Views;
// Windows.System 整包引进来会和 Microsoft.UI.Dispatching 的 DispatcherQueueTimer 撞名，只借这一个枚举
using VirtualKey = Windows.System.VirtualKey;

namespace WinDouYin;

public sealed partial class MainWindow : Window
{
    /// <summary>标题栏区域重算的合拍器：拖拽缩放时每个 SizeChanged 都重算会带上一次 TransformToVisual（强制布局），
    /// 连续改动会把 resize 循环拖慢；等缩放停下来 120ms 再算一次就够——期间按钮短暂不可点无碍。</summary>
    private readonly DispatcherQueueTimer _titlebarSettle;

    // 缩放骤发取证：事件数 / 合成帧数 / 我们处理器自占耗时，爆完记一行到 startup.log
    private readonly DispatcherQueueTimer _resizeReport;
    private readonly Stopwatch _resizeClock = new();
    private int _resizeEvents;
    private int _resizeFrames;
    private long _resizeHandlerTicks;
    private bool _resizeFramesHooked;

    // 内容冻结框（见 UpdateNavFreeze）：拖拽期间导航内容钉在旧尺寸，只更新缩放变换
    private bool _navFrozen;
    private double _freezeW;
    private double _freezeH;

    // 用户主页（Frame 按类型缓存，就这一份）：登录态一变，哪怕它不在前台也要记脏
    private UserProfilePage? _profilePage;

    // 页脚「账号」那一项：图标跟着登录账号的头像走（未登录时回落到字形）
    private NavItem? _accountNavItem;

    // ── 诊断总闸：窗口尺寸变化路径上的"我们的代码"─────────────────────────
    // false = 拖拽/缩放窗口时，下列全部不跑：诊断量尺（帧计数/日志）、内容冻结框（UpdateNavFreeze）、
    // 舞台的缩放通知（NoteWindowResize）、标题栏续拍、登录面板随窗适配。
    // 只剩两件保命动作：标题栏命中区在拖拽停下算一次；视频元素把新尺寸应用一次。
    // 目的：回答"缩放卡顿到底是不是我们自己的代码造成的"——全关了还卡，就该去框架/机器侧找。
    internal static readonly bool ResizeWorkEnabled = false;

    // 诊断裁剪链（顶栏档 + 本人页构建档）2026-10-21 结案后拆干净了：
    // 那发 0xC0000005 定死在"缩放宿主(Viewbox) + 第二台并排 ItemsRepeater"这棵树上，
    // 与顶栏自管命中区、与集合替换、与线程都无关（详见 HANDOVER 与仓库笔记第 50 条）。
    // 包时间戳这条留着——它挡的是"拿旧二进制下结论"那一类假证，成本一行。
    private static string PackageStamp()
    {
        try
        {
            return "包=" + System.IO.File.GetLastWriteTime(Environment.ProcessPath ?? "WinDouYin.exe").ToString("MM-dd HH:mm:ss");
        }
        catch (Exception)
        {
            return "包=?";
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        App.Log("MW: XAML 已加载 · " + PackageStamp());
        AppWindow.Resize(new SizeInt32(1560, 980));

        // 页脚「账号」用登录账号的头像做图标；登录/退出时由 LoginVM 的属性通知带过来
        _accountNavItem = FindNavItem("Account") as NavItem;
        SyncAccountAvatar();

        ContentFrame.Navigated += (_, e) =>
        {
            // 本人主页的登录闸门要弹主窗口那张统一登录框：页面只管喊，宿主负责开
            if (e.Content is UserProfilePage page)
            {
                _profilePage = page;
                page.LoginRequested -= OnLoginRequested;
                page.LoginRequested += OnLoginRequested;
            }

            SyncTitleBarBack();
        };

        var startup = AppPages.Find(((App)Application.Current).ViewModel.AppSettingsVM.StartupPage)
            ?? AppPages.StartupChoices[0];
        Nav.SelectedItem = FindNavItem(startup.Tag);
        NavigateTo(startup);
        App.Log("MW: 起始站已导航 " + startup.Tag);
        Nav.ItemInvoked += Nav_ItemInvoked;

        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        // 头像格点开的是账号菜单（AccountBadge 自己的 Flyout），菜单里"登录"那枚才喊到这里：开居中登录面板；
        // "添加账号"开同一张面板，但要它挂一张新码并保留当前号。登录成功后面板自己请我们收起
        AccountBadgeHost.LoginRequested += OnLoginRequested;
        AccountBadgeHost.AddAccountRequested += OnAddAccountRequested;
        AccountBadgeHost.MessagesRequested += OnMessagesRequested;
        MessagesLayer.ShareOpenRequested += OnShareOpenRequested;
        LoginOverlayPanel.DismissRequested += CloseLoginDialog;

        // 顶部搜索框的分工：输词走 AutoSuggestBox 原生下拉，空输入走自绘的三段弹层（SuggestFlyout）。
        // 原生那面只管建议列表本身，焦点/回车/上下键都是它的，我们只喂 ItemsSource。
        TitleBarSearchBox.TextChanged += OnSearchTextChanged;
        TitleBarSearchBox.SuggestionChosen += OnSearchSuggestionChosen;
        TitleBarSearchBox.QuerySubmitted += OnSearchQuerySubmitted;
        TitleBarSearchBox.GotFocus += OnSearchBoxGotFocus;
        TitleBarSearchBox.LostFocus += OnSearchBoxLostFocus;
        TitleBarSearchBox.PreviewKeyDown += OnSearchBoxPreviewKeyDown;
        SuggestFlyout.Closed += (_, _) => ClearSuggestionRows();
        RootLayout.Loaded += (_, _) => _ = EnsureSuggestionDataAsync();

        // 预输入补全的合拍器：官方是每敲一下发一发，我们敲字停 220ms 才发（一发 20KB，且回包按字轮转）
        _sugDebounce = DispatcherQueue.CreateTimer();
        _sugDebounce.Interval = TimeSpan.FromMilliseconds(220);
        _sugDebounce.IsRepeating = false;
        _sugDebounce.Tick += OnSugDebounceTick;

        // 头像属性带着页脚「账号」那颗走；账号本体换了（加号/切号/退出）由下面的 AccountChanged 统一收场。
        ((App)Application.Current).ViewModel.LoginVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LoginViewModel.Avatar))
            {
                SyncAccountAvatar();
            }
        };

        // 多账号下只看 IsLoggedIn 不够：A 号切 B 号时它一直是 true，上一号的关注/朋友/精选列表却全失效了。
        ((App)Application.Current).ViewModel.LoginVM.AccountChanged += OnAccountContextChanged;
        RootLayout.SizeChanged += (_, _) =>
        {
            if (!ResizeWorkEnabled)
            {
                return; // 诊断实验：窗口尺寸变化时我们的代码一律不跑（见 ResizeWorkEnabled）
            }

            // 让舞台知道窗口在动：缩放期间不做工作集修剪（那是一次同步页表活）
            long t0 = Stopwatch.GetTimestamp();
            MfStageControl.NoteWindowResize();
            UpdateNavFreeze();
            if (_loginDialogOpen)
            {
                FitLoginOverlay();
            }

            _resizeHandlerTicks += Stopwatch.GetTimestamp() - t0;
            NoteResizeBurst();

            // 标题栏命中区续拍：纵向拖动时标题栏尺寸不变、AppTitleBar.SizeChanged 不来，得由这里续
            _titlebarSettle.Stop();
            _titlebarSettle.Start();
        };
        _titlebarSettle = DispatcherQueue.CreateTimer();
        _titlebarSettle.Interval = TimeSpan.FromMilliseconds(120);
        _titlebarSettle.IsRepeating = false;
        _titlebarSettle.Tick += (_, _) => NudgeDragRegions();

        // 缩放骤发的帧率取证（见 NoteResizeBurst）：手停下来 400ms 记一行
        _resizeReport = DispatcherQueue.CreateTimer();
        _resizeReport.Interval = TimeSpan.FromMilliseconds(400);
        _resizeReport.IsRepeating = false;
        _resizeReport.Tick += (_, _) => ReportResizeBurst();
        AppTitleBar.Loaded += (_, _) => NudgeDragRegions();
        AppTitleBar.SizeChanged += (_, _) =>
        {
            _titlebarSettle.Stop();
            _titlebarSettle.Start();
        };

        // 沉浸态跨控件共享：舞台那颗按钮翻状态，这里收顶栏、藏侧边栏、亮左上角关闭按钮
        Chrome.ImmersiveChanged += ApplyImmersive;

        // 主窗口关掉时把小窗一起收掉：它挂着那条播放器，留着就成一个没有回处的窗口。
        // 当前号的会话与 guard 材料也在这儿落一次——切号过程中攒的新 cookie 不该只留在内存里。
        Closed += (_, _) =>
        {
            VideoDiskCache.Flush();
            Douyin.Feed.PersistCurrentAccountNow();
            MiniPlayerWindow.Current?.CloseWithoutReturn();
        };

        // 内置设置项不会被 MenuItemTemplate 套用，且自带 AnimatedIcon 会与竖排模板重复；换成竖排模板并清掉图标
        Nav.Loaded += (_, _) =>
        {
            if (Nav.SettingsItem is NavigationViewItem settingsItem)
            {
                settingsItem.Icon = null;
                settingsItem.ContentTemplate = (DataTemplate)Nav.Resources["SettingsItemTemplate"];
            }
        };
    }

    private object? FindNavItem(string tag)
    {
        foreach (var item in Nav.MenuItems.Concat(Nav.FooterMenuItems))
        {
            if (item is NavItem nav && nav.Tag == tag)
            {
                return item;
            }
        }
        return null;
    }

    /// <summary>页脚「账号」的图标：登录账号的头像；没有头像（未登录/取图失败）就回落到人形字形。</summary>
    private void SyncAccountAvatar()
    {
        if (_accountNavItem is not null)
        {
            _accountNavItem.Avatar = ((App)Application.Current).ViewModel.LoginVM.Avatar;
        }
    }

    private void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            if (ContentFrame.CurrentSourcePageType != typeof(SettingsPage))
            {
                ContentFrame.Navigate(typeof(SettingsPage));
            }
        }
        else if (args.InvokedItem is NavItem item && AppPages.Find(item.Tag) is { } page)
        {
            NavigateTo(page);
        }
        else
        {
            return;
        }

        // 点左栏那几项是"换根页面"、不是"往里钻"：清掉回访栈，返回键才只在真有来路的地方出现
        ContentFrame.BackStack.Clear();
        SyncTitleBarBack();
    }

    /// <summary>
    /// 顶栏那颗返回键（模板里的 <c>PART_BackButton</c>）要不要进场。<b>true = 照常按 <c>CanGoBack</c> 亮/藏</b>。
    /// 留这个开关只为取证：再遇到顶栏相关的原生崩溃，把它设 false 让那颗延迟零件永不进树，一眼分辨是不是它。
    /// 2026-10-21 那次 <c>0xC0000005</c> 就是用它排除过一轮（设 false 照崩 ⇒ 与返回键无关）。
    /// 开着它时命中区要替那颗键挖洞，见 <see cref="SetRegionsForCustomTitleBar"/>。
    /// </summary>
    internal static readonly bool TitleBarBackButtonEnabled = true;

    /// <summary>
    /// 返回键归标题栏那颗（NavigationView 栏顶那颗永久收着）：只在真有来路时出现，不常驻占位。
    /// 亮/藏之后请控件自己重算一次命中区——顶栏这本账归系统 TitleBar 控件，我们只在沉浸态才动手。
    /// </summary>
    private void SyncTitleBarBack()
    {
        if (TitleBarBackButtonEnabled)
        {
            AppTitleBar.IsBackButtonVisible = ContentFrame.CanGoBack;
        }

        NudgeDragRegions();
    }

    private void OnTitleBarBackRequested(TitleBar sender, object args)
    {
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
        }
    }

    /// <summary>按注册表导航：本人主页和他人主页共用一个 Page 类型，但参数不一样，不能只比对类型。</summary>
    private void NavigateTo(AppPage page)
    {
        if (page.SelfProfile)
        {
            if (ContentFrame.Content is UserProfilePage { IsSelfProfile: true })
            {
                return;   // 已经在本人主页上：再点一次不重载、不丢翻到的页
            }

            UserProfilePage.OpenSelf(ContentFrame);
            return;
        }

        if (ContentFrame.CurrentSourcePageType != page.PageType)
        {
            ContentFrame.Navigate(page.PageType);
        }
    }

    private bool _loginDialogOpen;

    private void OnLoginRequested() => _ = OpenLoginDialogAsync(addAccount: false);

    private void OnAddAccountRequested() => _ = OpenLoginDialogAsync(addAccount: true);

    /// <summary>
    /// 当前号换了：消息面板、账号相关页面都得跟着换，不能把上一个号的列表继续摆着。
    /// 前台页重建一次就够；不在前台的本人主页只记脏，等导航回来自动重载。
    /// </summary>
    private void OnAccountContextChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnAccountContextChanged);
            return;
        }

        MessagesSplit.IsPaneOpen = false;
        MessagesLayer.Reset();

        // 精选账本按号分：先作废旧内容，再在后台把「全部」第一页给新号取回来。
        // 页面在缓存里复用时不会因为这行重建，但账面已经清空，OnPageLoaded 会重新铺。
        string featuredUid = Douyin.Feed.MyUid.Length > 0 ? Douyin.Feed.MyUid : Douyin.Feed.CurrentAccountUid;
        FeaturedViewModel.Shared.ResetForAccount(featuredUid);
        if (Douyin.Feed.IsLoggedIn)
        {
            FeaturedViewModel.Shared.PrefetchAfter(
                FeaturedViewModel.CategoryTable[0], TimeSpan.FromSeconds(2));
        }

        // 搜索历史也按号分本；标脏一次，下次聚焦搜索框重新读当前那本。
        _historyUid = null;

        if (_profilePage is not null)
        {
            _profilePage.MarkSelfProfileStale();
            if (ReferenceEquals(ContentFrame.Content, _profilePage) && _profilePage.IsSelfProfile)
            {
                _profilePage.ReloadSelfProfile();
                SyncAccountAvatar();
                return;
            }
        }

        switch (ContentFrame.Content)
        {
            case FeaturedPage featured:
                // 精选页有按分类的账本，直接把账清到新号并重铺，别赌缓存页重新 Loaded。
                featured.ReloadForAccountChange();
                break;
            case FollowingPage:
            case FriendsPage:
                ReNavigateCurrentPage();
                break;
        }

        SyncAccountAvatar();
    }

    /// <summary>搜索历史按账号分本：uid 没认出来就归到"匿名"那一本，别让 A 号的词跑到 B 号面板上。</summary>
    private static string CurrentSearchUid() =>
        Douyin.Feed.MyUid.Length > 0 ? Douyin.Feed.MyUid : Douyin.Feed.CurrentAccountUid;

    /// <summary>把当前这一站整页重建（账号切了，页面上的关注/朋友/精选列表都不再属于这个号）。</summary>
    private void ReNavigateCurrentPage()
    {
        Type? pageType = ContentFrame.CurrentSourcePageType;
        if (pageType is null)
        {
            return;
        }

        ContentFrame.BackStack.Clear();
        ContentFrame.Navigate(pageType);
        SyncTitleBarBack();
    }

    private void OnMessagesRequested()
    {
        MessagesSplit.IsPaneOpen = true;
        _ = MessagesLayer.OpenAsync();
    }

    private void OnMessagesCloseClick(object? sender, EventArgs e) =>
        MessagesSplit.IsPaneOpen = false;

    // ===== 顶部搜索框 =====

    private IReadOnlyList<HotWord> _hotSearches = [];
    private List<string> _guessWords = [];
    private List<string> _history = [];
    private string? _historyUid;
    // 预输入（search/sug）：敲字停一下才发；回包认序号——用户又敲了字的过期包一律丢；
    // 同一个前缀这一程缓存着，来回删字不再重发。
    private readonly DispatcherQueueTimer _sugDebounce;
    private string _sugPending = string.Empty;
    private int _sugSeq;
    private readonly Dictionary<string, List<string>> _sugCache = new(StringComparer.Ordinal);
    private bool _suggestionLoading;
    // AutoSuggestBox 自身不拿焦点（焦点在内层 TextBox 上），它的 FocusState 一直是 Unfocused；
    // 面板开合只能靠这两个路由事件记账
    private bool _searchBoxFocused;
    // 弹层记账：Flyout 被系统光点掉（点外面）时不会通知我们，只能跟着它的 Closed 走
    private bool _suggestionsShown;

    private void OnSearchBoxGotFocus(object sender, RoutedEventArgs e)
    {
        _searchBoxFocused = true;
        // 焦点刚落到内层 TextBox 时开列表不可靠，等这一拍聚焦流程走完再摆
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_searchBoxFocused)
            {
                _ = EnsureSuggestionDataAsync();
            }
        });
    }

    // 焦点离开输入框不主动收面板：点外面本来就由 Flyout 的光点 dismiss 负责（和原先自带下拉一个行为），
    // 而点建议行/换一换那一下焦点正是往弹层里走的，这时候收等于把这一击吞掉。
    private void OnSearchBoxLostFocus(object sender, RoutedEventArgs e) => _searchBoxFocused = false;

    /// <summary>
    /// 面板要用的两份远端数据（热搜榜、猜你想搜）各取一次、进程内缓存；搜索历史读盘。
    /// 哪一份取不到就少摆哪一栏，不拦着面板出现。
    /// </summary>
    private async Task EnsureSuggestionDataAsync()
    {
        string uid = CurrentSearchUid();
        if (_historyUid != uid || _history.Count == 0)
        {
            _historyUid = uid;
            _history = SearchHistory.Load(uid);
        }

        if ((_hotSearches.Count > 0 || _guessWords.Count > 0) || _suggestionLoading)
        {
            ShowSearchSuggestions(TitleBarSearchBox.Text);
            return;
        }

        _suggestionLoading = true;
        try
        {
            // 签名与几十 KB 报文解析都是同步活，扔线程池；两发互不相干，一起等
            Task<IReadOnlyList<HotWord>> hot = Task.Run(() => Douyin.Feed.HotSearchAsync(CancellationToken.None));
            Task<IReadOnlyList<string>> guess = Task.Run(() => Douyin.Feed.GuessWordsAsync(CancellationToken.None));
            await Task.WhenAll(hot, guess);
            // WhenAll 只把第一个异常抛上来，所以两份各按任务状态取，成功的那份不跟着丢
            _hotSearches = hot.IsCompletedSuccessfully ? hot.Result : [];
            _guessWords = guess.IsCompletedSuccessfully ? [.. guess.Result] : [];
        }
        catch (Exception ex)
        {
            MfLog.Write("app: 搜索建议取不到 " + ex.GetType().Name + " · " + ex.Message);
        }
        finally
        {
            _suggestionLoading = false;
        }

        ShowSearchSuggestions(TitleBarSearchBox.Text);
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ShowSearchSuggestions(sender.Text);
        }
    }

    /// <summary>原生下拉里选中一条（回车或点击都会走到这）：把词填回输入框。</summary>
    private void OnSearchSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is SearchSuggestion suggestion)
        {
            sender.Text = suggestion.Word;
        }
    }

    private void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        string keyword = (args.QueryText ?? string.Empty).Trim();
        if (keyword.Length == 0 && args.ChosenSuggestion is SearchSuggestion chosen)
        {
            keyword = chosen.Word.Trim();
        }

        if (keyword.Length == 0)
        {
            return;
        }

        sender.Text = keyword;
        CloseSuggestions();
        OpenSearch(keyword);
    }

    /// <summary>自绘弹层开着时 Esc 收它。上下键与回车一律不拦——输词那面是原生下拉，那是它的活。</summary>
    private void OnSearchBoxPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_suggestionsShown && e.Key == VirtualKey.Escape)
        {
            CloseSuggestions();
            e.Handled = true;
        }
    }

    /// <summary>面板里任何一行被点中或被回车选中：填进搜索框、收面板、进搜索页（历史在 OpenSearch 里记）。</summary>
    private void Choose(string word)
    {
        word = word.Trim();
        if (word.Length == 0)
        {
            return;
        }

        _searchBoxFocused = false;
        TitleBarSearchBox.Text = word;
        CloseSuggestions();
        OpenSearch(word);
    }

    /// <summary>复合面板的一行（历史词 / 猜你想搜 / 热点词）：Tag 就是那颗词。</summary>
    private void OnSuggestRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string word })
        {
            Choose(word);
        }
    }

    /// <summary>段标题右侧那两颗：清空（本机历史）与换一换（重发猜你想搜那一发）。走完再重摆一次，面板不收。</summary>
    private async void OnSuggestHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string command })
        {
            return;
        }

        if (command == "clear")
        {
            string uid = CurrentSearchUid();
            SearchHistory.Clear(uid);
            _history = [];
            _historyUid = uid;
        }
        else if (command == "shuffle")
        {
            try
            {
                IReadOnlyList<string> fresh = await Task.Run(() => Douyin.Feed.GuessWordsAsync(CancellationToken.None));
                _guessWords = [.. fresh];
            }
            catch (Exception ex)
            {
                MfLog.Write("app: 猜你想搜换一换失败 " + ex.GetType().Name + " · " + ex.Message);
            }
        }

        ShowSearchSuggestions(TitleBarSearchBox.Text);
    }

    /// <summary>两面一起收：自绘弹层 Hide，原生下拉关列表。</summary>
    private void CloseSuggestions()
    {
        TitleBarSearchBox.IsSuggestionListOpen = false;
        if (_suggestionsShown)
        {
            SuggestFlyout.Hide();
        }
    }

    /// <summary>弹层收起（含系统光点掉）后清场：三份行数据连同容器一并放掉，别挂在内存里。</summary>
    private void ClearSuggestionRows()
    {
        _suggestionsShown = false;
        Composite.Visibility = Visibility.Collapsed;
        HistoryList.ItemsSource = null;
        GuessList.ItemsSource = null;
        HotList.ItemsSource = null;
    }

    /// <summary>
    /// 摆哪一面看有没有输词：空输入是三段复合面板（搜索历史 / 猜你想搜 / 抖音热点），一开输就换成
    /// <c>AutoSuggestBox</c> 原生下拉喂单列建议，最上面那条永远是"搜索这句话"。官方也是这个分工——
    /// 复合面板只在空输入时出现。输词这面的词由本地池先出、服务端 <c>search/sug</c> 补全晚到再换表。
    /// </summary>
    private void ShowSearchSuggestions(string text)
    {
        // 面板还开着就允许刷新：点「换一换」「清空」时焦点已经不在输入框上了
        if (!_searchBoxFocused && !_suggestionsShown)
        {
            return;
        }

        string query = (text ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            ShowCompositePanel();
            return;
        }

        // 本地池先出，保证敲下第一个字就有东西看；真正的补全在下面那一发
        List<SearchSuggestion> items = BuildSuggestions(query, null);

        // 输词这面整个交回 AutoSuggestBox 原生下拉：只喂数据、开列表，焦点/上下键/回车都是它自己的事
        if (_suggestionsShown)
        {
            SuggestFlyout.Hide();
        }

        TitleBarSearchBox.ItemsSource = items;
        TitleBarSearchBox.IsSuggestionListOpen = items.Count > 0;

        RequestSug(query);
    }

    /// <summary>
    /// 输词那一面的建议表：第一条永远是"搜索这句话"，其后先服务端补全、再本地（历史 → 热点 → 猜你想搜）。
    /// 跟当前输入同词的丢掉、重复词只留先到的那条；命中的正好在热榜上，就顺手带上名次与热度。
    /// </summary>
    private List<SearchSuggestion> BuildSuggestions(string query, IReadOnlyList<string>? sug)
    {
        var items = new List<SearchSuggestion> { SearchSuggestion.ForQuery(query) };

        void Add(string word)
        {
            if (items.Count >= 10 || word.Length == 0 || string.Equals(word, query, StringComparison.Ordinal))
            {
                return;
            }

            foreach (SearchSuggestion seen in items)
            {
                if (string.Equals(seen.Word, word, StringComparison.Ordinal))
                {
                    return;
                }
            }

            int hot = IndexOfHot(word);
            items.Add(hot >= 0
                ? SearchSuggestion.ForHot(word, hot + 1, _hotSearches[hot].HotValue)
                : SearchSuggestion.ForPlain(word));
        }

        if (sug != null)
        {
            foreach (string word in sug)
            {
                Add(word);
            }
        }

        foreach (string word in _history)
        {
            if (word.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                Add(word);
            }
        }

        foreach (HotWord hot in _hotSearches)
        {
            if (hot.Word.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                Add(hot.Word);
            }
        }

        foreach (string word in _guessWords)
        {
            if (word.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                Add(word);
            }
        }

        return items;
    }

    private int IndexOfHot(string word)
    {
        for (int i = 0; i < _hotSearches.Count; i++)
        {
            if (string.Equals(_hotSearches[i].Word, word, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>给这一发补全排期：命中缓存就地换表，否则重计时 220ms（连打只发最后一发）。</summary>
    private void RequestSug(string query)
    {
        if (_sugCache.TryGetValue(query, out List<string>? cached))
        {
            MergeSug(query, cached);
            return;
        }

        _sugPending = query;
        _sugSeq++;
        _sugDebounce.Stop();
        _sugDebounce.Start();
    }

    private async void OnSugDebounceTick(object? sender, object e)
    {
        _sugDebounce.Stop();
        string query = _sugPending;
        int seq = _sugSeq;
        try
        {
            IReadOnlyList<string> words = await Task.Run(() => Douyin.Feed.SugAsync(query, CancellationToken.None));
            var list = new List<string>(words);
            if (_sugCache.Count > 120)
            {
                _sugCache.Clear();
            }

            _sugCache[query] = list;
            MergeSug(query, list, seq);
        }
        catch (Exception ex)
        {
            MfLog.Write("app: 预输入补全失败 " + ex.GetType().Name + " · " + ex.Message);
        }
    }

    /// <summary>
    /// 补全回包换表。三道闸：序号过期（用户又敲了字）、词已经改了（删字/清空）、列表被 Esc 收掉——
    /// 任一条踩中就丢包，晚到的包不许把面板重新撑开。
    /// </summary>
    private void MergeSug(string query, IReadOnlyList<string> words, int seq = -1)
    {
        if (seq >= 0 && seq != _sugSeq)
        {
            return;
        }

        if (!TitleBarSearchBox.IsSuggestionListOpen
            || (TitleBarSearchBox.Text ?? string.Empty).Trim() != query)
        {
            return;
        }

        TitleBarSearchBox.ItemsSource = BuildSuggestions(query, words);
    }

    /// <summary>空输入那一面：三段各自按"有没有货"显隐，三段都空就干脆不开面板。</summary>
    private void ShowCompositePanel()
    {
        // 原生那面断源，免得两层建议叠着出
        TitleBarSearchBox.ItemsSource = null;
        TitleBarSearchBox.IsSuggestionListOpen = false;

        // 历史整罐倒进去（本机存 20 条）：四行是可视档，剩下的由这一段自己滚，面板那层不再跟着长高
        var history = new List<SearchSuggestion>();
        foreach (string word in _history)
        {
            history.Add(SearchSuggestion.ForPlain(word));
        }

        // 猜你想搜两列铺开，摆四行（官方那一栏就是 8 条）
        var guess = new List<SearchGuessPair>();
        for (int i = 0; i < _guessWords.Count && guess.Count < 4; i += 2)
        {
            string right = i + 1 < _guessWords.Count ? _guessWords[i + 1] : string.Empty;
            guess.Add(new SearchGuessPair(_guessWords[i], right));
        }

        var hot = new List<SearchSuggestion>();
        for (int i = 0; i < _hotSearches.Count && i < 10; i++)
        {
            hot.Add(SearchSuggestion.ForHot(_hotSearches[i].Word, i + 1, _hotSearches[i].HotValue));
        }

        HistoryHeader.Visibility = history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GuessHeader.Visibility = guess.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HotHeader.Visibility = hot.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryList.ItemsSource = history;
        GuessList.ItemsSource = guess;
        HotList.ItemsSource = hot;

        Composite.Visibility = history.Count > 0 || guess.Count > 0 || hot.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        OpenPanel();
    }

    /// <summary>自绘弹层该不该在：三段有内容就开、全空就收。</summary>
    private void OpenPanel()
    {
        if (Composite.Visibility != Visibility.Visible)
        {
            CloseSuggestions();
            return;
        }

        if (_suggestionsShown)
        {
            return;
        }

        _suggestionsShown = true;
        SuggestFlyout.ShowAt(TitleBarSearchBox);
    }

    private void OpenSearch(string keyword)
    {
        // 记一笔本机搜索历史。官方按 uid 分本，这里也跟着切号；当前号还没认出来就进"匿名"那本。
        string uid = CurrentSearchUid();
        SearchHistory.Append(uid, keyword);
        _historyUid = uid;
        _history = SearchHistory.Load(uid);

        if (ContentFrame.CurrentSourcePageType == typeof(SearchPage) && ContentFrame.Content is SearchPage page)
        {
            page.Search(keyword);
            return;
        }

        if (!ContentFrame.Navigate(typeof(SearchPage), keyword))
        {
            MfLog.Write("app: 搜索页导航失败 " + keyword);
        }
    }

    /// <summary>聊天里点分享卡：取这条视频的详情，在主窗口 ContentFrame 中导航到 SharedVideoPage。</summary>
    private async void OnShareOpenRequested(object? sender, ShareOpenEventArgs e)
    {
        if (e.VideoId.Length == 0)
        {
            return;
        }

        try
        {
            (WinDouYin.Contracts.VideoCard? card, bool takenDown) = await Douyin.Feed.VideoDetailProbeAsync(e.VideoId, CancellationToken.None);
            if (takenDown)
            {
                // 下架/不存在：multi/aweme/detail 只回一个空数组。进详情页照样能看来源评论，视频位置放占位。
                MessagesSplit.IsPaneOpen = false;
                if (!ContentFrame.Navigate(
                        typeof(SharedVideoPage),
                        new SharedVideoArgs(VideoEntry.TakenDown(e.VideoId), e.ReviewArgs, e.Context)))
                {
                    await ShowShareErrorAsync("主窗口没能导航到视频页");
                }

                return;
            }

            if (card is null)
            {
                await ShowShareErrorAsync("详情接口没取到这条视频（id=" + e.VideoId + ")");
                return;
            }

            MessagesSplit.IsPaneOpen = false;
            if (!ContentFrame.Navigate(
                    typeof(SharedVideoPage),
                    new SharedVideoArgs(VideoEntry.From(card!), e.ReviewArgs, e.Context)))
            {
                await ShowShareErrorAsync("主窗口没能导航到视频页");
            }
        }
        catch (Exception ex)
        {
            await ShowShareErrorAsync(ex.GetType().Name + " · " + ex.Message);
        }
    }

    private async Task ShowShareErrorAsync(string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "打不开这条视频",
                Content = message,
                CloseButtonText = "知道了",
                XamlRoot = RootLayout.XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch
        {
            // 已经有别的 ContentDialog 开着时不抢
        }
    }

    /// <summary>
    /// 登录面板 = 默认样式的 ContentDialog。开门前只做两件事之外的三个小动作：
    /// ① 默认的 ContentDialogMaxWidth 是 548，会把面板憋小——按窗口预算放开（只动尺寸，观感保持系统默认）；
    /// ② 每次打开重挂一张码（LoginPanel.Restart，上一张多半已过期）；
    /// ③ "添加账号"先把当前号落档——登录框那一下会把它自己的 guard 材料顶成全局那份。
    /// </summary>
    private async Task OpenLoginDialogAsync(bool addAccount)
    {
        if (_loginDialogOpen)
        {
            return;
        }

        _loginDialogOpen = true;
        try
        {
            if (addAccount)
            {
                await ((App)Application.Current).ViewModel.LoginVM.PrepareAddAccountAsync();
            }
        }
        catch (Exception ex)
        {
            App.Log("添加账号前落档失败：" + ex.Message);
        }

        LoginDialog.Resources["ContentDialogMaxWidth"] = Math.Max(600, RootLayout.ActualWidth - 160);
        if (LoginDialog.XamlRoot is null)
        {
            LoginDialog.XamlRoot = RootLayout.XamlRoot;
        }

        FitLoginOverlay();
        if (addAccount)
        {
            LoginOverlayPanel.BeginAddAccount();
        }

        LoginOverlayPanel.Restart();
        try
        {
            await LoginDialog.ShowAsync();
        }
        catch (Exception ex)
        {
            App.Log("登录面板没弹出来：" + ex.Message);
        }
        finally
        {
            // 面板收场统一走这儿：停轮询、放掉这一趟、退出添加模式
            _loginDialogOpen = false;
            LoginOverlayPanel.Cancel();
            LoginOverlayPanel.EndAddAccount();
        }
    }

    private void CloseLoginDialog()
    {
        if (_loginDialogOpen)
        {
            LoginDialog.Hide();
        }
    }

    /// <summary>登录卡片比例 726:483：按窗口留边给预算，小窗就再等比缩（FitTo 里封顶 1:1）。</summary>
    private void FitLoginOverlay()
    {
        LoginOverlayPanel.FitTo(
            Math.Max(360, RootLayout.ActualWidth - 240),
            Math.Max(260, RootLayout.ActualHeight - 200));
    }

    private void OnLoginEscInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!_loginDialogOpen)
        {
            return;
        }

        args.Handled = true;
        CloseLoginDialog();
    }

    /// <summary>
    /// 缩放骤发的帧率取证：事件数、合成帧数（CompositionTarget.Rendering）、我们处理器自占耗时，
    /// 一并记到 startup.log。fps 低而自占可忽略 → 重的不是我们的代码，得从渲染/框架侧找。
    /// </summary>
    private void NoteResizeBurst()
    {
        _resizeEvents++;
        if (!_resizeFramesHooked)
        {
            _resizeFramesHooked = true;
            _resizeClock.Restart();
            CompositionTarget.Rendering += OnResizeFrame;
        }

        _resizeReport.Stop();
        _resizeReport.Start();
    }

    private void OnResizeFrame(object? sender, object e) => _resizeFrames++;

    private void ReportResizeBurst()
    {
        if (_resizeFramesHooked)
        {
            CompositionTarget.Rendering -= OnResizeFrame;
            _resizeFramesHooked = false;
        }

        double ms = _resizeClock.Elapsed.TotalMilliseconds;
        _resizeClock.Reset();
        double fps = ms > 1 ? _resizeFrames * 1000.0 / ms : 0;
        double selfMs = _resizeHandlerTicks * 1000.0 / Stopwatch.Frequency;
        string page = ContentFrame.CurrentSourcePageType?.Name ?? "?";
        App.Log($"缩放骤发：页面 {page} · 事件 {_resizeEvents} · 帧 {_resizeFrames} · 时长 {ms:F0}ms · fps {fps:F0} · 处理器自占 {selfMs:F1}ms");

        // 手停稳了（本计时器就是"安静 400ms"）：冻结框解冻，整棵树在最终尺寸上真落位
        UnfreezeNav();
        _resizeEvents = 0;
        _resizeFrames = 0;
        _resizeHandlerTicks = 0;
    }

    /// <summary>
    /// 拖拽缩放期间的内容冻结框：把导航内容钉在第一拍量到的尺寸上，之后只更新缩放变换——
    /// NavigationView 里的整棵树（两页乒乓、视频面、信息区、控制条）不再逐帧重排重渲。
    /// 观感就是浏览器式的：窗口先跟手，内容拉伸着等，手停 400ms 后真落位。
    /// </summary>
    private void UpdateNavFreeze()
    {
        double availW = Math.Max(1, RootLayout.ActualWidth);
        double availH = Math.Max(1, RootLayout.ActualHeight - AppTitleBar.ActualHeight);

        if (!_navFrozen)
        {
            double w = NavFreeze.ActualWidth;
            double h = NavFreeze.ActualHeight;
            if (w < 1 || h < 1)
            {
                return;
            }

            _navFrozen = true;
            _freezeW = w;
            _freezeH = h;
            NavFreeze.HorizontalAlignment = HorizontalAlignment.Left;
            NavFreeze.VerticalAlignment = VerticalAlignment.Top;
            NavFreeze.Width = w;
            NavFreeze.Height = h;
            NavFreeze.RenderTransform = new ScaleTransform();
        }

        if (NavFreeze.RenderTransform is ScaleTransform scale)
        {
            scale.ScaleX = availW / _freezeW;
            scale.ScaleY = availH / _freezeH;
        }
    }

    /// <summary>缩放骤发结束（或从未开始）：冻结框解冻，整棵树在最终尺寸上真落一次位。</summary>
    private void UnfreezeNav()
    {
        if (!_navFrozen)
        {
            return;
        }

        _navFrozen = false;
        NavFreeze.Width = double.NaN;
        NavFreeze.Height = double.NaN;
        NavFreeze.HorizontalAlignment = HorizontalAlignment.Stretch;
        NavFreeze.VerticalAlignment = VerticalAlignment.Stretch;
        NavFreeze.RenderTransform = null;
    }

    /// <summary>进出沉浸的时长：一眼看得出是"滑过去"，又不拖沓。</summary>
    private const double ImmersiveMilliseconds = 220;

    /// <summary>顶栏高度，与 XAML 里 AppTitleBar.Height 一致：折叠动画要从它算起。</summary>
    private const double TitleBarHeight = 48;

    /// <summary>侧边栏宽度，与 XAML 里 Nav.OpenPaneLength 一致。</summary>
    private const double PaneWidth = 72;

    /// <summary>
    /// 沉浸（程序内全屏）：顶栏收掉、侧边栏滑出屏，左上角亮一颗关闭按钮。两边都不硬切。
    /// 退沉浸时顺手把"真全屏"也退了：全屏是"窗口满屏 + 收栏"合起来才算视频全屏，
    /// 光把栏放回来、窗口还满着，看着就是没退出（Esc 与左上角关闭按钮都走这条）。
    /// </summary>
    private void ApplyImmersive(bool on)
    {
        if (on)
        {
            EnterImmersive();
        }
        else
        {
            Chrome.LeaveFullscreen(AppWindow);
            ExitImmersive();
        }
    }

    /// <summary>
    /// 进沉浸：顶栏高度 48→0（内容跟着长高）、侧边栏滑出屏外。
    /// 滑法是给 NavFreeze（内容那一层）加宽 72 再整体左移 72：门滑出屏、内容原地长大到满宽。
    /// 动画跑完才真 IsPaneVisible=false，所以收尾这一下不跳。
    /// </summary>
    private void EnterImmersive()
    {
        // 顶栏要收平（高 48→0），搜索建议先撤：留着就成了悬在片上的孤儿弹层
        CloseSuggestions();
        ImmersiveCloseButton.Visibility = Visibility.Visible;

        double width = NavFreeze.ActualWidth;
        bool slide = width > 1;
        if (slide)
        {
            NavFreeze.HorizontalAlignment = HorizontalAlignment.Left;
            NavFreeze.Width = width;
            NavFreeze.RenderTransform = new TranslateTransform();
        }

        var board = new Storyboard();
        AnimateDependent(board, AppTitleBar, "Height", TitleBarHeight, 0, ImmersiveMilliseconds);
        AnimateDependent(board, AppTitleBar, "Opacity", 1, 0, ImmersiveMilliseconds * 0.6);
        if (slide)
        {
            AnimateDependent(board, NavFreeze, "Width", width, width + PaneWidth, ImmersiveMilliseconds);
            AnimateDependent(board, (TranslateTransform)NavFreeze.RenderTransform, "X", 0, -PaneWidth, ImmersiveMilliseconds);
        }

        board.Completed += (_, _) =>
        {
            // 先 Stop 把动画时钟摘掉：HoldEnd 会一直压着 Height/Opacity，后面的显式赋值落不下去
            board.Stop();
            AppTitleBar.Visibility = Visibility.Collapsed;
            AppTitleBar.Opacity = 1;
            AppTitleBar.Height = TitleBarHeight;
            Nav.IsPaneVisible = false;
            ResetNavSlide();
            NudgeDragRegions();
        };
        board.Begin();
    }

    /// <summary>
    /// 退沉浸：同一条曲线倒着放。先把冻结框摆成"门在屏外"的样子（宽 +72、左移 72）再把门放回来——
    /// 门回来这一下不会让画面宽度变，然后才动画滑回原位。
    /// </summary>
    private void ExitImmersive()
    {
        ImmersiveCloseButton.Visibility = Visibility.Collapsed;

        AppTitleBar.Visibility = Visibility.Visible;
        AppTitleBar.Height = 0;
        AppTitleBar.Opacity = 0;

        double width = NavFreeze.ActualWidth;
        bool slide = width > 1;
        if (slide)
        {
            NavFreeze.HorizontalAlignment = HorizontalAlignment.Left;
            NavFreeze.Width = width + PaneWidth;
            NavFreeze.RenderTransform = new TranslateTransform { X = -PaneWidth };
            Nav.IsPaneVisible = true;
        }

        var board = new Storyboard();
        AnimateDependent(board, AppTitleBar, "Height", 0, TitleBarHeight, ImmersiveMilliseconds);
        AnimateDependent(board, AppTitleBar, "Opacity", 0, 1, ImmersiveMilliseconds * 0.6);
        if (slide)
        {
            AnimateDependent(board, NavFreeze, "Width", width + PaneWidth, width, ImmersiveMilliseconds);
            AnimateDependent(board, (TranslateTransform)NavFreeze.RenderTransform, "X", -PaneWidth, 0, ImmersiveMilliseconds);
        }

        board.Completed += (_, _) =>
        {
            board.Stop();
            AppTitleBar.Height = TitleBarHeight;
            AppTitleBar.Opacity = 1;
            ResetNavSlide();
            NudgeDragRegions();
        };
        board.Begin();
    }

    /// <summary>冻结框归位（与 UnfreezeNav 同一套）：宽高清空、变换摘掉，回到常规布局。</summary>
    private void ResetNavSlide()
    {
        NavFreeze.Width = double.NaN;
        NavFreeze.HorizontalAlignment = HorizontalAlignment.Stretch;
        NavFreeze.RenderTransform = null;
    }

    /// <summary>挂一段补间：Width/Height 这类布局属性必须开 EnableDependentAnimation，否则动画不动。</summary>
    private static void AnimateDependent(Storyboard board, DependencyObject target, string property, double from, double to, double milliseconds)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        board.Children.Add(animation);
    }

    private void OnImmersiveCloseClick(object sender, RoutedEventArgs e) => Chrome.SetImmersive(false);

    /// <summary>沉浸态下 Esc 等同那颗关闭按钮；不在沉浸态就不管（加速器挂在这颗按钮上，收起时它也够不着）。</summary>
    private void OnImmersiveEscInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!Chrome.Immersive)
        {
            return;
        }

        args.Handled = true;
        Chrome.SetImmersive(false);
    }

    /// <summary>
    /// 顶栏命中区这本账**整个归我们记**：把顶栏上可点的那几块挖成 Passthrough，其余留给系统当拖动区。
    ///
    /// 为什么不交回系统 <c>TitleBar</c> 控件自管（XAML 里 <c>AutoRefreshDragRegions=False</c>）：
    /// 开着自管时它要在布局那一拍遍历自己那棵树去算矩形，而这条遍历在本工程这棵顶栏树上读空指针——
    /// <c>0xC0000005 读取位置 0x0</c>，栈顶与循环体整屏是同一组框架地址（<c>Microsoft.ui.xaml.dll</c> 偏移 …bbe614d 起）。
    /// 三轮 F5 排掉了"我们主动调 <c>RecomputeDragRegions()</c>"（删掉调用照崩）、
    /// 排掉了"调的时机"（延后一整拍 dispatcher 照崩，第八十六轮的时序假说作废）、
    /// 也排掉了模板里那颗延迟装载的 <c>PART_BackButton</c>（<see cref="TitleBarBackButtonEnabled"/> 设 false 让它永不进树，照崩）。
    /// 剩下能确定的是：**只要它去走那棵树就炸**，所以这条路整个不走。
    ///
    /// 代价：顶栏每加一个可点控件，下面就得补一块矩形——漏挖的表现是"看得见点不动，按下变拖窗"。
    ///
    /// 另记一笔已核过的账（头像让位）：控件模板第 11 列 RightPaddingColumn 运行时已按 AppWindow 的 inset
    /// 折算成 DIP 占好位，RightHeader 贴的那个"最右"本来就是按钮区左沿，所以 XAML 里那道 margin 一律不要。
    /// </summary>
    private void SetRegionsForCustomTitleBar()
    {
        // 构造期"起始站已导航"那一下也会走到这里，可那时窗口还没实化：XamlRoot 是 null、
        // RasterizationScale 也还是 0（实测直接 NullReferenceException）。等 AppTitleBar.Loaded
        // 与缩放合拍器再算就对了，这里整次跳过。
        double scale = RootLayout.XamlRoot?.RasterizationScale ?? 0;
        if (scale <= 0)
        {
            return;
        }

        var rects = new List<RectInt32>();
        if (AppTitleBar.Visibility == Visibility.Visible)
        {
            // 顶栏在场：中间那颗搜索框、右边那颗账号格要能点；返回键进场了也得挖（它在模板里，只能按名找）。
            AddPassthrough(rects, TitleBarSearchBox, scale);
            AddPassthrough(rects, AccountBadgeHost, scale);
            if (AppTitleBar.IsBackButtonVisible)
            {
                AddPassthrough(rects, FindTemplatePart("PART_BackButton"), scale);
            }
        }
        else
        {
            // 沉浸态顶栏整个收掉了，只剩左上角那颗关闭按钮还悬在系统拖动区里。不挖就是"看得见点不动"。
            AddPassthrough(rects, ImmersiveCloseButton, scale);
        }

        InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(
            NonClientRegionKind.Passthrough, rects.ToArray());
    }

    /// <summary>量一块矩形记进账本；还没排版出来（没进树、宽或高为 0）的跳过——硬量会得一个 0x0 的洞。</summary>
    private static void AddPassthrough(List<RectInt32> rects, FrameworkElement? element, double scale)
    {
        if (element is { IsLoaded: true } el && el.ActualWidth > 0 && el.ActualHeight > 0)
        {
            rects.Add(RectOf(el, scale));
        }
    }

    /// <summary>
    /// 按名找顶栏模板里的零件。<c>PART_BackButton</c> 这类是 <c>x:DeferLoadStrategy="Lazy"</c> 的模板件，
    /// 外面没有可引用的字段，只能等它进树之后从视觉树里找；没进场就交 null（那时它本来也不该点）。
    /// </summary>
    private FrameworkElement? FindTemplatePart(string name) => FindDescendant(AppTitleBar, name);

    private static FrameworkElement? FindDescendant(DependencyObject root, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: not null } fe && string.Equals(fe.Name, name, StringComparison.Ordinal))
            {
                return fe;
            }

            FrameworkElement? hit = FindDescendant(child, name);
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    /// <summary>这一发已经排下去了就别再排。</summary>
    private bool _dragRegionNudgePending;

    /// <summary>
    /// 过一拍 dispatcher 再记命中区的账，并且去重。
    /// 为什么要延后：沉浸态那一支要读 <c>XamlRoot.RasterizationScale</c> 与关闭按钮的
    /// <c>TransformToVisual</c>，这些在排版没落定时是 0/null（构造期实测直接 NullReferenceException）。
    /// **注意它不是拿来治崩溃的**：崩溃那条是 <c>RecomputeDragRegions()</c> 本身，
    /// 延后一整拍照崩，已经按"不再调用它"收（见 <see cref="SetRegionsForCustomTitleBar"/> 上方那段账）。
    /// </summary>
    private void NudgeDragRegions()
    {
        if (_dragRegionNudgePending)
        {
            return;
        }

        _dragRegionNudgePending = true;
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
        {
            _dragRegionNudgePending = false;
            SetRegionsForCustomTitleBar();
        }))
        {
            _dragRegionNudgePending = false;
        }
    }

    private static RectInt32 RectOf(FrameworkElement element, double scale)
    {
        Windows.Foundation.Rect bounds = element.TransformToVisual(null).TransformBounds(
            new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));

        return new RectInt32(
            (int)Math.Round(bounds.X * scale),
            (int)Math.Round(bounds.Y * scale),
            (int)Math.Round(bounds.Width * scale),
            (int)Math.Round(bounds.Height * scale));
    }
}

public sealed partial class NavItem : INotifyPropertyChanged
{
    private ImageSource? _avatar;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string TextKey { get; set; } = "";
    public string Text => Strings.Get(TextKey);
    public string Glyph { get; set; } = "";
    public string Tag { get; set; } = "";

    /// <summary>有头像时模板换成圆头像（页脚「账号」），没有就继续画 <see cref="Glyph"/>。</summary>
    public ImageSource? Avatar
    {
        get => _avatar;
        set
        {
            if (ReferenceEquals(_avatar, value))
            {
                return;
            }

            _avatar = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Avatar)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvatarVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlyphVisibility)));
        }
    }

    public Visibility AvatarVisibility => _avatar is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility GlyphVisibility => _avatar is null ? Visibility.Visible : Visibility.Collapsed;
}
