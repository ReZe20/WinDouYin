using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using WinDouYin.Contracts;

namespace WinDouYin.Controls;

/// <summary>
/// 官方登录框宿主。为什么非得借一次浏览器内核：扫码确认之后，服务端只把会话交给过了 ticket_guard
/// 设备证明的那一侧——纯协议实测连"要你去做身份验证"这个挑战都收不到（照抄浏览器的 sign/qs/p_no/
/// account_sdk_source_info/风控 cookie/HTTP2 全部无效，确认那一刻只回 error_code 7）。
/// 所以这里让抖音自己的页面出码、扫码、过验证，完成后把它罐子里那枚 httpOnly 的 sessionid 读出来，
/// 灌进我们自己的原生会话并落盘。取数链路仍然是纯 C#，WebView2 只活在这一个框里。
/// </summary>
public sealed partial class LoginWebView : UserControl
{
    private const string LoginUrl = "https://www.douyin.com/";

    private static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// 只留登录框。三步：
    /// ① 从"扫码登录"往上找最外面那层"白底、够大"的容器——那就是卡片本体，官方尺寸我们不猜；
    /// ② 逐层把不带它的兄弟 display:none（弹窗渲染在 #root 里面，只藏 body 子节点等于没藏）；
    /// ③ 祖先一律透明（半透明遮罩就是这么把灰底带进来的），再把卡片摆到左上角、**按量出来的尺寸缩放**到刚好塞满视口并居中。
    /// 缩放读 offsetWidth/offsetHeight 而不是 getBoundingClientRect：后者会把上一拍自己设的 scale 算进去，
    /// 每拍再乘一次就一路缩没了。
    /// 类名是混淆的、随时会变，所以只按文案找元素，不依赖 class。
    ///
    /// 三条保命的（2026-10-12 晚实测加）：**藏兄弟只在"换了新卡片"那一拍做**——每拍重藏会把抖音随后的
    /// 验证页当兄弟一起吃掉；**一旦认出"身份验证"页（"请使用已登录账号的设备扫码"那套文案）就全部让路**；
    /// **让过路之后本页彻底放手（handoff）**——它的验证子系统还有短信窗、输码页好几屏，再插手就再吃一次
    /// （那次"接受短信验证码的窗口弹一下就回去"就是这么丢的）。面板重开会重载页面，标记清零、重新接管。
    /// </summary>
    private const string Isolate = """
        (function () {
          try {
            var all = document.querySelectorAll('div,span,p,button,a,label,li,section');

            // 一次遍历把候选文案全收了：innerText 是昂贵读取，不为每段文案各扫一遍整页。
            var qrHits = [], smsHits = [], verifyHits = [];
            var kScan = 0, kCode = 0;
            for (var i0 = 0; i0 < all.length; i0++) {
              var t0 = (all[i0].innerText || '').trim();
              if (t0.indexOf('扫码') !== -1) { kScan++; }
              if (t0.indexOf('验证码') !== -1) { kCode++; }
              if (t0 === '扫码登录') { qrHits.push(all[i0]); }
              else if (t0 === '验证码登录' || t0 === '短信登录' || t0 === '密码登录') { smsHits.push(all[i0]); }
              else if (t0 === '请使用已登录账号的设备扫码' || t0.indexOf('以确保为本人操作') !== -1 || t0.indexOf('请使用「抖音 APP」扫码验证') !== -1) { verifyHits.push(all[i0]); }
            }

            // 把现场放回去：解掉我们藏过的兄弟节点、摘掉卡片上的固定姿势、还原祖先与 body 的底色。
            // 抖音自己的后续步骤（"身份验证"）必须原样可见、可点——我们只管登录卡片的观感。
            function restore() {
              try {
                var hid = window.__wyHidden;
                if (hid) {
                  for (var h = 0; h < hid.length; h++) { try { hid[h].style.removeProperty('display'); } catch (e) { } }
                }
                window.__wyHidden = [];
                var st = window.__wyStyled;
                if (st) {
                  var props = ['position', 'left', 'top', 'margin', 'transform-origin', 'transform', 'background', 'z-index'];
                  for (var j = 0; j < props.length; j++) { try { st.style.removeProperty(props[j]); } catch (e) { } }
                  window.__wyStyled = null;
                }
                var chain = window.__wyUpchain;
                if (chain) {
                  for (var c = 0; c < chain.length; c++) {
                    try { chain[c].style.removeProperty('background'); chain[c].style.removeProperty('backdrop-filter'); } catch (e) { }
                  }
                  window.__wyUpchain = null;
                }
                if (window.__wyDocEl) { window.__wyDocEl.style.removeProperty('background'); window.__wyDocEl = null; }
                if (window.__wyBodyEl) { window.__wyBodyEl.style.removeProperty('background'); window.__wyBodyEl.style.removeProperty('overflow'); window.__wyBodyEl = null; }
                window.__wyCardNode = null;
                window.__wyHidCount = 0;
              } catch (e) { }
            }

            // 本页一旦让过路就彻底放手：抖音的验证子系统（身份验证、短信窗、输码页）还有好几屏，
            // 我们再插一次手就可能又把它吃掉（2026-10-12 晚："接受短信验证码"的窗口弹一下就回去，就是这么丢的）。
            // 面板重新打开会重载页面，window 上的这些标记随之清零，届时重新开始接管。
            if (window.__wyHandedOff) {
              return 'pass@' + location.pathname;
            }

            // "身份验证"页先于一切：它一出现就全线让路（还原现场），并从这一刻起彻底放手。
            // 那是抖音官方的二次验证步骤（ato_web 模板），之后的每一步都归它演。
            if (verifyHits.length) {
              restore();
              window.__wyHandedOff = true;
              return 'verify@' + location.pathname + ' n=' + verifyHits.length;
            }

            // 同一段文字往往嵌好几层（祖先的 innerText 也等于它），只留最深的那层。
            function deepest(hits) {
              var deep = [];
              for (var i = 0; i < hits.length; i++) {
                var covered = false;
                for (var j = 0; j < hits.length; j++) {
                  if (hits[j] !== hits[i] && hits[i].contains(hits[j])) { covered = true; break; }
                }
                if (!covered) { deep.push(hits[i]); }
              }
              return deep;
            }

            var qr = deepest(qrHits);
            var sms = deepest(smsHits);

            // 选哪枚"扫码登录"：优先跟"验证码登录"这一对（官方弹窗的两个 tab 挨着）；
            // 站内嵌的登录提示卡只有"扫码登录"一处文案，配不成对，正好被排除。
            // 一枚都配不成对时退回旧行为（文档序最后一枚）。
            var leaf = null;
            var pick = 'none';
            if (qr.length) {
              var best = null, bestUp = 999, bestIdx = -1;
              for (var i = 0; i < qr.length; i++) {
                var up0 = qr[i].parentElement, lv = 1, found = false;
                while (up0 && up0 !== document.body && lv < 40) {
                  for (var j = 0; j < sms.length; j++) {
                    if (up0.contains(sms[j])) { found = true; break; }
                  }
                  if (found) { break; }
                  up0 = up0.parentElement;
                  lv++;
                }
                if (found && lv < bestUp) { best = qr[i]; bestUp = lv; bestIdx = i; }
              }
              if (best) { leaf = best; pick = bestIdx + '/' + qr.length + '@' + bestUp; }
              else {
                // 配不成对时退而求其次：挑"有 position:fixed 祖先"的那枚——弹窗遮罩是 fixed 的，站内卡片不是。
                for (var i2 = qr.length - 1; i2 >= 0; i2--) {
                  var up1 = qr[i2].parentElement, fixedHit = false;
                  while (up1 && up1 !== document.body) {
                    if (getComputedStyle(up1).position === 'fixed') { fixedHit = true; break; }
                    up1 = up1.parentElement;
                  }
                  if (fixedHit) { leaf = qr[i2]; pick = 'fixed' + i2 + '/' + qr.length; break; }
                }
                if (!leaf) { leaf = qr[qr.length - 1]; pick = 'last/' + qr.length; }
              }
            }
            if (!leaf) {
              // 弹窗不在：先把现场放回去（我们藏过东西的话）。
              var hadIso = !!(window.__wyHidden && window.__wyHidden.length) || !!window.__wyStyled || !!window.__wyUpchain;
              restore();
              if (hadIso) {
                // 裁出过的卡片退场了（转运验证、登录完成、或站方自己换页）：从这一刻起本页彻底放手，
                // 后面每一屏（短信窗、输码页…）都必须原样出现——不再点、不再藏。
                window.__wyHandedOff = true;
                return 'pass@' + location.pathname;
              }

              // 还没接管过：负责把"登录"点出来，下一拍再裁（每 5 秒最多点一次）。
              // 点的那枚挑"看得见、最靠右"的：真按钮在右上角；藏着的、页脚的死文案不算数。
              var clickN = 0, bestLogin = null, bestX = -1, clickAt = '';
              for (var j = 0; j < all.length; j++) {
                var b = all[j];
                if (b.childElementCount === 0 && (b.innerText || '').trim() === '登录') {
                  var rl = b.getBoundingClientRect();
                  if (rl.width >= 2 && rl.height >= 2) {
                    clickN++;
                    if (rl.left > bestX) { bestX = rl.left; bestLogin = b; clickAt = Math.round(rl.left) + ',' + Math.round(rl.top); }
                  }
                }
              }

              var clicked = 'none';
              if (bestLogin && (!window.__wyLoginClickAt || Date.now() - window.__wyLoginClickAt > 5000)) {
                bestLogin.click();
                window.__wyLoginClickAt = Date.now();
                clicked = 'yes';
              } else if (bestLogin) {
                clicked = 'cool';
              }
              var ifrList = [];
              var ifrAll = document.getElementsByTagName('iframe');
              for (var f0 = 0; f0 < ifrAll.length && f0 < 4; f0++) {
                ifrList.push(String(ifrAll[f0].src || '').replace(/^https?:\/\//, '').split('/')[0]);
              }
              return 'no-modal@' + location.pathname + ' click=' + clicked + '(' + clickN + (clickAt ? '@' + clickAt : '') + ')' + ' qr=' + qr.length + ' sms=' + sms.length + ' scan=' + kScan + ' code=' + kCode + ' ifr=' + ifrAll.length + '[' + ifrList.join(' ') + ']' + ' t=' + String(document.title || '').slice(0, 24);
            }

            // 往上找卡片。旧的"白底、够大"判据在暗色主题下整页都不白、全落空，所以加一道闸：
            // 碰到第一个 position:fixed 的壳（弹窗遮罩一般就是它）为止，壳以上的整页容器不当卡片。
            var card = null;
            var wide = null;
            var wideBelow = null;
            var capped = false;
            var probe = leaf;
            while (probe && probe !== document.body) {
              var s = getComputedStyle(probe);
              // 尺寸一律读 offsetWidth/offsetHeight：getBoundingClientRect 会把上一拍自己设的 scale 算进去，
              // 每拍再缩一次就一路缩没了
              if (probe.offsetWidth >= 300 && probe.offsetHeight >= 300) {
                wide = probe;
                if (!capped) { wideBelow = probe; }
                if (s.backgroundColor === 'rgb(255, 255, 255)') { card = probe; }
              }
              if (s.position === 'fixed') { capped = true; }
              probe = probe.parentElement;
            }
            // 白底找不到（暗色主题）就用"壳以内最外层够大的"那层；都没有再退到旧口径的"够大"或叶子本身
            if (!card) { card = wideBelow || wide || leaf; }
            var cardBg = getComputedStyle(card).backgroundColor;
            var rect0 = card.getBoundingClientRect();

            // 藏兄弟只在"换了新卡片"时做一遍（一次性）：每拍重藏会把站方随后挂上来的
            // "身份验证"页当兄弟一起藏掉——2026-10-12 晚那次"验证页闪一下就没"就是这么丢的。
            var hidden = 0;
            if (window.__wyCardNode !== card) {
              window.__wyCardNode = card;
              var node = card;
              while (node && node !== document.body && node.parentElement) {
                var kids = node.parentElement.children;
                for (var k = 0; k < kids.length; k++) {
                  if (kids[k] !== node && kids[k].style.display !== 'none') {
                    kids[k].style.setProperty('display', 'none', 'important');
                    (window.__wyHidden = window.__wyHidden || []).push(kids[k]);
                    hidden++;
                  }
                }
                node = node.parentElement;
              }
              window.__wyHidCount = (window.__wyHidCount || 0) + hidden;
            }

            var chain = [];
            var up2 = card.parentElement;
            while (up2 && up2 !== document.body) {
              up2.style.setProperty('background', 'transparent', 'important');
              up2.style.setProperty('backdrop-filter', 'none', 'important');
              chain.push(up2);
              up2 = up2.parentElement;
            }
            window.__wyUpchain = chain;
            window.__wyDocEl = document.documentElement;
            window.__wyBodyEl = document.body;
            document.documentElement.style.setProperty('background', '#fff', 'important');
            document.body.style.setProperty('background', '#fff', 'important');
            document.body.style.setProperty('overflow', 'hidden', 'important');

            // 基准尺寸只量一次（记在 window 上）：position:fixed 会改掉流内尺寸，每拍重量的 w/h 会被上一拍
            // 自己改变，@k 就逐拍漂。窗口缩放只影响算出来的 k，不影响基准尺寸。
            var size = window.__wyCardSize;
            if (!size) {
              var bw = card.offsetWidth;
              var bh = card.offsetHeight;
              if (bw < 50 || bh < 50) { return 'card-small ' + bw + 'x' + bh + ' pick=' + pick; }
              size = { w: bw, h: bh };
              window.__wyCardSize = size;
            }
            var w = size.w;
            var h = size.h;
            var k = Math.min(window.innerWidth / w, window.innerHeight / h);
            card.style.setProperty('position', 'fixed', 'important');
            card.style.setProperty('left', ((window.innerWidth - w * k) / 2) + 'px', 'important');
            card.style.setProperty('top', ((window.innerHeight - h * k) / 2) + 'px', 'important');
            card.style.setProperty('margin', '0', 'important');
            card.style.setProperty('transform-origin', '0 0', 'important');
            card.style.setProperty('transform', 'scale(' + k + ')', 'important');
            card.style.setProperty('background', /^rgb\(/.test(cardBg) ? cardBg : '#fff', 'important');
            card.style.setProperty('z-index', '2147483647', 'important');
            window.__wyStyled = card;

            // 把卡片头几层子元素写进日志：没有界面可看时，日志就是"眼睛"，一眼看出卡片里装的是什么。
            var kidL = [];
            for (var c1 = 0; c1 < card.children.length && c1 < 5; c1++) {
              var ck = card.children[c1];
              kidL.push(ck.tagName + '.' + (ck.className || '').toString().split(' ')[0].slice(0, 14) + ' ' + ck.offsetWidth + 'x' + ck.offsetHeight + ' t=' + (ck.innerText || '').trim().slice(0, 10));
            }
            return 'isolated ' + w + 'x' + h + '@' + k.toFixed(2) + ' vw=' + window.innerWidth + ' vh=' + window.innerHeight + ' hid=' + (window.__wyHidCount || 0) + ' bg=' + cardBg + ' r=' + Math.round(rect0.left) + ',' + Math.round(rect0.top) + ' ' + card.tagName + '.' + (card.className || '').toString().slice(0, 24) + ' pick=' + pick + ' sms=' + sms.length + ' ct=' + (card.innerText || '').trim().slice(0, 24) + ' kid=[' + kidL.join(' / ') + ']';
          } catch (err) {
            return 'err:' + err.message;
          }
        })();
        """;

