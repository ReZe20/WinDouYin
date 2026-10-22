
using WinDouYin.Contracts;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WinDouYin.Models;
using WinDouYin.Service;

namespace WinDouYin.ViewModels;

/// <summary>
/// 全应用唯一的设置视图模型，由 App 构造时创建；各页面从 App 取同一实例，
/// 因此任何子 VM 的属性变化都会触发自动保存，设置页与功能页天然同步。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    // ==== 持久化 ====
    private readonly IConfigService _configService;
    private AppSettings _settings = new();
    private CancellationTokenSource? _saveCts;
    private readonly TimeSpan _saveDelay = TimeSpan.FromMilliseconds(500);
    private readonly SemaphoreSlim _saveSemaphore = new(1, 1);
    private int _saveGuard;

    // ==== 批量守卫 ====
    private bool _isBatchUpdating;
    private bool _initialized;

    // ==== 子 VM ====
    public AppSettingsViewModel AppSettingsVM { get; } = new();

    /// <summary>登录态不参与 settings.json 的防抖保存：它落在 session.cookies.json，由扫码流程自己写。</summary>
    public LoginViewModel LoginVM { get; } = new();

    public SettingsViewModel(IConfigService configService)
    {
        _configService = configService;

        // 任意子 VM 的属性变化都触发保存
        AppSettingsVM.PropertyChanged += OnSubViewModelPropertyChanged;
    }

    private void OnSubViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 磁盘缓存开关要立刻生效，不等 500ms 防抖落盘（批量装载时由 ApplySettingsToViewModels 统一对齐）
        if (e.PropertyName == nameof(AppSettingsViewModel.VideoDiskCache))
        {
            VideoDiskCache.Enabled = AppSettingsVM.VideoDiskCache;
        }

        if (_isBatchUpdating) return;
        _ = SaveAsync();
    }

    public async Task InitializeAsync()
    {
        // 幂等守卫：App 启动与页面首载都调本方法，第二次直接跳过
        if (_initialized) return;
        _initialized = true;

        _isBatchUpdating = true;
        _settings = await _configService.LoadAsync();
        ApplySettingsToViewModels();
        _isBatchUpdating = false;

        await SaveAsync();
        OnPropertyChanged(string.Empty);
    }

    /// <summary>恢复默认：装载默认模型后统一存一次，并全量刷新级联项。</summary>
    public async Task ResetAllSettingsAsync()
    {
        _isBatchUpdating = true;
        _settings = new AppSettings();
        ApplySettingsToViewModels();
        _isBatchUpdating = false;

        await SaveAsync();
        OnPropertyChanged(string.Empty);
    }

    /// <summary>把 _settings 的当前值填充到所有子 VM（InitializeAsync 与 ResetAllSettingsAsync 共用）。</summary>
    private void ApplySettingsToViewModels()
    {
        AppSettingsVM.LoadFrom(_settings);
        VideoDiskCache.Enabled = AppSettingsVM.VideoDiskCache;
    }

    private async Task SaveAsync()
    {
        if (_isBatchUpdating) return;

        // 守卫：同一时刻只允许一笔保存进入执行体，后续冗余调用静默丢弃
        if (Interlocked.Exchange(ref _saveGuard, 1) == 1) return;

        try
        {
            // 防抖：取消上一笔待写，500ms 内无新变化才落盘
            _saveCts?.Cancel();
            _saveCts = new CancellationTokenSource();
            var token = _saveCts.Token;

            try
            {
                await Task.Delay(_saveDelay, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await _saveSemaphore.WaitAsync();
            try
            {
                AppSettingsVM.SaveTo(_settings);
                await _configService.SaveAsync(_settings);
            }
            finally
            {
                _saveSemaphore.Release();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _saveGuard, 0);
        }
    }
}
