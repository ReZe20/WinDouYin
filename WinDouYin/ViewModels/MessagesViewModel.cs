using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinDouYin.Controls;
using WinDouYin.Contracts;
using WinDouYin.Views;

namespace WinDouYin.ViewModels;

/// <summary>消息列表里的一行：头像 + 会话名 + 最后一条摘要 + 相对时间。</summary>
public sealed class MessageRow
{
    public MessageRow(ImConversation conversation)
    {
        Conversation = conversation;
        string title = conversation.Title.Length > 0 ? conversation.Title : "抖音用户";
        Title = title;
        Preview = conversation.Preview.Length > 0 ? conversation.Preview : "暂无消息";
        TimeText = conversation.LastTimeMs > 0
            ? RelativeTime.Format(conversation.LastTimeMs / 1000)
            : string.Empty;
        Avatar = ImageOf(conversation.AvatarUrl);
        Initials = title[..1];
    }

    /// <summary>点进去聊天要用的原始会话（ticket、short id、对端资料都在里面）。</summary>
    public ImConversation Conversation { get; }

    public string Title { get; }

    public string Preview { get; }

    public string TimeText { get; }

    public string Initials { get; }

    public ImageSource? Avatar { get; }

    internal static ImageSource? ImageOf(string url)
    {
        if (url.Length == 0)
        {
            return null;
        }

        // 群头像实测有 http:// 的图床地址，统一抬到 https，免得被图片管线拦
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url[7..];
        }

