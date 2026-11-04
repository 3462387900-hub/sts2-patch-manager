# 参考与依赖

## WorkshopAndroidDownloader

- 项目：https://github.com/Apricityx/WorkshopAndroidDownloader
- 审阅提交：`7197d8c0866eadc12ea0abed5da6cacf6d432b46`
- Apache License 2.0，原文见 `licenses/WorkshopAndroidDownloader-Apache-2.0.txt`。
- 阅读了 `steam-protocol/src/main/kotlin/top/apricityx/workshop/steam/protocol/OkHttpSteamCmSession.kt`、`SteamContentClient.kt`，以及 `workshop-core/src/main/kotlin/top/apricityx/workshop/workshop/PublishedFileResolver.kt`、`UgcWorkshopDownloader.kt`、`WorkshopChunkProcessor.kt`、`WorkshopFileIntegrity.kt`。
- 参考其公开协议流程：公开元数据、匿名会话、UGC Manifest、CDN 分块缓存与按 offset 组装。此模块为独立 C# 实现，未复制或逐句翻译 Kotlin 源文件；加解密和协议实现使用 SteamKit2。
- 明确未采用其无请求码回退、无法解密仍继续处理文件名、整体 SHA-1 不符仍接受块校验的宽松分支。本模块遇到拒绝或不一致即失败。

## SteamKit2 3.4.0

- 项目：https://github.com/SteamRE/SteamKit
- NuGet：`SteamKit2` 版本 `3.4.0`（精确依赖锁见各项目 `packages.lock.json`）。
- 对应源码提交：`1c7bc9c41a529e8fbb1e6890f1e4dbcdc5200cb7`
- Copyright (C) 2018 Ryan Stecker & SteamRE Team。
- LGPL-2.1-only。原声明见 `licenses/SteamKit2-LGPL-2.1.txt`；完整许可证见 `licenses/LGPL-2.1.txt`。
- 通过 NuGet 动态引用，未修改、复制或静态嵌入 SteamKit2 源码。审阅了 CDN Client/DepotChunk/Server、DepotManifest、SteamContent 和 WebSocket/配置相关 API，以确认授权结果、块格式及校验行为。
- 后续若分发二进制，请附带声明、许可证和该版本对应源码或符合 LGPL 的源码提供方式；保持库可替换并遵守 LGPL 的调试修改/反向工程例外，不将该依赖简单视为本项目自有许可证覆盖。

锁定的传递依赖为 protobuf-net / protobuf-net.Core 3.2.56（Apache-2.0）、ZstdSharp.Port 0.8.7（MIT）、System.IO.Hashing 10.0.1（MIT）。许可证以锁定 NuGet 包内声明为准；本阶段仅提交源码、锁文件及测试记录，不发布二进制分发包。
