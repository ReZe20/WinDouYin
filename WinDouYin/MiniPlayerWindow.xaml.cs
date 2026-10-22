
using WinDouYin.Contracts;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Media.Playback;
using WinDouYin.Service;

namespace WinDouYin;

/// <summary>
/// 小窗：一个**独立的置顶小窗口**，里面就是这条视频。
///
/// 画面用的是主窗口那一页的 <see cref="MediaPlayer"/>（挂过来渲染），所以**声音与进度都不重来**——
/// 关掉它（还原按钮 / Esc / 系统关闭按钮）就把播放器挂回主窗口那一页，哪儿停的哪儿接着。
/// 一个 MediaPlayer 同一时刻只能挂在一个 MediaPlayerElement 上，所以主窗口那边先松手（见 MfStageControl.ToggleMiniPlayer）。
/// </summary>
public sealed partial class MiniPlayerWindow : Window
{
    /// <summary>当前开着的小窗（同一时刻只留一个）；主窗口关掉时要把它一起收掉。</summary>
    public static MiniPlayerWindow? Current { get; private set; }

    /// <summary>这条小窗放的是哪条播放器：舞台靠它认"小窗是不是自己开的"（是的话换台不该把它收掉）。</summary>
    public MediaPlayer Player => _player;

    /// <summary>客户区尺寸的兜底值：设置里没记过就用它。</summary>
    private const int ClientWidth = 400;
    private const int ClientHeight = 225;

    /// <summary>小窗最小窗口尺寸（物理像素）：再小就没法看了，拖到更小时弹回来。</summary>
    private const int MinWindowWidth = 240;
    private const int MinWindowHeight = 135;

    /// <summary>尺寸变完停这么久才记一次（拖动途中每帧都写设置太吵）。</summary>
    private const int RememberDelayMs = 800;

    /// <summary>鼠标不动这么久就把控制条收起来。</summary>
    private const int IdleHideMs = 2500;

    /// <summary>控制条收起时往下让开的距离（够它滑出画面）。</summary>
    private const double HiddenOffset = 46;

    private readonly MediaPlayer _player;
    private readonly Action _returnToHost;

    /// <summary>尺寸变了报给宿主记住（宿主写进设置，随设置落盘）。</summary>
    private readonly Action<int, int> _rememberSize;

    private readonly DispatcherQueueTimer _remember;
    private int _reportedWidth;
    private int _reportedHeight;

    /// <summary>正在给自己兜最小尺寸？那一发尺寸变化不算新的用户操作。</summary>
    private bool _clamping;

    private readonly DispatcherQueueTimer _tick;

    /// <summary>鼠标不动这么久就把控制条收起来。</summary>
    private readonly DispatcherQueueTimer _idle;

    private readonly TypedEventHandler<MediaPlaybackSession, object> _stateChanged;

    /// <summary>控制条现在露着吗。</summary>
    private bool _chromeShown;

    /// <summary>正按着进度条？按着的时候不许自动隐藏（指针压着不动也算在操作）。</summary>
    private bool _holding;

    /// <summary>正在把播放进度灌进滑块？那一下不算用户拖动。</summary>
    private bool _syncingSlider;

    /// <summary>已经收过场了（含"静默关"）：回调只跑一次。</summary>
    private bool _closed;

    /// <summary>使用账本：上一次计时的时刻（0 = 当前没在放）。小窗接管时舞台那边跳开，由这里记。</summary>
    private long _watchLastMs;

    // 页面拆掉时接过来的东西：源与落地文件（见 AdoptPlayback）。
    // 非空就说明"这条视频现在归小窗自己放了"——关窗时要把它停掉并延后收尸。
    private MediaSource? _adoptedMedia;
    private string? _adoptedTemp;

