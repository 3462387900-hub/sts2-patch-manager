using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace SpirePatch;
public partial class MainWindow
{
 string? coopComparison;
 JsonElement? coopPeer,coopLocal;
 List<CoopDifference>? coopRows;
 readonly HashSet<string> coopSelected=new(StringComparer.OrdinalIgnoreCase);
 CoopPlan? coopPlan;CoopOutcome? coopOutcome;
 Button? coopConfirmButton;
 string coopStatus="";
 void ClearCoop(){coopComparison=null;coopPeer=null;coopLocal=null;coopRows=null;coopSelected.Clear();coopPlan=null;coopOutcome=null;coopStatus="";}
 async Task<JsonElement> FreshReportAsync()
 {
  ClearCoop();
  if(!demo){
   if(selectedInstallRoot==null)throw new InvalidOperationException("请先选择游戏目录。");
   string root=selectedInstallRoot;var catalog=sourceCurrent?onlineCatalog:null;
   var definitions=catalog?.Patches??Catalog.Bundled;
   // Invalidate the old snapshot before IO: failures must never reuse a previous success.
   report=null;states=[];ClearSelection();installPreview=null;lastInstallResult=null;modInventory=null;
   var fresh=await Task.Run(()=>CoopAlignment.Scan(root,definitions,catalog?.Commit));
   report=fresh.Report;states=report.Patches;scannedCatalog=definitions;modInventory=fresh.Inventory;
   return fresh.Snapshot;
  }
  return CoopReports.Create(report,states,demo);
 }
 internal async Task ExportFreshReportAsync(string path)=>CoopReports.Write(path,await FreshReportAsync());
 internal async Task CompareFreshReportAsync(string path)
 {
  ClearCoop();
  var peer=await Task.Run(()=>CoopReports.Read(path));
  await SetCoopComparisonAsync(peer);
 }
 async Task SetCoopComparisonAsync(JsonElement peer)
 {
  var local=await FreshReportAsync();
  coopComparison=CoopReports.Compare(local,peer);
  if(!demo){var scan=report!;var inventory=modInventory!;coopRows=await Task.Run(()=>CoopPlanner.Analyze(local,peer,scan,inventory));}
  coopPeer=peer;coopLocal=local;
  toast.Text=demo?"演示清单仅用于查看，不执行真实处理。":"已与本机最新扫描比对。";
 }
 async Task RunReportActionAsync(Func<Task> action)
 {
  if(busy||transactionWriting)return;
  busy=true;content.IsEnabled=false;ClearCoop();UpdateGlobalControls();
  toast.Text=demo?"正在处理演示清单…":"正在重新扫描本机文件，请稍候…";
  try{await action();}
  catch(Exception ex){coopComparison="本次操作未完成，不能确认一致。\n"+ex.Message;toast.Text="清单操作未完成："+ex.Message;}
  finally{busy=false;content.IsEnabled=true;UpdateGlobalControls();if(page=="联机检查")ShowCoop();}
 }
 async void RefreshCoop(){if(coopPeer is not {} peer||busy)return;await RunReportActionAsync(()=>SetCoopComparisonAsync(peer));}
 async void PrepareCoop()
 {
  if(busy||transactionWriting||coopPeer is not {} peer||selectedInstallRoot==null||coopSelected.Count==0)return;
  if(onlineBusy){coopStatus="补丁源正在刷新，请完成后再准备方案。";ShowCoop();return;}
  string root=selectedInstallRoot;string[] chosen=coopSelected.ToArray();bool needsPackage=coopRows!.Any(r=>chosen.Contains(r.Id)&&r.Action==CoopAction.Install);
  busy=true;coopPlan=null;coopOutcome=null;preparationCancellation=new(TimeSpan.FromMinutes(5));coopStatus="正在重新核对差异与适配条件…";ShowCoop();
  try{
   PreparedPackage? package=null;
   if(needsPackage)package=await GetPreparedPackageAsync(preparationCancellation.Token,status=>{coopStatus=status;ShowCoop();});
   var plan=await CoopAlignment.PrepareAsync(installer,package,root,peer,chosen);preparationCancellation.Token.ThrowIfCancellationRequested();
   coopPlan=plan;coopStatus="方案已就绪，请查看具体改动，再确认处理。";
  }catch(Exception e){coopStatus="未能准备方案："+e.Message+"\n未修改游戏文件。请重新扫描并比较。";}
  finally{busy=false;preparationCancellation.Dispose();preparationCancellation=null;ShowCoop();if(coopPlan!=null)_=Dispatcher.BeginInvoke(()=>coopConfirmButton?.BringIntoView());}
 }
 async void ConfirmCoop()
 {
  if(busy||transactionWriting||coopPlan==null)return;
  var plan=coopPlan;busy=true;transactionWriting=true;coopPlan=null;coopStatus="正在再次核对并执行方案，请等待完成…";ShowCoop();
  CoopOutcome? outcome=null;
  try{
   outcome=await CoopAlignment.ApplyAsync(installer,preparedPackage,plan);
   await SetCoopComparisonAsync(plan.Peer);coopOutcome=outcome;
   coopStatus=outcome.Success?"所选操作已完成，已重新扫描。停用保留与完全缺失会作为记录差异显示，双方均未启用时无需再处理。":"处理已停止："+outcome.Error;
  }catch(Exception e){coopOutcome=outcome;coopStatus=(outcome==null?"处理未完成：":"处理已结束，但重新比较失败：")+e.Message;coopRows=null;coopComparison=null;}
  finally{InvalidateLifecycle();modTogglePreview=null;busy=false;transactionWriting=false;ShowCoop();}
 }
 void AddCoopResults(Panel root)
 {
  coopConfirmButton=null;
  if(coopStatus.Length>0){root.Children.Add(Card(V(T(coopStatus,14,busy?gold:ink))));Space(root,12);}
  if(busy){root.Children.Add(new ProgressBar{IsIndeterminate=true,Height=5,Foreground=gold});Space(root,12);}
  if(coopOutcome is {} outcome){
   var result=V(T(outcome.Success?"本次处理完成":"已停止后续处理",18,ink,true),T(outcome.Completed.Length==0?"没有已确认完成的步骤，请结合重新扫描和操作记录核对。":string.Join("\n",outcome.Completed),13,muted));
   Space(result,8);result.Children.Add(T("已移出的补丁可在模组管理中恢复；安装改动可在安装与回滚中检查最近批次。",12,muted));
   result.Children.Add(H(Btn("查看操作记录",()=>OpenUrl(outcome.Journal),"Ghost"),Btn("模组管理",()=>{ShowMods();RefreshMods();},"Ghost"),Btn("安装与回滚",ShowInstaller,"Ghost")));root.Children.Add(Card(result));Space(root,16);
  }
  if(coopComparison==null)return;
  if(coopRows==null){root.Children.Add(Card(T(coopComparison,13,muted)));Space(root,16);return;}
  string heading=coopRows.Count>0&&coopRows.All(r=>r.Diagnosis=="停用记录不同")?$"启用状态已对齐 · {coopRows.Count} 项停用记录不同":coopComparison.Split("\n\n")[0];
  var summary=V(T(heading,18,ink,true));
  if(coopLocal is {} local&&coopPeer is {} peer)summary.Children.Add(T($"本机扫描：{local.GetProperty("time").GetDateTimeOffset().ToLocalTime():yyyy-MM-dd HH:mm:ss}　队友清单：{peer.GetProperty("time").GetDateTimeOffset().ToLocalTime():yyyy-MM-dd HH:mm:ss}",12,muted));
  summary.Children.Add(T("以队友导出时的清单为目标；只调整本机已收录补丁。勾选不会改动文件。",12,muted));root.Children.Add(summary);Space(root,12);
  if(coopPeer is {} legacy&&!CoopPlanner.PresenceReport(legacy)){root.Children.Add(T("这是旧版清单，只支持查看。请双方更新至 0.9.0 或更新版本并重新导出，才能使用一键处理。",13,gold));Space(root,12);}
  var bar=new WrapPanel();foreach(var button in new[]{Btn("全选可处理项",()=>{coopSelected.Clear();foreach(var row in coopRows.Where(r=>r.Selectable))coopSelected.Add(row.Id);coopPlan=null;ShowCoop();},"Ghost"),Btn("清空选择",()=>{coopSelected.Clear();coopPlan=null;ShowCoop();},"Ghost"),Btn("重新扫描并比较",RefreshCoop,"Ghost")}){button.Margin=new(0,0,8,8);bar.Children.Add(button);}
  var process=Btn($"预览一键处理（{coopSelected.Count}）",PrepareCoop,"Primary");process.IsEnabled=coopSelected.Count>0;bar.Children.Add(process);root.Children.Add(bar);Space(root,8);
  root.Children.Add(T($"{coopRows.Count(r=>r.Selectable)} 项可处理 · {coopRows.Count(r=>!r.Selectable)} 项需核对或无需处理。全选包含移出项，执行前会再次展示范围。",12,muted));Space(root,12);
  foreach(var row in coopRows){
   var check=new CheckBox{Content=row.Name,FontSize=15,Foreground=ink,IsChecked=coopSelected.Contains(row.Id),IsEnabled=row.Selectable,Margin=new(0,0,0,7)};
   check.Checked+=(_,_)=>{coopSelected.Add(row.Id);coopPlan=null;ShowCoop();};check.Unchecked+=(_,_)=>{coopSelected.Remove(row.Id);coopPlan=null;ShowCoop();};
   var body=V(check,H(Badge(row.Diagnosis,row.Selectable?gold:muted),T(row.ActionLabel,12,row.Selectable?gold:muted)),T($"本机：{row.Mine}　/　队友：{row.Peer}",12,muted),T(row.Detail,12,muted));root.Children.Add(Card(body,"#BB182942",16));Space(root,8);
  }
  if(coopPlan is {} plan){
   Space(root,12);var review=V(T("确认本次对齐方案",21,ink,true),T("缺失项从已审核修复包安装；多出项移到可恢复的停用区。不会修改队友电脑。",13,muted));
   foreach(var row in plan.Rows)review.Children.Add(T(row.Name+" · "+row.ActionLabel,14,ink));
   if(plan.Install is {} installation){Space(review,10);review.Children.Add(T("实际安装范围（含必要关联）："+string.Join("、",(installation.SelectedIds??[]).Select(id=>Catalog.All.Single(p=>p.Id==id).Name))+$"\n将修改 {installation.Changed} 个文件，保留备份。",13,gold));}
   foreach(var move in plan.Moves){Space(review,8);review.Children.Add(T($"{move.Plan.Name}：{(move.Plan.Enable?"恢复启用":"移出并保留")} {move.Plan.FileCount} 个文件\n{move.Plan.Folder}",12,muted));}
   Space(review,12);review.Children.Add(T("逐步执行并记录结果。任一步骤失败会停止后续处理，已完成项会保留并明确列出；可通过现有恢复入口撤销。",12,muted));Space(review,14);
   coopConfirmButton=Btn("确认一键处理",ConfirmCoop,"Primary");review.Children.Add(H(coopConfirmButton,Btn("取消本次方案",()=>{coopPlan=null;coopStatus="已取消方案，未修改游戏文件。";ShowCoop();},"Ghost")));root.Children.Add(Card(review,"#EDF3E8"));Space(root,12);
   if(plan.Install is {} files){AddPlanGroups(root,"将安装或更新的文件",files.Files,true);}
  }
 }
}
