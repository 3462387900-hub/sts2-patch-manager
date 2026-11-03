using System.IO;
using System.Text.Json;

namespace SpirePatch;

internal enum LifecycleState
{
 Compatible, NeedsReview, SuspectedUpstreamFix, VerifiedUpstreamFix, Incompatible, Unknown, Disabled
}
internal record LifecyclePatchItem(PatchInfo Patch, string InstallState, LifecycleState State,
 string Message, string[] RelatedMods, LifecycleEvidence? Evidence, bool CanRetire, string? BlockReason);
internal record PatchLifecycleReport(ModHistoryResult History, LifecyclePatchItem[] Patches,
 string EvidenceStatus, DateTimeOffset Time, string Root);

internal static class PatchLifecycle
{
 static string[] Related(OnlinePatch patch)
 {
  var metadata=patch.Metadata;
  var ids=metadata.GetProperty("components").EnumerateArray()
   .Where(e=>e.GetProperty("role").GetString() is "affected" or "trigger")
   .Select(e=>e.GetProperty("mod_id").GetString()!).ToList();
  foreach(var profile in metadata.GetProperty("compatibility").GetProperty("profiles").EnumerateArray())
   foreach(var mod in profile.GetProperty("required_mods").EnumerateArray())
    ids.Add(mod.GetProperty("mod_id").GetString()!);
  return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
 }
 internal static bool MatchesEvidence(string root,ModInventory inventory,LifecycleEvidence evidence)
 {
  string path;
  if(evidence.TargetModId.Equals("GAME",StringComparison.OrdinalIgnoreCase))
   path=GameEnvironment.Child(root,evidence.RelativePath);
  else
  {
   var matching=inventory.Mods.Where(m=>m.Enabled&&m.Id.Equals(evidence.TargetModId,StringComparison.OrdinalIgnoreCase)).ToArray();
   if(matching.Length!=1)return false;
   path=GameEnvironment.Child(matching[0].Folder,evidence.RelativePath);
  }
  return File.Exists(path)&&Catalog.Hash(path).Equals(evidence.Sha256,StringComparison.OrdinalIgnoreCase);
 }