    /// <summary>
    /// 页面内取证钩子：只在文档创建时装一次（AddScriptToExecuteOnDocumentCreated），
    /// 把 check_qrconnect / get_qrcode 的应答原文记进 window.__wyLog。
    /// 走 JS 而不是 WebResourceResponseReceived.GetContentAsync——这版 WinRT 元数据没投影那个 API（编译实测）。
    /// </summary>
    private const string PollHook = """
        (function () {
          try {
            if (window.__wyHooked) { return; }
            window.__wyHooked = true;
            window.__wyLog = [];
            var KEYS = ['check_qrconnect', 'get_qrcode', '/passport/'];
            function hit(u, s, b) {
              try {
                u = String(u || ''); s = String(s || '');
                var ok = false;
                for (var i = 0; i < KEYS.length; i++) { if (u.indexOf(KEYS[i]) >= 0) { ok = true; } }
                if (!ok) { return; }
                if (window.__wyLog.length > 80) { return; }
                window.__wyLog.push(s + ' ' + u.slice(0, 110) + ' :: ' + String(b || '').replace(/[\s"\\]+/g, ' ').slice(0, 220));
              } catch (e) { }
            }
            if (window.fetch) {
              var of = window.fetch;
              window.fetch = function (input, init) {
                var url = (typeof input === 'string') ? input : (input && input.url);
                return of.apply(this, arguments).then(function (res) {
                  try { res.clone().text().then(function (t) { hit(url, res.status, t); }); } catch (e) { }
                  return res;
                });
              };
            }
            var osend = XMLHttpRequest.prototype.send;
            XMLHttpRequest.prototype.send = function () {
              try {
                var xhr = this;
                xhr.addEventListener('load', function () {
                  try { hit(xhr.responseURL || '', xhr.status, xhr.responseText || ''); } catch (e) { }
                });
              } catch (e) { }
              return osend.apply(this, arguments);
            };
          } catch (e) { }
        })();
        """;

