using WinDouYin.Views;

namespace WinDouYin;

public sealed record AppPage(string Tag, string TitleKey, Type PageType, bool SelfProfile = false)
{
    /// <summary>界面显示名：每次取值都按当前显示语言从 resw 解析，供下拉/标题绑定。</summary>
    public string DisplayName => Strings.Get(TitleKey);
}

public static class AppPages
{
    // 顺序即"启动页面"下拉里的顺序；不含设置页本身
    public static readonly IReadOnlyList<AppPage> StartupChoices =
    [
        new("Featured", "Nav_Featured", typeof(FeaturedPage)),
        new("ForYou", "Nav_ForYou", typeof(ForYouPage)),
        new("Ai", "Nav_Ai", typeof(AiPage)),
        new("Following", "Nav_Following", typeof(FollowingPage)),
        new("Friends", "Nav_Friends", typeof(FriendsPage)),
        new("Me", "Nav_Me", typeof(MePage)),
        new("Live", "Nav_Live", typeof(LivePage)),
        // 本人主页：不只换页名，进去还要先补自己的 sec_uid，导航时按 SelfProfile 走 OpenSelf
        new("Account", "Nav_Account", typeof(UserProfilePage), SelfProfile: true),
    ];

    public static AppPage? Find(string? tag) =>
        tag is null ? null : StartupChoices.FirstOrDefault(p => p.Tag == tag);
}
