using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace SpirePatch;

internal sealed record WorkshopModBaseline(
    ulong WorkshopId,
    string ManifestId,
    string LocalVersion,
    string LocalSnapshotSha256,
    DateTimeOffset RecordedAt);

/// <summary>
/// Remembers the manually confirmed Steam Workshop source and the last verified
/// installed Manifest identity for each local MOD. Never stores CDN tokens or depot keys.
/// </summary>
internal sealed class WorkshopModSources
{
    readonly string file;
    readonly string baselineFile;
    readonly Dictionary<string, string> bindings = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, WorkshopModBaseline> baselines = new(StringComparer.OrdinalIgnoreCase);

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public WorkshopModSources(string dataRoot, string gameRoot)
    {
        string root = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
        string directory = Path.Combine(dataRoot, "创意工坊来源");
        file = Path.Combine(directory, key + ".json");
        baselineFile = Path.Combine(directory, key + ".versions.json");

        if (File.Exists(file))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                if (saved is not null)
                {
                    foreach (var pair in saved)
                    {
                        if (!string.IsNullOrWhiteSpace(pair.Key) &&
                            ulong.TryParse(pair.Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) &&
                            id > 0)
                            bindings[pair.Key] = id.ToString(CultureInfo.InvariantCulture);
                    }
                }
            }
            catch (JsonException)
            {
                // Broken source preferences must never prevent the manager from opening.
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        if (File.Exists(baselineFile))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<Dictionary<string, WorkshopModBaseline>>(
                    File.ReadAllText(baselineFile));
                if (saved is not null)
                {
                    foreach (var pair in saved)
                    {
                        var baseline = pair.Value;
                        if (!string.IsNullOrWhiteSpace(pair.Key) &&
                            baseline is not null &&
                            baseline.WorkshopId > 0 &&
                            ulong.TryParse(baseline.ManifestId, NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
                            !string.IsNullOrWhiteSpace(baseline.LocalVersion) &&
                            IsSha256(baseline.LocalSnapshotSha256))
                            baselines[pair.Key] = baseline;
                    }
                }
            }
            catch (JsonException)
            {
                // A broken baseline is treated as missing and must be rebuilt by verification.
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public ulong? Get(string modId) =>
        bindings.TryGetValue(modId, out string? value) &&
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)
            ? id
            : null;

    public string? ModIdFor(ulong workshopId)
    {
        string value = workshopId.ToString(CultureInfo.InvariantCulture);
        return bindings.FirstOrDefault(pair => pair.Value == value).Key;
    }

    public WorkshopModBaseline? GetBaseline(string modId)
    {
        if (!baselines.TryGetValue(modId, out var baseline))
            return null;
        return Get(modId) == baseline.WorkshopId ? baseline : null;
    }

    public bool IsBaselineCurrent(ManagedMod mod, ulong workshopId)
    {
        var baseline = GetBaseline(mod.Id);
        if (baseline is null ||
            baseline.WorkshopId != workshopId ||
            !baseline.LocalVersion.Equals(mod.Version, StringComparison.Ordinal))
            return false;

        try
        {
            return Fingerprint(ModManager.FileSnapshot(mod.Folder))
                .Equals(baseline.LocalSnapshotSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    public void Bind(string modId, ulong workshopId)
    {
        if (string.IsNullOrWhiteSpace(modId) || modId.Length > 256)
            throw new ArgumentException("MOD ID 无效。", nameof(modId));
        if (workshopId == 0)
            throw new ArgumentOutOfRangeException(nameof(workshopId), "Workshop ID 不能为 0。");

        string value = workshopId.ToString(CultureInfo.InvariantCulture);
        var conflict = bindings.FirstOrDefault(pair =>
            pair.Value == value && !pair.Key.Equals(modId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(conflict.Key))
            throw new InvalidOperationException("这个 Workshop ID 已关联到另一个 MOD ID：" + conflict.Key);

        if (bindings.TryGetValue(modId, out var previous) &&
            !previous.Equals(value, StringComparison.Ordinal) &&
            baselines.Remove(modId))
            SaveBaselines();

        bindings[modId] = value;
        Save();
    }

    public void RecordBaseline(
        string modId,
        ulong workshopId,
        string manifestId,
        string localVersion,
        IReadOnlyDictionary<string, string> localFiles)
    {
        if (string.IsNullOrWhiteSpace(modId) || modId.Length > 256)
            throw new ArgumentException("MOD ID 无效。", nameof(modId));
        if (Get(modId) != workshopId || workshopId == 0)
            throw new InvalidOperationException("Workshop 来源尚未正确关联，不能记录版本基线。");
        if (!ulong.TryParse(manifestId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong manifestNumber))
            throw new ArgumentException("Manifest ID 无效。", nameof(manifestId));
        manifestId = manifestNumber.ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(localVersion))
            throw new ArgumentException("本地 MOD 版本无效。", nameof(localVersion));
        if (localFiles is null || localFiles.Count == 0)
            throw new ArgumentException("本地 MOD 文件快照为空，不能建立版本基线。", nameof(localFiles));

        baselines[modId] = new WorkshopModBaseline(
            workshopId,
            manifestId,
            localVersion,
            Fingerprint(localFiles),
            DateTimeOffset.UtcNow);
        SaveBaselines();
    }

    public void ClearBaseline(string modId)
    {
        if (baselines.Remove(modId))
            SaveBaselines();
    }

    public void Unbind(string modId)
    {
        // Remove the baseline first. If persistence fails, retain the source binding
        // rather than leave an old baseline attached to a source the user tried to remove.
        if (baselines.Remove(modId))
            SaveBaselines();
        if (bindings.Remove(modId))
            Save();
    }

    internal static string Fingerprint(IReadOnlyDictionary<string, string> files)
    {
        var canonical = new StringBuilder();
        foreach (var pair in files.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            canonical.Append(pair.Key.Replace('\\', '/'))
                .Append((char)0)
                .Append(pair.Value.ToUpperInvariant())
                .Append((char)10);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(character => Uri.IsHexDigit(character));

    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(bindings, JsonOptions));
            File.Move(temp, file, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    void SaveBaselines()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(baselineFile)!);
        string temp = baselineFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(baselines, JsonOptions));
            File.Move(temp, baselineFile, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
