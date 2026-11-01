using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpirePatch;
internal record CoopMove(string Id,ModTogglePlan Plan,Dictionary<string,string> Files);
internal record CoopPlan(string Root,JsonElement Peer,string[] Requested,CoopDifference[] Rows,InstallResult? Install,CoopMove[] Moves,string Token);
internal record CoopOutcome(bool Success,string[] Completed,string? Error,string Journal);
internal static class CoopAlignment
{
 public static (ScanReport Report,ModInventory Inventory,JsonElement Snapshot) Scan(string root,IReadOnlyList<OnlinePatch>? definitions=null,string? revision=null)
 {
  var report=Catalog.Scan(root,definitions,revision);var inventory=ModManager.Scan(root);
  if(inventory.Issues.Count>0)report=report with{InventoryComplete=false,Issues=report.Issues.Concat(inventory.Issues).Distinct().ToList()};
  return(report,inventory,CoopReports.Create(report,report.Patches,false,inventory));
 }
 public static async Task<CoopPlan> PrepareAsync(BundleInstaller installer,PreparedPackage? package,string root,JsonElement peer,IEnumerable<string> selected)
 {
  root=GameEnvironment.Root(root);if(GameEnvironment.Running(root))throw new InvalidOperationException("请先退出游戏，再准备联机对齐方案。");
  var scan=await Task.Run(()=>Scan(root));var rows=CoopPlanner.Analyze(scan.Snapshot,peer,scan.Report,scan.Inventory);
  string[] chosen=selected.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();if(chosen.Length==0)throw new InvalidOperationException("请至少勾选一项可处理差异。");
  var targets=chosen.Select(id=>rows.SingleOrDefault(r=>r.Id.Equals(id,StringComparison.OrdinalIgnoreCase))??throw new InvalidOperationException("差异已变化，请重新比较："+id)).ToArray();
  foreach(var row in targets)if(!row.Selectable)throw new InvalidOperationException(row.Name+"："+row.Detail);
  string[] downloads=targets.Where(r=>r.Action==CoopAction.Install).Select(r=>r.Id).ToArray();InstallResult? install=null;
  if(downloads.Length>0){
   if(package==null)throw new InvalidOperationException("需要先下载并校验本版修复包。");
   var selection=SelectionPlanner.Resolve(root,downloads,scan.Report);var theirs=CoopReports.Patches(peer);
   foreach(string id in selection.Included)if(!theirs.TryGetValue(id,out var p)||!CoopPlanner.WantsInstalled(p)||CoopPlanner.Value(p,"patchVersion")!=Catalog.All.Single(x=>x.Id==id).Version)throw new InvalidOperationException("必要关联项与队友目标不一致："+id+"。请先统一队友清单，不能强行拆开依赖。");
   var moving=targets.Where(r=>r.Action is CoopAction.Enable or CoopAction.Disable).Select(r=>r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
   foreach(var patch in Catalog.Bundled.Where(p=>selection.Included.Contains(p.ComponentId))){
    var required=patch.Metadata.GetProperty("compatibility").GetProperty("profiles").EnumerateArray().SelectMany(p=>p.GetProperty("required_mods").EnumerateArray()).Select(m=>CoopPlanner.Value(m,"mod_id"));
    if(moving.Contains(patch.ComponentId)||required.Any(moving.Contains))throw new InvalidOperationException("安装项与启停项存在前置关联，请分批处理并重新比较："+patch.Title);
   }
   install=await installer.RunAsync(package,root,"Preview",selectedIds:selection.Included);
  }
  var moves=new List<CoopMove>();
  foreach(var row in targets.Where(r=>r.Action is CoopAction.Enable or CoopAction.Disable)){
   var mod=scan.Inventory.Mods.Single(m=>m.Id.Equals(row.Id,StringComparison.OrdinalIgnoreCase));
   var move=await Task.Run(()=>ModManager.Preview(root,mod.Folder,row.Action==CoopAction.Enable));
   moves.Add(new(row.Id,move,ModManager.FileSnapshot(mod.Folder)));
  }
  string token=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{root,chosen,install=install?.Token,moves=moves.Select(m=>new{m.Id,m.Plan.Token,m.Files})}))));
  return new(root,peer.Clone(),chosen,targets,install,moves.ToArray(),token);
 }
 public static async Task<CoopOutcome> ApplyAsync(BundleInstaller installer,PreparedPackage? package,CoopPlan plan)
 {
  // Rebuild every preflight before the first mutation, including selection closure.
  var current=await PrepareAsync(installer,package,plan.Root,plan.Peer,plan.Requested);
  if(current.Token!=plan.Token)throw new InvalidOperationException("文件或清单已变化。没有执行本次方案，请重新预览。");
  string journal=BundleInstaller.Child(installer.DataRoot,"联机对齐记录/"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".json");Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
  var completed=new List<string>();string? error=null;string phase="准备执行";string backup="";
  void Save(){string temp=journal+".tmp";File.WriteAllText(temp,JsonSerializer.Serialize(new{time=DateTimeOffset.Now,root=plan.Root,phase,requested=plan.Requested,actions=plan.Rows,completed,error,backup},new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,journal,true);}
  Save();
  try{
   if(plan.Install is {} preview){
    phase="安装修复";Save();var installed=await installer.RunAsync(package!,plan.Root,"Install",preview.Token,preview.SelectedIds);backup=installed.Backup;
    completed.Add("安装并校验："+string.Join("、",(installed.SelectedIds??[]).Select(id=>Catalog.All.Single(p=>p.Id==id).Name)));Save();
   }
   foreach(var move in plan.Moves){
    phase=(move.Plan.Enable?"恢复启用：":"移出并保留：")+move.Plan.Name;Save();
    var fresh=await Task.Run(()=>Scan(plan.Root));var row=CoopPlanner.Analyze(fresh.Snapshot,plan.Peer,fresh.Report,fresh.Inventory).SingleOrDefault(r=>r.Id==move.Id);
    if(row?.Action!=(move.Plan.Enable?CoopAction.Enable:CoopAction.Disable))throw new InvalidOperationException("后续启停条件已变化，已停止："+move.Id+"；"+row?.Detail);
    await Task.Run(()=>ModManager.ApplyReviewed(move.Plan,move.Files));completed.Add(phase);Save();
   }
   phase="处理后校验";Save();var final=await Task.Run(()=>Scan(plan.Root));
   if(!final.Report.InventoryComplete)throw new InvalidOperationException("文件操作已执行，但最终扫描不完整，请核对报告。");
   foreach(string id in (plan.Install?.SelectedIds??[]).Concat(plan.Moves.Where(m=>m.Plan.Enable).Select(m=>m.Id)))if(final.Report.Patches.Single(p=>p.Patch.Id==id).Status!="已安装")throw new InvalidOperationException("处理后尚未匹配目标修复："+id);
   foreach(var move in plan.Moves.Where(m=>!m.Plan.Enable)){
    var stored=final.Inventory.Mods.Single(m=>m.Id==move.Id);var files=ModManager.FileSnapshot(stored.Folder);
    if(stored.Enabled||files.Count!=move.Files.Count||files.Any(f=>!move.Files.TryGetValue(f.Key,out var hash)||hash!=f.Value))throw new InvalidOperationException("移出后的保留文件校验未通过："+move.Id);
   }
   phase="完成";Save();return new(true,completed.ToArray(),null,journal);
  }catch(Exception e){error=e.Message;phase="停止，保留已完成步骤";Save();return new(false,completed.ToArray(),error,journal);}
 }
}
