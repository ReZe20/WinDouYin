namespace WinDouYin.Contracts;

// 这一文件是界面侧唯一认得的"数据形状"。字段名与顺序和取数层一一对上，但**不含任何线上知识**：
// 哪个字段来自报文哪一格、哪种游标形式、哪种参数是服务端要的，全都留在取数层里。

/// <summary>一条作品（视频/图文/合集首集都用它）。<see cref="Kind"/> 用来分档："video" / "imagepost" / …</summary>
public sealed record VideoCard(
    string Id, string Kind, string Desc, string Author, string Cover, string Play, string Stats, long CreateTime,
    string SecUid, string AuthorId, string Avatar, int Digg, int Comment, int Collect, int Share, int Width, int Height,
    VideoQualitySet Qualities, string Music = "", bool Pinned = false, int DurationMs = 0, string FriendRecommender = "");

/// <summary>一张视频分享卡（私信里发的那种），以及它的作者格。</summary>
public sealed record ShareCard(
    string Id, string Title, string Cover, int Width, int Height, string AuthorId, string AuthorAvatar);

/// <summary>"直接打开这条"的结果：拿得到的播放地址 + 头几条评论 + 一句话说明。</summary>
public sealed record OpenResult(string Id, string Desc, string Play, string Stats, IReadOnlyList<string> Comments, string Note);

/// <summary>一个人（关注/粉丝/搜索用户页签同形）。<see cref="FollowStatus"/> 是服务端原值：1=我关注 TA，2=TA 关注我，3=互关。</summary>
public sealed record UserInfo(
    string Uid, string SecUid, string Nickname, string Avatar, string Signature,
    int FollowerCount, int FollowingCount, int AwemeCount, int FollowStatus, string Handle,
    int TotalFavorited = 0);

/// <summary>一页人。翻页把 <see cref="NextMaxTime"/> 原样当下一页的时间游标传回去。</summary>
public sealed record UserInfoPage(IReadOnlyList<UserInfo> Items, long NextMaxTime, bool HasMore, int Total);

/// <summary>某个用户的一页作品，另带回包自带的月份表（主页「日期筛选」的数据源）。</summary>
public sealed record UserPostPage(IReadOnlyList<VideoCard> Items, long MaxCursor, bool HasMore, IReadOnlyList<string>? Months = null);

/// <summary>他人主页的资料卡。IP 属地服务端已带前缀；年龄是纯数字，「岁」由界面补。</summary>
public sealed record UserProfile(
    string Uid, string SecUid, string Nickname, string Avatar, string Handle, string Signature,
    string IpLocation, string Age, string Area, string Cover, string CoverColor, string VerifyLabel,
    int FollowerCount, int FollowingCount, int AwemeCount, int TotalFavorited, int FollowStatus,
    bool ShowFavoriteList, bool IsMixUser, int MixCount, bool IsSeriesUser, int SeriesCount,
    bool IsLiving, long RoomId);

/// <summary>登录态下"我是谁"。</summary>
public sealed record OwnProfile(string Nickname, string AvatarUrl, string Note);

public enum SignInStatus
{
    Idle,
    /// <summary>码已给出，等手机扫。</summary>
    Waiting,
    /// <summary>手机上已扫，等用户在 App 里点确认。</summary>
    Scanned,
    /// <summary>确认完成，会话已落进罐子。</summary>
    Confirmed,
    /// <summary>服务端换了新码，图要重画。</summary>
    Refreshed,
    Expired,
    /// <summary>被风控/参数不过，带着一句原文。</summary>
    Refused,
    /// <summary>没见过的状态：继续轮，别当失败收场。</summary>
    Unknown,
}

/// <summary>扫码这一拍。<see cref="Token"/> 是这一张码的身份（轮询要带回）；<see cref="ErrorCode"/> 是「太频繁」那类要退避的码。</summary>
public sealed record SignInTicket(SignInStatus Status, string Token, byte[]? Png, string Note, int ErrorCode = 0);

/// <summary>一个收藏夹。<see cref="Count"/> 是夹内作品数。</summary>
public sealed record CollectFolder(string Id, string Name, int Count, string Cover = "");

/// <summary>一页收藏夹。</summary>
public sealed record FolderPage(IReadOnlyList<CollectFolder> Items, long Cursor, bool HasMore);

/// <summary>收藏的一页作品（夹内或全账号平铺）。</summary>
public sealed record CollectedPage(IReadOnlyList<VideoCard> Items, long Cursor, bool HasMore);

/// <summary>一页观看历史。<see cref="WatchedAt"/> 是作品 id → 观看时刻（毫秒），与发布时间不是一回事。</summary>
public sealed record WatchHistoryPage(
    IReadOnlyList<VideoCard> Items, IReadOnlyDictionary<string, long> WatchedAt, long MaxCursor, bool HasMore);

