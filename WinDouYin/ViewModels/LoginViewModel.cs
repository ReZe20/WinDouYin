using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using WinDouYin.Controls;
using WinDouYin.Contracts;
using WinDouYin.Service;

namespace WinDouYin.ViewModels;

/// <summary>
/// 登录这一趟的状态。出码、扫码、过验证全部交给 <see cref="LoginWebView"/>（真浏览器内核），
/// 这里只接它读回来的会话：灌进原生罐子、落进多账号档、去问一次"我是谁"。
///
/// 多账号之后这个类还兼一份"当前是排好的哪一号"：账号菜单要能列、能加、能切，
/// 但会话罐与 guard 钥匙的搬迁一律在 <see cref="FeedService"/> 里做，界面只拿回执。
/// </summary>
public sealed partial class LoginViewModel : ObservableObject
{
    private readonly DispatcherQueue? _ui = DispatcherQueue.GetForCurrentThread();

    // AOT 收口：partial 属性替代字段式 [ObservableProperty]（MVVMTK0045），理由见 ForYouViewModel 同处注释。
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoggedIn { get; set; }

    /// <summary>已登录且拿到了头像才换图，否则标题栏那格仍是 E710 圆框。</summary>
    [ObservableProperty]
    public partial ImageSource? Avatar { get; set; }

    /// <summary>登录上的那个账号叫什么。头像拿不到时，面板至少要说得出"登进来的是谁"。</summary>
    [ObservableProperty]
    public partial string Nickname { get; set; } = string.Empty;

    /// <summary>账号菜单里上一次动作的落点（切号失败、目标号过期）。菜单打开时清掉，不留着反复吓人了。</summary>
    [ObservableProperty]
    public partial string AccountMessage { get; set; } = string.Empty;

    /// <summary>切号在飞：别让人连点两下把两个号往罐子里倒。</summary>
    [ObservableProperty]
    public partial bool IsAccountBusy { get; set; }

    /// <summary>这台机器上登过的号（含当前号）。账号菜单直接铺这一排。</summary>
    public ObservableCollection<AccountRowViewModel> Accounts { get; } = [];

    /// <summary>标题栏那格显示占位字形还是真头像。</summary>
    public bool IsGlyphVisible => Avatar is null;

    public bool IsAvatarVisible => Avatar is not null;

    public Visibility AccountMessageVisibility => AccountMessage.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility AccountsVisibility => Accounts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 当前会话换了/退了/切回来了，宿主按这个信号收起并重建账号相关页面。
    /// 只看 <see cref="IsLoggedIn"/> 不够——A 号切 B 号时它一直是 true。
    /// </summary>
    public event Action? AccountChanged;

    /// <summary>
    /// 会话是从盘上灌回来的，所以启动流程要在还原之后回填一次登录态——这回是**真问服务端**的
    /// （罐里有 sessionid 不等于没过期，见 NativeFeed.LoginVerified）。
    /// </summary>
    public async Task RefreshLoginStateAsync()
    {
        await Douyin.Feed.RefreshLoginStateAsync();
        Douyin.Feed.SyncUsageAccount();   // 登录态这一问回来才拿到 uid：使用账本跟着换到/退出当前号

        // 真登录态为准；问不出来（null）才退回"罐里有没有 sessionid"
        IsLoggedIn = Douyin.Feed.LoginVerified ?? Douyin.Feed.IsLoggedIn;
        if (!IsLoggedIn && Douyin.Feed.IsLoggedIn)
        {
            StatusText = "登录已过期，重新登一次";
        }

        SavedAccountInfo? current = Douyin.Feed.CurrentSavedAccount();
        if (IsLoggedIn && current is not null)
        {
            if (Nickname.Length == 0 && current.Nickname.Length > 0)
            {
                Nickname = current.Nickname;
            }

            if (Avatar is null && current.AvatarUrl.Length > 0)
            {
                string url = current.AvatarUrl;
                await RunOnUiAsync(async () =>
                {
                    SoftwareBitmapSource? source = await BlurCover.LoadAvatarAsync(url);
                    if (source is not null)
                    {
                        Avatar = source;
                    }
                });
            }
        }

        RefreshAccounts();

        if (IsLoggedIn && (Nickname.Length == 0 || Avatar is null))
        {
            _ = LoadSelfAsync();
        }
    }

