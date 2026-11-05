using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Compressors.ZStandard;

namespace SpirePatch;

/// <summary>
/// Processes SteamPipe UGC chunks using the depot key supplied by the authorized
/// descriptor. It never writes keys or decrypted payloads to diagnostic reports.
/// </summary>
internal static class SteamDepotChunkProcessor
{
    private const int MaxChunkBytes = 64 * 1024 * 1024;
    private const ushort VZipHeaderMagic = 0x5A56;
    private const ushort VZipFooterMagic = 0x767A;
    private const int VZipHeaderAndPropertiesLength = 12;
    private const int VZipFooterLength = 10;
    private const int VZstdHeaderLength = 8;
    private const int VZstdFooterLength = 15;

    public static byte[] Process(
        byte[] rawChunk,
        SteamDepotManifestChunk manifestChunk,
        byte[] depotKey)
    {
        ArgumentNullException.ThrowIfNull(rawChunk);
        ArgumentNullException.ThrowIfNull(manifestChunk);
        ArgumentNullException.ThrowIfNull(depotKey);

        if (depotKey.Length != 32)
            throw new InvalidDataException("Steam Depot Key 必须为 32 字节。");
        if (manifestChunk.UncompressedLength is 0 or > MaxChunkBytes)
            throw new InvalidDataException("Manifest 声明的 Chunk 解压长度超出安全限制。");
        if (rawChunk.Length != manifestChunk.CompressedLength)
            throw new InvalidDataException("原始 Chunk 长度与 Manifest 声明不一致。");

        byte[] decryptedPayload = SteamDepotCrypto.DecryptWithEncryptedIv(rawChunk, depotKey);
        try
        {
            int expectedLength = checked((int)manifestChunk.UncompressedLength);
            byte[] content = Decompress(decryptedPayload, expectedLength);

            if (content.Length != expectedLength)
                throw new InvalidDataException($"Chunk {manifestChunk.Id} 解压长度不匹配。");

            uint checksum = ComputeSteamAdler32(content);
            if (checksum != manifestChunk.Checksum)
                throw new InvalidDataException($"Chunk {manifestChunk.Id} Adler32 校验失败。");

            return content;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decryptedPayload);
        }
    }

    private static byte[] Decompress(byte[] payload, int expectedLength)
    {
        if (HasPrefix(payload, "VSZa"u8))
            return DecompressVZstd(payload, expectedLength);
        if (HasPrefix(payload, "VZa"u8))
            return DecompressVZip(payload, expectedLength);
        if (HasPrefix(payload, [0x50, 0x4B, 0x03, 0x04]))
            return DecompressSingleEntryZip(payload, expectedLength);

        throw new InvalidDataException("Chunk 已解密，但压缩格式标记不受支持。");
    }

    private static byte[] DecompressVZip(byte[] payload, int expectedLength)
    {
        if (payload.Length < VZipHeaderAndPropertiesLength + VZipFooterLength + 1)
            throw new InvalidDataException("VZip Chunk 长度不足。");

        if (BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2)) != VZipHeaderMagic ||
            payload[2] != (byte)'a')
            throw new InvalidDataException("VZip Chunk 文件头无效。");

        byte propertyBits = payload[7];
        int dictionarySize = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8, 4));
        if (propertyBits > 224 || dictionarySize <= 0 || dictionarySize > MaxChunkBytes)
            throw new InvalidDataException("VZip LZMA 属性或字典大小无效。");

        int footerOffset = payload.Length - VZipFooterLength;
        uint footerSize = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(footerOffset + 4, 4));
        ushort footerMagic = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(footerOffset + 8, 2));
        if (footerMagic != VZipFooterMagic || footerSize != (uint)expectedLength)
            throw new InvalidDataException("VZip Chunk 文件尾或解压长度与 Manifest 不匹配。");

        int compressedOffset = VZipHeaderAndPropertiesLength;
        int compressedLength = footerOffset - compressedOffset;
        if (compressedLength <= 0)
            throw new InvalidDataException("VZip Chunk 不包含有效的 LZMA 数据。");

        byte[] properties = new byte[5];
        properties[0] = propertyBits;
        BinaryPrimitives.WriteInt32LittleEndian(properties.AsSpan(1, 4), dictionarySize);

        using var compressed = new MemoryStream(
            payload, compressedOffset, compressedLength, writable: false);
        using var decoder = LzmaStream.Create(
            properties,
            compressed,
            compressedLength,
            expectedLength,
            leaveOpen: true);

        byte[] output = new byte[expectedLength];
        decoder.ReadExactly(output);
        if (decoder.ReadByte() != -1)
            throw new InvalidDataException("VZip LZMA 解压结果超过 Manifest 声明长度。");

        return output;
    }

    private static byte[] DecompressVZstd(byte[] payload, int expectedLength)
    {
        if (payload.Length < VZstdHeaderLength + VZstdFooterLength + 1)
            throw new InvalidDataException("VZstd Chunk 长度不足。");

        int expectedSizeOffset = payload.Length - 11;
        uint declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(
            payload.AsSpan(expectedSizeOffset, 4));
        if (declaredSize != (uint)expectedLength)
            throw new InvalidDataException("VZstd 文件尾解压长度与 Manifest 不匹配。");

        int compressedOffset = VZstdHeaderLength;
        int compressedLength = payload.Length - VZstdHeaderLength - VZstdFooterLength;
        if (compressedLength <= 0)
            throw new InvalidDataException("VZstd Chunk 不包含有效的 Zstandard 数据。");

        byte[] output = new byte[expectedLength];
        using var decoder = new Decompressor();
        int written = decoder.Unwrap(
            payload.AsSpan(compressedOffset, compressedLength),
            output);
        if (written != expectedLength)
            throw new InvalidDataException("VZstd 解压结果长度不匹配。");

        return output;
    }

    private static byte[] DecompressSingleEntryZip(byte[] payload, int expectedLength)
    {
        using var input = new MemoryStream(payload, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        var entries = archive.Entries
            .Where(entry => !entry.FullName.EndsWith("/", StringComparison.Ordinal) &&
                            !entry.FullName.EndsWith("\\", StringComparison.Ordinal))
            .ToArray();

        if (entries.Length != 1)
            throw new InvalidDataException("Chunk ZIP 必须且只能包含一个文件条目。");

        var entry = entries[0];
        if (entry.Length != expectedLength)
            throw new InvalidDataException("Chunk ZIP 条目长度与 Manifest 不匹配。");

        using var entryStream = entry.Open();
        byte[] output = new byte[expectedLength];
        entryStream.ReadExactly(output);
        if (entryStream.ReadByte() != -1)
            throw new InvalidDataException("Chunk ZIP 条目解压结果超过 Manifest 声明长度.");

        return output;
    }

    private static bool HasPrefix(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix) =>
        value.Length >= prefix.Length && value[..prefix.Length].SequenceEqual(prefix);

    internal static uint ComputeSteamAdler32(ReadOnlySpan<byte> bytes)
    {
        const uint modulus = 65521;
        uint a = 0;
        uint b = 0;
        foreach (byte value in bytes)
        {
            a = (a + value) % modulus;
            b = (b + a) % modulus;
        }
        return (b << 16) | a;
    }
}

