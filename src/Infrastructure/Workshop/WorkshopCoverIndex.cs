using System.Globalization;
using System.Text.Json;

namespace SpirePatch;

// This index supplies artwork only. Installation and update checks use WorkshopModSources.
internal sealed record IndexedWorkshopCover(ulong WorkshopId, string Name, string Author, bool NeedsReview);

internal static class WorkshopCoverIndex
{
    sealed record Document(int Schema, uint AppId, Entry[] Mods);
    sealed record Entry(string ModId, string Name, string Author, string WorkshopId, string Verification);
    static readonly Dictionary<string, IndexedWorkshopCover> Entries = Read();

    static Dictionary<string, IndexedWorkshopCover> Read()
    {
        using var stream = typeof(WorkshopCoverIndex).Assembly.GetManifestResourceStream("SpirePatch.WorkshopSourceIndex.json");
        if (stream == null || stream.Length > 1024 * 1024) return [];
        try
        {
            var index = JsonSerializer.Deserialize<Document>(stream, new JsonSerializerOptions
                { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
            if (index?.Schema != 1 || index.AppId != SpireSupplyStationClient.SupportedAppId || index.Mods == null) return [];
            var entries = new Dictionary<string, IndexedWorkshopCover>(StringComparer.OrdinalIgnoreCase);
            var workshopIds = new HashSet<ulong>();
            foreach (var item in index.Mods)
            {
                if (item == null || item.Verification != "local_binding" || string.IsNullOrWhiteSpace(item.ModId) ||
                    string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(item.Author) ||
                    !ulong.TryParse(item.WorkshopId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
                    id == 0 || !workshopIds.Add(id)) return [];
                // data/README.md identifies the generic author and the republished multiplayer build.
                bool review = string.Equals(item.Author.Trim(), "Author", StringComparison.OrdinalIgnoreCase) ||
                    (item.ModId.Equals("RemoveMultiplayerPlayerLimit", StringComparison.OrdinalIgnoreCase) && id == 3809094500UL);
                if (!entries.TryAdd(item.ModId, new(id, item.Name, item.Author, review))) return [];
            }
            return entries;
        }
        catch (JsonException) { return []; }
    }

    internal static IndexedWorkshopCover? Match(ManagedMod mod, WorkshopModSources? bindings, IReadOnlySet<string> duplicateIds)
    {
        if (duplicateIds.Contains(mod.Id) || bindings?.Get(mod.Id) != null ||
            !Entries.TryGetValue(mod.Id, out var entry) || bindings?.ModIdFor(entry.WorkshopId) != null) return null;
        return string.Equals(mod.Name.Trim(), entry.Name.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(mod.Author.Trim(), entry.Author.Trim(), StringComparison.OrdinalIgnoreCase) ? entry : null;
    }
}
