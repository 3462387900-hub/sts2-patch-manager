using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace SpirePatch;

/// <summary>
/// Remembers the manually confirmed Steam Workshop source for each local MOD.
/// This is metadata only; it never stores CDN tokens or depot keys.
/// </summary>
internal sealed class WorkshopModSources
{
    readonly string file;
    readonly Dictionary<string, string> bindings = new(StringComparer.OrdinalIgnoreCase);

    public WorkshopModSources(string dataRoot, string gameRoot)
    {
        string root = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
        file = Path.Combine(dataRoot, "创意工坊来源", key + ".json");

        if (!File.Exists(file))
            return;

        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
            if (saved is null)
                return;

            foreach (var pair in saved)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) &&
                    ulong.TryParse(pair.Value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) &&
                    id > 0)
                    bindings[pair.Key] = id.ToString(CultureInfo.InvariantCulture);
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

    public ulong? Get(string modId) =>
        bindings.TryGetValue(modId, out string? value) &&
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id)
            ? id
            : null;

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

        bindings[modId] = value;
        Save();
    }

    public void Unbind(string modId)
    {
        if (bindings.Remove(modId))
            Save();
    }

    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(
                bindings,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, file, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
