using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SpirePatch;

/// <summary>
/// Self-contained fixture ZIPs and fixture game roots only. No network, no real
/// game discovery and no source data mutation are permitted by these checks.
/// </summary>
internal static class LocalModZipImportWorkflowChecks
{
    const string ValidManifest =
        """{"id":"fixture.mod","name":"Fixture","version":"1.2.3","author":"Tester","description":"safe","has_dll":true,"dependencies":[{"id":"base.mod","min_version":"1.0","optional":false}]}""";

    sealed record ZipEntry(string Path, string Body, int Attributes = 0);

    static string CreateZip(string parent, params ZipEntry[] entries)
    {
        string zip = Path.Combine(parent, Guid.NewGuid().ToString("N") + ".zip");
        using var output = File.Create(zip);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(item.Path, CompressionLevel.Optimal);
            entry.ExternalAttributes = item.Attributes;
            if (!item.Path.EndsWith("/", StringComparison.Ordinal))
            {
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(item.Body);
            }
        }
        return zip;
    }

    static string Fixture(string parent)
    {
        string root = Path.Combine(parent, "game-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "mods"));
        File.WriteAllText(Path.Combine(root, "SlayTheSpire2.exe"), "fixture-not-a-game");
        return root;
    }

    static LocalModZipImportWorkflowService Workflow(string parent,
        string? dataRoot = null, Func<string, bool>? running = null)
    {
        string stage = Path.Combine(parent, "staging-" + Guid.NewGuid().ToString("N"));
        return new(dataRoot ?? Path.Combine(parent, "data"), stagingRoot: stage,
            gameRunning: running ?? (_ => false));
    }

    static ZipEntry[] Valid(string prefix = "Sample/") =>
        [new(prefix + "mod_manifest.json", ValidManifest),
         new(prefix + "sample.dll", "dll-fixture"),
         new(prefix + "resources/asset.bin", "content")];

    static string ReplaceManifest(string text, string parent) =>
        CreateZip(parent, new ZipEntry("Sample/mod_manifest.json", text),
            new ZipEntry("Sample/sample.dll", "dll-fixture"));

    static Dictionary<string, string> SnapshotFiles(string root)
    {
        if (!Directory.Exists(root)) return new(StringComparer.OrdinalIgnoreCase);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            using var stream = File.OpenRead(path);
            hashes[Path.GetRelativePath(root, path)] =
                Convert.ToHexString(SHA256.HashData(stream));
        }
        return hashes;
    }

    static bool SameFiles(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out string? hash) && hash == pair.Value);

    public static async Task RunAsync(string home, Action<bool, string> check)
    {
        string parent = Path.Combine(home, "local-zip-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        string root = Fixture(parent);
        var service = Workflow(parent);
        string zip = CreateZip(parent, Valid("Sample/"));
        var ready = await service.PreviewAsync(root, zip);
        check(ready.State == LocalZipPreviewState.Ready && ready.Preview is not null &&
              ready.Preview.SourceKind == LocalZipSourceKind.UnverifiedLocalZip &&
              ready.Preview.SourceTrust == "Unverified" &&
              ready.Preview.Package.ModId == "fixture.mod" &&
              ready.Preview.Package.Version == "1.2.3" &&
              ready.Preview.Package.Author == "Tester" &&
              ready.Preview.Package.Description == "safe" &&
              ready.Preview.Package.Dependencies.Count == 1 &&
              ready.Preview.Package.Files.Count == 3 &&
              ready.Preview.Package.Files.All(f => f.Sha256.Length == 64) &&
              ready.Preview.Package.DestinationFolderName == "Sample" &&
              ready.Preview.Package.FolderNameReason.Contains("包装目录") &&
              ready.Preview.Package.ArchiveSha256.Length == 64,
              "local ZIP isolated preview contains full immutable metadata, SHA-256 files and unverified source");
        if (ready.Preview is null) throw new InvalidOperationException("Fixture must preview.");
        check((await service.VerifyAsync(ready.Preview)).IsCurrent &&
              !Directory.Exists(ready.Preview.DestinationDirectory),
              "preview integrity verification performs no MOD installation");
        string workplace = ready.Preview.Package.WorkspaceDirectory;
        service.Discard(ready.Preview);
        service.Discard(ready.Preview);
        check(!Directory.Exists(workplace), "Discard is idempotent and deletes only owned staging");
        bool blockedDelete = false;
        try { service.Discard(Path.Combine(parent, "game-example")); }
        catch (InvalidOperationException) { blockedDelete = true; }
        check(blockedDelete, "Discard refuses unrelated paths");

        string rootManifest = CreateZip(parent, new ZipEntry("mod_manifest.json", ValidManifest),
            new ZipEntry("sample.dll", "dll"));
        var direct = await service.PreviewAsync(root, rootManifest);
        check(direct.Preview?.Package.DestinationFolderName == "fixture.mod" &&
              direct.Preview.Package.PackageRootPath == "." &&
              direct.State == LocalZipPreviewState.Ready,
              "root-level MOD manifest selects ID as destination folder");
        service.Discard(direct.Preview);

        var deep = await service.PreviewAsync(root, CreateZip(parent, Valid("outer/inner/Sample/")));
        check(deep.Preview?.Package.DestinationFolderName == "Sample" &&
              deep.Preview.Package.PackageRootPath == "outer/inner/Sample",
              "multiple wrapper directories are peeled to manifest parent");
        service.Discard(deep.Preview);

        async Task Reject(string label, params ZipEntry[] entries)
        {
            var result = await service.PreviewAsync(root, CreateZip(parent, entries));
            check(result.State == LocalZipPreviewState.Failed && result.Preview is null,
                label);
        }

        await Reject("reject unrelated ZIP root files",
            new("Sample/mod_manifest.json", ValidManifest),
            new("Sample/sample.dll", "dll"), new("unrelated.txt", "extra"));
        await Reject("reject missing MOD manifest", new ZipEntry("Sample/sample.dll", "dll"));
        await Reject("reject multiple manifests",
            new("A/mod_manifest.json", ValidManifest), new("A/a.dll", "x"),
            new("B/manifest.json", ValidManifest), new("B/b.dll", "x"));
        await Reject("reject malformed manifest JSON",
            new("Sample/mod_manifest.json", "{broken"),
            new("Sample/sample.dll", "x"));
        await Reject("reject duplicate JSON key",
            new("Sample/mod_manifest.json", ValidManifest.Replace("\"name\":\"Fixture\",",
                "\"name\":\"Fixture\",\"name\":\"Duplicate\",")),
            new("Sample/sample.dll", "x"));
        await Reject("reject case-insensitive duplicate JSON key",
            new("Sample/mod_manifest.json", ValidManifest.Replace("\"name\":\"Fixture\",",
                "\"name\":\"Fixture\",\"Name\":\"Duplicate\",")),
            new("Sample/sample.dll", "x"));
        await Reject("reject missing id", new("Sample/mod_manifest.json",
            ValidManifest.Replace("\"id\":\"fixture.mod\",", "")), new("Sample/sample.dll", "x"));
        await Reject("reject missing version", new("Sample/mod_manifest.json",
            ValidManifest.Replace("\"version\":\"1.2.3\",", "")), new("Sample/sample.dll", "x"));
        await Reject("reject invalid dependency object", new("Sample/mod_manifest.json",
            ValidManifest.Replace("\"id\":\"base.mod\"", "\"id\":42")), new("Sample/sample.dll", "x"));
        await Reject("reject invalid has_dll type",
            new("Sample/mod_manifest.json", ValidManifest.Replace("\"has_dll\":true", "\"has_dll\":\"true\"")),
            new("Sample/sample.dll", "x"));
        await Reject("reject missing declared DLL",
            new("Sample/mod_manifest.json", ValidManifest), new("Sample/data.pck", "pck"));
        await Reject("reject missing declared PCK",
            new("Sample/mod_manifest.json",
                ValidManifest.Replace("\"has_dll\":true", "\"has_pck\":true")),
            new("Sample/sample.dll", "dll"));
        await Reject("reject script executable payload",
            new("Sample/mod_manifest.json", ValidManifest), new("Sample/sample.dll", "dll"),
            new("Sample/start.ps1", "write-host dangerous"));

        check(LocalModZipArchiveBackend.WithinLimits(256L * 1024 * 1024,
                512L * 1024 * 1024, 256L * 1024 * 1024, 2000, 128 * 1024) &&
              !LocalModZipArchiveBackend.WithinLimits(256L * 1024 * 1024 + 1,
                100, 100, 1, 1) &&
              !LocalModZipArchiveBackend.WithinLimits(100, 512L * 1024 * 1024 + 1,
                100, 1, 1) &&
              !LocalModZipArchiveBackend.WithinLimits(100, 100,
                256L * 1024 * 1024 + 1, 1, 1) &&
              !LocalModZipArchiveBackend.WithinLimits(100, 100, 100, 2001, 1) &&
              !LocalModZipArchiveBackend.WithinLimits(100, 100, 100, 1, 128 * 1024 + 1),
              "local ZIP archive/expanded/single/entry/manifest bounds enforce 256/512/256 MiB, 2000 and 128 KiB");
        check(LocalModZipArchiveBackend.AcceptCompression(1000, 1) &&
              !LocalModZipArchiveBackend.AcceptCompression(1001, 1) &&
              !LocalModZipArchiveBackend.AcceptCompression(1, 0) &&
              LocalModZipArchiveBackend.AcceptCompression(0, 0),
              "compression ratio uses floating-point comparison with exact 1000 boundary and rejects non-empty zero compressed length");

        await Reject("reject ../ traversal", new("../escape.txt", "bad"),
            new("Sample/mod_manifest.json", ValidManifest), new("Sample/sample.dll", "dll"));
        await Reject("reject backslash traversal", new(@"..\escape.txt", "bad"),
            new("Sample/mod_manifest.json", ValidManifest), new("Sample/sample.dll", "dll"));
        await Reject("reject drive-root paths", new("C:/escape.txt", "bad"),
            new("Sample/mod_manifest.json", ValidManifest), new("Sample/sample.dll", "dll"));
        await Reject("reject absolute paths", new("/tmp/escape.txt", "bad"),
            new("Sample/mod_manifest.json", ValidManifest), new("Sample/sample.dll", "dll"));
        await Reject("reject UNC paths", new(@"\\server\share\bad.txt", "bad"),
            new("Sample/mod_manifest.json", ValidManifest), new("Sample/sample.dll", "dll"));
        await Reject("reject reserved Windows names", new("CON/f.txt", "bad"),
            new("Sample/mod_manifest.json", ValidManifest), new("Sample/sample.dll", "dll"));
        await Reject("reject deep paths", new(string.Join("/", Enumerable.Repeat("folder", 17)) + "/x.txt", "bad"),
            new("Sample/mod_manifest.json", ValidManifest), new("Sample/sample.dll", "dll"));
        await Reject("reject case-insensitive duplicates",
            new("Sample/mod_manifest.json", ValidManifest), new("Sample/sample.dll", "x"),
            new("sample/SAMPLE.dll", "y"));
        await Reject("reject file parent child conflict",
            new("Sample/mod_manifest.json", ValidManifest), new("Sample/sample.dll", "dll"),
            new("Sample/parent", "x"), new("Sample/parent/child", "y"));
        await Reject("reject file and directory same path",
            new("Sample/mod_manifest.json", ValidManifest), new("Sample/sample.dll", "dll"),
            new("Sample/hello/", ""), new("Sample/hello", "x"));
        await Reject("reject Unix symlink",
            new("Sample/mod_manifest.json", ValidManifest), new("Sample/sample.dll", "dll"),
            new("Sample/link", "x", 0xA000 << 16));
        await Reject("reject Unix special type",
            new("Sample/mod_manifest.json", ValidManifest), new("Sample/sample.dll", "dll"),
            new("Sample/fifo", "x", 0x1000 << 16));
        await Reject("reject Windows reparse point",
            new("Sample/mod_manifest.json", ValidManifest), new("Sample/sample.dll", "dll"),
            new("Sample/reparse", "x", (int)FileAttributes.ReparsePoint));

        // Physical ZIPs with many small headers exercise the entry-count limit.
        var entryOverflow = Enumerable.Range(0, 2001).Select(i => new ZipEntry(
            "Sample/" + i + ".bin", "")).Prepend(new ZipEntry(
            "Sample/mod_manifest.json", ValidManifest)).ToArray();
        await Reject("reject more than 2000 actual ZIP entries", entryOverflow);
        string hugeManifest = new string('x', 128 * 1024 + 1);
        await Reject("reject oversized real ZIP manifest",
            new("Sample/mod_manifest.json", hugeManifest), new("Sample/sample.dll", "dll"));

        // Conflicts are diagnostics: the valid package is still staged for inspection,
        // but CanInstallNew is false, and Discard remains explicit and safe.
        string sameId = Path.Combine(root, "mods", "existing");
        Directory.CreateDirectory(sameId);
        File.WriteAllText(Path.Combine(sameId, "mod_manifest.json"), ValidManifest);
        var enabled = await service.PreviewAsync(root, zip);
        check(enabled.State == LocalZipPreviewState.Blocked &&
              enabled.Preview!.Conflicts.Any(c => c.Kind == LocalZipConflictKind.SameIdEnabled) &&
              enabled.Preview.Conflicts.Any(c => c.Kind == LocalZipConflictKind.SameIdDifferentDirectory),
              "existing enabled MOD ID in another directory is blocked with distinct diagnostics");
        service.Discard(enabled.Preview);
        Directory.Delete(sameId, true);

        string sameDir = Path.Combine(root, "mods", "Sample");
        Directory.CreateDirectory(sameDir);
        File.WriteAllText(Path.Combine(sameDir, "mod_manifest.json"),
            ValidManifest.Replace("fixture.mod", "another.mod"));
        var different = await service.PreviewAsync(root, zip);
        check(different.Preview!.Conflicts.Any(c =>
              c.Kind == LocalZipConflictKind.TargetDirectoryDifferentId),
              "destination directory belonging to different ID is blocked");
        service.Discard(different.Preview);
        Directory.Delete(sameDir, true);

        File.WriteAllText(sameDir, "occupied");
        var occupied = await service.PreviewAsync(root, zip);
        check(occupied.Preview!.Conflicts.Any(c => c.Kind == LocalZipConflictKind.TargetFileOccupied),
              "destination occupied by a file is blocked");
        service.Discard(occupied.Preview);
        File.Delete(sameDir);

        string storage = Path.Combine(root, ModManager.StorageName, "slot1");
        Directory.CreateDirectory(Path.Combine(storage, "内容"));
        string disabledRecord = Path.Combine(storage, "记录.json");
        File.WriteAllText(disabledRecord, JsonSerializer.Serialize(new
            { ModId = "fixture.mod", Original = "mods/old-fixture",
              Files = new Dictionary<string, string>(), Status = "disabled" }));
        var disabled = await service.PreviewAsync(root, zip);
        check(disabled.Preview!.Conflicts.Any(c => c.Kind == LocalZipConflictKind.SameIdDisabled),
              "disabled MOD IDs are blocked without invoking recovery");
        service.Discard(disabled.Preview);

        // Even pending journal recovery is only diagnosed, never executed.
        File.WriteAllText(disabledRecord, JsonSerializer.Serialize(new
            { ModId = "fixture.mod", Original = "mods/old-fixture",
              Files = new Dictionary<string, string>(), Status = "prepared" }));
        string pending = Path.Combine(root, "尖塔修补站_环境恢复暂存", "00000000000000000000000000000001");
        Directory.CreateDirectory(pending);
        File.WriteAllText(Path.Combine(pending, "restore.json"), "pending-transaction-fixture");
        string dataRoot = Path.Combine(parent, "source-data");
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant())));
        string githubFile = Path.Combine(dataRoot, "GitHub模组更新", key + ".json");
        string workshopFile = Path.Combine(dataRoot, "创意工坊来源", key + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(githubFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(workshopFile)!);
        File.WriteAllText(githubFile, """{"fixture.mod":"known/repository"}""");
        File.WriteAllText(workshopFile, """{"fixture.mod":"1234567890"}""");
        string baselineFile = Path.Combine(dataRoot, "创意工坊来源", key + ".versions.json");
        File.WriteAllText(baselineFile, """{"fixture.mod":{"ManifestId":"123"}}""");

        var withSources = Workflow(parent, dataRoot);
        var before = SnapshotFiles(root);
        var sourcesBefore = SnapshotFiles(dataRoot);
        var withPending = await withSources.PreviewAsync(root, zip);
        var after = SnapshotFiles(root);
        var sourcesAfter = SnapshotFiles(dataRoot);
        check(withPending.State == LocalZipPreviewState.Blocked &&
              withPending.Preview!.Conflicts.Any(c => c.Kind == LocalZipConflictKind.InventoryProblem &&
              c.Detail.Contains("恢复")) &&
              SameFiles(before, after) && SameFiles(sourcesBefore, sourcesAfter),
              "pending recovery marker and disabled transaction stay byte-identical across strictly read-only preview");
        check(withPending.Preview!.HistoricalSources.GitHubRepository == "known/repository" &&
              withPending.Preview.HistoricalSources.WorkshopId == "1234567890" &&
              withPending.Preview.HistoricalSources.WorkshopBaselineExists &&
              withPending.Preview.HistoricalSources.HasHistoricalBinding &&
              withPending.Preview.HistoricalSources.Warning.Contains("不得自动继承") &&
              withPending.Preview.HistoricalSources.GitHubCached is null,
              "historical GitHub/Workshop bindings are warnings, not trusted local ZIP identity or fabricated cache state");
        withSources.Discard(withPending.Preview);

        var running = Workflow(parent, running: _ => true);
        var gameActive = await running.PreviewAsync(Fixture(parent), CreateZip(parent, Valid()));
        check(gameActive.Preview!.Conflicts.Any(c => c.Kind == LocalZipConflictKind.GameRunning),
              "game-running conflict is structured without launching a process");
        running.Discard(gameActive.Preview);

        // Test a reserved folder without touching the destination or legacy importer.
        var reserved = await service.PreviewAsync(Fixture(parent), CreateZip(parent, Valid(".spirepatch-store/")));
        check(reserved.Preview!.Conflicts.Any(c => c.Kind == LocalZipConflictKind.ReservedDirectory),
              "manager-reserved destination is blocked");
        service.Discard(reserved.Preview);

        // Original ZIP and staged hashes are both rechecked independently.
        string verificationRoot = Fixture(parent);
        string tamperZip = CreateZip(parent, Valid());
        var sample = await service.PreviewAsync(verificationRoot, tamperZip);
        if (sample.Preview is null) throw new InvalidOperationException("Fixture must preview.");
        string staged = Path.Combine(sample.Preview.Package.PayloadDirectory, "sample.dll");
        File.WriteAllText(staged, "changed-after-preview");
        check(!(await service.VerifyAsync(sample.Preview)).IsCurrent,
              "staging file modification invalidates immutable preview");
        service.Discard(sample.Preview);
        var sampleZip = await service.PreviewAsync(verificationRoot, tamperZip);
        if (sampleZip.Preview is null) throw new InvalidOperationException("Fixture must preview.");
        using (var append = new FileStream(tamperZip, FileMode.Append, FileAccess.Write))
            append.WriteByte(0x21);
        check(!(await service.VerifyAsync(sampleZip.Preview)).IsCurrent,
              "original ZIP mutation invalidates immutable preview");
        service.Discard(sampleZip.Preview);

        var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancellation = await service.PreviewAsync(verificationRoot,
            CreateZip(parent, Valid()), cancelled.Token);
        check(cancellation.State == LocalZipPreviewState.Cancelled &&
              cancellation.Preview is null, "pre-cancelled preview returns Cancelled without creating a stage");
        var cleanupRejected = await service.PreviewAsync(verificationRoot,
            CreateZip(parent, new ZipEntry("bad.txt", "not-a-mod")));
        check(cleanupRejected.State == LocalZipPreviewState.Failed,
              "invalid package cleans its new staging workspace");
    }
}
