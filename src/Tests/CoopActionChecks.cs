using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpirePatch;
internal static class CoopActionChecks
{
 public static void Run(string output)
 {
  var lines=new List<string>();void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);lines.Add("PASS "+name);File.WriteAllLines(output,lines);}
  string root=WorkspaceChecks.Fixture(Path.GetDirectoryName(output)!);var inventory=ModManager.Scan(root);
  string id="LargeHealDisplayFix";var info=Catalog.All.Single(p=>p.Id==id);
  ScanReport Report(string status,bool matches)=>new(root,inventory.Mods.Count,[new(info,status,"test",matches)],[],DateTime.Now,Catalog.Revision);
  JsonElement Snapshot(string status,string presence,bool matches=true,string component="LargeHealDisplayFix")=>JsonSerializer.SerializeToElement(new{schema=2,presenceEvidenceVersion=1,catalogVersion=Catalog.Revision,inventoryComplete=true,mode="read-only",time=DateTimeOffset.Now,issues=Array.Empty<string>(),patches=new[]{new{id=component,name=component,status,filesMatch=matches,presence,patchVersion=Catalog.All.First(p=>p.Id==component).Version}}});
  JsonElement Edit(JsonElement value,Action<JsonObject> action){var node=JsonNode.Parse(value.GetRawText())!.AsObject();action(node);return JsonSerializer.SerializeToElement(node);}
  CoopDifference Row(JsonElement me,JsonElement peer)=>CoopPlanner.Analyze(me,peer,Report(me.GetProperty("patches")[0].GetProperty("status").GetString()!,me.GetProperty("patches")[0].GetProperty("filesMatch").GetBoolean()),inventory).Single();
  var installed=Snapshot("已安装","active");var missing=Snapshot("待修复","absent",false);
  Check(Row(missing,installed).Action==CoopAction.Install&&Row(missing,installed).Diagnosis=="本机缺少","missing local patch gets install action");
  Check(Row(installed,missing).Action==CoopAction.Disable&&Row(installed,missing).Diagnosis=="本机多出","extra standalone patch gets reversible removal");
  Check(Row(Snapshot("待修复","active",false),installed).Diagnosis=="版本或文件不同","old existing file is not misdiagnosed as absent");
  Check(!Row(installed,Snapshot("待修复","active",false)).Selectable,"newer local patch is never downgraded to older teammate");
  Check(!Row(Snapshot("不适用","absent",false),installed).Selectable,"missing original MOD blocks patch-only install");
  Check(!Row(Snapshot("需核对","active",false),installed).Selectable,"unknown file cannot be overwritten");
  Check(!Row(installed,Snapshot("需核对","absent",false)).Selectable,"uncertain peer cannot trigger removal");
  Check(!Row(installed,Edit(missing,n=>n.Remove("presenceEvidenceVersion"))).Selectable,"legacy report remains read-only");
  Check(CoopPlanner.Analyze(installed,Edit(installed,n=>{n.Remove("presenceEvidenceVersion");n["patches"]![0]!.AsObject().Remove("presence");}),Report("已安装",true),inventory).Count==0,"legacy same-status report does not create false presence differences");
  Check(!Row(installed,Edit(missing,n=>n["inventoryComplete"]=false)).Selectable,"incomplete teammate report blocks actions");
  Check(!Row(installed,Edit(missing,n=>n["patches"]![0]!["status"]="已安装")).Selectable,"contradictory presence and status blocks removal");
  Check(!Row(missing,Edit(installed,n=>n["patches"]![0]!["patchVersion"]="99.0.0")).Selectable,"unsupported peer patch version blocks installation");
  Check(!Row(installed,Edit(missing,n=>n["patches"]!.AsArray().Clear())).Selectable,"missing report record is not proof of an absent patch");
  var disabled=Snapshot("已停用","disabled",false);Check(!Row(disabled,missing).Selectable&&Row(disabled,missing).Diagnosis=="停用记录不同","disabled versus absent does not request repeated removal or download");
  var originalId="Act4FinalAscent";var originalInfo=Catalog.All.Single(p=>p.Id==originalId);var originalInventory=new ModInventory([new(originalId,originalId,"1","","",Path.Combine(root,"mods",originalId),"",true,[])],[]);
  var originalReport=new ScanReport(root,1,[new(originalInfo,"已安装","",true)],[],DateTime.Now,Catalog.Revision);
  var originalRow=CoopPlanner.Analyze(Snapshot("已安装","active",true,originalId),Snapshot("不适用","absent",false,originalId),originalReport,originalInventory).Single();
  Check(!originalRow.Selectable&&originalRow.Diagnosis=="原 MOD 内置修复不同","in-place patch never removes its original MOD");
  inventory.Mods.Add(new("Dependent","Dependent","1","","",Path.Combine(root,"mods","Dependent"),"",true,[new(id)]));Check(!Row(installed,missing).Selectable,"enabled dependent blocks removing its patch dependency");inventory.Mods.RemoveAt(inventory.Mods.Count-1);
  inventory.Mods.Add(new("Shared","Shared","1","","",inventory.Mods.Single(m=>m.Id==id).Folder,"",true,[]));Check(!Row(installed,missing).Selectable,"shared folder cannot be moved as one patch");inventory.Mods.RemoveAt(inventory.Mods.Count-1);
  inventory.Issues.Add("duplicate");Check(!Row(installed,missing).Selectable,"inventory issues disable all actions");
 }
 public static async Task IntegrationAsync(string zip,string fixture,string output)
 {
  InstallerChecks.AssertFixture(fixture);string home=Path.Combine(Path.GetDirectoryName(output)!,"联机处理事务-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(home);
  string root=Path.Combine(home,"测试游戏");
  void Copy(string from,string to){GameEnvironment.NoLinks(from);Directory.CreateDirectory(to);foreach(var file in Directory.EnumerateFiles(from)){GameEnvironment.NoLinks(file);File.Copy(file,Path.Combine(to,Path.GetFileName(file)));}foreach(var dir in Directory.EnumerateDirectories(from))Copy(dir,Path.Combine(to,Path.GetFileName(dir)));}
  Copy(fixture,root);InstallerChecks.AssertFixture(root);
  var lines=new List<string>();void Check(bool ok,string label){if(!ok)throw new InvalidOperationException(label);lines.Add("PASS "+label);File.WriteAllLines(output,lines);}
  async Task Reject(Func<Task> action,string label){try{await action();}catch(InvalidOperationException){Check(true,label);return;}Check(false,label);}
  var backend=new BundleInstaller(Path.Combine(home,"缓存"));var package=await backend.PrepareAsync(zip);
  // Bring only these independent reviewed fixes to a known baseline in the copied fixture.
  string first="LargeHealDisplayFix",second="IgnisTurnSyncFix";
  foreach(string id in new[]{first,second}){
   var mod=ModManager.Scan(root).Mods.SingleOrDefault(m=>m.Id==id);
   if(mod!=null&&mod.Enabled){string target=Path.Combine(home,"原有-"+id);Directory.Move(mod.Folder,target);}
  }
  var initial=CoopAlignment.Scan(root);Check(initial.Report.Patches.Where(p=>p.Patch.Id==first||p.Patch.Id==second).All(p=>p.Status=="待修复"),"fixture has exact dependencies for two absent fixes");
  var pre=await backend.RunAsync(package,root,"Preview",selectedIds:[first]);await backend.RunAsync(package,root,"Install",pre.Token,[first]);
  var peer=CoopAlignment.Scan(root).Snapshot; // Peer has first, lacks second.
  CoopReports.Write(Path.Combine(home,"队友清单.json"),peer);
  var modFirst=ModManager.Scan(root).Mods.Single(m=>m.Id==first);Directory.Move(modFirst.Folder,Path.Combine(home,"移出的第一个补丁"));
  pre=await backend.RunAsync(package,root,"Preview",selectedIds:[second]);await backend.RunAsync(package,root,"Install",pre.Token,[second]);
  var local=CoopAlignment.Scan(root);var rows=CoopPlanner.Analyze(local.Snapshot,peer,local.Report,local.Inventory);
  File.WriteAllText(Path.Combine(home,"差异诊断.json"),JsonSerializer.Serialize(new{local=local.Snapshot,peer,rows},new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
  Check(rows.Single(r=>r.Id==first).Action==CoopAction.Install&&rows.Single(r=>r.Id==second).Action==CoopAction.Disable,"mixed missing and extra fixes get opposite actions");
  Dictionary<string,string> Files()=>Directory.EnumerateFiles(Path.Combine(root,"mods"),"*",SearchOption.AllDirectories).ToDictionary(f=>f,Catalog.Hash);
  var before=Files();var plan=await CoopAlignment.PrepareAsync(backend,package,root,peer,[first,second]);
  Check(before.Count==Files().Count&&before.All(f=>Catalog.Hash(f.Key)==f.Value),"prepare is read-only on mods and captures both actions");
  string dll=Path.Combine(plan.Moves.Single().Plan.Folder,second+".dll");byte[] bytes=File.ReadAllBytes(dll);File.WriteAllBytes(dll,bytes.Concat(new byte[]{0}).ToArray());
  await Reject(()=>CoopAlignment.ApplyAsync(backend,package,plan),"changed files invalidate confirmation before any installation");
  Check(!ModManager.Scan(root).Mods.Any(m=>m.Id==first),"stale plan rejection leaves missing patch absent");File.WriteAllBytes(dll,bytes);
  var outcome=await CoopAlignment.ApplyAsync(backend,package,plan);Check(outcome.Success&&outcome.Completed.Length==2&&File.Exists(outcome.Journal),"one confirmation installs missing and moves extra with journal");
  var after=CoopAlignment.Scan(root);Check(after.Report.Patches.Single(p=>p.Patch.Id==first).Status=="已安装"&&!after.Inventory.Mods.Single(m=>m.Id==second).Enabled,"post-check verifies installed and disabled state");
  Check(before.Where(f=>!f.Key.StartsWith(plan.Moves.Single().Plan.Folder+Path.DirectorySeparatorChar)).All(f=>File.Exists(f.Key)&&Catalog.Hash(f.Key)==f.Value),"mixed processing preserves unrelated mod bytes");
  var moved=after.Inventory.Mods.Single(m=>m.Id==second);Check(Catalog.Hash(Path.Combine(moved.Folder,second+".dll"))==Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),"extra patch retained byte for byte outside mods");
  Check(!CoopPlanner.Analyze(after.Snapshot,peer,after.Report,after.Inventory).Any(r=>r.Selectable&&new[]{first,second}.Contains(r.Id)),"processed items do not offer repeat installation or removal");
  // Restore the removed patch through the same coop workflow using a new peer snapshot.
  var peerNode=JsonNode.Parse(peer.GetRawText())!.AsObject();var record=peerNode["patches"]!.AsArray().Single(p=>p!["id"]!.GetValue<string>()==second)!;record["presence"]="active";record["status"]="已安装";record["filesMatch"]=true;
  var enablePeer=JsonSerializer.SerializeToElement(peerNode);var restore=await CoopAlignment.PrepareAsync(backend,null,root,enablePeer,[second]);
  Check(restore.Install==null&&restore.Moves.Single().Plan.Enable,"known disabled fix restores without download");
  outcome=await CoopAlignment.ApplyAsync(backend,null,restore);Check(outcome.Success&&ModManager.Scan(root).Mods.Single(m=>m.Id==second).Enabled,"one-click restore retains original location");
  var undo=await backend.RunAsync(package,root,"RollbackPreview");Check(undo.Files.All(f=>f.Relative.StartsWith("mods\\"+first+"\\")),"existing installer rollback targets the coop installation batch");await backend.RunAsync(package,root,"Rollback",undo.Token);
  Check(!ModManager.Scan(root).Mods.Any(m=>m.Id==first&&m.Enabled),"coop-installed missing patch can be rolled back");
  var partialPlan=await CoopAlignment.PrepareAsync(backend,package,root,peer,[first,second]);
  string lockedFile=Path.Combine(ModManager.Scan(root).Mods.Single(m=>m.Id==second).Folder,second+".dll");
  using(var held=new FileStream(lockedFile,FileMode.Open,FileAccess.Read,FileShare.Read)){
   var partial=await CoopAlignment.ApplyAsync(backend,package,partialPlan);
   Check(!partial.Success&&partial.Completed.Length==1&&File.ReadAllText(partial.Journal).Contains("completed"),"locked move stops batch and records completed installation");
   Check(ModManager.Scan(root).Mods.Single(m=>m.Id==second).Enabled,"failed move preserves extra patch in original location");
  }
  var retry=await CoopAlignment.PrepareAsync(backend,null,root,peer,[second]);var retried=await CoopAlignment.ApplyAsync(backend,null,retry);
  Check(retried.Success,"remaining move can be retried after releasing file lock");
  restore=await CoopAlignment.PrepareAsync(backend,null,root,enablePeer,[second]);await CoopAlignment.ApplyAsync(backend,null,restore);
  undo=await backend.RunAsync(package,root,"RollbackPreview");await backend.RunAsync(package,root,"Rollback",undo.Token);
  File.WriteAllText(Path.Combine(Path.GetDirectoryName(output)!,"界面测试入口.json"),JsonSerializer.Serialize(new{root,peer=Path.Combine(home,"队友清单.json"),zip}));
 }
}
