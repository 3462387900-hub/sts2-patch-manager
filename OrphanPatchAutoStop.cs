using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpirePatch;

internal record PatchRemovalObservation(string Root,string[] ActiveIds,DateTimeOffset Time);
internal record OrphanPatchCandidate(string PatchId,string PatchName,string[] TargetIds);
internal record OrphanPatchOutcome(string[] Disabled,string[] Skipped,ModInventory Inventory)
{
 internal static OrphanPatchOutcome Empty(ModInventory inventory)=>new([],[],inventory);
}

/// <summary>
/// Only an exact, previously observed target removal can auto-disable a recognized standalone fix.
/// The game directory is never written except through ModManager.Preview/Apply.
/// </summary>
internal static class OrphanPatchAutoStop
{
 const int MaxObservationBytes=64*1024;

 static string StatePath(string root,string dataRoot)
 {
  string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())));
  return BundleInstaller.Child(Path.GetFullPath(dataRoot),"孤立补丁监测/"+key+".json");
 }
 static string SettingPath(string dataRoot)=>BundleInstaller.Child(Path.GetFullPath(dataRoot),"自动停用补丁设置.json");

 internal static bool Enabled(string dataRoot)
 {
  try
  {
   string path=SettingPath(dataRoot);
   if(!File.Exists(path))return true;
   using var settings=JsonDocument.Parse(File.ReadAllText(path));
   return settings.RootElement.GetProperty("enabled").GetBoolean();
  }
  catch{return false;} // Fail closed: a corrupt opt-out cannot turn automation on.
 }
 internal static void SetEnabled(string dataRoot,bool enabled)
 {
  string path=SettingPath(dataRoot);
  WriteAtomic(path,JsonSerializer.Serialize(new{enabled}));
 }

 static void WriteAtomic(string path,string text)
 {
  Directory.CreateDirectory(Path.GetDirectoryName(path)!);
  string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
  try{File.WriteAllText(temporary,text);File.Move(temporary,path,true);}
  finally{if(File.Exists(temporary))File.Delete(temporary);}
 }

 static PatchRemovalObservation? Read(string root,string dataRoot)
 {
  string path=StatePath(root,dataRoot);
  if(!File.Exists(path))return null;
  if(new FileInfo(path).Length>MaxObservationBytes)throw new InvalidDataException("已保存的 MOD 观察记录过大，停止自动停用。");
  var state=JsonSerializer.Deserialize<PatchRemovalObservation>(File.ReadAllText(path))??
   throw new InvalidDataException("无法读取以前的 MOD 观察记录。");
  if(!state.Root.Equals(root,StringComparison.OrdinalIgnoreCase)||state.ActiveIds==null||
   state.ActiveIds.Length>6000||state.ActiveIds.Any(s=>string.IsNullOrWhiteSpace(s)||s.Length>160)||
   state.ActiveIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=state.ActiveIds.Length)
   throw new InvalidDataException("MOD 观察记录不属于当前游戏或格式无效，停止自动停用。");
  return state;
 }

 static void Save(string root,string dataRoot,ModInventory inventory)
 {
  if(inventory.Issues.Count!=0)throw new InvalidOperationException("MOD 清单不完整，不能更新自动停用观察记录。");
  var ids=inventory.Mods.Where(x=>x.Enabled).Select(x=>x.Id)
   .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();
  if(ids.Length>6000)throw new InvalidDataException("MOD 数量超过监测记录限制。");
  WriteAtomic(StatePath(root,dataRoot),JsonSerializer.Serialize(new PatchRemovalObservation(root,ids,DateTimeOffset.UtcNow)));
 }

 // 'affected' describes the fixed MOD. Some narrow fixes only declare a 'trigger'.
 // Never treat generic prerequisites, optional companions or the patch's own ID as the target.
 internal static string[] Targets(OnlinePatch patch)
 {
  var parts=patch.Metadata.GetProperty("components").EnumerateArray();
  string[] Pick(string role)=>parts.Where(x=>x.GetProperty("role").GetString()==role)
   .Select(x=>x.GetProperty("mod_id").GetString()!)
   .Where(x=>!x.Equals(patch.ComponentId,StringComparison.OrdinalIgnoreCase))
   .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
  var affected=Pick("affected");
  return affected.Length>0?affected:Pick("trigger");
 }

 internal static OrphanPatchCandidate[] Candidates(PatchRemovalObservation? previous,ModInventory current)
 {
  if(previous==null||current.Issues.Count>0)return [];
  var before=previous.ActiveIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
  var knownNow=current.Mods.Select(m=>m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
  var candidates=new List<OrphanPatchCandidate>();
  foreach(var patch in Catalog.Bundled)
  {
   var targets=Targets(patch);
   if(targets.Length==0||!targets.Any(before.Contains)||targets.Any(knownNow.Contains))continue;
   var matching=current.Mods.Where(m=>m.Enabled&&m.Id.Equals(patch.ComponentId,StringComparison.OrdinalIgnoreCase)
    &&ModPresentation.IsIndependentPatch(m)).ToArray();
   if(matching.Length!=1)continue;
   candidates.Add(new(patch.ComponentId,matching[0].Name,targets));
  }
  return candidates.ToArray();
 }

 internal static OrphanPatchCandidate[] Observe(string root,string dataRoot,ModInventory inventory)
 {
  root=GameEnvironment.Root(root);
  return GameEnvironment.WithLock(root,()=>{
   if(inventory.Issues.Count>0)throw new InvalidDataException("MOD 清单不完整，无法安全检查孤立补丁。");
   var previous=Read(root,dataRoot);
   if(previous==null){Save(root,dataRoot,inventory);return [];}
   var candidates=Candidates(previous,inventory);
   if(candidates.Length==0)Save(root,dataRoot,inventory);
   return candidates;
  });
 }

 static bool Fixture(string root)=>File.Exists(Path.Combine(root,".spirepatch-fixture"))&&
  new FileInfo(Path.Combine(root,"SlayTheSpire2.exe")).Length==0;

 // Test-only bypass of the approved payload fingerprint. Real games MUST pass it.
 internal static OrphanPatchOutcome Reconcile(string root,string dataRoot,bool fixture=false)
 {
  root=GameEnvironment.Root(root);
  if(fixture&&!Fixture(root))throw new InvalidOperationException("自动停用隔离验证只能用于专用模拟游戏目录。");
  return GameEnvironment.WithLock(root,()=>{
   var inventory=ModManager.Scan(root);
   if(inventory.Issues.Count>0)throw new InvalidDataException("模组清单存在问题，已停止自动停用。");
   var previous=Read(root,dataRoot);
   if(previous==null){Save(root,dataRoot,inventory);return OrphanPatchOutcome.Empty(inventory);}
   var candidates=Candidates(previous,inventory);
   if(candidates.Length==0){Save(root,dataRoot,inventory);return OrphanPatchOutcome.Empty(inventory);}
   var stopped=new List<string>();
   var skipped=new List<string>();
   if(GameEnvironment.Running(root))
    return new([],["游戏正在运行，需退出后重新刷新才能停用孤立补丁。"],inventory);
   foreach(var candidate in candidates)
   {
    // Rescan before EACH action: the user may re-add a target while updating files.
    inventory=ModManager.Scan(root);
    if(inventory.Issues.Count>0){skipped.Add("扫描不完整，停止剩余自动操作。");break;}
    if(!Candidates(previous,inventory).Any(x=>x.PatchId.Equals(candidate.PatchId,StringComparison.OrdinalIgnoreCase)))
     continue;
    var mod=inventory.Mods.Single(x=>x.Enabled&&x.Id.Equals(candidate.PatchId,StringComparison.OrdinalIgnoreCase));
    if(!fixture&&!PatchOrganizer.ApprovedStandalone(mod))
    {
     skipped.Add(candidate.PatchName+"：文件指纹不是已审核版本，未自动停用。");
     continue;
    }
    try
    {
     var plan=ModManager.Preview(root,mod.Folder,false);
     // ModManager.Apply revalidates all manifests, exact files and dependency restrictions.
     ModManager.Apply(plan);
     stopped.Add(candidate.PatchName);
    }
    catch(Exception e)when(e is InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
    {
     skipped.Add(candidate.PatchName+"：未能安全停用（"+e.Message+"）。");
    }
   }
   inventory=ModManager.Scan(root);
   if(inventory.Issues.Count==0&&skipped.Count==0)Save(root,dataRoot,inventory);
   return new(stopped.ToArray(),skipped.ToArray(),inventory);
  });
 }
}
