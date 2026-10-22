using WinDouYin.Contracts;

namespace WinDouYin;

/// <summary>
/// 取数那半对界面侧**唯一的** public 入口。这一枚文件两个仓都有、也看得见——它就是接缝本身；
/// 实现（<c>Native/</c> 报文与会话、<c>Signing/</c> 三套签名、<c>FeedService</c> 会话罐与多账号倒罐、
/// 存 cookie/guard 材料与服务端回包快照的那三个 Service）只在私有仓，其余符号一律 <c>internal</c>。
///
/// 为什么要这么一枚：<c>FeedService</c> 是 internal，跨程序集够不着，所以把"挂门面"这一步放进取数侧，
/// 界面只剩一句 <see cref="Attach"/>。而公开仓那一边，同一句 <see cref="Attach"/> 挂上的是
/// <see cref="StubFeed"/>（档位由 <c>WinDouYin.Douyin.csproj</c> 按"真源码在不在"决定，
/// 编译期靠 <c>DOUYIN_STUB</c> 分叉）——于是 <c>App.xaml.cs</c> 那两句在两个仓里一模一样。
/// </summary>
public static class DouyinBoot
{
    /// <summary>
    /// 把取数门面挂到 <see cref="Douyin"/> 定位器上（幂等：挂过就什么都不做，重复调不会换实现）。
    /// 必须在第一次取数之前调，且只调一次——之后全工程只认 <see cref="Douyin.Feed"/>。
    /// 会话还原不在这枚里：<c>await Douyin.Feed.RestoreSessionAsync()</c> 由调用方接着做，
    /// 因为那条是 await 门面成员，本来就属于开源侧能看见的契约面。
    /// </summary>
    public static void Attach()
    {
        if (Douyin.Attached)
        {
            return;
        }

#if DOUYIN_STUB
        Douyin.Attach(new StubFeed());
#else
        Douyin.Attach(FeedService.Facade);
#endif
    }
}
