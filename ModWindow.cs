using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SpirePatch;
public partial class MainWindow
{
 ModInventory? modInventory;
 ModTogglePlan? modTogglePreview;
 string modSection="普通 MOD",modQuery="",modFilter="全部",modStatus="读取模组清单，管理本地模组的启用和停用。";
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
  var root=V(Header("LOCAL MODS","模组管理","普通 MOD 与独立修复补丁分别查看；任何启用、停用操作都先预览。"));Space(root,18);
  if(selectedInstallRoot==null){
   root.Children.Add(Card(V(T("先定位你的游戏目录",20,ink,true),H(Btn("自动定位目录",LocateGame,"Primary"),Btn("手动选择目录",ChooseFolder,"Ghost")))));
   SetPage("模组管理",root,restore:ShowMods);return;
  }
  var refresh=Btn("刷新模组",RefreshMods,"Primary");refresh.IsEnabled=!busy;
  root.Children.Add(H(refresh,Btn("打开模组目录",()=>{
   string path=Path.Combine(selectedInstallRoot,"mods");
   if(Directory.Exists(path))OpenUrl(path);else toast.Text="此目录尚未创建 mods 文件夹。";
  },"Ghost")));
  Space(root,10);root.Children.Add(T(modStatus,12,muted));Space(root,16);

