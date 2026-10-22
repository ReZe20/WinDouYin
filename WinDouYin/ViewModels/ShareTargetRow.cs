using Microsoft.UI.Xaml.Media;
using WinDouYin.Contracts;

namespace WinDouYin.ViewModels;

/// <summary>分享面板里的一个可发送会话：头像/名字摊平好给 x:Bind 直接吃。</summary>
public sealed class ShareTargetRow
{
    public ShareTargetRow(ImConversation conversation)
    {
        Conversation = conversation;
        Name = conversation.Title.Length > 0 ? conversation.Title : "抖音用户";
        Initials = Name[..1];
        Avatar = MessageRow.ImageOf(conversation.AvatarUrl);
    }

    public ImConversation Conversation { get; }

    public string Name { get; }

    public string Initials { get; }

    public ImageSource? Avatar { get; }
}