    /// <summary>
    /// 登录框摸到 sessionid 之后的收尾：这条会话可能是"第一次登录"，也可能是"再加一个号"。
    /// 会话罐与账号档案的搬迁、失败回滚都在 <see cref="Douyin.Feed.AdoptSessionAsync"/> 里；
    /// 这里只把回执写成界面状态。**判"登上了没"以认得出 uid 为准**（刚登完服务端偶尔慢半拍，见 FeedService）。
    /// </summary>
    public async Task AdoptSessionAsync(IReadOnlyList<SessionCookie> seeds)
    {
        AccountMessage = string.Empty;
        AccountResult result = await Douyin.Feed.AdoptSessionAsync(seeds);
        if (result.Kind == AccountOutcome.Success)
        {
            IsLoggedIn = true;
            Avatar = null;
            Nickname = result.Nickname;
            StatusText = result.Nickname.Length > 0 ? "登录成功：" + result.Nickname : "登录成功";
            RefreshAccounts();
            Douyin.Feed.PrefetchCollects();
            Douyin.Feed.PrefetchMessages();
            await LoadSelfAsync();
            RaiseAccountChanged();
            _ = ReverifyLoginLaterAsync();
        }
        else
        {
            await RestoreUiFromCurrentSessionAsync();
            StatusText = result.Message;
            AccountMessage = result.Message;
            RaiseAccountChanged();
        }
    }

    /// <summary>开"添加账号"面板之前先把当前号落档；登录框那一下会换掉全局 guard 材料。</summary>
    public async Task PrepareAddAccountAsync()
    {
        AccountMessage = string.Empty;
        if (IsLoggedIn)
        {
            await Douyin.Feed.PersistCurrentAccountAsync();
        }

        RefreshAccounts();
    }

    /// <summary>切到已保存的某个号。成功后会话、缓存、使用账本和界面都跟着换。返回是否真的切开。</summary>
    public async Task<bool> SwitchAccountAsync(string uid)
    {
        if (IsAccountBusy || uid.Length == 0)
        {
            return false;
        }

        if (string.Equals(Douyin.Feed.CurrentAccountUid, uid, StringComparison.Ordinal)
            && IsLoggedIn
            && Douyin.Feed.MyUid.Length > 0)
        {
            AccountMessage = string.Empty;
            return true;
        }

        IsAccountBusy = true;
        AccountMessage = string.Empty;
        bool ok;
        try
        {
            AccountResult result = await Douyin.Feed.SwitchAccountAsync(uid);
            ok = result.Kind == AccountOutcome.Success;
            if (ok)
            {
                IsLoggedIn = true;
                Avatar = null;
                Nickname = result.Nickname;
                StatusText = result.Nickname.Length > 0 ? "已切换到：" + result.Nickname : "已切换账号";
                RefreshAccounts();
                Douyin.Feed.PrefetchCollects();
                Douyin.Feed.PrefetchMessages();
                await LoadSelfAsync();
            }
            else
            {
                await RestoreUiFromCurrentSessionAsync();
                StatusText = result.Message;
                AccountMessage = result.Message;
            }
        }
        finally
        {
            IsAccountBusy = false;
        }

        RaiseAccountChanged();
        return ok;
    }

    /// <summary>刚登完服务端可能慢半拍：过两秒再问一次真登录态，问出来就记上（问不出来保持原样）。</summary>
    private async Task ReverifyLoginLaterAsync()
    {
        await Task.Delay(2000);
        await Douyin.Feed.RefreshLoginStateAsync();
        Douyin.Feed.SyncUsageAccount();
    }

    /// <summary>
    /// 退出当前登录：档案里当前号被抹掉、罐子退回匿名。
    /// 其他已保存的号留在列表里（"退出这个号"不等于"这台机器上登过的都没了"）。
    /// 登录框自己那个 profile 不在这里动——它每次开都是清的（见 LoginWebView）。
    /// </summary>
    public async Task SignOutAsync()
    {
        AccountMessage = string.Empty;
        await Douyin.Feed.SignOutAsync();
        IsLoggedIn = false;
        Nickname = string.Empty;
        Avatar = null;
        StatusText = "已退出登录";
        RefreshAccounts();
        RaiseAccountChanged();
    }

    /// <summary>账号菜单开时调一句：上一次的失败提示不该跟着下一次开合反复出来。</summary>
    public void ClearAccountMessage() => AccountMessage = string.Empty;

