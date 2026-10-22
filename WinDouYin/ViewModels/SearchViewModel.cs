using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinDouYin.Contracts;

namespace WinDouYin.ViewModels;

/// <summary>
/// 搜索结果里的一格，规格照抄网页版搜索卡实测（见 Views/SearchPage.xaml 注释）：
/// 封面 4:3 + 左下心形点赞 + 右下时长黑标，信息区 12 内边距 + 标题两行 + 「@作者 · 日期」一行。
/// </summary>
public sealed class SearchResultRow
{
    /// <summary>卡片在 220 宽的格子里画，按 1.8 倍封顶解码，别把整张大封面解进内存。</summary>
    private const int CoverDecodeWidth = 360;

    /// <summary>单列行的头像画 42，按 2 倍封顶解码。</summary>
    private const int AvatarDecodeWidth = 84;

    public SearchResultRow(VideoCard card)
    {
        Card = card;
        Id = card.Id;
        Desc = card.Desc.Length > 0 ? card.Desc : "无标题";
        Author = card.Author.Length > 0 ? card.Author : "抖音用户";
        LikeText = CountText.Format(card.Digg);
        bool pic = string.Equals(card.Kind, "pic", StringComparison.Ordinal);
        ImageBadgeVisibility = pic ? Visibility.Visible : Visibility.Collapsed;
        DurationText = pic ? string.Empty : FormatDuration(card.Stats);
        DurationVisibility = DurationText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DateText = FormatDate(card.CreateTime);
        DateVisibility = DateText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Cover = CoverUrl(card) is { Length: > 0 } cover ? Decode(cover, CoverDecodeWidth) : null;
        Avatar = card.Avatar.Length > 0 ? Decode(card.Avatar, AvatarDecodeWidth) : null;
        Initials = Name[..1];
    }

    public VideoCard Card { get; }

    public string Id { get; }

    public string Desc { get; }

    public string Author { get; }

    /// <summary>单列行的作者名，就是昵称本身（官方那行不带 @）。</summary>
    public string Name => Author;

    public string LikeText { get; }

    public string DurationText { get; }

    public string DateText { get; }

    public Visibility ImageBadgeVisibility { get; }

    public Visibility DurationVisibility { get; }

    public Visibility DateVisibility { get; }

    public ImageSource? Cover { get; }

    /// <summary>单列行的作者头像（官方 42 圆）。</summary>
    public ImageSource? Avatar { get; }

    public string Initials { get; }

