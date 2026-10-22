using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace WinDouYin;

/// <summary>
/// 视频字节的磁盘缓存：只服务"重新打开时起播要用的那一小段"，不是整条视频落地。
///
/// 实体是一个块文件（1MB 一块，最多 <see cref="SlotCount"/> 块 = 512MB）+ 一份索引：
/// 索引记每个视频（按播放地址里稳定的 <c>/video/tos/…</c> 对象后缀分键）落了哪些块、总长多少、
/// 最后用是什么时候。签名/节点会换，对象后缀不会——两次抓包里同一条 aweme 的整条 URL 变了、
/// <c>/video/tos/…</c> 一模一样，缓存就是靠这个跨进程认人。
///
/// 三条约束是用户点名要的：
/// - **低 IO 优先级**：块文件句柄挂 <c>IoPriorityHintVeryLow</c>，写线程进 background 模式；
/// - **限速**：写侧令牌桶 6MB/s，读侧不限（读是播放要用的，而且它只是替代一次同样大小的网络读）；
/// - **可关**：<see cref="Enabled"/> 置 false 后读写都停，只保留已有文件（再打开接着用）。
///
/// 写入永远在后台线程：调用方（解码线程/预取线程）只把 1MB 拷进池里的缓冲就返回。
/// 单条视频最多留 <see cref="PerVideoBlocks"/> 块（16MB），超了就丢它最靠前的那块（首块永远保）：
/// 长视频既不会整条落地，也不会把局部缓冲窗口挤掉。
/// </summary>
internal static class VideoDiskCache
{
    public const int BlockSize = 1024 * 1024;

    /// <summary>槽数 × 块大小 = 磁盘硬上限（512MB）。</summary>
    private const int SlotCount = 512;

    /// <summary>单条视频最多留这么多块：够起播、够回看一小段，再长就滑窗。</summary>
    private const int PerVideoBlocks = 16;

    /// <summary>写侧限速：帮游戏/系统 IO 让路，6MB/s 对起播缓存足够。</summary>
    private const int WriteBytesPerSecond = 6 * 1024 * 1024;

    /// <summary>索引最多每 5 秒落一次盘（数据块本身是直写的，丢的只是索引里的几行记录）。</summary>
    private const int IndexSaveIntervalMs = 5000;

    /// <summary>待写队列上限：每块 1MB，排太多等于拿内存换缓存，满了就丢新请求。</summary>
    private const int MaxPending = 32;

    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<WriteJob> Pending = new();
    private static readonly AutoResetEvent Signal = new(false);
    private static readonly HashSet<int> UsedSlots = [];

    private static readonly string DefaultRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDouYin", "video-cache");

    /// <summary>探针用：把根目录改到临时目录；生产代码从不设置它。</summary>
    internal static string RootOverride = string.Empty;

    private static string FolderPath => RootOverride.Length > 0 ? RootOverride : DefaultRoot;

    private static string DataPath => Path.Combine(FolderPath, "blocks.bin");

    private static string IndexPath => Path.Combine(FolderPath, "index.json");

    private static FileStream? _data;
    private static DiskIndex? _index;
    private static Thread? _writer;
    private static long _writeBudgetMs;
    private static long _lastSaveMs;
    private static bool _dirty;
    private static volatile bool _enabled = true;

