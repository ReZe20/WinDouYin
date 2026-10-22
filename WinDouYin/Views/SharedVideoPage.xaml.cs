using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using WinDouYin.Contracts;
using WinDouYin.Service;

namespace WinDouYin.Views;

public sealed record SharedReviewArgs(string Text, string Author, string CommentId, IReadOnlyList<ImageSource> Images, IReadOnlyList<ImageSource> Stickers, string ImageUrl = "");

/// <summary>消息上下文里的一条分享视频：详情页的上一/下一列表按它走（ReviewArgs 只对 105 评论分享卡非空）。</summary>
public sealed record SharedVideoRef(string VideoId, SharedReviewArgs? ReviewArgs);

public sealed record SharedVideoArgs(
    VideoEntry Video, SharedReviewArgs? Review = null, IReadOnlyList<SharedVideoRef>? Context = null)
{
    public SharedVideoArgs() : this(VideoEntry.Empty)
    {
    }

    /// <summary>进门是否自动展开评论：消息分享卡默认展开；搜索结果进视频不展开。</summary>
    public bool OpenComments { get; init; } = true;
}

/// <summary>面板向宿主请求打开分享卡时携带的数据；Context 是当前会话里已加载的分享视频顺序。</summary>
public sealed record ShareOpenEventArgs(
    string VideoId, SharedReviewArgs? ReviewArgs = null, IReadOnlyList<SharedVideoRef>? Context = null);

/// <summary>
/// 聊天分享卡的视频详情页：双页轮换直接用标准 <see cref="VideoPage"/>（和推荐页同一种舞台），
/// 本页只负责把详情数据与来源评论接上。由主窗口 ContentFrame 导航进来（不另开窗口）。
/// 页面在 Frame 里缓存：离场时显式 Suspend 两页（释放播放源），回场先 Bind 再 Resume，只装新条目一次。
/// </summary>
public sealed partial class SharedVideoPage : Page, INotifyPropertyChanged
{
    private const int WheelNotch = 120;
    private const long WheelCooldownMs = 260;

    /// <summary>两张卡之间的舞台底色缝，与推荐页一致。</summary>
    private const double CardGap = 12;

    private const double SwitchMilliseconds = 250;

    private readonly List<SharedVideoRef> _context = [];
    private readonly Dictionary<string, VideoEntry> _cache = new(StringComparer.Ordinal);

    private SharedVideoArgs _args = new(VideoEntry.Empty);
    private int _index;
    private int _wheelAccumulator;
    private long _lastSwitchMs;
    private bool _switching;
    private int _switchVersion;
    private bool _left;

    /// <summary>场上的那页。</summary>
    private VideoPage _frontStage = null!;

    /// <summary>幕外那页：装着下一/上一条、静音预热好等着上场。</summary>
    private VideoPage _backStage = null!;

    private VideoEntry _frontVideo = VideoEntry.Empty;
    private VideoEntry _backVideo = VideoEntry.Empty;

    /// <summary>滑页动效；持有引用防止被 GC 收走后动画中途停住。</summary>
    private Storyboard? _board;

    /// <summary>预热代次：换台/离开时 +1，作废还在取详情的幕外装载。</summary>
    private int _prepareVersion;

