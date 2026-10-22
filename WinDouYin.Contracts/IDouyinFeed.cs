namespace WinDouYin.Contracts;

/// <summary>
/// 取数门面：界面侧唯一允许依赖的取数入口。方法名与参数刻意和取数层保持同名同序
/// （只有返回类型换成本文件的开源类型），这样"从直连 NativeFeed 换成走门面"是纯 token 替换，
/// 不掺逻辑改动——真要把某档参数（服务端要的档位号、用户类型）藏回取数层，才单独改调用点。
/// </summary>
public interface IDouyinFeed
{
    /// <summary>扫码登录那一档（只有它自己知道 passport 的轮询节奏与状态码）。</summary>
    ISignIn SignIn { get; }

    string Session { get; }

    bool IsLoggedIn { get; }

    /// <summary>真问过服务端才有的结论；null = 还没问过。罐里有 sessionid 不等于没过期。</summary>
    bool? LoginVerified { get; }

    string MyUid { get; }

    string MySecUid { get; }

    string MyAvatarUrl { get; }

    Task RefreshLoginStateAsync(CancellationToken ct = default);

    Task<string> EnsureMyIdentityAsync(CancellationToken ct);

    // ── 信息流与条目 ────────────────────────────────────────────────
    Task<IReadOnlyList<VideoCard>> NextBurstAsync(IReadOnlyList<string> presentedIds, int fanout, CancellationToken ct);

    Task<IReadOnlyList<VideoCard>> NextPageAsync(IReadOnlyList<string> presentedIds, CancellationToken ct);

    Task<VideoCard?> VideoDetailAsync(string id, CancellationToken ct);

    Task<(VideoCard? Card, bool TakenDown)> VideoDetailProbeAsync(string id, CancellationToken ct);

    Task<OpenResult> OpenAsync(string id, CancellationToken ct);

    Task<(string Url, string Note)> ShortLinkAsync(string awemeId, CancellationToken ct);

    Task<FeaturedFeedPage> FeaturedFeedAsync(string tagId, int refreshIndex, CancellationToken ct = default);

    Task<FollowFeedPage> FollowFeedAsync(long cursor, int pullType, CancellationToken ct = default);

    Task<FamiliarFeedPage> FamiliarFeedAsync(long cursor, int pullType, CancellationToken ct = default);

    // ── 点赞 / 收藏 / 历史 ─────────────────────────────────────────
    Task<(bool Ok, bool Liked, string Note)> LikeAsync(string awemeId, bool like, CancellationToken ct, bool flush = true);

    Task<(bool Ok, bool Liked, string Note)> DiggStateAsync(string awemeId, CancellationToken ct);

    /// <summary>收藏/取消到默认夹。作品类型号由取数层自己认，界面上没有那一格。</summary>
    Task<(bool Ok, string Note)> CollectAsync(string awemeId, bool collect, CancellationToken ct);

    Task<IReadOnlyList<CollectFolder>> CollectsFoldersAsync(bool force, CancellationToken ct);

    Task<(bool Ok, string Note)> CollectsMoveAsync(string awemeId, IReadOnlyList<CollectFolder> folders, CancellationToken ct);

    Task<string> WatchLaterAddAsync(string awemeId, CancellationToken ct);

    Task<CollectedPage> CollectsVideosAsync(string collectsId, long cursor, int count, CancellationToken ct);

    Task<CollectedPage> CollectedVideosAsync(long cursor, int count, CancellationToken ct);

    Task<WatchHistoryPage> HistoryPageAsync(long maxCursor, int count, CancellationToken ct);

    Task<(bool Ok, string Note)> HistoryWriteAsync(string awemeId, string authorId, string preItemId, CancellationToken ct);

    // ── 评论与弹幕 ─────────────────────────────────────────────────
    Task<CommentPage> CommentsAsync(string awemeId, long cursor, int count, CancellationToken ct);

