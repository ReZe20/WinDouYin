using System.ComponentModel;
using WinDouYin.Contracts;

namespace WinDouYin.ViewModels;

/// <summary>收藏夹悬浮面板里的一行：id/名字/作品数 + 复选框状态（未勾选是每次打开时的初始态）。</summary>
public sealed partial class CollectFolderRow : INotifyPropertyChanged
{
    // CheckBox.IsChecked 是 bool?：null 会画成"半勾"。初始态必须是明确的未勾选。
    private bool? _isChecked = false;

    public CollectFolderRow(CollectFolder folder)
    {
        Id = folder.Id;
        Name = folder.Name;
        Count = folder.Count;
        CountText = folder.Count > 0 ? WinDouYin.CountText.Format(folder.Count) + " 个作品" : string.Empty;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public string Name { get; }

    public int Count { get; }

    public string CountText { get; }

    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }
}
