using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpirePatch;

/// <summary>Windows-only, isolated regression coverage for the native installer and legacy journals.</summary>
internal static class NativePatchInstallerChecks
{
 internal static async Task RunAsync(string output)
 {
  string home=Path.Combine(Path.GetDirectoryName(output)!,"原生安装器验证-"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(home);
  var lines=new List<string>();
  void Check(bool ok,string label)
  {
   if(!ok)throw new InvalidOperationException("NATIVE PATCH INSTALLER TEST FAILED: "+label);
   lines.Add("PASS "+label);File.WriteAllLines(output,lines);
  }
  void Reject(Action action,string label)
  {
   bool rejected=false;try{action();}catch(Exception e)when(e is InvalidOperationException or InvalidDataException){rejected=true;}
   Check(rejected,label);
  }
  string packageRoot=Path.Combine(home,"native-package");
  var package=CreatePackage(packageRoot);
  Check(!File.Exists(Path.Combine(package.Directory,"Common.ps1")),"native package planning does not require Common.ps1");
  BundleInstaller.VerifyStage(package);
  Check(true,"synthetic archive staging passes exact file and SHA-256 verification");

  byte[] legacyScript=Encoding.UTF8.GetBytes("Write-Host 'legacy only'"),payloadBytes=Encoding.UTF8.GetBytes("verified payload");
  string legacyHash=Convert.ToHexString(SHA256.HashData(legacyScript));
  string payloadHash=Convert.ToHexString(SHA256.HashData(payloadBytes));
  byte[] checksJson=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[]{
   new{path="Common.ps1",sha256=legacyHash},new{path="payload/data.bin",sha256=payloadHash}
  }));
  byte[] checksBytes=Encoding.UTF8.GetPreamble().Concat(checksJson).ToArray();
  void MakeLegacyZip(string path,byte[] scriptBytes)
  {
   using var zip=ZipFile.Open(path,ZipArchiveMode.Create);
   void Add(string name,byte[] bytes){using var stream=zip.CreateEntry(BundleInstaller.PackageFolder+"/"+name).Open();stream.Write(bytes);}
   Add("checksums.json",checksBytes);Add("Common.ps1",scriptBytes);Add("payload/data.bin",payloadBytes);
  }
  string opaqueZip=Path.Combine(home,"legacy-script-opaque.zip"),opaqueStage=Path.Combine(home,"legacy-script-stage");
  MakeLegacyZip(opaqueZip,legacyScript);BundleInstaller.ExtractArchive(opaqueZip,opaqueStage);
  Check(!File.Exists(Path.Combine(opaqueStage,BundleInstaller.PackageFolder,"Common.ps1"))&&
   File.Exists(Path.Combine(opaqueStage,BundleInstaller.PackageFolder,"payload","data.bin")),
   "legacy Common.ps1 is checksum-verified from the ZIP stream but never extracted");
  string tamperedZip=Path.Combine(home,"legacy-script-tampered.zip");
  MakeLegacyZip(tamperedZip,Encoding.UTF8.GetBytes("tampered script"));
  Reject(()=>BundleInstaller.ExtractArchive(tamperedZip,Path.Combine(home,"legacy-script-tampered-stage")),
   "tampered legacy Common.ps1 is rejected by its in-memory ZIP member hash");

  string disabledRoot=Fixture(home,"停用保护");
  string disabledSlot=Path.Combine(disabledRoot,ModManager.StorageName,"slot");Directory.CreateDirectory(disabledSlot);
  File.WriteAllText(Path.Combine(disabledSlot,"记录.json"),JsonSerializer.Serialize(new{ModId="FixAlpha",Original="mods\\FixAlpha",Files=new Dictionary<string,string>(),Status="disabled"}));
  Reject(()=>PatchInstallPlanner.Create(package,disabledRoot),
   "native planner refuses to install over a disabled component");

  string selectedRoot=Fixture(home,"选择安装");
  Reject(()=>PatchInstallPlanner.Create(package,selectedRoot,["FixAlpha"]),
   "selection planner refuses a missing required companion patch");
  var selected=PatchInstallPlanner.Create(package,selectedRoot,["FixAlpha","FixBeta"]);
  Check(selected.SelectedIds.SequenceEqual(["FixAlpha","FixBeta"])&&
   selected.All.All(x=>x.Relative.StartsWith("mods\\_SpirePatch_Fixes\\",StringComparison.OrdinalIgnoreCase)),
   "selected plan keeps only selected components in the dedicated fixes folder");
  string selectedBackup=PatchFileTransaction.Apply(selected);
  Check(Directory.Exists(selectedBackup)&&File.Exists(Path.Combine(selectedRoot,"mods","_SpirePatch_Fixes","FixAlpha","FixAlpha.dll"))&&
   File.Exists(Path.Combine(selectedRoot,"mods","_SpirePatch_Fixes","FixBeta","FixBeta.dll")),
   "selected C# transaction writes only the selected patch payloads");
  var selectedUndo=PatchRollbackManager.Preview(selectedRoot);
  PatchRollbackManager.Apply(selectedUndo,selectedUndo.Token);
  Check(!Directory.EnumerateFiles(Path.Combine(selectedRoot,"mods"),"*",SearchOption.AllDirectories).Any(),
   "selected batch rollback restores the exact pre-install file state");

