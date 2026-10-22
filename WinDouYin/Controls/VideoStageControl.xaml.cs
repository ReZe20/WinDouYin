
using WinDouYin.Contracts;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using WinRT.Interop;

namespace WinDouYin.Controls;

/// <summary>
/// 双通道换台 + 相邻预载：前台通道播当前条，后台通道永远在替下一条做准备。
/// 页面把下一条的地址接在 NextSource/NextCover 上，控件让它在本通道里静音起播、停在开头附近，
/// 候场位置在幕外（滑动起点那一侧）。滚到它时直接滑，不用等加载，滑进来的第一帧就有画面有封面。
/// 页面常驻缓存（NavigationCacheMode=Required）时，切走自动收声、切回按离开时的播放意图续播。
/// </summary>
public sealed partial class VideoStageControl : UserControl
{
    private const string PlayGlyph = "\uE768";
    private const string PauseGlyph = "\uE769";

    // 官方网页实测：一次换台位移=条目高+12px 间隙，transform transition 250ms、缓动 CSS ease。
    private const double SwitchMilliseconds = 250;
    private const double CardGap = 12;

    /// <summary>标题区收起时的底部留白，必须与 VideoInfoControl.xaml 里 Margin 的底边值一致。</summary>
    private const double InfoRestInset = 16;

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source),
        typeof(string),
        typeof(VideoStageControl),
        new PropertyMetadata(null, OnBindingChanged));

    public static readonly DependencyProperty CoverProperty = DependencyProperty.Register(
        nameof(Cover),
        typeof(string),
        typeof(VideoStageControl),
        new PropertyMetadata(null, OnBindingChanged));

    // 预载目标：不上屏，只提前把视频和封面在后台通道里装好
    public static readonly DependencyProperty NextSourceProperty = DependencyProperty.Register(
        nameof(NextSource),
        typeof(string),
        typeof(VideoStageControl),
        new PropertyMetadata(null, OnBindingChanged));

    public static readonly DependencyProperty NextCoverProperty = DependencyProperty.Register(
        nameof(NextCover),
        typeof(string),
        typeof(VideoStageControl),
        new PropertyMetadata(null, OnBindingChanged));

    // 右侧竖排：头像地址 + 四个已格式化的计数，和作者/时间/描述一样只搬家，不参与装载裁决
    public static readonly DependencyProperty AvatarProperty = DependencyProperty.Register(
        nameof(Avatar),
        typeof(string),
        typeof(VideoStageControl),
        new PropertyMetadata(null, OnRailChanged));

    public static readonly DependencyProperty DiggTextProperty = DependencyProperty.Register(
        nameof(DiggText),
        typeof(string),
        typeof(VideoStageControl),
        new PropertyMetadata("0", OnRailChanged));

    public static readonly DependencyProperty CommentTextProperty = DependencyProperty.Register(
        nameof(CommentText),
        typeof(string),
        typeof(VideoStageControl),
        new PropertyMetadata("0", OnRailChanged));

    public static readonly DependencyProperty CollectTextProperty = DependencyProperty.Register(
        nameof(CollectText),
        typeof(string),
        typeof(VideoStageControl),
        new PropertyMetadata("0", OnRailChanged));

    public static readonly DependencyProperty ShareTextProperty = DependencyProperty.Register(
        nameof(ShareText),
        typeof(string),
        typeof(VideoStageControl),
        new PropertyMetadata("0", OnRailChanged));

    // 音量：页面把它双向绑到共享设置 VM 上（改即防抖落盘），控件这边负责同步两个播放声道
    public static readonly DependencyProperty VolumeProperty = DependencyProperty.Register(
        nameof(Volume),
        typeof(int),
        typeof(VideoStageControl),
        new PropertyMetadata(100, OnVolumeSettingChanged));

    // 信息区数据：只往子控件上搬，不参与装载裁决，所以回调里不做 Evaluate
    public static readonly DependencyProperty AuthorProperty = DependencyProperty.Register(
        nameof(Author),
        typeof(string),
        typeof(VideoStageControl),
        new PropertyMetadata(null, OnInfoChanged));

    public static readonly DependencyProperty TimeTextProperty = DependencyProperty.Register(
        nameof(TimeText),
        typeof(string),
        typeof(VideoStageControl),
        new PropertyMetadata(null, OnInfoChanged));

    public static readonly DependencyProperty DescProperty = DependencyProperty.Register(
        nameof(Desc),
        typeof(string),
        typeof(VideoStageControl),
        new PropertyMetadata(null, OnInfoChanged));

    private readonly Channel _channelA;
    private readonly Channel _channelB;
    private readonly DispatcherQueue _ui;

    private Channel _front;
    private Channel _back;
    private int _loadVersion;
    private Storyboard? _board;
    private string _frontUrl = string.Empty;
    private bool _evalQueued;
    private bool _scrubbing;
    private bool _seekPending;
    private bool _resumeOnLoad;

    // 面板本地能力：连播开关、倍速当前档、切全屏前留住的那个 presenter
    private bool _autoPlay;
    private bool _cleared;
    private int _rateIndex = 1;
    private OverlappedPresenter? _normalPresenter;
    private static readonly double[] Rates = [0.75, 1, 1.25, 1.5, 2];

    // 会话内续播：按播放地址记前台最新进度，切回来时 seek 回去（关进程即清空，不落盘）。
    // 总时长另记一份：装载阶段要用"进度/总时长"换成字节偏移，提前把 seek 落点那两块取进内存。
    private readonly Dictionary<string, double> _progress = [];
    private readonly Dictionary<string, double> _duration = [];

    // 换台节流：发起换台的那一刻记一枪，页面拿 IsSwitching 决定滚轮要不要吞
    private const long SwitchGuardMilliseconds = 2500;
    private long _switchStartedMs;
    private int _seekVersion;

    /// <summary>换台松手的播放源隔这么久才真收走，见 <see cref="RetireSource"/></summary>
    private const long SourceReapDelayMs = 3000;

    // 换台松手的 MediaSource 不能当场 Dispose：Media Foundation 还在读最后几块，一关源就是从 XAML 里返回
    // RO_E_CLOSED，裹成 0xC000027B 存根异常把进程带走。可只松引用也不行——要等 GC 终结 RCW 才释放管线，
    // 来回切三十条就是三十套解码器挂在进程里，峰值就是这么冲到几百 MB 的。
    // 所以先收下，等换台落地几秒之后再在 UI 线程关掉。
    private readonly Queue<(MediaSource Source, long RetiredMs)> _corpses = new();
    private DispatcherQueueTimer? _corpsesTimer;

    // 底部浮动面板：鼠标进入热区滑上来，移出滑下去
    private bool _panelVisible;
    private Storyboard? _panelBoard;

    public VideoStageControl()
    {
        InitializeComponent();
        _ui = DispatcherQueue;

        _channelA = new Channel(LayerA, CoverA, PlayerA);
        _channelB = new Channel(LayerB, CoverB, PlayerB);
        _front = _channelA;
        _back = _channelB;

        foreach (Channel channel in new[] { _channelA, _channelB })
        {
            // MediaPlayerElement 自己没有媒体事件，但 SetMediaPlayer 收 Windows.Media.Playback.MediaPlayer，
            // 所以自建播放器挂上去，失败原因只能从它的事件里拿。
            // 订阅一律用 lambda：不写事件参数类型名，免得再撞 WinUI3/UWP 的类型差异。
            channel.View.SetMediaPlayer(channel.Player);

            MediaPlayer player = channel.Player;
            player.MediaOpened += (_, _) => _ui.TryEnqueue(() => OnMediaOpened(player));
            player.MediaFailed += (_, e) => OnMediaFailed(player, e);
            player.PlaybackSession.PlaybackStateChanged += (_, _) => _ui.TryEnqueue(() => OnPlaybackState(player));
            player.PlaybackSession.PositionChanged += (_, _) => _ui.TryEnqueue(() => OnPosition(player));
            player.MediaEnded += (_, _) => _ui.TryEnqueue(() => OnMediaEnded(player));
        }

        // 旧通道要整条滑出舞台边界、预载通道要在幕外候场，不裁都会露出来
        Root.SizeChanged += (_, e) =>
        {
            Root.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
            LayoutVideo(_front);
            LayoutVideo(_back);
            if (_board is null && !_back.Wanted && _back.Layer.Visibility == Visibility.Visible)
            {
                _back.Transform.Y = ParkOffset();
            }
        };

        // 页面开了导航缓存（NavigationCacheMode=Required）会常驻：切走脱树时收声，切回按离开时的播放意图续播
        Loaded += (_, _) => ResumeAfterNavigation();
        Unloaded += (_, _) => SuspendForNavigation();

        // Slider 模板里的 Thumb 会把指针事件标记为已处理，XAML 上挂的 Pointer* 处理器收不到——拖完松手不 seek 就栽在这。
        // 改用 handledEventsToo=true 的 AddHandler：不管事件有没有被处理都能收到。
        ProgressSlider.AddHandler(PointerPressedEvent, new PointerEventHandler(OnProgressPressed), true);
        ProgressSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnProgressReleased), true);
        ProgressSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnProgressCaptureLost), true);
        // 拖拽气泡的默认转换器会把 Value 的原始秒数（63.417293…）直接摊出来，换成 mm:ss
        ProgressSlider.ThumbToolTipValueConverter = new SecondsToolTipConverter();

        // ===== 底部浮动面板显隐 =====
        // 用 Root.PointerMoved 判断鼠标 Y 坐标：在底部区域内 → 面板滑上来；离开 → 滑下去。
        // 不用热区/Enter/Exit 事件，避免面板和热区交替遮挡导致的反复弹起。
        Root.AddHandler(PointerMovedEvent, new PointerEventHandler(OnRootPointerMoved), true);

        // 初始隐藏面板：把面板推到屏底以下（+16 是底部 margin）
        PanelTransform.Y = (ControlPanel.ActualHeight > 0 ? ControlPanel.ActualHeight : 60) + 16;
        ControlPanel.Opacity = 0;
    }

    /// <summary>
    /// 换台动效方向：0 = 不动效（首次加载），1 = 下一条从下方滑入，-1 = 上一条从上方滑入。
    /// 由页面在换条目前设置；控件的裁决器真正发起换台动作时读走并清零。
    /// </summary>
    public int SwitchDirection { get; set; }

    /// <summary>
    /// 换台是否还挂着：动效在跑，或后台通道压着一个还没上屏的换台目标。
    /// 页面用它挡滚轮——上一次滚动没落地之前不接受下一次，否则装载被一路打断，每条都要从头探文件头，看着反而更慢。
    /// 卡住的装载不该拉滚轮陪葬：超过 <see cref="SwitchGuardMilliseconds"/> 就放手，等它自己落地。
    /// </summary>
    public bool IsSwitching => _board is not null
        || (_back.Wanted && Environment.TickCount64 - _switchStartedMs <= SwitchGuardMilliseconds);

    /// <summary>前台这条播到底了（只在开了连播且页面在台上时抛）：由页面决定去取下一条。</summary>
    public event EventHandler? Ended;

    /// <summary>播放地址（play_addr 里 www.douyin.com/aweme/v1/play/ 那条）。</summary>
    public string? Source
    {
        get => (string?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>封面地址，只用来给非视频区域做模糊铺底。</summary>
    public string? Cover
    {
        get => (string?)GetValue(CoverProperty);
        set => SetValue(CoverProperty, value);
    }

    /// <summary>下一条的播放地址：滚到它之前就装好，滚动时即刻切换。</summary>
    public string? NextSource
    {
        get => (string?)GetValue(NextSourceProperty);
        set => SetValue(NextSourceProperty, value);
    }

    /// <summary>下一条的封面地址。</summary>
    public string? NextCover
    {
        get => (string?)GetValue(NextCoverProperty);
        set => SetValue(NextCoverProperty, value);
    }

    /// <summary>信息区：作者昵称（不含 @）。</summary>
    public string? Author
    {
        get => (string?)GetValue(AuthorProperty);
        set => SetValue(AuthorProperty, value);
    }

    /// <summary>信息区：预格式化的相对时间文本（"刚刚"/"3天前"…）。</summary>
    public string? TimeText
    {
        get => (string?)GetValue(TimeTextProperty);
        set => SetValue(TimeTextProperty, value);
    }

    /// <summary>信息区：描述原文，#话题由信息区解析成黄字。</summary>
    public string? Desc
    {
        get => (string?)GetValue(DescProperty);
        set => SetValue(DescProperty, value);
    }

    /// <summary>竖排：作者头像地址（author.avatar_thumb）。</summary>
    public string? Avatar
    {
        get => (string?)GetValue(AvatarProperty);
        set => SetValue(AvatarProperty, value);
    }

    /// <summary>竖排：点赞数，已按抖音口径格式化（"12.4w"）。</summary>
    public string? DiggText
    {
        get => (string?)GetValue(DiggTextProperty);
        set => SetValue(DiggTextProperty, value);
    }

    /// <summary>竖排：评论数。</summary>
    public string? CommentText
    {
        get => (string?)GetValue(CommentTextProperty);
        set => SetValue(CommentTextProperty, value);
    }

    /// <summary>竖排：收藏数。</summary>
    public string? CollectText
    {
        get => (string?)GetValue(CollectTextProperty);
        set => SetValue(CollectTextProperty, value);
    }

    /// <summary>竖排：转发数。</summary>
    public string? ShareText
    {
        get => (string?)GetValue(ShareTextProperty);
        set => SetValue(ShareTextProperty, value);
    }

    /// <summary>播放音量，0~100 整数百分比。</summary>
    public int Volume
    {
        get => (int)GetValue(VolumeProperty);
        set => SetValue(VolumeProperty, value);
    }

    private static void OnBindingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((VideoStageControl)d).QueueEvaluate();

    /// <summary>三条信息区数据谁先更新没有保证，任意一条动了就整组搬一遍，搬家是幂等的。</summary>
    private static void OnInfoChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var stage = (VideoStageControl)d;
        stage.Info.Author = stage.Author;
        stage.Info.TimeText = stage.TimeText;
        stage.Info.Desc = stage.Desc;
    }

    /// <summary>竖排五个值同样不保证到达顺序，任意一个动了就整排重搬一遍。</summary>
    private static void OnRailChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((VideoStageControl)d).RefreshRail();

    private void RefreshRail()
    {
        LikeCount.Text = DiggText ?? "0";
        CommentCount.Text = CommentText ?? "0";
        FavoriteCount.Text = CollectText ?? "0";
        RepostCount.Text = ShareText ?? "0";
        _ = LoadAvatarAsync();
    }

    /// <summary>头像是网络图：取回来可能已经换到下一条了，所以对不上地址就不往画布上盖。</summary>
    private async Task LoadAvatarAsync()
    {
        string url = Avatar ?? string.Empty;
        SoftwareBitmapSource? source = url.Length > 0 ? await BlurCover.LoadAvatarAsync(url) : null;
        if (Avatar == url)
        {
            AuthorAvatarBrush.ImageSource = source;
        }
    }

    /// <summary>
    /// 四条绑定谁先更新没有保证；挪到下一拍再一起读，
    /// 既拿到同一条目的 Source+Cover，也让换台目标和预载目标在同一帧里一起裁决。
    /// </summary>
    private void QueueEvaluate()
    {
        if (_evalQueued)
        {
            return;
        }

        _evalQueued = true;
        _ui.TryEnqueue(() =>
        {
            _evalQueued = false;
            Evaluate();
        });
    }

    /// <summary>
    /// 唯一裁决点，读的都是最新绑定值：
    /// - Source 是前台没播过的新条目：后台通道装好它再上屏；后台要是已经预载着它，立刻滑不等；
    /// - Source 还是前台这条：只是预载请求，把 NextSource 装进后台通道静音候场；
    /// - 动效进行中：不动作，收尾时还会再评一次（那时读到的仍是最新值，滚动绝不丢）。
    /// </summary>
    private void Evaluate()
    {
        if (_board is not null)
        {
            return;
        }

        string url = Source ?? string.Empty;
        if (url.Length == 0)
        {
            return;
        }

        if (url == _frontUrl)
        {
            PrefetchNext();
            return;
        }

        int direction = SwitchDirection;
        SwitchDirection = 0;

        if (_back.Url == url)
        {
            _back.Wanted = true;
            _switchStartedMs = Environment.TickCount64;
            _back.Direction = direction;
            if (_back.Opened)
            {
                // 预载命中：视频一直在幕后跑着，直接滑
                TriggerSwitch(_back, direction);
            }

            return;
        }

        StartLoad(_back, url, Cover ?? string.Empty, wanted: true, direction);
    }

    /// <summary>后台通道替下一条待命；已经有位了就什么都不做。</summary>
    private void PrefetchNext()
    {
        string url = NextSource ?? string.Empty;
        if (url.Length == 0 || url == _frontUrl || _back.Url == url)
        {
            return;
        }

        StartLoad(_back, url, NextCover ?? string.Empty, wanted: false, direction: 0);
    }

    /// <summary>
    /// 把一个条目装进后台通道（前台通道从不装载）。
    /// wanted=true：换台目标，装好上屏；wanted=false：纯预载，静音在幕外候场。
    /// 摆位：不动效的首条直接摆到位（模糊封面先顶上），其余停在滑动起点一侧的幕外。
    /// </summary>
    private void StartLoad(Channel target, string url, string cover, bool wanted, int direction)
    {
        int version = ++_loadVersion;
        // 换台即作废：还压着的 seek 预热不许把上一条的时间点跳到新视频身上
        _seekVersion++;
        _seekPending = false;
        target.Version = version;
        target.Url = url;
        target.Wanted = wanted;
        target.Direction = direction;
        target.Opened = false;

        target.Cover.Source = null;
        target.Player.Source = null;
        RetireSource(target.Source); // 播放器已经松开了它，排队等会儿再真关：理由见 RetireSource
        target.Source = null;
        target.Stream = null;
        DeleteTemp(target);

        target.Layer.Visibility = Visibility.Visible;
        target.Transform.Y = wanted && direction == 0 ? 0 : direction < 0 ? -ParkOffset() : ParkOffset();

        if (wanted)
        {
            _switchStartedMs = Environment.TickCount64;
            StageError.Visibility = Visibility.Collapsed;
            SetLoading(true);
        }

        target.CoverTask = SetCoverAsync(target, cover, version);
        _ = LoadMediaAsync(target, url, version);
    }

    private async Task LoadMediaAsync(Channel target, string url, int version)
    {
        MediaSource media;
        string? tempPath = null;
        HttpRangeStream? stream = null;
        try
        {
            // 先走 Range：PrimeAsync 会同步确认 CDN 接受分段请求（/play/ 那条会先 302 到 weba 节点），
            // 失败在这里就能抓到。CreateFromStream 本身不碰网络，Media Foundation 要到自己的线程上读数据才报错，那时已经来不及回退。
            stream = new HttpRangeStream(url);
            await stream.PrimeAsync();

            // 回看带着续播点起播：先把 seek 落点附近取进内存再交给播放器。
            // 否则 Media Foundation 落到那个偏移的第一读就是同步 HTTP（100~300ms 断供），那一下就是往回切的掉帧。
            if (_progress.TryGetValue(url, out double resume) && _duration.TryGetValue(url, out double total) && total > 0.5)
            {
                await stream.WarmAsync((long)(resume / total * stream.Length), CancellationToken.None);
            }

            media = MediaSource.CreateFromStream(stream.AsRandomAccessStream(), "video/mp4");
        }
        catch (Exception rangeFailure)
        {
            // 回退到整段落地：这是最早真正播出来过的那条路，代价是要等下载完。
            try
            {
                tempPath = await HttpRangeStream.DownloadToTempFileAsync(url);
                media = MediaSource.CreateFromUri(new Uri(tempPath));
            }
            catch (Exception fileFailure)
            {
                string detail = "range: " + rangeFailure.Message + " | file: " + fileFailure.Message;
                _ui.TryEnqueue(() => FailLoad(target, detail));
                return;
            }
        }

        if (version != _loadVersion)
        {
            // 已被后续装载取代，这条作废
            media.Dispose();
            DeleteTempFile(tempPath);
            return;
        }

        target.Source = media;
        target.Stream = stream; // 走到这里版本已经核过：后来者的流不会被我们顶掉
        target.TempPath = tempPath;
        target.Player.IsMuted = !ReferenceEquals(target, _front);
        target.Player.Source = media;
        target.Player.Play();
    }

    private async Task SetCoverAsync(Channel target, string url, int version)
    {
        SoftwareBitmapSource? source = url.Length > 0 ? await BlurCover.LoadAsync(url) : null;
        if (version != _loadVersion)
        {
            return;
        }

        target.Cover.Source = source;
    }

    private void OnMediaOpened(MediaPlayer player)
    {
        Channel? channel = ChannelOf(player);
        if (channel is null || channel.Version != _loadVersion || !SessionLive(player))
        {
            return;
        }

        channel.Opened = true;
        LayoutVideo(channel);

        // 续播：这个地址本会话内看过就回到上次进度；忽略片头 0.5s 内和接近片尾的（那些从头播）
        if (_progress.TryGetValue(channel.Url, out double resume))
        {
            double total = player.PlaybackSession.NaturalDuration.TotalSeconds;
            if (resume > 0.5 && (total <= 0 || resume < total - 1))
            {
                player.PlaybackSession.Position = TimeSpan.FromSeconds(resume);
            }
        }

        if (ReferenceEquals(channel, _front))
        {
            // 前台通道不装载（所有装载都走后台通道），保底分支：真有也从现在起照常播
            player.Play();
            RefreshTransport();
            return;
        }

        if (channel.Wanted)
        {
            _ = AdvanceAsync(channel);
        }
        else
        {
            _ = WarmThenParkAsync(channel);
        }
    }

    private async Task AdvanceAsync(Channel target)
    {
        // 动效是整条滑：新通道上的模糊封面得先在位，否则滑进来的一段是黑的。等它，但最多等 600ms，不让封面拖住换台。
        await Task.WhenAny(target.CoverTask ?? Task.CompletedTask, Task.Delay(600));
        if (target.Version != _loadVersion || _board is not null || !ReferenceEquals(target, _back) || !target.Wanted)
        {
            return;
        }

        TriggerSwitch(target, target.Direction);
    }

    /// <summary>预载条目在幕后先跑半秒：解码器热起来、首帧推上屏、顺带攒一点缓冲，再把画面停在开头附近。
    /// 真正切过来时直接续播，不用从头等解码；久等也不会跑掉进度（停着不动）。
    /// 用版本 + 仍在后台 + 没被指定为换台目标三重校验，换台动作一旦发生就不会被这里停掉。</summary>
    private async Task WarmThenParkAsync(Channel channel)
    {
        channel.Player.Play();
        await Task.Delay(300);
        if (channel.Version == _loadVersion && ReferenceEquals(channel, _back) && !channel.Wanted)
        {
            channel.Player.Pause();
        }
    }

    /// <summary>就现在换：预载命中（视频已经在幕后跑着）走这条，不加载、不等。direction=0 表示不动效。</summary>
    private void TriggerSwitch(Channel incoming, int direction)
    {
        StageError.Visibility = Visibility.Collapsed;
        SetLoading(false);
        if (direction == 0)
        {
            SwapNow(incoming);
        }
        else
        {
            BeginSwitch(incoming, direction);
        }
    }

    private void BeginSwitch(Channel incoming, int direction)
    {
        Channel outgoing = _front;
        double offset = ParkOffset();
        double sign = direction >= 0 ? 1 : -1;

        incoming.Layer.Visibility = Visibility.Visible;
        incoming.Transform.Y = 0;
        // 正常时声音立刻切给新视频；页面已切走时先静音，收尾再停
        incoming.Player.IsMuted = !IsLoaded;
        incoming.Player.Play();
        // 旧视频滑出期间画面照走，但声音立刻切给新视频，避免两条音轨叠着
        outgoing.Player.IsMuted = true;

        var board = new Storyboard();
        // 旧通道：基准值先改成终点——FillBehavior=Stop 停回基准时正好停在终点，不会闪回原位
        outgoing.Transform.Y = -sign * offset;
        AddSlide(board, outgoing.Transform, 0, -sign * offset);
        // 新通道：基准值保持 0，起点由关键帧给出，停回 0 正好是终点
        AddSlide(board, incoming.Transform, sign * offset, 0);
        board.Completed += (_, _) =>
        {
            // _board 这根字段必须在动画跑完前把 Storyboard 引用住，否则它可能被 GC 收走、动画中途停住
            if (ReferenceEquals(_board, board))
            {
                _board = null;
            }

            FinishSwitch(outgoing, incoming);
        };
        _board = board;
        board.Begin();
    }

    private void SwapNow(Channel incoming)
    {
        incoming.Layer.Visibility = Visibility.Visible;
        incoming.Transform.Y = 0;
        incoming.Player.IsMuted = !IsLoaded;
        incoming.Player.Play();
        _front.Player.IsMuted = true;
        FinishSwitch(_front, incoming);
    }

    /// <summary>
    /// 一根 Storyboard 里两根动画同时开跑：旧的从 0 滑到 ∓(高+12)，新的从 ±(高+12) 滑到 0，
    /// 中途两卡之间露出 12px 舞台底色，和官方一致。
    /// </summary>
    private static void AddSlide(Storyboard board, TranslateTransform transform, double from, double to)
    {
        var slide = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromMilliseconds(SwitchMilliseconds)),
            FillBehavior = FillBehavior.Stop,
        };
        slide.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = from,
        });
        // KeySpline 就是 CSS 的 cubic-bezier：官方网页实测缓动是 ease=(0.25,0.1)(0.25,1)，这里逐字复刻
        slide.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(SwitchMilliseconds)),
            Value = to,
            KeySpline = new KeySpline { ControlPoint1 = new Point(0.25, 0.1), ControlPoint2 = new Point(0.25, 1) },
        });
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "Y");
        board.Children.Add(slide);
    }

    /// <summary>
    /// 换台收尾：旧通道让位。旧播放源交给 <see cref="RetireSource"/> 延后关——
    /// 当场 Dispose 会掐掉 Media Foundation 还在读的最后几块，那是一次穿不出 WinRT 边界的 RO_E_CLOSED，
    /// 进程当场没（换台偶发崩溃的来历）；可只松引用又要等 GC 才放管线，来回切几十条就把内存顶到几百 MB。
    /// </summary>
    private void FinishSwitch(Channel outgoing, Channel incoming)
    {
        outgoing.Layer.Visibility = Visibility.Collapsed;
        outgoing.Transform.Y = 0;
        outgoing.Player.Source = null;
        RetireSource(outgoing.Source);
        outgoing.Source = null;
        outgoing.Stream = null;
        outgoing.Cover.Source = null;
        outgoing.Url = string.Empty;
        outgoing.Wanted = false;
        outgoing.Direction = 0;
        outgoing.Opened = false;
        DeleteTemp(outgoing);

        (_front, _back) = (incoming, outgoing);
        _frontUrl = incoming.Url;
        incoming.Wanted = false;
        RefreshTransport();

        if (!IsLoaded)
        {
            // 换台收尾发生在页面已切走之后：画面换好就行，声音不能跟出来
            _front.Player.Pause();
        }

        // 收尾紧接着再评一次：滚动期间排队的新目标继续换，没有就替下一条开始预载
        Evaluate();
    }

    /// <summary>装载失败：通道清空回岗，是换台目标才上屏报错；纯预载失败就当没事，滑过来时自然重试。</summary>
    private void FailLoad(Channel channel, string detail)
    {
        if (channel.Version != _loadVersion)
        {
            return;
        }

        bool wanted = channel.Wanted;
        AbortLoad(channel);
        if (wanted)
        {
            SetLoading(false);
            StageError.Text = detail;
            StageError.Visibility = Visibility.Visible;
        }
    }

    private void AbortLoad(Channel channel)
    {
        _loadVersion++;
        channel.Layer.Visibility = Visibility.Collapsed;
        channel.Transform.Y = 0;
        channel.Cover.Source = null;
        channel.Player.Source = null;
        RetireSource(channel.Source); // 播放器已经松开了它，排队等会儿再真关：理由见 FinishSwitch
        channel.Source = null;
        channel.Stream = null;
        DeleteTemp(channel);
        channel.Url = string.Empty;
        channel.Wanted = false;
        channel.Direction = 0;
        channel.Opened = false;
    }

    private void OnMediaFailed(MediaPlayer player, MediaPlayerFailedEventArgs e)
    {
        string detail = e.Error + " · " + e.ErrorMessage;
        _ = ReportFailureAsync(player, detail);
    }

    /// <summary>
    /// MediaPlayer 的事件从媒体线程抛出，直接碰 UI 会 0x8001010E（跨线程）。
    /// 失败不立刻上屏：解码器对个别样本报错后画面常常照常走，等两秒再看它是不是真的瘫着——真瘫了才把原因摊出来。
    /// </summary>
    private async Task ReportFailureAsync(MediaPlayer player, string detail)
    {
        await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        _ui.TryEnqueue(() => SettleFailure(player, detail));
    }

    private void SettleFailure(MediaPlayer player, string detail)
    {
        Channel? channel = ChannelOf(player);
        if (channel is null || channel.Version != _loadVersion || !SessionLive(player))
        {
            return;
        }

        if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            return;
        }

        if (ReferenceEquals(channel, _back))
        {
            FailLoad(channel, detail);
            return;
        }

        StageError.Text = detail;
        StageError.Visibility = Visibility.Visible;
        SetLoading(false);
    }

    private void OnPlaybackState(MediaPlayer player)
    {
        if (!ReferenceEquals(player, _front.Player) || !SessionLive(player))
        {
            return;
        }

        MediaPlaybackState state = player.PlaybackSession.PlaybackState;
        PlayPauseIcon.Glyph = state == MediaPlaybackState.Playing ? PauseGlyph : PlayGlyph;

        // 前台缓冲/开播中亮提示，播起来就收（用户暂停是 Paused 态，不会误亮）
        if (state == MediaPlaybackState.Buffering || state == MediaPlaybackState.Opening)
        {
            SetLoading(true);
        }
        else if (state == MediaPlaybackState.Playing)
        {
            SetLoading(false);
        }
    }

    private void SetLoading(bool on)
    {
        StageLoading.IsActive = on;
        StageLoading.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPosition(MediaPlayer player)
    {
        if (!ReferenceEquals(player, _front.Player) || _scrubbing || _seekPending || !SessionLive(player))
        {
            return;
        }

        double seconds = player.PlaybackSession.Position.TotalSeconds;
        double total = player.PlaybackSession.NaturalDuration.TotalSeconds;
        if (total > 0)
        {
            ProgressSlider.Maximum = total;
        }

        // 实时记前台进度，供切回来时续播；总时长另记一份，装载时用它换算 seek 落点
        if (_front.Url.Length > 0)
        {
            _progress[_front.Url] = seconds;
            if (total > 0)
            {
                _duration[_front.Url] = total;
                // 播放点每往前挪一个块，就把它身后的块退回预算——动态缓存，缓存大小不变但一直留着刚才看过的那段
                MediaCache.FreeBehind(_front.Url, seconds, total);
            }
        }

        ProgressSlider.Value = seconds;
        UpdateTimeLabel();
    }

    private void RefreshTransport()
    {
        MediaPlayer player = _front.Player;
        LayoutVideo(_front);
        if (!SessionLive(player))
        {
            return;
        }

        PlayPauseIcon.Glyph = player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing ? PauseGlyph : PlayGlyph;

        // 每条新视频都是新 session，倍速会掉回 1x；按钮上写的档位得重新按上去
        player.PlaybackSession.PlaybackRate = Rates[_rateIndex];
        double total = player.PlaybackSession.NaturalDuration.TotalSeconds;
        ProgressSlider.Maximum = total > 0 ? total : 1;
        ProgressSlider.Value = player.PlaybackSession.Position.TotalSeconds;
        UpdateTimeLabel();
    }

    /// <summary>最右时间：当前位置 / 总长；总长还没拿到（媒体未打开）时只显示当前位置。</summary>
    private void UpdateTimeLabel()
    {
        if (!SessionLive(_front.Player))
        {
            return;
        }

        double seconds = _front.Player.PlaybackSession.Position.TotalSeconds;
        double total = _front.Player.PlaybackSession.NaturalDuration.TotalSeconds;
        TimeLabel.Text = total > 0 ? FormatTime(seconds) + " / " + FormatTime(total) : FormatTime(seconds);
    }

    /// <summary>秒 → mm:ss，满一小时给 h:mm:ss。</summary>
    private static string FormatTime(double seconds)
    {
        TimeSpan time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? $"{time.Hours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    /// <summary>页面被切走（缓存页脱树）：记住离开时在不在播，然后停掉声音。</summary>
    private void SuspendForNavigation()
    {
        MediaPlayer player = _front.Player;
        _resumeOnLoad = SessionLive(player) && player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        player.Pause();
    }

    /// <summary>页面切回（缓存页重新上树）：离开时在播就续播，本来就是暂停的保持暂停。</summary>
    private void ResumeAfterNavigation()
    {
        _front.Player.IsMuted = false;
        if (_resumeOnLoad)
        {
            _front.Player.Play();
        }

        _resumeOnLoad = false;
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        MediaPlayer player = _front.Player;
        if (!SessionLive(player))
        {
            return;
        }

        if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            player.Pause();
        }
        else
        {
            player.Play();
        }
    }

    private void OnProgressPressed(object sender, PointerRoutedEventArgs e)
    {
        _scrubbing = true;
        _seekVersion++; // 又上手了：上一次还没落地的跳转作废
    }

    private void OnProgressReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_scrubbing)
        {
            SeekToSlider();
        }
    }

    private void OnProgressCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_scrubbing)
        {
            SeekToSlider();
        }
    }

    private void SeekToSlider()
    {
        _scrubbing = false;
        if (!SessionLive(_front.Player))
        {
            return;
        }

        // 预热这几百毫秒里播放位置还在往前走，不放个 pending 挡住进度回调，滑块会先弹回旧位置再跳过去
        _seekPending = true;
        _ = SeekWithWarmupAsync(ProgressSlider.Value);
    }

    /// <summary>
    /// 拖完进度条先暖 seek 落点那几块再跳：直接设 Position，Media Foundation 到那个偏移的第一读就是同步 HTTP，
    /// 那 100~300ms 就是拖条上的那一下卡顿。暖数最多等 400ms，等不到也照样跳——宁可多等一下，不能让进度条不按。
    /// </summary>
    private async Task SeekWithWarmupAsync(double seconds)
    {
        int version = ++_seekVersion;
        HttpRangeStream? stream = _front.Stream;
        double total = _front.Player.PlaybackSession.NaturalDuration.TotalSeconds;
        if (stream is not null && total > 0.5)
        {
            try
            {
                Task warming = stream.WarmAsync((long)(seconds / total * stream.Length), CancellationToken.None);
                await Task.WhenAny(warming, Task.Delay(400));
                if (warming.IsCompleted)
                {
                    await warming; // 让它自己的异常走这里的 catch，别留一团没人认领的尾巴
                }
            }
            catch (Exception ex)
            {
                // 暖不到不拦跳转：正常读取路径到了那里自己会再取一次
                System.Diagnostics.Debug.WriteLine("[Stage] seek 落点预热失败：" + ex.Message + " | " + _front.Url);
            }
        }

        // 回到 UI 线程再跳：PlaybackSession 不是自由线程能碰的（0x8001010E）
        _ui.TryEnqueue(() =>
        {
            _seekPending = false;
            if (version == _seekVersion && SessionLive(_front.Player))
            {
                _front.Player.PlaybackSession.Position = TimeSpan.FromSeconds(seconds);
            }
        });
    }

    // ===== 底部面板显隐 =====
    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // Flyout 打开时不隐藏面板（鼠标可能移到 Flyout 上）
        if (VolumeFlyout.IsOpen || DanmakuSettingsFlyout.IsOpen)
        {
            return;
        }

        double y = e.GetCurrentPoint(Root).Position.Y;
        double threshold = Root.ActualHeight - 100;
        if (y >= threshold)
        {
            ShowPanel();
        }
        else
        {
            TryHidePanel();
        }
    }

    private void ShowPanel()
    {
        if (_panelVisible)
        {
            return;
        }

        double h = (ControlPanel.ActualHeight > 0 ? ControlPanel.ActualHeight : 60) + 16;
        var board = new Storyboard();

        var slide = new DoubleAnimation { From = h, To = 0, Duration = TimeSpan.FromMilliseconds(200) };
        Storyboard.SetTarget(slide, PanelTransform);
        Storyboard.SetTargetProperty(slide, "Y");
        board.Children.Add(slide);

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(200) };
        Storyboard.SetTarget(fade, ControlPanel);
        Storyboard.SetTargetProperty(fade, "Opacity");
        board.Children.Add(fade);

        // 标题区跟着面板一起上浮，否则两行描述正好被面板压住
        var lift = new DoubleAnimation { From = 0, To = -InfoLift(h), Duration = TimeSpan.FromMilliseconds(200) };
        Storyboard.SetTarget(lift, InfoTransform);
        Storyboard.SetTargetProperty(lift, "Y");
        board.Children.Add(lift);

        board.Completed += (_, _) =>
        {
            if (ReferenceEquals(_panelBoard, board))
            {
                _panelBoard = null;
            }
        };
        _panelBoard = board;
        board.Begin();
        _panelVisible = true;
    }

    private void TryHidePanel()
    {
        // 鼠标在面板内拖动进度条时不隐藏
        if (_scrubbing)
        {
            return;
        }

        if (!_panelVisible)
        {
            return;
        }

        double h = (ControlPanel.ActualHeight > 0 ? ControlPanel.ActualHeight : 60) + 16;
        var board = new Storyboard();

        var slide = new DoubleAnimation { From = 0, To = h, Duration = TimeSpan.FromMilliseconds(300) };
        Storyboard.SetTarget(slide, PanelTransform);
        Storyboard.SetTargetProperty(slide, "Y");
        board.Children.Add(slide);

        var fade = new DoubleAnimation { From = 1, To = 0, Duration = TimeSpan.FromMilliseconds(300) };
        Storyboard.SetTarget(fade, ControlPanel);
        Storyboard.SetTargetProperty(fade, "Opacity");
        board.Children.Add(fade);

        var lift = new DoubleAnimation { From = -InfoLift(h), To = 0, Duration = TimeSpan.FromMilliseconds(300) };
        Storyboard.SetTarget(lift, InfoTransform);
        Storyboard.SetTargetProperty(lift, "Y");
        board.Children.Add(lift);

        board.Completed += (_, _) =>
        {
            if (ReferenceEquals(_panelBoard, board))
            {
                _panelBoard = null;
            }
        };
        _panelBoard = board;
        board.Begin();
        _panelVisible = false;
    }

    // ===== 面板功能按钮 =====
    private void OnDanmakuToggle(object sender, RoutedEventArgs e) { }

    private void OnDanmakuSend(object sender, RoutedEventArgs e)
    {
        string text = DanmakuInput.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        // TODO: 发送弹幕（text）
        DanmakuInput.Text = string.Empty;
    }

    // 恢复默认：不透明 80%、显示区域两行、字体适中、速度适中
    private void OnDanmakuReset(object sender, RoutedEventArgs e)
    {
        DanmakuOpacitySlider.Value = 80;
        DanmakuAreaSlider.Value = 1;
        DanmakuFontSlider.Value = 1;
        DanmakuSpeedSlider.Value = 1;
    }

    private void OnDanmakuOpacityChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (DanmakuOpacityText is null)
        {
            return;
        }

        DanmakuOpacityText.Text = ((int)e.NewValue) + "%";
        // TODO: 应用到弹幕渲染的不透明度
    }

    private void OnDanmakuAreaChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (DanmakuAreaText is null)
        {
            return;
        }

        DanmakuAreaText.Text = ((int)e.NewValue) switch
        {
            0 => "单行",
            1 => "两行",
            _ => "三行",
        };
        // TODO: 应用到弹幕显示区域
    }

    private void OnDanmakuFontChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (DanmakuFontText is null)
        {
            return;
        }

        DanmakuFontText.Text = ((int)e.NewValue) switch
        {
            0 => "较小",
            1 => "适中",
            _ => "较大",
        };
        // TODO: 应用到弹幕字体大小
    }

    private void OnDanmakuSpeedChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (DanmakuSpeedText is null)
        {
            return;
        }

        DanmakuSpeedText.Text = ((int)e.NewValue) switch
        {
            0 => "较慢",
            1 => "适中",
            _ => "较快",
        };
        // TODO: 应用到弹幕移动速度
    }

    private void OnDanmakuList(object sender, RoutedEventArgs e)
    {
        // TODO: 打开弹幕列表
    }

    // ===== 面板本地能力 =====
    private void OnAutoPlayToggled(object sender, RoutedEventArgs e) => _autoPlay = ((ToggleSwitch)sender).IsOn;

    /// <summary>预载通道在幕后也会跑到结尾，所以只认前台这条，且页面得在台上。</summary>
    private void OnMediaEnded(MediaPlayer player)
    {
        if (_autoPlay && IsLoaded && ReferenceEquals(player, _front.Player))
        {
            Ended?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnSpeedClick(object sender, RoutedEventArgs e)
    {
        if (!SessionLive(_front.Player))
        {
            return;
        }

        _rateIndex = (_rateIndex + 1) % Rates.Length;
        double rate = Rates[_rateIndex];
        _front.Player.PlaybackSession.PlaybackRate = rate;
        SpeedLabel.Text = rate.ToString("0.##") + "x";
    }

    private void OnClearScreenToggled(object sender, RoutedEventArgs e)
    {
        _cleared = ((ToggleSwitch)sender).IsOn;
        Info.Visibility = _cleared ? Visibility.Collapsed : Visibility.Visible;
        RightRail.Visibility = _cleared ? Visibility.Collapsed : Visibility.Visible;
        if (_cleared)
        {
            TryHidePanel();
        }
    }

    private void OnFullscreenClick(object sender, RoutedEventArgs e)
    {
        AppWindow window = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this)));
        if (window.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            // 切回时复用切走前那个 presenter 实例：新建一个会把标题栏延伸、Tall 高度这些既有设置丢掉
            window.SetPresenter(_normalPresenter ??= OverlappedPresenter.Create());
        }
        else
        {
            _normalPresenter = window.Presenter as OverlappedPresenter;
            window.SetPresenter(FullScreenPresenter.Create());
        }
    }

    private void OnSmartClick(object sender, RoutedEventArgs e) { }

    private void OnSettingsClick(object sender, RoutedEventArgs e) { }

    // 这轮接不了：Windows App SDK 没有画中画（Microsoft.UI.Xaml.xml 里既无 IsPictureInPictureEnabled 也无 TryNewPictureInPictureAsync）
    private void OnPipClick(object sender, RoutedEventArgs e) { }

    // ===== 右侧竖排的写操作 =====
    // 点赞/收藏/发评论都是写接口：当前会话是自举来的匿名 ttwid，没有 sessionid，必吃登录墙；
    // 而它们的参数集与风控头仓库里从没抓到过样本，不能凭记忆拼。等登录态有了再按真实报文接。
    private void OnAuthorClick(object sender, RoutedEventArgs e) { }

    private void OnLikeClick(object sender, RoutedEventArgs e) { }

    private void OnCommentClick(object sender, RoutedEventArgs e) { }

    private void OnFavoriteClick(object sender, RoutedEventArgs e) { }

    private void OnRepostClick(object sender, RoutedEventArgs e) { }

    /// <summary>滑块只写依赖属性：值经页面的双向绑定回到共享设置 VM，由它防抖落盘。</summary>
    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_front is null)
        {
            return;
        }

        Volume = (int)e.NewValue;
    }

    private static void OnVolumeSettingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((VideoStageControl)d).ApplyVolume();

    /// <summary>
    /// 音量挂在 MediaPlayer 上而不是 session 上，所以两个声道要一起设：
    /// 换台是把声音交给另一条声道，只设前台的话下一条立刻掉回 100%。
    /// 静音不用 IsMuted 表达——那是声道角色（后台预载不能出声）在用的，0% 本身就是静音。
    /// </summary>
    private void ApplyVolume()
    {
        int percent = Math.Clamp(Volume, 0, 100);
        double level = percent / 100.0;
        _channelA.Player.Volume = level;
        _channelB.Player.Volume = level;

        if (VolumeSlider.Value != percent)
        {
            VolumeSlider.Value = percent;
        }

        VolumeText.Text = percent + "%";
        VolumeIcon.Glyph = percent <= 0 ? "\uE784" : "\uE767";
    }

    private double ParkOffset() => (Root.ActualHeight > 0 ? Root.ActualHeight : 640) + CardGap;

    /// <summary>
    /// 标题区该抬多高：传进来的 panelTop 是面板顶缘距舞台底的距离（16 底边距 + 面板实测高），
    /// 抬到它上面再留 12 间隙；信息区收起时本来就占着 InfoRestInset 那段底距，所以要扣掉。
    /// </summary>
    private static double InfoLift(double panelTop) => Math.Max(0, panelTop + 12 - InfoRestInset);

    /// <summary>
    /// 把视频元素缩到画面在可用区（舞台减 60px 右边距）里 Uniform 适配的实际尺寸并居中，
    /// 这样 SwapChainPanel 只盖住画面本身，四周让给 Cover 模糊板；自然尺寸还没拿到时退回铺满。
    /// </summary>
    private void LayoutVideo(Channel channel)
    {
        if (!SessionLive(channel.Player))
        {
            return;
        }

        var session = channel.Player.PlaybackSession;
        double vw = session.NaturalVideoWidth;
        double vh = session.NaturalVideoHeight;
        double availW = Root.ActualWidth - 60;
        double availH = Root.ActualHeight;

        if (vw > 0 && vh > 0 && availW > 0 && availH > 0)
        {
            double scale = Math.Min(availW / vw, availH / vh);
            channel.View.HorizontalAlignment = HorizontalAlignment.Center;
            channel.View.VerticalAlignment = VerticalAlignment.Center;
            channel.View.Width = vw * scale;
            channel.View.Height = vh * scale;
        }
        else
        {
            channel.View.HorizontalAlignment = HorizontalAlignment.Stretch;
            channel.View.VerticalAlignment = VerticalAlignment.Stretch;
            channel.View.Width = double.NaN;
            channel.View.Height = double.NaN;
        }
    }

    private Channel? ChannelOf(MediaPlayer player) =>
        ReferenceEquals(player, _front.Player) ? _front : ReferenceEquals(player, _back.Player) ? _back : null;

    /// <summary>
    /// 播放器一旦没有源，它的 PlaybackSession 就随旧条目一起被框架收掉了；
    /// 之后任何迟到回调（媒体线程入队的、动画收尾的、脱树触发的）再去读它，
    /// 就是从 XAML 的回调里抛出 ObjectDisposedException——它会被裹成 0xC000027B 存根异常直接带走进程。
    /// 往回切时数据和封面全在缓存里，整条链在一个 dispatcher 轮次内跑完，最容易撞上这个窗口。
    /// </summary>
    private static bool SessionLive(MediaPlayer player) => player.Source is not null;

    private static void DeleteTemp(Channel channel)
    {
        if (channel.TempPath is not null)
        {
            DeleteTempFile(channel.TempPath);
            channel.TempPath = null;
        }
    }

    /// <summary>
    /// 把刚从通道上摘下来的播放源收进待葬队列，<see cref="SourceReapDelayMs"/> 之后 Dispose。
    /// 只在 UI 线程操作，且入队即停表、清空即停表，不留一根常转的定时器。
    /// </summary>
    private void RetireSource(MediaSource? source)
    {
        if (source is null)
        {
            return;
        }

        _corpses.Enqueue((source, Environment.TickCount64));

        _corpsesTimer ??= CreateReapTimer();
        if (!_corpsesTimer.IsRunning)
        {
            _corpsesTimer.Start();
        }
    }

    private DispatcherQueueTimer CreateReapTimer()
    {
        DispatcherQueueTimer timer = _ui.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(1000);
        timer.Tick += (_, _) => ReapSources();
        return timer;
    }

    private void ReapSources()
    {
        long now = Environment.TickCount64;
        while (_corpses.Count > 0 && now - _corpses.Peek().RetiredMs >= SourceReapDelayMs)
        {
            MediaSource source = _corpses.Dequeue().Source;
            try
            {
                source.Dispose();
            }
            catch (Exception ex)
            {
                // 收尸收出问题不往上抛：宁可留一套管线等 GC，也不能把换台打断
                System.Diagnostics.Debug.WriteLine("[Stage] 旧播放源回收失败：" + ex.Message);
            }
        }

        if (_corpses.Count == 0)
        {
            _corpsesTimer?.Stop();
        }
    }

    private static void DeleteTempFile(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            /* 播放器还占着就留给进程退出后清理 */
        }
    }

    /// <summary>进度条拖拽气泡的值转换：秒数显示成 mm:ss。</summary>
    private sealed partial class SecondsToolTipConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            value is double seconds ? FormatTime(seconds) : string.Empty;

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }

    private sealed class Channel(Grid layer, Image cover, MediaPlayerElement view)
    {
        public Grid Layer { get; } = layer;

        public Image Cover { get; } = cover;

        public MediaPlayerElement View { get; } = view;

        public MediaPlayer Player { get; } = new();

        public TranslateTransform Transform => (TranslateTransform)Layer.RenderTransform;

        public MediaSource? Source { get; set; }

        /// <summary>这根通道当前用的取数流：拖进度条时要拿它预热 seek 落点，别让 Media Foundation 去发同步 HTTP。</summary>
        public HttpRangeStream? Stream { get; set; }

        public string? TempPath { get; set; }

        public int Version { get; set; }

        public Task? CoverTask { get; set; }

        /// <summary>通道当前装载/持有的播放地址；空串表示空通道。</summary>
        public string Url { get; set; } = string.Empty;

        /// <summary>这笔装载是不是"装好就上屏"（换台目标）；false = 纯预载。</summary>
        public bool Wanted { get; set; }

        /// <summary>wanted 装载对应的滑动方向，装好上屏时用。</summary>
        public int Direction { get; set; }

        /// <summary>视频已经开出来了（MediaOpened）；预载命中的判断就靠它。</summary>
        public bool Opened { get; set; }
    }
}