    Task<ReplyPage> CommentRepliesAsync(string awemeId, string commentId, long cursor, int count, CancellationToken ct);

    Task<(bool Ok, string Note)> CommentDiggAsync(string awemeId, string cid, bool like, CancellationToken ct);

    Task<(bool Ok, string Cid, string Note)> CommentPublishAsync(
        string awemeId, string text, CancellationToken ct, string replyId = "", string replyToId = "");

    Task<(bool Ok, string Note)> CommentDeleteAsync(string awemeId, string cid, CancellationToken ct);

    Task<DanmakuPage> DanmakuPageAsync(string awemeId, long startMs, CancellationToken ct);

    Task<(bool Ok, string Id, string Note)> DanmakuPostAsync(string awemeId, string text, long offsetMs, CancellationToken ct);

    Task<(bool Ok, string Note)> DanmakuDiggAsync(string awemeId, string danmakuId, bool like, CancellationToken ct);

    /// <summary>撤回自己发的那条（"哪一类用户"那一格由取数层自己填）。</summary>
    Task<(bool Ok, string Note)> DanmakuDeleteAsync(string danmakuId, CancellationToken ct);

    Task<IReadOnlyList<EmojiItem>> EmojiListAsync(CancellationToken ct);

    // ── 人 / 主页 / 合集 ───────────────────────────────────────────
    Task<UserInfoPage> FollowingAsync(long maxTime, int count, CancellationToken ct);

    Task<UserInfoPage> FollowersAsync(long maxTime, int count, CancellationToken ct);

    Task<UserInfoPage> FollowingSidebarAsync(long maxTime, int sourceType, int count, CancellationToken ct = default);

    Task<(bool Ok, string Note)> FollowUserAsync(string userId, string secUserId, bool follow, CancellationToken ct);

    Task<UserProfile> UserProfileOtherAsync(string secUserId, CancellationToken ct);

    /// <summary>某个用户的一页作品；<paramref name="fromSelf"/> = 看自己（本人页与他人页是两套参数）。</summary>
    Task<UserPostPage> UserPostsAsync(string secUserId, long maxCursor, int count, CancellationToken ct, bool fromSelf = false);

    Task<MixListPage> UserMixPageAsync(string kind, string secUserId, long cursor, int count, CancellationToken ct);

    Task<MixListPage> FavoriteMixPageAsync(string kind, long cursor, int count, CancellationToken ct);

    Task<MusicPage> FavoriteMusicsAsync(long cursor, int count, CancellationToken ct);

    Task<MixDetail> MixDetailAsync(string mixId, CancellationToken ct);

    Task<MixEpisodePage> MixEpisodesAsync(string mixId, long cursor, int count, CancellationToken ct);

    Task<UserPostPage> UserFavoritesAsync(string secUserId, long maxCursor, int count, CancellationToken ct);

    // ── 搜索 ──────────────────────────────────────────────────────
    Task<SearchVideoPage> SearchAsync(SearchQuery request, CancellationToken ct);

    Task<SearchUserPage> SearchUsersAsync(SearchQuery request, CancellationToken ct);

    Task<IReadOnlyList<HotWord>> HotSearchAsync(CancellationToken ct);

    Task<IReadOnlyList<string>> GuessWordsAsync(CancellationToken ct);

    Task<IReadOnlyList<string>> SugAsync(string keyword, CancellationToken ct);

    // ── 私信 ──────────────────────────────────────────────────────
    Task<IReadOnlyList<ImConversation>> ImConversationsAsync(bool force, CancellationToken ct);

    Task<ImMessagePage> ImMessagesAsync(ImConversation conversation, long beforeSortUs, CancellationToken ct);

    Task<IReadOnlyDictionary<string, ImContact>> ImUsersAsync(IReadOnlyList<string> secUids, CancellationToken ct);

    Task<(bool Ok, long ServerId, string Note)> ImSendTextAsync(ImConversation conversation, string text, CancellationToken ct);

