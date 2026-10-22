using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinDouYin.Contracts;

namespace WinDouYin.ViewModels;

/// <summary>
/// 关注/粉丝列表里的一行。数据来自既有的 <c>relation/list</c> 读链（<see cref="UserInfo"/>），
/// 头像按 2 倍封顶解码；点行进去是对方主页。
/// </summary>
public sealed class UserRelationRow
{
    private const int AvatarDecodeWidth = 96;

    public UserRelationRow(UserInfo user)
    {
        User = user;
        SecUid = user.SecUid;
        Name = user.Nickname.Length > 0 ? user.Nickname : "抖音用户";
        HandleText = user.Handle.Length > 0 ? "抖音号：" + user.Handle : string.Empty;
        StatsText = string.Join(" · ", new[]
        {
            user.FollowerCount > 0 ? CountText.FormatCn(user.FollowerCount) + "粉丝" : string.Empty,
            user.TotalFavorited > 0 ? CountText.FormatCn(user.TotalFavorited) + "获赞" : string.Empty,
        }.Where(static s => s.Length > 0));
        HandleVisibility = HandleText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StatsVisibility = StatsText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SignatureVisibility = user.Signature.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Initials = Name[..1];
        Avatar = user.Avatar.Length > 0
            ? new BitmapImage(new Uri(user.Avatar)) { DecodePixelWidth = AvatarDecodeWidth }
            : null;
    }

    public UserInfo User { get; }

    public string SecUid { get; }

    public string Name { get; }

    public string HandleText { get; }

    public string StatsText { get; }

    public string Initials { get; }

    public ImageSource? Avatar { get; }

    public Visibility HandleVisibility { get; }

    public Visibility StatsVisibility { get; }

    public Visibility SignatureVisibility { get; }

    /// <summary>签名原文直接绑，不另存。</summary>
    public string Signature => User.Signature;
}

/// <summary>
/// 「关注 / 粉丝」列表的数据面。读链就是工程里既有的 <see cref="NativeFeed.FollowingAsync"/> /
/// <see cref="NativeFeed.FollowersAsync"/>（<c>max_time</c> 翻页，offset 服务端忽略）。
/// **只对自己的账号成立**：2026-10-20 实测官方他人主页点粉丝数不发请求、也没有弹层，
/// 所以这个页面只从自己的主页进（他人页的数字保持静态）。
/// </summary>
public sealed partial class UserRelationViewModel : INotifyPropertyChanged
{
    private const int PageSize = 20;

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private bool _followers;
    private long _nextMaxTime;
    private bool _hasMore = true;
    private bool _loading;
    private string _status = string.Empty;
    private int _total;
    private int _gen;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<UserRelationRow> Rows { get; } = [];

    public bool IsFollowers => _followers;

    public string Title => _followers ? "粉丝" : "关注";

    public string CountLabel => _total > 0 ? CountText.FormatCn(_total) : string.Empty;

    public bool HasMore => _hasMore;

    public bool IsLoading => _loading;

    public Visibility LoadingVisibility => _loading ? Visibility.Visible : Visibility.Collapsed;

    public string StatusText => _status;

    public Task LoadAsync(bool followers)
    {
        _followers = followers;
        _gen++;
        Rows.Clear();
        _ids.Clear();
        _nextMaxTime = 0;
        _hasMore = true;
        _total = 0;
        _status = string.Empty;
        NotifyEverything();
        return LoadMoreAsync();
    }

    /// <summary>滚到底续页；正在取或没下一页时忽略。</summary>
    public async Task LoadMoreAsync()
    {
        await _loadLock.WaitAsync();
        try
        {
            if (_loading || !_hasMore)
            {
                return;
            }

            int gen = _gen;
            _loading = true;
            Notify(nameof(IsLoading));
            Notify(nameof(LoadingVisibility));

            UserInfoPage page = new([], 0, false, 0);
            string error = string.Empty;
            try
            {
                page = await Task.Run(() => _followers
                    ? Douyin.Feed.FollowersAsync(_nextMaxTime, PageSize, CancellationToken.None)
                    : Douyin.Feed.FollowingAsync(_nextMaxTime, PageSize, CancellationToken.None));
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            if (gen != _gen)
            {
                return;
            }

            if (error.Length > 0)
            {
                _hasMore = false;
                _status = "列表没读回来：" + error;
                NotifyEverything();
                return;
            }

            foreach (UserInfo user in page.Items)
            {
                string key = user.Uid.Length > 0 ? user.Uid : user.SecUid;
                if (key.Length == 0 || !_ids.Add(key))
                {
                    continue;
                }

                Rows.Add(new UserRelationRow(user));
            }

            _total = page.Total > 0 ? page.Total : _total;
            _nextMaxTime = page.NextMaxTime;
            _hasMore = page.HasMore && page.NextMaxTime > 0;
            _status = Rows.Count == 0 ? (_followers ? "还没有粉丝" : "还没关注任何人") : string.Empty;
            NotifyEverything();
        }
        finally
        {
            // 与主页同一口径：作废的请求也要把 loading 放掉，否则续页会永远被挡
            _loading = false;
            Notify(nameof(IsLoading));
            Notify(nameof(LoadingVisibility));
            _loadLock.Release();
        }
    }

    private void NotifyEverything()
    {
        foreach (string name in Names)
        {
            Notify(name);
        }
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static readonly string[] Names =
    [
        nameof(Title), nameof(CountLabel), nameof(IsFollowers),
        nameof(HasMore), nameof(IsLoading), nameof(LoadingVisibility), nameof(StatusText),
    ];
}
