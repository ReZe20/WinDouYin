using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinDouYin.Contracts;

namespace WinDouYin.Controls;

/// <summary>
/// 登录这一块的容器。真正的登录界面是官方页面（LoginWebView 里那个框），这里只做四件事：
/// 把会话接进原生罐子、把状态话说给人看、在浮层开合时开关那个内核、分得清"重新登录"和"再加一个号"。
/// </summary>
public sealed partial class LoginPanel : UserControl
{
    public ViewModels.SettingsViewModel ViewModel { get; } = ((App)Application.Current).ViewModel;

    private LoginWebView? _web;
    private double _fitW;
    private double _fitH;
    private bool _addingAccount;

    /// <summary>登录成功后请宿主收起面板（居中面板用；内嵌的"我的"站没人听就没人听）。</summary>
    public event Action? DismissRequested;

    public LoginPanel()
    {
        InitializeComponent();
    }

    /// <summary>宿主给的尺寸预算：里面的登录框按登录卡片比例（726:483）缩放适配。</summary>
    public void FitTo(double maxW, double maxH)
    {
        _fitW = maxW;
        _fitH = maxH;
        _web?.FitTo(maxW, maxH);
    }

    /// <summary>宿主开的是"添加账号"面板：已登录也必须挂一张新码，登回来的号加进档案而不是顶掉当前号。</summary>
    public void BeginAddAccount() => _addingAccount = true;

    public void EndAddAccount() => _addingAccount = false;

    /// <summary>
    /// 浮层每次打开都重新挂一次登录框：上一回那张码多半已经过期，留着只会让人扫一张死码。
    /// WebView2 是这里唯一的重东西，所以到这一步才建它——启动路径上绝不能碰到。
    /// </summary>
    public void Restart()
    {
        if (ViewModel.LoginVM.IsLoggedIn && !_addingAccount)
        {
            // 已登录且不是来加号的，就没登录框什么事：把上次留下的那页藏掉，只留状态话和"退出登录"
            if (_web is not null)
            {
                _web.Visibility = Visibility.Collapsed;
            }
            Status.Text = ViewModel.LoginVM.StatusText;
            return;
        }

        Status.Text = _addingAccount ? "登录一个新账号（当前账号会保留）" : string.Empty;
        _web ??= Create();
        _web.Restart();
    }

    public void Cancel() => _web?.Stop();

    private LoginWebView Create()
    {
        var view = new LoginWebView();
        if (_fitW > 0 && _fitH > 0)
        {
            view.FitTo(_fitW, _fitH);
        }
        // 登录态不能被"面板又打开了"这件事重启掉（Loaded 会跟着面板开合反复响）；加号那一趟除外。
        view.ShouldAutoStart = () => _addingAccount || !ViewModel.LoginVM.IsLoggedIn;
        view.SessionLanded += OnSessionLanded;
        view.Failed += msg => Status.Text = "登录框起不来：" + msg;
        view.StatusChanged += note => Status.Text = note switch
        {
            _ when note.StartsWith("isolated", StringComparison.Ordinal) => "已裁出登录框 " + note.Split(' ')[1],
            _ when note.StartsWith("verify", StringComparison.Ordinal) => "需要二次验证：请按抖音提示用手机完成（这一步它要再扫一次码）",
            _ when note.StartsWith("pass", StringComparison.Ordinal) => "交给抖音的验证流程了：按它的提示继续完成即可",
            _ when note.StartsWith("no-modal", StringComparison.Ordinal) => "页面里还没有登录框，正试着把它点开",
            _ when note.Contains("err:", StringComparison.Ordinal) => "裁剪脚本出错：" + note,
            _ => note,
        };
        WebHost.Children.Add(view);
        return view;
    }

    private void OnReload(object sender, RoutedEventArgs e) => Restart();

    /// <summary>退出登录后立刻把登录框重新开出来，一步到位换号。</summary>
    private async void OnSignOut(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoginVM.SignOutAsync();
        Status.Text = ViewModel.LoginVM.StatusText;
        Restart();
    }

    private async void OnSessionLanded(IReadOnlyList<SessionCookie> seeds)
    {
        await ViewModel.LoginVM.AdoptSessionAsync(seeds);
        Status.Text = ViewModel.LoginVM.StatusText;
        if (ViewModel.LoginVM.IsLoggedIn)
        {
            DismissRequested?.Invoke();
        }
    }
}