  string fullRoot=Fixture(home,"完整安装"),dataRoot=Path.Combine(home,"manager-data");
  var installer=new BundleInstaller(dataRoot);
  var before=Snapshot(fullRoot);
  var preview=await installer.RunAsync(package,fullRoot,"Preview");
  Check(preview.Message=="ReadyToInstall"&&preview.Changed==4&&Same(before,Snapshot(fullRoot)),
   "native preview lists changes without touching game files");
  var baseline=Process.GetProcessesByName("powershell").Select(p=>{try{return p.Id;}finally{p.Dispose();}}).ToHashSet();
  bool powershellStarted=false;
  PatchFileTransaction.BeforeApplyForTests=_=>Thread.Sleep(65);
  try
  {
   var task=installer.RunAsync(package,fullRoot,"Install",preview.Token);
   while(!task.IsCompleted)
   {
    foreach(var process in Process.GetProcessesByName("powershell"))
    {
     try{if(!baseline.Contains(process.Id))powershellStarted=true;}
     finally{process.Dispose();}
    }
    await Task.Delay(10);
   }
   var installed=await task;
   Check(!powershellStarted,"install preview and commit do not launch powershell.exe");
   Check(installed.Message=="Installed"&&Directory.Exists(installed.Backup),
    "native installation writes the legacy backup directory structure");
   Check(!Directory.EnumerateFiles(dataRoot,"InstallerBridge.ps1",SearchOption.AllDirectories).Any()&&
    !Directory.EnumerateFiles(dataRoot,"SelectionBridge.ps1",SearchOption.AllDirectories).Any(),
    "operation records no longer extract either bridge script");
   Check(Directory.EnumerateDirectories(Path.Combine(dataRoot,"操作记录")).All(d=>
    File.Exists(Path.Combine(d,"request.json"))&&File.Exists(Path.Combine(d,"result.json"))&&File.Exists(Path.Combine(d,"执行日志.txt"))),
    "native operation records retain the legacy request/result/log file layout");
   using(var journal=JsonDocument.Parse(File.ReadAllText(Path.Combine(installed.Backup,"restore.json"))))
   {
    var root=journal.RootElement;
    Check(root.GetProperty("status").GetString()=="installed"&&
     root.GetProperty("version").GetString()=="20261102.3"&&
     root.GetProperty("files").GetArrayLength()==4&&
     root.GetProperty("files").EnumerateArray().All(x=>x.TryGetProperty("relative",out _)&&x.TryGetProperty("backup",out _)),
     "new restore.json keeps the legacy version/status/files/backup field contract");
   }
   var move=new PatchFolderMove("FixAlpha","FixAlpha",Path.Combine(fullRoot,"mods","FixAlpha"),
    Path.Combine(fullRoot,"mods",PatchOrganizer.CollectionName,"FixAlpha"),new Dictionary<string,string>());
   var journalMoves=PatchJournalRelocator.Plan(fullRoot,[move]);
   Check(journalMoves.Length==1&&journalMoves[0].UpdatedText.Contains(PatchOrganizer.CollectionName),
    "PatchJournalRelocator can rewrite a journal produced by the native C# transaction engine");
   var undo=await installer.RunAsync(package,fullRoot,"RollbackPreview");
   var restored=await installer.RunAsync(package,fullRoot,"Rollback",undo.Token);
   Check(restored.Message=="RolledBack"&&Same(before,Snapshot(fullRoot)),
    "native C# rollback restores every original file byte-for-byte");
   Check(JsonDocument.Parse(File.ReadAllText(Path.Combine(installed.Backup,"restore.json")))
    .RootElement.GetProperty("status").GetString()=="rolled-back",
    "native rollback finalizes the existing restore.json status field");
  }
  finally{PatchFileTransaction.BeforeApplyForTests=null;}

