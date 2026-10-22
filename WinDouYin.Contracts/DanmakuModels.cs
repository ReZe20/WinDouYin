namespace WinDouYin.Contracts;

/// <summary>
/// 一条弹幕：<see cref="Seconds"/> 是它在时间轴上的位置（取数层已把毫秒换算成秒）、
/// <see cref="Text"/> 正文、<see cref="UserId"/> 发送者、<see cref="DanmakuId"/> 是这条弹幕自己的身份
/// （点赞按它走，空身份就别摆赞按钮）、<see cref="Digg"/> 服务端赞数、<see cref="Liked"/> 我赞过没有。
/// 界面自己发出去的那条也要构造它，所以这里是可构造的普通 record。
/// </summary>
public sealed record DanmakuItem(
    double Seconds, string Text, string UserId, string DanmakuId, int Digg, bool Liked);

/// <summary>一页弹幕 + 下一页从哪个毫秒起（0 = 到头了）。续页游标用毫秒是取数层的形式，界面按它回传即可。</summary>
public sealed record DanmakuPage(IReadOnlyList<DanmakuItem> Items, long NextStartMs);
