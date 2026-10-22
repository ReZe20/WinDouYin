using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinDouYin.Contracts;

namespace WinDouYin.ViewModels;

/// <summary>
/// 关注页左栏的一行。视频流里的作者和直播房主都摊成这一格：
/// 直播行多一圈红框 + 「直播中」，其余字段（头像/昵称/sec_uid）一样。
/// </summary>
public sealed class FollowUserRow
{
    public FollowUserRow(UserInfo user)
    {
        Uid = user.Uid;
        SecUid = user.SecUid;
        Nickname = user.Nickname.Length > 0 ? user.Nickname : "抖音用户";
        Avatar = user.Avatar.Length > 0 ? new BitmapImage(new Uri(user.Avatar)) : null;
        IsLive = false;
        StatusText = string.Empty;
    }

    public FollowUserRow(LiveCard live)
    {
        Uid = live.Uid;
        SecUid = live.SecUid;
        Nickname = live.Nickname;
        Avatar = live.Avatar.Length > 0 ? new BitmapImage(new Uri(live.Avatar)) : null;
        IsLive = true;
        StatusText = live.Title;
    }

    public string Uid { get; }

    public string SecUid { get; }

    public string Nickname { get; }

    public ImageSource? Avatar { get; }

    public string Initials => Nickname.Length > 0 ? Nickname[..1] : "抖";

    public bool IsLive { get; }

    public string StatusText { get; }

    public Visibility BadgeVisibility => IsLive ? Visibility.Visible : Visibility.Collapsed;

    public Visibility StatusVisibility => StatusText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RingVisibility => IsLive ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// 左栏的小节头：「正在直播(n)」「我的关注(n)」。和用户行一起平铺在 ListView 里，
/// 模板由 <see cref="WinDouYin.Controls.FollowItemTemplateSelector"/> 按类型选。
/// </summary>
public sealed class FollowSectionRow
{
    public FollowSectionRow(string header)
    {
        Header = header;
    }

    public string Header { get; }
}

/// <summary>
/// 「关注」页的账面：舞台吃 <c>follow/feed</c>（一页 5~6 条，首页 pull=0、续页 pull=2 + cursor），
/// 左栏吃 <c>user/following/list</c>（综合=source_type 4 / 最近=1 / 最早=3，续页用 min_time 当 max_time）。
/// 两路互不阻塞：一路失败另一路照常出。
///
/// 舞台条目复用推荐页的 <see cref="VideoItem"/>；直播条目只进左栏（应用没有直播播放，不摆一张点不开的卡）。
/// </summary>
public sealed partial class FollowingViewModel : INotifyPropertyChanged
{
    private const int ListPageSize = 20;
    private const int MaxVideos = 240;

    private readonly List<VideoItem> _videos = [];
    private readonly List<FollowUserRow> _allFollows = [];
    private readonly List<FollowUserRow> _allLives = [];
    private readonly HashSet<string> _videoIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _liveUids = new(StringComparer.Ordinal);
    private int _index = -1;
    private int _feedGeneration;
    private int _listGeneration;
    private long _cursor;
    private bool _feedMore = true;
    private bool _feedBusy;
    private bool _listBusy;
    private long _followNext;
    private bool _followMore = true;
    private int _followTotal;
    private int _sortSource = 4;
    private string _search = string.Empty;
    private bool _loaded;
    private bool _loggedInLastLoad;
    private VideoItem? _current;
    private VideoItem? _next;
    private string _statusText = string.Empty;
    private Visibility _statusVisibility = Visibility.Collapsed;
    private Visibility _loadingVisibility = Visibility.Collapsed;
    private Visibility _listLoadingVisibility = Visibility.Collapsed;
    private string _listStatus = string.Empty;
    private Visibility _listStatusVisibility = Visibility.Collapsed;

    public FollowingViewModel()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>左栏的行：小节头（<see cref="FollowSectionRow"/>）与用户行（<see cref="FollowUserRow"/>）混排。</summary>
    public ObservableCollection<object> ListItems { get; } = [];

    /// <summary>收起后的头像条：直播在前、普通关注在后，和官方 72px 窄条一致。</summary>
    public ObservableCollection<FollowUserRow> RailItems { get; } = [];