  string staleRoot=Fixture(home,"过期预览");
  var stalePreview=await installer.RunAsync(package,staleRoot,"Preview");
  string target=Path.Combine(staleRoot,"mods","FixAlpha","FixAlpha.dll");
  Directory.CreateDirectory(Path.GetDirectoryName(target)!);
  File.WriteAllBytes(target,File.ReadAllBytes(Path.Combine(package.Directory,"payload","FixAlpha","FixAlpha.dll")));
  bool staleRejected=false;
  try{await installer.RunAsync(package,staleRoot,"Install",stalePreview.Token);}
  catch(Exception e)when(e is InvalidOperationException or InvalidDataException){staleRejected=true;}
  Check(staleRejected,"changing a target after preview invalidates the confirmation token");

  string failedRoot=Fixture(home,"失败恢复");
  var failedPlan=PatchInstallPlanner.Create(package,failedRoot);
  PatchFileTransaction.BeforeApplyForTests=index=>{if(index==1)throw new IOException("injected native write failure");};
  try
  {
   Reject(()=>PatchFileTransaction.Apply(failedPlan),"an injected mid-transaction write failure is reported");
  }
  finally{PatchFileTransaction.BeforeApplyForTests=null;}
  Check(Snapshot(failedRoot).Count==0,"failed transaction restores all files already written");
  string failedJournal=Directory.EnumerateFiles(Path.Combine(failedRoot,"mod_fix_backups"),"restore.json",SearchOption.AllDirectories).Single();
  Check(JsonDocument.Parse(File.ReadAllText(failedJournal)).RootElement.GetProperty("status").GetString()=="install-failed-restored",
   "failed transaction records the legacy install-failed-restored status");

  string oldJob=Path.Combine(dataRoot,"操作记录","20261120-120000-legacy");Directory.CreateDirectory(oldJob);
  foreach(var pair in new Dictionary<string,string>{{"request.json","legacy request"},{"result.json","legacy result"},{"执行日志.txt","legacy powershell log"},{"InstallerBridge.ps1","legacy bridge marker"}})
   File.WriteAllText(Path.Combine(oldJob,pair.Key),pair.Value);
  string oldJobHash=Catalog.Hash(Path.Combine(oldJob,"result.json"));

