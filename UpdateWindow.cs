using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SpirePatch;
public partial class MainWindow
{
 readonly ManagerUpdates managerUpdates=new();
 ManagerRelease? managerRelease;
 CancellationTokenSource? managerUpdateCancellation;
 bool managerUpdateBusy,managerUpdateChecked;
 string managerUpdateStatus="检查管理器自己的新版本，更新与否由你决定。";
 string? managerDownload;
 Button? managerUpdateButton;
 TextBlock? managerProgress;
 void ShowManagerUpdates()
 {
  var root=V(Header("MANAGER UPDATES","检查更新","查看尖塔修补站的新版本与更新内容，按需下载。"));Space(root,20);
  var summary=V(Badge("当前版本  "+ManagerUpdates.CurrentVersion,gold),T(managerUpdateStatus,16,ink,true));Space(summary,16);
  var check=Btn(managerUpdateBusy?"正在处理…":"检查更新",()=>_=CheckManagerUpdatesAsync(true),"Primary");check.IsEnabled=!managerUpdateBusy;
  summary.Children.Add(H(check,Btn("打开发布页",()=>OpenUrl(ManagerUpdates.RepositoryUrl+"/releases/latest"),"Ghost")));
  if(managerUpdateBusy&&managerUpdateCancellation!=null){Space(summary,10);summary.Children.Add(Btn("取消",()=>managerUpdateCancellation?.Cancel(),"Ghost"));}
  root.Children.Add(Card(summary));Space(root,20);
  if(managerRelease is { } release){
   bool newer=ManagerUpdates.IsNewer(release,ManagerUpdates.CurrentVersion);
   var details=V(T("正式版本  "+release.Version,22,ink,true),T("发布于 "+release.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")+" · Windows x64 · "+(release.Size/1024d/1024).ToString("F1")+" MB",12,muted));Space(details,18);
   if(newer){
    var download=Btn("下载并更新",DownloadAndApplyManagerUpdate,"Primary");
    download.IsEnabled=CanUpdateManager;
    var alternate=Btn("备用官方通道更新",DownloadAndApplyManagerUpdateViaApi,"Ghost");
    alternate.IsEnabled=CanUpdateManager;
    var manual=Btn("仅下载 ZIP",DownloadManagerUpdate,"Ghost");manual.IsEnabled=!managerUpdateBusy;
    var later=Btn("暂不更新",DeferManagerUpdate,"Ghost");later.IsEnabled=!managerUpdateBusy;
    details.Children.Add(H(download,alternate,manual,later));
    details.Children.Add(T("备用通道使用 GitHub 官方附件 API；可能仍走相同的下载网络，不保证提速。两种方式均验证 SHA-256。",11,muted));
   }
   Space(details,18);details.Children.Add(T("更新内容",17,ink,true));Space(details,8);details.Children.Add(T(release.Notes,13,muted));Space(details,12);
   details.Children.Add(Btn("查看此版本发布页",()=>OpenUrl(release.ReleaseUrl),"Ghost"));root.Children.Add(Card(details));Space(root,18);
  }
  managerProgress=T("",12,gold);root.Children.Add(managerProgress);
  if(managerDownload!=null){var apply=Btn("安装此更新并重启",()=>_=InstallDownloadedManagerAsync(false),"Primary");apply.IsEnabled=CanUpdateManager;root.Children.Add(Card(V(T("更新包已下载并通过 SHA256 校验",17,green,true),T(managerDownload,12,muted),H(apply,Btn("打开下载文件夹",()=>OpenUrl(Path.GetDirectoryName(managerDownload)!),"Ghost")))));Space(root,18);}
  root.Children.Add(T("如何更新",17,ink,true));Space(root,8);
  root.Children.Add(T("选择“下载并更新”并确认后，下载、校验、备份、替换与重新打开会自动完成。安装路径、快捷方式、设置和缓存继续使用；游戏与模组文件不受影响。文件替换失败时会尝试恢复上一版。",13,muted));
  Space(root,10);root.Children.Add(Btn("查看管理器更新记录",()=>{string folder=Path.Combine(installer.DataRoot,"管理器更新");if(Directory.Exists(folder))OpenUrl(folder);else toast.Text="还没有管理器更新记录。";},"Ghost"));
  string resultPath=Path.Combine(installer.DataRoot,"管理器更新","最近结果.json");if(File.Exists(resultPath)){try{using var result=System.Text.Json.JsonDocument.Parse(File.ReadAllText(resultPath));Space(root,12);root.Children.Add(T("最近一次："+result.RootElement.GetProperty("message").GetString(),13,muted));}catch{}}
  SetPage("检查更新",root,restore:ShowManagerUpdates);toast.Text="更新由你确认；会保留程序备份和个人数据。";
 }
 void DeferManagerUpdate(){if(managerUpdateBusy)return;if(backHistory.Count>0)GoBack();else ShowOverview();}
 bool CanUpdateManager=>!managerUpdateBusy&&!busy&&!transactionWriting&&!onlineBusy;
 bool ConfirmManagerUpdate()=>MessageBox.Show(this,"将下载并校验新版，自动保存当前程序备份，然后关闭此窗口、替换程序并重新打开。\n\n设置、缓存、原快捷方式与游戏模组会保留。现在更新吗？","更新管理器",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes;
 async void DownloadAndApplyManagerUpdate()=>await DownloadAndApplyManagerUpdateCoreAsync(false);
 async void DownloadAndApplyManagerUpdateViaApi()=>await DownloadAndApplyManagerUpdateCoreAsync(true);
 async Task DownloadAndApplyManagerUpdateCoreAsync(bool preferAssetApi)
 {
  if(!CanUpdateManager||managerRelease==null||!ConfirmManagerUpdate())return;
  try{var release=managerRelease;string destination=BundleInstaller.Child(installer.DataRoot,"管理器下载/"+release.FileName);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
   await DownloadManagerUpdateToAsync(release,destination,preferAssetApi);
   if(managerDownload!=null)await InstallDownloadedManagerAsync(true);
  }catch(Exception e){managerUpdateStatus="无法准备更新下载："+e.Message;ShowManagerUpdates();}
 }
 async Task InstallDownloadedManagerAsync(bool confirmed)
 {
  if(!CanUpdateManager||managerRelease==null||managerDownload==null||(!confirmed&&!ConfirmManagerUpdate()))return;
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
  if(managerUpdateBusy)return;managerUpdateBusy=true;managerUpdateChecked=true;managerRelease=null;managerDownload=null;managerUpdateStatus="正在检查正式发布…";managerUpdateCancellation=new();
  if(showPage)ShowManagerUpdates();
  try{
   managerRelease=await managerUpdates.CheckAsync(managerUpdateCancellation.Token);
   int comparison=ManagerUpdates.StableVersion(managerRelease.Version).CompareTo(ManagerUpdates.StableVersion(ManagerUpdates.CurrentVersion));
   managerUpdateStatus=comparison>0?"发现新版本，可以先查看更新内容，再决定是否下载。":comparison==0?"当前已是最新正式版本。":"当前版本高于仓库正式版本，无需降级。";
   if(managerUpdateButton!=null)managerUpdateButton.Content=comparison>0?"↑  发现 v"+managerRelease.Version:"检查更新";
  }catch(OperationCanceledException){managerUpdateStatus="检查已取消或超时，未改动程序。可以重试或打开发布页。";}
  catch(Exception e){managerUpdateStatus="检查未完成："+e.Message;}
  finally{managerUpdateBusy=false;managerUpdateCancellation.Dispose();managerUpdateCancellation=null;if(managerRelease==null&&managerUpdateButton!=null)managerUpdateButton.Content="检查更新";if(currentVisit?.Key=="检查更新")ShowManagerUpdates();}
 }
 async void DownloadManagerUpdate()
 {
  if(managerUpdateBusy||managerRelease==null||!ManagerUpdates.IsNewer(managerRelease,ManagerUpdates.CurrentVersion))return;
  var release=managerRelease;var save=new SaveFileDialog{Title="保存管理器更新包",FileName=release.FileName,Filter="ZIP 更新包|*.zip",DefaultExt=".zip",AddExtension=true};if(save.ShowDialog(this)!=true)return;
  await DownloadManagerUpdateToAsync(release,save.FileName);
 }
 async Task DownloadManagerUpdateToAsync(ManagerRelease release,string destination,bool preferAssetApi=false)
 {
  if(managerUpdateBusy)return;managerUpdateBusy=true;managerDownload=null;managerUpdateCancellation=new();managerUpdateStatus=preferAssetApi?"正在通过 GitHub 官方附件 API 下载并校验…":"正在下载，完成后自动校验…";ShowManagerUpdates();
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
