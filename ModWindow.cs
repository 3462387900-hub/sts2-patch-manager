using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SpirePatch;
public partial class MainWindow
{
 ModInventory? modInventory;
 string? orphanPatchNotice;
 ModTogglePlan? modTogglePreview;
 string modSection="普通 MOD",modQuery="",modFilter="全部",modStatus="读取模组清单，管理本地模组的启用和停用。";
 ListBox? activeModList;
 GitHubModUpdates? githubModUpdates;
 GitHubModDiscovery? githubDiscovery;
 GitHubModDiscovery Discovery()=>githubDiscovery??=new GitHubModDiscovery();
 string? githubRoot;
 GitHubModUpdates GitHubUpdates()
 {
  if(selectedInstallRoot==null)throw new InvalidOperationException("请先选择游戏目录。");
  if(githubModUpdates==null||!string.Equals(githubRoot,selectedInstallRoot,StringComparison.OrdinalIgnoreCase))
  {
   githubModUpdates?.Dispose();githubModUpdates=new GitHubModUpdates(installer.DataRoot,selectedInstallRoot);
   githubRoot=selectedInstallRoot;
  }
  return githubModUpdates;
 }
 async void DiscoverGitHubMods()
 {
  if(busy||modInventory==null||selectedInstallRoot==null)return;
  busy=true;
  int found=0,verified=0,examined=0;
  try
  {
   var mods=modInventory.Mods.Where(m=>!ModPresentation.IsIndependentPatch(m)&&GitHubUpdates().Repository(m.Id)==null)
    .GroupBy(m=>m.Id,StringComparer.OrdinalIgnoreCase).Select(g=>g.First())
    .Where(m=>Discovery().Cached(m)==null).Take(8).ToArray();
   foreach(var mod in mods)
   {
    toast.Text="正在查找 GitHub 来源："+mod.Name+"（"+(examined+1)+"/"+mods.Length+"）";
    var result=await Discovery().DiscoverAsync(mod);
    if(result.Candidates.Length>0)found++;
    if(result.Candidates.Any(c=>c.Verified))verified++;
    examined++;
    if(result.Retryable)
    {
     modStatus=$"已检查 {examined} 个 MOD，GitHub 搜索暂不可用："+result.Note+
      "。已查到的候选仍可在详情确认；稍后可继续批量查找。";
     return;
    }
   }
   modStatus=mods.Length==0
    ?"尚无待查找的普通 MOD；已关联的来源或近期检查结果均予以保留。"
    :$"查找完成：检查 {examined} 个 MOD，找到 {found} 个候选，其中 {verified} 个有明确来源证据。"+
      "进入 MOD 详情核对并确认；每次最多查找 8 个，重复点击可继续下一批。";
  }
  catch(Exception e){modStatus=$"查找中断，已检查 {examined} 个："+e.Message;}
  finally{busy=false;ShowMods();}
 }
 async void DiscoverGitHubMod(ManagedMod mod)
 {
  if(busy)return;
  busy=true;
  try
  {
   var result=await Discovery().DiscoverAsync(mod,true);
   if(result.Candidates.Length==0)toast.Text=mod.Name+"："+result.Note;
   else if(result.Retryable)toast.Text=mod.Name+"："+result.Note+"；已有候选可在详情查看。";
  }
  catch(Exception e){toast.Text="查找失败："+e.Message;}
  finally{busy=false;ShowModDetail(mod.Folder);}
 }
 async void CheckGitHubMods()
 {
  if(busy||modInventory==null||selectedInstallRoot==null)return;
  busy=true;
  try
  {
   var updates=GitHubUpdates();
   var count=await updates.CheckAllAsync(modInventory.Mods);
   modStatus=$"已检查 {count} 个绑定 GitHub 的普通 MOD；检测结果仅供参考，不会修改游戏文件。";
  }
  catch(Exception e){modStatus="GitHub 检查未完成："+e.Message;}
  finally{busy=false;ShowMods();}
 }
 void BindGitHubMod(ManagedMod mod)
 {
  var updates=GitHubUpdates();
  var dialog=new Window{Title="关联 GitHub 仓库 · "+mod.Name,Owner=this,Width=550,Height=225,
   WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize};
  var body=new StackPanel{Margin=new Thickness(18)};
  body.Children.Add(T("输入公开 GitHub 仓库地址（检测正式 Release，并支持校验后安装）：",13,muted));
  var input=new TextBox{Text=updates.Repository(mod.Id)??"",Margin=new Thickness(0,12,0,12)};
  body.Children.Add(input);
  var hint=T("示例：https://github.com/作者/仓库。绑定按 MOD ID 保存，不改变模组文件。",11,muted);
  body.Children.Add(hint);
  var save=Btn("保存关联",()=>{try{updates.Bind(mod.Id,input.Text);dialog.DialogResult=true;}catch(Exception e){hint.Text=e.Message;}},"Primary");
  var remove=Btn("解除关联",()=>{try{updates.Unbind(mod.Id);dialog.DialogResult=true;}catch(Exception e){hint.Text=e.Message;}},"Ghost");
  body.Children.Add(H(save,remove,Btn("取消",()=>dialog.DialogResult=false,"Ghost")));
  dialog.Content=body;input.Focus();
  if(dialog.ShowDialog()==true)ShowModDetail(mod.Folder);
 }
 async void CheckGitHubMod(ManagedMod mod)
 {
  if(busy)return;
  busy=true;
  try
  {
   var result=await GitHubUpdates().CheckAsync(mod,true);
   toast.Text=mod.Name+" · "+result.Status+" · "+result.Message;
  }
  catch(Exception e){toast.Text="检查失败："+e.Message;}
  finally{busy=false;ShowModDetail(mod.Folder);}
 }


