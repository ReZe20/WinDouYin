using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinDouYin.Contracts;
using WinDouYin.Service;

namespace WinDouYin.ViewModels;

/// <summary>
/// 主页一级页签：官方他人视角是 <c>作品 / 推荐 / 喜欢</c>，本人「账号」页再多 <c>收藏</c> 与 <c>观看历史</c>。
/// 2026-10-20 内置浏览器实测：他人视角点「推荐」与「喜欢」官方**连请求都不发**，
/// 页面直接写「推荐内容不可见」「喜欢内容不可见 · 该用户已将喜欢设为私密」，
/// 判据在资料回包里（<c>profile_tab_info.profile_tab_list</c> 空、<c>show_favorite_list=false</c>）。
/// </summary>
public enum UserProfileTab
{
    Works,
    Recommend,
    Likes,
    Favorite,
    History,
}

/// <summary>「作品」下的二级页签：<c>作品 / 合集 / 短剧</c>（后两枚按资料的 is_mix_user / is_series_user 决定摆不摆）。</summary>
public enum UserProfileSection
{
    Posts,
    Mix,
    Series,
}

/// <summary>
/// 「收藏」下的二级页签，官方实测就这五档（<c>semiTabfavorite_folder</c> / <c>semiTabvideo</c> /
/// <c>semiTabmusic</c> / <c>semiTabcompilation</c> / <c>semiTabplaylet</c>），端点逐条实测过：
/// 收藏夹 <c>collects/list</c> + <c>collects/video/list</c>、视频 <c>aweme/listcollection</c>、
/// 音乐 <c>music/listcollection</c>、合集 <c>mix/listcollection</c>、短剧 <c>series/collections</c>。
/// 合集与短剧的回包数组键与主页那两档同形，所以共用那张卡模板。
/// </summary>
public enum UserFavoriteSection
{
    Folders,
    Videos,

    /// <summary>音乐：卡是"原声"轨，与视频卡、合集卡都不同形，单独一份账。</summary>
    Music,

    /// <summary>合集：走 mix/listcollection，卡与主页「作品→合集」同形，复用同一张卡模板。</summary>
    Compilation,

    /// <summary>短剧：走 series/collections，同样复用合集卡模板。</summary>
    Playlet,
}

/// <summary>
/// 主页作品卡的一行。规格取官方主页实测：封面 <c>3:4</c>（实录 311×413），
/// 卡上那个数是**点赞**不是播放（实测卡面「203.9万」对回包 <c>statistics.digg_count=2038849</c>），
/// 前三条带「置顶」（回包 <c>is_top=1</c>，已落成 <see cref="VideoCard.Pinned"/>）。
/// </summary>
public sealed class UserProfilePostRow
{
    /// <summary>卡宽按 240 画，2 倍封顶解码，别把整张竖封面解进内存。</summary>
    private const int CoverDecodeWidth = 480;

    public UserProfilePostRow(VideoCard card, long watchedAtMs = 0)
    {
        Card = card;
        Id = card.Id;
        Desc = card.Desc.Length > 0 ? card.Desc : "无标题";
        LikeText = CountText.FormatCn(card.Digg);
        PinnedVisibility = card.Pinned ? Visibility.Visible : Visibility.Collapsed;
        bool pic = string.Equals(card.Kind, "pic", StringComparison.Ordinal);
        ImageBadgeVisibility = pic ? Visibility.Visible : Visibility.Collapsed;
        Cover = card.Cover.Length > 0 ? Decode(card.Cover) : null;
        WatchedAtText = watchedAtMs > 0 ? "看过 · " + FormatWatchedAt(watchedAtMs) : string.Empty;
        WatchedAtMs = watchedAtMs;

        // 时长与发布时间都来自同一条 aweme：video.duration（毫秒）与 create_time（秒），
        // 2026-10-21 拿 aweme/post 的真实回包核过——21 条里 20 条时长 > 0、21 条都带 create_time。
        // 图文帖（aweme_type=68）video.duration 是 0，实况图也可能有值，所以按 Kind 挡掉而不是按时长挡。
        DurationText = pic ? string.Empty : FormatDuration(card.DurationMs);
        DurationVisibility = DurationText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PublishedText = FormatPublishedAt(card.CreateTime);
        PublishedVisibility = PublishedText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>点卡进视频页要的原条目。</summary>
    public VideoCard Card { get; }

    public string Id { get; }

    public string Desc { get; }

    public string LikeText { get; }

    public Visibility PinnedVisibility { get; }

    public Visibility ImageBadgeVisibility { get; }

    public ImageSource? Cover { get; }

    /// <summary>观看历史卡的"什么时候看过"；作品/喜欢两档没有这一项（空串 → 整行收起）。</summary>
    public string WatchedAtText { get; }

    /// <summary>观看时间原值：落盘缓存时要把它跟条目一起存回去（只有历史档有）。</summary>
    public long WatchedAtMs { get; }

    public Visibility WatchedAtVisibility => WatchedAtText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>封面右下角的时长（原生 m:ss / h:mm:ss）；图文帖为空。</summary>
    public string DurationText { get; }

    public Visibility DurationVisibility { get; }

    /// <summary>卡底右侧的发布时间；当年只写「10月19日」，跨年补年份。</summary>
    public string PublishedText { get; }

    public Visibility PublishedVisibility { get; }

    /// <summary>毫秒 → m:ss（过一小时补到 h:mm:ss）。四舍五入到秒，38820ms 记作 0:39 而不是 0:38。</summary>
    private static string FormatDuration(int durationMs)
    {
        int seconds = (durationMs + 500) / 1000;
        return seconds <= 0
            ? string.Empty
            : seconds >= 3600
                ? $"{seconds / 3600:00}:{seconds % 3600 / 60:00}:{seconds % 60:00}"
                : $"{seconds / 60}:{seconds % 60:00}";
    }

    private static string FormatPublishedAt(long unixSeconds)
    {
        if (unixSeconds <= 0)
        {
            return string.Empty;
        }

        DateTime when = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).LocalDateTime;
        return when.Year == DateTime.Now.Year
            ? $"{when.Month}月{when.Day}日"
            : $"{when.Year}年{when.Month}月{when.Day}日";
    }

    /// <summary>观看时间：今天/昨天只给时分，今年只给月日，跨年才带年份（账号历史回到很早也不至于太长）。</summary>
    private static string FormatWatchedAt(long unixMs)
    {
        DateTimeOffset at = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime();
        DateTime today = DateTime.Today;
        string day = at.Date == today
            ? "今天"
            : at.Date == today.AddDays(-1)
                ? "昨天"
                : at.Year == today.Year
                    ? at.ToString("M月d日")
                    : at.ToString("yyyy年M月d日");
        return day + " " + at.ToString("HH:mm");
    }

    private static BitmapImage Decode(string url)
    {
        var bitmap = new BitmapImage { DecodePixelWidth = CoverDecodeWidth };
        bitmap.UriSource = new Uri(url);
        return bitmap;
    }
}

/// <summary>
/// 合集 / 短剧卡的一行：竖封面 + 名称 + 「更 N 集」+ 热度。
/// 字段口径见 <see cref="MixCard"/>（实测 statis.updated_to_episode / play_vv）。
/// </summary>
public sealed class UserProfileMixRow
{
    private const int CoverDecodeWidth = 480;

    public UserProfileMixRow(MixCard mix)
    {
        IsMix = true;
        MixSource = mix;
        Id = mix.Id;
        Name = mix.Name.Length > 0 ? mix.Name : "合集";
        Desc = mix.Desc;
        EpisodeText = mix.UpdatedToEpisode > 0 ? $"更{mix.UpdatedToEpisode}集" : string.Empty;
        HotText = CountText.FormatCn(mix.PlayCount);
        Cover = mix.Cover.Length > 0 ? Decode(mix.Cover) : null;
    }

    public UserProfileMixRow(SeriesCard series)
    {
        SeriesSource = series;
        Id = series.Id;
        Name = series.Name.Length > 0 ? series.Name : "短剧";
        Desc = series.Desc;
        EpisodeText = series.EpisodeCount > 0 ? $"共{series.EpisodeCount}集" : string.Empty;
        HotText = string.Empty;
        Cover = series.Cover.Length > 0 ? Decode(series.Cover) : null;
    }

