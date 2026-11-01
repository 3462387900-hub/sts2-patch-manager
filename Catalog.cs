using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace SpirePatch;
public record PatchInfo(string Id,string Name,string Category,string Summary,string Detail,string Version,string[] Requirements);
public record PatchState(PatchInfo Patch,string Status,string Reason,bool FilesMatch=false,string[]? RelatedPatchIds=null,string[]? Evidence=null);
public record ScanReport(string Directory,int ModCount,List<PatchState> Patches,List<string> Issues,DateTime Time,string CatalogRevision="",bool InventoryComplete=true);
public static class Catalog
{
 public static JsonDocument Plan()=>JsonDocument.Parse(Assembly.GetExecutingAssembly().GetManifestResourceStream("SpirePatch.catalog-plan.json")!);
 static readonly JsonElement snapshot=ReadSnapshot();
 static JsonElement ReadSnapshot(){using var d=JsonDocument.Parse(Assembly.GetExecutingAssembly().GetManifestResourceStream("SpirePatch.patch-catalog.json")!);return d.RootElement.Clone();}
 public static string Revision=>snapshot.GetProperty("commit").GetString()!;
 public static readonly OnlinePatch[] Bundled=snapshot.GetProperty("patches").EnumerateArray().Select(e=>FromMetadata(e.GetProperty("component_id").GetString()!,e.GetProperty("metadata"))).ToArray();
 public static OnlinePatch FromMetadata(string component,JsonElement m)=>new(m.GetProperty("id").GetString()!,component,m.GetProperty("title").GetString()!,m.GetProperty("summary").GetString()!,m.GetProperty("issue").GetProperty("diagnosis").GetProperty("conclusion").GetString()!,m.GetProperty("limitations").EnumerateArray().Select(x=>x.GetString()!).ToArray(),m.Clone());
 public static PatchInfo Info(OnlinePatch p)=>new(p.ComponentId,p.Title,"模组适配",p.Summary,p.Detail,p.Metadata.GetProperty("version").GetString()!,p.Metadata.GetProperty("components").EnumerateArray().Select(c=>c.GetProperty("mod_id").GetString()!).ToArray());
 public static readonly PatchInfo[] All=Bundled.Select(Info).ToArray();
 public static bool ReviewedBundle(OnlinePatch p)=>Bundled.Any(b=>b.ComponentId==p.ComponentId&&JsonNode.DeepEquals(JsonNode.Parse(b.Metadata.GetRawText()),JsonNode.Parse(p.Metadata.GetRawText())));
 public static List<PatchState> Demo(bool installed=false)=>All.Select(p=>new PatchState(p,!installed&&(p.Id is "Act4FinalAscent" or "SpireTogetherFix")?"待修复":"已安装","演示：与示例模组版本匹配")).ToList();
 public static string Hash(string path){using var s=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(s));}
 public static ScanReport Scan(string root,IReadOnlyList<OnlinePatch>? patches=null,string? revision=null)
 {
  var report=new RecommendationScanner(root,patches??Bundled,revision??Revision).Run();var disabled=ModManager.DisabledIds(report.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
  return report with{Patches=report.Patches.Select(p=>disabled.Contains(p.Patch.Id)?p with{Status="已停用",Reason="已在模组管理中停用；请先启用，再检查修复。",FilesMatch=false,RelatedPatchIds=[]}:p).ToList()};
 }
}
