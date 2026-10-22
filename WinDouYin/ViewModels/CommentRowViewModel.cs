using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinDouYin.Contracts;

namespace WinDouYin.ViewModels;

/// <summary>
/// 评论面板里的一行：头像 + 昵称 + 正文 + 尾注（"赞 1.2w · 3小时前"）+ 二级回复。
/// 头像是 <c>user.avatar_thumb.url_list[0]</c>，直接 <see cref="BitmapImage"/> 取（签名链不带任何头）。
/// 用可写属性而不是 record 的位置参数（init-only）：XamlTypeInfo 生成器要给模板里的类型建 setter，init-only 会编译不过。
/// </summary>
public sealed partial class CommentRowViewModel : INotifyPropertyChanged
{
    private bool _expanded;
    private bool _expanding;
    private bool _moreLoading;
    private bool _hasMoreReplies;
    private string _expandLabel;
    private int _digg;
    private bool _liked;
    private bool _pinned;

    public CommentRowViewModel(string author, string text, string meta, string id = "", string avatarUrl = "", int replyTotal = 0,
        IReadOnlyList<CommentImageItem>? images = null, CommentStickerItem? sticker = null, int digg = 0, bool liked = false)
    {
        Id = id;
        Author = author;
        Text = text;
        Meta = meta;
        ReplyTotal = replyTotal;
        Avatar = AvatarFrom(avatarUrl);
        Initials = author.Length > 0 ? author[..1] : "?";
        _expandLabel = replyTotal > 0 ? $"展开{replyTotal}条回复" : string.Empty;
        Images = CommentImageViewModel.From(images);
        Sticker = CommentStickerViewModel.From(sticker);
        _digg = digg;
        _liked = liked;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>一级评论 cid（展开二级评论要用）。</summary>
    public string Id { get; }

    /// <summary>昵称（报文里没有就由铺行的一方填个默认）。</summary>
    public string Author { get; set; }

    /// <summary>正文，原样（含换行）。</summary>
    public string Text { get; set; }

    /// <summary>尾注（点赞数与相对时间；没有赞时就只剩时间）。</summary>
    public string Meta { get; set; }

    /// <summary>没有头像时 PersonPicture 摆的字。</summary>
    public string Initials { get; }

    public ImageSource? Avatar { get; }

    /// <summary>图片评论的图（一张以上走方网格，见 <see cref="ImageGridVisibility"/>）。</summary>
    public List<CommentImageViewModel> Images { get; }

    /// <summary>表情包评论的那张图（没有就是 null）。</summary>
    public CommentStickerViewModel? Sticker { get; }

    /// <summary>单图评论：按原比例画，点开看原图。</summary>
    public Visibility SingleImageVisibility => Images.Count == 1 ? Visibility.Visible : Visibility.Collapsed;

    public ImageSource? SingleImageSource => Images.Count == 1 ? Images[0].Source : null;

    public double SingleImageWidth => Images.Count == 1 ? Images[0].DisplayWidth : 0;

    public double SingleImageHeight => Images.Count == 1 ? Images[0].DisplayHeight : 0;

    public string SingleImageUrl => Images.Count == 1 ? Images[0].Url : string.Empty;

    /// <summary>多图评论：3 列方网格（缩略），点开看原图。</summary>
    public Visibility ImageGridVisibility => Images.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility StickerVisibility => Sticker is not null ? Visibility.Visible : Visibility.Collapsed;

    public ImageSource? StickerSource => Sticker?.Source;

    public double StickerWidth => Sticker?.DisplayWidth ?? 0;

    public double StickerHeight => Sticker?.DisplayHeight ?? 0;

    public string StickerUrl => Sticker?.Url ?? string.Empty;

    /// <summary>这条评论被置顶（分享卡详情里的来源评论）：列表移到最上面，卡头多一行“置顶”。</summary>
    public bool IsPinned => _pinned;

    public Visibility PinnedVisibility => _pinned ? Visibility.Visible : Visibility.Collapsed;

    internal void SetPinned(bool pinned)
    {
        if (_pinned == pinned)
        {
            return;
        }

        _pinned = pinned;
        Notify(nameof(IsPinned));
        Notify(nameof(PinnedVisibility));
    }

    /// <summary>这条评论的赞数（点赞按钮上那个数字）。</summary>
    public string LikeLabel => WinDouYin.CountText.Format(_digg);

    /// <summary>当前会话赞过没有（列表接口的 <c>user_digged</c>）：心形空心/实心由它定。</summary>
    public bool Liked => _liked;

    /// <summary>
    /// 点赞状态与计数一起改：点亮 +1、取消 -1（不低于 0）。
    /// 界面先乐观更新，写接口失败再原地翻回来。
    /// </summary>
    public void ApplyLike(bool liked)
    {
        if (_liked == liked)
        {
            return;
        }

        _liked = liked;
        _digg = Math.Max(0, _digg + (liked ? 1 : -1));
        Notify(nameof(Liked));
        Notify(nameof(LikeLabel));
    }

    /// <summary>这条一级评论下面一共有多少条回复（报文里的 <c>reply_comment_total</c>）。</summary>
    public int ReplyTotal { get; private set; }

    /// <summary>本地追加一条回复（发成功后直接插到列表头）时同步总数与展开标签。</summary>
    public void IncrementReplyTotal()
    {
        ReplyTotal++;
        Notify(nameof(ExpandVisibility));
        if (!_expanded)
        {
            _expandLabel = $"展开{ReplyTotal}条回复";
            Notify(nameof(ExpandLabel));
        }
    }

    /// <summary>展开后取回来的直接回复；更深的回复挂在各自的 <see cref="CommentReplyRowViewModel.Children"/> 里。</summary>
    public ObservableCollection<CommentReplyRowViewModel> Replies { get; } = [];

    /// <summary>原始回复（接口是平铺一串，三级也在里面；按 reply_to 链在代码里拼树后铺进 <see cref="Replies"/>）。</summary>
    internal List<ReplyItem> ReplyItems { get; } = [];

    /// <summary>已点开"展开回复"的那几条回复 cid（逐层展开；整楼收起再开时保留）。</summary>
    internal HashSet<string> ExpandedReplyIds { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// 回复那颗心的本地状态（cid → 赞没赞过 + 当时赞数）：回复树每次重铺都会按原始
    /// <see cref="ReplyItem"/> 重建 VM，这里把用户点过的状态压上去，免得"展开/收起"一下心又空了。
    /// 服务端读回（换视频重开面板）时以接口为准。
    /// </summary>
    internal Dictionary<string, (bool Liked, int Digg)> ReplyLikeState { get; } = new(StringComparer.Ordinal);

    /// <summary>回复列表的续页游标（接口回的是 offset）。</summary>
    public long ReplyCursor { get; set; }

    /// <summary>服务端回的回复总数（<c>total</c>，可能比一级的 reply_comment_total 多一枚）。</summary>
    public int ReplyServerTotal { get; set; }

    /// <summary>没有回复就不摆"展开"那颗按钮。</summary>
    public Visibility ExpandVisibility => ReplyTotal > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>只有真的展开了（且取到了东西）才摆回复列表。</summary>
    public Visibility RepliesVisibility => _expanded && Replies.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>展开着且服务端说还有下一页，才摆"加载更多回复"。</summary>
    public Visibility MoreRepliesVisibility => _expanded && _hasMoreReplies ? Visibility.Visible : Visibility.Collapsed;

    public string MoreRepliesLabel => _moreLoading
        ? "正在取更多回复…"
        : ReplyServerTotal > ReplyItems.Count ? $"加载更多回复（还有 {ReplyServerTotal - ReplyItems.Count} 条）" : "加载更多回复";

    public string ExpandLabel
    {
        get => _expandLabel;
        private set
        {
            _expandLabel = value;
            Notify(nameof(ExpandLabel));
        }
    }

    public bool IsExpanded => _expanded;

    /// <summary>点"展开"→ 正在取：返回 false 表示已经有一发在飞，别再来第二下。</summary>
    public bool BeginRepliesLoad()
    {
        if (_expanding)
        {
            return false;
        }

        _expanding = true;
        ExpandLabel = "正在取回复…";
        return true;
    }

    /// <summary>取到了：摆出列表，标签变"收起回复"。</summary>
    public void CompleteRepliesLoad()
    {
        _expanding = false;
        _expanded = true;
        ExpandLabel = "收起回复";
        Notify(nameof(RepliesVisibility));
        Notify(nameof(MoreRepliesVisibility));
    }

    /// <summary>没取到（200 空体/失败）：保持收起，标签给重试提示——别把空当"没有回复"。</summary>
    public void FailRepliesLoad()
    {
        _expanding = false;
        _expanded = false;
        ExpandLabel = $"展开{ReplyTotal}条回复（没取到，再点一次）";
        Notify(nameof(RepliesVisibility));
        Notify(nameof(MoreRepliesVisibility));
    }

    public void CollapseReplies()
    {
        _expanded = false;
        ExpandLabel = $"展开{ReplyTotal}条回复";
        Notify(nameof(RepliesVisibility));
        Notify(nameof(MoreRepliesVisibility));
    }

    /// <summary>记下这一页的游标/还有没有下一页；<paramref name="loading"/> 只改"加载更多"那颗按钮的字。</summary>
    public void SetReplyPage(long cursor, bool hasMore, int total, bool loading)
    {
        ReplyCursor = cursor;
        _hasMoreReplies = hasMore;
        ReplyServerTotal = total;
        _moreLoading = loading;
        Notify(nameof(MoreRepliesVisibility));
        Notify(nameof(MoreRepliesLabel));
    }

    internal static ImageSource? AvatarFrom(string url) =>
        url.Length > 0 ? new BitmapImage(new Uri(url)) : null;

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 回复节点：头像 + 昵称 + 正文 + 尾注 + 自己的"展开回复"；展开后的子回复放在 <see cref="Children"/>。
/// 界面把它画成一张圆角卡，父级的卡会把子级的卡包在里面；<see cref="ReplyToLabel"/> 是"回复 @某人"。
/// </summary>
[Microsoft.UI.Xaml.Data.Bindable]
public sealed partial class CommentReplyRowViewModel : INotifyPropertyChanged
{
    private int _digg;
    private bool _liked;

    public CommentReplyRowViewModel(string author, string text, string meta, string id, CommentRowViewModel owner,
        string avatarUrl = "", string replyTo = "", bool hasChildren = false, bool isExpanded = false,
        Action<CommentReplyRowViewModel>? toggle = null, IReadOnlyList<CommentImageItem>? images = null,
        CommentStickerItem? sticker = null, Action<CommentReplyRowViewModel>? like = null, int digg = 0, bool liked = false,
        Action<CommentReplyRowViewModel>? reply = null)
    {
        Id = id;
        Owner = owner;
        Toggle = toggle;
        Like = like;
        Reply = reply;
        Author = author;
        Text = text;
        Meta = meta;
        Avatar = CommentRowViewModel.AvatarFrom(avatarUrl);
        Initials = author.Length > 0 ? author[..1] : "?";
        ReplyToLabel = replyTo;
        ReplyToVisibility = replyTo.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExpandVisibility = hasChildren ? Visibility.Visible : Visibility.Collapsed;
        ExpandLabel = isExpanded ? "收起回复" : "展开回复";
        Images = CommentImageViewModel.From(images);
        Sticker = CommentStickerViewModel.From(sticker);
        _digg = digg;
        _liked = liked;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>这条回复的 cid（逐层展开时记状态用）。</summary>
    internal string Id { get; }

    /// <summary>所属一级评论（点开/收起时重铺整棵显示树）。</summary>
    internal CommentRowViewModel Owner { get; }

    /// <summary>点"展开回复"/"收起回复"——回到一级评论那边重铺整棵显示树。</summary>
    internal Action<CommentReplyRowViewModel>? Toggle { get; }

    /// <summary>点这颗心：宿主拿这条回复的 cid 去发评论点赞写请求。</summary>
    internal Action<CommentReplyRowViewModel>? Like { get; }

    /// <summary>点"回复"：宿主切到"回复 @这条"的输入状态。</summary>
    internal Action<CommentReplyRowViewModel>? Reply { get; }

    public string Author { get; set; }

    public string Text { get; set; }

    public string Meta { get; set; }

    public string Initials { get; }

    public ImageSource? Avatar { get; }

    /// <summary>回复里的图片（一张以上走方网格）。</summary>
    public List<CommentImageViewModel> Images { get; }

    /// <summary>回复里的表情包（没有就是 null）。</summary>
    public CommentStickerViewModel? Sticker { get; }

    public Visibility SingleImageVisibility => Images.Count == 1 ? Visibility.Visible : Visibility.Collapsed;

    public ImageSource? SingleImageSource => Images.Count == 1 ? Images[0].Source : null;

    public double SingleImageWidth => Images.Count == 1 ? Images[0].DisplayWidth : 0;

    public double SingleImageHeight => Images.Count == 1 ? Images[0].DisplayHeight : 0;

    public string SingleImageUrl => Images.Count == 1 ? Images[0].Url : string.Empty;

    public Visibility ImageGridVisibility => Images.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility StickerVisibility => Sticker is not null ? Visibility.Visible : Visibility.Collapsed;

    public ImageSource? StickerSource => Sticker?.Source;

    public double StickerWidth => Sticker?.DisplayWidth ?? 0;

    public double StickerHeight => Sticker?.DisplayHeight ?? 0;

    public string StickerUrl => Sticker?.Url ?? string.Empty;

    /// <summary>这条回复的赞数（点赞按钮上那个数字）。</summary>
    public string LikeLabel => WinDouYin.CountText.Format(_digg);

    /// <summary>当前会话赞过没有（<c>user_digged</c>）。</summary>
    public bool Liked => _liked;

    /// <summary>当前显示用的赞数（重铺时写回本地状态）。</summary>
    internal int DiggValue => _digg;

    /// <summary>点赞状态与计数一起改（乐观更新/失败回滚都走这里）。</summary>
    public void ApplyLike(bool liked)
    {
        if (_liked == liked)
        {
            return;
        }

        _liked = liked;
        _digg = Math.Max(0, _digg + (liked ? 1 : -1));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Liked)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LikeLabel)));
    }

