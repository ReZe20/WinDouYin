using WinDouYin.Contracts;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace WinDouYin;

/// <summary>
/// 按 Range 分段取流的只读随机访问流：Media Foundation 要 seek，WinRT 自带的 HTTP 流在非打包桌面进程里用不了，
/// 所以自己拿 HttpClient 拼。带浏览器 UA/Referer；/play/ 那条地址会先 302 再出 206，交给 HttpClient 自动跟随。
/// 长度不单独探测（多一发请求），从首个分段响应的 Content-Range 里顺带拿。
/// 取数全程按 1MB 对齐块走，并经过 <see cref="MediaCache"/>：同一地址在本会话内重开（回看、切回）直接命中内存，不再碰网络。
///
/// 异步段一律 ConfigureAwait(false)：这条流的调用方可能是 UI 线程（装载），但网络续体绝不该弹回 UI——
/// 首块的 1MB 落在哪个线程上读，就是哪个线程在等磁盘/网络。
/// </summary>
internal sealed partial class HttpRangeStream : Stream
{
    private static readonly HttpClient Http = new();

    private readonly string _url;

    // 跨进程认人用的键：整条地址每次播放都会换签名/节点，只有 /video/tos/… 对象后缀不变
    private readonly string _diskKey;
    private readonly Lock _gate = new();

    // Media Foundation 会从自己的线程上同时喂视频与音频解码器，读取与 seek 必须串行，
    // 否则偶发的"读数错位"会在播放中途表现为 DecodingError。
    // 工作缓冲整条流只租一根、Dispose 时归还：原来每次补取都 new 一根 1MB，一条视频就是几百次大对象分配
    private readonly byte[] _chunk = MediaCache.RentBuffer();
    private long _length = -1;
    private long _chunkStart = -1;
    private int _chunkLength;
    private long _position;

    // 出过一次事就彻底收手：解码器还在轮询读数，再抛第二次就是第二次存根异常
    private bool _dead;
    private bool _released;

    // 读前向同一时刻只允许一根链在跑：视频与音频两条读线程都在填块，不然会各发一份重复请求
    private int _warmBusy;

