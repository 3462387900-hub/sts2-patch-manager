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
  if(managerUpdateBusy){Space(summary,10);summary.Children.Add(Btn("取消",()=>managerUpdateCancellation?.Cancel(),"Ghost"));}
  root.Children.Add(Card(summary));Space(root,20);
  if(managerRelease is { } release){
   bool newer=ManagerUpdates.IsNewer(release,ManagerUpdates.CurrentVersion);
   var details=V(T("正式版本  "+release.Version,22,ink,true),T("发布于 "+release.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")+" · Windows x64 · "+(release.Size/1024d/1024).ToString("F1")+" MB",12,muted));Space(details,18);
   details.Children.Add(T("更新内容",17,ink,true));Space(details,8);details.Children.Add(T(release.Notes,13,muted));Space(details,20);
   if(newer){var download=Btn("下载更新包",DownloadManagerUpdate,"Primary");download.IsEnabled=!managerUpdateBusy;var later=Btn("暂不更新",DeferManagerUpdate,"Ghost");later.IsEnabled=!managerUpdateBusy;details.Children.Add(H(download,later));}
   details.Children.Add(Btn("查看此版本发布页",()=>OpenUrl(release.ReleaseUrl),"Ghost"));root.Children.Add(Card(details));Space(root,18);
  }
  managerProgress=T("",12,gold);root.Children.Add(managerProgress);
  if(managerDownload!=null){root.Children.Add(Card(V(T("更新包已下载并通过 SHA256 校验",17,green,true),T(managerDownload,12,muted),Btn("打开下载文件夹",()=>OpenUrl(Path.GetDirectoryName(managerDownload)!),"Primary"))));Space(root,18);}
  root.Children.Add(T("如何更新",17,ink,true));Space(root,8);
  root.Children.Add(T("完整解压更新包到新文件夹 → 关闭旧版 → 运行新版 SpirePatch.exe。需要保留设置与下载缓存时，在程序关闭后复制旧版的“数据”文件夹到新版。旧版会保留，游戏目录与补丁备份无需迁移。",13,muted));
  SetPage("检查更新",root,restore:ShowManagerUpdates);toast.Text="检查不会下载或安装；下载完成后由你解压运行新版。";
 }
 void DeferManagerUpdate(){if(managerUpdateBusy)return;if(backHistory.Count>0)GoBack();else ShowOverview();}
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
 async Task DownloadManagerUpdateToAsync(ManagerRelease release,string destination)
 {
  if(managerUpdateBusy)return;managerUpdateBusy=true;managerDownload=null;managerUpdateCancellation=new();managerUpdateStatus="正在下载，完成后自动校验…";ShowManagerUpdates();
  try{
   await managerUpdates.DownloadAsync(release,destination,new Progress<long>(n=>{if(managerUpdateBusy&&currentVisit?.Key=="检查更新"&&managerProgress!=null)managerProgress.Text=$"已下载 {n/1024d/1024:F1} / {release.Size/1024d/1024:F1} MB";}),managerUpdateCancellation.Token);
   managerDownload=destination;managerUpdateStatus="下载完成，当前程序仍保持原版本；完整解压后运行新版。";
  }catch(OperationCanceledException){managerUpdateStatus="下载已取消或超时，未改动当前程序。";}
  catch(Exception e){managerUpdateStatus="下载未完成："+e.Message;}
  finally{managerUpdateBusy=false;managerUpdateCancellation.Dispose();managerUpdateCancellation=null;if(currentVisit?.Key=="检查更新")ShowManagerUpdates();}
 }
}
