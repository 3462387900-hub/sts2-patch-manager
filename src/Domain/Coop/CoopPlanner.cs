using System.IO;
using System.Text.Json;

namespace SpirePatch;
internal enum CoopAction { None, Install, Disable, Enable }
internal record CoopDifference(string Id,string Name,string Mine,string Peer,string Diagnosis,CoopAction Action,string Detail)
{
 public bool Selectable=>Action!=CoopAction.None;
 public string ActionLabel=>Action switch{CoopAction.Install=>"下载并安装",CoopAction.Disable=>"移出并保留",CoopAction.Enable=>"恢复启用",_=>Diagnosis=="停用记录不同"?"无需处理":"需人工核对"};
}
internal static class CoopPlanner
{
 internal static string Value(JsonElement p,string name)=>p.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??"":"";
 internal static bool Standalone(string id){using var plan=Catalog.Plan();return plan.RootElement.GetProperty("components").EnumerateArray().Any(p=>p.GetProperty("id").GetString()==id&&!p.GetProperty("required").GetBoolean());}
 static bool Complete(JsonElement r)=>r.GetProperty("inventoryComplete").GetBoolean()&&r.GetProperty("issues").GetArrayLength()==0;
 internal static bool PresenceReport(JsonElement r)=>r.TryGetProperty("presenceEvidenceVersion",out var v)&&v.GetInt32()==1;
 internal static bool WantsInstalled(JsonElement p)=>Value(p,"presence")=="active"&&Value(p,"status")=="已安装"&&p.GetProperty("filesMatch").GetBoolean();
 public static List<CoopDifference> Analyze(JsonElement local,JsonElement peer,ScanReport report,ModInventory inventory)
 {
  _=CoopReports.Compare(local,peer);var mine=CoopReports.Patches(local);var theirs=CoopReports.Patches(peer);var rows=new List<CoopDifference>();
  foreach(string id in mine.Keys.Union(theirs.Keys,StringComparer.OrdinalIgnoreCase)){
   bool a=mine.TryGetValue(id,out var me),b=theirs.TryGetValue(id,out var them);
   string ms=a?Value(me,"status"):"未记录",ts=b?Value(them,"status"):"未记录",mp=a?Value(me,"presence"):"",tp=b?Value(them,"presence"):"";
   if(a&&b&&ms==ts&&(!PresenceReport(local)||!PresenceReport(peer)||mp==tp)&&me.GetProperty("filesMatch").GetBoolean()==them.GetProperty("filesMatch").GetBoolean()&&ms!="需核对")continue;
   string diagnosis="版本或文件不同",detail="无法自动判断目标版本，请双方核对原 MOD 与补丁版本。";CoopAction action=CoopAction.None;
   var known=Catalog.Bundled.FirstOrDefault(p=>p.ComponentId.Equals(id,StringComparison.OrdinalIgnoreCase));
   var mod=inventory.Mods.FirstOrDefault(m=>m.Id.Equals(id,StringComparison.OrdinalIgnoreCase));
   if(mp=="absent"&&tp=="active")diagnosis="本机缺少";
   else if(mp=="active"&&tp is "absent" or "disabled")diagnosis="本机多出";
   else if(mp=="disabled"&&tp=="active")diagnosis="本机已停用";
   else if(mp is "absent" or "disabled"&&tp is "absent" or "disabled")diagnosis="停用记录不同";
   if(!a||!b){diagnosis="清单未记录";detail="清单中没有记录不等于缺少补丁，请双方更新管理器后重新导出。";}
   else if(Value(local,"mode")!="read-only"){detail="演示清单不能用于真实处理。";}
   else if(!PresenceReport(local)||!PresenceReport(peer)){detail="旧清单没有实际存在信息。请双方使用 0.9.0 或更新版本重新导出后处理。";}
   else if(!Complete(local)||!Complete(peer)||inventory.Issues.Count>0){detail="扫描不完整或存在清单问题，请先处理后重新导出。";}
   else if(mp=="unknown"||tp=="unknown"||ms=="需核对"||ts=="需核对"){diagnosis="文件需核对";detail="存在未知版本、残留或读取问题，不能自动覆盖或移出。";}
   else if(known==null||report.CatalogRevision!=Catalog.Revision||Value(them,"patchVersion")!=Catalog.Info(known).Version){detail="此版本不属于本管理器已审核的修复包，请更新管理器或人工核对。";}
   else if((ts=="已安装"&&!WantsInstalled(them))||(tp=="absent"&&ts is not("待修复" or "需联动" or "不适用"))||(tp=="disabled"&&ts!="已停用")){detail="队友清单的存在信息与状态矛盾，请重新导出。";}
   else if(WantsInstalled(them)&&ms is "待修复" or "需联动"){
    action=CoopAction.Install;detail=mp=="absent"?"从已审核修复包下载缺失项；安装前再次核对适配并备份。":"升级到队友已校验的修复版本；已匹配文件跳过。";
   }
   else if(WantsInstalled(them)&&ms=="已停用"&&mod!=null){
    string? blocked=RestoreReason(report.Directory,mod,inventory,known,report);
    if(blocked==null){action=CoopAction.Enable;detail="本地已保留匹配的修复文件，直接恢复启用，无需重复下载。";}else detail=blocked;
   }
   else if(mp=="active"&&tp is "absent" or "disabled"&&ms=="已安装"&&mod!=null){
    if(!Standalone(id)){diagnosis="原 MOD 内置修复不同";detail="这项修复直接修改原 MOD，不能移走整个 MOD。请核对双方原 MOD 版本或在安装与回滚中检查备份。";}
    else {
     string? blocked=ModManager.BlockReason(report.Directory,mod,inventory);
     var dependent=Catalog.Bundled.FirstOrDefault(p=>p.ComponentId!=id&&inventory.Mods.Any(m=>m.Enabled&&m.Id==p.ComponentId)&&(
      p.Metadata.GetProperty("relationships").GetProperty("requires_patches").EnumerateArray().Any(r=>Value(r,"patch_id")==known.Id)||
      p.Metadata.GetProperty("relationships").GetProperty("requires_when_mod_present").EnumerateArray().Any(r=>Value(r,"patch_id")==known.Id&&inventory.Mods.Any(m=>m.Enabled&&m.Id==Value(r,"mod_id")))||
      p.Metadata.GetProperty("compatibility").GetProperty("profiles").EnumerateArray().Any(pr=>pr.GetProperty("required_mods").EnumerateArray().Any(m=>Value(m,"mod_id")==id))));
     if(dependent!=null)blocked="启用中的修复依赖此项，请先核对关联修复："+dependent.Title;
     if(blocked==null){action=CoopAction.Disable;detail="以队友清单为目标，将独立补丁移出 mods 并保留完整文件，可在模组管理中恢复。";}else detail=blocked;
    }
   }
   else if(mp is "absent" or "disabled"&&tp is "absent" or "disabled"){detail="双方都没有启用此补丁，无需下载或删除停用备份。";}
   else if(ms=="不适用"&&tp=="active"){detail="本机缺少适用的原 MOD 组合。请先核对原 MOD，不能只安装补丁。";}
   else if(ms=="已安装"&&tp=="active"){detail="本机修复已匹配，队友尚未匹配。建议队友更新，不自动降级本机。";}
   rows.Add(new(id,known?.Title??Value(a?me:them,"name"),ms,ts,diagnosis,action,detail));
  }
  return rows;
 }
 static string? RestoreReason(string root,ManagedMod mod,ModInventory inventory,OnlinePatch patch,ScanReport report)
 {
  try{
   string? reason=ModManager.BlockReason(root,mod,inventory);if(reason!=null)return reason;
   _=ModManager.Preview(root,mod.Folder,true);
   string Actual(string id,string relative){var m=id==mod.Id?mod:inventory.Mods.SingleOrDefault(m=>m.Enabled&&m.Id==id);string path=id.Length==0?GameEnvironment.Child(root,relative):m==null?"":relative.Equals(id+".json",StringComparison.OrdinalIgnoreCase)?m.Manifest:GameEnvironment.Child(m.Folder,relative);return File.Exists(path)?Catalog.Hash(path):"";}
   var meta=patch.Metadata;
   if(!meta.GetProperty("detection").GetProperty("installed_fingerprints").EnumerateArray().All(f=>Actual(Value(f,"mod_id"),Value(f,"relative_path"))==Value(f,"sha256").ToUpperInvariant()))return "停用区文件不是队友使用的已收录版本，请先在模组管理核对。";
   bool fits=meta.GetProperty("compatibility").GetProperty("profiles").EnumerateArray().Any(p=>
    p.GetProperty("game_files").EnumerateArray().All(f=>Actual("",Value(f,"relative_path"))==Value(f,"sha256").ToUpperInvariant())&&
    p.GetProperty("required_mods").EnumerateArray().All(m=>m.GetProperty("files").EnumerateArray().All(f=>Actual(Value(m,"mod_id"),Value(f,"relative_path"))==Value(f,"sha256").ToUpperInvariant()))&&
    !p.GetProperty("excluded_mod_ids").EnumerateArray().Any(x=>inventory.Mods.Any(m=>m.Enabled&&m.Id==x.GetString())));
   if(!fits)return "停用补丁的当前前置版本不匹配，请核对后再启用。";
   var relations=meta.GetProperty("relationships");var required=relations.GetProperty("requires_patches").EnumerateArray().Concat(relations.GetProperty("requires_when_mod_present").EnumerateArray().Where(r=>inventory.Mods.Any(m=>m.Enabled&&m.Id==Value(r,"mod_id"))));
   if(required.Any(r=>!Catalog.Bundled.Any(p=>p.Id==Value(r,"patch_id")&&Version.Parse(Catalog.Info(p).Version)>=Version.Parse(Value(r,"minimum_version"))&&report.Patches.Any(s=>s.Patch.Id==p.ComponentId&&s.Status=="已安装")))||relations.GetProperty("conflicts_patch_ids").EnumerateArray().Any(r=>Catalog.Bundled.Any(p=>p.Id==r.GetString()&&inventory.Mods.Any(m=>m.Enabled&&m.Id==p.ComponentId))))return "关联补丁尚未就绪或存在冲突，请先核对关联项。";
   if(Catalog.Bundled.Where(p=>inventory.Mods.Any(m=>m.Enabled&&m.Id==p.ComponentId)).Any(p=>p.Metadata.GetProperty("relationships").GetProperty("conflicts_patch_ids").EnumerateArray().Any(c=>c.GetString()==patch.Id)||p.Metadata.GetProperty("compatibility").GetProperty("profiles").EnumerateArray().All(pr=>pr.GetProperty("excluded_mod_ids").EnumerateArray().Any(c=>c.GetString()==mod.Id))))return "当前启用的补丁声明与此项冲突，请先核对。";
   return null;
  }catch(Exception e){return "恢复前检查未通过："+e.Message;}
 }
}
