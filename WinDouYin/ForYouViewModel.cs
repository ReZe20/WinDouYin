using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;
using WinDouYin.Contracts;

namespace WinDouYin;

/// <summary>
/// 推荐流里的一条。计数在报文里是裸整数，右侧竖排要的是"12.4w"这种，
/// 所以在这里预格式化成属性给 x:Bind 直接吃，不引转换器。
/// Qualities 是这条视频的清晰度档位（从高到低，外带"默认流是哪一档"），"清晰度"菜单照它铺。
/// </summary>
public sealed record VideoItem(
    string Id, string Desc, string Author, string PlayUrl, string CoverUrl, long CreateTime,
    string SecUid, string AuthorId, string Avatar, int Digg, int Comment, int Collect, int Share, int Width, int Height,
    VideoQualitySet Qualities, string Music = "", string FriendRecommender = "")
{
    public string DiggText => CountText.Format(Digg);

    public string CommentText => CountText.Format(Comment);

    public string CollectText => CountText.Format(Collect);

    public string ShareText => CountText.Format(Share);
}

public sealed partial class ForYouViewModel : ObservableObject
{
    // 列表尾部的缓冲剩得比这还少，就顺手把下一页取回来：滑到底时永远接得上。
    // 阈值要大于单页条数（8），让预取在还剩一页多时就启动，把 900ms 间隔+网络藏在正在看的视频背后。
    private const int PrefetchThreshold = 12;

    // 每次翻页带出去给服务端的已推 id 窗口，和落盘保留的历史上限。
    // 窗口实测过 50/200：匿名会话的返回池几乎全落在历史里，服务端只按 presented_ids 去重，
    // 带 50 时一批 20 条里净新增 0~3 条，带 200 才稳定起来；再大就是 4KB 以上的 URL，没测过别乱加。
    private const int PresentedWindow = 200;
    private const int HistoryCap = 400;

    // 一批并发几发翻页：单发只回 2~6 条，并发 4 发实测净新增速率约是串行的两倍
    private const int BurstFanout = 4;

    // 取数抛了之后缓多久再试一把：这条链没人接异常，卡一拍比连着打服务端强
    private const int FetchRetryDelayMs = 1200;

    // 跨进程记住看过哪些条：重启后拿最近这批当 presented_ids 发出去，服务端就不会重播上次的推荐
    private static readonly string HistoryPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDouYin", "feed_seen.txt");

    private readonly List<VideoItem> _items = new();
    private readonly HashSet<string> _seenIds = new(StringComparer.Ordinal);
    private readonly List<string> _history = new();
    private int _index = -1;
    private Task? _fetching;

    // AOT 收口：[ObservableProperty] 走 partial 属性而不是字段。字段式生成的代码要靠运行时反射拿
    // 属性信息，WinUI 3 / Native AOT 下不兼容（警告 MVVMTK0045）；partial 属性让源生成器把
    // INotifyPropertyChanged 与 WinRT 投影都直接写死在编译期，OnXxxChanged 钩子名不变。
    [ObservableProperty]
    public partial VideoItem? Current { get; set; }