/// <summary>主页「合集」卡：<see cref="UpdatedToEpisode"/> 是更到第几集，<see cref="PlayCount"/> 是热度。</summary>
public sealed record MixCard(string Id, string Name, string Desc, string Cover, int PlayCount, int UpdatedToEpisode, long UpdateTime);

/// <summary>主页「短剧」卡（字段与合集同构，没有正经样本前先按空处理）。</summary>
public sealed record SeriesCard(string Id, string Name, string Desc, string Cover, int EpisodeCount, long UpdateTime);

/// <summary>一页合集/短剧。两档各管一侧，用不上的那侧给空表。</summary>
public sealed record MixListPage(IReadOnlyList<MixCard> Mixes, IReadOnlyList<SeriesCard> Series, long Cursor, bool HasMore, int Total);

/// <summary>合集二级页的资料。</summary>
public sealed record MixDetail(
    string Id, string Name, string Desc, string Cover, int PlayCount, int EpisodeCount,
    long CreateTime, long UpdateTime, string Author, string AuthorSecUid, string AuthorAvatar);

/// <summary>合集里的一集：卡片 + 服务端给的集序号。</summary>
public sealed record MixEpisode(VideoCard Card, int Number);

/// <summary>合集逐集一页。</summary>
public sealed record MixEpisodePage(IReadOnlyList<MixEpisode> Items, long Cursor, bool HasMore);

/// <summary>「喜欢 → 音乐」一条。</summary>
public sealed record MusicCard(string Id, string Title, string Author, string Cover);

/// <summary>收藏音乐一页（实测一发取全，字段仍在）。</summary>
public sealed record MusicPage(IReadOnlyList<MusicCard> Items, long Cursor, bool HasMore);

/// <summary>热搜榜上的一条词。</summary>
public sealed record HotWord(string Word, int HotValue);

/// <summary>搜索页签。直播那档服务端有回包，但应用还没有直播播放。</summary>
public enum SearchKind
{
    General,
    Video,
    User,
    Live,
}

/// <summary>一次搜索：页签 + 词 + 续页游标 + 筛选 + 多列/单列。筛选两组值的编码分流由取数层处理。</summary>
public sealed record SearchQuery(
    SearchKind Kind, string Keyword, long Offset, int Count,
    string SearchId = "", int SortType = 0, int PublishTime = 0, bool MultiColumn = true);

/// <summary>
/// 一页关键词搜索。<see cref="SearchId"/> 必须原样带回下一发，否则第二页会被挡（那时 <see cref="NilType"/> 说清了原因）。
/// <see cref="GuideWords"/> 是官方第二行那排相关词。
/// </summary>
public sealed record SearchVideoPage(
    IReadOnlyList<VideoCard> Items, long Cursor, bool HasMore, string SearchId,
    IReadOnlyList<string> GuideWords = default!, string NilType = "");

/// <summary>一页「用户」页签结果。</summary>
public sealed record SearchUserPage(IReadOnlyList<UserInfo> Items, long Cursor, bool HasMore, string SearchId, string NilType = "");

/// <summary>精选卡：一条作品 + "作者是不是我关注的"（官方卡上那枚绿标）。</summary>
public sealed record FeaturedCard(VideoCard Card, bool Followed);

/// <summary>精选一页。<see cref="StatusCode"/> 用服务端原值；<see cref="Note"/> 给空态/被挡时的一句话。</summary>
public sealed record FeaturedFeedPage(IReadOnlyList<FeaturedCard> Items, bool HasMore, int StatusCode, string Note);

/// <summary>关注流里的一条直播（只进列表，不进舞台）。</summary>
public sealed record LiveCard(
    string RoomId, string Title, string Uid, string SecUid, string Nickname, string Avatar, int UserCount);

/// <summary>关注流一页：视频与直播分开摊。</summary>
public sealed record FollowFeedPage(
    IReadOnlyList<VideoCard> Videos, IReadOnlyList<LiveCard> Lives,
    long NextCursor, bool HasMore, int StatusCode, string Note);

/// <summary>朋友流一页（推荐人已摊进 <see cref="VideoCard.FriendRecommender"/>）。</summary>
public sealed record FamiliarFeedPage(
    IReadOnlyList<VideoCard> Videos, long NextCursor, bool HasMore, int StatusCode, string Note);

/// <summary>私信的一个会话。<see cref="Ticket"/> 与 <see cref="LastSortUs"/> 是发下一条与拉历史要带的东西。</summary>
public sealed record ImConversation(
    string Id, long ShortId, int Type, string Title, string AvatarUrl, string Preview,
    long LastTimeMs, long LastSortUs, string Ticket, int Unread, string PeerUid, string PeerSecUid);

