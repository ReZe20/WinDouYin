
using WinDouYin.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinDouYin.Service;
using WinDouYin.ViewModels;

namespace WinDouYin.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        // 设置页从不自己 new ConfigService：状态归 App 持有的共享单例
        var app = Application.Current as App;
        ViewModel = app?.ViewModel ?? new SettingsViewModel(new ConfigService());
        InitializeComponent();
        DataContext = this;
    }

    private async void ResetSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = Strings.Get("Settings_ResetConfirm.Title"),
            Content = Strings.Get("Settings_ResetConfirm.Content"),
            PrimaryButtonText = Strings.Get("Settings_ResetConfirm.PrimaryButtonText"),
            CloseButtonText = Strings.Get("Settings_ResetConfirm.CloseButtonText"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ResetAllSettingsAsync();
        }
    }
}
