
using WinDouYin.Contracts;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;

namespace WinDouYin.Views;

/// <summary>
/// 推荐页的宿主：常驻两个 <see cref="VideoPage"/> 轮流上场。
/// 场上一页在播、幕外一页候场预热下一条——换台就是把两页按滚轮方向对滑，
/// 轮到的那条早就缓冲好停在首帧上了，不用等网络。
///
/// 这也是为什么不用 Frame 导航：页面切换自带的 SlideNavigationTransitionInfo 只有
/// FromBottom/FromLeft/FromRight 三档（**没有 FromTop**），往上的那一格做不出方向感，
/// 而且导航出来的页面在场上之前没机会预热。
/// </summary>
public sealed partial class ForYouPage : Page
{
    private const int WheelNotch = 120;

    /// <summary>手速护栏，略大于换台动效：上一次滑完之前连着来的第二格不该钻进去。</summary>
    private const long WheelCooldownMs = 260;

    /// <summary>两张卡之间的舞台底色缝，和官方一致（实测 12px）。</summary>
    private const double CardGap = 12;

    private const double SwitchMilliseconds = 250;

    private int _wheelAccumulator;
    private long _lastSwitchMs;
    private int _direction = 1;
    private bool _started;
    private bool _suspended;
    private Storyboard? _board;

    /// <summary>上一条报过历史的 id：history/write 的 pre_item_id 串链用它（照页面实录的连播链）。</summary>
    private string _historyPrevId = string.Empty;

    /// <summary>场上的那条。</summary>
    private VideoPage _front;

    /// <summary>幕外那条：装着下一条、静音预热好等着上场。</summary>
    private VideoPage _back;

    public ForYouViewModel ViewModel { get; } = new();

    public ForYouPage()
    {
        InitializeComponent();
        _front = PageA;
        _back = PageB;

        // 幕外那条一开始就摆到台下（不用 Collapsed：让播放表面保持活着，预热才有地方落）
        ParkBack(1);

        // 裁剪只在滑页动效里开：静止时两页都在界内，关掉裁剪能让每帧（尤其缩放时）少一层合成成本
        SetStageClip(false);

        ViewModel.PropertyChanged += OnViewModelChanged;
        Host.SizeChanged += (_, _) =>
        {
            if (_board is not null)
            {
                HostClip.Rect = new Rect(0, 0, Host.ActualWidth, Host.ActualHeight);
            }

            // 窗口尺寸变了，幕外那页的停机位要跟着重算，否则窗口一变大它可能露进画面
            if (_board is null)
            {
                ParkBack(1);
            }
        };
        PageA.Ended += OnFrontEnded;
        PageB.Ended += OnFrontEnded;
        PageA.WheelRolled += OnFrontWheelRolled;
        PageB.WheelRolled += OnFrontWheelRolled;
        PageA.DragStarted += OnDragStarted;
        PageB.DragStarted += OnDragStarted;
        PageA.DragDelta += OnDragDelta;
        PageB.DragDelta += OnDragDelta;
        PageA.DragCompleted += OnDragCompleted;
        PageB.DragCompleted += OnDragCompleted;
        PageA.CommentsPaneChanged += OnCommentsPaneChanged;
        PageB.CommentsPaneChanged += OnCommentsPaneChanged;

        // 点作者头像 → 用本页的 Frame 开用户主页。离场停播由既有的 OnNavigatedFrom/Suspend 那条管，
        // 这儿只负责把导航发出去（两页轮流上场，所以两边都挂）。
        PageA.AuthorOpenRequested += (_, e) => UserProfilePage.Open(Frame, e.SecUid, e.Nickname);
        PageB.AuthorOpenRequested += (_, e) => UserProfilePage.Open(Frame, e.SecUid, e.Nickname);
    }

    /// <summary>推荐条目摊平给标准舞台页。</summary>
    private static VideoEntry ToEntry(VideoItem item) => new(
        item.Id, item.PlayUrl, item.CoverUrl, item.Width, item.Height,
        item.Author, item.Desc, item.Avatar, RelativeTime.Format(item.CreateTime),
        item.Digg, item.Comment, item.Collect, item.Share, item.Qualities, item.Music);

