# Steam 创意工坊匿名下载验证 · 第一阶段

这是独立的 .NET 9 下载库、命令行和隔离测试，不接入 WPF 管理器，不安装 MOD、不启动下载内容、不操作游戏和存档。仅接受游戏 `2868840` 的公开条目。

**2026-11-04 的真实验证：公开元数据和匿名 CM 登录成功；三个条目的 Manifest 授权均返回 `AccessDenied (15)`，实际 MOD 文件为 0。不能据此宣称已实现该游戏的可用匿名下载。** 详细记录见 [第一阶段验证报告](第一阶段验证报告.md) 和 `evidence/2026-11-04/`。

## 运行

要求 Windows、.NET SDK 9.0；库本身为 `net9.0`，命令行不依赖 WPF。NuGet 包缓存位于此工作树 `.packages`，与现有管理器无项目引用关系。

在仓库根目录运行：

```powershell
dotnet restore workshop/SpirePatch.Workshop.Cli --locked-mode
dotnet build workshop/SpirePatch.Workshop.Cli -c Release --no-restore
$cli = 'workshop/SpirePatch.Workshop.Cli/bin/Release/net9.0/SpirePatch.Workshop.Cli.dll'
dotnet $cli init workshop/test-output/manual
dotnet $cli metadata 'https://steamcommunity.com/sharedfiles/filedetails/?id=3830179251' --sandbox workshop/test-output/manual
dotnet $cli download 3830179251 --sandbox workshop/test-output/manual
```

`init` 只创建不存在的目录；不能接管已有目录。路径包含 `SlayTheSpire2.exe` 的祖先、Steam 内容、`mods`、`saves`、`AppData` 或目录联接时拒绝。元数据、日志、缓存和下载载荷都写入已初始化的目录。不要将该目录手动移入游戏目录。

可选 `--proxy http://127.0.0.1:端口` 使用自己已有的网络代理；不更改系统设置。默认遵循 .NET 系统/环境代理。不支持代理 URL 内账号密码。工具不会读取 Steam 客户端登录、账号、Cookie、密钥或存档。

`Ctrl+C` 取消；`--cancel-after-seconds 120` 用于可重复取消验证；`--timeout-seconds 30` 控制单次请求/CM 操作超时（1–300 秒，默认 30）。HTTP 瞬时故障最多三次尝试，CM 节点最多三个，明确权限拒绝不重试。退出码：成功 0、参数不足 2、失败 1、取消 130。

离线验证（**不代表真实 Steam 下载**）：

```powershell
./workshop/验证.ps1 -IncludeManagerChecks
```

执行锁定依赖还原、下载 CLI 构建、27 项离线检查，以及现有管理器 12 组隔离回归检查；不生成发布 ZIP，不访问真实游戏目录。GitHub 工作流 `workshop-check.yml` 运行相同检查。运行文件均忽略提交，只提交人工选取的文本/JSON 证据。

## 模块和技术路径

| 模块 | 职责 |
|---|---|
| `MetadataClient` / `WorkshopInput` | ID/完整链接解析；公开 `ISteamRemoteStorage/GetPublishedFileDetails/v1/`；核对条目 ID、consumer AppID、可见性、大小、类型 |
| `SteamHttp` | 官方 HTTPS 主机限制、逐次重定向验证、响应体限额、超时和重试 |
| `AnonymousSession` | 官方 CM 节点发现、WebSocket `LogOnAnonymous`、Manifest 请求码、depot key、CDN token；保留 EResult |
| `ManifestPlan` | Manifest 身份、路径、块覆盖范围校验；Chunk 缓存、组装、SHA-1/Adler32/SHA-256 |
| `TestSandbox` | 新目录显式初始化、路径穿越/ADS/设备名/链接检查、互斥写入、原子写文件 |
| `WorkshopDownloader` | 直链优先、CDN 后备、临时目录、完成收据、重复下载验证 |

方式 A：`file_url` 存在时下载原始字节到 `content.bin`，不解压、不执行。使用 HTTPS、Steam 声明大小及本地 SHA-256 校验。公开接口没有提供可信的原始文件哈希，所以收据明确标注 **local SHA-256 only**：本地哈希可发现后续损坏，但不能证明与作者原始文件密码学一致。非官方 HTTPS 地址保守拒绝。

方式 B：`file_url` 缺失且 `hcontent_file` 有效时，以固定 AppID/depot `2868840` 请求匿名 CM 授权；必须得到 `EResult.OK` 和非零请求码，再获取 Steam 签发的 depot key；下载压缩 Manifest、验证 depot/Manifest ID、解密文件名、从 CDN 获取块，由 SteamKit2 解密/解压，验证块长度、Adler32 和 SHA-1，按连续 offset 组装，最后验证整个文件大小及 Manifest SHA-1。成功后另记 SHA-256。

直接检查 Unified Message 的 `EResult`，避免 SteamKit2 `SteamContent.GetManifestRequestCode` 封装仅返回 `0` 而丢失拒绝原因。请求码、密钥、授权 token 不写入收据。CDN 仅在 CM 已授权后才能申请正常 CDN token；不会在权限拒绝后尝试无请求码 URL、匿名公用镜像或登录他人账号。

CDN 成功路径的端到端真实校验尚未完成；当前真实请求在 Manifest 授权处停止。加密 ZIP Chunk、组装和最终哈希的验证证据来自明确标记的离线夹具。

## 文件生命周期与重试

- `metadata/<id>.json`：Steam 原始公开响应；元数据获取成功不会创建完成收据。
- `runs/<utc>-<random>.json`：命令、条目 ID、阶段事件、成功/取消/网络/权限结果。
- `.incomplete/<random>/`：单次下载暂存区。异常保留 `failure.json`，永不移入 `completed`。直链半成品删除；CDN 已校验块保留以供重试。
- `cache/<SHA1>.chunk`：只有通过长度、Adler32、SHA-1 的块才发布为缓存；复用前全部重新校验，损坏则重新下载。按块续传，非 HTTP Range 续传。
- `completed/<id>/<version>/payload/`：全部文件通过验证后才原子移动完成目录，并附 `receipt.json`。程序不会自动导入此目录。

再次下载重新查询元数据；CDN 还重新取得授权和当前 Manifest。已有完成文件逐个重新计算哈希、比对文件集合，全部一致才复用。损坏或添加/缺少文件时拒绝覆盖，保留现场。不同版本保留在不同目录。直链没有可靠原始哈希，暂不做跨进程 Range 续传；失败重试从头开始，避免混合不同版本。

安全限制：默认每个条目最多 2 GiB、10,000 个 Manifest 条目、单块 16 MiB；Manifest 压缩/解压限额分别 16/32 MiB。库调用者可调整总大小上限。不存在解压 MOD ZIP、运行脚本、赋予可执行属性、修改安装状态或联机清单的代码。路径策略针对下载内容和已有链接，不将同权限本地恶意进程不断替换目录的竞争视为可防御的安全边界；测试目录应由当前用户独占使用。

## 来源和许可证

见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。本阶段不发布二进制安装包。未来分发必须保留 SteamKit2 LGPL 声明、许可证、对应源码获取途径和可替换动态库等义务，并复核传递依赖许可证。