    /// <summary>把攒下的应答取走并清空（每拍一次），日志里一行一条轮询证词。</summary>
    private const string DrainPolls = """
        (function () {
          if (!window.__wyLog || !window.__wyLog.length) { return ''; }
          var a = window.__wyLog.slice(0);
          window.__wyLog = [];
          var s = 'poll[' + a.length + '] ' + a.join(' || ');
          return s.length > 1500 ? s.slice(0, 1500) : s;
        })();
        """;

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private bool _watching;

    /// <summary>摸到 sessionid 就触发，带出来的是官方罐子里那一整组 cookie。</summary>
    public event Action<IReadOnlyList<SessionCookie>>? SessionLanded;

    /// <summary>宿主面板用它把 WebView2 的失败原因显示成人话。</summary>
    public event Action<string>? Failed;

    /// <summary>裁剪脚本每跑一次会回一句状态，只在变了的时候往上抛——不然一秒刷两行没法看。</summary>
    public event Action<string>? StatusChanged;

    private string _lastNote = string.Empty;

    private bool _revealed;
    private bool _telemetryWired;
    private bool _hookInstalled;
    private string _navUri = string.Empty;
    private DateTime _coverDeadline;
    private int _noModalTicks;
    private int _ticks;
    private HashSet<string> _knownCookies = new();

