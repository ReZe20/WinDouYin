using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinDouYin.Service;

/// <summary>
/// 本机使用账本：按天累计“内容实际播放了多久”，给本人主页的使用热力图用。
///
/// 落盘是<b>能直接看的 JSON</b>：<c>%LOCALAPPDATA%\WinDouYin\usage.&lt;账号&gt;.json</c>——
/// 日期、账号键都是明文，<b>只有每天的秒数是一段密文</b>（2026-10-22 用户点名：取消整本 DPAPI，
/// 改 json 记录，但时间要加密，且与日期和账号的某个固定值绑定）。
///
/// 算法选 <b>AES-256-GCM</b>（AEAD，BCL 自带、Native AOT 可用，不引第三方包），每条这么封：
/// <list type="number">
/// <item>钥匙用 <b>HKDF-SHA256</b> 现推，不落盘：IKM = 应用内固定胡椒，salt = 账号键
///   （uid 的 SHA256 前 16 位十六进制），info = <c>"day|yyyy-MM-dd"</c> →
///   每账号每天一把 32 字节钥匙，<b>换了账号或日期就解不开</b>；</item>
/// <item><c>"v3|账号键|日期"</c> 再当 AAD 参与认证——把某天的密文抄到别的日期/别的账号下，GCM 校验必失败；</item>
/// <item>存盘串 = <c>Base64(nonce 12B | tag 16B | 密文 8B)</c>，8 字节就是一个 double 秒数。</item>
/// </list>
/// 校验失败的只丢<b>那一条</b>（别的日期照用），并置 <see cref="RecordRejected"/>，热力图会出提示。
/// 仍然只挡“随手改”：胡椒在程序里、算法是公开的，真有人写脚本重新封一条照样能伪造，
/// 用户点名“真想改不拦着”。要真正不可伪造只能服务端签名。
///
/// 账号侧 <c>history/read</c> 虽然能读，但一页 20 条、按观看时间翻页，补一年要几千发，
/// 所以历史不做回填，从本版本开始按天累计。
///
/// 记的是墙钟播放时间（倍速按实际耗时算），暂停、缓冲、幕外候场都不进账；
/// 视频和图文 BGM 都算，时长比“看了几条”更能反映长视频与短视频的差别。
///
/// public 的理由与 <see cref="MfLog"/> 同一条：这本账有两个读者——界面侧记时长，闭源侧在认完账号之后调
/// <see cref="SetAccount"/> 把账本切到那个 uid 的本子上。各编一份副本的话就是两份进程内静态状态对着
/// 同一个 <c>usage.&lt;键&gt;.json</c> 各自攒、各自整本写回，后落盘的那本把前一天的累计盖掉。
/// </summary>
public static class UsageStore
{
    /// <summary>账本格式版本：1 存的是“条数”，2 存“秒数”但整本 DPAPI，3 起是“JSON + 按天 AES-GCM”。</summary>
    private const int CurrentVersion = 3;

    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int PayloadSize = sizeof(double);
    private const int SealedSize = NonceSize + TagSize + PayloadSize;

    /// <summary>一天最多 24 小时：解出来超这个数只可能是被硬塞的，按坏条目丢掉。</summary>
    private const double MaxSecondsPerDay = 86400;

    private static readonly object Gate = new();
    private static readonly string FolderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDouYin");

    /// <summary>HKDF 的 IKM：应用内固定胡椒。它不是秘密（程序里读得到），作用只是把我们的密文
    /// 与“换个人照 RFC 抄一遍”区分开；真正把值钉住的是 salt（账号键）与 info（日期）。</summary>
    private static readonly byte[] KeyPepper = SHA256.HashData(
        Encoding.UTF8.GetBytes("WinDouYin.UsageStore.v3|day-key"));

    /// <summary>最多留一年多一点；滚出去的日期不再占文件。</summary>
    private const int RetainDays = 400;

    /// <summary>两次落盘至少隔这么久：记账是每秒都在加的，不能每一拍都写文件。</summary>
    private const long SaveIntervalMs = 15000;

    private static UsageLog? _log;
    private static bool _loaded;
    private static long _lastSaveMs;

    /// <summary>当前账本属于哪个账号：uid 的哈希前 16 位。空串 = 没登录，不记账也不读盘。
    /// 它同时是文件名、UsageFile.Account 与 HKDF 的 salt——同一个值把三处钉在一起。</summary>
    private static string _accountKey = string.Empty;

    /// <summary>
    /// 盘上那份账本里有条目没能通过校验（被改过、日期被挪过、或文件损坏），那些条目已丢弃，
    /// 其余日期照用。热力图拿它出一句提示，别把“被改过”静默演成“本来就没记”。
    /// </summary>
    public static bool RecordRejected { get; private set; }

    /// <summary>内存里的账本（秒数明文）。落盘前后由 <see cref="UsageFile"/> 做一层“日期 → 密文”的映射。</summary>
    internal sealed class UsageLog
    {
        public int Version { get; set; } = CurrentVersion;

