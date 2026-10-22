
using WinDouYin.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinDouYin.ViewModels;

namespace WinDouYin.Controls;

public sealed partial class ReplyCardControl : UserControl
{
    public ReplyCardControl()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bindings.Update();
    }

    /// <summary>模板里的数据行。x:Bind 走它，避免每张回复卡二十多条反射绑定。</summary>
    public CommentReplyRowViewModel? Row => DataContext as CommentReplyRowViewModel;

    /// <summary>点了卡里的图片/表情包：把原图地址抛给宿主（评论面板那边负责弹大图）。</summary>
    public event EventHandler<string>? ImageTapped;

    private void OnExpandClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CommentReplyRowViewModel reply)
        {
            reply.Toggle?.Invoke(reply);
        }
    }

    private void OnImageClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url } && url.Length > 0)
        {
            ImageTapped?.Invoke(this, url);
        }
    }

    /// <summary>点这颗心：回复卡的 VM 自带回调（宿主负责发写请求），卡片不碰网络。</summary>
    private void OnLikeClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CommentReplyRowViewModel reply)
        {
            reply.Like?.Invoke(reply);
        }
    }

    /// <summary>点"回复"：把这条回复的 VM 抛给宿主，宿主切到"回复 @这条"的输入状态。</summary>
    private void OnReplyClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CommentReplyRowViewModel reply)
        {
            reply.Reply?.Invoke(reply);
        }
    }

    /// <summary>子回复卡里的图：逐层往上传（子卡不会自己冒到宿主控件）。</summary>
    private void OnChildImageTapped(object? sender, string url) => ImageTapped?.Invoke(this, url);
}
