using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using WinDouYin.Contracts;

namespace WinDouYin.ViewModels;

/// <summary>
/// 「朋友」页的账面：官方 <c>/friend</c> 只吃一条 <c>familiar/feed</c> 的竖向流。
/// App 吃 <b>level=1「朋友推荐」</b>：每条带 `friend_recommend_info.label_user_list`，推荐人已经由
/// <see cref="VideoCard.FriendRecommender"/> 摊平，页面在底部信息区显示「朋友 刘鼎业 推荐」。
/// 首页 6 条；回包 cursor 是这一发的时间戳，回传只会拿到同一批，续页用
/// <c>cursor=0&amp;pull_type=2</c> 换一批，按 id 去重，直到没有新条为止。
/// （level=2 是互关好友作品，没有推荐人字段，备用。）
/// </summary>
public sealed partial class FriendsViewModel : INotifyPropertyChanged
{
    private const int MaxVideos = 120;

    private readonly List<VideoItem> _videos = [];
    private readonly HashSet<string> _videoIds = new(StringComparer.Ordinal);
    private int _index = -1;
    private int _generation;
    private bool _more = true;
    private bool _busy;
    private bool _loaded;
    private bool _loggedInLastLoad;
    private VideoItem? _current;
    private VideoItem? _next;
    private string _statusText = string.Empty;
    private Visibility _statusVisibility = Visibility.Collapsed;
    private Visibility _loadingVisibility = Visibility.Collapsed;

