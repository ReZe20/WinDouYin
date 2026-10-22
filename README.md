# WinDouYin

Windows 上的第三方抖音桌面客户端，WinUI 3 / Windows App SDK 写的原生界面：推荐、精选、关注、朋友、搜索、用户主页、合集与收藏、评论与弹幕、私信（含引用跳转）、图集与实况帖、迷你播放器，以及按账号分账的本地使用时长热力图。

## 免责声明

- 本项目**不是**抖音（Douyin）或字节跳动的产品，与它们没有任何关系；名称、图标仅作指代用途。
- 仓库内容用于**个人学习与技术研究**。不得用于批量抓取、自动化刷量、数据分析转售或任何商业分发；由此产生的账号封禁、数据丢失等后果由使用者自负。
- 仓库**不携带任何官方素材**：`WinDouYin/Assets` 里那 7 张图标是 Visual Studio 模板的占位图，头像、封面、表情、图片与视频全部在运行时按登录态从服务端取。
- 登录在应用内自己完成（扫码），本项目不收集、不上传任何凭据；会话与账号材料只落在你本机的 `%LocalAppData%\WinDouYin`。

## 界面与取数的边界

这个仓是**开源那一半**：界面、视图模型、本地磁盘缓存与账本、平台适配，以及界面与取数层之间的契约。

```
WinDouYin.slnx
├─ WinDouYin/              应用本体（Views / Controls / ViewModels / Converters / Service 里的本地存储）
├─ WinDouYin.Contracts/    契约层：IDouyinFeed 门面 + 数据形状（裸 net10.0，不含任何 WinRT 类型）
├─ WinDouYin.Core/         两边共用的工具面：日志、JSON 字面量转义、使用时长账本
└─ WinDouYin.Douyin/       取数层的工程；本仓里它只有工程文件与一枚空实现桩
```

真正与抖音服务器对话的那半——报文解析与会话管理、多账号 cookie、`a_bogus` / `bd-ticket-guard` / `webSign` 三套签名——不在这里，源码在私有仓库 `WinDouYin-dev`。

`WinDouYin.Douyin` 在两个仓里是**同一份工程文件**，它按 `Native/NativeFeed.cs` 在不在盘上选编译哪一套源码：

| 档位 | 命中条件 | 编进去的东西 |
| --- | --- | --- |
| Real | 私有仓（闭源源码在场） | `Native/` `Signing/` `Service/` `FeedService.cs`，签名资产内嵌进程序集 |
| Stub | 本仓 clone 出来的样子 | 只有 `StubFeed.cs`：`IDouyinFeed` 的每个成员回空页或回失败 |

所以本仓**不依赖任何闭源源码就能编译**（`dotnet build` 会打印 `档位：Stub`），这也是这条边界唯一的机器验证：开源那半但凡偷偷用了闭源类型，这一发就编不过。Stub 档不是测试替身，也不承诺行为——应用起得来、页面全空态、写操作提示"这套构建没带取数实现"。想让界面真出数据，得把 `WinDouYin-dev` 里那四个目录与 `FeedService.cs` 放进 `WinDouYin.Douyin/`，档位自己翻成 Real。

接缝只有两行，两边都只认契约：

```csharp
DouyinBoot.Attach();                       // 把取数门面挂到 Contracts 的 Douyin 定位器上
await Douyin.Feed.RestoreSessionAsync();   // 之后全工程只认 Douyin.Feed
```

`WinDouYin.Contracts` 是本仓的权威定义：界面代码从头到尾只认这套开放类型，编译期碰不到任何报文形状。想自己接一个取数实现，实现 `IDouyinFeed` 再 `Douyin.Attach(...)` 即可，界面一行不用改。

## 构建与运行

环境：Windows 10 17763 及以上（界面按 WinUI 3 设计语言，建议 Win11），.NET 10 SDK，Visual Studio 2022/2026 的「Windows 应用程序开发」负载 + Windows App SDK 2.5.1 运行时。

```powershell
dotnet build WinDouYin.slnx -p:Platform=x64
```

在 VS 里直接 F5 也可以。工程默认 `PublishAot=true`：发布产物是原生镜像，非打包应用（`WindowsPackageType=None`）。要出调试符号包用 `-p:AotSymbols=true`，要对照体积先关 AOT 用 `-p:PublishAot=false`。

> 改过 XAML 之后请 **Rebuild** 而不是增量 Build：`obj/` 里遗留的 `.g.cs` 会报出假的 CS1061。

## 已知限制

- 本仓 clone 下来是 **Stub 档**：能编译、能启动，但所有列表是空的、写操作一律失败——取数实现不在这里（见上）。
- 播放依赖 Media Foundation，走服务端返回的直链；某些清晰度档位需要登录后才给。
- 使用时长账本按账号分本、按天加密存放，**不跨机器同步、不回填历史**。
- 私信与写操作（点赞、收藏、关注）是逆向出来的非公开接口，随时可能失效或触发风控。
- Uno Platform 移植（Linux / macOS）在进行中：契约层与共享工具层已经刻意留在裸 `net10.0`，平台适配还没铺完。

## 许可

MIT，见 [LICENSE](LICENSE)。