    /// <summary>true=合集（内容端点已验，可点开）；false=短剧（内容端点未验，点了只给说明）。</summary>
    public bool IsMix { get; }

    /// <summary>角标按真实形态写：这两档共用一张卡，短剧不能挂着"合集"两个字。</summary>
    public string BadgeText => IsMix ? "合集" : "短剧";

    /// <summary>落盘缓存的原始条目（二选一）。</summary>
    public MixCard? MixSource { get; }

    public SeriesCard? SeriesSource { get; }

    public string Id { get; }

    public string Name { get; }

    public string Desc { get; }

    public string EpisodeText { get; }

    public Visibility EpisodeVisibility => EpisodeText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string HotText { get; }

    public Visibility HotVisibility => HotText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public ImageSource? Cover { get; }

    private static BitmapImage Decode(string url)
    {
        var bitmap = new BitmapImage { DecodePixelWidth = CoverDecodeWidth };
        bitmap.UriSource = new Uri(url);
        return bitmap;
    }
}

/// <summary>
/// 收藏音乐卡的一行：方封面 + 曲名 + 作者。官方这条只是「原声」，播放链我们没验过，
/// 所以卡面不做成按钮——能摆形状，不能给动作。
/// </summary>
public sealed class UserProfileMusicRow
{
    private const int CoverDecodeWidth = 360;

    public UserProfileMusicRow(MusicCard music)
    {
        Music = music;
        Id = music.Id;
        Title = music.Title.Length > 0 ? music.Title : "未命名音乐";
        Author = music.Author;
        AuthorVisibility = music.Author.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Cover = music.Cover.Length > 0 ? Decode(music.Cover) : null;
    }

    public string Id { get; }

    /// <summary>落盘缓存用的原始条目。</summary>
    public MusicCard Music { get; }

    public string Title { get; }

    public string Author { get; }

    public Visibility AuthorVisibility { get; }

    public ImageSource? Cover { get; }

    private static BitmapImage Decode(string url)
    {
        var bitmap = new BitmapImage { DecodePixelWidth = CoverDecodeWidth };
        bitmap.UriSource = new Uri(url);
        return bitmap;
    }
}

/// <summary>
/// 收藏夹卡的一行：一张封面 + 夹名 + 「共 N 作品」。
/// 官方卡面是「夹名 + 一排 5 枚小封面」，但 <c>collects/list</c> 只给一枚 <c>collects_cover</c>
/// （<c>sicily_collects_cover_list</c> 实测为 null，5 枚是页面再按夹逐发 <c>collects/video/list</c> 拼的），
/// 所以这里用单封面卡，不为省一次请求去发 84 发。
/// </summary>
public sealed class UserProfileCollectFolderRow
{
    private const int CoverDecodeWidth = 480;

    public UserProfileCollectFolderRow(CollectFolder folder)
    {
        Folder = folder;
        Id = folder.Id;
        Name = folder.Name.Length > 0 ? folder.Name : "未命名收藏夹";
        CountText = "共 " + folder.Count + " 作品";
        Cover = folder.Cover.Length > 0 ? Decode(folder.Cover) : null;
    }

    /// <summary>点开夹内列表要用回包原对象（id + 名字 + 条数）。</summary>
    public CollectFolder Folder { get; }

    public string Id { get; }

    public string Name { get; }

    public string CountText { get; }

    public ImageSource? Cover { get; }

    private static BitmapImage Decode(string url)
    {
        var bitmap = new BitmapImage { DecodePixelWidth = CoverDecodeWidth };
        bitmap.UriSource = new Uri(url);
        return bitmap;
    }
}

/// <summary>
/// 用户主页的数据面：一份资料 + 「作品」下的作品/合集/短剧三档 + 「喜欢」+ 本人页的「收藏」与「观看历史」，各自分页。
/// 一级切页签换的是账面与取数目标，二级切档同理；「推荐」按官方口径只给不可见文案（实测它不发请求）。
/// 签名与解析都是同步 CPU 活，整发扔线程池，回 UI 才往 ObservableCollection 里加（照 SearchViewModel 那套）。
/// </summary>
public sealed partial class UserProfileViewModel : INotifyPropertyChanged
{
    /// <summary>官方页面实录 count=18（作品与合集都是 18）。</summary>
    private const int PageSize = 18;

    /// <summary>history/read 实测一页最多 20 条（count=50 回 142B 空页），单独一档。</summary>
    private const int HistoryPageSize = 20;

    /// <summary>收藏两档（夹内 <c>collects/video/list</c> 与全量 <c>aweme/listcollection</c>）官方页面实录都是 count=10。</summary>
    private const int FavoritePageSize = 10;

    /// <summary>收藏的合集/短剧两档官方页面实录 count=12（与主页那两档的 18 不同，端点也不是同一条）。</summary>
    private const int CollectionPageSize = 12;

    /// <summary>收藏音乐 <c>music/listcollection</c> 官方页面实录 count=20（实测回 8 条，账号本来就这么些）。</summary>
    private const int MusicPageSize = 20;

    private static readonly IReadOnlyDictionary<string, long> EmptyWatchedAt = new Dictionary<string, long>();

    /// <summary>
    /// 每档最多留这么多行。虚拟化只省可视元素、不省行对象里的位图，滚几百条之后内存线性涨；
    /// 到顶就停止续页并在状态行说明——这是有意的上限，不是取不到。
    /// </summary>
    private const int MaxRows = 500;

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly FeedState _works = new("works");
    private readonly FeedState _likes = new("likes");
    private readonly FeedState _history = new("history");
    private readonly FeedState _mix = new("mix");
    private readonly FeedState _series = new("series");

    /// <summary>「收藏 &gt; 视频」这一档的账（全账号收藏平铺，<c>aweme/listcollection</c>）。</summary>
    private readonly FeedState _favorite = new("favorite");

    /// <summary>「收藏 &gt; 收藏夹」格子的账：一发取全，Loaded 之后 HasMore 一直是 false（不参与续页）。</summary>
    private readonly FeedState _foldersState = new("folders");

    /// <summary>点开某个收藏夹后那一档的账（<c>collects/video/list</c>）。</summary>
    private readonly FeedState _folder = new("folder");

    /// <summary>「收藏 &gt; 音乐」（<c>music/listcollection</c>，回包 <c>mc_list</c>）。</summary>
    private readonly FeedState _favMusic = new("favMusic");

    /// <summary>「收藏 &gt; 合集」（<c>mix/listcollection</c>，回包 <c>mix_infos</c>，与主页合集同形）。</summary>
    private readonly FeedState _favMix = new("favMix");

    /// <summary>「收藏 &gt; 短剧」（<c>series/collections</c>，回包 <c>series_infos</c>）。</summary>
    private readonly FeedState _favSeries = new("favSeries");

    private readonly ObservableCollection<UserProfileCollectFolderRow> _folders = [];
    private UserFavoriteSection _favoriteSection = UserFavoriteSection.Folders;
    private CollectFolder? _openedFolder;
    private string _secUid = string.Empty;
    private UserProfile? _profile;

    /// <summary>这个人硬盘上那份快照（读完一直留在内存里：切页签时按档补铺，不必再读盘）。</summary>
    private ProfileSnapshot? _cache;

    // 头像/封面位图按 URL 缓存：这两个属性每轮 NotifyEverything 都被 x:Bind 重读一次
    // （CoverVisibility 原来还要再读一遍 CoverImage），而 getter 里每次都 new BitmapImage、
    // 封面按 1280 宽解码——进一次主页就是十来张封面的重复下载解码，卡顿主要出在这。
    // BitmapImage 有线程亲和，只能在 UI 线程建，所以这里只缓存、不往线程池挪。
    private BitmapImage? _avatarSource;
    private string _avatarSourceUrl = string.Empty;
    private BitmapImage? _coverSource;
    private string _coverSourceUrl = string.Empty;
    private UserProfileTab _tab = UserProfileTab.Works;
    private UserProfileSection _section = UserProfileSection.Posts;
    private IReadOnlyList<string> _months = [];
    private bool _loading;
    private bool _selfMode;
    private bool _selfLoginRequired;
    private string _status = string.Empty;
    private int _gen;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 作品 / 喜欢 / 观看历史 / 合集 / 短剧，再加收藏那一族（格子、夹内、视频、音乐、合集、短剧）——每档一份账
    /// （集合 + 游标 + 去重 + 是否已取过第一页）。
    /// 切页签只换账面、不重取：已加载的档切回来直接还原，行对象与位图都复用。
    /// </summary>
    private sealed class FeedState
    {
        public FeedState(string cacheKey) => CacheKey = cacheKey;

