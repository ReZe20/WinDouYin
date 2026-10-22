using Microsoft.UI.Xaml;
using WinDouYin.Contracts;
using WinDouYin.Service;
using WinDouYin.ViewModels;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinDouYin
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        private static readonly string StartupLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDouYin", "startup.log");

        private static StreamWriter? _startupLog;

        /// <summary>启动取证：把每一步落到盘上，不带调试器也能看出停在哪儿。每次启动截断，不留历史。</summary>
        internal static void Log(string message)
        {
            try
            {
                _startupLog ??= CreateWriter();
                _startupLog.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message);
            }
            catch
            {
                // 日志写不下去不算事
            }
        }

        private static StreamWriter CreateWriter()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StartupLogPath)!);
            return new StreamWriter(StartupLogPath, append: false) { AutoFlush = true };
        }

        /// <summary>全应用共享的设置视图模型单例，页面一律从这里取，不得自建。</summary>
        public SettingsViewModel ViewModel { get; private set; } = null!;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
            ViewModel = new SettingsViewModel(new ConfigService());
            TrackDisposedThrows();
            UnhandledException += (_, e) => LogException("xaml-unhandled", e.Exception);
        }

        private static readonly string ExceptionLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDouYin", "exception.log");

        private static readonly object ExceptionGate = new();
        private static bool _exceptionLogStarted;
        private static int _exceptionLogCount;

        /// <summary>
        /// 崩溃取证：0xC000027B 是 WinRT 存根异常，XAML/AppDomain 的 Unhandled 都未必拦得到，
        /// 但被裹进去的托管异常（历史实测是 ObjectDisposedException）会先以 FirstChance 出现。
        /// 落盘而不是只写 Debug——没挂调试器时也能拿到最后一发异常的类型/消息/栈。
        /// </summary>
        private static void LogException(string source, Exception? ex)
        {
            if (ex is null)
            {
                return;
            }

            try
            {
                lock (ExceptionGate)
                {
                    if (!_exceptionLogStarted)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(ExceptionLogPath)!);
                        try
                        {
                            // 只保留最近一段：崩溃后重启不能把上一轮的现场冲掉
                            if (new FileInfo(ExceptionLogPath).Length > 2_000_000)
                            {
                                File.WriteAllText(ExceptionLogPath, string.Empty);
                            }
                        }
                        catch
                        {
                            // 读不到大小就照常追加
                        }

                        File.AppendAllText(ExceptionLogPath, $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\r\n");
                        _exceptionLogStarted = true;
                    }

                    if (_exceptionLogCount++ >= 200)
                    {
                        return;
                    }

                    File.AppendAllText(ExceptionLogPath, $"{DateTime.Now:HH:mm:ss.fff} [{source}] {ex}\r\n");
                }
            }
            catch
            {
                // 取证不能反过来把进程带走
            }
        }

        /// <summary>
        /// 崩溃取证钩子（拿掉之前先确认往回切不崩了）。
        /// 换台崩的退出码是 0xC000027B——WinRT 存根异常，由框架在别人的线程上生成，
        /// UnhandledException、try/catch、VS 的异常设置全都拦不到它，只能看到进程没了。
        /// 但它内部裹着的那个托管异常（本机 WER 签名实测 0x80000013 = ObjectDisposedException）
        /// 是先在托管侧抛出、再被裹进去的：FirstChanceException 正好处在"刚抛出、还没被裹"的那一刻，
        /// 于是能把类型名、ObjectName 和完整托管栈打出来，直接指认是谁被提前释放了。
        /// </summary>
        private static void TrackDisposedThrows()
        {
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                Exception ex = e.Exception;
                if (ex is ObjectDisposedException or NullReferenceException or InvalidOperationException)
                {
                    System.Diagnostics.Debug.WriteLine("[" + ex.GetType().Name + "] " + ex);
                    LogException("first-chance", ex);
                }
            };

            AppDomain.CurrentDomain.UnhandledException +=
                (_, e) => LogException("appdomain-unhandled", e.ExceptionObject as Exception);
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            Log("OnLaunched 进");
            try
            {
                // 先装载设置， MainWindow 要用它决定起始页面
                await ViewModel.InitializeAsync();
                Log("设置已装载");

                // 组合根：闭源那半对外的唯一入口就这一枚 DouyinBoot（见 DouyinBoot.cs），
                // 挂上之后界面只认 Douyin.Feed，编译期碰不到任何实现类型。
                DouyinBoot.Attach();

                // 会话要在第一次取数之前灌回罐子，否则首屏拿到的还是匿名那份
                await Douyin.Feed.RestoreSessionAsync();

                await ViewModel.LoginVM.RefreshLoginStateAsync();
                Log("会话已还原，已登录=" + ViewModel.LoginVM.IsLoggedIn);

                // --webprobe：只开探针窗（看得见、能点、自带抓包），不建主窗。做写接口测绘用，见 WebProbeWindow。
                // --webprobe-all（全量记录）也算这个名字——它只加记录范围，别因为名字长了一截就落进主窗。
                string[] argv = Environment.GetCommandLineArgs();
                if (argv.Contains("--webprobe", StringComparer.Ordinal) || argv.Contains("--webprobe-all", StringComparer.Ordinal))
                {
                    _window = new WebProbeWindow();
                    _window.Activate();
                    Log("webprobe 窗口已开");
                    return;
                }

                _window = new MainWindow();
                Log("MainWindow 已构造");

                _window.Activate();
                Log("Activate 已调");

                // 收藏夹在登录态就绪后立刻预取，悬停面板直接用缓存（读失败不影响首屏）
                if (ViewModel.LoginVM.IsLoggedIn)
                {
                    Douyin.Feed.PrefetchCollects();
                    Douyin.Feed.PrefetchMessages();

                    // 精选第一页要三四秒（3MB 上下）且要排取数节拍：先让它在后台跑，
                    // 用户点到「精选」时账面已经有内容；若点得更早就等同一发，不重复取。
                    FeaturedViewModel.Shared.PrefetchAfter(
                        FeaturedViewModel.CategoryTable[0], TimeSpan.FromSeconds(3));
                }

                // 首帧到底画没画出来，只能事后问一句布局有没有发生
                if (_window.Content is Microsoft.UI.Xaml.FrameworkElement root)
                {
                    root.LayoutUpdated += (_, _) => LogOnce("首帧布局已更新");
                    root.Loaded += (_, _) => LogOnce("根元素 Loaded");
                }

                Heartbeat();
            }
            catch (Exception ex)
            {
                Log("启动抛异常：" + ex);
            }
        }

        private static int _layoutLogged;

        /// <summary>
        /// 分家用的：心跳还在走 = UI 线程活着，那就是合成器没出帧；心跳停 = 卡在某个回调里。
        /// 这两条的查法完全不同，所以先把它们分开。
        /// </summary>
        private static void Heartbeat()
        {
            var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (queue is null)
            {
                Log("心跳：拿不到 DispatcherQueue");
                return;
            }

            int beat = 0;
            Microsoft.UI.Dispatching.DispatcherQueueTimer timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1);
            timer.IsRepeating = true;
            timer.Tick += (_, _) =>
            {
                if (++beat <= 6)
                {
                    Log("心跳 " + beat);
                }
                else
                {
                    timer.Stop();
                }
            };
            timer.Start();
        }

        private static void LogOnce(string message)
        {
            if (Interlocked.Exchange(ref _layoutLogged, 1) == 0)
            {
                Log(message);
            }
        }
    }
}
