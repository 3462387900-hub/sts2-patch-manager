using System.IO;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SpirePatch;

internal sealed record SteamDepotManifestSummary(
    uint DepotId,
    ulong ManifestId,
    uint CreatedAtUnix,
    bool FileNamesEncrypted,
    uint EncryptedCrc,
    uint ClearCrc,
    uint DeclaredUniqueChunkCount,
    int FileCount,
    int UniqueChunkCount,
    long TotalFileBytes,
    long TotalChunkCompressedBytes,
    long TotalChunkUncompressedBytes,
    int SignatureBytes);

internal sealed record SteamDepotManifestChunk(
    string Id,
    byte[] IdBytes,
    uint Checksum,
    ulong Offset,
    uint CompressedLength,
    uint UncompressedLength);

internal sealed record SteamDepotManifestFile(
    string Path,
    long Size,
    uint Flags,
    byte[] ShaContent,
    string? LinkTarget,
    IReadOnlyList<SteamDepotManifestChunk> Chunks);

internal sealed record SteamDepotManifestData(
    SteamDepotManifestSummary Summary,
    IReadOnlyList<SteamDepotManifestFile> Files)
{
    public SteamDepotManifestData DecryptFileNames(byte[] depotKey)
    {
        if (!Summary.FileNamesEncrypted)
            return this;

        if (depotKey.Length != 32)
            throw new InvalidDataException("Steam Depot Key 必须为 32 字节。");

        var decryptedFiles = Files.Select(file => file with
        {
            Path = SteamDepotCrypto.DecryptManifestName(file.Path, depotKey),
            LinkTarget = string.IsNullOrWhiteSpace(file.LinkTarget)
                ? null
                : SteamDepotCrypto.DecryptManifestName(file.LinkTarget, depotKey)
        }).ToArray();

        return this with
        {
            Summary = Summary with { FileNamesEncrypted = false },
            Files = decryptedFiles
        };
    }

    public IReadOnlyList<SteamDepotManifestChunk> UniqueChunks()
    {
        var chunks = new Dictionary<string, SteamDepotManifestChunk>(StringComparer.Ordinal);
        foreach (var file in Files)
        {
            foreach (var chunk in file.Chunks)
            {
                if (chunks.TryGetValue(chunk.Id, out var existing))
                {
                    if (existing.Checksum != chunk.Checksum ||
                        existing.CompressedLength != chunk.CompressedLength ||
                        existing.UncompressedLength != chunk.UncompressedLength)
                        throw new InvalidDataException("Steam manifest reuses a Chunk ID with conflicting content metadata.");
                }
                else
                {
                    chunks.Add(chunk.Id, chunk);
                }
            }
        }
        return chunks.Values.ToArray();
    }
}

internal static class SteamDepotManifestParser
{
    private const uint PayloadMagic = 0x71F617D0;
    private const uint MetadataMagic = 0x1F4812BE;
    private const uint SignatureMagic = 0x1B81B817;
    private const uint EndMagic = 0x32C415AB;
    private const int MaxArchiveBytes = 64 * 1024 * 1024;
    private const int MaxManifestBytes = 64 * 1024 * 1024;
    private const int MaxSectionBytes = 64 * 1024 * 1024;
    private const int MaxFiles = 100_000;
    private const int MaxChunks = 2_000_000;
    private const long MaxTotalFileBytes = 2L * 1024 * 1024 * 1024;
    private const uint MaxChunkBytes = 64 * 1024 * 1024;

    public static SteamDepotManifestSummary ParseZip(byte[] archiveBytes) =>
        ParseZipDetailed(archiveBytes).Summary;

