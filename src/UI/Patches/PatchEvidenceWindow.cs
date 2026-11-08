using System.Windows;
using System.Windows.Controls;

namespace SpirePatch;

public partial class MainWindow
{
 void ShowPatchEvidence(string componentId)
 {
  if(selectedInstallRoot==null){ShowMods();return;}
  var mod=modInventory?.Mods.FirstOrDefault(x=>x.Id.Equals(componentId,StringComparison.OrdinalIgnoreCase));
  if(mod==null||!ModPresentation.IsIndependentPatch(mod)){ShowMods();return;}
  var root=V(Header("PATCH EVIDENCE","查看依据",mod.Name+" · "+mod.Id));Space(root,16);
  root.Children.Add(H(Btn("返回模组列表",ShowMods,"Ghost"),
   Btn("重新检查健康状态",RefreshLifecycleFromMods,"Primary")));Space(root,16);
  root.Children.Add(T("安装状态："+(mod.Enabled?"已启用":"已停用")+
   " · 健康状态："+LifecycleTag(componentId),14,muted,true));Space(root,12);
  if(!HasValidLifecycleReport||lifecycleReport is not { } report)
  {
   root.Children.Add(Card(V(T(lifecycleStale?"旧扫描结果已失效":"尚未进行健康检查",17,ink,true),
    T("当前没有可用的判断依据。点击上方重新检查，读取本地文件指纹及已审核的上游记录。",13,muted))));
   SetPage("模组管理",root,"patch-evidence:"+componentId,()=>ShowPatchEvidence(componentId));return;
  }
  var item=report.Patches.FirstOrDefault(x=>x.Patch.Id.Equals(componentId,StringComparison.OrdinalIgnoreCase));
  if(item==null)
  {
   root.Children.Add(Card(V(T("当前补丁没有匹配的健康记录。",15,ink,true),
    T("这不意味着补丁安全或不安全；请核对补丁库覆盖范围。",12,muted))));
   SetPage("模组管理",root,"patch-evidence:"+componentId,()=>ShowPatchEvidence(componentId));return;
  }
  root.Children.Add(Card(V(T("检查结论："+LifecycleLabel(item.State),18,ink,true),
   T(item.Message,13,muted),
   T("文件状态："+item.InstallState+" · 检查时间："+report.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),12,muted))));
  Space(root,14);
  var related=item.RelatedMods.Length==0?"无明确关联记录":string.Join("、",item.RelatedMods);
  var pending=report.History.PendingReview.Where(x=>item.RelatedMods.Contains(x.ModId,StringComparer.OrdinalIgnoreCase)).ToArray();
  var mods=V(T("关联 MOD 与更新核对",16,ink,true),T("关联 MOD："+related,12,muted));Space(mods,7);
  if(pending.Length==0)mods.Children.Add(T("暂无未完成的更新核对事项；不代表没有其他未收录的兼容性问题。",12,muted));
  foreach(var change in pending)
  {
   var changed=V(T(change.ModId+"："+change.OldVersion+" → "+change.NewVersion+
    $" · 变化文件 {change.ChangedFiles} 个",12,gold),
    T("这条更新仍待人工核对。重新扫描不会自动把它标记为已解决。",11,muted));
   Space(changed,5);
   changed.Children.Add(Btn("标记该 MOD 已核对",()=>AcknowledgeHealthMod(change.ModId,componentId),"Ghost"));
   mods.Children.Add(changed);Space(mods,8);
  }
  root.Children.Add(Card(mods));Space(root,14);
  var evidence=V(T("上游修复依据",16,ink,true),T(report.EvidenceStatus,12,muted));Space(evidence,9);
  if(item.Evidence is not { } proof)
   evidence.Children.Add(T("没有匹配到可核验的上游 DLL 精确指纹。缺乏证据不能证明旧补丁已不需要。",13,muted));
  else
  {
   evidence.Children.Add(T("审核："+proof.ReviewStatus+" · 结论："+proof.Decision+
    " · 对应版本："+proof.Version,12,muted));Space(evidence,6);
   evidence.Children.Add(T(proof.Note,13,ink));Space(evidence,6);
   evidence.Children.Add(T("匹配文件："+proof.TargetModId+"/"+proof.RelativePath,12,muted));
   evidence.Children.Add(T("SHA-256："+proof.Sha256,11,muted));Space(evidence,8);
   evidence.Children.Add(Btn("复制完整 SHA-256",()=>{
    try{Clipboard.SetText(proof.Sha256);toast.Text="已复制完整 SHA-256。";}
    catch(Exception ex){toast.Text="复制失败："+ex.Message;}
   },"Ghost"));
   foreach(var url in proof.Sources)evidence.Children.Add(Btn("打开证据来源",()=>OpenUrl(url),"Ghost"));
  }
  root.Children.Add(Card(evidence));Space(root,14);
  var decision=V(T("下一步建议",16,ink,true),
   T(item.CanRetire?"已有经过审核且与当前文件匹配的退役依据；仍需预览并再次验证才能安全停用。":
    item.BlockReason??"暂不建议仅凭此页面停用旧补丁。可以继续使用或手动核对 MOD 作者的更新说明。",13,muted));
  if(item.CanRetire)
  {
   Space(decision,8);
   var retire=Btn("预览安全停用",()=>PrepareRetirement(item),"Primary");
   retire.IsEnabled=!busy;decision.Children.Add(retire);
  }
  root.Children.Add(Card(decision));
  root.Children.Add(T("提示：这是最近一次扫描的快照，不会实时证明游戏已修复。外部修改游戏文件后请重新扫描。",12,muted));
  SetPage("模组管理",root,"patch-evidence:"+componentId,()=>ShowPatchEvidence(componentId));
 }
 async void AcknowledgeHealthMod(string modId,string componentId)
 {
  if(busy||selectedInstallRoot==null)return;
  if(MessageBox.Show(this,"确认你已人工核对 "+modId+" 的更新？\n此操作仅清除待核对提醒，不修改 MOD、不证明 Bug 已修复，也不会授权补丁退役。",
   "标记更新已核对",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
  string root=selectedInstallRoot;busy=true;
  try
  {
   await Task.Run(()=>ModHistory.AcknowledgeReview(root,installer.DataRoot,modId));
   InvalidateLifecycle("已记录人工核对，请重新扫描以刷新判断。");
  }
  catch(Exception ex){lifecycleStatus="未能标记为已核对："+ex.Message;}
  finally{busy=false;ShowPatchEvidence(componentId);toast.Text=lifecycleStatus;}
 }
}
