using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinDouYin.Contracts;

namespace WinDouYin.ViewModels;

/// <summary>精选页一个分类（页面实例的 17 项，顺序照 2026-10-21 官方 DOM 的页签条）。</summary>
public sealed record FeaturedCategory(string Label, string Tag);

/// <summary>
/// 一张精选卡。底层是 feed 那条 <see cref="VideoCard"/>，这里只补"视图要的两副面孔"：
/// 大卡（标题/作者压在封面上）与小卡（信息在封面下）。同一份模板切换，避免两套封面重复下载。
/// </summary>
public sealed class FeaturedRow : INotifyPropertyChanged
{
    private bool _isHero;
    private bool _hovered;
    private bool _menuOpen;

    public FeaturedRow(FeaturedCard item)
    {
        Item = item;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FeaturedCard Item { get; }

    public VideoCard Card => Item.Card;

    public string Id => Card.Id;

    public string Play => Card.Play;

    public string Cover => Card.Cover;

    public string Title => Card.Desc;

    public string LikeText => CountText.FormatCn(Card.Digg);

    public string AuthorLine => "@" + Card.Author + " · " + RelativeTime.Format(Card.CreateTime);

    public string DurationText => Clock(Card.DurationMs);

    public Visibility FollowVisibility => Item.Followed ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>首卡大卡态：由页面按内容宽切换（官方 ≥1536 视口才起）。</summary>
    public bool IsHero
    {
        get => _isHero;
        set
        {
            if (_isHero == value)
            {
                return;
            }

            _isHero = value;
            Raise(nameof(HeroVisibility));
            Raise(nameof(SmallVisibility));
            Raise(nameof(MoreVisibility));
        }
    }

    /// <summary>指针停在卡上：官方那枚 ⋮ 才浮出来。</summary>
    public bool Hovered
    {
        get => _hovered;
        set
        {
            if (_hovered == value)
            {
                return;
            }

            _hovered = value;
            Raise(nameof(MoreVisibility));
        }
    }

    /// <summary>⋮ 的菜单开着：这期间指针离开卡片也不能把按钮收掉（收掉锚点，菜单会跟着关）。</summary>
    public bool MenuOpen
    {
        get => _menuOpen;
        set
        {
            if (_menuOpen == value)
            {
                return;
            }

            _menuOpen = value;
            Raise(nameof(MoreVisibility));
        }
    }

    public Visibility HeroVisibility => IsHero ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SmallVisibility => IsHero ? Visibility.Collapsed : Visibility.Visible;

    public Visibility MoreVisibility => (Hovered || MenuOpen) && !IsHero ? Visibility.Visible : Visibility.Collapsed;

    private void Raise([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string Clock(int milliseconds)
    {
        if (milliseconds <= 0)
        {
            return string.Empty;
        }

        int whole = milliseconds / 1000;
        int hours = whole / 3600;
        int minutes = whole % 3600 / 60;
        int seconds = whole % 60;
        return hours > 0
            ? $"{hours}:{minutes:00}:{seconds:00}"
            : $"{minutes:00}:{seconds:00}";
    }
}

/// <summary>
/// 精选页账面：分页取数（module/feed，一页 20）、分类切换、大卡态，以及**按分类的进程内缓存**。
///
/// 缓存的重要性在切页签：页面实例被 Frame 缓存着，但官方每次切分类都从 refresh_index=1 重发一发
/// （3MB 上下、还要排 900ms 的取数节拍），来回点几下就白等十几秒。这里每个 tag 记住
/// 已经取到的那一段（Rows / 下一页序号 / has_more / 空态提示），切回去直接铺，不再重发。
///
/// 账面做成进程内单例而不是页面实例的字段：启动/登录/切号之后要先在后台把「全部」第一页拿回来，
/// 那时页面往往还没被创建。缓存只在内存里，按账号重置（<see cref="ResetForAccount"/>），
/// 不落盘、不跨号复用。
/// </summary>
public sealed class FeaturedViewModel : INotifyPropertyChanged
{
    public const int PageSize = 20;
    public const int MaxItems = 240;

    /// <summary>17 枚页签的官方顺序与 tag_id（全部是空串，不是 0）。</summary>
    public static readonly IReadOnlyList<FeaturedCategory> CategoryTable =
    [
        new("全部", ""),
        new("公开课", "100000"),
        new("游戏", "300205"),
        new("二次元", "300206"),
        new("音乐", "300209"),
        new("影视", "300215"),
        new("美食", "300204"),
        new("知识", "300213"),
        new("小剧场", "300214"),
        new("生活vlog", "300216"),
        new("体育", "300207"),
        new("旅行", "300221"),
        new("亲子", "300217"),
        new("动物", "300220"),
        new("三农", "300219"),
        new("汽车", "300218"),
        new("美妆穿搭", "300222"),
    ];

    /// <summary>页面、启动预取、账号切换共用这一本账。</summary>
    public static FeaturedViewModel Shared { get; } = new();

    /// <summary>一个分类已经取到的那一段。Rows 只在第一页成功或续页追加时动，失败时留着旧内容。</summary>
    private sealed class CategoryState
    {
        public CategoryState(string tag) => Tag = tag;

        public string Tag { get; }

        public List<FeaturedRow> Rows { get; } = [];

        public HashSet<string> Ids { get; } = new(StringComparer.Ordinal);

        /// <summary>最后成功取到的 refresh_index；下一页从它 +1。0 表示还没取到过。</summary>
        public int RefreshIndex { get; set; }

        public bool HasMore { get; set; } = true;

        /// <summary>第一页已有结论（成功，含"这一类没内容"），切回来不再重发。</summary>
        public bool Completed { get; set; }

        public bool MoreLoading { get; set; }

        public Task? FirstTask { get; set; }

        public Task? MoreTask { get; set; }

        /// <summary>有几个 SelectAsync 正等着这一页（用来避免 LoadFirst 和 SelectAsync 各铺一遍）。</summary>
        public int SelectWaiters { get; set; }

        /// <summary>续页失败后的冷却点：触底事件会反复来，别让同一发失败页每 900ms 重打一次。</summary>
        public DateTimeOffset RetryAfter { get; set; }

        /// <summary>连续跳过的空页数（整页只有直播卡/无播放地址），最多跳两页。</summary>
        public int EmptySkip { get; set; }

        /// <summary>这一发是空页，外层 LoadMore 收尾后自动再要一页。</summary>
        public bool SkipNext { get; set; }

        public string Note { get; set; } = string.Empty;
    }

    private readonly ObservableCollection<FeaturedRow> _items = [];
    private readonly Dictionary<string, CategoryState> _states = new(StringComparer.Ordinal);
    private readonly DispatcherQueue? _ui = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource _accountCts = new();
    private CategoryState? _shown;
    private int _epoch;
    private int _switchVersion;
    private string _accountUid = string.Empty;
    private bool _heroMode;
    private bool _prefetching;
    private bool _pageVisible;
    private FeaturedCategory? _prefetchQueued;
    private string _statusText = string.Empty;
    private Visibility _statusVisibility = Visibility.Collapsed;
    private Visibility _busyVisibility = Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>铺完一页（含从缓存切回来）之后叫一声：页面借此把大卡播放器挂到新的首卡上。</summary>
    public event EventHandler? ItemsChanged;

    public ObservableCollection<FeaturedRow> Items => _items;

    public IReadOnlyList<FeaturedCategory> Categories => CategoryTable;

    public FeaturedCategory? Current { get; private set; }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value, nameof(StatusText));
    }

    public Visibility StatusVisibility
    {
        get => _statusVisibility;
        private set => Set(ref _statusVisibility, value, nameof(StatusVisibility));
    }

    public Visibility BusyVisibility
    {
        get => _busyVisibility;
        private set => Set(ref _busyVisibility, value, nameof(BusyVisibility));
    }

    public bool HasHero => _heroMode && _items.Count > 0;

    /// <summary>页面按内容宽设定的模式（空账面时也要能读，用来避免重复设值）。</summary>
    public bool HeroMode => _heroMode;

    /// <summary>
    /// 当前分类的内容还没落到界面上（首屏在别处取回、页面重新进来、或上一次切页被取消）：
    /// 页面 Loaded 时据此补一次 SelectAsync。已经在屏幕上的缓存不重建。
    /// </summary>
    public bool CurrentNeedsSelect
    {
        get
        {
            if (Current is null || !_states.TryGetValue(Current.Tag, out CategoryState? state))
            {
                return false;
            }

            if (!ReferenceEquals(_shown, state))
            {
                return true;
            }

            // 屏幕上就是这一页，但第一页是失败的（零条、没完成、也没在取）：重新进页时补一次
            return !state.Completed && state.Rows.Count == 0 && state.FirstTask is null;
        }
    }

    public bool CanLoadMore
    {
        get
        {
            if (Current is null || !_states.TryGetValue(Current.Tag, out CategoryState? state))
            {
                return false;
            }

            return state.Completed && state.HasMore
                && state.FirstTask is null && state.MoreTask is null
                && state.RetryAfter <= DateTimeOffset.UtcNow
                && state.Rows.Count < MaxItems;
        }
    }

    /// <summary>
    /// 切分类：有缓存就直接铺（同步返回，不碰网络）；第一次来才取第一页。
    /// 已经在取的那一发（启动预取）直接等它，不重复发。
    /// </summary>
    public async Task SelectAsync(FeaturedCategory category)
    {
        int version = ++_switchVersion;
        Current = category;
        CategoryState state = StateOf(category.Tag);

        if (state.Completed || state.Rows.Count > 0)
        {
            Show(state);
            return;
        }

        _shown = null;
        _items.Clear();
        StatusText = string.Empty;
        StatusVisibility = Visibility.Collapsed;
        BusyVisibility = Visibility.Visible;
        ItemsChanged?.Invoke(this, EventArgs.Empty);

        Task? task;
        try
        {
            task = EnsureFirstAsync(state);
        }
        catch (Exception ex)
        {
            // 建请/派生请求时的同步异常也别让它冒成 async void 未处理异常
            state.Note = "精选没取到：" + ex.Message;
            task = null;
        }

        if (task is not null)
        {
            state.SelectWaiters++;
            try
            {
                await task;
            }
            catch (Exception ex)
            {
                // LoadFirstAsync 自己会记，这里只兜底；别让 async void 的调用方炸掉
                if (version == _switchVersion && state.Note.Length == 0)
                {
                    state.Note = "精选没取到：" + ex.Message;
                }
            }
            finally
            {
                state.SelectWaiters--;
            }
        }

        // LoadFirstAsync 自己可能在完成时已经铺过（预取替当前分类取回首页的情形），
        // 这里只在画面上还是空的时候补一发，避免同一批卡片清空又重建。
        if (version == _switchVersion && _items.Count == 0)
        {
            Show(state);
        }
    }

    /// <summary>滚到底：取下一页。同一时间只放一发；切走了也照常写进缓存，只是不碰当前界面。</summary>
    public async Task LoadMoreAsync()
    {
        if (Current is null)
        {
            return;
        }

        CategoryState state = StateOf(Current.Tag);
        if (!CanLoadMore)
        {
            return;
        }

        int epoch = _epoch;
        int index = state.RefreshIndex + 1;
        state.MoreLoading = true;
        BusyVisibility = Visibility.Visible;
        Task task = LoadMoreCoreAsync(state, index, epoch);
        state.MoreTask = task;
        try
        {
            await task;
        }
        finally
        {
            if (epoch == _epoch)
            {
                state.MoreLoading = false;
                state.MoreTask = null;
                bool skip = state.SkipNext;
                state.SkipNext = false;
                bool continueSkip = skip && _pageVisible && IsActive(state)
                    && state.HasMore && state.EmptySkip <= 2;
                if (skip && !continueSkip)
                {
                    state.Note = string.Empty;   // 页面走了/切了，别再挂着"正在跳过空页…"
                    if (IsActive(state))
                    {
                        StatusText = string.Empty;
                        StatusVisibility = Visibility.Collapsed;
                    }
                }

                if (IsActive(state) && !continueSkip)
                {
                    BusyVisibility = Visibility.Collapsed;
                }

                if (continueSkip)
                {
                    _ = LoadMoreAsync();
                }
            }
        }
    }

    /// <summary>页面按内容宽调：宽了起大卡，窄了退回统一网格。</summary>
    public void SetHeroMode(bool hero)
    {
        if (_heroMode == hero)
        {
            return;
        }

        _heroMode = hero;
        if (Current is not null && _states.TryGetValue(Current.Tag, out CategoryState? state))
        {
            ApplyHero(state);
        }
    }

    /// <summary>页面进了/离开了视觉树：只用来决定空页自动跳转要不要接着跑，预取不受影响。</summary>
    public void SetPageVisible(bool visible) => _pageVisible = visible;

    /// <summary>
    /// 账号换了：整本账作废。在飞的请求靠 epoch 丢掉结果，晚到的预取也不会把上一个号的内容铺回来。
    /// uid 没变就一个字都不动（页面加载与账号回调会各喊一次）。
    /// </summary>
    public void ResetForAccount(string uid)
    {
        if (string.Equals(_accountUid, uid, StringComparison.Ordinal))
        {
            return;
        }

        _accountUid = uid;
        _epoch++;
        _switchVersion++;

        // 旧号那发 3MB 还在飞就把闸门和 HTTP 连接一起收掉，别让新号的第一页排在它后面。
        _accountCts.Cancel();
        _accountCts = new CancellationTokenSource();

        _states.Clear();
        _prefetching = false;
        _prefetchQueued = null;
        Current = null;
        _shown = null;
        _items.Clear();
        StatusText = string.Empty;
        StatusVisibility = Visibility.Collapsed;
        BusyVisibility = Visibility.Collapsed;
        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>延迟预取（启动、登录、切号后各排一次）。到点时登录态/账号已经换了就不做。</summary>
    public void PrefetchAfter(FeaturedCategory category, TimeSpan delay)
    {
        if (_ui is not null && !_ui.HasThreadAccess)
        {
            _ui.TryEnqueue(() => PrefetchAfter(category, delay));
            return;
        }

        // 启动预取发生在页面创建之前：先把账本认到当前号上，否则页面第一次 Loaded
        // 会拿 A 去 Reset 一本 _accountUid 还是空串的账，把刚预取好的首页又清掉。
        AdoptCurrentAccount();
        int epoch = _epoch;
        _ = PrefetchAfterCoreAsync(category, delay, epoch);
    }

    /// <summary>
    /// 后台取某分类的第一页。只写缓存，不动当前列表；同一时间只放一发，后来的排一个"最新"的队，
    /// 免得鼠标扫过一排页签就压出十七发 3MB 的请求。
    /// </summary>
    public void Prefetch(FeaturedCategory category)
    {
        if (_ui is null)
        {
            return;
        }

        if (!_ui.HasThreadAccess)
        {
            _ui.TryEnqueue(() => Prefetch(category));
            return;
        }

        if (!Douyin.Feed.IsLoggedIn)
        {
            return;
        }

        CategoryState state = StateOf(category.Tag);
        if (state.Completed || state.FirstTask is not null)
        {
            return;
        }

        if (_prefetching)
        {
            _prefetchQueued = category;
            return;
        }

        _ = PrefetchCoreAsync(state, _epoch);
    }

    private async Task PrefetchAfterCoreAsync(FeaturedCategory category, TimeSpan delay, int epoch)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }
        }
        catch
        {
            return;
        }

