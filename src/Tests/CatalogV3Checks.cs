using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpirePatch;
public static class CatalogV3Checks
{
 public static readonly string[] Added=["TurnEndOrderFix","VictoryBreedsArroganceSyncFix","KakarotElbowTargetFix","NymphKazdelRewardFix","Act3SnapshotSaveFix","NeowOptionOrderFix"];
 public static void Contract(string output)
 {
  using var doc=Catalog.Plan();var components=doc.RootElement.GetProperty("components").EnumerateArray().ToArray();var checks=new List<string>();
  void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);checks.Add("PASS "+name);}
  Check(components.Length==17&&components.Select(c=>c.GetProperty("id").GetString()).ToHashSet().SetEquals(Catalog.All.Select(p=>p.Id)),"embedded catalog and installer plan cover the same 17 IDs");
  Check(doc.RootElement.GetProperty("version").GetString()=="20261102.3","payload plan version is v3");
  foreach(string id in Added){var component=components.Single(c=>c.GetProperty("id").GetString()==id);var files=component.GetProperty("files").EnumerateArray().ToArray();var patch=Catalog.Bundled.Single(p=>p.ComponentId==id);
   Check(files.Length==2&&files.All(f=>f.GetProperty("name").GetString() is {} name&&(name==id+".dll"||name==id+".json"))&&patch.Metadata.GetProperty("detection").GetProperty("installed_fingerprints").EnumerateArray().All(f=>files.Any(p=>p.GetProperty("name").GetString()==f.GetProperty("relative_path").GetString()&&p.GetProperty("sha256").GetString()==f.GetProperty("sha256").GetString())),id+" installs only matching runtime DLL and manifest, without patch-info");
  }
  File.WriteAllLines(output,checks);
 }
 public static async Task RunAsync(string zip,string root,string output)
 {
  InstallerChecks.AssertFixture(root);var lines=new List<string>();void Check(bool ok,string label){if(!ok)throw new InvalidOperationException(label);lines.Add("PASS "+label);File.WriteAllLines(output,lines);}
  async Task Reject(Func<Task> action,string label){try{await action();}catch(Exception e)when(e is InvalidOperationException or InvalidDataException or OperationCanceledException){Check(true,label);return;}throw new InvalidOperationException(label);}
  Dictionary<string,string> Files()=>Directory.EnumerateFiles(Path.Combine(root,"mods"),"*",SearchOption.AllDirectories).ToDictionary(f=>Path.GetRelativePath(root,f),Catalog.Hash);
  bool Same(Dictionary<string,string> a){var b=Files();return a.Count==b.Count&&a.All(f=>b.GetValueOrDefault(f.Key)==f.Value);}
  var baseline=Files();var installer=new BundleInstaller(Path.Combine(Path.GetDirectoryName(output)!,"17项安装缓存"));var package=await installer.PrepareAsync(zip);
  Check(Catalog.Scan(root).Patches.Count==17,"real scan covers all 17 patches");
  var nymph=SelectionPlanner.Resolve(root,["NymphKazdelRewardFix"]);
  Check(nymph.Report.Patches.Single(p=>p.Patch.Id=="CyanrainNymphBeta").Status=="已安装"&&!nymph.Included.Contains("CyanrainNymphBeta"),"Nymph reward uses the verified existing base repair without reinstalling it");
  var snapshotSelection=SelectionPlanner.Resolve(root,["Act3SnapshotSaveFix"]);
  Check(snapshotSelection.Included.Contains("Act4FinalAscent")&&snapshotSelection.Included.Contains("SpireTogetherFix"),"snapshot fix coordinates Act4 and an already-present companion");
  string spire=ModManager.Scan(root).Mods.Single(m=>m.Id=="SpireTogetherFix").Folder,parked=Path.Combine(Path.GetDirectoryName(output)!,"临时保留联机补丁-"+Guid.NewGuid().ToString("N"));Directory.Move(spire,parked);
  try{Check(!SelectionPlanner.Resolve(root,["Act3SnapshotSaveFix"]).Included.Contains("SpireTogetherFix"),"snapshot fix does not auto-install an absent SpireTogetherFix");}finally{Directory.Move(parked,spire);}
  using(var canceled=new CancellationTokenSource()){canceled.Cancel();await Reject(()=>installer.PrepareAsync(zip,canceled.Token),"cancelled preparation writes no game files");}
  var inventory=ModManager.Scan(root);var wine=inventory.Mods.Single(m=>m.Id=="STS2_WineFox");string duplicate=Path.Combine(root,"mods","重复测试");Directory.CreateDirectory(duplicate);string duplicateFile=Path.Combine(duplicate,"duplicate.json");File.Copy(wine.Manifest,duplicateFile);
  try{await Reject(()=>installer.RunAsync(package,root,"Preview",selectedIds:[Added[0]]),"duplicate dependency ID blocks installation");}finally{File.Delete(duplicateFile);Directory.Delete(duplicate);}
  var initialPreview=await installer.RunAsync(package,root,"Preview",selectedIds:[Added[0]]);string dependency=Path.Combine(wine.Folder,"STS2_WineFox.dll");byte[] original=File.ReadAllBytes(dependency);File.WriteAllBytes(dependency,original.Concat(new byte[]{1}).ToArray());
  try{await Reject(()=>installer.RunAsync(package,root,"Install",initialPreview.Token,[Added[0]]),"changed dependency invalidates confirmation");}finally{File.WriteAllBytes(dependency,original);}
  Check(Same(baseline),"preflight, cancellation and rejection leave every mod byte unchanged");
  foreach(string id in Added){
   var preview=await installer.RunAsync(package,root,"Preview",selectedIds:[id]);
   Check(Same(baseline)&&preview.Files.Where(f=>f.Relative.StartsWith("mods\\"+id+"\\")).Count()==2,id+" previews its two runtime files without changing mods");
   var installed=await installer.RunAsync(package,root,"Install",preview.Token,preview.SelectedIds);var after=Catalog.Scan(root);
   Check(installed.Version=="20261102.3"&&after.Patches.Where(p=>installed.SelectedIds!.Contains(p.Patch.Id)).All(p=>p.Status=="已安装"),id+" installs and validates required closure");
   Check(!Directory.Exists(Path.Combine(root,"mods",id,"patch-info")),id+" keeps descriptive JSON outside game mods");
   var noop=await installer.RunAsync(package,root,"Preview",selectedIds:[id]);Check(noop.Changed==0,id+" repeated installation has no writes");
   if(id==Added[0]||id==Added[1]){string journal=Path.Combine(installed.Backup,"restore.json");var legacy=JsonNode.Parse(File.ReadAllText(journal))!;legacy["version"]=id==Added[0]?"20261102.1":"20261102.2";File.WriteAllText(journal,legacy.ToJsonString());}
   var undo=await installer.RunAsync(package,root,"RollbackPreview");await installer.RunAsync(package,root,"Rollback",undo.Token);
   Check(Same(baseline),id+" restores exact prior bytes including legacy journal version compatibility");
  }
  var combined=await installer.RunAsync(package,root,"Preview",selectedIds:Added);var done=await installer.RunAsync(package,root,"Install",combined.Token,combined.SelectedIds);var scan=CoopAlignment.Scan(root);var peer=scan.Snapshot;
  Check(Added.All(id=>scan.Report.Patches.Single(p=>p.Patch.Id==id).Status=="已安装"),"all six new fixes install together and enter coop reports");
  var rollback=await installer.RunAsync(package,root,"RollbackPreview");await installer.RunAsync(package,root,"Rollback",rollback.Token);Check(Same(baseline),"combined batch rollback restores baseline");
  scan=CoopAlignment.Scan(root);var diffs=CoopPlanner.Analyze(scan.Snapshot,peer,scan.Report,scan.Inventory);
  Check(Added.All(id=>diffs.Any(r=>r.Id==id&&r.Action==CoopAction.Install)),"coop recognizes every new missing patch as an installable difference");
  var align=await CoopAlignment.PrepareAsync(installer,package,root,peer,Added);var outcome=await CoopAlignment.ApplyAsync(installer,package,align);Check(outcome.Success,"coop one-click alignment installs all six with their dependencies");
  rollback=await installer.RunAsync(package,root,"RollbackPreview");await installer.RunAsync(package,root,"Rollback",rollback.Token);Check(Same(baseline),"coop batch remains reversible");
 }
}
