using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SpirePatch;

public partial class MainWindow
{
 PatchLifecycleReport? lifecycleReport;
 bool lifecycleStale,lifecycleScanning;
 int lifecycleEpoch;
 bool HasValidLifecycleReport=>lifecycleReport!=null&&selectedInstallRoot!=null&&
  lifecycleReport.Root.Equals(selectedInstallRoot,StringComparison.OrdinalIgnoreCase);
 string LifecycleTag(string id)
 {
  if(lifecycleScanning)return "检查中";
  if(!HasValidLifecycleReport)return lifecycleStale?"需重新检查":"未检查";
  var item=lifecycleReport!.Patches.FirstOrDefault(x=>x.Patch.Id.Equals(id,StringComparison.OrdinalIgnoreCase));
  return item==null?"未收录":LifecycleLabel(item.State);
 }
 void InvalidateLifecycle(string reason="游戏环境已变化，请重新扫描补丁健康状态。")
 {
  lifecycleEpoch++;lifecycleStale=true;
  lifecycleReport=null;
  lifecycleEvidence=LifecycleEvidenceSource.Empty;
  retirePreview=null;
  retiringItem=null;
  lifecycleStatus=reason;
 }
 LifecycleRegistry lifecycleEvidence=LifecycleEvidenceSource.Empty;
 string lifecycleStatus="首次扫描将建立当前环境基线；之后可识别 MOD 文件变化。";
 string lifecycleFilter="全部";
 ModTogglePlan? retirePreview;
 LifecyclePatchItem? retiringItem;

 static string LifecycleLabel(LifecycleState state)=>state switch
 {
  LifecycleState.Compatible=>"已知构建匹配",
  LifecycleState.NeedsReview=>"MOD 已变化，需核对",
  LifecycleState.SuspectedUpstreamFix=>"疑似上游修复",
  LifecycleState.VerifiedUpstreamFix=>"上游修复已验证",
  LifecycleState.Incompatible=>"已确认不兼容",
  LifecycleState.Disabled=>"已停用",
  _=>"暂无确认证据"
 };
 async void RefreshLifecycle()=>await RefreshLifecycleAsync(ShowLifecycle);
 async void RefreshLifecycleFromMods()
 {
  string? key=currentVisit?.Key;
  Action render=key!=null&&key.StartsWith("patch-evidence:",StringComparison.Ordinal)
   ?()=>ShowPatchEvidence(key["patch-evidence:".Length..]):ShowMods;
  await RefreshLifecycleAsync(render);
 }
 async Task RefreshLifecycleAsync(Action render)
 {
  if(busy)return;
  if(selectedInstallRoot==null){LocateGame();return;}
  string root=selectedInstallRoot;
  int epoch=lifecycleEpoch;
  busy=true;lifecycleScanning=true;
  lifecycleStatus="正在扫描 MOD 文件及补丁适配情况…";
  render();
  try
  {
   var scan=await Task.Run(()=>{
    var history=ModHistory.CompareAndStore(root,installer.DataRoot);
    var inventory=ModManager.Scan(root);
    var patchStates=Catalog.Scan(root);
    return (history,inventory,patchStates);
   });
   string evidenceStatus;
   LifecycleRegistry evidence;
   try
   {
    evidence=await LifecycleEvidenceSource.FetchAsync();
    evidenceStatus=$"已检查补丁库：{evidence.Entries.Length} 条有证据的上游记录（未收录的修复不会被推断为已修复）。";
   }
   catch(Exception ex)
   {
    evidence=LifecycleEvidenceSource.Empty;
    evidenceStatus="在线退役证据不可用，仅显示本地状态，禁止自动退役："+ex.Message;
   }
   var evaluated=PatchLifecycle.Evaluate(root,scan.history,evidence,evidenceStatus,scan.patchStates,scan.inventory);
   if(epoch!=lifecycleEpoch||!root.Equals(selectedInstallRoot,StringComparison.OrdinalIgnoreCase))
   {
    InvalidateLifecycle("检查期间环境已变化，请重新扫描。");
    return;
   }
   lifecycleEvidence=evidence;lifecycleReport=evaluated;lifecycleStale=false;
   lifecycleStatus=scan.history.FirstScan
    ?"已建立首次扫描基线；适配标签仅依据已收录条件，不能证明游戏运行没有错误。"
    :$"本次新增变化 {scan.history.Changes.Length} 项 · 尚待核对的 MOD {scan.history.PendingReview.Length} 项。";
  }
  catch(Exception ex){InvalidateLifecycle("扫描未完成，请检查后重试："+ex.Message);}
  finally{lifecycleScanning=false;busy=false;render();}
 }

