
using WinDouYin.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WinDouYin.ViewModels;

/// <summary>
/// 面板上那枚「清晰度」的全部界面状态：正在放哪一档 + 这条视频真有哪些档。
/// 舞台控件换条视频、换档、改设置时（以及媒体开出来、起播那两下）重铺一遍，
/// 显示值一律经这里绑出去——代码不往控件上写字（用户的规矩：值要有出处，靠 x:Bind 走）。
///
/// 菜单是死档位表（XAML 里那五行 RadioMenuFlyoutItem）：哪几行可用由 <see cref="Available"/> 经转换器算出来，
/// 那枚点由 SyncQualityMenu 从代码里摆——与倍速的 SyncSpeedFlyout 同一路（RadioMenuFlyoutItem 的点
/// 从绑定驱动时不渲染，只有点选那一下的内部单选组记账与代码写 IsChecked 是可靠的）。
/// </summary>
public partial class QualityPickerViewModel : ObservableObject
{
    /// <summary>
    /// 这条视频真有的档位（Key："2160"/"1440"/"1080"…，没有就是空表）。
    /// 每次都整份换掉——绑定靠 PropertyChanged 重算，别在旧表上原地增删。
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> Available { get; set; } = [];

    /// <summary>正在放的那一档（菜单里那枚点落在它身上；认不出就是空串，谁都不点）。</summary>
    [ObservableProperty]
    public partial string CurrentKey { get; set; } = string.Empty;

    /// <summary>按钮脸上的当前档（"1080P" …）——**实际在放**的那一档，不是记住的那一档；认不出时显示控件名。</summary>
    [ObservableProperty]
    public partial string CurrentLabel { get; set; } = "清晰度";
}
