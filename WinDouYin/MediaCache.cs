using System.Buffers;

namespace WinDouYin;

/// <summary>
/// 视频字节的进程内缓存：**固定槽位池 + 块级 LRU + 播放位置驱动的动态回收**。
///
/// 为什么不再按"整条 URL"逐出：一条七分钟的解说视频（1MB 一块，动辄几百块）就能把预算吃满，
/// 逐出时又是整条一起丢——别的视频回看还要用的块跟着没了，回看 seek 到中途必然 miss，
/// 补取还是同步 HTTP，掉帧就是这么来的。
///
/// 为什么改成槽位池而不是"要一块分一块"：按块 new byte[1MB] 时，缓存的活对象确实只有预算那么多，
/// 但逐出与新取让大对象堆不停抖动，进程占用能涨到实际数据的好几倍。
/// 槽位第一次用到时才分配、之后终身复用，写入是 Array.Copy 进槽——
/// 分配次数从"每条视频几百次"降到"整个会话不超过 <see cref="SlotCount"/> 次"，
/// 上限也变成结构性的：占用与看过多少条视频无关。
///
/// 四条规则：
/// - 逐出以块为单位，最久没被读的块先走；
/// - 地址级只留回看窗口（<see cref="MaxUrls"/> 条）：第 7 条地址进来，最远那条整块清零，
///   免得"离当前很远的视频"抱着文件头和半个身子的字节不出力，把窗口内那条挤掉反而更卡；
/// - 每条地址的首块尽量保住（Media Foundation 起播要先探文件头），实在腾不出位置才动它；
/// - 播放点推进时由 <see cref="FreeBehind"/> 主动让位，身后只留 <see cref="KeepBehind"/> 块。
///
/// 缓存数组从不外借：读命中是往调用方自己的缓冲里拷一次（1MB 约几十微秒，相比补取的 100ms 可忽略），
/// 所以逐出之后不存在"外面还挂着同一根数组"的悬空问题。
/// </summary>
internal static class MediaCache
{
    public const int BlockSize = 1024 * 1024;

    /// <summary>槽数 × 块大小就是缓存的硬上限（48MB）。要调内存占用只改这一个数。</summary>
    private const int SlotCount = 48;

    /// <summary>播放点身后保留的块数：往回拖一点进度条不用重新取数。</summary>
    private const int KeepBehind = 3;

    /// <summary>
    /// 同时在场的地址条数上限。一条视频真正会用到的也就是"首块 + 身后 3 块 + 前向 3 块"≈8 块，
    /// 6 条正好把 48 个槽占满；再多就是压箱底的老地址占着槽不出力，所以整条撤掉。
    /// 这个数也就是回看窗口：往上切 5 条以内还在缓存里，第 6 条要重新取数。
    /// </summary>
    private const int MaxUrls = 6;

    private static readonly object Gate = new();
    private static readonly Dictionary<(string Url, long Block), LinkedListNode<Entry>> Index = new();
    private static readonly LinkedList<Entry> Lru = new();

    /// <summary>地址级 LRU：越往后越是"离当前视频越远"的地址，超出 MaxUrls 就整条清退。</summary>
    private static readonly LinkedList<string> UrlOrder = new();
    private static readonly Dictionary<string, LinkedListNode<string>> UrlNodes = new(StringComparer.Ordinal);

    /// <summary>每槽一根 1MB 数组，首次领用时分配，此后只换内容不换对象。</summary>
    private static readonly byte[][] Buffers = new byte[SlotCount][];

    /// <summary>已经分配过、当前空着的槽。</summary>
    private static readonly Stack<int> Recycled = new();

    /// <summary>每条地址的文件长度，秒→块换算要用。</summary>
    private static readonly Dictionary<string, long> Totals = new();

    /// <summary>每条地址已经回收过播放点身后的哪一块，避免每次进度回调都重扫一遍链表。</summary>
    private static readonly Dictionary<string, long> FreedUpTo = new();

    /// <summary>还没领用过的新槽编号。</summary>
    private static int _fresh;

    /// <summary>读一个块：命中就把内容拷进 dest，并把它挪到 LRU 头部（最近用的先留住）。</summary>
    public static bool TryGet(string url, long block, byte[] dest, out int length, out long totalLength)
    {
        lock (Gate)
        {
            length = 0;
            totalLength = 0;
            if (!Index.TryGetValue((url, block), out LinkedListNode<Entry>? node))
            {
                return false;
            }

            Touch(node);
            TouchUrl(node.Value.Url);
            Entry entry = node.Value;
            Array.Copy(Buffers[entry.Slot], 0, dest, 0, entry.Length);
            length = entry.Length;
            totalLength = entry.TotalLength;
            return true;
        }
    }

    /// <summary>这块是不是已经在缓存里；预取用它避免重复取。</summary>
    public static bool Has(string url, long block)
    {
        lock (Gate)
        {
            return Index.ContainsKey((url, block));
        }
    }

