using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace SpirePatch;
public static class AutoUpdateChecks
{
 static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
 static readonly string[] RuntimeFiles=["SpirePatch.exe","SpirePatch.dll","SpirePatch.deps.json","SpirePatch.runtimeconfig.json"];
 static void Manifest(string root,string version,IEnumerable<string> names)=>File.WriteAllText(Path.Combine(root,ManagerAutoUpdate.ManifestName),JsonSerializer.Serialize(new ManagerFileManifest(1,"SpirePatch",version,names.ToDictionary(n=>n,n=>Catalog.Hash(Path.Combine(root,n)))),Json));
 static string Fixture(string parent){string root=Path.Combine(parent,"程序 中文 空格 "+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,ManagerAutoUpdate.FixtureMarker),"isolated");foreach(var file in RuntimeFiles)File.WriteAllText(Path.Combine(root,file),"old:"+file);File.WriteAllText(Path.Combine(root,"retired.dll"),"old retired");Manifest(root,"1.0.0",RuntimeFiles.Append("retired.dll"));Directory.CreateDirectory(Path.Combine(root,"数据"));File.WriteAllText(Path.Combine(root,"数据","偏好.json"),"user preference");File.WriteAllText(Path.Combine(root,"个人备注.txt"),"keep me");return root;}
 static (ManagerRelease Release,string Zip) Package(string parent,string? extra=null,bool declared=false)
 {
  string folder=Path.Combine(parent,"新版本 "+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);foreach(var file in RuntimeFiles)File.WriteAllText(Path.Combine(folder,file),"new:"+file);File.WriteAllText(Path.Combine(folder,"added.json"),"new file");var names=RuntimeFiles.Append("added.json").ToList();if(extra!=null){File.WriteAllText(Path.Combine(folder,extra),"extra");if(declared)names.Add(extra);}Manifest(folder,"1.1.0",names);
  string archive=folder+".zip";using(var zip=ZipFile.Open(archive,ZipArchiveMode.Create)){foreach(var file in Directory.EnumerateFiles(folder))zip.CreateEntryFromFile(file,"尖塔修补站-v1.1.0/"+Path.GetFileName(file));}
  return(Release(archive),archive);
 }
 static (ManagerRelease Release,string Zip) TidyPackage(string parent)
 {
  string folder=Path.Combine(parent,"整洁目录版本 "+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  foreach(var file in RuntimeFiles)File.WriteAllText(Path.Combine(folder,file),"new:"+file);
  var names=RuntimeFiles.ToList();
  var docs=new[]{"00-启动尖塔修补站.lnk","使用说明/使用说明.md","使用说明/本版更新内容.md","使用说明/发布指南.md","使用说明/补丁发布指南.md","使用说明/鸣谢与来源.md","使用说明/THIRD-PARTY-NOTICES.md"};
  foreach(var relative in docs)
  {
   string path=Path.Combine(folder,relative.Replace('/',Path.DirectorySeparatorChar));
   Directory.CreateDirectory(Path.GetDirectoryName(path)!);
   File.WriteAllText(path,"tidy release fixture: "+relative);
   names.Add(relative);
  }
  Manifest(folder,"1.1.0",names);
  string archive=folder+".zip";
  using(var zip=ZipFile.Open(archive,ZipArchiveMode.Create))
   foreach(var file in Directory.EnumerateFiles(folder,"*",SearchOption.AllDirectories))
   {
    string relative=Path.GetRelativePath(folder,file).Replace('\\','/');
    zip.CreateEntryFromFile(file,"尖塔修补站-v1.1.0/"+relative);
   }
  return(Release(archive),archive);
 }
 static ManagerRelease Release(string zip)=>new("1.1.0","isolated automatic updater fixture",DateTimeOffset.UtcNow,"SpirePatch-v1.1.0-win-x64.zip",new FileInfo(zip).Length,Catalog.Hash(zip),ManagerUpdates.RepositoryUrl+"/releases/download/v1.1.0/SpirePatch-v1.1.0-win-x64.zip",ManagerUpdates.Api+"/releases/assets/1",ManagerUpdates.RepositoryUrl+"/releases/tag/v1.1.0");
 static Dictionary<string,string> Snapshot(string root)=>Directory.EnumerateFiles(root).ToDictionary(f=>Path.GetFileName(f)!,f=>Catalog.Hash(f));
 static bool Same(string root,Dictionary<string,string> before){var now=Snapshot(root);return before.Count==now.Count&&before.All(x=>now.GetValueOrDefault(x.Key)==x.Value);}
 static async Task<(int Code,string Status)> Run(ReadyManagerUpdate plan,int failAfter=0,bool recover=false)
 {
  var start=ManagerAutoUpdate.WorkerStart(plan,true,failAfter);if(recover)start.ArgumentList.Add("-Recover");start.RedirectStandardError=true;start.RedirectStandardOutput=true;
  using var process=Process.Start(start)!;var error=process.StandardError.ReadToEndAsync();var output=process.StandardOutput.ReadToEndAsync();await process.WaitForExitAsync();string detail=await error+await output;
  string result=Path.Combine(plan.Job,"result.json");if(!File.Exists(result))throw new InvalidOperationException("Updater did not produce a result: "+detail+(File.Exists(Path.Combine(plan.Job,"error.txt"))?File.ReadAllText(Path.Combine(plan.Job,"error.txt")):""));
  using var doc=JsonDocument.Parse(File.ReadAllText(result));return(process.ExitCode,doc.RootElement.GetProperty("status").GetString()!);
 }
 public static async Task RunAsync(string output)
 {
  Directory.CreateDirectory(Path.GetDirectoryName(output)!);var lines=new List<string>();void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);lines.Add("PASS "+message);File.WriteAllLines(output,lines);}
  string parent=Path.Combine(Path.GetDirectoryName(output)!,"自动更新隔离 "+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(parent);
  var package=Package(parent);string root=Fixture(parent);
  Check(ManagerAutoUpdate.IsCanonicalLauncher(root,Path.Combine(root,"SpirePatch.exe")),"canonical executable path is accepted as the self-update entry");
  Check(!ManagerAutoUpdate.IsCanonicalLauncher(root,Path.Combine(root,"00-启动尖塔修补站.exe"))&&
   !ManagerAutoUpdate.IsCanonicalLauncher(root,null),"renamed executable copies and unknown process paths are not accepted as self-update entries");
  var original=Snapshot(root);
  var plan=await ManagerAutoUpdate.PrepareAsync(package.Release,package.Zip,root,fixture:true);Check(Same(root,original),"preparation verifies and extracts without changing application files");
  Check(File.Exists(Path.Combine(plan.Job,"恢复上一版.cmd")),"recovery command accompanies the verified update plan");
  var run=await Run(plan);Check(run.Code==0&&run.Status=="complete"&&File.ReadAllText(Path.Combine(root,"SpirePatch.dll")).StartsWith("new:"),"external worker replaces application after full preflight");
  Check(!File.Exists(Path.Combine(root,"retired.dll"))&&File.Exists(Path.Combine(root,"added.json")),"obsolete owned files are removed and declared new files installed");
  Check(File.ReadAllText(Path.Combine(root,"数据","偏好.json"))=="user preference"&&File.ReadAllText(Path.Combine(root,"个人备注.txt"))=="keep me","personal data and unrelated files survive automatic update");
  Check(File.Exists(Path.Combine(plan.Job,"backup","SpirePatch.dll")),"changed original program files are preserved in backup");
  // Beta tag identifies the release; archive & worker still use the numeric assembly core.
  var betaRelease=package.Release with{
   Version="1.1.0-beta.1",FileName="SpirePatch-v1.1.0-beta.1-win-x64.zip",
   DownloadUrl=ManagerUpdates.RepositoryUrl+"/releases/download/v1.1.0-beta.1/SpirePatch-v1.1.0-beta.1-win-x64.zip",
   ReleaseUrl=ManagerUpdates.RepositoryUrl+"/releases/tag/v1.1.0-beta.1"};
  string betaRoot=Fixture(parent);
  var betaOriginal=Snapshot(betaRoot);
  var betaPlan=await ManagerAutoUpdate.PrepareAsync(betaRelease,package.Zip,betaRoot,fixture:true);
  Check(betaPlan.Request.ToVersion=="1.1.0"&&Same(betaRoot,betaOriginal),
   "Beta plan preserves numeric manifest and makes no changes before confirmation");
  var betaRun=await Run(betaPlan);
  Check(betaRun.Code==0&&betaRun.Status=="complete"&&
   File.ReadAllText(Path.Combine(betaRoot,"SpirePatch.dll")).StartsWith("new:"),
   "Beta update runs verified forward-only worker instead of bypassing safeguards");
  var betaRestored=await Run(betaPlan,recover:true);
  Check(betaRestored.Code==0&&betaRestored.Status=="restored"&&Same(betaRoot,betaOriginal),
   "Beta upgrade uses existing byte-exact rollback");
  var equalCore=betaRelease with{
   Version="1.0.0-beta.2",FileName="SpirePatch-v1.0.0-beta.2-win-x64.zip",
   DownloadUrl=ManagerUpdates.RepositoryUrl+"/releases/download/v1.0.0-beta.2/SpirePatch-v1.0.0-beta.2-win-x64.zip",
   ReleaseUrl=ManagerUpdates.RepositoryUrl+"/releases/tag/v1.0.0-beta.2"};
  try{await ManagerAutoUpdate.PrepareAsync(equalCore,package.Zip,betaRoot,fixture:true);
   throw new InvalidOperationException("same-core Beta overwrite accepted");}
  catch(InvalidDataException){}
  Check(Same(betaRoot,betaOriginal),
   "same-core Beta cannot overwrite installed application");

  // Verify the real player-facing directory transition: old root-level documents
  // are removed, nested documentation and the Chinese shortcut are installed, and rollback restores the old layout.
  root=Fixture(parent);
  File.WriteAllText(Path.Combine(root,"使用说明.md"),"old root readme");
  File.WriteAllText(Path.Combine(root,"发布指南.md"),"old root guide");
  Manifest(root,"1.0.0",RuntimeFiles.Append("retired.dll").Append("使用说明.md").Append("发布指南.md"));
  var tidyOriginal=Snapshot(root);
  var tidyPackage=TidyPackage(parent);
  var tidyPlan=await ManagerAutoUpdate.PrepareAsync(tidyPackage.Release,tidyPackage.Zip,root,fixture:true);
  var tidyRun=await Run(tidyPlan);
  Check(tidyRun.Code==0&&tidyRun.Status=="complete"&&
   !File.Exists(Path.Combine(root,"使用说明.md"))&&
   File.Exists(Path.Combine(root,"使用说明","使用说明.md"))&&
   File.Exists(Path.Combine(root,"使用说明","本版更新内容.md"))&&
   File.Exists(Path.Combine(root,"00-启动尖塔修补站.lnk")),
   "automatic update migrates the old player directory to grouped documentation and installs the Chinese shortcut");
  Check(File.ReadAllText(Path.Combine(root,"数据","偏好.json"))=="user preference"&&
   File.ReadAllText(Path.Combine(root,"个人备注.txt"))=="keep me",
   "tidy directory migration preserves user data and unrelated root files");
  var tidyRestored=await Run(tidyPlan,recover:true);
  bool groupedDocumentsRemain=Directory.Exists(Path.Combine(root,"使用说明"))&&
   Directory.EnumerateFiles(Path.Combine(root,"使用说明"),"*",SearchOption.AllDirectories).Any();
  Check(tidyRestored.Code==0&&tidyRestored.Status=="restored"&&Same(root,tidyOriginal)&&
   !groupedDocumentsRemain&&
   File.ReadAllText(Path.Combine(root,"使用说明.md"))=="old root readme"&&
   File.ReadAllText(Path.Combine(root,"发布指南.md"))=="old root guide",
   "rollback restores all previous program files and removes new grouped documents (an empty folder is harmless)");

  var recovered=await Run(plan,recover:true);Check(recovered.Code==0&&recovered.Status=="restored"&&Same(root,original),"recovery restores byte-exact previous application including retired files");
  root=Fixture(parent);original=Snapshot(root);plan=await ManagerAutoUpdate.PrepareAsync(package.Release,package.Zip,root,fixture:true);run=await Run(plan,failAfter:2);Check(run.Code!=0&&run.Status=="restored"&&Same(root,original),"interrupted file replacement automatically restores previous application");
  root=Fixture(parent);original=Snapshot(root);plan=await ManagerAutoUpdate.PrepareAsync(package.Release,package.Zip,root,fixture:true);using(var locked=new FileStream(Path.Combine(root,"SpirePatch.dll"),FileMode.Open,FileAccess.Read,FileShare.Read)){run=await Run(plan);Check(run.Code!=0&&run.Status=="restored"&&Same(root,original),"locked application file triggers rollback without forcing another process closed");}
  root=Fixture(parent);plan=await ManagerAutoUpdate.PrepareAsync(package.Release,package.Zip,root,fixture:true);File.WriteAllText(Path.Combine(root,"SpirePatch.dll"),"later edit");original=Snapshot(root);run=await Run(plan);Check(run.Status=="failed"&&Same(root,original),"changed application invalidates stale update before any replacement");
  root=Fixture(parent);original=Snapshot(root);plan=await ManagerAutoUpdate.PrepareAsync(package.Release,package.Zip,root,fixture:true);File.AppendAllText(Path.Combine(plan.Job,"payload","SpirePatch.dll"),"tampered");run=await Run(plan);Check(run.Status=="failed"&&Same(root,original),"changed staged payload is rejected by external worker");
  root=Fixture(parent);plan=await ManagerAutoUpdate.PrepareAsync(package.Release,package.Zip,root,fixture:true);File.AppendAllText(Path.Combine(plan.Job,"request.json")," ");try{ManagerAutoUpdate.WorkerStart(plan,true);throw new Exception("changed request accepted");}catch(InvalidDataException){}Check(true,"changed updater request cannot be handed off");
  root=Fixture(parent);plan=await ManagerAutoUpdate.PrepareAsync(package.Release,package.Zip,root,fixture:true);await Run(plan);File.WriteAllText(Path.Combine(root,"SpirePatch.dll"),"later version");original=Snapshot(root);run=await Run(plan,recover:true);Check(run.Status=="failed"&&Same(root,original),"manual recovery refuses to overwrite later file changes");
  root=Fixture(parent);plan=await ManagerAutoUpdate.PrepareAsync(package.Release,package.Zip,root,fixture:true);await Run(plan);File.WriteAllText(Path.Combine(plan.Job,"backup","SpirePatch.dll"),"damaged");original=Snapshot(root);run=await Run(plan,recover:true);Check(run.Status=="failed"&&Same(root,original),"damaged backup is rejected before manual recovery changes files");
  foreach(var bad in new[]{Package(parent,"个人备注.txt",true),Package(parent,"unlisted.txt")}){root=Fixture(parent);original=Snapshot(root);try{await ManagerAutoUpdate.PrepareAsync(bad.Release,bad.Zip,root,fixture:true);throw new Exception("unsafe package accepted");}catch(InvalidDataException){}Check(Same(root,original),"unowned-file collision or undeclared archive file cannot overwrite current application");}
  foreach(string badName in new[]{"尖塔修补站-v1.1.0/../escape.txt","尖塔修补站-v1.1.0/数据/偏好.json","尖塔修补站-v1.1.0/CON.txt"}){
   string zip=Path.Combine(parent,Guid.NewGuid().ToString("N")+".zip");using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create)){using var writer=new StreamWriter(archive.CreateEntry(badName).Open());writer.Write("bad");}
   root=Fixture(parent);original=Snapshot(root);try{await ManagerAutoUpdate.PrepareAsync(Release(zip),zip,root,fixture:true);throw new Exception("unsafe path accepted");}catch(InvalidDataException){}Check(Same(root,original),"traversal, personal data and device-name archive paths are rejected");
  }
  foreach(bool link in new[]{false,true}){string zip=Path.Combine(parent,Guid.NewGuid().ToString("N")+".zip");using(var archive=ZipFile.Open(zip,ZipArchiveMode.Create)){var entry=archive.CreateEntry("尖塔修补站-v1.1.0/duplicate.txt");if(link)entry.ExternalAttributes=unchecked((int)0xA0000000);else archive.CreateEntry("尖塔修补站-v1.1.0/Duplicate.txt");}root=Fixture(parent);original=Snapshot(root);try{await ManagerAutoUpdate.PrepareAsync(Release(zip),zip,root,fixture:true);throw new Exception("unsafe archive accepted");}catch(InvalidDataException){}Check(Same(root,original),"duplicate-case paths and archive links are rejected without program writes");}
  root=Fixture(parent);original=Snapshot(root);using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();try{await ManagerAutoUpdate.PrepareAsync(package.Release,package.Zip,root,cancelled.Token,true);throw new Exception("cancel ignored");}catch(OperationCanceledException){}}Check(Same(root,original),"cancelled preparation leaves the application unchanged");
  root=Fixture(parent);plan=await ManagerAutoUpdate.PrepareAsync(package.Release,package.Zip,root,fixture:true);original=Snapshot(root);using(var mutex=new Mutex(false,ManagerAutoUpdate.MutexName(root))){bool held=mutex.WaitOne(0);try{run=await Run(plan);Check(run.Status=="failed"&&Same(root,original),"another running manager blocks update through shared directory mutex");}finally{if(held)mutex.ReleaseMutex();}}
 }
}
