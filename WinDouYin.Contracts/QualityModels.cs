namespace WinDouYin.Contracts;

/// <summary>
/// 一档清晰度：<c>Key</c> 是档位标识、<c>Label</c> 是界面上的字面（"1080P"）、<c>Url</c> 是这一档自己的播放地址。
/// 这是 <c>NativeQuality</c> 的开源对面——档位从接口哪一格摊平而来属于取数层，界面只认这三样。
/// </summary>
public sealed record VideoQuality(string Key, string Label, string Url);

/// <summary>
/// 一条视频能播的档（从高到低）+ 默认流现在是哪一档。
/// <c>DefaultKey</c> 空串表示对不上（菜单就别勾任何东西）。
/// </summary>
public sealed record VideoQualitySet(IReadOnlyList<VideoQuality> Items, string DefaultKey)
{
    /// <summary>没有档位数据（图文帖、老报文）：菜单全置灰，只放下发的那条。</summary>
    public static readonly VideoQualitySet Empty = new([], string.Empty);
}
