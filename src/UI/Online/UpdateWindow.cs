using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SpirePatch;
public partial class MainWindow
{
 readonly ManagerUpdates managerUpdates=new();
 ManagerRelease? managerRelease;
 ManagerUpdateChannel managerUpdateChannel=ManagerUpdateChannel.Stable;
 CancellationTokenSource? managerUpdateCancellation;
 bool managerUpdateBusy,managerUpdateChecked,managerCleanupBusy;
 string managerCleanupStatus="保留最近一次成功更新的完整回滚任务；仅清理已确认归属的旧任务和缓存。";
 string managerUpdateStatus="检查管理器自己的新版本，更新与否由你决定。";
 string? managerDownload;
 Button? managerUpdateButton;
 TextBlock? managerProgress;
 void ShowManagerUpdates()
 {
  var root=V(Header("MANAGER UPDATES","检查更新","自行选择正式发布或 Beta 预发布，下载前核对版本内容。"));Space(root,16);
  var channelTabs=new StackPanel{Orientation=Orientation.Horizontal};
  foreach(var (name,channel) in new[]{("稳定版",ManagerUpdateChannel.Stable),("测试版 Beta",ManagerUpdateChannel.Beta)})
  {
   var selected=channel==managerUpdateChannel;
   var tab=Btn(name,()=>SelectManagerUpdateChannel(channel),
    selected?"ModSegmentActive":"ModSegment");
   tab.Margin=new Thickness(0,0,4,0);tab.IsEnabled=!managerUpdateBusy&&!managerCleanupBusy&&!busy&&!transactionWriting;
   channelTabs.Children.Add(tab);
  }
  root.Children.Add(new Border{Child=channelTabs,HorizontalAlignment=HorizontalAlignment.Left,
   Padding=new Thickness(4),CornerRadius=new CornerRadius(9),Background=B("#68304760")});
  Space(root,8);
  root.Children.Add(T(managerUpdateChannel==ManagerUpdateChannel.Stable?
   "稳定版：仅检查正式 Release，推荐日常使用。":
   "测试版：检查 GitHub Beta 预发布；可能包含未经过完整实机验证的改动。",12,muted));
  Space(root,12);
  var summary=V(Badge("当前已安装  v"+ManagerUpdates.CurrentReleaseVersion,gold),
   T(managerUpdateStatus,15,ink,true));Space(summary,13);
  var check=Btn(managerUpdateBusy?"正在处理…":"检查更新",()=>_=CheckManagerUpdatesAsync(true),"Primary");check.IsEnabled=!managerUpdateBusy&&!managerCleanupBusy;
  summary.Children.Add(H(check,Btn("打开发布页",()=>OpenUrl(ManagerUpdates.RepositoryUrl+(managerUpdateChannel==ManagerUpdateChannel.Stable?"/releases/latest":"/releases")),"Ghost")));
  if(managerUpdateBusy&&managerUpdateCancellation!=null){Space(summary,10);summary.Children.Add(Btn("取消",()=>managerUpdateCancellation?.Cancel(),"Ghost"));}
  root.Children.Add(Card(summary));Space(root,20);
  if(managerRelease is { } release){
   bool newer=ManagerUpdates.IsNewer(release);
   bool canInstall=CanInstallManagerRelease(release);
   var details=V(T((managerUpdateChannel==ManagerUpdateChannel.Beta?"Beta 测试版  ":"稳定版本  ")+release.Version,22,ink,true),T("发布于 "+release.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")+" · Windows x64 · "+(release.Size/1024d/1024).ToString("F1")+" MB",12,muted));Space(details,18);
   if(newer){
    var download=Btn("下载并更新",DownloadAndApplyManagerUpdate,"Primary");
    download.IsEnabled=CanUpdateManager&&canInstall;
    var alternate=Btn("备用官方通道更新",DownloadAndApplyManagerUpdateViaApi,"Ghost");
    alternate.IsEnabled=CanUpdateManager&&canInstall;
    var manual=Btn("仅下载 ZIP",DownloadManagerUpdate,"Ghost");manual.IsEnabled=!managerUpdateBusy&&!managerCleanupBusy;
    var later=Btn("暂不更新",DeferManagerUpdate,"Ghost");later.IsEnabled=!managerUpdateBusy&&!managerCleanupBusy;
    if(canInstall)details.Children.Add(H(download,alternate,manual,later));
    else
    {
     details.Children.Add(T("此版本与当前安装的程序主版本相同。为保护更新事务，不能自动覆盖；可仅下载 ZIP 并手动另行解压。",12,gold));
     details.Children.Add(H(manual,later));
    }
    details.Children.Add(T("备用通道使用 GitHub 官方附件 API；可能仍走相同的下载网络，不保证提速。两种方式均验证 SHA-256。",11,muted));
   }
   Space(details,18);details.Children.Add(T("更新内容",17,ink,true));Space(details,8);details.Children.Add(T(release.Notes,13,muted));Space(details,12);
   details.Children.Add(Btn("查看此版本发布页",()=>OpenUrl(release.ReleaseUrl),"Ghost"));root.Children.Add(Card(details));Space(root,18);
  }
  managerProgress=T("",12,gold);root.Children.Add(managerProgress);
  if(managerDownload!=null){var apply=Btn("安装此更新并重启",()=>_=InstallDownloadedManagerAsync(false),"Primary");apply.IsEnabled=CanUpdateManager&&managerRelease!=null&&CanInstallManagerRelease(managerRelease);root.Children.Add(Card(V(T("更新包已下载并通过 SHA256 校验",17,green,true),T(managerDownload,12,muted),H(apply,Btn("打开下载文件夹",()=>OpenUrl(Path.GetDirectoryName(managerDownload)!),"Ghost")))));Space(root,18);}
  root.Children.Add(T("如何更新",17,ink,true));Space(root,8);
  root.Children.Add(T("选择“下载并更新”并确认后，下载、校验、备份、替换与重新打开会自动完成。安装路径、快捷方式、设置和缓存继续使用；游戏与模组文件不受影响。文件替换失败时会尝试恢复上一版。",13,muted));
  Space(root,10);root.Children.Add(Btn("查看管理器更新记录",()=>{string folder=Path.Combine(installer.DataRoot,"管理器更新");if(Directory.Exists(folder))OpenUrl(folder);else toast.Text="还没有管理器更新记录。";},"Ghost"));
  Space(root,18);
  var cleanup=Btn(managerCleanupBusy?"正在扫描旧版本…":"清理旧版本文件",CleanupOldManagerFiles,"Ghost");
  cleanup.IsEnabled=CanUpdateManager&&!managerCleanupBusy;
  root.Children.Add(Card(V(T("旧版本文件清理",17,ink,true),
   T("仅检查管理器生成的历史更新任务和旧版 ZIP。自动保留最近一次成功更新的回滚记录；失败、未完成、来源不明或含用户文件的目录不会清理。",13,muted),
   T(managerCleanupStatus,12,gold),cleanup)));
  string resultPath=Path.Combine(installer.DataRoot,"管理器更新","最近结果.json");if(File.Exists(resultPath)){try{using var result=System.Text.Json.JsonDocument.Parse(File.ReadAllText(resultPath));Space(root,12);root.Children.Add(T("最近一次："+result.RootElement.GetProperty("message").GetString(),13,muted));}catch{}}
  SetPage("检查更新",root,restore:ShowManagerUpdates);toast.Text="更新由你确认；会保留程序备份和个人数据。";
 }
 void SelectManagerUpdateChannel(ManagerUpdateChannel channel)
 {
  if(channel==managerUpdateChannel||!CanUpdateManager)return;
  try{if(rememberEnvironment)ManagerUpdates.SaveChannel(installer.DataRoot,channel);}
  catch(Exception e){managerUpdateStatus="更新通道偏好未保存："+e.Message;ShowManagerUpdates();return;}
  managerUpdateChannel=channel;managerRelease=null;managerDownload=null;managerUpdateChecked=false;
  managerUpdateStatus=channel==ManagerUpdateChannel.Beta?
   "已切换到 Beta 测试版，尚未检查。":"已切换到稳定版，尚未检查。";
  if(managerUpdateButton!=null)managerUpdateButton.Content="检查更新";
  ShowManagerUpdates();
  _=CheckManagerUpdatesAsync(true);
 }
 // The updater's worker enforces an increasing three-part assembly version.
 // Same-core Beta -> stable or Beta -> Beta upgrades need an explicitly newer assembly.
 bool CanInstallManagerRelease(ManagerRelease release)=>
  ManagerUpdates.StableVersion(ManagerUpdates.CoreVersion(release.Version))>
   ManagerUpdates.StableVersion(ManagerUpdates.CurrentVersion);
 void DeferManagerUpdate(){if(managerUpdateBusy||managerCleanupBusy)return;if(backHistory.Count>0)GoBack();else ShowOverview();}
 bool CanUpdateManager=>!managerUpdateBusy&&!managerCleanupBusy&&!busy&&!transactionWriting&&!onlineBusy;
 async void CleanupOldManagerFiles()
 {
  if(!CanUpdateManager)return;
  managerCleanupBusy=true;ShowManagerUpdates();
  try
  {
   var preview=await Task.Run(()=>ManagerUpdateCleanup.Preview(AppContext.BaseDirectory));
   if(preview.Items.Length==0)managerCleanupStatus="未发现可安全清理的历史更新文件。";
   else
   {
    string size=(preview.Bytes/1024d/1024d).ToString("F1");
    var approve=MessageBox.Show(this,$"预计释放 {size} MiB，包含 {preview.Jobs} 个历史更新任务和 {preview.Downloads} 个旧版 ZIP。\n\n最近一次成功更新的回滚记录、个人设置、MOD 和游戏文件会保留。清理后的历史更新任务不可恢复。\n\n确认清理吗？",
     "确认清理旧版本文件",MessageBoxButton.YesNo,MessageBoxImage.Warning);
    if(approve==MessageBoxResult.Yes)
    {
     var result=await Task.Run(()=>ManagerUpdateCleanup.Clean(AppContext.BaseDirectory,preview));
     managerCleanupStatus=$"已清理 {result.Removed} 项，释放 {result.FreedBytes/1024d/1024d:F1} MiB。"+
      (result.Errors.Length>0?" 部分文件未能清理："+string.Join("；",result.Errors.Take(2)):"");
    }
    else managerCleanupStatus="已取消清理，所有文件保持不变。";
   }
  }
  catch(Exception ex){managerCleanupStatus="清理未执行："+ex.Message;}
  finally{managerCleanupBusy=false;if(currentVisit?.Key=="检查更新")ShowManagerUpdates();}
 }
 internal async Task CleanupOnNormalLaunchAsync()
 {
  try
  {
   await Task.Delay(1200);
   for(int retry=0;retry<30&&(managerUpdateBusy||busy||transactionWriting||onlineBusy);retry++)
    await Task.Delay(500);
   if(!CanUpdateManager)return;
   managerCleanupBusy=true;
   var preview=await Task.Run(()=>ManagerUpdateCleanup.Preview(AppContext.BaseDirectory));
   if(preview.Items.Length==0)return;
   var result=await Task.Run(()=>ManagerUpdateCleanup.Clean(AppContext.BaseDirectory,preview));
   if(result.Removed>0)managerCleanupStatus=$"自动清理了 {result.Removed} 项历史文件，释放 {result.FreedBytes/1024d/1024d:F1} MiB；最近一次回滚备份已保留。";
  }
  catch(Exception){/* Startup cleanup is best effort and must never block the manager. */}
  finally{managerCleanupBusy=false;if(currentVisit?.Key=="检查更新")ShowManagerUpdates();}
 }
 bool ConfirmManagerUpdate()=>MessageBox.Show(this,"将下载并校验新版，自动保存当前程序备份，然后关闭此窗口、替换程序并重新打开。\n\n设置、缓存、原快捷方式与游戏模组会保留。现在更新吗？","更新管理器",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes;
 async void DownloadAndApplyManagerUpdate()=>await DownloadAndApplyManagerUpdateCoreAsync(false);
 async void DownloadAndApplyManagerUpdateViaApi()=>await DownloadAndApplyManagerUpdateCoreAsync(true);
 async Task DownloadAndApplyManagerUpdateCoreAsync(bool preferAssetApi)
 {
  if(!CanUpdateManager||managerRelease==null||!CanInstallManagerRelease(managerRelease)||!ConfirmManagerUpdate())return;
  try{var release=managerRelease;string destination=BundleInstaller.Child(installer.DataRoot,"管理器下载/"+release.FileName);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
   await DownloadManagerUpdateToAsync(release,destination,preferAssetApi);
   if(managerDownload!=null)await InstallDownloadedManagerAsync(true);
  }catch(Exception e){managerUpdateStatus="无法准备更新下载："+e.Message;ShowManagerUpdates();}
 }
 async Task InstallDownloadedManagerAsync(bool confirmed)
 {
  if(!CanUpdateManager||managerRelease==null||!CanInstallManagerRelease(managerRelease)||managerDownload==null||(!confirmed&&!ConfirmManagerUpdate()))return;
  var release=managerRelease;var archive=managerDownload;busy=true;transactionWriting=true;managerUpdateBusy=true;managerUpdateStatus="正在核对程序文件并准备更新…";ShowManagerUpdates();
  bool handoff=false;
  try{
   var ready=await ManagerAutoUpdate.PrepareAsync(release,archive,AppContext.BaseDirectory);
   managerUpdateStatus="已准备完成，正在交接给更新助手…";ShowManagerUpdates();
   await ManagerAutoUpdate.StartAndWaitReadyAsync(ready);handoff=true;
  }catch(Exception e){managerUpdateStatus="自动更新未开始："+e.Message;}
  finally{busy=false;transactionWriting=false;managerUpdateBusy=false;if(!handoff&&currentVisit?.Key=="检查更新")ShowManagerUpdates();}
  if(handoff)Application.Current.Shutdown();
 }
 async Task CheckManagerUpdatesAsync(bool showPage)
 {
  if(managerUpdateBusy||managerCleanupBusy)return;
  var channel=managerUpdateChannel;
  managerUpdateBusy=true;managerUpdateChecked=true;managerRelease=null;managerDownload=null;
  managerUpdateStatus=channel==ManagerUpdateChannel.Beta?"正在检查 Beta 预发布…":"正在检查稳定版…";
  managerUpdateCancellation=new();
  if(showPage)ShowManagerUpdates();
  try{
   managerRelease=await managerUpdates.CheckAsync(channel,managerUpdateCancellation.Token);
   int comparison=ManagerUpdates.CompareVersions(managerRelease.Version,ManagerUpdates.CurrentReleaseVersion);
   managerUpdateStatus=comparison>0?
    CanInstallManagerRelease(managerRelease)?"发现此通道的新版本，可先查看变更后选择更新。":
     "发现同主版本的新构建；自动覆盖不受支持，可仅下载 ZIP 手动安装。":
    comparison==0?"当前已是此通道的最新版本。":
     "当前安装版本高于此通道提供的版本，不会自动降级。";
   if(managerUpdateButton!=null)managerUpdateButton.Content=comparison>0?"↑  发现 v"+managerRelease.Version:"检查更新";
  }catch(OperationCanceledException){managerUpdateStatus="检查已取消或超时，未改动程序。可以重试或打开发布页。";}
  catch(Exception e){managerUpdateStatus="检查未完成："+e.Message;}
  finally{managerUpdateBusy=false;managerUpdateCancellation.Dispose();managerUpdateCancellation=null;if(managerRelease==null&&managerUpdateButton!=null)managerUpdateButton.Content="检查更新";if(currentVisit?.Key=="检查更新")ShowManagerUpdates();}
 }
 async void DownloadManagerUpdate()
 {
  if(managerUpdateBusy||managerCleanupBusy||managerRelease==null||!ManagerUpdates.IsNewer(managerRelease))return;
  var release=managerRelease;var save=new SaveFileDialog{Title="保存管理器更新包",FileName=release.FileName,Filter="ZIP 更新包|*.zip",DefaultExt=".zip",AddExtension=true};if(save.ShowDialog(this)!=true)return;
  await DownloadManagerUpdateToAsync(release,save.FileName);
 }
 async Task DownloadManagerUpdateToAsync(ManagerRelease release,string destination,bool preferAssetApi=false)
 {
  if(managerUpdateBusy||managerCleanupBusy)return;managerUpdateBusy=true;managerDownload=null;managerUpdateCancellation=new();managerUpdateStatus=preferAssetApi?"正在通过 GitHub 官方附件 API 下载并校验…":"正在下载，完成后自动校验…";ShowManagerUpdates();
  try{
   var started=DateTimeOffset.UtcNow;
   await managerUpdates.DownloadAsync(release,destination,new Progress<long>(n=>{
    if(managerUpdateBusy&&currentVisit?.Key=="检查更新"&&managerProgress!=null)
    {
     double seconds=Math.Max(1,(DateTimeOffset.UtcNow-started).TotalSeconds);
     managerProgress.Text=$"已下载 {n/1024d/1024:F1} / {release.Size/1024d/1024:F1} MB · 平均 {(n/1024d/1024)/seconds:F2} MB/s";
    }
   }),managerUpdateCancellation.Token,preferAssetApi);
   managerDownload=destination;managerUpdateStatus="下载完成且校验一致，可安装此更新并重启，也可保留 ZIP 稍后处理。";
  }catch(OperationCanceledException){managerUpdateStatus="下载已取消或超时，未改动当前程序。";}
  catch(Exception e){managerUpdateStatus="下载未完成："+e.Message;}
  finally{managerUpdateBusy=false;managerUpdateCancellation.Dispose();managerUpdateCancellation=null;if(currentVisit?.Key=="检查更新")ShowManagerUpdates();}
 }
}
