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

internal static class ModUpdateStagingWorkflowChecks
{
    private sealed class FakeBackend(
        Func<ModUpdateStagingRequest, string, Action<string>?, CancellationToken,
            Task<ModUpdateDownloadedArtifact>> download) : IModUpdateStagingBackend
    {
        public int Calls { get; private set; }

        public Task<ModUpdateDownloadedArtifact> DownloadAsync(
            ModUpdateStagingRequest request,
            string workspaceDirectory,
            Action<string>? progress,
            CancellationToken cancellationToken)
        {
            Calls++;
            return download(request, workspaceDirectory, progress, cancellationToken);
        }
    }

    private static byte[] Archive(params (string Name, string Content)[] entries) =>
        ArchiveWithAttributes(entries.Select(item => (item.Name, item.Content, 0)).ToArray());

    private static byte[] ArchiveWithAttributes(params (string Name, string Content, int Attributes)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(item.Name);
                entry.ExternalAttributes = item.Attributes;
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(item.Content);
            }
        }
        return stream.ToArray();
    }

    private static string Manifest(string id = "fixture.mod", string version = "2.0.0") =>
        JsonSerializer.Serialize(new
        {
            id,
            name = "Fixture MOD",
            version,
            has_dll = true,
            has_pck = false,
            dependencies = Array.Empty<string>()
        });

    private static byte[] ValidGitHubArchive(
        string version = "2.0.0",
        string id = "fixture.mod") =>
        Archive(
            ("Fixture/mod_manifest.json", Manifest(id, version)),
            ("Fixture/fixture.dll", "new dll"),
            ("Fixture/new.dat", "new file"),
            ("Fixture/keep.txt", "same"));

    private static ModUpdateStagingRequest GitHubRequest(
        byte[] archive,
        string releaseTag = "v2.0.0",
        string? expectedHash = null)
    {
        const string repository = "fixture/target-mod";
        const string fileName = "target-mod.github.zip";
        return new ModUpdateStagingRequest(
            ModUpdateStagingSource.GitHub,
            repository,
            releaseTag,
            fileName,
            archive.LongLength,
            expectedHash ?? Convert.ToHexString(SHA256.HashData(archive)),
            "https://github.com/fixture/target-mod/releases/download/" +
                Uri.EscapeDataString(releaseTag) + "/" + Uri.EscapeDataString(fileName),
            "https://github.com/fixture/target-mod/releases/tag/" + releaseTag);
    }

    private static FakeBackend GitHubBackend(byte[] archive) =>
        new(async (_, workspace, _, cancellationToken) =>
        {
            string path = Path.Combine(workspace, "package.zip");
            await File.WriteAllBytesAsync(path, archive, cancellationToken);
            return new ModUpdateDownloadedArtifact(
                ArchivePath: path,
                UpstreamIntegrityVerified: true);
        });

    private static (string GameRoot, string ModFolder, ManagedMod Mod) CreateGame(string parent)
    {
        string gameRoot = Path.Combine(parent, "game-fixture");
        string modFolder = Path.Combine(gameRoot, "mods", "Fixture");
        Directory.CreateDirectory(modFolder);
        File.WriteAllText(Path.Combine(gameRoot, "SlayTheSpire2.exe"), "fixture-only");
        File.WriteAllText(Path.Combine(gameRoot, ".spirepatch-fixture"), "never launch");
        string manifestPath = Path.Combine(modFolder, "mod_manifest.json");
        File.WriteAllText(manifestPath, Manifest("fixture.mod", "1.0.0"));
        File.WriteAllText(Path.Combine(modFolder, "fixture.dll"), "old dll");
        File.WriteAllText(Path.Combine(modFolder, "remove.txt"), "remove me");
        File.WriteAllText(Path.Combine(modFolder, "keep.txt"), "same");

        var mod = new ManagedMod(
            "fixture.mod",
            "Fixture MOD",
            "1.0.0",
            "test",
            "",
            modFolder,
            manifestPath,
            true,
            []);
        return (gameRoot, modFolder, mod);
    }

    private static string SnapshotText(string root) =>
        string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                return relative + ":" + hash;
            }));

    private static bool NoWorkspaceChildren(string stagingRoot) =>
        !Directory.Exists(stagingRoot) || !Directory.EnumerateFileSystemEntries(stagingRoot).Any();

    private static ModUpdateDownloadedFileEvidence Evidence(string path, string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        return new ModUpdateDownloadedFileEvidence(
            path,
            bytes.LongLength,
            Convert.ToHexString(SHA1.HashData(bytes)),
            true);
    }

    internal static IModUpdateStagingBackend WorkshopBackend(string workshopId, string manifestId, string modId = "fixture.mod", string version = "1.1.0") =>
        new FakeBackend(async (_, workspace, _, cancellationToken) =>
        {
            string payload = Path.Combine(workspace, "workshop-assembled");
            Directory.CreateDirectory(payload);

            string manifest = Manifest(modId, version);
            const string dll = "workshop update";
            await File.WriteAllTextAsync(
                Path.Combine(payload, "mod_manifest.json"), manifest, cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(payload, "fixture.dll"), dll, cancellationToken);

            ModUpdateDownloadedFileEvidence[] evidence =
            [
                Evidence("mod_manifest.json", manifest),
                Evidence("fixture.dll", dll)
            ];

            string report = JsonSerializer.Serialize(new
            {
                appId = 2868840,
                publishedFileId = workshopId,
                manifestId,
                depotId = 1234,
                fileCount = evidence.Length,
                files = evidence.Select(file => new
                {
                    path = file.RelativePath,
                    sizeBytes = file.SizeBytes,
                    sha1 = file.Sha1,
                    sha1Verified = file.Sha1Verified
                }),
                fileNamesDecrypted = true,
                chunkChecksumsVerified = true,
                fileSha1Verified = true,
                manifestSignatureVerified = false,
                gameBranchVerified = false,
                modFilesAssembled = true,
                source = "spire_supply_station"
            });
            await File.WriteAllTextAsync(
                Path.Combine(payload, "assembly-manifest.json"), report, cancellationToken);

            return new ModUpdateDownloadedArtifact(
                PayloadDirectory: payload,
                SourceManifestId: manifestId,
                WorkshopId: ulong.Parse(workshopId),
                UpstreamIntegrityVerified: true,
                Files: evidence);
        });

    public static async Task RunAsync(string parent, Action<bool, string> check)
    {
        string testRoot = Path.Combine(parent, "MOD 更新暂存工作流测试 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);

        try
        {
            var fixture = CreateGame(testRoot);
            string stagingRoot = Path.Combine(testRoot, "isolated-staging");
            string before = SnapshotText(fixture.ModFolder);

            // A normal GitHub package produces an exact read-only file-change preview.
            byte[] validArchive = ValidGitHubArchive();
            var githubBackend = GitHubBackend(validArchive);
            var githubService = new ModUpdateStagingWorkflowService(githubBackend, stagingRoot);
            ModUpdateStagingResult preview = await githubService.StageAndPreviewAsync(
                fixture.GameRoot,
                fixture.Mod,
                GitHubRequest(validArchive));

            check(preview.State == ModUpdateStagingState.Succeeded &&
                preview.ValidationPassed && preview.IntegrityVerified &&
                preview.StagedVersion == "2.0.0" && preview.VersionChanged &&
                preview.AddedCount == 1 && preview.ModifiedCount == 2 &&
                preview.DeletedCount == 1 &&
                preview.FileChanges.Any(item =>
                    item.RelativePath == "new.dat" && item.Kind == ModUpdateFileChangeKind.Added) &&
                preview.FileChanges.Any(item =>
                    item.RelativePath == "fixture.dll" && item.Kind == ModUpdateFileChangeKind.Modified) &&
                preview.FileChanges.Any(item =>
                    item.RelativePath == "remove.txt" && item.Kind == ModUpdateFileChangeKind.Deleted) &&
                preview.StagingDirectory is not null && Directory.Exists(preview.StagingDirectory),
                "GitHub 包下载到隔离目录、清单/版本/SHA-256/结构校验通过，并准确预览新增、修改和删除文件");

            check(githubBackend.Calls == 1 && SnapshotText(fixture.ModFolder) == before,
                "正常暂存与预览只读取目标 MOD，不请求线上服务且不修改目标目录");
            githubService.Discard(preview.StagingDirectory!);
            check(!Directory.Exists(preview.StagingDirectory),
                "成功预览可显式丢弃隔离暂存内容");

            // Missing manifest, wrong metadata, malformed package structure and hash mismatch.
            byte[] missingManifest = Archive(("Fixture/fixture.dll", "no manifest"));
            var missingResult = await new ModUpdateStagingWorkflowService(
                GitHubBackend(missingManifest), stagingRoot).StageAndPreviewAsync(
                    fixture.GameRoot, fixture.Mod, GitHubRequest(missingManifest));
            check(missingResult.State == ModUpdateStagingState.Failed &&
                missingResult.FailureKind == ModUpdateStagingFailureKind.MissingManifest &&
                NoWorkspaceChildren(stagingRoot),
                "缺失 MOD 清单会拒绝暂存并清理临时目录");

            byte[] wrongId = ValidGitHubArchive(id: "another.mod");
            var wrongIdResult = await new ModUpdateStagingWorkflowService(
                GitHubBackend(wrongId), stagingRoot).StageAndPreviewAsync(
                    fixture.GameRoot, fixture.Mod, GitHubRequest(wrongId));
            check(wrongIdResult.FailureKind == ModUpdateStagingFailureKind.ManifestMismatch &&
                !wrongIdResult.ValidationPassed && NoWorkspaceChildren(stagingRoot),
                "清单 MOD ID 与目标不匹配时拒绝并返回结构化失败原因");

            byte[] wrongVersion = ValidGitHubArchive(version: "2.1.0");
            var wrongVersionResult = await new ModUpdateStagingWorkflowService(
                GitHubBackend(wrongVersion), stagingRoot).StageAndPreviewAsync(
                    fixture.GameRoot, fixture.Mod, GitHubRequest(wrongVersion));
            check(wrongVersionResult.FailureKind == ModUpdateStagingFailureKind.VersionMismatch &&
                NoWorkspaceChildren(stagingRoot),
                "清单版本与 GitHub Release 标签不一致时拒绝并清理暂存目录");

            byte[] duplicateManifest = Archive(
                ("Fixture/mod_manifest.json", Manifest()),
                ("Fixture/manifest.json", Manifest()),
                ("Fixture/fixture.dll", "dll"));
            var duplicateResult = await new ModUpdateStagingWorkflowService(
                GitHubBackend(duplicateManifest), stagingRoot).StageAndPreviewAsync(
                    fixture.GameRoot, fixture.Mod, GitHubRequest(duplicateManifest));
            check(duplicateResult.FailureKind == ModUpdateStagingFailureKind.InvalidPackageStructure &&
                NoWorkspaceChildren(stagingRoot),
                "包含多个 MOD 清单的非法包结构会被拒绝");

            byte[] missingDll = Archive(("Fixture/mod_manifest.json", Manifest()));
            var missingDllResult = await new ModUpdateStagingWorkflowService(
                GitHubBackend(missingDll), stagingRoot).StageAndPreviewAsync(
                    fixture.GameRoot, fixture.Mod, GitHubRequest(missingDll));
            check(missingDllResult.FailureKind == ModUpdateStagingFailureKind.InvalidPackageStructure &&
                NoWorkspaceChildren(stagingRoot),
                "清单声明需要 DLL 但包内缺少 DLL 时拒绝暂存");

            var hashMismatchResult = await new ModUpdateStagingWorkflowService(
                GitHubBackend(validArchive), stagingRoot).StageAndPreviewAsync(
                    fixture.GameRoot,
                    fixture.Mod,
                    GitHubRequest(validArchive, expectedHash: new string('0', 64)));
            check(hashMismatchResult.FailureKind == ModUpdateStagingFailureKind.HashMismatch &&
                NoWorkspaceChildren(stagingRoot),
                "下载包 SHA-256 不匹配时拒绝校验并清理暂存目录");

            // Traversal, rooted paths, drive paths, backslashes and ZIP symlinks.
            var maliciousArchives = new[]
            {
                Archive(("../escape.txt", "bad"), ("Fixture/mod_manifest.json", Manifest()),
                    ("Fixture/fixture.dll", "dll")),
                Archive(("/absolute.txt", "bad"), ("Fixture/mod_manifest.json", Manifest()),
                    ("Fixture/fixture.dll", "dll")),
                Archive(("C:/drive.txt", "bad"), ("Fixture/mod_manifest.json", Manifest()),
                    ("Fixture/fixture.dll", "dll")),
                Archive(("..\\escape.txt", "bad"), ("Fixture/mod_manifest.json", Manifest()),
                    ("Fixture/fixture.dll", "dll")),
                ArchiveWithAttributes(
                    ("Fixture/link", "target", unchecked((int)0xA0000000) | 0x1FF),
                    ("Fixture/mod_manifest.json", Manifest(), 0),
                    ("Fixture/fixture.dll", "dll", 0))
            };

            int maliciousIndex = 0;
            foreach (byte[] maliciousArchive in maliciousArchives)
            {
                maliciousIndex++;
                var maliciousResult = await new ModUpdateStagingWorkflowService(
                    GitHubBackend(maliciousArchive), stagingRoot).StageAndPreviewAsync(
                        fixture.GameRoot, fixture.Mod, GitHubRequest(maliciousArchive));
                check(maliciousResult.State == ModUpdateStagingState.Failed &&
                    maliciousResult.FailureKind == ModUpdateStagingFailureKind.UnsafeArchivePath &&
                    NoWorkspaceChildren(stagingRoot) &&
                    !File.Exists(Path.Combine(stagingRoot, "escape.txt")),
                    "恶意 ZIP 路径/链接拦截 #" + maliciousIndex + "，未在暂存根目录外创建文件");
            }

            // A backend failure and cancellation during download both remove partial workspaces.
            var failingBackend = new FakeBackend((_, _, _, _) =>
                throw new IOException("simulated isolated download failure"));
            var failedDownload = await new ModUpdateStagingWorkflowService(
                failingBackend, stagingRoot).StageAndPreviewAsync(
                    fixture.GameRoot, fixture.Mod, GitHubRequest(validArchive));
            check(failedDownload.State == ModUpdateStagingState.Failed &&
                failedDownload.FailureKind == ModUpdateStagingFailureKind.Io &&
                NoWorkspaceChildren(stagingRoot),
                "下载失败会返回结构化错误并清理部分暂存内容");

            var downloadEntered = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellingBackend = new FakeBackend(async (_, workspace, _, cancellationToken) =>
            {
                await File.WriteAllTextAsync(
                    Path.Combine(workspace, "partial-download.tmp"), "partial", cancellationToken);
                downloadEntered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new ModUpdateDownloadedArtifact();
            });
            using (var cancellation = new CancellationTokenSource())
            {
                var pending = new ModUpdateStagingWorkflowService(
                    cancellingBackend, stagingRoot).StageAndPreviewAsync(
                        fixture.GameRoot, fixture.Mod, GitHubRequest(validArchive), cancellation.Token);
                await downloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                var cancelled = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                check(cancelled.State == ModUpdateStagingState.Cancelled &&
                    cancelled.FailureKind == ModUpdateStagingFailureKind.Cancelled &&
                    NoWorkspaceChildren(stagingRoot),
                    "下载阶段取消后立即清理不完整的临时暂存目录");
            }

            // Cancellation triggered by the extraction progress callback exercises the validation path.
            using (var cancellation = new CancellationTokenSource())
            {
                bool cancelledDuringExtraction = false;
                var extractionService = new ModUpdateStagingWorkflowService(
                    GitHubBackend(validArchive), stagingRoot);
                var cancelled = await extractionService.StageAndPreviewAsync(
                    fixture.GameRoot,
                    fixture.Mod,
                    GitHubRequest(validArchive),
                    cancellation.Token,
                    progress =>
                    {
                        if (progress.Stage == "Extract" && progress.Completed == 1)
                        {
                            cancelledDuringExtraction = true;
                            cancellation.Cancel();
                        }
                    });
                check(cancelledDuringExtraction &&
                    cancelled.State == ModUpdateStagingState.Cancelled &&
                    NoWorkspaceChildren(stagingRoot),
                    "解压/校验阶段取消后清理已解压文件，不留下半成品");
            }

            // Workshop uses the existing assembler evidence, but the workflow still verifies it locally.
            const string workshopId = "12345";
            const string workshopManifestId = "987654";
            var workshopBackend = WorkshopBackend(workshopId, workshopManifestId);
            var workshopRequest = new ModUpdateStagingRequest(
                ModUpdateStagingSource.Workshop,
                workshopId,
                ExpectedWorkshopManifestId: workshopManifestId);
            var workshopResult = await new ModUpdateStagingWorkflowService(
                workshopBackend, stagingRoot).StageAndPreviewAsync(
                    fixture.GameRoot, fixture.Mod, workshopRequest);
            check(workshopResult.State == ModUpdateStagingState.Succeeded &&
                workshopResult.IntegrityVerified &&
                workshopResult.SourceManifestId == workshopManifestId &&
                workshopResult.StagedVersion == "1.1.0" &&
                workshopResult.FileChanges.Any(item =>
                    item.RelativePath == "fixture.dll" && item.Kind == ModUpdateFileChangeKind.Modified) &&
                SnapshotText(fixture.ModFolder) == before,
                "Workshop 模拟组装结果通过 Manifest ID、Chunk/文件 SHA-1 证据复核并生成预览，目标 MOD 保持不变");
            if (workshopResult.StagingDirectory is not null)
                new ModUpdateStagingWorkflowService(workshopBackend, stagingRoot)
                    .Discard(workshopResult.StagingDirectory);

            var wrongWorkshopBackend = WorkshopBackend(workshopId, "999999");
            var wrongWorkshopRequest = workshopRequest with { ExpectedWorkshopManifestId = "987654" };
            var wrongWorkshopResult = await new ModUpdateStagingWorkflowService(
                wrongWorkshopBackend, stagingRoot).StageAndPreviewAsync(
                    fixture.GameRoot, fixture.Mod, wrongWorkshopRequest);
            check(wrongWorkshopResult.State == ModUpdateStagingState.Failed &&
                wrongWorkshopResult.FailureKind == ModUpdateStagingFailureKind.IntegrityCheckFailed &&
                NoWorkspaceChildren(stagingRoot),
                "Workshop Manifest ID 与预期来源不一致时拒绝暂存");

            check(SnapshotText(fixture.ModFolder) == before &&
                NoWorkspaceChildren(stagingRoot),
                "整个下载暂存、校验、预览和失败清理流程均未修改目标 MOD 目录");

            Directory.Delete(testRoot, recursive: true);
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
    }
}