    private static bool IsShowing(VideoPage page, VideoItem item) =>
        string.Equals(page.ItemId, item.Id, StringComparison.Ordinal);

    /// <summary>
    /// 离场：松开两页的播放源，并把拖拽/换台动效收干净。
    /// 推荐页是缓存页（NavigationCacheMode=Required），控件不会重建，不清的话两路视频会一直占着内存。
    /// </summary>
    private void SuspendPlayback()
    {
        if (_suspended)
        {
            return;
        }

        _suspended = true;
        _wheelAccumulator = 0;
        _deferredItem = null;
        _endedWhileDragging = false;
        _suppressAdvance = false;
        _isDragging = false;
        _dragDirection = 0;
        _dragEdgeSign = 0;

        if (_board is not null)
        {
            _board.Stop();
            _board = null;
        }

        ((TranslateTransform)_front.RenderTransform).Y = 0;
        ParkBack(1);
        SetStageClip(false);

        // 收成一致的静止态：回场时前页出声、后页静音预热（有换台被打断时也不会让幕外页先出声）。
        // 先 Suspend 再设，避免这一拍把正要停的播放器又 Play 起来。
        _front.Suspend();
        _back.Suspend();
        _front.Standby = false;
        _back.Standby = true;
    }

    /// <summary>
    /// 回场：重架播放链路。列表在离场期间可能已经推进（连播/预取），画面要追到 Current。
    /// </summary>
    private void ResumePlayback()
    {
        if (!_suspended)
        {
            return;
        }

        _suspended = false;
        _front.Resume();
        _back.Resume();

        DispatcherQueue.TryEnqueue(() =>
        {
            VideoItem? item = ViewModel.Current;
            if (_suspended || item is null || IsShowing(_front, item))
            {
                return;
            }

            _direction = 1;
            Advance();
        });
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        SuspendPlayback();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ResumePlayback();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 页面离场期间列表照常推进，但这里不动画面：回场时 Resume 再对齐到 Current
        if (_suspended)
        {
            return;
        }

        if (e.PropertyName == nameof(ForYouViewModel.Status))
        {
            _front.StatusText = ViewModel.Status;
            _back.StatusText = ViewModel.Status;
            return;
        }

        if (e.PropertyName != nameof(ForYouViewModel.Current))
        {
            return;
        }

        // 列表是在 awaits 之后落地的：续体万一不在 UI 线程，换页必须转回去再做
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(Advance);
            return;
        }