    /// <summary>从档案重建账号那一排；头像随后按档案 URL 各取一张。</summary>
    public void RefreshAccounts()
    {
        string current = Douyin.Feed.CurrentAccountUid;
        List<AccountRowViewModel> rows =
        [
            .. Douyin.Feed.RecentSavedAccounts().Select(a => new AccountRowViewModel(a.Uid, a.Nickname, a.AvatarUrl, a.Uid == current)),
        ];

        Accounts.Clear();
        foreach (AccountRowViewModel row in rows)
        {
            Accounts.Add(row);
        }

        OnPropertyChanged(nameof(AccountsVisibility));
        _ = LoadAccountAvatarsAsync(rows);
    }

    /// <summary>
    /// 问一次"我是谁"：昵称直接上面板，头像走封面那套现成的取图+解码管线（BlurCover 自带缩解与缓存）。
    /// 这一发失败不该影响"已登录"这件事本身，所以只把状态话写清楚。
    /// </summary>
    private async Task LoadSelfAsync()
    {
        try
        {
            // 昵称/头像以用户资料接口为准（account/info/v2 的 name 是账号默认名，会跟主页显示不一致）；
            // 资料拿不到再退回 passport 的身份那条。
            OwnProfile self = await Douyin.Feed.SignIn.ProfileAsync(CancellationToken.None);
            if (self.Nickname.Length == 0)
            {
                self = await Douyin.Feed.SignIn.SelfAsync(CancellationToken.None);
            }

            if (self.Nickname.Length > 0)
            {
                Nickname = self.Nickname;
            }

            StatusText = Nickname.Length > 0 ? "已登录：" + Nickname : "已登录（账号信息没拿到）";

            if (self.AvatarUrl.Length > 0)
            {
                string url = self.AvatarUrl;
                await RunOnUiAsync(async () =>
                {
                    SoftwareBitmapSource? source = await BlurCover.LoadAvatarAsync(url);
                    if (source is not null)
                    {
                        Avatar = source;
                    }
                });
            }

            await Douyin.Feed.UpdateCurrentProfileAsync(Douyin.Feed.MySecUid, Nickname, self.AvatarUrl);
            RefreshAccounts();
        }
        catch
        {
            // 身份问不到就算了
        }
    }

    /// <summary>
    /// 切号/加号失败之后，会话可能已经被 FeedService 回滚回上一个号。
    /// 界面不能继续拿失败前那份昵称/头像演"还切成功了"，一律按罐子里现在的状态重铺。
    /// </summary>
    private async Task RestoreUiFromCurrentSessionAsync()
    {
        IsLoggedIn = Douyin.Feed.LoginVerified ?? Douyin.Feed.IsLoggedIn;
        SavedAccountInfo? current = Douyin.Feed.CurrentSavedAccount();
        Nickname = IsLoggedIn ? current?.Nickname ?? string.Empty : string.Empty;
        Avatar = null;

        if (IsLoggedIn && current is not null && current.AvatarUrl.Length > 0)
        {
            string url = current.AvatarUrl;
            await RunOnUiAsync(async () =>
            {
                SoftwareBitmapSource? source = await BlurCover.LoadAvatarAsync(url);
                if (source is not null)
                {
                    Avatar = source;
                }
            });
        }

        RefreshAccounts();
    }

    private async Task LoadAccountAvatarsAsync(IReadOnlyList<AccountRowViewModel> rows)
    {
        foreach (AccountRowViewModel row in rows)
        {
            if (row.AvatarUrl.Length == 0 || row.Avatar is not null)
            {
                continue;
            }

            await RunOnUiAsync(async () =>
            {
                SoftwareBitmapSource? source = await BlurCover.LoadAvatarAsync(row.AvatarUrl);
                if (source is not null && row.Avatar is null)
                {
                    row.Avatar = source;
                }
            });
        }
    }

    private void RaiseAccountChanged() => AccountChanged?.Invoke();

    /// <summary>SoftwareBitmapSource 只能在 UI 线程构造，所以整段要挪过去等。</summary>
    private Task RunOnUiAsync(Func<Task> action)
    {
        if (_ui is null || _ui.HasThreadAccess)
        {
            return action();
        }

        var tcs = new TaskCompletionSource();
        _ui.TryEnqueue(async () =>
        {
            try
            {
                await action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    partial void OnAvatarChanged(ImageSource? value)
    {
        OnPropertyChanged(nameof(IsGlyphVisible));
        OnPropertyChanged(nameof(IsAvatarVisible));
    }

    partial void OnAccountMessageChanged(string value) => OnPropertyChanged(nameof(AccountMessageVisibility));
}
