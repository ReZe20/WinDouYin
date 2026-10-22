
using WinDouYin.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinDouYin.ViewModels;

namespace WinDouYin.Controls;

/// <summary>
/// 标题栏右上角那一格账号头像。做成控件而不是写在 Window 里：
/// Window 内的 x:Bind 生成类要靠 FrameworkElement 找转换器，Window 不是 FrameworkElement。
/// 点它弹账号菜单：已保存的账号直接列在这一排，点谁切谁；"添加账号"喊宿主开登录框，
/// "退出登录"只管当前号，其他号留在档案里等下一次点回来。
/// </summary>
public sealed partial class AccountBadge : UserControl
{
    public SettingsViewModel ViewModel { get; } = ((App)Application.Current).ViewModel;

    public string AccountTip => Strings.Get("TitleBar_Account");

    /// <summary>菜单里"登录"被按了就喊宿主把居中登录面板弹出来。</summary>
    public event Action? LoginRequested;

    /// <summary>菜单里"添加账号"被按了就喊宿主以"加号"模式开登录面板。</summary>
    public event Action? AddAccountRequested;

    /// <summary>菜单里"消息"被按了就喊宿主把右侧消息面板弹出来。</summary>
    public event Action? MessagesRequested;

    public AccountBadge()
    {
        InitializeComponent();

        // 菜单内容跟着登录态走：开的那一刻现算，省得为两种状态各养一套绑定
        AccountFlyout.Opening += (_, _) => SyncMenuState();
    }

    private void SyncMenuState()
    {
        ViewModel.LoginVM.ClearAccountMessage();
        ViewModel.LoginVM.RefreshAccounts();

        bool on = ViewModel.LoginVM.IsLoggedIn;
        FlyoutHeader.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        FlyoutMessages.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        FlyoutLogin.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        FlyoutSignOut.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFlyoutMessages(object sender, RoutedEventArgs e)
    {
        AccountFlyout.Hide();
        MessagesRequested?.Invoke();
    }

    private void OnFlyoutLogin(object sender, RoutedEventArgs e)
    {
        AccountFlyout.Hide();
        LoginRequested?.Invoke();
    }

    private void OnFlyoutAddAccount(object sender, RoutedEventArgs e)
    {
        AccountFlyout.Hide();
        AddAccountRequested?.Invoke();
    }

    /// <summary>
    /// 退出登录：只把当前号从档案和会话罐里清掉；其他号仍留在这一排，下一次点回来不必重扫。
    /// </summary>
    private void OnFlyoutSignOut(object sender, RoutedEventArgs e)
    {
        AccountFlyout.Hide();
        _ = ViewModel.LoginVM.SignOutAsync();
    }

    /// <summary>
    /// 点某个已保存账号就切过去。切失败时弹层留着，好让人看见那句"为什么没切开"；
    /// 切成功才收起——头像格自己会跟着 LoginVM.Avatar 换人。
    /// </summary>
    private async void OnAccountRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string uid } || uid.Length == 0)
        {
            return;
        }

        if (ViewModel.LoginVM.IsAccountBusy)
        {
            return;
        }

        bool isCurrent = string.Equals(Douyin.Feed.CurrentAccountUid, uid, StringComparison.Ordinal)
            && ViewModel.LoginVM.IsLoggedIn;
        if (isCurrent)
        {
            AccountFlyout.Hide();
            return;
        }

        if (await ViewModel.LoginVM.SwitchAccountAsync(uid))
        {
            AccountFlyout.Hide();
        }
    }
}