 async void UpdateGitHubMod(ManagedMod mod)
 {
  if(busy||transactionWriting||modInventoryDirty||selectedInstallRoot==null)return;
  string gameRoot=selectedInstallRoot;
  string? repo=GitHubUpdates().Repository(mod.Id);
  if(repo==null){toast.Text="请先关联 GitHub 来源。";return;}
  busy=true;
  bool changed=false;
  try
  {
   using var installerSource=new GitHubModInstaller();
   toast.Text="正在读取 GitHub 正式版附件与 SHA-256 校验信息…";
   var packages=await installerSource.PackagesAsync(mod,repo);
   if(packages.Length==0)
   {
    toast.Text="这个 Release 没有提供可安全验证的单 MOD ZIP 安装包，请从作者发布页手动更新。";
    return;
   }
   var dialog=new Window{Title="选择 GitHub MOD 安装包 · "+mod.Name,Owner=this,
    Width=660,Height=340,MinWidth=560,WindowStartupLocation=WindowStartupLocation.CenterOwner};
   var body=new StackPanel{Margin=new Thickness(18)};
   body.Children.Add(T("本地 "+mod.Version+" → GitHub "+packages[0].Tag+" · "+repo,14,ink,true));
   Space(body,10);
   body.Children.Add(T("请选择适合当前游戏版本的安装包。兼容版和标准版不能自动混用；不确定时请查看作者发布说明。",12,muted));
   Space(body,10);
   var picker=new ComboBox{ItemsSource=packages,SelectedIndex=packages.Length==1?0:-1,
    MinHeight=32,MaxDropDownHeight=230};
   body.Children.Add(picker);
   Space(body,9);
   body.Children.Add(T("仅支持带 GitHub SHA-256 摘要的 ZIP。下载后检查 MOD ID、版本及文件结构；旧目录完整备份，操作前需要关闭游戏。",11,muted));
   Space(body,12);
   var accept=Btn("确认选择",()=>{
    if(picker.SelectedItem is not GitHubModPackage){toast.Text="请先明确选择一个安装包。";return;}
    dialog.DialogResult=true;
   },"Primary");
   body.Children.Add(H(accept,Btn("取消",()=>dialog.DialogResult=false,"Ghost"),
    Btn("查看作者发布页",()=>OpenUrl(packages[0].ReleaseUrl),"Ghost")));
   dialog.Content=body;
   if(dialog.ShowDialog()!=true||picker.SelectedItem is not GitHubModPackage selected)return;
   if(MessageBox.Show(this,
    "将从 GitHub 下载："+selected.FileName+"\n"+
    "更新对象："+mod.Name+" ("+mod.Id+")\n"+
    "安装前验证 SHA-256、清单 ID 和版本，旧 MOD 将备份并支持恢复。\n"+
    "此操作会替换当前 MOD 目录，目录内的自定义文件保留在备份中。请确认游戏已经退出。",
    "确认下载并更新 MOD",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
   transactionWriting=true;
   try
   {
    toast.Text="正在下载、验证并安装："+selected.FileName;
    var result=await installerSource.InstallAsync(gameRoot,mod,selected);
    changed=true;
    orphanPatchNotice="已更新 "+mod.Name+" 至 "+result.Version+
     "。旧版本备份："+result.BackupFolder+"；可以在 MOD 详情中恢复。";
    toast.Text="MOD 更新成功："+mod.Name+" → "+result.Version;
    InvalidateLifecycle("MOD 已更新，现有修复补丁健康结果需要重新检查。");
    report=null;states=[];ClearCoop();ClearSelection();installPreview=null;lastInstallResult=null;
   }
   finally{transactionWriting=false;}
  }
  catch(Exception ex){toast.Text="MOD 更新未完成："+ex.Message;}
  finally
  {
   busy=false;
   if(changed)RefreshMods();
   else if(gameRoot==selectedInstallRoot)ShowModDetail(mod.Folder);
  }
 }
 void CleanOldModBackups(ManagedMod mod)
 {
  if(busy||transactionWriting||modInventoryDirty||selectedInstallRoot==null)return;
  string root=selectedInstallRoot;
  try
  {
   var candidates=GitHubModInstaller.CleanupCandidates(root,mod);
   if(candidates.Length==0){toast.Text="没有可清理的旧版本备份；最近一次备份始终保留。";return;}
   var window=new Window{Title="清理旧 MOD 版本 · "+mod.Name,Owner=this,Width=690,Height=475,
    MinWidth=540,WindowStartupLocation=WindowStartupLocation.CenterOwner};
   var layout=new DockPanel{Margin=new Thickness(18)};
   var title=V(T("选择要清理的旧版本备份",19,ink,true),
    T("仅删除此 MOD 的已选旧备份，始终保留最近一次备份用于恢复。删除后无法从该备份恢复；不会删除当前 MOD。",12,muted));
   DockPanel.SetDock(title,Dock.Top);layout.Children.Add(title);
   var selection=new List<(CheckBox Box,string Path)>();
   var rows=new StackPanel{Margin=new Thickness(0,16,0,12)};
   foreach(var path in candidates)
   {
    var box=new CheckBox{Content=System.IO.Path.GetFileName(path),Margin=new Thickness(0,5,0,2)};
    selection.Add((box,path));rows.Children.Add(box);
    var size=Directory.EnumerateFiles(path,"*",SearchOption.AllDirectories)
     .Sum(f=>new FileInfo(f).Length);
    rows.Children.Add(T("约 "+(size/1048576.0).ToString("F1")+" MB · "+path,11,muted));
   }
   var scroll=new ScrollViewer{Content=rows,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
   var actions=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,12,0,0)};
   var clear=Btn("删除选中的旧备份",()=>{
    var chosen=selection.Where(x=>x.Box.IsChecked==true).Select(x=>x.Path).ToArray();
    if(chosen.Length==0){MessageBox.Show(window,"请先勾选旧备份。");return;}
    if(MessageBox.Show(window,"将永久删除 "+chosen.Length+" 个已选旧版本备份。最近一次备份仍会保留，当前 MOD 不会受影响。\\n确定删除？",
      "最终确认",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
    try
    {
     busy=true;transactionWriting=true;
     int count=GitHubModInstaller.CleanupBackups(root,mod,chosen);
     window.DialogResult=true;toast.Text="已清理 "+count+" 个旧版本备份，最近一次备份已保留。";
    }
    catch(Exception ex){MessageBox.Show(window,"清理中断："+ex.Message+"\\n已删除的备份无法恢复，其余备份保持原状。");}
    finally{busy=false;transactionWriting=false;}
   },"Primary");
   actions.Children.Add(clear);
   actions.Children.Add(Btn("取消",()=>window.DialogResult=false,"Ghost"));
   DockPanel.SetDock(actions,Dock.Bottom);layout.Children.Add(actions);
   layout.Children.Add(scroll);
   window.Content=layout;window.ShowDialog();
   if(root==selectedInstallRoot)ShowModDetail(mod.Folder);
  }
  catch(Exception ex){toast.Text="无法列出旧版本备份："+ex.Message;}
 }
 void RestoreGitHubMod(ManagedMod mod)
 {
  if(busy||transactionWriting||modInventoryDirty||selectedInstallRoot==null)return;
  var root=selectedInstallRoot;
  try
  {
   var backups=GitHubModInstaller.Backups(root,mod);
   if(backups.Length==0){toast.Text="没有找到可恢复的 MOD 备份。";return;}
   string latest=backups[0];
   if(MessageBox.Show(this,
    "恢复 "+mod.Name+" 最近一次备份？\n备份路径："+latest+"\n"+
    "当前版本也会另存为备份，原有文件不会直接删除。\n请先关闭游戏。",
    "恢复 MOD 备份",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
   busy=true;transactionWriting=true;
   try
   {
    var previous=GitHubModInstaller.Restore(root,mod,latest);
    toast.Text="已恢复 MOD 备份；恢复前的版本保存在："+previous;
    InvalidateLifecycle("MOD 备份已恢复，现有修复补丁健康结果需要重新检查。");
    report=null;states=[];ClearCoop();ClearSelection();installPreview=null;lastInstallResult=null;
   }
   finally{busy=false;transactionWriting=false;}
   RefreshMods();
  }
  catch(Exception ex){busy=false;transactionWriting=false;toast.Text="恢复未完成："+ex.Message;ShowModDetail(mod.Folder);}
 }

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
   if(modInventoryDirty)githubDiscovery?.ClearCache();
   modInventory=inventory;modInventoryDirty=false;
   modStatus=$"共 {inventory.Mods.Count} 项 · {inventory.Mods.Count(m=>m.Enabled)} 项启用 · {inventory.Mods.Count(m=>!m.Enabled)} 项停用";
   // Only a complete, stable inventory is eligible for orphan-patch automation.
   // This observation belongs to the manager data directory, not the game.
   if(inventory.Issues.Count==0)
   {
    try
    {
     bool enabled=OrphanPatchAutoStop.Enabled(installer.DataRoot);
     var candidates=await Task.Run(()=>OrphanPatchAutoStop.Observe(root,installer.DataRoot,inventory,enabled));
     if(enabled&&candidates.Length>0)
     {
      modStatus="发现可能失去对应 MOD 的独立修复补丁，等待文件操作结束并再次校验…";
      await Task.Delay(3500);
      if(watchVersion!=modChangeVersion||!root.Equals(selectedInstallRoot,StringComparison.OrdinalIgnoreCase))
      {
       modInventoryDirty=true;
       modStatus="扫描期间 MOD 环境再次发生变化，已取消自动停用并安排复查。";
       modWatchDebounce?.Stop();modWatchDebounce?.Start();
       return;
      }
      if(OrphanPatchAutoStop.Enabled(installer.DataRoot))
      {
       transactionWriting=true;
       try
       {
        var outcome=await Task.Run(()=>OrphanPatchAutoStop.Reconcile(root,installer.DataRoot));
        modInventory=outcome.Inventory;
        modStatus=$"共 {outcome.Inventory.Mods.Count} 项 · {outcome.Inventory.Mods.Count(m=>m.Enabled)} 项启用 · {outcome.Inventory.Mods.Count(m=>!m.Enabled)} 项停用";
        if(outcome.Disabled.Length>0)
        {
         orphanPatchNotice="已自动停用 "+outcome.Disabled.Length+" 个失去目标 MOD 的修复补丁："+string.Join("、",outcome.Disabled.Take(3))+
          (outcome.Disabled.Length>3?" 等":"")+"。补丁文件完整保留，可在修复补丁中重新启用。";
         toast.Text=orphanPatchNotice;
         InvalidateLifecycle("目标 MOD 已移除，相关修复补丁已安全停用，请重新扫描。");
         report=null;states=[];ClearCoop();ClearSelection();installPreview=null;lastInstallResult=null;
        }
        if(outcome.Skipped.Length>0)
        {
         orphanPatchNotice="部分修复补丁未能自动停用："+string.Join("；",outcome.Skipped.Take(2))+
          "。未改变这些补丁的文件，请退出游戏或检查 MOD 目录后再刷新。";
         toast.Text=orphanPatchNotice;
        }
       }
       finally{transactionWriting=false;}
      }
     }
    }
    catch(Exception e)
    {
     orphanPatchNotice="孤立补丁自动检查未完成："+e.Message+"。不会未经核对移动补丁文件。";
     toast.Text=orphanPatchNotice;
    }
   }
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
  var discoverAll=Btn("批量查找 GitHub 来源",DiscoverGitHubMods,"Ghost");
  discoverAll.IsEnabled=!busy&&modInventory!=null&&!modInventoryDirty;
  discoverAll.Visibility=modSection=="普通 MOD"?Visibility.Visible:Visibility.Collapsed;
  header.Children.Add(H(refresh,Btn("打开模组目录",()=>{
   string directory=Path.Combine(selectedInstallRoot,"mods");
   if(Directory.Exists(directory))OpenUrl(directory);else toast.Text="此目录尚未创建 mods 文件夹。";
  },"Ghost"),discoverAll));
  Space(header,6);
  header.Children.Add(T(modStatus,12,muted));Space(header,11);
  if(modInventoryDirty)
  {
   header.Children.Add(Card(V(T("检测到 MOD 文件变化 · 正在核对列表",13,gold,true),
    T("补丁健康结论已失效，需要重新扫描。",11,muted)),padding:10));
   Space(header,10);
  }
  if(orphanPatchNotice!=null)
  {
   header.Children.Add(Card(V(T("自动补丁管理",13,ink,true),T(orphanPatchNotice,12,muted)),padding:10));
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
   bool autoOn=OrphanPatchAutoStop.Enabled(installer.DataRoot);
   header.Children.Add(H(
    Btn("删除目标 MOD 后自动停用补丁："+(autoOn?"开启":"关闭"),()=>{
     try
     {
      OrphanPatchAutoStop.SetEnabled(installer.DataRoot,!autoOn);
      orphanPatchNotice=autoOn?"已关闭自动停用；之后只手动管理修复补丁。":
       "已开启自动停用；首次完整扫描会记录 MOD 环境，之后删除目标 MOD 时会自动核对、停用并保留文件。";
      ShowMods();
      if(!autoOn)RefreshMods();
     }
     catch(Exception ex){toast.Text="设置保存失败："+ex.Message;}
    },"Ghost")));
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

  var rows=modInventory==null?[]:ModListView.Rows(selectedInstallRoot,modInventory,LifecycleTag,mod=>{
   if(ModPresentation.IsIndependentPatch(mod))return "";
   var updates=GitHubUpdates();
   if(updates.Repository(mod.Id)==null)
   {
    var candidates=Discovery().Cached(mod);
    return candidates==null?"":candidates.Candidates.Length>0
     ?"GitHub："+(candidates.Candidates.Any(c=>c.Verified)?"已核验来源":"疑似来源")+
      " "+candidates.Candidates.Length+" 个 · 待确认"
     :candidates.Retryable?"GitHub：查找受限，可重试":"GitHub：暂无来源";
   }
   var result=updates.Cached(mod.Id);
   return result==null?"GitHub：已关联 · 待检查":"GitHub："+result.Status+(result.LatestVersion.Length>0?" · "+result.LatestVersion:"");
  });
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
  if(!ModPresentation.IsIndependentPatch(mod))
  {
   var updates=GitHubUpdates();var repo=updates.Repository(mod.Id);var result=updates.Cached(mod.Id);
   root.Children.Add(T("GitHub 更新检测",17,ink,true));Space(root,8);
   root.Children.Add(T(repo==null?"尚未关联 GitHub 仓库。":"仓库："+repo,12,muted));Space(root,6);
   if(result!=null)
   {
    root.Children.Add(H(Badge(result.Status,result.Status=="有更新"?gold:muted),
     T("本地 "+result.LocalVersion+" · GitHub "+(result.LatestVersion.Length>0?result.LatestVersion:"未知"),12,muted)));
    root.Children.Add(T(result.Message+" · 检查于 "+result.CheckedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),12,muted));
    if(result.ReleaseUrl.Length>0)root.Children.Add(Btn("打开 Release 发布页",()=>OpenUrl(result.ReleaseUrl),"Ghost"));
   }
   else if(repo!=null)root.Children.Add(T("尚未检查该仓库。",12,muted));
   var discovery=Discovery().Cached(mod);
   if(repo==null&&discovery!=null)
   {
    Space(root,6);
    root.Children.Add(T(discovery.Candidates.Length>0
     ?"找到 "+discovery.Candidates.Length+" 个候选来源；优先参考核验 MOD ID 的仓库，请确认再关联。"
     :discovery.Note,12,muted));
    if(discovery.Candidates.Length>0&&discovery.Note.Length>0)
     root.Children.Add(T(discovery.Note,11,muted));
    foreach(var candidate in discovery.Candidates)
    {
     var evidence=candidate.Reason.StartsWith("MOD 清单声明",StringComparison.Ordinal)
       ?"本地清单声明":candidate.Verified?"仓库 MOD ID 已核验":"尚未核验，请仔细确认";
      var row=V(T(candidate.Repository,13,ink,true),T(candidate.Reason+" · "+evidence,11,muted));
     var confirm=Btn("确认关联",()=>{
      try{updates.Bind(mod.Id,candidate.Repository);ShowModDetail(mod.Folder);}
      catch(Exception e){toast.Text="关联失败："+e.Message;}
     },"Ghost");
     var openSource=Btn("查看仓库",()=>OpenUrl("https://github.com/"+candidate.Repository),"Ghost");
     row.Children.Add(H(confirm,openSource));root.Children.Add(Card(row,padding:10));Space(root,6);
    }
   }
   Space(root,9);
   if(repo==null)
   {
    var discover=Btn("自动查找来源",()=>DiscoverGitHubMod(mod),"Ghost");discover.IsEnabled=!busy;
    root.Children.Add(H(discover));Space(root,7);
   }
   var bind=Btn(repo==null?"关联 GitHub 仓库":"修改 GitHub 关联",()=>BindGitHubMod(mod),"Ghost");
   var check=Btn("检查更新",()=>CheckGitHubMod(mod),"Ghost");check.IsEnabled=repo!=null&&!busy;
   var update=Btn("下载并更新",()=>UpdateGitHubMod(mod),"Primary");
   update.IsEnabled=repo!=null&&!busy&&!transactionWriting&&!modInventoryDirty&&mod.Enabled&&result?.Status=="有更新";
   root.Children.Add(H(bind,check,update));Space(root,7);
   if(mod.Enabled&&selectedInstallRoot!=null&&GitHubModInstaller.Backups(selectedInstallRoot,mod).Length>0)
    root.Children.Add(H(Btn("恢复上次 MOD 备份",()=>RestoreGitHubMod(mod),"Ghost"),Btn("清理旧版本备份",()=>CleanOldModBackups(mod),"Ghost")));
   Space(root,20);
  }
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