 internal static PatchLifecycleReport Evaluate(string root,ModHistoryResult history,LifecycleRegistry registry,
 string evidenceStatus,ScanReport scan,ModInventory inventory)
 {
  if(inventory.Issues.Count>0||!scan.InventoryComplete)
   throw new InvalidDataException("扫描不完整，不能给出补丁退役判断。");
  var changed=history.Changes.Select(m=>m.ModId).ToHashSet(StringComparer.OrdinalIgnoreCase);
  var patches=new List<LifecyclePatchItem>();
  foreach(var meta in Catalog.Bundled)
  {
   var patch=Catalog.Info(meta);
   string[] related=Related(meta);
   var current=scan.Patches.SingleOrDefault(x=>x.Patch.Id==patch.Id);
   if(current==null)continue;
   var installed=inventory.Mods.Where(m=>m.Id.Equals(patch.Id,StringComparison.OrdinalIgnoreCase)).ToArray();
   bool active=installed.Length==1&&installed[0].Enabled;
   bool disabled=installed.Length==1&&!installed[0].Enabled;
   // Evidence must apply to a dependency of this exact patch, not a coincidentally named other MOD.
   var evidence=registry.Entries.Where(e=>e.PatchId.Equals(meta.Id,StringComparison.OrdinalIgnoreCase) &&
     (e.TargetModId.Equals("GAME",StringComparison.OrdinalIgnoreCase)||
       related.Contains(e.TargetModId,StringComparer.OrdinalIgnoreCase)))
     .FirstOrDefault(e=>MatchesEvidence(root,inventory,e));
   var state=LifecycleState.Unknown;
   string reason="没有已确认的上游修复证据；文件扫描不能证明 Bug 已解决。";
   if(disabled)
   {
    state=LifecycleState.Disabled;
    reason="此补丁已经停用，可在模组管理里重新启用。";
   }
   else if(evidence?.ReviewStatus=="approved"&&evidence.Decision=="fixed_upstream")
   {
    state=LifecycleState.VerifiedUpstreamFix;
    reason="当前文件与已审核的上游修复版本指纹一致，可考虑退役旧补丁。";
   }
   else if(evidence?.ReviewStatus=="approved"&&evidence.Decision=="incompatible")
   {
    state=LifecycleState.Incompatible;
    reason="当前文件与已审核的不兼容构建一致，建议停用旧补丁。";
   }
   else if(evidence?.Decision=="suspected_fixed")
   {
    state=LifecycleState.SuspectedUpstreamFix;
    reason="作者更新证据提示可能已修复，但尚未确认，请先复核。";
   }
   else if(related.Any(changed.Contains) ||
           (active&&current.Status is "需核对" or "不适用"))
   {
    state=LifecycleState.NeedsReview;
    reason="相关 MOD 发生变化或现有修复不再匹配已审核的文件条件；暂不能认定 Bug 已解决。";
   }
   else if(current.Status is "已安装" or "待修复" or "需联动")
   {
    state=LifecycleState.Compatible;
    reason="符合目前已知的补丁文件适配条件；不代表已证明所有运行场景无故障。";
   }
   var block=(string?)null;
   bool canRetire=false;
   if(state is LifecycleState.VerifiedUpstreamFix or LifecycleState.Incompatible && active)
   {
    using var plan=Catalog.Plan();
    var component=plan.RootElement.GetProperty("components").EnumerateArray()
     .FirstOrDefault(x=>x.GetProperty("id").GetString()==patch.Id);
    bool standalone=component.ValueKind!=JsonValueKind.Undefined&&!component.GetProperty("required").GetBoolean();
    if(!standalone)block="该修复修改原 MOD 文件，不能直接停用；需安装并验证作者原版更新文件。";
    else block=ModManager.BlockReason(root,installed[0],inventory);
    canRetire=standalone&&block==null;
   }
   patches.Add(new(patch,current.Status,state,reason,related,evidence,canRetire,block));
  }
  return new(history,patches.ToArray(),evidenceStatus,DateTimeOffset.Now,root);
 }

 internal static ModTogglePlan PreviewRetire(PatchLifecycleReport report,string componentId,LifecycleRegistry registry)
 {
  var root=GameEnvironment.Root(report.Root);
  var inventory=ModManager.Scan(root);
  if(inventory.Issues.Count>0)throw new InvalidOperationException("模组清单异常，不能安全停用。");
  var current=Catalog.Scan(root);
  var item=Evaluate(root,report.History,registry,report.EvidenceStatus,current,inventory).Patches
   .SingleOrDefault(p=>p.Patch.Id==componentId)??throw new InvalidOperationException("未识别的退役补丁。");
  if(!item.CanRetire)throw new InvalidOperationException(item.BlockReason??"当前不符合安全退役条件，无法停用。");
  var mod=inventory.Mods.Single(m=>m.Enabled&&m.Id==componentId);
  return ModManager.Preview(root,mod.Folder,false);
 }

 internal static void RecordRetirement(string root,string dataRoot,LifecyclePatchItem item,ModTogglePlan plan)
 {
  string store=BundleInstaller.Child(Path.GetFullPath(dataRoot),"补丁退役记录");
  Directory.CreateDirectory(store);
  string name=DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".json";
  string target=BundleInstaller.Child(store,name);
  File.WriteAllText(target,JsonSerializer.Serialize(new
  {
   time=DateTimeOffset.Now,gameRoot=root,item.Patch.Id,item.Patch.Name,
   state=item.State.ToString(),oldDirectory=plan.Folder,action="mod-disabled-reversible",
   evidence=item.Evidence
  },new JsonSerializerOptions{WriteIndented=true}));
 }
}