    /// <summary>展开后铺在父卡内部的子回复（只有点开了才有内容）。</summary>
    public ObservableCollection<CommentReplyRowViewModel> Children { get; } = [];

    /// <summary>"回复 @某人"（只有父回复在本页且正文里没自带 @ 时才填）。</summary>
    public string ReplyToLabel { get; }

    public Visibility ReplyToVisibility { get; }

    /// <summary>手里已经取到它的子回复，才摆"展开回复"。</summary>
    public Visibility ExpandVisibility { get; }

    public string ExpandLabel { get; }
}

/// <summary>
/// 评论里的一张图片：面板画 <see cref="Source"/>（中等缩略，载入快），点开看 <see cref="Url"/>（原图）。
/// <see cref="DisplayWidth"/>/<see cref="DisplayHeight"/> 是按原图比例塞进 200×200 框后的尺寸——
/// 单图不裁剪，竖长截图也能整张看到（点击再放大）。
/// </summary>
[Microsoft.UI.Xaml.Data.Bindable]
public sealed class CommentImageViewModel
{
    private const double MaxSide = 200;

    private CommentImageViewModel(string url, string thumbUrl, int width, int height)
    {
        Url = url;
        (DisplayWidth, DisplayHeight) = Fit(width, height, MaxSide, MaxSide);
        Source = thumbUrl.Length > 0 ? Decode(thumbUrl, DisplayWidth, width) : null;
    }