    public FriendsViewModel()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public VideoItem? Current
    {
        get => _current;
        private set
        {
            if (ReferenceEquals(_current, value))
            {
                return;
            }

            _current = value;
            Raise(nameof(Current));
            UpdateNext();
            StatusVisibility = _statusText.Length > 0 && value is null ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public VideoItem? Next => _next;

    /// <summary>拖拽落位/取邻居用：当前下标与前后各一条。</summary>
    public int CurrentIndex => _index;

    public VideoItem? ItemAt(int direction)
    {
        int index = _index + direction;
        return index >= 0 && index < _videos.Count ? _videos[index] : null;
    }

    /// <summary>拖拽落位：画面已经滑到位，这里只把账面索引跟着挪过去。</summary>
    public void GoToIndex(int index)
    {
        if (index < 0 || index >= _videos.Count || index == _index)
        {
            return;
        }

        _index = index;
        Current = _videos[_index];
        StatusText = string.Empty;
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (string.Equals(_statusText, value, StringComparison.Ordinal))
            {
                return;
            }

            _statusText = value;
            Raise(nameof(StatusText));
            StatusVisibility = value.Length > 0 && _current is null ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    public Visibility StatusVisibility
    {
        get => _statusVisibility;
        private set
        {
            if (_statusVisibility == value)
            {
                return;
            }

            _statusVisibility = value;
            Raise(nameof(StatusVisibility));
        }
    }

    public Visibility LoadingVisibility
    {
        get => _loadingVisibility;
        private set
        {
            if (_loadingVisibility == value)
            {
                return;
            }

            _loadingVisibility = value;
            Raise(nameof(LoadingVisibility));
        }
    }

    /// <summary>页面第一次进场：登录态变了就重来，否则沿用已有账面（页面是缓存页）。</summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        bool loggedIn = Douyin.Feed.IsLoggedIn;
        if (_loaded && loggedIn == _loggedInLastLoad)
        {
            return;
        }

        _loaded = true;
        _loggedInLastLoad = loggedIn;
        await RefreshAsync(ct);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        int gen = ++_generation;
        _videos.Clear();
        _videoIds.Clear();
        _index = -1;
        _more = true;
        _busy = false;
        Current = null;
        StatusText = string.Empty;
        LoadingVisibility = Visibility.Visible;

        if (!Douyin.Feed.IsLoggedIn)
        {
            LoadingVisibility = Visibility.Collapsed;
            StatusText = "登录后才会出现朋友内容";
            return;
        }

        try
        {
            FamiliarFeedPage page = await Douyin.Feed.FamiliarFeedAsync(0, 0, ct);
            if (gen != _generation)
            {
                return;
            }

            AppendFeed(page);
            _more = page.HasMore && _videos.Count > 0;
            if (_videos.Count > 0)
            {
                _index = 0;
                Current = _videos[0];
            }
            else
            {
                StatusText = page.Note.Length > 0 ? page.Note : "朋友暂时没有新作品";
            }

            PrefetchIfNearEnd();
        }
        catch (Exception ex)
        {
            if (gen == _generation)
            {
                StatusText = "朋友流没取到：" + ex.Message;
            }
        }
        finally
        {
            if (gen == _generation)
            {
                LoadingVisibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>舞台往前一条：列表见底就续一批；续完还是见底就停在原处。</summary>
    public async Task MoveNextAsync(CancellationToken ct = default)
    {
        int start = _index;
        if (_index + 1 >= _videos.Count)
        {
            await LoadMoreAsync(ct);
        }

        if (_generation == 0 || _index != start)
        {
            return;
        }

        if (_index + 1 < _videos.Count)
        {
            _index++;
            Current = _videos[_index];
            PrefetchIfNearEnd();
        }
        else
        {
            StatusText = "朋友暂时没有更多作品";
        }
    }

    /// <summary>上一条只在已取到的列表里回退，不重取。</summary>
    public void MovePrevious()
    {
        if (_index > 0)
        {
            _index--;
            Current = _videos[_index];
            StatusText = string.Empty;
        }
    }

    private async Task LoadMoreAsync(CancellationToken ct)
    {
        if (_busy || !_more || Douyin.Feed.IsLoggedIn == false)
        {
            return;
        }

        int gen = _generation;
        _busy = true;
        try
        {
            // level=1 的续页固定 cursor=0 + pull=2“换一批”；回包 cursor 是这一发的时间戳，回传只会重复同一批。
            FamiliarFeedPage page = await Douyin.Feed.FamiliarFeedAsync(0, 2, ct);
            if (gen != _generation)
            {
                return;
            }

            int before = _videos.Count;
            AppendFeed(page);
            if (!page.HasMore || _videos.Count == before || _videos.Count >= MaxVideos)
            {
                _more = false;
            }
        }
        catch (Exception ex)
        {
            if (gen == _generation)
            {
                MfLog.Write("app: 朋友流续批抛了 " + ex.GetType().Name + " · " + ex.Message);
                StatusText = "朋友流续批没取到";
            }
        }
        finally
        {
            if (gen == _generation)
            {
                _busy = false;
            }
        }
    }

    private void PrefetchIfNearEnd()
    {
        if (_more && !_busy && _videos.Count - _index <= 2)
        {
            _ = LoadMoreAsync(CancellationToken.None);
        }
    }

    private void AppendFeed(FamiliarFeedPage page)
    {
        foreach (VideoCard card in page.Videos)
        {
            if (card.Id.Length == 0 || !_videoIds.Add(card.Id) || _videos.Count >= MaxVideos)
            {
                continue;
            }

            _videos.Add(ItemOf(card));
        }

        UpdateNext();
    }

    private void UpdateNext()
    {
        VideoItem? next = _index >= 0 && _index + 1 < _videos.Count ? _videos[_index + 1] : null;
        if (ReferenceEquals(_next, next))
        {
            return;
        }

        _next = next;
        Raise(nameof(Next));
    }

    private static VideoItem ItemOf(VideoCard card) => new(
        card.Id, card.Desc, card.Author, card.Play, card.Cover, card.CreateTime,
        card.SecUid, card.AuthorId, card.Avatar, card.Digg, card.Comment, card.Collect, card.Share,
        card.Width, card.Height, card.Qualities, card.Music, card.FriendRecommender);

    private void Raise([CallerMemberName] string name = "") =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
