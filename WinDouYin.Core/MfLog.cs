namespace WinDouYin;

/// <summary>
/// 运行期关键节点，落到 %LocalAppData%\WinDouYin\mf.log。
/// 每次运行从头覆盖：这条分支要看的正是"本次启动走到哪一步"，留着历史只会看错。
/// 写文件失败（目录被占、盘满）不该把功能一起带走，所以整体吞掉。
///
/// 原先是播放舞台的私有类，后来提出来共用：推荐流那条链（取数、解析）是
/// `_ = MoveNextAsync()` 起的火，异常没人接——只有落进这里的日志才看得见它抛了什么。
///
/// 为什么 public 而不是 internal：分仓后它住在开源共享工程 <c>WinDouYin.Core</c> 里，闭源那半
/// （<c>WinDouYin.Douyin</c>）要调它写取数与签名的关键节点。不这么办就得两边各编一份自己的副本，
/// 两份 MfLog 去抢同一个 <c>mf.log</c>——<c>StreamWriter</c> 默认只开 FileShare.Read，第二份当场抛
/// IOException，日志反而少一半。
/// </summary>
public static class MfLog
{
    private static readonly object Gate = new();
    private static readonly string FilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDouYin", "mf.log");

    /// <summary>一直开着的写入器。</summary>
    private static System.IO.StreamWriter? _writer;

    public static void Open()
    {
        lock (Gate)
        {
            // 一条视频一个页面：每个页面都会来开一次日志。进程内只截一次，
            // 否则换台就把上一段日志截掉了——排查时正被这个坑过。
            if (_writer is not null)
            {
                _writer.WriteLine($"=== {DateTime.Now:O} 新播放页 ===");
                return;
            }
        }

        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            lock (Gate)
            {
                _writer = new System.IO.StreamWriter(FilePath, append: false) { AutoFlush = true };
                _writer.WriteLine($"=== {DateTime.Now:O} ===");
            }
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine("mf.log 打不开：" + e.Message);
        }
    }

    public static void Write(string line)
    {
        try
        {
            // 行首带毫秒时间戳：跨线程的先后在文件里本来就乱，判断"卡顿时谁在等谁"只能靠自己的时间戳
            string stamped = DateTime.Now.ToString("HH:mm:ss.fff") + " " + line;
            lock (Gate)
            {
                _writer?.WriteLine(stamped);
            }
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine("mf.log 写不进：" + e.Message);
        }
    }
}
