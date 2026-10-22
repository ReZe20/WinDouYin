namespace WinDouYin;

/// <summary>抖音式相对时间：刚刚/x分钟前/x小时前/x天前；超过一个月给日期。</summary>
internal static class RelativeTime
{
    /// <param name="unixSeconds">aweme 的 create_time，unix 秒。</param>
    public static string Format(long unixSeconds)
    {
        if (unixSeconds <= 0)
        {
            return string.Empty;
        }

        DateTimeOffset published = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        TimeSpan age = DateTimeOffset.UtcNow - published;
        if (age < TimeSpan.Zero)
        {
            // 服务器时钟比本机快一点时按"刚刚"处理，别显示负数
            age = TimeSpan.Zero;
        }

        if (age < TimeSpan.FromMinutes(1))
        {
            return "刚刚";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return (int)age.TotalMinutes + "分钟前";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return (int)age.TotalHours + "小时前";
        }

        if (age < TimeSpan.FromDays(30))
        {
            return (int)age.TotalDays + "天前";
        }

        DateTimeOffset local = published.ToLocalTime();
        return local.Year == DateTimeOffset.Now.Year
            ? local.Month + "月" + local.Day + "日"
            : local.Year + "年" + local.Month + "月" + local.Day + "日";
    }
}