internal static class SteamDepotCrypto
{
    public static byte[] DecryptWithEncryptedIv(ReadOnlySpan<byte> encrypted, ReadOnlySpan<byte> depotKey)
    {
        if (depotKey.Length != 32)
            throw new InvalidDataException("Steam Depot Key 必须为 32 字节。");
        if (encrypted.Length <= 16 || (encrypted.Length - 16) % 16 != 0)
            throw new InvalidDataException("Steam 加密数据长度或 AES 分块边界无效。");

        byte[] key = depotKey.ToArray();
        byte[] encryptedIv = encrypted[..16].ToArray();
        byte[] iv;
        try
        {
            using var aesEcb = Aes.Create();
            aesEcb.Key = key;
            aesEcb.Mode = CipherMode.ECB;
            aesEcb.Padding = PaddingMode.None;
            using var decryptor = aesEcb.CreateDecryptor();
            iv = decryptor.TransformFinalBlock(encryptedIv, 0, encryptedIv.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptedIv);
        }

        try
        {
            using var aesCbc = Aes.Create();
            aesCbc.Key = key;
            aesCbc.IV = iv;
            aesCbc.Mode = CipherMode.CBC;
            aesCbc.Padding = PaddingMode.PKCS7;
            using var decryptor = aesCbc.CreateDecryptor();
            byte[] ciphertext = encrypted[16..].ToArray();
            try
            {
                return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ciphertext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(iv);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static string DecryptManifestName(string encodedName, ReadOnlySpan<byte> depotKey)
    {
        ArgumentNullException.ThrowIfNull(encodedName);
        string compact = new(encodedName.Where(character => character > ' ').ToArray());
        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(compact);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("Manifest 加密文件名的 Base64 格式无效。", error);
        }

        if (encrypted.Length <= 16)
            throw new InvalidDataException("Manifest 加密文件名数据长度不足。");

        byte[] decrypted = DecryptWithEncryptedIv(encrypted, depotKey);
        CryptographicOperations.ZeroMemory(encrypted);
        try
        {
            int length = decrypted.Length;
            while (length > 0 && decrypted[length - 1] == 0)
                length--;
            string value = new System.Text.UTF8Encoding(false, true)
                .GetString(decrypted, 0, length);
            return value.Replace('\\', '/');
        }
        catch (System.Text.DecoderFallbackException error)
        {
            throw new InvalidDataException("Manifest 文件名解密后不是有效 UTF-8。", error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decrypted);
        }
    }
}
