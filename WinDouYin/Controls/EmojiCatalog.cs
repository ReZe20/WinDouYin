using WinDouYin.Contracts;

namespace WinDouYin.Controls;

/// <summary>
/// 官方表情码 → 图。面板、输入框标签、评论正文三处共用同一份表：
/// 面板按 <see cref="IndexOf"/> 记序号，正文按码查图。
/// 表来自 <c>emoji/list</c>（hide=0 的那一套），一次运行只装一次。
/// </summary>
internal static class EmojiCatalog
{
    private static List<EmojiItem> _list = [];
    private static Dictionary<string, string> _urls = new(StringComparer.Ordinal);

    /// <summary>表里现有多少条（0 = 还没取到）。</summary>
    public static int Count => _list.Count;

    public static IReadOnlyList<EmojiItem> List => _list;

    /// <summary>装表：只在真的拿到东西时替换，失败的空白不清掉已有的。</summary>
    public static void Set(IReadOnlyList<EmojiItem> list)
    {
        if (list.Count == 0)
        {
            return;
        }

        _list = [.. list];
        _urls = new Dictionary<string, string>(list.Count, StringComparer.Ordinal);
        foreach (EmojiItem emoji in _list)
        {
            _urls.TryAdd(emoji.Code, emoji.ImageUrl);
        }
    }

    public static int IndexOf(EmojiItem emoji)
    {
        for (int i = 0; i < _list.Count; i++)
        {
            if (_list[i] == emoji)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>序号 → 码（输入框里的透明标签字符靠它还原成发送正文）。</summary>
    public static string CodeAt(int index) =>
        index >= 0 && index < _list.Count ? _list[index].Code : string.Empty;

    public static bool TryGetUrl(string code, out string url) => _urls.TryGetValue(code, out url!);
}