    public VideoItem? Current
    {
        get => _current;
        private set
        {
            if (ReferenceEquals(_current, value))
            {
                return;
            }

            _current = value;
            Raise(nameof(Current));
            UpdateNext();
            StatusVisibility = _statusText.Length > 0 && value is null ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public VideoItem? Next => _next;

    /// <summary>拖拽落位/取邻居用：当前下标与前后各一条。</summary>
    public int CurrentIndex => _index;

    public VideoItem? ItemAt(int direction)
    {
        int index = _index + direction;
        return index >= 0 && index < _videos.Count ? _videos[index] : null;
    }

    /// <summary>拖拽落位：画面已经滑到位，这里只把账面索引跟着挪过去。</summary>
    public void GoToIndex(int index)
    {
        if (index < 0 || index >= _videos.Count || index == _index)
        {
            return;
        }

        _index = index;
        Current = _videos[_index];
        StatusText = string.Empty;
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (string.Equals(_statusText, value, StringComparison.Ordinal))
            {
                return;
            }

            _statusText = value;
            Raise(nameof(StatusText));
            StatusVisibility = value.Length > 0 && _current is null ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public Visibility StatusVisibility
    {
        get => _statusVisibility;
        private set
        {
            if (_statusVisibility == value)
            {
                return;
            }

            _statusVisibility = value;
            Raise(nameof(StatusVisibility));
        }
    }

    public Visibility LoadingVisibility
    {
        get => _loadingVisibility;
        private set
        {
            if (_loadingVisibility == value)
            {
                return;
            }

            _loadingVisibility = value;
            Raise(nameof(LoadingVisibility));
        }
    }

    public Visibility ListLoadingVisibility
    {
        get => _listLoadingVisibility;
        private set
        {
            if (_listLoadingVisibility == value)
            {
                return;
            }

            _listLoadingVisibility = value;
            Raise(nameof(ListLoadingVisibility));
        }
    }

    public string ListStatus
    {
        get => _listStatus;
        private set
        {
            if (string.Equals(_listStatus, value, StringComparison.Ordinal))
            {
                return;
            }

            _listStatus = value;
            Raise(nameof(ListStatus));
            ListStatusVisibility = value.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public Visibility ListStatusVisibility
    {
        get => _listStatusVisibility;
        private set
        {
            if (_listStatusVisibility == value)
            {
                return;
            }

            _listStatusVisibility = value;
            Raise(nameof(ListStatusVisibility));
        }
    }

    public string SortLabel => _sortSource switch
    {
        1 => "最近关注",
        3 => "最早关注",
        _ => "综合排序",
    };

    public int SortSource => _sortSource;

    public string SearchText => _search;

    public bool HasMoreFollows => _followMore && !_listBusy;

    /// <summary>页面第一次进场：登录态变了就重来，否则沿用已有账面（页面是缓存页）。</summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        bool loggedIn = Douyin.Feed.IsLoggedIn;
        if (_loaded && loggedIn == _loggedInLastLoad)
        {
            return;
        }

        _loaded = true;
        _loggedInLastLoad = loggedIn;
        await RefreshAsync(ct);
    }

    /// <summary>切进这一页/登录态变化时整页重来：视频流回首页，左栏回第一页。</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        int feedGen = ++_feedGeneration;
        int listGen = ++_listGeneration;
        _videos.Clear();
        _videoIds.Clear();
        _allLives.Clear();
        _liveUids.Clear();
        _index = -1;
        _cursor = 0;
        _feedMore = true;
        Current = null;
        UpdateNext();
        StatusText = string.Empty;
        LoadingVisibility = Visibility.Visible;

        if (!Douyin.Feed.IsLoggedIn)
        {
            LoadingVisibility = Visibility.Collapsed;
            StatusText = "登录后才会出现关注内容";
            RebuildList();
            return;
        }

        try
        {
            // 先要视频流；左栏随后单独跑——两路共用发送闸，反正也是串行。
            FollowFeedPage page = await Douyin.Feed.FollowFeedAsync(0, 0, ct);
            if (feedGen != _feedGeneration)
            {
                return;
            }

            AppendFeed(page);
            if (_videos.Count > 0)
            {
                _index = 0;
                Current = _videos[0];
            }
            else
            {
                StatusText = page.Note.Length > 0 ? page.Note : "关注的人暂时没有新作品";
            }
        }
        catch (Exception ex)
        {
            if (feedGen == _feedGeneration)
            {
                StatusText = "关注流没取到：" + ex.Message;
            }
        }
        finally
        {
            if (feedGen == _feedGeneration)
            {
                LoadingVisibility = Visibility.Collapsed;
            }
        }

        await ReloadFollowsAsync(listGen, ct);
    }

    /// <summary>舞台往前一条：列表见底就续一页；续完还是见底就停在原处。</summary>
    public async Task MoveNextAsync(CancellationToken ct = default)
    {
        int start = _index;
        if (_index + 1 >= _videos.Count)
        {
            await LoadMoreFeedAsync(ct);
        }

        if (_feedGeneration == 0 || _index != start)
        {
            return;
        }

        if (_index + 1 < _videos.Count)
        {
            _index++;
            Current = _videos[_index];
            PrefetchIfNearEnd();
        }
        else
        {
            StatusText = "没有更多了";
        }
    }

    /// <summary>上一条只在已取到的列表里回退，不重取。</summary>
    public void MovePrevious()
    {
        if (_index > 0)
        {
            _index--;
            Current = _videos[_index];
            StatusText = string.Empty;
        }
    }

    /// <summary>搜索框：只过滤左栏（视频流不受影响），本地过滤已加载的关注行。</summary>
    public void SetSearch(string text)
    {
        string next = (text ?? string.Empty).Trim();
        if (string.Equals(_search, next, StringComparison.Ordinal))
        {
            return;
        }

        _search = next;
        RebuildList();
    }

    /// <summary>排序档变化：只重取左栏（官方三个档就是 source_type 4/1/3）。</summary>
    public async Task SetSortAsync(int sourceType, CancellationToken ct = default)
    {
        if (_sortSource == sourceType)
        {
            return;
        }

        _sortSource = sourceType;
        Raise(nameof(SortLabel));
        Raise(nameof(SortSource));
        int gen = ++_listGeneration;
        _allFollows.Clear();
        _followNext = 0;
        _followMore = true;
        RebuildList();
        await ReloadFollowsAsync(gen, ct);
    }

    /// <summary>左栏滚到底：按 min_time 续页。服务端 has_more 常是 0，所以只看 next 与条数。</summary>
    public async Task LoadMoreFollowsAsync(CancellationToken ct = default)
    {
        if (_listBusy || !_followMore || Douyin.Feed.IsLoggedIn == false)
        {
            return;
        }

        int gen = _listGeneration;
        _listBusy = true;
        ListLoadingVisibility = Visibility.Visible;
        try
        {
            UserInfoPage page = await Douyin.Feed.FollowingSidebarAsync(_followNext, _sortSource, ListPageSize, ct);
            if (gen != _listGeneration)
            {
                return;
            }

            int added = AppendFollows(page);
            _followNext = page.NextMaxTime;
            _followMore = added > 0 && page.NextMaxTime > 0;
            _followTotal = page.Total;
            RebuildList();
        }
        catch (Exception ex)
        {
            if (gen == _listGeneration)
            {
                ListStatus = "关注列表没取到：" + ex.Message;
            }
        }
        finally
        {
            if (gen == _listGeneration)
            {
                _listBusy = false;
                ListLoadingVisibility = Visibility.Collapsed;
            }
        }
    }

    private async Task ReloadFollowsAsync(int generation, CancellationToken ct)
    {
        _allFollows.Clear();
        _followNext = 0;
        _followMore = true;
        RebuildList();
        if (!Douyin.Feed.IsLoggedIn)
        {
            return;
        }

        _listBusy = true;
        ListLoadingVisibility = Visibility.Visible;
        ListStatus = string.Empty;
        try
        {
            UserInfoPage page = await Douyin.Feed.FollowingSidebarAsync(0, _sortSource, ListPageSize, ct);
            if (generation != _listGeneration)
            {
                return;
            }

            int added = AppendFollows(page);
            _followNext = page.NextMaxTime;
            _followMore = added > 0 && page.NextMaxTime > 0;
            _followTotal = page.Total;
            RebuildList();
        }
        catch (Exception ex)
        {
            if (generation == _listGeneration)
            {
                ListStatus = "关注列表没取到：" + ex.Message;
            }
        }
        finally
        {
            if (generation == _listGeneration)
            {
                _listBusy = false;
                ListLoadingVisibility = Visibility.Collapsed;
            }
        }
    }

    private int AppendFollows(UserInfoPage page)
    {
        int added = 0;
        foreach (UserInfo user in page.Items)
        {
            if (user.Uid.Length == 0 || _allFollows.Any(r => r.Uid == user.Uid))
            {
                continue;
            }

            _allFollows.Add(new FollowUserRow(user));
            added++;
        }

        return added;
    }

    private void AppendFeed(FollowFeedPage page)
    {
        foreach (VideoCard card in page.Videos)
        {
            if (card.Id.Length == 0 || !_videoIds.Add(card.Id) || _videos.Count >= MaxVideos)
            {
                continue;
            }

            _videos.Add(ItemOf(card));
        }

        foreach (LiveCard live in page.Lives)
        {
            if (live.Uid.Length == 0 || !_liveUids.Add(live.Uid))
            {
                continue;
            }

            _allLives.Add(new FollowUserRow(live));
        }

        _cursor = page.NextCursor;
        _feedMore = page.HasMore && _videos.Count < MaxVideos;
        UpdateNext();
        RebuildList();
    }

    private async Task LoadMoreFeedAsync(CancellationToken ct)
    {
        if (_feedBusy || !_feedMore)
        {
            return;
        }

        int gen = _feedGeneration;
        _feedBusy = true;
        try
        {
            FollowFeedPage page = await Douyin.Feed.FollowFeedAsync(_cursor, 2, ct);
            if (gen != _feedGeneration)
            {
                return;
            }

            int before = _videos.Count;
            AppendFeed(page);
            if (_videos.Count == before && page.Lives.Count == 0)
            {
                _feedMore = false;
            }
        }
        catch (Exception ex)
        {
            MfLog.Write("app: 关注流续页抛了 " + ex.GetType().Name + " · " + ex.Message);
            StatusText = "关注流续页没取到";
        }
        finally
        {
            if (gen == _feedGeneration)
            {
                _feedBusy = false;
            }
        }
    }

    private void PrefetchIfNearEnd()
    {
        if (_feedMore && !_feedBusy && _videos.Count - _index <= 2)
        {
            _ = LoadMoreFeedAsync(CancellationToken.None);
        }
    }

    private void UpdateNext()
    {
        VideoItem? next = _index >= 0 && _index + 1 < _videos.Count ? _videos[_index + 1] : null;
        if (ReferenceEquals(_next, next))
        {
            return;
        }

        _next = next;
        Raise(nameof(Next));
    }

    private void RebuildList()
    {
        string query = _search;
        List<FollowUserRow> lives = query.Length == 0
            ? [.. _allLives]
            : [.. _allLives.Where(r => r.Nickname.Contains(query, StringComparison.OrdinalIgnoreCase))];
        List<FollowUserRow> follows = query.Length == 0
            ? [.. _allFollows]
            : [.. _allFollows.Where(r => r.Nickname.Contains(query, StringComparison.OrdinalIgnoreCase))];

        ListItems.Clear();
        RailItems.Clear();
        if (lives.Count > 0)
        {
            ListItems.Add(new FollowSectionRow("正在直播(" + lives.Count + ")"));
            foreach (FollowUserRow live in lives)
            {
                ListItems.Add(live);
                RailItems.Add(live);
            }
        }

        string followHeader = query.Length == 0 && _followTotal > 0
            ? "我的关注(" + Math.Max(_followTotal, _allFollows.Count) + ")"
            : "我的关注(" + follows.Count + ")";
        ListItems.Add(new FollowSectionRow(followHeader));
        foreach (FollowUserRow row in follows)
        {
            ListItems.Add(row);
            RailItems.Add(row);
        }

        ListStatus = _allFollows.Count == 0 && _allLives.Count == 0
            ? (Douyin.Feed.IsLoggedIn ? "还没有关注任何人" : "登录后才会出现关注列表")
            : query.Length > 0 && follows.Count == 0 && lives.Count == 0 ? "没有匹配的用户"
            : string.Empty;
    }

    private static VideoItem ItemOf(VideoCard card) => new(
        card.Id, card.Desc, card.Author, card.Play, card.Cover, card.CreateTime,
        card.SecUid, card.AuthorId, card.Avatar, card.Digg, card.Comment, card.Collect, card.Share,
        card.Width, card.Height, card.Qualities, card.Music, card.FriendRecommender);

    private void Raise([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
