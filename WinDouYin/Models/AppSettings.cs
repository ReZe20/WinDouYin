namespace WinDouYin.Models;

/// <summary>
/// 唯一持久化模型：属性名即 settings.json 的键，改名要配套迁移。
/// </summary>
public class AppSettings
{
    /// <summary>配置结构版本号，破坏性变更时递增并在迁移里补步骤。</summary>
    public int Version { get; set; } = 1;

    /// <summary>启动页面 Tag，取值见 <see cref="AppPages.StartupChoices"/>。</summary>
    public string StartupPage { get; set; } = "Featured";

    /// <summary>播放音量，0~100 的整数百分比，和音量滑块的档位一致。</summary>
    public int Volume { get; set; } = 100;

    /// <summary>自动连播：一条播完自动放列表里的下一条。跨启动记住（用户 2026-10-13 点名要持久化）。</summary>
    public bool AutoPlay { get; set; }

    /// <summary>小窗的客户区宽度（物理像素）：用户拖过就记住，下次开小窗照这个尺寸来。</summary>
    public int MiniWidth { get; set; } = 400;

    /// <summary>小窗的客户区高度（物理像素）。</summary>
    public int MiniHeight { get; set; } = 225;

    /// <summary>弹幕开关：关掉就整层收起并清空。跨启动记住（用户 2026-10-13 点名要持久化）。</summary>
    public bool DanmakuOn { get; set; } = true;

    /// <summary>弹幕不透明度（百分比 0~100，面板给的是滑块值）。</summary>
    public int DanmakuOpacity { get; set; } = 80;

    /// <summary>弹幕显示区域：0/1/2 → 单行/两行/三行。</summary>
    public int DanmakuRows { get; set; } = 1;

    /// <summary>弹幕字体档：0/1/2 → 较小/适中/较大。</summary>
    public int DanmakuFont { get; set; } = 1;

    /// <summary>弹幕速度档：0/1/2 → 较慢/适中/较快。</summary>
    public int DanmakuSpeed { get; set; } = 1;

    /// <summary>
    /// 视频磁盘缓存：把看过的视频分块落到本机（上限 512MB、低 IO 优先级、写限速），
    /// 重新打开时起播更快。关掉后不再读写磁盘，已有缓存留着不删。
    /// </summary>
    public bool VideoDiskCache { get; set; } = true;

    /// <summary>
    /// 清晰度档：空串 = 默认（feed 下发的那条流），否则是分辨率数字（"1080" / "720" / "540"…）。
    /// 跟着一条条视频沿用；这条视频没有该档时自动降档（见 MfStageControl.PickQuality）。
    /// </summary>
    public string QualityKey { get; set; } = string.Empty;
}