    public MiniPlayerWindow(
        MediaPlayer player,
        string author,
        int initialWidth,
        int initialHeight,
        Action returnToHost,
        Action<int, int> rememberSize)
    {
        InitializeComponent();
        _player = player;
        _returnToHost = returnToHost;
        _rememberSize = rememberSize;

        Title = string.IsNullOrWhiteSpace(author) ? "小窗" : "小窗 · " + author;
        Video.SetMediaPlayer(player);

        AppWindow window = AppWindow;
        if (window.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = true;

            // 标题栏去掉，但**边框留着**：原生拖边/拖角缩放要的就是它（IsResizable 只开权限，句柄靠边框）。
            // 纯无边框要自己写命中测试与光标，先不冒那个险；要真正干净的无边框，等你说。
            presenter.SetBorderAndTitleBar(true, false);
        }

        // 客户区尺寸：用上次记住的（物理像素），钉在屏幕右下角（工作区内缩 16）
        int width = Math.Clamp(initialWidth, MinWindowWidth, 7680);
        int height = Math.Clamp(initialHeight, MinWindowHeight, 4320);
        window.ResizeClient(new SizeInt32(width, height));
        RectInt32 work = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Primary).WorkArea;
        const int Gap = 16;
        window.Move(new PointInt32(
            work.X + work.Width - window.Size.Width - Gap,
            work.Y + work.Height - window.Size.Height - Gap));

        // 播放状态变了就刷一次图标（事件从媒体线程来，转回 UI）
        _stateChanged = (_, _) => DispatcherQueue.TryEnqueue(Refresh);
        player.PlaybackSession.PlaybackStateChanged += _stateChanged;

        // 进度不靠媒体事件：按 250ms 主动读，和舞台那边一个节奏
        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(250);
        _tick.Tick += (_, _) => Refresh();
        _tick.Start();

        // 滑块事件在树建好之后接：XAML 里那条会在载入时先响一次，把播放器拽到 0（舞台那边刚踩过这坑）
        ProgressSlider.ValueChanged += OnProgressChanged;

        // 控制条的自动隐藏：鼠标一动就亮，停 2.5 秒自己收；压着进度条时不收
        _idle = DispatcherQueue.CreateTimer();
        _idle.Interval = TimeSpan.FromMilliseconds(IdleHideMs);
        _idle.IsRepeating = false;
        _idle.Tick += (_, _) => HideChrome();

        // 尺寸记住：拖完停 800ms 记一次，拖动途中每帧都写设置太吵
        _remember = DispatcherQueue.CreateTimer();
        _remember.Interval = TimeSpan.FromMilliseconds(RememberDelayMs);
        _remember.IsRepeating = false;
        _remember.Tick += (_, _) => ReportSize();