    Task<(bool Ok, long ServerId, string Note)> ImSendStickerAsync(ImConversation conversation, ImSticker sticker, CancellationToken ct);

    Task<(bool Ok, long ServerId, string Note)> ImSendShareAsync(ImConversation conversation, ShareCard card, CancellationToken ct);

    Task<IReadOnlyList<ImSticker>> ImStickersAsync(bool force, CancellationToken ct);

    Task<IReadOnlyList<ImSticker>> ImFavoriteStickersAsync(bool force, CancellationToken ct);

    Task<IReadOnlyList<ImSticker>> ImRecentStickersAsync(CancellationToken ct);

    Task<ImStickerPage> ImSearchStickersAsync(string keyword, long cursor, string convId, CancellationToken ct);

    Task<(bool Ok, string Note)> ImStickerCollectAsync(ImSticker sticker, bool collect, CancellationToken ct);

    Task ImStickerRecentRecordAsync(ImSticker sticker, string target, CancellationToken ct);

    bool IsImStickerFavorite(string id);

    /// <summary>自己发出去那条要回显时用的正文形状（形状本身是线上知识，界面只当黑盒串）。</summary>
    string ImTextContent(string text);

    string ImStickerContent(ImSticker sticker);

    // ── 会话与账号（登录交接、切号、落盘、预取）────────────────────
    string CurrentAccountUid { get; }

    Task RestoreSessionAsync();

    Task<AccountResult> AdoptSessionAsync(IReadOnlyList<SessionCookie> cookies);

    Task<AccountResult> SwitchAccountAsync(string uid);

    Task<bool> PersistCurrentAccountAsync();

    void PersistCurrentAccountNow();

    Task UpdateCurrentProfileAsync(string secUid, string nickname, string avatarUrl);

    Task SignOutAsync();

    void SyncUsageAccount();

    void PrefetchCollects();

    void PrefetchMessages();

    // ── 取图/取流那一发的头（域名、UA、Referer、cookie 全留在取数层）──
    void ApplyMediaHeaders(HttpRequestMessage request);

    // ── 登录那趟从活页面采回的 guard 材料：脚本与判定全在取数层，界面只当黑盒搬运 ──
    string GuardExtractScript { get; }

    string GuardDrainScript { get; }

    string DecodeGuardScript(string raw);

    bool AdoptGuardMaterials(string pageJson);

    string GuardSummary { get; }

    string GuardKeyFingerprint { get; }

    // ── 个人主页的本地快照：装的是开源类型，但落在哪个目录、什么格式归取数层 ──
    ProfileSnapshot? LoadProfileSnapshot(string secUid);

    ProfileSnapshot? LoadSelfSnapshot(string uid);

    void SaveProfileSnapshot(ProfileSnapshot snapshot);

    string? LoadSelfSecUid(string uid);

    void SaveSelfSecUid(string uid, string secUid);

    /// <summary>单档最多存多少行；超了整档不存（截断会跟游标对不上，留下看不到的空洞）。</summary>
    int MaxProfileCacheRows { get; }

    /// <summary>
    /// 存档里的一个号（只到"账号菜单能列、哪个是当前"这一层）。
    /// 那一整罐 cookie 与 guard 材料不在界面上出现——切号/加号一律回 <see cref="AccountResult"/>。
    /// </summary>
    SavedAccountInfo? CurrentSavedAccount();

    IReadOnlyList<SavedAccountInfo> RecentSavedAccounts();
}

/// <summary>扫码登录那一档。</summary>
public interface ISignIn
{
    Task<SignInTicket> IssueAsync(CancellationToken ct);

    Task<SignInTicket> PollAsync(string ticket, CancellationToken ct);

    Task<OwnProfile> SelfAsync(CancellationToken ct);

    Task<OwnProfile> ProfileAsync(CancellationToken ct);

    Task<(bool? LoggedIn, string Uid)> CheckLoginAsync(CancellationToken ct);
}
