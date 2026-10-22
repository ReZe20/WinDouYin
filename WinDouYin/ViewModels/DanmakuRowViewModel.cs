using CommunityToolkit.Mvvm.ComponentModel;
using WinDouYin.Contracts;

namespace WinDouYin.ViewModels;

/// <summary>
/// 弹幕列表里的一行：身份 + 拼好的显示字 + "点没点过赞"。
/// 字形与颜色是视图的事（XAML 里经转换器转，见 <c>Converters/DanmakuLikeGlyphConverter.cs</c> 与
/// <c>Converters/DanmakuLikeBrushConverter.cs</c>），这里只保管那份会变的状态——Liked 一变，绑定自己跟着走。
/// </summary>
public partial class DanmakuRowViewModel : ObservableObject
{
    public DanmakuRowViewModel(DanmakuItem item, string timeText, bool liked)
    {
        Item = item;
        DisplayText = timeText + "   " + item.Text;
        Liked = liked;
    }

    /// <summary>原始弹幕（身份 / 文字 / 服务端赞数都在这）。</summary>
    public DanmakuItem Item { get; }

    /// <summary>一行字：时间 + 弹幕正文（格式化的活在这一处收口）。</summary>
    public string DisplayText { get; }

    /// <summary>有没有弹幕 id：没有的话这行不放心——拿不到身份，赞不了。</summary>
    public bool CanLike => Item.DanmakuId.Length > 0;

    /// <summary>点没点过赞（别人赞过的数在 <see cref="DanmakuItem.Digg"/>；这里只管我自己这一票，跟 _likedDanmakuIds 一本账）。</summary>
    [ObservableProperty]
    public partial bool Liked { get; set; }
}
