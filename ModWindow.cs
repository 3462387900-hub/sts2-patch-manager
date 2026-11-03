using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SpirePatch;
public partial class MainWindow
{
 ModInventory? modInventory;
 ModTogglePlan? modTogglePreview;
 string modSection="普通 MOD",modQuery="",modFilter="全部",modStatus="读取模组清单，管理本地模组的启用和停用。";
 ListBox? activeModList;
 async void RefreshMods()
 {
  if(busy)return;
  if(selectedInstallRoot==null){LocateGame();return;}
  string root=selectedInstallRoot;
  int watchVersion=modChangeVersion;
  busy=true;modTogglePreview=null;modStatus="正在读取模组清单…";
  if(page=="模组管理"&&currentVisit?.Key=="模组管理")ShowMods();
  try
  {
   var inventory=await Task.Run(()=>ModManager.Scan(root));
   if(!root.Equals(selectedInstallRoot,StringComparison.OrdinalIgnoreCase)||watchVersion!=modChangeVersion)
   {
    modInventoryDirty=true;modStatus="扫描期间目录发生变化，正在准备重新刷新。";
    modWatchDebounce?.Stop();modWatchDebounce?.Start();
    return;
   }
   modInventory=inventory;modInventoryDirty=false;
   modStatus=$"共 {inventory.Mods.Count} 项 · {inventory.Mods.Count(m=>m.Enabled)} 项启用 · {inventory.Mods.Count(m=>!m.Enabled)} 项停用";
  }
  catch(Exception ex){modInventoryDirty=true;modStatus="无法读取模组："+ex.Message;}
  finally
  {
   busy=false;
   if(page=="模组管理"&&currentVisit?.Key=="模组管理")ShowMods();
  }
 }
 void ShowMods()
 {
  var root=V(Header("LOCAL MODS","模组管理","普通 MOD 与独立修复补丁分别查看；任何启用、停用操作都先预览。"));Space(root,18);
  if(selectedInstallRoot==null)
  {
   root.Children.Add(Card(V(T("先定位你的游戏目录",20,ink,true),
    H(Btn("自动定位目录",LocateGame,"Primary"),Btn("手动选择目录",ChooseFolder,"Ghost")))));
   SetPage("模组管理",root,restore:ShowMods);return;
  }
  var refresh=Btn("刷新模组",RefreshMods,"Primary");refresh.IsEnabled=!busy;
  root.Children.Add(H(refresh,Btn("打开模组目录",()=>{
   string path=Path.Combine(selectedInstallRoot,"mods");
   if(Directory.Exists(path))OpenUrl(path);else toast.Text="此目录尚未创建 mods 文件夹。";
  },"Ghost")));
  Space(root,10);root.Children.Add(T(modStatus,12,muted));Space(root,12);
  if(modInventoryDirty)
  {
   root.Children.Add(Card(V(T("检测到 MOD 文件变化",15,gold,true),
    T("自动刷新只更新模组列表；旧补丁健康结果已经失效，请在修复补丁页重新检查。",12,muted)),padding:14));
   Space(root,10);
  }
  var mods=modInventory?.Mods??[];
  int fixCount=mods.Count(ModPresentation.IsIndependentPatch),normalCount=mods.Count-fixCount;
  var groups=new WrapPanel{Margin=new(0,0,0,12)};
  foreach(var (name,count) in new[]{("普通 MOD",normalCount),("修复补丁",fixCount)})
  {
   string selected=name;
   var tab=Btn($"{name}  ({count})",()=>{modSection=selected;ShowMods();},
    modSection==name?"Primary":"Ghost");
   tab.Margin=new(0,0,9,0);groups.Children.Add(tab);
  }
  root.Children.Add(groups);
  root.Children.Add(T(modSection=="修复补丁"
   ?"仅将已收录的独立补丁 ID 与同名清单归入本页。分类不代表文件已通过校验；原 MOD 内置修复仍归在普通 MOD 中。"
   :"这里显示普通 MOD 和含有内置修复的原 MOD；独立修复补丁在右侧标签页单独管理。",12,muted));
  Space(root,12);
  if(modSection=="修复补丁")
  {
   root.Children.Add(T(HasValidLifecycleReport
    ?"上次健康检查："+lifecycleReport!.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")+" · 这是检查时的快照"
    :lifecycleStale?"环境已变化，健康状态需要重新检查。":"尚无健康检查结果。",12,muted));
   Space(root,8);
   root.Children.Add(H(Btn("整理独立补丁",PreparePatchOrganization,"Ghost"),
    Btn("重新检查健康状态",RefreshLifecycleFromMods,"Ghost")));Space(root,12);
  }
  if(modInventory?.Issues.Count>0)
  {
   root.Children.Add(Card(V(T("模组清单存在问题，部分操作可能被阻止",15,gold,true),
    T(string.Join("\n",modInventory.Issues.Take(5)),12,muted))));
   Space(root,14);
  }
  var search=new TextBox{Text=modQuery,ToolTip="搜索名称、ID 或作者",Margin=new(0,0,0,10)};
  search.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,"搜索模组");
  root.Children.Add(search);
  var filters=new WrapPanel{Margin=new(0,0,0,10)};
  foreach(var name in new[]{"全部","启用","停用"})
  {
   string selected=name;
   var button=Btn(name,()=>{modFilter=selected;ShowMods();},
    modFilter==name?"Primary":"Ghost");
   button.Margin=new(0,0,7,0);filters.Children.Add(button);
  }
  root.Children.Add(filters);
  var rows=modInventory==null?[]:ModListView.Rows(selectedInstallRoot,modInventory,LifecycleTag);
  var list=ModListView.Create(row=>ShowModDetail(row.Mod.Folder),(row,button)=>{
   var menu=new ContextMenu{PlacementTarget=button};
   var toggle=new MenuItem{Header=row.Mod.Enabled?"预览停用":"预览启用",
    IsEnabled=!busy&&!transactionWriting&&!modInventoryDirty&&row.BlockReason==null,
    ToolTip=modInventoryDirty?"MOD 环境有变动，请先刷新。":row.BlockReason};
   toggle.Click+=(_,_)=>PrepareModToggle(row.Mod);menu.Items.Add(toggle);
   var open=new MenuItem{Header="打开文件夹"};
   open.Click+=(_,_)=>OpenUrl(row.Mod.Folder);menu.Items.Add(open);
   button.ContextMenu=menu;menu.IsOpen=true;
  });
  activeModList=list;
  var hint=T("当前分类或搜索条件下没有符合的模组。",13,muted);
  void FilterRows()
  {
   var visible=ModListView.Filter(rows,modSection,modFilter,modQuery);
   list.ItemsSource=visible;
   hint.Visibility=visible.Length==0&&!busy?Visibility.Visible:Visibility.Collapsed;
  }
  search.TextChanged+=(_,_)=>{
   modQuery=search.Text;FilterRows();
   string q=modQuery,f=modFilter,g=modSection;
   RememberPage("模组管理",()=>{modQuery=q;modFilter=f;modSection=g;ShowMods();});
  };
  root.Children.Add(list);root.Children.Add(hint);FilterRows();
  Space(root,8);
  root.Children.Add(T("列表按需加载可见行；滚动模组请使用列表右侧滚动条。",11,muted));
  string savedQuery=modQuery,savedFilter=modFilter,savedSection=modSection;
  SetPage("模组管理",root,restore:()=>{
   modQuery=savedQuery;modFilter=savedFilter;modSection=savedSection;ShowMods();
  });
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
