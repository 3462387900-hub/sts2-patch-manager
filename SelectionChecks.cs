using System.IO;
using System.Text.Json;

namespace SpirePatch;
public static class SelectionChecks
{
 public static async Task RunAsync(string zip,string root,string output)
 {
  InstallerChecks.AssertFixture(root);var lines=new List<string>();
  void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);lines.Add("PASS "+name);File.WriteAllLines(output,lines);}
  async Task Reject(Func<Task> action,string name){bool failed=false;try{await action();}catch(Exception ex)when(ex is InvalidOperationException or InvalidDataException){failed=true;}Check(failed,name);}
  Dictionary<string,string> Snapshot(string r)=>Directory.EnumerateFiles(Path.Combine(r,"mods"),"*",SearchOption.AllDirectories).ToDictionary(f=>Path.GetRelativePath(r,f),Catalog.Hash);
  bool Same(Dictionary<string,string> a,Dictionary<string,string> b)=>a.Count==b.Count&&a.All(x=>b.TryGetValue(x.Key,out var h)&&h==x.Value);
  var original=Snapshot(root);var backend=new BundleInstaller(Path.Combine(Path.GetDirectoryName(output)!,"选择安装缓存"));var package=await backend.PrepareAsync(zip);
  var paired=SelectionPlanner.Resolve(root,["Act4FinalAscent"]);Check(paired.Included.ToHashSet().SetEquals(["Act4FinalAscent","SpireTogetherFix"]),"Act4 selection automatically includes its required Spire update");
  Check(SelectionPlanner.Resolve(root,["SpireTogetherFix"]).Included.ToHashSet().SetEquals(paired.Included),"reverse Spire selection includes Act4 and has stable closure");
  await Reject(()=>backend.RunAsync(package,root,"Preview",selectedIds:[]),"empty selection never falls back to complete bundle");
  await Reject(()=>backend.RunAsync(package,root,"Preview",selectedIds:["UnknownFix"]),"unknown component cannot select arbitrary payload");
  var preview=await backend.RunAsync(package,root,"Preview",selectedIds:["LargeHealDisplayFix"]);
  Check(preview.SelectedIds!.SequenceEqual(["LargeHealDisplayFix"])&&preview.Files.All(f=>f.Relative.StartsWith("mods\\LargeHealDisplayFix\\")),"single patch plan contains only its payload and documentation");
  Check(Same(original,Snapshot(root))&&!Directory.Exists(Path.Combine(root,"mod_fix_backups")),"selected preflight changes no files and creates no game backup");
  await Reject(()=>backend.RunAsync(package,root,"Install",preview.Token,["IgnisTurnSyncFix"]),"switching selection invalidates prior confirmation token");
  string hextech=Directory.EnumerateFiles(Path.Combine(root,"mods"),"HextechRunes.dll",SearchOption.AllDirectories).Single();byte[] old=File.ReadAllBytes(hextech);File.WriteAllBytes(hextech,old.Concat(new byte[]{1}).ToArray());
  await Reject(()=>backend.RunAsync(package,root,"Install",preview.Token,["LargeHealDisplayFix"]),"dependency change between preview and confirm blocks selected installation");File.WriteAllBytes(hextech,old);
  var first=await backend.RunAsync(package,root,"Install",preview.Token,["LargeHealDisplayFix"]);var afterFirst=Snapshot(root);
  Check(first.Message=="Installed"&&original.All(x=>afterFirst.TryGetValue(x.Key,out var hash)&&hash==x.Value),"single install leaves every original and unrelated legacy file unchanged");
  Check(afterFirst.Keys.Except(original.Keys).All(p=>p.StartsWith("mods\\LargeHealDisplayFix\\")),"single install adds no unselected fix folders or descriptions");
  var noOp=await backend.RunAsync(package,root,"Preview",selectedIds:["LargeHealDisplayFix"]);Check(noOp.Changed==0,"selected patch recheck has no repeated writes");
  var next=await backend.RunAsync(package,root,"Preview",selectedIds:["IgnisTurnSyncFix"]);await backend.RunAsync(package,root,"Install",next.Token,["IgnisTurnSyncFix"]);
  var undo=await backend.RunAsync(package,root,"RollbackPreview");Check(undo.Files.All(f=>f.Relative.StartsWith("mods\\IgnisTurnSyncFix\\")),"rollback selects latest selected batch, not all installed patches");await backend.RunAsync(package,root,"Rollback",undo.Token);
  Check(Same(afterFirst,Snapshot(root)),"rolling back second batch retains the first independent patch");
  undo=await backend.RunAsync(package,root,"RollbackPreview");await backend.RunAsync(package,root,"Rollback",undo.Token);Check(Same(original,Snapshot(root)),"rolling back first batch restores exact original environment");
  var linked=await backend.RunAsync(package,root,"Preview",selectedIds:["Act4FinalAscent"]);
  Check(linked.SelectedIds!.ToHashSet().SetEquals(paired.Included)&&linked.Files.Any(f=>f.Operation=="remove"&&f.Relative.Contains("ChoraleCompatibilityFix")),"linked selection explicitly includes verified superseded legacy removal");
  await backend.RunAsync(package,root,"Install",linked.Token,["Act4FinalAscent"]);
  var report=Catalog.Scan(root);Check(report.Patches.Where(p=>paired.Included.Contains(p.Patch.Id)).All(p=>p.Status=="已安装"),"joint Act4 and Spire install satisfies real metadata and dependencies");
  undo=await backend.RunAsync(package,root,"RollbackPreview");await backend.RunAsync(package,root,"Rollback",undo.Token);Check(Same(original,Snapshot(root)),"linked rollback restores both fixes and retired legacy files together");
  // Minimal supported environment: unrelated original MODs are not mandatory.
  string minimal=Path.Combine(Path.GetDirectoryName(output)!,"最小模组场景-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(minimal,"mods"));
  void CopyTree(string from,string to){Directory.CreateDirectory(to);foreach(var file in Directory.EnumerateFiles(from))File.Copy(file,Path.Combine(to,Path.GetFileName(file)));foreach(var dir in Directory.EnumerateDirectories(from))CopyTree(dir,Path.Combine(to,Path.GetFileName(dir)));}
  CopyTree(Path.Combine(root,"data_sts2_windows_x86_64"),Path.Combine(minimal,"data_sts2_windows_x86_64"));CopyTree(Path.GetDirectoryName(Directory.EnumerateFiles(Path.Combine(root,"mods"),"HextechRunes.json",SearchOption.AllDirectories).Single())!,Path.Combine(minimal,"mods","海克斯 自选目录"));
  File.WriteAllText(Path.Combine(minimal,"SlayTheSpire2.exe"),"");File.WriteAllText(Path.Combine(minimal,".spirepatch-fixture"),"isolated");var minimalOriginal=Snapshot(minimal);
  await Reject(()=>backend.RunAsync(package,minimal,"Preview"),"complete bundle still rejects missing unrelated original MODs");
  var single=await backend.RunAsync(package,minimal,"Preview",selectedIds:["LargeHealDisplayFix"]);await backend.RunAsync(package,minimal,"Install",single.Token,["LargeHealDisplayFix"]);
  Check(Catalog.Scan(minimal).Patches.Single(p=>p.Patch.Id=="LargeHealDisplayFix").Status=="已安装","single patch installs with only its actual supported dependencies");
  undo=await backend.RunAsync(package,minimal,"RollbackPreview");await backend.RunAsync(package,minimal,"Rollback",undo.Token);Check(Same(minimalOriginal,Snapshot(minimal)),"minimal environment rollback restores all original files");
 }
}