        /// <summary>账本归属（uid 的哈希前 16 位）：文件名与这里对不上就不认，防止两个号的账本对调。</summary>
        public string Account { get; set; } = string.Empty;

        /// <summary>yyyy-MM-dd → 当天累计播放秒数。</summary>
        public Dictionary<string, double> Days { get; set; } = [];
    }

    /// <summary>盘上那一份：日期明文、秒数密文。</summary>
    internal sealed class UsageFile
    {
        public int Version { get; set; } = CurrentVersion;

        public string Account { get; set; } = string.Empty;

        /// <summary>yyyy-MM-dd → Base64(nonce|tag|密文)。用 <see cref="SortedDictionary{TKey,TValue}"/>
        /// 让 JSON 里的日期按时间排好，人翻文件时好读。</summary>
        public SortedDictionary<string, string> Days { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// 切换账本归属（登录/换号/退出时调）。同一个号重复调是空操作；
    /// 换号会先把旧号还没落盘的秒数写掉，再按新号重新装盘。
    /// </summary>
    public static void SetAccount(string uid)
    {
        string key = uid.Length == 0 ? string.Empty : KeyOf(uid);
        lock (Gate)
        {
            if (string.Equals(_accountKey, key, StringComparison.Ordinal))
            {
                return;
            }

            if (_loaded)
            {
                SaveLocked(LoadLocked());   // 用旧 key 的路径落盘
            }

            _accountKey = key;
            _log = null;
            _loaded = false;
            _lastSaveMs = 0;
            RecordRejected = false;
        }
    }

    /// <summary>累计一段实际播放时间（调用方按播放状态自己折算）。没登录就不记账。</summary>
    public static void AddWatch(TimeSpan span)
    {
        double seconds = span.TotalSeconds;
        if (seconds <= 0)
        {
            return;
        }

        lock (Gate)
        {
            if (_accountKey.Length == 0)
            {
                return;
            }

            UsageLog log = LoadLocked();
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            // 一天顶格 24 小时：跨午夜回拨/改系统钟这类怪事下也不会记出一个解不开的荒唐值。
            log.Days[today] = Math.Min(log.Days.GetValueOrDefault(today) + seconds, MaxSecondsPerDay);
            PruneLocked(log);

            long now = Environment.TickCount64;
            if (now - _lastSaveMs >= SaveIntervalMs)
            {
                SaveLocked(log);
                _lastSaveMs = now;
            }
        }
    }

    /// <summary>把内存里的账本立刻落盘（拆台/离场时调，别把最后十几秒丢了）。</summary>
    public static void Flush()
    {
        lock (Gate)
        {
            if (!_loaded || _accountKey.Length == 0)
            {
                return;   // 这一程什么都没记过，不必凭拆台造一份空账本
            }

            SaveLocked(LoadLocked());
            _lastSaveMs = Environment.TickCount64;
        }
    }

    /// <summary>给热力图取一份只读快照（yyyy-MM-dd → 秒）；没登录时是空的。</summary>
    public static IReadOnlyDictionary<string, double> Snapshot()
    {
        lock (Gate)
        {
            if (_accountKey.Length == 0)
            {
                return new Dictionary<string, double>(StringComparer.Ordinal);
            }

            return new Dictionary<string, double>(LoadLocked().Days, StringComparer.Ordinal);
        }
    }

    private static UsageLog LoadLocked()
    {
        if (_loaded)
        {
            return _log ??= new UsageLog();
        }

        _loaded = true;

        if (_accountKey.Length == 0)
        {
            return _log ??= new UsageLog();
        }

        string path = FilePathFor(_accountKey);
        try
        {
            if (!File.Exists(path))
            {
                return _log ??= new UsageLog();
            }

            UsageFile? file = JsonSerializer.Deserialize(
                File.ReadAllText(path, Encoding.UTF8), UsageJsonContext.Default.UsageFile);
            if (file is null
                || file.Version != CurrentVersion
                || !string.Equals(file.Account, _accountKey, StringComparison.Ordinal))
            {
                RecordRejected = true;   // 不是这个号的本子（被对调/改过），或格式版本不认
                return _log ??= new UsageLog();
            }

            file.Days ??= new SortedDictionary<string, string>(StringComparer.Ordinal);
            var log = new UsageLog { Version = CurrentVersion, Account = _accountKey };
            bool bad = false;
            foreach ((string date, string sealedText) in file.Days)
            {
                if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out _)
                    || !TryOpenSeconds(_accountKey, date, sealedText, out double seconds))
                {
                    bad = true;   // 认不出的日期 / 解不开、被挪过的那一条：丢一条，别把整本废掉
                    continue;
                }

                log.Days[date] = seconds;
            }

            if (bad)
            {
                RecordRejected = true;
            }

            _log = log;
        }
        catch (JsonException)
        {
            RecordRejected = true;   // 不是合法 JSON / 值不是字符串：结构被改坏了，按“有条目没过校验”报出来
        }
        catch
        {
            // 读不出来（IO/权限这类）就当从今天开始，别让热力图把主页带崩
        }

        return _log ??= new UsageLog();
    }