        try
        {
            return new BitmapImage(new Uri(url));
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// 聊天窗里的一条消息。正文 JSON 按消息类型拆成：文本（含 [码] → 表情图）、表情包、
/// 图片、分享卡片、系统提示；界面按可见性挑一种画。界面是「会话行」——没有彩色气泡，
/// 但文字与引用一起装进系统卡片框住；头像与名字只在一次连续发言的第一条出现，
/// 时间每条都出、统一一行放在消息下面；对方在左、自己在右，自己的名字用系统 Accent 标出。
/// </summary>
public sealed class ChatMessageRow
{
    /// <param name="showSenderName">这一条要不要出头像与名字：换人或隔了 5 分钟以上才出。</param>
    /// <param name="quoteSender">被引用那条的昵称兜底（f18 里 nickname 缺失时由调用方按 uid 认）。</param>
    public ChatMessageRow(
        ImMessage message, ImConversation conversation,
        ImContact? sender = null, bool showSenderName = true, string quoteSender = "")
    {
        IsMine = message.IsMine;
        IsSystem = message.MessageType is 1 or 1001;
        IsGroup = conversation.Type == 2;
        ServerId = message.ServerId;
        SortUs = message.SortUs;
        ReferServerId = message.ReferServerId;
        ReferSortUs = message.ReferSortUs;

        long timeMs = TimeOf(message);
        // 时间不再跟着发言头分组，每条消息下面都出一行（系统提示本来就没有时间）。
        TimeText = IsSystem ? string.Empty : FormatTime(timeMs);
        TimeVisibility = TimeText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 1:1 里对方的名字取会话标题；自己的写「我」。群聊里拿不到成员资料时留空，
        // 发言头整行收起（头像仍是群头像/首字），不要编一个假名字。
        SenderName = IsMine
            ? "我"
            : IsGroup
                ? sender?.DisplayName ?? string.Empty
                : conversation.Title;
        bool header = !IsSystem && showSenderName;
        PeerHeaderVisibility = header && !IsMine ? Visibility.Visible : Visibility.Collapsed;
        MineHeaderVisibility = header && IsMine ? Visibility.Visible : Visibility.Collapsed;
        PeerAvatarVisibility = PeerHeaderVisibility;
        MineAvatarVisibility = MineHeaderVisibility;
        PeerNameVisibility = PeerHeaderVisibility == Visibility.Visible && SenderName.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        string incomingAvatar = IsGroup && sender is not null && sender.AvatarUrl.Length > 0
            ? sender.AvatarUrl
            : conversation.AvatarUrl;
        Avatar = IsMine
            ? MessageRow.ImageOf(Douyin.Feed.MyAvatarUrl)
            : MessageRow.ImageOf(incomingAvatar);
        string incomingName = IsGroup && sender is not null ? sender.DisplayName : conversation.Title;
        AvatarInitials = IsMine
            ? "我"
            : incomingName.Length > 0 ? incomingName[..1] : IsGroup ? "群" : "友";

        // 引用回复：优先用 f18 里的 nickname，拿不到才用调用方补的名字（我 / 1:1 会话标题 / 群成员）。
        string quoteName = message.ReferSenderName.Length > 0 ? message.ReferSenderName : quoteSender;
        QuoteText = message.ReferText.Length > 0
            ? quoteName.Length > 0 ? quoteName + "：" + message.ReferText : message.ReferText
            : string.Empty;
        QuoteVisibility = QuoteText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        Parse(message);
    }

    public bool IsMine { get; }

    public bool IsSystem { get; }

    public bool IsGroup { get; }

    public string SenderName { get; }

    /// <summary>发言头与头像按方向分两侧：对方在左、自己在右；连续同一人的后续消息两侧都收起。</summary>
    public Visibility PeerHeaderVisibility { get; }

    public Visibility MineHeaderVisibility { get; }

    public Visibility PeerAvatarVisibility { get; }

    public Visibility MineAvatarVisibility { get; }

    public Visibility PeerNameVisibility { get; }

    /// <summary>图片、贴纸、分享卡贴向自己那一侧。</summary>
    public HorizontalAlignment AttachmentAlign => IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    /// <summary>正文卡片只在「有文字」或「有引用」时出框；图片/贴纸/分享卡本身已经是框。</summary>
    public Visibility BodyCardVisibility => TextVisibility == Visibility.Visible || QuoteVisibility == Visibility.Visible
        ? Visibility.Visible
        : Visibility.Collapsed;

    /// <summary>引用回复块：没有 f18 或取不到摘要时收起。</summary>
    public Visibility QuoteVisibility { get; }

    /// <summary>「原发言者：摘要」；群聊里拿不到昵称时就只出摘要。</summary>
    public string QuoteText { get; }

    /// <summary>本条消息的 server id 与 sort：点引用时靠它们认人。</summary>
    public long ServerId { get; }

    public long SortUs { get; }

    /// <summary>被引用那条的 server id / sort（f18.f1、f18.f8）；两个都缺就没法跳。</summary>
    public long ReferServerId { get; }

    public long ReferSortUs { get; }

    public string TimeText { get; }

    public Visibility TimeVisibility { get; }

    public ImageSource? Avatar { get; }

    public string AvatarInitials { get; }

    public string Text { get; private set; } = string.Empty;

    public Visibility TextVisibility { get; private set; } = Visibility.Collapsed;

    public ImageSource? StickerImage { get; private set; }

    public Visibility StickerVisibility { get; private set; } = Visibility.Collapsed;

    public ImageSource? PhotoImage { get; private set; }

    public Visibility PhotoVisibility { get; private set; } = Visibility.Collapsed;

    public string ShareTitle { get; private set; } = string.Empty;

    public string ShareSubtitle { get; private set; } = string.Empty;

    /// <summary>分享卡对应的视频 id（点开详情/评论用）；没有就空，卡片点了不动作。</summary>
    public string ShareVideoId { get; private set; } = string.Empty;

    public ImageSource? ShareCover { get; private set; }

    public Visibility ShareVisibility { get; private set; } = Visibility.Collapsed;

    /// <summary>105 评论分享卡：评论正文（纯文本，不含图/表情占位）。</summary>
    public string ReviewText { get; private set; } = string.Empty;

    /// <summary>105 评论分享卡：评论者昵称。</summary>
    public string ReviewAuthor { get; private set; } = string.Empty;

    /// <summary>105 评论分享卡：评论里的图片，多张。</summary>
    public IReadOnlyList<ImageSource> ReviewImages { get; private set; } = Array.Empty<ImageSource>();

    /// <summary>105 评论分享卡：评论里的表情包。</summary>
    public IReadOnlyList<ImageSource> ReviewStickers { get; private set; } = Array.Empty<ImageSource>();

    /// <summary>105 评论分享卡：评论原文、图、表情包（传给 SharedVideoPage 在视频上方显示）。</summary>
    public SharedReviewArgs? ReviewArgs { get; private set; }

    public Visibility ReviewVisibility => ReviewArgs is not null ? Visibility.Visible : Visibility.Collapsed;

    internal static long TimeOf(ImMessage message) =>
        message.TimeMs > 0 ? message.TimeMs : message.SortUs / 1000;

    private void Parse(ImMessage message)
    {
        if (message.Content.Length == 0)
        {
            ShowPlain(message.MessageType switch
            {
                5 => "[表情]",
                8 or 77 => "[分享]",
                105 => "[视频]",
                27 => "[图片]",
                _ => "[消息]",
            });
            return;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(message.Content);
            JsonElement root = doc.RootElement;
            switch (message.MessageType)
            {
                case 5:
                    StickerImage = MessageRow.ImageOf(UrlOf(root, "url"));
                    if (StickerImage is not null)
                    {
                        StickerVisibility = Visibility.Visible;
                    }
                    else
                    {
                        ShowPlain("[表情]");
                    }
                    return;
                case 27:
                    PhotoImage = MessageRow.ImageOf(PhotoUrl(root));
                    if (PhotoImage is not null)
                    {
                        PhotoVisibility = Visibility.Visible;
                    }
                    else
                    {
                        ShowPlain("[图片]");
                    }
                    return;

                case 7 or 714:
                    ShowPlain(Str(root, "text"));
                    return;

                case 8 or 77:
                    ShareTitle = Str(root, "content_title");
                    if (ShareTitle.Length == 0)
                    {
                        ShareTitle = Str(root, "content_name");
                    }

                    ShareSubtitle = "分享视频";
                    ShareCover = MessageRow.ImageOf(UrlOf(root, "cover_url"));
                    ShareVideoId = ShareVideoIdOf(root);
                    break;

                case 105:
                    ShareTitle = Str(root, "aweme_title");
                    ShareSubtitle = "分享视频";
                    ShareCover = MessageRow.ImageOf(UrlOf(root, "cover_url"));
                    ShareVideoId = ShareVideoIdOf(root);
                    TryParseReview(root);
                    break;

                case 110:
                    if (root.TryGetProperty("aweme_info", out JsonElement info) && info.ValueKind == JsonValueKind.Object)
                    {
                        ShareTitle = Str(info, "desc");
                        if (ShareTitle.Length == 0)
                        {
                            ShareTitle = Str(info, "aweme_title");
                        }

                        ShareSubtitle = "分享作品";
                        ShareCover = MessageRow.ImageOf(UrlOf(info, "cover_url"));
                        ShareVideoId = ShareVideoIdOf(info);
                    }

                    break;

                case 68:
                    // 图文分享卡：正文 images[].url_list[]
                    break;

                case 1 or 1001:
                    ShowPlain(Regex.Replace(Str(root, "tips"), @"\{\{\d+\}\}", string.Empty).Trim());
                    return;

                default:
                    ShowPlain("[暂不支持的消息]");
                    return;
            }

            if (ShareTitle.Length == 0)
            {
                ShareTitle = "[分享]";
            }

            ShareVisibility = Visibility.Visible;
        }
        catch (JsonException)
        {
            ShowPlain(message.Content);
        }
    }

    private void ShowPlain(string text)
    {
        Text = text;
        TextVisibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatTime(long ms)
    {
        if (ms <= 0)
        {
            return string.Empty;
        }

        DateTimeOffset time = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
        DateTimeOffset now = DateTimeOffset.Now;
        if (time.Date == now.Date)
        {
            return time.ToString("HH:mm");
        }

        if (time.Date == now.Date.AddDays(-1))
        {
            return "昨天 " + time.ToString("HH:mm");
        }

        return time.Year == now.Year
            ? time.ToString("M月d日 HH:mm")
            : time.ToString("yyyy年M月d日 HH:mm");
    }

    private static string PhotoUrl(JsonElement root)
    {
        if (!root.TryGetProperty("resource_url", out JsonElement resource) || resource.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (string list in new[] { "medium_url_list", "large_url_list", "origin_url_list" })
        {
            string url = FirstInList(resource, list);
            if (url.Length > 0)
            {
                return url;
            }
        }

        return string.Empty;
    }

    private static string UrlOf(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement holder) || holder.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        string url = FirstInList(holder, "url_list");
        if (url.Length > 0)
        {
            return url;
        }

        return Str(holder, "uri");
    }

    private static string FirstInList(JsonElement holder, string name)
    {
        if (!holder.TryGetProperty(name, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (JsonElement item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } url)
            {
                return url;
            }
        }

        return string.Empty;
    }

    /// <summary>分享正文里视频 id 的字段名实测是 itemId（不是 aweme_id）；110 的嵌套作品用 aweme_info.aweme_id。</summary>
    private static string ShareVideoIdOf(JsonElement root)
    {
        foreach (string name in new[] { "itemId", "item_id", "aweme_id" })
        {
            string id = Str(root, name);
            if (id.Length > 0)
            {
                return id;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 105 评论分享卡：按 2026-10-18 实录的真实字段解析——
    /// comment=评论文字、comment_user_name=评论者、comment_url={url_list}=评论图/表情包、
    /// comment_content_type=2 表示 comment_url 是图/表情。没有 content_rich_text 这类字段。
    /// </summary>
    private void TryParseReview(JsonElement root)
    {
        string text = Str(root, "comment");
        string commentUrl = string.Empty;
        ImageSource? commentImage = null;
        if (root.TryGetProperty("comment_url", out JsonElement urlEl) && urlEl.ValueKind == JsonValueKind.Object)
        {
            commentUrl = FirstInList(urlEl, "url_list");
            if (commentUrl.Length > 0)
            {
                commentImage = MessageRow.ImageOf(commentUrl);
            }
        }

        if (text.Length == 0 && commentImage is null)
        {
            return;
        }

        ReviewText = text;
        ReviewAuthor = Str(root, "comment_user_name");
        ReviewImages = commentImage is not null ? [commentImage] : Array.Empty<ImageSource>();
        ReviewStickers = Array.Empty<ImageSource>();
        ReviewArgs = new SharedReviewArgs(
            ReviewText, ReviewAuthor, Str(root, "comment_id"), ReviewImages.ToList(), ReviewStickers.ToList(), commentUrl);
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>表情包面板的四个来源。</summary>
public enum StickerSource
{
    Recent,
    Favorite,
    Trending,
    Search,
}

/// <summary>表情包里的一格：图 + 名字 + 当前号有没有收藏（右键菜单文案跟着变）。</summary>
public sealed partial class StickerItem : INotifyPropertyChanged
{
    private bool _isFavorite;

    public StickerItem(ImSticker sticker, bool isFavorite)
    {
        Sticker = sticker;
        _isFavorite = isFavorite;
        Image = MessageRow.ImageOf(sticker.Url);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ImSticker Sticker { get; }

    public string Id => Sticker.Id;

    public string DisplayName => Sticker.DisplayName.Length > 0 ? Sticker.DisplayName : "表情包";

    public ImageSource? Image { get; }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value)
            {
                return;
            }

            _isFavorite = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FavoriteText)));
        }
    }

    public string FavoriteText => _isFavorite ? "取消收藏" : "收藏表情";
}

/// <summary>
/// 右上角账号菜单 →「消息」弹出的右侧面板。会话列表 + 点进去的聊天窗：
/// 历史消息走 imapi get_by_conversation（向上翻页），发送走 /v1/message/send；
/// 表情走官方 emoji/list，表情包走 im/resources/emoticon/trending + 最近/收藏/搜索。
/// </summary>
public sealed partial class MessagesViewModel : INotifyPropertyChanged
{
    private readonly List<ImMessage> _chatMessages = [];
    private readonly Dictionary<string, ImContact> _chatUsers = new(StringComparer.Ordinal);

    private bool _isLoading;
    private string _status = string.Empty;

    private ImConversation? _chat;
    private bool _chatOpen;
    private bool _chatLoading;
    private bool _loadingOlder;
    private bool _sending;
    private string _chatStatus = string.Empty;
    private string _sendStatus = string.Empty;
    private long _olderCursor;
    private bool _hasMore;

    private IReadOnlyList<ImSticker> _stickers = [];
    private IReadOnlyList<ImSticker> _recentStickers = [];
    private readonly ObservableCollection<StickerItem> _stickerItems = [];
    private Task<IReadOnlyList<ImSticker>>? _stickerTask;
    private Task<IReadOnlyList<EmojiItem>>? _emojiTask;
    private StickerSource _stickerPane = StickerSource.Trending;
    private string _stickerStatus = string.Empty;
    private bool _stickerLoading;
    private string _searchKeyword = string.Empty;
    private long _searchCursor;
    private bool _searchHasMore;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<MessageRow> Rows { get; } = [];

    public ObservableCollection<ChatMessageRow> ChatRows { get; } = [];

    /// <summary>表情包面板当前这一页的格子（最近/收藏/热门/搜索结果共用）。</summary>
    public ObservableCollection<StickerItem> StickerItems => _stickerItems;

    public string StickerStatus => _stickerStatus;

    public Visibility StickerStatusVisibility =>
        !_stickerLoading && _stickerStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility StickerLoadingVisibility => _stickerLoading ? Visibility.Visible : Visibility.Collapsed;

    public Visibility StickerSearchVisibility => _stickerPane == StickerSource.Search ? Visibility.Visible : Visibility.Collapsed;

    public Visibility StickerMoreVisibility =>
        _stickerPane == StickerSource.Search && _searchHasMore && !_stickerLoading ? Visibility.Visible : Visibility.Collapsed;

    public string Status => _status;

    public Visibility LoadingVisibility => _isLoading ? Visibility.Visible : Visibility.Collapsed;

    public Visibility StatusVisibility => !_isLoading && _status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ListVisibility => _chatOpen ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ChatVisibility => _chatOpen ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ChatLoadingVisibility => _chatLoading ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ChatStatusVisibility =>
        !_chatLoading && _chatStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility OlderVisibility => _hasMore && !_chatLoading ? Visibility.Visible : Visibility.Collapsed;

    public string ChatTitle => _chat?.Title is { Length: > 0 } title ? title : "抖音用户";

    public string ChatInitials => ChatTitle[..1];

    public ImageSource? ChatAvatar => _chat is null ? null : MessageRow.ImageOf(_chat.AvatarUrl);

    public string ChatStatus => _chatStatus;

    public string SendStatus => _sendStatus;

    public Visibility SendStatusVisibility => _sendStatus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public async Task LoadAsync()
    {
        if (_isLoading)
        {
            return;
        }

        if (!Douyin.Feed.IsLoggedIn)
        {
            Reset();
            _status = "登录后可以查看消息";
            NotifyState();
            return;
        }

        _isLoading = true;
        _status = string.Empty;
        NotifyState();
        try
        {
            IReadOnlyList<ImConversation> list =
                await Douyin.Feed.ImConversationsAsync(force: false, CancellationToken.None);

            Rows.Clear();
            foreach (ImConversation conversation in list)
            {
                Rows.Add(new MessageRow(conversation));
            }

            _status = Rows.Count == 0 ? "还没有消息" : string.Empty;
        }
        catch (Exception ex)
        {
            _status = "消息读取失败：" + ex.Message;
        }
        finally
        {
            _isLoading = false;
            NotifyState();
        }
    }

    /// <summary>点会话 → 进聊天窗，先拉最新一页（50 条）。</summary>
    public async Task OpenChatAsync(MessageRow row)
    {
        _chat = row.Conversation;
        _chatOpen = true;
        _chatLoading = true;
        _chatStatus = string.Empty;
        _sendStatus = string.Empty;
        _hasMore = false;
        _olderCursor = 0;
        _chatMessages.Clear();
        _chatUsers.Clear();
        ChatRows.Clear();
        NotifyChatState();

        await LoadChatPageAsync(first: true);
    }

    /// <summary>收起聊天窗回会话列表（数据留在 Rows，不重新拉）。</summary>
    public void CloseChat()
    {
        _chat = null;
        _chatOpen = false;
        _chatMessages.Clear();
        _chatUsers.Clear();
        ChatRows.Clear();
        _sendStatus = string.Empty;
        NotifyChatState();
    }

    /// <summary>退出登录 / 面板重开：整块清掉，别把上一个号的消息留在内存里。</summary>
    public void Reset()
    {
        _chat = null;
        _chatOpen = false;
        _chatLoading = false;
        _loadingOlder = false;
        _sending = false;
        _chatStatus = string.Empty;
        _sendStatus = string.Empty;
        _hasMore = false;
        _olderCursor = 0;
        _chatMessages.Clear();
        _chatUsers.Clear();
        ChatRows.Clear();
        Rows.Clear();
        _stickerItems.Clear();
        _stickerStatus = string.Empty;
        _stickerPane = StickerSource.Trending;
        _searchKeyword = string.Empty;
        _searchCursor = 0;
        _searchHasMore = false;
        _status = string.Empty;
        _isLoading = false;
        NotifyState();
        NotifyChatState();
        NotifyStickerState();
    }

    /// <summary>向上翻一页更早的消息（往 _chatMessages 头部并，重建时间分隔）。</summary>
    public async Task LoadOlderAsync()
    {
        if (_chat is null || _chatLoading || _loadingOlder || !_hasMore)
        {
            return;
        }

        _loadingOlder = true;
        NotifyChatState();
        try
        {
            ImMessagePage page = await Douyin.Feed.ImMessagesAsync(_chat, _olderCursor, CancellationToken.None);
            var known = _chatMessages.Select(KeyOf).ToHashSet(StringComparer.Ordinal);
            foreach (ImMessage message in page.Items)
            {
                if (known.Add(KeyOf(message)))
                {
                    _chatMessages.Add(message);
                }
            }

            _chatMessages.Sort(static (a, b) =>
            {
                long ta = ChatMessageRow.TimeOf(a);
                long tb = ChatMessageRow.TimeOf(b);
                int byTime = ta.CompareTo(tb);
                return byTime != 0 ? byTime : a.Seq.CompareTo(b.Seq);
            });

            Task usersTask = LoadChatUsersAsync(page.Items);
            _olderCursor = page.MinSortUs > 0 ? page.MinSortUs : _olderCursor;
            _hasMore = page.HasMore && page.Items.Count > 0;

            // 气泡里的 [码] 要在建行之前有表情表，否则首开会一直显示字面码。
            // 表是一次运行一份的缓存，取不到就照常出字（不阻塞聊天）；群成员资料与表情表并行取。
            try
            {
                await EnsureEmojisAsync();
            }
            catch
            {
            }

            await usersTask;
            RebuildChatRows();
            _chatStatus = _chatMessages.Count == 0 ? "还没有消息" : string.Empty;
        }
        catch (Exception ex)
        {
            _chatStatus = "更早的消息没读回来：" + ex.Message;
        }
        finally
        {
            _loadingOlder = false;
            NotifyChatState();
        }
    }

    /// <summary>发文本（含官方表情码）。成功本地先贴一条，输入框由界面清。</summary>
    public async Task<bool> SendTextAsync(string text)
    {
        if (_chat is null || _sending || text.Length == 0)
        {
            return false;
        }

        _sending = true;
        _sendStatus = string.Empty;
        NotifyChatState();
        try
        {
            (bool ok, long serverId, string note) =
                await Douyin.Feed.ImSendTextAsync(_chat, text, CancellationToken.None);
            if (!ok)
            {
                _sendStatus = note;
                return false;
            }

            AppendOwn(Douyin.Feed.ImTextContent(text), 7, serverId);
            TouchPreview(text);
            return true;
        }
        catch (Exception ex)
        {
            _sendStatus = "发送失败：" + ex.Message;
            return false;
        }
        finally
        {
            _sending = false;
            NotifyChatState();
        }
    }

    /// <summary>发一张表情包（网页版点一下直接发，message_type=5）。</summary>
    public async Task<bool> SendStickerAsync(ImSticker sticker)
    {
        if (_chat is null || _sending)
        {
            return false;
        }

        _sending = true;
        _sendStatus = string.Empty;
        NotifyChatState();
        try
        {
            (bool ok, long serverId, string note) =
                await Douyin.Feed.ImSendStickerAsync(_chat, sticker, CancellationToken.None);
            if (!ok)
            {
                _sendStatus = note;
                return false;
            }

            AppendOwn(Douyin.Feed.ImStickerContent(sticker), 5, serverId);
            TouchPreview("[表情]");
            _ = RecordRecentSafeAsync(sticker);
            return true;
        }
        catch (Exception ex)
        {
            _sendStatus = "表情发不出去：" + ex.Message;
            return false;
        }
        finally
        {
            _sending = false;
            NotifyChatState();
        }
    }

    /// <summary>发送后上报「最近使用」；失败不影响已发出去的表情包。</summary>
    private async Task RecordRecentSafeAsync(ImSticker sticker)
    {
        if (_chat is null)
        {
            return;
        }

        try
        {
            await Douyin.Feed.ImStickerRecentRecordAsync(sticker, _chat.Id, CancellationToken.None);
        }
        catch
        {
            // 最近列表是锦上添花
        }
    }

    /// <summary>官方表情表：一次运行取一次（和评论/弹幕共用 EmojiCatalog）。</summary>
    internal async Task<IReadOnlyList<EmojiItem>> EnsureEmojisAsync()
    {
        if (EmojiCatalog.Count > 0)
        {
            return EmojiCatalog.List;
        }

        try
        {
            return await (_emojiTask ??= LoadEmojisAsync());
        }
        catch
        {
            _emojiTask = null;
            throw;
        }
    }

    private static async Task<IReadOnlyList<EmojiItem>> LoadEmojisAsync()
    {
        IReadOnlyList<EmojiItem> list = await Douyin.Feed.EmojiListAsync(CancellationToken.None);
        EmojiCatalog.Set(list);
        return EmojiCatalog.List;
    }

    /// <summary>表情包资源（热门那一组）：View 只取一次；失败放空、下次再试。</summary>
    public async Task<IReadOnlyList<ImSticker>> EnsureStickersAsync()
    {
        if (_stickers.Count > 0)
        {
            return _stickers;
        }

        try
        {
            _stickers = await (_stickerTask ??= Douyin.Feed.ImStickersAsync(force: false, CancellationToken.None));
            return _stickers;
        }
        catch
        {
            _stickerTask = null;
            throw;
        }
        finally
        {
            _stickerTask = null;
        }
    }

    /// <summary>面板预取/打开收藏 tab 前先把「我的收藏」取上：主要是为了让热门/搜索里的右键菜单认得出已收藏。</summary>
    public async Task EnsureFavoritesAsync()
    {
        try
        {
            await Douyin.Feed.ImFavoriteStickersAsync(force: false, CancellationToken.None);
        }
        catch
        {
            // 收藏列表取不到不影响热门/搜索/发送
        }
    }

    /// <summary>切表情包来源：最近 / 收藏 / 热门 / 搜索。</summary>
    public async Task ShowStickerPaneAsync(StickerSource pane)
    {
        _stickerPane = pane;
        _stickerStatus = string.Empty;
        _stickerLoading = true;
        _searchHasMore = false;
        NotifyStickerState();
        try
        {
            switch (pane)
            {
                case StickerSource.Recent:
                    _recentStickers = await Douyin.Feed.ImRecentStickersAsync(CancellationToken.None);
                    FillStickerItems(_recentStickers, allFavorite: false);
                    if (_stickerItems.Count == 0)
                    {
                        _stickerStatus = "还没有最近用过的表情包";
                    }

                    break;

                case StickerSource.Favorite:
                    IReadOnlyList<ImSticker> favorites =
                        await Douyin.Feed.ImFavoriteStickersAsync(force: false, CancellationToken.None);
                    FillStickerItems(favorites, allFavorite: true);
                    if (_stickerItems.Count == 0)
                    {
                        _stickerStatus = "还没有收藏表情包。在热门或搜索结果上点右键可以收藏。";
                    }

                    break;

                case StickerSource.Trending:
                    FillStickerItems(await EnsureStickersAsync(), allFavorite: false);
                    if (_stickerItems.Count == 0)
                    {
                        _stickerStatus = "表情包是空的";
                    }

                    break;

                case StickerSource.Search:
                    if (_searchKeyword.Length == 0)
                    {
                        _stickerItems.Clear();
                        _stickerStatus = "输入关键词，搜官方表情包";
                    }
                    else
                    {
                        await SearchFirstPageAsync();
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            _stickerStatus = "表情包读取失败：" + ex.Message;
        }
        finally
        {
            _stickerLoading = false;
            NotifyStickerState();
        }
    }

    /// <summary>搜索框回车/点搜索：换关键词，从第一页开始。</summary>
    public async Task SearchStickersAsync(string keyword)
    {
        _searchKeyword = keyword.Trim();
        _stickerPane = StickerSource.Search;
        _stickerStatus = string.Empty;
        _searchCursor = 0;
        _searchHasMore = false;
        _stickerLoading = true;
        NotifyStickerState();
        try
        {
            if (_searchKeyword.Length == 0)
            {
                _stickerItems.Clear();
                _stickerStatus = "输入关键词，搜官方表情包";
                return;
            }

            await SearchFirstPageAsync();
        }
        catch (Exception ex)
        {
            _stickerStatus = "搜索失败：" + ex.Message;
        }
        finally
        {
            _stickerLoading = false;
            NotifyStickerState();
        }
    }

    /// <summary>搜索结果「看更多」：按 next_cursor 接一页。</summary>
    public async Task LoadMoreStickersAsync()
    {
        if (_stickerPane != StickerSource.Search || !_searchHasMore || _stickerLoading || _searchKeyword.Length == 0)
        {
            return;
        }

        _stickerLoading = true;
        NotifyStickerState();
        try
        {
            ImStickerPage page = await Douyin.Feed.ImSearchStickersAsync(
                _searchKeyword, _searchCursor, _chat?.Id ?? string.Empty, CancellationToken.None);
            AppendStickerItems(page.Items, allFavorite: false);
            _searchCursor = page.NextCursor;
            _searchHasMore = page.HasMore;
            if (page.Items.Count == 0)
            {
                _stickerStatus = "没有更多了";
            }
        }
        catch (Exception ex)
        {
            _stickerStatus = "接下一页失败：" + ex.Message;
        }
        finally
        {
            _stickerLoading = false;
            NotifyStickerState();
        }
    }

    /// <summary>右键菜单收藏/取消收藏；成功后就地翻文案，收藏 tab 里取消的当场移出。</summary>
    public async Task ToggleStickerFavoriteAsync(string id)
    {
        StickerItem? item = _stickerItems.FirstOrDefault(s => s.Id == id);
        if (item is null)
        {
            return;
        }

        bool collect = !item.IsFavorite;
        _stickerStatus = string.Empty;
        NotifyStickerState();
        try
        {
            (bool ok, string note) =
                await Douyin.Feed.ImStickerCollectAsync(item.Sticker, collect, CancellationToken.None);
            _stickerStatus = note;
            if (!ok)
            {
                return;
            }

            item.IsFavorite = collect;
            if (_stickerPane == StickerSource.Favorite && !collect)
            {
                _stickerItems.Remove(item);
                if (_stickerItems.Count == 0)
                {
                    _stickerStatus = "还没有收藏表情包。在热门或搜索结果上点右键可以收藏。";
                }
            }

            _ = ClearStickerStatusAsync(note);
        }
        catch (Exception ex)
        {
            _stickerStatus = "收藏失败：" + ex.Message;
        }
        finally
        {
            NotifyStickerState();
        }
    }

    private async Task SearchFirstPageAsync()
    {
        _stickerItems.Clear();
        ImStickerPage page = await Douyin.Feed.ImSearchStickersAsync(
            _searchKeyword, 0, _chat?.Id ?? string.Empty, CancellationToken.None);
        FillStickerItems(page.Items, allFavorite: false);
        _searchCursor = page.NextCursor;
        _searchHasMore = page.HasMore;
        if (_stickerItems.Count == 0)
        {
            _stickerStatus = "没搜到「" + _searchKeyword + "」";
        }
    }

    private void FillStickerItems(IReadOnlyList<ImSticker> list, bool allFavorite)
    {
        _stickerItems.Clear();
        AppendStickerItems(list, allFavorite);
    }

    private void AppendStickerItems(IReadOnlyList<ImSticker> list, bool allFavorite)
    {
        var known = _stickerItems.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (ImSticker sticker in list)
        {
            if (sticker.Id.Length > 0 && !known.Add(sticker.Id))
            {
                continue;
            }

            bool favorite = allFavorite || Douyin.Feed.IsImStickerFavorite(sticker.Id);
            _stickerItems.Add(new StickerItem(sticker, favorite));
        }
    }

    /// <summary>成功提示只闪一下，不长期压在表情格子上。</summary>
    private async Task ClearStickerStatusAsync(string expected)
    {
        await Task.Delay(2000);
        if (_stickerStatus == expected)
        {
            _stickerStatus = string.Empty;
            NotifyStickerState();
        }
    }

    private void NotifyStickerState()
    {
        Notify(nameof(StickerStatus));
        Notify(nameof(StickerStatusVisibility));
        Notify(nameof(StickerLoadingVisibility));
        Notify(nameof(StickerSearchVisibility));
        Notify(nameof(StickerMoreVisibility));
    }

    private async Task LoadChatPageAsync(bool first)
    {
        if (_chat is null)
        {
            return;
        }

        ImConversation chat = _chat;
        _chatLoading = true;
        if (first)
        {
            _chatStatus = string.Empty;
        }

        NotifyChatState();
        try
        {
            long before = first
                ? chat.LastSortUs > 0
                    ? chat.LastSortUs + 10_000
                    : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000
                : _olderCursor;
            ImMessagePage page = await Douyin.Feed.ImMessagesAsync(chat, before, CancellationToken.None);
            if (first)
            {
                _chatMessages.Clear();
                _chatMessages.AddRange(page.Items);
            }
            else
            {
                var known = _chatMessages.Select(KeyOf).ToHashSet(StringComparer.Ordinal);
                foreach (ImMessage message in page.Items)
                {
                    if (known.Add(KeyOf(message)))
                    {
                        _chatMessages.Add(message);
                    }
                }

                _chatMessages.Sort(static (a, b) => ChatMessageRow.TimeOf(a).CompareTo(ChatMessageRow.TimeOf(b)));
            }

            Task usersTask = LoadChatUsersAsync(page.Items);
            _olderCursor = page.MinSortUs > 0 ? page.MinSortUs : _olderCursor;
            _hasMore = page.HasMore && page.Items.Count > 0;

            // 气泡里的 [码] 要在建行之前有表情表，否则首开会一直显示字面码。
            // 表是一次运行一份的缓存，取不到就照常出字（不阻塞聊天）；群成员资料与表情表并行取。
            try
            {
                await EnsureEmojisAsync();
            }
            catch
            {
            }

            await usersTask;
            RebuildChatRows();
            _chatStatus = _chatMessages.Count == 0 ? "还没有消息，发一条打个招呼吧" : string.Empty;
        }
        catch (Exception ex)
        {
            _chatStatus = "聊天记录读取失败：" + ex.Message;
        }
        finally
        {
            _chatLoading = false;
            NotifyChatState();
        }
    }

    /// <summary>
    /// 群聊里每条消息只带发送者 uid/sec_uid，昵称和头像要另取。一页里出现的发送者去重后
    /// 走一次 im/user/info（30 个一批），失败就回落群头像，不阻塞消息上屏。
    /// </summary>
    private async Task LoadChatUsersAsync(IReadOnlyList<ImMessage> messages)
    {
        if (_chat is null || _chat.Type != 2)
        {
            return;
        }

        var wanted = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ImMessage message in messages)
        {
            if (message.IsMine || message.MessageType is 1 or 1001)
            {
                continue;
            }

            string sec = message.SenderSecUid;
            if (sec.Length == 0 || _chatUsers.ContainsKey(sec) || !seen.Add(sec))
            {
                continue;
            }

            wanted.Add(sec);
        }

        for (int i = 0; i < wanted.Count; i += 30)
        {
            var batch = wanted.Skip(i).Take(30).ToList();
            try
            {
                IReadOnlyDictionary<string, ImContact> map =
                    await Douyin.Feed.ImUsersAsync(batch, CancellationToken.None);
                foreach ((string sec, ImContact user) in map)
                {
                    _chatUsers[sec] = user;
                }
            }
            catch
            {
                // 拿不到名字就先用群头像/空名字，不挡消息
            }
        }
    }

    /// <summary>
    /// 点引用：先在已加载的行里认被回复那条（按 server id，缺 id 才按 sort）；
    /// 没命中就往上翻最多 6 页再认。翻完还没有就把话写在发送状态那一行，不另开提示位。
    /// </summary>
    public async Task<ChatMessageRow?> LocateQuotedRowAsync(ChatMessageRow source)
    {
        ChatMessageRow? hit = MatchQuote(source);
        for (int attempt = 0; hit is null && attempt < 6; attempt++)
        {
            if (!_hasMore || _chatLoading || _loadingOlder)
            {
                break;
            }

            int before = ChatRows.Count;
            try
            {
                await LoadOlderAsync();
            }
            catch
            {
                break;
            }

            if (ChatRows.Count == before)
            {
                break;
            }

            hit = MatchQuote(source);
        }

        if (hit is null)
        {
            SetSendNote(source.ReferServerId == 0 && source.ReferSortUs == 0
                ? "这条回复没带原消息的编号，跳不过去"
                : "没找到被回复的那条消息（已翻到本地历史的最前面）");
        }

        return hit;
    }

    private ChatMessageRow? MatchQuote(ChatMessageRow source)
    {
        foreach (ChatMessageRow row in ChatRows)
        {
            if (source.ReferServerId > 0 && row.ServerId == source.ReferServerId)
            {
                return row;
            }

            if (source.ReferServerId == 0 && source.ReferSortUs > 0 && row.SortUs == source.ReferSortUs)
            {
                return row;
            }
        }

        return null;
    }

    private void SetSendNote(string note)
    {
        _sendStatus = note;
        Notify(nameof(SendStatus));
        Notify(nameof(SendStatusVisibility));
    }

    private void RebuildChatRows()
    {
        ChatRows.Clear();
        if (_chat is null)
        {
            return;
        }

        bool group = _chat.Type == 2;
        long previous = 0;
        string previousSender = string.Empty;
        foreach (ImMessage message in _chatMessages)
        {
            long time = ChatMessageRow.TimeOf(message);
            bool showTime = previous == 0 || time - previous >= 5 * 60 * 1000;
            ImContact? sender = null;
            if (group && !message.IsMine && message.SenderSecUid.Length > 0)
            {
                _chatUsers.TryGetValue(message.SenderSecUid, out sender);
            }

            bool showSender = showTime || message.SenderUid != previousSender;
            string quoteSender = string.Empty;
            if (message.ReferType > 0 && message.ReferSenderName.Length == 0)
            {
                if (message.ReferSenderUid == Douyin.Feed.MyUid)
                {
                    quoteSender = "我";
                }
                else if (!group)
                {
                    quoteSender = _chat.Title;
                }
                else if (message.ReferSenderSecUid.Length > 0
                    && _chatUsers.TryGetValue(message.ReferSenderSecUid, out ImContact? quoteUser))
                {
                    quoteSender = quoteUser.DisplayName;
                }
            }

            ChatRows.Add(new ChatMessageRow(message, _chat, sender, showSender, quoteSender));
            previous = time;
            previousSender = message.SenderUid;
        }
    }

    private void AppendOwn(string content, int messageType, long serverId)
    {
        if (_chat is null)
        {
            return;
        }

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _chatMessages.Add(new ImMessage(
            _chat.Id, serverId, messageType, messageType,
            Douyin.Feed.MyUid, Douyin.Feed.MySecUid, content,
            nowMs, nowMs * 1000, 0, string.Empty, true));
        RebuildChatRows();
    }

    private void TouchPreview(string preview)
    {
        if (_chat is null)
        {
            return;
        }

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ImConversation updated = _chat with
        {
            Preview = preview,
            LastTimeMs = nowMs,
            LastSortUs = nowMs * 1000,
        };
        _chat = updated;

        for (int i = 0; i < Rows.Count; i++)
        {
            if (Rows[i].Conversation.Id == updated.Id)
            {
                Rows[i] = new MessageRow(updated);
                break;
            }
        }
    }

    private static string KeyOf(ImMessage message) =>
        message.ServerId > 0
            ? "s:" + message.ServerId
            : "c:" + message.SortUs + ":" + message.SenderUid + ":" + message.MessageType + ":" + message.Content;

    private void NotifyState()
    {
        Notify(nameof(Status));
        Notify(nameof(LoadingVisibility));
        Notify(nameof(StatusVisibility));
        Notify(nameof(ListVisibility));
        Notify(nameof(ChatVisibility));
    }

    private void NotifyChatState()
    {
        Notify(nameof(ListVisibility));
        Notify(nameof(ChatVisibility));
        Notify(nameof(ChatLoadingVisibility));
        Notify(nameof(ChatStatusVisibility));
        Notify(nameof(OlderVisibility));
        Notify(nameof(ChatTitle));
        Notify(nameof(ChatInitials));
        Notify(nameof(ChatAvatar));
        Notify(nameof(ChatStatus));
        Notify(nameof(SendStatus));
        Notify(nameof(SendStatusVisibility));
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
