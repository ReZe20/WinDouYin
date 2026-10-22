using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using WinDouYin.Contracts;

namespace WinDouYin;

/// <summary>
/// 开发用探针窗口：<c>WinDouYin.exe --webprobe</c> 只开它，不开主窗。
/// 一个**看得见、能点**的 WebView2，开在固定的 profile 上（<c>%LOCALAPPDATA%\WinDouYin\webprobe-profile</c>，
/// 登录态能留住、重启不丢），并且整个网络层挂着 CDP 抓包——
/// 凡是打在抖音上的请求与响应（**全套请求头、query、body、状态、响应头名、响应体**）都落进
/// <c>%LOCALAPPDATA%\WinDouYin\webprobe.log</c>。
///
/// 为什么要有它（2026-10-14）：发弹幕/点赞这些**写**接口在纯 C# 那条路上会回 200 空包（被服务端丢），
/// 而浏览器里同样的操作没事——要把"浏览器到底多带了什么"逐字看清，就得有个能真发、又能抓的地方。
/// 它顺带就是"写操作走 WebView2"那条备选路线的原型。
/// 日志里 **Cookie 与 Set-Cookie 的值一律不落**（只留名字与属性），凭据不外流。
///
/// 2026-10-19 补三条，收口"登录那一发抓不到"的取证盲区：
/// ① **文档/导航应答一律记**（不受观察名单约束）——会话是随顶层导航的 Set-Cookie 落进罐子的，
///    按 URL 里的 `passport` 关键字永远逮不到它；② 挂 <c>responseReceivedExtraInfo</c> 逐枚记
///    Set-Cookie 的名字+域+路径+属性，连**没落成的**（blockedCookieLines / blockReason）一起记，
///    名单每变一次对一次罐子、关键 cookie 落罐打 ★——<c>responseReceived</c> 的 headers 是合并过的，
///    同名只留一条，Set-Cookie 只有 extraInfo 这一发才齐全；③ 挂 WS 建链/首帧/关闭，长连接不再是盲区。
/// </summary>
internal sealed class WebProbeWindow : Window
{
    private const string StartUrl = "https://www.douyin.com/video/7684255763782520107";

    private static readonly string FolderPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDouYin");

    private static readonly string ProfileDir = Path.Combine(FolderPath, "webprobe-profile");
    private static readonly string LogPath = Path.Combine(FolderPath, "webprobe.log");
    private static readonly object Gate = new();

    /// <summary>
    /// <c>--webprobe-cookies=&lt;路径&gt;</c>：每次导航完成后把这个 profile 里 douyin 域的整罐 cookie
    /// 导成 jar（与 <c>session.cookies.json</c> 同形、PascalCase）——给命令行探针当"活会话"用，值不落日志。
    /// </summary>
    private static readonly string CookieOut = GetArg("--webprobe-cookies=");

    /// <summary>
    /// <c>--webprobe-cookies-in=&lt;路径&gt;</c>：导航前把 session.cookies.json 灌进这个 profile。
    /// 探针 profile 与登录框 profile 是分开的；要看"登录后自己的主页"，不用重扫一次码。
    /// 只读文件、不落值，异常只报枚数。
    /// </summary>
    private static readonly string CookieIn = GetArg("--webprobe-cookies-in=");

    /// <summary>有哪些请求要"全过程"记（其余只记一行）。图文帖调查：aweme_images 的图也要在名单里。</summary>
    private static readonly string[] Watch =
    [
        "/danmaku/", "mssdk", "asgw", "report", "web_sign", "ticket", "passport", "collect", "commit", "dislike",
        "aweme/detail", "aweme_images", "image_post", "/share/video", "slidelist", "slide/v1",
    ];

    /// <summary>--webprobe-all：全量记录版——观察名单之外的请求也刻（举报/回复这种还没摸清名单的就这么逮）。</summary>
    private static readonly bool LogAll = Environment.GetCommandLineArgs().Contains("--webprobe-all", StringComparer.Ordinal);

    /// <summary>全量模式下这些静态资源不抓正文（没信息量，还撑爆日志）。</summary>
    private static readonly string[] AssetExt =
    [
        ".js", ".css", ".png", ".jpg", ".jpeg", ".webp", ".gif", ".svg", ".ico", ".woff", ".woff2", ".mp4", ".avif",
    ];

