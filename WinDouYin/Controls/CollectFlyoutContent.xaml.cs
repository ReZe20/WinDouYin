using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinDouYin.Contracts;
using WinDouYin.ViewModels;

namespace WinDouYin.Controls;

/// <summary>
/// 收藏夹悬浮面板的内容：只负责勾选状态与三态（载入/列表/错误），不碰网络。
/// 写入时机由宿主（<see cref="MfStageControl"/>）在 flyout 关闭后统一防抖处理。
/// </summary>
public sealed partial class CollectFlyoutContent : UserControl
{
    private readonly ObservableCollection<CollectFolderRow> _rows = [];

    public CollectFlyoutContent()
    {
        InitializeComponent();
        FoldersList.ItemsSource = _rows;
        SetLoading();
    }

    /// <summary>勾选/取消勾选（宿主据此更新待写入集合）。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>错误态点了"重试"。</summary>
    public event EventHandler? RetryRequested;

    public int SelectedCount => _rows.Count(static r => r.IsChecked == true);

    public IReadOnlyList<CollectFolder> SelectedFolders =>
        _rows.Where(static r => r.IsChecked == true)
            .Select(static r => new CollectFolder(r.Id, r.Name, r.Count))
            .ToList();

    public void SetLoading()
    {
        ListHost.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Visible;
    }

    public void SetFolders(IReadOnlyList<CollectFolder> folders)
    {
        _rows.Clear();
        foreach (CollectFolder folder in folders)
        {
            _rows.Add(new CollectFolderRow(folder));
        }

        StatusText.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListHost.Visibility = folders.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void SetError(string message)
    {
        ListHost.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    /// <summary>每次打开面板时清空上一回的勾选。</summary>
    public void ResetSelection()
    {
        foreach (CollectFolderRow row in _rows)
        {
            row.IsChecked = false;
        }
    }

    private void OnFolderChecked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CollectFolderRow row)
        {
            row.IsChecked = true;
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnFolderUnchecked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CollectFolderRow row)
        {
            row.IsChecked = false;
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRetryClick(object sender, RoutedEventArgs e) => RetryRequested?.Invoke(this, EventArgs.Empty);
}
