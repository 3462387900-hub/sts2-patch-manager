using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace SpirePatch;
public static class InstallerChecks
{
 public static void AssertFixture(string root)
 {
  if(!File.Exists(Path.Combine(root,".spirepatch-fixture"))||!File.Exists(Path.Combine(root,"SlayTheSpire2.exe"))||new FileInfo(Path.Combine(root,"SlayTheSpire2.exe")).Length!=0)throw new InvalidOperationException("安装测试只允许带标记且游戏程序为零字节的隔离目录。");
 }
 static Dictionary<string,string> Snapshot(string root)=>Directory.EnumerateFiles(Path.Combine(root,"mods"),"*",SearchOption.AllDirectories).ToDictionary(p=>Path.GetRelativePath(root,p),Catalog.Hash,StringComparer.OrdinalIgnoreCase);
 static bool Same(Dictionary<string,string> a,Dictionary<string,string> b)=>a.Count==b.Count&&a.All(x=>b.TryGetValue(x.Key,out var hash)&&x.Value==hash);
 public static async Task RunAsync(string archive,string root,string output)
 {
  AssertFixture(root);var lines=new List<string>();void Check(bool b,string label){if(!b)throw new InvalidOperationException(label);lines.Add("PASS "+label);File.WriteAllLines(output,lines);}
  async Task Reject(Func<Task> operation,string label){bool failed=false;try{await operation();}catch(Exception e)when(e is InvalidOperationException or InvalidDataException){failed=true;}Check(failed,label);}
  string checks=Path.GetDirectoryName(output)!;var backend=new BundleInstaller(Path.Combine(checks,"执行缓存"));var original=Snapshot(root);
  var package=await backend.PrepareAsync(archive);Check(package.Files.Count>60,"reviewed ZIP extracted and every file verified");
  Check(Catalog.Hash(Path.Combine(backend.DataRoot,"下载",BundleInstaller.SupportedBundle.FileName))==BundleInstaller.SupportedHash,"imported ZIP remains cached for offline rollback");
  var preview=await backend.RunAsync(package,root,"Preview");Check(preview.Changed>0&&Same(original,Snapshot(root))&&!Directory.Exists(Path.Combine(root,"mod_fix_backups")),"preview is read-only and creates no game backups");
  string act4=Path.Combine(root,preview.Files.First(f=>f.Relative.EndsWith("Act4FinalAscent.dll")).Relative);byte[] oldAct4=File.ReadAllBytes(act4);
  File.Copy(Path.Combine(package.Directory,"payload","Act4FinalAscent","Act4FinalAscent.dll"),act4,true);var changed=Snapshot(root);
  await Reject(()=>backend.RunAsync(package,root,"Install",preview.Token),"changed-but-recognized files invalidate the reviewed plan");Check(Same(changed,Snapshot(root)),"stale plan refusal leaves all game files unchanged");File.WriteAllBytes(act4,oldAct4);
  File.WriteAllBytes(act4,oldAct4.Concat(new byte[]{1}).ToArray());changed=Snapshot(root);
  await Reject(()=>backend.RunAsync(package,root,"Preview"),"unknown original DLL is rejected");Check(Same(changed,Snapshot(root)),"unknown DLL refusal is read-only");File.WriteAllBytes(act4,oldAct4);
  preview=await backend.RunAsync(package,root,"Preview");
  var installed=await backend.RunAsync(package,root,"Install",preview.Token);
  Check(installed.Message=="Installed"&&Directory.Exists(installed.Backup),"real installation writes files and creates a verified backup");
  Check(Directory.EnumerateFiles(Path.Combine(root,"mods"),"patch.json",SearchOption.AllDirectories).Count()==11,"all 11 patches carry their metadata after installation");
  Check(Catalog.Scan(root).Patches.All(p=>p.Status=="已安装"),"installed bundle satisfies all eleven metadata profiles and patch relations");
  var installedSnapshot=Snapshot(root);int backupCount=Directory.GetDirectories(Path.Combine(root,"mod_fix_backups")).Length;
  var again=await backend.RunAsync(package,root,"Preview");Check(again.Changed==0,"installed files produce an empty change plan");
  var noOp=await backend.RunAsync(package,root,"Install",again.Token);Check(noOp.Message=="AlreadyInstalled"&&Same(installedSnapshot,Snapshot(root))&&backupCount==Directory.GetDirectories(Path.Combine(root,"mod_fix_backups")).Length,"repeated install changes neither files nor backup count");
  byte[] patched=File.ReadAllBytes(act4);File.WriteAllBytes(act4,patched.Concat(new byte[]{2}).ToArray());changed=Snapshot(root);
  await Reject(()=>backend.RunAsync(package,root,"RollbackPreview"),"later DLL updates prevent rollback");Check(Same(changed,Snapshot(root)),"update protection prevents partial rollback");File.WriteAllBytes(act4,patched);
  using(var journal=JsonDocument.Parse(File.ReadAllText(Path.Combine(installed.Backup,"restore.json")))){
   var entry=journal.RootElement.GetProperty("files").EnumerateArray().First(x=>x.GetProperty("existed").GetBoolean());
   string backup=Path.Combine(installed.Backup,entry.GetProperty("backup").GetString()!);byte[] originalBackup=File.ReadAllBytes(backup);File.WriteAllBytes(backup,originalBackup.Concat(new byte[]{3}).ToArray());
   await Reject(()=>backend.RunAsync(package,root,"RollbackPreview"),"damaged backup is rejected before rollback");Check(Same(installedSnapshot,Snapshot(root)),"backup rejection leaves installed files unchanged");File.WriteAllBytes(backup,originalBackup);
  }
  var undo=await backend.RunAsync(package,root,"RollbackPreview");Check(undo.Backup==installed.Backup&&undo.Changed==installed.Changed,"rollback preview identifies the exact installation backup");
  var restored=await backend.RunAsync(package,root,"Rollback",undo.Token);Check(restored.Message=="RolledBack"&&Same(original,Snapshot(root)),"real rollback restores byte-exact originals including retired legacy files");
  Check(Catalog.Scan(root).Patches.Count(p=>p.Status=="需联动")==2,"rollback restores the coordinated Act4 and Spire update recommendation");
  undo=await backend.RunAsync(package,root,"RollbackPreview");Check(undo.Changed==0&&undo.Message=="NothingToRollback","repeat rollback does not select an already-restored journal");
  string stageScript=Path.Combine(package.Directory,"Common.ps1");byte[] script=File.ReadAllBytes(stageScript);File.WriteAllBytes(stageScript,script.Concat(new byte[]{32}).ToArray());
  await Reject(()=>backend.RunAsync(package,root,"Preview"),"tampered cached installer is never executed");File.WriteAllBytes(stageScript,script);
  string badArchive=Path.Combine(checks,"损坏修复包.zip");File.Copy(archive,badArchive,true);using(var f=new FileStream(badArchive,FileMode.Open,FileAccess.Write)){f.Position=f.Length-1;f.WriteByte(1);}
  await Reject(async()=>{await backend.PrepareAsync(badArchive);},"incorrect ZIP hash is rejected before staging");
  string hostile=Path.Combine(checks,"路径越界测试.zip"),stage=Path.Combine(checks,"路径验证");Directory.CreateDirectory(stage);
  using(var zip=ZipFile.Open(hostile,ZipArchiveMode.Create)){using var writer=new StreamWriter(zip.CreateEntry("STS2-All-Fixes-20261102-v2/../../escape.txt").Open());writer.Write("escape");}
  await Reject(()=>Task.Run(()=>BundleInstaller.ExtractArchive(hostile,stage)),"archive path traversal is rejected");Check(!File.Exists(Path.Combine(checks,"escape.txt")),"no file escapes the extraction directory");
  Check(Same(original,Snapshot(root)),"all tests leave fixture mods in their original state");
 }
}
