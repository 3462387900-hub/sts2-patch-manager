using System.IO;
using System.Windows.Threading;

namespace SpirePatch;

public partial class MainWindow
{
 // A file watcher is an early warning, NOT an integrity check or permission to retire patches.
 readonly List<FileSystemWatcher> modWatchers=[];
 DispatcherTimer? modWatchDebounce;
 string? watchedGameRoot;
 bool modInventoryDirty,modWatchOverflow,modWatchTreeChanged;
 int modChangeVersion;

 void StopModWatch()
 {
  modWatchDebounce?.Stop();
  foreach(var watcher in modWatchers){watcher.EnableRaisingEvents=false;watcher.Dispose();}
  modWatchers.Clear();watchedGameRoot=null;
 }

 void StartModWatch(string root)
 {
  if(root.Equals(watchedGameRoot,StringComparison.OrdinalIgnoreCase))return;
  StopModWatch();
  modInventoryDirty=false;modWatchOverflow=false;modWatchTreeChanged=false;
  watchedGameRoot=root;
  modWatchDebounce??=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(850)};
  modWatchDebounce.Tick-=OnModWatchQuiet;
  modWatchDebounce.Tick+=OnModWatchQuiet;
  try
  {
   WatchFolder(root,root,false,true);
   string mods=Path.Combine(root,"mods");
   if(Directory.Exists(mods))WatchFolder(root,mods,true,false);
   string disabled=Path.Combine(root,ModManager.StorageName);
   if(Directory.Exists(disabled))WatchFolder(root,disabled,true,false);
  }
  catch(Exception ex)
  {
   StopModWatch();modWatchOverflow=true;modInventoryDirty=true;
   modStatus="文件自动监测无法启动，请手动刷新："+ex.Message;
   InvalidateLifecycle("文件监测未启用，请重新扫描健康状态。");
  }
 }

 void WatchFolder(string root,string folder,bool recursive,bool onlyTopFolders)
 {
  var watcher=new FileSystemWatcher(folder){
   IncludeSubdirectories=recursive,NotifyFilter=NotifyFilters.FileName|NotifyFilters.DirectoryName|
    NotifyFilters.LastWrite|NotifyFilters.Size|NotifyFilters.CreationTime,
   InternalBufferSize=32*1024
  };
  if(onlyTopFolders)
  {
   watcher.Filters.Clear();
   watcher.Filters.Add("mods");
   watcher.Filters.Add(ModManager.StorageName);
  }
  watcher.Changed+=(_,_)=>QueueModChange(root,false,onlyTopFolders);
  watcher.Created+=(_,_)=>QueueModChange(root,false,onlyTopFolders);
  watcher.Deleted+=(_,_)=>QueueModChange(root,false,onlyTopFolders);
  watcher.Renamed+=(_,_)=>QueueModChange(root,false,onlyTopFolders);
  watcher.Error+=(_,_)=>QueueModChange(root,true,onlyTopFolders);
  modWatchers.Add(watcher);
  watcher.EnableRaisingEvents=true;
 }

 void QueueModChange(string root,bool overflow,bool tree)
 {
  if(Dispatcher.HasShutdownStarted||Dispatcher.HasShutdownFinished)return;
  _=Dispatcher.BeginInvoke(new Action(()=>{
   if(!root.Equals(selectedInstallRoot,StringComparison.OrdinalIgnoreCase))return;
   modChangeVersion++;
   modWatchTreeChanged|=tree;
   if(transactionWriting)
   {
    modInventoryDirty=true;
    modWatchDebounce?.Stop();modWatchDebounce?.Start();
    return;
   }
   // One burst invalidates the current report once, but subsequent writes restart the quiet timer.
   if(!modInventoryDirty||overflow)
   {
    modInventoryDirty=true;
    modWatchOverflow|=overflow;
    InvalidateLifecycle(overflow
     ?"文件监测发生溢出，无法保证通知完整。请重新扫描。"
     :"检测到 MOD 文件变化，请重新检查健康状态。");
    report=null;states=[];ClearCoop();ClearSelection();installPreview=null;modTogglePreview=null;
    modStatus=overflow?"文件监测可能遗漏变化，请手动刷新。":"检测到 MOD 文件变化，正在等待文件写入结束。";
   }
   modWatchDebounce?.Stop();modWatchDebounce?.Start();
   if(page=="模组管理"&&currentVisit?.Key=="模组管理"&&!busy&&!transactionWriting)ShowMods();
  }),DispatcherPriority.Background);
 }

 void OnModWatchQuiet(object? sender,EventArgs e)
 {
  modWatchDebounce?.Stop();
  if(selectedInstallRoot==null)return;
  if(busy||transactionWriting){modWatchDebounce?.Start();return;}
  string root=selectedInstallRoot;
  // A new mods / disabled-folder tree may have appeared since monitoring began.
  if(modWatchOverflow||modWatchTreeChanged||modWatchers.Count<2)
  {
   watchedGameRoot=null;
   StartModWatch(root);
   // Keep the stale warning even if monitoring was successfully recreated.
   modInventoryDirty=true;
  }
  if(modInventoryDirty&&HasValidLifecycleReport)
  {
   InvalidateLifecycle("文件监测发现 MOD 环境变化，请重新扫描补丁健康状态。");
   report=null;states=[];ClearCoop();ClearSelection();installPreview=null;modTogglePreview=null;
  }
  // Even when the user is viewing another tab, a known previous inventory
  // must be reconciled so manually removed target MODs can retire their fixes.
  if(modInventoryDirty&&modInventory!=null)
   RefreshMods();
  else if(modInventoryDirty)
   toast.Text="检测到 MOD 文件变化。进入模组管理刷新列表，并重新检查补丁健康状态。";
 }

 void CloseModWatch()=>StopModWatch();
}
