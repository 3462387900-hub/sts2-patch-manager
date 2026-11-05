using System.IO;
using System.Text.Json;

namespace SpirePatch;
public static class CoreChecks
{
 public static void Run(string output)
 {
  var lines=new List<string>();
  void Check(bool yes,string label){if(!yes)throw new InvalidOperationException(label);lines.Add("PASS "+label);}
  Check(Catalog.All.Length==17,"catalog contains the 17 reviewed fix components");
  Check(Catalog.Demo().Count(s=>s.Status=="待修复")==2,"demo has two related fixes pending");
  Check(Catalog.Demo(true).All(s=>s.Status=="已安装"),"demo completion state updates without game access");
  ManagedMod FixtureMod(string id,string version,bool enabled,params ModDependency[] dependencies)=>
   new(id,id,version,"test","",Path.Combine("fixture",id),Path.Combine("fixture",id,"manifest.json"),enabled,dependencies);
  var dependencyInventory=new ModInventory(new List<ManagedMod>{
   FixtureMod("fixture.consumer","1.0.0",true,new ModDependency("fixture.core","2.0.0")),
   FixtureMod("fixture.core","1.5.0",true),
   FixtureMod("fixture.missing.consumer","1.0.0",true,new ModDependency("fixture.absent")),
   FixtureMod("fixture.disabled.consumer","1.0.0",true,new ModDependency("fixture.disabled.core")),
   FixtureMod("fixture.disabled.core","1.0.0",false),
   FixtureMod("fixture.range.consumer","1.0.0",true,new ModDependency("fixture.range.core","1.0.0","2.0.0")),
   FixtureMod("fixture.range.core","1.5.0",true),
   FixtureMod("fixture.cycle.a","1.0.0",true,new ModDependency("fixture.cycle.b")),
   FixtureMod("fixture.cycle.b","1.0.0",true,new ModDependency("fixture.cycle.a")),
   FixtureMod("fixture.duplicate.consumer","1.0.0",true,new ModDependency("fixture.duplicate")),
   FixtureMod("fixture.duplicate","1.0.0",true),
   FixtureMod("fixture.duplicate","1.1.0",false)
  },[]);
  var dependencyReport=ModDependencyGraph.Analyze(dependencyInventory);
  Check(dependencyReport.Edges.Single(edge=>edge.Dependent.Id=="fixture.consumer").Status==ModDependencyStatus.VersionMismatch,
   "dependency graph detects prerequisite version below the declared minimum");
  Check(dependencyReport.Edges.Single(edge=>edge.Dependent.Id=="fixture.missing.consumer").Status==ModDependencyStatus.Missing,
   "dependency graph detects a prerequisite that is not installed");
  Check(dependencyReport.Edges.Single(edge=>edge.Dependent.Id=="fixture.disabled.consumer").Status==ModDependencyStatus.DisabledPrerequisite,
   "dependency graph detects an installed but disabled prerequisite");
  Check(dependencyReport.Edges.Single(edge=>edge.Dependent.Id=="fixture.range.consumer").Status==ModDependencyStatus.Satisfied,
   "dependency graph accepts a prerequisite version inside the declared range");
  Check(dependencyReport.Edges.Single(edge=>edge.Dependent.Id=="fixture.duplicate.consumer").Status==ModDependencyStatus.Duplicate,
   "dependency graph refuses ambiguous duplicate prerequisite IDs");
  Check(dependencyReport.Cycles.Length==1&&dependencyReport.Cycles[0].ModIds.Length==2&&
   dependencyReport.Edges.Where(edge=>edge.Dependent.Id.StartsWith("fixture.cycle.",StringComparison.Ordinal)).All(edge=>edge.IsCycle),
   "dependency graph identifies and marks a two-MOD dependency cycle");
  Check(dependencyReport.Summary("fixture.consumer").HasIssue,
   "MOD dependency summary exposes an unsatisfied prerequisite");
  var root=Path.Combine(Path.GetDirectoryName(output)!,"scan-test-中文 空格-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"mods"));File.WriteAllText(Path.Combine(root,"SlayTheSpire2.exe"),"");
  var empty=Catalog.Scan(root);Check(empty.ModCount==0,"empty mod directory scans without errors");Check(empty.Patches.Single(p=>p.Patch.Id=="Act4FinalAscent").Status=="不适用","missing original mod is not recommended");
  var mod=Path.Combine(root,"mods","renamed folder");Directory.CreateDirectory(mod);File.WriteAllText(Path.Combine(mod,"manifest.json"),"{\"id\":\"Act4FinalAscent\",\"has_dll\":true}");File.WriteAllText(Path.Combine(mod,"Act4FinalAscent.dll"),"unknown build");
  var changed=Catalog.Scan(root);Check(changed.Patches.Single(p=>p.Patch.Id=="Act4FinalAscent").Status=="需核对","unknown DLL is not marked compatible");
  var duplicate=Path.Combine(root,"mods","duplicate");Directory.CreateDirectory(duplicate);File.Copy(Path.Combine(mod,"manifest.json"),Path.Combine(duplicate,"manifest.json"));Check(Catalog.Scan(root).Issues.Any(i=>i.Contains("重复")),"duplicate mod IDs are reported");
  Check(File.ReadAllText(Path.Combine(mod,"Act4FinalAscent.dll"))=="unknown build","scanner leaves game files unchanged");
  var settings=Path.Combine(root,"manager-data");
  string disabledSlot=Path.Combine(root,ModManager.StorageName,"fixture-slot");
  Directory.CreateDirectory(Path.Combine(disabledSlot,"内容"));
  File.WriteAllText(Path.Combine(disabledSlot,"记录.json"),"""{"ModId":"fixture.disabled","Original":"mods/fixture-disabled","Files":{},"Status":"disabled"}""");
  File.WriteAllText(Path.Combine(disabledSlot,"内容","disabled.dat"),"disabled payload");
  var environmentSnapshot=ModEnvironmentSnapshots.Create(root,settings,"CoreChecks 快照");
  string dllPath=Path.Combine(mod,"Act4FinalAscent.dll");
  File.WriteAllText(dllPath,"modified after snapshot");
  string extraPath=Path.Combine(mod,"extra-after-snapshot.txt");
  File.WriteAllText(extraPath,"temporary extra file");
  File.Delete(Path.Combine(disabledSlot,"记录.json"));
  var restorePreview=ModEnvironmentSnapshots.PreviewRestore(root,settings,environmentSnapshot.Id);
  Check(restorePreview.ChangedFiles>=1&&restorePreview.AddedFiles>=1&&restorePreview.RemovedFiles>=1,
   "environment snapshot preview detects changed, missing and extra files");
  File.WriteAllText(dllPath,"changed again after preview");
  bool stalePreviewRejected=false;
  try{ModEnvironmentSnapshots.Restore(root,settings,environmentSnapshot.Id,restorePreview.Token);}
  catch(InvalidOperationException){stalePreviewRejected=true;}
  Check(stalePreviewRejected&&File.ReadAllText(dllPath)=="changed again after preview"&&File.Exists(extraPath),
   "environment restore refuses a stale preview without changing live MOD files");
  File.WriteAllText(dllPath,"modified after snapshot");
  var freshRestorePreview=ModEnvironmentSnapshots.PreviewRestore(root,settings,environmentSnapshot.Id);
  var restoreResult=ModEnvironmentSnapshots.Restore(root,settings,environmentSnapshot.Id,freshRestorePreview.Token);
  Check(File.ReadAllText(dllPath)=="unknown build"&&!File.Exists(extraPath)&&
   File.Exists(Path.Combine(disabledSlot,"记录.json"))&&
   File.ReadAllText(Path.Combine(disabledSlot,"内容","disabled.dat"))=="disabled payload",
   "environment restore replaces active and disabled MOD trees with the exact snapshot contents");
  var snapshotEntries=ModEnvironmentSnapshots.List(root,settings);
  Check(snapshotEntries.Length==2&&snapshotEntries.Any(entry=>entry.Id==restoreResult.SafetySnapshotId&&
   entry.Purpose=="恢复前自动保护"),
   "environment restore automatically snapshots the pre-restore state");
  var recoverySafety=ModEnvironmentSnapshots.Create(root,settings,"恢复日志测试");
  string recoveryStageId=Guid.NewGuid().ToString("N");
  string recoveryStage=Path.Combine(root,"尖塔修补站_环境恢复暂存",recoveryStageId);
  string recoveryPrevious=Path.Combine(recoveryStage,"previous");
  Directory.CreateDirectory(recoveryPrevious);
  Directory.Move(Path.Combine(root,"mods"),Path.Combine(recoveryPrevious,"mods"));
  Directory.CreateDirectory(Path.Combine(root,"mods"));
  string partialTarget=Path.Combine(root,"mods","partial-target.txt");
  File.WriteAllText(partialTarget,"interrupted target tree");
  var recoveryJournal=new ModEnvironmentSnapshots.ModEnvironmentRestoreJournal(
   1,Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),recoveryStageId,
   environmentSnapshot.Id,recoverySafety.Id,true,true,true,true,"new-moved-mods",DateTimeOffset.UtcNow);
  ModEnvironmentSnapshots.WriteRestoreJournal(recoveryStage,recoveryJournal);
  ModEnvironmentSnapshots.RecoverInterrupted(root,settings);
  Check(File.ReadAllText(dllPath)=="unknown build"&&!File.Exists(partialTarget)&&!Directory.Exists(recoveryStage),
   "interrupted environment restore is rolled back before MOD scanning");
  string snapshotStore=Directory.GetDirectories(Path.Combine(settings,"MOD环境快照")).Single();
  string snapshotPayload=Path.Combine(snapshotStore,environmentSnapshot.Id,"mods","renamed folder","Act4FinalAscent.dll");
  File.WriteAllText(snapshotPayload,"tampered snapshot payload");
  bool tamperedRejected=false;
  try{ModEnvironmentSnapshots.PreviewRestore(root,settings,environmentSnapshot.Id);}
  catch(InvalidDataException){tamperedRejected=true;}
  Check(tamperedRejected,"environment restore rejects a snapshot whose payload hash no longer matches");
  ModEnvironmentSnapshots.Delete(root,settings,environmentSnapshot.Id);
  ModEnvironmentSnapshots.Delete(root,settings,restoreResult.SafetySnapshotId);
  ModEnvironmentSnapshots.Delete(root,settings,recoverySafety.Id);
  Check(ModEnvironmentSnapshots.List(root,settings).Length==0,
   "environment snapshots can be explicitly deleted without touching live MOD files");
  using(var github=new GitHubModUpdates(settings,root))
  {
   Check(GitHubModUpdates.TryNormalize("https://github.com/example/mod/releases",out var normalized)&&normalized=="example/mod","GitHub repository URL normalization");
   Check(!GitHubModUpdates.TryNormalize("http://github.com/example/mod",out _),"GitHub rejects insecure repository URLs");
   Check(!GitHubModUpdates.TryNormalize("https://example.com/example/mod",out _),"GitHub rejects unrelated hosts");
   github.Bind("example.mod","https://github.com/example/mod");
   Check(github.Repository("example.mod")=="example/mod","GitHub repository binding saved");
  }
  using(var reloaded=new GitHubModUpdates(settings,root))
  {
   Check(reloaded.Repository("example.mod")=="example/mod","GitHub binding survives restart");
   reloaded.Unbind("example.mod");
   Check(reloaded.Repository("example.mod")==null,"GitHub binding can be removed");
  }
  var sourced=GitHubModDiscovery.ManifestCandidates("""{"id":"example.mod","github_url":"https://github.com/example/mod"}""");
  Check(sourced.Length==1&&sourced[0].Repository=="example/mod"&&sourced[0].Verified,"GitHub MOD discovery reads explicit manifest source");
  Check(GitHubModDiscovery.ManifestCandidates("""{"id":"example.mod","homepage":"https://example.org"}""").Length==0,"GitHub discovery ignores non-GitHub sites");
  File.WriteAllLines(output,lines);
 }
}
