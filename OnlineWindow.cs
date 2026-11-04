using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using Microsoft.Win32;

namespace SpirePatch;
public partial class MainWindow
{
 readonly OnlineSource onlineSource=new();
 OnlineCatalog? onlineCatalog;bool onlineBusy,sourceCurrent;
 string onlineStatus="正在连接公开补丁仓库…";
 CancellationTokenSource? onlineCancellation;
 void OpenUrl(string url){try{Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}catch(Exception e){MessageBox.Show(this,e.Message,"无法打开链接");}}
 void ShowOnline()
 {
  var root=V(Header("PUBLIC PATCH SOURCE","在线补丁","从补丁自身携带的说明中，查看用途、适用范围和验证结果。"));Space(root,18);
  var info=V(T("3462387900-hub / sts2-patches",17,ink,true),T(onlineStatus,13,onlineBusy?gold:muted));Space(info,14);
  var refresh=Btn(onlineBusy?"正在处理…":"刷新补丁源",RefreshOnline,"Primary");refresh.IsEnabled=!onlineBusy;
  info.Children.Add(H(Btn("扫描游戏并推荐",ChooseFolder,"Primary"),refresh,Btn("打开 GitHub 仓库",()=>OpenUrl(OnlineSource.RepositoryUrl),"Ghost")));
  if(onlineBusy)info.Children.Add(Btn("取消",()=>onlineCancellation?.Cancel(),"Ghost"));
  root.Children.Add(Card(info));Space(root,18);
  if(onlineCatalog!=null)
  {
   var bundle=onlineCatalog.Bundle;
   var download=Btn("下载整合包并校验",DownloadOnline,"Primary");download.IsEnabled=!onlineBusy&&sourceCurrent;
   string support=BundleInstaller.Supports(bundle)?$"本版可安装 {Catalog.All.Length} 项已审核补丁。下载完整包，写入只限所选项目与必要关联项。":"在线安装包已更新，请先在「检查更新」升级管理器，再安装新增补丁。";
   string usage=bundle.InstallMode=="manager_payload"?"这是管理器专用载荷包，请在「安装与回滚」使用；离线时可导入 ZIP。":"可在管理器安装，也可解压后运行包内的 Install.cmd。";
   root.Children.Add(Card(V(T("当前下载：修复整合包",19,ink,true),T(bundle.FileName,13,gold),T(support+"\n"+usage,13,muted),H(Btn("进入安装与回滚",ShowInstaller,"Primary"),download,Btn("发布说明",()=>OpenUrl(bundle.ReleaseUrl),"Ghost")))));
   Space(root,22);root.Children.Add(T($"仓库收录 {onlineCatalog.Patches.Count} 项 · "+onlineCatalog.RetrievedAt.ToLocalTime().ToString("MM-dd HH:mm"),16,ink,true));Space(root,10);
   foreach(var patch in onlineCatalog.Patches)
   {
    string state=demo?"尚未扫描本地文件":report?.CatalogRevision!=onlineCatalog.Commit?"目录已更新，请重新扫描":states.FirstOrDefault(s=>s.Patch.Id==patch.ComponentId)?.Status??"未收录本地检查";
    if(state=="已安装")state="修复文件一致";
    var item=V(T(patch.Title,16,ink,true),T(patch.Summary,13,muted));Space(item,8);
    item.Children.Add(H(Badge(state),Btn("查看随补丁说明 →",()=>ShowOnlineDetail(patch),"Ghost")));
    root.Children.Add(Card(item,"#D9182942",18));Space(root,9);
   }
  }
  SetPage("在线补丁",root);toast.Text="公开访问无需 GitHub 登录。下载不会执行安装；安装器会再次核对原模组文件。";
 }
 void ShowOnlineDetail(OnlinePatch patch)
 {
  var m=patch.Metadata;var root=V(Btn("← 返回在线补丁",ShowOnline,"Ghost"));Space(root,16);
  root.Children.Add(Header(patch.ComponentId,patch.Title,patch.Summary));Space(root,20);
  void Section(string title,string body){root.Children.Add(T(title,17,ink,true));Space(root,7);root.Children.Add(T(body,13,muted));Space(root,18);}
  Section("问题与依据",patch.Detail);
  Section("涉及 MOD",string.Join("\n",m.GetProperty("components").EnumerateArray().Select(c=>$"{c.GetProperty("display_name").GetString()} · {Label(c.GetProperty("role").GetString())}\n{c.GetProperty("explanation").GetString()}")));
  Section("适用构建",string.Join("\n",m.GetProperty("compatibility").GetProperty("profiles").EnumerateArray().Select(p=>"游戏 "+p.GetProperty("game_version_display").GetString()+"；"+string.Join("、",p.GetProperty("required_mods").EnumerateArray().Select(x=>x.GetProperty("mod_id").GetString()+" "+x.GetProperty("version_display").GetString()))).Distinct())+"\n最终以安装器的精确文件校验为准。");
  Section("玩法影响",m.GetProperty("changes").GetProperty("gameplay_impact").GetString()!);
  Section("联机与存档",m.GetProperty("multiplayer").GetProperty("explanation").GetString()+"\n"+m.GetProperty("saved_runs").GetProperty("explanation").GetString());
  Section("验证情况",string.Join("\n",m.GetProperty("validation").EnumerateArray().Select(t=>$"{Label(t.GetProperty("kind").GetString())} · {Label(t.GetProperty("result").GetString())}：{t.GetProperty("summary").GetString()}")));
  Section("本版管理器支持", "已审核整合包内的补丁可勾选安装，关联修复会一起处理，回滚按最近一次批次执行。随补丁保留的下方说明可能描述早期手动安装方式。");
  Section("限制",string.Join("\n",patch.Limits.Select(x=>"• "+x)));
  Section("撤销",m.GetProperty("rollback").GetProperty("description").GetString()!);
  root.Children.Add(Btn("返回下载整合包",ShowOnline,"Primary"));SetPage("在线补丁",root,"online-detail:"+patch.Id,()=>ShowOnlineDetail(onlineCatalog?.Patches.FirstOrDefault(p=>p.Id==patch.Id)??patch));
 }
 static string Label(string? s)=>s switch{"affected"=>"受影响模组","trigger"=>"触发条件涉及","dependency"=>"运行前置","companion"=>"需联动处理","code_review"=>"代码审查","automated"=>"自动化验证","local_game"=>"本机游戏","multiplayer"=>"联机测试","pass"=>"通过","partial"=>"部分验证","not_run"=>"未测试","fail"=>"失败",_=>s??"未知"};
 async void RefreshOnline()
 {
  if(busy||onlineBusy)return;onlineBusy=true;sourceCurrent=false;onlineCancellation=new(TimeSpan.FromMinutes(2));onlineStatus="正在读取仓库索引和每项补丁的说明…";ShowOnline();
  try{onlineCatalog=await onlineSource.LoadAsync(onlineCancellation.Token);sourceCurrent=true;onlineStatus=$"已连接 · {onlineCatalog.Patches.Count} 项补丁 · 无需登录";}
  catch(OperationCanceledException){onlineStatus="连接已取消或超时。可以重试或打开仓库网页。";}
  catch(Exception e){onlineStatus="连接未完成："+e.Message+"。若存在旧列表，仅供查看，请刷新后下载。";}
  finally{onlineBusy=false;onlineCancellation.Dispose();onlineCancellation=null;if(currentVisit?.Key=="在线补丁")ShowOnline();}
 }
 async void DownloadOnline()
 {
  if(onlineBusy||onlineCatalog==null||!sourceCurrent)return;
  var save=new SaveFileDialog{Title="保存修复整合包",FileName=onlineCatalog.Bundle.FileName,Filter="ZIP 修复包|*.zip"};if(save.ShowDialog(this)!=true)return;
  onlineBusy=true;onlineCancellation=new(TimeSpan.FromMinutes(5));onlineStatus="正在下载，完成后自动核对 SHA256…";ShowOnline();
  try{
   await onlineSource.DownloadAsync(onlineCatalog.Bundle,save.FileName,new Progress<long>(n=>toast.Text=$"已下载 {n/1024} / {onlineCatalog.Bundle.Size/1024} KB"),onlineCancellation.Token);
   onlineStatus="下载完成且 SHA256 一致。尚未安装到游戏。";
   MessageBox.Show(this,"已下载并校验：\n"+save.FileName+"\n\n"+(onlineCatalog.Bundle.InstallMode=="manager_payload"?"请进入「安装与回滚」，导入此 ZIP 并选择自己的游戏目录。此包由管理器执行安装。":"请完整解压后运行 Install.cmd，选择自己的游戏目录。"),"下载完成",MessageBoxButton.OK,MessageBoxImage.Information);
  }catch(OperationCanceledException){onlineStatus="下载已取消或超时，未保留未完成文件。";}
  catch(Exception e){onlineStatus="下载未完成："+e.Message;}
  finally{onlineBusy=false;onlineCancellation.Dispose();onlineCancellation=null;if(currentVisit?.Key=="在线补丁")ShowOnline();}
 }
 public async Task CaptureOnlineAsync(string directory)
 {
  Directory.CreateDirectory(directory);onlineCatalog=await onlineSource.LoadAsync();sourceCurrent=true;onlineStatus="已连接公开补丁源 · 无需登录";ShowOnline();await Task.Delay(300);UpdateLayout();
  void Shot(string file){var bitmap=new RenderTargetBitmap((int)shell.ActualWidth,(int)shell.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(shell);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(Path.Combine(directory,file));encoder.Save(output);}
  Shot("online-source.png");ShowOnlineDetail(onlineCatalog.Patches.First(p=>p.ComponentId=="Act4FinalAscent"));await Task.Delay(100);UpdateLayout();Shot("online-detail.png");
 }
}
