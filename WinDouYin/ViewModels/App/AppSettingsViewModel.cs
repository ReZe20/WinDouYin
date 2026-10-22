
using WinDouYin.Contracts;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WinDouYin.Models;

namespace WinDouYin.ViewModels;

/// <summary>程序级设置：启动页面。</summary>
public partial class AppSettingsViewModel : ObservableObject
{
    /// <summary>
    /// 下拉数据源：<see cref="AppPages.StartupChoices"/> 仍是唯一事实源，这里只把它灌成
    /// <see cref="ObservableCollection{T}"/>——XAML 的 <c>ItemsSource</c> 过 WinRT 向量那道口只认这一种，
    /// 交 <c>IReadOnlyList</c>/数组会在 x:Bind 设源当场抛（点开设置就断在 <c>Debugger.Break()</c>、
    /// 报 "Value does not fall within the expected range."；exception.log 里 2026-10-19 20:16 那条
    /// <c>IItemsControlMethods.set_ItemsSource → SettingsPage_obj1_Bindings.Update_ViewModel_AppSettingsVM</c>
    /// 就是它，热力图那台 <c>ItemsRepeater</c> 是同一家族，见 <see cref="UsageHeatmapViewModel.Days"/>）。
    /// 候选项运行期不变，一次灌好即可。
    /// </summary>
    public ObservableCollection<AppPage> StartupPageOptions { get; } = [.. AppPages.StartupChoices];

    /// <summary>启动页面 Tag，取值见 <see cref="AppPages.StartupChoices"/>。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedStartupPage))]
    public partial string StartupPage { get; set; } = "Featured";

    /// <summary>
    /// 下拉框那格实际选中的页面对象：读走 <see cref="AppPages.Find"/>，写回落到 <see cref="StartupPage"/>
    /// （落盘的仍是那个 Tag 字符串，口径没变）。AOT 收口时用它替掉 ComboBox 的
    /// DisplayMemberPath/SelectedValuePath 反射路径——那两条在裁剪后不保证还能解析。
    /// </summary>
    public AppPage SelectedStartupPage
    {
        get => AppPages.Find(StartupPage) ?? AppPages.StartupChoices[0];
        set => StartupPage = value.Tag;
    }

    /// <summary>播放音量（0~100）。播放组件按这个值同时设两个声道，换台才不会掉回 100%。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeText))]
    public partial int Volume { get; set; } = 100;

    /// <summary>
    /// 音量条上那行百分比文字（"80%"）：由 Volume 派生，XAML 里 x:Bind 它。
    /// 显示值就该跟着数据走——不要在代码里 <c>VolumeText.Text = …</c> 去戳控件。
    /// </summary>
    public string VolumeText => Volume + "%";

    /// <summary>自动连播：改成这个值就自动落盘（SettingsViewModel 防抖 500ms），视频页的开关直接绑它。</summary>
    [ObservableProperty]
    public partial bool AutoPlay { get; set; }

    /// <summary>小窗的客户区宽度（物理像素）；用户每拖一次小窗就更新，随设置落盘。</summary>
    [ObservableProperty]
    public partial int MiniWidth { get; set; } = 400;

    /// <summary>小窗的客户区高度（物理像素）。</summary>
    [ObservableProperty]
    public partial int MiniHeight { get; set; } = 225;

    /// <summary>弹幕开关（面板上那枚「弹」）：写它即防抖落盘，换台时新上场那页从这儿读。</summary>
    [ObservableProperty]
    public partial bool DanmakuOn { get; set; } = true;

    /// <summary>弹幕不透明度（百分比）。</summary>
    [ObservableProperty]
    public partial int DanmakuOpacity { get; set; } = 80;

    /// <summary>弹幕显示区域：0/1/2 → 单行/两行/三行。</summary>
    [ObservableProperty]
    public partial int DanmakuRows { get; set; } = 1;

    /// <summary>弹幕字体档：0/1/2 → 较小/适中/较大。</summary>
    [ObservableProperty]
    public partial int DanmakuFont { get; set; } = 1;

    /// <summary>弹幕速度档：0/1/2 → 较慢/适中/较快。</summary>
    [ObservableProperty]
    public partial int DanmakuSpeed { get; set; } = 1;

    /// <summary>视频磁盘缓存开关（默认开）：改动即生效并防抖落盘。</summary>
    [ObservableProperty]
    public partial bool VideoDiskCache { get; set; } = true;

    /// <summary>
    /// 清晰度档（空串=默认）：面板上那枚「清晰度」写它即防抖落盘，换台时新上场那页从这儿读。
    /// 存的是分辨率数字（"1080"/"720"…），这条视频没有那一档就自动降档（逻辑在舞台控件里）。
    /// </summary>
    [ObservableProperty]
    public partial string QualityKey { get; set; } = string.Empty;

    public void LoadFrom(AppSettings settings)
    {
        StartupPage = string.IsNullOrEmpty(settings.StartupPage) ? "Featured" : settings.StartupPage;
        Volume = Math.Clamp(settings.Volume, 0, 100);
        AutoPlay = settings.AutoPlay;
        MiniWidth = Math.Clamp(settings.MiniWidth, 240, 3840);
        MiniHeight = Math.Clamp(settings.MiniHeight, 135, 2160);
        DanmakuOn = settings.DanmakuOn;
        DanmakuOpacity = Math.Clamp(settings.DanmakuOpacity, 0, 100);
        DanmakuRows = Math.Clamp(settings.DanmakuRows, 0, 2);
        DanmakuFont = Math.Clamp(settings.DanmakuFont, 0, 2);
        DanmakuSpeed = Math.Clamp(settings.DanmakuSpeed, 0, 2);
        VideoDiskCache = settings.VideoDiskCache;
        QualityKey = settings.QualityKey ?? string.Empty;
    }

    public void SaveTo(AppSettings settings)
    {
        settings.StartupPage = StartupPage;
        settings.Volume = Volume;
        settings.AutoPlay = AutoPlay;
        settings.MiniWidth = MiniWidth;
        settings.MiniHeight = MiniHeight;
        settings.DanmakuOn = DanmakuOn;
        settings.DanmakuOpacity = DanmakuOpacity;
        settings.DanmakuRows = DanmakuRows;
        settings.DanmakuFont = DanmakuFont;
        settings.DanmakuSpeed = DanmakuSpeed;
        settings.VideoDiskCache = VideoDiskCache;
        settings.QualityKey = QualityKey;
    }
}