    public HttpRangeStream(string url)
    {
        _url = url;
        _diskKey = VideoDiskCache.KeyOf(url);
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            try
            {
                lock (_gate)
                {
                    return _dead ? SafeLength() : LengthCore();
                }
            }
            catch (Exception ex)
            {
                MarkDead(ex);
                return 0;
            }
        }
    }

    public override long Position
    {
        get
        {
            lock (_gate)
            {
                return _position;
            }
        }

        set
        {
            lock (_gate)
            {
                _position = value;
            }
        }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        try
        {
            lock (_gate)
            {
                if (!_dead)
                {
                    _position = origin switch
                    {
                        SeekOrigin.Begin => offset,
                        SeekOrigin.Current => _position + offset,
                        SeekOrigin.End => LengthCore() + offset,
                        _ => _position,
                    };
                }

                return _position;
            }
        }
        catch (Exception ex)
        {
            MarkDead(ex);
            return _position;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            lock (_gate)
            {
                if (_dead || _position >= LengthCore())
                {
                    return 0;
                }

                long inChunk = _position - _chunkStart;
                if (_chunkStart < 0 || inChunk < 0 || inChunk >= _chunkLength)
                {
                    FillChunk(_position);
                    inChunk = _position - _chunkStart;
                }

                int taken = Math.Min(count, _chunkLength - (int)inChunk);
                Array.Copy(_chunk, inChunk, buffer, offset, taken);
                _position += taken;
                return taken;
            }
        }
        catch (Exception ex)
        {
            MarkDead(ex);
            return 0;
        }
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // 流适配器回收我们时（换台收尾、GC 兜底），MF 那边很可能还有一读在路上：标记死透，之后一律按 EOF 回
        // 归还工作缓冲要在同一把锁里排队：判死之后没人再碰 _chunk，才敢把它交回池子。
        // 一根数组只能交回去一次——Close 和 Dispose 走的是同一条路，重复入池会让池子把同一根数组租给两家
        bool release;
        lock (_gate)
        {
            _dead = true;
            release = !_released;
            _released = true;
        }

        if (release)
        {
            MediaCache.ReturnBuffer(_chunk);
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// 这几处重写是 Media Foundation 在它自己的线程上、隔着 WinRT 边界调进来的，
    /// 异常抛出去就是一次 0xC000027B 存根异常，进程当场没——所以这条路上只许认输不许抛：
    /// 认一次失败，此后一律按 EOF 收，让解码器自己走 MediaFailed。
    /// </summary>
    private void MarkDead(Exception ex)
    {
        _dead = true;
        System.Diagnostics.Debug.WriteLine("[HttpRangeStream] 取数失败，按 EOF 收：" + ex.Message + " | " + _url);
    }

    /// <summary>已经死了就没法再去探长度，能给多少报多少。</summary>
    private long SafeLength() => _length < 0 ? 0 : _length;

    /// <summary>
    /// 整段下载到指定文件（右键菜单「保存视频」用）：同一套 UA/Referer，但**不带 Range**，
    /// 让服务端直接给完整 200。失败或取消时删掉半截文件，别在用户选的文件夹里留个坏 mp4。
    /// </summary>
    public static async Task DownloadToFileAsync(string url, string path, IProgress<long>? progress, CancellationToken ct)
    {
        bool created = false;
        try
        {
            using var request = NewRequest(url, null, null);
            using HttpResponseMessage response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            created = true;
            byte[] buffer = new byte[81920];
            long total = 0;
            int got;
            while ((got = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, got), ct).ConfigureAwait(false);
                total += got;
                progress?.Report(total);
            }
        }
        catch
        {
            // 只删自己刚建出来的半截文件；CreateNew 撞上同名文件时绝不碰别人的
            if (created)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // 删不掉也不该盖住真正的下载错误
                }
            }

            throw;
        }
    }

    /// <summary>
    /// 回退路径：有些 CDN 节点不吃 Range，那就整段落到临时文件再播本地 mp4。
    /// 这条路径实测能拿到 200，代价是要等整个文件下完。
    /// </summary>
    public static async Task<string> DownloadToTempFileAsync(string url)
    {
        string folder = Path.Combine(Path.GetTempPath(), "WinDouYin");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".mp4");

        using var request = NewRequest(url, null, null);
        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using (Stream body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        await using (FileStream file = File.Create(path))
        {
            await body.CopyToAsync(file).ConfigureAwait(false);
        }

        return path;
    }

    private long LengthCore()
    {
        if (_length < 0)
        {
            FillChunk(0);
        }

        return _length;
    }

    private void FillChunk(long from)
    {
        // 按 1MB 对齐块取：块号=起点/1MB。命中缓存直接把槽内容拷进本流的工作缓冲——回看/切回来的地址一个网络请求都不用发
        long blockStart = from / MediaCache.BlockSize * MediaCache.BlockSize;
        long block = blockStart / MediaCache.BlockSize;
        if (MediaCache.TryGet(_url, block, _chunk, out int length, out long totalLength))
        {
            _chunkStart = blockStart;
            _chunkLength = length;
            _length = totalLength;
            WarmAhead(blockStart, totalLength);
            return;
        }

        // 再退一步查磁盘：上一进程看过的视频（含重新打开后的推荐页）这里就能直接起播
        if (VideoDiskCache.TryRead(_diskKey, block, _chunk, out length, out totalLength))
        {
            _chunkStart = blockStart;
            _chunkLength = length;
            _length = totalLength;
            MediaCache.Put(_url, block, _chunk, length, totalLength);
            WarmAhead(blockStart, totalLength);
            return;
        }

        using HttpResponseMessage response = Http.SendAsync(
                NewRequest(_url, blockStart, blockStart + MediaCache.BlockSize - 1),
                HttpCompletionOption.ResponseHeadersRead)
            .GetAwaiter()
            .GetResult();
        FillFromResponse(response, blockStart);
        WarmAhead(blockStart, _length);
    }

    /// <summary>
    /// 读前向：刚服务完一块，就顺手把后面三块用异步路径取进缓存（约 3MB，按常见码率够垫 10~30 秒）。
    /// 因为 FillChunk 是跑在 Media Foundation 自己的读线程上的同步取数——缓存一 miss 就是 100~300ms 断供，
    /// 解码器等不到字节就掉帧。回看要 seek 到中途，那一块多半早被回收（槽池只有 48 块，长视频一段就能占满），
    /// 所以症状偏偏是往回切才明显。命中缓存时这里也会被再排一次，于是读前向一路跟着播放点走。
    /// </summary>
    private void WarmAhead(long blockStart, long total)
    {
        long from = blockStart + MediaCache.BlockSize;
        if (total <= from || Interlocked.Exchange(ref _warmBusy, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await WarmRangeAsync(from, Math.Min(total, from + 3L * MediaCache.BlockSize), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 预取失败不算这条流的死：正常读取路径自己会再试一次，那里才有判据
                System.Diagnostics.Debug.WriteLine("[HttpRangeStream] 预取失败：" + ex.Message + " | " + _url);
            }
            finally
            {
                Interlocked.Exchange(ref _warmBusy, 0);
            }
        });
    }

    /// <summary>
    /// 装载阶段主动预热一段字节：调用方知道这条地址上次停在哪（秒），按比例换成字节偏移，
    /// 在把流交给 Media Foundation 之前先把落点那几块取进内存——否则 seek 落点的第一读就是同步 HTTP，那一下就是掉帧。
    /// </summary>
    public async Task WarmAsync(long fromByte, CancellationToken ct)
    {
        long total = Length;
        if (total <= fromByte)
        {
            return;
        }

        long start = fromByte / MediaCache.BlockSize * MediaCache.BlockSize;
        await WarmRangeAsync(start, Math.Min(total, start + 3L * MediaCache.BlockSize), ct).ConfigureAwait(false);
    }

    private async Task WarmRangeAsync(long from, long to, CancellationToken ct)
    {
        for (long start = from; start < to; start += MediaCache.BlockSize)
        {
            ct.ThrowIfCancellationRequested();
            if (!MediaCache.Has(_url, start / MediaCache.BlockSize))
            {
                await CacheBlockAsync(start, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task CacheBlockAsync(long blockStart, CancellationToken ct)
    {
        long block = blockStart / MediaCache.BlockSize;
        byte[] buffer = MediaCache.RentBuffer();
        try
        {
            // 磁盘里已经有这一块就不必再走网络，直接回填内存
            if (VideoDiskCache.TryRead(_diskKey, block, buffer, out int diskLength, out long diskTotal))
            {
                MediaCache.Put(_url, block, buffer, diskLength, diskTotal);
                return;
            }

            using HttpResponseMessage response = await Http.SendAsync(
                NewRequest(_url, blockStart, blockStart + MediaCache.BlockSize - 1),
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                return;
            }

            int got = 0;
            using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            int n;
            while (got < MediaCache.BlockSize && (n = await body.ReadAsync(buffer.AsMemory(got, MediaCache.BlockSize - got), ct).ConfigureAwait(false)) > 0)
            {
                got += n;
            }

            long total = response.Content.Headers.ContentRange?.Length ?? -1;

            // 和同步路径同一条规矩：只有拿满这一块才进缓存，短读的块留着会让后续读取吃到空洞
            if (total > 0 && got == Math.Min((long)MediaCache.BlockSize, total - blockStart))
            {
                MediaCache.Put(_url, block, buffer, got, total);
                VideoDiskCache.RequestWrite(_diskKey, block, buffer, got, total);
            }
        }
        finally
        {
            MediaCache.ReturnBuffer(buffer);
        }
    }

    /// <summary>
    /// 先把首块拉下来。CreateFromStream 不会立即访问网络，403 要到 Media Foundation 在自己的线程上
    /// 读数据时才爆出来，那样就没法回退了，所以这里必须提前 await 一次。
    /// 缓存里已有首块（本会话取过同一地址）就等于验证过了，不再惊动网络。
    /// 这里同样 ConfigureAwait(false)：首块的落盘读法绝不该在 UI 线程上排队。
    /// </summary>
    public async Task PrimeAsync()
    {
        lock (_gate)
        {
            if (_length >= 0)
            {
                return;
            }

            if (MediaCache.TryGet(_url, 0, _chunk, out int length, out long totalLength))
            {
                _chunkStart = 0;
                _chunkLength = length;
                _length = totalLength;
                return;
            }

            // 首块在磁盘里（上一进程播过）就不用发这一发请求，起播直接走本地
            if (VideoDiskCache.TryRead(_diskKey, 0, _chunk, out length, out totalLength))
            {
                _chunkStart = 0;
                _chunkLength = length;
                _length = totalLength;
                MediaCache.Put(_url, 0, _chunk, length, totalLength);
                MfLog.Write($"cache: 首块磁盘命中 {length}B/{totalLength}B · {_diskKey}");
                return;
            }
        }

        using HttpResponseMessage response = await Http.SendAsync(NewRequest(_url, 0, MediaCache.BlockSize - 1), HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        lock (_gate)
        {
            if (_length >= 0)
            {
                return;
            }

            FillFromResponse(response, 0);
        }
    }

    private void FillFromResponse(HttpResponseMessage response, long blockStart)
    {
        response.EnsureSuccessStatusCode();

        // 不是 206 就说明服务端根本没理 Range，再按 from 记偏移会把后面的读取整体错位，
        // 所以这里直接判失败，让调用方回退到整段下载。
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new InvalidOperationException("服务端未按 Range 返回，状态 " + (int)response.StatusCode);
        }

        if (_length < 0)
        {
            _length = response.Content.Headers.ContentRange?.Length
                ?? response.Content.Headers.ContentLength
                ?? throw new InvalidOperationException("响应里没有视频长度");
        }

        int got = 0;
        using Stream body = response.Content.ReadAsStream();
        int n;
        while (got < MediaCache.BlockSize && (n = body.Read(_chunk, got, MediaCache.BlockSize - got)) > 0)
        {
            got += n;
        }

        _chunkStart = blockStart;
        _chunkLength = got;

        // 只有拿满这一块才进缓存：短读的块留着会让后续读取吃到空洞。Put 是拷进槽，_chunk 之后照旧归本流用
        if (got == Math.Min(MediaCache.BlockSize, _length - blockStart))
        {
            MediaCache.Put(_url, blockStart / MediaCache.BlockSize, _chunk, got, _length);
            VideoDiskCache.RequestWrite(_diskKey, blockStart / MediaCache.BlockSize, _chunk, got, _length);
        }
    }

    private static HttpRequestMessage NewRequest(string url, long? from, long? to)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        Douyin.Feed.ApplyMediaHeaders(request);

        if (from is not null)
        {
            // to 留空即 bytes=from- ，让服务端自己给 Content-Range 的总长度
            request.Headers.Range = new RangeHeaderValue(from, to);
        }

        return request;
    }
}
