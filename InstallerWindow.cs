using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SpirePatch;
public partial class MainWindow
{
 readonly BundleInstaller installer=new(Path.Combine(AppContext.BaseDirectory,"数据"));
 PreparedPackage? preparedPackage;InstallResult? installPreview,lastInstallResult;
 string? selectedInstallRoot,localPackage;
 string installStatus="先选择自己的游戏目录，再检查将要修改的文件。";
 bool transactionWriting;
 CancellationTokenSource? preparationCancellation;
 void ShowInstaller()
 {
  var root=V(Header("INSTALL & ROLLBACK","安装与回滚","检查适配 → 查看改动 → 自动备份 → 安装并校验。"));Space(root,20);
  var folder=selectedInstallRoot??(!demo?report?.Directory:null);
  var location=V(Badge("真实文件操作",gold),T(folder??"尚未选择游戏目录",17,ink,true));Space(location,9);
  location.Children.Add(T("退出游戏后操作。可选择本版已审核的补丁，必要关联项会自动加入。回滚恢复最近一次安装批次。",13,muted));Space(location,14);
  location.Children.Add(H(Btn("选择游戏目录",ChooseInstallRoot),Btn("选择已下载的 ZIP",ChooseInstallZip,"Ghost")));
  if(localPackage!=null)location.Children.Add(T("本地修复包："+localPackage,11,muted));
  root.Children.Add(Card(location));Space(root,16);
  if(!busy){root.Children.Add(H(Btn(selectionMode?"调整补丁选择":"选择要处理的补丁",ChoosePatchSelection,"Primary"),Btn("使用完整整合包",()=>{ClearSelection();installPreview=null;lastInstallResult=null;installStatus=$"已切换为完整包检查，将核对本版 {Catalog.All.Length} 项修复。";ShowInstaller();},"Ghost")));Space(root,12);}
  if(selectionMode){root.Children.Add(T(activeSelection==null?"请选择补丁后再检查方案。":$"本次范围：{activeSelection.Included.Length} 项补丁，其中 {activeSelection.Automatic.Length} 项自动关联。",13,gold));Space(root,12);}
  root.Children.Add(T(installStatus,14,busy?gold:muted));Space(root,16);
  if(busy){
   root.Children.Add(new ProgressBar{IsIndeterminate=true,Height=5,Foreground=gold,Background=B("#EAF0E5")});Space(root,12);
   if(transactionWriting)root.Children.Add(T("正在执行文件事务，请等待完成。若写入失败，安装器会尝试恢复已改动文件。",13,gold));
   else root.Children.Add(Btn("取消准备",()=>preparationCancellation?.Cancel(),"Ghost"));
  }else{
   root.Children.Add(H(Btn("检查安装方案",()=>BeginPreparation(false),"Primary"),Btn("检查最近一次回滚",()=>BeginPreparation(true)),Btn("打开操作记录",()=>OpenLocalRecords(),"Ghost")));Space(root,18);
  }
  if(installPreview!=null&&!busy){
   var preview=installPreview;var view=new PlanPresentation(preview);
   var summary=V(T(view.Title,21,view.DocumentsOnly?green:ink,true),T(view.Summary,13,muted));Space(summary,12);
   if(preview.SelectedIds?.Length>0){summary.Children.Add(T("本次补丁："+string.Join("、",preview.SelectedIds.Select(id=>Catalog.All.First(p=>p.Id==id).Name)),12,gold));Space(summary,10);}
   summary.Children.Add(T(view.Detail,13,muted));
   if(!view.Undo&&view.MatchedPatches>0){Space(summary,10);summary.Children.Add(T($"{view.MatchedPatches} 项修复的功能文件已匹配，将直接跳过。",13,green));}
   if(preview.Backup.Length>0)summary.Children.Add(T("使用备份："+preview.Backup,12,gold));
   Space(summary,15);
   if(preview.Changed>0)summary.Children.Add(Btn(view.Confirm,ConfirmTransaction,"Primary"));
   else summary.Children.Add(Badge(view.Undo?"无需回滚":"无需重复安装或补齐说明",green));
   root.Children.Add(Card(summary));Space(root,16);
   AddPlanGroups(root,view.Undo?"将撤销的修复文件":"修复文件改动",view.Runtime,true);
   AddPlanGroups(root,"旧补丁清理",view.Legacy,true);
   AddPlanGroups(root,view.Undo?"将撤销的说明文件":"说明与验证记录",view.Documents,false);
   AddPlanGroups(root,"已匹配并跳过",view.Unchanged,false,true);
  }
  if(lastInstallResult!=null&&!busy){
   var result=lastInstallResult;var view=new PlanPresentation(result);
   var done=V(T(view.Completion,23,green,true),T(view.Summary,13,muted));Space(done,12);
   done.Children.Add(T(view.DocumentsOnly?"本次只处理说明与验证记录，不需要为此重新启动游戏。":result.Changed==0?"本次没有改动游戏文件。":"文件校验通过不代表所有游戏场景已验证。重新启动游戏后，再验证原来的问题。",13,muted));
   if(result.Backup.Length>0){Space(done,12);done.Children.Add(T("备份保留在："+result.Backup,12,gold));done.Children.Add(Btn("打开备份目录",()=>OpenUrl(result.Backup),"Ghost"));}
   root.Children.Add(Card(done,"#EFF5EC",24));
  }
  SetPage("安装与回滚",root);toast.Text="准备和预览只读游戏目录。只有点击确认按钮后才会安装或回滚。";
 }
 void AddPlanGroups(Panel root,string title,InstallFile[] files,bool expanded,bool skipped=false)
 {
  if(files.Length==0)return;
  var groups=files.GroupBy(PlanPresentation.Folder,StringComparer.OrdinalIgnoreCase).ToArray();var body=V();
  foreach(var group in groups){
   var rows=V(T(PlanPresentation.GroupName(group.Key),14,ink,true),T(group.Key,11,muted));Space(rows,8);
   foreach(var file in group){var row=T((skipped?"已匹配，跳过":PlanPresentation.Action(file))+" · "+Path.GetFileName(file.Relative.Replace('/',Path.DirectorySeparatorChar)),12,muted);row.ToolTip=file.Relative;rows.Children.Add(row);Space(rows,4);}
   body.Children.Add(Card(rows,"#FFFFFF",14));Space(body,8);
  }
  root.Children.Add(new Expander{Header=$"{title} · {groups.Length} 个模组目录 / {files.Length} 个文件",Content=body,IsExpanded=expanded});Space(root,12);
 }
 void ChooseInstallRoot()
 {
  if(busy)return;
  var dialog=new OpenFolderDialog{Title="选择包含 SlayTheSpire2.exe 的游戏根目录"};if(dialog.ShowDialog(this)!=true)return;
  installPreview=null;lastInstallResult=null;
  string folder=dialog.FolderName;
  try{UseGameRoot(folder);installPreview=null;lastInstallResult=null;installStatus="目录已选择，所有功能将使用此目录。";}catch(Exception e){installStatus=e.Message+" 当前目录未切换。";}ShowInstaller();
 }
 void ChooseInstallZip()
 {
  if(busy)return;var open=new OpenFileDialog{Title="选择本版支持的修复载荷 ZIP",Filter="ZIP 修复包|*.zip"};if(open.ShowDialog(this)!=true)return;
  localPackage=open.FileName;preparedPackage=null;installPreview=null;lastInstallResult=null;installStatus="已选择本地 ZIP；准备时会重新校验完整文件。";ShowInstaller();
 }
 async Task<PreparedPackage> GetPreparedPackageAsync(CancellationToken ct,Action<string>? showStatus=null)
 {
  if(preparedPackage!=null){await Task.Run(()=>BundleInstaller.VerifyStage(preparedPackage),ct);return preparedPackage;}
  string archive;
  if(localPackage!=null)archive=localPackage;
  else{
   Directory.CreateDirectory(installer.DataRoot);archive=BundleInstaller.Child(installer.DataRoot,"下载/"+BundleInstaller.SupportedBundle.FileName);Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
   if(!File.Exists(archive)||new FileInfo(archive).Length!=BundleInstaller.SupportedSize||Catalog.Hash(archive)!=BundleInstaller.SupportedHash){
    installStatus="正在下载本版已验证的修复包…";if(showStatus!=null)showStatus(installStatus);else ShowInstaller();
    await onlineSource.DownloadAsync(BundleInstaller.SupportedBundle,archive,new Progress<long>(n=>toast.Text=$"已下载 {n/1024} / {BundleInstaller.SupportedSize/1024} KB"),ct);
   }
  }
  installStatus="正在校验修复包和准备安装器…";if(showStatus!=null)showStatus(installStatus);else ShowInstaller();
  return preparedPackage=await installer.PrepareAsync(archive,ct);
 }
 async void BeginPreparation(bool rollback)
 {
  if(busy)return;
  if(onlineBusy){installStatus="在线请求仍在进行，请等待完成后检查。";ShowInstaller();return;}
  selectedInstallRoot??=!demo?report?.Directory:null;
  if(selectedInstallRoot==null){ChooseInstallRoot();if(selectedInstallRoot==null)return;}
  if(!rollback&&selectionMode&&activeSelection==null){ShowPatchSelection();return;}
  if(!rollback&&localPackage==null&&sourceCurrent&&onlineCatalog!=null&&!BundleInstaller.Supports(onlineCatalog.Bundle)){installStatus="在线包已经更新，本版尚未支持此安装包。请在「检查更新」升级管理器后重试。";ShowInstaller();return;}
  busy=true;installPreview=null;lastInstallResult=null;preparationCancellation=new(TimeSpan.FromMinutes(5));installStatus=rollback?"正在核对最近一次备份与当前文件…":"正在检查适用版本与原文件…";ShowInstaller();
  try{
   var package=await GetPreparedPackageAsync(preparationCancellation.Token);
   var result=await installer.RunAsync(package,selectedInstallRoot,rollback?"RollbackPreview":"Preview",selectedIds:!rollback&&selectionMode?activeSelection!.Requested:null);
   preparationCancellation.Token.ThrowIfCancellationRequested();if(!rollback&&selectionMode&&activeSelection!=null&&result.SelectedIds!=null)activeSelection=activeSelection with{Included=result.SelectedIds,Automatic=result.SelectedIds.Except(activeSelection.Requested).ToArray()};installPreview=result;installStatus=result.Changed==0?"检查完成，没有需要处理的文件。":new PlanPresentation(result).DocumentsOnly?"检查完成：仅有说明文件需要处理。":"检查通过。请查看具体改动后确认。";
  }catch(OperationCanceledException){installStatus="准备已取消或超时，没有修改游戏文件。";}
  catch(Exception e){preparedPackage=null;installStatus="检查未通过："+e.Message;}
  finally{busy=false;preparationCancellation.Dispose();preparationCancellation=null;ShowInstaller();}
 }
 async void ConfirmTransaction()
 {
  if(busy||installPreview==null||preparedPackage==null||installPreview.Changed==0)return;
  var preview=installPreview;bool undo=preview.Mode=="RollbackPreview";
  busy=true;transactionWriting=true;installPreview=null;lastInstallResult=null;installStatus=undo?"正在核对备份、恢复文件并校验…":new PlanPresentation(preview).DocumentsOnly?"正在记录说明文件改动并校验…":"正在再次核对原文件、保存备份并应用改动…";ShowInstaller();
  try{
   lastInstallResult=await installer.RunAsync(preparedPackage,preview.Root,undo?"Rollback":"Install",preview.Token,!undo&&preview.SelectedIds?.Length>0?preview.SelectedIds:null);
   ClearCoop();
   modInventory=null;modTogglePreview=null;modStatus="补丁文件已更新，请刷新模组列表。";installStatus="操作完成，详细结果如下。";
   try{var catalog=sourceCurrent?onlineCatalog:null;var definitions=catalog?.Patches??Catalog.Bundled;report=await Task.Run(()=>Catalog.Scan(preview.Root,definitions,catalog?.Commit));scannedCatalog=definitions;states=report.Patches;demo=false;}
   catch(Exception e){installStatus="文件操作已完成，但刷新概览失败："+e.Message;}
  }catch(Exception e){installStatus="操作未完成："+e.Message+"\n请保留操作记录和备份；重新检查后再继续。";}
  finally{InvalidateLifecycle();busy=false;transactionWriting=false;ShowInstaller();}
 }
 void OpenLocalRecords(){try{Directory.CreateDirectory(installer.DataRoot);OpenUrl(installer.DataRoot);}catch(Exception e){MessageBox.Show(this,e.Message,"无法打开记录目录");}}
 public async Task CaptureInstallerAsync(string zip,string fixture,string directory)
 {
  InstallerChecks.AssertFixture(fixture);Directory.CreateDirectory(directory);selectedInstallRoot=fixture;preparedPackage=await installer.PrepareAsync(zip);
  async Task Shot(string name){await Task.Delay(150);UpdateLayout();var image=new RenderTargetBitmap((int)shell.ActualWidth,(int)shell.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(shell);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var output=File.Create(Path.Combine(directory,name+".png"));encoder.Save(output);}
  IEnumerable<Button> Buttons(DependencyObject parent){for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is Button b)yield return b;foreach(var nested in Buttons(child))yield return nested;}}
  async Task Click(string label){Buttons(shell).Single(b=>b.Content?.ToString()==label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));while(busy)await Task.Delay(70);}
  installPreview=await installer.RunAsync(preparedPackage,fixture,"Preview");installStatus="隔离目录检查通过，尚未执行安装。";ShowInstaller();await Shot("安装确认");
  await Click(new PlanPresentation(installPreview!).Confirm);if(lastInstallResult?.Message!="Installed")throw new InvalidOperationException("UI installation did not complete: "+installStatus);await Shot("安装完成");
  await Click("检查最近一次回滚");if(installPreview?.Mode!="RollbackPreview")throw new InvalidOperationException("UI rollback preview did not complete: "+installStatus);await Shot("回滚确认");
  await Click(new PlanPresentation(installPreview!).Confirm);if(lastInstallResult?.Message!="RolledBack")throw new InvalidOperationException("UI rollback did not complete: "+installStatus);await Shot("回滚完成");
  File.WriteAllText(Path.Combine(directory,"界面交互验证.txt"),"PASS 实际确认安装按钮完成隔离目录写入及备份。\nPASS 实际回滚检查、确认按钮完成原文件恢复。\n");
 }
}
