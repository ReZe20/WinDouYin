
using WinDouYin.Contracts;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using WinDouYin.ViewModels;

namespace WinDouYin.Views;

/// <summary>
/// 朋友页的宿主：没有左栏，整页就是标准 VideoPage 的竖向舞台。
/// 两页常驻、幕外预热、按滚轮方向对滑 250ms；右缘 68px 放上下两条箭头。
/// 数据只吃 <c>familiar/feed</c> 的 <b>level=1（朋友推荐）</b>：首页 pull=0，续批 cursor=0 + pull=2，按 id 去重；
/// 每条带出的推荐人由信息区在作者/时间下面显示「朋友 XXX 推荐」。
/// </summary>
public sealed partial class FriendsPage : Page
{
    private const int WheelNotch = 120;
    private const long WheelCooldownMs = 260;
    private const double CardGap = 12;
    private const double SwitchMilliseconds = 250;

    private int _wheelAccumulator;
    private long _lastSwitchMs;
    private int _direction = 1;
    private bool _started;
    private bool _suspended;
    private bool _loadedOnce;
    private bool _lastLoggedIn;
    private Storyboard? _board;
    private VideoItem? _deferredItem;
    private int _deferredDirection = 1;
    private string _historyPrevId = string.Empty;
    private VideoPage _front;
    private VideoPage _back;

    // ===== 鼠标跟手拖拽换台（与推荐页同一套手感） =====
    private const double DragCommitRatio = 0.22;
    private const double DragFlickVelocity = 650;
    private const double EdgeDragRatio = 0.22;
    private const double DragSwitchMilliseconds = 170;
    private const double DragSnapBackMilliseconds = 190;

    private bool _isDragging;
    private int _dragDirection;
    private int _dragEdgeSign;
    private bool _suppressAdvance;
    private bool _endedWhileDragging;

    public FriendsPage()
    {
        InitializeComponent();
        _front = PageA;
        _back = PageB;
        ParkBack(1);
        SetStageClip(false);

        Loaded += OnPageLoaded;
        ViewModel.PropertyChanged += OnViewModelChanged;
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
        PageA.AuthorOpenRequested += (_, e) => UserProfilePage.Open(Frame, e.SecUid, e.Nickname);
        PageB.AuthorOpenRequested += (_, e) => UserProfilePage.Open(Frame, e.SecUid, e.Nickname);
    }

    public FriendsViewModel ViewModel { get; } = new();

    private static VideoEntry ToEntry(VideoItem item) => new(
        item.Id, item.PlayUrl, item.CoverUrl, item.Width, item.Height,
        item.Author, item.Desc, item.Avatar, RelativeTime.Format(item.CreateTime),
        item.Digg, item.Comment, item.Collect, item.Share, item.Qualities, item.Music,
        FriendRecommender: item.FriendRecommender);

    private static bool IsShowing(VideoPage page, VideoItem item) =>
        string.Equals(page.ItemId, item.Id, StringComparison.Ordinal);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ResumePlayback();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        SuspendPlayback();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        bool loggedIn = Douyin.Feed.IsLoggedIn;
        if (!_loadedOnce)
        {
            _loadedOnce = true;
            _lastLoggedIn = loggedIn;
            await ViewModel.EnsureLoadedAsync();
            return;
        }

        if (loggedIn && !_lastLoggedIn)
        {
            _lastLoggedIn = true;
            await ViewModel.RefreshAsync();
            return;
        }

        _lastLoggedIn = loggedIn;

        if (_suspended)
        {
            ResumePlayback();
        }
        else if (!_started && ViewModel.Current is { } item)
        {
            Advance();
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e) => SuspendPlayback();

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

