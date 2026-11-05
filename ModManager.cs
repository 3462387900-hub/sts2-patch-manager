using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpirePatch;
public record ModDependency(string Id,string Minimum="",string Maximum="");
public record ManagedMod(string Id,string Name,string Version,string Author,string Description,string Folder,string Manifest,bool Enabled,ModDependency[] Dependencies,string? Storage=null,string? Original=null);
public record ModInventory(List<ManagedMod> Mods,List<string> Issues);
public record ModTogglePlan(string Root,string Folder,bool Enable,string Name,string Token,int FileCount);
public record DisabledModRecord(string ModId,string Original,Dictionary<string,string> Files,string Status);
public static class ModManager
{
 public const string StorageName="已停用模组_尖塔修补站";
 static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true};
 static string Text(JsonElement e,string key,string fallback="")=>e.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()??fallback:fallback;
 static ManagedMod? ReadManifest(string path,bool enabled,string? storage=null,string? original=null)
 {
  using var doc=JsonDocument.Parse(File.ReadAllText(path));var m=doc.RootElement;
  if(m.ValueKind!=JsonValueKind.Object||!m.TryGetProperty("id",out _)||!(m.TryGetProperty("has_dll",out _)||m.TryGetProperty("has_pck",out _)))return null;
  string id=Text(m,"id");if(string.IsNullOrWhiteSpace(id))throw new InvalidDataException("模组清单缺少 ID："+path);
  var deps=new List<ModDependency>();
  if(m.TryGetProperty("dependencies",out var dependencies))foreach(var d in dependencies.EnumerateArray()){
   if(d.ValueKind==JsonValueKind.String){deps.Add(new(d.GetString()!));continue;}
   if(d.TryGetProperty("optional",out var optional)&&optional.ValueKind==JsonValueKind.True)continue;
   string dep=Text(d,"id");if(dep.Length==0)throw new InvalidDataException("无法识别依赖声明："+id);deps.Add(new(dep,Text(d,"min_version"),Text(d,"max_version")));
  }
  return new(id,Text(m,"name",id),Text(m,"version","未声明"),Text(m,"author","未声明"),Text(m,"description"),Path.GetDirectoryName(path)!,path,enabled,deps.ToArray(),storage,original);
 }
 static IEnumerable<string> Files(string directory)
 {
  GameEnvironment.NoLinks(directory);
  foreach(var item in Directory.EnumerateFileSystemEntries(directory)){
   if((File.GetAttributes(item)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("模组中存在链接路径："+item);
   if(Directory.Exists(item)){foreach(var file in Files(item))yield return file;}else yield return item;
  }
 }
 static Dictionary<string,string> Snapshot(string directory)=>Files(directory).Order(StringComparer.OrdinalIgnoreCase).ToDictionary(f=>Path.GetRelativePath(directory,f).Replace('\\','/'),Catalog.Hash,StringComparer.OrdinalIgnoreCase);
 internal static Dictionary<string,string> FileSnapshot(string directory)=>Snapshot(directory);
 internal static void ApplyReviewed(ModTogglePlan plan,IReadOnlyDictionary<string,string> reviewedFiles)=>GameEnvironment.WithLock(plan.Root,()=>{
  var current=Snapshot(plan.Folder);
  if(current.Count!=reviewedFiles.Count||current.Any(f=>!reviewedFiles.TryGetValue(f.Key,out var hash)||hash!=f.Value))throw new InvalidOperationException("待移动文件已变化，请重新查看方案。");
  // Other approved steps may have changed manifests; recheck dependencies and bind
  // a new toggle token while holding the same game lock as the actual move.
  Apply(Preview(plan.Root,plan.Folder,plan.Enable));return true;
 });
 static IEnumerable<(string Slot,DisabledModRecord Record)> Stored(string root)=>GameEnvironment.WithLock(root,()=>StoredLocked(root).ToArray());
 static IEnumerable<(string Slot,DisabledModRecord Record)> StoredLocked(string root)
 {
  string parent=GameEnvironment.Child(root,StorageName);if(!Directory.Exists(parent))yield break;
  foreach(var slot in Directory.EnumerateDirectories(parent)){
   GameEnvironment.NoLinks(slot);string recordPath=GameEnvironment.Child(slot,"记录.json");
   if(!File.Exists(recordPath)){
    // A crash may occur immediately after creating the empty transaction slot.
    if(!Directory.EnumerateFileSystemEntries(slot).Any())continue;
    throw new InvalidOperationException("停用区记录缺失，请核对："+slot);
   }
   var r=JsonSerializer.Deserialize<DisabledModRecord>(File.ReadAllText(recordPath))??throw new InvalidDataException("停用记录无效");
   if(r.Status is "enabled" or "failed-restored")continue;
   if(r.Status is not("disabled" or "prepared"))throw new InvalidDataException("未知停用记录状态");
   string original=GameEnvironment.Child(root,r.Original);string prefix=Path.Combine(root,"mods").TrimEnd('\\')+"\\";
   if(!original.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("停用记录的原目录超出 mods 范围。");
   string payload=GameEnvironment.Child(slot,"内容");
   if(!Directory.Exists(payload)){
    // No file moves during recovery: only finalize a journal after exact verification.
    if(File.Exists(payload)||!Directory.Exists(original)||!Matches(original,r.Files))throw new InvalidOperationException("停用事务中断且文件无法核对，请保留两处目录并核对："+slot);
    WriteRecord(slot,r with{Status=r.Status=="prepared"?"failed-restored":"enabled"});continue;
   }
   if(r.Status=="prepared"){
    if(File.Exists(original)||Directory.Exists(original)||!Matches(payload,r.Files))throw new InvalidOperationException("停用事务中断且目录冲突或内容变化，请核对："+slot);
    WriteRecord(slot,r with{Status="disabled"});r=r with{Status="disabled"};
   }
   yield return(slot,r);
  }
 }
 static bool Matches(string directory,Dictionary<string,string> expected){
  if(expected==null||expected.Count==0)return false;
  var actual=Snapshot(directory);return actual.Count==expected.Count&&actual.All(f=>expected.TryGetValue(f.Key,out var hash)&&hash==f.Value);
 }
 public static string[] DisabledIds(string root)=>Stored(root).Select(x=>x.Record.ModId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
 public static ModInventory Scan(string path)
 {
  string root=GameEnvironment.Root(path);var mods=new List<ManagedMod>();var issues=new List<string>();
  void Read(string directory,bool enabled,string? slot=null,string? original=null){
   try{foreach(var file in Files(directory).Where(f=>f.EndsWith(".json",StringComparison.OrdinalIgnoreCase))){try{var mod=ReadManifest(file,enabled,slot,original);if(mod!=null)mods.Add(mod);}catch(Exception e)when(e is InvalidDataException or IOException or JsonException or InvalidOperationException or UnauthorizedAccessException){issues.Add("无法核对清单 "+file+"："+e.Message);}}}
   catch(Exception e)when(e is InvalidDataException or IOException or InvalidOperationException or UnauthorizedAccessException){issues.Add(e.Message);}
  }
  string active=GameEnvironment.Child(root,"mods");if(Directory.Exists(active))Read(active,true);
  try{foreach(var stored in Stored(root)){
   int start=mods.Count;Read(Path.Combine(stored.Slot,"内容"),false,stored.Slot,stored.Record.Original);
   if(mods.Count-start!=1||mods[start].Id!=stored.Record.ModId)issues.Add("停用记录与模组 ID 不一致："+stored.Slot);
  }}catch(Exception e)when(e is InvalidDataException or IOException or JsonException or InvalidOperationException or UnauthorizedAccessException){issues.Add(e.Message);}
  foreach(var group in mods.GroupBy(m=>m.Id,StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()>1))issues.Add("存在重复模组 ID（含停用区）："+group.Key);
  return new(mods.OrderBy(m=>m.Name,StringComparer.CurrentCultureIgnoreCase).ToList(),issues);
 }
 internal static IEnumerable<string> RequiredByPatch(string id)
 {
  var patch=Catalog.Bundled.FirstOrDefault(p=>p.ComponentId.Equals(id,StringComparison.OrdinalIgnoreCase));if(patch==null)return [];
  var sets=patch.Metadata.GetProperty("compatibility").GetProperty("profiles").EnumerateArray().Select(p=>p.GetProperty("required_mods").EnumerateArray().Select(m=>m.GetProperty("mod_id").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase)).ToArray();
  if(sets.Length==0)return [];var common=new HashSet<string>(sets[0],StringComparer.OrdinalIgnoreCase);foreach(var set in sets.Skip(1))common.IntersectWith(set);common.Remove(id);return common;
 }
 public static string? BlockReason(string root,ManagedMod mod,ModInventory inventory)
 {
  if(inventory.Issues.Count>0)return "先处理清单读取或重复 ID 问题，再启用/停用。";
  var enabled=inventory.Mods.Where(m=>m.Enabled).ToArray();
  if(mod.Enabled){
   string mods=Path.Combine(root,"mods").TrimEnd('\\');
   if(mod.Folder.Equals(mods,StringComparison.OrdinalIgnoreCase))return "清单位于 mods 根目录，无法作为独立模组移动。";
   if(inventory.Mods.Any(m=>m!=mod&&m.Enabled&&!m.Folder.TrimEnd('\\').Equals(mods,StringComparison.OrdinalIgnoreCase)&&(m.Folder.Equals(mod.Folder,StringComparison.OrdinalIgnoreCase)||m.Folder.StartsWith(mod.Folder.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)||mod.Folder.StartsWith(m.Folder.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))))return "此目录与其他模组共用或嵌套，请先手动整理为独立目录。";
   var dependents=enabled.Where(m=>m!=mod&&(m.Dependencies.Any(d=>d.Id.Equals(mod.Id,StringComparison.OrdinalIgnoreCase))||RequiredByPatch(m.Id).Contains(mod.Id,StringComparer.OrdinalIgnoreCase))).Select(m=>m.Name).ToArray();
   if(dependents.Length>0)return "这些启用项依赖它，请先停用依赖项："+string.Join("、",dependents);
  }else{
   if(mod.Original==null)return "缺少原目录记录。";
   string destination=GameEnvironment.Child(root,mod.Original);if(Directory.Exists(destination)||File.Exists(destination))return "原位置已被占用，不能覆盖："+mod.Original;
   foreach(var dependency in mod.Dependencies.Concat(RequiredByPatch(mod.Id).Select(id=>new ModDependency(id)))){
    var actual=enabled.FirstOrDefault(m=>m.Id.Equals(dependency.Id,StringComparison.OrdinalIgnoreCase));if(actual==null)return "请先启用前置模组："+dependency.Id;
    if(dependency.Minimum.Length>0||dependency.Maximum.Length>0){
     if(!Version.TryParse(actual.Version.TrimStart('v','V'),out var version))return "无法核对前置版本："+actual.Id+" "+actual.Version;
     if(dependency.Minimum.Length>0&&(!Version.TryParse(dependency.Minimum.TrimStart('v','V'),out var min)||version<min))return "前置版本未满足："+actual.Id+"，最低 "+dependency.Minimum;
     if(dependency.Maximum.Length>0&&(!Version.TryParse(dependency.Maximum.TrimStart('v','V'),out var max)||version>max))return "前置版本未满足："+actual.Id+"，最高 "+dependency.Maximum;
    }
   }
  }
  return null;
 }
 static (ManagedMod Mod,Dictionary<string,string> Files,string Token) State(string root,string folder,bool enable)
 {
  root=GameEnvironment.Root(root);var inventory=Scan(root);var mod=inventory.Mods.SingleOrDefault(m=>m.Folder.Equals(folder,StringComparison.OrdinalIgnoreCase)&&m.Enabled!=enable)??throw new InvalidOperationException("模组状态已改变，请刷新列表。");
  string? reason=BlockReason(root,mod,inventory);if(reason!=null)throw new InvalidOperationException(reason);
  var files=Snapshot(mod.Folder);
  if(enable){var record=Stored(root).Single(x=>x.Slot==mod.Storage).Record;if(files.Count!=record.Files.Count||files.Any(f=>!record.Files.TryGetValue(f.Key,out var h)||h!=f.Value))throw new InvalidOperationException("停用期间文件被修改，不能直接恢复，请先核对保留区内容。");}
  var manifests=inventory.Mods.OrderBy(m=>m.Manifest,StringComparer.OrdinalIgnoreCase).Select(m=>new{m.Manifest,m.Enabled,Hash=Catalog.Hash(m.Manifest)}).ToArray();
  string token=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{root,folder,enable,files,manifests}))));return(mod,files,token);
 }
 public static ModTogglePlan Preview(string root,string folder,bool enable)=>GameEnvironment.WithLock(GameEnvironment.Root(root),()=>{
  if(GameEnvironment.Running(root))throw new InvalidOperationException("请先退出此目录的游戏，再启用或停用模组。");
  var state=State(root,folder,enable);return new ModTogglePlan(GameEnvironment.Root(root),folder,enable,state.Mod.Name,state.Token,state.Files.Count);
 });
 static void WriteRecord(string slot,DisabledModRecord record){string path=Path.Combine(slot,"记录.json"),temp=path+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(record,JsonOptions));File.Move(temp,path,true);}
 public static void Apply(ModTogglePlan plan)=>GameEnvironment.WithLock(GameEnvironment.Root(plan.Root),()=>{
  if(GameEnvironment.Running(plan.Root))throw new InvalidOperationException("游戏正在运行，请先退出。");
  var state=State(plan.Root,plan.Folder,plan.Enable);if(state.Token!=plan.Token)throw new InvalidOperationException("清单或文件已变化，请重新查看改动后确认。");
  var mod=state.Mod;
  if(plan.Enable){
   string slot=mod.Storage!,destination=GameEnvironment.Child(plan.Root,mod.Original!);var record=Stored(plan.Root).Single(x=>x.Slot==slot).Record;
   Directory.CreateDirectory(Path.GetDirectoryName(destination)!);Directory.Move(mod.Folder,destination);
   try{WriteRecord(slot,record with{Status="enabled"});}catch{Directory.Move(destination,mod.Folder);throw;}
  }else{
   string parent=GameEnvironment.Child(plan.Root,StorageName);Directory.CreateDirectory(parent);string slot=GameEnvironment.Child(parent,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(slot);
   string destination=GameEnvironment.Child(slot,"内容");var record=new DisabledModRecord(mod.Id,Path.GetRelativePath(plan.Root,mod.Folder),state.Files,"prepared");WriteRecord(slot,record);
   try{Directory.Move(mod.Folder,destination);WriteRecord(slot,record with{Status="disabled"});}
   catch{if(Directory.Exists(destination)&&!Directory.Exists(mod.Folder))Directory.Move(destination,mod.Folder);WriteRecord(slot,record with{Status="failed-restored"});throw;}
  }
  return true;
 });
}
