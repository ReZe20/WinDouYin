
using WinDouYin.Contracts;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinDouYin.Views;

/// <summary>
/// 一条视频一整页的标准组件：舞台 + 信息区 + 右侧竖排 + 控制条都在 <see cref="Controls.MfStageControl"/> 里，
/// 页面只负责把一条 <see cref="VideoEntry"/> 接上去。推荐页和消息分享页共用这一套。
/// 这一页不是用 Frame 导航出来的，生命周期由宿主直接管（Bind / Standby / Suspend / Resume）。
/// </summary>
public sealed partial class VideoPage : Page, INotifyPropertyChanged
{
    private VideoEntry _entry = VideoEntry.Empty;
    private string _statusText = string.Empty;

    public VideoPage()
    {
        InitializeComponent();
        Stage.Ended += (_, _) => Ended?.Invoke(this, EventArgs.Empty);
        Stage.WheelRolled += (_, delta) => WheelRolled?.Invoke(this, delta);
        Stage.DragStarted += (_, _) => DragStarted?.Invoke(this, EventArgs.Empty);
        Stage.DragDelta += (_, e) => DragDelta?.Invoke(this, e);
        Stage.DragCompleted += (_, e) => DragCompleted?.Invoke(this, e);
        Stage.CommentsPaneChanged += (_, open) => CommentsPaneChanged?.Invoke(this, open);
    }

    /// <summary>这一页装的是哪条；x:Bind 靠这里的 PropertyChanged 重新取值。</summary>
    public VideoEntry Entry
    {
        get => _entry;
        private set
        {
            _entry = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Entry)));
        }
    }

    /// <summary>左上角的诊断状态文本（推荐页显示会话/序号，分享页留空）。</summary>
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (string.Equals(_statusText, value, StringComparison.Ordinal))
            {
                return;
            }

            _statusText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        }
    }

    /// <summary>全应用共享的设置视图模型：音量经它双向绑定并防抖落盘，页面不自建。</summary>
    public ViewModels.SettingsViewModel Settings { get; } = ((App)Application.Current).ViewModel;

    /// <summary>前台这条播到底了（连播开着时才有）：宿主拿它去取下一条。</summary>
    public event EventHandler? Ended;

    /// <summary>图文帖被手势层截下的那发滚轮（舞台不认翻图，只当换台）：参数是滚轮增量，宿主照自己那条滚轮路处理。</summary>
    public event EventHandler<int>? WheelRolled;

    /// <summary>在视频上按住并竖向拖动：舞台只报偏移，两页怎么跟手由宿主摆。</summary>
    public event EventHandler? DragStarted;
    public event EventHandler<Controls.MfStageControl.DragDeltaEventArgs>? DragDelta;
    public event EventHandler<Controls.MfStageControl.DragCompletedEventArgs>? DragCompleted;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>换台还没落地：宿主拿它挡滚轮。</summary>
    public bool IsSwitching => Stage.IsSwitching;

    /// <summary>评论面板是否展开（宿主处理滚轮落点用）。</summary>
    public bool IsCommentsPaneOpen => Stage.IsCommentsPaneOpen;

    /// <summary>评论面板开合变了：宿主把状态同步到另一页，两页保持同一个开合。</summary>
    public event EventHandler<bool>? CommentsPaneChanged;

    /// <summary>评论面板展开宽度。</summary>
    public double CommentsPaneWidth => Stage.CommentsPaneWidth;

    /// <summary>这一页正在放哪条的 id：宿主靠它避免把同一条重复装一次。</summary>
    public string ItemId => Stage.ItemId ?? string.Empty;

    /// <summary>候场态：静音、起播即停（幕外那页用它预热下一条）。</summary>
    public bool Standby
    {
        get => Stage.Standby;
        set => Stage.Standby = value;
    }

    /// <summary>换成本条：舞台上会去装载新地址；起播与静音由 <see cref="Standby"/> 决定。
    /// 使用账本的计时在舞台里按播放状态累计，页面不再按“看了几条”记账。</summary>
    public void Bind(VideoEntry entry, string status = "")
    {
        StatusText = status;
        Entry = entry;
    }

    /// <summary>来源评论按 cid 置顶到评论列表（分享卡详情用）。</summary>
    public void SetPinnedComment(string commentId, string author, string text, string imageUrl) =>
        Stage.SetPinnedComment(commentId, author, text, imageUrl);

    public void ClearPinnedComment() => Stage.ClearPinnedComment();

    public void OpenCommentsPanel() => Stage.OpenCommentsPanel();

    public void CloseCommentsPanel() => Stage.CloseCommentsPanel();

    /// <summary>宿主离场：松开播放源与订阅（锁屏式的幕外页不再偷偷装载）。</summary>
    public void Suspend() => Stage.Suspend();

    /// <summary>宿主回场：重新架起播放链路，按当前 <see cref="Entry"/> 重装一次。</summary>
    public void Resume() => Stage.Resume();

    /// <summary>
    /// 点了作者头像（舞台回抛的）。这一页不是用 Frame 导航出来的、手里没有 Frame，
    /// 所以原样再往上一抛，让宿主（推荐页 / 分享视频页）用它们的 Frame 去开主页。
    /// </summary>
    public event EventHandler<Controls.MfStageControl.AuthorOpenEventArgs>? AuthorOpenRequested;

    private void OnAuthorOpen(object sender, Controls.MfStageControl.AuthorOpenEventArgs e) =>
        AuthorOpenRequested?.Invoke(this, e);
}