    private static string GetArg(string prefix)
    {
        foreach (string a in Environment.GetCommandLineArgs())
        {
            if (a.StartsWith(prefix, StringComparison.Ordinal))
            {
                return a[prefix.Length..];
            }
        }

        return string.Empty;
    }

    /// <summary>--webprobe-url=<url>：探针窗起手就导航到这条 URL（不填用默认的视频页）。</summary>
    private static readonly string StartUrlArg = GetArg("--webprobe-url=");

    private readonly WebView2 _view = new();
    private readonly TextBlock _status = new()
    {
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(10, 6, 10, 6),
        Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
    };

    /// <summary>requestId → url，loadingFinished 时拿它认领响应体。</summary>
    private readonly Dictionary<string, string> _pending = [];

    /// <summary>WS 账本：requestId →（url、收帧数、首帧前缀）。连接一多就数帧不存原文，只有首帧落日志。</summary>
    private readonly Dictionary<string, (string Url, int Frames, string First)> _ws = [];

    /// <summary>文档应答的 requestId：loadingFinished 靠它认领"这一跳的正文也要"。</summary>
    private readonly HashSet<string> _docIds = [];

    /// <summary>罐子名字快照（name@domain）：变了就记一行"落点"，只比名字、不碰值。</summary>
    private HashSet<string> _jarNames = [];

    private bool _jarBusy;

    /// <summary>登录链的路标 cookie：这些落罐要在日志里一眼看见。</summary>
    private static readonly string[] KeyCookies =
    [
        "sessionid", "sessionid_ss", "ssid_ucp_v1", "d_ticket", "passport_mfa_token",
        "passport_csrf_token", "ttwid", "webid", "bd_ticket_guard_client_data",
    ];

