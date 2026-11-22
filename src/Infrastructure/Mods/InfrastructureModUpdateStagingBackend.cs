using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SpirePatch;

/// <summary>
/// Bridges the Application staging contract to the existing GitHub and Workshop
/// download implementations. It only downloads/assembles into the supplied
/// isolated workspace; it never inspects or writes the active MOD directory.
/// </summary>
internal sealed class InfrastructureModUpdateStagingBackend : IModUpdateStagingBackend, IDisposable
{
    private readonly GitHubModInstaller githubInstaller;
    private readonly bool ownsGitHubInstaller;
    private readonly Func<HttpMessageHandler?> workshopHandlerFactory;

    public InfrastructureModUpdateStagingBackend(
        GitHubModInstaller? githubInstaller = null,
        Func<HttpMessageHandler?>? workshopHandlerFactory = null)
    {
        this.githubInstaller = githubInstaller ?? new GitHubModInstaller();
        ownsGitHubInstaller = githubInstaller is null;
        this.workshopHandlerFactory = workshopHandlerFactory ?? (() => null);
    }

    public async Task<ModUpdateDownloadedArtifact> DownloadAsync(
        ModUpdateStagingRequest request,
        string workspaceDirectory,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        Directory.CreateDirectory(workspaceDirectory);

        if (request.Source == ModUpdateStagingSource.GitHub)
        {
            string archive = Path.Combine(workspaceDirectory, "package.zip");
            var package = new GitHubModPackage(
                request.SourceIdentifier,
                request.ReleaseTag ?? "",
                request.FileName ?? "",
                request.ExpectedArchiveSize ?? 0,
                request.ExpectedArchiveSha256 ?? "",
                request.DownloadUrl ?? "",
                request.ReleaseUrl ?? "");

            progress?.Invoke("正在下载 GitHub Release 附件…");
            await githubInstaller.DownloadPackageAsync(package, archive, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            return new ModUpdateDownloadedArtifact(
                ArchivePath: archive,
                UpstreamIntegrityVerified: true);
        }

        if (request.Source == ModUpdateStagingSource.Workshop)
        {
            if (!ulong.TryParse(request.SourceIdentifier, NumberStyles.None,
                    CultureInfo.InvariantCulture, out ulong workshopId) ||
                workshopId == 0)
                throw new InvalidDataException("Workshop ID 无效。");

            string rawChunks = Path.Combine(workspaceDirectory, "workshop-raw-chunks");
            string assembledDirectory = Path.Combine(workspaceDirectory, "workshop-assembled");
            using var client = new SpireSupplyStationClient(workshopHandlerFactory());

            progress?.Invoke("正在通过现有创意工坊下载服务获取授权并下载 Chunk…");
            SupplyStationChunkProbeResult chunks = await client.ProbeChunksAsync(
                SpireSupplyStationClient.SupportedAppId,
                workshopId,
                rawChunks,
                maxConcurrentChunks: 4,
                progress,
                cancellationToken);

            if (chunks.DownloadedChunks <= 0 || chunks.DownloadedChunks != chunks.ChunkCount)
                throw new InvalidDataException("Workshop 原始 Chunk 未完整下载。");

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke("Chunk 下载完成，正在复用现有解密与组装流程…");
            SupplyStationAssemblyProbeResult assembly = await client.AssembleChunksAsync(
                SpireSupplyStationClient.SupportedAppId,
                workshopId,
                rawChunks,
                assembledDirectory,
                progress,
                cancellationToken);

            if (!assembly.ModFilesAssembled ||
                !assembly.ChunkChecksumsVerified ||
                !assembly.FileSha1Verified ||
                assembly.FileCount <= 0 ||
                assembly.Files.Count != assembly.FileCount)
            {
                throw new InvalidDataException("Workshop 内容未通过完整的 Chunk 和文件校验。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new ModUpdateDownloadedArtifact(
                PayloadDirectory: assembly.OutputDirectory,
                SourceManifestId: assembly.ManifestId,
                WorkshopId: workshopId,
                UpstreamIntegrityVerified: true,
                Files: assembly.Files.Select(file => new ModUpdateDownloadedFileEvidence(
                    file.Path,
                    file.SizeBytes,
                    file.Sha1,
                    file.Sha1Verified)).ToArray());
        }

        throw new InvalidOperationException("不支持的 MOD 更新来源类型。");
    }

    public void Dispose()
    {
        if (ownsGitHubInstaller)
            githubInstaller.Dispose();
    }
}