        /// <summary>硬盘缓存里的档名（works / likes / history / favorite / folders / folder:&lt;id&gt; / mix / series / favMusic / favMix / favSeries）。</summary>
        public string CacheKey { get; set; }

        public HashSet<string> Ids { get; } = new(StringComparer.Ordinal);

        public ObservableCollection<UserProfilePostRow> Posts { get; } = [];

        public ObservableCollection<UserProfileMixRow> Mixes { get; } = [];

        /// <summary>只有「收藏→音乐」那份额外用得到（音乐轨与视频卡/合集卡不同形）。</summary>
        public ObservableCollection<UserProfileMusicRow> Musics { get; } = [];

        public long Cursor { get; set; }

        public bool HasMore { get; set; } = true;

        public bool Loaded { get; set; }

        /// <summary>换号/退出/重开主页时清回未加载态（集合清空、游标归零、去重表丢掉）。</summary>
        public void Reset()
        {
            Ids.Clear();
            Posts.Clear();
            Mixes.Clear();
            Musics.Clear();
            Cursor = 0;
            HasMore = true;
            Loaded = false;
        }
    }

    private FeedState CurrentState => _tab switch
    {
        UserProfileTab.Likes => _likes,
        UserProfileTab.History => _history,
        UserProfileTab.Favorite when _favoriteSection == UserFavoriteSection.Videos => _favorite,
        UserProfileTab.Favorite when _favoriteSection == UserFavoriteSection.Music => _favMusic,
        UserProfileTab.Favorite when _favoriteSection == UserFavoriteSection.Compilation => _favMix,
        UserProfileTab.Favorite when _favoriteSection == UserFavoriteSection.Playlet => _favSeries,
        UserProfileTab.Favorite when _openedFolder is not null => _folder,
        UserProfileTab.Favorite => _foldersState,
        UserProfileTab.Works when _section == UserProfileSection.Series => _series,
        UserProfileTab.Works when _section == UserProfileSection.Mix => _mix,
        _ => _works,
    };

    /// <summary>当前档的作品列（作品、喜欢、历史、收藏的视频与夹内视频共用同一张卡模板）。</summary>
    public ObservableCollection<UserProfilePostRow> Posts => CurrentState.Posts;

    /// <summary>当前档的合集/短剧列。</summary>
    public ObservableCollection<UserProfileMixRow> Mixes => CurrentState.Mixes;

    /// <summary>「收藏 &gt; 音乐」那一档的卡列（音乐轨，别的档用不上，空表）。</summary>
    public ObservableCollection<UserProfileMusicRow> Musics => CurrentState.Musics;

    /// <summary>「收藏 &gt; 收藏夹」的卡列表（一发取全，不续页）。</summary>
    public ObservableCollection<UserProfileCollectFolderRow> CollectFolders => _folders;

    public string SecUid => _secUid;

    /// <summary>「分享主页」用的地址：浏览器实测这条 URL 直接能开，短链那条只对作品有效。</summary>
    public string ShareUrl => _secUid.Length > 0 ? "https://www.douyin.com/user/" + _secUid : string.Empty;

    /// <summary>是不是从底部「账号」进来的本人主页（决定页名与未登录闸门）。</summary>
    public bool IsSelfProfile => _selfMode;

    /// <summary>本机使用热力图（只有本人「账号」页显示；别人的使用数据拿不到）。</summary>
    public UsageHeatmapViewModel Heatmap { get; } = new();

    public Visibility HeatmapVisibility => _selfMode ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>本人主页的页名：底部入口叫「账号」，就不在页头再写一遍「个人主页」。</summary>
    public string PageTitle => _selfMode ? "账号" : "个人主页";

    /// <summary>
    /// 本人主页但还没登录：资料取不回来，页面给一块居中的登录提示，别让头部停在「正在读取主页…」。
    /// 他人主页永远不亮这块。
    /// </summary>
    public Visibility SelfLoginGateVisibility => _selfLoginRequired ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>闸门亮着时把整块主页内容收起，别让空头部/空列表从后面露出来。</summary>
    public Visibility SelfProfileBodyVisibility => _selfLoginRequired ? Visibility.Collapsed : Visibility.Visible;

    // ===== 头部：全部由资料派生，没取到之前是占位文案 + 藏起来 =====

    public string Nickname => _profile?.Nickname ?? "正在读取主页…";

    public string VerifyLabel => _profile?.VerifyLabel ?? string.Empty;

    public Visibility VerifyVisibility => _profile?.VerifyLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string FollowCountText => CountText.FormatCn(_profile?.FollowingCount ?? 0);

    public string FollowerCountText => CountText.FormatCn(_profile?.FollowerCount ?? 0);

    public string FavoritedCountText => CountText.FormatCn(_profile?.TotalFavorited ?? 0);

    /// <summary>三数这一行取到资料才摆（没取到时不该出现一排 0）。</summary>
    public Visibility StatsVisibility => _profile is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// 自己的主页才把「关注 / 粉丝」做成入口：2026-10-20 实测**他人主页点那两个数不发请求、也没有弹层**，
    /// 所以他人页保持纯文本（照官方），自己的页给可点入口。
    /// </summary>
    public Visibility StatsEntryVisibility => IsSelf ? Visibility.Visible : Visibility.Collapsed;

    public Visibility StatsTextVisibility => IsSelf ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>「抖音号：superslow」——回包 <c>unique_id</c>，没有就退回 <c>short_id</c>，两个都空就整行不摆。</summary>
    public string HandleLine => _profile is null || _profile.Handle.Length == 0
        ? string.Empty
        : "抖音号：" + _profile.Handle;

    public Visibility HandleVisibility => HandleLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>回包 <c>ip_location</c> 实测**自带「IP属地：」前缀**，这里照抄，别再拼一遍。</summary>
    public string IpLine => _profile?.IpLocation ?? string.Empty;

    public Visibility IpVisibility => IpLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>「30岁 · 浙江·杭州」——年龄是 <c>user_age</c> 纯数字，「岁」和分隔点都是这儿补的。</summary>
    public string RegionLine
    {
        get
        {
            if (_profile is null)
            {
                return string.Empty;
            }

            string age = _profile.Age.Length > 0 ? _profile.Age + "岁" : string.Empty;
            string area = _profile.Area;
            return age.Length > 0 && area.Length > 0 ? age + " · " + area : age + area;
        }
    }

    public Visibility RegionVisibility => RegionLine.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string SignatureText => _profile?.Signature ?? string.Empty;

    public Visibility SignatureVisibility => SignatureText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private bool _signatureExpanded;

    /// <summary>简介够长才给「更多」；官方折叠成 3 行，点开就不限行。</summary>
    public Visibility SignatureToggleVisibility => SignatureText.Length > 60 || _signatureExpanded
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string SignatureToggleLabel => _signatureExpanded ? "收起" : "更多";

    /// <summary>头部简介的 <c>MaxLines</c>（0 = 不限，WinUI 的默认值就是它）。</summary>
    public int SignatureMaxLines => _signatureExpanded ? 0 : 3;

    public void ToggleSignature()
    {
        _signatureExpanded = !_signatureExpanded;
        Notify(nameof(SignatureToggleVisibility));
        Notify(nameof(SignatureToggleLabel));
        Notify(nameof(SignatureMaxLines));
    }

    public ImageSource? AvatarImage => CachedImage(ref _avatarSource, ref _avatarSourceUrl, _profile?.Avatar, 224);

    public ImageSource? CoverImage => CachedImage(ref _coverSource, ref _coverSourceUrl, _profile?.Cover, 1280);