        if (_ui is null)
        {
            return;
        }

        // epoch 只在 UI 线程上读写：先回去，再判号有没有换、要不要预取
        if (!_ui.HasThreadAccess)
        {
            _ui.TryEnqueue(() =>
            {
                if (epoch == _epoch)
                {
                    Prefetch(category);
                }
            });
            return;
        }

        if (epoch == _epoch)
        {
            Prefetch(category);
        }
    }

    private async Task PrefetchCoreAsync(CategoryState state, int epoch)
    {
        _prefetching = true;
        try
        {
            Task? task = EnsureFirstAsync(state);
            if (task is not null)
            {
                await task;
            }
        }
        catch (Exception ex)
        {
            // 预取失败不打扰页面；切过去时还会按需再取
            MfLog.Write("app: 精选预取失败 " + ex.GetType().Name + " · " + ex.Message);
        }
        finally
        {
            // 换号时 ResetForAccount 已经把这两个标记清掉了；旧任务晚到不能再动新账本的闸
            if (epoch == _epoch)
            {
                _prefetching = false;
                FeaturedCategory? next = _prefetchQueued;
                _prefetchQueued = null;
                if (next is not null && !string.Equals(next.Tag, state.Tag, StringComparison.Ordinal))
                {
                    Prefetch(next);
                }
            }
        }
    }

    /// <summary>把账本认到当前账号（预取可能先于页面发生）。uid 没变时 ResetForAccount 是空操作。</summary>
    private void AdoptCurrentAccount()
    {
        string uid = Douyin.Feed.MyUid.Length > 0 ? Douyin.Feed.MyUid : Douyin.Feed.CurrentAccountUid;
        ResetForAccount(uid);
    }

    private CategoryState StateOf(string tag)
    {
        if (!_states.TryGetValue(tag, out CategoryState? state))
        {
            state = new CategoryState(tag);
            _states[tag] = state;
        }

        return state;
    }

    private Task? EnsureFirstAsync(CategoryState state)
    {
        if (state.Completed)
        {
            return null;
        }

        if (state.FirstTask is not null)
        {
            return state.FirstTask;
        }

        if (!Douyin.Feed.IsLoggedIn)
        {
            state.Note = "登录后才会出现精选内容";
            return null;
        }

        state.FirstTask = LoadFirstAsync(state);
        return state.FirstTask;
    }

    private async Task LoadFirstAsync(CategoryState state)
    {
        int epoch = _epoch;
        CancellationToken ct = _accountCts.Token;
        try
        {
            FeaturedFeedPage page = await Douyin.Feed.FeaturedFeedAsync(state.Tag, 1, ct);
            if (epoch != _epoch)
            {
                return;   // 已经切号
            }

            state.Rows.Clear();
            state.Ids.Clear();
            state.RetryAfter = default;
            state.EmptySkip = 0;
            state.SkipNext = false;
            if (page.StatusCode != 0)
            {
                // 被挡/空包：整页不记账，下次切回来还会重试。别把半页当"已完成"——
                // 那样 CanLoadMore 永远为 false，只能看到残缺的第一页。
                state.RefreshIndex = 0;
                state.HasMore = true;
                state.Completed = false;
                state.Note = page.Note.Length > 0 ? page.Note : "精选没取到";
            }
            else
            {
                state.HasMore = true;   // 上一次失败可能把 has_more 写成 false，重试成功要以新回包为准
                foreach (FeaturedCard item in page.Items)
                {
                    if (state.Rows.Count >= MaxItems)
                    {
                        state.HasMore = false;
                        break;
                    }

                    AddRow(state, item);
                }

                state.RefreshIndex = 1;
                state.HasMore &= page.HasMore;
                state.Completed = true;
                state.Note = state.Rows.Count == 0
                    ? (page.Note.Length > 0 ? page.Note : "这一类暂时没有内容")
                    : !state.HasMore ? "没有更多了" : string.Empty;
            }
        }
        catch (Exception ex)
        {
            if (epoch == _epoch)
            {
                state.Note = "精选没取到：" + ex.Message;
            }
        }
        finally
        {
            if (epoch == _epoch)
            {
                state.FirstTask = null;

                // 预取（启动/悬停）可能正好替当前分类取回了首页，而页面没有 SelectAsync 在等它：
                // 这一下自己铺，否则界面会一直停在空态/错误提示上。有人在等就交给它铺，避免铺两遍。
                if (IsActive(state) && state.SelectWaiters == 0 && _items.Count == 0)
                {
                    Show(state);
                }
            }
        }
    }

    private async Task LoadMoreCoreAsync(CategoryState state, int index, int epoch)
    {
        CancellationToken ct = _accountCts.Token;
        try
        {
            FeaturedFeedPage page = await Douyin.Feed.FeaturedFeedAsync(state.Tag, index, ct);
            if (epoch != _epoch)
            {
                return;
            }

            if (page.StatusCode != 0)
            {
                // 这一发没成：序号不推进、has_more 也不改，下一发重试的仍是同一页；
                // 冷却五秒，别让触底事件把失败页每 900ms 重打一次。
                state.RetryAfter = DateTimeOffset.UtcNow.AddSeconds(5);
                state.Note = page.Note.Length > 0 ? page.Note : "精选没取到";
                if (IsActive(state))
                {
                    StatusText = state.Note;
                    StatusVisibility = Visibility.Visible;
                }

                return;
            }

            state.RetryAfter = default;
            int added = 0;
            foreach (FeaturedCard item in page.Items)
            {
                if (state.Rows.Count >= MaxItems)
                {
                    state.HasMore = false;
                    break;
                }

                int before = state.Rows.Count;
                AddRow(state, item);
                if (state.Rows.Count > before)
                {
                    added++;
                    if (IsActive(state))
                    {
                        _items.Add(state.Rows[^1]);
                    }
                }
            }

            state.RefreshIndex = index;
            state.HasMore &= page.HasMore;
            if (state.Rows.Count >= MaxItems)
            {
                state.HasMore = false;
            }

            if (added == 0 && page.Items.Count == 0 && state.HasMore && state.EmptySkip < 2)
            {
                // 整页都是直播卡/无播放地址被滤掉了：序号已经推进，收尾后再要下一页，最多跳两页
                state.EmptySkip++;
                state.SkipNext = true;
                state.Note = "正在跳过空页…";
            }
            else if (added == 0)
            {
                // 回包是空的或全是重复条目：别再让滚轮反复触底重发
                state.HasMore = false;
                state.Note = "没有更多了";
            }
            else
            {
                state.EmptySkip = 0;
                state.Note = state.HasMore
                    ? string.Empty
                    : state.Rows.Count >= MaxItems ? "先看到这里（" + MaxItems + " 条）" : "没有更多了";
            }

            if (IsActive(state))
            {
                ApplyHero(state);
                StatusText = state.Note;
                StatusVisibility = state.Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            if (epoch == _epoch)
            {
                state.RetryAfter = DateTimeOffset.UtcNow.AddSeconds(5);
                state.Note = "精选没取到：" + ex.Message;
                if (IsActive(state))
                {
                    StatusText = state.Note;
                    StatusVisibility = Visibility.Visible;
                }
            }
        }
    }

    private static void AddRow(CategoryState state, FeaturedCard item)
    {
        if (item.Card.Id.Length == 0 || !state.Ids.Add(item.Card.Id))
        {
            return;
        }

        state.Rows.Add(new FeaturedRow(item));
    }

    private void Show(CategoryState state)
    {
        _shown = state;
        _items.Clear();
        foreach (FeaturedRow row in state.Rows)
        {
            // 缓存里的行可能停在悬停态：换回来时指针早就不在卡上了
            row.Hovered = false;
            row.MenuOpen = false;
            _items.Add(row);
        }

        ApplyHero(state);
        StatusText = state.Note;
        StatusVisibility = state.Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BusyVisibility = state.FirstTask is not null || state.MoreLoading
            ? Visibility.Visible
            : Visibility.Collapsed;
        ItemsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyHero(CategoryState state)
    {
        for (int i = 0; i < state.Rows.Count; i++)
        {
            state.Rows[i].IsHero = _heroMode && i == 0;
        }
    }

    private bool IsActive(CategoryState state) =>
        Current is not null && string.Equals(Current.Tag, state.Tag, StringComparison.Ordinal);

    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
