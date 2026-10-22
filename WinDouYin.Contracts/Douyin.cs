namespace WinDouYin.Contracts;

/// <summary>
/// 取数层的挂载点。实现住在闭源那半（只碰抖音服务器的那层），由 App 启动时 <see cref="Attach"/> 一次。
/// 界面侧从头到尾只认 <see cref="IDouyinFeed"/> 与同目录的开源类型，编译期碰不到任何报文形状——
/// 所以公开仓没有闭源工程也能独立编译。
/// </summary>
public static class Douyin
{
    private static IDouyinFeed? _feed;

    /// <summary>取数门面。没挂上就直接炸，别静默给一套假数据。</summary>
    public static IDouyinFeed Feed => _feed ?? throw new InvalidOperationException(
        "取数层还没挂上：App 启动里要先调一次 Douyin.Attach(...)。");

    public static bool Attached => _feed is not null;

    public static void Attach(IDouyinFeed feed) => _feed = feed;
}
