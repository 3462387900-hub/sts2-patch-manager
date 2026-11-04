# 第二轮依据（读取于 2026-11-04）

1. Valve 官方 [ISteamUGC.DownloadItem](https://partner.steamgames.com/doc/api/ISteamUGC#DownloadItem)：
   “If the user is not subscribed to the item (e.g. a Game Server using anonymous login), the workshop item will be downloaded and cached temporarily.”
   文档同时要求等待 DownloadItemResult_t，并核对回调 AppID。这明确描述匿名游戏服务器场景，不保证所有匿名 CM 用户/应用可用。
2. Valve 官方 [BInitWorkshopForGameServer](https://partner.steamgames.com/doc/api/ISteamUGC#BInitWorkshopForGameServer)：传入 unWorkshopDepotID 与绝对存储路径；调用方必须是游戏服务器。没有实际调用或伪装此身份。
3. [SteamKit2 3.4.0 SteamContent](https://github.com/SteamRE/SteamKit/blob/1c7bc9c41a529e8fbb1e6890f1e4dbcdc5200cb7/SteamKit2/SteamKit2/Steam/Handlers/SteamContent/SteamContent.cs)：GetManifestRequestCode 将 public 分支转为 null，填入 app_id/depot_id/manifest_id，返回响应体的 manifest_request_code。本项目额外保留头部 EResult。
4. [SteamKit2 生成的消息定义](https://github.com/SteamRE/SteamKit/blob/1c7bc9c41a529e8fbb1e6890f1e4dbcdc5200cb7/SteamKit2/SteamKit2/Base/Generated/SteamMsgContentSystem.cs)：CContentServerDirectory_GetManifestRequestCode_Request 的 protobuf 字段 1/2/3 是 app_id/depot_id/manifest_id，字段 4/5 是可选 branch/password hash。这是开源协议实现依据，不将其称作 Valve 官方公开 API 保证。
5. [DepotDownloader ContentDownloader](https://github.com/SteamRE/DepotDownloader/blob/107e4dfc2c2cfc590d86291a71a0c0fee0d8a4ba/DepotDownloader/ContentDownloader.cs)：ProcessPublishedFileAsync 以 file_url 或 hcontent_file 选择路径；DownloadAppAsync 的 isUgc 分支读取 depots/workshopdepot，使用相应 Manifest；请求码为零会取消。UGC depot 映射与普通分支 manifest 选择不同。
6. [DepotDownloader Steam3Session](https://github.com/SteamRE/DepotDownloader/blob/107e4dfc2c2cfc590d86291a71a0c0fee0d8a4ba/DepotDownloader/Steam3Session.cs)：公开应用 PICS 查询和 GetDepotManifestRequestCodeAsync。诊断只使用匿名会话自动签发的内存中临时令牌，不读取 Steam 账号登录资料。
7. [WorkshopAndroidDownloader UgcWorkshopDownloader](https://github.com/Apricityx/WorkshopAndroidDownloader/blob/7197d8c0866eadc12ea0abed5da6cacf6d432b46/workshop-core/src/main/kotlin/top/apricityx/workshop/workshop/UgcWorkshopDownloader.kt)：请求码/密钥请求失败后仍尝试公共路径，并有更宽松的最终文件哈希策略。这是项目策略差异，不能作为本游戏绕过拒绝的授权依据。本轮没有复制其代码或采用该回退。
8. 选择 Mod Options `2169435993` 的依据：[ZomboidSorter 公开验证记录](https://github.com/LookImEcho-Rick/ZomboidSorter/blob/454a23f9d12a44b202499de3420e7bc77d157e3a/docs/research/WORKSHOP_COLLECTION_IMPORT.md)。其中记载匿名 SteamCMD 下载 31,729 字节条目成功，并提醒 bootstrap 退出码可能与子进程真实结果不同。这是第三方记录；本轮用自己的真实文件与哈希另行确认。
9. 选择 VVE `3459650108` 的依据：[DepotDownloader issue 633](https://github.com/SteamRE/DepotDownloader/issues/633)。报告者写明同一批 RimWorld 条目匿名可以下载、登录账户反而因不具备 app license 被拒；其中命令指定 app 294100、pubfile 3459650108。作为有限选样线索，不将该用户报告视为官方权限保证。

上述开源源码只用于核对流程，没有复制成产品实现。原有许可证说明继续适用。无法访问的搜索页面、未执行的 SteamCMD 流程均不作为成功证据。