    public WebProbeWindow()
    {
        Title = "WinDouYin 探针：在这个窗口里登录并发弹幕";

        var panel = new Grid { Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 18, 18, 20)) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_status, 0);
        Grid.SetRow(_view, 1);
        panel.Children.Add(_status);
        panel.Children.Add(_view);

        _status.Text = "起 WebView2…（这个窗口里的每一步请求都会被记进 webprobe.log）";
        Content = panel;
        AppWindow.Resize(new SizeInt32(1180, 860));

        _view.CoreWebView2Initialized += (_, _) => OnCoreReady();
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            // 固定 profile：登录态留住（别用默认的临时目录，重启就没了）
            var options = new CoreWebView2EnvironmentOptions
            {
                // 全量抓包（--webprobe-all）时要拿"举报那一发"这种还没进观察名单的请求，出事了能自己上手看
                AdditionalBrowserArguments = "--remote-debugging-port=9222",
            };
            CoreWebView2Environment env = await CoreWebView2Environment.CreateWithOptionsAsync(null, ProfileDir, options);
            await _view.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            Note("起不了内核：" + ex.Message);
        }
    }

    private void OnCoreReady()
    {
        CoreWebView2 core = _view.CoreWebView2;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;

        core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent").DevToolsProtocolEventReceived +=
            (_, e) => OnRequestSent(e.ParameterObjectAsJson);
        core.GetDevToolsProtocolEventReceiver("Network.responseReceived").DevToolsProtocolEventReceived +=
            (_, e) => OnResponse(e.ParameterObjectAsJson);
        core.GetDevToolsProtocolEventReceiver("Network.loadingFinished").DevToolsProtocolEventReceived +=
            (_, e) => OnLoadingFinished(e.ParameterObjectAsJson);
        // Set-Cookie 逐枚齐全（含没落成的那几枚）只在这一发里给；responseReceived 的 headers 同名只留一条
        core.GetDevToolsProtocolEventReceiver("Network.responseReceivedExtraInfo").DevToolsProtocolEventReceived +=
            (_, e) => OnExtraInfo(e.ParameterObjectAsJson);
        core.GetDevToolsProtocolEventReceiver("Network.webSocketCreated").DevToolsProtocolEventReceived +=
            (_, e) => OnWsCreated(e.ParameterObjectAsJson);
        core.GetDevToolsProtocolEventReceiver("Network.webSocketFrameReceived").DevToolsProtocolEventReceived +=
            (_, e) => OnWsFrame(e.ParameterObjectAsJson);
        core.GetDevToolsProtocolEventReceiver("Network.webSocketClosed").DevToolsProtocolEventReceived +=
            (_, e) => OnWsClosed(e.ParameterObjectAsJson);
        // 导航本身：会话就是随某一跳主帧导航的 Set-Cookie 落的，得看得见"哪一跳、什么 MIME"
        core.GetDevToolsProtocolEventReceiver("Page.frameNavigated").DevToolsProtocolEventReceived +=
            (_, e) => OnFrameNavigated(e.ParameterObjectAsJson);
        _ = core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
        _ = core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");

        if (CookieIn.Length > 0)
        {
            ImportCookies(core);
        }

        _view.Source = new Uri(StartUrlArg.Length > 0 ? StartUrlArg : StartUrl);
        if (CookieOut.Length > 0)
        {
            // 每次导航完等 3 秒再导（让 Set-Cookie 落地），覆盖式写：拿到的永远是最新那罐。
            _view.NavigationCompleted += async (_, _) =>
            {
                try
                {
                    await Task.Delay(3000);
                    await DumpCookiesAsync();
                }
                catch (Exception ex)
                {
                    Note("cookie 导出异常：" + ex.Message);
                }
            };
        }

        Note("抓包已挂（导航/Set-Cookie 落点/WS 都记）。用法：在页面里做你要的动作（登录、评论、点赞、任何点按），完事聊天里说一声对账。日志：webprobe.log");
    }

    /// <summary>把 douyin 域的整罐 cookie 导成 jar（与 session.cookies.json 同形）。只落本地文件，值不进日志。</summary>
    private async Task DumpCookiesAsync()
    {
        IReadOnlyList<CoreWebView2Cookie> list = await _view.CoreWebView2.CookieManager.GetCookiesAsync("https://www.douyin.com");
        SessionCookie[] seeds = list.Select(c => new SessionCookie(
            c.Name,
            c.Value,
            c.Domain,
            c.Path.Length == 0 ? "/" : c.Path,
            c.Expires > 0 && c.Expires < 4_102_444_800 ? (long)c.Expires : 0)).ToArray();
        string text = JsonSerializer.Serialize(seeds, ProbeCookieJsonContext.Default.SessionCookieArray);
        string? dir = Path.GetDirectoryName(CookieOut);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(CookieOut, text);
        Note($"cookie 已导出（{seeds.Length} 枚）→ {CookieOut}");
    }

    /// <summary>把 session.cookies.json 灌进探针 profile（只报成功枚数，值不进日志）。</summary>
    private void ImportCookies(CoreWebView2 core)
    {
        try
        {
            if (!File.Exists(CookieIn))
            {
                Note("cookie 导入：文件不存在 → " + CookieIn);
                return;
            }

            SessionCookie[] seeds = JsonSerializer.Deserialize(
                File.ReadAllText(CookieIn), ProbeCookieJsonContext.Default.SessionCookieArray) ?? [];
            CoreWebView2CookieManager manager = core.CookieManager;
            int added = 0;
            foreach (SessionCookie s in seeds)
            {
                try
                {
                    if (s.Name.Length == 0 || s.Domain.Length == 0)
                    {
                        continue;
                    }

                    CoreWebView2Cookie cookie = manager.CreateCookie(
                        s.Name, s.Value, s.Domain, s.Path.Length == 0 ? "/" : s.Path);
                    if (s.ExpiresUnix > 0)
                    {
                        cookie.Expires = s.ExpiresUnix;
                    }

                    manager.AddOrUpdateCookie(cookie);
                    added++;
                }
                catch
                {
                    // 单枚不合规（__Host- 域约束、过期时间等）跳过，不拖累整罐
                }
            }

            Note($"cookie 已导入探针 profile：{added}/{seeds.Length} 枚（值不落日志）");
        }
        catch (Exception ex)
        {
            Note("cookie 导入异常：" + ex.GetType().Name);
        }
    }

    private void OnRequestSent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement p = doc.RootElement;
            if (!p.TryGetProperty("request", out JsonElement req))
            {
                return;
            }

            string url = Str(req, "url");
            string method = Str(req, "method");
            string id = Str(p, "requestId");
            string type = Str(p, "type");
            _pending[id] = url;

            // 文档/导航一律记（它就是"会话落罐那一跳"的载体）；其余仍按观察名单筛，别把日志淹了
            bool watched = LogAll || type.Equals("Document", StringComparison.Ordinal)
                || Watch.Any(w => url.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (!watched)
            {
                return;   // 没在观察名单里的只当作背景噪音
            }

            var sb = new StringBuilder();
            sb.Append("\n>>> ").Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ').Append(method).Append(' ').Append(url);
            if (type.Length > 0)
            {
                sb.Append("  [").Append(type).Append("]");
            }

            if (req.TryGetProperty("headers", out JsonElement hs) && hs.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty h in hs.EnumerateObject())
                {
                    // Cookie 绝不落值——只报它有几枚
                    if (h.Name.Equals("cookie", StringComparison.OrdinalIgnoreCase))
                    {
                        int n = (h.Value.GetString() ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries).Length;
                        sb.Append("\n    cookie: (").Append(n).Append(" 枚，值不落日志)");
                        continue;
                    }

                    sb.Append("\n    ").Append(h.Name).Append(": ").Append(h.Value.GetString());
                }
            }

            string? post = req.TryGetProperty("postData", out JsonElement pd) ? (pd.ValueKind == JsonValueKind.String ? pd.GetString() : pd.ToString()) : null;
            if (!string.IsNullOrEmpty(post))
            {
                // 分享卡/发送链要紧的字段（message_type、f9、ticket）在正文后半段，1200 会切断；
                // 但身份令牌是凭据，落盘前先打码。
                post = System.Text.RegularExpressions.Regex.Replace(post, "\"token\":\"[^\"]*\"", "\"token\":\"…\"");
                int limit = url.Contains("/v1/message/send", StringComparison.OrdinalIgnoreCase) ? 20000 : 1200;
                sb.Append("\n    body: ").Append(post.Length > limit ? post[..limit] + "…" : post);
            }

            Write(sb.ToString());
        }
        catch (Exception ex)
        {
            Write("!! requestWillBeSent 解析失败：" + ex.Message);
        }
    }

    private void OnResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement p = doc.RootElement;
            if (!p.TryGetProperty("response", out JsonElement resp))
            {
                return;
            }

            string url = Str(resp, "url");
            string type = Str(p, "type");
            bool isDoc = type.Equals("Document", StringComparison.Ordinal);
            if (!LogAll && !isDoc && !Watch.Any(w => url.Contains(w, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            int status = resp.TryGetProperty("status", out JsonElement s) && s.TryGetInt32(out int sv) ? sv : 0;
            var sb = new StringBuilder();
            sb.Append("\n<<< ").Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ').Append(status).Append(' ').Append(url);
            if (type.Length > 0)
            {
                sb.Append("  [").Append(type).Append("]");
            }

            bool landed = false;
            if (resp.TryGetProperty("headers", out JsonElement hs) && hs.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty h in hs.EnumerateObject())
                {
                    if (h.Name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.Append("\n    set-cookie: ").Append(CookieLanding(h.Value));
                        landed = true;
                        continue;
                    }

                    if (h.Name.StartsWith("x-", StringComparison.OrdinalIgnoreCase) || h.Name is "content-type" or "content-length")
                    {
                        sb.Append("\n    ").Append(h.Name).Append(": ").Append(h.Value.GetString());
                    }
                }
            }

            // 302 那一跳的应答躲在 redirectResponse 里，导航链就得整条看见（会话常常是链尾那跳落的）
            if (resp.TryGetProperty("redirectResponse", out JsonElement rr) && rr.ValueKind == JsonValueKind.Object)
            {
                sb.Append("\n    上一跳: ").Append(Raw(rr, "status")).Append(' ').Append(Shorten(Str(rr, "url"), 110));
                if (rr.TryGetProperty("headers", out JsonElement rhs) && rhs.ValueKind == JsonValueKind.Object
                    && rhs.TryGetProperty("set-cookie", out JsonElement rsc))
                {
                    sb.Append("\n    上一跳 set-cookie: ").Append(CookieLanding(rsc));
                    landed = true;
                }
            }

            Write(sb.ToString());
            if (isDoc)
            {
                _docIds.Add(Str(p, "requestId"));   // loadingFinished 那一拍靠它认领正文
            }

            if (landed || isDoc)
            {
                _ = DiffJarAsync();
            }
        }
        catch (Exception ex)
        {
            Write("!! responseReceived 解析失败：" + ex.Message);
        }
    }

    private async void OnLoadingFinished(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            string id = doc.RootElement.TryGetProperty("requestId", out JsonElement r) ? r.GetString() ?? string.Empty : string.Empty;
            if (id.Length == 0 || !_pending.TryGetValue(id, out string? url))
            {
                return;
            }

            _pending.Remove(id);
            // 只对"有判决意义的应答"取正文：写接口那三条、观察名单命中的（challenge/validate_code 回的
            // 就是判据本身）、文档那一跳。静态资源照旧跳过。
            bool wanted = LogAll || _docIds.Remove(id)
                || url.Contains("/danmaku/", StringComparison.OrdinalIgnoreCase)
                || url.Contains("mssdk", StringComparison.OrdinalIgnoreCase)
                || url.Contains("asgw", StringComparison.OrdinalIgnoreCase)
                || Watch.Any(w => url.Contains(w, StringComparison.OrdinalIgnoreCase));
            if (!wanted || AssetExt.Any(e => url.Contains(e, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            string args = "{\"requestId\":" + JsonText.Quote(id) + "}";
            string body = await _view.CoreWebView2.CallDevToolsProtocolMethodAsync("Network.getResponseBody", args);
            Write("\n    resp-body: " + Shorten(Scrub(body), 1200));
        }
        catch (Exception ex)
        {
            Write("!! getResponseBody 失败：" + ex.Message);
        }
    }

    /// <summary>
    /// Set-Cookie 的逐枚原文只有这一发齐全（<c>responseReceived</c> 的 headers 同名只留一条），
    /// 而"该落却没落"的那几枚（blockedCookieLines / blockReason）也只出现在这里——
    /// 会话没落罐时要看的就是这条。同样只记名字与属性，值不落。
    /// </summary>
    private void OnExtraInfo(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement p = doc.RootElement;
            var sb = new StringBuilder();
            bool any = false;

            if (p.TryGetProperty("headers", out JsonElement hs) && hs.ValueKind == JsonValueKind.Object
                && hs.TryGetProperty("set-cookie", out JsonElement sc))
            {
                sb.Append("\n    set-cookie: ").Append(CookieLanding(sc));
                any = true;
            }

            if (p.TryGetProperty("blockedCookieLines", out JsonElement bl) && bl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement line in bl.EnumerateArray())
                {
                    if (line.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    sb.Append("\n    没落成: ").Append(Blocked(line.GetString() ?? string.Empty));
                    any = true;
                }
            }

            if (p.TryGetProperty("associatedCookies", out JsonElement ac) && ac.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement c in ac.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("blockReason", out JsonElement br)
                        || br.ValueKind != JsonValueKind.String || br.GetString() is not { Length: > 0 } why)
                    {
                        continue;   // 没被挡的别占行，罐里每枚都列一遍就没法看了
                    }

                    JsonElement ck = c.TryGetProperty("cookie", out JsonElement x) && x.ValueKind == JsonValueKind.Object
                        ? x : default;
                    sb.Append("\n    被挡: ").Append(Str(ck, "name")).Append('@').Append(Str(ck, "domain")).Append(Str(ck, "path"))
                        .Append(" 原因=").Append(Shorten(why, 160));
                    any = true;
                }
            }

            if (!any)
            {
                return;
            }

            Write("\n### " + DateTime.Now.ToString("HH:mm:ss.fff") + " 落点 " + Shorten(Str(p, "url"), 110) + sb);
            _ = DiffJarAsync();
        }
        catch (Exception ex)
        {
            Write("!! responseReceivedExtraInfo 解析失败：" + ex.Message);
        }
    }

    /// <summary>Set-Cookie 的落点写法：名字+域+路径+属性，**值一律不落**；同名多枚（数组或换行拼接）逐条摊开。</summary>
    private static string CookieLanding(JsonElement value)
    {
        var parts = new List<string>();
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement v in value.EnumerateArray())
            {
                if (v.ValueKind == JsonValueKind.String)
                {
                    AddLandings(parts, v.GetString() ?? string.Empty);
                }
            }
        }
        else
        {
            AddLandings(parts, value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText());
        }

        return string.Join(" | ", parts);
    }

    /// <summary>
    /// Chromium 在合并过的 headers 里会把多枚 Set-Cookie **用换行拼成一条串**——不先按行切开，
    /// 后一枚的 `名字=值` 会被当前这一枚当成属性整段抄进日志（2026-10-19 实测泄漏过 `__ac_nonce` 的值）。
    /// </summary>
    private static void AddLandings(List<string> into, string raw)
    {
        foreach (string line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            into.Add(Landing(line));
        }
    }

    private static string Landing(string raw)
    {
        string[] seg = raw.Split(';');
        int first = seg[0].IndexOf('=');
        var attrs = new List<string>();
        foreach (string a in seg.Skip(1))
        {
            string t = a.Trim();
            int i = t.IndexOf('=');
            string key = (i > 0 ? t[..i] : t).Trim();
            // Expires 那串又长又没信息量——落点时间由日志自己盖
            if (key.Equals("Expires", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (key is "Domain" or "Path" or "Max-Age" or "HttpOnly" or "Secure" or "SameSite" or "Partitioned")
            {
                // 值只取等号后到下一个空白为止，多余内容（换行拼进来的下一枚）一律丢掉
                string v = i > 0 ? t[(i + 1)..].Split([' ', '\t'], 2)[0].Trim() : string.Empty;
                attrs.Add(i > 0 && v.Length > 0 ? key + "=" + v : key);
            }
        }

        return (first > 0 ? seg[0][..first] : seg[0]).Trim() + " {" + string.Join(" ", attrs) + "}";
    }

    /// <summary>CDP 的 blockedCookieLines 形如 <c>Set-Cookie: name=value; attrs: 原因</c>——名字与原因都要，值抹掉。</summary>
    private static string Blocked(string line)
    {
        string body = line.StartsWith("Set-Cookie:", StringComparison.OrdinalIgnoreCase)
            ? line["Set-Cookie:".Length..].Trim()
            : line;
        int reason = body.LastIndexOf(": ", StringComparison.Ordinal);
        string tail = reason > 0 ? Shorten(body[(reason + 2)..].Trim(), 120) : "—";
        int eq = body.IndexOf('=');
        string head = eq > 0 ? body[..eq] : body;
        int semi = head.IndexOf(';');
        return (semi > 0 ? head[..semi] : head).Trim() + " 原因=" + tail;
    }

    private void OnWsCreated(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            string url = Str(doc.RootElement, "url");
            _ws[Str(doc.RootElement, "requestId")] = (url, 0, string.Empty);
            Write("\n~~~ " + DateTime.Now.ToString("HH:mm:ss.fff") + " WS 建链 " + Shorten(url, 150));
        }
        catch (Exception ex)
        {
            Write("!! webSocketCreated 解析失败：" + ex.Message);
        }
    }

    /// <summary>每根连接只把**首帧**落原文，往后逐帧只计数——"是不是私有协议"看的就是首帧那一眼的形状。</summary>
    private void OnWsFrame(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement p = doc.RootElement;
            string id = Str(p, "requestId");
            if (!_ws.TryGetValue(id, out var e))
            {
                return;
            }

            _ws[id] = (e.Url, e.Frames + 1, e.First);
            if (e.Frames >= 1)
            {
                return;
            }

            JsonElement resp = p.TryGetProperty("response", out JsonElement r) && r.ValueKind == JsonValueKind.Object ? r : default;
            int op = resp.ValueKind == JsonValueKind.Object && resp.TryGetProperty("opcode", out JsonElement o)
                && o.TryGetInt32(out int ov) ? ov : -1;
            string data = Str(resp, "payloadData");
            string preview = Shorten(Printable(data), 220);
            _ws[id] = (e.Url, e.Frames + 1, preview);
            Write("\n~~~ " + DateTime.Now.ToString("HH:mm:ss.fff") + " WS 首帧 op=" + op + " len=" + data.Length
                + "（二进制帧这里给的是 base64） " + preview);
        }
        catch (Exception ex)
        {
            Write("!! webSocketFrameReceived 解析失败：" + ex.Message);
        }
    }

    private void OnWsClosed(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            string id = Str(doc.RootElement, "requestId");
            if (!_ws.TryGetValue(id, out var e))
            {
                return;
            }

            Write("\n~~~ " + DateTime.Now.ToString("HH:mm:ss.fff") + " WS 关闭 收 " + e.Frames + " 帧 " + Shorten(e.Url, 150));
        }
        catch (Exception ex)
        {
            Write("!! webSocketClosed 解析失败：" + ex.Message);
        }
    }

    private static string Printable(string s) =>
        new(s.Select(static c => c >= 32 && c < 127 ? c : '?').ToArray());

    /// <summary>导航这一跳（主帧/子帧）：会话落罐就挂在它身上，MIME 与 URL 都要留。</summary>
    private void OnFrameNavigated(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement fr = doc.RootElement.TryGetProperty("frame", out JsonElement f) && f.ValueKind == JsonValueKind.Object
                ? f
                : default;
            string url = Str(fr, "url");
            if (url.Length == 0 || url.StartsWith("about:", StringComparison.Ordinal))
            {
                return;
            }

            Write("\n>>> " + DateTime.Now.ToString("HH:mm:ss.fff") + " 导航 "
                + (Str(fr, "parentId").Length == 0 ? "主帧" : "子帧") + " [" + Str(fr, "mimeType") + "] "
                + Shorten(url, 140) + " loader=" + Str(fr, "loaderId"));
        }
        catch (Exception ex)
        {
            Write("!! frameNavigated 解析失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 罐子对账：douyin 域 cookie 的**名字**每变一次记一行，路标那几枚落罐打 ★。
    /// "会话到底随哪一跳落进来"以此为准，比从报文里猜可靠，且值一个不落。
    /// </summary>
    private async Task DiffJarAsync()
    {
        if (_jarBusy)
        {
            return;
        }

        _jarBusy = true;
        try
        {
            IReadOnlyList<CoreWebView2Cookie> list = await _view.CoreWebView2.CookieManager.GetCookiesAsync("https://www.douyin.com");
            HashSet<string> now = list.Select(static c => c.Name + "@" + c.Domain).ToHashSet();
            if (_jarNames.Count > 0 && !now.SetEquals(_jarNames))
            {
                string added = string.Join(",", now.Except(_jarNames));
                string gone = string.Join(",", _jarNames.Except(now));
                string stars = string.Join(",", added.Split(',').Where(static n => KeyCookies.Contains(n.Split('@')[0])));
                Write("\n### " + DateTime.Now.ToString("HH:mm:ss.fff") + " 罐子变了 +[" + added + "] -[" + gone
                    + "] 共 " + now.Count + " 枚" + (stars.Length > 0 ? "  ★ " + stars : string.Empty));
            }

            _jarNames = now;
        }
        catch (Exception ex)
        {
            Write("!! 罐子对账失败：" + ex.Message);
        }
        finally
        {
            _jarBusy = false;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>数字型字段（status 那一档）取原文。</summary>
    private static string Raw(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind != JsonValueKind.Null
            ? v.GetRawText()
            : string.Empty;

    private static string Shorten(string text, int max)
    {
        string one = text.ReplaceLineEndings(" ").Trim();
        return one.Length <= max ? one : one[..max] + "…";
    }

    /// <summary>落盘前打码：令牌/票据/手机号/二维码图不留原文。</summary>
    private static string Scrub(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, "\"(token|ticket|passportiv|mobile|qrcode)\":\"[^\"]*\"", "\"$1\":\"…\"");

    private void Note(string text) => _status.Text = text;

    private static void Write(string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(FolderPath);
                File.AppendAllText(LogPath, text + "\n", Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // 日志写不进去不该把探针弄挂
        }
    }
}

/// <summary>
/// 探针自己的 cookie 序列化上下文（源生成，无反射）。
/// 类型用 <see cref="SessionCookie"/> 而不是闭源侧那枚 <c>CookieSeed</c>：两者字段名与顺序完全一致
/// （Name/Value/Domain/Path/ExpiresUnix），而源生成上下文的成员名就是属性名，
/// 所以这个探针导出的文件与 <c>session.cookies.json</c> 同形，闭源侧照样吃得进来——
/// 界面侧因此一处都不必认识闭源类型。
/// </summary>
[JsonSerializable(typeof(SessionCookie[]))]
internal sealed partial class ProbeCookieJsonContext : JsonSerializerContext
{
}
