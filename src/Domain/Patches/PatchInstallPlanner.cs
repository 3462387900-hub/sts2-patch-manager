using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpirePatch;

internal sealed record PatchPlannedFile(string Relative,string Target,string Source,bool Existed,string OldHash,string NewHash,string Operation);
internal sealed record PatchPlanContext(string Path,string Sha256);
internal sealed record PatchInstallPlan(string Root,string Version,PatchPlannedFile[] All,PatchPlannedFile[] Changes,string[] SelectedIds,PatchPlanContext[] Context,string Token);

/// <summary>Builds the exact file plan for the reviewed archive without executing package scripts.</summary>
internal static class PatchInstallPlanner
{
 static readonly StringComparer Paths=StringComparer.OrdinalIgnoreCase;
 static string Text(JsonElement value,string name,string fallback="") =>
  value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(name,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString()??fallback:fallback;
 static bool Bool(JsonElement value,string name,bool fallback=false) =>
  value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(name,out var p)&&p.ValueKind is JsonValueKind.True or JsonValueKind.False?p.GetBoolean():fallback;
 static JsonElement Array(JsonElement value,string name) =>
  value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(name,out var p)&&p.ValueKind==JsonValueKind.Array?p:default;
 static string Relative(string root,string path)=>Path.GetRelativePath(root,path).Replace('/','\\');

 internal static PatchInstallPlan Create(PreparedPackage package,string gameRoot,IEnumerable<string>? selectedIds=null)
 {
  string root=GameEnvironment.Root(gameRoot);
  GameEnvironment.NoLinks(package.Directory);
  string planPath=BundleInstaller.Child(package.Directory,"plan.json");
  using var document=JsonDocument.Parse(File.ReadAllText(planPath));
  var spec=document.RootElement;
  string version=Text(spec,"version");
  if(version.Length==0)throw new InvalidDataException("补丁包缺少版本号。");
  var components=Array(spec,"components").EnumerateArray().Select(x=>x.Clone()).ToArray();
  if(components.Length==0)throw new InvalidDataException("补丁包没有可安装组件。");
  var inventory=Inventory(root);
  var selected=(selectedIds??[]).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
  AssertEnabledScope(root,components,selected);
  if(selected.Length==0)return CreateFull(package,root,spec,components,inventory,version);
  return CreateSelected(package,root,spec,components,inventory,version,selected);
 }

 static void AssertEnabledScope(string root,JsonElement[] components,string[] selected)
 {
  var scope=(selected.Length>0?selected:components.Select(x=>Text(x,"id")).ToArray())
   .ToHashSet(StringComparer.OrdinalIgnoreCase);
  string disabledRoot=BundleInstaller.Child(root,ModManager.StorageName);
  if(!Directory.Exists(disabledRoot))return;
  GameEnvironment.NoLinks(disabledRoot);
  foreach(string slot in Directory.EnumerateDirectories(disabledRoot,"*",SearchOption.TopDirectoryOnly))
  {
   GameEnvironment.NoLinks(slot);
   string recordPath=BundleInstaller.Child(slot,"记录.json");
   if(!File.Exists(recordPath))throw new InvalidDataException("停用区记录缺失，请核对："+slot);
   using var document=JsonDocument.Parse(File.ReadAllText(recordPath));
   var record=document.RootElement;
   string status=Text(record,"Status");
   string id=Text(record,"ModId");
   if(status is not("enabled" or "failed-restored")&&scope.Contains(id))
    throw new InvalidOperationException("Mod is disabled: "+id+". Enable it in the mod manager first.");
  }
 }

 static Dictionary<string,string> Inventory(string root)
 {
  string mods=BundleInstaller.Child(root,"mods");
  if(!Directory.Exists(mods))throw new InvalidOperationException("Mods folder not found.");
  GameEnvironment.NoLinks(mods);
  var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  var options=new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=0,IgnoreInaccessible=false};
  foreach(string file in Directory.EnumerateFiles(mods,"*.json",options))
  {
   JsonDocument? document=null;
   try{document=JsonDocument.Parse(File.ReadAllText(file));}
   catch(Exception e)when(e is JsonException or IOException or UnauthorizedAccessException){continue;}
   using(document)
   {
    var manifest=document!.RootElement;
    if(manifest.ValueKind!=JsonValueKind.Object||!manifest.TryGetProperty("id",out var id)||
       !(manifest.TryGetProperty("has_dll",out _)||manifest.TryGetProperty("has_pck",out _)))continue;
    string key=id.ValueKind==JsonValueKind.String?id.GetString()??"":id.ToString();
    if(key.Length==0)continue;
    string safe=BundleInstaller.Child(mods,Relative(mods,file));
    if(!result.TryAdd(key,safe))throw new InvalidOperationException("Duplicate mod ID: "+key+". Remove duplicate copies before installation.");
   }
  }
  return result;
 }