    /// <summary>存入一个完整块：内容拷进某个槽。短读的块别传进来，留着会让后续读取吃到空洞。</summary>
    public static void Put(string url, long block, byte[] src, int length, long totalLength)
    {
        if (length <= 0 || length > BlockSize)
        {
            return;
        }

        lock (Gate)
        {
            if (totalLength > 0)
            {
                Totals[url] = totalLength;
            }

            // 同一块只认第一次写入：同步读和预取会各自取到它，第二份内容一样，直接丢
            if (Index.ContainsKey((url, block)))
            {
                TouchUrl(url);
                return;
            }

            // 这条地址进 cache 之前先按地址级 LRU 排队：离当前太远、又不再回看的整条让位
            TouchUrl(url);

            int slot = Acquire();
            if (slot < 0)
            {
                return;
            }

            byte[] buffer = Buffers[slot] ??= new byte[BlockSize];
            Array.Copy(src, 0, buffer, 0, length);
            Index[(url, block)] = Lru.AddFirst(new Entry(url, block, slot, length, totalLength));
        }
    }

    /// <summary>
    /// 播放点推进后回收身后的块。秒→块按"进度 / 总时长 × 总块数"估算，和装载阶段的预热同一套换算；
    /// 长度还没探出来时什么都不做——宁可晚点让位，也不能把还要用的块错杀。
    /// </summary>
    public static void FreeBehind(string url, double positionSeconds, double durationSeconds)
    {
        if (positionSeconds <= 0 || durationSeconds <= 0)
        {
            return;
        }

        lock (Gate)
        {
            if (!Totals.TryGetValue(url, out long total) || total <= 0)
            {
                return;
            }

            long upto = (long)(positionSeconds / durationSeconds * (total / BlockSize)) - KeepBehind;
            if (upto <= (FreedUpTo.TryGetValue(url, out long done) ? done : 0))
            {
                return;
            }

            FreedUpTo[url] = upto;
            LinkedListNode<Entry>? node = Lru.First;
            while (node is not null)
            {
                LinkedListNode<Entry>? next = node.Next;
                if (node.Value.Url == url && node.Value.Block > 0 && node.Value.Block < upto)
                {
                    Recycled.Push(Retire(node));
                }

                node = next;
            }
        }
    }

    /// <summary>取数用的临时缓冲：1MB 走 ArrayPool 现成的桶，进程内翻来覆去就那几根。</summary>
    public static byte[] RentBuffer() => ArrayPool<byte>.Shared.Rent(BlockSize);

    public static void ReturnBuffer(byte[] buffer) => ArrayPool<byte>.Shared.Return(buffer);

    private static void Touch(LinkedListNode<Entry> node)
    {
        Lru.Remove(node);
        Lru.AddFirst(node);
    }

    /// <summary>
    /// 把地址挪到"最近在用"这头，并从最远那头把超额的整条清退。
    /// 是整条退而不是按块退：一条远处的地址只剩文件头没价值（回看要的是 seek 落点那几块），
    /// 留着只会把窗口内那条视频的块挤掉，反而更容易掉帧。
    /// </summary>
    private static void TouchUrl(string url)
    {
        if (!UrlNodes.TryGetValue(url, out LinkedListNode<string>? node))
        {
            node = UrlOrder.AddFirst(url);
            UrlNodes[url] = node;
        }
        else
        {
            UrlOrder.Remove(node);
            UrlOrder.AddFirst(node); // 传节点的 AddFirst 只负责挂回去，节点还是原来那个
        }

        while (UrlOrder.Count > MaxUrls)
        {
            LinkedListNode<string>? stale = UrlOrder.Last;
            if (stale is null)
            {
                return;
            }

            UrlOrder.Remove(stale);
            EvictUrl(stale.Value);
        }
    }

    private static void EvictUrl(string url)
    {
        UrlNodes.Remove(url);

        LinkedListNode<Entry>? node = Lru.First;
        while (node is not null)
        {
            LinkedListNode<Entry>? next = node.Next;
            if (node.Value.Url == url)
            {
                Recycled.Push(Retire(node));
            }

            node = next;
        }

        // 换算表跟着走：不然看过几百条后这两张表攒的全是再也不会读的地址
        Totals.Remove(url);
        FreedUpTo.Remove(url);
    }

    /// <summary>要一个空槽：先领回收来的，再领没用过的，都没有就逐一块。</summary>
    private static int Acquire()
    {
        if (Recycled.Count > 0)
        {
            return Recycled.Pop();
        }

        if (_fresh < SlotCount)
        {
            return _fresh++;
        }

        LinkedListNode<Entry>? victim = null;
        for (LinkedListNode<Entry>? node = Lru.Last; node is not null; node = node.Previous)
        {
            if (node.Value.Block != 0)
            {
                victim = node;
                break;
            }
        }

        // 满仓全是首块（看过很多条、每条只剩文件头）：只能拿最久没用的首块顶，否则缓存就此僵死
        victim ??= Lru.Last;
        return victim is null ? -1 : Retire(victim);
    }

    private static int Retire(LinkedListNode<Entry> node)
    {
        Index.Remove((node.Value.Url, node.Value.Block));
        Lru.Remove(node);
        return node.Value.Slot;
    }

    private sealed class Entry(string url, long block, int slot, int length, long totalLength)
    {
        public string Url { get; } = url;

        public long Block { get; } = block;

        public int Slot { get; } = slot;

        public int Length { get; } = length;

        public long TotalLength { get; } = totalLength;
    }
}
