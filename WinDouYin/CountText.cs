namespace WinDouYin;

/// <summary>
/// 抖音式计数：不足一万照抄，一万以上收成 w（官方竖排就是带一位小数的 w），亿以上收成 亿。
/// </summary>
internal static class CountText
{
    public static string Format(int count)
    {
        if (count < 10_000)
        {
            return count.ToString();
        }

        return count < 100_000_000
            ? Trim(count / 10_000.0) + "w"
            : Trim(count / 100_000_000.0) + "亿";
    }

    /// <summary>
    /// 中文档（万/亿），用户主页那三个数用这个。口径照 2026-10-20 官方主页实测：
    /// 万档**保留一位小数**（「1677.0万」），亿档才把 .0 收掉（「1.5亿」）。
    /// 与 <see cref="Format"/> 分开是有意的——搜索位官方用「万」但项目里那处仍是 w（第五十四轮记的未改项），
    /// 谁要切中文档改调用点就行，别把这枚方法改回 w。
    /// </summary>
    public static string FormatCn(int count)
    {
        if (count < 10_000)
        {
            return count.ToString();
        }

        return count < 100_000_000
            ? (count / 10_000.0).ToString("F1") + "万"
            : Trim(count / 100_000_000.0) + "亿";
    }

    private static string Trim(double value)
    {
        string text = value.ToString("F1");
        return text.EndsWith(".0", StringComparison.Ordinal) ? text[..^2] : text;
    }
}