    public static SteamDepotManifestData ParseZipDetailed(byte[] archiveBytes)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);
        if (archiveBytes.Length == 0 || archiveBytes.Length > MaxArchiveBytes)
            throw new InvalidDataException("Steam CDN manifest archive is empty or exceeds 64 MiB.");

        using var archiveStream = new MemoryStream(archiveBytes, writable: false);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
        var entries = archive.Entries
            .Where(entry => !entry.FullName.EndsWith("/", StringComparison.Ordinal) &&
                            !entry.FullName.EndsWith("\\", StringComparison.Ordinal))
            .ToArray();
        if (entries.Length != 1)
            throw new InvalidDataException("Steam manifest archive must contain exactly one file.");

        var entry = entries[0];
        if (entry.Length <= 0 || entry.Length > MaxManifestBytes)
            throw new InvalidDataException("Steam manifest payload is empty or exceeds 64 MiB.");

        using var input = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > MaxManifestBytes)
                throw new InvalidDataException("Decompressed Steam manifest exceeds 64 MiB.");
            output.Write(buffer, 0, read);
        }

        return ParseDetailed(output.ToArray());
    }

    public static SteamDepotManifestSummary Parse(byte[] bytes) => ParseDetailed(bytes).Summary;

    public static SteamDepotManifestData ParseDetailed(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0 || bytes.Length > MaxManifestBytes)
            throw new InvalidDataException("Steam manifest payload is empty or exceeds 64 MiB.");

        byte[]? payload = null;
        byte[]? metadata = null;
        int signatureBytes = 0;
        bool sawEnd = false;
        int offset = 0;

        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 4)
                throw new InvalidDataException("Steam manifest ended inside a section marker.");

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            offset += 4;
            if (magic == EndMagic)
            {
                sawEnd = true;
                break;
            }

            if (bytes.Length - offset < 4)
                throw new InvalidDataException("Steam manifest ended before a section length.");

            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            offset += 4;
            if (length > MaxSectionBytes || length > bytes.Length - offset)
                throw new InvalidDataException("Steam manifest section length is invalid or exceeds the limit.");

            int sectionLength = checked((int)length);
            var section = bytes.AsMemory(offset, sectionLength);
            offset += sectionLength;

            switch (magic)
            {
                case PayloadMagic:
                    if (payload is not null)
                        throw new InvalidDataException("Steam manifest contains duplicate payload sections.");
                    payload = section.ToArray();
                    break;
                case MetadataMagic:
                    if (metadata is not null)
                        throw new InvalidDataException("Steam manifest contains duplicate metadata sections.");
                    metadata = section.ToArray();
                    break;
                case SignatureMagic:
                    signatureBytes = sectionLength;
                    break;
                default:
                    throw new InvalidDataException($"Unknown Steam manifest section marker 0x{magic:X8}.");
            }
        }

        if (!sawEnd)
            throw new InvalidDataException("Steam manifest end marker is missing.");
        if (payload is null || metadata is null)
            throw new InvalidDataException("Steam manifest payload or metadata section is missing.");

        var metadataSummary = ParseMetadata(metadata);
        var files = ParsePayload(payload, metadataSummary.FileNamesEncrypted);
        var uniqueChunks = UniqueChunks(files);
        long totalFileBytes = files.Aggregate(0L, (sum, file) => checked(sum + file.Size));

        var summary = new SteamDepotManifestSummary(
            metadataSummary.DepotId,
            metadataSummary.ManifestId,
            metadataSummary.CreatedAtUnix,
            metadataSummary.FileNamesEncrypted,
            metadataSummary.EncryptedCrc,
            metadataSummary.ClearCrc,
            metadataSummary.DeclaredUniqueChunkCount,
            files.Count,
            uniqueChunks.Count,
            totalFileBytes,
            uniqueChunks.Values.Sum(chunk => (long)chunk.CompressedLength),
            uniqueChunks.Values.Sum(chunk => (long)chunk.UncompressedLength),
            signatureBytes);

        if (totalFileBytes > MaxTotalFileBytes)
            throw new InvalidDataException("Steam manifest total file size exceeds the 2 GiB safety limit.");

        return new SteamDepotManifestData(summary, files);
    }

    private sealed record MetadataSummary(
        uint DepotId,
        ulong ManifestId,
        uint CreatedAtUnix,
        bool FileNamesEncrypted,
        uint EncryptedCrc,
        uint ClearCrc,
        uint DeclaredUniqueChunkCount);

    private static MetadataSummary ParseMetadata(byte[] bytes)
    {
        var reader = new ProtoReader(bytes);
        uint depotId = 0, createdAt = 0, encryptedCrc = 0, clearCrc = 0, uniqueChunks = 0;
        ulong manifestId = 0;
        bool filenamesEncrypted = false;

        while (reader.TryReadTag(out int field, out int wire))
        {
            switch (field)
            {
                case 1 when wire == 0: depotId = checked((uint)reader.ReadVarint()); break;
                case 2 when wire == 0: manifestId = reader.ReadVarint(); break;
                case 3 when wire == 0: createdAt = checked((uint)reader.ReadVarint()); break;
                case 4 when wire == 0: filenamesEncrypted = reader.ReadVarint() != 0; break;
                case 7 when wire == 0: uniqueChunks = checked((uint)reader.ReadVarint()); break;
                case 8 when wire == 0: encryptedCrc = checked((uint)reader.ReadVarint()); break;
                case 9 when wire == 0: clearCrc = checked((uint)reader.ReadVarint()); break;
                default: reader.SkipField(wire); break;
            }
        }

        if (depotId == 0 || manifestId == 0)
            throw new InvalidDataException("Steam manifest metadata is missing a valid Depot ID or Manifest ID.");

        return new MetadataSummary(depotId, manifestId, createdAt, filenamesEncrypted,
            encryptedCrc, clearCrc, uniqueChunks);
    }

    private static List<SteamDepotManifestFile> ParsePayload(byte[] bytes, bool filenamesEncrypted)
    {
        var reader = new ProtoReader(bytes);
        var files = new List<SteamDepotManifestFile>();
        while (reader.TryReadTag(out int field, out int wire))
        {
            if (field == 1 && wire == 2)
            {
                if (files.Count >= MaxFiles)
                    throw new InvalidDataException("Steam manifest contains too many files.");
                files.Add(ParseFileMapping(reader.ReadLengthDelimited(), filenamesEncrypted));
            }
            else
            {
                reader.SkipField(wire);
            }
        }

        if (files.Count == 0)
            throw new InvalidDataException("Steam manifest contains no file mappings.");

        return files;
    }

    private static SteamDepotManifestFile ParseFileMapping(byte[] bytes, bool filenamesEncrypted)
    {
        var reader = new ProtoReader(bytes);
        string path = "";
        string? linkTarget = null;
        ulong size = 0;
        uint flags = 0;
        byte[] shaContent = [];
        var chunks = new List<SteamDepotManifestChunk>();

        while (reader.TryReadTag(out int field, out int wire))
        {
            switch (field)
            {
                case 1 when wire == 2:
                    path = SanitizeManifestString(Encoding.UTF8.GetString(reader.ReadLengthDelimited()));
                    break;
                case 2 when wire == 0:
                    size = reader.ReadVarint();
                    break;
                case 3 when wire == 0:
                    flags = checked((uint)reader.ReadVarint());
                    break;
                case 5 when wire == 2:
                    shaContent = reader.ReadLengthDelimited();
                    break;
                case 6 when wire == 2:
                    chunks.Add(ParseChunk(reader.ReadLengthDelimited()));
                    break;
                case 7 when wire == 2:
                    linkTarget = SanitizeManifestString(Encoding.UTF8.GetString(reader.ReadLengthDelimited()));
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("Steam manifest contains a file mapping with an empty path.");
        if (size > long.MaxValue)
            throw new InvalidDataException("Steam manifest file size exceeds the supported range.");
        if (shaContent.Length is not (0 or 20))
            throw new InvalidDataException("Steam manifest file SHA-1 field has an invalid length.");
        if (chunks.Count > MaxChunks)
            throw new InvalidDataException("Steam manifest file contains too many chunks.");

        if (!filenamesEncrypted)
        {
            path = path.Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(linkTarget))
                linkTarget = linkTarget.Replace('\\', '/');
        }

        foreach (var chunk in chunks)
        {
            if (chunk.Offset > (ulong)size ||
                chunk.UncompressedLength > (ulong)size - chunk.Offset)
                throw new InvalidDataException("Steam manifest chunk range exceeds its file size.");
        }

        return new SteamDepotManifestFile(path, (long)size, flags, shaContent, linkTarget, chunks);
    }

    private static SteamDepotManifestChunk ParseChunk(byte[] bytes)
    {
        var reader = new ProtoReader(bytes);
        byte[]? sha = null;
        uint checksum = 0, compressedLength = 0, uncompressedLength = 0;
        ulong offset = 0;

        while (reader.TryReadTag(out int field, out int wire))
        {
            switch (field)
            {
                case 1 when wire == 2: sha = reader.ReadLengthDelimited(); break;
                case 2 when wire == 5: checksum = reader.ReadFixed32(); break;
                case 3 when wire == 0: offset = reader.ReadVarint(); break;
                case 4 when wire == 0: uncompressedLength = checked((uint)reader.ReadVarint()); break;
                case 5 when wire == 0: compressedLength = checked((uint)reader.ReadVarint()); break;
                default: reader.SkipField(wire); break;
            }
        }

        if (sha is null || sha.Length != 20 || compressedLength == 0 || uncompressedLength == 0 ||
            compressedLength > MaxChunkBytes || uncompressedLength > MaxChunkBytes)
            throw new InvalidDataException("Steam manifest contains an invalid or oversized Chunk record.");

        return new SteamDepotManifestChunk(Convert.ToHexString(sha), sha, checksum, offset,
            compressedLength, uncompressedLength);
    }

    private static Dictionary<string, SteamDepotManifestChunk> UniqueChunks(
        IReadOnlyList<SteamDepotManifestFile> files)
    {
        var chunks = new Dictionary<string, SteamDepotManifestChunk>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (var chunk in file.Chunks)
            {
                if (chunks.TryGetValue(chunk.Id, out var existing))
                {
                    if (existing.Checksum != chunk.Checksum ||
                        existing.CompressedLength != chunk.CompressedLength ||
                        existing.UncompressedLength != chunk.UncompressedLength)
                        throw new InvalidDataException("Steam manifest reuses a Chunk ID with conflicting content metadata.");
                }
                else
                {
                    if (chunks.Count >= MaxChunks)
                        throw new InvalidDataException("Steam manifest contains too many unique chunks.");
                    chunks.Add(chunk.Id, chunk);
                }
            }
        }
        return chunks;
    }

    private static string SanitizeManifestString(string value)
    {
        int start = 0;
        int end = value.Length;
        while (start < end && (value[start] <= ' ' || value[start] == '\0'))
            start++;
        while (end > start && (value[end - 1] <= ' ' || value[end - 1] == '\0'))
            end--;
        return value[start..end];
    }

    private sealed class ProtoReader(byte[] bytes)
    {
        private readonly byte[] data = bytes;
        private int position;

        public bool TryReadTag(out int field, out int wire)
        {
            if (position == data.Length)
            {
                field = wire = 0;
                return false;
            }

            ulong tag = ReadVarint();
            field = checked((int)(tag >> 3));
            wire = (int)(tag & 7);
            if (field <= 0)
                throw new InvalidDataException("Steam manifest contains an invalid protobuf field tag.");
            return true;
        }

        public ulong ReadVarint()
        {
            ulong value = 0;
            for (int shift = 0; shift < 70; shift += 7)
            {
                if (position >= data.Length)
                    throw new InvalidDataException("Steam manifest protobuf varint is truncated.");
                byte current = data[position++];
                if (shift == 63 && (current & 0xFE) != 0)
                    throw new InvalidDataException("Steam manifest protobuf varint overflows UInt64.");
                value |= (ulong)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                    return value;
            }
            throw new InvalidDataException("Steam manifest protobuf varint is malformed.");
        }

        public byte[] ReadLengthDelimited()
        {
            ulong length = ReadVarint();
            if (length > int.MaxValue || length > (ulong)(data.Length - position))
                throw new InvalidDataException("Steam manifest protobuf field length is invalid.");
            int count = (int)length;
            var result = data.AsSpan(position, count).ToArray();
            position += count;
            return result;
        }

        public uint ReadFixed32()
        {
            if (data.Length - position < 4)
                throw new InvalidDataException("Steam manifest protobuf fixed32 is truncated.");
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position, 4));
            position += 4;
            return value;
        }

        public void SkipField(int wire)
        {
            switch (wire)
            {
                case 0: _ = ReadVarint(); break;
                case 1: Advance(8); break;
                case 2:
                    ulong length = ReadVarint();
                    if (length > int.MaxValue)
                        throw new InvalidDataException("Steam manifest protobuf field is too large.");
                    Advance((int)length);
                    break;
                case 5: Advance(4); break;
                default:
                    throw new InvalidDataException("Steam manifest contains an unsupported protobuf wire type.");
            }
        }

        private void Advance(int count)
        {
            if (count < 0 || count > data.Length - position)
                throw new InvalidDataException("Steam manifest protobuf field is truncated.");
            position += count;
        }
    }
}
