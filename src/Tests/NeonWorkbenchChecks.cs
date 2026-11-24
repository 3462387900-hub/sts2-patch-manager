using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SpirePatch;

public partial class MainWindow
{
    /// <summary>
    /// Creates an isolated real-filesystem game fixture with real manifests, DLLs,
    /// disabled slots via ModManager, reviewed patch fingerprints, and mock update caches.
    /// Every UI judgment runs through official C# production logic.
    /// </summary>
    internal async Task<string> SetupNeonIsolatedFixtureAsync(string parent)
    {
        parent = Path.GetFullPath(parent);
        Directory.CreateDirectory(parent);
        string root = Path.Combine(parent, "Neon测试游戏_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "mods"));
        Directory.CreateDirectory(Path.Combine(root, "data_sts2_windows_x86_64"));
        File.WriteAllBytes(Path.Combine(root, "SlayTheSpire2.exe"), []);
        File.WriteAllText(Path.Combine(root, ".spirepatch-fixture"), "isolated");
        File.WriteAllText(Path.Combine(root, "data_sts2_windows_x86_64", "sts2.dll"), "sts2-fixture-0.111.0");

        void WriteMod(string id, string name, string version, string author, string desc, params (string Id, string Min)[] deps)
        {
            string dir = Path.Combine(root, "mods", id);
            Directory.CreateDirectory(dir);
            var manifest = new
            {
                id,
                name,
                version,
                author,
                description = desc,
                has_dll = true,
                dependencies = deps.Select(d => string.IsNullOrEmpty(d.Min)
                    ? (object)new { id = d.Id }
                    : new { id = d.Id, min_version = d.Min }).ToArray()
            };
            File.WriteAllText(Path.Combine(dir, id + ".json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(dir, id + ".dll"), $"{id}@{version} fixture payload");
        }

        // Core libraries
        WriteMod("BaseLib", "BaseLib · 杀戮尖塔 2 模组基础库", "3.4.5", "Alchyr",
            "多数角色、遗物与联机补丁依赖的核心底层 Hook 与扩展接口库。");
        WriteMod("STS2-RitsuLib", "STS2-RitsuLib · 律律通用角色前置库", "0.6.7", "RitsuDev",
            "提供额外能量条、角色语音事件与自定义奖励克隆工具，为莱伊与妮芙扩展前置。",
            ("BaseLib", "3.4.0"));
        WriteMod("CznCore", "CznCore · 角色框架核心库", "1.0.2", "CznStudio",
            "Czn 系列角色共用状态机、动画帧管理与本地化加载器。",
            ("BaseLib", "3.4.0"));

        // Characters & Content (exact realistic scenario matching design/neon-exploration)
        WriteMod("Ray", "莱伊 · 沙原猎手", "0.1.0", "SandHunterLab",
            "以装填弹药与沙兽协同为核心的远程狙击角色，含专属 75 张卡牌与 8 件遗物。",
            ("BaseLib", "3.4.5"), ("STS2-RitsuLib", "0.6.5"));
        WriteMod("Phrolova", "弗洛洛 · 声骸共鸣角色", "1.1.0", "ResonanceWorkshop",
            "围绕定音顺序、彩乐保护与满音符替换机制设计的原创共鸣角色。",
            ("BaseLib", "3.4.7"));
        WriteMod("CyanrainNymphBeta", "妮芙 · 蓝雨测试版", "0.0.0-beta+exhaustfix.20261029", "Cyanrain",
            "以印花染色、消耗回响与卡兹戴尔事件为核心机制的角色模组。",
            ("BaseLib", "3.4.5"));
        WriteMod("Act4FinalAscent", "第四幕：最终登塔 · 建筑家终战扩展", "0.1.3", "SpireTeam",
            "新增第四幕地图路线、双精英试炼与最终首领「建筑家」完整阶段战。",
            ("BaseLib", "3.4.5"));
        WriteMod("HextechRunes", "海克斯大乱斗 / ARAM: Mayhem", "0.9.9", "MayhemForge",
            "引入阶梯强化符文、联机随机增益与高倍率数值流派的玩法扩展。",
            ("BaseLib", "3.4.5"));
        // Also place HextechRunes nested DLL matching patch evidence path
        string hexLibDir = Path.Combine(root, "mods", "HextechRunes", "lib", "0.111.0");
        Directory.CreateDirectory(hexLibDir);
        string hexDllPath = Path.Combine(hexLibDir, "HextechRunes.dll");
        File.WriteAllText(hexDllPath, "HextechRunes@0.9.9-upstream-fixed-heal-display");
        string hexDllSha = Catalog.Hash(hexDllPath);

        WriteMod("HextechRunesSponsorPack", "海克斯大乱斗 · 赞助符文扩展包", "0.9.2", "MayhemForge",
            "为海克斯大乱斗追加 26 枚彩阶符文与联动事件。",
            ("HextechRunes", "0.9.8"), ("BaseLib", "3.4.5"));
        WriteMod("YuWanCard", "鱼丸卡牌与博士猪猪联动扩展包（含焰魔回合机制）", "0.5.12", "YuWanWorkshop",
            "包含大量社区联动事件、博士猪猪三选一牌池及焰魔延迟结算牌组。",
            ("BaseLib", "3.4.5"));
        WriteMod("CznTipheraCharacter", "提菲拉 · Czn 角色扩展", "2.0.1-beta", "CznStudio",
            "依赖 CznCore 的原创角色，具备形态切换与专属奖励池。",
            ("CznCore", "1.0.0"), ("BaseLib", "3.4.5"));
        WriteMod("Sts2ModTranslator", "杀戮尖塔 2 模组通用自动翻译框架（含词库热加载）", "1.20.0", "SpireCN-Group",
            "为英文社区 MOD 提供词库覆盖与本地化热加载，不修改原 MOD 文件。");
        WriteMod("RemoveMultiplayerPlayerLimit", "解除多人联机四人人数上限与观战视角扩展", "0.0.15", "CoopModder",
            "允许 5–8 人联机建房，并在队员阵亡后保持战局同步观战。");
        WriteMod("Kakarot", "卡卡罗特 · 赛亚人战斗模组", "6.1.1", "ZFighterMod",
            "气力蓄能、变身阶段与十倍龟派气功爆发机制角色。",
            ("BaseLib", "3.4.5"));
        WriteMod("AK_Exusiai", "能天使 · 企鹅物流", "1.1.0", "RhodesSpire",
            "高频连射与过载节奏角色，适配联机队友增益交互。",
            ("BaseLib", "3.4.5"));
        WriteMod("STS2_WineFox", "酒狐 · 东方风格角色与事件扩展", "1.2.18", "KitsuneBrew",
            "以醉意层数与回合末选牌调整为核心的特色角色。",
            ("BaseLib", "3.4.5"));
        WriteMod("Noctis", "诺克提斯 · 路西斯王储", "1.0.2", "LucisForge",
            "幻影剑切换与位移突袭机制角色。",
            ("BaseLib", "3.4.0"));
        WriteMod("Noctis_Translation", "诺克提斯简体中文汉化补丁", "1.0.0", "SpireCN-Group",
            "为诺克提斯角色补齐完整简体中文卡牌、遗物与关键字说明。",
            ("Noctis", "1.0.0"));
        WriteMod("intentgraph2", "敌人意图预测图谱 2", "1.6.0", "IntentLab",
            "在敌方意图图标悬停时展示下一回合出招概率分支与伤害范围。");
        WriteMod("CombatStats", "战斗数据统计面板", "0.8.2", "SpireMetrics",
            "记录单场与整局伤害、格挡、抽牌效率和联机队友贡献分布。");
        WriteMod("EndlessMod", "无尽爬塔模式（Endless）", "1.24.1", "LoopMaster",
            "通关第三幕/第四幕后开启递增难度的无限循环爬塔与荒疫词条。",
            ("BaseLib", "3.4.5"));

        // Independent patches: 3 inside mods/_SpirePatch_Fixes/, plus 1 loose in mods/ root so PatchOrganizer detects it!
        void WritePatchMod(string id, string name, string version, bool loose = false)
        {
            string dir = loose
                ? Path.Combine(root, "mods", id)
                : Path.Combine(root, "mods", "_SpirePatch_Fixes", id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, id + ".json"), JsonSerializer.Serialize(new
            {
                id,
                name,
                version,
                author = "SpirePatch",
                description = "已收录独立修复补丁",
                has_dll = true
            }));
            File.WriteAllText(Path.Combine(dir, id + ".dll"), $"{id}@{version} patch binary");
        }
        WritePatchMod("LargeHealDisplayFix", "超大回血数字溢出修复", "1.0.0");
        WritePatchMod("PhrolovaNoteSyncFix", "弗洛洛音符同步修复", "1.0.0");
        WritePatchMod("NymphKazdelRewardFix", "妮芙卡兹戴尔奖励克隆修复", "1.0.0", loose: true);
        WritePatchMod("IgnisTurnSyncFix", "焰魔回合结算同步修复", "1.0.0");

        // Disable EndlessMod via official ModManager transaction
        ModManager.Apply(ModManager.Preview(root, Path.Combine(root, "mods", "EndlessMod"), false));

        // Disable STS2-RitsuLib temporarily BEFORE Ray/NymphKazdelRewardFix block it — actually create disabled slot cleanly
        // by moving Ray & NymphKazdelRewardFix aside while running real ModManager.Apply(Preview(STS2-RitsuLib, false)),
        // mirroring a player who disabled a prerequisite externally or before installing Ray!
        string tempHold = Path.Combine(parent, "hold-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(tempHold);
        Directory.Move(Path.Combine(root, "mods", "Ray"), Path.Combine(tempHold, "Ray"));
        Directory.Move(Path.Combine(root, "mods", "NymphKazdelRewardFix"), Path.Combine(tempHold, "NymphKazdelRewardFix"));
        ModManager.Apply(ModManager.Preview(root, Path.Combine(root, "mods", "STS2-RitsuLib"), false));
        Directory.Move(Path.Combine(tempHold, "Ray"), Path.Combine(root, "mods", "Ray"));
        Directory.Move(Path.Combine(tempHold, "NymphKazdelRewardFix"), Path.Combine(root, "mods", "NymphKazdelRewardFix"));
        Directory.Delete(tempHold, true);

        // Configure isolated game root and real update/source stores (never touches user's real game or AppData!)
        string dataRoot = Path.Combine(parent, "neon-data-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dataRoot);
        neonFixtureRoot = root;
        neonFixtureDataRoot = dataRoot;
        neonLastUpdateCheckTimeLabel = DateTime.Now.ToString("HH:mm:ss");
        neonActivityLog.Clear();
        UseGameRoot(root);

        // Wire mock HTTP GitHubModUpdates so BaseLib & Sts2ModTranslator have genuine cached updates ("有更新")
        githubRoot = root;
        githubModUpdates?.Dispose();
        githubModUpdates = new GitHubModUpdates(dataRoot, root, new UpdateChecks.Handler(req =>
        {
            string uri = req.RequestUri?.AbsoluteUri ?? "";
            if (uri.Contains("Alchyr/BaseLib-StS2", StringComparison.OrdinalIgnoreCase))
                return UpdateChecks.Reply("""{"tag_name":"v3.4.7","html_url":"https://github.com/Alchyr/BaseLib-StS2/releases/tag/v3.4.7","body":"适配游戏 0.111.0；补齐联机牌池同步钩子。"}""");
            if (uri.Contains("SpireCN-Group/Sts2ModTranslator", StringComparison.OrdinalIgnoreCase))
                return UpdateChecks.Reply("""{"tag_name":"v1.21.0","html_url":"https://github.com/SpireCN-Group/Sts2ModTranslator/releases/tag/v1.21.0","body":"新增第四幕事件词条。"}""");
            if (uri.Contains("CznStudio/CznCore-STS2", StringComparison.OrdinalIgnoreCase))
                return UpdateChecks.Reply("""{"tag_name":"v1.0.2","html_url":"https://github.com/CznStudio/CznCore-STS2/releases/tag/v1.0.2"}""");
            if (uri.Contains("IntentLab/intentgraph2", StringComparison.OrdinalIgnoreCase))
                return UpdateChecks.Reply("""{"tag_name":"v1.6.0","html_url":"https://github.com/IntentLab/intentgraph2/releases/tag/v1.6.0"}""");
            if (uri.Contains("LoopMaster/STS2-EndlessMod", StringComparison.OrdinalIgnoreCase))
                return UpdateChecks.Reply("""{"tag_name":"v1.24.1","html_url":"https://github.com/LoopMaster/STS2-EndlessMod/releases/tag/v1.24.1"}""");
            return UpdateChecks.Reply("""{"tag_name":"v1.0.0","html_url":"https://github.com/example/repo/releases/tag/v1.0.0"}""");
        }));

        githubModUpdates.Bind("BaseLib", "Alchyr/BaseLib-StS2");
        githubModUpdates.Bind("CznCore", "CznStudio/CznCore-STS2");
        githubModUpdates.Bind("Sts2ModTranslator", "SpireCN-Group/Sts2ModTranslator");
        githubModUpdates.Bind("intentgraph2", "IntentLab/intentgraph2");
        githubModUpdates.Bind("EndlessMod", "LoopMaster/STS2-EndlessMod");

        // Workshop bindings for Workshop-sourced MODs
        workshopSourceRoot = root;
        workshopModSources = new WorkshopModSources(dataRoot, root);
        workshopModSources.Bind("STS2-RitsuLib", 3748102911);
        workshopModSources.Bind("Ray", 3751029483);
        workshopModSources.Bind("Phrolova", 3749821045);
        workshopModSources.Bind("HextechRunes", 3748937859);
        workshopModSources.Bind("HextechRunesSponsorPack", 3749001124);
        workshopModSources.Bind("YuWanCard", 3746552190);
        workshopModSources.Bind("Kakarot", 3745902211);
        workshopModSources.Bind("AK_Exusiai", 3747120983);
        workshopModSources.Bind("STS2_WineFox", 3748332910);
        workshopModSources.Bind("Noctis", 3749312208);
        workshopModSources.Bind("CombatStats", 3744019284);
        workshopUpdatesRoot = root;
        string depotKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(v => (byte)v).ToArray());
        workshopModUpdates = new WorkshopModUpdates(() => new SpireSupplyStationClient(new UpdateChecks.Handler(req =>
        {
            var resp = UpdateChecks.Reply(JsonSerializer.Serialize(new
            {
                mode = "UGC",
                appId = SpireSupplyStationClient.SupportedAppId,
                publishedFileId = "3748937859",
                title = "海克斯大乱斗 / ARAM: Mayhem",
                fileName = "HextechRunes.zip",
                fileSizeBytes = 1024L,
                contentVersion = "0.9.10",
                directUrl = (string?)null,
                manifestId = "6601928374651984412",
                depotId = 2868840u,
                requestCode = "123456789",
                endpoints = new[] { new { origin = "http://cdn.example", query = "fixture=1" } },
                depotKeyBase64 = depotKey,
                expiresAt = "2099-01-01T00:00:00Z"
            }));
            resp.RequestMessage = req;
            return resp;
        })));

        // Build approved LifecycleEvidence matching the REAL HextechRunes.dll SHA-256 on disk
        // so PatchLifecycle.Evaluate & PatchLifecycle.PreviewRetire execute 100% real verification!
        string largeHealPatchId = Catalog.Bundled.Single(p => p.ComponentId == "LargeHealDisplayFix").Id;
        neonOverrideLifecycleEvidence = new LifecycleRegistry(1,
        [
            new LifecycleEvidence(
                PatchId: largeHealPatchId,
                TargetModId: "HextechRunes",
                RelativePath: @"lib\0.111.0\HextechRunes.dll",
                Sha256: hexDllSha,
                Version: "0.9.9",
                Decision: "fixed_upstream",
                ReviewStatus: "approved",
                Note: "当前本地 HextechRunes 0.9.9 主程序集精确匹配已审核上游修复指纹（fixed_upstream），该独立补丁可安全停用退役。",
                Sources: ["https://github.com/3462387900-hub/sts2-patches"])
        ]);

        // Wait for initial watcher events from fixture creation to settle
        await Task.Delay(1100);
        for (int i = 0; i < 80 && (busy || modInventoryRefreshRunning); i++) await Task.Delay(40);

        // Run real ModManager.Scan, GitHub update checks, and Workshop update check (HextechRunes)
        modInventory = ModManager.Scan(root);
        modInventoryDirty = false;
        modInventoryCleanVersion = Math.Max(modInventoryCleanVersion, Volatile.Read(ref modChangeVersion));
        foreach (string id in new[] { "BaseLib", "CznCore", "Sts2ModTranslator", "intentgraph2", "EndlessMod" })
        {
            var m = modInventory.Mods.Single(x => x.Id == id);
            await githubModUpdates.CheckAsync(m, force: true);
        }
        var hexMod = modInventory.Mods.Single(x => x.Id == "HextechRunes");
        workshopModSources.RecordBaseline("HextechRunes", 3748937859, "6601928374651120001", hexMod.Version, ModManager.FileSnapshot(hexMod.Folder));
        await workshopModUpdates.CheckAsync(hexMod, 3748937859, workshopModSources, force: true);

        // Run real Catalog.Scan + real PatchLifecycle.Evaluate!
        // Also craft a real approved patch definition matching IgnisTurnSyncFix's real fixture files
        // so RecommendationScanner genuinely evaluates Status == "已安装" -> "校验通过"!
        var fixtureCatalog = BuildFixtureVerifiedCatalog(root);
        onlineCatalog = new OnlineCatalog(
            Catalog.Revision,
            fixtureCatalog,
            new OnlineBundle("v0.0.0-fixture", "fixture.zip", 0, new string('0', 64), "https://example.invalid/fixture.zip", "https://example.invalid", "fixture"),
            DateTimeOffset.Now);
        sourceCurrent = true;

        var scan = Catalog.Scan(root, fixtureCatalog, Catalog.Revision);
        var history = ModHistory.CompareAndStore(root, dataRoot);
        var evaluated = PatchLifecycle.Evaluate(root, history, neonOverrideLifecycleEvidence,
            "已加载 1 条已审核上游修复证据记录。", scan, modInventory);

        // Create one real initial MOD environment snapshot for Recovery page
        var initialSnap = ModEnvironmentSnapshots.Create(root, dataRoot, "初始隔离环境自动保护");
        // Seed one realistic mod_fix_backups batch journal (installed) for Recovery batch card
        string demoBackupDir = Path.Combine(root, "mod_fix_backups", "STS2-AllFixes-20261102-demo");
        Directory.CreateDirectory(demoBackupDir);
        File.WriteAllText(Path.Combine(demoBackupDir, "restore.json"), JsonSerializer.Serialize(new
        {
            status = "installed",
            version = "20261102.3",
            files = new[]
            {
                new { relative = @"mods\_SpirePatch_Fixes\IgnisTurnSyncFix\IgnisTurnSyncFix.dll", existed = false, backup = "", oldHash = "" },
                new { relative = @"mods\NymphKazdelRewardFix\NymphKazdelRewardFix.dll", existed = false, backup = "", oldHash = "" }
            }
        }, new JsonSerializerOptions { WriteIndented = true }));

        report = scan;
        scannedCatalog = fixtureCatalog;
        states = scan.Patches;
        lifecycleEvidence = neonOverrideLifecycleEvidence;
        lifecycleReport = evaluated;
        lifecycleStale = false;
        lifecycleStatus = "已完成本地指纹与健康基线核验；适配结论仅覆盖已收录条件。";
        demo = false;

        // Seed a realistic schema 2 Co-op peer snapshot in dataRoot and analyze against current local scan
        string peerPath = WriteFixtureCoopPeerSnapshot(dataRoot, scan, modInventory);
        var peerDoc = CoopReports.Read(peerPath);
        var localSnap = CoopReports.Create(scan, scan.Patches, false, modInventory);
        coopPeer = peerDoc;
        coopLocal = localSnap;
        coopComparison = CoopReports.Compare(localSnap, peerDoc);
        coopRows = CoopPlanner.Analyze(localSnap, peerDoc, scan, modInventory);
        neonCoopPeerLabel = "队友「阿哲」（示例快照）";

        RecordNeonActivity("snapshot", $"自动创建初始环境快照（{initialSnap.Files.Length} 文件 · {ModEnvironmentSnapshots.FormatBytes(initialSnap.TotalBytes)}）。");
        RecordNeonActivity("scan", "完成隔离目录只读扫描与补丁健康核验（21 项普通 MOD · 4 项已装独立补丁）。");

        ChangeUiTheme(UiThemes.Neon);
        if (neonWorkbenchView != null)
            neonWorkbenchView.DemoUtilityControlsVisible = true;
        RefreshNeonWorkbench(preserveViewport: false);
        return root;
    }

    static string WriteFixtureCoopPeerSnapshot(string dataRoot, ScanReport localScan, ModInventory localInventory)
    {
        var baseLocal = CoopReports.Create(localScan, localScan.Patches, false, localInventory);
        var node = JsonNode.Parse(baseLocal.GetRawText())!.AsObject();
        if (node["patches"] is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is not JsonObject obj) continue;
                string id = obj["id"]?.GetValue<string>() ?? "";
                if (id.Equals("IgnisTurnSyncFix", StringComparison.OrdinalIgnoreCase))
                {
                    // Peer doesn't have IgnisTurnSyncFix -> Local has "本机多出" (CoopAction.Disable, selectable!)
                    obj["status"] = "不适用";
                    obj["presence"] = "absent";
                    obj["filesMatch"] = false;
                }
                else if (id.Equals("Act4FinalAscent", StringComparison.OrdinalIgnoreCase))
                {
                    // Peer has Act4FinalAscent installed -> Local needs manual review (unselectable!)
                    obj["status"] = "已安装";
                    obj["presence"] = "active";
                    obj["filesMatch"] = true;
                }
                else if (id.Equals("LargeHealDisplayFix", StringComparison.OrdinalIgnoreCase))
                {
                    obj["status"] = "已停用";
                    obj["presence"] = "disabled";
                    obj["filesMatch"] = false;
                }
            }
        }
        string peerPath = Path.Combine(dataRoot, "coop-peer-azhe.json");
        File.WriteAllText(peerPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return peerPath;
    }

    static OnlinePatch[] BuildFixtureVerifiedCatalog(string root)
    {
        string sts2Sha = Catalog.Hash(Path.Combine(root, "data_sts2_windows_x86_64", "sts2.dll"));
        string yuwanSha = Catalog.Hash(Path.Combine(root, "mods", "YuWanCard", "YuWanCard.dll"));
        string ignisDllSha = Catalog.Hash(Path.Combine(root, "mods", "_SpirePatch_Fixes", "IgnisTurnSyncFix", "IgnisTurnSyncFix.dll"));
        string ignisJsonSha = Catalog.Hash(Path.Combine(root, "mods", "_SpirePatch_Fixes", "IgnisTurnSyncFix", "IgnisTurnSyncFix.json"));

        var list = new List<OnlinePatch>();
        foreach (var bundled in Catalog.Bundled)
        {
            if (bundled.ComponentId.Equals("IgnisTurnSyncFix", StringComparison.OrdinalIgnoreCase))
            {
                string json = JsonSerializer.Serialize(new
                {
                    schema_version = "1.0",
                    id = bundled.Id,
                    version = "1.0.0",
                    publication_status = "published",
                    review_status = "approved",
                    title = "焰魔回合结算同步修复",
                    summary = "在结束回合前等待已开始的选牌动作完成，减少主客机结算时序偏差。",
                    issue = new { diagnosis = new { conclusion = "校验本地文件指纹通过。" } },
                    components = new[] { new { mod_id = "YuWanCard", role = "affected" } },
                    compatibility = new
                    {
                        profiles = new[]
                        {
                            new
                            {
                                profile_id = "fixture-0.111.0",
                                game_files = new[] { new { relative_path = "data_sts2_windows_x86_64/sts2.dll", sha256 = sts2Sha } },
                                required_mods = new[]
                                {
                                    new
                                    {
                                        mod_id = "YuWanCard",
                                        version_display = "v0.5.12",
                                        files = new[] { new { relative_path = "YuWanCard.dll", sha256 = yuwanSha } }
                                    }
                                },
                                excluded_mod_ids = Array.Empty<string>()
                            }
                        }
                    },
                    detection = new
                    {
                        installed_fingerprints = new[]
                        {
                            new { mod_id = "IgnisTurnSyncFix", relative_path = "IgnisTurnSyncFix.dll", sha256 = ignisDllSha },
                            new { mod_id = "IgnisTurnSyncFix", relative_path = "IgnisTurnSyncFix.json", sha256 = ignisJsonSha }
                        }
                    },
                    relationships = new
                    {
                        requires_patches = Array.Empty<object>(),
                        requires_when_mod_present = Array.Empty<object>(),
                        conflicts_patch_ids = Array.Empty<string>()
                    },
                    limitations = new[] { "文件扫描仅核对已收录构建指纹。" }
                });
                using var doc = JsonDocument.Parse(json);
                list.Add(Catalog.FromMetadata("IgnisTurnSyncFix", doc.RootElement));
            }
            else
            {
                list.Add(bundled);
            }
        }
        return list.ToArray();
    }

    internal async Task CaptureNeonWorkbenchAsync(string parent, string output)
    {
        parent = Path.GetFullPath(parent);
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);

        var checks = new List<string>();
        void Check(bool ok, string label)
        {
            if (!ok) throw new InvalidOperationException("验证失败：" + label);
            checks.Add("PASS " + label);
            File.WriteAllLines(Path.Combine(output, "工作台验证报告.txt"), checks);
        }

        async Task Idle()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            UpdateLayout();
        }

        static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (var nested in Descendants<T>(child)) yield return nested;
            }
        }

        static string AllText(DependencyObject root) => string.Join(" ", Descendants<TextBlock>(root).Select(t =>
        {
            if (t.Inlines.Count == 0) return t.Text;
            return string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text));
        }));

        static void Click(Button btn)
        {
            if (!btn.IsEnabled || !btn.IsVisible)
                throw new InvalidOperationException("目标按钮不可用或不可见：" + (AutomationProperties.GetName(btn) ?? btn.Content?.ToString()));
            btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, btn));
        }

        async Task ShotAtScaleAsync(string name, double scale, double logicalWidth = 1280, double logicalHeight = 850)
        {
            Width = logicalWidth;
            Height = logicalHeight;
            await Task.Delay(240);
            await Idle();
            var target = neonWorkbenchView!;
            double w = target.ActualWidth > 0 ? target.ActualWidth : logicalWidth;
            double h = target.ActualHeight > 0 ? target.ActualHeight : logicalHeight;

            if (Math.Abs(scale - 1.0) > 0.01)
                VisualTreeHelper.SetRootDpi(this, new DpiScale(scale, scale));
            InvalidateMeasure();
            InvalidateArrange();
            await Idle();

            int pxW = Math.Max(1, (int)Math.Round(w * scale));
            int pxH = Math.Max(1, (int)Math.Round(h * scale));
            double dpi = 96.0 * scale;
            var bitmap = new RenderTargetBitmap(pxW, pxH, dpi, dpi, PixelFormats.Pbgra32);
            bitmap.Render(target);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, name + ".png"));
            encoder.Save(stream);

            if (Math.Abs(scale - 1.0) > 0.01)
            {
                VisualTreeHelper.SetRootDpi(this, new DpiScale(1.0, 1.0));
                InvalidateMeasure();
                InvalidateArrange();
                await Idle();
            }
        }

        // --------------------------------------------------------------------
        // STEP 1: Setup isolated fixture & verify initial state + 100%/125% captures
        // --------------------------------------------------------------------
        string fixture = await SetupNeonIsolatedFixtureAsync(parent);
        await Idle();
        var view = neonWorkbenchView!;
        var snap = view.CurrentSnapshot!;

        Check(snap.AllRows.Length == 21, $"真实清单读取 21 个普通 MOD（实际 {snap.AllRows.Length}）");
        Check(snap.HardBlockCount == 2, $"真实依赖图检测到 2 类阻断（停用前置 STS2-RitsuLib + 版本差 BaseLib，实际 {snap.HardBlockCount}）");
        Check(view.NavWorkbenchDotControl.Visibility == Visibility.Visible, "导航「工作台」旁小红点在有阻断时显示");
        Check(view.NavPatchesPillControl.Visibility == Visibility.Visible && int.Parse(view.NavPatchesCountControl.Text) == snap.PendingPatchesCount,
            $"导航「补丁」旁计数来自真实扫描（{snap.PendingPatchesCount}）");
        Check(view.NavAcquirePillControl.Visibility == Visibility.Visible && view.NavAcquireCountControl.Text == "3",
            "导航「获取与更新」旁计数来自真实更新缓存（3：BaseLib + HextechRunes + Sts2ModTranslator）");
        Check(snap.FilterCounts["issues"] == 6 && snap.FilterCounts["disabled"] == 2 &&
              snap.FilterCounts["lib"] == 3 && snap.FilterCounts["unbound"] == 5,
            $"筛选标签计数来自真实清单与筛选（需处理 {snap.FilterCounts["issues"]}、前置库 {snap.FilterCounts["lib"]}、已停用 {snap.FilterCounts["disabled"]}、未绑来源 {snap.FilterCounts["unbound"]}）");

        var baseLibRow = snap.AllRows.Single(r => r.Mod.Id == "BaseLib");
        var phrolovaRow = snap.AllRows.Single(r => r.Mod.Id == "Phrolova");
        var diffParts = NeonVersionDiff.Compute(baseLibRow.Mod.Version, phrolovaRow.OwnVersionMismatch!.RequiredVersion);
        Check(diffParts.FirstDiffIndex == 2 && diffParts.ActualSegments[2] == "5" && diffParts.ExpectedSegments[2] == "7",
            "版本号差异算法仅突出差异位（3.4.[5] vs 3.4.[7]，前缀 3.4. 不高亮）");

        await ShotAtScaleAsync("01-wpf-workbench-100", 1.0, 1280, 850);
        await ShotAtScaleAsync("02-wpf-workbench-125", 1.25, 1280, 850);
        await ShotAtScaleAsync("03-wpf-workbench-125-compact", 1.25, 1220, 740);
        Width = 1280; Height = 850; await Idle();

        view.ApplySnapshot(snap with { AllRows = [] });
        await Idle();
        Check(AllText(view.DeskContentControl).Contains("清单中暂无普通 MOD"), "空清单提供导入入口");
        Click(Descendants<Button>(view.DeskContentControl).Single(b => Equals(b.Content, "前往获取与更新")));
        Check(view.ActiveTab == "acquire", "空清单入口打开获取与更新页");
        view.SwitchTab("workbench");
        view.ApplySnapshot(snap);
        await Idle();

        // --------------------------------------------------------------------
        // STEP 2: Search MOD with dependency anomaly -> Inspect -> Preview -> Cancel preserves position -> Confirm updates all!
        // --------------------------------------------------------------------
        view.SearchBoxControl.Focus();
        view.SearchBoxControl.Text = "莱伊";
        await Idle();
        var visibleAfterSearch = view.DeskContentControl.Children.OfType<Border>().ToArray();
        var rayRowVisual = visibleAfterSearch.SingleOrDefault(r => Equals(r.Tag, "Ray"));
        var ritsuRowVisual = visibleAfterSearch.SingleOrDefault(r => Equals(r.Tag, "STS2-RitsuLib"));
        Check(rayRowVisual != null && ritsuRowVisual != null && visibleAfterSearch.Length == 2,
            "搜索「莱伊」精准定位到有依赖异常的 MOD（Ray）及其停用前置（STS2-RitsuLib）");

        // Open Ray's Causal Inspector
        var detailBtn = Descendants<Button>(rayRowVisual!)
            .Single(b => (AutomationProperties.GetName(b) ?? "").Contains("查看详情"));
        Click(detailBtn);
        await Idle();
        Check(view.IsInspectorOpen && view.SelectedModId == "Ray", "点击详情展开右侧因果检视面板，定位 Ray");

        string inspectorTextBefore = string.Join(" ", Descendants<TextBlock>(view.InspectorBodyControl).Select(t => t.Text));
        Check(inspectorTextBefore.Contains("STS2-RitsuLib") && inspectorTextBefore.Contains("已停用"),
            "因果检视面板清晰指出缺失/停用前置 STS2-RitsuLib");

        await ShotAtScaleAsync("04-wpf-search-inspector-100", 1.0, 1280, 850);
        await ShotAtScaleAsync("05-wpf-search-inspector-125", 1.25, 1280, 850);

        // STEP 2.1: Direct inline restore pilot — Failure Boundary #1 (Real SHA-256 tamper -> NO modal, disk untouched, switch stays OFF!)
        var ritsuBefore = ModManager.Scan(fixture).Mods.Single(m => m.Id == "STS2-RitsuLib");
        Check(!ritsuBefore.Enabled, "恢复前 STS2-RitsuLib 在磁盘上确实处于停用区");
        Check(AllText(view.InspectorBodyControl).Contains("预计解除莱伊的前置阻断"),
            "操作前右侧红卡用人话说明预期结果（恢复 STS2-RitsuLib 后预计解除莱伊的前置阻断）");

        string ritsuDllInSlot = Path.Combine(ritsuBefore.Folder, "STS2-RitsuLib.dll");
        byte[] originalDllBytes = File.ReadAllBytes(ritsuDllInSlot);
        try
        {
            // Simulate offline tamper in the disabled slot (Scan succeeds since manifest is intact; real Preview SHA-256 check must catch it!)
            File.AppendAllText(ritsuDllInSlot, " TAMPER");
            await Task.Delay(250);
            for (int i = 0; i < 60 && (busy || modInventoryRefreshRunning); i++) await Task.Delay(40);
            await RefreshModsAsync();
            await Idle();

            var restoreBtnTamper = Descendants<Button>(view.InspectorBodyControl)
                .Single(b => (AutomationProperties.GetName(b) ?? "").Contains("恢复启用 STS2-RitsuLib"));
            Click(restoreBtnTamper);
            for (int i = 0; i < 80 && (busy || transactionWriting); i++) await Task.Delay(30);
            await Idle();

            Check(!view.IsModalOpen, "直启停样板不弹出重型事务弹窗");
            Check(!ModManager.Scan(fixture).Mods.Single(m => m.Id == "STS2-RitsuLib").Enabled,
                "文件指纹被篡改时拒绝恢复，磁盘保持原状且 STS2-RitsuLib 仍在停用区");
            string tamperInspector = AllText(view.InspectorBodyControl);
            Check(view.GetInlineToggleStatus("STS2-RitsuLib")?.Phase == NeonInlineTogglePhase.Failed &&
                  !view.CurrentSnapshot!.AllRows.Single(r => r.Mod.Id == "STS2-RitsuLib").Mod.Enabled &&
                  tamperInspector.Contains("停用期间文件被修改"),
                "恢复失败时开关保持停用不假切换，并在原位给出真实指纹篡改原因");
        }
        finally
        {
            File.WriteAllBytes(ritsuDllInSlot, originalDllBytes);
            await Task.Delay(250);
            for (int i = 0; i < 60 && (busy || modInventoryRefreshRunning); i++) await Task.Delay(40);
            await RefreshModsAsync();
            await Idle();
        }

        // STEP 2.2: Direct inline restore pilot — Moment 1 (click) -> Moment 2 (in-flight pending) -> Moment 3 (in-place Sage receipt after Toast gone!)
        int issuesBefore = view.CurrentSnapshot!.FilterCounts["issues"];
        int disabledBefore = view.CurrentSnapshot!.FilterCounts["disabled"];
        neonTestRestoreStageDelayMs = 360;
        try
        {
            var restoreBtn = Descendants<Button>(view.InspectorBodyControl)
                .Single(b => (AutomationProperties.GetName(b) ?? "").Contains("恢复启用 STS2-RitsuLib"));
            Click(restoreBtn);
            await Task.Delay(70);
            await Idle();

            string midInspector = AllText(view.InspectorBodyControl);
            string midDesk = AllText(view.DeskContentControl);
            Check(!view.IsModalOpen &&
                  view.GetInlineToggleStatus("STS2-RitsuLib")?.Phase == NeonInlineTogglePhase.Applying &&
                  midInspector.Contains("正在启用 STS2-RitsuLib") &&
                  midDesk.Contains("正在启用…") &&
                  !view.CurrentSnapshot!.AllRows.Single(r => r.Mod.Id == "STS2-RitsuLib").Mod.Enabled,
                "处理期间原位显示「正在启用…」，且滑块保持停用位不提前偷跑");

            // Capture live mid-flight pending frame immediately without waiting 240ms
            {
                var rtb = new RenderTargetBitmap((int)Math.Round(view.ActualWidth), (int)Math.Round(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                rtb.Render(view);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = File.Create(Path.Combine(output, "06-wpf-inline-restoring-100.png"));
                enc.Save(fs);
            }

            for (int i = 0; i < 160 && (busy || transactionWriting || modInventoryRefreshRunning); i++)
                await Task.Delay(40);
            await Idle();
        }
        finally
        {
            neonTestRestoreStageDelayMs = 0;
        }

        Check(ModManager.Scan(fixture).Mods.Single(m => m.Id == "STS2-RitsuLib").Enabled,
            "直接恢复通过正式 ModManager.Preview 与 Apply 在隔离磁盘目录恢复启用了 STS2-RitsuLib");
        var snapAfter = view.CurrentSnapshot!;
        var rayAfter = snapAfter.AllRows.Single(r => r.Mod.Id == "Ray");
        var ritsuAfter = snapAfter.AllRows.Single(r => r.Mod.Id == "STS2-RitsuLib");
        Check(!rayAfter.HasDanger && ritsuAfter.Mod.Enabled && !ritsuAfter.HasDanger,
            "恢复完成后 Ray 与 STS2-RitsuLib 相关行同步更新，异常状态清除");
        Check(snapAfter.FilterCounts["issues"] == issuesBefore - 2 && snapAfter.FilterCounts["disabled"] == disabledBefore - 1,
            $"筛选计数同步更新（需处理 {issuesBefore}→{snapAfter.FilterCounts["issues"]}，已停用 {disabledBefore}→{snapAfter.FilterCounts["disabled"]}）");
        Check(view.SearchBoxControl.Text == "莱伊" && view.SelectedModId == "Ray" && view.IsInspectorOpen,
            "恢复完成后保持当前搜索和 Ray 详情打开状态");

        // Moment 3: Even after Toasts are cleared/dismissed, the in-place Sage receipt stays where the red warning was!
        view.ClearToasts();
        await Idle();
        string inspectorTextAfter = AllText(view.InspectorBodyControl);
        Check(!inspectorTextAfter.Contains("依赖阻断：") &&
              inspectorTextAfter.Contains("STS2-RitsuLib 已启用，莱伊的前置阻断已解除。") &&
              !inspectorTextAfter.Contains("同时修好") &&
              inspectorTextAfter.Contains("v0.6.7 ✓"),
            "Toast 消失后原红卡位置留驻松绿确认回执「STS2-RitsuLib 已启用，莱伊的前置阻断已解除。」且前置链更新为 v0.6.7 ✓，不超前承诺补丁结论");
        Check(view.WatcherBannerControl.Visibility == Visibility.Visible &&
              AllText(view.WatcherBannerControl).Contains("补丁健康结论已过期"),
            "顶部同步诚实提示补丁健康结论需重新核验，与前置解除各说清一件事");

        await ShotAtScaleAsync("07-wpf-after-confirm-100", 1.0, 1280, 850);

        // --------------------------------------------------------------------
        // STEP 3: Health verification -> Verified "校验通过" + Side-by-Side Patch Status & Type -> Stale Health Immediately Withdraws "校验通过"!
        // --------------------------------------------------------------------
        await RefreshNeonFullScanAndHealthAsync(includeLifecycle: true);
        await Idle();

        // Inspect YuWanCard (has IgnisTurnSyncFix genuinely verified as "校验通过" + "独立补丁")
        view.SearchBoxControl.Text = "";
        await Idle();
        view.ClearToasts();
        view.FocusMod("YuWanCard");
        await Idle();
        view.InspectorScrollControl.ScrollToVerticalOffset(220);
        await Idle();
        string yuwanInspectorText = string.Join(" ", Descendants<TextBlock>(view.InspectorBodyControl).Select(t => t.Text));
        Check(yuwanInspectorText.Contains("校验通过") && yuwanInspectorText.Contains("独立补丁"),
            "有效健康核验下，YuWanCard 详情并排显示补丁状态「校验通过」与类型「独立补丁」");
        await ShotAtScaleAsync("08-wpf-verified-patch-badges-100", 1.0, 1280, 850);

        // Trigger external file edit -> MUST withdraw old "校验通过" ON THE SPOT!
        string extMarker = Path.Combine(fixture, "mods", "external-touch.txt");
        File.WriteAllText(extMarker, "external edit");
        modInventoryDirty = true;
        InvalidateLifecycle("检测到 MOD 文件变化，请重新检查健康状态。");
        await Idle();
        view.InspectorScrollControl.ScrollToVerticalOffset(220);
        await Idle();
        string allVisibleTextWhenStale = string.Join(" ",
            Descendants<TextBlock>(view.LaunchGateControl).Concat(Descendants<TextBlock>(view.InspectorBodyControl)).Select(t => t.Text));
        Check(view.WatcherBannerControl.Visibility == Visibility.Visible,
            "外部文件变动后立刻弹出失效提示带");
        Check(!allVisibleTextWhenStale.Contains("校验通过") && allVisibleTextWhenStale.Contains("需重新检查"),
            "健康结论过期时当场撤下旧的「校验通过」标记并改为「需重新检查」");
        await ShotAtScaleAsync("09-wpf-stale-health-withdrawn-100", 1.0, 1280, 850);

        // Clean marker & re-verify health for patch retirement check
        File.Delete(extMarker);
        await Task.Delay(1000);
        for (int i = 0; i < 80 && (busy || modInventoryRefreshRunning); i++) await Task.Delay(40);
        await RefreshNeonFullScanAndHealthAsync(includeLifecycle: true);
        await Idle();
        view.ClearToasts();

        // --------------------------------------------------------------------
        // STEP 4: Patch Retirement Preflight using Real PatchLifecycle Evidence
        // --------------------------------------------------------------------
        view.FocusMod("HextechRunes");
        await Idle();
        view.InspectorScrollControl.ScrollToVerticalOffset(240);
        await Idle();
        string hexInspectorText = string.Join(" ", Descendants<TextBlock>(view.InspectorBodyControl).Select(t => t.Text));
        Check(hexInspectorText.Contains("可安全退役") && hexInspectorText.Contains("独立补丁"),
            "HextechRunes 精确匹配已审核上游修复 SHA-256 时，并排显示「可安全退役」与「独立补丁」");
        await ShotAtScaleAsync("09b-wpf-retirable-patch-badges-100", 1.0, 1280, 850);
        var retireBtn = Descendants<Button>(view.InspectorBodyControl)
            .Single(b => (AutomationProperties.GetName(b) ?? "").Contains("预览安全退役 LargeHealDisplayFix"));
        Click(retireBtn);
        for (int i = 0; i < 100 && (!view.IsModalOpen || busy); i++) await Task.Delay(30);
        await Idle();
        Check(view.IsModalOpen && retirePreview != null,
            "补丁退役通过正式 PatchLifecycle.PreviewRetire 生成可逆退役预览");
        var techToggleBtn = Descendants<Button>(view.ModalBackdropControl)
            .Single(b => (AutomationProperties.GetName(b) ?? "").Contains("展开或收起技术细节"));
        Check(AllText(view.ModalBackdropControl).Contains("原模组已确认修复此问题") &&
              AllText(techToggleBtn).Contains("查看路径与校验细节"),
            "保留重弹窗默认采用「人话摘要优先 + 路径/指纹/令牌折叠收纳」");
        await ShotAtScaleAsync("10-wpf-patch-retire-preview-100", 1.0, 1280, 850);
        Click(techToggleBtn);
        await Idle();
        Check(AllText(techToggleBtn).Contains("收起技术细节"),
            "点击「查看路径与校验细节」可按需展开完整 SHA-256、路径与令牌记录");
        await ShotAtScaleAsync("10b-wpf-patch-retire-expanded-100", 1.0, 1280, 850);
        Click(techToggleBtn);
        await Idle();
        // Cancel retirement preview cleanly
        Click(Descendants<Button>(view.ModalBackdropControl).Single(b => Equals(b.Content, "取消")));
        await Idle();

        // Also check Act4FinalAscent shows built-in fix type ("原 MOD 内置修复") side-by-side
        view.FocusMod("Act4FinalAscent");
        await Idle();
        string act4InspectorText = string.Join(" ", Descendants<TextBlock>(view.InspectorBodyControl).Select(t => t.Text));
        Check(act4InspectorText.Contains("原 MOD 内置修复"),
            "内置修复补丁并排显示状态与「原 MOD 内置修复」类型标签");

        // --------------------------------------------------------------------
        // STEP 5: Background Refresh Preserves Search, Focus, Caret & Scroll Position!
        // --------------------------------------------------------------------
        view.CloseInspector();
        await Idle();
        // Type a query that still leaves a tall scrollable list ("BaseLib" matches 15+ mods via dependency search!)
        view.SearchBoxControl.Text = "BaseLib";
        await Idle();
        var scope = FocusManager.GetFocusScope(view.SearchBoxControl);
        FocusManager.SetFocusedElement(scope, view.SearchBoxControl);
        Keyboard.Focus(view.SearchBoxControl);
        view.SearchBoxControl.CaretIndex = view.SearchBoxControl.Text.Length;
        await Idle();
        bool SearchFocused() => view.SearchBoxControl.IsKeyboardFocused ||
            ReferenceEquals(FocusManager.GetFocusedElement(scope), view.SearchBoxControl);
        Check(view.DeskScrollControl.ScrollableHeight > 120,
            $"搜索「BaseLib」后列表具备足够滚动高度（{view.DeskScrollControl.ScrollableHeight:F0}px）");
        double targetScroll = Math.Min(180, view.DeskScrollControl.ScrollableHeight - 20);
        view.DeskScrollControl.ScrollToVerticalOffset(targetScroll);
        await Idle();
        double offsetBeforeRefresh = view.DeskScrollControl.VerticalOffset;
        int caretBeforeRefresh = view.SearchBoxControl.CaretIndex;
        Check(SearchFocused() && offsetBeforeRefresh > 80,
            $"后台刷新前状态就绪：焦点在搜索框、光标位置 {caretBeforeRefresh}、滚动位置 {offsetBeforeRefresh:F1}px");

        // Run a real background inventory refresh!
        await RefreshModsAsync();
        await Idle();
        Check(view.SearchBoxControl.Text == "BaseLib", "后台刷新后搜索词「BaseLib」完整保留");
        Check(SearchFocused() && view.SearchBoxControl.CaretIndex == caretBeforeRefresh,
            $"后台刷新后搜索框焦点与光标位置（{view.SearchBoxControl.CaretIndex}）完整保留");
        Check(Math.Abs(view.DeskScrollControl.VerticalOffset - offsetBeforeRefresh) < 1.5,
            $"后台刷新后滚动位置完整保留（刷新前 {offsetBeforeRefresh:F1}px → 刷新后 {view.DeskScrollControl.VerticalOffset:F1}px）");

        // --------------------------------------------------------------------
        // STEP 6: Dependency Lens Visual Capture at 100% and 125%
        // --------------------------------------------------------------------
        view.SearchBoxControl.Text = "";
        await Idle();
        var depBtn = Descendants<Button>(view)
            .Single(b => Equals(b.Content, "依赖图谱"));
        Click(depBtn);
        await Idle();
        await ShotAtScaleAsync("11-wpf-dep-lens-100", 1.0, 1280, 850);
        await ShotAtScaleAsync("12-wpf-dep-lens-125", 1.25, 1280, 850);

        var depSnapshot = view.CurrentSnapshot!;
        var graph = depSnapshot.DependencyGraph!;
        view.SearchBoxControl.Text = "无匹配关系-测试";
        await Idle();
        Check(AllText(view.DeskContentControl).Contains("没有匹配的依赖关系") &&
              !AllText(view.DeskContentControl).Contains("当前没有依赖异常关系"), "搜索无匹配时不会误报依赖正常");
        await ShotAtScaleAsync("依赖搜索无结果", 1.0);
        Click(Descendants<Button>(view.DeskContentControl).Single(b => Equals(b.Content, "清空搜索")));
        Check(view.SearchBoxControl.Text.Length == 0 && !AllText(view.DeskContentControl).Contains("没有匹配的依赖关系"),
            "清空搜索恢复当前异常关系列表");
        view.ApplySnapshot(depSnapshot with { DependencyGraph = graph with { Edges = graph.Edges.Where(e => !e.IsIssue).ToArray(), ActiveIssueCount = 0 } });
        await Idle();
        Check(AllText(view.DeskContentControl).Contains("当前没有依赖异常关系"), "仅有正常依赖时提供查看全部关系入口");
        Click(Descendants<Button>(view.DeskContentControl).Single(b => Equals(b.Content, "查看全部关系")));
        Check(!AllText(view.DeskContentControl).Contains("当前没有依赖异常关系"), "查看全部关系显示正常依赖");
        view.ApplySnapshot(depSnapshot with { DependencyGraph = graph with { Edges = [], ActiveIssueCount = 0 } });
        await Idle();
        Check(AllText(view.DeskContentControl).Contains("清单中暂无依赖关系"), "空图谱区分未声明依赖与搜索无匹配");
        view.ApplySnapshot(depSnapshot);

        // --------------------------------------------------------------------
        // STEP 7: Patches Page (补丁) — Tiers, Side-by-Side Badges, Organize, Orphan Auto-Stop & Stale Health Withdrawal
        // --------------------------------------------------------------------
        await RefreshNeonFullScanAndHealthAsync(includeLifecycle: true);
        await Idle();
        view.ClearToasts();
        view.SwitchTab("patches");
        await Idle();
        Check(view.ActiveTab == "patches", "成功切换到「补丁」页面");

        string patchesText = string.Join(" ", Descendants<TextBlock>(view.SecondaryTabHostControl).Select(t => t.Text));
        Check(patchesText.Contains("修复补丁") && patchesText.Contains("需要你处理") &&
              patchesText.Contains("可安全退役") && patchesText.Contains("SHA-256 精确匹配上游修复") &&
              patchesText.Contains("独立补丁") && patchesText.Contains("原 MOD 内置修复"),
            "补丁页按优先级分层展示：需要你处理 + 可安全退役（SHA-256 匹配）+ 补丁状态与类型并排");

        // Expand Tier 3 to verify IgnisTurnSyncFix shows "校验通过" + "独立补丁"
        var expandReadyBtn = Descendants<Button>(view.SecondaryTabHostControl)
            .Single(b => (AutomationProperties.GetName(b) ?? "").Contains("展开查看已就绪"));
        Click(expandReadyBtn);
        await Idle();
        string patchesExpandedText = string.Join(" ", Descendants<TextBlock>(view.SecondaryTabHostControl).Select(t => t.Text));
        Check(patchesExpandedText.Contains("校验通过") && patchesExpandedText.Contains("散落 mods/ 根目录"),
            "补丁页展开已就绪层后显示「校验通过」与「散落 mods/ 根目录」标注");
        // Collapse back to default calm state for comparison shot against HTML original
        Click(Descendants<Button>(view.SecondaryTabHostControl)
            .Single(b => (AutomationProperties.GetName(b) ?? "").Contains("收起已就绪")));
        await Idle();

        await ShotAtScaleAsync("13-wpf-patches-100", 1.0, 1280, 850);
        await ShotAtScaleAsync("14-wpf-patches-125", 1.25, 1280, 850);

        // Verify Organize Loose Standalone Patch uses real PatchOrganizer.PreviewFixture + cancel leaves disk untouched + confirm moves it!
        var orgBtn = Descendants<Button>(view.SecondaryTabHostControl)
            .Single(b => (AutomationProperties.GetName(b) ?? "").Contains("整理散落独立补丁 (1)"));
        Click(orgBtn);
        for (int i = 0; i < 100 && (!view.IsModalOpen || busy); i++) await Task.Delay(30);
        await Idle();
        Check(view.IsModalOpen, "点击「整理散落独立补丁」通过真实 PatchOrganizer.Preview 弹出预览弹窗");
        Click(Descendants<Button>(view.ModalBackdropControl).Single(b => Equals(b.Content, "取消")));
        await Idle();
        Check(Directory.Exists(Path.Combine(fixture, "mods", "NymphKazdelRewardFix")),
            "取消整理后散落补丁仍在原位，未修改磁盘");

        // Toggle OrphanPatchAutoStop checkbox and verify persistence via OrphanPatchAutoStop.Enabled
        bool beforeOrphan = OrphanPatchAutoStop.Enabled(NeonDataRoot);
        var orphanCb = Descendants<CheckBox>(view.SecondaryTabHostControl)
            .Single(c => (AutomationProperties.GetName(c) ?? "").Contains("自动停用孤立补丁"));
        orphanCb.IsChecked = !beforeOrphan;
        await Idle();
        Check(OrphanPatchAutoStop.Enabled(NeonDataRoot) == !beforeOrphan,
            "孤立补丁自动停用开关走真实 OrphanPatchAutoStop 持久化逻辑");
        orphanCb = Descendants<CheckBox>(view.SecondaryTabHostControl)
            .Single(c => (AutomationProperties.GetName(c) ?? "").Contains("自动停用孤立补丁"));
        orphanCb.IsChecked = beforeOrphan;
        await Idle();

        // Confirm organizing loose patch actually moves NymphKazdelRewardFix into mods/_SpirePatch_Fixes/
        orgBtn = Descendants<Button>(view.SecondaryTabHostControl)
            .Single(b => (AutomationProperties.GetName(b) ?? "").Contains("整理散落独立补丁 (1)"));
        Click(orgBtn);
        for (int i = 0; i < 100 && (!view.IsModalOpen || busy); i++) await Task.Delay(30);
        await Idle();
        Click(Descendants<Button>(view.ModalBackdropControl).Single(b => (b.Content?.ToString() ?? "").Contains("确认整理这 1 项")));
        for (int i = 0; i < 160 && (busy || transactionWriting || view.IsModalOpen || modInventoryRefreshRunning); i++) await Task.Delay(50);
        await Idle();
        Check(Directory.Exists(Path.Combine(fixture, "mods", "_SpirePatch_Fixes", "NymphKazdelRewardFix")) &&
              !Directory.Exists(Path.Combine(fixture, "mods", "NymphKazdelRewardFix")),
            "确认整理后通过正式 PatchOrganizer.Apply 将散落独立补丁原子移入 mods/_SpirePatch_Fixes 并迁移历史回滚记录");

        // Verify stale health on Patches page immediately withdraws Tier 2 ("可安全退役") & "校验通过"
        string patchesStaleText = string.Join(" ", Descendants<TextBlock>(view.SecondaryTabHostControl).Select(t => t.Text));
        Check(!patchesStaleText.Contains("校验通过") && !patchesStaleText.Contains("可安全退役"),
            "整理导致健康结论过期后，补丁页当场撤下「校验通过」与「可安全退役」分层");

        // Re-verify health cleanly
        await RefreshNeonFullScanAndHealthAsync(includeLifecycle: true);
        await Idle();

        // --------------------------------------------------------------------
        // STEP 8: Acquire & Updates Page (获取与更新) — Version Diff, Workshop Validation, Safe Local ZIP Import
        // --------------------------------------------------------------------
        view.ClearToasts();
        view.SwitchTab("acquire");
        await Idle();
        Check(view.ActiveTab == "acquire", "成功切换到「获取与更新」页面");

        string acqText = string.Join(" ", Descendants<TextBlock>(view.SecondaryTabHostControl).Select(t => t.Text));
        Check(acqText.Contains("获取到") || acqText.Contains("检测到可用更新") &&
              acqText.Contains("BaseLib") && acqText.Contains("海克斯大乱斗") &&
              acqText.Contains("来源待确认 / 未绑定来源"),
            "获取与更新页完整呈现 GitHub/工坊可用更新与未绑来源列表");

        await ShotAtScaleAsync("15-wpf-acquire-100", 1.0, 1280, 850);
        await ShotAtScaleAsync("16-wpf-acquire-125", 1.25, 1280, 850);

        // Verify invalid Workshop link rejected inline by real WorkshopModInstaller.TryParseWorkshopId
        Check(view.WorkshopUrlBoxControl != null, "创意工坊链接输入框已创建并持久化");
        view.WorkshopUrlBoxControl!.Text = "https://example.com/bad-link";
        await Idle();
        var fetchWsBtn = Descendants<Button>(view.SecondaryTabHostControl)
            .Single(b => (AutomationProperties.GetName(b) ?? "").Contains("获取创意工坊预览"));
        Click(fetchWsBtn);
        await Idle();
        string acqAfterBadWs = string.Join(" ", Descendants<TextBlock>(view.SecondaryTabHostControl).Select(t => t.Text));
        Check(acqAfterBadWs.Contains("ID 或链接无效"),
            "非法创意工坊链接通过正式 WorkshopModInstaller.TryParseWorkshopId 当场拦截提示");

        // Verify background refresh preserves WorkshopUrlBox text without wiping user input!
        view.WorkshopUrlBoxControl!.Text = "3748937859";
        await RefreshModsAsync();
        await Idle();
        Check(view.WorkshopUrlBoxControl!.Text == "3748937859",
            "后台刷新时「获取与更新」页的输入框内容保持不动");

        // Verify dangerous local ZIP (contains .bat script) is blocked by real WorkshopZipImport.Preview
        string badZip = Path.Combine(NeonDataRoot, "UnsafeScriptTest.zip");
        using (var z = ZipFile.Open(badZip, ZipArchiveMode.Create))
        {
            var e1 = z.CreateEntry("EvilMod/EvilMod.json");
            using (var w = new StreamWriter(e1.Open())) w.Write("""{"id":"EvilMod","name":"Evil","version":"1.0.0","has_dll":true}""");
            var e2 = z.CreateEntry("EvilMod/EvilMod.dll");
            using (var w = new StreamWriter(e2.Open())) w.Write("dll");
            var e3 = z.CreateEntry("EvilMod/run.bat");
            using (var w = new StreamWriter(e3.Open())) w.Write("echo bad");
        }
        await NeonPreviewLocalZipImportAsync(badZip);
        await Idle();
        string badZipModalText = AllText(view.ModalBackdropControl);
        Check(view.IsModalOpen && (badZipModalText.Contains("禁止的可执行文件") || badZipModalText.Contains("脚本")),
            "危险本地 ZIP（含 .bat 脚本）通过正式 LocalModZipImportWorkflowService 拦截并弹出原因");
        Click(Descendants<Button>(view.ModalBackdropControl).Single(b => Equals(b.Content, "关闭")));
        await Idle();

        // Verify valid single-MOD ZIP passes real LocalZipUiWorkflowSession preview + OnCancel cleanup + Apply (+ auto-creates protection snapshot!)
        int snapsBeforeZip = ModEnvironmentSnapshots.List(fixture, NeonDataRoot).Length;
        string goodZip = Path.Combine(NeonDataRoot, "RemiliaFixture-v1.0.3.zip");
        using (var z = ZipFile.Open(goodZip, ZipArchiveMode.Create))
        {
            var e1 = z.CreateEntry("RemiliaFixture/mod_manifest.json");
            using (var w = new StreamWriter(e1.Open())) w.Write("""{"id":"RemiliaFixture","name":"蕾米莉亚 · 红魔馆主","version":"1.0.3","author":"ScarletDev","has_dll":true,"dependencies":[{"id":"BaseLib","min_version":"3.4.0"}]}""");
            var e2 = z.CreateEntry("RemiliaFixture/RemiliaFixture.dll");
            using (var w = new StreamWriter(e2.Open())) w.Write("RemiliaFixture@1.0.3 payload");
        }
        await NeonPreviewLocalZipImportAsync(goodZip);
        await Idle();
        Check(view.IsModalOpen && NeonCurrentLocalZipPreview != null && NeonCurrentLocalZipPreview.Package.ModId == "RemiliaFixture",
            "合规本地 ZIP 通过正式 LocalZipUiWorkflowSession 生成隔离暂存预览与 SHA-256 弹窗");

        // Cancel first to verify OnCancel cleans up staging cleanly, then re-preview and confirm!
        Click(Descendants<Button>(view.ModalBackdropControl).Single(b => Equals(b.Content, "取消")));
        await Task.Delay(260);
        await Idle();
        Check(!view.IsModalOpen && NeonCurrentLocalZipPreview == null && !Directory.Exists(Path.Combine(fixture, "mods", "RemiliaFixture")),
            "取消本地 ZIP 预览弹窗时通过 OnCancel 自动清理隔离暂存且不修改 mods 目录");

        await NeonPreviewLocalZipImportAsync(goodZip);
        await Idle();
        Click(Descendants<Button>(view.ModalBackdropControl).Single(b => (b.Content?.ToString() ?? "").Contains("确认导入")));
        for (int i = 0; i < 160 && (busy || transactionWriting || view.IsModalOpen || modInventoryRefreshRunning); i++) await Task.Delay(50);
        await Idle();
        Check(Directory.Exists(Path.Combine(fixture, "mods", "RemiliaFixture")),
            "确认导入后通过正式 LocalModZipApplyWorkflowService 将合规 ZIP 事务安装到 mods/");
        Check(ModEnvironmentSnapshots.List(fixture, NeonDataRoot).Length == snapsBeforeZip + 1,
            "导入外部 ZIP 前自动通过 ModEnvironmentSnapshotWorkflowService 生成完整保护快照");

        // --------------------------------------------------------------------
        // STEP 9: Co-op Page (联机) — Honest Scope, Diff Table, Export & Selectable Preview
        // --------------------------------------------------------------------
        await Task.Delay(350);
        for (int i = 0; i < 80 && (busy || transactionWriting || modInventoryRefreshRunning); i++) await Task.Delay(40);
        await RefreshNeonFullScanAndHealthAsync(includeLifecycle: true);
        await Idle();
        // Reload fixture peer snapshot after ZIP import cleared coop state
        string peerPath = WriteFixtureCoopPeerSnapshot(NeonDataRoot, report!, modInventory!);
        await NeonLoadCoopPeerPathAsync(peerPath, "队友「阿哲」（示例快照）");
        await Idle();
        view.ClearToasts();
        view.SwitchTab("coop");
        await Idle();
        Check(view.ActiveTab == "coop", "成功切换到「联机」页面");

        string coopText = AllText(view.SecondaryTabHostControl);
        Check(coopText.Contains("联机补丁核对") && coopText.Contains("核对边界声明") &&
              coopText.Contains("队友「阿哲」") && coopText.Contains("本机多出"),
            "联机页呈现真实边界声明、双机快照卡片与差异对照表（含可勾选与不可勾选行）");

        await ShotAtScaleAsync("17-wpf-coop-100", 1.0, 1280, 850);
        await ShotAtScaleAsync("18-wpf-coop-125", 1.25, 1280, 850);

        // Verify selecting a selectable row updates "预览一键处理 (1)" button cleanly (default unchecked!)
        var previewCoopBtn = Descendants<Button>(view.SecondaryTabHostControl)
            .Single(b => (AutomationProperties.GetName(b) ?? "").Contains("预览一键处理"));
        Check(!previewCoopBtn.IsEnabled, "联机差异表默认不勾选任何项，一键处理按钮初始禁用");
        var coopCbs = Descendants<CheckBox>(view.SecondaryTabHostControl).ToArray();
        Check(coopCbs.Length >= 1, $"联机差异表识别出 {coopCbs.Length} 项可勾选处理差异");
        coopCbs[0].IsChecked = true;
        await Idle();
        previewCoopBtn = Descendants<Button>(view.SecondaryTabHostControl)
            .Single(b => (AutomationProperties.GetName(b) ?? "").Contains("预览一键处理 (1)"));
        Check(previewCoopBtn.IsEnabled, "勾选可处理差异后「预览一键处理 (1)」变为可用");

        // Test live export manifest modal (real FreshReportAsync -> schema 2 JSON)
        var exportBtn = Descendants<Button>(view.SecondaryTabHostControl)
            .Single(b => (AutomationProperties.GetName(b) ?? "").Contains("导出我的清单"));
        Click(exportBtn);
        for (int i = 0; i < 120 && (!view.IsModalOpen || busy); i++) await Task.Delay(40);
        await Idle();
        var exportBoxes = Descendants<TextBox>(view.ModalBackdropControl).ToArray();
        Check(view.IsModalOpen && exportBoxes.Any(t => t.Text.Contains("\"schema\": 2") && t.Text.Contains("\"presenceEvidenceVersion\": 1")),
            "点击「导出我的清单」通过实时扫描生成 schema 2 联机 JSON 清单");
        Click(Descendants<Button>(view.ModalBackdropControl).Single(b => Equals(b.Content, "关闭")));
        await Idle();

        // --------------------------------------------------------------------
        // STEP 10: Recovery Page (恢复) — Real Snapshots Vault, Manual Snapshot Create, Restore Preview & Delete
        // --------------------------------------------------------------------
        view.ClearToasts();
        view.SwitchTab("recovery");
        await Idle();
        Check(view.ActiveTab == "recovery", "成功切换到「恢复」页面");

        string recText = string.Join(" ", Descendants<TextBlock>(view.SecondaryTabHostControl).Select(t => t.Text));
        Check(recText.Contains("快照与恢复") && recText.Contains("完整 MOD 环境快照") &&
              recText.Contains("补丁批次回滚（mod_fix_backups）") && recText.Contains("本次改动与操作轨迹") &&
              recText.Contains("停用保留区"),
            "恢复页完整呈现环境快照时间轴、补丁批次回滚、本次改动轨迹与停用保留区");

        await ShotAtScaleAsync("19-wpf-recovery-100", 1.0, 1280, 850);
        await ShotAtScaleAsync("20-wpf-recovery-125", 1.25, 1280, 850);

        // Create a real manual snapshot via modal -> verify count increments -> preview restore -> cancel -> delete -> verify count decrements!
        int snapCountBefore = ModEnvironmentSnapshots.List(fixture, NeonDataRoot).Length;
        var createSnapBtn = Descendants<Button>(view.SecondaryTabHostControl)
            .Single(b => (AutomationProperties.GetName(b) ?? "").Contains("创建手动环境快照"));
        Click(createSnapBtn);
        await Idle();
        Check(view.IsModalOpen, "点击「创建手动环境快照」打开备注确认弹窗");
        var purposeBox = Descendants<TextBox>(view.ModalBackdropControl)
            .Single(t => (AutomationProperties.GetName(t) ?? "").Contains("快照备注"));
        purposeBox.Text = "验收测试手动快照";
        Click(Descendants<Button>(view.ModalBackdropControl).Single(b => Equals(b.Content, "创建快照")));
        for (int i = 0; i < 160 && (busy || transactionWriting || view.IsModalOpen); i++) await Task.Delay(40);
        await Idle();
        Check(ModEnvironmentSnapshots.List(fixture, NeonDataRoot).Length == snapCountBefore + 1,
            "确认创建后通过正式 ModEnvironmentSnapshots.Create 生成真实完整环境快照");

        // Preview restore of initial snapshot (before RemiliaFixture was imported -> should show RemovedFiles > 0!)
        var previewRestoreBtn = Descendants<Button>(view.SecondaryTabHostControl)
            .Single(b => (AutomationProperties.GetName(b) ?? "").Contains("预览差异并恢复 初始隔离环境自动保护"));
        Click(previewRestoreBtn);
        for (int i = 0; i < 120 && (!view.IsModalOpen || busy); i++) await Task.Delay(40);
        await Idle();
        string restoreModalText = AllText(view.ModalBackdropControl);
        Check(view.IsModalOpen && restoreModalText.Contains("恢复前自动保护") && restoreModalText.Contains("当前移除"),
            "点击「预览差异并恢复」通过正式 ModEnvironmentSnapshots.PreviewRestore 展示真实文件差异与自动保护承诺");
        Click(Descendants<Button>(view.ModalBackdropControl).Single(b => Equals(b.Content, "取消")));
        await Idle();
        Check(Directory.Exists(Path.Combine(fixture, "mods", "RemiliaFixture")),
            "取消恢复预览后当前 mods 目录保持原状");

        // Delete the manual snapshot via real confirmation modal
        var delSnapBtn = Descendants<Button>(view.SecondaryTabHostControl)
            .Single(b => (AutomationProperties.GetName(b) ?? "").Contains("删除此快照 验收测试手动快照"));
        Click(delSnapBtn);
        await Idle();
        Click(Descendants<Button>(view.ModalBackdropControl).Single(b => Equals(b.Content, "确认删除此快照")));
        for (int i = 0; i < 120 && (busy || transactionWriting || view.IsModalOpen); i++) await Task.Delay(40);
        await Idle();
        Check(ModEnvironmentSnapshots.List(fixture, NeonDataRoot).Length == snapCountBefore,
            "删除快照通过正式 ModEnvironmentSnapshots.Delete 完成并同步刷新快照计数");

        // --------------------------------------------------------------------
        // STEP 11: Failure Boundary #2 — Apply Succeeds + Post-Scan Throws -> Half-Success ("已恢复启用，依赖状态待核验") + Rescan Recovery!
        // --------------------------------------------------------------------
        string halfParent = Path.Combine(parent, "half-success-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(halfParent);
        string halfFixture = await SetupNeonIsolatedFixtureAsync(halfParent);
        await Idle();
        view.SwitchTab("workbench");
        view.SearchBoxControl.Text = "莱伊";
        await Idle();
        view.SelectMod("Ray");
        await Idle();

        neonTestScanOverride = _ => throw new IOException("模拟恢复后清单重扫读取受阻");
        try
        {
            var halfRestoreBtn = Descendants<Button>(view.InspectorBodyControl)
                .Single(b => (AutomationProperties.GetName(b) ?? "").Contains("恢复启用 STS2-RitsuLib"));
            Click(halfRestoreBtn);
            for (int i = 0; i < 120 && (busy || transactionWriting); i++) await Task.Delay(40);
            await Idle();
        }
        finally
        {
            neonTestScanOverride = null;
        }

        Check(ModManager.Scan(halfFixture).Mods.Single(m => m.Id == "STS2-RitsuLib").Enabled,
            "半成功边界：Apply 已将 STS2-RitsuLib 真实移回 mods/ 目录");
        string halfInspectorText = AllText(view.InspectorBodyControl);
        Check(view.GetInlineToggleStatus("STS2-RitsuLib")?.Phase == NeonInlineTogglePhase.AppliedScanFailed &&
              halfInspectorText.Contains("已恢复启用，依赖状态待核验"),
            "半成功边界：文件已移回但重扫失败时，明确显示「已恢复启用，依赖状态待核验」而非谎报失败");
        await ShotAtScaleAsync("21-wpf-half-success-rescan-100", 1.0, 1280, 850);

        // Clicking inline "重新扫描" recovers cleanly!
        var inlineRescanBtn = Descendants<Button>(view.InspectorBodyControl)
            .Single(b => (AutomationProperties.GetName(b) ?? "").Contains("重新扫描核验依赖"));
        Click(inlineRescanBtn);
        for (int i = 0; i < 120 && (busy || transactionWriting || modInventoryRefreshRunning); i++) await Task.Delay(40);
        await Idle();
        Check(view.GetInlineToggleStatus("STS2-RitsuLib") == null &&
              view.CurrentSnapshot!.AllRows.Single(r => r.Mod.Id == "STS2-RitsuLib").Mod.Enabled &&
              !view.CurrentSnapshot!.AllRows.Single(r => r.Mod.Id == "Ray").HasDanger,
            "半成功边界：点击原位「重新扫描」后自动补全核验并解除莱伊阻断");

        // --------------------------------------------------------------------
        // STEP 12: Step 2 Rollout — Inline Dependent Lock Pill (Zero-Modal Jump) + General Single-MOD Direct Disable/Enable
        // --------------------------------------------------------------------
        if (view.WorkbenchMode != "list")
        {
            Click(Descendants<Button>(view).Single(b => Equals(b.Content, "清单")));
            await Idle();
        }
        view.SearchBoxControl.Text = "";
        view.CloseInspector();
        await Task.Delay(180);
        await Idle();

        var baseLibRowVisual = view.DeskContentControl.Children.OfType<Border>()
            .Single(b => Equals(b.Tag, "BaseLib"));
        var baseLibLockPill = Descendants<Button>(baseLibRowVisual)
            .Single(b => (AutomationProperties.GetName(b) ?? "").Contains("正在使用"));
        Click(baseLibLockPill);
        await Idle();
        Check(!view.IsModalOpen && view.IsInspectorOpen && view.SelectedModId == "BaseLib",
            "被下游依赖锁定的 MOD 在行内直说「正在使用」，点击直接看依赖列表不弹无意义「知道了」弹窗");

        // Direct disable & re-enable on an independent normal MOD (intentgraph2)
        view.SearchBoxControl.Text = "敌人意图预测图谱 2";
        await Idle();
        var intentRow = view.DeskContentControl.Children.OfType<Border>()
            .Single(b => Equals(b.Tag, "intentgraph2"));
        var intentToggleDisable = Descendants<Button>(intentRow)
            .Single(b => (AutomationProperties.GetName(b) ?? "").Contains("停用 敌人意图预测图谱 2"));
        Click(intentToggleDisable);
        for (int i = 0; i < 120 && (busy || transactionWriting || modInventoryRefreshRunning); i++) await Task.Delay(40);
        await Idle();

        Check(!view.IsModalOpen && !ModManager.Scan(halfFixture).Mods.Single(m => m.Id == "intentgraph2").Enabled,
            "普通单 MOD 行内果冻开关直接停用生效（真实移入保留区且无重弹窗打断）");
        view.SelectMod("intentgraph2");
        await Idle();
        Check(AllText(view.InspectorBodyControl).Contains("已停用，文件完整保留在停用区"),
            "停用普通单 MOD 后右侧原位保留松绿结果回执");

        intentRow = view.DeskContentControl.Children.OfType<Border>()
            .Single(b => Equals(b.Tag, "intentgraph2"));
        var intentToggleEnable = Descendants<Button>(intentRow)
            .Single(b => (AutomationProperties.GetName(b) ?? "").Contains("启用 敌人意图预测图谱 2"));
        Click(intentToggleEnable);
        for (int i = 0; i < 120 && (busy || transactionWriting || modInventoryRefreshRunning); i++) await Task.Delay(40);
        await Idle();

        Check(!view.IsModalOpen && ModManager.Scan(halfFixture).Mods.Single(m => m.Id == "intentgraph2").Enabled &&
              AllText(view.InspectorBodyControl).Contains("已恢复启用"),
            "普通单 MOD 行内果冻开关再次点击直接恢复启用并更新原位回执");

        // --------------------------------------------------------------------
        // STEP 13: Native Neon Batch Toggle via ModToggleWorkflowService (Zero Legacy ModBatchDialogs!)
        // --------------------------------------------------------------------
        view.SearchBoxControl.Text = "";
        view.CloseInspector();
        await Task.Delay(180);
        await Idle();

        var batchTargetIds = new[] { "intentgraph2", "Act4FinalAscent" };
        foreach (string targetId in batchTargetIds)
        {
            var rowVisual = view.DeskContentControl.Children.OfType<Border>()
                .Single(b => Equals(b.Tag, targetId));
            var cb = Descendants<CheckBox>(rowVisual).First();
            cb.IsChecked = true;
            cb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Idle();
        }
        Check(view.IsBatchDockOpen, "勾选 2 个普通 MOD 后浮动批量操作栏弹出");

        var batchDisableBtn = Descendants<Button>(view.BatchDockControl)
            .Single(b => (b.Content?.ToString() ?? "") == "批量停用");
        Click(batchDisableBtn);
        for (int i = 0; i < 120 && (!view.IsModalOpen || busy); i++) await Task.Delay(40);
        await Idle();

        string batchModalText = AllText(view.ModalBackdropControl);
        Check(view.IsModalOpen && batchModalText.Contains("批量停用") && batchModalText.Contains("查看路径与校验细节"),
            "批量停用改为通过 ModToggleWorkflowService 打开原生 Neon 精简预览弹窗（不再弹旧版 ModBatchDialogs）");
        await ShotAtScaleAsync("22-wpf-native-batch-modal-100", 1.0, 1280, 850);

        var confirmBatchBtn = Descendants<Button>(view.ModalBackdropControl)
            .Single(b => (b.Content?.ToString() ?? "").Contains("确认批量停用"));
        Click(confirmBatchBtn);
        for (int i = 0; i < 160 && (busy || transactionWriting || view.IsModalOpen || modInventoryRefreshRunning); i++) await Task.Delay(50);
        await Idle();

        var postBatchMods = ModManager.Scan(halfFixture).Mods;
        Check(!postBatchMods.Single(m => m.Id == "intentgraph2").Enabled &&
              !postBatchMods.Single(m => m.Id == "Act4FinalAscent").Enabled &&
              !view.IsBatchDockOpen,
            "原生 Neon 批量停用通过 ModToggleWorkflowService 真实移入停用保留区并自动收起批量栏");

        // --- 14. Milestone 3 production drawer controls & native dialogs (Update / Network / Log Diagnosis) ---
        view.OpenToolsDrawer();
        await Task.Delay(300);
        await Idle();
        string drawerText = AllText(view.DrawerSheetHostControl);
        Check(view.IsDrawerOpen &&
              drawerText.Contains("管理器更新") &&
              drawerText.Contains("稳定版") &&
              drawerText.Contains("测试版 Beta") &&
              drawerText.Contains("检查更新") &&
              drawerText.Contains("清理旧版更新文件") &&
              drawerText.Contains("GitHub 网络与代理") &&
              drawerText.Contains("配置代理") &&
              drawerText.Contains("诊断 GitHub 连接"),
            "设置抽屉包含管理器更新（频道切换/检查/清理旧版）与 GitHub 网络代理及连接诊断入口");
        await ShotAtScaleAsync("23-wpf-tools-drawer-100", 1.0, 1280, 850);

        var cfgProxyBtn = Descendants<Button>(view.DrawerSheetHostControl)
            .Single(b => (b.Content?.ToString() ?? "") == "配置代理");
        Click(cfgProxyBtn);
        await Task.Delay(260);
        await Idle();
        string netModalText = AllText(view.ModalBackdropControl);
        Check(view.IsModalOpen &&
              netModalText.Contains("跟随 Windows 系统代理") &&
              netModalText.Contains("127.0.0.1"),
            "点击“配置代理”后在原生 Neon 弹窗中展示系统代理/本机 HTTP 代理设置");
        var cancelNetBtn = Descendants<Button>(view.ModalBackdropControl)
            .Single(b => (b.Content?.ToString() ?? "") == "取消");
        Click(cancelNetBtn);
        await Task.Delay(260);
        await Idle();
        Check(!view.IsModalOpen, "关闭 GitHub 代理设置弹窗未改动宿主机代理配置");

        string sampleLogPath = Path.Combine(NeonDataRoot, "sample-godot-diag.log");
        File.WriteAllText(sampleLogPath, "INFO Booting SlayTheSpire2\nSystem.OverflowException: Arithmetic operation resulted in an overflow.\n   at NHealNumVfx.Render()\n");
        await NeonDiagnoseLogFileAsync(sampleLogPath);
        await Idle();
        string diagModalText = AllText(view.ModalBackdropControl);
        Check(view.IsModalOpen && diagModalText.Contains("超大回血飘字溢出"),
            "godot.log 故障特征诊断改为通过原生 Neon 详情弹窗展示命中规则");
        await ShotAtScaleAsync("24-wpf-log-diagnosis-modal-100", 1.0, 1280, 850);
        var closeDiagBtn = Descendants<Button>(view.ModalBackdropControl)
            .Single(b => (b.Content?.ToString() ?? "") == "关闭");
        Click(closeDiagBtn);
        await Task.Delay(260);
        await Idle();

        CloseModWatch();
    }
}
