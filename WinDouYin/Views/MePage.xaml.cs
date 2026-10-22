
using WinDouYin.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinDouYin.Service;
using WinDouYin.ViewModels;

namespace WinDouYin.Views;

public sealed partial class MePage : Page
{
    public SettingsViewModel ViewModel { get; }

    public MePage()
    {
        // 状态归 App 持有的共享单例，页面不自建
        var app = Application.Current as App;
        ViewModel = app?.ViewModel ?? new SettingsViewModel(new ConfigService());
        InitializeComponent();
        DataContext = this;
    }

    /// <summary>走到这一站就把登录框撑起来：面板自己不会建内核，得有人喊一句。</summary>
    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        FitLoginPanel();
        LoginPanelHost.Restart();
    }

    /// <summary>内嵌场景同样按登录卡片比例（726:483）吃页面预算，免得溢出或被拉变形。</summary>
    private void FitLoginPanel()
    {
        LoginPanelHost.FitTo(
            Math.Max(360, ActualWidth - 240),
            Math.Max(260, ActualHeight - 200));
    }

    /// <summary>离开这一站就停掉轮询：码没人看还一直打 passport，是白给的风险。</summary>
    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        LoginPanelHost.Cancel();
    }
}
