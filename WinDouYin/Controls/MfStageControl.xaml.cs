using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using WinDouYin.Contracts;
using WinDouYin.Service;
using WinRT.Interop;

namespace WinDouYin.Controls;

/// <summary>
/// MediaPlayerElement 版舞台（当前唯一的舞台）：播放核是 WinUI 3 自带的 MediaPlayerElement（Media Foundation）。
/// 原先与它并排对照的 libvlc 版舞台 <c>VlcStageControl</c> 已于 2026-10-19 连同 LibVLCSharp 依赖一并退役。
///
/// 内存这条线**自己管，且尽量不在 UI 线程上做**（2026-10-13 第三版）：
/// - 换下来的 <see cref="MediaSource"/> 不能当场 Dispose（MF 还在读最后几块，RO_E_CLOSED 会裹成
///   0xC000027B 把进程带走），也不能不管（终结 RCW 时机不可控，几十条管线叠进程里）。
///   收尸队列**加锁跨线程**：UI 只负责入队，到期后的 Dispose 与删落地文件在线程池上的
///   <see cref="ReapLoopAsync"/> 里做，全程不占 UI；万一对象是线程亲和的，异常会被接住并转 UI 兜底一次；
/// - 块缓存记账（<see cref="MediaCache.FreeBehind"/>）同样丢线程池——它自带锁，不需要 UI；
/// - 取数端的异步段一律 ConfigureAwait(false)（见 HttpRangeStream）：网络续体不弹回 UI；
/// - 留在 UI 的只有真正必须 UI 的：元素尺寸、滑条/文字、以及媒体事件回灌后的收尾。
///
/// 取数其余部分沿用老 MF 版验证过的路：HttpRangeStream（UA/Referer 自备、PrimeAsync 先对质 CDN）、
/// 不行再回退整段落地。单实例、瞬时换台：宿主（ForYouPage）用两个 VideoPage 乒乓驱动。
/// </summary>
public sealed partial class MfStageControl : UserControl
{
    private const string PlayGlyph = "\uE768";
    private const string PauseGlyph = "\uE769";

    /// <summary>装载发起后到 Playing 之间的兜底上限：卡住太久不该把滚轮一起锁死。</summary>
    private const long SwitchGuardMilliseconds = 5000;

    /// <summary>看过这么久以内不值得记续播点，也别往回跳。</summary>
    private const double ResumeMinSeconds = 5;

    /// <summary>换档那一跳的期限：到这个点还没跳到位就作罢（并放开续播表记账）。状态事件是连着来的，
    /// 跳点要是被管线咽掉了，不能一直挂着等。</summary>
    private const long ResumeDeadlineMs = 5000;

    /// <summary>播放点进到续播点这个宽限以内就算"跳到位"（播放器不会分毫不差地停在目标上）。</summary>
    private const double ResumeLandedSlackSeconds = 1.0;

    /// <summary>实况尾帧到静态图的渐显时长。</summary>
    private const int LiveFadeMilliseconds = 180;

    /// <summary>播放源从播放器上摘下来之后，隔这么久再显式 Dispose：给 MF 最后几块读干净的时间。</summary>
    private const long SourceReapDelayMs = 3000;

    /// <summary>续播两张表的键上限：看过几百条后不许无限攒。</summary>
    private const int SeenCap = 200;

    /// <summary>工作集修剪阈值（整进程工作集，含共享页）：任务管理器那条"内存"读数要守住的线在它下面。
    /// 32GB 物理内存上热力图约 1%（≈330MB）起步泛黄，这里按整工作集 350MB 掐线，
    /// 专用工作集（TM 显示的那条）天然比它低一档，落在线内。</summary>
    private const long TrimThresholdBytes = 350L * 1024 * 1024;

    /// <summary>两次修剪之间至少隔这么久：别把播放中要用的帧页反复抖进 standby。</summary>
    private const long TrimIntervalMs = 5000;

    /// <summary>尺寸变化日志最快 500ms 记一条：拖拽缩放时它每帧都来，而 MfLog 是同步写盘，逐条落盘会把 resize 循环拖慢。</summary>
    private const long SizeLogIntervalMs = 500;

