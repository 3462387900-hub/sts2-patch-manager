# 尖塔修补站 · Spire Repair Station

**面向《杀戮尖塔 2》（Slay the Spire 2）的第三方 Windows MOD 管理器与兼容性修复工作台。**

使用 **C# · .NET 9 · WPF** 构建，帮助玩家集中管理本地 MOD、获取与更新 MOD、检查依赖关系，以及独立维护已审核的修复补丁。ATLAS 为原生 WPF 界面，不依赖 WebView2。

> 本项目并非 Mega Crit、Valve / Steam 或尖塔补给站的官方产品；不提供《杀戮尖塔 2》游戏本体，不随程序分发第三方 MOD。

**[下载稳定版](https://github.com/3462387900-hub/sts2-patch-manager/releases/latest)** · **[下载 ATLAS 测试版](https://github.com/3462387900-hub/sts2-patch-manager/releases/tag/v0.14.6-beta.1)** · [所有版本与发布说明](https://github.com/3462387900-hub/sts2-patch-manager/releases) · [补丁数据仓库](https://github.com/3462387900-hub/sts2-patches)

## 已实现的功能

| 模块 | 功能 |
| --- | --- |
| 游戏环境 | 自动查找常见 Steam 游戏安装位置，支持手动切换目录、一键启动游戏、扫描当前 MOD 与补丁状态。 |
| MOD 管理 | 查看名称、ID、作者、版本、来源和启停状态；搜索、筛选、排序、卡片与虚拟化列表；批量勾选和逐项安全预览。 |
| MOD 启用 / 停用 | 关闭游戏后，经依赖和清单核对、只读预览与明确确认，移动至独立停用保留区或恢复；不会直接删除 MOD。 |
| Steam 创意工坊 | 经维护者授权使用**尖塔补给站**接口，支持通过 Workshop ID / 链接获取 MOD，执行下载、文件校验、预览与安装。 |
| GitHub 来源 | 手动绑定或尝试发现仓库，查看已缓存的更新状态、检查可用更新并通过原有下载、备份和确认流程更新。自动匹配并不保证成功。 |
| 本地 ZIP 导入 | 对单个 MOD 的压缩包进行路径、内容和冲突检查，预览后导入；不将任意 ZIP 视为可信安装包。 |
| 依赖分析 | 根据 MOD 声明展示缺失、停用、版本不匹配和循环依赖；支持依赖关系视图及分页。 |
| 独立修复补丁 | 与普通 MOD 分开管理，扫描已收录补丁的适用性、健康依据、关联前置及版本状态，提供安装/停用预览、备份与回滚。 |
| 孤立补丁保护 | MOD 消失或被移除时，可按设置自动停用相关独立补丁，并保留原文件及状态提示。 |
| 环境快照与恢复 | 保存已启用及停用保留区的 MOD 状态，查看差异后恢复；写入前保留保护与校验流程。 |
| 联机排查 | 导出、比较已收录补丁状态并显示差异；它不能证明全部 MOD、存档或游戏运行时完全兼容。 |
| 管理器更新 | 独立的稳定版 / 测试版 Beta 更新通道；查看更新内容，校验下载附件，进行受保护的程序更新和恢复。 |

### 界面

- **ATLAS 原生工作台（Beta）**：总览、模组库、修复中心、更新中心、下载中心、设置。
- **经典 / 新版 WPF 工作台**：保留原有详细功能入口，便于逐项比较及回退。
- 大量 MOD 时采用 **DataGrid 行虚拟化**、卡片分页、搜索防抖与窗口自适应布局；实际性能仍取决于设备及 MOD 数量。

## 下载与使用

1. 从上方 **Releases** 下载 Windows ZIP，解压到独立、可写的文件夹。不要只复制 exe，也不要覆盖已有 `数据` 文件夹。
2. 运行 **`SpirePatch.exe`**，部分便携发布包还包含中文入口 **`00-启动尖塔修补站.exe`**。
3. 首次启动后确认检测到的《杀戮尖塔 2》目录；如不正确，手动选择包含 `SlayTheSpire2.exe` 的文件夹。
4. 在模组库查看已安装 MOD；在修复中心检查已收录补丁；下载或更新前先看预览和依赖提示。

**想体验新版 ATLAS：** 在「检查更新」中选择「测试版 Beta」并主动检查，或直接下载 [v0.14.6 Beta 1](https://github.com/3462387900-hub/sts2-patch-manager/releases/tag/v0.14.6-beta.1)。该版本是**预发布测试版**，不会通过稳定版更新通道自动推送。测试前请备份 MOD 和管理器数据。

## 文件安全与使用边界

- 下载、启停、安装、更新与恢复使用各自的只读预检、确认、备份和校验流程。文件不符合条件时会保守拒绝操作。
- 关闭游戏后再执行移动、安装和回滚；不要手动清理管理器创建的停用保留区、备份及更新恢复记录。
- GitHub / Workshop 的来源数据和下载服务可能变化，无法保证所有 MOD 都可自动识别、下载或兼容当前游戏版本。
- 创意工坊的 **Manifest 签名验证与本机游戏分支匹配** 尚未完成完整验证；文件完整性通过不等于游戏兼容性通过。
- 依赖分析以清单声明和管理器已知信息为准，不能推断所有运行时隐式依赖。
- 本工具不需要玩家提供 Steam 账号密码；外部来源、社区服务和网络状态仍可能影响下载结果。

## 仓库结构

整理后的源码按职责组织；**源码布局与玩家解压后的程序目录是两回事**。

```text
SpirePatch.csproj          # WPF/.NET 9 项目文件
App.xaml / App.xaml.cs     # 应用入口
src/
  UI/
    Atlas/                 # ATLAS 原生工作台
    Shell/                 # 导航、主题、主窗口
    Mods/ Patches/ Online/ # MOD、修复与在线页面
  Domain/
    Mods/ Patches/         # MOD 与补丁业务规则
    Environment/ Coop/     # 环境扫描、联机差异
  Infrastructure/
    GitHub/ Workshop/      # 下载来源、安装与校验
    Updates/ Online/       # 管理器更新与联网基础设施
  Tests/                   # 隔离检查和 WPF 界面回归
scripts/                   # 打包、隔离样本、发布检查
data/                      # 公开来源识别数据
releases/                  # 各历史版本的发布说明
docs/                      # 专题文档和实机验收清单
third_party/               # 第三方许可证文本
catalog-plan.json          # 已审核补丁安装方案
patch-catalog.json         # 已收录补丁说明快照
```

## 开发与验证

在安装 .NET 9 SDK 的 Windows 环境中运行：

```powershell
dotnet publish .\SpirePatch.csproj -c Release -r win-x64 --self-contained true
pwsh ./scripts/打包.ps1 -BuildOnly
```

第二条命令执行隔离构建与检查，不会打包为正式发布版。GitHub Actions 还会执行 WPF 界面回归、脚本检查及发布包安全验证；**CI 通过不代表实机游戏环境已通过测试**。

贡献或报告问题时，建议提供版本、Windows 分辨率与缩放、MOD 数量、复现步骤及脱敏截图；不要上传 Steam 凭证、个人存档或其他敏感数据。

历史更新内容统一查看 [GitHub Releases](https://github.com/3462387900-hub/sts2-patch-manager/releases)，不再在仓库首页维护累计更新日志。补丁作者与具体适配信息请查看 [补丁项目](https://github.com/3462387900-hub/sts2-patches)。

## 鸣谢与来源

特别感谢 **Apricityx** 及 **尖塔补给站** 维护者，授权尖塔修补站使用相关下载接口并提供接入支持，让玩家能够在 Windows 管理器中完成部分创意工坊 MOD 的获取与安装。

**尖塔补给站官网：https://workshop.apricityx.top/**

同时感谢 [WorkshopAndroidDownloader](https://github.com/Apricityx/WorkshopAndroidDownloader)（Apache-2.0）、[Sts2MobileLauncher](https://github.com/ModinMobileSTS/Sts2MobileLauncher)（协议兼容性参考）及 [SharpCompress](https://github.com/adamhathcock/sharpcompress)（MIT）等项目、开发者与社区贡献者。

详见 [鸣谢与来源](鸣谢与来源.md) 和 [第三方版权说明](THIRD-PARTY-NOTICES.md)。本管理器与上述服务及项目相互独立，不代表它们的官方客户端或背书。
