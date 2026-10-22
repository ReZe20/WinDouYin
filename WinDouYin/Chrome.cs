using Microsoft.UI.Windowing;

namespace WinDouYin;

/// <summary>
/// 窗口外壳的状态位，只有一件：**沉浸（程序内全屏）**。
///
/// 舞台在 NavigationView → Frame → ForYouPage → VideoPage 的深处，改不了 MainWindow 自己的 XAML；
/// 反过来 MainWindow 也不想抓着舞台上那颗按钮。两边都只跟这里说话：
/// 舞台点按钮 → SetImmersive(!Immersive)；MainWindow 订阅后收顶栏、藏侧边栏、亮左上角那颗关闭按钮。
/// 静态事件，订阅方必须在对的时候退订（舞台拆台时、窗口关掉时），否则控件会被事件表留住。
/// </summary>
internal static class Chrome
{
    public static event Action<bool>? ImmersiveChanged;

    /// <summary>现在是不是沉浸态。跨页面共享：从推荐页切走再回来，状态还在。</summary>
    public static bool Immersive { get; private set; }

    /// <summary>
    /// 切真全屏前那个 presenter 实例：退全屏时复用它。
    /// **不能新建一个**——新建会把标题栏延伸（SetRegionsForCustomTitleBar）、Tall 高度这些既有设置丢掉。
    /// 放在这儿是因为"视频全屏"有两个出口：舞台那颗按钮，和窗口外壳的 Esc / 左上角关闭按钮。
    /// </summary>
    public static OverlappedPresenter? NormalPresenter { get; private set; }

    public static void SetImmersive(bool on)
    {
        if (Immersive == on)
        {
            return;
        }

        Immersive = on;
        ImmersiveChanged?.Invoke(on);
    }

    /// <summary>进真全屏（记住切走前那个 presenter）。已经在全屏就什么都不做。</summary>
    public static void EnterFullscreen(AppWindow window)
    {
        if (window.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            return;
        }

        NormalPresenter = window.Presenter as OverlappedPresenter;
        window.SetPresenter(FullScreenPresenter.Create());
    }

    /// <summary>退真全屏（复用记住的那个 presenter）。不在全屏就什么都不做。</summary>
    public static void LeaveFullscreen(AppWindow window)
    {
        if (window.Presenter.Kind != AppWindowPresenterKind.FullScreen)
        {
            return;
        }

        window.SetPresenter(NormalPresenter ??= OverlappedPresenter.Create());
    }
}