    /// <summary>判据看 URL 而不是判缓存：绑定的求值顺序不保证 CoverImage 先跑。</summary>
    public Visibility CoverVisibility => _profile is { Cover.Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    private static ImageSource? CachedImage(ref BitmapImage? cache, ref string cachedUrl, string? url, int decodeWidth)
    {
        if (string.IsNullOrEmpty(url))
        {
            cache = null;
            cachedUrl = string.Empty;
            return null;
        }

        if (!string.Equals(url, cachedUrl, StringComparison.Ordinal))
        {
            cachedUrl = url;
            cache = Decode(url, decodeWidth);
        }

        return cache;
    }

    public string Initials => Nickname.Length > 0 ? Nickname[..1] : "?";

    // ===== 关注（写链 2026-10-20 实测：POST commit/follow/user，form type=1|0&user_id=<数字 uid>）=====

    public bool IsFollowing => _profile is { FollowStatus: > 0 };

    /// <summary>
    /// 是不是自己的主页：是的话不摆关注按钮（关注自己必然失败）。
    /// 底部「账号」入口直接按本人模式加载（<see cref="_selfMode"/>）；从他人链接进来时仍按
    /// <c>profile.SecUid == MySecUid</c> 判断，二者任一成立都算本人。
    /// </summary>
    public bool IsSelf => _profile is not null
        && (_selfMode
            || (Douyin.Feed.MySecUid.Length > 0
                && string.Equals(_profile.SecUid, Douyin.Feed.MySecUid, StringComparison.Ordinal)));

    /// <summary>未关注时的强调色按钮；资料没取到、是自己的主页、或已关注时收起来。</summary>
    public Visibility FollowButtonVisibility => _profile is null || IsSelf || IsFollowing
        ? Visibility.Collapsed
        : Visibility.Visible;

    /// <summary>已关注时的次要按钮（官方点它直接取关，没有二次确认）。自己的主页不摆。</summary>
    public Visibility FollowingButtonVisibility => !IsSelf && IsFollowing ? Visibility.Visible : Visibility.Collapsed;

    public bool FollowBusy { get; private set; }

    /// <summary>
    /// 关注 / 取关。回执绿只说明这一发被接受，账号上的真实状态**读回 follow_status 为准**
    /// （commit 族"滞后一拍"的既有结论），所以成功后重取一次资料再刷新按钮。
    /// </summary>
    public async Task ToggleFollowAsync()
    {
        if (FollowBusy || _profile is null || _profile.Uid.Length == 0)
        {
            return;
        }

        if (Douyin.Feed.LoginVerified != true)
        {
            _status = "先登录再关注";
            Notify(nameof(StatusText));
            return;
        }

        FollowBusy = true;
        bool follow = !IsFollowing;
        bool ok;
        string note;
        try
        {
            (ok, note) = await Task.Run(() => Douyin.Feed.FollowUserAsync(
                _profile.Uid, _secUid, follow, CancellationToken.None));
        }
        catch (Exception ex)
        {
            ok = false;
            note = "关注没发出去：" + ex.Message;
        }

        FollowBusy = false;
        if (!ok)
        {
            _status = note;
            Notify(nameof(StatusText));
            return;
        }

        await RefreshProfileAsync();
    }

    /// <summary>只重取资料并刷新头部账面（关注/取关后核对 follow_status 用，不碰作品列表）。</summary>
    private async Task RefreshProfileAsync()
    {
        try
        {
            _profile = await Task.Run(() => Douyin.Feed.UserProfileOtherAsync(_secUid, CancellationToken.None));
            _status = string.Empty;
        }
        catch (Exception ex)
        {
            _status = "资料刷新失败：" + ex.Message;
        }

        NotifyEverything();
    }

    // ===== 页签账面 =====

    public UserProfileTab Tab => _tab;

    public UserProfileSection Section => _section;

    public string WorksHeaderLabel => "作品";

    /// <summary>页签文字：官方是「作品 540」，计数取资料回包的 <c>aweme_count</c>。</summary>
    public string WorksHeaderLine => _profile is null ? "作品" : "作品 " + _profile.AwemeCount;

    /// <summary>资料说正在直播时，昵称后面挂一枚「直播中」（项目没有直播播放，所以只做标识不做跳转）。</summary>
    public Visibility IsLivingVisibility => _profile is { IsLiving: true } ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>页签上那颗计数（实测「作品 540」）。</summary>
    public string WorksCountText => _profile is null ? string.Empty : _profile.AwemeCount.ToString();

    public Visibility WorksCountVisibility => _profile is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// 二级「合集」照官方照摆，不看 is_mix_user：2026-10-20 拿到实测反例——@影视飓风 的
    /// is_series_user=false / series_count=0，官方「短剧」页签**照样摆着**，点进去只是空态
    /// 「暂无内容 · 该账号还未发布过短剧哦～」。既然短剧都照摆，合集没理由按开关藏。
    /// </summary>
    public Visibility MixSectionVisibility => Visibility.Visible;

    /// <summary>二级「短剧」：同上，照摆；有没有内容由空态文本说。</summary>
    public Visibility SeriesSectionVisibility => Visibility.Visible;

    public bool IsWorksTab => _tab == UserProfileTab.Works;

    public bool IsRecommendTab => _tab == UserProfileTab.Recommend;

    public bool IsLikesTab => _tab == UserProfileTab.Likes;

    public bool IsHistoryTab => _tab == UserProfileTab.History;

    public bool IsFavoriteTab => _tab == UserProfileTab.Favorite;

    /// <summary>
    /// 「收藏」与「观看历史」都只在自己的页面上摆：读的是本账号的 <c>collects/*</c> 与 <c>history/read</c>，
    /// 官方他人主页也没有这两档。本人模式（底部「账号」）先亮，按 sec_uid 认出是自己后也亮。
    /// </summary>
    public Visibility SelfOnlyTabVisibility => _selfMode || IsSelf ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>「收藏」下的二级页签条：只在收藏档亮。</summary>
    public Visibility FavoriteSectionsVisibility => IsFavoriteTab ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>收藏夹格子（没点开夹时摆）。</summary>
    public Visibility FolderGridVisibility =>
        IsFavoriteTab && _favoriteSection == UserFavoriteSection.Folders && _openedFolder is null
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>点开夹后那一行的夹头（返回 + 夹名 + 条数）。</summary>
    public Visibility FolderHeaderVisibility =>
        IsFavoriteTab && _favoriteSection == UserFavoriteSection.Folders && _openedFolder is not null
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string OpenedFolderName => _openedFolder?.Name ?? string.Empty;

    public string OpenedFolderCountText => _openedFolder is null ? string.Empty : "共 " + _openedFolder.Count + " 作品";

    public Visibility ListAreaVisibility => IsWorksTab || IsLikesTab || IsHistoryTab || IsFavoriteTab
        ? Visibility.Visible
        : Visibility.Collapsed;

    /// <summary>「推荐」档的文案照官方原句（实测他人主页就是这句，且不发请求）。</summary>
    public string RecommendNotice => "推荐内容不可见";

    public Visibility RecommendNoticeVisibility => IsRecommendTab ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>「喜欢」档：资料没公开就照官方给两句提示；公开了才去取（实测多半回 200·0B，那时就是真没喜欢）。</summary>
    public string LikesNotice => _profile?.ShowFavoriteList == true
        ? string.Empty
        : "喜欢内容不可见\n该用户已将喜欢设为私密";

    public Visibility LikesNoticeVisibility => IsLikesTab && _profile?.ShowFavoriteList != true
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility CardsVisibility => ShowsCards ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>当前档该不该摆卡列表（喜欢档在没公开时不摆，列表是空的）。</summary>
    private bool ShowsCards => (_tab == UserProfileTab.Works)
        || _tab == UserProfileTab.History
        || IsFavoriteTab
        || (_tab == UserProfileTab.Likes && _profile?.ShowFavoriteList == true);

    /// <summary>
    /// 视频卡列表该不该摆。收藏档特殊：停在「收藏夹」格子时摆的是夹卡，视频列表要让位；
    /// 一旦点开某个夹（或切到「视频」），又回到这张共用的卡列表。
    /// </summary>
    public Visibility PostsViewVisibility => IsFavoriteTab
        ? _favoriteSection == UserFavoriteSection.Videos || _openedFolder is not null
            ? Visibility.Visible
            : Visibility.Collapsed
        : _tab == UserProfileTab.Likes || _tab == UserProfileTab.History || _section == UserProfileSection.Posts
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>合集卡那张列表该不该摆：主页「作品→合集/短剧」，以及收藏→合集/短剧（回包同形，共用模板）。</summary>
    public Visibility MixViewVisibility => (IsWorksTab && _section != UserProfileSection.Posts)
        || (IsFavoriteTab && _favoriteSection is UserFavoriteSection.Compilation or UserFavoriteSection.Playlet)
        ? Visibility.Visible
        : Visibility.Collapsed;

    /// <summary>音乐卡那张列表只在「收藏 → 音乐」摆（别的档没这形）。</summary>
    public Visibility MusicViewVisibility => IsFavoriteTab && _favoriteSection == UserFavoriteSection.Music
        ? Visibility.Visible
        : Visibility.Collapsed;

    public bool HasMore => NeedsFetch && CurrentState.HasMore;

    public bool IsLoading => _loading;

    public Visibility LoadingVisibility => _loading ? Visibility.Visible : Visibility.Collapsed;

    public string StatusText => _status;

    /// <summary>
    /// 当前档已经取回来、却一条都没有 → 列表区摆一行占位，把"为什么是空的"说清（文案照官方原句压成一行）。
    /// 做成派生属性而不是往 <c>_status</c> 里塞：切页签/换档时 _status 要在七八个地方归零，占位跟着账面走
    /// 才不会把上一档的空态说成这一档的。判据只看 <c>state.Loaded</c>（第一页真回来过）与 NeedsFetch
    /// （本来就不该取的档——推荐、没公开的喜欢——不亮）：首帧在飞的时候 Loaded 还是 false，
    /// 所以那几种是"还没拿"，不是"没有"。
    /// </summary>
    public string EmptyHintText
    {
        get
        {
            FeedState state = CurrentState;
            if (!state.Loaded || !NeedsFetch)
            {
                return string.Empty;
            }

            bool folderGrid = _tab == UserProfileTab.Favorite
                && _favoriteSection == UserFavoriteSection.Folders
                && _openedFolder is null;
            int count = folderGrid
                ? _folders.Count
                : MusicViewVisibility == Visibility.Visible ? state.Musics.Count
                    : MixViewVisibility == Visibility.Visible ? state.Mixes.Count : state.Posts.Count;
            if (count > 0)
            {
                return string.Empty;
            }

            return _tab switch
            {
                UserProfileTab.History => "暂无观看历史 · 看过的视频会按时间记在这里",
                UserProfileTab.Likes => _selfMode ? "喜欢列表暂时没有取到内容" : "没有公开的喜欢",
                UserProfileTab.Favorite => folderGrid
                    ? "还没有收藏夹 · 在视频页把它收进某个夹，这里就会出现"
                    : _openedFolder is not null
                        ? "这个收藏夹还没有取到作品"
                        : _favoriteSection switch
                        {
                            UserFavoriteSection.Music => "还没有收藏的音乐",
                            UserFavoriteSection.Compilation => "还没有收藏的合集",
                            UserFavoriteSection.Playlet => "还没有收藏的短剧",
                            _ => "还没有收藏的视频",
                        },
                _ => _section switch
                {
                    UserProfileSection.Series => "暂无内容 · 该账号还未发布过短剧哦～",
                    UserProfileSection.Mix => "暂无内容 · 该账号还未发布过合集哦～",
                    _ => "暂无内容 · 该账号还未发布过作品哦～",
                },
            };
        }
    }

    public Visibility EmptyHintVisibility => EmptyHintText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>作品回包自带的月份表（<c>time_list</c>，实测 "2026·10" 这样）。主页「日期筛选」的数据源，动作还没接。</summary>
    public IReadOnlyList<string> Months => _months;

    /// <summary>
    /// 进页面（他人视角）：先把硬盘上那份快照铺出来（资料 + 上次的作品），再向服务端做增量更新
    /// ——资料整发重取，作品只把第一页里没见过的条目补到最前。
    /// </summary>
    public async Task LoadAsync(string secUid)
    {
        _selfMode = false;
        Reset(secUid);
        int gen = _gen;

        string wanted = _secUid;
        ProfileSnapshot? cache = await Task.Run(() => Douyin.Feed.LoadProfileSnapshot(wanted));
        if (gen != _gen)
        {
            return;
        }

        if (cache is not null)
        {
            ApplyCache(cache);
            NotifyEverything();
        }

        await LoadProfileAsync(gen);
    }

    /// <summary>
    /// 底部「账号」：本人主页。先问一句"我是谁"拿自己的 <c>sec_uid</c>（user/profile/self），
    /// 再照他人主页同一条 <c>profile/other</c> 取整包——2026-10-20 探针实测本人 sec_uid 也是 200 · 14KB，
    /// 认证/封面/喜欢公开等字段与解析器和他人主页完全同一套。
    /// 没登录就拿不到 sec_uid：这时不假装在取资料，页面给登录闸门。
    /// </summary>
    public async Task LoadSelfAsync()
    {
        _selfMode = true;
        Reset(string.Empty);
        _ = Heatmap.RefreshAsync();   // 读账本与建格子都在线程池，别卡住进页这一拍

        int gen = _gen;

        // 先用"这个 uid 上次本人是谁"把缓存铺出来，身份那一发在后台核对——不用等网络才见内容。
        // uid 还没认出来就不拿全局旧 self.json 赌：多账号下那很可能是上一个号的本人页。
        string knownUid = Douyin.Feed.MyUid;
        ProfileSnapshot? cache = await Task.Run(() => Douyin.Feed.LoadSelfSnapshot(knownUid));
        if (gen != _gen)
        {
            return;
        }

        if (cache is not null)
        {
            _secUid = cache.SecUid;
            ApplyCache(cache);
            NotifyEverything();
        }

        string secUid;
        try
        {
            secUid = await Task.Run(() => Douyin.Feed.EnsureMyIdentityAsync(CancellationToken.None));
        }
        catch (Exception ex)
        {
            if (gen == _gen)
            {
                _status = "读取自己的账号失败：" + ex.Message;
                NotifyEverything();
            }

            return;
        }

        // 身份这一发顺带把 uid 补回来（启动时那一问偶尔问不出）：使用账本跟到这个号上。
        // 放在 gen 判断之前——身份是全局的，页面换掉了账本归属也得对。
        // 换号时这一步会把旧账落盘（逐条 AES-GCM 封装 + 写 JSON），所以扔线程池，别在 UI 线程上做。
        await Task.Run(Douyin.Feed.SyncUsageAccount);

        if (gen != _gen)
        {
            return;   // 身份还没回来又换页了，这一发作废
        }

        _ = Heatmap.RefreshAsync();   // 归属刚对齐：重算一次，别拿上一个号的热力图顶上

        if (secUid.Length == 0)
        {
            // 真登录过（服务端说过 true）却拿不到身份，多半是这一发没回来：别误报成"没登录"去弹登录框
            bool verified = Douyin.Feed.LoginVerified == true;
            _selfLoginRequired = !verified;
            _status = verified
                ? "自己的账号身份没取回来，稍后再试"
                : "还没有登录，登录后才能查看自己的主页";
            NotifyEverything();
            return;
        }

        if (!string.Equals(secUid, _secUid, StringComparison.Ordinal))
        {
            // 缓存铺的是上一个号（或压根没铺）：换成本号那本重来，别把别人的列表当自己的。
            _secUid = secUid;
            Reset(secUid);
            gen = _gen;
            ProfileSnapshot? own = await Task.Run(() => Douyin.Feed.LoadProfileSnapshot(secUid));
            if (gen != _gen)
            {
                return;
            }

            if (own is not null)
            {
                ApplyCache(own);
                NotifyEverything();
            }
        }
        else
        {
            _secUid = secUid;
        }

        string ownUid = Douyin.Feed.MyUid;
        if (ownUid.Length > 0 && cache?.SecUid != secUid)
        {
            _ = Task.Run(() => Douyin.Feed.SaveSelfSecUid(ownUid, secUid));
        }

        await LoadProfileAsync(gen);
    }

    /// <summary>回到已加载的主页：资料重取一发，当前档补第一页里没见过的条目（增量），已翻的页不动。</summary>
    public Task RefreshAsync() => _secUid.Length == 0 ? Task.CompletedTask : LoadProfileAsync(_gen);

    /// <summary>回到本人主页/主题变化时只重算本机使用热力图，不碰已加载的主页数据。</summary>
    public void RefreshUsageHeatmap() => _ = Heatmap.RefreshAsync();

    /// <summary>
    /// 把硬盘快照铺到当前档：资料先顶上，当前档的行直接进场（封面位图仍走网络，但骨架与文字立刻在）。
    /// 只铺"还没加载过"的档——已经翻了更深的页就别拿旧快照盖回去。
    /// </summary>
    private void ApplyCache(ProfileSnapshot cache)
    {
        _cache = cache;
        if (cache.Profile is not null)
        {
            _profile = cache.Profile;
        }

        HydrateFromCache(CurrentState);
    }

    private bool HydrateFromCache(FeedState state)
    {
        if (state.Loaded || state.CacheKey.Length == 0 || _cache is null
            || !_cache.States.TryGetValue(state.CacheKey, out FeedSnapshot? snap))
        {
            return false;
        }

        foreach (VideoCard card in snap.Cards)
        {
            if (card.Id.Length == 0 || !state.Ids.Add(card.Id))
            {
                continue;
            }

            state.Posts.Add(new UserProfilePostRow(card, snap.WatchedAt.GetValueOrDefault(card.Id)));
        }

        foreach (MixCard mix in snap.Mixes)
        {
            if (mix.Id.Length == 0 || !state.Ids.Add(mix.Id))
            {
                continue;
            }

            state.Mixes.Add(new UserProfileMixRow(mix));
        }

        foreach (SeriesCard one in snap.Series)
        {
            if (one.Id.Length == 0 || !state.Ids.Add(one.Id))
            {
                continue;
            }

            state.Mixes.Add(new UserProfileMixRow(one));
        }

        foreach (MusicCard music in snap.Musics)
        {
            if (music.Id.Length == 0 || !state.Ids.Add(music.Id))
            {
                continue;
            }

            state.Musics.Add(new UserProfileMusicRow(music));
        }

        if (ReferenceEquals(state, _foldersState))
        {
            _folders.Clear();
            foreach (CollectFolder folder in snap.Folders)
            {
                if (folder.Id.Length == 0 || !state.Ids.Add(folder.Id))
                {
                    continue;
                }

                _folders.Add(new UserProfileCollectFolderRow(folder));
            }
        }

        if (ReferenceEquals(state, _works) && snap.Months.Count > 0)
        {
            _months = [.. snap.Months];
        }

        state.Cursor = snap.Cursor;
        state.HasMore = snap.HasMore;
        state.Loaded = true;
        return true;
    }

    /// <summary>
    /// 把已加载的各档连同资料写成快照。超过上限的档不存（截断会跟游标对不上，留下看不到的空洞）。
    /// 组装必须在 UI 线程（要读绑定集合），序列化与写盘在线程池。
    /// </summary>
    private void SaveCache()
    {
        if (_secUid.Length == 0)
        {
            return;
        }

        ProfileSnapshot file = BuildCacheFile();
        _ = Task.Run(() => Douyin.Feed.SaveProfileSnapshot(file));
    }

    private ProfileSnapshot BuildCacheFile()
    {
        var file = new ProfileSnapshot { SecUid = _secUid, Profile = _profile };
        foreach (FeedState state in AllStates())
        {
            if (!state.Loaded || state.CacheKey.Length == 0)
            {
                continue;
            }

            int rows = state.Posts.Count + state.Mixes.Count + state.Musics.Count;
            if (ReferenceEquals(state, _foldersState))
            {
                rows = _folders.Count;
            }

            if (rows == 0 || rows > Douyin.Feed.MaxProfileCacheRows)
            {
                continue;   // 空档没有可铺的；超上限的档宁可下次重取
            }

            var snap = new FeedSnapshot { Cursor = state.Cursor, HasMore = state.HasMore };
            snap.Cards.AddRange(state.Posts.Select(static r => r.Card));
            foreach (UserProfilePostRow row in state.Posts)
            {
                if (row.WatchedAtMs > 0)
                {
                    snap.WatchedAt[row.Id] = row.WatchedAtMs;
                }
            }

            snap.Mixes.AddRange(state.Mixes.Select(static r => r.MixSource).OfType<MixCard>());
            snap.Series.AddRange(state.Mixes.Select(static r => r.SeriesSource).OfType<SeriesCard>());
            snap.Musics.AddRange(state.Musics.Select(static r => r.Music));
            if (ReferenceEquals(state, _foldersState))
            {
                snap.Folders.AddRange(_folders.Select(static r => r.Folder));
            }

            if (ReferenceEquals(state, _works))
            {
                snap.Months.AddRange(_months);
            }

            file.States[state.CacheKey] = snap;
        }

        return file;
    }

    private IEnumerable<FeedState> AllStates()
    {
        yield return _works;
        yield return _likes;
        yield return _history;
        yield return _mix;
        yield return _series;
        yield return _favorite;
        yield return _foldersState;
        yield return _folder;
        yield return _favMusic;
        yield return _favMix;
        yield return _favSeries;
    }

    /// <summary>把各档账面与头部整体清回未加载态；他人/本人都从这里起步，免得两套重置逻辑漂移。</summary>
    private void Reset(string secUid)
    {
        _secUid = secUid.Trim();
        _gen++;
        _profile = null;
        _cache = null;
        _folder.CacheKey = "folder";
        _tab = UserProfileTab.Works;
        _section = UserProfileSection.Posts;
        _favoriteSection = UserFavoriteSection.Folders;
        _openedFolder = null;
        _months = [];
        _selfLoginRequired = false;
        foreach (FeedState state in new[]
        {
            _works, _likes, _history, _favorite, _foldersState, _folder, _mix, _series,
            _favMusic, _favMix, _favSeries,
        })
        {
            state.Reset();
        }

        _folders.Clear();
        _status = string.Empty;
        NotifyEverything();
    }

    /// <summary>换一级页签：推荐那档不取数（官方也不发请求）；别的档先吃硬盘快照，再补第一页增量。</summary>
    public Task SelectTabAsync(UserProfileTab tab)
    {
        if (tab == _tab)
        {
            return Task.CompletedTask;
        }

        _tab = tab;
        _gen++;
        _status = string.Empty;
        NotifyEverything();
        return EnsureFeedAsync();
    }

    /// <summary>换二级页签（作品 / 合集 / 短剧）：每档各有一份账，切回已加载的档不重取。</summary>
    public Task SelectSectionAsync(UserProfileSection section)
    {
        if (section == _section || _tab != UserProfileTab.Works)
        {
            return Task.CompletedTask;
        }

        _section = section;
        _gen++;
        _status = string.Empty;
        NotifyEverything();
        return EnsureFeedAsync();
    }

    /// <summary>滚到底续页：按当前档的游标往前。</summary>
    public Task LoadMoreAsync() => NeedsFetch && HasMore && !_loading ? LoadNextPageAsync(_gen) : Task.CompletedTask;

    /// <summary>
    /// 「收藏」下的二级页签（五档各有一份账）：切回已加载的档不重取。
    /// 切档顺手把点开的收藏夹收回去——官方也是这个行为（换页签就回到夹列表）。
    /// </summary>
    public Task SelectFavoriteSectionAsync(UserFavoriteSection section)
    {
        if (section == _favoriteSection && _openedFolder is null)
        {
            return Task.CompletedTask;
        }

        _favoriteSection = section;
        _openedFolder = null;
        _gen++;
        _status = string.Empty;
        NotifyEverything();
        return EnsureFeedAsync();
    }

    /// <summary>点开一个收藏夹：换到那一档的账上从头取（夹内列表按 cursor 递增，实测 0/10/20…）。</summary>
    public Task OpenFolderAsync(UserProfileCollectFolderRow row)
    {
        _openedFolder = row.Folder;
        _favoriteSection = UserFavoriteSection.Folders;
        _folder.CacheKey = "folder:" + row.Folder.Id;
        _folder.Reset();
        _gen++;
        _status = string.Empty;
        bool hydrated = HydrateFromCache(_folder);
        NotifyEverything();
        return LoadNextPageAsync(_gen, topRefresh: hydrated);
    }

    /// <summary>从夹内列表退回收藏夹格子：账面还在，不重取。</summary>
    public void CloseFolder()
    {
        if (_openedFolder is null)
        {
            return;
        }

        _openedFolder = null;
        _gen++;
        _status = string.Empty;
        NotifyEverything();
    }

    /// <summary>当前档要不要真去取数（推荐档永远不取，喜欢档只在公开时取，历史与收藏只读自己的）。</summary>
    private bool NeedsFetch => _tab switch
    {
        UserProfileTab.Recommend => false,
        UserProfileTab.Likes => _profile?.ShowFavoriteList == true,
        UserProfileTab.History => _selfMode || IsSelf,
        UserProfileTab.Favorite => _selfMode || IsSelf,
        _ => true,
    };

    /// <summary>
    /// 切到某一档：没加载过就先吃硬盘快照；吃过快照的顺手补一发第一页增量，
    /// 连快照都没有才去取第一页。资料还没回来时先不动——LoadProfileAsync 收尾会按当前档取。
    /// </summary>
    private Task EnsureFeedAsync()
    {
        if (!NeedsFetch || CurrentState.Loaded)
        {
            return Task.CompletedTask;
        }

        bool hydrated = HydrateFromCache(CurrentState);
        if (hydrated)
        {
            NotifyEverything();
        }

        if (_profile is null)
        {
            return Task.CompletedTask;
        }

        return LoadNextPageAsync(_gen, topRefresh: hydrated);
    }

    private async Task LoadProfileAsync(int gen)
    {
        UserProfile? profile = null;
        string error = string.Empty;
        try
        {
            profile = await Task.Run(() => Douyin.Feed.UserProfileOtherAsync(_secUid, CancellationToken.None));
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (gen != _gen)
        {
            return;
        }

        if (profile is not null && profile.SecUid.Length > 0 && !string.Equals(profile.SecUid, _secUid, StringComparison.Ordinal))
        {
            // 回包给的 sec_uid 与请求的不是同一个人（跳转/失效会话都可能出现）：认回包，免得头部与列表对不上
            _secUid = profile.SecUid;
        }

        _profile = profile;
        if (error.Length > 0)
        {
            _status = "主页资料没读回来：" + error;
        }

        NotifyEverything();
        if (CurrentState.Loaded)
        {
            // 缓存已经把这一档铺出来了：只补第一页里没见过的条目（增量），已翻的页与游标不动
            await LoadNextPageAsync(gen, topRefresh: true);
        }
        else if (NeedsFetch)
        {
            await LoadNextPageAsync(gen);
        }
        else
        {
            SaveCache();   // 这一档不取数（推荐 / 未公开的喜欢）：至少把资料存下来
        }
    }

    /// <summary>
    /// 取一页。默认从这一档的游标往后接；<paramref name="topRefresh"/>＝只取第一页（cursor 0），
    /// 把缓存里没见过的条目插到最前——用于"进页时吃缓存 + 增量"。
    /// </summary>
    private async Task LoadNextPageAsync(int gen, bool topRefresh = false)
    {
        await _loadLock.WaitAsync();
        try
        {
            if (gen != _gen || !NeedsFetch)
            {
                return;
            }

            _loading = true;
            Notify(nameof(IsLoading));
            Notify(nameof(LoadingVisibility));
            Notify(nameof(StatusText));

            FeedState state = CurrentState;
            long requestCursor = topRefresh ? 0 : state.Cursor;

            // 收藏夹格子：collects/list 一发取全（NativeFeed 内部已把 5 页翻完并缓存），不参与续页
            if (_tab == UserProfileTab.Favorite && _favoriteSection == UserFavoriteSection.Folders && _openedFolder is null)
            {
                await LoadCollectFoldersAsync(state, gen, force: topRefresh);
                return;
            }

            bool favMixSection = IsFavoriteTab
                && _favoriteSection is UserFavoriteSection.Compilation or UserFavoriteSection.Playlet;
            bool musicMode = IsFavoriteTab && _favoriteSection == UserFavoriteSection.Music;
            bool mixMode = (_tab == UserProfileTab.Works && _section != UserProfileSection.Posts) || favMixSection;
            IReadOnlyList<VideoCard> cards = [];
            IReadOnlyDictionary<string, long> watchedAt = EmptyWatchedAt;
            IReadOnlyList<MixCard> mixes = [];
            IReadOnlyList<SeriesCard> series = [];
            IReadOnlyList<MusicCard> musics = [];
            long nextCursor = 0;
            bool nextHasMore = false;
            IReadOnlyList<string> months = [];
            string error = string.Empty;
            try
            {
                if (musicMode)
                {
                    MusicPage page = await Task.Run(() => Douyin.Feed.FavoriteMusicsAsync(
                        requestCursor, MusicPageSize, CancellationToken.None));
                    musics = page.Items;
                    nextCursor = page.Cursor;
                    nextHasMore = page.HasMore;
                }
                else if (mixMode)
                {
                    // 主页那两档带 sec_user_id、走 mix/list 与 series/list；收藏那两档不带、走 mix/listcollection
                    // 与 series/collections。count 也不同（官方 12 对 18），回包数组键同形所以一份解析、一张卡模板。
                    MixListPage page = favMixSection
                        ? await Task.Run(() => Douyin.Feed.FavoriteMixPageAsync(
                            _favoriteSection == UserFavoriteSection.Playlet ? "series" : "mix",
                            requestCursor, CollectionPageSize, CancellationToken.None))
                        : await Task.Run(() => Douyin.Feed.UserMixPageAsync(
                            _section == UserProfileSection.Series ? "series" : "mix",
                            _secUid, requestCursor, PageSize, CancellationToken.None));
                    mixes = page.Mixes;
                    series = page.Series;
                    nextCursor = page.Cursor;
                    nextHasMore = page.HasMore;
                }
                else if (_tab == UserProfileTab.Likes)
                {
                    UserPostPage page = await Task.Run(() => Douyin.Feed.UserFavoritesAsync(
                        _secUid, requestCursor, PageSize, CancellationToken.None));
                    cards = page.Items;
                    nextCursor = page.MaxCursor;
                    nextHasMore = page.HasMore;
                }
                else if (_tab == UserProfileTab.History)
                {
                    // 历史接口一页最多 20 条（count=50 回空页），别跟作品一样用 18。
                    WatchHistoryPage page = await Task.Run(() => Douyin.Feed.HistoryPageAsync(
                        requestCursor, HistoryPageSize, CancellationToken.None));
                    cards = page.Items;
                    watchedAt = page.WatchedAt;
                    nextCursor = page.MaxCursor;
                    nextHasMore = page.HasMore;
                }
                else if (_tab == UserProfileTab.Favorite)
                {
                    // 点开夹就走 collects/video/list（游标是已取条数 0/10/20…），
                    // 「视频」档走 aweme/listcollection（游标是时间戳形态），两档都原样回传上一页给的 cursor。
                    string folderId = _openedFolder?.Id ?? string.Empty;
                    CollectedPage page = await Task.Run(() => folderId.Length > 0
                        ? Douyin.Feed.CollectsVideosAsync(folderId, requestCursor, FavoritePageSize, CancellationToken.None)
                        : Douyin.Feed.CollectedVideosAsync(requestCursor, FavoritePageSize, CancellationToken.None));
                    cards = page.Items;
                    nextCursor = page.Cursor;
                    nextHasMore = page.HasMore;
                }
                else
                {
                    // 本人视角要走网页版「我」的那套页面参数（from_user_page=0），私密作品才会一起回来；
                    // 他人视角仍用 from_user_page=1，免得把"私密作品不可见"的边界混掉。
                    bool fromSelf = _selfMode || IsSelf;
                    UserPostPage page = await Task.Run(() => Douyin.Feed.UserPostsAsync(
                        _secUid, requestCursor, PageSize, CancellationToken.None, fromSelf));
                    cards = page.Items;
                    months = [.. page.Months ?? []];
                    nextCursor = page.MaxCursor;
                    nextHasMore = page.HasMore;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (gen != _gen)
            {
                return;   // 期间又切了页签/档：这一发作废（loading 由 finally 放掉）
            }

            if (error.Length > 0)
            {
                state.HasMore = false;
                _status = "内容没读回来：" + error;
                NotifyEverything();
                return;
            }

            if (musicMode)
            {
                int added = 0;
                int insertAt = 0;
                foreach (MusicCard one in musics)
                {
                    if (state.Musics.Count >= MaxRows)
                    {
                        break;
                    }

                    if (one.Id.Length == 0 || !state.Ids.Add(one.Id))
                    {
                        continue;
                    }

                    var row = new UserProfileMusicRow(one);
                    if (topRefresh)
                    {
                        state.Musics.Insert(insertAt++, row);
                    }
                    else
                    {
                        state.Musics.Add(row);
                    }

                    added++;
                }

                if (!topRefresh)
                {
                    state.Cursor = nextCursor;
                    // 续页判据用「这一发真加进了行」而不是回包条数：这档的游标形态没像作品那样确认可推进，
                    // 万一它每次都回同一批，去重后加不进新行就该就地收住，而不是让滚到底一直空转重发。
                    state.HasMore = nextHasMore && added > 0 && state.Musics.Count < MaxRows;
                }
            }
            else if (mixMode)
            {
                int insertAt = 0;
                foreach (MixCard mix in mixes)
                {
                    if (state.Mixes.Count >= MaxRows)
                    {
                        break;
                    }

                    if (mix.Id.Length == 0 || !state.Ids.Add(mix.Id))
                    {
                        continue;
                    }

                    var row = new UserProfileMixRow(mix);
                    if (topRefresh)
                    {
                        state.Mixes.Insert(insertAt++, row);
                    }
                    else
                    {
                        state.Mixes.Add(row);
                    }
                }

                foreach (SeriesCard one in series)
                {
                    if (state.Mixes.Count >= MaxRows)
                    {
                        break;
                    }

                    if (one.Id.Length == 0 || !state.Ids.Add(one.Id))
                    {
                        continue;
                    }

                    var row = new UserProfileMixRow(one);
                    if (topRefresh)
                    {
                        state.Mixes.Insert(insertAt++, row);
                    }
                    else
                    {
                        state.Mixes.Add(row);
                    }
                }

                if (!topRefresh)
                {
                    state.Cursor = nextCursor;
                    state.HasMore = nextHasMore && (mixes.Count > 0 || series.Count > 0) && state.Mixes.Count < MaxRows;
                }
            }
            else
            {
                int insertAt = 0;
                foreach (VideoCard card in cards)
                {
                    if (state.Posts.Count >= MaxRows)
                    {
                        break;
                    }

                    if (card.Id.Length == 0 || !state.Ids.Add(card.Id))
                    {
                        continue;
                    }

                    long watched = watchedAt.TryGetValue(card.Id, out long ms) ? ms : 0;
                    var row = new UserProfilePostRow(card, watched);
                    if (topRefresh)
                    {
                        state.Posts.Insert(insertAt++, row);
                    }
                    else
                    {
                        state.Posts.Add(row);
                    }
                }

                if (!topRefresh)
                {
                    state.Cursor = nextCursor;
                    state.HasMore = nextHasMore && cards.Count > 0 && state.Posts.Count < MaxRows;
                }

                if (months.Count > 0)
                {
                    _months = months;
                }
            }

            state.Loaded = true;
            bool capped = (musicMode ? state.Musics.Count : mixMode ? state.Mixes.Count : state.Posts.Count) >= MaxRows;
            // 空档不在这里说话：走 EmptyHintText 那行占位（跟着账面派生），这一行只报上限与错误。
            _status = capped ? "只显示最近 " + MaxRows + " 条（再多会一直往内存里堆）" : string.Empty;

            SaveCache();
            NotifyEverything();
        }
        finally
        {
            // 发次作废（切了页签/档）时也要把 loading 放掉：否则旧请求一作废，
            // _loading 永远停在 true，新档的续页就再也发不出去。
            _loading = false;
            Notify(nameof(IsLoading));
            Notify(nameof(LoadingVisibility));
            _loadLock.Release();
        }
    }

    /// <summary>
    /// 收藏夹格子：一发 <c>collects/list</c> 取全（这一族启动就预取过，正常是吃缓存）。
    /// 取完把 HasMore 钉成 false——格子不分页，滚到底也不该再发；84 个夹的卡都在内存里，
    /// 只有一张封面位图，不像视频卡那样按 500 行封顶。
    /// </summary>
    private async Task LoadCollectFoldersAsync(FeedState state, int gen, bool force)
    {
        IReadOnlyList<CollectFolder> folders = [];
        string error = string.Empty;
        try
        {
            folders = await Task.Run(() => Douyin.Feed.CollectsFoldersAsync(force, CancellationToken.None));
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (gen != _gen)
        {
            return;
        }

        state.Loaded = true;
        state.HasMore = false;
        if (error.Length > 0)
        {
            _status = "收藏夹没读回来：" + error;
            NotifyEverything();
            return;
        }

        if (force)
        {
            // 增量那一发要按服务端最新的夹列表整体换一遍（夹可能改名、增减）
            state.Ids.Clear();
            _folders.Clear();
        }

        if (_folders.Count == 0)
        {
            foreach (CollectFolder one in folders)
            {
                if (one.Id.Length == 0 || !state.Ids.Add(one.Id))
                {
                    continue;
                }

                _folders.Add(new UserProfileCollectFolderRow(one));
            }
        }

        _status = string.Empty;
        SaveCache();
        NotifyEverything();
    }

    private static BitmapImage Decode(string url, int decodeWidth)
    {
        var bitmap = new BitmapImage { DecodePixelWidth = decodeWidth };
        bitmap.UriSource = new Uri(url);
        return bitmap;
    }

    /// <summary>主页的账面都从一份资料与几个游标派生，翻一处就整串通知——比一个个点名少漏。</summary>
    private void NotifyEverything()
    {
        // 一轮通知是进页/切档的重活：超过一帧就落一笔，下次再听"卡"时有据可查。
        long start = Environment.TickCount64;
        foreach (string name in Names)
        {
            Notify(name);
        }

        long cost = Environment.TickCount64 - start;
        if (cost >= 16)
        {
            MfLog.Write("profile: 一轮属性通知 " + cost + "ms");
        }
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>要通知的属性名一次算好，免得每回翻页都去反射一遍。</summary>
    private static readonly string[] Names =
    [
        nameof(Nickname), nameof(VerifyLabel), nameof(VerifyVisibility),
        nameof(FollowCountText), nameof(FollowerCountText), nameof(FavoritedCountText),
        nameof(StatsVisibility),
        nameof(StatsEntryVisibility), nameof(StatsTextVisibility),
        nameof(HandleLine), nameof(HandleVisibility), nameof(IpLine), nameof(IpVisibility),
        nameof(RegionLine), nameof(RegionVisibility), nameof(SignatureText), nameof(SignatureVisibility),
        nameof(SignatureToggleVisibility), nameof(SignatureToggleLabel), nameof(SignatureMaxLines),
        nameof(WorksHeaderLine), nameof(IsLivingVisibility),
        nameof(AvatarImage), nameof(CoverImage), nameof(CoverVisibility), nameof(Initials),
        nameof(IsFollowing), nameof(FollowButtonVisibility), nameof(FollowingButtonVisibility),
        nameof(Tab), nameof(Section), nameof(WorksCountText), nameof(WorksCountVisibility),
        nameof(MixSectionVisibility), nameof(SeriesSectionVisibility),
        nameof(IsWorksTab), nameof(IsRecommendTab), nameof(IsLikesTab), nameof(IsHistoryTab), nameof(IsFavoriteTab),
        nameof(SelfOnlyTabVisibility), nameof(FavoriteSectionsVisibility), nameof(FolderGridVisibility),
        nameof(FolderHeaderVisibility), nameof(OpenedFolderName), nameof(OpenedFolderCountText),
        nameof(ListAreaVisibility), nameof(RecommendNotice), nameof(RecommendNoticeVisibility),
        nameof(LikesNotice), nameof(LikesNoticeVisibility),
        nameof(CardsVisibility), nameof(PostsViewVisibility), nameof(MixViewVisibility), nameof(MusicViewVisibility),
        nameof(Posts), nameof(Mixes), nameof(Musics), nameof(CollectFolders),
        nameof(HasMore), nameof(IsLoading), nameof(LoadingVisibility), nameof(StatusText), nameof(Months),
        nameof(EmptyHintText), nameof(EmptyHintVisibility),
        nameof(IsSelfProfile), nameof(HeatmapVisibility), nameof(PageTitle),
        nameof(SelfLoginGateVisibility), nameof(SelfProfileBodyVisibility),
    ];
}
