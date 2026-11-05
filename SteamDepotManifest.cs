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

    private sealed record ChunkInfo(string Id, uint Checksum, ulong Offset, uint CompressedLength, uint UncompressedLength);

    public static SteamDepotManifestSummary ParseZip(byte[] archiveBytes)
    {
        ArgumentNullException.ThrowIfNull(archiveBytes);
        if (archiveBytes.Length == 0 || archiveBytes.Length > MaxArchiveBytes)
            throw new InvalidDataException("Steam CDN manifest archive is empty or exceeds 64 MiB.");

        using var archiveStream = new MemoryStream(archiveBytes, writable: false);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
        var files = archive.Entries
            .Where(entry => !entry.FullName.EndsWith("/", StringComparison.Ordinal) &&
                            !entry.FullName.EndsWith("\\", StringComparison.Ordinal))
            .ToArray();
        if (files.Length != 1)
            throw new InvalidDataException("Steam manifest archive must contain exactly one file.");

        var entry = files[0];
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

        return Parse(output.ToArray());
    }

    public static SteamDepotManifestSummary Parse(byte[] bytes)
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
                    payload = section.ToArray();
                    break;
                case MetadataMagic:
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
        var payloadSummary = ParsePayload(payload);
        return new SteamDepotManifestSummary(
            metadataSummary.DepotId,
            metadataSummary.ManifestId,
            metadataSummary.CreatedAtUnix,
            metadataSummary.FileNamesEncrypted,
            metadataSummary.EncryptedCrc,
            metadataSummary.ClearCrc,
            metadataSummary.DeclaredUniqueChunkCount,
            payloadSummary.FileCount,
            payloadSummary.Chunks.Count,
            payloadSummary.TotalFileBytes,
            payloadSummary.Chunks.Values.Sum(chunk => (long)chunk.CompressedLength),
            payloadSummary.Chunks.Values.Sum(chunk => (long)chunk.UncompressedLength),
            signatureBytes);
    }

    private sealed record MetadataSummary(
        uint DepotId,
        ulong ManifestId,
        uint CreatedAtUnix,
        bool FileNamesEncrypted,
        uint EncryptedCrc,
        uint ClearCrc,
        uint DeclaredUniqueChunkCount);

    private sealed record PayloadSummary(int FileCount, long TotalFileBytes, Dictionary<string, ChunkInfo> Chunks);

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

    private static PayloadSummary ParsePayload(byte[] bytes)
    {
        var reader = new ProtoReader(bytes);
        var chunks = new Dictionary<string, ChunkInfo>(StringComparer.Ordinal);
        int fileCount = 0;
        long totalFileBytes = 0;

        while (reader.TryReadTag(out int field, out int wire))
        {
            if (field == 1 && wire == 2)
            {
                if (++fileCount > MaxFiles)
                    throw new InvalidDataException("Steam manifest contains too many files.");
                var file = ParseFileMapping(reader.ReadLengthDelimited());
                totalFileBytes = checked(totalFileBytes + file.Size);

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
            else
            {
                reader.SkipField(wire);
            }
        }

        if (fileCount == 0)
            throw new InvalidDataException("Steam manifest contains no file mappings.");

        return new PayloadSummary(fileCount, totalFileBytes, chunks);
    }

    private sealed record FileSummary(long Size, List<ChunkInfo> Chunks);

    private static FileSummary ParseFileMapping(byte[] bytes)
    {
        var reader = new ProtoReader(bytes);
        ulong size = 0;
        var chunks = new List<ChunkInfo>();

        while (reader.TryReadTag(out int field, out int wire))
        {
            switch (field)
            {
                case 2 when wire == 0:
                    size = reader.ReadVarint();
                    break;
                case 6 when wire == 2:
                    chunks.Add(ParseChunk(reader.ReadLengthDelimited()));
                    break;
                default:
                    reader.SkipField(wire);
                    break;
            }
        }

        if (size > long.MaxValue)
            throw new InvalidDataException("Steam manifest file size exceeds the supported range.");

        foreach (var chunk in chunks)
        {
            if (chunk.Offset > (ulong)size ||
                chunk.UncompressedLength > (ulong)size - chunk.Offset)
                throw new InvalidDataException("Steam manifest chunk range exceeds its file size.");
        }

        return new FileSummary((long)size, chunks);
    }

    private static ChunkInfo ParseChunk(byte[] bytes)
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

        if (sha is null || sha.Length != 20 || compressedLength == 0 || uncompressedLength == 0)
            throw new InvalidDataException("Steam manifest contains an invalid Chunk record.");

        return new ChunkInfo(Convert.ToHexString(sha), checksum, offset, compressedLength, uncompressedLength);
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
                case 1:
                    Advance(8);
                    break;
                case 2:
                    ulong length = ReadVarint();
                    if (length > int.MaxValue) throw new InvalidDataException("Steam manifest protobuf field is too large.");
                    Advance((int)length);
                    break;
                case 5:
                    Advance(4);
                    break;
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