    /// <summary>下一条：播放组件拿它做预载（静音候场），滚动到它时即刻切换。</summary>
    [ObservableProperty]
    public partial VideoItem? Next { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    public ForYouViewModel()
    {
        LoadHistory();
        _ = MoveNextAsync();
    }

    /// <summary>下一条：先走已取到的列表；列表见底才等翻页结果（多半是预取已经在取的那一根）。
    /// 取数抛了不能就这么走掉——这条链是 `_ = MoveNextAsync()` 起的火，异常没人接，
    /// 表现出来就是"点了没反应、一片黑"。落一条日志，缓一拍再试一把，还不行就等下一次动作。</summary>
    public async Task MoveNextAsync()
    {
        // 等取数期间用户可能已经拖拽落位/换过台：回来时索引要是变了，这一发就作废。
        // 不作废的话会基于新索引再往前推一条（快滑时就是多跳一条/两套换台撞在一起）。
        int startIndex = _index;

        if (_index + 1 >= _items.Count)
        {
            try
            {
                await FillAsync();
            }
            catch (Exception ex)
            {
                MfLog.Write("app: 推荐取数抛了 " + ex.GetType().Name + " · " + ex.Message);
                await Task.Delay(FetchRetryDelayMs);
                try
                {
                    await EnsureFetchAsync();
                }
                catch (Exception again)
                {
                    MfLog.Write("app: 再试又抛了 " + again.GetType().Name + " · " + again.Message);
                }
            }
        }

        if (_index != startIndex)
        {
            return;
        }

        if (_index + 1 < _items.Count)
        {
            Current = _items[++_index];
            PrefetchIfNearEnd();
        }
    }

    /// <summary>上一条：只在已取到的列表里回退，不发请求。</summary>
    public void MovePrevious()
    {
        if (_index > 0)
        {
            Current = _items[--_index];
        }
    }

    /// <summary>当前索引：宿主做跟手拖拽切换时需要直接定位，不经过 Current 变更通知。</summary>
    public int CurrentIndex => _index;

    /// <summary>按当前索引的偏移取条目：-1 上一条，+1 下一条，0 当前条。</summary>
    public VideoItem? ItemAt(int offset)
    {
        int idx = _index + offset;
        return idx >= 0 && idx < _items.Count ? _items[idx] : null;
    }

    /// <summary>直接跳到某一条：仅用于拖拽切换完成后的最终落位。</summary>
    public void GoToIndex(int index)
    {
        if (index < 0 || index >= _items.Count || index == _index)
        {
            return;
        }

        _index = index;
        Current = _items[index];

        // 拖拽落位不走 MoveNextAsync，快划到列表尾部时预取得在这里补一脚
        PrefetchIfNearEnd();
    }

    // 播放层没有可订阅的失败事件，那就在状态文本里带上方括号位置：
    // 黑屏但方括号有数字 = 地址已下发、是播放器的问题；没有数字 = 压根没取到可播条目
    partial void OnCurrentChanged(VideoItem? value)
    {
        if (value is not null)
        {
            Status = Douyin.Feed.Session + " [" + (_index + 1) + "/" + _items.Count + "]";
        }

        UpdateNext();
    }

    /// <summary>列表里下一条的指针；还没取到下一条（列表见底）时为 null，预载随之暂停。</summary>
    private void UpdateNext() => Next = _index >= 0 && _index + 1 < _items.Count ? _items[_index + 1] : null;

    /// <summary>快划到末尾时就该动手：剩的不足一截缓冲就后台翻页，不等划到底现发请求。</summary>
    private void PrefetchIfNearEnd()
    {
        if (_items.Count - 1 - _index <= PrefetchThreshold)
        {
            _ = EnsureFetchAsync();
        }
    }

    /// <summary>已在取的那次复用同一根任务（预取与见底等待共用），不叠第二个请求。</summary>
    private Task EnsureFetchAsync()
    {
        if (_fetching is null || _fetching.IsCompleted)
        {
            _fetching = FetchPageAsync();
        }

        return _fetching;
    }

    private static VideoItem ItemOf(VideoCard card) => new(
        card.Id, card.Desc, card.Author, card.Play, card.Cover, card.CreateTime,
        card.SecUid, card.AuthorId, card.Avatar, card.Digg, card.Comment, card.Collect, card.Share, card.Width, card.Height,
        card.Qualities, card.Music, card.FriendRecommender);

    private async Task FetchPageAsync()
    {
        // 把最近看过的 id 交给服务端（presented_ids），让它别再回吐旧条目；_history 跨进程保留，重启也接得上
        var presented = new List<string>();
        for (int i = Math.Max(0, _history.Count - PresentedWindow); i < _history.Count; i++)
        {
            presented.Add(_history[i]);
        }

        IReadOnlyList<VideoCard> page = await Douyin.Feed.NextBurstAsync(presented, BurstFanout, CancellationToken.None);
        bool grew = false;
        foreach (VideoCard card in page)
        {
            // _seenIds.Add 返回 false 表示这条历史里已经推过——跳过，杜绝跨会话的重复条目
            if (card.Play.Length > 0 && _seenIds.Add(card.Id))
            {
                _items.Add(ItemOf(card));
                _history.Add(card.Id);
                grew = true;
            }
        }

        // 一批全是老条目时不能空手而归：匿名会话的推荐池就那么大，历史只会越攒越长，
        // 严格去重会把整批都判成"看过"，库存见底就是黑屏没得播。宁可重播最久没看的那条。
        if (!grew)
        {
            VideoCard? replay = null;
            int oldest = int.MaxValue;
            foreach (VideoCard c in page)
            {
                int at = c.Play.Length > 0 && c.Id != Current?.Id ? _history.IndexOf(c.Id) : -1;
                if (at >= 0 && at < oldest)
                {
                    oldest = at;
                    replay = c;
                }
            }

            if (replay is not null)
            {
                _items.Add(ItemOf(replay));
                grew = true;
            }
        }

        if (grew)
        {
            SaveHistory();
        }

        // 列表长了，原先"没有下一条"的可能就有了：预载跟着补上
        UpdateNext();
        Status = Douyin.Feed.Session;
    }

    /// <summary>启动时读回历史：填 _seenIds（去重）和 _history（presented_ids 的种子）。</summary>
    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath))
            {
                return;
            }

            foreach (string line in File.ReadAllLines(HistoryPath))
            {
                string id = line.Trim();
                if (id.Length > 0 && _seenIds.Add(id))
                {
                    _history.Add(id);
                }
            }
        }
        catch (IOException)
        {
            /* 读不到就当没历史，最坏是重启重播一次，不影响播放 */
        }
    }

    /// <summary>落盘前裁到 HistoryCap，避免文件无限涨；写失败静默（进度记录而已）。</summary>
    private void SaveHistory()
    {
        try
        {
            if (_history.Count > HistoryCap)
            {
                _history.RemoveRange(0, _history.Count - HistoryCap);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            File.WriteAllLines(HistoryPath, _history);
        }
        catch (IOException)
        {
            /* 同上 */
        }
    }

    /// <summary>列表见底、又没有预取接上时才走到这：等本次结果，最多连着翻 3 页。</summary>
    private async Task FillAsync()
    {
        for (int attempt = 0; attempt < 3 && _index + 1 >= _items.Count; attempt++)
        {
            await EnsureFetchAsync();
        }
    }
}