    public SharedVideoPage()
    {
        InitializeComponent();
        _frontStage = StageA;
        _backStage = StageB;

        // 幕外那页一开始就摆到台下（不用 Collapsed：让播放表面保持活着，预热才有地方落）
        ParkBack(1);
        SetStageClip(false);

        StageA.Ended += OnFrontEnded;
        StageB.Ended += OnFrontEnded;
        StageA.WheelRolled += OnFrontWheelRolled;
        StageB.WheelRolled += OnFrontWheelRolled;

        // 点作者头像 → 用本页的 Frame 开用户主页（两页轮流上场，所以两边都挂）
        StageA.AuthorOpenRequested += (_, e) => UserProfilePage.Open(Frame, e.SecUid, e.Nickname);
        StageB.AuthorOpenRequested += (_, e) => UserProfilePage.Open(Frame, e.SecUid, e.Nickname);

        Host.SizeChanged += (_, _) =>
        {
            if (_board is not null)
            {
                HostClip.Rect = new Rect(0, 0, Host.ActualWidth, Host.ActualHeight);
            }
            else
            {
                ParkBack(1);
            }
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SharedVideoArgs Args
    {
        get => _args;
        private set
        {
            _args = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Args)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTakenDown)));
        }
    }

    /// <summary>视频下架/不存在：Source 为空即判定，占位层盖住舞台。</summary>
    public bool IsTakenDown => string.IsNullOrEmpty(Args.Video.Source);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not SharedVideoArgs args)
        {
            return;
        }

        _left = false;
        if (_board is not null)
        {
            _board.Stop();
            _board = null;
        }

        _switching = false;
        Args = args;

        // 视频列表 = 这条消息所在的会话里已加载的分享视频（按聊天时间顺序）；
        // 没有上下文就退化成"只有这一条"。
        _cache.Clear();
        _cache[args.Video.Id] = args.Video;
        _context.Clear();
        if (args.Context is { Count: > 0 } context)
        {
            _context.AddRange(context);
        }
        else
        {
            _context.Add(new SharedVideoRef(args.Video.Id, args.Review));
        }

        _index = _context.FindIndex(r => string.Equals(r.VideoId, args.Video.Id, StringComparison.Ordinal));
        if (_index < 0)
        {
            _context.Insert(0, new SharedVideoRef(args.Video.Id, args.Review));
            _index = 0;
        }

        // 舞台：场上那页直接落位，幕外那页先清空（缓存页上可能还挂着上一次来访的条目）。
        // 顺序很重要：Bind 在 Suspend 期间只落属性不开源，Resume 再按新属性装一次，只开一路。
        _frontVideo = args.Video;
        _backVideo = VideoEntry.Empty;
        BindStage(_frontStage, args.Video, args.Review, standby: false);
        BindStage(_backStage, VideoEntry.Empty, null, standby: true);
        ((TranslateTransform)_frontStage.RenderTransform).Y = 0;
        ParkBack(1);

        _frontStage.Resume();
        _backStage.Resume();

        _ = PrepareBackAsync(1);

        // 等 DPs 落下去再开评论，否则 MfStageControl 还拿着空 ItemId。
        DispatcherQueue.TryEnqueue(TryOpenComments);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _left = true;
        _switchVersion++;
        _prepareVersion++;
        if (_board is not null)
        {
            _board.Stop();
            _board = null;
        }

        // 页面留在 Frame 缓存里：两页的播放源与订阅当场松掉，回来再按新条目重装
        _frontStage.Suspend();
        _backStage.Suspend();

        base.OnNavigatedFrom(e);
    }

    private void TryOpenComments()
    {
        if (_left || !Args.OpenComments || string.IsNullOrEmpty(_frontStage.ItemId))
        {
            return;
        }

        _frontStage.OpenCommentsPanel();   // 来源评论已在 BindStage 里置顶
    }

    /// <summary>连播（Stage.Ended 只在开了连播时抛）和滚轮都走这里：在消息上下文里前后切。</summary>
    private async Task SwitchAsync(int direction)
    {
        if (_switching || _left || _board is not null)
        {
            return;
        }

        int target = _index + direction;
        if (target < 0 || target >= _context.Count)
        {
            return;
        }

        _switching = true;
        _prepareVersion++;   // 还在飞的幕外预热作废，别让它改到这次要用的那页
        int version = ++_switchVersion;
        bool sliding = false;
        SharedVideoRef reference = _context[target];
        try
        {
            VideoEntry? video = await LoadVideoAsync(reference.VideoId);
            if (_left || version != _switchVersion || video is null)
            {
                return;
            }

            // 幕外那页预热的正好是它就直接滑；不是（往回翻/首次还没预热到）就先装上再滑
            if (!string.Equals(_backVideo.Id, video.Id, StringComparison.Ordinal))
            {
                BindStage(_backStage, video, reference.ReviewArgs, standby: true);
                _backVideo = video;
            }

            _index = target;
            Args = new SharedVideoArgs(video, reference.ReviewArgs, _context);
            sliding = StartSwitch(direction);
        }
        catch (Exception ex)
        {
            MfLog.Write("app: 消息视频上下文切换失败 " + ex.GetType().Name + " · " + ex.Message);
        }
        finally
        {
            if (!sliding && version == _switchVersion)
            {
                _switching = false;
            }
        }
    }

    private async Task<VideoEntry?> LoadVideoAsync(string id)
    {
        if (_cache.TryGetValue(id, out VideoEntry? cached))
        {
            return cached;
        }

        try
        {
            (VideoCard? card, bool takenDown) =
                await Douyin.Feed.VideoDetailProbeAsync(id, CancellationToken.None);
            VideoEntry video = takenDown || card is null
                ? VideoEntry.TakenDown(id)
                : VideoEntry.From(card);
            _cache[id] = video;
            return video;
        }
        catch (Exception ex)
        {
            MfLog.Write("app: 消息视频详情取失败 " + ex.GetType().Name + " · " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 幕外那页去装上下文里的下一条（按刚走过的方向），静音起播预热。
    /// 换台/离开之后回来的那些一律丢掉（代次对不上）。
    /// </summary>
    private async Task PrepareBackAsync(int direction)
    {
        if (_left)
        {
            return;
        }

        int target = _index + direction;
        if (target < 0 || target >= _context.Count)
        {
            return;
        }

        int version = ++_prepareVersion;
        SharedVideoRef reference = _context[target];
        VideoEntry? video = await LoadVideoAsync(reference.VideoId);
        if (_left || version != _prepareVersion || _switching || _board is not null
            || video is null || target != _index + direction)
        {
            return;
        }

        if (!string.Equals(_backVideo.Id, video.Id, StringComparison.Ordinal))
        {
            BindStage(_backStage, video, reference.ReviewArgs, standby: true);
            _backVideo = video;
        }

        ParkBack(1);
    }

    /// <summary>把一条视频交给标准舞台页。</summary>
    private static void BindStage(VideoPage stage, VideoEntry video, SharedReviewArgs? review, bool standby)
    {
        stage.Standby = standby;
        stage.Bind(video);

        // 来源评论跟着这一条走：有就置顶到评论列表最上面（接口没回这条时用消息自带内容兜底），没有就清掉
        if (review is null)
        {
            stage.ClearPinnedComment();
        }
        else
        {
            stage.SetPinnedComment(review.CommentId, review.Author, review.Text, review.ImageUrl);
        }
    }

    /// <summary>换台动效：旧页朝方向反侧滑出，新页从方向侧滑入，中途露 12px 舞台底色。</summary>
    private bool StartSwitch(int direction)
    {
        ParkBack(direction);

        var incoming = _backStage;
        var outgoing = _frontStage;
        double offset = Offset();
        double sign = direction >= 0 ? 1 : -1;
        var inTransform = (TranslateTransform)incoming.RenderTransform;
        var outTransform = (TranslateTransform)outgoing.RenderTransform;

        // 声音当场交给新的一条；旧的那条立刻静音（画面照滑，只是不出声）
        incoming.Standby = false;
        outgoing.Standby = true;

        // 动效期间两页会滑出边界，先开裁剪
        SetStageClip(true);

        var board = new Storyboard();
        // 旧页：基准值先改成终点——FillBehavior=Stop 停回基准时正好停在终点，不会闪回原位
        outTransform.Y = -sign * offset;
        AddSlide(board, outTransform, 0, -sign * offset);
        // 新页：基准值保持 0，起点由关键帧给出，停回 0 正好是终点
        inTransform.Y = 0;
        AddSlide(board, inTransform, sign * offset, 0);

        board.Completed += (_, _) =>
        {
            if (ReferenceEquals(_board, board))
            {
                _board = null;
            }

            if (!_left)
            {
                FinishSwitch(outgoing, incoming, direction);
            }
        };
        _board = board;
        board.Begin();
        return true;
    }

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

    /// <summary>收尾：旧页下场（静音停播、挪回台下基准位），场上/幕外对调，再接续预热。</summary>
    private void FinishSwitch(VideoPage outgoing, VideoPage incoming, int direction)
    {
        bool commentsOpen = outgoing.IsCommentsPaneOpen;

        outgoing.Standby = true;
        ((TranslateTransform)outgoing.RenderTransform).Y = 0;

        (_frontStage, _backStage) = (incoming, outgoing);
        (_frontVideo, _backVideo) = (_backVideo, _frontVideo);
        _switching = false;

        // 评论面板的开合跟着换到新的一页：开着就继续开着（面板内会按新 ItemId 重取）
        if (commentsOpen)
        {
            _frontStage.OpenCommentsPanel();
        }
        else
        {
            _frontStage.CloseCommentsPanel();
        }

        // 幕后那页停在台下（朝下的那一侧——朝上会探进上面的来源评论行）
        ParkBack(1);
        SetStageClip(false);
        _ = PrepareBackAsync(direction);
    }

    private void ParkBack(int direction)
    {
        double offset = Offset();
        ((TranslateTransform)_backStage.RenderTransform).Y = direction >= 0 ? offset : -offset;
    }

    private double Offset() => (Host.ActualHeight > 0 ? Host.ActualHeight : 640) + CardGap;

    /// <summary>滑页动效的裁剪开关：动效期间两页会滑出舞台边界，必须裁；静止时关掉省合成成本。</summary>
    private void SetStageClip(bool on)
    {
        if (on)
        {
            HostClip.Rect = new Rect(0, 0, Host.ActualWidth, Host.ActualHeight);
            Host.Clip = HostClip;
        }
        else
        {
            Host.Clip = null;
        }
    }

    private void OnFrontEnded(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _frontStage))
        {
            return;
        }

        _ = SwitchAsync(1);
    }

    private void OnFrontWheelRolled(object? sender, int delta)
    {
        if (!ReferenceEquals(sender, _frontStage))
        {
            return;
        }

        RollWheel(delta);
    }

    /// <summary>滚轮在视频区上下翻消息上下文里的分享视频；右侧评论面板上的滚轮留给评论列表。</summary>
    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var point = e.GetCurrentPoint(_frontStage);
        if (_frontStage.IsCommentsPaneOpen
            && point.Position.X >= _frontStage.ActualWidth - _frontStage.CommentsPaneWidth)
        {
            return;
        }

        e.Handled = true;
        RollWheel(point.Properties.MouseWheelDelta);
    }

    /// <summary>换台那一滚：图文层截住滚轮之后也是走这里，攒格与冷却只有这一份。</summary>
    private void RollWheel(int delta)
    {
        if (_context.Count <= 1)
        {
            return;
        }

        if (_switching || _board is not null || _frontStage.IsSwitching
            || Environment.TickCount64 - _lastSwitchMs < WheelCooldownMs)
        {
            _wheelAccumulator = 0;
            return;
        }

        _wheelAccumulator += delta;
        if (Math.Abs(_wheelAccumulator) < WheelNotch)
        {
            return;
        }

        _lastSwitchMs = Environment.TickCount64;
        int direction = _wheelAccumulator > 0 ? -1 : 1;
        _wheelAccumulator = 0;
        _ = SwitchAsync(direction);
    }
}
