namespace WinDouYin.Contracts;

/// <summary>
/// 评论里带的一张图。<c>Url</c> 原图、<c>ThumbUrl</c> 缩略（列表铺这一枚，点开才用原图）。
/// 宽高是给"按比例占位"用的，别自己猜。
/// </summary>
public sealed record CommentImageItem(string Url, string ThumbUrl, int Width, int Height);

/// <summary>
/// 评论里的一枚表情包。这里统一给**静图**地址：动图是 webp 动画，WinUI 的 Image 只画第一帧，
/// 摆静图反而不误导（宽高照原文带，占位用）。
/// </summary>
public sealed record CommentStickerItem(string Url, int Width, int Height);

/// <summary>
/// 一条一级评论。<see cref="CreateTime"/> 是 unix 秒，相对时间由界面自己格式化（别在这儿拼字）。
/// 带图或带表情包的评论正文可能**是空串**，所以判"这条有没有内容"不能只看 <see cref="Text"/>。
/// </summary>
public sealed record CommentItem(
    string Id, string Author, string Text, int Digg, int CreateTime,
    string Avatar = "", int ReplyTotal = 0,
    IReadOnlyList<CommentImageItem>? Images = null, CommentStickerItem? Sticker = null, bool Liked = false);

/// <summary>
/// 一页评论。<see cref="Cursor"/> 是下一页要传回的游标；
/// <c>HasMore=false</c> 或游标没动就是到底了（两种都要认，服务端两种都发过）。
/// </summary>
public sealed record CommentPage(IReadOnlyList<CommentItem> Items, long Cursor, bool HasMore, int Total);

/// <summary>
/// 一条二级评论（回复）。与一级同形，另带 <see cref="ReplyToId"/>（在回复谁；空=直接回一级）与
/// <see cref="Level"/>。接口回的是**按时间平铺的一整串**，三级也在里面，靠 reply_to 链在界面侧还原成树。
/// </summary>
public sealed record ReplyItem(
    string Id, string Author, string Text, int Digg, int CreateTime,
    string Avatar = "", string ReplyToId = "", int Level = 0,
    IReadOnlyList<CommentImageItem>? Images = null, CommentStickerItem? Sticker = null, bool Liked = false);

/// <summary>一页回复。<see cref="Ok"/>=false 表示这一发没拿到正经内容（空体或网络失败），不等于"没有回复"。</summary>
public sealed record ReplyPage(IReadOnlyList<ReplyItem> Items, long Cursor, bool HasMore, int Total, bool Ok = true);

/// <summary>官方表情表里的一条：<see cref="Code"/> 就是正文里那个码（<c>[微笑]</c>），<see cref="ImageUrl"/> 是图。</summary>
public sealed record EmojiItem(string Code, string ImageUrl);
