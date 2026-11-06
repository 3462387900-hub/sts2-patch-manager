using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpirePatch;

// Public, reviewable source map. Never export per-user game paths, manifest
// baselines, hashes, cached download tokens, or personal manager settings.
internal sealed record PublicWorkshopModEntry(
 [property:JsonPropertyName("mod_id")] string ModId,
 [property:JsonPropertyName("name")] string Name,
 [property:JsonPropertyName("author")] string Author,
 [property:JsonPropertyName("workshop_id")] string WorkshopId,
 [property:JsonPropertyName("verification")] string Verification);

internal sealed record PublicWorkshopIndex(
 [property:JsonPropertyName("schema")] int Schema,
 [property:JsonPropertyName("app_id")] uint AppId,
 [property:JsonPropertyName("mods")] PublicWorkshopModEntry[] Mods);

internal static class PublicWorkshopSourceExport
{
 internal const string DefaultFileName="workshop-source-index.json";
 internal static PublicWorkshopIndex Build(IEnumerable<ManagedMod> inventory,WorkshopModSources sources)
 {
  ArgumentNullException.ThrowIfNull(inventory);
  ArgumentNullException.ThrowIfNull(sources);
  var result=new List<PublicWorkshopModEntry>();
  // A repeated internal ID may refer to different installations. If the
  // identities differ, don't publish a potentially misleading global mapping.
  foreach(var group in inventory.Where(mod=>!ModPresentation.IsIndependentPatch(mod))
    .GroupBy(mod=>mod.Id,StringComparer.OrdinalIgnoreCase))
  {
   if(string.IsNullOrWhiteSpace(group.Key)||group.Key.Length>256)continue;
   var variants=group.Select(m=>new {Name=m.Name.Trim(),Author=m.Author.Trim()})
    .Distinct().ToArray();
   if(variants.Length!=1)continue;
   ulong? id=sources.Get(group.Key);
   if(id is not >0)continue;
   string name=variants[0].Name,author=variants[0].Author;
   if(name.Length is <1 or >200||author.Length>200)continue;
   result.Add(new PublicWorkshopModEntry(group.Key,name,author,
    id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    "local_binding"));
  }
  return new PublicWorkshopIndex(1,SpireSupplyStationClient.SupportedAppId,
   result.OrderBy(item=>item.ModId,StringComparer.OrdinalIgnoreCase).ToArray());
 }
 internal static string Serialize(PublicWorkshopIndex index)=>
  JsonSerializer.Serialize(index,new JsonSerializerOptions{WriteIndented=true})+"\n";
}