    /// <summary>设置页那枚开关；关掉只是停手，不清已有缓存（再打开接着用）。</summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value)
            {
                // 设置页的勾选在 UI 线程上翻：排空队列最多要等 1.5 秒，搬后台去做；
                // 关掉之后写线程会丢掉队列里剩下的块，排在后面的写很快就能排完
                _ = Task.Run(() =>
                {
                    DrainPending();
                    SaveIndexIfDirty(force: true);
                });
            }
        }
    }

    /// <summary>
    /// 播放地址 → 缓存键。只认 <c>/video/tos/…</c> 这段对象路径（去掉 query 与前面两段签名/节点），
    /// 实测两次抓包里整条 URL 会变、这一段不变；不是这个形状的地址就不缓存（返回空串）。
    /// </summary>
    public static string KeyOf(string url)
    {
        int at = url.IndexOf("/video/tos/", StringComparison.Ordinal);
        if (at < 0)
        {
            return string.Empty;
        }

        int cut = url.IndexOf('?', at);
        return cut > at ? url[at..cut] : url[at..];
    }

    /// <summary>读一块：命中就拷进 dest。同步做——调用方是解码线程，这一下替代的是一次 100~300ms 的网络读。</summary>
    public static bool TryRead(string key, long block, byte[] dest, out int length, out long total)
    {
        length = 0;
        total = 0;
        if (!_enabled || key.Length == 0)
        {
            return false;
        }

        lock (Gate)
        {
            EnsureOpenLocked();
            if (_index is null || _data is null || !_index.Videos.TryGetValue(key, out DiskVideo? video))
            {
                return false;
            }

            if (!video.Blocks.TryGetValue(block, out DiskBlock? entry) || entry.Length <= 0 || entry.Length > BlockSize)
            {
                return false;
            }

            try
            {
                _data.Position = (long)entry.Slot * BlockSize;
                int got = _data.Read(dest, 0, entry.Length);
                if (got != entry.Length)
                {
                    DropBlockLocked(key, block);
                    return false;
                }

                length = got;
                total = video.Total;
                video.LastUse = Environment.TickCount64;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>把一块排进后台写队列（拷进池缓冲后立即返回，调用方可以复用 src）。</summary>
    public static void RequestWrite(string key, long block, byte[] src, int length, long total)
    {
        if (!_enabled || key.Length == 0 || length <= 0 || length > BlockSize || total <= 0)
        {
            return;
        }

        if (Pending.Count >= MaxPending)
        {
            return;
        }

        byte[] copy = ArrayPool<byte>.Shared.Rent(BlockSize);
        Buffer.BlockCopy(src, 0, copy, 0, length);
        Pending.Enqueue(new WriteJob(key, block, copy, length, total));
        EnsureWriter();
        Signal.Set();
    }

    /// <summary>把还有的索引立刻落盘（退出前调；不关句柄）。</summary>
    public static void Flush()
    {
        DrainPending();
        SaveIndexIfDirty(force: true);
    }

    /// <summary>探针用：落索引并放掉句柄，后续调用会重新打开。</summary>
    internal static void Shutdown()
    {
        DrainPending();
        lock (Gate)
        {
            SaveIndexIfDirtyLocked(force: true);
            _data?.Dispose();
            _data = null;
            _index = null;
            UsedSlots.Clear();
        }
    }

    // ===== 写线程 =====

    private readonly record struct WriteJob(string Key, long Block, byte[] Buffer, int Length, long Total);

    private static void EnsureWriter()
    {
        if (_writer is not null)
        {
            return;
        }

        lock (Gate)
        {
            if (_writer is not null)
            {
                return;
            }

            _writer = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "VideoDiskCache",
                Priority = ThreadPriority.BelowNormal,
            };
            _writer.Start();
        }
    }

    private static void WriterLoop()
    {
        // 整个线程进 background 模式：正常 IO 排队时系统先饿着它
        SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);
        try
        {
            while (true)
            {
                Signal.WaitOne();
                while (Pending.TryDequeue(out WriteJob job))
                {
                    try
                    {
                        WriteBlock(job);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(job.Buffer);
                    }

                    Throttle(job.Length);
                }

                SaveIndexIfDirty();
            }
        }
        finally
        {
            SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd);
        }
    }

    private static void WriteBlock(WriteJob job)
    {
        lock (Gate)
        {
            if (!_enabled)
            {
                return;
            }

            EnsureOpenLocked();
            if (_index is null || _data is null)
            {
                return;
            }

            if (!_index.Videos.TryGetValue(job.Key, out DiskVideo? video))
            {
                video = new DiskVideo { Total = job.Total };
                _index.Videos[job.Key] = video;
            }

            if (video.Blocks.ContainsKey(job.Block))
            {
                return;   // 同一块只认第一次
            }

            if (video.Total != job.Total)
            {
                // 同一对象后缀但长度变了（换码/重传）：旧块不可信，整条重来
                EvictLocked(job.Key);
                video = new DiskVideo { Total = job.Total };
                _index.Videos[job.Key] = video;
            }

            TrimVideoLocked(job.Key, video);
            EnsureFreeSlotLocked(job.Key);

            int slot = AcquireSlotLocked();
            if (slot < 0)
            {
                return;
            }

            try
            {
                _data.Position = (long)slot * BlockSize;
                _data.Write(job.Buffer, 0, job.Length);
                video.Blocks[job.Block] = new DiskBlock { Slot = slot, Length = job.Length };
                video.LastUse = Environment.TickCount64;
                _dirty = true;
            }
            catch
            {
                UsedSlots.Remove(slot);
            }
        }
    }

    /// <summary>限速：按"每块应占的毫秒数"排下一次写的时刻。</summary>
    private static void Throttle(int bytes)
    {
        long need = bytes * 1000L / WriteBytesPerSecond;
        long now = Environment.TickCount64;
        _writeBudgetMs = Math.Max(_writeBudgetMs, now) + need;
        long wait = _writeBudgetMs - now;
        if (wait > 0)
        {
            Thread.Sleep((int)Math.Min(wait, 250));
        }
    }

    private static void DrainPending()
    {
        // 最多等 1.5 秒把队列排空；排不空也不阻塞退出（块数据可丢，索引下次重建）
        for (int i = 0; i < 150 && !Pending.IsEmpty; i++)
        {
            Signal.Set();
            Thread.Sleep(10);
        }
    }

    // ===== 容量与淘汰（都在 Gate 里） =====

    private static void TrimVideoLocked(string key, DiskVideo video)
    {
        while (video.Blocks.Count >= PerVideoBlocks)
        {
            // 首块（起播要用）永远保；其余从最靠前的块开始丢，留下最近看的滑窗
            long victim = long.MaxValue;
            foreach (long block in video.Blocks.Keys)
            {
                if (block > 0 && block < victim)
                {
                    victim = block;
                }
            }

            if (victim == long.MaxValue)
            {
                return;   // 只有首块，没得丢
            }

            DropBlockLocked(key, victim);
        }
    }

    /// <summary>腾一个空槽：先按整条 LRU 淘汰最久没用的视频（不动正在写的这条）。</summary>
    private static void EnsureFreeSlotLocked(string keepKey)
    {
        if (UsedSlots.Count < SlotCount)
        {
            return;
        }

        while (UsedSlots.Count >= SlotCount)
        {
            string? victimKey = null;
            long oldest = long.MaxValue;
            foreach ((string key, DiskVideo video) in _index!.Videos)
            {
                if (string.Equals(key, keepKey, StringComparison.Ordinal))
                {
                    continue;
                }

                if (video.LastUse < oldest)
                {
                    oldest = video.LastUse;
                    victimKey = key;
                }
            }

            if (victimKey is null)
            {
                return;   // 只剩自己：没地方腾
            }

            EvictLocked(victimKey);
        }
    }

    private static int AcquireSlotLocked()
    {
        for (int slot = 0; slot < SlotCount; slot++)
        {
            if (UsedSlots.Add(slot))
            {
                return slot;
            }
        }

        return -1;
    }

    private static void EvictLocked(string key)
    {
        if (_index is null || !_index.Videos.Remove(key, out DiskVideo? video))
        {
            return;
        }

        foreach (DiskBlock block in video.Blocks.Values)
        {
            UsedSlots.Remove(block.Slot);
        }
    }

    private static void DropBlockLocked(string key, long block)
    {
        if (_index is null || !_index.Videos.TryGetValue(key, out DiskVideo? video)
            || !video.Blocks.Remove(block, out DiskBlock? entry))
        {
            return;
        }

        UsedSlots.Remove(entry.Slot);
        _dirty = true;
        if (video.Blocks.Count == 0)
        {
            _index.Videos.Remove(key);
        }
    }

    // ===== 打开 / 索引 =====

    private static void EnsureOpenLocked()
    {
        if (_data is not null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(FolderPath);
            _data = new FileStream(DataPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
                bufferSize: 1, FileOptions.RandomAccess);
            TrySetLowIoPriority(_data.SafeFileHandle);

            _index = LoadIndex();
            if (_index is null)
            {
                // 索引没了，块文件里的槽号就没有意义：清空重来
                _data.SetLength(0);
                _index = new DiskIndex();
            }
            else
            {
                ValidateLocked();
            }
        }
        catch
        {
            _data?.Dispose();
            _data = null;
            _index = null;
            UsedSlots.Clear();
        }
    }

    /// <summary>索引与文件长度对不上（删过文件/写到一半掉电）就丢掉对不上的条目。</summary>
    private static void ValidateLocked()
    {
        long length = _data!.Length;
        List<(string Key, long Block)> broken = [];
        foreach ((string key, DiskVideo video) in _index!.Videos)
        {
            foreach ((long block, DiskBlock entry) in video.Blocks)
            {
                long end = (long)entry.Slot * BlockSize + entry.Length;
                if (entry.Slot < 0 || entry.Slot >= SlotCount || entry.Length <= 0 || end > length)
                {
                    broken.Add((key, block));
                    continue;
                }

                UsedSlots.Add(entry.Slot);
            }
        }

        foreach ((string key, long block) in broken)
        {
            DropBlockLocked(key, block);
        }
    }

    private static DiskIndex? LoadIndex()
    {
        try
        {
            if (!File.Exists(IndexPath))
            {
                return null;
            }

            DiskIndex? index = JsonSerializer.Deserialize(
                File.ReadAllBytes(IndexPath), VideoDiskCacheJsonContext.Default.DiskIndex);
            return index is { Version: DiskIndex.CurrentVersion } ? index : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveIndexIfDirty(bool force = false)
    {
        lock (Gate)
        {
            SaveIndexIfDirtyLocked(force);
        }
    }

    private static void SaveIndexIfDirtyLocked(bool force)
    {
        if (_index is null || !_dirty)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (!force && now - _lastSaveMs < IndexSaveIntervalMs)
        {
            return;
        }

        _lastSaveMs = now;
        _dirty = false;
        try
        {
            Directory.CreateDirectory(FolderPath);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(_index, VideoDiskCacheJsonContext.Default.DiskIndex);
            string temp = IndexPath + ".tmp";
            File.WriteAllBytes(temp, json);
            File.Move(temp, IndexPath, overwrite: true);
        }
        catch
        {
            _dirty = true;
        }
    }

    // ===== 低 IO 优先级 =====

    private const int FileIoPriorityHintInfoClass = 12;

    private const int IoPriorityHintVeryLow = 0;

    private const int ThreadModeBackgroundBegin = 0x00010000;

    private const int ThreadModeBackgroundEnd = 0x00020000;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIoPriorityHintInfo
    {
        public int PriorityHint;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file, int fileInformationClass, ref FileIoPriorityHintInfo info, int bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);

    private static void TrySetLowIoPriority(SafeFileHandle handle)
    {
        try
        {
            var info = new FileIoPriorityHintInfo { PriorityHint = IoPriorityHintVeryLow };
            SetFileInformationByHandle(handle, FileIoPriorityHintInfoClass, ref info, sizeof(int));
        }
        catch
        {
            // 挂不上就按默认优先级走，不影响缓存本身
        }
    }
}

/// <summary>块索引：视频键 → 已落的块。数量最多 512，每几秒整份重写一次。</summary>
internal sealed class DiskIndex
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public Dictionary<string, DiskVideo> Videos { get; set; } = [];
}

internal sealed class DiskVideo
{
    public long Total { get; set; }

    public long LastUse { get; set; }

    public Dictionary<long, DiskBlock> Blocks { get; set; } = [];
}

internal sealed class DiskBlock
{
    public int Slot { get; set; }

    public int Length { get; set; }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(DiskIndex))]
internal sealed partial class VideoDiskCacheJsonContext : JsonSerializerContext
{
}
