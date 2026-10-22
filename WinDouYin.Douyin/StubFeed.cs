using System.Net.Http;
using WinDouYin.Contracts;

namespace WinDouYin;

/// <summary>
/// 取数门面的**空实现**，只在没有闭源源码的构建里被编进来（公开仓 clone 出来的样子，或私有仓显式
/// <c>-p:DouyinBackend=Stub</c>）。用途写清楚，免得被当成能用：<b>它只证明开源那半不依赖任何闭源源码
/// 也能编译，并顺带把每一页的空态跑出来</b>。它不是测试替身，也不承诺任何线上行为——所有读都回空集合、
/// 所有写都回失败并带同一句话说明，登录、取流、guard 材料一律不工作。
///
/// 为什么用 <c>Exists('Native/NativeFeed.cs')</c> 而不是 <c>#if</c> 手工开关：这样档位由"源码在不在"决定，
/// 私有仓不用记得切，公开仓也不用记得设属性；两个仓编的是同一枚 csproj。
/// </summary>
public sealed class StubFeed : IDouyinFeed
{
    private const string Msg = "这套构建没带取数实现（后端源码不开源）：这一档只能空着。";

    public ISignIn SignIn { get; } = new StubSignIn();

    public string Session => string.Empty;

    public bool IsLoggedIn => false;

    /// <summary>没问过就是 null——别用 false 冒充"服务端说这号过期了"。</summary>
    public bool? LoginVerified => null;

    public string MyUid => string.Empty;

    public string MySecUid => string.Empty;

    public string MyAvatarUrl => string.Empty;

    public string CurrentAccountUid => string.Empty;

    public Task RefreshLoginStateAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<string> EnsureMyIdentityAsync(CancellationToken ct) => Task.FromResult(string.Empty);

