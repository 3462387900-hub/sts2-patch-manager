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
  var original=Snapshot();Check(ModManager.Scan(root).Mods.Count==7,"manifest inventory reads IDs and shared directories without changing files");
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
