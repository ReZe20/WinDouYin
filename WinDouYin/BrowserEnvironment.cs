using Microsoft.Web.WebView2.Core;

namespace WinDouYin;

/// <summary>
/// WebView2 浏览器环境的创建入口。
/// </summary>
internal static class BrowserEnvironment
{
    // Chromium 启动参数只在创建环境的那一刻生效，之后没有任何 API 能改，所以全部集中在这里。
    private const string BrowserArguments =
        // 否则窗口一最小化或被遮挡，Chromium 就把页面判为不可见并降级渲染，音频随之中断。
        "--disable-features=CalculateNativeWinOcclusion " +
        "--disable-backgrounding-occluded-windows " +
        "--disable-renderer-backgrounding " +
        "--disable-background-timer-throttling " +
        // 视频信息流的滚动和合成对帧率敏感，走 GPU 光栅化并允许所有显卡硬件加速。
        "--enable-gpu-rasterization " +
        "--enable-zero-copy " +
        "--ignore-gpu-blocklist"
#if DEBUG
        // 验证期用 CDP 从外部驱动页面（滚动、翻页、读 DOM），Release 不带这个端口。
        + " --remote-debugging-port=9224"
#endif
        ;

    public static string UserDataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinDouYin",
        "WebView2");

    public static async Task<CoreWebView2Environment> CreateAsync()
    {
        Directory.CreateDirectory(UserDataFolder);

        var options = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = BrowserArguments };
        return await CoreWebView2Environment.CreateWithOptionsAsync(null, UserDataFolder, options);
    }
}
