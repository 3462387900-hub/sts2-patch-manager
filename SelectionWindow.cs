using System.Windows;
using System.Windows.Controls;

namespace SpirePatch;
public partial class MainWindow
{
 bool selectionMode;
 PatchSelection? activeSelection;
 ScanReport? selectionReport;
 readonly HashSet<string> requestedPatches=new(StringComparer.OrdinalIgnoreCase);
 string selectionStatus="";
 string? preferredSelection;
 void SelectPatchForInstallation(string id){if(!demo&&report!=null&&selectedInstallRoot!=report.Directory){UseGameRoot(report.Directory);ClearSelection();}preferredSelection=id;ChoosePatchSelection();}
 void ClearSelection(){selectionMode=false;activeSelection=null;selectionReport=null;requestedPatches.Clear();preferredSelection=null;}
 async void ChoosePatchSelection()
 {
  if(busy||onlineBusy)return;
  selectedInstallRoot??=!demo?report?.Directory:null;
  if(selectedInstallRoot==null){ChooseInstallRoot();if(selectedInstallRoot==null)return;}
  bool preserve=selectionMode&&selectionReport?.Directory==selectedInstallRoot;var previous=requestedPatches.ToArray();
  busy=true;activeSelection=null;selectionReport=null;installPreview=null;lastInstallResult=null;installStatus="正在按已审核的补丁版本扫描，可用项随后显示…";ShowInstaller();
  try{
   selectionReport=await Task.Run(()=>Catalog.Scan(selectedInstallRoot));selectionMode=true;requestedPatches.Clear();
   if(preferredSelection!=null){requestedPatches.Add(preferredSelection);preferredSelection=null;}
   else if(preserve){foreach(var id in previous)if(selectionReport.Patches.Any(p=>p.Patch.Id==id&&p.Status is "待修复" or "需联动" or "已安装"))requestedPatches.Add(id);}
   else foreach(var p in selectionReport.Patches.Where(p=>p.Status is "待修复" or "需联动"))requestedPatches.Add(p.Patch.Id);
   ResolveSelection();
  }catch(Exception e){selectionStatus="扫描未完成："+e.Message;}
  finally{busy=false;if(selectionReport!=null&&selectionMode)ShowPatchSelection();else{installStatus=selectionStatus;ShowInstaller();}}
 }
 void ResolveSelection()
 {
  activeSelection=null;installPreview=null;lastInstallResult=null;
  if(requestedPatches.Count==0){selectionStatus="尚未选择补丁。已安装的项目也可以勾选，用于补齐说明文件。";return;}
  try{activeSelection=SelectionPlanner.Resolve(selectedInstallRoot!,requestedPatches,selectionReport!);selectionStatus=$"手动选择 {activeSelection.Requested.Length} 项 · 自动关联 {activeSelection.Automatic.Length} 项";}
  catch(Exception e){selectionStatus="暂时不能生成方案："+e.Message;}
 }
 void ShowPatchSelection()
 {
  if(selectionReport==null){ChoosePatchSelection();return;}
  var root=V(Header("CHOOSE PATCHES","选择本次要处理的补丁","已匹配的文件会跳过。必要的关联修复会自动加入，并在确认前一起展示。"));Space(root,18);
  root.Children.Add(T(selectedInstallRoot??"",12,muted));Space(root,10);
  root.Children.Add(T("当前可选择本版已审核整合包中的 11 项修复。下载仍使用完整 ZIP，写入只限所选项目；回滚按最近一次安装批次执行。",12,muted));Space(root,16);
  var actions=H(Btn("选择推荐项",()=>{requestedPatches.Clear();foreach(var p in selectionReport.Patches.Where(p=>p.Status is "待修复" or "需联动"))requestedPatches.Add(p.Patch.Id);ResolveSelection();ShowPatchSelection();},"Ghost"),Btn("清空选择",()=>{requestedPatches.Clear();ResolveSelection();ShowPatchSelection();},"Ghost"),Btn("重新扫描",ChoosePatchSelection,"Ghost"));root.Children.Add(actions);Space(root,12);
  root.Children.Add(T(selectionStatus,14,activeSelection==null?muted:gold));Space(root,12);
  var inspect=Btn("检查所选补丁方案",()=>BeginPreparation(false),"Primary");inspect.IsEnabled=activeSelection!=null&&!busy;root.Children.Add(inspect);Space(root,18);
  foreach(var state in selectionReport.Patches.OrderByDescending(p=>activeSelection?.Included.Contains(p.Patch.Id)==true)){
   string id=state.Patch.Id;bool automatic=activeSelection?.Automatic.Contains(id)==true;bool allowed=state.Status is "待修复" or "需联动" or "已安装";
   var checkbox=new CheckBox{Content=state.Patch.Name,Foreground=ink,FontSize=14,IsChecked=requestedPatches.Contains(id)||automatic,IsEnabled=allowed&&!automatic,Margin=new(0,0,0,6)};
   checkbox.Checked+=(_,_)=>{requestedPatches.Add(id);ResolveSelection();ShowPatchSelection();};checkbox.Unchecked+=(_,_)=>{requestedPatches.Remove(id);ResolveSelection();ShowPatchSelection();};
   string caption=automatic?"自动关联 · 取消依赖它的选择后会自动移除":state.Status=="已安装"?"功能文件已匹配 · 可选择补齐说明":state.Status;
   root.Children.Add(Card(V(checkbox,T(caption,12,automatic?gold:muted),T(state.Reason,12,muted)),"#FFFFFF",14));Space(root,8);
  }
  SetPage("安装与回滚",root,"selection",ShowPatchSelection);toast.Text="选择与检查不会修改游戏文件；下一步会列出实际差异，确认后才写入。";
 }
}
