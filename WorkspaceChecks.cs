using System.IO;
using System.Text.Json;

namespace SpirePatch;
public static class WorkspaceChecks
{
 public static string Fixture(string parent)
 {
  string root=Path.Combine(parent,"游戏 中文 空格 "+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"mods"));File.WriteAllText(Path.Combine(root,"SlayTheSpire2.exe"),"");File.WriteAllText(Path.Combine(root,".spirepatch-fixture"),"isolated");
  void Mod(string id,string folder,string[] deps){string dir=Path.Combine(root,"mods",folder);Directory.CreateDirectory(dir);File.WriteAllText(Path.Combine(dir,id+".json"),JsonSerializer.Serialize(new{id,name=id=="TestSolo"?"独立模组":id,version="1.0.0",has_dll=true,dependencies=deps.Select(d=>new{id=d,min_version="1.0.0"}).ToArray()}));File.WriteAllText(Path.Combine(dir,id+".dll"),id+" fixture");}
  Mod("TestBase","基础 库",[]);Mod("TestChild","依赖模组",["TestBase"]);Mod("TestSolo","独立 模组",[]);Mod("HextechRunes","海克斯测试",[]);Mod("LargeHealDisplayFix","回血修复测试",[]);
  Mod("SharedOne","共享目录",[]);Mod("SharedTwo","共享目录",[]);return root;
 }
 public static async Task RunAsync(string zip,string output)
 {
  string parent=Path.GetDirectoryName(output)!;Directory.CreateDirectory(parent);string root=Fixture(parent);var lines=new List<string>();
  void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);lines.Add("PASS "+name);File.WriteAllLines(output,lines);}
  void Reject(Action action,string name){bool rejected=false;try{action();}catch(Exception e)when(e is InvalidOperationException or InvalidDataException){rejected=true;}Check(rejected,name);}
  ManagedMod Find(string id)=>ModManager.Scan(root).Mods.Single(m=>m.Id==id);
  Dictionary<string,string> Snapshot()=>Directory.EnumerateFiles(Path.Combine(root,"mods"),"*",SearchOption.AllDirectories).ToDictionary(f=>f,Catalog.Hash);
  bool Same(Dictionary<string,string> expected)=>expected.Count==Snapshot().Count&&expected.All(f=>File.Exists(f.Key)&&Catalog.Hash(f.Key)==f.Value);
  // Paging is deterministic and never truncates the underlying dependency report.
  int[] relations=Enumerable.Range(1,27).ToArray();
  Check(ModDependencyPaging.Count(0)==1&&ModDependencyPaging.Count(10)==1&&
   ModDependencyPaging.Count(11)==2&&ModDependencyPaging.Count(27)==3,
   "dependency page count respects 10 relations per page, including empty results");
  Check(ModDependencyPaging.Items(relations,1).SequenceEqual(Enumerable.Range(1,10))&&
   ModDependencyPaging.Items(relations,2).SequenceEqual(Enumerable.Range(11,10))&&
   ModDependencyPaging.Items(relations,3).SequenceEqual(Enumerable.Range(21,7)),
   "dependency paging partitions all relations without duplicate or missing rows");
  Check(ModDependencyPaging.Clamp(0,27)==1&&ModDependencyPaging.Clamp(99,27)==3&&
   ModDependencyPaging.Items(relations,99).SequenceEqual(Enumerable.Range(21,7)),
   "out-of-range pages clamp safely after filtering");
  // Export only the explicitly available local source mapping. No private paths or
  // version-baseline metadata may appear in a community index upload.
  string publicIndexData=Path.Combine(parent,"public-source-fixture-"+Guid.NewGuid().ToString("N"));
  var localSources=new WorkshopModSources(publicIndexData,root);
  var inventoryBefore=ModManager.Scan(root);
  Check(PublicWorkshopSourceExport.Build(inventoryBefore.Mods,localSources).Mods.Length==0,
   "public source export omits all unbound mods");
  localSources.Bind("TestBase",3737335127);
  localSources.Bind("TestSolo",3833333333);
  localSources.Bind("LargeHealDisplayFix",3933333333);
  var export=PublicWorkshopSourceExport.Build(inventoryBefore.Mods,localSources);
  string exportedJson=PublicWorkshopSourceExport.Serialize(export);
  using var parsed=JsonDocument.Parse(exportedJson);
  var entries=parsed.RootElement.GetProperty("mods").EnumerateArray().ToArray();
  Check(parsed.RootElement.GetProperty("schema").GetInt32()==1&&
   parsed.RootElement.GetProperty("app_id").GetUInt32()==2868840&&
   entries.Length==2&&entries.Any(e=>e.GetProperty("mod_id").GetString()=="TestBase"&&
    e.GetProperty("workshop_id").GetString()=="3737335127")&&
   entries.Any(e=>e.GetProperty("mod_id").GetString()=="TestSolo")&&
   entries.All(e=>e.GetProperty("verification").GetString()=="local_binding"),
   "export contains only real bound MOD IDs, excludes independent patch and avoids claiming remote verification");
  Check(!exportedJson.Contains(root,StringComparison.OrdinalIgnoreCase)&&
   !exportedJson.Contains("Folder",StringComparison.OrdinalIgnoreCase)&&
   !exportedJson.Contains("ManifestId",StringComparison.OrdinalIgnoreCase)&&
   !exportedJson.Contains("LocalSnapshotSha256",StringComparison.OrdinalIgnoreCase)&&
   !exportedJson.Contains(".versions.json",StringComparison.OrdinalIgnoreCase)&&
   new WorkshopModSources(publicIndexData,root).Get("TestBase")==3737335127,
   "public index excludes local paths, baselines, secrets and preserves local bindings");
  Check(PublicWorkshopSourceExport.Build(inventoryBefore.Mods.Append(
    inventoryBefore.Mods.Single(m=>m.Id=="TestBase") with{Name="不同作者重发版"}),localSources)
   .Mods.All(m=>m.ModId!="TestBase"),
   "conflicting variants with identical internal MOD IDs are never exported automatically");
  Reject(()=>localSources.Bind("SharedOne",3737335127),
   "existing binding rejects reusing one Workshop ID for multiple MOD IDs");
  var original=Snapshot();Check(ModManager.Scan(root).Mods.Count==7,"manifest inventory reads IDs and shared directories without changing files");
  var presentation=ModListView.Rows(root,ModManager.Scan(root),_=>"未检查");
  Check(ModListView.Filter(presentation,"修复补丁","全部","").Length==1&&
   ModListView.Filter(presentation,"普通 MOD","全部","").Length==6&&
   ModListView.Filter(presentation,"普通 MOD","全部","独立").Single().Mod.Id=="TestSolo"&&
   ModListView.Filter(presentation,"普通 MOD","启用","").Length==6,
   "virtual list filters only data by category, enabled state, and search");
  Check(ModPresentation.IsIndependentPatch(Find("LargeHealDisplayFix"))&&!ModPresentation.IsIndependentPatch(Find("TestSolo"))&&
   !ModPresentation.IsIndependentPatch(Find("HextechRunes")),"mod list groups only catalogued independent patches, never original mods");
  Reject(()=>ModManager.Preview(root,Find("TestBase").Folder,false),"cannot disable a declared dependency while a dependent mod is active");
  Reject(()=>ModManager.Preview(root,Find("SharedOne").Folder,false),"shared mod directory cannot be moved as a single mod");
  var solo=Find("TestSolo");var preview=ModManager.Preview(root,solo.Folder,false);Check(Same(original)&&!Directory.Exists(Path.Combine(root,ModManager.StorageName)),"toggle preview is read-only and creates no storage folder");
  string dll=Path.Combine(solo.Folder,"TestSolo.dll");string old=File.ReadAllText(dll);File.WriteAllText(dll,"changed");Reject(()=>ModManager.Apply(preview),"file changes invalidate prior toggle confirmation");File.WriteAllText(dll,old);
  ModManager.Apply(ModManager.Preview(root,solo.Folder,false));solo=Find("TestSolo");Check(!solo.Enabled&&!Directory.Exists(Path.Combine(root,"mods","独立 模组"))&&File.ReadAllText(Path.Combine(solo.Folder,"TestSolo.dll"))==old,"disable atomically retains the whole mod outside mods");
  File.WriteAllText(Path.Combine(solo.Folder,"TestSolo.dll"),"edited while disabled");Reject(()=>ModManager.Preview(root,solo.Folder,true),"edited disabled content is not silently restored");File.WriteAllText(Path.Combine(solo.Folder,"TestSolo.dll"),old);
  string restore=Path.Combine(root,solo.Original!);Directory.CreateDirectory(restore);Reject(()=>ModManager.Preview(root,solo.Folder,true),"restore never overwrites an occupied original location");Directory.Delete(restore);
  string record=Path.Combine(solo.Storage!,"记录.json"),saved=File.ReadAllText(record);var malformed=JsonSerializer.Deserialize<DisabledModRecord>(saved)! with{Original="../outside"};File.WriteAllText(record,JsonSerializer.Serialize(malformed));Check(ModManager.Scan(root).Issues.Count>0,"escaped original location in a disabled record is rejected");File.WriteAllText(record,saved);
  ModManager.Apply(ModManager.Preview(root,solo.Folder,true));Check(Same(original),"enable restores original folder and all file bytes");
  ModManager.Apply(ModManager.Preview(root,Find("TestChild").Folder,false));ModManager.Apply(ModManager.Preview(root,Find("TestBase").Folder,false));
  Reject(()=>ModManager.Preview(root,Find("TestChild").Folder,true),"cannot enable a mod before its required dependency");
  ModManager.Apply(ModManager.Preview(root,Find("TestBase").Folder,true));string baseManifest=Find("TestBase").Manifest,baseText=File.ReadAllText(baseManifest);File.WriteAllText(baseManifest,baseText.Replace("1.0.0","0.5.0"));
  Reject(()=>ModManager.Preview(root,Find("TestChild").Folder,true),"declared dependency minimum version is enforced");File.WriteAllText(baseManifest,baseText);ModManager.Apply(ModManager.Preview(root,Find("TestChild").Folder,true));Check(Same(original),"ordered dependency toggles preserve the complete original environment");
  Reject(()=>ModManager.Preview(root,Find("HextechRunes").Folder,false),"known patch metadata protects dependencies omitted from mod manifest");
  ModManager.Apply(ModManager.Preview(root,Find("LargeHealDisplayFix").Folder,false));Check(Catalog.Scan(root).Patches.Single(p=>p.Patch.Id=="LargeHealDisplayFix").Status=="已停用","disabled patch is not recommended for reinstall");
  var backend=new BundleInstaller(Path.Combine(parent,"安装器联动验证"));var package=await backend.PrepareAsync(zip);bool blocked=false;
  try{await backend.RunAsync(package,root,"Preview");}catch(InvalidOperationException e){blocked=e.Message.Contains("已停用");}Check(blocked,"full installer rejects a disabled component inside the shared transaction lock");
  ModManager.Apply(ModManager.Preview(root,Find("LargeHealDisplayFix").Folder,true));Check(Same(original),"all toggle checks leave original mod files intact");
  // Orphaned repair patches: explicit target removal, stable recheck and reversible folder transaction.
  string orphanRoot=Fixture(parent),orphanData=Path.Combine(parent,"孤立补丁测试数据");
  var originalInventory=ModManager.Scan(orphanRoot);
  Check(OrphanPatchAutoStop.Observe(orphanRoot,orphanData,originalInventory).Length==0,
   "first complete MOD observation establishes a baseline without auto-disabling any patch");
  var largePatch=Catalog.Bundled.Single(x=>x.ComponentId=="LargeHealDisplayFix");
  Check(OrphanPatchAutoStop.Targets(largePatch).SequenceEqual(new[]{"HextechRunes"})&&
   OrphanPatchAutoStop.Targets(Catalog.Bundled.Single(x=>x.ComponentId=="CanonicalRelicHoverFix")).SequenceEqual(new[]{"Phrolova"})&&
   OrphanPatchAutoStop.Targets(Catalog.Bundled.Single(x=>x.ComponentId=="SpireTogetherFix")).Length==0,
   "target detection uses affected/trigger roles, never generic dependencies or the patch itself");
  var multi=Catalog.Bundled.Single(x=>x.ComponentId=="NeowOptionOrderFix");
  Check(OrphanPatchAutoStop.Targets(multi).ToHashSet(StringComparer.OrdinalIgnoreCase)
   .SetEquals(new[]{"YUIExtra","YuWanCard"}),"multi-target fixes list both affected MODs");
  var dummy=originalInventory.Mods.Single(m=>m.Id=="LargeHealDisplayFix")
   with{Id="NeowOptionOrderFix",Manifest=Path.Combine(orphanRoot,"mods","NeowOptionOrderFix.json")};
  var dummyTarget=originalInventory.Mods.Single(m=>m.Id=="TestSolo") with{Id="YuWanCard"};
  var pair=new PatchRemovalObservation(orphanRoot,["YUIExtra","YuWanCard"],DateTimeOffset.UtcNow);
  Check(OrphanPatchAutoStop.Candidates(pair,new([dummy,dummyTarget],[])).Length==0&&
   OrphanPatchAutoStop.Candidates(pair,new([dummy],[])).Length==1,
   "multi-target repair patch remains enabled while ANY affected MOD still exists");
  string hexFolder=originalInventory.Mods.Single(m=>m.Id=="HextechRunes").Folder;
  string removedHex=Path.Combine(parent,"暂时移出 "+Guid.NewGuid().ToString("N"));
  Directory.Move(hexFolder,removedHex);
  var missing=ModManager.Scan(orphanRoot);
  var missingList=OrphanPatchAutoStop.Observe(orphanRoot,orphanData,missing);
  Check(missingList.Length==1&&missingList[0].PatchId=="LargeHealDisplayFix",
   "removing the previously observed HextechRunes targets exactly its reviewed standalone fix");
  Directory.Move(removedHex,hexFolder);
  var restored=OrphanPatchAutoStop.Reconcile(orphanRoot,orphanData,fixture:true);
  Check(restored.Disabled.Length==0&&restored.Inventory.Mods.Any(m=>m.Id=="LargeHealDisplayFix"&&m.Enabled),
   "target MOD re-added before execution cancels automatic patch disable");
  Directory.Move(hexFolder,removedHex);
  Check(OrphanPatchAutoStop.Observe(orphanRoot,orphanData,ModManager.Scan(orphanRoot)).Length==1,
   "a later real deletion is detected after the restored baseline");
  var unreviewed=OrphanPatchAutoStop.Reconcile(orphanRoot,orphanData);
  Check(unreviewed.Disabled.Length==0&&unreviewed.Skipped.Length==1&&
   ModManager.Scan(orphanRoot).Mods.Any(m=>m.Id=="LargeHealDisplayFix"&&m.Enabled),
   "unreviewed fixture fingerprints do not auto-disable in the real-game code path");
  var safe=OrphanPatchAutoStop.Reconcile(orphanRoot,orphanData,fixture:true);
  var storedFix=safe.Inventory.Mods.Single(m=>m.Id=="LargeHealDisplayFix");
  Check(safe.Disabled.Length==1&&!storedFix.Enabled&&File.Exists(Path.Combine(storedFix.Folder,"LargeHealDisplayFix.dll"))&&
   !Directory.Exists(Path.Combine(orphanRoot,"mods","回血修复测试")),
   "approved isolated transaction moves only the orphan patch into recoverable disabled storage");
  Check(OrphanPatchAutoStop.Reconcile(orphanRoot,orphanData,fixture:true).Disabled.Length==0,
   "repeated scans never disable the same patch twice");
  Directory.Move(removedHex,hexFolder);
  ModManager.Apply(ModManager.Preview(orphanRoot,storedFix.Folder,true));
  Check(ModManager.Scan(orphanRoot).Mods.Single(m=>m.Id=="LargeHealDisplayFix").Enabled,
   "manually restoring an auto-disabled patch uses the original reversible enable action");
  OrphanPatchAutoStop.SetEnabled(orphanData,false);
  Check(!OrphanPatchAutoStop.Enabled(orphanData)&&
   OrphanPatchAutoStop.Observe(orphanRoot,orphanData,ModManager.Scan(orphanRoot),enabled:false).Length==0,
   "user opt-out is saved and disables automatic patch retirement");
  OrphanPatchAutoStop.SetEnabled(orphanData,true);
  Check(OrphanPatchAutoStop.Enabled(orphanData),"user can opt back into orphan-patch automation");

  // Discovery fixtures are not executable and are never launched.
  string steam=Path.Combine(parent,"Steam 测试"),library=Path.Combine(parent,"第二个 游戏库");Directory.CreateDirectory(Path.Combine(steam,"steamapps"));Directory.CreateDirectory(Path.Combine(library,"steamapps"));
  string first=Path.Combine(steam,"steamapps","common","Slay the Spire 2"),second=Path.Combine(library,"steamapps","common","中文自选名称");
  foreach(string dir in new[]{first,second}){Directory.CreateDirectory(dir);File.WriteAllBytes(Path.Combine(dir,"SlayTheSpire2.exe"),new byte[128]);}
  File.WriteAllText(Path.Combine(steam,"steamapps","libraryfolders.vdf"),"\"libraryfolders\" { \"1\" { \"path\" \""+library.Replace("\\","\\\\")+"\" } }");
  File.WriteAllText(Path.Combine(library,"steamapps","appmanifest_123.acf"),"\"AppState\" { \"name\" \"Slay the Spire 2\" \"installdir\" \"中文自选名称\" }");
  var found=GameEnvironment.Discover(first,[steam],[]);Check(found.Count==2&&found.Count(c=>c.Directory==first)==1&&found.Any(c=>c.Directory==second),"Steam library VDF and manifests discover multiple installs and deduplicate saved path");
  Check(GameEnvironment.Discover(Path.Combine(parent,"不存在"),[],[]).Count==0,"invalid remembered game directory is ignored");
  string drive=Path.Combine(parent,"自选盘"),common=Path.Combine(drive,"Games","SlayTheSpire2");Directory.CreateDirectory(common);File.WriteAllText(Path.Combine(common,"SlayTheSpire2.exe"),"");Check(GameEnvironment.Discover(null,[],[drive]).Single().Directory==common,"bounded common-directory search works without hardcoded drive");
  string settings=Path.Combine(parent,"设置","偏好.json");GameEnvironment.SaveRoot(settings,second);Check(GameEnvironment.LoadRoot(settings)==second,"chosen directory persists for next launch");
  File.WriteAllText(settings,"broken");Check(GameEnvironment.LoadRoot(settings)==null,"broken preferences do not crash startup");
  Reject(()=>GameEnvironment.LaunchInfo(root),"marked test fixtures cannot launch a game");Reject(()=>GameEnvironment.LaunchInfo(first),"invalid game executable is rejected before launching");
  byte[] exe=new byte[128];exe[0]=(byte)'M';exe[1]=(byte)'Z';File.WriteAllBytes(Path.Combine(first,"SlayTheSpire2.exe"),exe);var info=GameEnvironment.LaunchInfo(first);
  Check(info.FileName==Path.Combine(first,"SlayTheSpire2.exe")&&info.WorkingDirectory==first&&!info.UseShellExecute&&info.Arguments.Length==0,"launch uses exact selected executable and game working directory without shell interpolation");
  bool contention=GameEnvironment.WithLock(root,()=>Task.Run(()=>{try{GameEnvironment.WithLock(root,()=>true);return false;}catch(InvalidOperationException){return true;}}).GetAwaiter().GetResult());Check(contention,"concurrent game-folder operations are blocked by named mutex");
  File.WriteAllText(Path.Combine(parent,"互斥锁验证.json"),JsonSerializer.Serialize(new{root,name=GameEnvironment.MutexName(root)}));
 }
}