        _front.Suspend();
        _back.Suspend();
        _front.Standby = false;
        _back.Standby = true;
    }

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

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suspended)
        {
            return;
        }

        if (e.PropertyName == nameof(FriendsViewModel.StatusText))
        {
            _front.StatusText = ViewModel.StatusText;
            _back.StatusText = ViewModel.StatusText;
            return;
        }

        if (e.PropertyName != nameof(FriendsViewModel.Current))
        {
            return;
        }

        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(Advance);
            return;
        }

        Advance();
    }

    private void Advance()
    {
        if (_suspended)
        {
            return;
        }

        VideoItem? item = ViewModel.Current;
        if (item is null || IsShowing(_front, item))
        {
            return;
        }

        // 拖拽落位：两页已经摆到位，这里只记历史；否则 GoToIndex 会再起一遍整页滑页。
        if (_suppressAdvance)
        {
            ReportHistory(item);
            return;
        }

        if (_isDragging || _board is not null)
        {
            _deferredItem = item;
            _deferredDirection = _direction;
            return;
        }

        ReportHistory(item);

        if (!_started)
        {
            _started = true;
            _front.Standby = false;
            _front.Bind(ToEntry(item), ViewModel.StatusText);
            ((TranslateTransform)_front.RenderTransform).Y = 0;
            PrepareBack();
            return;
        }

        SwitchTo(item);
    }

    private void OnFrontEnded(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _front) || _suspended)
        {
            return;
        }

        // 拖到一半不换源：等松手回弹（或落位）之后再续下一条
        if (_isDragging)
        {
            _endedWhileDragging = true;
            return;
        }

        if (_board is not null)
        {
            return;
        }

        _direction = 1;
        _ = ViewModel.MoveNextAsync();
    }

    private void SwitchTo(VideoItem item)
    {
        int direction = _direction;
        if (!IsShowing(_back, item))
        {
            _back.Standby = true;
            _back.Bind(ToEntry(item), ViewModel.StatusText);
        }

        ParkBack(direction);

        VideoPage incoming = _back;
        VideoPage outgoing = _front;
        double offset = Offset();
        double sign = direction >= 0 ? 1 : -1;
        var inTransform = (TranslateTransform)incoming.RenderTransform;
        var outTransform = (TranslateTransform)outgoing.RenderTransform;

        incoming.Standby = false;
        outgoing.Standby = true;
        SetStageClip(true);

        var board = new Storyboard();
        outTransform.Y = -sign * offset;
        AddSlide(board, outTransform, 0, -sign * offset);
        inTransform.Y = 0;
        AddSlide(board, inTransform, sign * offset, 0);

        board.Completed += (_, _) =>
        {
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

    private void FinishSwitch(VideoPage outgoing, VideoPage incoming)
    {
        outgoing.Standby = true;
        ((TranslateTransform)outgoing.RenderTransform).Y = 0;
        (_front, _back) = (incoming, outgoing);
        PrepareBack();
        SetStageClip(false);
        ApplyDeferred();
    }

    private void PrepareBack()
    {
        VideoItem? next = ViewModel.Next;
        if (next is not null && !IsShowing(_back, next))
        {
            _back.Standby = true;
            _back.Bind(ToEntry(next), ViewModel.StatusText);
        }

        ParkBack(1);
    }

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
    /// 页面级收滚轮（和推荐页同一条）：整个舞台 + 右侧箭头缝都算换台区；
    /// 播放器自己截走的那几发由 <see cref="OnFrontWheelRolled"/> 补进来。
    /// </summary>
    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
        RollWheel(e.GetCurrentPoint(this).Properties.MouseWheelDelta);
    }

    private void RollWheel(int delta)
    {
        if (_board is not null || _front.IsSwitching || _isDragging || _suspended
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
        if (ReferenceEquals(sender, _front))
        {
            RollWheel(delta);
        }
    }

    // ===== 鼠标跟手拖拽换台：与推荐页同一套（阈值/甩速/到头阻尼都一样） =====

    private void OnDragStarted(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _front) || _isDragging)
        {
            return;
        }

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
        if (_dragDirection == 0)
        {
            int sign = Math.Sign(dy);
            if (sign == 0)
            {
                return;
            }

            if (_dragEdgeSign != 0 && sign != _dragEdgeSign)
            {
                _dragEdgeSign = 0;
            }

            if (_dragEdgeSign == 0)
            {
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
                        _back.Bind(ToEntry(neighbor), ViewModel.StatusText);
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

    private void AnimateDragSwitch(double fromOffset, int direction, int newIndex)
    {
        double page = Offset();
        var outgoing = _front;
        var incoming = _back;
        var outTransform = (TranslateTransform)outgoing.RenderTransform;
        var inTransform = (TranslateTransform)incoming.RenderTransform;

        incoming.Standby = false;
        outgoing.Standby = true;
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

                // 画面已落位，只把账面索引跟过去；这发 Current 通知不再起第二遍滑页。
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

    /// <summary>拖动期间场上的视频播完了：等画面回位再续下一条。</summary>
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

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_board is not null || _front.IsSwitching)
        {
            return;
        }

        _direction = 1;
        await ViewModel.MoveNextAsync();
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e)
    {
        if (_board is not null || _front.IsSwitching)
        {
            return;
        }

        _direction = -1;
        ViewModel.MovePrevious();
    }
}