    /// <summary>
    /// 面板里最多画 200 逻辑像素：缩略图按 2 倍 DPI 封顶解码，不按原缩略尺寸整张解。
    /// 一条长截图的缩略就有几 MB 解码内存，评论列表在缓存页里留着时这块最占地方。
    /// </summary>
    internal static BitmapImage Decode(string url, double displayWidth, int sourceWidth)
    {
        int wanted = Math.Max(1, (int)Math.Ceiling(displayWidth * 2));
        if (sourceWidth > 0)
        {
            wanted = Math.Min(wanted, sourceWidth);
        }

        var bitmap = new BitmapImage { DecodePixelWidth = wanted };
        bitmap.UriSource = new Uri(url);
        return bitmap;
    }

    /// <summary>原图地址（点开大图用）。</summary>
    public string Url { get; }

    public ImageSource? Source { get; }

    public double DisplayWidth { get; }

    public double DisplayHeight { get; }

    internal static List<CommentImageViewModel> From(IReadOnlyList<CommentImageItem>? images)
    {
        if (images is null || images.Count == 0)
        {
            return [];
        }

        List<CommentImageViewModel> list = new(images.Count);
        foreach (CommentImageItem img in images)
        {
            list.Add(new CommentImageViewModel(img.Url, img.ThumbUrl, img.Width, img.Height));
        }

        return list;
    }

