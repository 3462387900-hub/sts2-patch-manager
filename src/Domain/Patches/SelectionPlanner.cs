using System.IO;
using System.Text.Json;

namespace SpirePatch;
public record PatchSelection(string[] Requested,string[] Included,string[] Automatic,ScanReport Report);
public static class SelectionPlanner
{
 public static PatchSelection Resolve(string root,IEnumerable<string> requested)
 {
  var report=Catalog.Scan(root);return Resolve(root,requested,report);
 }
 public static PatchSelection Resolve(string root,IEnumerable<string> requested,ScanReport report)
 {
  if(!report.InventoryComplete||report.CatalogRevision!=Catalog.Revision||!Path.GetFullPath(root).Equals(report.Directory,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("请先完整扫描本版支持的补丁目录。");
  var original=requested.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
  if(original.Length==0)throw new InvalidOperationException("请至少选择一项补丁。");
  var states=report.Patches.ToDictionary(p=>p.Patch.Id,StringComparer.OrdinalIgnoreCase);
  var included=original.ToHashSet(StringComparer.OrdinalIgnoreCase);
  var installed=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach(var file in Directory.EnumerateFiles(Path.Combine(root,"mods"),"*.json",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint,IgnoreInaccessible=false})){
   using var d=JsonDocument.Parse(File.ReadAllText(file));var m=d.RootElement;
   if(m.ValueKind==JsonValueKind.Object&&m.TryGetProperty("id",out var id)&&(m.TryGetProperty("has_dll",out _)||m.TryGetProperty("has_pck",out _)))installed.Add(id.GetString()!);
  }
  bool changed;
  do{
   changed=false;
   foreach(var id in included.ToArray()){
    if(!states.TryGetValue(id,out var state)||state.Status is not("待修复" or "需联动" or "已安装"))throw new InvalidOperationException("不能选择此补丁："+id+"；"+(state?.Reason??"不属于本版已审核的整合包"));
    foreach(var related in state.RelatedPatchIds??[])changed|=included.Add(related);
   }
   // Existing dependent patches must not silently remain on a mismatched build.
   foreach(var p in Catalog.Bundled.Where(p=>installed.Contains(p.ComponentId)&&!included.Contains(p.ComponentId))){
    if(states[p.ComponentId].Status=="已安装")continue;
    bool affected=p.Metadata.GetProperty("compatibility").GetProperty("profiles").EnumerateArray().Any(profile=>profile.GetProperty("required_mods").EnumerateArray().Any(m=>included.Contains(m.GetProperty("mod_id").GetString()!)));
    if(affected)changed|=included.Add(p.ComponentId);
   }
  }while(changed);
  var sorted=included.Order(StringComparer.Ordinal).ToArray();
  return new(original,sorted,sorted.Except(original,StringComparer.OrdinalIgnoreCase).ToArray(),report);
 }
}