    /// <summary>抖音登录卡片的自然尺寸（日志实测）；裁剪脚本对别的尺寸也能兜底，但面板比例按它走。</summary>
    private const double PanelWidth = 726;

    private const double PanelHeight = 483;

    /// <summary>
    /// 宿主给的闸：Loaded 再响的时候要不要顺手启动登录流程。
    /// 面板每次打开都会让这个控件重新 Loaded，但"已经登录了"的时候绝不能再启动——
    /// 启动会清登录框的 cookie 再导航，等于把刚登好的号在眼前变回一张二维码。
    /// </summary>
    public Func<bool>? ShouldAutoStart { get; set; }

    public LoginWebView()
    {
        InitializeComponent();
        FitTo(PanelWidth, PanelHeight);
        _tick.Tick += async (_, _) => await WatchOnceAsync();
        Loaded += (_, _) =>
        {
            if (ShouldAutoStart?.Invoke() ?? true)
            {
                _ = StartAsync();
            }
        };
        Unloaded += (_, _) => Stop();
    }

    /// <summary>
    /// 按登录卡片比例（726:483）把舞台放进宿主编的预算里；封顶 1:1，别把卡片放大糊掉。
    /// 舞台与卡片同比例 → 内置裁剪脚本算出来的缩放是均匀的，卡片正好铺满，不再有"横卡塞竖盒"的大白边。
    /// </summary>
    public void FitTo(double maxW, double maxH)
    {
        double s = Math.Min(1.0, Math.Min(maxW / PanelWidth, maxH / PanelHeight));
        if (s < 0.4)
        {
            s = 0.4;
        }
        Stage.Width = Math.Round(PanelWidth * s);
        Stage.Height = Math.Round(PanelHeight * s);
    }

