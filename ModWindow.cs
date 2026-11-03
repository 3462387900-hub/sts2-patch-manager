using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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
 double modListOffset;
 static ScrollViewer? ListScroll(DependencyObject element)
 {
  if(element is ScrollViewer viewer)return viewer;
  for(int i=0;i<VisualTreeHelper.GetChildrenCount(element);i++)
  {
   var found=ListScroll(VisualTreeHelper.GetChild(element,i));
   if(found!=null)return found;
  }
  return null;
 }
 void CaptureModListOffset()
 {
  if(activeModList!=null&&ListScroll(activeModList) is { } viewer)
   modListOffset=viewer.VerticalOffset;
 }
 void ShowMods()
 {
  var root=new Grid();
  root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
  root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
  root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
  var header=V(Header("LOCAL MODS","模组管理","普通 MOD 和独立修复补丁分开管理；启停操作始终需要预览。"));
  root.Children.Add(header);Space(header,13);
  if(selectedInstallRoot==null)
  {
   header.Children.Add(Card(V(T("请先定位游戏目录",17,ink,true),
    H(Btn("自动定位目录",LocateGame,"Primary"),Btn("手动选择目录",ChooseFolder,"Ghost")))));
   SetPage("模组管理",root,restore:ShowMods);return;
  }
  var refresh=Btn("刷新模组",RefreshMods,"Primary");refresh.IsEnabled=!busy;
  header.Children.Add(H(refresh,Btn("打开模组目录",()=>{
   string directory=Path.Combine(selectedInstallRoot,"mods");
   if(Directory.Exists(directory))OpenUrl(directory);else toast.Text="此目录尚未创建 mods 文件夹。";
  },"Ghost")));
  Space(header,6);
  header.Children.Add(T(modStatus,12,muted));Space(header,11);
  if(modInventoryDirty)
  {
   header.Children.Add(Card(V(T("检测到 MOD 文件变化 · 正在核对列表",13,gold,true),
    T("补丁健康结论已失效，需要重新扫描。",11,muted)),padding:10));
   Space(header,10);
  }
  var mods=modInventory?.Mods??[];
  int fixCount=mods.Count(ModPresentation.IsIndependentPatch),normalCount=mods.Count-fixCount;
  var groups=new WrapPanel{Margin=new Thickness(0,0,0,9)};
  foreach(var (name,count) in new[]{("普通 MOD",normalCount),("修复补丁",fixCount)})
  {
   string selected=name;
   var tab=Btn($"{name}  ({count})",()=>{
    CaptureModListOffset();modListOffset=0;modSection=selected;ShowMods();
   },modSection==name?"Primary":"Ghost");
   tab.Margin=new Thickness(0,0,9,0);
   groups.Children.Add(tab);
  }
  header.Children.Add(groups);
  header.Children.Add(T(modSection=="修复补丁"
   ?"已收录的独立补丁集中在此；分类不代表通过文件指纹校验。"
   :"普通 MOD（包括原 MOD 内置修复）显示在此。",12,muted));
  Space(header,9);
  if(modSection=="修复补丁")
  {
   header.Children.Add(T(HasValidLifecycleReport
    ?"补丁健康最近检查："+lifecycleReport!.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
    :lifecycleStale?"文件有变化，建议重新检查补丁健康。":"尚未检查补丁健康。",12,muted));
   Space(header,5);
   header.Children.Add(H(Btn("整理独立补丁",PreparePatchOrganization,"Ghost"),
    Btn("重新检查健康状态",RefreshLifecycleFromMods,"Ghost")));
   Space(header,8);
  }
  if(modInventory?.Issues.Count>0)
  {
   header.Children.Add(Card(V(T("部分 MOD 清单需要处理",14,gold,true),
    T(string.Join("\n",modInventory.Issues.Take(3)),12,muted)),padding:12));
   Space(header,8);
  }

  var searchArea=new Grid{Margin=new Thickness(0,0,0,8)};
  var search=new TextBox{Text=modQuery,ToolTip="按 MOD 名称、ID、作者搜索"};
  search.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,"搜索模组");
  searchArea.Children.Add(search);
  var placeholder=T("搜索名称、MOD ID 或作者…",12,muted);
  placeholder.Margin=new Thickness(12,0,0,0);
  placeholder.VerticalAlignment=VerticalAlignment.Center;
  placeholder.IsHitTestVisible=false;
  placeholder.Visibility=string.IsNullOrEmpty(modQuery)?Visibility.Visible:Visibility.Collapsed;
  searchArea.Children.Add(placeholder);
  header.Children.Add(searchArea);
  var filterBar=new DockPanel{LastChildFill=false,Margin=new Thickness(0,0,0,7)};
  var shownCount=T("",12,muted);shownCount.VerticalAlignment=VerticalAlignment.Center;
  DockPanel.SetDock(shownCount,Dock.Right);filterBar.Children.Add(shownCount);
  var filters=new WrapPanel();
  foreach(var name in new[]{"全部","启用","停用"})
  {
   string selected=name;
   var button=Btn(name,()=>{CaptureModListOffset();modListOffset=0;modFilter=selected;ShowMods();},
    modFilter==name?"Primary":"Ghost");
   button.Margin=new Thickness(0,0,7,0);filters.Children.Add(button);
  }
  filterBar.Children.Add(filters);header.Children.Add(filterBar);

  var rows=modInventory==null?[]:ModListView.Rows(selectedInstallRoot,modInventory,LifecycleTag);
  var list=ModListView.Create(row=>{
   CaptureModListOffset();ShowModDetail(row.Mod.Folder);
  },(row,button)=>{
   var menu=new ContextMenu{PlacementTarget=button};
   var toggle=new MenuItem{Header=row.Mod.Enabled?"预览停用":"预览启用",
    IsEnabled=!busy&&!transactionWriting&&!modInventoryDirty&&row.BlockReason==null,
    ToolTip=modInventoryDirty?"MOD 环境有变动，请先刷新。":row.BlockReason};
   toggle.Click+=(_,_)=>{CaptureModListOffset();PrepareModToggle(row.Mod);};
   menu.Items.Add(toggle);
   var open=new MenuItem{Header="打开文件夹"};
   open.Click+=(_,_)=>OpenUrl(row.Mod.Folder);menu.Items.Add(open);
   button.ContextMenu=menu;menu.IsOpen=true;
  });
  activeModList=list;Grid.SetRow(list,1);root.Children.Add(list);
  var empty=T("没有符合条件的 MOD。请修改搜索或筛选条件。",12,muted);
  var footer=V(empty);
  Grid.SetRow(footer,2);root.Children.Add(footer);
  void FilterRows()
  {
   var visible=ModListView.Filter(rows,modSection,modFilter,modQuery);
   list.ItemsSource=visible;
   shownCount.Text=$"显示 {visible.Length} / {(modSection=="修复补丁"?fixCount:normalCount)} 项";
   empty.Visibility=visible.Length==0&&!busy?Visibility.Visible:Visibility.Collapsed;
  }
  search.TextChanged+=(_,_)=>{
   modQuery=search.Text;
   placeholder.Visibility=string.IsNullOrEmpty(modQuery)?Visibility.Visible:Visibility.Collapsed;
   modListOffset=0;
   FilterRows();ListScroll(list)?.ScrollToTop();
   string q=modQuery,f=modFilter,g=modSection;
   RememberPage("模组管理",()=>{modQuery=q;modFilter=f;modSection=g;ShowMods();});
  };
  FilterRows();
  list.Loaded+=(_,_)=>Dispatcher.BeginInvoke(new Action(()=>{
   if(activeModList==list&&ListScroll(list) is { } scroll)scroll.ScrollToVerticalOffset(modListOffset);
  }),System.Windows.Threading.DispatcherPriority.Loaded);
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