 static PatchInstallPlan CreateFull(PreparedPackage package,string root,JsonElement spec,JsonElement[] components,
  Dictionary<string,string> inventory,string version)
 {
  var all=new List<PatchPlannedFile>();var changes=new List<PatchPlannedFile>();
  var projected=new Dictionary<string,string>(Paths);
  string mods=BundleInstaller.Child(root,"mods");
  foreach(var component in components)
  {
   string id=Text(component,"id");
   if(id.Length==0)throw new InvalidDataException("补丁组件缺少 ID。");
   bool required=Bool(component,"required");
   inventory.TryGetValue(id,out var manifest);
   if(required&&manifest==null)throw new InvalidOperationException("Required original mod missing: "+id);
   string folder=manifest!=null?Path.GetDirectoryName(manifest)!:BundleInstaller.Child(mods,id);
   if(id=="StartingDeckTweaks"&&File.Exists(BundleInstaller.Child(folder,"StartingDeckTweaks.pck")))
    throw new InvalidOperationException("StartingDeckTweaks has an unexpected PCK; this package targets the verified DLL-only build.");
   AddComponentFiles(package,root,component,folder,manifest,required,all,changes,projected);
  }
  foreach(var dependency in Array(spec,"dependencies").EnumerateArray())
  {
   string modId=Text(dependency,"modId"),folder=root;
   if(modId.Length>0)
   {
    if(!inventory.TryGetValue(modId,out var manifest))throw new InvalidOperationException("Required dependency mod missing: "+modId);
    folder=Path.GetDirectoryName(manifest)!;
   }
   string path=BundleInstaller.Child(folder,Text(dependency,"path"));
   string actual=projected.TryGetValue(path,out var future)?future:File.Exists(path)?Catalog.Hash(path):"";
   if(!actual.Equals(Text(dependency,"sha256"),StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Dependency version mismatch: "+modId+" / "+Text(dependency,"path")+". No game files changed.");
  }
  AddRetired(root,spec,inventory,changes);
  return Finish(root,version,[],all,changes,[]);
 }

 static void AddComponentFiles(PreparedPackage package,string root,JsonElement component,string folder,string? manifest,bool required,
  List<PatchPlannedFile> all,List<PatchPlannedFile> changes,Dictionary<string,string> projected)
 {
  foreach(var file in Array(component,"files").EnumerateArray())
  {
   string name=Text(file,"name"),sourceRelative=Text(file,"source");
   if(name.Length==0||sourceRelative.Length==0)throw new InvalidDataException("补丁文件清单不完整。");
   bool isManifest=Bool(file,"isManifest");
   string target=isManifest&&manifest!=null?manifest:BundleInstaller.Child(folder,name);
   string relative=Relative(root,target);
   target=BundleInstaller.Child(root,relative);
   string source=BundleInstaller.Child(package.Directory,sourceRelative);
   if(!File.Exists(source))throw new InvalidDataException("补丁包缺少文件："+sourceRelative);
   bool exists=File.Exists(target);
   bool allowMissing=Bool(file,"allowMissing")&&name.StartsWith("patch-info/",StringComparison.OrdinalIgnoreCase);
   if(required&&!exists&&!allowMissing)throw new InvalidOperationException("Original mod file missing: "+target);
   string oldHash=exists?Catalog.Hash(target):"";
   var allowed=Array(file,"allowedExisting").EnumerateArray().Select(x=>x.GetString()??"").ToArray();
   if(exists&&!allowed.Contains(oldHash,StringComparer.OrdinalIgnoreCase))
    throw new InvalidOperationException("Unsupported existing file: "+target+". No game files changed.");
   string newHash=Text(file,"sha256");
   if(newHash.Length!=64)throw new InvalidDataException("补丁文件 SHA-256 无效："+sourceRelative);
   var item=new PatchPlannedFile(relative,target,source,exists,oldHash,newHash,"write");
   all.Add(item);projected[target]=newHash;
   if(!oldHash.Equals(newHash,StringComparison.OrdinalIgnoreCase))changes.Add(item);
  }
 }

 static void AddRetired(string root,JsonElement spec,Dictionary<string,string> inventory,List<PatchPlannedFile> changes)
 {
  foreach(var retired in Array(spec,"retired").EnumerateArray())
  {
   string id=Text(retired,"id");
   if(!inventory.TryGetValue(id,out var manifest))continue;
   string folder=Path.GetDirectoryName(manifest)!;
   var expected=new HashSet<string>(Paths);
   foreach(var file in Array(retired,"files").EnumerateArray())
   {
    string target=Bool(file,"isManifest")?manifest:BundleInstaller.Child(folder,Text(file,"name"));
    string hash=Text(file,"sha256");
    if(!File.Exists(target)||!Catalog.Hash(target).Equals(hash,StringComparison.OrdinalIgnoreCase))
     throw new InvalidOperationException("Unrecognized legacy fix: "+target);
    expected.Add(target);
    changes.Add(new PatchPlannedFile(Relative(root,target),target,"",true,hash,"","remove"));
   }
   foreach(string extra in Directory.EnumerateFileSystemEntries(folder))
    if(!expected.Contains(extra))throw new InvalidOperationException("Legacy fix has unknown files: "+extra);
  }
 }

 static PatchInstallPlan CreateSelected(PreparedPackage package,string root,JsonElement spec,JsonElement[] components,
  Dictionary<string,string> inventory,string version,string[] selected)
 {
  var known=new Dictionary<string,JsonElement>(StringComparer.OrdinalIgnoreCase);
  var metadata=new Dictionary<string,JsonElement>(StringComparer.OrdinalIgnoreCase);
  var byPatch=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  foreach(var component in components)
  {
   string id=Text(component,"id");
   if(id.Length==0||!known.TryAdd(id,component))throw new InvalidDataException("补丁组件 ID 重复或为空。");
   string path=BundleInstaller.Child(package.Directory,"payload/"+id+"/patch-info/patch.json");
   using var doc=JsonDocument.Parse(File.ReadAllText(path));
   var value=doc.RootElement.Clone();
   string patchId=Text(value,"id");
   if(patchId.Length==0||!byPatch.TryAdd(patchId,id))throw new InvalidDataException("补丁元数据 ID 重复或为空："+id);
   metadata[id]=value;
  }
  var chosen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach(string id in selected)
  {
   if(!known.ContainsKey(id)||!chosen.Add(id))throw new InvalidOperationException("Invalid patch selection: "+id);
  }
  if(chosen.Count==0)throw new InvalidOperationException("Empty patch selection.");
  var all=new List<PatchPlannedFile>();var changes=new List<PatchPlannedFile>();
  var projected=new Dictionary<string,string>(Paths);
  var context=new Dictionary<string,string>(Paths);
  string mods=BundleInstaller.Child(root,"mods");

  string ResolveFile(string modId,string relative)
  {
   if(modId.Length==0)return BundleInstaller.Child(root,relative);
   if(inventory.TryGetValue(modId,out var found))
   {
    if(relative.Equals(modId+".json",StringComparison.OrdinalIgnoreCase))return found;
    return BundleInstaller.Child(Path.GetDirectoryName(found)!,relative);
   }
   return BundleInstaller.Child(BundleInstaller.Child(mods,modId),relative);
  }
  string CurrentHash(string path)
  {
   string relative=Relative(root,path);
   string safe=BundleInstaller.Child(root,relative);
   string hash=File.Exists(safe)?Catalog.Hash(safe):"";
   context[relative]=hash;
   return hash;
  }

  foreach(string id in chosen.Order(StringComparer.Ordinal))
  {
   var component=known[id];bool required=Bool(component,"required");
   inventory.TryGetValue(id,out var manifest);
   if(required&&manifest==null)throw new InvalidOperationException("Required original mod missing: "+id);
   string folder=manifest!=null?Path.GetDirectoryName(manifest)!:
    required?BundleInstaller.Child(mods,id):BundleInstaller.Child(BundleInstaller.Child(mods,"_SpirePatch_Fixes"),id);
   if(id=="StartingDeckTweaks"&&File.Exists(BundleInstaller.Child(folder,"StartingDeckTweaks.pck")))
    throw new InvalidOperationException("Unexpected StartingDeckTweaks PCK.");
   AddComponentFiles(package,root,component,folder,manifest,required,all,changes,projected);
  }

  bool MatchesProfile(JsonElement item,bool future)
  {
   JsonElement profiles=default;
   if(item.TryGetProperty("compatibility",out var compatibility))profiles=Array(compatibility,"profiles");
   if(profiles.ValueKind!=JsonValueKind.Array)return false;
   foreach(var profile in profiles.EnumerateArray())
   {
    bool ok=true;
    foreach(var excluded in Array(profile,"excluded_mod_ids").EnumerateArray())
    {
     string id=excluded.GetString()??"";
     if(inventory.ContainsKey(id)||(future&&chosen.Contains(id))){ok=false;break;}
    }
    if(!ok)continue;
    foreach(var file in Array(profile,"game_files").EnumerateArray())
    {
     string path=ResolveFile("",Text(file,"relative_path"));
     if(!CurrentHash(path).Equals(Text(file,"sha256"),StringComparison.OrdinalIgnoreCase)){ok=false;break;}
    }
    if(!ok)continue;
    foreach(var mod in Array(profile,"required_mods").EnumerateArray())
    {
     string modId=Text(mod,"mod_id");
     if(!inventory.ContainsKey(modId)&&!(future&&chosen.Contains(modId))){ok=false;break;}
     foreach(var file in Array(mod,"files").EnumerateArray())
     {
      string path=ResolveFile(modId,Text(file,"relative_path"));
      string actual=CurrentHash(path);
      string hash=future&&projected.TryGetValue(path,out var futureHash)?futureHash:actual;
      if(!hash.Equals(Text(file,"sha256"),StringComparison.OrdinalIgnoreCase)){ok=false;break;}
     }
     if(!ok)break;
    }
    if(ok)return true;
   }
   return false;
  }

  var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  void AssertPatch(string id)
  {
   if(!visited.Add(id))return;
   if(!metadata.TryGetValue(id,out var item))throw new InvalidOperationException("Unknown patch dependency: "+id);
   if(!chosen.Contains(id))
   {
    if(!inventory.ContainsKey(id))throw new InvalidOperationException("Required patch missing: "+id);
    var detection=item.TryGetProperty("detection",out var det)?det:default;
    foreach(var fingerprint in Array(detection,"installed_fingerprints").EnumerateArray())
    {
     string path=ResolveFile(Text(fingerprint,"mod_id"),Text(fingerprint,"relative_path"));
     if(!CurrentHash(path).Equals(Text(fingerprint,"sha256"),StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("Related patch needs update: "+id);
    }
   }
   if(!MatchesProfile(item,true))throw new InvalidOperationException("Selected patch compatibility mismatch: "+id);
   var relationships=item.TryGetProperty("relationships",out var rel)?rel:default;
   foreach(var conflict in Array(relationships,"conflicts_patch_ids").EnumerateArray())
   {
    string patchId=conflict.GetString()??"";
    if(!byPatch.TryGetValue(patchId,out var componentId))throw new InvalidOperationException("Unknown patch conflict: "+patchId);
    if(inventory.ContainsKey(componentId)||chosen.Contains(componentId))throw new InvalidOperationException("Conflicting patch: "+componentId);
   }
   var requiredPatches=Array(relationships,"requires_patches").EnumerateArray().Select(x=>x.Clone()).ToList();
   foreach(var conditional in Array(relationships,"requires_when_mod_present").EnumerateArray())
   {
    string modId=Text(conditional,"mod_id");
    if(inventory.ContainsKey(modId)||chosen.Contains(modId))requiredPatches.Add(conditional.Clone());
   }
   foreach(var requiredPatch in requiredPatches)
   {
    string patchId=Text(requiredPatch,"patch_id");
    if(!byPatch.TryGetValue(patchId,out var componentId))throw new InvalidOperationException("Unknown patch dependency: "+patchId);
    string minimum=Text(requiredPatch,"minimum_version");
    string actualVersion=Text(metadata[componentId],"version");
    if(minimum.Length>0&&(!Version.TryParse(actualVersion,out var actual)||!Version.TryParse(minimum,out var min)||actual<min))
     throw new InvalidOperationException("Related patch version too old: "+componentId);
    AssertPatch(componentId);
   }
  }

  foreach(string id in chosen.Order(StringComparer.Ordinal))AssertPatch(id);
  foreach(string id in metadata.Keys.Order(StringComparer.Ordinal))
  {
   if(!inventory.ContainsKey(id)||chosen.Contains(id))continue;
   bool affected=false;
   foreach(var profile in Array(metadata[id].TryGetProperty("compatibility",out var c)?c:default,"profiles").EnumerateArray())
    foreach(var mod in Array(profile,"required_mods").EnumerateArray())
     if(chosen.Contains(Text(mod,"mod_id")))affected=true;
   if(affected)AssertPatch(id);
  }

  // The retired Chorale compatibility patch is superseded only by SpireTogetherFix.
  if(chosen.Contains("SpireTogetherFix"))AddRetired(root,spec,inventory,changes);
  foreach(var pair in inventory.OrderBy(x=>x.Key,StringComparer.Ordinal))
   _=CurrentHash(pair.Value);
  var contextRows=context.OrderBy(x=>x.Key,StringComparer.OrdinalIgnoreCase)
   .Select(x=>new PatchPlanContext(x.Key,x.Value)).ToArray();
  return Finish(root,version,selected,all,changes,contextRows);
 }

 static PatchInstallPlan Finish(string root,string version,string[] selected,List<PatchPlannedFile> all,
  List<PatchPlannedFile> changes,PatchPlanContext[] context)
 {
  var orderedAll=all.ToArray();var orderedChanges=changes.ToArray();
  var payload=new
  {
   root,version,selected,
   context=context.Select(x=>new{path=x.Path,sha256=x.Sha256}).ToArray(),
   all=orderedAll.Select(x=>new{relative=x.Relative,existed=x.Existed,oldHash=x.OldHash,newHash=x.NewHash,operation=x.Operation}).ToArray(),
   changes=orderedChanges.Select(x=>new{relative=x.Relative,existed=x.Existed,oldHash=x.OldHash,newHash=x.NewHash,operation=x.Operation}).ToArray()
  };
  string token=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))));
  return new(root,version,orderedAll,orderedChanges,selected,context,token);
 }
}