    /// <summary>头像解出来了就不必再垫首字。</summary>
    public Visibility InitialsVisibility => Avatar is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>封面：视频条目用 video.cover；图文帖没有封面时退回第一张图（去掉实况段）。</summary>
    private static string CoverUrl(VideoCard card)
    {
        if (card.Cover.Length > 0)
        {
            return card.Cover;
        }

        const string Scheme = "imagepost://";
        if (!card.Play.StartsWith(Scheme, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        string first = card.Play[Scheme.Length..].Split('|')[0];
        int live = first.IndexOf("~live~", StringComparison.Ordinal);
        return live > 0 ? first[..live] : first;
    }

    /// <summary>右下时长标：VideoCard.Stats 开头是 "127s"；官方一小时以内 m:ss、过一小时补到两位小时。</summary>
    private static string FormatDuration(string stats)
    {
        int cut = stats.IndexOf('s');
        if (cut <= 0 || !int.TryParse(stats[..cut], out int seconds) || seconds <= 0)
        {
            return string.Empty;
        }

        return seconds >= 3600
            ? $"{seconds / 3600:00}:{seconds % 3600 / 60:00}:{seconds % 60:00}"
            : $"{seconds / 60}:{seconds % 60:00}";
    }

    /// <summary>发布日期：当年只写「· 9月25日」，跨年补上年份——照官方搜索卡的写法。</summary>
    private static string FormatDate(long unixSeconds)
    {
        if (unixSeconds <= 0)
        {
            return string.Empty;
        }

        DateTimeOffset when = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;
        DateTimeOffset now = DateTimeOffset.Now;
        return when.Year == now.Year ? $"· {when.Month}月{when.Day}日" : $"· {when.Year}年{when.Month}月{when.Day}日";
    }

    private static BitmapImage Decode(string url, int decodeWidth)
    {
        var bitmap = new BitmapImage { DecodePixelWidth = decodeWidth };
        bitmap.UriSource = new Uri(url);
        return bitmap;
    }
}

/// <summary>顶部搜索框的一条建议：热搜词带名次与热度，手输词单独一条（名次空、右侧提示"搜索"）。</summary>
public sealed class SearchSuggestion
{
    private SearchSuggestion(string word, int rank, int hotValue, bool isQuery)
    {
        Word = word;
        RankText = rank > 0 ? rank.ToString() : string.Empty;
        HotText = isQuery ? "搜索" : hotValue > 0 ? CountText.Format(hotValue) + " 热度" : string.Empty;
        RankVisibility = rank > 0 ? Visibility.Visible : Visibility.Collapsed;
        QueryIconVisibility = isQuery ? Visibility.Visible : Visibility.Collapsed;
        // 没热度也得让整列收掉：列上写 MinWidth 的话，空热度那几行右边会白占 72px，词没到右沿就折行
        HotVisibility = HotText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public string Word { get; }

    public string RankText { get; }

    public string HotText { get; }

    public Visibility RankVisibility { get; }

    public Visibility QueryIconVisibility { get; }

    public Visibility HotVisibility { get; }

    public static SearchSuggestion ForQuery(string word) => new(word, 0, 0, isQuery: true);

    public static SearchSuggestion ForHot(string word, int rank, int hotValue) => new(word, rank, hotValue, isQuery: false);

    /// <summary>只有一颗词，没名次也没热度：搜索历史那几颗胶囊、以及从「猜你想搜」里命中的词。</summary>
    public static SearchSuggestion ForPlain(string word) => new(word, 0, 0, isQuery: false);
}

/// <summary>
/// 「猜你想搜」的一行：官方那一栏是两列铺开的短词，所以一行装两条。
/// 词少时右列留空（<see cref="RightVisibility"/> 收掉），不用可空对象——模板里 x:Bind 走空路径不值当冒险。
/// </summary>
public sealed class SearchGuessPair
{
    public SearchGuessPair(string left, string right)
    {
        Left = left;
        Right = right;
        RightVisibility = right.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public string Left { get; }

    public string Right { get; }

    public Visibility RightVisibility { get; }
}

/// <summary>
/// 「用户」页签的一行，照官方整宽行卡实测：头像 62 圆（1px 描边）+ 昵称 16/24 半粗，
/// 下面 12/20 一行「抖音号｜获赞｜粉丝」用竖线分隔，再下面 12/20 六成白简介。官方那颗「关注」是写操作，这里不放。
/// </summary>
public sealed class SearchUserRow
{
    /// <summary>头像画 62，按 2 倍封顶解码。</summary>
    private const int AvatarDecodeWidth = 128;

    public SearchUserRow(UserInfo user)
    {
        User = user;
        Name = user.Nickname.Length > 0 ? user.Nickname : "抖音用户";
        HandleText = user.Handle.Length > 0 ? "抖音号 " + user.Handle : string.Empty;
        FavoritedText = user.TotalFavorited > 0 ? CountText.Format(user.TotalFavorited) + "获赞" : string.Empty;
        FollowerText = user.FollowerCount > 0 ? CountText.Format(user.FollowerCount) + "粉丝" : string.Empty;
        Signature = user.Signature;
        Initials = Name[..1];
        Avatar = user.Avatar.Length > 0
            ? new BitmapImage(new Uri(user.Avatar)) { DecodePixelWidth = AvatarDecodeWidth }
            : null;
        HandleVisibility = Shown(HandleText);
        FavoritedVisibility = Shown(FavoritedText);
        FollowerVisibility = Shown(FollowerText);
        SignatureVisibility = Shown(Signature);
        AvatarVisibility = Avatar is null ? Visibility.Collapsed : Visibility.Visible;
        InitialsVisibility = Avatar is null ? Visibility.Visible : Visibility.Collapsed;
        HasStatsVisibility = HandleText.Length > 0 || FavoritedText.Length > 0 || FollowerText.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        // 分隔竖线只在"两边都有内容"时出现
        FirstDividerVisibility = HandleText.Length > 0 && (FavoritedText.Length > 0 || FollowerText.Length > 0)
            ? Visibility.Visible
            : Visibility.Collapsed;
        SecondDividerVisibility = FavoritedText.Length > 0 && FollowerText.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static Visibility Shown(string text) => text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public UserInfo User { get; }

    /// <summary>点这一行进作者主页要带的：主页接口只认 sec_user_id。</summary>
    public string SecUid => User.SecUid;

    public string Name { get; }

    public string HandleText { get; }

    public string FavoritedText { get; }

    public string FollowerText { get; }

    public string Signature { get; }

    public string Initials { get; }

    public ImageSource? Avatar { get; }

    public Visibility HasStatsVisibility { get; }

    public Visibility HandleVisibility { get; }

    public Visibility FavoritedVisibility { get; }

    public Visibility FollowerVisibility { get; }

    public Visibility SignatureVisibility { get; }

    public Visibility AvatarVisibility { get; }

    public Visibility InitialsVisibility { get; }

    public Visibility FirstDividerVisibility { get; }

    public Visibility SecondDividerVisibility { get; }
}

/// <summary>
/// 相关词那排 chip（官方第二行，来自回包 <c>guide_search_words[]</c>）：点一枚就把关键词改成「词 该词」重搜。
/// 外观整个交给系统 <c>ToggleButton</c>——这里只报"选没选"，不报颜色不报字重。
/// </summary>
public sealed partial class SearchChip : INotifyPropertyChanged
{
    private bool _selected;

    public SearchChip(string text, string word, bool selected)
    {
        Text = text;
        Word = word;
        _selected = selected;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Text { get; }

    /// <summary>点它要附加到关键词上的词；「全部」是空串（清掉附加词）。</summary>
    public string Word { get; }

    /// <summary>只由页面读（OneWay 喂 IsChecked）；翻转一律由账面发起。</summary>
    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value)
            {
                return;
            }

            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}

/// <summary>
/// 搜索结果页的数据面：页签（综合/视频/用户）+ 筛选（排序/发布时间）+ 相关词 + 多列单列，
/// 每个维度都重取第一页；滚到底按同一组条件续页，按 id 去重（搜索会在不同页回吐同一条）。
/// 签名与 JSON 解析都是同步 CPU 活，整发扔线程池，回 UI 再往 ObservableCollection 里加。
/// </summary>
public sealed partial class SearchViewModel : INotifyPropertyChanged
{
    private const int PageSize = 10;

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly List<string> _guideWords = [];
    private string _keyword = string.Empty;
    private string _refine = string.Empty;
    private string _searchId = string.Empty;
    private string _nilType = string.Empty;
    private SearchKind _kind = SearchKind.General;
    private int _sortType;
    private int _publishTime;
    private bool _multiColumn = true;
    private long _cursor;
    private int _gen;
    private int _emptyPages;
    private bool _loading;
    private bool _hasMore;
    private string _status = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SearchResultRow> Results { get; } = [];

    public ObservableCollection<SearchUserRow> UserResults { get; } = [];

    /// <summary>「全部」+ 相关词；整排重建，所以每项的选中态直接算，不背 INPC。</summary>
    public ObservableCollection<SearchChip> Chips { get; } = [];

    public SearchKind Kind => _kind;

    public bool MultiColumn => _multiColumn;

    /// <summary>给视图那颗 ToggleSplitButton 的 IsChecked 用（单列=选中）。</summary>
    public bool SingleColumn => !_multiColumn;

    public string ColumnModeText => _multiColumn ? "多列" : "单列";

    /// <summary>有没有生效的筛选；有的话按钮上那颗系统 InfoBadge 点亮，ToolTip 写清是哪几项。</summary>
    public bool HasFilter => _sortType > 0 || _publishTime > 0;

    public Visibility FilterBadgeVisibility => HasFilter ? Visibility.Visible : Visibility.Collapsed;

    public string FilterSummaryText => _sortType > 0 || _publishTime > 0
        ? $"筛选：{SortLabelText} · {TimeLabelText}"
        : "不筛选";

    public string SortLabelText => _sortType switch { 1 => "最多点赞", 2 => "最新发布", _ => "综合排序" };

    public string TimeLabelText => _publishTime switch { 1 => "一天内", 7 => "一周内", 180 => "半年内", _ => "不限" };

    public int SortType => _sortType;

    public int PublishTime => _publishTime;

    /// <summary>视频类页签（综合/视频）共用卡片网格；用户页签走整宽行卡。</summary>
    public bool IsUserTab => _kind == SearchKind.User;

    public Visibility MultiViewVisibility => IsUserTab || !_multiColumn ? Visibility.Collapsed : Visibility.Visible;

    public Visibility SingleViewVisibility => IsUserTab || _multiColumn ? Visibility.Collapsed : Visibility.Visible;

    public Visibility UserViewVisibility => IsUserTab ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>相关词整排只在回包给了 guide_search_words 时才出现（官方没词就收掉这一行）。</summary>
    public Visibility ChipsVisibility => _kind != SearchKind.User && _guideWords.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    /// <summary>多列/单列只在综合页签给（官方 enableListChange 就综合这一枚开着）。</summary>
    public Visibility ColumnToggleVisibility => _kind == SearchKind.General ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>筛选只有综合/视频两组值可用（官方用户/直播页签的 filter 是空数组）。</summary>
    public Visibility FilterVisibility => _kind is SearchKind.General or SearchKind.Video
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool HasMore => _hasMore;

    public bool IsLoading => _loading;

    public Visibility LoadingVisibility => _loading ? Visibility.Visible : Visibility.Collapsed;

    public string StatusText => _status;

    public string EmptyText => (IsUserTab ? UserResults.Count : Results.Count) == 0 && !_loading
        ? _keyword.Length == 0 ? "输入关键词开始搜索" : EmptyReason
        : string.Empty;

    /// <summary>
    /// 空页分两种：真没结果，和回包明说的 <c>search_nil_type</c>（实测 <c>verify_check</c>＝有货也不给，
    /// 这一档被挡在验证之外）。混着报会让人白找——2026-10-20 那次搜索全空就是这么被误判成端点坏了的。
    /// </summary>
    private string EmptyReason => _nilType.Length == 0
        ? "没搜到相关内容"
        : $"这一档被抖音挡在验证之外（{_nilType}），过几分钟再搜";

    public Visibility EmptyVisibility => EmptyText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>换关键词：账面全收（含相关词），取第一页。同一个关键词重复调用也重取。</summary>
    public Task SearchAsync(string keyword)
    {
        keyword = keyword.Trim();
        if (keyword.Length == 0)
        {
            return Task.CompletedTask;
        }

        _keyword = keyword;
        _refine = string.Empty;
        _guideWords.Clear();
        RebuildChips();
        return ReloadAsync();
    }

    /// <summary>切页签：官方换页签就是重发一发（search_source 走 switch_tab，我们沿用 normal_search，实测都能通）。</summary>
    public Task SetKindAsync(SearchKind kind)
    {
        if (kind == _kind)
        {
            return Task.CompletedTask;
        }

        _kind = kind;
        _guideWords.Clear();
        RebuildChips();
        NotifyViewFlags();
        return ReloadAsync();
    }

    /// <summary>筛选：0 是不筛；任一非 0 都按官方口径把 is_filter_search 抬成 1。</summary>
    public Task SetFilterAsync(int sortType, int publishTime)
    {
        if (sortType == _sortType && publishTime == _publishTime)
        {
            return Task.CompletedTask;
        }

        _sortType = sortType;
        _publishTime = publishTime;
        Notify(nameof(HasFilter));
        Notify(nameof(FilterBadgeVisibility));
        Notify(nameof(FilterSummaryText));
        Notify(nameof(SortLabelText));
        Notify(nameof(TimeLabelText));
        return ReloadAsync();
    }

    /// <summary>这枚相关词现在选中吗（页面点完要拿它把 ToggleButton 的视觉拨回账面）。</summary>
    public bool IsChipSelected(string word) => string.Equals(word, _refine, StringComparison.Ordinal);

    /// <summary>点相关词：把该词拼到关键词后面重搜；「全部」清空附加词。</summary>
    public Task SetRefineAsync(string word)
    {
        if (string.Equals(word, _refine, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        _refine = word;
        UpdateChipSelection();
        return ReloadAsync();
    }

    /// <summary>多列/单列只换排布，不重取——同一批结果，官方也只是改 cookie 与 list_type。</summary>
    public void SetMultiColumn(bool multiColumn)
    {
        if (multiColumn == _multiColumn)
        {
            return;
        }

        _multiColumn = multiColumn;
        Notify(nameof(MultiColumn));
        Notify(nameof(SingleColumn));
        Notify(nameof(ColumnModeText));
        Notify(nameof(MultiViewVisibility));
        Notify(nameof(SingleViewVisibility));
        Notify(nameof(ColumnToggleVisibility));
    }

    /// <summary>滚到底续页。正在取 / 没有下一页时直接忽略。</summary>
    public async Task LoadMoreAsync()
    {
        if (_loading || !_hasMore || _keyword.Length == 0)
        {
            return;
        }

        await LoadPageAsync(_gen);
    }

    private Task ReloadAsync()
    {
        _gen++;
        _cursor = 0;
        _searchId = string.Empty;
        _emptyPages = 0;
        _hasMore = true;
        _ids.Clear();
        Results.Clear();
        UserResults.Clear();
        _status = "正在搜索…";
        Notify(nameof(StatusText));
        Notify(nameof(EmptyText));
        Notify(nameof(EmptyVisibility));
        NotifyViewFlags();
        return LoadPageAsync(_gen);
    }

    private async Task LoadPageAsync(int gen)
    {
        await _loadLock.WaitAsync();
        try
        {
            if (gen != _gen || _keyword.Length == 0 || !_hasMore)
            {
                return;
            }

            _loading = true;
            Notify(nameof(IsLoading));
            Notify(nameof(LoadingVisibility));
            Notify(nameof(EmptyVisibility));

            string query = _refine.Length > 0 ? _keyword + " " + _refine : _keyword;
            var request = new SearchQuery(
                _kind, query, _cursor, PageSize, _searchId, _sortType, _publishTime, _multiColumn);

            IReadOnlyList<VideoCard> cards = [];
            IReadOnlyList<UserInfo> users = [];
            long nextCursor = _cursor;
            bool nextHasMore = false;
            string nextSearchId = _searchId;
            List<string> guideWords = [];
            string nilType = string.Empty;
            string error = string.Empty;
            try
            {
                if (_kind == SearchKind.User)
                {
                    SearchUserPage page = await Task.Run(
                        () => Douyin.Feed.SearchUsersAsync(request, CancellationToken.None));
                    users = page.Items;
                    nextCursor = page.Cursor;
                    nextHasMore = page.HasMore;
                    nextSearchId = page.SearchId;
                    nilType = page.NilType;
                }
                else
                {
                    SearchVideoPage page = await Task.Run(
                        () => Douyin.Feed.SearchAsync(request, CancellationToken.None));
                    cards = page.Items;
                    guideWords = [.. page.GuideWords ?? []];
                    nextCursor = page.Cursor;
                    nextHasMore = page.HasMore;
                    nextSearchId = page.SearchId;
                    nilType = page.NilType;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (gen != _gen)
            {
                return;   // 期间又换了词/页签/筛选：这一发作废
            }

            if (error.Length > 0)
            {
                _hasMore = false;
                _nilType = string.Empty;
                _status = "搜索没读回来：" + error;
                Notify(nameof(StatusText));
                return;
            }

            int added = 0;
            if (_kind == SearchKind.User)
            {
                foreach (UserInfo user in users)
                {
                    string key = user.Uid.Length > 0 ? user.Uid : user.SecUid;
                    if (key.Length == 0 || !_ids.Add(key))
                    {
                        continue;
                    }

                    UserResults.Add(new SearchUserRow(user));
                    added++;
                }
            }
            else
            {
                foreach (VideoCard card in cards)
                {
                    if (card.Id.Length == 0 || !_ids.Add(card.Id))
                    {
                        continue;
                    }

                    Results.Add(new SearchResultRow(card));
                    added++;
                }
            }

            if (_kind != SearchKind.User && _guideWords.Count == 0 && guideWords.Count > 0)
            {
                _guideWords.AddRange(guideWords);
                RebuildChips();
            }

            int shown = IsUserTab ? UserResults.Count : Results.Count;
            _cursor = nextCursor;
            _searchId = nextSearchId;
            _nilType = nilType;
            // 一页可能全是榜单/相关卡这类非视频条目；连续几页都没添到新东西才认"到底"
            _emptyPages = added > 0 ? 0 : _emptyPages + 1;
            _hasMore = nextHasMore && _emptyPages < 3;
            _status = shown == 0
                ? (_hasMore ? "正在搜索…" : EmptyReason)
                : $"已取 {shown} 条" + (_hasMore ? " · 继续下滑加载更多" : " · 到底了");
            Notify(nameof(StatusText));
            Notify(nameof(HasMore));
        }
        finally
        {
            if (gen == _gen)
            {
                _loading = false;
            }

            Notify(nameof(IsLoading));
            Notify(nameof(LoadingVisibility));
            Notify(nameof(EmptyText));
            Notify(nameof(EmptyVisibility));
            _loadLock.Release();
        }
    }

    private void RebuildChips()
    {
        Chips.Clear();
        if (_kind == SearchKind.User)
        {
            return;
        }

        Chips.Add(new SearchChip("全部", string.Empty, _refine.Length == 0));
        foreach (string word in _guideWords)
        {
            Chips.Add(new SearchChip(word, word, string.Equals(word, _refine, StringComparison.Ordinal)));
        }

        Notify(nameof(ChipsVisibility));
    }

    /// <summary>只翻选中态，不重建集合：重建会让每枚 ToggleButton 重新过一次布局。</summary>
    private void UpdateChipSelection()
    {
        foreach (SearchChip chip in Chips)
        {
            chip.IsSelected = string.Equals(chip.Word, _refine, StringComparison.Ordinal);
        }
    }

    private void NotifyViewFlags()
    {
        Notify(nameof(MultiViewVisibility));
        Notify(nameof(SingleViewVisibility));
        Notify(nameof(UserViewVisibility));
        Notify(nameof(ChipsVisibility));
        Notify(nameof(ColumnToggleVisibility));
        Notify(nameof(FilterVisibility));
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