 void ShowLifecycle()
 {
  var root=V(Header("PATCH LIFECYCLE","补丁健康与退役",
   "MOD 更新后验证旧补丁是否仍然适用；不会仅凭版本号推断 Bug 已修复。"));
  Space(root,18);
  if(selectedInstallRoot==null)
  {
   root.Children.Add(Card(V(T("先选择游戏目录",17,ink,true),Btn("选择游戏目录",ChooseFolder,"Primary"))));
   SetPage("补丁健康",root,restore:ShowLifecycle);return;
  }
  var scan=Btn("扫描更新与修复状态",RefreshLifecycle,"Primary");
  scan.IsEnabled=!busy;
  root.Children.Add(H(scan,Btn("返回模组管理",ShowMods,"Ghost")));Space(root,13);
  root.Children.Add(T(lifecycleStatus,13,busy?gold:muted));Space(root,12);
  if(!HasValidLifecycleReport||lifecycleReport is not { } report)
  {
   root.Children.Add(Card(V(T("暂未生成检查报告",16,ink,true),
     T("首次扫描只会保存管理器自己的指纹记录，不会更改 MOD、存档或补丁。",13,muted))));
   SetPage("补丁健康",root,restore:ShowLifecycle);return;
  }
  var change=report.History.Changes;
  var pending=report.History.PendingReview;
  root.Children.Add(Card(V(
   T($"本次发现 {change.Length} 个 MOD 文件变化",17,ink,true),
   T(report.EvidenceStatus,12,muted),
   T(report.History.FirstScan?
     "首次使用只建立基线；下次更新 MOD 后再扫描，就能对比前后的 DLL/PCK 指纹。":
     (change.Length==0?"与上次扫描相比，已监测的 MOD 文件未变化。":
      string.Join("\n",change.Take(12).Select(x=>$"{x.ModId}：{x.OldVersion} → {x.NewVersion}，变化文件 {x.ChangedFiles} 个"))),
      12,muted)
  )));Space(root,14);
  if(pending.Length>0){
   root.Children.Add(T($"尚有 {pending.Length} 个 MOD 更新待人工核对；重复扫描不会自动清除此提醒。",12,gold));
   Space(root,12);
  }
  var filters=H();
  foreach(var f in new[]{"全部","需要核对","可退役"})
  {
   string selected=f;
   filters.Children.Add(Btn(f,()=>{lifecycleFilter=selected;ShowLifecycle();},
    lifecycleFilter==f?"Primary":"Ghost"));
  }
  root.Children.Add(filters);Space(root,12);
  int actionable=report.Patches.Count(x=>x.CanRetire);
  int review=report.Patches.Count(x=>x.State is LifecycleState.NeedsReview or LifecycleState.SuspectedUpstreamFix);
  root.Children.Add(T($"已收录 {report.Patches.Length} 项补丁 · {review} 项待观察/复核 · {actionable} 项有依据可安全停用",13,muted));
  Space(root,12);
  var shown=report.Patches.Where(p=>lifecycleFilter=="全部"||
   (lifecycleFilter=="需要核对"&&(p.State is LifecycleState.NeedsReview or LifecycleState.SuspectedUpstreamFix))||
   (lifecycleFilter=="可退役"&&p.CanRetire)).ToArray();
  if(shown.Length==0)root.Children.Add(T("当前筛选下没有项目。",13,muted));
  foreach(var p in shown)
  {
   var content=V(T(p.Patch.Name,16,ink,true),
      T($"{p.Patch.Id} · {LifecycleLabel(p.State)} · 文件状态：{p.InstallState}",12,p.CanRetire?green:muted));
   Space(content,6);
   content.Children.Add(T(p.Message,12,muted));
   Space(content,5);
   content.Children.Add(T("相关 MOD："+string.Join("、",p.RelatedMods),11,muted));
   if(p.Evidence!=null)
   {
    Space(content,7);
    content.Children.Add(T("依据："+p.Evidence.Note+
     $"\n匹配文件：{p.Evidence.TargetModId}/{p.Evidence.RelativePath} ({p.Evidence.Version})",12,muted));
    foreach(var source in p.Evidence.Sources)
     content.Children.Add(Btn("查看证据来源",()=>OpenUrl(source),"Ghost"));
   }
   if(p.BlockReason!=null){Space(content,6);content.Children.Add(T("无法自动退役："+p.BlockReason,12,gold));}
   if(p.CanRetire)
   {
    Space(content,9);
    var button=Btn("预览安全停用",()=>PrepareRetirement(p),"Primary");
    button.IsEnabled=!busy;
    content.Children.Add(button);
   }
   root.Children.Add(Card(content,"#FFFFFF",16));Space(root,9);
  }
  root.Children.Add(T("说明：建议停用仅适用于已审核、精确匹配上游修复指纹的独立补丁。"+
   "涉及原 MOD 文件替换的内置修复必须另行验证；历史整合包回滚前应先重新启用被停用的补丁。",12,muted));
  SetPage("补丁健康",root,restore:ShowLifecycle);
 }