    // ── 信息流与条目 ────────────────────────────────────────────────
    public Task<IReadOnlyList<VideoCard>> NextBurstAsync(IReadOnlyList<string> presentedIds, int fanout, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VideoCard>>([]);

    public Task<IReadOnlyList<VideoCard>> NextPageAsync(IReadOnlyList<string> presentedIds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VideoCard>>([]);

    public Task<VideoCard?> VideoDetailAsync(string id, CancellationToken ct) => Task.FromResult<VideoCard?>(null);

    public Task<(VideoCard? Card, bool TakenDown)> VideoDetailProbeAsync(string id, CancellationToken ct) =>
        Task.FromResult<(VideoCard?, bool)>((null, false));

    public Task<OpenResult> OpenAsync(string id, CancellationToken ct) =>
        Task.FromResult(new OpenResult(id, string.Empty, string.Empty, string.Empty, [], Msg));

    public Task<(string Url, string Note)> ShortLinkAsync(string awemeId, CancellationToken ct) =>
        Task.FromResult((string.Empty, Msg));

    public Task<FeaturedFeedPage> FeaturedFeedAsync(string tagId, int refreshIndex, CancellationToken ct = default) =>
        Task.FromResult(new FeaturedFeedPage([], false, 0, Msg));

    public Task<FollowFeedPage> FollowFeedAsync(long cursor, int pullType, CancellationToken ct = default) =>
        Task.FromResult(new FollowFeedPage([], [], 0, false, 0, Msg));

    public Task<FamiliarFeedPage> FamiliarFeedAsync(long cursor, int pullType, CancellationToken ct = default) =>
        Task.FromResult(new FamiliarFeedPage([], 0, false, 0, Msg));

    // ── 点赞 / 收藏 / 历史 ─────────────────────────────────────────
    public Task<(bool Ok, bool Liked, string Note)> LikeAsync(string awemeId, bool like, CancellationToken ct, bool flush = true) =>
        Task.FromResult((false, false, Msg));

    public Task<(bool Ok, bool Liked, string Note)> DiggStateAsync(string awemeId, CancellationToken ct) =>
        Task.FromResult((false, false, Msg));

    public Task<(bool Ok, string Note)> CollectAsync(string awemeId, bool collect, CancellationToken ct) =>
        Task.FromResult((false, Msg));

    public Task<IReadOnlyList<CollectFolder>> CollectsFoldersAsync(bool force, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<CollectFolder>>([]);

    public Task<(bool Ok, string Note)> CollectsMoveAsync(string awemeId, IReadOnlyList<CollectFolder> folders, CancellationToken ct) =>
        Task.FromResult((false, Msg));

    public Task<string> WatchLaterAddAsync(string awemeId, CancellationToken ct) => Task.FromResult(Msg);

    public Task<CollectedPage> CollectsVideosAsync(string collectsId, long cursor, int count, CancellationToken ct) =>
        Task.FromResult(new CollectedPage([], 0, false));

    public Task<CollectedPage> CollectedVideosAsync(long cursor, int count, CancellationToken ct) =>
        Task.FromResult(new CollectedPage([], 0, false));

    public Task<WatchHistoryPage> HistoryPageAsync(long maxCursor, int count, CancellationToken ct) =>
        Task.FromResult(new WatchHistoryPage([], new Dictionary<string, long>(), 0, false));

    public Task<(bool Ok, string Note)> HistoryWriteAsync(string awemeId, string authorId, string preItemId, CancellationToken ct) =>
        Task.FromResult((false, Msg));

    // ── 评论与弹幕 ─────────────────────────────────────────────────
    public Task<CommentPage> CommentsAsync(string awemeId, long cursor, int count, CancellationToken ct) =>
        Task.FromResult(new CommentPage([], 0, false, 0));

    public Task<ReplyPage> CommentRepliesAsync(string awemeId, string commentId, long cursor, int count, CancellationToken ct) =>
        Task.FromResult(new ReplyPage([], 0, false, 0, Ok: false));

    public Task<(bool Ok, string Note)> CommentDiggAsync(string awemeId, string cid, bool like, CancellationToken ct) =>
        Task.FromResult((false, Msg));

    public Task<(bool Ok, string Cid, string Note)> CommentPublishAsync(
        string awemeId, string text, CancellationToken ct, string replyId = "", string replyToId = "") =>
        Task.FromResult((false, string.Empty, Msg));

    public Task<(bool Ok, string Note)> CommentDeleteAsync(string awemeId, string cid, CancellationToken ct) =>
        Task.FromResult((false, Msg));

    public Task<DanmakuPage> DanmakuPageAsync(string awemeId, long startMs, CancellationToken ct) =>
        Task.FromResult(new DanmakuPage([], 0));

    public Task<(bool Ok, string Id, string Note)> DanmakuPostAsync(string awemeId, string text, long offsetMs, CancellationToken ct) =>
        Task.FromResult((false, string.Empty, Msg));

    public Task<(bool Ok, string Note)> DanmakuDiggAsync(string awemeId, string danmakuId, bool like, CancellationToken ct) =>
        Task.FromResult((false, Msg));

    public Task<(bool Ok, string Note)> DanmakuDeleteAsync(string danmakuId, CancellationToken ct) =>
        Task.FromResult((false, Msg));

    public Task<IReadOnlyList<EmojiItem>> EmojiListAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EmojiItem>>([]);

    // ── 人 / 主页 / 合集 ───────────────────────────────────────────
    public Task<UserInfoPage> FollowingAsync(long maxTime, int count, CancellationToken ct) =>
        Task.FromResult(new UserInfoPage([], 0, false, 0));

    public Task<UserInfoPage> FollowersAsync(long maxTime, int count, CancellationToken ct) =>
        Task.FromResult(new UserInfoPage([], 0, false, 0));

    public Task<UserInfoPage> FollowingSidebarAsync(long maxTime, int sourceType, int count, CancellationToken ct = default) =>
        Task.FromResult(new UserInfoPage([], 0, false, 0));

    public Task<(bool Ok, string Note)> FollowUserAsync(string userId, string secUserId, bool follow, CancellationToken ct) =>
        Task.FromResult((false, Msg));

    /// <summary>契约这里不给可空（界面按"必有一个资料对象"排的版），所以回一张全空的卡，字段一个都不填。</summary>
    public Task<UserProfile> UserProfileOtherAsync(string secUserId, CancellationToken ct) =>
        Task.FromResult(new UserProfile(
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, Msg,
            0, 0, 0, 0, 0, false, false, 0, false, 0, false, 0));

    public Task<UserPostPage> UserPostsAsync(string secUserId, long maxCursor, int count, CancellationToken ct, bool fromSelf = false) =>
        Task.FromResult(new UserPostPage([], 0, false));

    public Task<MixListPage> UserMixPageAsync(string kind, string secUserId, long cursor, int count, CancellationToken ct) =>
        Task.FromResult(new MixListPage([], [], 0, false, 0));

    public Task<MixListPage> FavoriteMixPageAsync(string kind, long cursor, int count, CancellationToken ct) =>
        Task.FromResult(new MixListPage([], [], 0, false, 0));

    public Task<MusicPage> FavoriteMusicsAsync(long cursor, int count, CancellationToken ct) =>
        Task.FromResult(new MusicPage([], 0, false));

    public Task<MixDetail> MixDetailAsync(string mixId, CancellationToken ct) =>
        Task.FromResult(new MixDetail(
            string.Empty, string.Empty, string.Empty, string.Empty, 0, 0, 0, 0, string.Empty, string.Empty, string.Empty));

    public Task<MixEpisodePage> MixEpisodesAsync(string mixId, long cursor, int count, CancellationToken ct) =>
        Task.FromResult(new MixEpisodePage([], 0, false));

    public Task<UserPostPage> UserFavoritesAsync(string secUserId, long maxCursor, int count, CancellationToken ct) =>
        Task.FromResult(new UserPostPage([], 0, false));

    // ── 搜索 ──────────────────────────────────────────────────────
    public Task<SearchVideoPage> SearchAsync(SearchQuery request, CancellationToken ct) =>
        Task.FromResult(new SearchVideoPage([], 0, false, string.Empty, [], Msg));

    public Task<SearchUserPage> SearchUsersAsync(SearchQuery request, CancellationToken ct) =>
        Task.FromResult(new SearchUserPage([], 0, false, string.Empty, Msg));

    public Task<IReadOnlyList<HotWord>> HotSearchAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<HotWord>>([]);

    public Task<IReadOnlyList<string>> GuessWordsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<string>> SugAsync(string keyword, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    // ── 私信 ──────────────────────────────────────────────────────
    public Task<IReadOnlyList<ImConversation>> ImConversationsAsync(bool force, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ImConversation>>([]);

    public Task<ImMessagePage> ImMessagesAsync(ImConversation conversation, long beforeSortUs, CancellationToken ct) =>
        Task.FromResult(new ImMessagePage([], 0, false));

    public Task<IReadOnlyDictionary<string, ImContact>> ImUsersAsync(IReadOnlyList<string> secUids, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, ImContact>>(new Dictionary<string, ImContact>());

    public Task<(bool Ok, long ServerId, string Note)> ImSendTextAsync(ImConversation conversation, string text, CancellationToken ct) =>
        Task.FromResult((false, 0L, Msg));

    public Task<(bool Ok, long ServerId, string Note)> ImSendStickerAsync(ImConversation conversation, ImSticker sticker, CancellationToken ct) =>
        Task.FromResult((false, 0L, Msg));

    public Task<(bool Ok, long ServerId, string Note)> ImSendShareAsync(ImConversation conversation, ShareCard card, CancellationToken ct) =>
        Task.FromResult((false, 0L, Msg));

    public Task<IReadOnlyList<ImSticker>> ImStickersAsync(bool force, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ImSticker>>([]);

    public Task<IReadOnlyList<ImSticker>> ImFavoriteStickersAsync(bool force, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ImSticker>>([]);

    public Task<IReadOnlyList<ImSticker>> ImRecentStickersAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ImSticker>>([]);

    public Task<ImStickerPage> ImSearchStickersAsync(string keyword, long cursor, string convId, CancellationToken ct) =>
        Task.FromResult(new ImStickerPage([], 0, false));

    public Task<(bool Ok, string Note)> ImStickerCollectAsync(ImSticker sticker, bool collect, CancellationToken ct) =>
        Task.FromResult((false, Msg));

    public Task ImStickerRecentRecordAsync(ImSticker sticker, string target, CancellationToken ct) => Task.CompletedTask;

    public bool IsImStickerFavorite(string id) => false;

    public string ImTextContent(string text) => text;

    public string ImStickerContent(ImSticker sticker) => string.Empty;

    // ── 会话与账号 ─────────────────────────────────────────────────
    public Task RestoreSessionAsync() => Task.CompletedTask;

    public Task<AccountResult> AdoptSessionAsync(IReadOnlyList<SessionCookie> cookies) =>
        Task.FromResult(new AccountResult(AccountOutcome.Failed, string.Empty, string.Empty, Msg));

    public Task<AccountResult> SwitchAccountAsync(string uid) =>
        Task.FromResult(new AccountResult(AccountOutcome.Failed, string.Empty, string.Empty, Msg));

    public Task<bool> PersistCurrentAccountAsync() => Task.FromResult(false);

    public void PersistCurrentAccountNow()
    {
    }

    public Task UpdateCurrentProfileAsync(string secUid, string nickname, string avatarUrl) => Task.CompletedTask;

    public Task SignOutAsync() => Task.CompletedTask;

    public void SyncUsageAccount()
    {
    }

    public void PrefetchCollects()
    {
    }

    public void PrefetchMessages()
    {
    }

    // ── 取图/取流的头 ──────────────────────────────────────────────
    /// <summary>真实现里这里带 UA/Referer/cookie。空实现什么都不加：官方图床会按缺头拒（403），
    /// 表现就是图片与视频取不到——与"没有后端"这个前提一致，不额外装样子。</summary>
    public void ApplyMediaHeaders(HttpRequestMessage request)
    {
    }

    // ── guard 材料 ─────────────────────────────────────────────────
    public string GuardExtractScript => string.Empty;

    public string GuardDrainScript => string.Empty;

    public string DecodeGuardScript(string raw) => string.Empty;

    public bool AdoptGuardMaterials(string pageJson) => false;

    public string GuardSummary => Msg;

    public string GuardKeyFingerprint => string.Empty;

    // ── 个人主页的本地快照 ────────────────────────────────────────
    public ProfileSnapshot? LoadProfileSnapshot(string secUid) => null;

    public ProfileSnapshot? LoadSelfSnapshot(string uid) => null;

    public void SaveProfileSnapshot(ProfileSnapshot snapshot)
    {
    }

    public string? LoadSelfSecUid(string uid) => null;

    public void SaveSelfSecUid(string uid, string secUid)
    {
    }

    /// <summary>0 = 永远不存（空实现不该往盘上写主页快照）。</summary>
    public int MaxProfileCacheRows => 0;

    public SavedAccountInfo? CurrentSavedAccount() => null;

    public IReadOnlyList<SavedAccountInfo> RecentSavedAccounts() => [];

    /// <summary>扫码那一档的空实现：发不出码，也不假装扫过了。</summary>
    private sealed class StubSignIn : ISignIn
    {
        public Task<SignInTicket> IssueAsync(CancellationToken ct) =>
            Task.FromResult(new SignInTicket(SignInStatus.Refused, string.Empty, null, Msg));

        public Task<SignInTicket> PollAsync(string ticket, CancellationToken ct) =>
            Task.FromResult(new SignInTicket(SignInStatus.Refused, ticket, null, Msg));

        public Task<OwnProfile> SelfAsync(CancellationToken ct) =>
            Task.FromResult(new OwnProfile(string.Empty, string.Empty, Msg));

        public Task<OwnProfile> ProfileAsync(CancellationToken ct) =>
            Task.FromResult(new OwnProfile(string.Empty, string.Empty, Msg));

        public Task<(bool? LoggedIn, string Uid)> CheckLoginAsync(CancellationToken ct) =>
            Task.FromResult<(bool?, string)>((false, string.Empty));
    }
}
