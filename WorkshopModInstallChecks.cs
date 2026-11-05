using System.IO;
using System.Text;
using System.Text.Json;

namespace SpirePatch;

public static class WorkshopModInstallChecks
{
    public static Task RunAsync(string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        var lines = new List<string>();
        void Check(bool valid, string description)
        {
            if (!valid)
                throw new InvalidOperationException(description);
            lines.Add("PASS " + description);
            File.WriteAllLines(output, lines, Encoding.UTF8);
        }

        const ulong workshopId = 3737335127;
        string root = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(output))!,
            "workshop-mod-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "SlayTheSpire2.exe"), "fixture");
        File.WriteAllText(Path.Combine(root, ".spirepatch-fixture"), "never launch");
        Directory.CreateDirectory(Path.Combine(root, "mods"));

        try
        {
            Check(WorkshopModInstaller.TryParseWorkshopId(workshopId.ToString(), out ulong numeric) &&
                numeric == workshopId,
                "numeric Workshop ID is accepted");
            Check(WorkshopModInstaller.TryParseWorkshopId(
                "https://steamcommunity.com/sharedfiles/filedetails/?id=3737335127&appid=2868840",
                out ulong steamLink) && steamLink == workshopId,
                "Steam Workshop share URL is parsed and AppID is checked");
            Check(WorkshopModInstaller.TryParseWorkshopId(
                "https://workshop.apricityx.top/mod/2868840/3737335127",
                out ulong supplyLink) && supplyLink == workshopId,
                "Apricityx Workshop URL is parsed");
            Check(!WorkshopModInstaller.TryParseWorkshopId(
                "http://steamcommunity.com/sharedfiles/filedetails/?id=3737335127", out _) &&
                !WorkshopModInstaller.TryParseWorkshopId(
                "https://steamcommunity.com/sharedfiles/filedetails/?id=3737335127&appid=123", out _) &&
                !WorkshopModInstaller.TryParseWorkshopId(
                "https://example.com/mod/2868840/3737335127", out _),
                "insecure, wrong-AppID and untrusted Workshop URLs are rejected");

            Check(WorkshopModInstaller.TryCompareVersions("v1.2.0", "1.1.9", out int versionOrder) &&
                versionOrder > 0 &&
                !WorkshopModInstaller.TryCompareVersions("nightly", "1.0.0", out _),
                "Workshop version comparison handles v-prefixed numeric versions and safely marks unknown formats");

            string assembledV1 = Path.Combine(root, "assembled-v1");
            CreateAssembled(assembledV1, "BaseLib", "BaseLib", "1.0.0", "old dll", "old pck");
            string workspaceV1 = Path.Combine(root, "workspace-v1");
            var preparedV1 = WorkshopModInstaller.PrepareAssembled(
                root, workshopId, "BaseLib", "1001", assembledV1, workspaceV1);
            Check(!preparedV1.IsUpdate && preparedV1.ModId == "BaseLib" &&
                preparedV1.FileCount == 3 && preparedV1.DestinationDirectory ==
                Path.Combine(root, "mods", "BaseLib"),
                "a single Workshop MOD manifest is recognized and previewed as a new install");

            var installedV1 = WorkshopModInstaller.Commit(root, preparedV1);
            Check(installedV1.Changed && installedV1.BackupFolder is null &&
                ModManager.Scan(root).Mods.Single().Version == "1.0.0" &&
                File.ReadAllText(Path.Combine(root, "mods", "BaseLib", "BaseLib.dll")) == "old dll",
                "new Workshop MOD is installed into its own mods folder");
            Check(!File.Exists(Path.Combine(root, "mods", "BaseLib", "assembly-manifest.json")),
                "the manager's assembly report is not copied into the installed MOD");
            WorkshopModInstaller.Cleanup(preparedV1);

            string sourceData = Path.Combine(root, "manager-data");
            var sources = new WorkshopModSources(sourceData, root);
            sources.Bind("BaseLib", workshopId);
            Check(sources.Get("BaseLib") == workshopId &&
                sources.ModIdFor(workshopId) == "BaseLib",
                "Workshop source binding is stored by MOD ID");
            try
            {
                sources.Bind("OtherMod", workshopId);
                throw new InvalidOperationException("duplicate Workshop source binding was accepted");
            }
            catch (InvalidOperationException error) when (error.Message.Contains("已关联", StringComparison.Ordinal))
            {
            }
            var reloaded = new WorkshopModSources(sourceData, root);
            Check(reloaded.Get("BaseLib") == workshopId,
                "Workshop source binding survives manager restart");

            string assembledV2 = Path.Combine(root, "assembled-v2");
            CreateAssembled(assembledV2, "BaseLib", "BaseLib", "1.1.0", "new dll", "new pck");
            string workspaceV2 = Path.Combine(root, "workspace-v2");
            var preparedV2 = WorkshopModInstaller.PrepareAssembled(
                root, workshopId, "BaseLib", "1002", assembledV2, workspaceV2);
            Check(preparedV2.IsUpdate && preparedV2.ExistingVersion == "1.0.0",
                "matching local MOD ID is detected as an update target");
            var installedV2 = WorkshopModInstaller.Commit(root, preparedV2);
            Check(installedV2.Changed && installedV2.BackupFolder is not null &&
                Directory.Exists(installedV2.BackupFolder) &&
                File.ReadAllText(Path.Combine(installedV2.BackupFolder, "BaseLib.dll")) == "old dll" &&
                ModManager.Scan(root).Mods.Single().Version == "1.1.0" &&
                File.ReadAllText(Path.Combine(root, "mods", "BaseLib", "BaseLib.dll")) == "new dll",
                "Workshop update backs up the old MOD and installs the verified new payload");
            WorkshopModInstaller.Cleanup(preparedV2);

            ManagedMod current = ModManager.Scan(root).Mods.Single();
            string backupDll = Path.Combine(installedV2.BackupFolder!, "BaseLib.dll");
            File.Delete(backupDll);
            try
            {
                GitHubModInstaller.Restore(root, current, installedV2.BackupFolder!);
                throw new InvalidOperationException("a backup missing its required DLL was restored");
            }
            catch (InvalidDataException error) when (error.Message.Contains("缺少", StringComparison.Ordinal))
            {
            }
            Check(ModManager.Scan(root).Mods.Single().Version == "1.1.0" &&
                File.ReadAllText(Path.Combine(root, "mods", "BaseLib", "BaseLib.dll")) == "new dll",
                "an incomplete backup is rejected without touching the current MOD");
            File.WriteAllText(backupDll, "old dll");

            string restored = GitHubModInstaller.Restore(root, current, installedV2.BackupFolder!);
            Check(ModManager.Scan(root).Mods.Single().Version == "1.0.0" &&
                File.ReadAllText(Path.Combine(root, "mods", "BaseLib", "BaseLib.dll")) == "old dll" &&
                Directory.Exists(restored),
                "the existing MOD backup restore workflow also restores Workshop manifests with custom JSON names");

            string assembledRollback = Path.Combine(root, "assembled-rollback");
            CreateAssembled(assembledRollback, "BaseLib", "BaseLib", "1.1.0", "failed dll", "failed pck");
            int backupCountBeforeRollback = GitHubModInstaller.Backups(root, current).Length;
            var preparedRollback = WorkshopModInstaller.PrepareAssembled(
                root, workshopId, "BaseLib", "1005",
                assembledRollback, Path.Combine(root, "workspace-rollback"));
            try
            {
                WorkshopModInstaller.Commit(root, preparedRollback,
                    () => throw new IOException("simulated payload move failure"));
                throw new InvalidOperationException("simulated update failure was not observed");
            }
            catch (IOException error) when (error.Message.Contains("已自动恢复旧版本", StringComparison.Ordinal))
            {
            }
            Check(ModManager.Scan(root).Mods.Single().Version == "1.0.0" &&
                File.ReadAllText(Path.Combine(root, "mods", "BaseLib", "BaseLib.dll")) == "old dll" &&
                GitHubModInstaller.Backups(root, ModManager.Scan(root).Mods.Single()).Length == backupCountBeforeRollback,
                "a failed Workshop replacement restores the original MOD and does not add a partial backup");
            WorkshopModInstaller.Cleanup(preparedRollback);

            string assembledDowngrade = Path.Combine(root, "assembled-downgrade");
            CreateAssembled(assembledDowngrade, "BaseLib", "BaseLib", "0.9.0", "old dll", "old pck");
            try
            {
                WorkshopModInstaller.PrepareAssembled(
                    root, workshopId, "BaseLib", "1006",
                    assembledDowngrade, Path.Combine(root, "workspace-downgrade"));
                throw new InvalidOperationException("a lower Workshop MOD version was accepted");
            }
            catch (InvalidOperationException error) when (error.Message.Contains("低于本地版本", StringComparison.Ordinal))
            {
            }
            Check(ModManager.Scan(root).Mods.Single().Version == "1.0.0",
                "a comparable older Workshop version is rejected before installation");

            string assembledSame = Path.Combine(root, "assembled-same");
            CreateAssembled(assembledSame, "BaseLib", "BaseLib", "1.0.0", "old dll", "old pck");
            var preparedSame = WorkshopModInstaller.PrepareAssembled(
                root, workshopId, "BaseLib", "1001", assembledSame, Path.Combine(root, "workspace-same"));
            Check(preparedSame.IsUnchanged,
                "identical Workshop payload is recognized without replacing local files");
            var unchanged = WorkshopModInstaller.Commit(root, preparedSame);
            Check(!unchanged.Changed && unchanged.BackupFolder is null,
                "identical Workshop payload creates no redundant backup");
            WorkshopModInstaller.Cleanup(preparedSame);

            string assembledV3 = Path.Combine(root, "assembled-v3");
            CreateAssembled(assembledV3, "BaseLib", "BaseLib", "1.2.0", "future dll", "future pck");
            var preparedV3 = WorkshopModInstaller.PrepareAssembled(
                root, workshopId, "BaseLib", "1003", assembledV3, Path.Combine(root, "workspace-v3"));
            File.AppendAllText(Path.Combine(root, "mods", "BaseLib", "BaseLib.dll"), " local edit");
            try
            {
                WorkshopModInstaller.Commit(root, preparedV3);
                throw new InvalidOperationException("changed local inventory was overwritten");
            }
            catch (InvalidOperationException error) when (error.Message.Contains("环境发生变化", StringComparison.Ordinal))
            {
            }
            Check(File.ReadAllText(Path.Combine(root, "mods", "BaseLib", "BaseLib.dll")) == "old dll local edit",
                "local MOD changes made during download prevent the update");
            WorkshopModInstaller.Cleanup(preparedV3);

            string collisionRoot = Path.Combine(root, "collision-fixture");
            Directory.CreateDirectory(collisionRoot);
            File.WriteAllText(Path.Combine(collisionRoot, "SlayTheSpire2.exe"), "fixture");
            Directory.CreateDirectory(Path.Combine(collisionRoot, "mods", "BaseLib"));
            File.WriteAllText(Path.Combine(collisionRoot, "mods", "BaseLib", "unmanaged.txt"), "keep");
            string collisionAssembly = Path.Combine(root, "assembled-collision");
            CreateAssembled(collisionAssembly, "BaseLib", "BaseLib", "1.0.0", "dll", "pck");
            try
            {
                WorkshopModInstaller.PrepareAssembled(
                    collisionRoot, workshopId, "BaseLib", "1001",
                    collisionAssembly, Path.Combine(root, "workspace-collision"));
                throw new InvalidOperationException("folder collision was accepted");
            }
            catch (IOException error) when (error.Message.Contains("已存在", StringComparison.Ordinal))
            {
            }
            Check(File.ReadAllText(Path.Combine(collisionRoot, "mods", "BaseLib", "unmanaged.txt")) == "keep",
                "a folder collision with unrelated files is rejected without overwriting them");

            string multiAssembly = Path.Combine(root, "assembled-multi");
            Directory.CreateDirectory(Path.Combine(multiAssembly, "One"));
            Directory.CreateDirectory(Path.Combine(multiAssembly, "Two"));
            WriteManifest(Path.Combine(multiAssembly, "One", "one.json"), "One", "1.0.0");
            File.WriteAllText(Path.Combine(multiAssembly, "One", "one.dll"), "one");
            WriteManifest(Path.Combine(multiAssembly, "Two", "two.json"), "Two", "1.0.0");
            File.WriteAllText(Path.Combine(multiAssembly, "Two", "two.dll"), "two");
            try
            {
                WorkshopModInstaller.PrepareAssembled(
                    root, workshopId, "Multi", "1004",
                    multiAssembly, Path.Combine(root, "workspace-multi"));
                throw new InvalidOperationException("multi-MOD bundle was accepted");
            }
            catch (InvalidDataException error) when (error.Message.Contains("多个 MOD 清单", StringComparison.Ordinal))
            {
            }
            Check(true, "multi-MOD bundles are rejected rather than installed as one MOD");

            File.WriteAllLines(output, lines, Encoding.UTF8);
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return Task.CompletedTask;
    }

    private static void CreateAssembled(
        string root,
        string folder,
        string id,
        string version,
        string dll,
        string pck)
    {
        string payload = Path.Combine(root, folder);
        Directory.CreateDirectory(payload);
        WriteManifest(Path.Combine(payload, folder + ".json"), id, version);
        File.WriteAllText(Path.Combine(payload, folder + ".dll"), dll);
        File.WriteAllText(Path.Combine(payload, folder + ".pck"), pck);
        File.WriteAllText(Path.Combine(root, "assembly-manifest.json"), "{}");
    }

    private static void WriteManifest(string path, string id, string version) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            id,
            name = id,
            version,
            has_dll = true,
            has_pck = true
        }));
}