    public void Restart() => _ = StartAsync();

    public void Stop()
    {
        _tick.Stop();
        _watching = false;
    }

    private async Task StartAsync()
    {
        if (_watching)
        {
            return;
        }

        _watching = true;
        _revealed = false;
        _coverDeadline = DateTime.UtcNow.AddSeconds(20);
        Visibility = Visibility.Visible;
        Cover.Visibility = Visibility.Visible;
        Spin.IsActive = true;
        Hint.Text = "正在打开官方登录框";

        try
        {
            // 松散（未打包）应用不能把用户数据目录默认放在 exe 旁边。这里走 WebView2 支持的环境变量，
            // 因为工程绑的是 WinRT 元数据那份 CoreWebView2Environment，CreateAsync 没有可选参数那一档重载。
            // 目录名必须专属于登录框：共用别的 profile 会把那边登着的号一起举回来（2026-10-12 撞过）。
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDouYin", "webview2-login");
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", dir);

            await InitCoreAsync();

            CoreWebView2 core = Web.CoreWebView2;

            // 不覆盖 UA：WebView2 原生就是本机 Edge 的 UA 串，跟 Sec-CH-UA 客户端提示头一致；
            // 之前覆盖的那串（Edg/154.0.0.0）跟提示头对不上——风控正好拿这种不一致当异常。
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;

            // 每次打开重开计数与罐子快照；监听线只挂一次（Restart 反复进这里，重复订阅会打重影）
            _ticks = 0;
            _knownCookies.Clear();
            WireTelemetry(core);

            // 页面内取证钩子：每次新建文档（含跳转后的新文档）都会先跑它，把 passport 应答原文记下来
            if (!_hookInstalled)
            {
                _hookInstalled = true;
                await core.AddScriptToExecuteOnDocumentCreatedAsync(PollHook);
            }

            // 每次开都从匿名起步：内核自己的 profile 里留着上一回的 cookie，不清就会把那个号直接举回来，
            // 人看到的就不是登录框、而是"已经登好了"，且登的是谁完全由上一回决定。
            // 我们这个框只负责"登一次"，登录态的正主是原生罐子 + 多账号档；
            // 要换号走"退出登录"，要再加一个号走"添加账号"，别让 profile 替用户决定登的是谁。
            // 清罐子用 DeleteAllCookies：这个 profile 只归登录框；拿 GetCookiesAsync 的结果逐个 DeleteCookie
            // 会因罐里某一枚 cookie 的属性被判非法、在第一个起就同步抛 E_INVALIDARG（2026-10-12 17:23 的
            // webview.log 连抛十次，登录框从此每次"起不来"）。清失败就继续走——最多显示上一回的登录态。
            try
            {
                core.CookieManager.DeleteAllCookies();
                WebLog.Write("已清空登录框 profile 的 cookie");
            }
            catch (Exception ex)
            {
                WebLog.Write("清 cookie 失败（继续）：" + ex.Message);
            }

            core.Navigate(LoginUrl);
            WebLog.Write("已导航 " + LoginUrl);
            _tick.Start();
        }
        catch (Exception ex)
        {
            _watching = false;
            Spin.IsActive = false;
            Hint.Text = "登录框起不来：" + ex.Message;
            WebLog.Write("起不来：" + ex);
            Failed?.Invoke(ex.Message);
        }
    }