    /// <summary>窗口缩放停止后多久才允许修剪：缩放期间（或刚停下）不做页表活，免得帧上添堵。</summary>
    private const long ResizeQuietMs = 1500;

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source),
        typeof(string),
        typeof(MfStageControl),
        new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty CoverProperty = DependencyProperty.Register(
        nameof(Cover),
        typeof(string),
        typeof(MfStageControl),
        new PropertyMetadata(null, OnCoverChanged));

    /// <summary>这条作品的 BGM（music.play_url）：图文帖的画面没有声轨，出声就靠它；视频条目不用。</summary>
    public static readonly DependencyProperty MusicProperty = DependencyProperty.Register(
        nameof(Music),
        typeof(string),
        typeof(MfStageControl),
        new PropertyMetadata(null, OnMusicChanged));

    /// <summary>
    /// 画面原始宽高（像素），来自 feed 的 video.width/height。
    /// 按比例缩元素由数据侧带进来——元素铺满整个舞台的话，播放器自己补的那块黑边会把模糊背景板整个盖掉。
    /// </summary>
    public static readonly DependencyProperty VideoWidthProperty = DependencyProperty.Register(
        nameof(VideoWidth),
        typeof(int),
        typeof(MfStageControl),
        new PropertyMetadata(0, OnVideoSizeChanged));

    public static readonly DependencyProperty VideoHeightProperty = DependencyProperty.Register(
        nameof(VideoHeight),
        typeof(int),
        typeof(MfStageControl),
        new PropertyMetadata(0, OnVideoSizeChanged));

    // 右侧竖排：头像地址 + 四个已格式化的计数，只搬家，不参与播放
    public static readonly DependencyProperty AvatarProperty = DependencyProperty.Register(
        nameof(Avatar),
        typeof(string),
        typeof(MfStageControl),
        new PropertyMetadata(null, OnRailChanged));

    public static readonly DependencyProperty DiggTextProperty = DependencyProperty.Register(
        nameof(DiggText),
        typeof(string),
        typeof(MfStageControl),
        new PropertyMetadata("0", OnRailChanged));

    public static readonly DependencyProperty CommentTextProperty = DependencyProperty.Register(
        nameof(CommentText),
        typeof(string),
        typeof(MfStageControl),
        new PropertyMetadata("0", OnRailChanged));

    public static readonly DependencyProperty CollectTextProperty = DependencyProperty.Register(
        nameof(CollectText),
        typeof(string),
        typeof(MfStageControl),
        new PropertyMetadata("0", OnRailChanged));

    public static readonly DependencyProperty ShareTextProperty = DependencyProperty.Register(
        nameof(ShareText),
        typeof(string),
        typeof(MfStageControl),
        new PropertyMetadata("0", OnRailChanged));

    // 四个**原始**计数（没格式化的整数）：只为本会话自己点的那一下 +1 用——
    // 显示仍以格式化好的那几个为准（值有出处），只有点过的那一条才用原始数重算那一格
    public static readonly DependencyProperty DiggRawProperty = DependencyProperty.Register(
        nameof(DiggRaw),
        typeof(int),
        typeof(MfStageControl),
        new PropertyMetadata(0, OnRailChanged));

    public static readonly DependencyProperty CommentRawProperty = DependencyProperty.Register(
        nameof(CommentRaw),
        typeof(int),
        typeof(MfStageControl),
        new PropertyMetadata(0, OnRailChanged));

    public static readonly DependencyProperty CollectRawProperty = DependencyProperty.Register(
        nameof(CollectRaw),
        typeof(int),
        typeof(MfStageControl),
        new PropertyMetadata(0, OnRailChanged));

    public static readonly DependencyProperty ShareRawProperty = DependencyProperty.Register(
        nameof(ShareRaw),
        typeof(int),
        typeof(MfStageControl),
        new PropertyMetadata(0, OnRailChanged));

    // 连播：持久化设置（settings.json 的 AutoPlay），两条视频页共享同一个值——
    // 页面把它双向绑到共享设置 VM 上（改即防抖落盘），控件这边只管把开关和播放行为对上
    public static readonly DependencyProperty AutoPlayProperty = DependencyProperty.Register(
        nameof(AutoPlay),
        typeof(bool),
        typeof(MfStageControl),
        new PropertyMetadata(false, OnAutoPlaySettingChanged));

    // 弹幕开关：页面双向绑到共享设置 VM（跨启动记住），控件这边把值落到层上
    public static readonly DependencyProperty DanmakuOnProperty = DependencyProperty.Register(
        nameof(DanmakuOn),
        typeof(bool),
        typeof(MfStageControl),
        new PropertyMetadata(true, OnDanmakuOnSettingChanged));

    // 这条视频的 id：加入稍后再看要用它（那个接口按 aweme_id 走），弹幕也按它取（换条就重取）
    public static readonly DependencyProperty ItemIdProperty = DependencyProperty.Register(
        nameof(ItemId),
        typeof(string),
        typeof(MfStageControl),
        new PropertyMetadata(null, OnItemIdChanged));

    // 作者 uid：分享卡（message_type=8）内容 JSON 的 uid 字段，播放链不碰
    public static readonly DependencyProperty AuthorIdProperty = DependencyProperty.Register(
        nameof(AuthorId),
        typeof(string),
        typeof(MfStageControl),
        new PropertyMetadata(null));

    // 作者 sec_uid：点作者头像进主页要用（user/profile/other 只认 sec_user_id，uid 进不去）
    public static readonly DependencyProperty AuthorSecUidProperty = DependencyProperty.Register(
        nameof(AuthorSecUid),
        typeof(string),
        typeof(MfStageControl),
        new PropertyMetadata(null));

    // 音量：页面双向绑到共享设置 VM（改即防抖落盘），控件这边把它交给播放器
    public static readonly DependencyProperty VolumeProperty = DependencyProperty.Register(
        nameof(Volume),
        typeof(int),
        typeof(MfStageControl),
        new PropertyMetadata(100, OnVolumeSettingChanged));

    // 清晰度档（空串=默认）：页面双向绑到共享设置 VM，控件这边照它挑流、点菜单也写回它
    public static readonly DependencyProperty QualityKeyProperty = DependencyProperty.Register(
        nameof(QualityKey),
        typeof(string),
        typeof(MfStageControl),
        new PropertyMetadata(string.Empty, OnQualitySettingChanged));

    // 这条视频的清晰度档位（从高到低 + 默认流属于哪一档）：干净的数据管道——页面把条目上那份绑进来，控件只读不取
    public static readonly DependencyProperty QualitiesProperty = DependencyProperty.Register(
        nameof(Qualities),
        typeof(VideoQualitySet),
        typeof(MfStageControl),
        new PropertyMetadata(null, OnQualitiesChanged));

    // 信息区数据：只往子控件上搬
    public static readonly DependencyProperty AuthorProperty = DependencyProperty.Register(
        nameof(Author),
        typeof(string),
        typeof(MfStageControl),
        new PropertyMetadata(null, OnInfoChanged));
    public static readonly DependencyProperty TimeTextProperty = DependencyProperty.Register(
        nameof(TimeText),
        typeof(string),
        typeof(MfStageControl),
        new PropertyMetadata(null, OnInfoChanged));

    public static readonly DependencyProperty DescProperty = DependencyProperty.Register(
        nameof(Desc),
        typeof(string),
        typeof(MfStageControl),
        new PropertyMetadata(null, OnInfoChanged));

    /// <summary>朋友推荐人（已排版好的名字串；空 = 不是朋友推荐）。</summary>
    public static readonly DependencyProperty FriendRecommenderProperty = DependencyProperty.Register(
        nameof(FriendRecommender),
        typeof(string),
        typeof(MfStageControl),
        new PropertyMetadata(null, OnInfoChanged));

    private readonly DispatcherQueue _ui;
    private readonly DispatcherQueueTimer _transport;

    /// <summary>常驻整理拍：每 5 秒看一眼工作集，越线就修剪（见 TrimWorkingSetIfHigh）。</summary>
    private readonly DispatcherQueueTimer _housekeep;
    private long _lastTrimMs;

    /// <summary>使用账本的心跳：每秒看一眼主播放器与图文 BGM，谁在放就把这一段墙钟时间记进去。</summary>
    private readonly DispatcherQueueTimer _watch;
    private long _watchLastMs;

    /// <summary>视频元素的落位合拍器：拖拽缩放期间不逐帧重配视频面（那是帧成本大头），停下 220ms 落一次最终位置。</summary>
    private readonly DispatcherQueueTimer _layoutSettle;
    private long _lastHostResizeMs;
    private long _lastSizeLogMs;

    /// <summary>上一次修剪还在路上？页表活丢给了线程池，同时只许一单。</summary>
    private volatile bool _trimBusy;

    /// <summary>最近一次窗口尺寸变动的时刻（MainWindow 每有 SizeChanged 就标进来）。</summary>
    private static long _lastWindowResizeMs = long.MinValue;

    /// <summary>宿主在 RootLayout 每次尺寸变动时调这里：缩放进行中不修剪——缩放的帧比页表账重要。</summary>
    internal static void NoteWindowResize() => _lastWindowResizeMs = Environment.TickCount64;

    /// <summary>
    /// WinUI3 自带的播放核：MediaPlayerElement + Windows.Media.Playback.MediaPlayer（Media Foundation）。
    /// 合成与交换链的拼装由 MediaPlayerElement 自己管，应用侧不参与。
    /// </summary>
    private readonly MediaPlayer _player = new();

    /// <summary>
    /// 图文帖的 BGM 专用播放器：纯音频，不挂任何元素。**不能借上面那个 <see cref="_player"/> 放**——
    /// 那条链上挂着装载/换档/续播/缓冲提示/弹幕游标一大串"视频才有的事"，灌一条 m4a 进去会全被惊动；
    /// 而且 MediaFailed 会被判成"这条视频播不出来"。音量与候场静音跟主播放器同一份（见 ApplyVolume）。
    /// </summary>
    private readonly MediaPlayer _bgm = new();

    /// <summary>当前在放的 BGM 地址（换个图文帖要换歌，同一地址不重开）。</summary>
    private string _bgmUrl = string.Empty;

    /// <summary>页面被换掉时整套拆干净（一条视频一个页面，见 Views/VideoPage），
    /// 之后迟到的媒体线程回调一律不许再碰 XAML。</summary>
    private bool _tornDown;

    /// <summary>当前这条的播放源 / 取数流 / 落地文件。</summary>
    private MediaSource? _media;
    private HttpRangeStream? _stream;
    private string? _tempPath;

    /// <summary>
    /// 换台松手的旧源收进这里延后处理。**不许改成当场 Dispose**：MF 还在读最后几块，
    /// 一关源就是从 XAML 里返回 RO_E_CLOSED，裹成 0xC000027B 存根异常把进程带走（老 MF 版的旧账）。
    /// 也**不许改成只松引用**：终结 RCW 的时机不可控，几十条解码管线叠在进程里，内存就是那么涨的。
    /// 队列是跨线程的（UI 入队 / 线程池收尸），用锁守着；Dispose 全程不在 UI 线程上做。
    /// </summary>
    private readonly object _corpseGate = new();
    private readonly Queue<(MediaSource? Source, string? Temp, long RetiredMs)> _corpses = new();

    /// <summary>收尸循环：只在队列非空时活着，线程池上跑，空了自己退场，下次入队再起。</summary>
    private Task? _reaper;

    /// <summary>装载版本号：MF 的装载是异步的，晚到的旧装载不许把新装载顶掉。</summary>
    private int _loadVersion;

    private string _playing = string.Empty;
    private int _coverVersion;
    private bool _opening;
    private long _switchStartedMs;
    private bool _scrubbing;

    /// <summary>面板本地能力：倍速当前档、切全屏前留住的那个 presenter</summary>
    private bool _autoPlay;

    /// <summary>正在从设置回灌开关？回灌那一下也会触发 Toggled，不掐断就和"开关改动写回设置"踢成环。</summary>
    private bool _autoPlaySyncing;
    private bool _cleared;
    private int _rateIndex = 1;

    /// <summary>
    /// 换档装载带过来的"还该停着"：播放器报 Playing、续播点跳完位之后把画面停回去。
    /// 不带这个，暂停着换清晰度就会自己播起来——用户按了暂停就得还是暂停。
    /// </summary>
    private bool _stayPaused;

    /// <summary>下一发装载是不是"暂停着换的档"（SwitchTo 置位，LoadAsync 认领时清掉）。</summary>
    private bool _pauseNextLoad;

    /// <summary>进真全屏前是不是已经沉浸着（退出全屏要还原成那样，别把程序内全屏一起关了）。</summary>
    private bool _immersiveBeforeFullscreen;

    private static readonly double[] Rates = [0.75, 1, 1.25, 1.5, 2];

    /// <summary>会话内续播：按播放地址记秒数，切回来时跳回去（关进程即清空，不落盘）。
    /// 总时长另记一份：装载时要用"进度/总时长"换成字节偏移，提前把 seek 落点那块取进内存。
    /// 两张表的键自己设上限（<see cref="SeenCap"/>），看过几百条后由 <see cref="_seenOrder"/> 顺次回收。</summary>
    private readonly Dictionary<string, double> _progress = [];
    private readonly Dictionary<string, double> _duration = [];

    /// <summary>
    /// 这一发装载要把画面从第几秒接上（0 = 从零起播）。切档时由 <see cref="SwitchTo"/> 按**新地址**记下续播点，
    /// LoadAsync 开局读进这里；装载路上每个节点都试着跳一次，直到 250ms 那一拍认掉（见 <see cref="SettleResume"/>）。
    /// 之所以要单独留一份：老写法只在 Playing 那一档里跳一次，而"换档重装"这条路的状态事件不一定再走到那儿。
    /// </summary>
    private double _resumeTarget;

    /// <summary>这一跳的期限（TickCount64）：过期没跳到就作废，免得续播表一直被拦着不记账。</summary>
    private long _resumeDeadlineMs;

    /// <summary>
    /// 这一发装载的媒体是不是已经开出来了（MediaOpened 到了）。换档时状态事件是连着来的：
    /// 老流那边排队的一跳可能落到位——没这个标记，那一跳会被当成"跳成了"，而真正的新流还在零秒上。
    /// </summary>
    private bool _resumeMediaOpened;
    private readonly Queue<string> _seenOrder = [];

    // 底部浮动面板：鼠标贴到底缘滑上来，离开面板区域滑下去
    private bool _panelVisible;
    private Storyboard? _panelBoard;

    // ===== 手势层 =====
    /// <summary>拖拽/点击状态：按住视频上下拽，画面跟着指针走；单击暂停，双击点赞。</summary>
    private readonly DispatcherQueueTimer _clickTimer;
    private Point _gestureOrigin;
    private bool _gesturePressed;
    private bool _gestureDragging;
    private bool _gestureMoved;
    private double _gestureOffset;
    private double _gestureReported;
    private double _gestureVelocity;
    private double _gestureLastY;
    private ulong _gestureLastTimestamp;
    private long _gestureLastMoveMs;
    private int _clickCount;
    private const double DragThresholdPx = 6;
    private const double DragHorizontalRatio = 1.2;

    /// <summary>松手前这么多毫秒内还在动，才把那一段速度算作"甩"。</summary>
    private const int FlickWindowMs = 120;

    private const int DoubleClickWindowMs = 300;

    /// <summary>拖拽跟手：按住越过死区后，每一次指针移动都实时把偏移报给宿主。</summary>
    public event EventHandler? DragStarted;
    public event EventHandler<DragDeltaEventArgs>? DragDelta;
    public event EventHandler<DragCompletedEventArgs>? DragCompleted;

    /// <summary>标题区收起时的底部留白，与 VideoInfoControl.xaml 里 Margin 的底边值一致。</summary>
    private const double InfoRestInset = 16;

    /// <summary>
    /// 唤起带：指针进到距底缘这么近才抬面板。宽度取的就是面板收起时那块底距（InfoRestInset），
    /// 再往上就是标题区——判定带压在文字上，鼠标去点标题时面板会弹出来把它顶走。
    /// </summary>
    private const double PanelShowBand = InfoRestInset;

    /// <summary>已经在面板上时上下多留一格余量：在按钮之间斜着挪不至于一闪一隐。</summary>
    private const double PanelHoverSlack = 8;

    // ===== 弹幕层 =====
    // 开关与四项设置都是"真作用在这一层上"的：关掉整层收起并清空，四项改的是正在飞的那些字。
    // 数据走 danmaku/get_v2（NativeFeed.DanmakuPageAsync）：换条清空重取第一页，之后随播放进度续页、吐字。

    /// <summary>弹幕开着吗（对应面板那枚「弹」开关，默认开）。</summary>
    private bool _danmakuOn = true;

    /// <summary>显示区域：0/1/2 → 一行/两行/三行。</summary>
    private int _danmakuRows = 1;

    /// <summary>不透明度 0~1（面板给的是百分比）。</summary>
    private double _danmakuOpacity = 0.8;

    /// <summary>字体档：0/1/2 → 较小/适中/较大，换算成 px 见 DanmakuFontSize。</summary>
    private int _danmakuFont = 1;

    /// <summary>速度档：0/1/2 → 较慢/适中/较快，换算成 px/s 见 DanmakuSpeed。</summary>
    private int _danmakuSpeed = 1;

    /// <summary>
    /// 在飞的弹幕。每条一条合成器动画（Storyboard，跑在合成线程上，不跟 UI 线程抢帧），
    /// 位置由"视频时间 − 这条的时间"决定，所以暂停、拖拽、倍速都跟着视频走（见 SeekDanmaku / SyncDanmakuMotion）。
    /// </summary>
    private readonly List<DanmakuRun> _danmakuRuns = [];

    /// <summary>本会话点"点赞"过的弹幕 id（界面状态，服务端那一发等写接口接线）。</summary>
    private readonly HashSet<string> _likedDanmakuIds = [];

    /// <summary>弹幕被指到时的颜色（抖音红）。</summary>
    private static readonly Windows.UI.Color DanmakuAccent = Windows.UI.Color.FromArgb(0xFF, 0xFE, 0x2C, 0x55);

    /// <summary>弹幕开关的勾是我们自己拨的（装载/另一页改的）：那一路别写回设置、也别弹提示。</summary>
    private bool _danmakuSyncing;

    /// <summary>
    /// 四项弹幕设置的装载标志：初值就是 true——XAML 里那四个滑块建树时各自会先响一次 ValueChanged，
    /// 那时候要是往设置里写，就把刚读进来的真值冲回默认了（音量那一轮踩过同一个坑）。
    /// 收尾的 LoadDanmakuSettings 才把它放下。
    /// </summary>
    private bool _danmakuSettingsLoading = true;

    /// <summary>这条视频第一次上屏记一笔（层尺寸、字宽），弹幕不显示时先从日志看这一行。</summary>
    private bool _danmakuPushed;

    /// <summary>这条视频的弹幕，按时间排好。接口一页只给 32 秒窗口，跟着播放往后续（见 PumpDanmaku）。</summary>
    private readonly List<DanmakuItem> _danmakuItems = [];

    /// <summary>弹幕列表（XAML 那条 DanmakuListFlyout 的数据源）：打开时铺一版快照——行里的心点没点过，看各自的 Liked。</summary>
    public ObservableCollection<ViewModels.DanmakuRowViewModel> DanmakuListRows { get; } = [];

    /// <summary>
    /// 面板上那枚「清晰度」的界面状态：按钮脸上的当前档 + 菜单那几行。
    /// 显示值一律经它绑出去（x:Bind），代码不往控件上的 TextBlock 写字——用户的规矩。
    /// </summary>
    public ViewModels.QualityPickerViewModel Quality { get; } = new();

    /// <summary>游标：下一条该吐的是哪一条。只往前走；往回拖就重新摆（见 SyncDanmakuCursor）。</summary>
    private int _danmakuCursorIndex;

    /// <summary>已经挂上号的 aweme id：换条就换它，取数回来对不上就丢掉（别把上一条的弹幕吐到这条上）。</summary>
    private string _danmakuFor = string.Empty;

    /// <summary>下一页弹幕从哪个毫秒开始；0 = 到头了。</summary>
    private long _danmakuNextStart;

    /// <summary>正在取一页？一次只放一发，别叠请求。</summary>
    private bool _danmakuLoading;

    /// <summary>同一条道上前后两条之间留的空当（像素），免得字挨着字。</summary>
    private const double DanmakuGap = 24;

    /// <summary>鼠标命中的宽容度（像素）：字很细，不放宽根本点不着。</summary>
    private const double DanmakuHitSlop = 5;

    /// <summary>一条道上的记账：最后放上去的那一条（滞后是活的，所以存 run 而不是抄下来的数）。</summary>
    private sealed class DanmakuLane
    {
        public DanmakuRun? Last { get; set; }
    }

    /// <summary>三条道的占用情况（显示区域设成几行就只用前几条）。</summary>
    private readonly DanmakuLane[] _danmakuLanes = [new(), new(), new()];

    /// <summary>
    /// 上一条弹幕落在哪条道上：挑道从它的下一条开始轮（见 <see cref="PickDanmakuLane"/>）。
    /// 不轮的话三条道里永远只有 0 号在干活——"显示区域"改成三行也看不出跟单行有什么区别。
    /// </summary>
    private int _lastDanmakuLane = -1;

    /// <summary>
    /// 自己的弹幕没道可落时先挂这儿（发送那条不能像别人的那样静默丢掉）：PumpDanmaku 每拍来捞，
    /// 等过 6 秒还挤就直接挑最闲的道硬上（见 FlushMineDanmaku）。
    /// </summary>
    private readonly List<DanmakuItem> _danmakuMineWaiting = [];

    /// <summary>已手动上过屏的弹幕 id：时间轴游标万一把它扫到，跳过那一遍别吐重。</summary>
    private readonly HashSet<string> _danmakuPostedIds = [];

    /// <summary>本次运行里自己发出去的弹幕 id：uid 还没记下（服务端应答形状变化/登录态没查成）时也能认出"这条是我发的"。</summary>
    private readonly HashSet<string> _danmakuMineIds = [];

    /// <summary>本会话点过赞的视频 id（界面状态；服务端那份状态只认回执里的 is_digg，不靠这本账）。</summary>
    private readonly HashSet<string> _likedVideos = [];

    /// <summary>点赞在途：一次动作发两发（意图+刷子），双击会把这四发交叉掉，用它在途挡重入。</summary>
    private bool _likeBusy;

    /// <summary>本会话收藏过的视频 id（同上）。</summary>
    private readonly HashSet<string> _collectedVideos = [];

    /// <summary>收藏那枚星的"已收藏"色：抖音的金黄（点赞那枚用 DanmakuAccent 的红）。</summary>
    private static readonly Windows.UI.Color CollectGold = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xC3, 0x00);

    /// <summary>评论面板那几行（点评论按钮时现铺；滚到底续页）。</summary>
    public System.Collections.ObjectModel.ObservableCollection<ViewModels.CommentRowViewModel> CommentRows { get; } = [];

    /// <summary>正在发的评论点赞（按 cid 挡重入：连点两下别发两发相反的写）。</summary>
    private readonly HashSet<string> _commentLikeBusy = new(StringComparer.Ordinal);

    /// <summary>评论面板此刻装的是哪条视频的评论、续页游标、还有没有下一页、有没有正在取的一发（代次挡老应答）。</summary>
    private string _commentFor = string.Empty;
    private long _commentCursor;
    private bool _commentHasMore;
    private bool _commentLoading;
    private bool _commentSending;
    private int _commentGen;

    /// <summary>要置顶的来源评论 cid（分享卡详情）：列表里找到就移到最上面并打“置顶”标。</summary>
    private string _pinnedCommentId = string.Empty;

    /// <summary>来源评论在分享消息里自带的内容：评论接口没回这条时用它兜底，先摆一条普通评论行置顶。</summary>
    private string _pinnedAuthor = string.Empty;
    private string _pinnedText = string.Empty;
    private string _pinnedImageUrl = string.Empty;
    private ViewModels.CommentRowViewModel? _pinnedFallbackRow;

    /// <summary>服务端说这条视频一共有多少条评论（-1 = 还没取到）。</summary>
    private int _commentTotal = -1;

    /// <summary>正在续取"更多回复"的评论行（按一级 cid 挡重入）。</summary>
    private readonly HashSet<string> _replyMoreLoading = new(StringComparer.Ordinal);

    /// <summary>当前输入框是回复哪条评论/回复：root 是一级评论，replyTo 是被点的那条（可为 null）。</summary>
    private ViewModels.CommentRowViewModel? _replyRootRow;
    private ViewModels.CommentReplyRowViewModel? _replyToReply;
    private string _replyToName = string.Empty;

    /// <summary>指针下正指着的那条（指着就按住不动），以及它被冻住时的左沿。</summary>
    private DanmakuRun? _hoverRun;
    private double _hoverX;

    /// <summary>弹幕动作面板开着没（开着的时候点住的那条一直停着，指针划过也不换）。</summary>
    private bool _danmakuFlyoutOpen;

    /// <summary>一句话提示的计时器（加入稍后再看的结果、小窗切换都走它）。</summary>
    private readonly DispatcherQueueTimer _toastTimer;

    /// <summary>音量条的自动收表（鼠标移开后 350ms）。</summary>
    private readonly DispatcherQueueTimer _volumeHoverTimer;

    /// <summary>点静音之前是多少（只活这一次运行；临时状态不落盘）。</summary>
    private int _volumeBeforeMute = 100;

    // ===== 收藏夹悬浮面板 =====
    /// <summary>收藏按钮悬停出来的收藏夹面板（复选框只改状态，关面板后才写）。</summary>
    private readonly CollectFlyoutContent _collectContent;
    private readonly Flyout _collectFlyout;
    private readonly DispatcherQueueTimer _collectHoverTimer;
    private readonly DispatcherQueueTimer _collectFlushTimer;

    /// <summary>收藏写入串行：默认收藏与"加入收藏夹"不要交叉发（视频收藏有滞后一拍，交叉会互相刷）。</summary>
    private readonly SemaphoreSlim _collectWriteGate = new(1, 1);

    private bool _collectPointerInButton;
    private bool _collectPointerInFlyout;
    private string _collectFlyoutVideoId = string.Empty;
    private string _collectClosingVideoId = string.Empty;

    /// <summary>程序化收面板（换条/切台）时先把勾选定住：Closed 万一来得晚，也不能读成下一轮已重置的空选择。</summary>
    private IReadOnlyList<CollectFolder>? _collectClosingSnapshot;
    private IReadOnlyList<CollectFolder> _collectFolderCache = [];

    /// <summary>面板关掉后攒下的写入（按视频排队）；350ms 防抖到点后逐条落。</summary>
    private readonly List<CollectWrite> _collectWrites = [];

    private sealed record CollectWrite(string VideoId, IReadOnlyList<CollectFolder> Folders);

    /// <summary>读夹代次：切视频/重试时让上一发的慢应答作废。</summary>
    private int _collectLoadGen;

    // ===== 小窗模式 =====
    /// <summary>这条舞台开着的小窗（我们自己开的那一个）。</summary>
    private MiniPlayerWindow? _miniWindow;

    public MfStageControl()
    {
        InitializeComponent();
        _ui = DispatcherQueue;

        MfLog.Open();

        // 播放核现在就建：MediaPlayerElement 不等任何初始化回调，组合它自己管
        Video.SetMediaPlayer(_player);
        Video.Loaded += (_, _) => MfLog.Write($"app: 舞台 Loaded {Video.ActualWidth:F0}x{Video.ActualHeight:F0}");

        // MediaPlayer 的事件从媒体线程抛出，一律转回 UI 线程再碰 XAML
        _player.MediaOpened += (_, _) => _ui.TryEnqueue(OnMediaOpened);
        _player.MediaFailed += (_, e) => _ui.TryEnqueue(() => OnMediaFailed(e));
        _player.MediaEnded += (_, _) => _ui.TryEnqueue(OnEndReached);
        _player.PlaybackSession.PlaybackStateChanged += (_, _) => _ui.TryEnqueue(OnPlaybackState);

        // BGM：整首循环（图文帖可能比一首歌长），失败只记一行——图上不该为此弹任何东西
        _bgm.IsLoopingEnabled = true;
        _bgm.MediaFailed += (_, e) => MfLog.Write("app: 图文 BGM 播放失败 " + e.Error + " · " + e.ErrorMessage);
        _bgm.MediaOpened += (s, _) => _ui.TryEnqueue(() =>
        {
            MfLog.Write($"app: 图文 BGM 开出来了 · 时长 {s.PlaybackSession.NaturalDuration} · {s.PlaybackSession.PlaybackState}");

            // 与实况同一道兜底：源落地之前下发的那发 Play 偶尔不算数，开出来还没在放就再点一次
            if (!_standby && _bgm.Source is not null
                && s.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
            {
                _bgm.Play();
            }
        });
        _bgm.PlaybackSession.PlaybackStateChanged += (s, _) => MfLog.Write($"app: 图文 BGM 状态 {s.PlaybackState}");
        _bgm.Volume = Math.Clamp(Volume, 0, 100) / 100.0;

        // 面板尺寸变化记一笔就够——拖拽缩放时它每帧都来，逐条同步落盘会把 resize 循环拖慢
        Video.SizeChanged += (_, e) =>
        {
            long now = Environment.TickCount64;
            if (now - _lastSizeLogMs < SizeLogIntervalMs)
            {
                return;
            }

            _lastSizeLogMs = now;
            MfLog.Write($"app: 面板 {e.PreviousSize.Width:F0}x{e.PreviousSize.Height:F0} → {e.NewSize.Width:F0}x{e.NewSize.Height:F0}");
        };

        // 可用区尺寸变了：先裁 VideoHost 防溢，再决定视频元素动不动。
        // 量尺结论：拖拽缩放时应用代码自占 0ms、帧率却只有 20~40——大头在"视频面逐帧重配"这类帧成本上，
        // 所以骤发期间冻结视频元素，停下 220ms 由合拍器落一次最终位置。
        _layoutSettle = _ui.CreateTimer();
        _layoutSettle.Interval = TimeSpan.FromMilliseconds(220);
        _layoutSettle.IsRepeating = false;
        _layoutSettle.Tick += (_, _) => LayoutVideo();
        VideoHost.SizeChanged += (_, _) =>
        {
            if (!MainWindow.ResizeWorkEnabled)
            {
                // 诊断实验：窗口缩放时只把新尺寸应用一次；冻结/合拍/裁剪全不跑
                LayoutVideo();
                return;
            }

            UpdateVideoHostClip();
            bool midBurst = Environment.TickCount64 - _lastHostResizeMs < 250;
            _lastHostResizeMs = Environment.TickCount64;
            if (!midBurst)
            {
                // 骤发的第一拍照常落位，免得单击式变化也要等合拍
                LayoutVideo();
            }

            _layoutSettle.Stop();
            _layoutSettle.Start();
        };

        // 进度不靠媒体事件：PositionChanged 的频率看解码器心情，统一按 250ms 主动读，停播即停表。
        // 表被暂停停过之后，恢复播放的 Playing 那一档必须把它拉回来（见 OnPlaybackState），
        // 不然就是"进度条有概率不动"。
        _transport = _ui.CreateTimer();
        _transport.Interval = TimeSpan.FromMilliseconds(250);
        _transport.Tick += (_, _) => RefreshTransport();

        // 常驻整理拍：每 5 秒看一眼工作集，越线就修剪——目的是让任务管理器那条"内存"读数稳定在阈值下，
        // 和播放逻辑无关；拆台时一起停掉
        _housekeep = _ui.CreateTimer();
        _housekeep.Interval = TimeSpan.FromSeconds(5);
        _housekeep.Tick += (_, _) => TrimWorkingSetIfHigh();
        _housekeep.Start();

        // 使用账本：每秒一拍，按实际播放状态累计（见 AccumulateWatchTime）
        _watch = _ui.CreateTimer();
        _watch.Interval = TimeSpan.FromSeconds(1);
        _watch.Tick += (_, _) => AccumulateWatchTime();
        _watch.Start();

        // 弹幕的运动不在这儿起表：每条弹幕上屏时自己带一条合成器动画（见 PushDanmaku），
        // 暂停/拖拽/倍速靠 SyncDanmakuMotion 与 SeekDanmaku 对回去。

        // 一句话提示：2.5 秒后自己收
        _toastTimer = _ui.CreateTimer();
        _toastTimer.Interval = TimeSpan.FromMilliseconds(2500);
        _toastTimer.IsRepeating = false;
        _toastTimer.Tick += (_, _) => StageToast.Visibility = Visibility.Collapsed;

        // 音量条：鼠标离开图标/条之后 350ms 才收——这段空档够你从图标挪到条上
        _volumeHoverTimer = _ui.CreateTimer();
        _volumeHoverTimer.Interval = TimeSpan.FromMilliseconds(350);
        _volumeHoverTimer.IsRepeating = false;
        _volumeHoverTimer.Tick += (_, _) => VolumeFlyout.Hide();

        // 收藏夹面板：和音量条同一套悬停手感（Transient 不抢焦点，收面板交给悬停表）。
        _collectContent = new CollectFlyoutContent();
        _collectContent.RetryRequested += (_, _) => _ = LoadCollectFoldersAsync(force: true);
        _collectContent.PointerEntered += OnCollectFlyoutPointerEntered;
        _collectContent.PointerExited += OnCollectFlyoutPointerExited;
        _collectFlyout = new Flyout
        {
            Content = _collectContent,
            Placement = FlyoutPlacementMode.Left,
        };
        _collectFlyout.Closed += OnCollectFlyoutClosed;

        _collectHoverTimer = _ui.CreateTimer();
        _collectHoverTimer.Interval = TimeSpan.FromMilliseconds(300);
        _collectHoverTimer.IsRepeating = false;
        _collectHoverTimer.Tick += (_, _) =>
        {
            if (!_collectPointerInButton && !_collectPointerInFlyout)
            {
                _collectFlyout.Hide();
            }
        };

        _collectFlushTimer = _ui.CreateTimer();
        _collectFlushTimer.Interval = TimeSpan.FromMilliseconds(350);
        _collectFlushTimer.IsRepeating = false;
        _collectFlushTimer.Tick += (_, _) => _ = FlushPendingCollectWritesAsync();

        // 作者行“图文”胶囊右侧的上一张/下一张：舞台负责真正翻页。
        Info.ImageStepRequested += (_, direction) => NavigateImage(direction);

        // 图文自动切换的表：每落到一张静态图上起一发，翻页/换帖/拆台都会重摆或停掉
        _imageDwell = _ui.CreateTimer();
        _imageDwell.Interval = TimeSpan.FromMilliseconds(ImageDwellMilliseconds);
        _imageDwell.IsRepeating = false;
        _imageDwell.Tick += (_, _) => OnImageCycleDue();

        // 沉浸态是跨控件共享的：外壳去收顶栏/藏侧边栏，舞台这颗按钮换个意思（图标点亮）。
        // 静态事件必须成对退订，否则拆掉的页面会被事件表留住
        Chrome.ImmersiveChanged += OnImmersiveChanged;

        // 一条视频一个页面：页面被换掉就该把播放源松掉，不留给 GC 拖
        Unloaded += (_, _) => Teardown();

        // 页面又挂回来（推荐页是缓存页，切走再回来不重建控件）：把拆掉的架子重新架上并重装当前这条
        Loaded += (_, _) => Rearm();

        // 上场时要对一次评论面板顶距：控件可能是在沉浸态里新造的，那发事件它没赶上
        Loaded += (_, _) => ApplyCommentPanelTopInset();

        // Slider 模板里的 Thumb 会把指针事件标记为已处理，XAML 上挂的 Pointer* 收不到——拖完松手不 seek 就栽在这
        ProgressSlider.AddHandler(PointerPressedEvent, new PointerEventHandler(OnProgressPressed), true);
        ProgressSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnProgressReleased), true);
        ProgressSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnProgressCaptureLost), true);
        ProgressSlider.ThumbToolTipValueConverter = new SecondsToolTipConverter();

        // 弹幕设置那四条滑块的气泡：显示的就是标题右边那个值（"适中"/"两行"/"78%"），不是滑块原始值 0/1/2
        DanmakuOpacitySlider.ThumbToolTipValueConverter = new DanmakuStepToolTipConverter(static v => v + "%");
        DanmakuAreaSlider.ThumbToolTipValueConverter = new DanmakuStepToolTipConverter(RowsStepText);
        DanmakuFontSlider.ThumbToolTipValueConverter = new DanmakuStepToolTipConverter(FontStepText);
        DanmakuSpeedSlider.ThumbToolTipValueConverter = new DanmakuStepToolTipConverter(SpeedStepText);

        // 用 Root.PointerMoved 判断鼠标 Y 坐标：在底部区域内→面板滑上来，离开→滑下去
        Root.AddHandler(PointerMovedEvent, new PointerEventHandler(OnRootPointerMoved), true);

        // 弹幕交互：层的留白不吃鼠标（Canvas 没有 Background），所以"指针下是哪条"得在 Root 这一层自己算
        Root.AddHandler(PointerMovedEvent, new PointerEventHandler(OnDanmakuPointerMoved), true);
        Root.AddHandler(PointerExitedEvent, new PointerEventHandler(OnDanmakuPointerExited), true);
        Root.AddHandler(TappedEvent, new TappedEventHandler(OnDanmakuTapped), true);

        // 初始隐藏面板：把面板推到屏底以下（+16 是底部 margin）
        PanelTransform.Y = (ControlPanel.ActualHeight > 0 ? ControlPanel.ActualHeight : 60) + 16;
        ControlPanel.Opacity = 0;

        // 收尾对齐一次连播开关：依赖属性可能比开关先到位（见 SyncAutoPlayToggle）
        SyncAutoPlayToggle();

        // 弹幕开关同理（依赖属性先到位那一下层还没建出来，这里补一次）
        ApplyDanmakuOn(_danmakuOn);
        SyncDanmakuToggle();

        // 弹幕那四项设置从共享设置摆到位（滑块初值先自己响过一轮，这里覆盖成记住的值）
        LoadDanmakuSettings();

        // 音量滑块：事件在树建好之后才接（XAML 里那条会在载入时先响一次）。
        // 判定"是不是用户在动"交给 OnVolumeSliderChanged 里的"小窗开着没"，这条路更实。
        VolumeSlider.ValueChanged += OnVolumeSliderChanged;

        // 音量条一律不抢焦点：悬停打开、点按钮（Button 自带 Flyout 那一路）都会用它
        VolumeFlyout.ShowMode = FlyoutShowMode.Transient;

        AutoPlayToggle.Toggled += OnAutoPlayToggled;
        ApplyVolume();

        // 清晰度：菜单与按钮脸先铺一次（依赖属性可能比树先到位，真值到齐时 OnSourceChanged/OnQualitySettingChanged 还会再来一遍）
        SyncQualityMenu();

        // 那枚点每次翻开菜单前再对一遍：RadioMenuFlyoutItem 的圆点是打开这一下才渲染的，
        // 摆早了（或在菜单关着时改的）它不一定认账——与倍速同一路，这里多给一道保险。
        QualityButton.Flyout.Opening += (_, _) => SyncQualityMenu();

        // 手势层：拖拽换台、单击暂停、双击点赞
        _clickTimer = _ui.CreateTimer();
        _clickTimer.Interval = TimeSpan.FromMilliseconds(DoubleClickWindowMs);
        _clickTimer.IsRepeating = false;
        _clickTimer.Tick += (_, _) => FlushClick();

        GestureLayer.AddHandler(PointerPressedEvent, new PointerEventHandler(OnGesturePressed), true);
        GestureLayer.AddHandler(PointerMovedEvent, new PointerEventHandler(OnGestureMoved), true);
        GestureLayer.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnGestureReleased), true);
        GestureLayer.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnGestureCaptureLost), true);
        GestureLayer.PointerWheelChanged += OnGestureWheel;
    }

    /// <summary>幕外候场中（见 <see cref="Standby"/>）。</summary>
    private bool _standby;

    /// <summary>
    /// 候场态：静音 + 起播即停。幕外那条页面用它预热下一条——媒体开出来、首帧解好停在那儿，
    /// 轮到它时解除候场就直接续播，不用等网络。
    /// </summary>
    public bool Standby
    {
        get => _standby;
        set
        {
            if (_standby == value)
            {
                return;
            }

            _standby = value;
            ApplyVolume();
            if (_standby)
            {
                _imageDwell.Stop();   // 幕外那页别再自动翻图
                _bgm.Pause();         // 幕外那页的 BGM 也停（静音不够：整首还在空转）
                if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                {
                    _player.Pause();
                }
            }
            else
            {
                // 上台这条可能是提前建好的（那期间用户改过设置）：四项弹幕设置按当下的共享设置再对齐一次。
                // 值没变就写不进去（ObservableProperty 相等不触发），所以不会凭空多落一次盘。
                LoadDanmakuSettings();

                if (_player.Source is not null)
                {
                    _player.Play();
                }

                ArmImageDwell();   // 图文这条：上台这一拍把当前这张的计时接上
                if (_bgm.Source is not null)
                {
                    _bgm.Play();   // 图文 BGM 跟着上台
                }
            }
        }
    }

    /// <summary>
    /// 页面被换掉：停表、松掉播放源（源交给延后收尸，见 <see cref="_corpses"/>）。
    /// 幂等：宿主显式 Suspend 之后控件自己的 Unloaded 还会再来一次，重复拆不能把账拆乱。
    /// </summary>
    private void Teardown()
    {
        if (_tornDown)
        {
            return;
        }

        _tornDown = true;
        _transport.Stop();
        _housekeep.Stop();
        _watch.Stop();
        _watchLastMs = 0;

        // 使用账本：离场这一下把内存里还没落盘的秒数写掉（小窗接管那条也在下面提前返回之前落）
        UsageStore.Flush();
        _toastTimer.Stop();
        _volumeHoverTimer.Stop();
        _collectHoverTimer.Stop();
        _collectFlushTimer.Stop();
        _imageDwell.Stop();
        StopImageMusic();

        // 图文帖的解码帧不小（一帖最多九张 WebP），离场时一并松掉；回场 Rearm 会按当前条目重新解码。
        _imagePostVersion++;
        CancelImageSlide();
        _imageFrames = Array.Empty<SoftwareBitmapSource>();
        ImageStill.Source = null;
        ImageIncoming.Source = null;
        ImageGallery.Visibility = Visibility.Collapsed;

        // 评论行也跟着松：大头是头像/缩略图位图，缓存页在别的页面待着时没人看，留着只是占内存。
        // 回场 Rearm 时面板还开着就按当前条目重取（见 Rearm）。
        _commentGen++;
        _commentLoading = false;
        CommentRows.Clear();
        _pinnedFallbackRow = null;
        _commentFor = string.Empty;
        _commentCursor = 0;
        _commentHasMore = true;
        _commentTotal = -1;
        ClearReplyTarget();
        HideCommentImage();
        ApplyCommentTotal();
        SetCommentStatus(string.Empty);

        if (_collectFlyout.IsOpen)
        {
            _collectFlyout.Hide();
        }

        if (ShareFlyout.IsOpen)
        {
            ShareFlyout.Hide();
        }

        ClearDanmaku();
        StopLiveVideo();

        // 静态事件成对退订：不退订的话拆掉的页面会被事件表留住（每换一次台留一份）
        Chrome.ImmersiveChanged -= OnImmersiveChanged;

        // 小窗还开着：这条视频整套交给它继续放（播放器挂在它那儿，源物件也交过去）。
        // 这里只拆页面自己的计时器与订阅，**绝不能碰播放器**——一 Pause 或 Source=null 就把小窗里的画面掐了。
        // 用户点名的要求：切程序内页面时当前视频要能接着播。
        if (_miniWindow is not null)
        {
            _miniWindow.AdoptPlayback(_media, _tempPath);
            _media = null;
            _tempPath = null;
            _stream = null;
            return;
        }
        try
        {
            _player.Pause();
        }
        catch (Exception e)
        {
            MfLog.Write("app: 拆台时 Pause 抛了：" + e.Message);
        }

        _player.Source = null;
        Retire(_media, _tempPath);
        _media = null;
        _stream = null;
        _tempPath = null;
        MfLog.Write("app: 拆台释放 " + UrlStub(_playing) + " · " + MemNote());
    }

    /// <summary>
    /// 重新上场：把 <see cref="Teardown"/> 收掉的订阅与节拍重新架上，再按当前条目重装一遍。
    /// 推荐页是缓存页（NavigationCacheMode=Required），切走再回来控件不重建——不补这一下，
    /// 舞台就永远停在"拆过"的样子：条目照来、地址照给，却一声不响，只剩一片黑。
    /// </summary>
    private void Rearm()
    {
        if (!_tornDown)
        {
            return;
        }

        _tornDown = false;
        Chrome.ImmersiveChanged += OnImmersiveChanged;
        _transport.Start();
        _housekeep.Start();
        _watch.Start();

        // 离场时评论行已经清空：面板还开着就按当前条目重取第一页
        //（Bind 在 Suspend 期间先跑过的那一路已经在取，别重发）
        if (CommentSplit.IsPaneOpen && !_commentLoading)
        {
            CommentScroll.ChangeView(null, 0, null, true);
            _ = LoadCommentsAsync(reset: true);
        }

        // 小窗还拿着这个播放器在放：回场只把页面的架子架好，别把源重新装进小窗那个播放器
        if (_miniWindow is not null)
        {
            UpdatePipIcon();
            return;
        }

        // 拆台时只松了源，没清"正在放哪条"的账：清掉这几笔，Load 才会真的重装一遍
        _playing = string.Empty;
        _opening = false;
        _loadVersion++;   // 拆台前可能还有一发在路上的装载，这一下让它彻底作废

        MfLog.Write("app: 重新上场 → " + UrlStub(PickUrl()));
        LoadEffectiveSource();
        ResetDanmaku(ItemId ?? string.Empty);
    }

    /// <summary>
    /// 宿主离场（缓存页面切走）：主动松开播放源。控件自己的 Unloaded 也会拆一次，这里只是把
    /// 释放时机钉死在导航上，不依赖框架是否真的把视觉树卸下来。
    /// </summary>
    public void Suspend() => Teardown();

    /// <summary>宿主回场：按当前依赖属性重新装载（顺序是先 Bind/Suspend 期间落属性，再 Resume 只装一次）。</summary>
    public void Resume() => Rearm();

    /// <summary>播放地址（play_addr 里 www.douyin.com/aweme/v1/play/ 那条）；图文帖是 imagepost://url1|url2。</summary>
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

    /// <summary>画面原始宽度（像素）。</summary>
    public int VideoWidth
    {
        get => (int)GetValue(VideoWidthProperty);
        set => SetValue(VideoWidthProperty, value);
    }

    /// <summary>画面原始高度（像素）。</summary>
    public int VideoHeight
    {
        get => (int)GetValue(VideoHeightProperty);
        set => SetValue(VideoHeightProperty, value);
    }

    /// <summary>信息区：作者昵称（不含 @）。</summary>
    public string? Author
    {
        get => (string?)GetValue(AuthorProperty);
        set => SetValue(AuthorProperty, value);
    }

    /// <summary>这条作品的 BGM 地址（图文帖出声用；视频条目的音轨在流里，给了也不用）。</summary>
    public string? Music
    {
        get => (string?)GetValue(MusicProperty);
        set => SetValue(MusicProperty, value);
    }

    /// <summary>信息区：预格式化的相对时间文本。</summary>
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

    /// <summary>信息区：朋友推荐人（空串则整行不显示）。</summary>
    public string? FriendRecommender
    {
        get => (string?)GetValue(FriendRecommenderProperty);
        set => SetValue(FriendRecommenderProperty, value);
    }

    /// <summary>竖排：作者头像地址。</summary>
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

    public string? CommentText
    {
        get => (string?)GetValue(CommentTextProperty);
        set => SetValue(CommentTextProperty, value);
    }

    public string? CollectText
    {
        get => (string?)GetValue(CollectTextProperty);
        set => SetValue(CollectTextProperty, value);
    }

    public string? ShareText
    {
        get => (string?)GetValue(ShareTextProperty);
        set => SetValue(ShareTextProperty, value);
    }

    /// <summary>点赞数的原始值（未格式化）：只为本会话点过的那一下 +1 重算用。</summary>
    public int DiggRaw
    {
        get => (int)GetValue(DiggRawProperty);
        set => SetValue(DiggRawProperty, value);
    }

    /// <summary>评论数的原始值（未格式化）。</summary>
    public int CommentRaw
    {
        get => (int)GetValue(CommentRawProperty);
        set => SetValue(CommentRawProperty, value);
    }

    /// <summary>收藏数的原始值（未格式化）：只为本会话收藏过的那一下 +1 重算用。</summary>
    public int CollectRaw
    {
        get => (int)GetValue(CollectRawProperty);
        set => SetValue(CollectRawProperty, value);
    }

    /// <summary>分享数的原始值（未格式化）。</summary>
    public int ShareRaw
    {
        get => (int)GetValue(ShareRawProperty);
        set => SetValue(ShareRawProperty, value);
    }

    /// <summary>播放音量，0~100 整数百分比。</summary>
    public int Volume
    {
        get => (int)GetValue(VolumeProperty);
        set => SetValue(VolumeProperty, value);
    }

    /// <summary>记住的清晰度档（空串=默认）。它是真值：两条视频页面、设置都看这一个。</summary>
    public string QualityKey
    {
        get => (string)GetValue(QualityKeyProperty);
        set => SetValue(QualityKeyProperty, value);
    }

    /// <summary>这条视频的清晰度档位（从高到低 + 默认流属于哪一档）；没绑就是空表，菜单那几行全置灰。</summary>
    public VideoQualitySet? Qualities
    {
        get => (VideoQualitySet?)GetValue(QualitiesProperty);
        set => SetValue(QualitiesProperty, value);
    }

    /// <summary>这条视频的 id（aweme_id）：加入稍后再看按它走。</summary>
    public string? ItemId
    {
        get => (string?)GetValue(ItemIdProperty);
        set => SetValue(ItemIdProperty, value);
    }

    /// <summary>作者 uid：分享卡内容 JSON 要带。</summary>
    public string? AuthorId
    {
        get => (string?)GetValue(AuthorIdProperty);
        set => SetValue(AuthorIdProperty, value);
    }

    /// <summary>作者 sec_uid：点作者头像进主页时交给宿主（控件自己不导航，宿主才有 Frame）。</summary>
    public string? AuthorSecUid
    {
        get => (string?)GetValue(AuthorSecUidProperty);
        set => SetValue(AuthorSecUidProperty, value);
    }

    /// <summary>点了作者头像：把 sec_uid 与昵称回抛给宿主页面去导航（宿主没有主页可开时也可以只更新提示）。</summary>
    public event EventHandler<AuthorOpenEventArgs>? AuthorOpenRequested;

    /// <summary>自动连播（持久化设置）。开关的勾与这个值永远一致，两条页面也不会各说各话。</summary>
    public bool AutoPlay
    {
        get => (bool)GetValue(AutoPlayProperty);
        set => SetValue(AutoPlayProperty, value);
    }

    /// <summary>弹幕开关（持久化设置）。同上：它是真值，控件的 <c>_danmakuOn</c> 只是镜像。</summary>
    public bool DanmakuOn
    {
        get => (bool)GetValue(DanmakuOnProperty);
        set => SetValue(DanmakuOnProperty, value);
    }

    /// <summary>前台这条播到底了（只在开了连播且页面在台上时抛）：由页面决定去取下一条。</summary>
    public event EventHandler? Ended;

    /// <summary>
    /// 换台是否还挂着：从发起装载到播放器报 Playing 之前。
    /// 页面用它挡滚轮——上一次没落地就再滚，装载会被一路打断，每条都从头开始拉流，看着反而更慢。
    /// 卡住的装载不该拉滚轮陪葬：超过 <see cref="SwitchGuardMilliseconds"/> 就放手。
    /// </summary>
    public bool IsSwitching => _opening && Environment.TickCount64 - _switchStartedMs <= SwitchGuardMilliseconds;

    // 换条：先铺菜单，再按"记住的档"挑一条流装载（这条视频没有该档就自动降档，见 LoadEffectiveSource）
    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var stage = (MfStageControl)d;
        stage.Info.IsImagePost = IsImagePostSource(stage.Source);
        stage.LoadEffectiveSource();
    }

    private static bool IsImagePostSource(string? source) =>
        source is not null && source.StartsWith(ImageScheme, StringComparison.Ordinal);

    // 档位表到了（或换了一条）：按记住的档重挑一次；已经在放这一档就是个空转
    private static void OnQualitiesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MfStageControl)d).ApplyQuality();

    // 记住的档变了（点了菜单、另一条页面改的、启动装载）：换流 + 把菜单对齐
    private static void OnQualitySettingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MfStageControl)d).ApplyQuality();

    private static void OnCoverChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        _ = ((MfStageControl)d).SetCoverAsync((string?)e.NewValue ?? string.Empty);

    private static void OnVideoSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MfStageControl)d).LayoutVideo();

    private static void OnInfoChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var stage = (MfStageControl)d;
        stage.Info.Author = stage.Author;
        stage.Info.TimeText = stage.TimeText;
        stage.Info.Desc = stage.Desc;
        stage.Info.FriendRecommender = stage.FriendRecommender;
        stage.Info.IsImagePost = IsImagePostSource(stage.Source);
    }

    private static void OnRailChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((MfStageControl)d).RefreshRail();

    private static void OnVolumeSettingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((MfStageControl)d).ApplyVolume();

    private void RefreshRail()
    {
        // "这一下是我点的"记在本会话那两本小账里：点过的这一格用**原始数 +1** 重算（格式化的那几个
        // 是 feed 下发的、不含我刚点的那一下），没点过的照旧用页面给的那份格式化文字（值有出处）。
        string id = ItemId ?? string.Empty;
        bool liked = id.Length > 0 && _likedVideos.Contains(id);
        bool collected = id.Length > 0 && _collectedVideos.Contains(id);
        LikeCount.Text = liked ? CountText.Format(DiggRaw + 1) : DiggText ?? "0";
        CommentCount.Text = CommentText ?? "0";
        FavoriteCount.Text = collected ? CountText.Format(CollectRaw + 1) : CollectText ?? "0";
        RepostCount.Text = ShareText ?? "0";
        LikeGlyph.Glyph = liked ? "\uEB52" : "\uEB51";                    // 爱心：实心/空心
        LikeGlyph.Foreground = new SolidColorBrush(liked ? DanmakuAccent : Microsoft.UI.Colors.White);
        FavoriteGlyph.Glyph = collected ? "\uE735" : "\uE734";            // 星：实心/空心
        FavoriteGlyph.Foreground = new SolidColorBrush(collected ? CollectGold : Microsoft.UI.Colors.White);
        _ = LoadAvatarAsync();
    }

    /// <summary>评论面板的标题：括号里只写服务端总评论数；还没取到时先写"评论"。</summary>
    private void ApplyCommentTotal() =>
        CommentTitleText.Text = _commentTotal >= 0 ? $"评论（{CountText.Format(_commentTotal)}）" : "评论";

    /// <summary>列表上的临时状态（正在取 / 还没评论）；空字符串就收起，不占布局。</summary>
    private void SetCommentStatus(string text)
    {
        CommentStatus.Text = text;
        CommentStatus.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private async Task SetCoverAsync(string url)
    {
        int version = ++_coverVersion;
        SoftwareBitmapSource? bitmap = url.Length > 0 ? await BlurCover.LoadAsync(url) : null;
        if (version == _coverVersion)
        {
            CoverImage.Source = bitmap;
        }
    }

    private int ImageCount() => _imageFrames.Count;

    /// <summary>一张图横向滑过多少毫秒（缓动与换台同一套 ease）。</summary>
    private const int ImageSlideMilliseconds = 260;

    /// <summary>进行中的翻页动画（持有 Storyboard，防止被 GC 收走后动画中途停住）。</summary>
    private Storyboard? _imageSlide;

    /// <summary>
    /// 翻页：新图从侧面滑进来、旧图滑出去，落定后把新图落到主图上。
    /// 图怎么缩放由 XAML 的 Stretch 负责，这里只搬内容。
    /// </summary>
    private void StartImageSlide(SoftwareBitmapSource next, int direction)
    {
        CancelImageSlide();

        ImageIncoming.Source = next;
        ImageIncoming.Visibility = Visibility.Visible;

        double offset = ImageGallery.ActualWidth;
        var outgoing = ImageStillTransform;
        var incoming = ImageIncomingTransform;

        // 与换台同一套：基准先改成终点，FillBehavior=Stop 停回基准正好停在终点，不会闪回
        outgoing.X = -direction * offset;
        incoming.X = 0;

        var board = new Storyboard();
        AddImageSlide(board, outgoing, 0, -direction * offset);
        AddImageSlide(board, incoming, direction * offset, 0);
        board.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_imageSlide, board))
            {
                return;
            }

            _imageSlide = null;
            ImageStill.Source = ImageIncoming.Source;
            ImageIncoming.Source = null;
            ImageIncoming.Visibility = Visibility.Collapsed;
            ImageStillTransform.X = 0;
            ImageIncomingTransform.X = 0;

            if (!_tornDown)
            {
                UpdateLiveVideo();   // 落页：装这一页的实况
            }
        };
        _imageSlide = board;
        board.Begin();
    }

    private static void AddImageSlide(Storyboard board, TranslateTransform transform, double from, double to)
    {
        var slide = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromMilliseconds(ImageSlideMilliseconds)),
            FillBehavior = FillBehavior.Stop,
        };
        slide.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = from,
        });
        slide.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ImageSlideMilliseconds)),
            Value = to,
            KeySpline = new KeySpline { ControlPoint1 = new Point(0.25, 0.1), ControlPoint2 = new Point(0.25, 1) },
        });
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "X");
        board.Children.Add(slide);
    }

    /// <summary>停下翻页动画并把纸面落定：中途再翻、切视频、换帖都不能让半张图挂在屏幕上。</summary>
    private void CancelImageSlide()
    {
        if (_imageSlide is { } slide)
        {
            _imageSlide = null;
            slide.Stop();
        }

        ImageStillTransform.X = 0;
        ImageIncomingTransform.X = 0;
        if (ImageIncoming.Visibility == Visibility.Visible)
        {
            // 上一次切到一半：纸面落到已经滑进来的那张上，下一次从这里继续
            ImageStill.Source = ImageIncoming.Source;
            ImageIncoming.Source = null;
            ImageIncoming.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>翻页指示、作者行的切换按钮状态，以及底部按图文数分段的进度条。</summary>
    private void ApplyImagePager(int page, int count)
    {
        ImagePagerText.Text = count > 1 ? $"{page + 1} / {count}" : string.Empty;
        Info.ImageCount = count;
        Info.ImagePage = page;

        ProgressSlider.Visibility = Visibility.Collapsed;
        ImageProgressBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateImageProgress(count, page);
    }

    /// <summary>底部图文进度：每张一段，当前段亮白、其余压暗；段本身是点击热区，点哪段去第几张。</summary>
    private void UpdateImageProgress(int count, int page)
    {
        if (count != _imageProgressCount)
        {
            _imageProgressCount = count;
            ImageProgressBar.ColumnDefinitions.Clear();
            ImageProgressBar.Children.Clear();
            for (int i = 0; i < count; i++)
            {
                ImageProgressBar.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star),
                });

                // 热区占整格（24 高），看得见的那根 4 高的条缩在中间
                var segment = new Border
                {
                    Background = TransparentBrush,
                    Tag = i,
                };
                ToolTipService.SetToolTip(segment, "第 " + (i + 1) + " 张");
                segment.Tapped += OnImageSegmentTapped;
                segment.Child = new Border
                {
                    Height = 4,
                    CornerRadius = new CornerRadius(2),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(segment, i);
                ImageProgressBar.Children.Add(segment);
            }
        }

        for (int i = 0; i < ImageProgressBar.Children.Count; i++)
        {
            if (ImageProgressBar.Children[i] is Border { Child: Border pill })
            {
                pill.Background = i == page ? ImageProgressOn : ImageProgressOff;
            }
        }
    }

    /// <summary>点分段进度条：翻到这一段对应的那张。</summary>
    private void OnImageSegmentTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Border { Tag: int index })
        {
            GoToImage(index);
        }
    }

    /// <summary>图文模式收起播放/时间/整条进度，换成按图分段的进度条。</summary>
    private void SetImageModeChrome(bool image)
    {
        ProgressSlider.Visibility = image ? Visibility.Collapsed : Visibility.Visible;
        PlayPauseButton.Visibility = image ? Visibility.Collapsed : Visibility.Visible;
        TimeLabel.Visibility = image ? Visibility.Collapsed : Visibility.Visible;
        if (!image)
        {
            _imageDwell.Stop();
            StopImageMusic();   // 换了视频：图文帖那首 BGM 收掉，别跟着视频一起响
            ImageProgressBar.Visibility = Visibility.Collapsed;
            Info.ImageCount = 0;
            Info.ImagePage = 0;
        }
    }

    private void NavigateImage(int direction) => GoToImage(_imagePage + direction);

    /// <summary>
    /// 翻到第 index 张：作者行的按钮、底部分段进度条、自动切换三处共用这一条。
    /// 越界夹回范围内，目标就是脚下这张就什么都不做。图怎么缩放由 XAML Stretch 负责。
    /// </summary>
    private void GoToImage(int index, bool animate = true)
    {
        if (ImageGallery.Visibility != Visibility.Visible)
        {
            return;
        }

        int count = ImageCount();
        if (count <= 0)
        {
            return;
        }

        int page = Math.Clamp(index, 0, count - 1);
        if (page == _imagePage)
        {
            return;
        }

        int direction = page > _imagePage ? 1 : -1;

        // 上一张的实况先收掉；落定后再装新页的，滑动期间不让它盖在图上
        StopLiveVideo();
        _imagePage = page;
        ApplyImagePager(page, count);

        if (animate)
        {
            StartImageSlide(_imageFrames[page], direction);
        }
        else
        {
            CancelImageSlide();
            ImageStill.Source = _imageFrames[page];
            UpdateLiveVideo();
        }

        ArmImageDwell();   // 静态三秒从切换这一拍起算；实况等播完
    }

    /// <summary>
    /// 图文帖盖着整层，滚轮由 <see cref="GestureLayer"/> 截下转到这里（见 OnGestureWheel）：
    /// 图文页不认滚轮翻图，这一发当"换台"交回宿主。
    /// </summary>
    private void OnImageWheel(int delta) => WheelRolled?.Invoke(this, delta);

    /// <summary>图文帖源的 scheme 前缀（NativeFeed.ParseCards 编码，Load 解码）。</summary>
    internal const string ImageScheme = "imagepost://";

    /// <summary>当前页对应的实拍地址（无实况=空串）。翻页/离开图文时对它的播放负责。</summary>
    private string _liveUrl = string.Empty;

    /// <summary>LiveVideo 的播放器（MediaEnded 一进一出成对，翻页/离开要退订）。</summary>
    private MediaPlayer? _livePlayer;

    /// <summary>静态图盖住尾帧的那段渐显（翻页/离开要停掉，别让动画值粘到新一页上）。</summary>
    private Storyboard? _liveFade;

    /// <summary>实况图循环代次：翻页/离开时 +1，作废还在等的那一秒静态画面。</summary>
    private int _liveVersion;

    /// <summary>
    /// 图文帖代次：换条（视频或另一帖图文）就 +1。ShowImagePost 要解码再铺图，
    /// 解码回来时这一帖可能已经不在台上了——代次对不上就不许再动舞台。
    /// </summary>
    private int _imagePostVersion;

    /// <summary>当前图文页索引（翻页切实况视频用）。</summary>
    private int _imagePage;

    /// <summary>这一帖解出来的静态图（主图 Source 从这里取，实况尾帧渐显也用同一份）。</summary>
    private IReadOnlyList<SoftwareBitmapSource> _imageFrames = Array.Empty<SoftwareBitmapSource>();

    private static readonly SolidColorBrush ImageProgressOn = new(Windows.UI.Color.FromArgb(255, 255, 255, 255));
    private static readonly SolidColorBrush ImageProgressOff = new(Windows.UI.Color.FromArgb(70, 255, 255, 255));

    /// <summary>分段进度条的热区底色：全透明但非 null——不铺黑底，命中测试照样落在它身上。</summary>
    private static readonly SolidColorBrush TransparentBrush = new(Microsoft.UI.Colors.Transparent);
    private int _imageProgressCount = -1;

    /// <summary>图文页里解出来的图与实拍一一对应（没有实拍的那格是空串）。</summary>
    private IReadOnlyList<string> _liveUrls = Array.Empty<string>();

    /// <summary>静态那张停多久自动往后切（用户点名：静态三秒、实况播完就切）。</summary>
    private const int ImageDwellMilliseconds = 3000;

    /// <summary>图文自动切换的表：每落到一张静态图上起一发。</summary>
    private readonly DispatcherQueueTimer _imageDwell;

    /// <summary>
    /// 图文层把滚轮截下来了（不截就成横向翻页）：换台这件事交回宿主，与页面自己那条滚轮路同一套节奏。
    /// 参数是滚轮增量。
    /// </summary>
    public event EventHandler<int>? WheelRolled;

    /// <summary>第 page 张带的实拍地址（没有实况=空串）。</summary>
    private string LiveUrlOf(int page) => page >= 0 && page < _liveUrls.Count ? _liveUrls[page] : string.Empty;

    /// <summary>图文自动切换能不能动：拆台、幕外候场、图列已收起的时候都不动。</summary>
    private bool CanAutoAdvance() =>
        !_tornDown && !_standby && ImageGallery is { Visibility: Visibility.Visible };

    /// <summary>
    /// 摆好这一张的自动切换：静态的停三秒（到点见 OnImageCycleDue），实况的交给 MediaEnded——
    /// 播完即推进，所以这里不起表（单张实况那样没什么可切的也照此，回零重播由 OnLiveEnded 收尾）。
    /// asStill：实拍没装载起来的那张当静态处理，别被上面那条"实况不起表"挡住。
    /// </summary>
    private void ArmImageDwell(bool asStill = false)
    {
        _imageDwell.Stop();
        if (!CanAutoAdvance() || (!asStill && LiveUrlOf(_imagePage).Length > 0))
        {
            return;
        }

        _imageDwell.Start();
    }

    /// <summary>静态那张的三秒到点：往后推一拍。</summary>
    private void OnImageCycleDue()
    {
        _imageDwell.Stop();
        if (CanAutoAdvance())
        {
            AdvanceImageCycle();
        }
    }

    /// <summary>
    /// 图文自动推进一拍（静态到点、实况播完都走这里）：没到末张就翻过去；
    /// 末张开着连播就交回宿主换条，没开就绕回第一张（与视频的单条循环同一条规矩）。
    /// 返回 false = 还在原地（单张实况由调用方回零重播）。
    /// </summary>
    private bool AdvanceImageCycle()
    {
        int count = ImageCount();
        if (count <= 1)
        {
            if (count == 1 && _autoPlay)
            {
                Ended?.Invoke(this, EventArgs.Empty);
                return true;
            }

            return false;
        }

        int page = Math.Clamp(_imagePage, 0, count - 1);
        if (page < count - 1)
        {
            GoToImage(page + 1);
            return true;
        }

        if (_autoPlay)
        {
            Ended?.Invoke(this, EventArgs.Empty);
            return true;
        }

        GoToImage(0, animate: false);
        return true;
    }

    /// <summary>当前图文页第几张（翻页指示与切实况用）。</summary>
    private int CurrentImagePage => _imagePage;

    // ===== 图文帖的 BGM =====
    // 抖音的图文帖是"图 + 一首 BGM"：画面本身没有声轨，出声全在 music.play_url 上
    // （feed 与 detail 两路都带，静态 CDN 地址、不用签名——NativeCheck --music-scan 实测）。
    // 翻图不重头放：整帖一首歌，切到下一张接着放。

    /// <summary>Music 依赖属性到齐（或换了条作品）：图文帖在图上就换歌，视频条目不碰。</summary>
    private static void OnMusicChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MfStageControl)d).ApplyImageMusic();

    /// <summary>
    /// 把这条图文帖的 BGM 放起来。同一地址不重开（翻页/换帖都走这里）；不在图文模式、没有地址、
    /// 或已拆台就收摊。候场那条只装不上声——上台那一拍由 <see cref="Standby"/> 接上。
    /// </summary>
    private void ApplyImageMusic()
    {
        string url = Music ?? string.Empty;
        if (_tornDown || ImageGallery.Visibility != Visibility.Visible || url.Length == 0)
        {
            MfLog.Write($"app: 图文 BGM 不适用 torn={_tornDown} vis={ImageGallery.Visibility} len={url.Length}");
            StopImageMusic();
            return;
        }

        if (string.Equals(url, _bgmUrl, StringComparison.Ordinal) && _bgm.Source is not null)
        {
            return;
        }

        _bgmUrl = url;
        try
        {
            _bgm.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(url));
            _bgm.Volume = _standby ? 0 : Math.Clamp(Volume, 0, 100) / 100.0;
            MfLog.Write($"app: 图文 BGM 起播 vol={_bgm.Volume:F2} standby={_standby} · {UrlStub(url)}");
            if (!_standby)
            {
                _bgm.Play();
            }
        }
        catch (Exception ex)
        {
            MfLog.Write("app: 图文 BGM 装载失败 " + ex.GetType().Name + " " + ex.Message);
            _bgmUrl = string.Empty;
        }
    }

    /// <summary>收掉 BGM（切到视频、拆台、或者这条本来就没有音乐）。</summary>
    private void StopImageMusic()
    {
        if (_bgmUrl.Length == 0 && _bgm.Source is null)
        {
            return;
        }

        string old = _bgmUrl;
        _bgmUrl = string.Empty;
        try
        {
            MfLog.Write("app: 图文 BGM 收掉 · " + UrlStub(old));
            _bgm.Pause();
            _bgm.Source = null;
        }
        catch (Exception ex)
        {
            MfLog.Write("app: 图文 BGM 停播失败 " + ex.GetType().Name + " " + ex.Message);
        }
    }

    /// <summary>图文帖：把解析出的图 URL 列表经 WIC 解码后铺到图文层（WebP 用 BitmapImage 常见空白）。
    /// URL 形如 img~live~mp4 的是实况图（live_photo_type=1），解码后把实拍叠上，播一遍停一秒静态图再循环。</summary>
    private async void ShowImagePost(string[] entries)
    {
        if (entries.Length == 0 || entries[0].Length == 0)
        {
            OnError("图文帖没有可用的图片地址");
            return;
        }

        // 这一帖自己的代次：解码是异步的，回来时舞台可能已经换成别的条了（见 _imagePostVersion）。
        int version = ++_imagePostVersion;

        // 图 URL 与实拍 URL 分开收：~live~ 右边是实拍 mp4，左边是静态图
        string[] images = new string[entries.Length];
        var lives = new string[entries.Length];
        for (int i = 0; i < entries.Length; i++)
        {
            int at = entries[i].IndexOf("~live~", StringComparison.Ordinal);
            if (at >= 0)
            {
                images[i] = entries[i][..at];
                lives[i] = entries[i][(at + "~live~".Length)..];
            }
            else
            {
                images[i] = entries[i];
                lives[i] = string.Empty;
            }
        }

        _liveUrls = lives;
        _imagePage = 0;
        _liveUrl = lives[0];

        StageError.Visibility = Visibility.Collapsed;
        SetLoading(false);
        ImageGallery.Visibility = Visibility.Visible;
        SetImageModeChrome(true);
        ApplyImageMusic();   // 图文帖的声轨：这条作品带的 BGM（Music 还没到齐也没关系，DP 到了会再叫一次）

        // 换一条图文：先把上一条的图和翻页动画清掉，别让上一帖停在第三张的状态带过来。
        _imageFrames = Array.Empty<SoftwareBitmapSource>();
        CancelImageSlide();
        ImageStill.Source = null;
        ApplyImagePager(0, 0);
        UpdateLiveVideo();

        IReadOnlyList<DecodedImage> frames = await ImagePostDecoder.DecodeAsync(images);
        if (_tornDown || version != _imagePostVersion)
        {
            // 解到一半换了条：这一帖的图不只不用铺，也不能把图文层重新露出来（那会把新视频盖住）
            MfLog.Write("app: 图文帖解码回来已过时，丢弃（" + entries.Length + " 张）");
            return;
        }

        if (frames.Count == 0)
        {
            OnError("图文帖的图全部取不下来");
            return;
        }

        // 坏图会被跳过：实况地址按"解出来的图"重新对齐，别让第 2 张的实况贴到第 1 张上。
        string[] mappedLives = frames.Select(f => lives[f.Index]).ToArray();
        if (!_liveUrls.SequenceEqual(mappedLives, StringComparer.Ordinal))
        {
            _liveUrls = mappedLives;
            StopLiveVideo();
        }

        ImageGallery.Visibility = Visibility.Visible;
        CancelImageSlide();
        _imageFrames = frames.Select(f => f.Source).ToList();
        _imagePage = 0;
        ImageStill.Source = _imageFrames[0];
        ApplyImagePager(0, frames.Count);
        UpdateLiveVideo();
        ArmImageDwell();   // 第一张：静态三秒、实况等播完
    }

    /// <summary>当前页带实拍就铺上去（播一遍 → 静态图停一秒 → 再循环）；没有就收掉。</summary>
    private void UpdateLiveVideo()
    {
        string url = _imagePage < _liveUrls.Count ? _liveUrls[_imagePage] : string.Empty;
        if (url.Length == 0)
        {
            StopLiveVideo();
            return;
        }

        // 同一页已经装好就只确保露出来，别重复拉流（滑动落页时常见）
        if (string.Equals(url, _liveUrl, StringComparison.Ordinal)
            && LiveVideo.Source is not null && _livePlayer is not null)
        {
            LiveVideo.Visibility = Visibility.Visible;
            return;
        }

        CancelLiveFade();
        _liveVersion++;
        DetachLivePlayer();
        _liveUrl = url;
        LiveVideo.Source = null;
        LiveVideo.Visibility = Visibility.Visible;
        _ = LoadLiveAsync(url);
    }

    private async Task LoadLiveAsync(string url)
    {
        try
        {
            var media = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(url));
            if (_tornDown || !string.Equals(_liveUrl, url, StringComparison.Ordinal))
            {
                media.Dispose();
                return;
            }

            LiveVideo.Source = media;
            if (LiveVideo.MediaPlayer is { } p)
            {
                // 不交给播放器循环：播到尾要先把视频收掉、露出下面那张静态图停一秒，再往后切
                // （就一张、幕外候场那种没得切的才回零重播，见 OnLiveEnded）。
                p.IsLoopingEnabled = false;

                // 实拍那段是"拍摄记录的动态"，不出声：整帖的声音只有上面那首 BGM。
                // 两路一起响就是"音乐不正常"，一场只有一条声源。
                p.Volume = 0;
                if (!ReferenceEquals(_livePlayer, p))
                {
                    if (_livePlayer is not null)
                    {
                        _livePlayer.MediaEnded -= OnLiveEnded;
                        _livePlayer.MediaFailed -= OnLiveFailed;
                        _livePlayer.MediaOpened -= OnLiveOpened;
                    }

                    _livePlayer = p;
                    p.MediaEnded += OnLiveEnded;
                    p.MediaFailed += OnLiveFailed;
                    p.MediaOpened += OnLiveOpened;
                }

                p.Play();
            }
            else
            {
                // 元素还没给播放器（模板/源还没落地）：这一次没起播，MediaOpened 那一兜也等不到
                MfLog.Write("app: 实况元素还没给出播放器，这一发没起播 · " + UrlStub(url));
            }
        }
        catch (Exception ex)
        {
            // 实拍取失败：盖回静态图，别打断看图
            MfLog.Write("app: 图文实况装载失败 " + ex.GetType().Name + " " + ex.Message);
            LiveVideo.Source = null;
            LiveVideo.Visibility = Visibility.Collapsed;

            // 这张就当静态的算（播完推进这条链断了，改由三秒表接上）。
            // 还在这一页才起表：已经翻走的话地址早就换/清了，别去动新那一页的计时。
            if (string.Equals(url, _liveUrl, StringComparison.Ordinal)
                && string.Equals(_liveUrl, LiveUrlOf(_imagePage), StringComparison.Ordinal))
            {
                ArmImageDwell(asStill: true);
            }
        }
    }

    /// <summary>实拍播到尾：尾帧保持不透明，静态图从上层渐显盖上去，停一秒，再往后切。</summary>
    private void OnLiveEnded(MediaPlayer sender, object args)
    {
        // 版次在事件这一下就取走：排到 UI 线程之前要是已经翻页，这一发尾帧就作废。
        int version = _liveVersion;
        string url = _liveUrl;
        DispatcherQueue.TryEnqueue(async () =>
        {
            bool Stale() => _tornDown || version != _liveVersion
                || !string.Equals(url, _liveUrl, StringComparison.Ordinal)
                || !ReferenceEquals(sender, _livePlayer);

            if (Stale())
            {
                return;
            }

            // 尾帧不动，把同一页的静态图从上层渐显盖上去；盖满后再让静态图完整停一秒
            CancelLiveFade();
            if (CurrentImageFrame() is { } still)
            {
                LiveStill.Source = still;
                LiveStill.Opacity = 0;
                LiveStill.Visibility = Visibility.Visible;
                await FadeLiveStillInAsync();
                if (Stale())
                {
                    return;
                }
            }

            LiveVideo.Visibility = Visibility.Collapsed;
            await Task.Delay(1000);
            if (Stale())
            {
                return;
            }

            CancelLiveFade();

            // 图文自动切换：播完这一张（含上面那一秒静态图）就该往后推。
            // 推不动（就一张、或者幕外候场那页）就照旧回零再放一遍。
            if (CanAutoAdvance() && AdvanceImageCycle())
            {
                return;
            }

            try
            {
                // 先回零再露面，别让上一轮尾帧闪一下
                sender.PlaybackSession.Position = TimeSpan.Zero;
                LiveVideo.Visibility = Visibility.Visible;
                sender.Play();
            }
            catch (Exception ex)
            {
                MfLog.Write("app: 图文实况重播失败 " + ex.GetType().Name + " " + ex.Message);
            }
        });
    }

    /// <summary>
    /// 实拍开出来了：确认它真的在放。MediaPlayerElement 的 Source 是经元素转给内部播放器的，
    /// 设完源立刻 Play() 那一发偶尔会赶在源真正落地之前——结果停在第一帧不动。
    /// 这里在 MediaOpened 再兜一次；已经过了当前页/换了条的一律不管。
    /// </summary>
    private void OnLiveOpened(MediaPlayer sender, object args) => _ui.TryEnqueue(() =>
    {
        if (_tornDown || !ReferenceEquals(sender, _livePlayer)
            || !string.Equals(_liveUrl, LiveUrlOf(_imagePage), StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            MfLog.Write($"app: 实况开出来了 · {sender.PlaybackSession.PlaybackState}");
            if (sender.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
            {
                LiveVideo.Visibility = Visibility.Visible;
                sender.Play();
            }
        }
        catch (Exception ex)
        {
            MfLog.Write("app: 实况起播失败 " + ex.GetType().Name + " " + ex.Message);
        }
    });

    /// <summary>
    /// 实拍播不出来（媒体开出来才失败的：403、断流）：收掉覆盖层露出静态图，
    /// 这一张当静态的算——不然"播完推进"那条链断了，这张会一直卡着。
    /// </summary>
    private void OnLiveFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => _ui.TryEnqueue(() =>
    {
        if (_tornDown || !ReferenceEquals(sender, _livePlayer))
        {
            return;
        }

        MfLog.Write("app: 图文实况播放失败 " + args.Error + " · " + args.ErrorMessage);
        LiveVideo.Visibility = Visibility.Collapsed;
        ArmImageDwell(asStill: true);
    });

    /// <summary>当前图文页已解码好的静态图（渐显用）；还没解好就没有介质，直接回落硬切。</summary>
    private SoftwareBitmapSource? CurrentImageFrame()
    {
        if (_imagePage < 0 || _imagePage >= _imageFrames.Count)
        {
            return null;
        }

        return _imageFrames[_imagePage];
    }

    /// <summary>尾帧 → 静态图的过渡：尾帧不动，静态图从 0 渐显到 1 盖上去。</summary>
    private async Task FadeLiveStillInAsync()
    {
        try
        {
            var fade = new Storyboard();
            var anim = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(LiveFadeMilliseconds)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(anim, LiveStill);
            Storyboard.SetTargetProperty(anim, "Opacity");
            fade.Children.Add(anim);

            _liveFade = fade;
            fade.Begin();
        }
        catch (Exception ex)
        {
            // 动画起不来就直接盖满，别停在半透明
            MfLog.Write("app: 图文实况渐显失败 " + ex.GetType().Name + " " + ex.Message);
            _liveFade = null;
            LiveStill.Opacity = 1;
            return;
        }

        await Task.Delay(LiveFadeMilliseconds);
    }

    /// <summary>停掉渐显并收起介质层（下一轮从 0 重新盖）。</summary>
    private void CancelLiveFade()
    {
        if (_liveFade is { } fade)
        {
            _liveFade = null;
            fade.Stop();
        }

        LiveStill.Opacity = 0;
        LiveStill.Visibility = Visibility.Collapsed;
        LiveStill.Source = null;
    }

    /// <summary>把实况播放器停下来并退订；还在等的那一秒重播由版本号作废。</summary>
    private void DetachLivePlayer()
    {
        if (_livePlayer is not { } p)
        {
            return;
        }

        p.MediaEnded -= OnLiveEnded;
        p.MediaFailed -= OnLiveFailed;
        p.MediaOpened -= OnLiveOpened;
        try
        {
            p.Pause();
        }
        catch (Exception ex)
        {
            MfLog.Write("app: 图文实况停播失败 " + ex.GetType().Name + " " + ex.Message);
        }

        _livePlayer = null;
    }

    /// <summary>整块收起实况视频（回到图文帖静态图）。</summary>
    private void StopLiveVideo()
    {
        _liveVersion++;
        _liveUrl = string.Empty;
        CancelLiveFade();
        DetachLivePlayer();
        LiveVideo.Source = null;
        LiveVideo.Visibility = Visibility.Collapsed;
    }

    private void Load(string url)
    {
        // 拆台期间只落属性：Suspend 后宿主换条会改 Source，但媒体等 Resume 再开，
        // 否则缓存页每 Bind 一次就要白开一路源再马上扔掉。
        if (_tornDown)
        {
            return;
        }

        if (url.Length == 0 || url == _playing)
        {
            return;
        }

        // 换条了：图文帖那条解码链和实况链一律作废（见 _imagePostVersion）。
        // 不作废的话，上一帖解到一半的解码回来会把已经换成视频的舞台重新盖上图文层——
        // 实况还在上面放，底下的视频看不见；反过来也一样。
        _imagePostVersion++;
        CancelImageSlide();
        StopLiveVideo();

        // 图文帖：Source 编码成 imagepost://url1|url2|…（见 NativeFeed.ParseCards）。
        // 不进 MF（它只吃 mp4），解出来铺到图文层，播放器停下来。
        if (url.StartsWith(ImageScheme, StringComparison.Ordinal))
        {
            _playing = url;
            _opening = false;
            SetLoading(false);
            StageError.Visibility = Visibility.Collapsed;
            ShowImagePost(url[ImageScheme.Length..].Split('|'));
            _player.Source = null;
            _player.Pause();

            // 图文帖上台：上一条视频的源物当场退役（播放器已经松手，不用等下一次换条）
            Retire(_media, _tempPath);
            _media = null;
            _stream = null;
            _tempPath = null;
            return;
        }

        if (ImageGallery is not null && ImageGallery.Visibility == Visibility.Visible)
        {
            ImageGallery.Visibility = Visibility.Collapsed;   // 实况在上面那发 StopLiveVideo 里已经收了
        }

        SetImageModeChrome(false);

        // 别的舞台开着小窗在放它那条：这边要开始播了，先把那个小窗收掉——
        // 两条声音一起响不是谁想要的。本页自己开的小窗（同一个播放器）不动，它会跟着换台一起变
        if (MiniPlayerWindow.Current is { } mini && !ReferenceEquals(mini.Player, _player))
        {
            mini.Close();
        }

        _ = LoadAsync(url);
    }

    /// <summary>
    /// 日志里的地址标识：query 里只挑 video_id 出来，别把带签名的整条地址落盘。
    /// 一条视频一个页面，每页都以 host+path 开头，没有这个 id 日志里就分不出是哪一条。
    /// </summary>
    private static string UrlStub(string url)
    {
        int cut = url.IndexOf('?');
        if (cut <= 0)
        {
            return url;
        }

        const string Key = "video_id=";
        string query = url[(cut + 1)..];
        int at = query.IndexOf(Key, StringComparison.Ordinal);
        if (at < 0)
        {
            return url[..cut];
        }

        int end = query.IndexOf('&', at);
        string id = end > at ? query[at..end] : query[at..];
        return url[..cut] + "?" + id;
    }

    /// <summary>
    /// 换台装载：取数走 HttpRangeStream（自带 UA/Referer，先 PrimeAsync 确认 CDN 接受分段请求），
    /// 交媒体用 MediaSource.CreateFromStream；Range 这条路走不通再回退整段落地（老的保底路）。
    /// </summary>
    private async Task LoadAsync(string url)
    {
        int version = ++_loadVersion;
        bool stayPaused = _pauseNextLoad;   // 这一发是不是"暂停着换的档"（认领后清掉，别漏给下一发）
        _pauseNextLoad = false;
        _playing = url;
        _opening = true;
        _switchStartedMs = Environment.TickCount64;

        StageError.Visibility = Visibility.Collapsed;
        SetLoading(true);
        ProgressSlider.Value = 0;

        // 续播点先算出来（切档那条是按**新地址**记的、回看那条是上次看到哪儿）：
        // 一路上它既要喂给 WarmAsync 预取，也要在装载落地后真跳过去。
        double resume = 0;
        double resumeTotal = 0;
        if (_progress.TryGetValue(url, out double saved) && _duration.TryGetValue(url, out double savedTotal)
            && savedTotal > 0.5 && saved > ResumeMinSeconds)
        {
            resume = saved;
            resumeTotal = savedTotal;
        }

        _resumeTarget = resume;
        _resumeMediaOpened = false;

        MediaSource media;
        string? tempPath = null;
        HttpRangeStream? stream = null;
        try
        {
            // PrimeAsync 会同步确认 CDN 接受分段请求（/play/ 那条会先 302 到 weba 节点），
            // 失败在这里就能抓到——CreateFromStream 本身不碰网络，MF 要到自己的线程上读数据才报错，那时已经来不及回退。
            stream = new HttpRangeStream(url);
            await stream.PrimeAsync();

            // 带续播点起播：先把 seek 落点附近取进内存再交给播放器，
            // 否则 MF 落到那个偏移的第一读就是同步 HTTP（100~300ms 断供），那一下就是往回切的掉帧。
            if (resumeTotal > 0)
            {
                await stream.WarmAsync((long)(resume / resumeTotal * stream.Length), CancellationToken.None);
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
                if (version == _loadVersion)
                {
                    OnError("range: " + rangeFailure.Message + " | file: " + fileFailure.Message);
                }

                return;
            }
        }

        if (version != _loadVersion || _tornDown)
        {
            // 已被后续装载取代（或页面已拆），这条作废。
            // 它还没挂到播放器上，可以当场松：源物 + 分段流的 1MB 工作缓冲一起归还，不用进待葬队列。
            DeleteTempFile(tempPath);
            try
            {
                media.Dispose();
            }
            catch (Exception ex)
            {
                MfLog.Write("app: 作废装载的源物释放抛了 " + ex.GetType().Name + " · " + ex.Message);
            }

            if (stream is not null)
            {
                try
                {
                    stream.Dispose();
                }
                catch (Exception ex)
                {
                    MfLog.Write("app: 作废装载的流释放抛了 " + ex.GetType().Name + " · " + ex.Message);
                }
            }

            return;
        }

        MediaSource? previous = _media;
        string? previousTemp = _tempPath;
        _media = media;
        _stream = stream;
        _tempPath = tempPath;
        _stayPaused = stayPaused;   // 暂停着换的档：交给 Playing 那一档收尾（位置跳完再停画面）
        MfLog.Write("app: Play " + UrlStub(url));
        _player.Source = media;

        // 期限从"源落下"起算：装载自己花掉的网络时间不该吃掉这一跳的窗口
        _resumeDeadlineMs = Environment.TickCount64 + ResumeDeadlineMs;
        ApplyResumeSeek("Source 落下");   // 跳点最早的一次机会；管线不认还有 MediaOpened 与 Playing 两次
        _player.Play();

        // 上一条现在才真正松手：按延后收尸的规矩处理（线程池上关管线、删落地文件）
        Retire(previous, previousTemp);

        // 诊断时间线：每换一条记一行内存读数，配合 VS 快照判断坡从第几条起爬
        int corpses;
        lock (_corpseGate)
        {
            corpses = _corpses.Count;
        }

        MfLog.Write($"app: 换台后内存 " + MemNote() + $" · 待葬 {corpses}");
    }

    /// <summary>
    /// 换下来的播放源进待葬队列。UI 线程只做这一下入队；到点后的 Dispose 在线程池上跑（见 <see cref="ReapLoopAsync"/>）。
    /// 不 Dispose 会叠管线、当场 Dispose 会炸 RO_E_CLOSED——延后 + 异步是这两个约束之间的落点。
    /// </summary>
    private void Retire(MediaSource? source, string? tempPath)
    {
        if (source is null && tempPath is null)
        {
            return;
        }

        lock (_corpseGate)
        {
            _corpses.Enqueue((source, tempPath, Environment.TickCount64));
            if (_reaper is null)
            {
                _reaper = Task.Run(ReapLoopAsync);
            }
        }
    }

    /// <summary>
    /// 收尸循环：睡 500ms 看一眼队头，到期就连 Dispose 带删落地文件一起做——全程线程池，不占 UI。
    /// 队列空了自己退场（在锁里置空，和 Retire 的"空就起"配成一对，不留真空窗口）。
    /// </summary>
    private async Task ReapLoopAsync()
    {
        while (true)
        {
            await Task.Delay(500).ConfigureAwait(false);

            long now = Environment.TickCount64;
            var due = new List<(MediaSource? Source, string? Temp)>();
            lock (_corpseGate)
            {
                while (_corpses.Count > 0 && now - _corpses.Peek().RetiredMs >= SourceReapDelayMs)
                {
                    (MediaSource? source, string? temp, _) = _corpses.Dequeue();
                    due.Add((source, temp));
                }
            }

            if (due.Count == 0)
            {
                lock (_corpseGate)
                {
                    if (_corpses.Count == 0)
                    {
                        _reaper = null;
                        return;
                    }
                }

                continue;
            }

            foreach ((MediaSource? source, string? temp) in due)
            {
                if (source is not null)
                {
                    try
                    {
                        // 关源会让 WinRT 适配器回收 HttpRangeStream，它租的 1MB 工作缓冲在这里才真正归还
                        source.Dispose();
                    }
                    catch (Exception ex)
                    {
                        // 多数是线程亲和问题：这一具转回 UI 兜底一次，其余照旧不往上抛
                        MfLog.Write("app: 线程池回收被拒（" + ex.GetType().Name + "），转 UI 兜底：" + ex.Message);
                        _ui.TryEnqueue(() =>
                        {
                            try
                            {
                                source.Dispose();
                            }
                            catch (Exception inner)
                            {
                                MfLog.Write("app: UI 兜底回收也失败：" + inner.Message);
                            }
                        });
                    }
                }

                DeleteTempFile(temp);
            }

            MfLog.Write($"app: 收尸 {due.Count} 具后内存 " + MemNote());
        }
    }

    // ===== 媒体事件（已转回 UI 线程） =====

    /// <summary>媒体开出来了（时长/自然尺寸就绪）。</summary>
    private void OnMediaOpened()
    {
        if (_tornDown)
        {
            return;
        }

        var session = _player.PlaybackSession;
        MfLog.Write($"app: MediaOpened 总长 {session.NaturalDuration.TotalSeconds:F1}s · 画面 {session.NaturalVideoWidth}x{session.NaturalVideoHeight}");

        // 媒体开出来 = 可以跳了：换档那条路的第二次机会（管线咽掉的那一跳在这里补上）
        _resumeMediaOpened = true;
        ApplyResumeSeek("MediaOpened");

        // 装载的账在 MediaOpened 就基本定了（_playing 早在 LoadAsync 开局就落了）：档位那几行在这里先对一次，
        // Playing 那一档不一定会再走到（换档就是这种情况），脸上不能一直空着
        SyncQualityMenu();
    }

    /// <summary>
    /// 把画面接到 <see cref="_resumeTarget"/> 上（0 = 这一发从零起播）。装载的每个节点各来一次：
    /// Source 落下、MediaOpened、第一次 Playing——状态事件是连着来的，"换档重装"不一定再走到 Playing 那一档，
    /// 只在那一处跳的话，用户看到的就是"换了档又从零开始"。跳没跳成由 250ms 那一拍认账（见 <see cref="SettleResume"/>）。
    /// </summary>
    private void ApplyResumeSeek(string why)
    {
        if (_tornDown || _resumeTarget <= 0 || Environment.TickCount64 > _resumeDeadlineMs)
        {
            return;
        }

        var session = _player.PlaybackSession;
        if (!session.CanSeek)
        {
            MfLog.Write($"app: 回跳 {_resumeTarget:F1}s（{why}）先放着 · 还跳不了");
            return;
        }

        try
        {
            session.Position = TimeSpan.FromSeconds(_resumeTarget);
            MfLog.Write($"app: 回跳 {_resumeTarget:F1}s（{why}）");
        }
        catch (Exception ex)
        {
            MfLog.Write($"app: 回跳 {_resumeTarget:F1}s（{why}）失败：{ex.GetType().Name} · {ex.Message}");
        }
    }

    /// <summary>
    /// 那一跳到底落没落地，250ms 那一拍认一次：这一发的媒体开出来之后、播放点进到跳点跟前就销账
    /// （这一刻起续播表按新位置记账）；过期还没落地也销账——不销的话续播表会被一直拦着不记，
    /// 后面的换档就接不上真位置了。落地那一下必须认这一发的 MediaOpened：换档时老流那边排队的
    /// 一跳也可能落到位，别把那一跳当成跳成了。
    /// </summary>
    private void SettleResume(double seconds)
    {
        if (_resumeTarget <= 0)
        {
            return;
        }

        if (_resumeMediaOpened && seconds >= _resumeTarget - ResumeLandedSlackSeconds)
        {
            MfLog.Write($"app: 回跳到位 {seconds:F1}s");
            _resumeTarget = 0;
        }
        else if (Environment.TickCount64 > _resumeDeadlineMs)
        {
            MfLog.Write($"app: 回跳没落地（过期，现在 {seconds:F1}s）");
            _resumeTarget = 0;
        }
    }

    /// <summary>播放状态变了：Playing 那一档里做起播收尾（续播跳点、倍速、候场即停）。</summary>
    private void OnPlaybackState()
    {
        if (_tornDown)
        {
            return;
        }

        MediaPlaybackState state = _player.PlaybackSession.PlaybackState;
        SyncDanmakuMotion();   // 暂停把在飞的弹幕按住、恢复再放开——弹幕跟视频时间走

        // 状态是修"换档/换台跟不上"时唯一的现场记录：掉帧的、卡 loading 的、跳点落不地的，都从这几行看出来
        MfLog.Write($"app: 状态 {state} · {_player.PlaybackSession.Position.TotalSeconds:F1}s · {(_opening ? "装载中" : "在放")}");

        if (state == MediaPlaybackState.Playing)
        {
            // 不管是新装载落地，还是从暂停/候场恢复：250ms 刷表这一拍都必须活着。
            // 它被 Paused 停过表，这里不拉回来就是"进度条有概率不动"——恢复播放永远会走到这一档。
            _transport.Start();

            if (!_opening)
            {
                // 暂停后续播：不该把续播点、倍速这些"起播时才做一次"的事再走一遍
                SetLoading(false);
                UpdatePlayPauseGlyph();
                RefreshTransport();
                return;
            }

            _opening = false;
            SetLoading(false);
            UpdatePlayPauseGlyph();
            ApplyRate();

            // "正在放"到这一刻才算数：下拉框的选中行（与档位表）都以它为准——
            // 首条视频那一下同步发生在 _playing 落下来之前，这里不补一次，脸上就是空的
            SyncQualityMenu();

            // 续播/换档那一跳的最后一次机会：Source 落下与 MediaOpened 已经各试过一回
            ApplyResumeSeek("Playing");

            // 暂停着换的清晰度：位置已经跳到位，把画面停住——用户按过暂停，换档之后还得是暂停
            if (_stayPaused)
            {
                _stayPaused = false;
                _player.Pause();
            }

            // 候场：媒体已经开出来、首帧解好了，停在这一帧上等上场
            if (_standby)
            {
                _player.Pause();
            }

            return;
        }

        if (state == MediaPlaybackState.Opening || state == MediaPlaybackState.Buffering)
        {
            SetLoading(true);
            return;
        }

        // Paused / None：停表；装载中撞上就收掉加载提示（别把转圈亮到下一帧）
        _transport.Stop();
        UpdatePlayPauseGlyph();
        if (_opening)
        {
            _opening = false;
            SetLoading(false);
        }
    }

    private void OnEndReached()
    {
        if (_tornDown)
        {
            return;
        }

        _opening = false;
        _transport.Stop();
        SetLoading(false);

        // 候场那条也可能播到底（短视频），它不是场上的：不推列表，也不循环（停在那儿等上场）
        if (!IsLoaded || _standby)
        {
            return;
        }

        if (_autoPlay)
        {
            Ended?.Invoke(this, EventArgs.Empty);   // 连播：交给宿主推列表，换下一条
            return;
        }

        LoopBack();
    }

    /// <summary>
    /// 没开连播时播到底：回到 0 秒接着放（单条循环）——用户点名的行为，别停在最后一帧。
    /// 弹幕跟着回卷：<see cref="SeekDanmaku"/> 把在飞的对回视频钟、游标重新摆到开头，
    /// 之后按 0 重新吐。续播表从这一拍起按新位置记——和用户自己把进度条拖回开头是同一条路。
    /// </summary>
    private void LoopBack()
    {
        MfLog.Write("app: 播完循环 " + UrlStub(_playing));
        _player.PlaybackSession.Position = TimeSpan.Zero;
        SeekDanmaku(0);
        _transport.Start();
        _player.Play();
    }

    private void OnMediaFailed(MediaPlayerFailedEventArgs e)
    {
        OnError(e.Error + " · " + e.ErrorMessage);
    }

    /// <summary>播不出来：原文上屏（装载阶段的失败才报，后面偶发的一律吞掉）。</summary>
    private void OnError(string detail)
    {
        if (_tornDown)
        {
            return;
        }

        _transport.Stop();
        if (!_opening)
        {
            return;
        }

        _opening = false;
        SetLoading(false);
        MfLog.Write("app: 播不出 " + UrlStub(_playing) + " —— " + detail);
        StageError.Text = "MediaPlayer 播不出这条：" + detail;
        StageError.Visibility = Visibility.Visible;
    }

    /// <summary>250ms 一拍：读播放会话的秒数刷滑块和时间，顺带记下续播点、回收身后的块。</summary>
    private void RefreshTransport()
    {
        if (_tornDown)
        {
            return;
        }

        var session = _player.PlaybackSession;
        double total = session.NaturalDuration.TotalSeconds;
        if (total > 0)
        {
            ProgressSlider.Maximum = total;
        }

        double seconds = session.Position.TotalSeconds;
        if (double.IsNaN(seconds))
        {
            return;
        }

        PumpDanmaku(seconds);
        SettleResume(seconds);

        // 跳点还没落地的那几拍先不记账：这会儿 seconds 是"从零起步"的那点秒数，记下去就把换档前的真进度盖掉了
        if (_playing.Length > 0 && seconds > 0 && _resumeTarget <= 0)
        {
            // 新地址入表在这里：超上限就从最早那条开始回收，不让这两张表无限攒
            RememberProgress(_playing, seconds, total);

            if (total > 0)
            {
                // 播放点往前走了：身后的块该让位了。这笔记账自带锁、不碰 UI，直接丢线程池
                string url = _playing;
                _ = Task.Run(() => MediaCache.FreeBehind(url, seconds, total));
            }
        }

        if (!_scrubbing)
        {
            ProgressSlider.Value = seconds;
        }

        UpdateTimeLabel();
    }

    /// <summary>
    /// 把"这条地址看到第几秒"记进续播表（两张表自己设了上限，超了从最早那条回收）。
    /// 250ms 那一拍与换清晰度转场共用：换档前先按**新地址**记一笔，LoadAsync 才能把 seek 落点预取好。
    /// </summary>
    private void RememberProgress(string url, double seconds, double total)
    {
        if (!_progress.ContainsKey(url))
        {
            _seenOrder.Enqueue(url);
            while (_seenOrder.Count > SeenCap)
            {
                string oldest = _seenOrder.Dequeue();
                _progress.Remove(oldest);
                _duration.Remove(oldest);
            }
        }

        _progress[url] = seconds;
        if (total > 0)
        {
            _duration[url] = total;
        }
    }

    /// <summary>
    /// 使用账本：按墙钟累计实际播放时长。倍速按实际耗时算；暂停、缓冲、幕外候场都不记。
    /// 主播放器在放视频，或图文帖的 BGM 在放，都算“在看”（图文没有可读的视频进度，BGM 就是它的在场信号）。
    /// 小窗接管画面时主窗口这边跳开，避免同一条被两边各记一遍（小窗自己记，见 MiniPlayerWindow.Refresh）。
    /// </summary>
    private void AccumulateWatchTime()
    {
        bool active = !_tornDown
            && !_standby
            && _miniWindow is null
            && (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
                || _bgm.PlaybackSession.PlaybackState == MediaPlaybackState.Playing);

        if (!active)
        {
            _watchLastMs = 0;   // 停过一段：下次起播重新取基准，别把暂停的空档记进去
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

        // UI 被长任务卡住时别把整段停顿都算成观看；正常一拍就是 1 秒上下
        if (delta > 5000)
        {
            delta = 5000;
        }

        UsageStore.AddWatch(TimeSpan.FromMilliseconds(delta));
    }

    // ===== 传输控制 =====

    private void UpdatePlayPauseGlyph()
    {
        PlayPauseIcon.Glyph = _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing ? PauseGlyph : PlayGlyph;
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => TogglePlayPause();

    // ===== 视频画面右键菜单 =====
    /// <summary>图文帖没有视频文件，保存与重载置灰；其余操作照常。</summary>
    private void OnVideoContextMenuOpening(object? sender, object e)
    {
        if (_tornDown)
        {
            VideoContextMenu.Hide();
            return;
        }

        bool video = ImageGallery.Visibility != Visibility.Visible
            && _playing.Length > 0
            && !_playing.StartsWith(ImageScheme, StringComparison.Ordinal);
        CtxSaveVideo.IsEnabled = video;
        CtxReload.IsEnabled = video;
    }

    /// <summary>视频信息：标题/作者/ID/画面/时长/清晰度/发布时间/计数/链接，主按钮复制链接、次按钮复制 ID。</summary>
    private async void OnContextVideoInfoClick(object sender, RoutedEventArgs e)
    {
        if (_tornDown)
        {
            return;
        }

        bool image = ImageGallery.Visibility == Visibility.Visible;
        double total = _player.PlaybackSession.NaturalDuration.TotalSeconds;
        string quality = Quality.CurrentLabel;
        if (quality.Length == 0 || quality == "清晰度")
        {
            quality = "默认";
        }

        string id = ItemId ?? string.Empty;
        InfoTitleText.Text = OrDash(Desc);
        InfoAuthorText.Text = OrDash(Author);
        InfoIdText.Text = OrDash(id);
        InfoFrameText.Text = image
            ? "图文帖 · " + ImageCount() + " 张"
            : VideoWidth > 0 && VideoHeight > 0 ? VideoWidth + " × " + VideoHeight : "—";
        InfoDurationText.Text = image || total <= 0 ? "—" : FormatTime(total);
        InfoQualityText.Text = image ? "—" : quality;
        InfoTimeText.Text = OrDash(TimeText);
        InfoStatsText.Text = "赞 " + CountText.Format(DiggRaw)
            + " · 评 " + CountText.Format(CommentRaw)
            + " · 藏 " + CountText.Format(CollectRaw)
            + " · 转 " + CountText.Format(ShareRaw);
        InfoLinkText.Text = id.Length > 0 ? "https://www.douyin.com/video/" + id : "—";

        if (VideoInfoDialog.XamlRoot is null)
        {
            VideoInfoDialog.XamlRoot = XamlRoot;
        }

        try
        {
            ContentDialogResult result = await VideoInfoDialog.ShowAsync();
            if (_tornDown)
            {
                return;
            }

            if (result == ContentDialogResult.Primary)
            {
                CopyShareLink();
            }
            else if (result == ContentDialogResult.Secondary)
            {
                CopyVideoId();
            }
        }
        catch (Exception ex)
        {
            MfLog.Write("app: 视频信息弹窗打不开 " + ex.GetType().Name + " · " + ex.Message);
        }
    }

    private static string OrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;

    private void OnContextOpenBrowserClick(object sender, RoutedEventArgs e)
    {
        if (!TryShareLink(out string link))
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MfLog.Write("app: 打不开浏览器 " + ex.GetType().Name + " · " + ex.Message);
            Toast("打不开浏览器：" + ex.Message);
        }
    }

    /// <summary>
    /// 保存当前正在放的那条流（当前清晰度）。先让用户选文件夹，再按正文/作者 + 视频 id 起名；
    /// 下载走 HttpRangeStream 同一套 UA/Referer，Range 不吃时的远端地址也能整段拉。
    /// </summary>
    private async void OnContextSaveVideoClick(object sender, RoutedEventArgs e)
    {
        if (_tornDown)
        {
            return;
        }

        string url = _playing;
        if (url.Length == 0 || url.StartsWith(ImageScheme, StringComparison.Ordinal))
        {
            Toast("这条没有可保存的视频文件");
            return;
        }

        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.VideosLibrary,
            };
            picker.FileTypeFilter.Add("*");
            IntPtr hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(HostWindow().Id);
            InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null || _tornDown)
            {
                return;
            }

            string path = UniqueSavePath(folder.Path);
            long lastReport = 0;
            var progress = new Progress<long>(bytes =>
            {
                long now = Environment.TickCount64;
                if (_tornDown || now - lastReport < 500)
                {
                    return;
                }

                lastReport = now;
                Toast("正在保存视频… " + (bytes / 1048576.0).ToString("0.0") + " MB");
            });

            Toast("正在保存视频…");
            await HttpRangeStream.DownloadToFileAsync(url, path, progress, CancellationToken.None);
            if (!_tornDown)
            {
                Toast("视频已保存：" + path);
            }
        }
        catch (Exception ex)
        {
            MfLog.Write("app: 保存视频失败 " + ex.GetType().Name + " · " + ex.Message);
            if (!_tornDown)
            {
                Toast("保存失败：" + ex.Message);
            }
        }
    }

    /// <summary>保存文件名：正文优先、没有就用作者，尾上挂视频 id 防重名；非法字符换成下划线。</summary>
    private string UniqueSavePath(string folder)
    {
        string title = (Desc ?? string.Empty).Trim().ReplaceLineEndings(" ");
        if (title.Length == 0)
        {
            title = (Author ?? string.Empty).Trim();
        }

        if (title.Length == 0)
        {
            title = "抖音视频";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(title.Length);
        foreach (char c in title)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }

        string name = sb.ToString().Trim().TrimEnd('.');
        if (name.Length > 60)
        {
            name = name[..60].TrimEnd();
        }

        string id = ItemId ?? string.Empty;
        if (id.Length > 0)
        {
            name += " - " + id;
        }

        string path = Path.Combine(folder, name + ".mp4");
        for (int i = 2; File.Exists(path) && i < 1000; i++)
        {
            path = Path.Combine(folder, name + " (" + i + ").mp4");
        }

        return path;
    }

    private void CopyVideoId()
    {
        string id = ItemId ?? string.Empty;
        if (id.Length == 0)
        {
            Toast("这条还没拿到 id");
            return;
        }

        Windows.ApplicationModel.DataTransfer.DataPackage pkg = new();
        pkg.SetText(id);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        Toast("视频 ID 已复制：" + id);
    }

    private void OnContextSystemShareClick(object sender, RoutedEventArgs e) => _ = OpenSystemShareAsync();

    /// <summary>重新装载当前地址：先记住位置与播/停状态，再让 Load 走一遍完整开流（缓存命中会很快）。</summary>
    private void OnContextReloadClick(object sender, RoutedEventArgs e)
    {
        if (_tornDown)
        {
            return;
        }

        string url = _playing;
        if (url.Length == 0 || url.StartsWith(ImageScheme, StringComparison.Ordinal))
        {
            Toast("这条没有可重载的视频");
            return;
        }

        MediaPlaybackSession session = _player.PlaybackSession;
        double position = session.Position.TotalSeconds;
        double total = session.NaturalDuration.TotalSeconds;
        if (position > 0 && total > 0.5)
        {
            RememberProgress(url, position, total);
        }

        _pauseNextLoad = session.PlaybackState == MediaPlaybackState.Paused;
        _playing = string.Empty;
        Toast("正在重新加载视频…");
        Load(url);
    }

    private void OnContextCopyLinkClick(object sender, RoutedEventArgs e) => CopyShareLink();

    private void TogglePlayPause()
    {
        if (_tornDown || _player.Source is null)
        {
            return;
        }

        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            _player.Pause();
        }
        else
        {
            _player.Play();
        }

        UpdatePlayPauseGlyph();
    }

    private void UpdateTimeLabel()
    {
        if (_tornDown)
        {
            return;
        }

        double total = _player.PlaybackSession.NaturalDuration.TotalSeconds;
        double seconds = _player.PlaybackSession.Position.TotalSeconds;
        TimeLabel.Text = total > 0 ? FormatTime(seconds) + " / " + FormatTime(total) : FormatTime(seconds);
    }

    /// <summary>秒 → mm:ss，满一小时给 h:mm:ss。</summary>
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

    private void SetLoading(bool on)
    {
        StageLoading.IsActive = on;
        StageLoading.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>把视频宿主裁到自身边界：拖拽期间视频元素冻结在旧尺寸，裁一刀防止它探进信息区。</summary>
    private void UpdateVideoHostClip()
    {
        double w = VideoHost.ActualWidth;
        double h = VideoHost.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            VideoHost.Clip = null;
            return;
        }

        if (VideoHost.Clip is RectangleGeometry clip)
        {
            clip.Rect = new Rect(0, 0, w, h);
        }
        else
        {
            VideoHost.Clip = new RectangleGeometry { Rect = new Rect(0, 0, w, h) };
        }
    }

    /// <summary>
    /// 把视频元素缩到画面实际比例并居中，四周让给模糊背景板。
    /// </summary>
    private void LayoutVideo()
    {
        double vw = VideoWidth;
        double vh = VideoHeight;
        double availW = VideoHost.ActualWidth;
        double availH = VideoHost.ActualHeight;

        if (vw > 0 && vh > 0 && availW > 0 && availH > 0)
        {
            double scale = Math.Min(availW / vw, availH / vh);
            double w = Math.Round(vw * scale);
            double h = Math.Round(vh * scale);
            if (Math.Abs(Video.Width - w) < 0.5 && Math.Abs(Video.Height - h) < 0.5)
            {
                return;
            }

            Video.HorizontalAlignment = HorizontalAlignment.Center;
            Video.VerticalAlignment = VerticalAlignment.Center;
            Video.Width = w;
            Video.Height = h;
        }
        else if (!double.IsNaN(Video.Width))
        {
            // 尺寸还不知道（或舞台还没布局）：先铺满，等数据/布局到了再收
            Video.HorizontalAlignment = HorizontalAlignment.Stretch;
            Video.VerticalAlignment = VerticalAlignment.Stretch;
            Video.Width = double.NaN;
            Video.Height = double.NaN;
        }
    }

    // ===== 进度条拖拽 =====

    private void OnProgressPressed(object sender, PointerRoutedEventArgs e) => _scrubbing = true;

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

    /// <summary>MF 的 seek 落点直接给播放会话；读不到源（已拆台/未装载）就什么都不做。</summary>
    private void SeekToSlider()
    {
        _scrubbing = false;
        if (_tornDown || _player.Source is null)
        {
            return;
        }

        _player.PlaybackSession.Position = TimeSpan.FromSeconds(ProgressSlider.Value);
        UpdateTimeLabel();

        // 跳转后把在飞的弹幕按新位置摆好（位置 = 视频时间 − 这条的时间）
        SeekDanmaku(ProgressSlider.Value);
    }

    // ===== 底部面板显隐 =====

    private void OnGesturePressed(object sender, PointerRoutedEventArgs e)
    {
        // 位置必须取窗口坐标：GestureLayer 本身随视频页一起被拖动，
        // 相对它取点等于在移动坐标系里量位移，会和页面位移互相抵消（拖起来一顿一顿）。
        var point = e.GetCurrentPoint(null);
        if (!point.Properties.IsLeftButtonPressed || _gesturePressed)
        {
            return;
        }

        // 正在等第二次点击时又按下了：这是双击序列，先掐掉单击计时
        if (_clickCount == 1)
        {
            _clickTimer.Stop();
        }

        _gestureOrigin = point.Position;
        _gesturePressed = true;
        _gestureDragging = false;
        _gestureMoved = false;
        _gestureOffset = 0;
        _gestureReported = 0;
        _gestureVelocity = 0;
        _gestureLastY = point.Position.Y;
        _gestureLastTimestamp = point.Timestamp;
        _gestureLastMoveMs = Environment.TickCount64;

        // 按住就抓指针：拖出视频区、拖到面板上甚至拖出窗口，Move/Release 都还回到这一层，
        // 画面不会在半路丢跟手（这是"拖动灵活"的前提，不能靠指针恰好停在视频上）。
        GestureLayer.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnGestureMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_gesturePressed)
        {
            return;
        }

        // 窗口坐标（同上：不能取随页面移动的 GestureLayer）
        var point = e.GetCurrentPoint(null);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        double dx = point.Position.X - _gestureOrigin.X;
        double dy = point.Position.Y - _gestureOrigin.Y;

        if (!_gestureDragging)
        {
            if (Math.Abs(dx) < DragThresholdPx && Math.Abs(dy) < DragThresholdPx)
            {
                return;
            }

            // 动过就不算单击了：横向划走后松手不能误触暂停
            _gestureMoved = true;

            // 竖向意图：横向分量更大时宁可什么都不做，也别把视频斜着拽出去
            if (Math.Abs(dy) < Math.Abs(dx) * DragHorizontalRatio)
            {
                return;
            }

            _gestureDragging = true;
            _gestureOffset = 0;
            _gestureReported = 0;
            _gestureVelocity = 0;

            // 速度从真正越过死区这一拍起算：按下后先停住再拖，不能被那段静止时间稀释
            _gestureLastY = point.Position.Y;
            _gestureLastTimestamp = point.Timestamp;
            _gestureLastMoveMs = Environment.TickCount64;
            DragStarted?.Invoke(this, EventArgs.Empty);
        }

        // 死区以内不动；越过死区后 1:1 跟手（先扣掉死区那几像素，起手不跳）
        _gestureOffset = dy - Math.Sign(dy) * DragThresholdPx;
        RecordGestureMove(point);
        DragDelta?.Invoke(this, new DragDeltaEventArgs(_gestureOffset, _gestureOffset - _gestureReported));
        _gestureReported = _gestureOffset;
        e.Handled = true;
    }

    private void OnGestureReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_gesturePressed)
        {
            return;
        }

        // 窗口坐标（同上）
        var point = e.GetCurrentPoint(null);
        double dy = point.Position.Y - _gestureOrigin.Y;
        double offset = _gestureDragging ? dy - Math.Sign(dy) * DragThresholdPx : 0;
        bool wasDragging = _gestureDragging;
        bool moved = _gestureMoved;
        double velocity = wasDragging ? CurrentGestureVelocity() : 0;

        // 先落状态再回调：宿主的吸附动画一开始，这一发旧手势就结束了
        _gesturePressed = false;
        _gestureDragging = false;
        _gestureMoved = false;
        _gestureOffset = 0;
        GestureLayer.ReleasePointerCapture(e.Pointer);
        e.Handled = true;

        if (wasDragging)
        {
            ResetClick();
            DragCompleted?.Invoke(this, new DragCompletedEventArgs(offset, velocity, false));
            return;
        }

        if (moved)
        {
            ResetClick();
            return;
        }

        _clickCount++;
        if (_clickCount == 1)
        {
            _clickTimer.Start();
        }
        else
        {
            _clickTimer.Stop();
            _clickCount = 0;
            OnDoubleClick();
        }
    }

    private void OnGestureCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_gesturePressed && !_gestureDragging)
        {
            return;
        }

        bool wasDragging = _gestureDragging;
        double offset = _gestureOffset;
        double velocity = wasDragging ? CurrentGestureVelocity() : 0;

        _gesturePressed = false;
        _gestureDragging = false;
        _gestureMoved = false;
        _gestureOffset = 0;
        ResetClick();

        if (wasDragging)
        {
            DragCompleted?.Invoke(this, new DragCompletedEventArgs(offset, velocity, true));
        }
    }

    private void OnGestureWheel(object sender, PointerRoutedEventArgs e)
    {
        // 图文帖被手势层盖住，滚轮在这里转交宿主当"换台"；普通视频不拦，继续冒泡给页面。
        if (ImageGallery.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            OnImageWheel(e.GetCurrentPoint(GestureLayer).Properties.MouseWheelDelta);
        }
    }

    /// <summary>速度用相邻两次 Move 的时间/位移估，再平滑一下；指针停住就不再算速度。</summary>
    private void RecordGestureMove(Microsoft.UI.Input.PointerPoint point)
    {
        long now = Environment.TickCount64;
        if (point.Timestamp > _gestureLastTimestamp)
        {
            double dt = (point.Timestamp - _gestureLastTimestamp) / 1_000_000.0;
            if (dt > 0)
            {
                double v = (point.Position.Y - _gestureLastY) / dt;
                _gestureVelocity = _gestureVelocity * 0.4 + v * 0.6;
            }
        }

        _gestureLastY = point.Position.Y;
        _gestureLastTimestamp = point.Timestamp;
        _gestureLastMoveMs = now;
    }

    private double CurrentGestureVelocity() =>
        Environment.TickCount64 - _gestureLastMoveMs <= FlickWindowMs ? _gestureVelocity : 0;

    private void ResetClick()
    {
        _clickCount = 0;
        _clickTimer.Stop();
    }

    private void FlushClick()
    {
        if (_clickCount == 1)
        {
            OnSingleClick();
        }

        _clickCount = 0;
    }

    private void OnSingleClick() => TogglePlayPause();

    private void OnDoubleClick() => _ = ToggleLikeAsync();

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // Flyout 打开时不隐藏面板（鼠标可能移到 Flyout 上）
        if (VolumeFlyout.IsOpen || DanmakuSettingsFlyout.IsOpen)
        {
            return;
        }

        double y = e.GetCurrentPoint(Root).Position.Y;

        // 只认贴底那一条窄带：面板收起时它落在底距里，不会碰到上方的标题；
        // 面板上来后手往面板里挪，由下面那条"停在面板上"的判定接住。
        if (y >= Root.ActualHeight - PanelShowBand)
        {
            ShowPanel();
            return;
        }

        if (_panelVisible && IsPointerOverPanel(y))
        {
            return;
        }

        TryHidePanel();
    }

    /// <summary>指针是不是压在面板上（含一格余量）：面板底边距 InfoRestInset，顶缘还要加它自己实测的那截高。</summary>
    private bool IsPointerOverPanel(double y)
    {
        double h = ControlPanel.ActualHeight;
        return h > 0 && y >= Root.ActualHeight - InfoRestInset - h - PanelHoverSlack;
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
        if (_scrubbing || !_panelVisible)
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

    /// <summary>
    /// 标题区该抬多高：panelTop 是面板顶缘距舞台底的距离（16 底边距 + 面板实测高），
    /// 抬到它上面再留 12 间隙；信息区收起时本来就占着 InfoRestInset 那段底距，所以要扣掉。
    /// </summary>
    private static double InfoLift(double panelTop) => Math.Max(0, panelTop + 12 - InfoRestInset);

    // ===== 面板功能按钮 =====

    /// <summary>
    /// 弹幕开关：开=层亮着，来的字照飞；关=整层收掉并把在飞的一起清空
    /// （不清的话再开时那些字会从半路接着飞，看着像没关过）。
    /// 给一句话提示是因为层里可能暂时没字——开关有没有生效得让人看得见。
    /// 真人点的那一路顺手写进共享设置（跨启动记住），跟我们自己拨勾的那一路分开。
    /// </summary>
    private void OnDanmakuToggle(object sender, RoutedEventArgs e)
    {
        bool on = DanmakuToggle.IsChecked == true;
        ApplyDanmakuOn(on);

        if (_danmakuSyncing)
        {
            return;   // 是我们自己拨的勾（装载或另一页改的）：别写回设置、也别弹提示
        }

        AppSettings().DanmakuOn = on;
        Toast(on ? "弹幕已开" : "弹幕已关");
    }

    /// <summary>表情格子里一行摆几个。</summary>
    private const int EmojiPerRow = 8;

    /// <summary>插进正文的表情图边长（DIP）与它高出基线多少（见 InsertImage 的 ascent）。</summary>
    private const int EmojiSize = 18;
    private const int EmojiAscent = 14;

    /// <summary>
    /// 表情"标签字符"的基码位：图片后面紧跟一个 1px 全透明的私用区字符，值 = 0xE000 + 表情序号。
    /// 图片本身不记来源（RichEditBox 也没给"读回图片"的口子），取发送文本时靠这个标签把码还回来。
    /// </summary>
    private const char EmojiTagBase = '\uE000';

    /// <summary>弹幕表情格子建过没（一次运行建一次：214 个 Image 不必每次开面板重来）。</summary>
    private bool _emojiBuilt;

    /// <summary>评论输入框的表情格子建过没（与弹幕那个各建一份）。</summary>
    private bool _commentEmojiBuilt;

    /// <summary>发送弹幕在飞（手快点两下别发两条）。</summary>
    private bool _danmakuSending;

    /// <summary>取表情表那一发（在飞的共用同一个任务，别各点各的）。</summary>
    private Task<IReadOnlyList<EmojiItem>>? _emojiTask;

    /// <summary>
    /// 表情面板打开：第一次去要官方表情表（371 条里 hide=0 的 214 条），摆成每行 8 个的格子。
    /// 图直接 BitmapImage 取（实测那些签名链不用带任何头，200 + webp，CDN 缓存一年）。
    /// </summary>
    private async void OnEmojiFlyoutOpened(object? sender, object e)
    {
        if (_emojiBuilt)
        {
            return;
        }

        IReadOnlyList<EmojiItem> list = await EnsureEmojiAsync();
        if (_tornDown || list.Count == 0)
        {
            MfLog.Write("app: 表情面板开不起来（表情表是空的）");
            return;   // 没拿到：下次开面板再试（_emojiBuilt 仍是 false）
        }

        _emojiBuilt = true;
        EmojiHost.Children.Add(BuildEmojiRows(list, DanmakuInput));
        MfLog.Write($"app: 表情面板 {list.Count} 个");
    }

    /// <summary>评论输入框的表情面板：与弹幕同一张表，点一个插到评论光标处。</summary>
    private async void OnCommentEmojiFlyoutOpened(object? sender, object e)
    {
        if (_commentEmojiBuilt)
        {
            return;
        }

        IReadOnlyList<EmojiItem> list = await EnsureEmojiAsync();
        if (_tornDown || list.Count == 0)
        {
            MfLog.Write("app: 评论表情面板开不起来（表情表是空的）");
            return;
        }

        _commentEmojiBuilt = true;
        CommentEmojiHost.Children.Add(BuildEmojiRows(list, CommentInput));
        MfLog.Write($"app: 评论表情面板 {list.Count} 个");
    }

    /// <summary>
    /// 表情表：一次运行取一次（在飞的共用同一个任务）。表放在 <see cref="EmojiCatalog"/> 里，
    /// 两个舞台实例与评论正文共用；别的实例已经取到就直接用，不再发第二发。
    /// </summary>
    private Task<IReadOnlyList<EmojiItem>> EnsureEmojiAsync()
    {
        if (EmojiCatalog.Count > 0)
        {
            return Task.FromResult(EmojiCatalog.List);
        }

        return _emojiTask ??= LoadEmojiAsync();
    }

    private async Task<IReadOnlyList<EmojiItem>> LoadEmojiAsync()
    {
        try
        {
            // 表情表跟评论同一发签名链；拉取也放线程池，回到 UI 再装表（EmojiCatalog 只许 UI 线程写）。
            IReadOnlyList<EmojiItem> list = await Task.Run(
                () => Douyin.Feed.EmojiListAsync(CancellationToken.None));
            EmojiCatalog.Set(list);   // 评论正文按这张表把 [码] 换成图
            return EmojiCatalog.List;
        }
        catch (Exception ex)
        {
            _emojiTask = null;   // 放开：下次再试
            MfLog.Write("app: 表情表取不到：" + ex.GetType().Name + " · " + ex.Message);
            return EmojiCatalog.List;
        }
    }

    /// <summary>把表情摆成每行 8 个的格子：每个是一枚无边框小按钮，点一下把表情插进目标输入框。</summary>
    private Panel BuildEmojiRows(IReadOnlyList<EmojiItem> list, RichEditBox target)
    {
        var rows = new StackPanel { Spacing = 2 };
        StackPanel? row = null;
        for (int i = 0; i < list.Count; i++)
        {
            if (i % EmojiPerRow == 0)
            {
                row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                rows.Children.Add(row);
            }

            EmojiItem emoji = list[i];
            var button = new Button
            {
                Width = 34,
                Height = 34,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = new Image
                {
                    Width = 24,
                    Height = 24,
                    Source = new BitmapImage(new Uri(emoji.ImageUrl)),
                },
            };
            ToolTipService.SetToolTip(button, emoji.Code);
            button.Click += (_, _) => _ = InsertEmojiAsync(emoji, target);
            row!.Children.Add(button);
        }

        return rows;
    }

    /// <summary>
    /// 往输入框里插一个官方表情（**真图片**，不是码）：图用 RichEditBox 的 InsertImage 插到光标处，
    /// 紧跟一个 1px 透明的标签字符带走"这是第几个表情"。图得先转成 PNG——抖音图床只给 webp，
    /// 而 InsertImage 只认 PNG/JPEG（见 EmojiImage）。
    /// </summary>
    private async Task InsertEmojiAsync(EmojiItem emoji, RichEditBox target)
    {
        int index = EmojiCatalog.IndexOf(emoji);
        if (index < 0 || index > char.MaxValue - EmojiTagBase)
        {
            return;
        }

        byte[]? png = await EmojiImage.LoadPngAsync(emoji.ImageUrl);
        if (_tornDown)
        {
            return;
        }

        if (png is null)
        {
            Toast("这个表情的图取不到");
            return;
        }

        try
        {
            ITextSelection selection = target.Document.Selection;
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(png.AsBuffer());
            stream.Seek(0);
            selection.InsertImage(EmojiSize, EmojiSize, EmojiAscent, VerticalCharacterAlignment.Baseline, emoji.Code, stream);

            // 标签字符：1px、全透明，用户看不见；取发送文本时它把码还回来
            selection.SetText(TextSetOptions.None, ((char)(EmojiTagBase + index)).ToString());
            selection.CharacterFormat.Size = 1;
            selection.CharacterFormat.ForegroundColor = Microsoft.UI.Colors.Transparent;
            selection.Collapse(false);

            // 光标停在标签后面：把插入点的字号/颜色恢复成文档默认，别让后面打的字也变成 1px 透明
            ITextCharacterFormat normal = target.Document.GetDefaultCharacterFormat();
            selection.CharacterFormat.Size = normal.Size;
            selection.CharacterFormat.ForegroundColor = normal.ForegroundColor;

            target.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            MfLog.Write("app: 插表情失败：" + ex.GetType().Name + " · " + ex.Message);
            Toast("这个表情插不进去");
        }
    }

    /// <summary>
    /// 取 RichEditBox 的正文（发送要用）：标签字符换回表情码，图片占位符与段落符丢掉。
    /// 弹幕和评论两条输入都走这里——它们的图一个字符都不占（只有一个对象符 + 一个标签）。
    /// </summary>
    private static string EmojiBoxText(RichEditBox box)
    {
        box.Document.GetText(TextGetOptions.None, out string raw);
        var text = new StringBuilder();
        foreach (char c in raw)
        {
            int index = c - EmojiTagBase;
            string code = EmojiCatalog.CodeAt(index);
            if (code.Length > 0)
            {
                text.Append(code);
                continue;
            }

            if (c is '\r' or '\n' or '\uFFFC')
            {
                continue;   // 段落符 / 图片占位符：跳过（码由标签带上）
            }

            text.Append(c);
        }

        return text.ToString();
    }

    private string DanmakuText() => EmojiBoxText(DanmakuInput);

    private string CommentBoxText() => EmojiBoxText(CommentInput);

    /// <summary>
    /// 发送弹幕：正文走 <see cref="DanmakuText"/>（表情图换回码），位置取当前播放时间（毫秒，接口按这个定位），
    /// 接口 <c>danmaku/post_v2/</c>（见 <see cref="NativeFeed.DanmakuPostAsync"/>）。
    /// 成了就清空输入框，并把自己这条推上屏（自己的弹幕服务端要等下次翻页窗口才带回来，先自己贴一条）；
    /// 没成弹服务端原话——**输入框里的字留着**，别让人白打一遍。
    /// </summary>
    private async void OnDanmakuSend(object sender, RoutedEventArgs e)
    {
        string text = DanmakuText().Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (NoAccount)
        {
            Toast("登录之后才能发弹幕");
            return;
        }

        if (_danmakuSending)
        {
            return;   // 手快点两下别发两条
        }

        double position = _player.PlaybackSession.Position.TotalSeconds;
        _danmakuSending = true;
        try
        {
            (bool ok, string id, string note) = await Douyin.Feed.DanmakuPostAsync(
                ItemId ?? string.Empty, text, (long)(position * 1000), CancellationToken.None);
            if (_tornDown)
            {
                return;
            }

            if (!ok)
            {
                MfLog.Write("app: 发弹幕没成 " + note);
                Toast(note);
                return;
            }

            DanmakuInput.Document.SetText(TextSetOptions.None, string.Empty);
            PushMineDanmaku(new DanmakuItem(position, text, Douyin.Feed.MyUid, id, 0, false));
            MfLog.Write($"app: 弹幕发出去了 id={id} · uid已记={Douyin.Feed.MyUid.Length > 0}");
            Toast("弹幕发出去了");
        }
        catch (Exception ex)
        {
            MfLog.Write("app: 发弹幕报错 " + ex.GetType().Name + " · " + ex.Message);
            Toast("发弹幕报错：" + ex.Message);
        }
        finally
        {
            _danmakuSending = false;
        }
    }

    // 恢复默认：不透明 80%、显示区域两行、字体适中、速度适中
    private static string RowsStepText(int value) => value switch
    {
        0 => "单行",
        1 => "两行",
        _ => "三行",
    };

    private static string FontStepText(int value) => value switch
    {
        0 => "较小",
        1 => "适中",
        _ => "较大",
    };

    private static string SpeedStepText(int value) => value switch
    {
        0 => "较慢",
        1 => "适中",
        _ => "较快",
    };

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
        _danmakuOpacity = e.NewValue / 100.0;
        ApplyDanmakuSettings();
        PersistDanmakuSettings();
    }

    private void OnDanmakuAreaChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (DanmakuAreaText is null)
        {
            return;
        }

        DanmakuAreaText.Text = RowsStepText((int)e.NewValue);
        _danmakuRows = (int)e.NewValue;
        ApplyDanmakuSettings();
        PersistDanmakuSettings();
    }

    private void OnDanmakuFontChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (DanmakuFontText is null)
        {
            return;
        }

        DanmakuFontText.Text = FontStepText((int)e.NewValue);
        _danmakuFont = (int)e.NewValue;
        ApplyDanmakuSettings();
        PersistDanmakuSettings();
    }

    private void OnDanmakuSpeedChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (DanmakuSpeedText is null)
        {
            return;
        }

        DanmakuSpeedText.Text = SpeedStepText((int)e.NewValue);
        _danmakuSpeed = (int)e.NewValue;
        ApplyDanmakuSettings();

        // 速度是唯一"新字才生效就不算数"的一项：场上这批当场换过去（见 ApplyDanmakuSpeed），
        // 否则改完得等它们在旧速度上飞完，看上去就是这个滑块不管用
        ApplyDanmakuSpeed();
        PersistDanmakuSettings();
    }

    /// <summary>
    /// 把四项设置从共享设置摆到滑块上（装载时走一次）。摆的过程会触发各自的 ValueChanged，
    /// 那条路会把字段、标签、层都对齐好——所以这里不用再手动 ApplyDanmakuSettings。
    /// </summary>
    private void LoadDanmakuSettings()
    {
        ViewModels.AppSettingsViewModel s = AppSettings();
        try
        {
            DanmakuOpacitySlider.Value = Math.Clamp(s.DanmakuOpacity, 0, 100);
            DanmakuAreaSlider.Value = Math.Clamp(s.DanmakuRows, 0, 2);
            DanmakuFontSlider.Value = Math.Clamp(s.DanmakuFont, 0, 2);
            DanmakuSpeedSlider.Value = Math.Clamp(s.DanmakuSpeed, 0, 2);
        }
        finally
        {
            _danmakuSettingsLoading = false;
        }
    }

    /// <summary>
    /// 四项设置写回共享设置（防抖落盘由 SettingsViewModel 兜）。
    /// 装载那一趟不写：那一路的值本来就是从设置读进来的，写回去等于拿 XAML 初值冲掉人家记住的
    /// （音量那一轮就是这么冲的——见 OnVolumeSliderChanged 上的注释）。
    /// </summary>
    private void PersistDanmakuSettings()
    {
        if (_danmakuSettingsLoading)
        {
            return;
        }

        ViewModels.AppSettingsViewModel s = AppSettings();
        s.DanmakuOpacity = (int)Math.Round(_danmakuOpacity * 100);
        s.DanmakuRows = _danmakuRows;
        s.DanmakuFont = _danmakuFont;
        s.DanmakuSpeed = _danmakuSpeed;
    }

    /// <summary>
    /// 弹幕列表：把取到的摊开看（行样全在 XAML 那条 DanmakuListFlyout 里，数据铺进 DanmakuListRows）。
    /// 位置钉在"弹幕设置"那颗按钮正上方（同一个锚点）；设置那个浮出层先收起来再开这个（两个 flyout 会互踩），
    /// 并且挪到下一拍再 Show——Hide 与 Show 挤在同一拍里，新的那个会被 light-dismiss 连带收掉。
    /// </summary>
    private void OnDanmakuList(object sender, RoutedEventArgs e)
    {
        DanmakuListRows.Clear();
        foreach (DanmakuItem item in _danmakuItems)
        {
            DanmakuListRows.Add(new ViewModels.DanmakuRowViewModel(
                item,
                FormatTime(item.Seconds),
                item.Liked || _likedDanmakuIds.Contains(DanmakuKey(item))));
        }

        DanmakuSettingsFlyout.Hide();
        _ui.TryEnqueue(() => FlyoutBase.ShowAttachedFlyout(DanmakuSettingsButton));
    }

    /// <summary>空列表那行字：条数一变函数绑定自己重来（见 DanmakuListFlyout 里那两处 x:Bind）。</summary>
    private string DanmakuListHint(int count) =>
        _danmakuFor.Length == 0 ? "这条还没取弹幕" : "这条还没弹幕（或者还在取的路上）";

    /// <summary>同上：空不空的换算（参数是条数——它的作用就是给绑定一条能跟着变的路径）。</summary>
    private Visibility DanmakuListHintVisible(int count) =>
        count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 列表里那颗心的点赞：跟场上按钮同一个核（写接口 danmaku/digg + <see cref="_likedDanmakuIds"/> 一本账）。
    /// 界面全交给绑定：行 VM 的 Liked 一变，字形（EB51↔EB52）与颜色自己跟着转（转换器在 Converters/ 下）。
    /// 服务端说成了才改，不假装赞上了。
    /// </summary>
    private async void OnDanmakuRowLike(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ViewModels.DanmakuRowViewModel row)
        {
            return;
        }

        if (NoAccount)
        {
            Toast("登录之后才能操作弹幕");
            return;
        }

        string danmakuId = row.Item.DanmakuId;
        string awemeId = ItemId ?? string.Empty;
        if (danmakuId.Length == 0 || awemeId.Length == 0)
        {
            Toast("拿不到这条的身份（弹幕 id / 视频 id），赞不了");
            return;
        }

        string key = DanmakuKey(row.Item);
        bool wasLiked = row.Liked;
        button.IsEnabled = false;
        try
        {
            (bool ok, string note) = await Douyin.Feed.DanmakuDiggAsync(awemeId, danmakuId, !wasLiked, CancellationToken.None);
            if (_tornDown)
            {
                return;
            }

            if (!ok)
            {
                Toast(note);
                return;
            }

            if (wasLiked)
            {
                _likedDanmakuIds.Remove(key);
            }
            else
            {
                _likedDanmakuIds.Add(key);
            }

            row.Liked = !wasLiked;
            Toast(note);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    // ===== 弹幕层 =====

    /// <summary>弹幕进层的门：PumpDanmaku 按播放进度往这儿送（手工插一条也走它：uid/id/赞数都空）。</summary>
    public void PushDanmaku(string text) =>
        PushDanmaku(new DanmakuItem(_player.PlaybackSession.Position.TotalSeconds, text, string.Empty, string.Empty, 0, false));

    /// <summary>
    /// 自己刚发出去的那条进层：先塞进**时间轴**（拖回到这个点再播会照常出现），再立刻上屏一次——
    /// 上屏碰不上空道就挂在等待队列里由每拍来捞（自己发的必须看得见，不按"没道就丢"办）。
    /// </summary>
    private void PushMineDanmaku(DanmakuItem item)
    {
        if (_tornDown)
        {
            return;
        }

        if (item.DanmakuId.Length > 0)
        {
            _danmakuMineIds.Add(item.DanmakuId);   // 在册：uid 判据失手（没查成登录态/应答形状变了）时靠它认"我发的"
        }

        int at = _danmakuItems.FindIndex(x => x.Seconds > item.Seconds);
        if (at < 0)
        {
            at = _danmakuItems.Count;
        }

        _danmakuItems.Insert(at, item);
        if (at <= _danmakuCursorIndex)
        {
            _danmakuCursorIndex++;   // 插在游标前：已经手动上过屏，别让游标再吐一遍
        }
        else
        {
            _danmakuPostedIds.Add(item.DanmakuId);   // 落在游标后面（罕见）：吐到它那一拍跳过
        }

        PushDanmaku(item);
    }

    /// <summary>
    /// 一条弹幕上屏：文字放进层里，运动交给**合成器动画**——一个 Storyboard 改它自己的
    /// TranslateTransform.X，跑在合成线程上，UI 线程再忙也不掉帧（原来 33ms 手搓位移那套又抖又跟不住视频）。
    /// 起点在右边界外、终点完全滑出左边界，时长 = 距离 ÷ 速度；SpeedRatio 跟着倍速。
    /// 位置由视频时间决定 ⇒ 暂停、拖拽、倍速三种情况都能精确对回去（见 SeekDanmaku / SyncDanmakuMotion）。
    /// </summary>
    private void PushDanmaku(DanmakuItem item) => PushDanmakuCore(item, allowQueue: true);

    /// <summary>
    /// 上屏的实体。别人发的：没空道就丢（比叠成一团强）。自己发的：<paramref name="allowQueue"/> 为真时先挂进
    /// _danmakuMineWaiting 等下一拍；等够 6 秒还挤就挑最闲的道硬上——自己刚发的必须看得见。
    /// </summary>
    private void PushDanmakuCore(DanmakuItem item, bool allowQueue)
    {
        string text = item.Text;
        if (_tornDown || !_danmakuOn || _standby || text.Length == 0 || DanmakuLayer.ActualWidth <= 1)
        {
            return;
        }

        double speed = DanmakuSpeed();
        double position = _player.PlaybackSession.Position.TotalSeconds;

        // 入场位置**按视频时间算**：这条"现在就该在"哪儿，就从哪儿起步。
        // 老写法一律从右边界外开始，可推送是 250ms 一拍——晚一拍就落后视频钟约 35px，
        // 一按住把它掰回"视频时间该在的位置"就是用户看到的那下抽动。
        // 这样动画位置 == 命中用的位置 == 按住钉住的位置，一个数，怎么摆都不跳。
        double from = DanmakuLayer.ActualWidth - ((position - item.Seconds) * speed);
        if (from + 1 >= DanmakuLayer.ActualWidth)
        {
            from = DanmakuLayer.ActualWidth;   // 还没到点（余量很小）：从右边界外进
        }

        bool mine = IsMine(item);
        var label = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            FontSize = DanmakuFontSize(),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Opacity = _danmakuOpacity,
        };

        // 自己的弹幕：文字外再套一圈高亮框（抖音红描边 + 红轻底），活视频里一眼认出是自己刚发的；
        // 别人的就是裸字。壳里的文字单独留一份引用：悬停变色、改字号都落在它身上（Border 没有 Foreground）。
        FrameworkElement view = label;
        if (mine)
        {
            view = new Border
            {
                Child = label,
                BorderBrush = new SolidColorBrush(DanmakuAccent),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFE, 0x2C, 0x55)),
                Padding = new Thickness(8, 0, 8, 2),
            };
        }

        // 宽度先自己量：动画终点（完全滑出左边）与"挑哪条道"都要用它，等布局回填太晚
        view.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = view.DesiredSize.Width;

        // 暂停里发出的字（只有自己发的那条会这么来）：视频时钟没在走，它"现在就该在"的地方就是右缘外——
        // 那样暂停时既看不见、也点不到。把锚点前挪"一条字宽 + 空当"，让它停在屏内（下面那拍会把它停住：
        // 视频停字就停），恢复播放从那儿接着飞。时间轴里那份仍按真实时间：拖回去再播照常从右缘进场。
        if (mine && _player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
        {
            item = item with { Seconds = position - ((width + DanmakuGap) / speed) };
            from = DanmakuLayer.ActualWidth - ((position - item.Seconds) * speed);
        }

        if (from + width <= 0)
        {
            return;   // 已经整条滑出去了（推送积压时会有）：不补放，跳过去
        }

        // 挑一条不会跟前一条叠上的道
        int lane = PickDanmakuLane(item.Seconds, width, speed);
        if (lane < 0 && mine)
        {
            if (allowQueue)
            {
                if (!_danmakuMineWaiting.Contains(item))
                {
                    _danmakuMineWaiting.Add(item);   // 自己发的：没道先不丢，下一拍再来（见 FlushMineDanmaku）
                }

                return;
            }

            lane = ForcedMineDanmakuLane();   // 等够久了还挤：挑最闲的道硬上（宁可短暂擦肩也不静默消失）
        }

        if (lane < 0)
        {
            return;   // 别人的：三条道都挤着就先不显示这条（比叠成一团强）
        }

        var slide = new TranslateTransform { X = from };
        view.RenderTransform = slide;
        Canvas.SetLeft(view, 0);
        Canvas.SetTop(view, DanmakuLaneTop(lane));

        var move = new DoubleAnimation
        {
            From = from,
            To = -width,
            Duration = TimeSpan.FromSeconds((from + width) / speed),
        };
        Storyboard.SetTarget(move, slide);
        Storyboard.SetTargetProperty(move, "X");

        var story = new Storyboard { SpeedRatio = DanmakuRate() };
        story.Children.Add(move);

        // 交互不挂在字上：层的留白不吃鼠标，命中统一在 Root 那层按"指针位置 + 视频时间"算
        // （见 DanmakuAt）——这样重叠时也只认真正被指着的那条。
        var run = new DanmakuRun(view, label, slide, story, move, item, lane, width, speed);
        story.Completed += (_, _) => RetireDanmaku(run);

        DanmakuLayer.Children.Add(view);
        _danmakuRuns.Add(run);
        _danmakuMineWaiting.Remove(item);
        _danmakuLanes[lane].Last = run;
        _lastDanmakuLane = lane;   // 下一条从下一条道开始轮（显示区域那几行才都会被用到）
        story.Begin();

        // 视频没在播（暂停里刚发的字、缓冲中推到的一拍）：起表就叫停——弹幕跟视频时钟走，视频停字就停。
        // 位置就停在 from 上（它等于"视频时间该在的位置"，命中/钉住也是这个数）；恢复播放由
        // SyncDanmakuMotion 接着放，和拖拽那条路（RetargetDanmaku）同一套路数。
        if (_player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
        {
            story.Stop();
            slide.X = from;
            run.Frozen = true;
            run.FrozenAtSeconds = position;
        }

        if (!_danmakuPushed)
        {
            _danmakuPushed = true;
            MfLog.Write($"app: 弹幕首次上屏 · 层 {DanmakuLayer.ActualWidth:F0}x{DanmakuLayer.ActualHeight:F0} · 字宽 {width:F0} · 速率 {DanmakuRate():0.##}");
        }
    }

    /// <summary>一条飞完了：从层里摘掉（合成器跑完自己回调，不用计时器轮询）。</summary>
    private void RetireDanmaku(DanmakuRun run)
    {
        if (ReferenceEquals(run, _hoverRun))
        {
            _hoverRun = null;
            _hoverX = 0;
        }

        _danmakuRuns.Remove(run);
        DanmakuLayer.Children.Remove(run.View);
    }

    /// <summary>清空弹幕层：在飞的动画一个个停掉（关开关和拆台都走它），道上的记账也归零。</summary>
    private void ClearDanmaku()
    {
        ClearDanmakuHover();

        foreach (DanmakuRun run in _danmakuRuns)
        {
            run.Story.Stop();
        }

        _danmakuRuns.Clear();
        DanmakuLayer.Children.Clear();
        _danmakuMineWaiting.Clear();   // 自己发的那几条还没上台的一并作废（关开关/换台都算"这一屏收摊"）

        foreach (DanmakuLane lane in _danmakuLanes)
        {
            lane.Last = null;
        }
    }

    /// <summary>当前播放速率（弹幕跟视频时间走：1.5 倍速时字也快 1.5 倍）。</summary>
    private double DanmakuRate()
    {
        double rate = _player.PlaybackSession.PlaybackRate;
        return double.IsFinite(rate) && rate > 0.1 ? rate : 1.0;
    }

    /// <summary>
    /// 播放状态对上：暂停就把在飞的动画全体按住，恢复播放再放开。
    /// 原来那套是靠墙上时钟自己飞，视频停了字还在走——这就是"不跟视频时间走"。
    /// </summary>
    private void SyncDanmakuMotion()
    {
        if (_tornDown)
        {
            return;
        }

        bool playing = _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        foreach (DanmakuRun run in _danmakuRuns.ToArray())   // 放开可能顺手摘掉出屏的那条：照快照走
        {
            if (ReferenceEquals(run, _hoverRun))
            {
                continue;   // 指针正压着它：保持钉住，别把字从指针底下放跑
            }

            if (playing)
            {
                UnfreezeDanmaku(run);
            }
            else
            {
                FreezeDanmaku(run);
            }
        }
    }

    /// <summary>
    /// 跳转 / 改倍速之后，把每条动画拨回它该在的位置：位置 = 视频时间 − 这条的时间。
    /// 这是"跟着视频时间走"的正解——拖到哪儿、什么速率，都是算出来的，不靠积分慢慢推。
    /// 顺带把游标也摆到当前位置（往回拖不补中间那段）。
    /// </summary>
    private void SeekDanmaku(double positionSeconds)
    {
        if (_tornDown)
        {
            return;
        }

        ClearDanmakuHover();   // 位置全变了，指着的那条冻住的坐标也就作废了

        foreach (DanmakuRun run in _danmakuRuns.ToArray())   // 对表可能顺手摘掉该出屏的：照快照走
        {
            RetargetDanmaku(run, positionSeconds);
        }

        SyncDanmakuCursor(positionSeconds);
    }

    /// <summary>
    /// 挑一条不会跟人叠上的道。同一条道上，新字的头必须落在前一条的尾后面（字挨字，留 DanmakuGap 的空当）；
    /// 速度比前一条快时，还要保证在前一条飞出屏之前追不上它。三条道都挤着就返回 -1（那条先不显示，
    /// 比叠成一团强）。全部按**视频时间**算：弹幕本来就跑在视频时钟上，两边同一套坐标。
    /// </summary>
    private int PickDanmakuLane(double seconds, double width, double speed)
    {
        double layerWidth = DanmakuLayer.ActualWidth;
        int rows = DanmakuRows();
        int hoverLane = _hoverRun?.Lane ?? -1;

        // 从"上一条用过的道"的下一条开始轮：总从 0 号道找的话，三条道里永远只有第一条在干活，
        // "显示区域"改成两行/三行也看不出区别（用户报的"不管用"就是这个）
        for (int step = 0; step < rows; step++)
        {
            int lane = (_lastDanmakuLane + 1 + step) % rows;
            if (lane == hoverLane)
            {
                continue;   // 那条道上有一条被鼠标按着不动：别往它身上送新的
            }

            DanmakuRun? last = _danmakuLanes[lane].Last;
            if (last is null)
            {
                return lane;   // 这条道还空着
            }

            // 前一条被按住过就会落后一段（滞后），它的尾巴也在更右边——把这一段一并算进空当
            if (speed <= last.Speed)
            {
                // 不快于前一条：只要头挨着尾后面放，之后就永远不会追上
                if (seconds >= last.Item.Seconds + ((last.Width + DanmakuGap + last.LagPx) / last.Speed))
                {
                    return lane;
                }
            }
            else
            {
                // 快字：算到前一条完全飞出屏那一刻，它还没追进 DanmakuGap 里才安全
                // （追尾只可能发生在前一条还在屏上的时候，所以判据是"到那一刻还差多少时间"）
                double exit = last.Item.Seconds + ((layerWidth + last.Width + last.LagPx) / last.Speed);
                if (exit - seconds <= (layerWidth - DanmakuGap) / speed)
                {
                    return lane;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// 是不是自己发的：① 发送者 uid 跟登录账号对上（服务端翻页把同一条带回来也算，一起高亮）；
    /// ② 本次运行里自己发出去、id 在册的（uid 没记下时的兜底）。
    /// </summary>
    private bool IsMine(DanmakuItem item) =>
        (item.UserId.Length > 0 && string.Equals(item.UserId, Douyin.Feed.MyUid, StringComparison.Ordinal))
        || (item.DanmakuId.Length > 0 && _danmakuMineIds.Contains(item.DanmakuId));

    /// <summary>
    /// 只给自己的弹幕用的兜底落道：正常的"没空道就丢"不适用——挑那条最闲的道（最后一条最早入场的），
    /// 宁可短暂擦肩也不让"自己刚发的"静默消失。
    /// </summary>
    private int ForcedMineDanmakuLane()
    {
        int rows = DanmakuRows();
        int hoverLane = _hoverRun?.Lane ?? -1;
        int best = -1;
        double earliest = double.PositiveInfinity;
        for (int lane = 0; lane < rows; lane++)
        {
            if (lane == hoverLane)
            {
                continue;
            }

            DanmakuRun? last = _danmakuLanes[lane].Last;
            if (last is null)
            {
                return lane;
            }

            if (last.Item.Seconds < earliest)
            {
                earliest = last.Item.Seconds;
                best = lane;
            }
        }

        return best >= 0 ? best : 0;
    }

    /// <summary>
    /// 指针底下是哪条弹幕：层的留白不吃鼠标，所以命中自己算——位置 = 视频时间 − 这条的时间
    /// （动画本身回答不了"现在在哪儿"：动的是 RenderTransform，读回来的是基准值）。
    /// 从最后放的那条往前找（后放的压在上层），横向纵向都给 DanmakuHitSlop 的宽容。
    /// </summary>
    private DanmakuRun? DanmakuAt(Point p, out double left)
    {
        left = 0;
        if (_tornDown || !_danmakuOn || _danmakuRuns.Count == 0 || DanmakuLayer.ActualWidth <= 1)
        {
            return null;
        }

        double layerWidth = DanmakuLayer.ActualWidth;
        double laneHeight = DanmakuFontSize() + 10;

        for (int i = _danmakuRuns.Count - 1; i >= 0; i--)
        {
            DanmakuRun run = _danmakuRuns[i];

            // 正被指着的那条停着：用它冻住的位置；别的一律按"视频时间 − 这条的时间 + 滞后"算
            // （每条用自己的入场速度 run.Speed，跟它那条动画是同一个数）
            double x = DanmakuX(run);
            if (x > layerWidth + 1 || x + run.Width < -1)
            {
                continue;
            }

            double top = DanmakuLaneTop(run.Lane);
            if (p.X >= x - DanmakuHitSlop && p.X <= x + run.Width + DanmakuHitSlop
                && p.Y >= top - DanmakuHitSlop && p.Y <= top + laneHeight + DanmakuHitSlop)
            {
                left = x;
                return run;
            }
        }

        return null;
    }

    /// <summary>鼠标一动就重新认一次指针下是哪条：指着的那条按住并点亮，别的一律放开。</summary>
    private void OnDanmakuPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_danmakuFlyoutOpen)
        {
            return;   // 动作面板开着：点住的那条一直停着，别被鼠标划过就抢走
        }

        DanmakuRun? hit = DanmakuAt(e.GetCurrentPoint(DanmakuLayer).Position, out double left);
        if (ReferenceEquals(hit, _hoverRun))
        {
            return;
        }

        ClearDanmakuHover();
        if (hit is not null)
        {
            _hoverRun = hit;
            _hoverX = left;
            SetDanmakuHover(hit, true);
        }
    }

    /// <summary>鼠标离开舞台：把指着的那条放开——但动作面板开着的时候不许放（那条要一直停着）。</summary>
    private void OnDanmakuPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_danmakuFlyoutOpen)
        {
            return;   // 面板弹出/收回都会带一次"指针离开"，那条正被点着呢
        }

        ClearDanmakuHover();
    }

    /// <summary>点在弹幕上：这条**停住**、开它的动作面板，并且把这一下吃掉（别让视频那边再处理一次）。</summary>
    private void OnDanmakuTapped(object sender, TappedRoutedEventArgs e)
    {
        DanmakuRun? hit = DanmakuAt(e.GetPosition(DanmakuLayer), out double left);
        if (hit is null)
        {
            return;
        }

        e.Handled = true;

        // 悬停时它就已经钉住了：指针还压在同一条上就什么都别动。
        // 一放一按（Unfreeze → Retarget）会让它瞬间跳到"视频时间该在的位置"——悬停那几秒
        // 位置一直在背后算着，所以看起来就是"单击后它瞬移过去、flyout 也在新位置打开"。
        if (ReferenceEquals(_hoverRun, hit))
        {
            ShowDanmakuActions(hit);
            return;
        }

        // 指针没在上面（没动过鼠标就点、或换了一条）：显式钉一次。位置按视频时间算，
        // 跟它那条动画同一个数，所以这一下是原地钉住，不动。
        ClearDanmakuHover();
        _hoverRun = hit;
        _hoverX = left;
        SetDanmakuHover(hit, true);
        MfLog.Write($"app: 弹幕按住 x={left:F0}（层宽 {DanmakuLayer.ActualWidth:F0}，字宽 {hit.Width:F0}）");
        ShowDanmakuActions(hit);
    }

    /// <summary>点亮/放开一条：颜色与字重在这儿，"动/停"交给 FreezeDanmaku。</summary>
    private void SetDanmakuHover(DanmakuRun run, bool hot)
    {
        run.Label.Foreground = new SolidColorBrush(hot ? DanmakuAccent : Microsoft.UI.Colors.White);
        run.Label.FontWeight = hot ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.SemiBold;

        if (hot)
        {
            FreezeDanmaku(run);
        }
        else if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            UnfreezeDanmaku(run);
        }
    }

    /// <summary>
    /// 把这条钉住：动画 Stop 掉、再把它的基准 X 设成"此刻该在的位置"，字就停在那儿不动了。
    /// **不用 <c>Storyboard.Pause()</c>**：那是时钟层的东西，对合成器动画不保证看得见停住；Stop + 基准值是硬的。
    /// 位置取自 <see cref="DanmakuX"/>（含滞后），所以这一下是**原地**钉住。
    /// </summary>
    private void FreezeDanmaku(DanmakuRun run)
    {
        if (run.Frozen)
        {
            return;
        }

        double x = DanmakuX(run);
        run.Story.Stop();
        run.Slide.X = x;
        run.Frozen = true;
        run.FrozenAtSeconds = _player.PlaybackSession.Position.TotalSeconds;
        if (ReferenceEquals(run, _hoverRun))
        {
            _hoverX = x;
        }
    }

    /// <summary>
    /// 放开：**从原地接着飞**（不往回弹）。按住这段时间视频时钟照走，欠下的位移记进滞后
    /// （<c>LagPx</c>）——这样它既没有瞬移，命中和钉住用的位置也仍然是一个数。
    /// 视频停着就继续钉着（媒体时钟没走，所以暂停本身不产生滞后）。
    /// </summary>
    private void UnfreezeDanmaku(DanmakuRun run)
    {
        if (!run.Frozen)
        {
            return;
        }

        double held = _player.PlaybackSession.Position.TotalSeconds - run.FrozenAtSeconds;
        if (held > 0)
        {
            run.LagPx += held * run.Speed;
        }

        run.Frozen = false;
        if (_player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
        {
            FreezeDanmaku(run);   // 视频没播，弹幕也不该走
            return;
        }

        ResumeFromCurrent(run);
    }

    /// <summary>
    /// 从字现在的位置接着飞：把动画的 From 改成当前位置再 Begin（不 Seek——Seek 会把它拉回
    /// "视频时间该在的位置"，那就是用户看到的那下瞬移）。
    /// </summary>
    private void ResumeFromCurrent(DanmakuRun run)
    {
        double x = run.Slide.X;
        if (x + run.Width <= 0)
        {
            RetireDanmaku(run);   // 按住期间已经整条出屏了
            return;
        }

        run.Move.From = x;
        run.Move.Duration = TimeSpan.FromSeconds((x + run.Width) / run.Speed);
        run.Story.SpeedRatio = DanmakuRate();
        run.Story.Begin();
    }

    /// <summary>
    /// 对回视频时间：滞后清零，从"视频时间该在的位置"重新起表。拖进度条、切倍速走它——
    /// 这两种情况本来就该让它回到视频钟上，所以这里的位移是**要**的。
    /// </summary>
    private void RetargetDanmaku(DanmakuRun run, double positionSeconds)
    {
        run.LagPx = 0;
        run.Frozen = false;

        double x = DanmakuLayer.ActualWidth - ((positionSeconds - run.Item.Seconds) * run.Speed);
        if (x + run.Width <= 0)
        {
            RetireDanmaku(run);   // 这一跳之后它早该出屏了
            return;
        }

        run.Move.From = x;
        run.Move.Duration = TimeSpan.FromSeconds((x + run.Width) / run.Speed);
        run.Story.SpeedRatio = DanmakuRate();
        run.Story.Begin();

        if (_player.PlaybackSession.PlaybackState != MediaPlaybackState.Playing)
        {
            // 视频停着：起表就叫停，钉在这个位置（媒体时钟不走，滞后仍然是 0）
            run.Story.Stop();
            run.Slide.X = x;
            run.Frozen = true;
            run.FrozenAtSeconds = positionSeconds;
        }
    }

    /// <summary>
    /// 这条此刻该在的左沿：<c>视频时间 − 这条的时间 + 滞后</c>。
    /// 命中测试、钉住、接着飞全用这一个算法——三处是同一个数，就不会有"按住时跳一下"这种事。
    /// </summary>
    private double DanmakuX(DanmakuRun run) =>
        ReferenceEquals(run, _hoverRun) && run.Frozen
            ? _hoverX
            : DanmakuLayer.ActualWidth
                - ((_player.PlaybackSession.Position.TotalSeconds - run.Item.Seconds) * run.Speed)
                + run.LagPx;

    /// <summary>放开当前指着的那条（换条、指针移开、拆台都走它）。</summary>
    private void ClearDanmakuHover()
    {
        if (_hoverRun is null)
        {
            return;
        }

        DanmakuRun run = _hoverRun;
        _hoverRun = null;
        _hoverX = 0;
        SetDanmakuHover(run, false);
    }

    /// <summary>
    /// "这边到底有没有账号"：罐里没 sessionid，或者**真问过服务端**说没登（<see cref="NativeFeed.LoginVerified"/> 为 false）——
    /// 两者都算没账号。凡是要号的动作（发弹幕 / 点赞 / 删弹幕 / 稍后再看）先过这一道：
    /// 罐里有枚过期的 sessionid 时，光看 <c>IsLoggedIn</c> 会一直以为有账号，于是"没账号还照有账号的规矩弹面板"。
    /// </summary>
    private static bool NoAccount =>
        !Douyin.Feed.IsLoggedIn || Douyin.Feed.LoginVerified == false;

    /// <summary>
    /// 点开一条弹幕：上面一行内容，下面一行小按钮、**靠右**（点赞；自己的再加删除）。没账号就别弹这个面板——
    /// 弹了也只能拿服务端那句"用户未登录"，不如当场说清楚。
    /// </summary>
    private void ShowDanmakuActions(DanmakuRun run)
    {
        if (NoAccount)
        {
            Toast("登录之后才能操作弹幕");
            return;
        }

        var text = new TextBlock
        {
            Text = run.Item.Text,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 13,
        };

        var panel = new StackPanel { Spacing = 8, Width = 224 };
        panel.Children.Add(text);
        var flyout = new Flyout { Content = panel, Placement = FlyoutPlacementMode.Top };
        _danmakuFlyoutOpen = true;
        flyout.Closed += (_, _) =>
        {
            _danmakuFlyoutOpen = false;
            ClearDanmakuHover();   // 面板收了才放开：开着的时候这条一直停着
        };

        // 赞过没有：接口的 digg_type 说一半，本次运行里刚点的说另一半
        bool liked = run.Item.Liked || _likedDanmakuIds.Contains(DanmakuKey(run.Item));
        var like = Small(LikeLabel(run.Item.Digg, liked));
        like.Click += (_, _) => ToggleDanmakuLike(run, like);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        row.Children.Add(like);

        // 自己的弹幕：点赞 + 删除；别人的只有点赞——网页版那颗"举报"实测是安慰弹窗（点击只弹提示、连请求都不发，
        // 2026-10-16 十次全链路取证），不做假动作，已撤掉；细节见 API-MAP.md「举报」节。
        if (IsMine(run.Item) && run.Item.DanmakuId.Length > 0)
        {
            var del = Small("删除");
            del.Click += async (_, _) =>
            {
                if (await DeleteMineDanmaku(run, del))
                {
                    flyout.Hide();   // 删成了才收面板（没删成就留着，那行服务端原话还在）
                }
            };

            row.Children.Add(del);
        }

        panel.Children.Add(row);
        flyout.ShowAt(run.View);

        static Button Small(string label) => new()
        {
            Content = label,
            FontSize = 12,
            Padding = new Thickness(8, 3, 8, 3),
            MinWidth = 0,
            CornerRadius = new CornerRadius(6),
        };
    }

    /// <summary>
    /// 点赞键：真发接口（GET danmaku/digg，action_type=1 赞 / 0 取消，见 DouyinClient.DanmakuDiggUrl）。
    /// 服务端 <c>status_code == 0</c> 才改界面状态，失败就把服务端原话弹出来——不假装赞上了。
    /// 成功的这一下只在界面上 +1：接口回的 digg_count 是"别人赞的数"，我们自己那一发要等下次拉弹幕才带上。
    /// </summary>
    private async void ToggleDanmakuLike(DanmakuRun run, Button button)
    {
        string danmakuId = run.Item.DanmakuId;
        string awemeId = ItemId ?? string.Empty;
        if (danmakuId.Length == 0 || awemeId.Length == 0)
        {
            Toast("拿不到这条的身份（弹幕 id / 视频 id），赞不了");
            return;
        }

        string key = DanmakuKey(run.Item);
        bool liked = _likedDanmakuIds.Contains(key) || run.Item.Liked;

        button.IsEnabled = false;
        try
        {
            (bool ok, string note) = await Douyin.Feed.DanmakuDiggAsync(awemeId, danmakuId, !liked, CancellationToken.None);
            if (!ok)
            {
                Toast(note);
                return;
            }

            if (liked)
            {
                _likedDanmakuIds.Remove(key);
            }
            else
            {
                _likedDanmakuIds.Add(key);
            }

            button.Content = LikeLabel(run.Item.Digg, !liked);
            Toast(note);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    /// <summary>
    /// 删掉自己发的那条（写接口 delete_v2，user_type=1——我发的都是 1）：删成了才把本地痕迹一并清掉
    /// （在飞的即刻退场、时间轴不留给回放、身份册销号，见 ForgetMineDanmaku）；没删成就照实弹服务端原话。
    /// </summary>
    private async Task<bool> DeleteMineDanmaku(DanmakuRun run, Button button)
    {
        string danmakuId = run.Item.DanmakuId;
        if (danmakuId.Length == 0)
        {
            Toast("拿不到这条的身份（弹幕 id），删不了");
            return false;
        }

        button.IsEnabled = false;
        try
        {
            (bool ok, string note) = await Douyin.Feed.DanmakuDeleteAsync(danmakuId, CancellationToken.None);
            if (_tornDown)
            {
                return false;
            }

            if (!ok)
            {
                Toast(note);
                button.IsEnabled = true;
                return false;
            }

            MfLog.Write($"app: 弹幕删掉了 id={danmakuId}");
            Toast("已删除");
            ForgetMineDanmaku(danmakuId);
            return true;
        }
        catch (Exception ex)
        {
            MfLog.Write("app: 删弹幕报错 " + ex.GetType().Name + " · " + ex.Message);
            Toast("删弹幕报错：" + ex.Message);
            button.IsEnabled = true;
            return false;
        }
    }

    /// <summary>
    /// 一条自己发的弹幕在服务端删掉之后，把本地痕迹清干净：在飞的即刻退场（先停表再摘）、
    /// 时间轴里别留着（拖回去不再出现；游标跟着挪）、身份册（_danmakuMineIds / _danmakuPostedIds）销号。
    /// </summary>
    private void ForgetMineDanmaku(string danmakuId)
    {
        foreach (DanmakuRun run in _danmakuRuns.ToArray())
        {
            if (string.Equals(run.Item.DanmakuId, danmakuId, StringComparison.Ordinal))
            {
                run.Story.Stop();
                RetireDanmaku(run);
            }
        }

        int at = _danmakuItems.FindIndex(x => string.Equals(x.DanmakuId, danmakuId, StringComparison.Ordinal));
        while (at >= 0)
        {
            _danmakuItems.RemoveAt(at);
            if (at < _danmakuCursorIndex)
            {
                _danmakuCursorIndex--;   // 摘掉的是游标前头那条：游标跟着往前挪一格
            }

            at = _danmakuItems.FindIndex(at, x => string.Equals(x.DanmakuId, danmakuId, StringComparison.Ordinal));
        }

        _danmakuMineIds.Remove(danmakuId);
        _danmakuPostedIds.Remove(danmakuId);
        _danmakuMineWaiting.RemoveAll(x => string.Equals(x.DanmakuId, danmakuId, StringComparison.Ordinal));
    }

    /// <summary>
    /// 点赞键上的字：操作名 + 点赞数，数来自接口的 digg_count。
    /// 赞成了才在这里 +1（服务端那份数要等下次拉弹幕才带上）；没成就照实弹服务端原话，数不动。
    /// </summary>
    private static string LikeLabel(int digg, bool liked) =>
        (liked ? "已赞 " : "点赞 ") + (liked ? digg + 1 : digg);

    /// <summary>弹幕的身份证：优先后端给的 danmaku_id，退而用文字。</summary>
    private static string DanmakuKey(DanmakuItem item) =>
        item.DanmakuId.Length > 0 ? item.DanmakuId : item.Text;

    // ===== 弹幕取数：换条重取第一页，之后跟着播放往后续 =====

    private static void OnItemIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((MfStageControl)d).SwitchItem((string?)e.NewValue ?? string.Empty);

    /// <summary>
    /// 换了一条：弹幕清空重取（先要第一页，剩下的随播放进度要），右侧那排的"我点过没有"跟着重对
    /// （计数文字可能两条一模一样，光靠 OnRailChanged 触发不了，所以在这里主动对一次）。
    /// </summary>
    private void SwitchItem(string id)
    {
        if (_collectFlyout.IsOpen)
        {
            _collectClosingVideoId = _collectFlyoutVideoId;
            _collectClosingSnapshot = _collectContent.SelectedFolders;
            _collectFlyout.Hide();
        }

        _collectLoadGen++;
        CommentRows.Clear();
        _pinnedCommentId = string.Empty;
        _pinnedAuthor = string.Empty;
        _pinnedText = string.Empty;
        _pinnedImageUrl = string.Empty;
        _pinnedFallbackRow = null;
        ClearReplyTarget();
        _commentFor = string.Empty;
        _commentCursor = 0;
        _commentHasMore = false;
        _commentTotal = -1;
        ApplyCommentTotal();
        SetCommentStatus(string.Empty);
        if (CommentSplit.IsPaneOpen)
        {
            _ = LoadCommentsAsync(reset: true);     // 面板开着就换这条的评论
        }

        RefreshRail();
        ResetDanmaku(id);
    }

    /// <summary>换了一条：弹幕清空重取（先要第一页，剩下的随播放进度要）。</summary>
    private void ResetDanmaku(string id)
    {
        _danmakuItems.Clear();
        _danmakuPostedIds.Clear();
        _danmakuCursorIndex = 0;
        _danmakuNextStart = 0;
        _danmakuFor = string.Empty;
        _danmakuPushed = false;
        ClearDanmaku();

        if (_tornDown || id.Length == 0)
        {
            return;
        }

        _danmakuFor = id;
        _ = FetchDanmakuAsync(id, 0);
    }

    /// <summary>
    /// 取一页弹幕（startMs 起，接口一页给 32 秒窗口）。回来时还认这条才收下：按时间插进表里、
    /// 记下下一页从哪儿起。取不到只写进 mf.log，界面上不吵——弹幕没有就是没有。
    /// </summary>
    private async Task FetchDanmakuAsync(string id, long startMs)
    {
        if (_danmakuLoading || _tornDown)
        {
            return;
        }

        _danmakuLoading = true;
        try
        {
            DanmakuPage page = await Douyin.Feed.DanmakuPageAsync(id, startMs, CancellationToken.None);
            if (_tornDown || !string.Equals(_danmakuFor, id, StringComparison.Ordinal))
            {
                return;   // 已经换到别的条了，这一页作废
            }

            if (page.Items.Count > 0)
            {
                _danmakuItems.AddRange(page.Items);
                _danmakuItems.Sort(static (a, b) => a.Seconds.CompareTo(b.Seconds));
            }

            _danmakuNextStart = page.NextStartMs;
            MfLog.Write($"app: 弹幕 +{page.Items.Count} 条（共 {_danmakuItems.Count}）· 下一页 {(page.NextStartMs > 0 ? page.NextStartMs + "ms" : "到头")}");
        }
        catch (Exception ex)
        {
            MfLog.Write("app: 弹幕取不到：" + ex.GetType().Name + " · " + ex.Message);
        }
        finally
        {
            _danmakuLoading = false;
        }
    }

    /// <summary>
    /// 捞等待队列里"自己的弹幕"：还有空道就正常落道；落后播放钟超过 6 秒还挤着，就走兜底硬上。
    /// 每 250ms 一拍由 PumpDanmaku 调；落道成功的那条在 PushDanmakuCore 里出队，失败的自留队里。
    /// </summary>
    private void FlushMineDanmaku(double positionSeconds)
    {
        foreach (DanmakuItem item in _danmakuMineWaiting.ToArray())
        {
            PushDanmakuCore(item, allowQueue: positionSeconds - item.Seconds <= 6);
        }
    }

    /// <summary>
    /// 250ms 一拍的吐字：把"时间到了"的推进层里（一跳最多 6 条，拖到远处也不会一次糊满屏）；
    /// 往回拖（seek）就把游标重摆；手里的快放完了（离末尾不足 30 秒）顺手去要下一页。
    /// 候场那条页面不上屏，但游标照样跟着走，免得它上台时把攒下的全吐出来。
    /// </summary>
    private void PumpDanmaku(double positionSeconds)
    {
        if (_tornDown || !_danmakuOn)
        {
            return;
        }

        // 等道的那几条"自己的弹幕"：每拍来捞一次（没取到弹幕的视频也要捞——所以放在条数判断前面）
        if (_danmakuMineWaiting.Count > 0)
        {
            FlushMineDanmaku(positionSeconds);
        }

        if (_danmakuItems.Count == 0)
        {
            return;
        }

        if (_danmakuCursorIndex > 0 && _danmakuCursorIndex <= _danmakuItems.Count
            && _danmakuItems[_danmakuCursorIndex - 1].Seconds > positionSeconds + 0.5)
        {
            SyncDanmakuCursor(positionSeconds);   // 往回拖了：不补中间的
            return;
        }

        int pushed = 0;
        while (_danmakuCursorIndex < _danmakuItems.Count
            && _danmakuItems[_danmakuCursorIndex].Seconds <= positionSeconds
            && pushed < 6)
        {
            DanmakuItem due = _danmakuItems[_danmakuCursorIndex];
            if (due.DanmakuId.Length == 0 || !_danmakuPostedIds.Remove(due.DanmakuId))
            {
                if (!_standby)
                {
                    PushDanmaku(due);
                }
            }

            _danmakuCursorIndex++;
            pushed++;
        }

        // 手里的窗口快放完了（下一页的起点离当前位置不足 20 秒）才去要下一段。
        // 判据必须看**窗口**（_danmakuNextStart），不能看"最后一条弹幕的时间"：
        // 没人打弹幕的那段视频里，最后一条永远停在开头，那个判据会每秒触发一发（实测爬到 608000ms）。
        if (_danmakuNextStart > 0 && !_danmakuLoading && _danmakuFor.Length > 0
            && _danmakuNextStart - (positionSeconds * 1000) < 20000)
        {
            _ = FetchDanmakuAsync(_danmakuFor, _danmakuNextStart);
        }
    }

    /// <summary>把游标摆到该从哪儿接着吐：第一条时间 ≥ 当前位置的。</summary>
    private void SyncDanmakuCursor(double positionSeconds)
    {
        int index = 0;
        while (index < _danmakuItems.Count && _danmakuItems[index].Seconds < positionSeconds)
        {
            index++;
        }

        _danmakuCursorIndex = index;
    }

    /// <summary>行数：设置那一档 0/1/2 → 1/2/3 行。</summary>
    private int DanmakuRows() => Math.Clamp(_danmakuRows + 1, 1, 3);

    /// <summary>字体：较小/适中/较大 → 14/17/21 px。</summary>
    private double DanmakuFontSize() => _danmakuFont switch { 0 => 14, 1 => 17, _ => 21 };

    /// <summary>速度：较慢/适中/较快 → 90/140/200 px/s。</summary>
    private double DanmakuSpeed() => _danmakuSpeed switch { 0 => 90, 1 => 140, _ => 200 };

    /// <summary>第 n 行的上沿：贴画面上缘 12px 起，行高跟着字体走。</summary>
    private double DanmakuLaneTop(int lane) => 12 + lane * (DanmakuFontSize() + 10);

    /// <summary>
    /// 四项设置改了就落到在飞的那些字上：字号与不透明度当场改；行数与速度另走两条路
    /// （挑道轮着来——见 <see cref="PickDanmakuLane"/>；场上这批当场换速——见 <see cref="ApplyDanmakuSpeed"/>）。
    /// </summary>
    private void ApplyDanmakuSettings()
    {
        if (_tornDown)
        {
            return;
        }

        double size = DanmakuFontSize();
        foreach (DanmakuRun run in _danmakuRuns)
        {
            run.Label.FontSize = size;
            run.Label.Opacity = _danmakuOpacity;
        }

        DanmakuLayer.Visibility = _danmakuOn ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 速度档改了：**场上这批**也当场换到新速度。每条的入场速度跟着换，然后从它现在的位置接着飞。
    /// 不按视频钟重摆（那就是 <see cref="RetargetDanmaku"/>）——那样改速度的一瞬间整屏的字会横向跳一下。
    /// 两边差出来的那截记进滞后：<c>位置 = 视频时间该在的位置 + 滞后</c>（见 <see cref="DanmakuX"/>），
    /// 于是命中、钉住、接着飞用的还是同一个数，脚底下也不动。
    /// 被钉住的那条只换速度：它停着是正常状态（按住/面板开着/视频停着），放开时自会按新速度走。
    /// </summary>
    private void ApplyDanmakuSpeed()
    {
        if (_tornDown)
        {
            return;
        }

        double speed = DanmakuSpeed();
        double position = _player.PlaybackSession.Position.TotalSeconds;
        double layerWidth = DanmakuLayer.ActualWidth;
        foreach (DanmakuRun run in _danmakuRuns.ToArray())   // 接着飞可能顺手摘掉该出屏的：照快照走
        {
            // 动画正写在 X 上（停着的也有基准值）：读回来就是它此刻在屏上的位置
            double x = run.Slide.X;
            run.Speed = speed;

            // 账要按新速度重算一笔，位置才仍然等于像素（"位置 = 视频钟 + 滞后"，命中/钉住都读这一个数）。
            // 停着的那条也一样要算：它的像素不动，但命中框算出来不能跟它停的地方差一截。
            run.LagPx = x - layerWidth + ((position - run.Item.Seconds) * speed);

            if (!run.Frozen)
            {
                ResumeFromCurrent(run);   // 从原地接着飞（不重摆）：整屏不跳，速度当场就变
            }
        }
    }

    /// <summary>在飞的一条：一条合成器动画 + 它属于视频里的第几秒（同步与交互都靠它）。</summary>
    private sealed class DanmakuRun(
        FrameworkElement view,
        TextBlock label,
        TranslateTransform slide,
        Storyboard story,
        DoubleAnimation move,
        DanmakuItem item,
        int lane,
        double width,
        double speed)
    {
        /// <summary>层里那个壳（普通弹幕就是文字本身；自己的弹幕是套了高亮框的 Border）。</summary>
        public FrameworkElement View { get; } = view;

        /// <summary>壳里的文字：悬停变色、改字号都落在它身上（Border 没有 Foreground）。</summary>
        public TextBlock Label { get; } = label;

        public TranslateTransform Slide { get; } = slide;

        public Storyboard Story { get; } = story;

        /// <summary>改 X 的那条动画：从"当前位置"接着飞时要改它的 From 与 Duration。</summary>
        public DoubleAnimation Move { get; } = move;

        public DanmakuItem Item { get; } = item;

        /// <summary>它在第几条道上（命中测试要按道的上沿算）。</summary>
        public int Lane { get; } = lane;

        /// <summary>入场时量下来的字宽。</summary>
        public double Width { get; } = width;

        /// <summary>
        /// 这条的入场速度（px/s）。默认入场时量下来就定了；**改速度档时要连它带位置一起换**
        /// （<see cref="ApplyDanmakuSpeed"/> 里先改它、再 RetargetDanmaku 重摆）——
        /// 只改一边的话，算出来的位置跟动画就不是一个数了。
        /// </summary>
        public double Speed { get; set; } = speed;

        /// <summary>现在是不是被钉住不动（鼠标压着、面板开着、或者视频暂停中）。</summary>
        public bool Frozen { get; set; }

        /// <summary>
        /// 被按住这段时间攒下的**滞后**（像素）：位置 = 视频时间该在的位置 + 滞后。
        /// 放开时就从原地接着走，不把欠的位移一次性补上（不然会看到往回弹一下）。
        /// </summary>
        public double LagPx { get; set; }

        /// <summary>钉住那一刻的视频时间（秒）：放开时用它算滞后（视频暂停期间媒体时钟不走，所以暂停不产生滞后）。</summary>
        public double FrozenAtSeconds { get; set; }
    }

    /// <summary>开关动了：先把值写进 MVVM（持久化那一侧），依赖属性再跟着走。</summary>
    private void OnAutoPlayToggled(object sender, RoutedEventArgs e)
    {
        if (_autoPlaySyncing)
        {
            return;
        }

        AppSettings().AutoPlay = AutoPlayToggle.IsOn;
        AutoPlay = AutoPlayToggle.IsOn;
    }

    /// <summary>
    /// 全应用共享的设置视图模型。XAML 里那几个**只读显示**（音量百分比之类）直接 x:Bind 它，
    /// 值变了自己会动——不在代码里戳控件（用户 2026-10-14 点名要求）。
    /// </summary>
    public ViewModels.AppSettingsViewModel Settings => AppSettings();

    /// <summary>
    /// 全应用共享的设置视图模型：音量/连播这些持久化项直接写它。
    /// 页面那边还挂着双向绑定（把设置灌回控件这条方向），这里只是把"用户改的值"这条路走实，
    /// 不指望绑定的回写方向——2026-10-13 用户点名要求"要写进 MVVM"。
    /// </summary>
    private static ViewModels.AppSettingsViewModel AppSettings() =>
        ((App)Application.Current).ViewModel.AppSettingsVM;

    /// <summary>
    /// 连播设置变了（含启动装载、另一条页面改的、设置页改的）：把开关的勾和播放行为一起对上。
    /// 换台时新上场那页正是靠这条把勾补齐——以前它是页面本地字段，所以会"有时候显示不对"。
    /// </summary>
    private static void OnAutoPlaySettingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var stage = (MfStageControl)d;
        stage._autoPlay = (bool)e.NewValue;
        stage.SyncAutoPlayToggle();
    }

    /// <summary>
    /// 把开关的勾与 <see cref="_autoPlay"/> 对齐。构造函数收尾也要调一次：
    /// 依赖属性是 x:Bind 装的，可能比开关本身先到位，那一下开关还没建出来，只能等树建好再补。
    /// </summary>
    private void SyncAutoPlayToggle()
    {
        if (AutoPlayToggle is null || AutoPlayToggle.IsOn == _autoPlay)
        {
            return;
        }

        _autoPlaySyncing = true;
        AutoPlayToggle.IsOn = _autoPlay;
        _autoPlaySyncing = false;
    }

    /// <summary>
    /// 弹幕开关设置变了（启动装载、另一页改的、设置页改的）：把镜像字段与层一起对上。
    /// 跟连播一个套路——控件里那个 bool 只是镜像，真值在共享设置 VM 里。
    /// </summary>
    private static void OnDanmakuOnSettingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var stage = (MfStageControl)d;
        stage.ApplyDanmakuOn((bool)e.NewValue);
        stage.SyncDanmakuToggle();
    }

    /// <summary>把开关的勾与 <see cref="_danmakuOn"/> 对齐（我们自己拨的勾，_danmakuSyncing 挡住写回）。</summary>
    private void SyncDanmakuToggle()
    {
        if (DanmakuToggle is null || DanmakuToggle.IsChecked == _danmakuOn)
        {
            return;
        }

        _danmakuSyncing = true;
        DanmakuToggle.IsChecked = _danmakuOn;
        _danmakuSyncing = false;
    }

    /// <summary>
    /// 开关的真动作：层亮/收、在飞的清掉、再开时从当前播放点接着吐。
    /// 树还没建出来就只记值——构造函数收尾会再调一次（依赖属性有可能比控件先到位）。
    /// </summary>
    private void ApplyDanmakuOn(bool on)
    {
        _danmakuOn = on;

        if (DanmakuLayer is null)
        {
            return;
        }

        DanmakuLayer.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on)
        {
            ClearDanmaku();
            return;
        }

        // 再开时从当前播放点接着吐：不然关这一会儿攒下的会一次性糊满屏
        SyncDanmakuCursor(_player.PlaybackSession.Position.TotalSeconds);
    }

    /// <summary>Flyout 里点了某一档：记下来应用到播放会话，按钮上的标签跟着换。</summary>
    private void OnSpeedPick(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem item
            && int.TryParse(item.Tag?.ToString(), out int index)
            && index >= 0
            && index < Rates.Length)
        {
            _rateIndex = index;
            ApplyRate();
        }
    }

    /// <summary>倍速是播放会话级状态，换台后新源会回到 1x，所以起播时也补一遍。</summary>
    private void ApplyRate()
    {
        double rate = Rates[_rateIndex];
        _player.PlaybackSession.PlaybackRate = rate;
        SpeedLabel.Text = rate.ToString("0.##") + "x";
        SyncSpeedFlyout();

        // 在飞的弹幕按新速率重新对表（SpeedRatio 跟着倍速，位置用视频时间重算）
        SeekDanmaku(_player.PlaybackSession.Position.TotalSeconds);
    }

    /// <summary>菜单里那枚勾跟着当前档走：起播时补的这一遍也要对上，别让按钮和菜单说两套话。</summary>
    private void SyncSpeedFlyout()
    {
        if (SpeedButton.Flyout is not MenuFlyout menu)
        {
            return;
        }

        foreach (object entry in menu.Items)
        {
            if (entry is RadioMenuFlyoutItem radio && int.TryParse(radio.Tag?.ToString(), out int index))
            {
                radio.IsChecked = index == _rateIndex;
            }
        }
    }

    // ===== 清晰度 =====

    /// <summary>换条了：先把菜单铺成这条的样子，再按"记住的档"挑一条流去装载（该档没有就自动降档）。</summary>
    private void LoadEffectiveSource()
    {
        SyncQualityMenu();
        Load(PickUrl());
    }

    /// <summary>
    /// 这条视频该用哪一档：记住的档命中就是它；没有那一档就**自动降档**——挑不高于它、最接近的那一档，
    /// 整条视频都比它糊就挑最低那档；记的是"默认"（或这条视频没有档位数据）时给空串，表示就用 feed 下发的那条。
    /// </summary>
    private string PickKey()
    {
        IReadOnlyList<VideoQuality> items = Qualities?.Items ?? [];
        string key = QualityKey ?? string.Empty;
        if (key.Length == 0 || items.Count == 0)
        {
            return string.Empty;
        }

        if (items.Any(q => q.Key == key))
        {
            return key;
        }

        if (!int.TryParse(key, out int want))
        {
            return string.Empty;
        }

        // 表是从高到低排好的：从尾往前找第一档不高于它的（降档）
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (int.TryParse(items[i].Key, out int height) && height <= want)
            {
                return items[i].Key;
            }
        }

        return items[^1].Key;   // 存的那档比这条视频所有档都低：给最低的那档，别再退回默认
    }

    /// <summary>这条视频该播哪条流：<see cref="PickKey"/> 挑出来的那一档；挑不出来就 feed 下发的那条。</summary>
    private string PickUrl()
    {
        IReadOnlyList<VideoQuality> items = Qualities?.Items ?? [];
        string key = PickKey();
        return key.Length > 0
            ? items.FirstOrDefault(q => q.Key == key)?.Url ?? Source ?? string.Empty
            : Source ?? string.Empty;
    }

    /// <summary>
    /// 现在放的是哪一档（菜单那枚勾、按钮脸上的档位都以它为准，不认"记住的"那一档）。
    /// 先按地址在档位表里认；认不出再看播的是不是"默认流"——解析时拿 data_size 把默认流对到了某一档上
    /// （见 NativeFeed.QualitiesOf），对得上就报那一档；都对不上给空串，菜单谁都不勾。
    /// </summary>
    private string CurrentQualityKey()
    {
        if (_playing.Length == 0)
        {
            return string.Empty;
        }

        VideoQuality? byUrl = (Qualities?.Items ?? []).FirstOrDefault(q => q.Url == _playing);
        if (byUrl is not null)
        {
            return byUrl.Key;
        }

        return _playing == (Source ?? string.Empty) ? Qualities?.DefaultKey ?? string.Empty : string.Empty;
    }

    /// <summary>
    /// 记住的档变了（点了菜单、另一条页面改的、启动装载）：按新档重挑一条流，跟**正在放的**不是同一档就换过去。
    /// 同一档不重装（换台时档位表可能先到，或默认流本来就是那一档的文件）——白卡一下没有意义；
    /// 但设置照记：下一次、下一条视频按新档走。
    /// </summary>
    private void ApplyQuality()
    {
        if (_tornDown)
        {
            return;
        }

        IReadOnlyList<VideoQuality> items = Qualities?.Items ?? [];
        string target = PickKey();
        if (target.Length > 0 && target != CurrentQualityKey())
        {
            if (items.FirstOrDefault(q => q.Key == target) is { } tier)
            {
                SwitchTo(tier.Url);
            }
        }

        SyncQualityMenu();
    }

    /// <summary>
    /// 菜单里点了一档：写设置（防抖落盘）→ 依赖属性 → 由 <see cref="ApplyQuality"/> 去换流。
    /// 跟音量那颗 <see cref="CommitVolume"/> 一个规矩：依赖属性与共享设置 VM 一起写，
    /// 光靠 x:Bind 的回写方向不算持久化路径。
    /// </summary>
    private void OnQualityPick(object sender, RoutedEventArgs e)
    {
        if (_tornDown
            || sender is not RadioMenuFlyoutItem item
            || item.Tag?.ToString() is not { Length: > 0 } key)
        {
            return;
        }

        CommitQuality(key);
    }

    /// <summary>把清晰度档落实：依赖属性（页面那条绑定）与共享设置 VM（落盘）一起写。</summary>
    private void CommitQuality(string key)
    {
        key ??= string.Empty;
        QualityKey = key;
        AppSettings().QualityKey = key;
    }

    /// <summary>
    /// 换档装载：位置与"播/停"都带过去。先把当前位置按**新地址**记进续播表——LoadAsync 正是拿它
    /// 把 seek 落点附近预取进内存；没有这一下，切档就是从头起播再跳回去，那一下就是肉眼可见的卡顿。
    ///
    /// 只对**同一条视频**的档位切换做这件事：换台时档位表可能比 Source 早到一步（x:Bind 逐项刷），
    /// 那一路要是也搬位置，下一条视频就会带着上一条的进度起播——所以按 video_id 认亲，
    /// 不是同一条就干干净净从头放。
    /// </summary>
    private void SwitchTo(string url)
    {
        MediaPlaybackSession session = _player.PlaybackSession;
        string wasStub = _playing.Length > 0 ? UrlStub(_playing) : string.Empty;
        bool sameVideo = wasStub.Contains("video_id=", StringComparison.Ordinal) && wasStub == UrlStub(url);
        double position = session.Position.TotalSeconds;
        double total = session.NaturalDuration.TotalSeconds;
        if (sameVideo && !double.IsNaN(position) && !double.IsNaN(total) && position > 0 && total > 0.5)
        {
            RememberProgress(url, position, total);
        }

        // 暂停着换的档：新流到货别自己播起来（Playing 那一档里把画面停回去，见 OnPlaybackState）
        _pauseNextLoad = sameVideo && session.PlaybackState != MediaPlaybackState.Playing;
        MfLog.Write("app: 切清晰度 " + LabelOf(url));
        Load(url);
    }

    /// <summary>
    /// 按钮脸与菜单按"正在放哪一档"对齐：这条视频真有的档位键交给它（哪几行亮）、当前档交给脸
    /// （写进 Quality.CurrentLabel，脸上那条 x:Bind 自己会刷），那枚点从代码里摆——与倍速的
    /// <see cref="SyncSpeedFlyout"/> 同一路：RadioMenuFlyoutItem 的点由绑定驱动时根本不渲染。
    /// 两份键表一律整份换掉，那五行是死档位表（XAML）。
    /// </summary>
    private void SyncQualityMenu()
    {
        IReadOnlyList<VideoQuality> items = Qualities?.Items ?? [];
        string current = CurrentQualityKey();
        Quality.Available = [.. items.Select(static q => q.Key)];
        Quality.CurrentKey = current;
        Quality.CurrentLabel = items.FirstOrDefault(q => q.Key == current)?.Label ?? "清晰度";

        if (QualityButton.Flyout is not MenuFlyout menu)
        {
            return;
        }

        foreach (object entry in menu.Items)
        {
            if (entry is RadioMenuFlyoutItem radio)
            {
                radio.IsChecked = radio.Tag?.ToString() == current;
            }
        }
    }

    /// <summary>这条地址是哪一档（切档那行日志用）：档位表里查一下，查不到就是"默认"那条。</summary>
    private string LabelOf(string url) =>
        (Qualities?.Items ?? []).FirstOrDefault(q => q.Url == url)?.Label ?? "默认";

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

    /// <summary>
    /// 全屏 = **视频全屏**：窗口满屏 + 把侧边栏与顶栏一起收掉（复用沉浸那一套）。
    /// 用户点名的要求："不要同时将侧边栏和顶部栏包含其中"。退出时把沉浸还原成进来前的样子，
    /// 别把人家本来开着的程序内全屏一并关了。presenter 的存取在 Chrome 里（Esc / 关闭按钮那条出口也要用）。
    /// </summary>
    private void OnFullscreenClick(object sender, RoutedEventArgs e)
    {
        AppWindow window = HostWindow();
        if (window.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            Chrome.LeaveFullscreen(window);
            Chrome.SetImmersive(_immersiveBeforeFullscreen);
            ApplyCommentPanelTopInset();   // 沉浸本来就开着时上面那发不发事件，这里补算一次
            return;
        }

        _immersiveBeforeFullscreen = Chrome.Immersive;
        Chrome.EnterFullscreen(window);
        Chrome.SetImmersive(true);
        ApplyCommentPanelTopInset();
    }

    /// <summary>
    /// 加入稍后再看。抖音那边它是一把系统收藏夹（collects/list 里那把椅子），得先读出来再往里头放。
    /// 这条链今天被 Argus 签名墙挡着：纯 C# 的 a_bogus 在这个端点族不被认（403 Blocked by ArgusSecurityPlugin
    /// Signature Not Found），写接口另需 path 绑定的 x-tt-session-dtrait。所以点下去把服务端的原话弹在屏上，
    /// 不装作加成功了；签名那一档解开后这条链就是现成的（见 NativeFeed.WatchLaterAddAsync）。
    /// </summary>
    private async void OnWatchLaterClick(object sender, RoutedEventArgs e)
    {
        if (_tornDown)
        {
            return;
        }

        string id = ItemId ?? string.Empty;
        if (id.Length == 0)
        {
            Toast("这条还没拿到 id，加不了");
            return;
        }

        if (NoAccount)
        {
            Toast("登录之后才能加入稍后再看");
            return;
        }

        Toast("正在加入稍后再看…");
        string note;
        try
        {
            note = await Douyin.Feed.WatchLaterAddAsync(id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            note = "加入失败：" + ex.Message;
        }

        if (_tornDown)
        {
            return;
        }

        bool ok = note.StartsWith("已加入", StringComparison.Ordinal);
        WatchLaterIcon.Foreground = new SolidColorBrush(
            ok ? Microsoft.UI.ColorHelper.FromArgb(255, 254, 44, 85) : Microsoft.UI.ColorHelper.FromArgb(204, 255, 255, 255));
        Toast(note);
    }

    /// <summary>程序内全屏：状态在窗口外壳那边（Chrome），这里只翻开关；按钮点亮由 OnImmersiveChanged 接。</summary>
    private void OnImmersiveClick(object sender, RoutedEventArgs e) => Chrome.SetImmersive(!Chrome.Immersive);

    /// <summary>
    /// 沉浸（窗口内全屏）时面板顶到窗顶：标题行连同收起按钮会落在系统窗口按钮那一条上，
    /// 既重叠又点不动（那一条是非客户区拖动区）。把面板内容整体下移这么高，让标题行落到拖动条外。
    /// 数值与 MainWindow 的 Tall 标题栏（48）一致。
    /// </summary>
    private const double ImmersiveTopInset = 48;

    /// <summary>
    /// 评论面板内容要不要避开顶上那条系统拖动条：只在"沉浸 + 窗口没进真全屏"时要
    /// （真全屏没有那三颗窗口按钮）；其余情况恢复 XAML 里那份 12 的顶距。
    /// </summary>
    private void ApplyCommentPanelTopInset()
    {
        bool underCaptionBar = XamlRoot is not null
            && Chrome.Immersive
            && HostWindow().Presenter.Kind != AppWindowPresenterKind.FullScreen;
        CommentPanel.Padding = underCaptionBar
            ? new Thickness(16, 12 + ImmersiveTopInset, 16, 12)
            : new Thickness(16, 12, 16, 12);
    }

    /// <summary>沉浸态变了：把按钮点亮/收回——图标和外框的实际状态不许说两套话；评论区顶距跟着变。</summary>
    private void OnImmersiveChanged(bool on)
    {
        ImmersiveIcon.Foreground = new SolidColorBrush(
            on ? Microsoft.UI.ColorHelper.FromArgb(255, 254, 44, 85) : Microsoft.UI.ColorHelper.FromArgb(204, 255, 255, 255));
        ApplyCommentPanelTopInset();
    }

    // 「小窗」= 开一个**独立的置顶小窗口**把这条视频挪进去（见 MiniPlayerWindow），不是把主窗口缩小：
    // 主窗口留着不动，画面过去、声音与进度不断；再点一次就把它收回主窗口这一页。

    /// <summary>
    /// 这个控件所在窗口的 AppWindow。
    /// **别再用 `WindowNative.GetWindowHandle(this)`**：那个要求传进去的对象自己实现 IWindowNative，
    /// UserControl 不实现，于是 CsWinRT 建 CCW 时直接抛
    /// InvalidCastException（HResult 0x80004002，IID EECDBF0E-…）——小窗与全屏两颗按钮都会炸在这。
    /// 从内容岛要 AppWindowId 才是任意元素拿窗口的正路。
    /// </summary>
    private AppWindow HostWindow() => AppWindow.GetFromWindowId(XamlRoot.ContentIslandEnvironment.AppWindowId);

    private void OnPipClick(object sender, RoutedEventArgs e) => ToggleMiniPlayer();

    /// <summary>
    /// 小窗：开一个独立的置顶小窗口，把**这条播放器**挪进去渲染（画面过去、声音与进度不断，主窗口留着不动）。
    /// 再点一次（或在小窗里点还原 / Esc）就把它收回来。
    /// 一个 MediaPlayer 同时只能挂一个 MediaPlayerElement，所以这边先把画面松手（松不掉也不算错，小窗一挂上去渲染面自然跟着走）。
    /// </summary>
    private void ToggleMiniPlayer()
    {
        if (_miniWindow is not null)
        {
            _miniWindow.Close(); // 关的时候它的回调会把播放器挂回来
            return;
        }

        MiniPlayerWindow.Current?.Close(); // 一次只留一个小窗：别的舞台开着的那个先收掉

        _miniWindow = new MiniPlayerWindow(
            _player,
            (Author ?? string.Empty).Trim(),
            AppSettings().MiniWidth,
            AppSettings().MiniHeight,
            OnMiniPlayerClosed,
            (width, height) =>
            {
                // 小窗拖过尺寸就记进设置：写的是共享设置 VM，落盘由它防抖（500ms）
                AppSettings().MiniWidth = width;
                AppSettings().MiniHeight = height;
            });

        try
        {
            // MediaPlayer 这个属性在 WinUI 里是只读的，松手得走 SetMediaPlayer；
            // 传 null 就是"从这块元素上摘下来"（小窗那边再挂上去）
            Video.SetMediaPlayer(null!);
        }
        catch (Exception ex)
        {
            MfLog.Write("app: 主窗口松播放器失败（不算致命）：" + ex.Message);
        }

        _miniWindow.Activate();
        UpdatePipIcon();
        Toast("已开小窗：画面挪到屏幕右下角，切页面也不断");
    }

    /// <summary>小窗关了：把播放器挂回主窗口这一页（页面已经在拆就不挂了）。</summary>
    private void OnMiniPlayerClosed()
    {
        _miniWindow = null;
        if (_tornDown)
        {
            return;
        }

        Video.SetMediaPlayer(_player);
        UpdatePipIcon();
    }

    /// <summary>小窗开着就把那颗图标点亮：图标与实际状态不许说两套话。</summary>
    private void UpdatePipIcon() =>
        PipIcon.Foreground = new SolidColorBrush(_miniWindow is null
            ? Microsoft.UI.ColorHelper.FromArgb(204, 255, 255, 255)
            : Microsoft.UI.ColorHelper.FromArgb(255, 254, 44, 85));

    /// <summary>屏上留一句话（2.5 秒后自己收）。</summary>
    private void Toast(string text)
    {
        if (_tornDown)
        {
            return;
        }

        StageToastText.Text = text;
        StageToast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // 点赞/收藏/发评论都是写接口：匿名 ttwid 没有 sessionid，必吃登录墙，等登录态再按真实报文接
    /// <summary>
    /// 点作者头像：带得上 sec_uid 就回抛给宿主开主页。
    /// 带不上就如实说——消息里那张分享卡的报文只有 uid 没有 sec_uid，
    /// 而 <c>user/profile/other</c> 只认 sec_user_id，硬去开只会拿到一个空主页。
    /// </summary>
    private void OnAuthorClick(object sender, RoutedEventArgs e) => OpenAuthor();

    /// <summary>信息对话框里的作者名也可点（官方那行就是入口）；先把对话框收掉，别挡在新页上面。</summary>
    private void OnInfoAuthorTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        try
        {
            VideoInfoDialog.Hide();   // WinUI 的 ContentDialog 没有 IsOpen，没开着时 Hide 也无害
        }
        catch (InvalidOperationException)
        {
        }

        OpenAuthor();
    }

    private void OpenAuthor()
    {
        string secUid = AuthorSecUid ?? string.Empty;
        if (secUid.Length == 0)
        {
            Toast("这条没带上作者的 sec_uid，主页开不了");
            return;
        }

        AuthorOpenRequested?.Invoke(this, new AuthorOpenEventArgs(secUid, Author ?? string.Empty));
    }

    private void OnLikeClick(object sender, RoutedEventArgs e) => _ = ToggleLikeAsync();

    /// <summary>
    /// 点赞：走真接口（commit/item/digg，形状照页面自己的 JS），服务端说成就换实心红心、失败把服务端原话弹出来。
    /// 界面状态只认回执里的 is_digg；本会话点过的记在 _likedVideos 里，换台回来还认得。
    /// </summary>
    private async Task ToggleLikeAsync()
    {
        if (_tornDown || _likeBusy)
        {
            return;
        }

        string id = ItemId ?? string.Empty;
        if (id.Length == 0)
        {
            Toast("这条还没拿到 id，赞不了");
            return;
        }

        if (NoAccount)
        {
            Toast("登录之后才能点赞");
            return;
        }

        _likeBusy = true;
        try
        {
            bool like = !_likedVideos.Contains(id);
            Toast(like ? "正在点赞…" : "正在取消赞…");
            (bool ok, bool liked, string note) = await Douyin.Feed.LikeAsync(id, like, CancellationToken.None);
            if (_tornDown)
            {
                return;
            }

            if (ok)
            {
                if (liked)
                {
                    _likedVideos.Add(id);
                }
                else
                {
                    _likedVideos.Remove(id);
                }
            }

            if (string.Equals(ItemId ?? string.Empty, id, StringComparison.Ordinal))
            {
                RefreshRail();
            }

            Toast(note);
        }
        finally
        {
            _likeBusy = false;
        }
    }

    /// <summary>评论按钮：面板开着的就收起，没开的就展开并取第一页。</summary>
    private void OnCommentClick(object sender, RoutedEventArgs e)
    {
        if (CommentSplit.IsPaneOpen)
        {
            SetCommentPanel(false);
            return;
        }

        SetCommentPanel(true);
    }

    /// <summary>面板右上角那颗 ×：收起。</summary>
    private void OnCommentCloseClick(object sender, RoutedEventArgs e) => SetCommentPanel(false);

    /// <summary>评论面板是否展开（宿主处理滚轮时判断落点用）。</summary>
    public bool IsCommentsPaneOpen => CommentSplit.IsPaneOpen;

    /// <summary>评论面板展开宽度：面板贴着舞台右缘，落在这一条上的滚轮归评论列表。</summary>
    public double CommentsPaneWidth => CommentSplit.OpenPaneLength;

    /// <summary>外部宿主（分享卡详情页）进门就把评论面板展开：用户点分享卡多半是来看评论的。</summary>
    public void OpenCommentsPanel() => SetCommentPanel(true);

    /// <summary>评论面板开合真的变了：宿主拿它把开合状态持久化到另一页（推荐页两页轮流上场）。</summary>
    public event EventHandler<bool>? CommentsPaneChanged;

    /// <summary>
    /// 分享卡详情：来源评论按 cid 置顶——评论列表里找到那一行后移到最上面、卡头显示“置顶”。
    /// 这一行仍是普通评论卡（头像/正文/图/赞/回复都在），只是位置和标记不同。
    /// 接口没回这条（或还没取到）时先用分享消息自带的内容兜底摆一条，不会把分享的评论弄丢。
    /// 换条时宿主重新喂一次，没有来源评论的条目用 <see cref="ClearPinnedComment"/>。
    /// </summary>
    public void SetPinnedComment(string commentId, string author, string text, string imageUrl)
    {
        string id = commentId ?? string.Empty;
        _pinnedAuthor = author ?? string.Empty;
        _pinnedText = text ?? string.Empty;
        _pinnedImageUrl = imageUrl ?? string.Empty;
        if (!string.Equals(_pinnedCommentId, id, StringComparison.Ordinal))
        {
            RemovePinnedFallbackRow();
            UnpinCommentRow();
            _pinnedCommentId = id;
        }

        ApplyPinnedCommentRow();
        if (CommentSplit.IsPaneOpen)
        {
            _ = EnsurePinnedCommentLoadedAsync();
        }
    }

    public void ClearPinnedComment()
    {
        _pinnedCommentId = string.Empty;
        _pinnedAuthor = string.Empty;
        _pinnedText = string.Empty;
        _pinnedImageUrl = string.Empty;
        RemovePinnedFallbackRow();
        UnpinCommentRow();
    }

    /// <summary>
    /// 列表里找到置顶那条就移到最上面并打上标记，同时把兜底行撤掉；
    /// 还没取到就保证兜底行在最上面（见 LoadCommentsAsync 每页落地后调用）。
    /// </summary>
    private void ApplyPinnedCommentRow()
    {
        if (_pinnedCommentId.Length == 0)
        {
            return;
        }

        ViewModels.CommentRowViewModel? hit = CommentRows.FirstOrDefault(
            r => !ReferenceEquals(r, _pinnedFallbackRow) && r.Id == _pinnedCommentId);
        if (hit is null)
        {
            ShowPinnedFallbackRow();
            return;
        }

        RemovePinnedFallbackRow();
        int at = CommentRows.IndexOf(hit);
        if (at > 0)
        {
            CommentRows.Move(at, 0);
            CommentScroll.ChangeView(null, 0, null, true);
        }

        hit.SetPinned(true);
    }

    /// <summary>接口里没有这条时的兜底：用分享消息自带的作者/正文/图拼一条普通评论行，置顶显示。</summary>
    private void ShowPinnedFallbackRow()
    {
        if (_pinnedFallbackRow is not null || _pinnedCommentId.Length == 0
            || CommentRows.Any(r => r.Id == _pinnedCommentId))
        {
            return;
        }

        CommentImageItem[] images = _pinnedImageUrl.Length > 0
            ? [new CommentImageItem(_pinnedImageUrl, _pinnedImageUrl, 0, 0)]
            : [];

        var row = new ViewModels.CommentRowViewModel(
            _pinnedAuthor.Length > 0 ? _pinnedAuthor : "抖音用户",
            _pinnedText,
            string.Empty,
            _pinnedCommentId,
            string.Empty,
            0,
            images,
            null,
            0,
            false);
        row.SetPinned(true);
        _pinnedFallbackRow = row;
        CommentRows.Insert(0, row);
    }

    private void RemovePinnedFallbackRow()
    {
        if (_pinnedFallbackRow is null)
        {
            return;
        }

        CommentRows.Remove(_pinnedFallbackRow);
        _pinnedFallbackRow = null;
    }

    private void UnpinCommentRow()
    {
        foreach (ViewModels.CommentRowViewModel row in CommentRows)
        {
            row.SetPinned(false);
        }
    }

    /// <summary>置顶的那条不在第一页：顺着续页往下找（最多 8 页），找到就移上去。</summary>
    private async Task EnsurePinnedCommentLoadedAsync()
    {
        string target = _pinnedCommentId;
        string item = ItemId ?? string.Empty;
        if (target.Length == 0 || item.Length == 0)
        {
            return;
        }

        int loads = 0;
        for (int attempt = 0; attempt < 40 && loads < 3; attempt++)
        {
            if (_tornDown || !string.Equals(_pinnedCommentId, target, StringComparison.Ordinal)
                || !string.Equals(ItemId ?? string.Empty, item, StringComparison.Ordinal))
            {
                return;
            }

            if (_commentLoading)
            {
                await Task.Delay(200);
                continue;
            }

            if (CommentRows.Any(r => !ReferenceEquals(r, _pinnedFallbackRow) && r.Id == target))
            {
                ApplyPinnedCommentRow();
                return;
            }

            if (!_commentHasMore)
            {
                return;
            }

            _ = LoadCommentsAsync(reset: false);
            loads++;
            await Task.Delay(450);
        }
    }

    /// <summary>宿主换页时把评论面板收起来（双舞台保持开合状态一致）。</summary>
    public void CloseCommentsPanel() => SetCommentPanel(false);

    /// <summary>
    /// 面板开合。开＝每次都从顶上开始看；里面要是别的视频的评论（或一条没有），就先取第一页。
    /// 关＝留着已经取到的行，下次开还是这条就直接看（换条时 SwitchItem 会把账面清掉）。
    /// 开合与挤压动画交给 SplitView 内联模式（2026-10-17 按用户要求换成 SplitView.Pane）：
    /// 面板从右滑入、内容整体让位（模板自带 0.2s 样条），不再手改 Margin。
    /// </summary>
    private void SetCommentPanel(bool open)
    {
        if (_tornDown)
        {
            return;
        }

        if (open == CommentSplit.IsPaneOpen)
        {
            // 缓存页回场/换条后重开：面板还开着，但账面是上一条的（SwitchItem 已把行清空），重取第一页
            if (open && !string.Equals(_commentFor, ItemId ?? string.Empty, StringComparison.Ordinal))
            {
                CommentScroll.ChangeView(null, 0, null, true);
                _commentCursor = 0;
                _commentHasMore = true;
                _ = LoadCommentsAsync(reset: true);
            }

            return;
        }

        CommentSplit.IsPaneOpen = open;
        if (open)
        {
            CommentScroll.ChangeView(null, 0, null, true);
            if (!string.Equals(_commentFor, ItemId ?? string.Empty, StringComparison.Ordinal))
            {
                _commentCursor = 0;
                _commentHasMore = true;
                _ = LoadCommentsAsync(reset: true);
            }

            _ = EnsurePinnedCommentLoadedAsync();
        }

        CommentsPaneChanged?.Invoke(this, open);
    }

    /// <summary>滚到底（留 40px 提前量）且还有下一页就续页；ViewChanged 一页里连发，靠 _commentLoading 挡重入。</summary>
    private void OnCommentScrollChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_commentLoading || !_commentHasMore || CommentRows.Count == 0)
        {
            return;
        }

        double room = CommentScroll.ScrollableHeight - CommentScroll.VerticalOffset - CommentScroll.ViewportHeight;
        if (room < 40)
        {
            _ = LoadCommentsAsync(reset: false);
        }
    }

    /// <summary>
    /// 取评论：reset=true 取第一页（行先清掉），false 顺着 <c>_commentCursor</c> 续页。
    /// 游标没往前走的应答当作"到底了"（防同一页取起来没完）；尾部那行字如实写这一页的结果。
    /// </summary>
    private async Task LoadCommentsAsync(bool reset)
    {
        string id = ItemId ?? string.Empty;
        if (_tornDown || id.Length == 0 || (!reset && _commentLoading))
        {
            return;
        }

        int gen = ++_commentGen;        // 换条/重开会翻一发，老的那发回来对不上就当没发生
        _commentLoading = true;
        if (reset)
        {
            CommentRows.Clear();
            _pinnedFallbackRow = null;
            ShowPinnedFallbackRow();   // 置顶的来源评论先摆上，不等接口（接口没有这条也不丢）
            ClearReplyTarget();
        }

        if (reset)
        {
            SetCommentStatus("正在取评论…");
        }

        try
        {
            long asked = _commentCursor;
            Task<IReadOnlyList<EmojiItem>> emojiTask = EnsureEmojiAsync();
            // 签名与 JSON 解析都是同步 CPU 活；从 UI 直接 await 会一路续在 UI 线程上，整发放线程池。
            CommentPage page = await Task.Run(
                () => Douyin.Feed.CommentsAsync(id, asked, 20, CancellationToken.None));
            if (_tornDown || gen != _commentGen || !string.Equals(ItemId ?? string.Empty, id, StringComparison.Ordinal))
            {
                return;
            }

            // 评论/回复正文要把 [码] 铺成官方表情图；表没到就晚一点再铺这一页
            await emojiTask;

            foreach (CommentItem c in page.Items)
            {
                CommentRows.Add(new ViewModels.CommentRowViewModel(
                    c.Author.Length > 0 ? c.Author : "抖音用户",
                    c.Text,
                    CommentMeta(c.CreateTime),
                    c.Id,
                    c.Avatar,
                    c.ReplyTotal,
                    c.Images,
                    c.Sticker,
                    c.Digg,
                    c.Liked));
            }

            _commentFor = id;
            _commentCursor = page.Cursor > asked ? page.Cursor : asked;
            _commentHasMore = page.HasMore && page.Items.Count > 0 && page.Cursor > asked;
            _commentTotal = page.Total;
            ApplyPinnedCommentRow();   // 置顶那条可能正好在这一页落地
            ApplyCommentTotal();
            SetCommentStatus(CommentRows.Count == 0 ? "这条还没评论" : string.Empty);
        }
        finally
        {
            if (gen == _commentGen)
            {
                _commentLoading = false;
            }
        }
    }

    /// <summary>评论尾注：相对时间（赞数与"赞过没有"在尾注右边那颗心+数字里）。</summary>
    private static string CommentMeta(int createTime) => RelativeTime.Format(createTime);

    /// <summary>
    /// 一级评论底下那颗"展开N条回复"：再点一次收起。
    /// 第一次展开才去取（20 条一页）；回空/失败给重试提示——别把空当成"没有回复"。
    /// （2026-10-17 以前一直回空的根因是 a_bogus 50 格模板旧了，已在画像里更新，见 NativeFeed.CommentRepliesAsync。）
    /// </summary>
    private async void OnReplyExpandClick(object sender, RoutedEventArgs e)
    {
        if (_tornDown || (sender as FrameworkElement)?.DataContext is not ViewModels.CommentRowViewModel row)
        {
            return;
        }

        if (row.IsExpanded)
        {
            row.CollapseReplies();
            return;
        }

        string id = ItemId ?? string.Empty;
        if (id.Length == 0 || row.Id.Length == 0)
        {
            return;
        }

        if (row.Replies.Count > 0)
        {
            row.CompleteRepliesLoad();   // 上一轮已经取到了，收起后再开直接摆回来
            return;
        }

        if (!row.BeginRepliesLoad())
        {
            return;
        }

        try
        {
            ReplyPage page = await Task.Run(
                () => Douyin.Feed.CommentRepliesAsync(id, row.Id, 0, 20, CancellationToken.None));
            if (_tornDown || !CommentRows.Contains(row))
            {
                return;   // 期间换了条/清了列表：这一发回来没用了
            }

            row.ReplyItems.Clear();
            row.ReplyItems.AddRange(page.Items);
            FillReplyRows(row);
            row.SetReplyPage(page.Cursor, page.HasMore && page.Items.Count > 0, page.Total, loading: false);

            if (row.Replies.Count == 0)
            {
                row.FailRepliesLoad();
                Toast("二级评论没取到（服务端回空），再点一次试试");
                return;
            }

            row.CompleteRepliesLoad();
        }
        catch (Exception ex)
        {
            row.FailRepliesLoad();
            MfLog.Write("app: 取二级评论出错 " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// 接口把回复按时间平铺成一串（三级/更深也在里面），这里按 <c>reply_to_reply_id</c> 还原成树：
    /// 直接回复进一级评论的 <see cref="ViewModels.CommentRowViewModel.Replies"/>，
    /// 已展开的回复把子回复放进自己的 <see cref="ViewModels.CommentReplyRowViewModel.Children"/>，
    /// 界面据此把子卡嵌进父卡。父不在已取回的页里（被翻页截断）→ 当作这一层的根；成环的脏数据靠 visited 兜底。
    /// </summary>
    private void FillReplyRows(ViewModels.CommentRowViewModel row)
    {
        row.Replies.Clear();
        var byId = new Dictionary<string, ReplyItem>(StringComparer.Ordinal);
        foreach (ReplyItem r in row.ReplyItems)
        {
            if (r.Id.Length > 0)
            {
                byId[r.Id] = r;
            }
        }

        var children = new Dictionary<string, List<ReplyItem>>(StringComparer.Ordinal);
        var roots = new List<ReplyItem>();
        foreach (ReplyItem r in row.ReplyItems)
        {
            if (r.ReplyToId.Length > 0 && !string.Equals(r.ReplyToId, r.Id, StringComparison.Ordinal) && byId.ContainsKey(r.ReplyToId))
            {
                if (!children.TryGetValue(r.ReplyToId, out List<ReplyItem>? kids))
                {
                    children[r.ReplyToId] = kids = [];
                }

                kids.Add(r);
            }
            else
            {
                roots.Add(r);
            }
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Walk(ReplyItem r, ObservableCollection<ViewModels.CommentReplyRowViewModel>? target)
        {
            if (!visited.Add(r.Id))
            {
                return;
            }

            if (target is null)
            {
                // 父级折叠着：整棵子树只标"已到达"，别让末尾补漏循环把它们当根铺出来
                if (children.TryGetValue(r.Id, out List<ReplyItem>? hidden))
                {
                    foreach (ReplyItem k in hidden)
                    {
                        Walk(k, null);
                    }
                }

                return;
            }

            bool hasChildren = children.TryGetValue(r.Id, out List<ReplyItem>? kids) && kids.Count > 0;
            bool expanded = hasChildren && row.ExpandedReplyIds.Contains(r.Id);

            string replyTo = string.Empty;
            if (r.ReplyToId.Length > 0 && !string.Equals(r.ReplyToId, r.Id, StringComparison.Ordinal)
                && byId.TryGetValue(r.ReplyToId, out ReplyItem? parent) && parent.Author.Length > 0
                && !r.Text.Contains("@" + parent.Author, StringComparison.Ordinal))
            {
                replyTo = "回复 @" + parent.Author;
            }

            // 重铺前先看本地点过的状态（用户赞过的回复不能因为展开/收起又变回空心）
            bool likedState = r.Liked;
            int diggState = r.Digg;
            if (row.ReplyLikeState.TryGetValue(r.Id, out (bool Liked, int Digg) saved))
            {
                likedState = saved.Liked;
                diggState = saved.Digg;
            }

            var vm = new ViewModels.CommentReplyRowViewModel(
                r.Author.Length > 0 ? r.Author : "抖音用户",
                r.Text,
                CommentMeta(r.CreateTime),
                r.Id,
                row,
                r.Avatar,
                replyTo,
                hasChildren,
                expanded,
                ToggleReplyRow,
                r.Images,
                r.Sticker,
                LikeReplyRow,
                diggState,
                likedState,
                ReplyToRow);
            target.Add(vm);

            if (hasChildren)
            {
                foreach (ReplyItem k in kids!)
                {
                    Walk(k, expanded ? vm.Children : null);
                }
            }
        }

        foreach (ReplyItem r in roots)
        {
            Walk(r, row.Replies);
        }

        foreach (ReplyItem r in row.ReplyItems)
        {
            Walk(r, row.Replies);   // 成环/父缺失的漏网项：至少铺成根（visited 防重）
        }
    }

    /// <summary>回复卡上的"展开回复"/"收起回复"：只动这一支的展开状态，重铺后卡片按层嵌套。</summary>
    private void ToggleReplyRow(ViewModels.CommentReplyRowViewModel reply)
    {
        if (_tornDown)
        {
            return;
        }

        ViewModels.CommentRowViewModel row = reply.Owner;
        if (!row.ExpandedReplyIds.Remove(reply.Id))
        {
            row.ExpandedReplyIds.Add(reply.Id);
        }

        FillReplyRows(row);
    }

    /// <summary>
    /// "加载更多回复"：顺着回复游标续一页（20 条），追加进 <see cref="ViewModels.CommentRowViewModel.ReplyItems"/> 后整棵树重铺。
    /// 游标没往前走就当作到底，别让按钮无限点。
    /// </summary>
    private async void OnMoreRepliesClick(object sender, RoutedEventArgs e)
    {
        if (_tornDown || (sender as FrameworkElement)?.DataContext is not ViewModels.CommentRowViewModel row || !row.IsExpanded)
        {
            return;
        }

        string id = ItemId ?? string.Empty;
        if (id.Length == 0 || row.Id.Length == 0 || !_replyMoreLoading.Add(row.Id))
        {
            return;
        }

        long asked = row.ReplyCursor;
        row.SetReplyPage(asked, true, row.ReplyServerTotal, loading: true);
        try
        {
            ReplyPage page = await Task.Run(
                () => Douyin.Feed.CommentRepliesAsync(id, row.Id, asked, 20, CancellationToken.None));
            if (_tornDown || !CommentRows.Contains(row))
            {
                return;
            }

            HashSet<string> known = row.ReplyItems.Select(static r => r.Id).ToHashSet(StringComparer.Ordinal);
            int added = 0;
            foreach (ReplyItem reply in page.Items)
            {
                if (reply.Id.Length > 0 && known.Add(reply.Id))
                {
                    row.ReplyItems.Add(reply);
                    added++;
                }
            }

            FillReplyRows(row);
            bool advanced = page.Cursor > asked;
            bool keepMore = !page.Ok || (page.HasMore && added > 0 && advanced);
            row.SetReplyPage(advanced ? page.Cursor : asked, keepMore, page.Total, loading: false);
            if (added == 0)
            {
                Toast(page.Ok ? "没有更多回复了" : "没取到更多回复，再点一次试试");
            }
        }
        catch (Exception ex)
        {
            row.SetReplyPage(asked, true, row.ReplyServerTotal, loading: false);
            MfLog.Write("app: 取更多回复出错 " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            _replyMoreLoading.Remove(row.Id);
        }
    }

    private void OnCommentSendClick(object sender, RoutedEventArgs e) => _ = SendCommentAsync();

    /// <summary>
    /// 一级评论那颗心：先本地翻转（乐观更新），写接口失败再翻回来。
    /// 评论点赞**立刻生效**（不需要视频点赞那种"刷子"），见 <see cref="NativeFeed.CommentDiggAsync"/>。
    /// </summary>
    private async void OnCommentLikeClick(object sender, RoutedEventArgs e)
    {
        if (_tornDown || (sender as FrameworkElement)?.DataContext is not ViewModels.CommentRowViewModel row)
        {
            return;
        }

        string id = ItemId ?? string.Empty;
        if (id.Length == 0 || row.Id.Length == 0 || !_commentLikeBusy.Add(row.Id))
        {
            return;
        }

        bool like = !row.Liked;
        row.ApplyLike(like);
        try
        {
            (bool ok, string note) = await Task.Run(
                () => Douyin.Feed.CommentDiggAsync(id, row.Id, like, CancellationToken.None));
            if (_tornDown)
            {
                return;
            }

            if (!ok)
            {
                row.ApplyLike(!like);
                Toast(note);
            }
        }
        catch (Exception ex)
        {
            row.ApplyLike(!like);
            MfLog.Write("app: 评论点赞抛了 " + ex.GetType().Name + " · " + ex.Message);
            Toast("点赞没发出去（网络？）");
        }
        finally
        {
            _commentLikeBusy.Remove(row.Id);
        }
    }

    /// <summary>回复那颗心：与一级同一条写链（comment/digg 对任意 cid 都认），失败同样原地翻回来。</summary>
    private async void LikeReplyRow(ViewModels.CommentReplyRowViewModel reply)
    {
        string id = ItemId ?? string.Empty;
        if (_tornDown || id.Length == 0 || reply.Id.Length == 0 || !_commentLikeBusy.Add(reply.Id))
        {
            return;
        }

        bool like = !reply.Liked;
        reply.ApplyLike(like);
        reply.Owner.ReplyLikeState[reply.Id] = (reply.Liked, reply.DiggValue);
        try
        {
            (bool ok, string note) = await Task.Run(
                () => Douyin.Feed.CommentDiggAsync(id, reply.Id, like, CancellationToken.None));
            if (_tornDown)
            {
                return;
            }

            if (!ok)
            {
                reply.ApplyLike(!like);
                reply.Owner.ReplyLikeState[reply.Id] = (reply.Liked, reply.DiggValue);
                Toast(note);
            }
        }
        catch (Exception ex)
        {
            reply.ApplyLike(!like);
            reply.Owner.ReplyLikeState[reply.Id] = (reply.Liked, reply.DiggValue);
            MfLog.Write("app: 回复点赞抛了 " + ex.GetType().Name + " · " + ex.Message);
            Toast("点赞没发出去（网络？）");
        }
        finally
        {
            _commentLikeBusy.Remove(reply.Id);
        }
    }

    /// <summary>点了一级评论的"回复"：把输入框切到"回复 @作者"，发送时带 reply_id。</summary>
    private void OnCommentReplyClick(object sender, RoutedEventArgs e)
    {
        if (_tornDown || (sender as FrameworkElement)?.DataContext is not ViewModels.CommentRowViewModel row)
        {
            return;
        }

        if (row.Id.Length == 0)
        {
            Toast("这条评论还没拿到 id，回复不了");
            return;
        }

        _replyRootRow = row;
        _replyToReply = null;
        _replyToName = row.Author;
        UpdateReplyHint();
        CommentInput.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    /// <summary>点了回复卡的"回复"：输入框切到"回复 @这条回复的作者"，发送时 reply_id=楼根 + reply_to_reply_id=这条。</summary>
    private void ReplyToRow(ViewModels.CommentReplyRowViewModel reply)
    {
        if (_tornDown)
        {
            return;
        }

        if (reply.Id.Length == 0)
        {
            Toast("这条回复还没拿到 id，回复不了");
            return;
        }

        _replyRootRow = reply.Owner;
        _replyToReply = reply;
        _replyToName = reply.Author;
        UpdateReplyHint();
        CommentInput.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    /// <summary>取消回复，回到发一级评论。</summary>
    private void OnCancelReplyClick(object sender, RoutedEventArgs e) => ClearReplyTarget();

    private void ClearReplyTarget()
    {
        _replyRootRow = null;
        _replyToReply = null;
        _replyToName = string.Empty;
        UpdateReplyHint();
    }

    private void UpdateReplyHint()
    {
        bool hasTarget = _replyRootRow is not null;
        CommentReplyToPanel.Visibility = hasTarget ? Visibility.Visible : Visibility.Collapsed;
        CommentReplyToText.Text = hasTarget ? $"回复 @{_replyToName}" : string.Empty;
        CommentInput.PlaceholderText = hasTarget ? $"回复 {_replyToName}…" : "发一条评论…";
    }

    /// <summary>点了评论里的图片/表情包（一级卡上的按钮）：按原图地址弹大图。</summary>
    private void OnCommentImageClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url } && url.Length > 0)
        {
            ShowCommentImage(url);
        }
    }

    /// <summary>回复卡（<see cref="ReplyCardControl"/>）里的图：卡片把原图地址抛上来。</summary>
    private void OnCommentImageTapped(object? sender, string url) => ShowCommentImage(url);

    /// <summary>弹大图：原图可能是几 MB 的长截图，这里才真正去取（缩略图早就画在卡里了）。</summary>
    private void ShowCommentImage(string url)
    {
        if (_tornDown || url.Length == 0)
        {
            return;
        }

        CommentImageViewerImage.Source = new BitmapImage(new Uri(url));
        CommentImageViewer.Visibility = Visibility.Visible;
    }

    /// <summary>点任意处/右上 × 关掉大图；顺手把位图放开（几百 KB 到几 MB，不必端着）。</summary>
    private void OnCommentImageViewerTapped(object sender, TappedRoutedEventArgs e) => HideCommentImage();

    private void OnCommentImageViewerCloseClick(object sender, RoutedEventArgs e) => HideCommentImage();

    private void HideCommentImage()
    {
        CommentImageViewer.Visibility = Visibility.Collapsed;
        CommentImageViewerImage.Source = null;
    }

    /// <summary>Enter 直接发：单行输入框里 Enter 没有别的含义。</summary>
    private void OnCommentInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _ = SendCommentAsync();
        }
    }

    /// <summary>
    /// 发评论：走 comment/publish 写链。发成功把这条插到列表头上（最新在前），不重取第一页。
    /// 回执绿只说明"这一发被接受了"——真正的到位以这条出现在服务端列表里为准；
    /// 所以状态行只如实写"发出去了"，等下次重开面板重取时对账。
    /// </summary>
    private async Task SendCommentAsync()
    {
        if (_tornDown || _commentSending)
        {
            return;
        }

        string id = ItemId ?? string.Empty;
        string text = CommentBoxText().Trim();
        if (id.Length == 0)
        {
            Toast("这条还没拿到 id，评论发不了");
            return;
        }

        if (text.Length == 0)
        {
            return;
        }

        if (text.Length > 200)
        {
            Toast("评论最多 200 字");
            return;
        }

        if (NoAccount)
        {
            Toast("登录之后才能发评论");
            return;
        }

        string replyId = _replyRootRow?.Id ?? string.Empty;
        string replyToId = _replyToReply?.Id ?? string.Empty;

        _commentSending = true;
        CommentSendButton.IsEnabled = false;
        try
        {
            (bool ok, string cid, string note) = await Task.Run(
                () => Douyin.Feed.CommentPublishAsync(id, text, CancellationToken.None, replyId, replyToId));
            if (_tornDown)
            {
                return;
            }

            if (ok)
            {
                CommentInput.Document.SetText(TextSetOptions.None, string.Empty);
                InsertCommentLocally(text, cid);
                _commentFor = id;
                if (_commentTotal >= 0)
                {
                    _commentTotal++;
                    ApplyCommentTotal();
                }

                SetCommentStatus(string.Empty);
            }

            Toast(note);
        }
        finally
        {
            CommentSendButton.IsEnabled = true;
            _commentSending = false;
        }
    }

    /// <summary>
    /// 发成功后本地先插一条：一级评论插到列表头；回复插到对应一级评论的 Replies 头（未展开则展开）。
    /// 这样用户点了"回复"后立刻能在目标楼层看到自己刚发的那条。
    /// </summary>
    private void InsertCommentLocally(string text, string cid)
    {
        string meta = "刚发的" + (cid.Length > 0 ? " · " + cid : string.Empty);
        if (_replyRootRow is null)
        {
            CommentRows.Insert(0, new ViewModels.CommentRowViewModel("我", text, meta));
            return;
        }

        ViewModels.CommentRowViewModel root = _replyRootRow;
        string replyToLabel = _replyToReply is null
            ? (root.Author.Length > 0 ? $"回复 @{root.Author}" : string.Empty)
            : (_replyToName.Length > 0 ? $"回复 @{_replyToName}" : string.Empty);

        var vm = new ViewModels.CommentReplyRowViewModel(
            "我", text, meta, cid, root,
            avatarUrl: "",
            replyTo: replyToLabel,
            hasChildren: false,
            isExpanded: false,
            toggle: ToggleReplyRow,
            images: null,
            sticker: null,
            like: LikeReplyRow,
            digg: 0,
            liked: false,
            reply: ReplyToRow);

        root.IncrementReplyTotal();
        if (!root.IsExpanded)
        {
            root.CompleteRepliesLoad();
        }

        root.Replies.Insert(0, vm);
        ClearReplyTarget();
    }

    /// <summary>
    /// 收藏按钮的悬停面板：指针进按钮就开；直接点按钮仍走默认收藏（面板里没提交的勾选在这条路上作废）。
    /// </summary>
    private void OnFavoritePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _collectPointerInButton = true;
        _collectHoverTimer.Stop();
        ShowCollectFlyout();
    }

    private void OnFavoritePointerExited(object sender, PointerRoutedEventArgs e)
    {
        _collectPointerInButton = false;
        if (_collectFlyout.IsOpen)
        {
            _collectHoverTimer.Start();
        }
    }

    private void OnCollectFlyoutPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _collectPointerInFlyout = true;
        _collectHoverTimer.Stop();
    }

    private void OnCollectFlyoutPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _collectPointerInFlyout = false;
        if (_collectFlyout.IsOpen)
        {
            _collectHoverTimer.Start();
        }
    }

    private void ShowCollectFlyout()
    {
        if (_tornDown || NoAccount)
        {
            return;
        }

        string id = ItemId ?? string.Empty;
        if (id.Length == 0)
        {
            return;
        }

        if (!_collectFlyout.IsOpen)
        {
            _collectClosingSnapshot = null;   // 上一回 Hide 没等到 Closed 就留下的陈快照，别带进这一轮
        }

        if (_collectFlyout.IsOpen)
        {
            if (string.Equals(_collectFlyoutVideoId, id, StringComparison.Ordinal))
            {
                return;
            }

            // 换了一条：先把上一轮面板收掉（Closed 里会把它的勾选安排写入）
            _collectClosingVideoId = _collectFlyoutVideoId;
            _collectClosingSnapshot = _collectContent.SelectedFolders;
            _collectFlyout.Hide();
        }

        // 350ms 防抖窗口里又回到这条：撤掉待写快照，勾选留着让用户接着改。
        // 别的视频的待写不动，它们自己的防抖照常走。
        int resumed = _collectWrites.FindIndex(w => string.Equals(w.VideoId, id, StringComparison.Ordinal));
        if (resumed >= 0)
        {
            _collectWrites.RemoveAt(resumed);
            if (_collectWrites.Count == 0)
            {
                _collectFlushTimer.Stop();
            }
        }
        else
        {
            _collectContent.ResetSelection();
            if (_collectFolderCache.Count > 0)
            {
                _collectContent.SetFolders(_collectFolderCache);
            }
            else
            {
                _collectContent.SetLoading();
            }
        }

        _collectFlyoutVideoId = id;
        _collectFlyout.ShowAt(FavoriteButton, new FlyoutShowOptions { ShowMode = FlyoutShowMode.Transient });
        _ = LoadCollectFoldersAsync(force: false);
    }

    /// <summary>面板关掉：把勾选快照排进待写队列，350ms 内没有再打开才真正写。</summary>
    private void OnCollectFlyoutClosed(object? sender, object e)
    {
        string closingId = _collectClosingVideoId;
        _collectClosingVideoId = string.Empty;
        _collectPointerInFlyout = false;
        _collectHoverTimer.Stop();
        if (_tornDown)
        {
            return;
        }

        // 程序化收起时用收面板那一刻的快照；用户自己关（悬停离开/点别处）时快照为空，读当前勾选
        IReadOnlyList<CollectFolder> selected = _collectClosingSnapshot ?? _collectContent.SelectedFolders;
        _collectClosingSnapshot = null;

        string vid = closingId.Length > 0 ? closingId : _collectFlyoutVideoId;
        if (vid.Length == 0)
        {
            return;
        }

        // 同一条重开又关：新的勾选覆盖旧的快照
        _collectWrites.RemoveAll(w => string.Equals(w.VideoId, vid, StringComparison.Ordinal));
        if (selected.Count > 0)
        {
            _collectWrites.Add(new CollectWrite(vid, selected.ToList()));
        }

        _collectFlushTimer.Stop();
        if (_collectWrites.Count > 0)
        {
            _collectFlushTimer.Start();
        }
    }

    private async Task LoadCollectFoldersAsync(bool force)
    {
        string id = _collectFlyoutVideoId;
        int gen = ++_collectLoadGen;
        try
        {
            IReadOnlyList<CollectFolder> folders = await Douyin.Feed.CollectsFoldersAsync(force, CancellationToken.None);
            if (_tornDown || gen != _collectLoadGen
                || !string.Equals(id, _collectFlyoutVideoId, StringComparison.Ordinal))
            {
                return;
            }

            _collectFolderCache = folders;
            if (_collectFlyout.IsOpen && _collectContent.SelectedCount == 0)
            {
                _collectContent.SetFolders(folders);
            }
        }
        catch (Exception ex)
        {
            MfLog.Write("app: 收藏夹读取失败 " + ex);
            if (_tornDown || gen != _collectLoadGen || !_collectFlyout.IsOpen || _collectFolderCache.Count > 0)
            {
                return;
            }

            _collectContent.SetError("收藏夹没读回来：" + ex.Message);
        }
    }

    /// <summary>防抖到点：把队列里的待写逐条落下去（写入仍是串行的）。</summary>
    private async Task FlushPendingCollectWritesAsync()
    {
        if (_tornDown || _collectWrites.Count == 0)
        {
            return;
        }

        CollectWrite[] batch = _collectWrites.ToArray();
        _collectWrites.Clear();
        foreach (CollectWrite write in batch)
        {
            if (_tornDown)
            {
                return;
            }

            await FlushCollectWriteAsync(write.VideoId, write.Folders);
        }
    }

    /// <summary>一条待写落地：先确认视频被收藏（没收藏的先发 aweme/collect），再逐个夹发 move。</summary>
    private async Task FlushCollectWriteAsync(string id, IReadOnlyList<CollectFolder> folders)
    {
        if (_tornDown || id.Length == 0 || folders.Count == 0)
        {
            return;
        }

        if (NoAccount)
        {
            Toast("登录之后才能收藏");
            return;
        }

        await _collectWriteGate.WaitAsync();
        try
        {
            if (!_collectedVideos.Contains(id))
            {
                (bool ok, string note) = await Douyin.Feed.CollectAsync(id, true, CancellationToken.None);
                if (_tornDown)
                {
                    return;
                }

                if (!ok)
                {
                    Toast(note);
                    return;
                }

                _collectedVideos.Add(id);
                if (string.Equals(ItemId ?? string.Empty, id, StringComparison.Ordinal))
                {
                    RefreshRail();
                }
            }

            (_, string moveNote) = await Douyin.Feed.CollectsMoveAsync(id, folders, CancellationToken.None);
            if (_tornDown)
            {
                return;
            }

            Toast(moveNote);
        }
        catch (Exception ex)
        {
            MfLog.Write("app: 加入收藏夹抛了 " + ex.GetType().Name + " · " + ex.Message);
            Toast("加入收藏夹没发出去（网络？）");
        }
        finally
        {
            _collectWriteGate.Release();
        }
    }

    private void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        // 直接点星 = 默认收藏夹（和以前一样）。这条路上把当前视频还没落地的"加入收藏夹"撤掉，
        // 免得"点星取消收藏"和"写入收藏夹"两发相反动作交叉。
        string current = ItemId ?? string.Empty;
        _collectWrites.RemoveAll(w => string.Equals(w.VideoId, current, StringComparison.Ordinal));
        if (_collectWrites.Count == 0)
        {
            _collectFlushTimer.Stop();
        }

        _collectClosingSnapshot = null;   // 这条路上勾选作废，Closed 不该再拿快照
        _collectContent.ResetSelection();
        if (_collectFlyout.IsOpen)
        {
            _collectFlyout.Hide();
        }

        _ = ToggleCollectAsync();
    }

    /// <summary>
    /// 收藏：走真接口（aweme/collect，形状照页面 JS）。判据只有 status_code——回执不带状态字段，
    /// 所以"收上了没有"以服务端的 status_code 为准，界面上的星星跟着本地那本账走。
    /// </summary>
    private async Task ToggleCollectAsync()
    {
        if (_tornDown)
        {
            return;
        }

        string id = ItemId ?? string.Empty;
        if (id.Length == 0)
        {
            Toast("这条还没拿到 id，收藏不了");
            return;
        }

        if (NoAccount)
        {
            Toast("登录之后才能收藏");
            return;
        }

        bool collect = !_collectedVideos.Contains(id);
        Toast(collect ? "正在收藏…" : "正在取消收藏…");

        // 与"加入收藏夹"的写链串行：收藏这族有滞后一拍，交叉发会互相刷
        await _collectWriteGate.WaitAsync();
        try
        {
            // aweme_type 照页面 JS 的默认给普通视频（0）；卡片上是图文时服务端会自己挑毛病，
            // 到时候把类型从报文里带出来再传（见 VideoCard）
            (bool ok, string note) = await Douyin.Feed.CollectAsync(id, collect, CancellationToken.None);
            if (_tornDown)
            {
                return;
            }

            if (ok)
            {
                if (collect)
                {
                    _collectedVideos.Add(id);
                }
                else
                {
                    _collectedVideos.Remove(id);
                }
            }

            if (string.Equals(ItemId ?? string.Empty, id, StringComparison.Ordinal))
            {
                RefreshRail();
            }

            Toast(note);
        }
        finally
        {
            _collectWriteGate.Release();
        }
    }

    /// <summary>
    /// 分享面板：应用内会话（发文本链）、复制链接、Windows 系统分享。
    /// 站点没有普通视频"取分享链接"的接口——网页端也是前端拼 <c>https://www.douyin.com/video/&lt;aweme_id&gt;</c>。
    /// </summary>
    private IReadOnlyList<ViewModels.ShareTargetRow> _shareTargets = Array.Empty<ViewModels.ShareTargetRow>();
    private Task<IReadOnlyList<ViewModels.ShareTargetRow>>? _shareTargetsLoading;
    private DateTimeOffset _shareTargetsAt;
    private string _shareTargetsError = string.Empty;

    /// <summary>悬停就先把会话列表拉上（NativeFeed 里还有 2 分钟缓存），点开面板不再干等。</summary>
    private void OnRepostPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_tornDown || !Douyin.Feed.IsLoggedIn || _shareTargets.Count > 0)
        {
            return;
        }

        _ = LoadShareTargetsAsync();
    }

    private async void OnShareFlyoutOpening(object? sender, object e)
    {
        if (_tornDown)
        {
            return;
        }

        ShareTargetList.ItemsSource = null;
        ShareTargetList.Visibility = Visibility.Collapsed;
        ShareTargetStatus.Text = string.Empty;
        ShareTargetRing.IsActive = false;
        ShareLoadingRow.Visibility = Visibility.Collapsed;

        if (!Douyin.Feed.IsLoggedIn)
        {
            ShareTargetStatus.Text = "登录后可以把视频分享给朋友";
            return;
        }

        ShareLoadingRow.Visibility = Visibility.Visible;
        ShareTargetRing.IsActive = true;
        IReadOnlyList<ViewModels.ShareTargetRow> rows = await LoadShareTargetsAsync();
        if (_tornDown)
        {
            return;
        }

        ShareTargetRing.IsActive = false;
        ShareLoadingRow.Visibility = Visibility.Collapsed;
        ShareTargetList.ItemsSource = rows;
        ShareTargetList.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (rows.Count == 0)
        {
            ShareTargetStatus.Text = _shareTargetsError.Length > 0
                ? "朋友列表没读回来：" + _shareTargetsError
                : "还没有可以分享的会话";
        }
    }

    private Task<IReadOnlyList<ViewModels.ShareTargetRow>> LoadShareTargetsAsync()
    {
        if (_shareTargets.Count > 0 && DateTimeOffset.UtcNow - _shareTargetsAt < TimeSpan.FromMinutes(2))
        {
            return Task.FromResult(_shareTargets);
        }

        return _shareTargetsLoading ??= LoadShareTargetsCoreAsync();
    }

    private async Task<IReadOnlyList<ViewModels.ShareTargetRow>> LoadShareTargetsCoreAsync()
    {
        try
        {
            IReadOnlyList<ImConversation> list =
                await Douyin.Feed.ImConversationsAsync(force: false, CancellationToken.None);
            var rows = list
                .Where(static c => c.Ticket.Length > 0)
                .Take(8)
                .Select(static c => new ViewModels.ShareTargetRow(c))
                .ToList();
            _shareTargets = rows;
            _shareTargetsAt = DateTimeOffset.UtcNow;
            _shareTargetsError = string.Empty;
            return rows;
        }
        catch (Exception ex)
        {
            _shareTargetsError = ex.GetType().Name + " · " + ex.Message;
            MfLog.Write("app: 分享会话列表读取失败 " + ex.GetType().Name + " · " + ex.Message);
            return Array.Empty<ViewModels.ShareTargetRow>();
        }
        finally
        {
            _shareTargetsLoading = null;
        }
    }

    /// <summary>点会话发一张视频分享卡（message_type=8，形状照官网分享实录）。</summary>
    private async void OnShareTargetClick(object sender, ItemClickEventArgs e)
    {
        if (_tornDown || e.ClickedItem is not ViewModels.ShareTargetRow row)
        {
            return;
        }

        ShareFlyout.Hide();
        if (!TryShareLink(out _))
        {
            return;
        }

        var card = new ShareCard(
            ItemId ?? string.Empty,
            ShareTitle(),
            Cover ?? string.Empty,
            VideoWidth,
            VideoHeight,
            AuthorId ?? string.Empty,
            Avatar ?? string.Empty);
        Toast("正在分享给 " + row.Name + "…");
        try
        {
            var (ok, _, note) = await Douyin.Feed.ImSendShareAsync(
                row.Conversation, card, CancellationToken.None);
            if (_tornDown)
            {
                return;
            }

            Toast(ok ? "已分享给 " + row.Name : "分享失败：" + note);
        }
        catch (Exception ex)
        {
            if (!_tornDown)
            {
                Toast("分享失败：" + ex.Message);
            }
        }
    }

    private void OnShareCopyClick(object sender, RoutedEventArgs e)
    {
        ShareFlyout.Hide();
        CopyShareLink();
    }

    /// <summary>复制站点拼出来的视频链接（分享面板与右键菜单共用）。</summary>
    private void CopyShareLink()
    {
        if (!TryShareLink(out string link))
        {
            return;
        }

        Windows.ApplicationModel.DataTransfer.DataPackage pkg = new();
        pkg.SetText(link);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        Toast("分享链接已复制：" + link);
    }

    private void OnShareSystemClick(object sender, RoutedEventArgs e)
    {
        ShareFlyout.Hide();
        _ = OpenSystemShareAsync();
    }

    /// <summary>
    /// Windows 系统分享（分享面板与右键菜单共用）：先把视频 id 换成官网同款
    /// <c>v.douyin.com</c> 短链，失败才回退规范长链；分享面板里的「复制链接」仍用长链。
    /// </summary>
    private async Task OpenSystemShareAsync()
    {
        if (!TryShareLink(out string link))
        {
            return;
        }

        // 标题/长链在等待短链之前就定格：请求途中换台也不能把旧链接配上新标题
        string title = ShareTitle();
        string id = ItemId ?? string.Empty;
        if (id.Length > 0)
        {
            Toast("正在生成分享链接…");
            (string shortUrl, string note) = await Douyin.Feed.ShortLinkAsync(id, CancellationToken.None);
            if (_tornDown)
            {
                return;
            }

            if (shortUrl.Length > 0)
            {
                link = shortUrl;
            }
            else
            {
                MfLog.Write("app: 短链生成失败，系统分享回退长链 · " + note);
            }
        }

        try
        {
            IntPtr hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(HostWindow().Id);
            SystemShare.Show(hwnd, title, link, "抖音视频");
            Toast("已打开系统分享");
        }
        catch (Exception ex)
        {
            MfLog.Write("app: 系统分享打不开 " + ex.GetType().Name + " · " + ex.Message);
            Toast("系统分享打不开：" + ex.Message);
        }
    }

    private bool TryShareLink(out string link)
    {
        string id = ItemId ?? string.Empty;
        if (id.Length == 0)
        {
            link = string.Empty;
            Toast("这条还没拿到 id，分享不了");
            return false;
        }

        link = "https://www.douyin.com/video/" + id;
        return true;
    }

    /// <summary>分享文本的标题：优先后半段描述，没有就用作者，再兜底一句通用文案。</summary>
    private string ShareTitle()
    {
        string desc = (Desc ?? string.Empty).Trim().ReplaceLineEndings(" ");
        if (desc.Length > 0)
        {
            return desc.Length > 60 ? desc[..60] + "…" : desc;
        }

        string author = (Author ?? string.Empty).Trim();
        return author.Length > 0 ? "@" + author + " 的视频" : "分享一条视频";
    }

    /// <summary>
    /// 音量滑块变了：拖动途中就把值写进设置（MVVM），好让播放器实时跟着变。
    ///
    /// **判定"这是不是用户在动它"靠音量小窗开没开**：
    /// - 一律写不行——XAML 初值、布局期内部纠偏、Flyout 挂载拆卸都会动滑块的值，那些经双向绑定会把设置冲掉
    ///   （2026-10-13 实测：连续启动三次，Volume 被冲成 0、0、99）；
    /// - 只认松手事件也不行——要指望 Thumb 把已处理的指针事件放行，而且真松手那一刻的值还要再读一次。
    /// 滑块只在那个小窗里，所以"小窗开着"就是"用户在动它"的充分判据。
    /// </summary>
    private void OnVolumeSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        int percent = (int)Math.Round(e.NewValue);

        if (!VolumeFlyout.IsOpen)
        {
            return; // 小窗没开：这一发是载入/布局来的，不是用户
        }

        CommitVolume(percent);
    }

    /// <summary>
    /// 鼠标停在音量图标上就把滑块条亮出来（抖音网页端就是这个手感）。
    /// **必须用 <see cref="FlyoutShowMode.Transient"/>**：默认那档（Standard）会把焦点抓进浮出层，
    /// 而这一发是从悬停里发起的——按钮没被点过、没有焦点，那一套焦点/light-dismiss 逻辑会当场把条收掉
    /// （用户实测"一打开就关闭"）。Transient 不抢焦点，收条交给下面那只悬停表。
    /// </summary>
    private void OnVolumePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _volumeHoverTimer.Stop();
        if (!VolumeFlyout.IsOpen)
        {
            VolumeFlyout.ShowAt(VolumeButton, new FlyoutShowOptions { ShowMode = FlyoutShowMode.Transient });
        }
    }

    private void OnVolumePointerExited(object sender, PointerRoutedEventArgs e) => _volumeHoverTimer.Start();

    private void OnVolumeFlyoutPointerEntered(object sender, PointerRoutedEventArgs e) => _volumeHoverTimer.Stop();

    private void OnVolumeFlyoutPointerExited(object sender, PointerRoutedEventArgs e) => _volumeHoverTimer.Start();

    /// <summary>
    /// 点音量图标 = 在"静音 / 上一次的音量"之间来回（用户 2026-10-14 点名的行为）。
    /// 记住的那个数只在控件里、只活这一次运行；解静音时它要是 0 就给回 100。
    /// </summary>
    private void OnVolumeClick(object sender, RoutedEventArgs e)
    {
        int now = Math.Clamp(Volume, 0, 100);
        if (now > 0)
        {
            _volumeBeforeMute = now;
            CommitVolume(0);
            return;
        }

        CommitVolume(_volumeBeforeMute > 0 ? _volumeBeforeMute : 100);
    }

    /// <summary>
    /// 把音量落实：依赖属性（播放器 + 页面那条绑定）与共享设置 VM（落盘）一起写。
    /// 滑块拖动、点图标静音/解静音都走这一条，别各自写一套。
    /// </summary>
    private void CommitVolume(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        Volume = percent;
        AppSettings().Volume = percent;
    }

    /// <summary>MF 的音量是 0~1 浮点，设置里存的是 0~100 整数百分比，这里换算；候场那条一律静音。</summary>
    private void ApplyVolume()
    {
        int percent = Math.Clamp(Volume, 0, 100);
        _player.Volume = _standby ? 0 : percent / 100.0;
        _bgm.Volume = _standby ? 0 : percent / 100.0;   // 图文 BGM 跟主播放器同一份音量

        // 这条灌下去不会再被当成用户操作：ValueChanged 直接丢（见 OnVolumeSliderChanged），写设置走滑块或点图标
        if (VolumeSlider.Value != percent)
        {
            VolumeSlider.Value = percent;
        }
    }

    /// <summary>进度条拖拽气泡的值转换：秒数显示成 mm:ss。</summary>
    private sealed partial class SecondsToolTipConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            FormatTime(System.Convert.ToDouble(value));

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// 弹幕设置那四条滑块的拖动气泡（ThumbToolTip）：不带转换器时它显示的是滑块原始值（0/1/2），
    /// 用户报的就是这个"只有 0、1、2"。文字与标题右边那个值同一个出处，两处不会说两样话。
    /// </summary>
    private sealed partial class DanmakuStepToolTipConverter(Func<int, string> text) : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            text((int)Math.Round(System.Convert.ToDouble(value)));

        public object ConvertBack(object value, Type targetType, object parameter, string language) =>
            throw new NotSupportedException();
    }

    /// <summary>手势层拖拽增量：从按下开始的累计 Y 偏移（已扣掉起手死区）与本次增量。</summary>
    public sealed class DragDeltaEventArgs(double cumulativeY, double deltaY) : EventArgs
    {
        public double CumulativeY { get; } = cumulativeY;
        public double DeltaY { get; } = deltaY;
    }

    /// <summary>手势层拖拽结束：最终累计 Y 偏移、松手瞬间的 Y 速度（px/s）与是否被取消。</summary>
    public sealed class DragCompletedEventArgs(double cumulativeY, double velocityY, bool canceled) : EventArgs
    {
        public double CumulativeY { get; } = cumulativeY;
        public double VelocityY { get; } = velocityY;
        public bool Canceled { get; } = canceled;
    }

    /// <summary>诊断用一行的"私有内存 / 工作集 / 托管堆"读数：任务管理器那条"内存"≈工作集那一档。</summary>
    private static string MemNote()
    {
        long priv = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64;
        long ws = Environment.WorkingSet;
        long managed = GC.GetTotalMemory(false);
        return $"私有 {priv / 1048576}MB · 工作集 {ws / 1048576}MB · 托管 {managed / 1048576}MB";
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>
    /// 工作集修剪：整进程工作集超过阈值就把页挪回 standby（EmptyWorkingSet——任务管理器自己打开时干的
    /// 也是同一件事）。这是**显示层的治理**：它不释放内存，真实 commit 不因此下降；被挪走的页在需要时
    /// 软缺页回来（微秒级），对播放无感，但任务管理器那条"内存"（专用工作集）能稳定压在阈值下。
    ///
    /// 三条刹车（2026-10-13 掉帧事故后加）：**页表活丢线程池**——EmptyWorkingSet 是同步全量走的，
    /// 几百 MB 的账在 UI 线程上做就是一次肉眼可见的顿帧；**窗口缩放期间不修剪**（含刚停下 1.5 秒），
    /// 否则拖拽时也被插一刀、且修剪后每个重绘帧都要软缺页回填；**同时只许一单**，别叠着做。
    /// </summary>
    private void TrimWorkingSetIfHigh()
    {
        long now = Environment.TickCount64;
        if (now - _lastTrimMs < TrimIntervalMs
            || now - _lastWindowResizeMs < ResizeQuietMs
            || _trimBusy
            || Environment.WorkingSet <= TrimThresholdBytes)
        {
            return;
        }

        _lastTrimMs = now;
        long before = Environment.WorkingSet;
        _trimBusy = true;

        _ = Task.Run(() =>
        {
            try
            {
                EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
            }
            catch (Exception e)
            {
                MfLog.Write("app: 工作集修剪失败：" + e.Message);
            }
            finally
            {
                _trimBusy = false;
            }

            MfLog.Write($"app: 工作集修剪 {before / 1048576}MB → {Environment.WorkingSet / 1048576}MB");
        });
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

    /// <summary>点作者头像回抛给宿主：要开的主页 sec_uid，和来路上先垫着的昵称。</summary>
    public sealed class AuthorOpenEventArgs(string secUid, string nickname) : EventArgs
    {
        public string SecUid { get; } = secUid;

        public string Nickname { get; } = nickname;
    }
}