/// <summary>一条私信。<see cref="Content"/> 是原始内容串（类型不同形状不同，由界面/摘要映射去解）；
/// Refer* 是引用回复那几格，<see cref="ReferServerId"/>/<see cref="ReferSortUs"/> 用来点引用跳回原位。</summary>
public sealed record ImMessage(
    string ConversationId, long ServerId, int Type, int MessageType, string SenderUid, string SenderSecUid,
    string Content, long TimeMs, long SortUs, long Seq, string ClientMessageId, bool IsMine,
    int ReferType = 0, string ReferText = "", string ReferSenderUid = "",
    string ReferSenderSecUid = "", string ReferSenderName = "",
    long ReferServerId = 0, long ReferSortUs = 0);

/// <summary>一页历史消息 + 下一页游标（这一页最老那条的排序时间）。</summary>
public sealed record ImMessagePage(IReadOnlyList<ImMessage> Items, long MinSortUs, bool HasMore);

/// <summary>私信对象资料。备注优先上屏。</summary>
public sealed record ImContact(string Uid, string SecUid, string Nickname, string RemarkName, string AvatarUrl)
{
    public string DisplayName => RemarkName.Length > 0 ? RemarkName : Nickname.Length > 0 ? Nickname : "抖音用户";
}

/// <summary>表情包资源。发送用静图（<see cref="Url"/>），<see cref="Animated"/> 只用来标"官方那份是动图"。</summary>
public sealed record ImSticker(
    string Id, string DisplayName, string Uri, IReadOnlyList<string> UrlList, int Width, int Height, bool Animated)
{
    public string Url => UrlList.Count > 0 ? UrlList[0] : string.Empty;
}

/// <summary>一页表情搜索结果（热门/收藏只有首页，不影响）。</summary>
public sealed record ImStickerPage(IReadOnlyList<ImSticker> Items, long NextCursor, bool HasMore);

/// <summary>
/// 一枚会话 cookie 的形状。它就是浏览器/WebView2 里那一格，不含任何站点知识；
/// 登录那趟要把采到的 cookie 交给取数层，靠它搬运。
/// </summary>
public readonly record struct SessionCookie(string Name, string Value, string Domain, string Path, long ExpiresUnix);

/// <summary>交接/切号那一趟的结果。<see cref="Expired"/> = 服务端明说这个号没登着，档案已抹掉；
/// <see cref="Failed"/> = 问不出结论（网络断了/回包不像 JSON），会话回滚到上一个号。</summary>
public enum AccountOutcome
{
    Success,
    Expired,
    Failed,
}

/// <summary>切号/加号的回执。<see cref="Uid"/> 与 <see cref="Nickname"/> 只在 <c>Success</c> 时有意义。</summary>
public sealed record AccountResult(AccountOutcome Kind, string Uid, string Nickname, string Message);

/// <summary>
/// 个人主页的一份本地快照：资料 + 各档已取到的那一段。它是**盘上的形状**而不是报文形状——
/// 字段名沿用旧档案，所以先前存的快照还能直接读。存与取都走取数门面（只有它知道该落在哪个目录）。
/// </summary>
public sealed class ProfileSnapshot
{
    public int Version { get; set; } = 1;

    public string SecUid { get; set; } = string.Empty;

    public long SavedAtMs { get; set; }

    public UserProfile? Profile { get; set; }

    /// <summary>按档名存的第一屏。档名由主页那边自己定，这里只当键。</summary>
    public Dictionary<string, FeedSnapshot> States { get; set; } = [];
}

/// <summary>快照里一档的行与游标；只填这一档用得上的那几个集合。</summary>
public sealed class FeedSnapshot
{
    public long Cursor { get; set; }

    public bool HasMore { get; set; }

    public List<VideoCard> Cards { get; set; } = [];

    public Dictionary<string, long> WatchedAt { get; set; } = [];

    public List<string> Months { get; set; } = [];

    public List<MixCard> Mixes { get; set; } = [];

    public List<SeriesCard> Series { get; set; } = [];

    public List<MusicCard> Musics { get; set; } = [];

    public List<CollectFolder> Folders { get; set; } = [];
}


/// <summary>
/// 存档里的一个号（只到"账号菜单要能列、能认当前是哪个"这一层）。
/// 那一整罐 cookie 与 guard 材料不在这里——它们留在取数层，界面拿不到也不该拿。
/// </summary>
public sealed record SavedAccountInfo(string Uid, string SecUid, string Nickname, string AvatarUrl, long LastUsedUnix);