    /// <summary>
    /// 建内核。两件事都在这儿兜：① 弹层里第一次初始化偶发 E_INVALIDARG（"参数错误。"，内核要在有真实尺寸的
    /// 视觉树里建），所以失败退一步重试；② 内核起不来时 EnsureCoreWebView2Async 是不完成、也不抛的，
    /// 没有超时面板上就只剩一个永远转的圈。它给的是 WinRT 的 IAsyncAction，不当 Task 用就当不出超时。
    /// </summary>
    private async Task InitCoreAsync()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Task init = Web.EnsureCoreWebView2Async().AsTask();
                if (await Task.WhenAny(init, Task.Delay(InitTimeout)) != init)
                {
                    throw new TimeoutException("WebView2 内核 " + (int)InitTimeout.TotalSeconds + " 秒没起来");
                }

                await init;
                WebLog.Write($"内核就绪（第 {attempt} 次尝试）");
                return;
            }
            catch (Exception ex) when (attempt < 3)
            {
                WebLog.Write($"内核第 {attempt} 次初始化失败：{ex.Message}");
                await Task.Delay(400);
            }
        }
    }

    /// <summary>
    /// 监听线：这个框里的每一步都在过风控，出了岔子要能从日志倒推"服务器说了什么"。
    /// 只挂一次（Restart 会反复进 StartAsync，重复订阅会把日志打重影）。
    /// </summary>
    private void WireTelemetry(CoreWebView2 core)
    {
        if (_telemetryWired)
        {
            return;
        }

        _telemetryWired = true;

        core.NavigationStarting += (_, e) =>
        {
            _navUri = e.Uri;
            WebLog.Write("导航开始 " + Shorten(e.Uri));
        };
        // 这版 WinRT 元数据的 NavigationCompleted 参数里没有 Uri（编译实测），用上一跳记下的
        core.NavigationCompleted += (_, e) => WebLog.Write(
            (e.IsSuccess ? "导航完成 " : "导航失败 " + e.WebErrorStatus + " ") + Shorten(_navUri));
        core.NewWindowRequested += (_, e) => WebLog.Write("请求开新窗 " + Shorten(e.Uri));
        core.ProcessFailed += (_, e) => WebLog.Write("内核异常 " + e.ProcessFailedKind);
    }

    private static string Shorten(string uri) => uri.Length > 96 ? uri[..96] : uri;

    private async Task WatchOnceAsync()
    {
        if (!_watching)
        {
            return;   // Stop()（登录落地/面板收起）之后仍在途的一拍不再往下走——抓材料那几秒尤其要紧
        }

        CoreWebView2? core = Web.CoreWebView2;
        if (core is null)
        {
            return;
        }

        try
        {
            string raw = await core.ExecuteScriptAsync(Isolate);
            string note = raw.Trim('"');
            if (note != _lastNote)
            {
                _lastNote = note;
                WebLog.Write("裁剪：" + note);
                StatusChanged?.Invoke(note);

                // 等待层只在"还没见过登录框"的阶段顶着；第一次裁出来后就不再回盖——
                // 扫码确认后的收尾（弹窗消失、页面跳转）得让人看得见，别自己闪来闪去遮住现场。
                if (note.StartsWith("isolated", StringComparison.Ordinal))
                {
                    _revealed = true;
                }
            }

            // 等不到登录框（风控冷却、页面改版、弹窗换了面孔……）就把等待层掀开：
            // 一直盖着的话，用户看到的是一个永远转圈的白盒子——看起来就是"页面加载不出来"。
            if (!_revealed && DateTime.UtcNow >= _coverDeadline)
            {
                _revealed = true;
                WebLog.Write("等待超时，掀开等待层让页面自己说话");
            }

            // 页面上连"扫码/验证码"的踪影都没有、还一直是 no-modal：30 拍（约 36 秒）重载一次登录页。
            // 有踪影或已经在别的状态就不动——别把人家已经弹出来的东西重载掉。
            if (note.StartsWith("no-modal", StringComparison.Ordinal)
                && note.Contains(" scan=0 ", StringComparison.Ordinal)
                && note.Contains(" code=0 ", StringComparison.Ordinal))
            {
                if (++_noModalTicks >= 30)
                {
                    _noModalTicks = 0;
                    WebLog.Write("页面上连登录的踪影都没有，重载登录页再试");
                    core.Navigate(LoginUrl);
                }
            }
            else
            {
                _noModalTicks = 0;
            }
        }
        catch
        {
            // 导航中途脚本会失败，下一拍再来
        }

        Cover.Visibility = _revealed ? Visibility.Collapsed : Visibility.Visible;
        Spin.IsActive = !_revealed;

        // 取走页面钩子攒下的 passport 应答（get_qrcode / check_qrconnect 的原文）
        try
        {
            string polls = (await core.ExecuteScriptAsync(DrainPolls)).Trim('"');
            if (polls.Length > 1)
            {
                WebLog.Write(polls);
            }
        }
        catch
        {
            // 导航中途取不到，下一拍再来
        }

        IReadOnlyList<SessionCookie> seeds = await ReadCookiesAsync(core);

        // 罐子每有变化都留痕：sessionid 这类关键 cookie 一到，日志第一时间见分晓
        var now = new HashSet<string>(seeds.Select(static s => s.Name + "@" + s.Domain));
        if (_knownCookies.Count > 0 && !now.SetEquals(_knownCookies))
        {
            string added = string.Join(",", now.Except(_knownCookies).Take(8));
            string gone = string.Join(",", _knownCookies.Except(now).Take(8));
            WebLog.Write("罐子变了：+[" + added + "] -[" + gone + "] 共 " + seeds.Count + " 枚");
        }
        _knownCookies = now;

        if (seeds.Any(static s => s.Name == "sessionid" && s.Value.Length > 0))
        {
            WebLog.Write("拿到 sessionid，交出去（共 " + seeds.Count + " 枚 cookie）");
            Stop();
            Cover.Visibility = Visibility.Collapsed;

            // 交会话**之前**先把 guard 材料捞回来：写接口（发弹幕）要用页面那把真钥匙，
            // 而这个页面马上就不再是"活页面"了（面板一收，谁也读不到 localStorage 里那份）。见 CaptureGuardAsync。
            await CaptureGuardAsync(core);

            SessionLanded?.Invoke(seeds);
        }

        // 心跳：每 25 拍（约 30 秒）写一行，证明轮询还活着——日志静默时先排除"轮询已死"
        if (++_ticks % 25 == 0)
        {
            string tail = _lastNote.Length > 36 ? _lastNote[..36] : _lastNote;
            WebLog.Write("轮询存活 #" + _ticks + " 拍（" + tail + "）");
        }
    }

    /// <summary>
    /// 登录落地这一拍，从活页面把 bd-ticket-guard 的材料捞回来：私钥/公钥
    /// （localStorage 的 security-sdk/s_sdk_crypt_sdk）、票据四件套（s_sdk_sign_data_key/web_protect）、
    /// cert_key。提取脚本同步先读 localStorage、IndexedDB/OPFS 异步兜底，结果暂存在页面的
    /// window.__wyGuard，这里轮询取回（最多 8 秒；异步那半边通常秒级跑完）。
    ///
    /// 为什么要捞：写接口（发弹幕/删弹幕）服务端只认**它绑给这个浏览器的那把钥匙**，
    /// 自产钥匙签名会被丢成 200 空包（2026-10-15 判定实验钉死）。这几样必须在登录这一刻
    /// 从页面里带走——页面一关，就只剩 profile 目录里的 leveldb 副本，掏不出干净的了。
    /// </summary>
    private static async Task CaptureGuardAsync(CoreWebView2 core)
    {
        string json = string.Empty;
        try
        {
            string kick = Douyin.Feed.DecodeGuardScript(await core.ExecuteScriptAsync(Douyin.Feed.GuardExtractScript));
            WebLog.Write("guard 材料：提取脚本已注入（" + Shorten(kick) + "）");

            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(800);
                string drain = Douyin.Feed.DecodeGuardScript(await core.ExecuteScriptAsync(Douyin.Feed.GuardDrainScript));
                if (drain.Length > 2)
                {
                    json = drain;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            WebLog.Write("guard 材料：提取出错（" + ex.Message + "）");
        }

        if (json.Length == 0)
        {
            WebLog.Write("guard 材料：没抓到（页面存储里没有 / 脚本没跑完）——写接口继续用自产钥匙");
            return;
        }

        if (!Douyin.Feed.AdoptGuardMaterials(json))
        {
            WebLog.Write("guard 材料：解析不到私钥（" + Douyin.Feed.GuardSummary + "）");
            return;
        }

        WebLog.Write("guard 材料已取回：" + Douyin.Feed.GuardSummary
            + " · 钥匙指纹 " + Douyin.Feed.GuardKeyFingerprint);
    }

    /// <summary>
    /// sessionid 是 httpOnly，JS 读不到，但 CookieManager 能——这正是"只借它过一次验证"
    /// 之后唯一要拿的东西。域名/路径要一起带走，回填时才不会把会话还原成半个。
    /// </summary>
    private static async Task<IReadOnlyList<SessionCookie>> ReadCookiesAsync(CoreWebView2 core)
    {
        var list = await core.CookieManager.GetCookiesAsync(LoginUrl);
        var seeds = new List<SessionCookie>(list.Count);
        foreach (CoreWebView2Cookie c in list)
        {
            // Expires 是 double（JS 那套"距 1970 的秒数"），不是 DateTime；会话级的那枚给的是负数或 NaN。
            long exp = c.Expires > 0 && c.Expires < 4_102_444_800 ? (long)c.Expires : 0;
            seeds.Add(new SessionCookie(c.Name, c.Value, c.Domain, c.Path.Length == 0 ? "/" : c.Path, exp));
        }

        return seeds;
    }
}

/// <summary>
/// 登录框自己的日志，落在 %LocalAppData%\WinDouYin\webview.log（每次进程第一行时截断）。
/// 裁剪这类事只在界面里说一句话，出了偏差要看的是"到底裁到了哪个元素、多大、缩放多少"，
/// 所以状态话也照抄一份到这儿。口径与其余日志一致：常驻 writer，别逐条开关文件。
/// </summary>
internal static class WebLog
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinDouYin", "webview.log");

    private static readonly object Gate = new();
    private static StreamWriter? _writer;

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                if (_writer is null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    _writer = new StreamWriter(FilePath, append: false) { AutoFlush = true };
                    _writer.WriteLine("=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
                }

                _writer.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line);
            }
        }
        catch
        {
            // 日志写不进去不能影响登录
        }
    }
}