        Advance();
    }

    /// <summary>
    /// 评论面板开合持久化：两页轮流上场、各自带一套 SplitView，一页变了另一页立刻跟上。
    /// 这样换台时面板不用"现开"——右栏在滑页前后保持同一个开合，新视频的评论也能提前取。
    /// </summary>
    private void OnCommentsPaneChanged(object? sender, bool open)
    {
        VideoPage other = ReferenceEquals(sender, _front) ? _back : _front;
        if (other.IsCommentsPaneOpen == open)
        {
            return;
        }

        if (open)
        {
            other.OpenCommentsPanel();
        }
        else
        {
            other.CloseCommentsPanel();
        }
    }

    /// <summary>当前条目变了就换台：滚轮、连播和拖拽落位都汇到这里。</summary>
    private void Advance()
    {
        // 离场期间不动画面；回场时 ResumePlayback 会主动补这一发
        if (_suspended)
        {
            return;
        }

        VideoItem? item = ViewModel.Current;
        if (item is null || IsShowing(_front, item))
        {
            return;
        }

        // 拖拽落位：两页在拖拽动效里已经摆到位了，这里只记历史。
        // 不拦的话 GoToIndex 触发的这发通知会再起一次整页滑页，画面连滑两遍。
        if (_suppressAdvance)
        {
            ReportHistory(item);
            return;
        }

        // 拖拽/换台动效期间 Current 又变了（连播推来下一条、在途的推列表刚回来）：
        // 先记下，等这一发收尾再换。两套动画同时改同一对 TranslateTransform，
        // 页面索引和前后台会对不上——快滑时尤其容易撞上。
        if (_isDragging || _board is not null)
        {
            _deferredItem = item;
            _deferredDirection = _direction;
            return;
        }

        ReportHistory(item);

        if (!_started)
        {
            // 首条：没有旧页可滑，直接落位
            _started = true;
            _front.Standby = false;
            _front.Bind(ToEntry(item), ViewModel.Status);
            ((TranslateTransform)_front.RenderTransform).Y = 0;
            PrepareBack();
            return;
        }

        SwitchTo(item);
    }

    /// <summary>场上的那条播到底了：连播由宿主推列表，推完 Current 一变自然就换台。</summary>
    private void OnFrontEnded(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _front))
        {
            return;
        }

        // 拖到一半不换源：等松手回弹（或落位）之后再续下一条
        if (_isDragging)
        {
            _endedWhileDragging = true;
            return;
        }

        // 换台动效里旧页还在场：它播完正好轮到新页，不再往前推一条
        if (_board is not null)
        {
            return;
        }

        _direction = 1;
        _ = ViewModel.MoveNextAsync();
    }

    /// <summary>
    /// 浏览即发：轮到一条就往 history/write 记一发（pre_item_id 串上一条，照页面连播链）。
    /// 只认"罐里有会话 + 服务端没说没登录"；写失败不打扰界面（历史是背景账），留一行日志。
    /// </summary>
    private void ReportHistory(VideoItem item)
    {
        string pre = _historyPrevId;
        _historyPrevId = item.Id;
        if (item.Id.Length == 0 || item.AuthorId.Length == 0
            || !Douyin.Feed.IsLoggedIn || Douyin.Feed.LoginVerified == false)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Douyin.Feed.HistoryWriteAsync(item.Id, item.AuthorId, pre, CancellationToken.None);
            }
            catch (Exception ex)
            {
                MfLog.Write("app: 历史写抛了 " + ex.GetType().Name + " · " + ex.Message);
            }
        });
    }

    /// <summary>
    /// 换到 <paramref name="item"/>：幕外那条装着它就直接滑，没装着（往回滚、或还没轮到）就先装上。
    /// 方向取 <see cref="_direction"/>——+1 下一条从下方入，-1 上一条从上方入。
    /// </summary>
    private void SwitchTo(VideoItem item)
    {
        int direction = _direction;

        if (!IsShowing(_back, item))
        {
            _back.Standby = true;
            _back.Bind(ToEntry(item), ViewModel.Status);
        }

        ParkBack(direction);

        var incoming = _back;
        var outgoing = _front;
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
            // _board 这根字段必须在动画跑完前把 Storyboard 引用住，否则它可能被 GC 收走、动画中途停住
            if (ReferenceEquals(_board, board))
            {
                _board = null;
            }

            if (_suspended)
            {
                return;
            }

            FinishSwitch(outgoing, incoming);
        };
        _board = board;
        board.Begin();
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

    /// <summary>收尾：旧页下场（静音停播、挪回台下基准位），换上来的那条接续预热再下一条。</summary>
    private void FinishSwitch(VideoPage outgoing, VideoPage incoming)
    {
        outgoing.Standby = true;
        outgoing.Visibility = Visibility.Visible;
        ((TranslateTransform)outgoing.RenderTransform).Y = 0;

        (_front, _back) = (incoming, outgoing);
        PrepareBack();

        // 收尾完成：幕外页已停到场下，关掉裁剪
        SetStageClip(false);

        // 这一发期间被推迟的 Current 变化在这里补上（通常会再起一次对滑）
        ApplyDeferred();
    }

    /// <summary>幕外那条去装列表里的下一条，静音起播预热；没有下一条就空着停在台下。</summary>
    private void PrepareBack()
    {
        VideoItem? next = ViewModel.Next;
        if (next is not null && !IsShowing(_back, next))
        {
            _back.Standby = true;
            _back.Bind(ToEntry(next), ViewModel.Status);
        }

        ParkBack(1);
    }

    /// <summary>
    /// 滑页动效的裁剪开关：动效期间两页会滑出舞台边界（往上那股会探进标题栏），必须裁；
    /// 静止时不裁——两页一个在场内、一个停在场下，天然不露，每帧少一层合成成本。
    /// </summary>
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

    private void ParkBack(int direction)
    {
        double offset = Offset();
        ((TranslateTransform)_back.RenderTransform).Y = direction >= 0 ? offset : -offset;
    }

    private double Offset() => (Host.ActualHeight > 0 ? Host.ActualHeight : 640) + CardGap;

    /// <summary>
    /// WinUI 3 没有 UWP 的 PointerWheelEventArgs，delta 要从 GetCurrentPoint 拿到的
    /// Microsoft.UI.Input.PointerPoint.Properties.MouseWheelDelta 读。
    /// 平滑滚轮一次惯性会连发几十个事件，攒满一格才换一条，否则会瞬间跳过十几条。
    /// 上一次换台没落地就再滚，动效会被一路打断，看着反而更糟。
    /// </summary>
    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        e.Handled = true;
        RollWheel(e.GetCurrentPoint(this).Properties.MouseWheelDelta);
    }

    /// <summary>
    /// 换台那一滚：图文层截住滚轮之后也是走这里（<see cref="VideoPage.WheelRolled"/>），
    /// 与页面自己收的这条同一条路、同一套攒格与冷却。
    /// </summary>
    private void RollWheel(int delta)
    {
        if (_board is not null || _front.IsSwitching || _isDragging
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
        if (_wheelAccumulator > 0)
        {
            _direction = -1;
            ViewModel.MovePrevious();
        }
        else
        {
            _direction = 1;
            _ = ViewModel.MoveNextAsync();
        }

        _wheelAccumulator = 0;
    }

    private void OnFrontWheelRolled(object? sender, int delta)
    {
        if (!ReferenceEquals(sender, _front))
        {
            return;
        }

        RollWheel(delta);
    }

    // ===== 鼠标跟手拖拽换台 =====
    private bool _isDragging;
    private int _dragDirection;   // 0=未定向；+1=下一条从下方入（向上拖），-1=上一条从上方入（向下拖）
    private int _dragEdgeSign;    // 到头了：指针往这个方向拖没有邻居，只给阻尼
    private bool _suppressAdvance;
    private bool _endedWhileDragging;

    /// <summary>拖拽/动效期间 Current 变过但还没换台：收尾时补这一发。</summary>
    private VideoItem? _deferredItem;
    private int _deferredDirection = 1;

    /// <summary>松手时越过舞台高度的这个比例就落位；没到也可以靠速度甩过去。</summary>
    private const double DragCommitRatio = 0.22;

    /// <summary>松手速度（px/s）超过这个数就按"甩"落位，不用非得拖够距离。</summary>
    private const double DragFlickVelocity = 650;

    /// <summary>到头了还继续拖：位移压到这么点，给一点"拉到头"的手感。</summary>
    private const double EdgeDragRatio = 0.22;

    private const double DragSwitchMilliseconds = 170;
    private const double DragSnapBackMilliseconds = 190;

    private void OnDragStarted(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _front) || _isDragging)
        {
            return;
        }

        // 首条还没落位、或这条还没开出来：先不接拖拽（换源/换台会打架）
        if (_suspended || _board is not null || _front.ItemId.Length == 0 || _front.IsSwitching)
        {
            return;
        }

        _isDragging = true;
        _dragDirection = 0;
        _dragEdgeSign = 0;
        _endedWhileDragging = false;
        _wheelAccumulator = 0;
        SetStageClip(true);
    }

    private void OnDragDelta(object? sender, Controls.MfStageControl.DragDeltaEventArgs e)
    {
        if (!_isDragging || !ReferenceEquals(sender, _front))
        {
            return;
        }

        double dy = e.CumulativeY;

        // 先定方向：有邻居才真换台；没有就只做一点压扁的位移。
        // 方向一旦定下不再改——反向拖只把页停在 0，不露出没有内容的黑边。
        if (_dragDirection == 0)
        {
            int sign = Math.Sign(dy);
            if (sign == 0)
            {
                return;
            }

            if (_dragEdgeSign != 0 && sign != _dragEdgeSign)
            {
                // 反向了：重看另一边有没有邻居
                _dragEdgeSign = 0;
            }

            if (_dragEdgeSign == 0)
            {
                // 指针向上（sign<0）→ 下一条从下方入（direction=+1）
                int direction = -sign;
                VideoItem? neighbor = ViewModel.ItemAt(direction);
                if (neighbor is null)
                {
                    _dragEdgeSign = sign;
                }
                else
                {
                    _dragDirection = direction;
                    _back.Standby = true;
                    if (!IsShowing(_back, neighbor))
                    {
                        _back.Bind(ToEntry(neighbor), ViewModel.Status);
                    }

                    ParkBack(direction);
                }
            }
        }

        double offset;
        if (_dragDirection != 0)
        {
            offset = _dragDirection > 0 ? Math.Min(dy, 0) : Math.Max(dy, 0);
        }
        else
        {
            offset = (_dragEdgeSign < 0 ? Math.Min(dy, 0) : Math.Max(dy, 0)) * EdgeDragRatio;
        }

        ((TranslateTransform)_front.RenderTransform).Y = offset;
        if (_dragDirection != 0)
        {
            ((TranslateTransform)_back.RenderTransform).Y = _dragDirection * Offset() + offset;
        }
    }

    private void OnDragCompleted(object? sender, Controls.MfStageControl.DragCompletedEventArgs e)
    {
        if (!_isDragging || !ReferenceEquals(sender, _front))
        {
            return;
        }

        double dy = e.CumulativeY;
        double threshold = Offset() * DragCommitRatio;
        bool commit = !e.Canceled && _dragDirection != 0
            && (_dragDirection > 0
                ? dy <= -threshold || e.VelocityY <= -DragFlickVelocity
                : dy >= threshold || e.VelocityY >= DragFlickVelocity);

        // 松手点和最后一次 Move 可能差几像素：落位从松手那一点接上，不跳
        double startOffset = 0;
        if (_dragDirection != 0)
        {
            startOffset = _dragDirection > 0 ? Math.Min(dy, 0) : Math.Max(dy, 0);
        }
        else if (_dragEdgeSign != 0)
        {
            startOffset = (_dragEdgeSign < 0 ? Math.Min(dy, 0) : Math.Max(dy, 0)) * EdgeDragRatio;
        }

        _isDragging = false;
        int direction = _dragDirection;
        _dragDirection = 0;
        _dragEdgeSign = 0;

        MfLog.Write($"app: 拖拽结束 dir={direction} dy={dy:F0} v={e.VelocityY:F0} commit={commit}");

        try
        {
            if (commit)
            {
                AnimateDragSwitch(startOffset, direction, ViewModel.CurrentIndex + direction);
            }
            else
            {
                AnimateDragSnapBack(startOffset, direction);
            }
        }
        catch (Exception ex)
        {
            // 输入事件里抛出去会被 WinRT 裹成 0xC000027B 把进程带走，这里先落日志再复位
            MfLog.Write("app: 拖拽落位抛了 " + ex);
            _board = null;
            ResetDragVisuals();
        }
    }

    private void ResetDragVisuals()
    {
        _isDragging = false;
        _dragDirection = 0;
        _dragEdgeSign = 0;
        ((TranslateTransform)_front.RenderTransform).Y = 0;
        ParkBack(1);
        SetStageClip(false);
    }

    /// <summary>
    /// 松手后落位：旧页继续滑出、新页补完最后一段。
    /// 基准值必须先摆到终点——FillBehavior=Stop，动画一停就落回基准，
    /// 不摆的话会从终点弹回拖拽位置，松手瞬间画面闪一下再跳走。
    /// </summary>
    private void AnimateDragSwitch(double fromOffset, int direction, int newIndex)
    {
        double page = Offset();
        var outgoing = _front;
        var incoming = _back;
        var outTransform = (TranslateTransform)outgoing.RenderTransform;
        var inTransform = (TranslateTransform)incoming.RenderTransform;

        // 声音当场交给新的一条；旧页立刻静音（画面照滑）
        incoming.Standby = false;
        outgoing.Standby = true;

        // 旧页沿拖拽方向继续滑出（向上拖 → 旧页去 -page），新页从拖到的位置补到 0。
        // 符号不能写反：旧页终点是 -direction*page，写反会松手后反向穿过屏幕，看着像又播了一遍滚轮换台。
        outTransform.Y = -direction * page;
        inTransform.Y = 0;

        var board = new Storyboard();
        AddDragSlide(board, outTransform, fromOffset, -direction * page, DragSwitchMilliseconds);
        AddDragSlide(board, inTransform, direction * page + fromOffset, 0, DragSwitchMilliseconds);

        board.Completed += (_, _) =>
        {
            try
            {
                if (ReferenceEquals(_board, board))
                {
                    _board = null;
                }

                if (_suspended)
                {
                    return;
                }

                // ViewModel 的索引跟界面一起落位。GoToIndex 会发 Current 通知，
                // Advance 靠 _suppressAdvance 只记历史、不再起第二遍滑页。
                _suppressAdvance = true;
                try
                {
                    ViewModel.GoToIndex(newIndex);
                }
                finally
                {
                    _suppressAdvance = false;
                }

                _endedWhileDragging = false;
                FinishSwitch(outgoing, incoming);
            }
            catch (Exception ex)
            {
                MfLog.Write("app: 拖拽换台收尾抛了 " + ex);
                ResetDragVisuals();
            }
        };
        _board = board;
        board.Begin();
    }

    private void AnimateDragSnapBack(double fromOffset, int direction)
    {
        if (fromOffset == 0)
        {
            // 回弹前幕外页可能停在"上方"那一侧（向下拖时）；关裁剪前先收回台下，
            // 否则它会探到舞台上面去
            if (direction != 0)
            {
                ParkBack(1);
            }

            SetStageClip(false);
            SettleDragEnd();
            return;
        }

        var front = (TranslateTransform)_front.RenderTransform;
        var board = new Storyboard();
        front.Y = 0;

        if (direction == 0)
        {
            // 到头了：只把前页弹回去，幕外页不动
            AddDragSlide(board, front, fromOffset, 0, DragSnapBackMilliseconds);
        }
        else
        {
            double page = Offset();
            var back = (TranslateTransform)_back.RenderTransform;
            back.Y = direction * page;
            AddDragSlide(board, front, fromOffset, 0, DragSnapBackMilliseconds);
            AddDragSlide(board, back, direction * page + fromOffset, direction * page, DragSnapBackMilliseconds);
        }

        board.Completed += (_, _) =>
        {
            try
            {
                if (ReferenceEquals(_board, board))
                {
                    _board = null;
                }

                if (_suspended)
                {
                    return;
                }

                if (direction != 0)
                {
                    ParkBack(1);
                }

                SetStageClip(false);
                SettleDragEnd();
            }
            catch (Exception ex)
            {
                MfLog.Write("app: 拖拽回弹收尾抛了 " + ex);
                ResetDragVisuals();
            }
        };
        _board = board;
        board.Begin();
    }

    /// <summary>拖拽结束结账：先落位被推迟的 Current 变化，没有再把"拖到一半播完"的那条接上。</summary>
    private void SettleDragEnd()
    {
        if (ApplyDeferred())
        {
            _endedWhileDragging = false;
            return;
        }

        ResumeEnded();
    }

    /// <summary>把拖拽/动效期间推迟的那一条补上；返回是否真的起了一次换台。</summary>
    private bool ApplyDeferred()
    {
        VideoItem? item = _deferredItem;
        _deferredItem = null;

        if (item is null || _isDragging || _board is not null
            || !ReferenceEquals(ViewModel.Current, item)
            || IsShowing(_front, item))
        {
            return false;
        }

        _direction = _deferredDirection;
        ReportHistory(item);
        SwitchTo(item);
        return true;
    }

    /// <summary>拖动期间场上的视频播完了：等画面回位再续下一条，不在拖到一半时换源。</summary>
    private void ResumeEnded()
    {
        if (!_endedWhileDragging)
        {
            return;
        }

        _endedWhileDragging = false;
        _direction = 1;
        _ = ViewModel.MoveNextAsync();
    }

    /// <summary>拖拽收尾动画：比滚轮换台短、ease-out 收住，接在跟手位移后面不显得顿。</summary>
    private static void AddDragSlide(Storyboard board, TranslateTransform transform, double from, double to, double milliseconds)
    {
        var slide = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            FillBehavior = FillBehavior.Stop,
        };
        slide.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = from,
        });
        slide.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)),
            Value = to,
            KeySpline = new KeySpline { ControlPoint1 = new Point(0.1, 0.9), ControlPoint2 = new Point(0.2, 1) },
        });
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "Y");
        board.Children.Add(slide);
    }
}
