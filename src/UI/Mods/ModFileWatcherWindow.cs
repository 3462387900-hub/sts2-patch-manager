using System.IO;
using System.Windows.Threading;

namespace SpirePatch;

public partial class MainWindow
{
 // The watcher is infrastructure; this partial only translates service events into existing UI state.
 readonly ModFileWatcherService modFileWatcher = new();
 bool modWatcherEventsSubscribed;
 string? watchedGameRoot;
 bool modInventoryDirty,modWatchOverflow,modWatchTreeChanged;
 int modChangeVersion,modInventoryCleanVersion;

 void EnsureModWatcherEvents()
 {
  if(modWatcherEventsSubscribed)return;
  modWatcherEventsSubscribed=true;
  modFileWatcher.ChangeDetected+=OnModWatchChangeDetected;
  modFileWatcher.QuietPeriodElapsed+=OnModWatchQuiet;
  modFileWatcher.MonitoringFailed+=OnModWatchMonitoringFailed;
 }

 void StopModWatch()
 {
  EnsureModWatcherEvents();
  Interlocked.Increment(ref neonRootEpoch);
  modFileWatcher.Stop();
  modChangeVersion=0;
  modInventoryCleanVersion=0;
  watchedGameRoot=null;
 }

 void StartModWatch(string root)
 {
  EnsureModWatcherEvents();
  if(root.Equals(watchedGameRoot,StringComparison.OrdinalIgnoreCase))return;
  Interlocked.Increment(ref neonRootEpoch);
  modFileWatcher.Stop();
  modInventoryDirty=false;modWatchOverflow=false;modWatchTreeChanged=false;
  watchedGameRoot=root;
  modFileWatcher.Start(root);
  modChangeVersion=modFileWatcher.ChangeVersion;
  modInventoryCleanVersion=modChangeVersion;
 }

 void OnModWatchChangeDetected(object? sender,ModFileChangeEventArgs e)
 {
  if(e.Root.Equals(modFileWatcher.CurrentRoot,StringComparison.OrdinalIgnoreCase))
  {
   int observed;
   do
   {
    observed=Volatile.Read(ref modChangeVersion);
    if(e.Version<=observed)break;
   }while(Interlocked.CompareExchange(ref modChangeVersion,e.Version,observed)!=observed);
  }
  if(Dispatcher.HasShutdownStarted||Dispatcher.HasShutdownFinished)return;
  _=Dispatcher.BeginInvoke(new Action(()=>{
   if(!e.Root.Equals(selectedInstallRoot,StringComparison.OrdinalIgnoreCase))return;
   if(!e.Overflow&&e.Version<=modInventoryCleanVersion)return;
   modWatchTreeChanged|=e.TreeChanged;
   modWatchOverflow|=e.Overflow;
   if(transactionWriting)
   {
    modInventoryDirty=true;
    return;
   }
   // One burst invalidates the current report once, but subsequent writes restart the service timer.
   if(!modInventoryDirty||e.Overflow)
   {
    modInventoryDirty=true;
    modWatchOverflow|=e.Overflow;
    InvalidateLifecycle(e.Overflow
     ?"文件监测发生溢出，无法保证通知完整。请重新扫描。"
     :"检测到 MOD 文件变化，请重新检查健康状态。");
    report=null;states=[];ClearCoop();ClearSelection();installPreview=null;modTogglePreview=null;
    modStatus=e.Overflow?"文件监测可能遗漏变化，请手动刷新。":"检测到 MOD 文件变化，正在等待文件写入结束。";
   }
   if(UiThemes.IsNeon&&neonWorkbenchView!=null)NotifyNeonHealthInvalidated();
   else if((page=="模组管理"||page=="本地补丁")&&currentVisit?.Key==page&&!busy&&!transactionWriting)ShowMods();
  }),DispatcherPriority.Background);
 }

 void OnModWatchMonitoringFailed(object? sender,ModFileWatchFailureEventArgs e)
 {
  if(Dispatcher.HasShutdownStarted||Dispatcher.HasShutdownFinished)return;
  _=Dispatcher.BeginInvoke(new Action(()=>{
   if(!e.Root.Equals(selectedInstallRoot,StringComparison.OrdinalIgnoreCase))return;
   watchedGameRoot=null;modWatchOverflow=true;modInventoryDirty=true;
   modStatus="文件自动监测无法启动，请手动刷新："+e.Message;
   InvalidateLifecycle("文件监测未启用，请重新扫描健康状态。");
  }),DispatcherPriority.Background);
 }

 void OnModWatchQuiet(object? sender,ModFileWatchQuietEventArgs e)
 {
  if(Dispatcher.HasShutdownStarted||Dispatcher.HasShutdownFinished)return;
  _=Dispatcher.BeginInvoke(new Action(()=>ProcessModWatchQuiet(e)),DispatcherPriority.Background);
 }

 void ProcessModWatchQuiet(ModFileWatchQuietEventArgs e)
 {
  if(selectedInstallRoot==null||!e.Root.Equals(selectedInstallRoot,StringComparison.OrdinalIgnoreCase))return;
  if(busy||transactionWriting)
  {
   modFileWatcher.DeferQuietPeriod();
   return;
  }

  bool staleToCurrentScan=!e.Overflow&&e.Version<=modInventoryCleanVersion;
  var reconfigured=modFileWatcher.CompleteQuietPeriod(e.Version);
  if(reconfigured.Error is not null)
  {
   watchedGameRoot=null;modWatchOverflow=true;modInventoryDirty=true;
   modStatus="文件自动监测重新启动失败，请手动刷新："+reconfigured.Error;
   InvalidateLifecycle("文件监测未启用，请重新扫描健康状态。");
  }
  else if(reconfigured.Recreated)
  {
   watchedGameRoot=e.Root;
   if(!staleToCurrentScan)
   {
    // The service has rebuilt the root / mods / disabled watchers. Keep the stale warning.
    modInventoryDirty=true;modWatchOverflow=false;modWatchTreeChanged=false;
   }
  }
  if(staleToCurrentScan&&reconfigured.Error is null)return;

  if(modInventoryDirty&&HasValidLifecycleReport)
  {
   InvalidateLifecycle("文件监测发现 MOD 环境变化，请重新扫描补丁健康状态。");
   report=null;states=[];ClearCoop();ClearSelection();installPreview=null;modTogglePreview=null;
  }
  // Even when the user is viewing another tab, a known previous inventory
  // must be reconciled so manually removed target MODs can retire their fixes.
  if(modInventoryDirty&&(UiThemes.IsNeon||modInventory!=null||(page=="模组管理"||page=="本地补丁")&&currentVisit?.Key==page))
   RefreshMods();
  else if(modInventoryDirty)
   toast.Text="检测到 MOD 文件变化。进入模组管理刷新列表，并重新检查补丁健康状态。";
 }

 void CloseModWatch()
 {
  modInventoryRefresh?.Cancel();
  StopModWatch();
  modFileWatcher.Dispose();
 }
}
