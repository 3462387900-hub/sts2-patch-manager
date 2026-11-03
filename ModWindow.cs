using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SpirePatch;
public partial class MainWindow
{
 ModInventory? modInventory;
 ModTogglePlan? modTogglePreview;
 string modQuery="",modFilter="全部",modStatus="读取模组清单，管理本地模组的启用和停用。";
 async void RefreshMods()
 {
  if(busy)return;if(selectedInstallRoot==null){LocateGame();return;}
  string root=selectedInstallRoot;busy=true;modTogglePreview=null;modStatus="正在读取模组清单…";ShowMods();
  try{modInventory=await Task.Run(()=>ModManager.Scan(root));modStatus=$"共 {modInventory.Mods.Count} 项 · {modInventory.Mods.Count(m=>m.Enabled)} 项启用 · {modInventory.Mods.Count(m=>!m.Enabled)} 项停用";}
  catch(Exception e){modInventory=null;modStatus="无法读取模组："+e.Message;}
  finally{busy=false;ShowMods();}
 }
 void ShowMods()
 {
  var root=V(Header("LOCAL MODS","模组管理","查看当前安装，按需启用或停用；停用的模组会保留，可随时恢复。"));Space(root,18);
  if(selectedInstallRoot==null){root.Children.Add(Card(V(T("先定位你的游戏目录",20,ink,true),H(Btn("自动定位目录",LocateGame,"Primary"),Btn("手动选择目录",ChooseFolder,"Ghost")))));SetPage("模组管理",root,restore:ShowMods);return;}
  var refresh=Btn("刷新模组",RefreshMods,"Primary");refresh.IsEnabled=!busy;
  var toolbar=new Grid();toolbar.ColumnDefinitions.Add(new(){Width=GridLength.Auto});toolbar.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
  toolbar.Children.Add(H(refresh,Btn("打开模组目录",()=>{string path=Path.Combine(selectedInstallRoot,"mods");if(Directory.Exists(path))OpenUrl(path);else toast.Text="此目录尚未创建 mods 文件夹。";},"Ghost"),Btn("整理独立补丁",PreparePatchOrganization,"Ghost"),Btn("补丁健康检查",()=>{ShowLifecycle();RefreshLifecycle();},"Ghost")));
  var count=T(modStatus,12,muted);count.VerticalAlignment=VerticalAlignment.Center;count.HorizontalAlignment=HorizontalAlignment.Right;Grid.SetColumn(count,1);toolbar.Children.Add(count);root.Children.Add(toolbar);Space(root,12);
  root.Children.Add(T("启用表示文件位于 mods 目录，游戏内另有设置时以游戏实际加载为准。启用/停用前请退出游戏。",12,muted));Space(root,12);
  if(modInventory?.Issues.Count>0){root.Children.Add(Card(V(T("有清单问题需要核对，启停操作暂不可用",15,gold,true),T(string.Join("\n",modInventory.Issues.Take(5)),12,muted))));Space(root,16);}
  root.Children.Add(T("搜索名称、ID 或作者",12,muted));Space(root,6);
  var search=new TextBox{Text=modQuery,ToolTip="搜索模组名称、ID 或作者",Margin=new(0,0,0,12)};var tabs=new WrapPanel();
  foreach(var label in new[]{"全部","启用","停用"}){var tab=Btn(label,()=>{modFilter=label;ShowMods();},modFilter==label?"Primary":"Ghost");tab.Margin=new(0,0,8,12);tabs.Children.Add(tab);}
  var list=V();void Render(){list.Children.Clear();foreach(var mod in modInventory?.Mods.Where(m=>(modFilter=="全部"||(modFilter=="启用")==m.Enabled)&&(m.Name+" "+m.Id+" "+m.Author).Contains(modQuery,StringComparison.OrdinalIgnoreCase))??[]){
   var item=V();var row=new Grid();row.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});row.Children.Add(V(T(mod.Name,15,ink,true),T(mod.Id+" · "+mod.Version+" · "+(mod.Enabled?"启用":"停用"),12,mod.Enabled?green:muted)));
   string? blocked=ModManager.BlockReason(selectedInstallRoot,mod,modInventory!);var toggle=Btn(mod.Enabled?"停用":"启用",()=>PrepareModToggle(mod),"Ghost");toggle.IsEnabled=!busy&&blocked==null;toggle.ToolTip=blocked;
   var actions=H(Btn("详情",()=>ShowModDetail(mod.Folder),"Ghost"),toggle,Btn("打开文件夹",()=>OpenUrl(mod.Folder),"Ghost"));actions.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(actions,1);row.Children.Add(actions);item.Children.Add(row);
   if(blocked!=null){Space(item,5);item.Children.Add(T(blocked.Length>110?blocked[..110]+"…":blocked,11,muted));}list.Children.Add(Card(item,"#FFFFFF",14));Space(list,8);
  }if(list.Children.Count==0&&!busy)list.Children.Add(T("没有匹配的模组。",14,muted));}
  search.TextChanged+=(_,_)=>{modQuery=search.Text;Render();string q=modQuery,f=modFilter;RememberPage("模组管理",()=>{modQuery=q;modFilter=f;ShowMods();});};root.Children.Add(search);root.Children.Add(tabs);root.Children.Add(list);Render();
  string savedQuery=modQuery,savedFilter=modFilter;SetPage("模组管理",root,restore:()=>{modQuery=savedQuery;modFilter=savedFilter;ShowMods();});toast.Text="查看清单不会改动文件；启用或停用会先展示改动，确认后执行。";
 }
 void ShowModDetail(string folder)
 {
  var mod=modInventory?.Mods.SingleOrDefault(m=>m.Folder==folder);if(mod==null){ShowMods();return;}
  var root=V(Header("MOD DETAILS",mod.Name,mod.Id));Space(root,18);root.Children.Add(T(mod.Description.Length>0?mod.Description:"作者未填写简介。",14,muted));Space(root,18);
  root.Children.Add(T("版本："+mod.Version+"\n作者："+mod.Author+"\n状态："+(mod.Enabled?"启用":"停用")+"\n目录："+mod.Folder,13,muted));Space(root,18);
  root.Children.Add(T("声明的前置模组",17,ink,true));Space(root,8);root.Children.Add(T(mod.Dependencies.Length==0?"清单未声明前置；这不代表一定没有其他依赖。":string.Join("\n",mod.Dependencies.Select(d=>d.Id+(d.Minimum.Length>0?" ≥ "+d.Minimum:"")+(d.Maximum.Length>0?" ≤ "+d.Maximum:""))),13,muted));Space(root,20);
  string? blocked=ModManager.BlockReason(selectedInstallRoot!,mod,modInventory!);var toggle=Btn(mod.Enabled?"预览停用":"预览启用",()=>PrepareModToggle(mod),"Primary");toggle.IsEnabled=!busy&&blocked==null;root.Children.Add(H(toggle,Btn("打开文件夹",()=>OpenUrl(mod.Folder),"Ghost")));Space(root,10);if(blocked!=null)root.Children.Add(T(blocked,13,gold));
  SetPage("模组管理",root,"mod:"+folder,()=>ShowModDetail(folder));toast.Text="启用或停用前会重新核对文件及声明的前置模组。";
 }
 async void PrepareModToggle(ManagedMod mod)
 {
  if(busy||selectedInstallRoot==null)return;busy=true;modTogglePreview=null;
  try{modTogglePreview=await Task.Run(()=>ModManager.Preview(selectedInstallRoot,mod.Folder,!mod.Enabled));}
  catch(Exception e){modStatus=e.Message;}
  finally{busy=false;if(modTogglePreview!=null)ShowModConfirmation();else ShowMods();}
 }
 void ShowModConfirmation()
 {
  if(modTogglePreview==null){ShowMods();return;}var plan=modTogglePreview;
  var root=V(Header("REVIEW MOD CHANGE",plan.Enable?"确认启用模组":"确认停用模组",plan.Name));Space(root,22);
  var detail=V(T(plan.Enable?"将模组恢复到原来的 mods 位置。":"将整个模组目录移出 mods，放入游戏目录中的停用保留区。",17,ink,true));Space(detail,14);
  detail.Children.Add(T($"涉及 {plan.FileCount} 个文件，文件内容保持不变。\n当前目录：{plan.Folder}",13,muted));Space(detail,14);
  detail.Children.Add(T(plan.Enable?"原位置被其他文件占用或停用内容被修改时，会停止恢复。":"此操作保留完整模组文件和原位置记录；重新启用时可以恢复。保存的游戏进度不会被修改，进入存档仍需保留该存档所需模组。",13,muted));Space(detail,20);
  detail.Children.Add(H(Btn(plan.Enable?"确认启用":"确认停用",ApplyModToggle,"Primary"),Btn("取消",()=>{modTogglePreview=null;if(backHistory.Count>0)GoBack();else ShowMods();},"Ghost")));root.Children.Add(Card(detail));SetPage("模组管理",root,"mod-confirm",ShowModConfirmation);toast.Text="确认后才会移动模组目录，所有文件会保留。";
 }
 async void ApplyModToggle()
 {
  if(busy||modTogglePreview==null)return;var plan=modTogglePreview;busy=true;transactionWriting=true;modTogglePreview=null;
  try{
   await Task.Run(()=>ModManager.Apply(plan));installPreview=null;lastInstallResult=null;ClearSelection();ClearCoop();report=null;states=[];demo=false;
   modInventory=await Task.Run(()=>ModManager.Scan(plan.Root));modStatus=plan.Name+(plan.Enable?" 已启用。":" 已停用，文件已完整保留。");
  }catch(Exception e){modStatus="操作未完成："+e.Message;}
  finally{InvalidateLifecycle();busy=false;transactionWriting=false;ShowMods();}
 }
}