    /// <summary>按比例塞进 maxW×maxH（不放大）；宽高缺失就按上限画，宁可大一点也别 0。</summary>
    internal static (double Width, double Height) Fit(int width, int height, double maxW, double maxH)
    {
        if (width <= 0 || height <= 0)
        {
            return (maxW, maxH);
        }

        double scale = Math.Min(maxW / width, maxH / height);
        return (Math.Round(width * scale), Math.Round(height * scale));
    }
}

/// <summary>
/// 评论里的表情包（<c>sticker</c>）：面板里按原比例画，最大 140×140（抖音表情包多为 140×69 / 240×240）。
/// 只取静图——WinUI 3 的 Image 不播 webp 动画，动图那档画出来也只有第一帧。
/// </summary>
public sealed class CommentStickerViewModel
{
    private const double MaxSide = 140;

    private CommentStickerViewModel(string url, int width, int height)
    {
        Url = url;
        (DisplayWidth, DisplayHeight) = CommentImageViewModel.Fit(width, height, MaxSide, MaxSide);
        Source = CommentImageViewModel.Decode(url, DisplayWidth, width);
    }

    public string Url { get; }

    public ImageSource? Source { get; }

    public double DisplayWidth { get; }

    public double DisplayHeight { get; }

    internal static CommentStickerViewModel? From(CommentStickerItem? sticker) =>
        sticker is null ? null : new CommentStickerViewModel(sticker.Url, sticker.Width, sticker.Height);
}

