
using WinDouYin.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinDouYin.ViewModels;

/// <summary>
/// 账号菜单里的一行。这里只是 <see cref="WinDouYin.Service.SavedAccount"/> 的显示壳：
/// 档案里的会话与钥匙一概不带进界面，行上只留昵称、头像和"是不是当前号"。
/// </summary>
public sealed partial class AccountRowViewModel : ObservableObject
{
    public AccountRowViewModel(string uid, string nickname, string avatarUrl, bool isCurrent)
    {
        Uid = uid;
        Nickname = nickname.Length > 0 ? nickname : "未命名的抖音账号";
        AvatarUrl = avatarUrl;
        IsCurrent = isCurrent;
    }

    public string Uid { get; }

    public string Nickname { get; }

    public string AvatarUrl { get; }

    public bool IsCurrent { get; }

    public Visibility CurrentMarkVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>头像按账号档案里那条 URL 现取（URL 会换，所以不落盘缓存）。拿不到就画人形字形。</summary>
    [ObservableProperty]
    public partial ImageSource? Avatar { get; set; }

    public bool HasAvatar => Avatar is not null;

    public bool HasGlyph => Avatar is null;

    public string AvatarTip => IsCurrent ? Nickname + "（当前账号）" : Nickname;

    partial void OnAvatarChanged(ImageSource? value)
    {
        OnPropertyChanged(nameof(HasAvatar));
        OnPropertyChanged(nameof(HasGlyph));
    }
}