  var mods=modInventory?.Mods??[];
  int fixCount=mods.Count(ModPresentation.IsIndependentPatch);
  int normalCount=mods.Count-fixCount;
  var groups=new WrapPanel{Margin=new(0,0,0,12)};
  foreach(var (name,count) in new[]{("普通 MOD",normalCount),("修复补丁",fixCount)}){
   string chosen=name;
   var tab=Btn($"{name}  ({count})",()=>{modSection=chosen;ShowMods();},modSection==name?"Primary":"Ghost");
   tab.Margin=new(0,0,9,0);groups.Children.Add(tab);
  }
  root.Children.Add(groups);
  root.Children.Add(T(modSection=="修复补丁"
    ?"仅将已收录的独立补丁 ID 与同名清单归入本页。分类不代表文件已通过校验；原 MOD 内置修复仍归在普通 MOD 中。"
    :"这里显示普通 MOD 和含有内置修复的原 MOD；独立修复补丁在右侧标签页单独管理。",12,muted));
  Space(root,12);
  if(modSection=="修复补丁"){
   root.Children.Add(H(Btn("整理独立补丁",PreparePatchOrganization,"Ghost"),
    Btn("重新检查健康状态",RefreshLifecycleFromMods,"Ghost")));
   Space(root,12);
  }
  if(modInventory?.Issues.Count>0){
   root.Children.Add(Card(V(T("模组清单存在问题，部分操作可能被阻止",15,gold,true),
    T(string.Join("\n",modInventory.Issues.Take(5)),12,muted))));
   Space(root,14);
  }
  var search=new TextBox{Text=modQuery,ToolTip="搜索名称、ID 或作者",Margin=new(0,0,0,10)};
  search.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,"搜索模组");
  root.Children.Add(search);
  var statusTabs=new WrapPanel{Margin=new(0,0,0,10)};
  foreach(var label in new[]{"全部","启用","停用"}){
   string chosen=label;
   var button=Btn(label,()=>{modFilter=chosen;ShowMods();},modFilter==label?"Primary":"Ghost");
   button.Margin=new(0,0,7,0);statusTabs.Children.Add(button);
  }
  root.Children.Add(statusTabs);
  var list=V();
  void Render(){
   list.Children.Clear();
   var shown=mods.Where(m=>ModPresentation.IsIndependentPatch(m)==(modSection=="修复补丁"))
    .Where(m=>(modFilter=="全部"||(modFilter=="启用")==m.Enabled)&&
      (m.Name+" "+m.Id+" "+m.Author).Contains(modQuery,StringComparison.OrdinalIgnoreCase))
    .OrderBy(m=>m.Name,StringComparer.OrdinalIgnoreCase).ToArray();
   foreach(var mod in shown){
    var row=new Grid();
    row.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
    row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
    row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
    var title=V(T(mod.Name,15,ink,true),T(mod.Id+" · "+mod.Version+" · "+mod.Author,11,muted));
    if(ModPresentation.IsIndependentPatch(mod))
    {
     Space(title,4);
     title.Children.Add(Badge("健康："+LifecycleTag(mod.Id),muted));
    }
    row.Children.Add(title);
    var state=Badge(mod.Enabled?"已启用":"已停用",mod.Enabled?green:muted);
    state.Margin=new(10,0,12,0);state.VerticalAlignment=VerticalAlignment.Center;
    Grid.SetColumn(state,1);row.Children.Add(state);
    string? blocked=ModManager.BlockReason(selectedInstallRoot,mod,modInventory!);
    var menu=new ContextMenu();
    var toggle=new MenuItem{Header=mod.Enabled?"预览停用":"预览启用",IsEnabled=!busy&&blocked==null,
     ToolTip=blocked};
    toggle.Click+=(_,_)=>PrepareModToggle(mod);menu.Items.Add(toggle);
    var open=new MenuItem{Header="打开文件夹"};
    open.Click+=(_,_)=>OpenUrl(mod.Folder);menu.Items.Add(open);
    var more=Btn("更多",()=>menu.IsOpen=true,"Ghost");
    more.ToolTip="启停及文件夹操作";
    more.ContextMenu=menu;menu.PlacementTarget=more;
    var actions=H(Btn("详情",()=>ShowModDetail(mod.Folder),"Ghost"),more);
    actions.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(actions,2);row.Children.Add(actions);
    var body=V(row);
    if(blocked!=null){
     Space(body,3);body.Children.Add(T("启停受限："+(blocked.Length>105?blocked[..105]+"…":blocked),11,muted));
    }
    list.Children.Add(Card(body,"#FFFFFF",12));Space(list,6);
   }
   if(shown.Length==0&&!busy)
    list.Children.Add(Card(V(T("当前没有匹配的"+(modSection=="修复补丁"?"独立修复补丁":"普通 MOD"),15,ink,true),
      T(modSection=="修复补丁"?"仅管理器已收录的独立补丁会列在这里；可以先刷新列表。":"可以清除搜索或切换启用状态筛选。",12,muted)),padding:16));
  }
  search.TextChanged+=(_,_)=>{
   modQuery=search.Text;Render();
   string q=modQuery,f=modFilter,g=modSection;
   RememberPage("模组管理",()=>{modQuery=q;modFilter=f;modSection=g;ShowMods();});
  };
  root.Children.Add(list);Render();
  string savedQuery=modQuery,savedFilter=modFilter,savedSection=modSection;
  SetPage("模组管理",root,restore:()=>{
   modQuery=savedQuery;modFilter=savedFilter;modSection=savedSection;ShowMods();
  });
  toast.Text="分类只影响列表展示；所有启停操作仍需预览、依赖校验和文件核对。";
 }
 void ShowModDetail(string folder)
 {
  var mod=modInventory?.Mods.SingleOrDefault(m=>m.Folder==folder);if(mod==null){ShowMods();return;}
  var root=V(Header("MOD DETAILS",mod.Name,mod.Id));Space(root,18);root.Children.Add(T(mod.Description.Length>0?mod.Description:"作者未填写简介。",14,muted));Space(root,18);
  root.Children.Add(T("版本："+mod.Version+"\n作者："+mod.Author+"\n状态："+(mod.Enabled?"启用":"停用")+"\n目录："+mod.Folder,13,muted));Space(root,18);
  root.Children.Add(T("声明的前置模组",17,ink,true));Space(root,8);root.Children.Add(T(mod.Dependencies.Length==0?"清单未声明前置；这不代表一定没有其他依赖。":string.Join("\n",mod.Dependencies.Select(d=>d.Id+(d.Minimum.Length>0?" ≥ "+d.Minimum:"")+(d.Maximum.Length>0?" ≤ "+d.Maximum:""))),13,muted));Space(root,20);
  if(ModPresentation.IsIndependentPatch(mod))
  {
   root.Children.Add(H(Badge("健康："+LifecycleTag(mod.Id),muted),
    Btn("查看依据",()=>ShowPatchEvidence(mod.Id),"Ghost")));
   Space(root,18);
  }
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
