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
 WorkshopZipPlan? zipImportPreview;
 string modSection="普通 MOD",modQuery="",modFilter="全部",modIssueFilter="所有状态",modStatus="读取模组清单，管理本地模组的启用和停用。";
 ListBox? activeModList;
 string? selectedModDetailFolder;
 GitHubModUpdates? githubModUpdates;
 GitHubModDiscovery? githubDiscovery;
 GitHubModDiscovery Discovery()=>githubDiscovery??=new GitHubModDiscovery();
 string? githubRoot;
 WorkshopModSources? workshopModSources;
 string? workshopSourceRoot;
 WorkshopModUpdates? workshopModUpdates;
 string? workshopUpdatesRoot;
 bool updateCenterAutoCheckQueued;
 WorkshopModUpdates WorkshopUpdates()
 {
  if(selectedInstallRoot==null)throw new InvalidOperationException("请先选择游戏目录。");
  if(workshopModUpdates==null||!string.Equals(workshopUpdatesRoot,selectedInstallRoot,StringComparison.OrdinalIgnoreCase))
  {
   workshopModUpdates=new WorkshopModUpdates();
   workshopUpdatesRoot=selectedInstallRoot;
  }
  return workshopModUpdates;
 }
 WorkshopModSources WorkshopSources()
 {
  if(selectedInstallRoot==null)throw new InvalidOperationException("请先选择游戏目录。");
  if(workshopModSources==null||!string.Equals(workshopSourceRoot,selectedInstallRoot,StringComparison.OrdinalIgnoreCase))
  {
   workshopModSources=new WorkshopModSources(installer.DataRoot,selectedInstallRoot);
   workshopSourceRoot=selectedInstallRoot;
  }
  return workshopModSources;
 }
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
  finally{busy=false;if(page=="更新中心")ShowModUpdatesCenter();else ShowMods();}
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
 async void CheckAllModUpdates()
 {
  if(busy||transactionWriting||modUpdateCheckRunning||modInventory==null||selectedInstallRoot==null)return;
  if(modInventoryDirty){toast.Text="本地 MOD 清单尚未稳定，请刷新后重试。";return;}
  // Capture the read-only scan scope: leaving the page must never redirect
  // the user back here, and switching game roots must not publish stale results.
  var inventory=modInventory;
  string gameRoot=selectedInstallRoot;
  var github=GitHubUpdates();
  var workshop=WorkshopUpdates();
  var sources=WorkshopSources();
  using var cancellation=new CancellationTokenSource();
  modUpdateCheckCancellation=cancellation;
  modUpdateCheckRunning=true;
  toast.Text="正在后台检查 GitHub 与创意工坊来源，可正常切换页面。";
  if(page=="更新中心")ShowModUpdatesCenter();
  try
  {
   int githubCount=await github.CheckAllAsync(inventory.Mods,cancellation.Token);
   int workshopCount=await workshop.CheckAllAsync(inventory.Mods,sources,cancellation.Token);
   if(cancellation.IsCancellationRequested||gameRoot!=selectedInstallRoot||
      !ReferenceEquals(inventory,modInventory))return;
   int githubNew=inventory.Mods.Count(m=>!ModPresentation.IsIndependentPatch(m)&&
    github.Cached(m.Id)?.Status=="有更新");
   int workshopNew=inventory.Mods.Count(m=>!ModPresentation.IsIndependentPatch(m)&&
    workshop.Cached(m.Id)?.Status=="有更新");
   int workshopNeedsReview=inventory.Mods.Count(m=>!ModPresentation.IsIndependentPatch(m)&&
    workshop.Cached(m.Id)?.Status is "待首次核验" or "本地文件已变化");
   modStatus=$"检查完成：GitHub {githubCount} 项（有更新 {githubNew}）；创意工坊 {workshopCount} 项（有更新 {workshopNew}，待核验 {workshopNeedsReview}）。仅检测来源内容，不会自动下载或修改游戏文件。";
   toast.Text=modStatus;
  }
  catch(OperationCanceledException) when(cancellation.IsCancellationRequested)
  {
   if(gameRoot==selectedInstallRoot)modStatus="已取消本次更新检查。";
  }
  catch(Exception e)
  {
   if(gameRoot==selectedInstallRoot)modStatus="MOD 更新检查未完成："+e.Message;
  }
  finally
  {
   if(ReferenceEquals(modUpdateCheckCancellation,cancellation))modUpdateCheckCancellation=null;
   modUpdateCheckRunning=false;
   // A background check never changes the active workspace on completion.
   if(page=="更新中心"&&IsVisible)ShowModUpdatesCenter();
  }
 }
 async void CheckWorkshopMod(ManagedMod mod)
 {
  if(busy||modInventoryDirty||selectedInstallRoot==null)return;
  ulong? workshopId=WorkshopSources().Get(mod.Id);
  if(workshopId is null){toast.Text="此 MOD 尚未关联创意工坊来源。";return;}
  busy=true;
  try
  {
   var result=await WorkshopUpdates().CheckAsync(mod,workshopId.Value,WorkshopSources(),force:true);
   toast.Text=mod.Name+" · 创意工坊："+result.Status+" · "+result.Message;
  }
  catch(Exception e){toast.Text="创意工坊检查失败："+e.Message;}
  finally{busy=false;if(page=="更新中心")ShowModUpdatesCenter();else ShowModDetail(mod.Folder);}
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
  if(dialog.ShowDialog()==true){if(page=="更新中心")ShowModUpdatesCenter();else ShowModDetail(mod.Folder);}
 }
 async void DownloadWorkshopMod(ManagedMod? target=null,ulong? presetWorkshopId=null)
 {
  if(busy||transactionWriting||modInventoryDirty||selectedInstallRoot==null)return;
  string gameRoot=selectedInstallRoot;
  ulong workshopId=presetWorkshopId??0;
  if(!presetWorkshopId.HasValue)
  {
   ulong? savedId=target==null?null:WorkshopSources().Get(target.Id);
   var dialog=new Window{Title="从尖塔补给站下载 MOD",Owner=this,Width=560,Height=245,
    MinWidth=500,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize};
   var body=new StackPanel{Margin=new Thickness(18)};
   body.Children.Add(T("输入 Steam 创意工坊 Workshop ID，或粘贴 Steam / 尖塔补给站分享链接。",13,muted));
   var input=new TextBox{Text=savedId?.ToString()??"",Margin=new Thickness(0,12,0,10),MinHeight=32};
   body.Children.Add(input);
   var hint=T("支持数字 ID、steamcommunity.com/sharedfiles/filedetails 链接，以及 workshop.apricityx.top/mod 链接。",11,muted);
   body.Children.Add(hint);
   var start=Btn("获取并预览",()=>{
    if(!WorkshopModInstaller.TryParseWorkshopId(input.Text,out workshopId))
    {hint.Text="ID 或链接无效；请确认属于《杀戮尖塔 2》（AppID 2868840）的创意工坊条目。";return;}
    dialog.DialogResult=true;
   },"Primary");
   body.Children.Add(H(start,Btn("取消",()=>dialog.DialogResult=false,"Ghost")));
   dialog.Content=body;input.Focus();
   if(dialog.ShowDialog()!=true)return;
 
  }
  busy=true;
  bool changed=false;
  WorkshopModPrepared? prepared=null;
  try
  {
   using var source=new WorkshopModInstaller();
   toast.Text="正在准备创意工坊 MOD…";
   prepared=await source.PrepareAsync(gameRoot,workshopId,
    progress:message=>Dispatcher.BeginInvoke(new Action(()=>{
     if(gameRoot==selectedInstallRoot)toast.Text=message;
    })));
   if(gameRoot!=selectedInstallRoot)
    throw new InvalidOperationException("游戏目录已切换，已取消本次安装。");

   string? boundModId=WorkshopSources().ModIdFor(workshopId);
   if(boundModId!=null&&!boundModId.Equals(prepared.ModId,StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("这个 Workshop ID 已关联到另一个 MOD ID："+boundModId+"；为避免来源错配，未安装。");

   string action=prepared.IsUnchanged?"本地文件与创意工坊内容完全一致，无需替换。":
    prepared.IsUpdate?"将更新现有 MOD，并先备份旧版本。":"将新增一个 MOD，不会覆盖现有目录。";
   string versionNote="";
   if(prepared.IsUpdate&&WorkshopModInstaller.TryCompareVersions(
      prepared.Version,prepared.ExistingVersion??"",out int versionOrder)&&
      versionOrder==0&&!prepared.IsUnchanged)
    versionNote="\n注意：版本号相同，但文件内容不同；将按文件内容执行更新。";
   else if(prepared.IsUpdate&&!WorkshopModInstaller.TryCompareVersions(
      prepared.Version,prepared.ExistingVersion??"",out _))
    versionNote="\n注意：版本号格式无法自动比较，请核对来源和版本内容。";
   string review=
    "来源：尖塔补给站（Apricityx）\n"+
    "Workshop ID："+prepared.WorkshopId+"\n"+
    "MOD："+prepared.ModName+"（"+prepared.ModId+"）\n"+
    "版本："+(prepared.IsUpdate?(prepared.ExistingVersion??"未知")+" → "+prepared.Version:prepared.Version)+"\n"+
    "操作："+action+"\n"+
    "文件数："+prepared.FileCount+" · 总大小："+prepared.TotalBytes.ToString("N0")+" 字节\n"+
    "目标目录："+prepared.DestinationDirectory+"\n\n"+
    "安全说明：Chunk 和文件 SHA-1 校验已通过；Manifest 签名验证、游戏分支匹配验证尚未执行。"+
    versionNote+
    "请仅安装你信任的创意工坊内容。安装前必须退出游戏。";
   if(prepared.IsUnchanged)
   {
    MessageBox.Show(this,review,"创意工坊 MOD 已是最新",MessageBoxButton.OK,MessageBoxImage.Information);
   }
   else if(MessageBox.Show(this,review+"\n\n确认后才会写入游戏 mods 目录；更新时旧版本会保留在尖塔修补站_MOD备份中。",
    prepared.IsUpdate?"确认更新 MOD":"确认安装 MOD",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)
    return;

   if(gameRoot!=selectedInstallRoot)
    throw new InvalidOperationException("游戏目录已切换，已取消本次安装。");
   transactionWriting=true;
   WorkshopModInstallResult result;
   string? safetySnapshotId=null;
   try
   {
    if(!prepared.IsUnchanged)
    {
     toast.Text="正在创建安装前完整环境快照…";
     var safety=await Task.Run(()=>ModEnvironmentSnapshots.Create(
      gameRoot,installer.DataRoot,"更新前自动保护"));
     safetySnapshotId=safety.Id;
    }
    toast.Text=prepared.IsUpdate?"正在备份旧版本并安装创意工坊 MOD…":"正在安装创意工坊 MOD…";
    result=await Task.Run(()=>WorkshopModInstaller.Commit(gameRoot,prepared));
   }
   finally{transactionWriting=false;}

   string? sourceSaveWarning=null;
   try
   {
    var sources=WorkshopSources();
    sources.Bind(result.ModId,workshopId);
    sources.RecordBaseline(result.ModId,workshopId,prepared.ManifestId,result.Version,prepared.PayloadSnapshot);
    WorkshopUpdates().Invalidate(result.ModId);
   }
   catch(Exception sourceError)
   {
    sourceSaveWarning="（来源或版本基线保存失败："+sourceError.Message+"）";
   }

   changed=result.Changed;
   if(changed)
   {
    orphanPatchNotice=result.BackupFolder==null
     ?"已从创意工坊新增 "+result.ModName+"（"+result.ModId+"）。"
     :"已从创意工坊更新 "+result.ModName+"。旧版本备份："+result.BackupFolder;
    toast.Text=(result.BackupFolder==null
     ?"创意工坊 MOD 安装成功："+result.ModName
     :"创意工坊 MOD 更新成功："+result.ModName+"；旧版本已备份。")+
     (safetySnapshotId==null?"":"；更新前环境快照："+safetySnapshotId)+(sourceSaveWarning??"");
    InvalidateLifecycle("创意工坊 MOD 已安装或更新，现有修复补丁健康结果需要重新检查。");
    report=null;states=[];ClearCoop();ClearSelection();installPreview=null;lastInstallResult=null;
   }
   else
   {
    toast.Text="创意工坊内容与本地文件一致，无需更新："+result.ModName+(sourceSaveWarning??"");
   }
  }
  catch(Exception ex){toast.Text="创意工坊 MOD 安装未完成："+ex.Message;}
  finally
  {
   WorkshopModInstaller.Cleanup(prepared);
   busy=false;transactionWriting=false;
   if(changed)RefreshMods();
   else if(gameRoot==selectedInstallRoot)
   {
    if(target!=null)ShowModDetail(target.Folder);
    else ShowMods();
   }
  }
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
  finally{busy=false;if(page=="更新中心")ShowModUpdatesCenter();else ShowModDetail(mod.Folder);}
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
    string? safetySnapshotId=null;
    var result=await installerSource.InstallAsync(gameRoot,mod,selected,
     beforeCommit:async()=>{
      toast.Text="安装包已校验，正在创建更新前完整环境快照…";
      var safety=await Task.Run(()=>ModEnvironmentSnapshots.Create(
       gameRoot,installer.DataRoot,"更新前自动保护"));
      safetySnapshotId=safety.Id;
      toast.Text="更新前环境已保存，正在替换 MOD…";
     });
    changed=true;
    orphanPatchNotice="已更新 "+mod.Name+" 至 "+result.Version+
     "。旧版本备份："+result.BackupFolder+"；环境快照："+safetySnapshotId+"。";
    toast.Text="MOD 更新成功："+mod.Name+" → "+result.Version+"；更新前快照："+safetySnapshotId;
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
   else if(gameRoot==selectedInstallRoot){if(page=="更新中心")ShowModUpdatesCenter();else ShowModDetail(mod.Folder);}
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
    rows.Children.Add(T("备份目录："+path,11,muted));
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
 void RestoreModBackup(ManagedMod mod)
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
    string baselineNote="";
    try{WorkshopSources().ClearBaseline(mod.Id);WorkshopUpdates().Invalidate(mod.Id);}
    catch(Exception baselineError){baselineNote="（版本基线清理未完成："+baselineError.Message+"；下次检查会要求重新核验本地文件。）";}
    toast.Text="已恢复 MOD 备份；恢复前的版本保存在："+previous+baselineNote;
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
  string? recoveryNotice=null;
  busy=true;modTogglePreview=null;modStatus="正在读取模组清单…";
  if((page=="模组管理"||page=="本地补丁")&&(currentVisit?.Key==page))ShowMods();
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
   recoveryNotice=ModEnvironmentSnapshots.TakeRecoveryNotice();
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
   if(recoveryNotice!=null&&root.Equals(selectedInstallRoot,StringComparison.OrdinalIgnoreCase))
    toast.Text=recoveryNotice;
   if((page=="模组管理"||page=="本地补丁")&&(currentVisit?.Key==page))ShowMods();
   else if(page=="模组管理"&&currentVisit?.Key?.StartsWith("mod:",StringComparison.Ordinal)==true)
   {
    string folder=currentVisit.Key["mod:".Length..];
    if(modInventory?.Mods.Any(m=>m.Folder.Equals(folder,StringComparison.OrdinalIgnoreCase))==true)
    {
     string detailToast=toast.Text;
     ShowModDetail(folder);
     toast.Text=detailToast;
    }
    else ShowMods();
   }
   else if(page=="模组管理"&&currentVisit?.Key=="mod-dependencies")ShowDependencyGraph();
   else if(page=="模组管理"&&currentVisit?.Key is "mod-snapshots" or "mod-snapshot-preview")ShowModEnvironmentSnapshots();
   else if(page=="更新中心")ShowModUpdatesCenter();
   else if(page=="总览")ShowOverview();
  }
 }
 readonly HashSet<string> chosenModFolders=new(StringComparer.OrdinalIgnoreCase);
 ManagedMod[] ChosenMods()=>modInventory?.Mods.Where(m=>chosenModFolders.Contains(m.Folder)&&
  ModPresentation.IsIndependentPatch(m)==(modSection=="修复补丁")).ToArray()??[];
 async void BatchFindGitHub()
 {
  if(busy||transactionWriting||modInventoryDirty||selectedInstallRoot==null)return;
  var selected=ChosenMods();
  var candidates=selected.Where(m=>!ModPresentation.IsIndependentPatch(m)&&
   GitHubUpdates().Repository(m.Id)==null&&Discovery().Cached(m)==null).Take(8).ToArray();
  var candidatesSet=candidates.Select(m=>m.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
  var reviews=selected.Select(m=>new ModBatchReview(m.Name,candidatesSet.Contains(m.Folder)?"可处理":"跳过",
   candidatesSet.Contains(m.Folder)?"只查找候选仓库，仍需手动核对后绑定。":"已有来源或搜索缓存；或者超出每轮 8 项上限。")).ToArray();
  if(!ModBatchDialogs.Confirm(this,"批量查找 GitHub 来源",
   "每轮最多检查 8 项。不会自动关联仓库，也不会下载或修改游戏文件；结果可在模组详情中核对。",reviews))return;
  if(busy||modInventoryDirty)return;
  busy=true;int found=0,failed=0,done=0;
  var progress=new ModBatchProgressWindow(this,"批量查找 GitHub 来源",selected.Length);progress.Show();
  try
  {
   foreach(var mod in candidates)
   {
    if(progress.CancelRequested)break;
    progress.Current(mod.Name,done+1,selected.Length);
    try
    {
     var result=await Discovery().DiscoverAsync(mod);
     if(result.Candidates.Length>0)found++;
     if(result.Retryable)failed++;
     string state=result.Retryable?"受限":result.Candidates.Length>0?"候选待确认":"未找到";
     done++;progress.Record(mod.Name,state,result.Note,done);
     if(result.Retryable)break;
    }
    catch(Exception e){failed++;done++;progress.Record(mod.Name,"失败",e.Message,done);}
   }
   foreach(var mod in selected.Where(m=>!candidatesSet.Contains(m.Folder)))
   {done++;progress.Record(mod.Name,"跳过","已关联、已有缓存，或超出本轮处理上限。",done);}
   if(done<selected.Length)foreach(var mod in candidates.Skip(done-selected.Length+candidates.Length))
   {done++;progress.Record(mod.Name,"未执行","用户取消或网络受限；可重新查找。",done);}
   modStatus=$"来源查找：发现候选 {found} 项，失败或受限 {failed} 项。需在详情逐个确认。";
  }
  finally{busy=false;progress.Finish(modStatus);ShowMods();}
 }
 async void BatchCheckGitHub()
 {
  if(busy||transactionWriting||modInventoryDirty||selectedInstallRoot==null)return;
  var selected=ChosenMods();
  var mods=selected.Where(m=>!ModPresentation.IsIndependentPatch(m)&&GitHubUpdates().Repository(m.Id)!=null).ToArray();
  var set=mods.Select(m=>m.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
  var reviews=selected.Select(m=>new ModBatchReview(m.Name,set.Contains(m.Folder)?"可处理":"跳过",
   set.Contains(m.Folder)?"检测已确认仓库的正式 Release，不修改模组文件。":"尚未关联 GitHub 来源，或属于修复补丁。")).ToArray();
  if(!ModBatchDialogs.Confirm(this,"批量检查 GitHub 更新",
   "仅读取已关联 GitHub 的 MOD 发布信息，无法检测的项目会显示原因，不会自动下载。",reviews))return;
  if(busy||modInventoryDirty)return;
  busy=true;int success=0,failed=0,done=0;
  var progress=new ModBatchProgressWindow(this,"批量检查 GitHub 更新",selected.Length);progress.Show();
  try
  {
   foreach(var mod in mods)
   {
    if(progress.CancelRequested)break;
    progress.Current(mod.Name,done+1,selected.Length);
    try
    {
     var result=await GitHubUpdates().CheckAsync(mod,true);
     bool ok=result.Status!="无法检测";
     if(ok)success++;else failed++;
     done++;progress.Record(mod.Name,result.Status,result.Message,done);
    }catch(Exception e){failed++;done++;progress.Record(mod.Name,"失败",e.Message,done);}
   }
   foreach(var mod in selected.Where(m=>!set.Contains(m.Folder)))
   {done++;progress.Record(mod.Name,"跳过","未关联 GitHub 仓库或不适用。",done);}
   if(done<selected.Length)foreach(var mod in mods.Skip(done-selected.Length+mods.Length))
   {done++;progress.Record(mod.Name,"未执行","用户已停止后续任务。",done);}
   modStatus=$"更新检测：完成 {success} 项，失败或受限 {failed} 项；未安装或修改模组。";
  }
  finally{busy=false;progress.Finish(modStatus);ShowMods();}
 }

 async void BatchCheckWorkshop()
 {
  if(busy||transactionWriting||modInventoryDirty||selectedInstallRoot==null)return;
  var selected=ChosenMods();
  var sources=WorkshopSources();
  var eligible=selected.Where(m=>!ModPresentation.IsIndependentPatch(m)&&sources.Get(m.Id).HasValue).ToArray();
  var eligibleFolders=eligible.Select(m=>m.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
  var reviews=selected.Select(m=>new ModBatchReview(m.Name,
   eligibleFolders.Contains(m.Folder)?"可处理":"跳过",
   eligibleFolders.Contains(m.Folder)?"只读取已关联的创意工坊 Manifest，不下载 MOD 文件。":
   ModPresentation.IsIndependentPatch(m)?"独立修复补丁不适用。":"尚未关联创意工坊来源。")).ToArray();
  if(!ModBatchDialogs.Confirm(this,"批量检查创意工坊更新",
   "只检查已关联来源的普通 MOD。仅读取远端内容身份，不下载 Chunk、不修改游戏文件；首次核验和本地文件变化会单独标记。",reviews))return;
  if(busy||transactionWriting||modInventoryDirty||selectedInstallRoot==null)return;

  busy=true;
  int checkedCount=0,updateCount=0,failed=0,completed=0;
  var progress=new ModBatchProgressWindow(this,"批量检查创意工坊更新",selected.Length);
  progress.Show();
  try
  {
   foreach(var mod in selected)
   {
    if(progress.CancelRequested)break;
    progress.Current(mod.Name,completed+1,selected.Length);
    if(!eligibleFolders.Contains(mod.Folder))
    {
     completed++;
     progress.Record(mod.Name,"跳过",
      ModPresentation.IsIndependentPatch(mod)?"独立修复补丁不适用。":"尚未关联创意工坊来源。",completed);
     continue;
    }
    try
    {
     ulong workshopId=sources.Get(mod.Id)??throw new InvalidOperationException("创意工坊来源关联已变化。");
     var result=await WorkshopUpdates().CheckAsync(mod,workshopId,sources,force:true);
     if(result.Status is "检查失败" or "暂不支持")failed++;
     else checkedCount++;
     if(result.Status=="有更新")updateCount++;
     completed++;
     progress.Record(mod.Name,result.Status,result.Message,completed);
    }
    catch(Exception e)
    {
     failed++;completed++;
     progress.Record(mod.Name,"失败",e.Message,completed);
    }
   }
   while(completed<selected.Length)
   {
    var mod=selected[completed++];
    progress.Record(mod.Name,"未执行","用户停止了剩余检查。",completed);
   }
   modStatus=$"创意工坊批量检查结束：完成 {checkedCount} 项，发现更新 {updateCount} 项，失败 {failed} 项；未关联来源或修复补丁已跳过。";
  }
  finally
  {
   busy=false;
   progress.Finish(modStatus);
   ShowMods();
  }
 }

 async void BatchUpdateWorkshop()
 {
  if(busy||transactionWriting||modInventoryDirty||selectedInstallRoot==null)return;
  string root=selectedInstallRoot;
  var selected=ChosenMods();
  var sources=WorkshopSources();
  var eligible=selected.Where(m=>!ModPresentation.IsIndependentPatch(m)&&m.Enabled&&
   sources.Get(m.Id).HasValue&&WorkshopUpdates().Cached(m.Id)?.Status=="有更新").ToArray();
  var eligibleFolders=eligible.Select(m=>m.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
  var reviews=selected.Select(m=>
  {
   if(eligibleFolders.Contains(m.Folder))
    return new ModBatchReview(m.Name,"可处理","已关联创意工坊来源，且最近一次检查发现新 Manifest。");
   if(ModPresentation.IsIndependentPatch(m))
    return new ModBatchReview(m.Name,"跳过","独立修复补丁不适用。");
   if(!m.Enabled)
    return new ModBatchReview(m.Name,"跳过","当前 MOD 已停用；批量更新只处理已启用的独立 MOD。");
   if(!sources.Get(m.Id).HasValue)
    return new ModBatchReview(m.Name,"跳过","尚未关联创意工坊来源。");
   var status=WorkshopUpdates().Cached(m.Id)?.Status;
   return new ModBatchReview(m.Name,"跳过",status==null?"尚未检查创意工坊更新。":"当前状态："+status+"，没有待处理的更新。");
  }).ToArray();
  if(!ModBatchDialogs.Confirm(this,"批量更新创意工坊 MOD",
   "只处理已选中、已关联来源且检查结果为「有更新」的启用 MOD。每项都会重新下载并显示详细预览；选择“是”更新、“否”跳过、“取消”停止剩余任务。每项独立备份，失败自动回滚并停止后续更新。",reviews))return;
  if(busy||transactionWriting||modInventoryDirty||!root.Equals(selectedInstallRoot,StringComparison.OrdinalIgnoreCase))return;

  busy=true;
  int updated=0,unchanged=0,failed=0,skipped=0,completed=0;
  bool changed=false,stopAfterFailure=false;
  var progress=new ModBatchProgressWindow(this,"批量更新创意工坊 MOD",selected.Length);
  progress.Show();
  try
  {
   for(int index=0;index<selected.Length;index++)
   {
    var mod=selected[index];
    if(progress.CancelRequested)break;
    progress.Current(mod.Name,completed+1,selected.Length);
    if(!eligibleFolders.Contains(mod.Folder))
    {
     skipped++;completed++;
     progress.Record(mod.Name,"跳过",reviews[index].Explanation,completed);
     continue;
    }

    WorkshopModPrepared? prepared=null;
    try
    {
     ulong workshopId=sources.Get(mod.Id)??throw new InvalidOperationException("创意工坊来源关联已变化。");
     using var source=new WorkshopModInstaller();
     toast.Text="正在准备创意工坊更新："+mod.Name;
     prepared=await source.PrepareAsync(root,workshopId,
      progress:message=>Dispatcher.BeginInvoke(new Action(()=>{
       if(root==selectedInstallRoot)toast.Text=message;
      })));

     if(progress.CancelRequested)
     {
      skipped++;completed++;
      progress.Record(mod.Name,"已取消","当前 MOD 已准备完成，但尚未写入游戏目录；后续任务将停止。",completed);
      break;
     }
     if(!root.Equals(selectedInstallRoot,StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("游戏目录已切换，已取消本次批量更新。");
     if(!prepared.ModId.Equals(mod.Id,StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("下载内容中的 MOD ID 与所选 MOD 不一致，已停止更新。");
     if(!prepared.IsUpdate)
      throw new InvalidOperationException("本地 MOD 已不存在或无法匹配；批量更新不会将其改为新增安装，请刷新列表后重试。");

     string? boundModId=WorkshopSources().ModIdFor(workshopId);
     if(boundModId!=null&&!boundModId.Equals(prepared.ModId,StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("该 Workshop ID 已关联到另一个 MOD ID："+boundModId+"；为避免来源错配，未更新。");

     string versionNote="";
     if(WorkshopModInstaller.TryCompareVersions(
       prepared.Version,prepared.ExistingVersion??"",out int versionOrder)&&versionOrder==0&&!prepared.IsUnchanged)
      versionNote="\n注意：版本号相同，但文件内容不同；将按文件内容执行更新。";
     else if(!WorkshopModInstaller.TryCompareVersions(
       prepared.Version,prepared.ExistingVersion??"",out _))
      versionNote="\n注意：版本号格式无法自动比较，请核对来源和版本内容。";

     string review=
      "来源：尖塔补给站（Apricityx）\n"+
      "Workshop ID："+prepared.WorkshopId+"\n"+
      "MOD："+prepared.ModName+"（"+prepared.ModId+"）\n"+
      "版本："+(prepared.ExistingVersion??"未知")+" → "+prepared.Version+"\n"+
      "操作："+(prepared.IsUnchanged?"本地文件与创意工坊内容完全一致，不需要替换。":"更新现有 MOD，并先备份旧版本。")+"\n"+
      "文件数："+prepared.FileCount+" · 总大小："+prepared.TotalBytes.ToString("N0")+" 字节\n"+
      "目标目录："+prepared.DestinationDirectory+"\n\n"+
      "安全说明：Chunk 和文件 SHA-1 校验已通过；Manifest 签名验证、游戏分支匹配验证尚未执行。"+
      versionNote+
      "\n请仅更新你信任的创意工坊内容。执行前必须退出游戏。";

     if(prepared.IsUnchanged)
     {
      MessageBox.Show(this,review+"\n\n下载内容与本地文件完全一致，不会替换文件；确认后只刷新已验证的版本基线。",
       "创意工坊内容已一致",MessageBoxButton.OK,MessageBoxImage.Information);
     }
     else
     {
      var choice=MessageBox.Show(this,
       review+"\n\n选择“是”更新此 MOD；选择“否”跳过此项；选择“取消”停止剩余任务。",
       "确认更新 "+mod.Name,MessageBoxButton.YesNoCancel,MessageBoxImage.Warning);
      if(choice==MessageBoxResult.No)
      {
       skipped++;completed++;
       progress.Record(mod.Name,"跳过","用户选择跳过此项；本地 MOD 未修改。",completed);
       continue;
      }
      if(choice!=MessageBoxResult.Yes)
      {
       skipped++;completed++;
       progress.Record(mod.Name,"已取消","用户停止了剩余更新；本地 MOD 未修改。",completed);
       break;
      }
     }

     if(progress.CancelRequested)
     {
      skipped++;completed++;
      progress.Record(mod.Name,"已取消","用户停止了剩余更新；当前 MOD 尚未写入游戏目录。",completed);
      break;
     }
     if(!root.Equals(selectedInstallRoot,StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("游戏目录已切换，已取消本次批量更新。");

     transactionWriting=true;
     WorkshopModInstallResult result;
     string? safetySnapshotId=null;
     try
     {
      if(!prepared.IsUnchanged)
      {
       toast.Text="正在为 "+mod.Name+" 创建更新前完整环境快照…";
       var safety=await Task.Run(()=>ModEnvironmentSnapshots.Create(
        root,installer.DataRoot,"更新前自动保护"));
       safetySnapshotId=safety.Id;
      }
      toast.Text=prepared.IsUnchanged?"正在核对本地 MOD 并刷新版本基线…":"正在备份旧版本并更新："+mod.Name;
      result=await Task.Run(()=>WorkshopModInstaller.Commit(root,prepared));
     }
     finally{transactionWriting=false;}

     string sourceSaveWarning="";
     try
     {
      var currentSources=WorkshopSources();
      currentSources.Bind(result.ModId,workshopId);
      currentSources.RecordBaseline(result.ModId,workshopId,prepared.ManifestId,result.Version,prepared.PayloadSnapshot);
     }
     catch(Exception sourceError)
     {
      sourceSaveWarning="（来源或版本基线保存失败："+sourceError.Message+"）";
     }
     WorkshopUpdates().Invalidate(result.ModId);

     if(result.Changed)
     {
      updated++;changed=true;completed++;
      progress.Record(mod.Name,"成功",
       "已更新至 "+result.Version+"；旧版本备份："+(result.BackupFolder??"未返回备份路径")+
        (safetySnapshotId==null?"":"；环境快照："+safetySnapshotId)+sourceSaveWarning,completed);
     }
     else
     {
      unchanged++;completed++;
      progress.Record(mod.Name,"无需更新","下载内容与本地文件一致，已刷新版本基线。"+sourceSaveWarning,completed);
     }
    }
    catch(Exception e)
    {
     failed++;completed++;
     progress.Record(mod.Name,"失败",e.Message,completed);
     stopAfterFailure=true;
     break;
    }
    finally
    {
     WorkshopModInstaller.Cleanup(prepared);
    }
   }

   while(completed<selected.Length)
   {
    var mod=selected[completed++];
    string reason=progress.CancelRequested?"用户停止了剩余任务。":
     stopAfterFailure?"前一项更新失败，已停止后续任务。":"未执行。";
    skipped++;
    progress.Record(mod.Name,progress.CancelRequested?"已取消":"未执行",reason,completed);
   }

   if(changed)
   {
    InvalidateLifecycle("创意工坊批量更新了 MOD，现有修复补丁健康结果需要重新检查。");
    report=null;states=[];ClearCoop();ClearSelection();installPreview=null;lastInstallResult=null;
    modInventory=await Task.Run(()=>ModManager.Scan(root));
    modInventoryDirty=false;
   }
   modStatus=$"创意工坊批量更新结束：成功更新 {updated} 项，内容一致 {unchanged} 项，失败 {failed} 项，跳过或未执行 {skipped} 项。";
  }
  catch(Exception e)
  {
   modStatus="创意工坊批量更新中断："+e.Message;
  }
  finally
  {
   transactionWriting=false;busy=false;
   progress.Finish(modStatus);
   ShowMods();
  }
 }

 async void BatchToggleMods(bool enable)
 {
  if(busy||transactionWriting||modInventoryDirty||selectedInstallRoot==null)return;
  var selected=ChosenMods();if(selected.Length==0)return;
  string root=selectedInstallRoot;
  var name=enable?"启用":"停用";
  busy=true;
  ModBatchReview[] reviews;
  try
  {
   reviews=await Task.Run(()=>selected.Select(mod=>{
    if(mod.Enabled==enable)return new ModBatchReview(mod.Name,"跳过","模组已经"+(enable?"启用":"停用")+"。");
    string? reason=ModManager.BlockReason(root,mod,modInventory!);
    if(reason!=null)
    {
     bool dependency=reason.StartsWith("这些启用项依赖它，请先停用依赖项：",StringComparison.Ordinal)
      ||reason.StartsWith("请先启用前置模组：",StringComparison.Ordinal);
     return new ModBatchReview(mod.Name,dependency?"需复核":"跳过",
      dependency?"依赖关系尚未满足，只有前置操作完成并重新校验后才能处理："+reason:reason);
    }
    try{ModManager.Preview(root,mod.Folder,!mod.Enabled);
     return new ModBatchReview(mod.Name,"可处理","初步校验通过；执行时仍将重新检查文件及依赖。");
    }catch(Exception e){return new ModBatchReview(mod.Name,"跳过",e.Message);}
   }).ToArray());
  }catch(Exception e){modStatus="批量预览失败："+e.Message;return;}
  finally{busy=false;}
  if(modInventoryDirty||selectedInstallRoot!=root){modStatus="模组环境发生变化，已取消旧预览。请刷新后重试。";ShowMods();return;}
  if(!ModBatchDialogs.Confirm(this,"批量"+name+" · 执行前预览",
   "预览不移动文件。依赖待复核项可能在其他模组处理后才能通过；每一项执行前仍会调用安全预检。操作成功的项不会因为后续失败自动回滚。",
   reviews))return;
  if(modInventoryDirty||busy||transactionWriting||selectedInstallRoot!=root){modStatus="执行前环境发生变化，请重新预览。";ShowMods();return;}
  var eligible=selected.Where((m,i)=>reviews[i].State!="跳过").ToArray();
  var result=new Dictionary<string,(string State,string Detail)>(StringComparer.OrdinalIgnoreCase);
  var errors=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  var progress=new ModBatchProgressWindow(this,"正在批量"+name,selected.Length);
  int success=0,completed=0;
  busy=true;transactionWriting=true;progress.Show();
  try
  {
   var pending=eligible.ToList();
   while(pending.Count>0&&!progress.CancelRequested)
   {
    int successesThisPass=0;
    var retry=new List<ManagedMod>();
    foreach(var mod in pending)
    {
     if(progress.CancelRequested)break;
     progress.Current(mod.Name,completed+1,selected.Length);
     try
     {
      await Task.Run(()=>{
       var plan=ModManager.Preview(root,mod.Folder,enable);
       ModManager.Apply(plan);
      });
      result[mod.Folder]=("成功","已"+name+"，文件由管理器安全移动和记录。");
      chosenModFolders.Remove(mod.Folder);errors.Remove(mod.Folder);
      success++;successesThisPass++;completed++;
      progress.Record(mod.Name,"成功","已"+name,completed);
     }
     catch(Exception e){retry.Add(mod);errors[mod.Folder]=e.Message;}
    }
    if(successesThisPass==0||progress.CancelRequested)break;
    pending=retry;
   }
   foreach(var mod in selected)
   {
    if(result.ContainsKey(mod.Folder))continue;
    int index=Array.IndexOf(selected,mod);
    string reason=reviews[index].State=="跳过"?reviews[index].Explanation:
     progress.CancelRequested?"用户停止了剩余任务。":
     errors.TryGetValue(mod.Folder,out var e)?e:"操作未完成，请重新检查环境。";
    string state=progress.CancelRequested&&reviews[index].State!="跳过"&&!errors.ContainsKey(mod.Folder)
     ?"已取消":"跳过";
    result[mod.Folder]=(state,reason);completed++;
    progress.Record(mod.Name,state,reason,completed);
   }
   modInventory=await Task.Run(()=>ModManager.Scan(root));
   modInventoryDirty=false;InvalidateLifecycle();ClearSelection();ClearCoop();
   report=null;states=[];installPreview=null;lastInstallResult=null;
   modStatus=$"批量{name}结束：成功 {success} 项，跳过 {selected.Length-success} 项。可在结果窗口查看每个模组的处理情况。";
  }
  catch(Exception e){modStatus="批量操作中断："+e.Message;}
  finally
  {
   transactionWriting=false;busy=false;
   progress.Finish(modStatus);
   ShowMods();
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
  if(UiThemes.IsClassic)ShowModsClassic();else ShowModsWorkspace();
 }
 void ShowModsClassic()
 {
  var root=new Grid();
  root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
  root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
  root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
  bool isFixSection=modSection=="修复补丁";
  var header=V(Header("LOCAL MODS",isFixSection?"本地修复补丁":"模组管理",
   isFixSection?"独立修复补丁集中管理；启停需要预览。":"管理已安装 MOD；点击名称查看详情。"));
  root.Children.Add(header);Space(header,9);
  if(isFixSection){header.Children.Add(PatchCenterTabs("本地补丁"));Space(header,2);}
  if(selectedInstallRoot==null)
  {
   header.Children.Add(Card(V(T("请先定位游戏目录",17,ink,true),
    H(Btn("自动定位目录",LocateGame,"Primary"),Btn("手动选择目录",ChooseFolder,"Ghost")))));
   SetPage("模组管理",root,restore:ShowMods);return;
  }
  if(!isFixSection)header.Children.Add(ModWorkspaceTabs("已安装"));
  var tools=new WrapPanel{Margin=new Thickness(0,0,0,4)};
  var refresh=Btn("刷新模组",RefreshMods,"Ghost");refresh.IsEnabled=!busy;
  refresh.Margin=new Thickness(0,0,7,4);tools.Children.Add(refresh);
  if(!isFixSection)
  {
   var import=Btn("导入 ZIP",ChooseWorkshopZip,"Ghost");
   import.Margin=new Thickness(0,0,7,4);tools.Children.Add(import);
  }
  var folderButton=Btn("打开目录",()=>{
   string directory=Path.Combine(selectedInstallRoot,"mods");
   if(Directory.Exists(directory))OpenUrl(directory);else toast.Text="此目录尚未创建 mods 文件夹。";
  },"Ghost");
  tools.Children.Add(folderButton);header.Children.Add(tools);
  var statusSummary=T(modStatus,11,muted);
  statusSummary.TextWrapping=TextWrapping.NoWrap;
  statusSummary.TextTrimming=TextTrimming.CharacterEllipsis;statusSummary.ToolTip=modStatus;
  header.Children.Add(statusSummary);Space(header,7);
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
  chosenModFolders.RemoveWhere(path=>!mods.Any(m=>m.Folder.Equals(path,StringComparison.OrdinalIgnoreCase)));
  int fixCount=mods.Count(ModPresentation.IsIndependentPatch),normalCount=mods.Count-fixCount;
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

  var searchArea=new Grid{Margin=new Thickness(0,0,0,6)};
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
  var filterBar=new DockPanel{LastChildFill=false,Margin=new Thickness(0,0,0,5)};
  var shownCount=T("",12,muted);shownCount.VerticalAlignment=VerticalAlignment.Center;
  DockPanel.SetDock(shownCount,Dock.Right);filterBar.Children.Add(shownCount);
  var filters=new WrapPanel();
  foreach(var name in new[]{"全部","启用","停用","有更新","有问题","依赖异常"})
  {
   string selected=name;
   var button=Btn(name,()=>{CaptureModListOffset();modListOffset=0;modFilter=selected;ShowMods();},
    modFilter==name?"SelectedTab":"Ghost");
   button.Margin=new Thickness(0,0,7,0);filters.Children.Add(button);
  }
  filterBar.Children.Add(filters);header.Children.Add(filterBar);
  var selectionBar=new WrapPanel{Margin=new Thickness(0,3,0,8)};
  var checkAll=new CheckBox{Content="全选当前结果",Margin=new Thickness(0,7,12,0)};
  var selectionCount=T("",13,ink,true);selectionCount.Margin=new Thickness(0,7,12,0);
  selectionBar.Children.Add(checkAll);selectionBar.Children.Add(selectionCount);
  var findBatch=Btn("查找 GitHub 来源",BatchFindGitHub,"Ghost");
  var checkBatch=Btn("检查 GitHub 更新",BatchCheckGitHub,"Ghost");
  var workshopCheckBatch=Btn("检查创意工坊更新",BatchCheckWorkshop,"Ghost");
  var workshopUpdateBatch=Btn("批量更新创意工坊",BatchUpdateWorkshop,"Ghost");
  var enableBatch=Btn("批量启用",()=>BatchToggleMods(true),"Ghost");
  var disableBatch=Btn("批量停用",()=>BatchToggleMods(false),"Ghost");
  var clearBatch=Btn("取消选择",()=>{chosenModFolders.Clear();ShowMods();},"Ghost");
  foreach(var button in new[]{findBatch,checkBatch,workshopCheckBatch,workshopUpdateBatch,enableBatch,disableBatch,clearBatch}){
   button.Padding=new Thickness(9,7,9,7);button.Margin=new Thickness(0,0,6,4);
   selectionBar.Children.Add(button);
  }
  header.Children.Add(selectionBar);

  var dependencyReport=modInventory==null?null:ModDependencyGraph.Analyze(modInventory);
  var rows=modInventory==null?[]:ModListView.Rows(selectedInstallRoot,modInventory,LifecycleTag,mod=>{
   if(ModPresentation.IsIndependentPatch(mod))return "";
   var context=new List<string>();
   var updates=GitHubUpdates();
   if(updates.Repository(mod.Id)==null)
   {
    var candidates=Discovery().Cached(mod);
    if(candidates!=null)
     context.Add(candidates.Candidates.Length>0
      ?"GitHub："+(candidates.Candidates.Any(c=>c.Verified)?"已核验来源":"疑似来源")+
       " "+candidates.Candidates.Length+" 个 · 待确认"
      :candidates.Retryable?"GitHub：查找受限，可重试":"GitHub：暂无来源");
   }
   else
   {
    var result=updates.Cached(mod.Id);
    context.Add(result==null?"GitHub：已关联 · 待检查":
     "GitHub："+result.Status+(result.LatestVersion.Length>0?" · "+result.LatestVersion:""));
   }
   if(WorkshopSources().Get(mod.Id).HasValue)
   {
    var workshopResult=WorkshopUpdates().Cached(mod.Id);
    context.Add(workshopResult==null?"创意工坊：已关联 · 待检查":"创意工坊："+workshopResult.Status);
   }
   return string.Join(" · ",context);
  },mod=>dependencyReport?.Summary(mod.Id).Label??"未分析",
   mod=>dependencyReport?.Summary(mod.Id).HasIssue??false);
  ListBox list=null!;
  var empty=T("没有符合条件的 MOD。请修改搜索或筛选条件。",12,muted);
  int cardColumns=2;
  var workspace=new Grid();
  workspace.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
  workspace.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(0)});
  workspace.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(0)});
  var detailHost=new ContentControl{Visibility=Visibility.Collapsed};
  void UpdateDetail()
  {
   var mod=modInventory?.Mods.FirstOrDefault(m=>m.Folder==selectedModDetailFolder);
   if(mod==null||ModPresentation.IsIndependentPatch(mod)!=isFixSection)
   {
    detailHost.Visibility=Visibility.Collapsed;detailHost.Content=null;return;
   }
   var report=dependencyReport?.Summary(mod.Id);
   var detail=V(T("模组详情",17,ink,true),T(mod.Name,15,ink,true),
    T("模组 ID："+mod.Id,12,muted),T("版本号："+mod.Version,12,muted),
    T("作者："+mod.Author,12,muted),T("状态："+(mod.Enabled?"已启用":"已停用"),12,muted),
    T("依赖情况："+(report?.Label??"未分析"),12,report?.HasIssue==true?gold:muted));
   Space(detail,10);
   var actions=new WrapPanel();
   var selected=rows.FirstOrDefault(r=>r.Mod.Folder==mod.Folder);
   var preview=Btn(mod.Enabled?"预览停用":"预览启用",()=>PrepareModToggle(mod),"Ghost");
   preview.IsEnabled=!busy&&!transactionWriting&&!modInventoryDirty&&selected?.BlockReason==null;
   preview.ToolTip=selected?.BlockReason;preview.Margin=new Thickness(0,0,7,7);
   actions.Children.Add(preview);
   actions.Children.Add(Btn("完整详情",()=>{CaptureModListOffset();ShowModDetail(mod.Folder);},"Primary"));
   detail.Children.Add(actions);Space(detail,7);
   detail.Children.Add(Btn("关闭详情",()=>{selectedModDetailFolder=null;UpdateDetail();ResizeDetail();},"Ghost"));
   detailHost.Content=new Border{
    Background=SpireGlassTheme.GlassPanel("#C01A2D48"),
    BorderBrush=SpireGlassTheme.PanelStroke(),BorderThickness=new Thickness(1),
    CornerRadius=new CornerRadius(12),Padding=new Thickness(12),
    Child=new ScrollViewer{Content=detail,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}};
   detailHost.Visibility=Visibility.Visible;
  }
  list=ModCardList.Create(row=>{
   CaptureModListOffset();
   if(workspace.ActualWidth<820){ShowModDetail(row.Mod.Folder);return;}
   selectedModDetailFolder=row.Mod.Folder;UpdateDetail();ResizeDetail();
  },(row,button)=>{
   var menu=new ContextMenu{PlacementTarget=button};
   var toggle=new MenuItem{Header=row.Mod.Enabled?"预览停用":"预览启用",
    IsEnabled=!busy&&!transactionWriting&&!modInventoryDirty&&row.BlockReason==null,
    ToolTip=row.BlockReason};
   toggle.Click+=(_,_)=>PrepareModToggle(row.Mod);menu.Items.Add(toggle);
   var folder=new MenuItem{Header="打开文件夹"};
   folder.Click+=(_,_)=>OpenUrl(row.Mod.Folder);menu.Items.Add(folder);
   if(!row.IsFix){
    var find=new MenuItem{Header="自动查找 GitHub 来源",IsEnabled=!busy};
    find.Click+=(_,_)=>DiscoverGitHubMod(row.Mod);menu.Items.Add(find);
    var bind=new MenuItem{Header="关联 / 修改 GitHub 来源",IsEnabled=!busy};
    bind.Click+=(_,_)=>BindGitHubMod(row.Mod);menu.Items.Add(bind);
   }
   var detail=new MenuItem{Header="查看完整详情"};
   detail.Click+=(_,_)=>ShowModDetail(row.Mod.Folder);menu.Items.Add(detail);
   button.ContextMenu=menu;menu.IsOpen=true;
  },row=>{if(!busy&&!transactionWriting&&!modInventoryDirty)PrepareModToggle(row.Mod);},
  row=>{
   if(busy||transactionWriting||modInventoryDirty)return;
   if(row.IsFix){ShowModDetail(row.Mod.Folder);return;}
   var updates=GitHubUpdates();
   if(updates.Repository(row.Mod.Id)==null)DiscoverGitHubMod(row.Mod);
   else if(updates.Cached(row.Mod.Id)?.Status=="有更新")UpdateGitHubMod(row.Mod);
   else CheckGitHubMod(row.Mod);
  },(row,value)=>{
   if(value)chosenModFolders.Add(row.Mod.Folder);else chosenModFolders.Remove(row.Mod.Folder);
   row.Selected=value;UpdateSelectionToolbar();
  });
  activeModList=list;
  workspace.Children.Add(list);Grid.SetColumn(detailHost,2);workspace.Children.Add(detailHost);
  void ResizeDetail()
  {
   bool show=detailHost.Content!=null&&workspace.ActualWidth>=820;
   detailHost.Visibility=show?Visibility.Visible:Visibility.Collapsed;
   workspace.ColumnDefinitions[1].Width=new GridLength(show?12:0);
   workspace.ColumnDefinitions[2].Width=new GridLength(show?290:0);
  }
  workspace.SizeChanged+=(_,_)=>ResizeDetail();
  Grid.SetRow(workspace,1);root.Children.Add(workspace);
  var footer=V(empty);
  Grid.SetRow(footer,2);root.Children.Add(footer);
  void UpdateSelectionToolbar()
  {
   int count=ChosenMods().Length;
   int visibleSelected=ModListView.Filter(rows,modSection,modFilter,modQuery)
    .Count(r=>chosenModFolders.Contains(r.Mod.Folder));
   selectionCount.Text=count==0?"":$"已选 {count} 项 · 当前显示 {visibleSelected} 项"+
    (count>visibleSelected?$" · 筛选隐藏 {count-visibleSelected} 项":"");
   foreach(var button in new[]{findBatch,checkBatch,workshopCheckBatch,workshopUpdateBatch,enableBatch,disableBatch,clearBatch})
    button.Visibility=count>0?Visibility.Visible:Visibility.Collapsed;
   bool hasWorkshopSource=ChosenMods().Any(m=>!ModPresentation.IsIndependentPatch(m)&&WorkshopSources().Get(m.Id).HasValue);
   bool hasWorkshopUpdate=ChosenMods().Any(m=>!ModPresentation.IsIndependentPatch(m)&&m.Enabled&&
    WorkshopSources().Get(m.Id).HasValue&&WorkshopUpdates().Cached(m.Id)?.Status=="有更新");
   findBatch.IsEnabled=checkBatch.IsEnabled=enableBatch.IsEnabled=disableBatch.IsEnabled=
    count>0&&!busy&&!transactionWriting&&!modInventoryDirty;
   workshopCheckBatch.IsEnabled=hasWorkshopSource&&!busy&&!transactionWriting&&!modInventoryDirty;
   workshopUpdateBatch.IsEnabled=hasWorkshopUpdate&&!busy&&!transactionWriting&&!modInventoryDirty;
   var visible=ModListView.Filter(rows,modSection,modFilter,modQuery);
   checkAll.Checked-=OnCheckAll;checkAll.Unchecked-=OnCheckAll;
   checkAll.IsChecked=visible.Length>0&&visible.All(r=>chosenModFolders.Contains(r.Mod.Folder));
   checkAll.Checked+=OnCheckAll;checkAll.Unchecked+=OnCheckAll;
  }
  void OnCheckAll(object sender,RoutedEventArgs e)
  {
   foreach(var row in ModListView.Filter(rows,modSection,modFilter,modQuery))
    if(checkAll.IsChecked==true)chosenModFolders.Add(row.Mod.Folder);
    else chosenModFolders.Remove(row.Mod.Folder);
   FilterRows();
  }
  void FilterRows()
  {
   var visible=ModListView.Filter(rows,modSection,modFilter,modQuery);
   foreach(var row in rows)row.Selected=chosenModFolders.Contains(row.Mod.Folder);
   ModCardList.SetItems(list,visible,cardColumns);
   UpdateSelectionToolbar();
   shownCount.Text=$"显示 {visible.Length} / {(modSection=="修复补丁"?fixCount:normalCount)} 项";
   empty.Visibility=visible.Length==0&&!busy?Visibility.Visible:Visibility.Collapsed;
   if(selectedModDetailFolder!=null&&!visible.Any(row=>
       row.Mod.Folder.Equals(selectedModDetailFolder,StringComparison.OrdinalIgnoreCase)))
   {
    selectedModDetailFolder=null;UpdateDetail();ResizeDetail();
   }
  }
  search.TextChanged+=(_,_)=>{
   modQuery=search.Text;
   placeholder.Visibility=string.IsNullOrEmpty(modQuery)?Visibility.Visible:Visibility.Collapsed;
   modListOffset=0;
   FilterRows();ListScroll(list)?.ScrollToTop();
   string q=modQuery,f=modFilter,g=modSection;
   RememberPage("模组管理",()=>{modQuery=q;modFilter=f;modSection=g;ShowMods();});
  };
  FilterRows();UpdateDetail();ResizeDetail();
  list.SizeChanged+=(_,_)=>{
   int next=ModListView.ColumnCount(list.ActualWidth);
   if(next!=cardColumns){cardColumns=next;FilterRows();}
  };
  list.Loaded+=(_,_)=>Dispatcher.BeginInvoke(new Action(()=>{
   if(activeModList==list&&ListScroll(list) is { } scroll)scroll.ScrollToVerticalOffset(modListOffset);
  }),System.Windows.Threading.DispatcherPriority.Loaded);
  string savedQuery=modQuery,savedFilter=modFilter,savedSection=modSection;
  SetPage(isFixSection?"本地补丁":"模组管理",root,restore:()=>{
   modQuery=savedQuery;modFilter=savedFilter;modSection=savedSection;ShowMods();
  });
 }
 void ShowModsWorkspace()
 {
  var root=new Grid();
  root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
  root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
  root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
  bool isFixSection=modSection=="修复补丁";
  if(modFilter is "有更新" or "有问题" or "依赖异常")
  {
   modIssueFilter=modFilter;modFilter="全部";
  }
  var header=V();
  root.Children.Add(header);
  string title=isFixSection?"本地修复补丁":"模组管理";
  string description=isFixSection?
   "集中管理独立修复补丁，启停前提供安全预览。":
   "管理已安装 MOD，点击卡片查看详情；勾选后可进行批量操作。";
  if(selectedInstallRoot==null)
  {
   header.Children.Add(ModWorkspaceHeader(title,description));
   header.Children.Add(Card(V(T("请先定位游戏目录",17,ink,true),
    H(Btn("自动定位目录",LocateGame,"Primary"),Btn("手动选择目录",ChooseFolder,"Ghost")))));
   SetPage(isFixSection?"本地补丁":"模组管理",root,restore:ShowMods);return;
  }
  var toolbarActions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,
   VerticalAlignment=VerticalAlignment.Center};
  var refresh=Btn("↻  刷新",RefreshMods,"ModToolbarAction");
  refresh.IsEnabled=!busy;refresh.Margin=new Thickness(0,0,5,0);
  toolbarActions.Children.Add(refresh);
  if(!isFixSection)
  {
   var import=Btn("＋  导入 ZIP",ChooseWorkshopZip,"ModToolbarPrimary");
   import.Margin=new Thickness(0,0,5,0);import.IsEnabled=!busy&&!transactionWriting;
   toolbarActions.Children.Add(import);
  }
  var moreTools=new Button{Content="⋯",FontSize=19,MinWidth=34,MinHeight=33,
   ToolTip="更多工作区工具"};
  if(FindResource("ModToolbarAction") is Style toolStyle)moreTools.Style=toolStyle;
  var toolsMenu=new ContextMenu();
  var openFolder=new MenuItem{Header="打开模组目录"};
  openFolder.Click+=(_,_)=>{
   string directory=Path.Combine(selectedInstallRoot,"mods");
   if(Directory.Exists(directory))OpenUrl(directory);else toast.Text="此目录尚未创建 mods 文件夹。";
  };
  toolsMenu.Items.Add(openFolder);
  if(isFixSection)
  {
   toolsMenu.Items.Add(new Separator());
   var organize=new MenuItem{Header="整理独立补丁",IsEnabled=!busy&&!transactionWriting};
   organize.Click+=(_,_)=>PreparePatchOrganization();toolsMenu.Items.Add(organize);
   var health=new MenuItem{Header="重新检查健康状态",IsEnabled=!busy&&!transactionWriting};
   health.Click+=(_,_)=>RefreshLifecycleFromMods();toolsMenu.Items.Add(health);
   var autoStop=new MenuItem{Header="删除目标 MOD 后自动停用补丁："+(OrphanPatchAutoStop.Enabled(installer.DataRoot)?"开启":"关闭")};
   autoStop.Click+=(_,_)=>{
    try
    {
     bool old=OrphanPatchAutoStop.Enabled(installer.DataRoot);
     OrphanPatchAutoStop.SetEnabled(installer.DataRoot,!old);
     orphanPatchNotice=old?"已关闭自动停用；之后只手动管理修复补丁。":
      "已开启自动停用；完整扫描会记录 MOD 环境，删除目标时自动核对并停用对应补丁。";
     ShowMods();
     if(!old)RefreshMods();
    }
    catch(Exception ex){toast.Text="设置保存失败："+ex.Message;}
   };
   toolsMenu.Items.Add(autoStop);
  }
  moreTools.ContextMenu=toolsMenu;
  moreTools.Click+=(_,_)=>{toolsMenu.PlacementTarget=moreTools;toolsMenu.IsOpen=true;};
  toolbarActions.Children.Add(moreTools);
  header.Children.Add(ModWorkspaceHeader(title,description,toolbarActions));
  header.Children.Add(isFixSection?PatchCenterTabs("本地补丁"):ModWorkspaceTabs("已安装"));
  if(isFixSection)
  {
   var healthLine=T(HasValidLifecycleReport
    ?"健康最近检查："+lifecycleReport!.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
    :lifecycleStale?"文件变化后健康状态待重新检查":"补丁健康尚未检查",11,muted);
   healthLine.Margin=new Thickness(0,0,0,7);
   header.Children.Add(healthLine);
  }
  if(!modStatus.StartsWith("共 ",StringComparison.Ordinal))
  {
   var operation=T(modStatus,11,muted);
   operation.TextTrimming=TextTrimming.CharacterEllipsis;
   operation.TextWrapping=TextWrapping.NoWrap;
   operation.ToolTip=modStatus;
   header.Children.Add(operation);
  }
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
  chosenModFolders.RemoveWhere(path=>!mods.Any(m=>m.Folder.Equals(path,StringComparison.OrdinalIgnoreCase)));
  int fixCount=mods.Count(ModPresentation.IsIndependentPatch),normalCount=mods.Count-fixCount;
  if(modInventory?.Issues.Count>0)
  {
   header.Children.Add(Card(V(T("部分 MOD 清单需要处理",14,gold,true),
    T(string.Join("\n",modInventory.Issues.Take(3)),12,muted)),padding:12));
   Space(header,8);
  }

  ListBox list=null!;
  var searchArea=new Grid{Margin=new Thickness(0,3,0,9)};
  var search=new TextBox{Text=modQuery,MinHeight=39,ToolTip="按 MOD 名称、ID、作者搜索"};
  search.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,"搜索模组");
  searchArea.Children.Add(search);
  var placeholder=T("⌕   搜索名称、MOD ID 或作者…",12,muted);
  placeholder.Margin=new Thickness(12,0,0,0);
  placeholder.VerticalAlignment=VerticalAlignment.Center;
  placeholder.IsHitTestVisible=false;
  placeholder.Visibility=string.IsNullOrEmpty(modQuery)?Visibility.Visible:Visibility.Collapsed;
  searchArea.Children.Add(placeholder);
  header.Children.Add(searchArea);

  var filterBar=new DockPanel{LastChildFill=false,Margin=new Thickness(0,0,0,8)};
  var shownCount=T("",11,muted);
  shownCount.VerticalAlignment=VerticalAlignment.Center;shownCount.Margin=new Thickness(9,0,0,0);
  DockPanel.SetDock(shownCount,Dock.Right);filterBar.Children.Add(shownCount);

  var compactFilters=new WrapPanel{VerticalAlignment=VerticalAlignment.Center};
  var segments=new StackPanel{Orientation=Orientation.Horizontal};
  var segmentButtons=new Dictionary<string,Button>();
  foreach(var name in new[]{"全部","启用","停用"})
  {
   var segment=Btn(name,()=>{},modFilter==name?"ModSegmentActive":"ModSegment");
   segment.Margin=new Thickness(1,0,1,0);
   segmentButtons[name]=segment;segments.Children.Add(segment);
  }
  var segmentHost=new Border{Background=B("#68304760"),CornerRadius=new CornerRadius(9),
   Padding=new Thickness(3),Child=segments,Margin=new Thickness(0,0,10,3)};
  compactFilters.Children.Add(segmentHost);

  var attentionButton=Btn(modIssueFilter=="所有状态"?"所有状态  ▾":modIssueFilter+"  ▾",()=>{},"ModToolbarAction");
  attentionButton.Margin=new Thickness(0,0,10,3);
  var attentionMenu=new ContextMenu();
  foreach(var option in new[]{"所有状态","有更新","有问题","依赖异常"})
  {
   var item=new MenuItem{Header=option,IsCheckable=true,IsChecked=modIssueFilter==option};
   attentionMenu.Items.Add(item);
  }
  attentionButton.ContextMenu=attentionMenu;
  attentionButton.Click+=(_,_)=>{attentionMenu.PlacementTarget=attentionButton;attentionMenu.IsOpen=true;};
  compactFilters.Children.Add(attentionButton);

  var checkAll=new CheckBox{Content="全选当前结果",Margin=new Thickness(0,0,0,3),
   VerticalAlignment=VerticalAlignment.Center};
  if(FindResource("ModCardCheckBox") is Style checkAllStyle)checkAll.Style=checkAllStyle;
  compactFilters.Children.Add(checkAll);
  filterBar.Children.Add(compactFilters);header.Children.Add(filterBar);

  // Batch tools occupy the bottom rail only after a selection, not a permanent header row.
  var selectionBar=new WrapPanel{Margin=new Thickness(9,6,9,6),VerticalAlignment=VerticalAlignment.Center};
  var selectionCount=T("",12,ink,true);
  selectionCount.Margin=new Thickness(0,7,14,0);
  selectionBar.Children.Add(selectionCount);
  var enableBatch=Btn("启用",()=>BatchToggleMods(true),"ModToolbarPrimary");
  var disableBatch=Btn("停用",()=>BatchToggleMods(false),"ModToolbarAction");
  var clearBatch=Btn("取消选择",()=>{chosenModFolders.Clear();ShowMods();},"ModToolbarAction");
  foreach(var b in new[]{enableBatch,disableBatch,clearBatch})
  {b.Margin=new Thickness(0,0,6,0);selectionBar.Children.Add(b);}
  var moreBatch=Btn("更多操作  ▾",()=>{},"ModToolbarAction");
  var batchMenu=new ContextMenu();
  var findBatch=new MenuItem{Header="查找 GitHub 来源"};
  findBatch.Click+=(_,_)=>BatchFindGitHub();
  var checkBatch=new MenuItem{Header="检查 GitHub 更新"};
  checkBatch.Click+=(_,_)=>BatchCheckGitHub();
  var workshopCheckBatch=new MenuItem{Header="检查创意工坊更新"};
  workshopCheckBatch.Click+=(_,_)=>BatchCheckWorkshop();
  var workshopUpdateBatch=new MenuItem{Header="批量更新创意工坊"};
  workshopUpdateBatch.Click+=(_,_)=>BatchUpdateWorkshop();
  foreach(var item in new[]{findBatch,checkBatch,workshopCheckBatch,workshopUpdateBatch})
   batchMenu.Items.Add(item);
  moreBatch.ContextMenu=batchMenu;
  moreBatch.Click+=(_,_)=>{batchMenu.PlacementTarget=moreBatch;batchMenu.IsOpen=true;};
  selectionBar.Children.Insert(selectionBar.Children.Count-1,moreBatch);
  var bulkHost=new Border{Background=SpireGlassTheme.GlassPanel("#D5213852"),
   BorderBrush=SpireGlassTheme.PanelStroke(),BorderThickness=new Thickness(1),
   CornerRadius=new CornerRadius(10),Child=selectionBar,
   Visibility=Visibility.Collapsed,Margin=new Thickness(0,6,0,0)};
  var dependencyReport=modInventory==null?null:ModDependencyGraph.Analyze(modInventory);
  var rows=modInventory==null?[]:ModListView.Rows(selectedInstallRoot,modInventory,LifecycleTag,mod=>{
   if(ModPresentation.IsIndependentPatch(mod))return "";
   var context=new List<string>();
   var updates=GitHubUpdates();
   if(updates.Repository(mod.Id)==null)
   {
    var candidates=Discovery().Cached(mod);
    if(candidates!=null)
     context.Add(candidates.Candidates.Length>0
      ?"GitHub："+(candidates.Candidates.Any(c=>c.Verified)?"已核验来源":"疑似来源")+
       " "+candidates.Candidates.Length+" 个 · 待确认"
      :candidates.Retryable?"GitHub：查找受限，可重试":"GitHub：暂无来源");
   }
   else
   {
    var result=updates.Cached(mod.Id);
    context.Add(result==null?"GitHub：已关联 · 待检查":
     "GitHub："+result.Status+(result.LatestVersion.Length>0?" · "+result.LatestVersion:""));
   }
   if(WorkshopSources().Get(mod.Id).HasValue)
   {
    var workshopResult=WorkshopUpdates().Cached(mod.Id);
    context.Add(workshopResult==null?"创意工坊：已关联 · 待检查":"创意工坊："+workshopResult.Status);
   }
   return string.Join(" · ",context);
  },mod=>dependencyReport?.Summary(mod.Id).Label??"未分析",
   mod=>dependencyReport?.Summary(mod.Id).HasIssue??false);
  var empty=T("没有符合条件的 MOD。请修改搜索或筛选条件。",12,muted);
  int cardColumns=2;
  var workspace=new Grid();
  workspace.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
  workspace.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(0)});
  workspace.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(0)});
  var detailHost=new ContentControl{Visibility=Visibility.Collapsed};
  void UpdateDetail()
  {
   var mod=modInventory?.Mods.FirstOrDefault(m=>m.Folder==selectedModDetailFolder);
   if(mod==null||ModPresentation.IsIndependentPatch(mod)!=isFixSection)
   {
    detailHost.Visibility=Visibility.Collapsed;detailHost.Content=null;return;
   }
   var report=dependencyReport?.Summary(mod.Id);
   var panelHeader=new DockPanel{LastChildFill=true};
   var close=Btn("✕",()=>{selectedModDetailFolder=null;UpdateDetail();ResizeDetail();},"ModToolbarAction");
   close.ToolTip="关闭快捷详情（Esc）";close.MinWidth=32;
   DockPanel.SetDock(close,Dock.Right);panelHeader.Children.Add(close);
   panelHeader.Children.Add(T("模组详情",17,ink,true));
   var detail=V(panelHeader,T(mod.Name,16,ink,true),
    T("模组 ID："+mod.Id,12,muted),T("版本号："+mod.Version,12,muted),
    T("作者："+mod.Author,12,muted),T("状态："+(mod.Enabled?"已启用":"已停用"),12,muted),
    T("依赖情况："+(report?.Label??"未分析"),12,report?.HasIssue==true?gold:muted));
   if(!isFixSection)
   {
    var selectedRow=rows.FirstOrDefault(r=>r.Mod.Folder==mod.Folder);
    if(selectedRow!=null&&!string.IsNullOrWhiteSpace(selectedRow.GitHubText))
     detail.Children.Add(T("来源与更新："+selectedRow.GitHubText,12,muted));
   }
   Space(detail,10);
   var actions=new WrapPanel();
   var selected=rows.FirstOrDefault(r=>r.Mod.Folder==mod.Folder);
   var preview=Btn(mod.Enabled?"预览停用":"预览启用",()=>PrepareModToggle(mod),"Ghost");
   preview.IsEnabled=!busy&&!transactionWriting&&!modInventoryDirty&&selected?.BlockReason==null;
   preview.ToolTip=selected?.BlockReason;preview.Margin=new Thickness(0,0,7,7);
   actions.Children.Add(preview);
   if(!isFixSection)
   {
    ulong? currentWorkshopId=WorkshopSources().Get(mod.Id);
    detail.Children.Add(T(currentWorkshopId.HasValue?
     "Workshop ID："+currentWorkshopId.Value:
     "创意工坊：尚未关联",12,muted));
    var manual=Btn(currentWorkshopId.HasValue?"修改 Workshop 关联":"手动关联 Workshop ID",
     ()=>BindWorkshopMod(mod),"ModToolbarAction");
    manual.Margin=new Thickness(0,0,7,7);
    actions.Children.Add(manual);
    var github=GitHubUpdates();
    var source=github.Repository(mod.Id);
    var cached=github.Cached(mod.Id);
    var update=Btn(source==null?"查找 GitHub 来源":cached?.Status=="有更新"?"下载并更新":"检查更新",()=>{
     if(busy||transactionWriting||modInventoryDirty)return;
     if(source==null)DiscoverGitHubMod(mod);
     else if(cached?.Status=="有更新")UpdateGitHubMod(mod);
     else CheckGitHubMod(mod);
    },"ModToolbarPrimary");
    update.IsEnabled=!busy&&!transactionWriting&&!modInventoryDirty;
    update.Margin=new Thickness(0,0,7,7);actions.Children.Add(update);
   }
   var full=Btn("完整详情",()=>{CaptureModListOffset();ShowModDetail(mod.Folder);},"ModToolbarAction");
   full.Margin=new Thickness(0,0,7,7);actions.Children.Add(full);
   detail.Children.Add(actions);Space(detail,10);
   detail.Children.Add(Btn("打开模组目录",()=>OpenUrl(mod.Folder),"ModToolbarAction"));
   
   detailHost.Content=new Border{
    Background=SpireGlassTheme.GlassPanel("#EE172A43"),
    BorderBrush=SpireGlassTheme.PanelStroke(),BorderThickness=new Thickness(1),
    CornerRadius=new CornerRadius(13),Padding=new Thickness(15),
    Effect=new System.Windows.Media.Effects.DropShadowEffect{
     Color=System.Windows.Media.Colors.Black,BlurRadius=22,ShadowDepth=3,Opacity=.42},
    Child=new ScrollViewer{Content=detail,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}};
   detailHost.Visibility=Visibility.Visible;
  }
  list=ModCardList.Create(row=>{
   CaptureModListOffset();
   if(workspace.ActualWidth<540){ShowModDetail(row.Mod.Folder);return;}
   selectedModDetailFolder=row.Mod.Folder;UpdateDetail();ResizeDetail();
  },(row,button)=>{
   var menu=new ContextMenu{PlacementTarget=button};
   var toggle=new MenuItem{Header=row.Mod.Enabled?"预览停用":"预览启用",
    IsEnabled=!busy&&!transactionWriting&&!modInventoryDirty&&row.BlockReason==null,
    ToolTip=row.BlockReason};
   toggle.Click+=(_,_)=>PrepareModToggle(row.Mod);menu.Items.Add(toggle);
   var folder=new MenuItem{Header="打开文件夹"};
   folder.Click+=(_,_)=>OpenUrl(row.Mod.Folder);menu.Items.Add(folder);
   if(!row.IsFix){
    var find=new MenuItem{Header="自动查找 GitHub 来源",IsEnabled=!busy};
    find.Click+=(_,_)=>DiscoverGitHubMod(row.Mod);menu.Items.Add(find);
    var bind=new MenuItem{Header="关联 / 修改 GitHub 来源",IsEnabled=!busy};
    bind.Click+=(_,_)=>BindGitHubMod(row.Mod);menu.Items.Add(bind);
   }
   var detail=new MenuItem{Header="查看完整详情"};
   detail.Click+=(_,_)=>ShowModDetail(row.Mod.Folder);menu.Items.Add(detail);
   button.ContextMenu=menu;menu.IsOpen=true;
  },row=>{if(!busy&&!transactionWriting&&!modInventoryDirty)PrepareModToggle(row.Mod);},
  row=>{
   if(busy||transactionWriting||modInventoryDirty)return;
   if(row.IsFix){ShowModDetail(row.Mod.Folder);return;}
   var updates=GitHubUpdates();
   if(updates.Repository(row.Mod.Id)==null)DiscoverGitHubMod(row.Mod);
   else if(updates.Cached(row.Mod.Id)?.Status=="有更新")UpdateGitHubMod(row.Mod);
   else CheckGitHubMod(row.Mod);
  },(row,value)=>{
   if(value)chosenModFolders.Add(row.Mod.Folder);else chosenModFolders.Remove(row.Mod.Folder);
   row.Selected=value;UpdateSelectionToolbar();
  });
  activeModList=list;
  workspace.Children.Add(list);
  detailHost.HorizontalAlignment=HorizontalAlignment.Right;
  detailHost.VerticalAlignment=VerticalAlignment.Stretch;
  Panel.SetZIndex(detailHost,10);
  workspace.Children.Add(detailHost);
  void ResizeDetail()
  {
   bool show=detailHost.Content!=null&&workspace.ActualWidth>=540;
   bool dock=show&&workspace.ActualWidth>=1400;
   detailHost.Visibility=show?Visibility.Visible:Visibility.Collapsed;
   detailHost.Width=show?(dock?335:330):0;
   workspace.ColumnDefinitions[1].Width=new GridLength(dock?12:0);
   workspace.ColumnDefinitions[2].Width=new GridLength(dock?335:0);
   Grid.SetColumn(detailHost,dock?2:0);
  }
  workspace.SizeChanged+=(_,_)=>ResizeDetail();
  workspace.PreviewKeyDown+=(_,e)=>{
   if(e.Key==System.Windows.Input.Key.Escape&&selectedModDetailFolder!=null)
   {
    selectedModDetailFolder=null;UpdateDetail();ResizeDetail();e.Handled=true;
   }
  };
  Grid.SetRow(workspace,1);root.Children.Add(workspace);
  var footer=V(empty,bulkHost);
  Grid.SetRow(footer,2);root.Children.Add(footer);
  void UpdateSelectionToolbar()
  {
   int count=ChosenMods().Length;
   int visibleSelected=ModListView.Filter(rows,modSection,modFilter,modQuery,modIssueFilter)
    .Count(r=>chosenModFolders.Contains(r.Mod.Folder));
   selectionCount.Text=count==0?"":$"已选 {count} 项 · 当前显示 {visibleSelected} 项"+
    (count>visibleSelected?$" · 筛选隐藏 {count-visibleSelected} 项":"");
   bulkHost.Visibility=count>0?Visibility.Visible:Visibility.Collapsed;
   bool hasWorkshopSource=ChosenMods().Any(m=>!ModPresentation.IsIndependentPatch(m)&&WorkshopSources().Get(m.Id).HasValue);
   bool hasWorkshopUpdate=ChosenMods().Any(m=>!ModPresentation.IsIndependentPatch(m)&&m.Enabled&&
    WorkshopSources().Get(m.Id).HasValue&&WorkshopUpdates().Cached(m.Id)?.Status=="有更新");
   findBatch.IsEnabled=checkBatch.IsEnabled=enableBatch.IsEnabled=disableBatch.IsEnabled=
    count>0&&!busy&&!transactionWriting&&!modInventoryDirty;
   moreBatch.IsEnabled=count>0&&!busy&&!transactionWriting&&!modInventoryDirty;
   workshopCheckBatch.IsEnabled=hasWorkshopSource&&!busy&&!transactionWriting&&!modInventoryDirty;
   workshopUpdateBatch.IsEnabled=hasWorkshopUpdate&&!busy&&!transactionWriting&&!modInventoryDirty;
   var visible=ModListView.Filter(rows,modSection,modFilter,modQuery,modIssueFilter);
   checkAll.Checked-=OnCheckAll;checkAll.Unchecked-=OnCheckAll;
   checkAll.IsChecked=visible.Length>0&&visible.All(r=>chosenModFolders.Contains(r.Mod.Folder));
   checkAll.Checked+=OnCheckAll;checkAll.Unchecked+=OnCheckAll;
  }
  void OnCheckAll(object sender,RoutedEventArgs e)
  {
   foreach(var row in ModListView.Filter(rows,modSection,modFilter,modQuery,modIssueFilter))
    if(checkAll.IsChecked==true)chosenModFolders.Add(row.Mod.Folder);
    else chosenModFolders.Remove(row.Mod.Folder);
   FilterRows();
  }
  void FilterRows()
  {
   var visible=ModListView.Filter(rows,modSection,modFilter,modQuery,modIssueFilter);
   foreach(var row in rows)row.Selected=chosenModFolders.Contains(row.Mod.Folder);
   ModCardList.SetItems(list,visible,cardColumns);
   UpdateSelectionToolbar();
   shownCount.Text=$"显示 {visible.Length} / {(modSection=="修复补丁"?fixCount:normalCount)} 项";
   empty.Visibility=visible.Length==0&&!busy?Visibility.Visible:Visibility.Collapsed;
   if(selectedModDetailFolder!=null&&!visible.Any(row=>
       row.Mod.Folder.Equals(selectedModDetailFolder,StringComparison.OrdinalIgnoreCase)))
   {
    selectedModDetailFolder=null;UpdateDetail();ResizeDetail();
   }
  }
  // Bind filtering only after all controls and row sources are constructed.
  // This also avoids capturing unassigned locals in a control factory callback.
  foreach(var entry in segmentButtons)
  {
   string state=entry.Key;
   entry.Value.Click+=(_,_)=>{
    modFilter=state;
    foreach(var choice in segmentButtons)
     choice.Value.Style=(Style)FindResource(choice.Key==modFilter?"ModSegmentActive":"ModSegment");
    modListOffset=0;FilterRows();ListScroll(list)?.ScrollToTop();
   };
  }
  foreach(var item in attentionMenu.Items.OfType<MenuItem>())
  {
   string chosen=item.Header?.ToString()??"所有状态";
   item.Click+=(_,_)=>{
    modIssueFilter=chosen;attentionButton.Content=chosen+"  ▾";
    foreach(var option in attentionMenu.Items.OfType<MenuItem>())
     option.IsChecked=option.Header?.ToString()==chosen;
    modListOffset=0;FilterRows();ListScroll(list)?.ScrollToTop();
   };
  }
  search.TextChanged+=(_,_)=>{
   modQuery=search.Text;
   placeholder.Visibility=string.IsNullOrEmpty(modQuery)?Visibility.Visible:Visibility.Collapsed;
   modListOffset=0;
   FilterRows();ListScroll(list)?.ScrollToTop();
   string q=modQuery,f=modFilter,g=modSection;
   string issue=modIssueFilter;
   RememberPage(isFixSection?"本地补丁":"模组管理",()=>{modQuery=q;modFilter=f;modSection=g;modIssueFilter=issue;ShowMods();});
  };
  FilterRows();UpdateDetail();ResizeDetail();
  list.SizeChanged+=(_,_)=>{
   int next=ModListView.ColumnCount(list.ActualWidth);
   if(next!=cardColumns){cardColumns=next;FilterRows();}
  };
  list.Loaded+=(_,_)=>Dispatcher.BeginInvoke(new Action(()=>{
   if(activeModList==list&&ListScroll(list) is { } scroll)scroll.ScrollToVerticalOffset(modListOffset);
  }),System.Windows.Threading.DispatcherPriority.Loaded);
  string savedQuery=modQuery,savedFilter=modFilter,savedSection=modSection,savedIssue=modIssueFilter;
  SetPage(isFixSection?"本地补丁":"模组管理",root,restore:()=>{
   modQuery=savedQuery;modFilter=savedFilter;modSection=savedSection;modIssueFilter=savedIssue;ShowMods();
  });
 }
 void ShowModDetail(string folder)
 {
  var mod=modInventory?.Mods.SingleOrDefault(m=>m.Folder==folder);if(mod==null){ShowMods();return;}
  var root=V(Header("MOD DETAILS",mod.Name,mod.Id));Space(root,18);root.Children.Add(T(mod.Description.Length>0?mod.Description:"作者未填写简介。",14,muted));Space(root,18);
  root.Children.Add(T("版本："+mod.Version+"\n作者："+mod.Author+"\n状态："+(mod.Enabled?"启用":"停用")+"\n目录："+mod.Folder,13,muted));Space(root,18);
  if(!ModPresentation.IsIndependentPatch(mod))
  {
   ulong? workshopId=WorkshopSources().Get(mod.Id);
   root.Children.Add(T("Steam 创意工坊来源",17,ink,true));Space(root,8);
   root.Children.Add(T(workshopId.HasValue
    ?"已关联 Workshop ID："+workshopId.Value
    :"尚未关联创意工坊来源；下载时可输入 Workshop ID。",12,muted));
   var workshopStatus=workshopId.HasValue?WorkshopUpdates().Cached(mod.Id):null;
   if(workshopStatus!=null)
   {
    Space(root,5);
    root.Children.Add(T("创意工坊状态："+workshopStatus.Status+" · "+workshopStatus.Message,11,
     workshopStatus.Status=="有更新"?gold:muted));
   }
   var workshopActions=new List<UIElement>
   {
    Btn(workshopId.HasValue?"修改 Workshop 关联":"手动关联 Workshop ID",
     ()=>BindWorkshopMod(mod),"Ghost")
   };
   if(workshopId.HasValue)
    workshopActions.Add(Btn("检查创意工坊更新",()=>CheckWorkshopMod(mod),"Ghost"));
   string workshopActionLabel=workshopId is null?"从创意工坊下载并关联":
    workshopStatus?.Status switch
    {
     "待首次核验"=>"首次核验并建立基线",
     "本地文件已变化"=>"重新核验本地文件",
     "有更新"=>"获取更新并预览",
     _=>"从创意工坊重新下载 / 更新"
    };
   workshopActions.Add(Btn(workshopActionLabel,()=>DownloadWorkshopMod(mod),"Ghost"));
   if(workshopId.HasValue)
    workshopActions.Add(Btn("解除来源关联",()=>{
     try{WorkshopSources().Unbind(mod.Id);WorkshopUpdates().Invalidate(mod.Id);ShowModDetail(mod.Folder);}
     catch(Exception e){toast.Text="解除关联失败："+e.Message;}
    },"Ghost"));
   root.Children.Add(H(workshopActions.ToArray()));Space(root,18);

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
   Space(root,20);
  }
  if(mod.Enabled&&selectedInstallRoot!=null)
  {
   var backups=GitHubModInstaller.Backups(selectedInstallRoot,mod);
   if(backups.Length>0)
   {
    root.Children.Add(T("MOD 版本备份",17,ink,true));Space(root,8);
    root.Children.Add(T("当前有 "+backups.Length+" 个历史版本备份。最近一次可用于恢复；清理旧备份时会保留最近一次。",12,muted));Space(root,8);
    root.Children.Add(H(
     Btn("恢复最近一次备份",()=>RestoreModBackup(mod),"Ghost"),
     Btn("管理旧版本备份",()=>CleanOldModBackups(mod),"Ghost")));
    Space(root,18);
   }
  }
  var dependencyReport=ModDependencyGraph.Analyze(modInventory!);
  var dependencySummary=dependencyReport.Summary(mod.Id);
  root.Children.Add(T("依赖关系",17,ink,true));Space(root,8);
  root.Children.Add(T(dependencySummary.Label,13,dependencySummary.HasIssue?gold:muted,true));
  if(dependencyReport.Edges.Any(edge=>edge.Dependent.Id.Equals(mod.Id,StringComparison.OrdinalIgnoreCase)))
  {
   foreach(var edge in dependencyReport.Edges.Where(edge=>edge.Dependent.Id.Equals(mod.Id,StringComparison.OrdinalIgnoreCase)))
   {
    string status=edge.IsCycle?"循环依赖":ModDependencyGraphPresentation.Status(edge.Status);
    root.Children.Add(T(edge.DependencyId+" · "+status+"\n"+edge.Message,12,edge.IsIssue?gold:muted));
   }
  }
  else root.Children.Add(T("清单未声明必需前置；这不代表一定没有其他运行时依赖。",12,muted));
  var incomingDependencies=dependencyReport.Edges.Where(edge=>
   edge.DependencyId.Equals(mod.Id,StringComparison.OrdinalIgnoreCase)).ToArray();
  Space(root,7);
  root.Children.Add(T("依赖此 MOD 的其他项目",14,ink,true));
  if(incomingDependencies.Length==0)
   root.Children.Add(T("没有在当前清单或修复补丁规则中发现其他 MOD 依赖它。",12,muted));
  else
  {
   root.Children.Add(T("共 "+incomingDependencies.Length+" 条依赖关系；停用前请特别留意仍处于启用状态的项目。",12,muted));
   foreach(var edge in incomingDependencies)
    root.Children.Add(T(edge.Dependent.Name+" · "+(edge.Dependent.Enabled?"已启用":"已停用")+
     (edge.FromPatchRule?" · 修复补丁兼容规则":" · MOD 清单声明"),12,
     edge.Dependent.Enabled?gold:muted));
  }
  root.Children.Add(Btn("查看完整依赖关系图",()=>ShowDependencyGraph(),"Ghost"));Space(root,20);
  if(ModPresentation.IsIndependentPatch(mod))
  {
   root.Children.Add(H(Badge("健康："+LifecycleTag(mod.Id),muted),
    Btn("查看依据",()=>ShowPatchEvidence(mod.Id),"Ghost")));
   Space(root,18);
  }
  string? blocked=ModManager.BlockReason(selectedInstallRoot!,mod,modInventory!);var toggle=Btn(mod.Enabled?"预览停用":"预览启用",()=>PrepareModToggle(mod),"Primary");toggle.IsEnabled=!busy&&blocked==null;root.Children.Add(H(toggle,Btn("打开文件夹",()=>OpenUrl(mod.Folder),"Ghost")));Space(root,10);if(blocked!=null)root.Children.Add(T(blocked,13,gold));
  SetPage("模组管理",root,"mod:"+folder,()=>ShowModDetail(folder));toast.Text="启用或停用前会重新核对文件及声明的前置模组。";
 }
 async void ChooseWorkshopZip()
 {
  if(busy||transactionWriting||selectedInstallRoot==null)return;
  var dialog=new Microsoft.Win32.OpenFileDialog{
   Title="选择已下载的 MOD ZIP（不会连接第三方下载服务）",
   Filter="ZIP 压缩包 (*.zip)|*.zip",CheckFileExists=true,Multiselect=false};
  if(dialog.ShowDialog(this)!=true)return;
  string root=selectedInstallRoot,zip=dialog.FileName;
  busy=true;zipImportPreview=null;
  try
  {
   var plan=await Task.Run(()=>WorkshopZipImport.Preview(root,zip));
   if(selectedInstallRoot!=root||modInventoryDirty)
    throw new InvalidOperationException("游戏目录或 MOD 文件发生变化，请重新选择 ZIP。");
   zipImportPreview=plan;ShowWorkshopZipConfirmation();
  }
  catch(Exception e){toast.Text="ZIP 预检未通过："+e.Message;ShowMods();}
  finally{busy=false;}
 }
 void ShowWorkshopZipConfirmation()
 {
  if(zipImportPreview==null){ShowMods();return;}
  var plan=zipImportPreview;
  var root=V(Header("IMPORT LOCAL MOD ZIP","确认导入 MOD",plan.ModName));
  Space(root,18);
  root.Children.Add(Card(V(
   T("仅导入已有 ZIP，不会联网下载、不执行文件，也不会覆盖已存在的 MOD。",14,ink,true),
   T("MOD ID："+plan.ModId+"\n文件数量："+plan.FileCount+
    "\n解压总大小："+(plan.Bytes/1024d/1024d).ToString("F2")+" MiB"+
    "\nZIP 来源："+plan.ZipPath+"\n目标目录："+plan.Destination,12,muted),
   T("声明前置："+(plan.Dependencies.Length==0?"未声明":string.Join("、",plan.Dependencies)),12,muted),
   T("来自浏览器或第三方站点的 ZIP 未经过 Steam 官方身份验证，需自行确认来源与兼容性。",12,gold),
   H(Btn("确认导入",ApplyWorkshopZip,"Primary"),
     Btn("取消",()=>{zipImportPreview=null;ShowMods();},"Ghost")))));
  SetPage("模组管理",root,"mod-zip-confirm",ShowWorkshopZipConfirmation);
 }
 async void ApplyWorkshopZip()
 {
  if(busy||transactionWriting||zipImportPreview==null||selectedInstallRoot!=zipImportPreview.Root)return;
  var plan=zipImportPreview;zipImportPreview=null;busy=true;transactionWriting=true;
  bool imported=false;
  try
  {
   var safety=await Task.Run(()=>ModEnvironmentSnapshots.Create(
    plan.Root,installer.DataRoot,"本地 ZIP 导入前自动保护"));
   await Task.Run(()=>WorkshopZipImport.Apply(plan));
   imported=true;
   string baselineWarning="";
   try
   {
    WorkshopSources().ClearBaseline(plan.ModId);
    WorkshopUpdates().Invalidate(plan.ModId);
   }
   catch(Exception baselineError){baselineWarning="；版本基线未能重置："+baselineError.Message;}
   InvalidateLifecycle("导入了新的 MOD，请重新检查补丁健康状态。");
   report=null;states=[];ClearCoop();ClearSelection();installPreview=null;lastInstallResult=null;modTogglePreview=null;
   modInventoryDirty=true;
   try
   {
    modInventory=await Task.Run(()=>ModManager.Scan(plan.Root));
    modInventoryDirty=false;
    modStatus="成功导入 "+plan.ModName+"；请检查依赖与游戏兼容性。";
    toast.Text=modStatus+"；导入前环境快照："+safety.Id+baselineWarning;
   }
   catch(Exception scanError)
   {
    modStatus="ZIP 已写入，但刷新 MOD 清单失败："+scanError.Message;
    toast.Text=modStatus+"；导入前环境快照："+safety.Id;
   }
  }
  catch(Exception e)
  {
   modInventoryDirty=imported;
   modStatus=imported?"ZIP 已导入，但后续处理失败："+e.Message:"MOD 导入未完成："+e.Message;
   toast.Text=modStatus;
  }
  finally{busy=false;transactionWriting=false;ShowMods();}
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