  string legacyRoot=Fixture(home,"旧版PowerShell备份");
  string legacyTarget=Path.Combine(legacyRoot,"mods","LegacyFix","LegacyFix.dll");
  Directory.CreateDirectory(Path.GetDirectoryName(legacyTarget)!);
  byte[] original=Encoding.UTF8.GetBytes("legacy original bytes"),installedBytes=Encoding.UTF8.GetBytes("legacy PowerShell installed bytes");
  File.WriteAllBytes(legacyTarget,installedBytes);
  string legacyBackup=Path.Combine(legacyRoot,"mod_fix_backups",
   "STS2-AllFixes-20261102-20261121010101000-11111111111111111111111111111111");
  Directory.CreateDirectory(legacyBackup);
  File.WriteAllBytes(Path.Combine(legacyBackup,"original-0.bak"),original);
  var legacyJournal=new
  {
   version="20261102.3",status="installed",
   files=new[]{new{
    relative="mods\\LegacyFix\\LegacyFix.dll",existed=true,
    oldHash=Convert.ToHexString(SHA256.HashData(original)),
    newHash=Convert.ToHexString(SHA256.HashData(installedBytes)),
    operation="write",backup="original-0.bak"
   }}
  };
  File.WriteAllText(Path.Combine(legacyBackup,"restore.json"),JsonSerializer.Serialize(legacyJournal,new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(true));
  var legacyPreview=await installer.RunAsync(package,legacyRoot,"RollbackPreview");
  Check(legacyPreview.Message=="ReadyToRollback"&&legacyPreview.Files.Length==1,
   "C# rollback reader recognizes a BOM-encoded PowerShell restore.json journal");
  var legacyResult=await installer.RunAsync(package,legacyRoot,"Rollback",legacyPreview.Token);
  Check(legacyResult.Message=="RolledBack"&&File.ReadAllBytes(legacyTarget).SequenceEqual(original),
   "legacy PowerShell backup and journal can be rolled back by the new C# engine");
  Check(JsonDocument.Parse(File.ReadAllText(Path.Combine(legacyBackup,"restore.json")))
   .RootElement.GetProperty("status").GetString()=="rolled-back",
   "C# rollback updates the legacy PowerShell journal in place");
  Check(Catalog.Hash(Path.Combine(oldJob,"result.json"))==oldJobHash&&File.Exists(Path.Combine(oldJob,"InstallerBridge.ps1")),
   "legacy PowerShell operation records remain untouched after C# rollback");

  string preparedRoot=Fixture(home,"未完成旧事务");
  string preparedDir=Path.Combine(preparedRoot,"mod_fix_backups","STS2-AllFixes-20261102-20261121020202000-22222222222222222222222222222222");
  Directory.CreateDirectory(preparedDir);
  File.WriteAllText(Path.Combine(preparedDir,"restore.json"),JsonSerializer.Serialize(new{version="20261102.3",status="prepared",files=Array.Empty<object>()},new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(true));
  bool preparedBlocked=false;
  try{await installer.RunAsync(package,preparedRoot,"Preview");}
  catch(Exception e)when(e is InvalidOperationException or InvalidDataException){preparedBlocked=true;}
  Check(preparedBlocked,"an incomplete legacy installer transaction blocks a new preview");

  Check(!File.Exists(Path.Combine(home,"escape.txt")),"all native installer fixtures remain inside their isolated roots");
 }

 static string Fixture(string parent,string name)
 {
  string root=Path.Combine(parent,name+"-"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(Path.Combine(root,"mods"));
  File.WriteAllText(Path.Combine(root,"SlayTheSpire2.exe"),"");
  File.WriteAllText(Path.Combine(root,".spirepatch-fixture"),"isolated");
  return root;
 }

 static Dictionary<string,string> Snapshot(string root)=>
  Directory.EnumerateFiles(Path.Combine(root,"mods"),"*",SearchOption.AllDirectories)
   .ToDictionary(x=>Path.GetRelativePath(root,x),Catalog.Hash,StringComparer.OrdinalIgnoreCase);
 static bool Same(Dictionary<string,string> a,Dictionary<string,string> b)=>
  a.Count==b.Count&&a.All(x=>b.TryGetValue(x.Key,out var hash)&&hash.Equals(x.Value,StringComparison.OrdinalIgnoreCase));

 static PreparedPackage CreatePackage(string directory)
 {
  Directory.CreateDirectory(directory);
  var components=new List<object>();
  var packageFiles=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  foreach(string id in new[]{"FixAlpha","FixBeta"})
  {
   string folder=Path.Combine(directory,"payload",id,"patch-info");
   Directory.CreateDirectory(folder);
   byte[] dll=Encoding.UTF8.GetBytes(id+" verified native payload");
   byte[] manifest=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{id,name=id,version="1.0.0",has_dll=true}));
   string dllPath=Path.Combine(directory,"payload",id,id+".dll");
   string manifestPath=Path.Combine(directory,"payload",id,id+".json");
   File.WriteAllBytes(dllPath,dll);File.WriteAllBytes(manifestPath,manifest);
   var metadata=new
   {
    schema_version=1,id="native."+id.ToLowerInvariant(),version="1.0.0",
    compatibility=new{profiles=new[]{new{profile_id="fixture",game_files=Array.Empty<object>(),required_mods=Array.Empty<object>(),excluded_mod_ids=Array.Empty<string>()}}},
    detection=new{installed_fingerprints=Array.Empty<object>()},
    relationships=new{
     requires_patches=id=="FixAlpha"?new[]{new{patch_id="native.fixbeta",minimum_version="1.0.0"}}:Array.Empty<object>(),
     requires_when_mod_present=Array.Empty<object>(),conflicts_patch_ids=Array.Empty<string>()
    }
   };
   string metadataPath=Path.Combine(folder,"patch.json");
   File.WriteAllText(metadataPath,JsonSerializer.Serialize(metadata,new JsonSerializerOptions{WriteIndented=true}));
   string dllHash=Convert.ToHexString(SHA256.HashData(dll));
   string manifestHash=Convert.ToHexString(SHA256.HashData(manifest));
   components.Add(new{
    id,required=false,
    files=new object[]{
     new{source="payload/"+id+"/"+id+".dll",name=id+".dll",sha256=dllHash,allowedExisting=new[]{dllHash},isManifest=false},
     new{source="payload/"+id+"/"+id+".json",name=id+".json",sha256=manifestHash,allowedExisting=new[]{manifestHash},isManifest=true}
    }
   });
  }
  string plan=Path.Combine(directory,"plan.json");
  File.WriteAllText(plan,JsonSerializer.Serialize(new{version="20261102.3",components,dependencies=Array.Empty<object>(),retired=Array.Empty<object>()},new JsonSerializerOptions{WriteIndented=true}));
  foreach(string file in Directory.EnumerateFiles(directory,"*",SearchOption.AllDirectories))
  {
   string relative=Path.GetRelativePath(directory,file).Replace('\\','/');
   packageFiles[relative]=Catalog.Hash(file);
  }
  var checks=packageFiles.Select(x=>new{path=x.Key,sha256=x.Value}).ToArray();
  string checksPath=Path.Combine(directory,"checksums.json");
  File.WriteAllText(checksPath,JsonSerializer.Serialize(checks,new JsonSerializerOptions{WriteIndented=true}));
  packageFiles["checksums.json"]=Catalog.Hash(checksPath);
  return new PreparedPackage(directory,packageFiles);
 }
}