 async void PrepareRetirement(LifecyclePatchItem item)
 {
  if(busy||lifecycleReport==null)return;
  busy=true;retirePreview=null;retiringItem=null;
  try
  {
   var plan=await Task.Run(()=>PatchLifecycle.PreviewRetire(lifecycleReport,item.Patch.Id,lifecycleEvidence));
   retirePreview=plan;retiringItem=item;
  }
  catch(Exception ex){lifecycleStatus="不能退役："+ex.Message;}
  finally{busy=false;if(retirePreview!=null)ShowRetirementConfirmation();else ShowLifecycle();}
 }

 void ShowRetirementConfirmation()
 {
  if(retirePreview==null||retiringItem==null){ShowLifecycle();return;}
  var plan=retirePreview;var item=retiringItem;
  var root=V(Header("REVIEW RETIREMENT","确认停用旧补丁",item.Patch.Name));Space(root,20);
  root.Children.Add(Card(V(
   T("仅移出 MOD 加载目录，不删除补丁文件",17,ink,true),
   T("退役依据："+item.Evidence?.Note,13,muted),
   T("当前目录："+plan.Folder+ $"\n将保留 {plan.FileCount} 个文件及原路径记录，可在模组管理中重新启用。",13,muted),
   T("注意：本操作不删除历史安装备份。如需执行包含此补丁的旧安装批次回滚，请先重新启用该补丁。",13,gold),
   H(Btn("确认安全停用",ConfirmRetirement,"Primary"),
     Btn("取消",()=>{retirePreview=null;retiringItem=null;ShowLifecycle();},"Ghost"))
  )));SetPage("补丁健康",root,"patch-retire-confirm",ShowRetirementConfirmation);
 }

 async void ConfirmRetirement()
 {
  if(busy||transactionWriting||retirePreview==null||retiringItem==null||selectedInstallRoot==null)return;
  var plan=retirePreview;var item=retiringItem;retirePreview=null;retiringItem=null;
  busy=true;transactionWriting=true;
  try
  {
   await Task.Run(()=>GameEnvironment.WithLock(plan.Root,()=>
   {
    var proof=item.Evidence??throw new InvalidOperationException("缺少可审核的上游修复证据，请重新预览。");
    if(proof.ReviewStatus!="approved"||proof.Decision is not("fixed_upstream" or "incompatible")||
       !PatchLifecycle.MatchesEvidence(plan.Root,ModManager.Scan(plan.Root),proof))
     throw new InvalidOperationException("原 MOD 的文件指纹在预览后发生变化，请重新扫描并核对。");
    ModManager.Apply(plan);
    return true;
   }));
   string? auditWarning=null;
   try{PatchLifecycle.RecordRetirement(plan.Root,installer.DataRoot,item,plan);}
   catch(Exception e){auditWarning="退役审计记录写入失败："+e.Message;}
   installPreview=null;lastInstallResult=null;ClearSelection();ClearCoop();report=null;states=[];demo=false;
   modInventory=await Task.Run(()=>ModManager.Scan(plan.Root));
   InvalidateLifecycle(item.Patch.Name+" 已安全停用，文件保留在模组停用区。请重新扫描补丁健康状态。"+
    (auditWarning==null?"":" "+auditWarning));
  }
  catch(Exception ex){InvalidateLifecycle("停用未完成，请重新扫描并核对当前文件："+ex.Message);}
  finally{busy=false;transactionWriting=false;ShowLifecycle();}
 }
}