        // Window 不是 UIElement，这几个静态事件要写明从 UIElement 上取
        ProgressSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnSliderHolding), true);
        ProgressSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnSliderReleased), true);
        ProgressSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnSliderReleased), true);

        Root.Loaded += (_, _) =>
        {
            BarTransform.Y = HiddenOffset; // 收起态的起点：条在画面下沿外
            SetupDragRegion();
            ShowChrome();                  // 先亮一次让人看见它在这儿，之后自己收
            _idle.Start();
        };

        // 尺寸变了：重算 caption 区（物理像素）、兜一下最小值、把新尺寸记进设置
        Root.SizeChanged += (_, _) =>
        {
            SetupDragRegion();
            ClampWindowSize();
            ScheduleRemember();
        };

        // 窗口被点到最前面也把控制条亮一下（别的播放器也是这个习惯）
        Activated += (_, _) =>
        {
            ShowChrome();
            _idle.Stop();
            _idle.Start();
        };

        Closed += (_, _) =>
        {
            _tick.Stop();
            _watchLastMs = 0;
            UsageStore.Flush();   // 小窗关掉这一下把还没落盘的观看秒数写掉
            player.PlaybackSession.PlaybackStateChanged -= _stateChanged;
            if (Current == this)
            {
                Current = null;
            }

            if (_closed)
            {
                return; // 静默关（主窗口在关 / 页面在拆）：不回挂播放器
            }

            _closed = true;

            ReportSize(); // 临了再记一次，免得最后那一下拖动没赶上防抖

            if (_adoptedMedia is not null || _adoptedTemp is not null)
            {
                // 宿主页面已经拆了：这条视频归我们自己放，关窗就是它寿终——
                // 先停声音（Pause + 摘源是舞台平时那套安全动作），源的 Dispose 得延后（见下面的收尸）
                try
                {
                    _player.Pause();
                    _player.Source = null;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("小窗收尾时停播放器抛了：" + ex.Message);
                }

                RetireAdopted();
                return;
            }

            _returnToHost();
        };

        Current = this;
        Refresh();
    }

    /// <summary>静默关掉：不回挂播放器（页面拆了、主窗口正在关的时候用）。</summary>
    public void CloseWithoutReturn()
    {
        _closed = true;
        Close();
    }

    /// <summary>
    /// 宿主页面先把这条视频整套交过来（页面拆掉时走这里）：播放器本来就挂在小窗上，
    /// 这里只是把源物件也接住——不让页面那边一拆就 Dispose，把画面掐掉。
    /// 非空即表示"这条现在归小窗自己放"，关窗时按自己那条收尾（见 Closed 里的判定）。
    /// </summary>
    public void AdoptPlayback(MediaSource? media, string? tempPath)
    {
        _adoptedMedia = media;
        _adoptedTemp = tempPath;
    }

    /// <summary>
    /// 接过来的源延后收尸：MediaSource 不能当场 Dispose——MF 还在读最后几块，
    /// 一关源就是从 XAML 里返回 RO_E_CLOSED、裹成 0xC000027B 把进程带走（舞台那边那笔老账）。
    /// 落地文件同理，等播放器真撒手了再删。
    /// </summary>
    private void RetireAdopted()
    {
        MediaSource? media = _adoptedMedia;
        string? temp = _adoptedTemp;
        _adoptedMedia = null;
        _adoptedTemp = null;

        _ = Task.Run(async () =>
        {
            await Task.Delay(3000).ConfigureAwait(false);

            try
            {
                media?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("小窗的源收尸失败：" + ex.Message);
            }

            if (temp is not null)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                    // 播放器还占着就留着，系统临时目录自己会清
                }
            }
        });
    }

    /// <summary>250ms/次：刷进度、时间、播放暂停那个图标。</summary>
    private void Refresh()
    {
        if (_closed)
        {
            return;
        }

        MediaPlaybackSession session = _player.PlaybackSession;
        AccumulateWatchTime(session);
        double total = session.NaturalDuration.TotalSeconds;
        double seconds = session.Position.TotalSeconds;

        if (total > 0 && ProgressSlider.Maximum != total)
        {
            ProgressSlider.Maximum = total;
        }

        if (!double.IsNaN(seconds))
        {
            _syncingSlider = true;
            ProgressSlider.Value = seconds;
            _syncingSlider = false;
            TimeLabel.Text = total > 0 ? FormatTime(seconds) + " / " + FormatTime(total) : FormatTime(seconds);
        }

        PlayPauseIcon.Glyph = session.PlaybackState == MediaPlaybackState.Playing ? "\uE769" : "\uE768";
    }

    /// <summary>
    /// 使用账本：小窗里实际播放了多久。舞台把小窗开着时自己不计时（见 MfStageControl.AccumulateWatchTime），
    /// 页面拆掉、播放器整套交给小窗之后也由这里接着记——不然挂在小窗看一小时，热力图上会是空的。
    /// </summary>
    private void AccumulateWatchTime(MediaPlaybackSession session)
    {
        if (_closed || session.PlaybackState != MediaPlaybackState.Playing)
        {
            _watchLastMs = 0;
            return;
        }

        long now = Environment.TickCount64;
        if (_watchLastMs == 0)
        {
            _watchLastMs = now;
            return;
        }

        long delta = now - _watchLastMs;
        _watchLastMs = now;
        if (delta <= 0)
        {
            return;
        }

        if (delta > 5000)
        {
            delta = 5000;   // 与舞台同一条：长卡顿不算连续观看
        }

        UsageStore.AddWatch(TimeSpan.FromMilliseconds(delta));
    }

    private void OnProgressChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingSlider || _closed)
        {
            return;
        }

        _player.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue);
        TimeLabel.Text = FormatTime(e.NewValue) + (ProgressSlider.Maximum > 1 ? " / " + FormatTime(ProgressSlider.Maximum) : string.Empty);
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            _player.Pause();
        }
        else
        {
            _player.Play();
        }

        Refresh();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ===== 无标题栏的拖动区 =====

    /// <summary>
    /// 没有标题栏也要能拖窗口：把顶条登记成窗口的 caption 区（系统按标题栏那样处理拖动/双击），
    /// 右上角那颗按钮挖成 Passthrough 才点得动——跟 MainWindow 里那套是同一个手法。
    /// </summary>
    private void SetupDragRegion()
    {
        if (Root.XamlRoot is null)
        {
            return;
        }

        double scale = Root.XamlRoot.RasterizationScale;
        InputNonClientPointerSource source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        source.SetRegionRects(NonClientRegionKind.Caption, [RectOf(CaptionStrip, scale)]);
        source.SetRegionRects(NonClientRegionKind.Passthrough, [RectOf(CloseButton, scale)]);
    }

    private static RectInt32 RectOf(FrameworkElement element, double scale)
    {
        Windows.Foundation.Rect bounds = element.TransformToVisual(null).TransformBounds(
            new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));

        return new RectInt32(
            (int)Math.Round(bounds.X * scale),
            (int)Math.Round(bounds.Y * scale),
            (int)Math.Round(bounds.Width * scale),
            (int)Math.Round(bounds.Height * scale));
    }

    // ===== 尺寸：拖完记住 =====

    private void ScheduleRemember()
    {
        _remember.Stop();
        _remember.Start();
    }

    /// <summary>把当前客户区尺寸报给宿主记住（设置里存的是物理像素，DIP 要乘缩放）。</summary>
    private void ReportSize()
    {
        if (Root.XamlRoot is null)
        {
            return;
        }

        double scale = Root.XamlRoot.RasterizationScale;
        int width = (int)Math.Round(Root.ActualWidth * scale);
        int height = (int)Math.Round(Root.ActualHeight * scale);
        if (width <= 0 || height <= 0 || (width == _reportedWidth && height == _reportedHeight))
        {
            return;
        }

        _reportedWidth = width;
        _reportedHeight = height;
        _rememberSize(width, height);
    }

    /// <summary>
    /// 最小尺寸自己兜：系统那边没有"设下限"的口子，就等它缩过头了再弹回来。
    /// 只在真的小于下限时动一次，_clamping 挡住自己引发的那一轮尺寸变化。
    /// </summary>
    private void ClampWindowSize()
    {
        if (_clamping || _closed)
        {
            return;
        }

        SizeInt32 size = AppWindow.Size;
        int width = Math.Max(size.Width, MinWindowWidth);
        int height = Math.Max(size.Height, MinWindowHeight);
        if (width == size.Width && height == size.Height)
        {
            return;
        }

        _clamping = true;
        try
        {
            AppWindow.Resize(new SizeInt32(width, height));
        }
        finally
        {
            _clamping = false;
        }
    }

    // ===== 控制条的自动隐藏 =====

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ShowChrome();
        _idle.Stop();
        _idle.Start();
    }

    private void OnRootPointerExited(object sender, PointerRoutedEventArgs e) => HideChrome();

    private void OnSliderHolding(object sender, PointerRoutedEventArgs e)
    {
        _holding = true;
        _idle.Stop();
    }

    private void OnSliderReleased(object sender, PointerRoutedEventArgs e)
    {
        _holding = false;
        _idle.Stop();
        _idle.Start();
    }

    private void ShowChrome()
    {
        if (_chromeShown || _closed)
        {
            return;
        }

        _chromeShown = true;
        var board = new Storyboard();
        AddTween(board, Bar, "Opacity", Bar.Opacity, 1, 160);
        AddTween(board, BarTransform, "Y", BarTransform.Y, 0, 160);
        board.Begin();
    }

    private void HideChrome()
    {
        if (!_chromeShown || _closed || _holding)
        {
            return;
        }

        _chromeShown = false;
        _idle.Stop();
        var board = new Storyboard();
        AddTween(board, Bar, "Opacity", Bar.Opacity, 0, 220);
        AddTween(board, BarTransform, "Y", BarTransform.Y, HiddenOffset, 220);
        board.Begin();
    }

    /// <summary>挂一段补间：透明度和位移都是合成友好的属性，不必开依赖动画。</summary>
    private static void AddTween(Storyboard board, DependencyObject target, string property, double from, double to, double milliseconds)
    {
        var tween = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
        };
        Storyboard.SetTarget(tween, target);
        Storyboard.SetTargetProperty(tween, property);
        board.Children.Add(tween);
    }

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Close();
    }

    /// <summary>秒 → mm:ss，满一小时给 h:mm:ss（与舞台那边同一套写法）。</summary>
    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0)
        {
            seconds = 0;
        }

        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? string.Format("{0:0}:{1:00}:{2:00}", (int)span.TotalHours, span.Minutes, span.Seconds)
            : string.Format("{0:00}:{1:00}", span.Minutes, span.Seconds);
    }
}