    private static bool SaveLocked(UsageLog log)
    {
        if (_accountKey.Length == 0)
        {
            return false;
        }

        string path = FilePathFor(_accountKey);
        string temp = path + ".tmp";
        log.Account = _accountKey;
        try
        {
            Directory.CreateDirectory(FolderPath);
            var file = new UsageFile
            {
                Version = CurrentVersion,
                Account = _accountKey,
                Days = new SortedDictionary<string, string>(StringComparer.Ordinal),
            };
            foreach ((string date, double seconds) in log.Days)
            {
                if (seconds > 0)
                {
                    file.Days[date] = SealSeconds(_accountKey, date, seconds);
                }
            }

            string text = JsonSerializer.Serialize(file, UsageJsonContext.Default.UsageFile);

            // 先写临时文件再原子替换：写到一半掉电也不会留下半截 JSON
            File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch
        {
            // 记录失败不影响播放；下一次到点再写
            try
            {
                File.Delete(temp);
            }
            catch
            {
                // 临时文件清不掉也不影响主账本
            }

            return false;
        }
    }

    private static string FilePathFor(string accountKey) =>
        Path.Combine(FolderPath, "usage." + accountKey + ".json");

    /// <summary>文件名与 HKDF 的 salt 只放 uid 的哈希前 16 位：不把账号 ID 明晃晃摊在目录里，
    /// 也足够避免两个号撞名。</summary>
    private static string KeyOf(string uid) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uid)))[..16];

    private static void PruneLocked(UsageLog log)
    {
        string cutoff = DateTime.Now.AddDays(-RetainDays).ToString("yyyy-MM-dd");
        List<string> stale = [];
        foreach (string key in log.Days.Keys)
        {
            if (string.CompareOrdinal(key, cutoff) < 0)
            {
                stale.Add(key);
            }
        }

        foreach (string key in stale)
        {
            log.Days.Remove(key);
        }
    }

    // ===== 秒数额的封装：AES-256-GCM，钥匙按 (账号, 日期) 现推，不落盘 =====

    /// <summary>每账号每天一把钥匙：HKDF-SHA256(IKM=应用胡椒, salt=账号键, info="day|yyyy-MM-dd")。
    /// 所以把 A 号某天的密文搬到 B 号或别的日期下，解出来只会是 GCM 校验失败。</summary>
    private static byte[] DeriveDayKey(string accountKey, string date) =>
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            KeyPepper,
            KeySize,
            Encoding.UTF8.GetBytes(accountKey),
            Encoding.UTF8.GetBytes("day|" + date));

    /// <summary>AAD：把“我们这套格式 + 账号 + 日期”一起签进去，GCM 顺手就把“挪位置”这类改动判死。</summary>
    private static byte[] AadFor(string accountKey, string date) =>
        Encoding.UTF8.GetBytes("v3|" + accountKey + "|" + date);

    private static string SealSeconds(string accountKey, string date, double seconds)
    {
        byte[] key = DeriveDayKey(accountKey, date);
        byte[] aad = AadFor(accountKey, date);
        byte[] packed = new byte[SealedSize];

        Span<byte> nonce = packed.AsSpan(0, NonceSize);
        Span<byte> tag = packed.AsSpan(NonceSize, TagSize);
        Span<byte> cipher = packed.AsSpan(NonceSize + TagSize, PayloadSize);
        Span<byte> plain = stackalloc byte[PayloadSize];
        BitConverter.TryWriteBytes(plain, seconds);
        RandomNumberGenerator.Fill(nonce);

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plain, cipher, tag, aad);
        }

        return Convert.ToBase64String(packed);
    }

    private static bool TryOpenSeconds(string accountKey, string date, string sealedText, out double seconds)
    {
        seconds = 0;
        byte[] packed;
        try
        {
            packed = Convert.FromBase64String(sealedText);
        }
        catch
        {
            return false;
        }

        if (packed.Length != SealedSize)
        {
            return false;
        }

        byte[] key = DeriveDayKey(accountKey, date);
        byte[] aad = AadFor(accountKey, date);
        Span<byte> plain = stackalloc byte[PayloadSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(
                packed.AsSpan(0, NonceSize),
                packed.AsSpan(NonceSize + TagSize, PayloadSize),
                packed.AsSpan(NonceSize, TagSize),
                plain,
                aad);
        }
        catch (CryptographicException)
        {
            return false;
        }

        double value = BitConverter.ToDouble(plain);
        if (!double.IsFinite(value) || value < 0 || value > MaxSecondsPerDay)
        {
            return false;   // 解开了但数值荒唐：同样按坏条目丢掉
        }

        seconds = value;
        return true;
    }
}

/// <summary>源生成序列化（Native AOT 下不用反射）。JSON 写明缩进：人翻文件、手改都容易看清结构
/// （<c>version</c> / <c>account</c> / <c>days: { "yyyy-MM-dd": "密文" }</c>）——日期与账号键本来就看得懂，
/// 只有每个日期对应的那段 `days` 值才是密文。</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(UsageStore.UsageFile))]
internal sealed partial class UsageJsonContext : JsonSerializerContext
{
}
