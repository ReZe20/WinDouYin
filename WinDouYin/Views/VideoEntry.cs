using WinDouYin.Contracts;

namespace WinDouYin.Views;

/// <summary>
/// 舞台要的一条视频。推荐流和消息分享卡的数据结构不同，进页面之前都在这里摊平成同一份，
/// <see cref="VideoPage"/> 只认它，不再反过来依赖某个列表的 ViewModel。
/// </summary>
public sealed record VideoEntry(
    string Id, string Source, string Cover, int Width, int Height,
    string Author, string Desc, string Avatar, string TimeText,
    int Digg, int Comment, int Collect, int Share,
    VideoQualitySet Qualities, string Music = "", string AuthorId = "", string AuthorSecUid = "",
    string FriendRecommender = "")
{
    /// <summary>还没装条目时的占位，避免 x:Bind 先撞 null。</summary>
    public static VideoEntry Empty { get; } = new(
        string.Empty, string.Empty, string.Empty, 0, 0, string.Empty, string.Empty,
        string.Empty, string.Empty, 0, 0, 0, 0, VideoQualitySet.Empty);

    public string DiggText => CountText.Format(Digg);

    public string CommentText => CountText.Format(Comment);

    public string CollectText => CountText.Format(Collect);

    public string ShareText => CountText.Format(Share);

    /// <summary>AuthorSecUid 是给「点作者进主页」用的：<c>user/profile/other</c> 只认 sec_user_id，
    /// 光有 uid（AuthorId）进不去，所以这里把它一起摊平带过来。</summary>
    public static VideoEntry From(VideoCard card) => new(
        card.Id, card.Play, card.Cover, card.Width, card.Height, card.Author, card.Desc, card.Avatar,
        RelativeTime.Format(card.CreateTime), card.Digg, card.Comment, card.Collect, card.Share,
        card.Qualities, card.Music, card.AuthorId, card.SecUid, card.FriendRecommender);

    public static VideoEntry TakenDown(string id) => new(
        id, string.Empty, string.Empty, 0, 0, string.Empty, "这条视频已下架或不存在，无法播放",
        string.Empty, string.Empty, 0, 0, 0, 0, VideoQualitySet.Empty);
}
