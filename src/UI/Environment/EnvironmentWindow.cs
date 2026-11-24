using System.IO;
using System.Windows;

namespace SpirePatch;
public partial class MainWindow
{
 bool rememberEnvironment;
 string SettingsPath=>Path.Combine(installer.DataRoot,"偏好.json");
 List<GameCandidate> gameCandidates=[];
 string locateStatus="检查 Steam 库、上次使用的目录和常用安装位置。";
 GameEnvironmentSessionService? environmentSession;
 GameEnvironmentSessionService EnvironmentSession
 {
  get
  {
   if(environmentSession!=null)return environmentSession;
   EnsureModWatcherEvents();
   environmentSession=new GameEnvironmentSessionService(SettingsPath,modFileWatcher,selectedInstallRoot);
   environmentSession.DirectoryChanging+=(_,e)=>
   {
    Interlocked.Increment(ref neonRootEpoch);
    InvalidateNeonRecoveryCache();
    neonPreviewPreparationCancellation?.Cancel();
    NeonCancelOwnedPreview();
    CancelModUpdateCheck();
    modInventoryRefresh?.Cancel();
    localZipOperationCancellation?.Cancel();
    DiscardLocalZipPreview();
    try { neonFixtureLocalZipSession?.DiscardPreview(); } catch { }
    neonFixtureLocalZipSession=null;
    neonFixtureLocalZipRoot=null;
    localZipSession=null;
    localZipSessionRoot=null;
    localZipRecoveryBlocked=false;
    localZipRecoveryNotice=null;
   };
   environmentSession.DirectoryChanged+=(_,e)=>
   {
    selectedInstallRoot=e.CurrentRoot;
    neonRecoveryStates.Remove(e.CurrentRoot);
    watchedGameRoot=e.CurrentRoot;
    if(modInventoryRefreshRunning)pendingModInventoryRefreshRoot=e.CurrentRoot;
    modInventoryDirty=UiThemes.IsNeon;modWatchOverflow=false;modWatchTreeChanged=false;modChangeVersion=0;
    modInventoryCleanVersion=0;
    report=null;states=[];ClearCoop();demo=false;ClearSelection();installPreview=null;lastInstallResult=null;
    modInventory=null;modTogglePreview=null;orphanPatchNotice=null;
    modStatus="目录已切换，请刷新模组列表。";
    InvalidateLifecycle("已切换游戏目录，请重新扫描补丁健康状态。");
   };
   return environmentSession;
  }
 }
 void UseGameRoot(string path)
 {
  var result=EnvironmentSession.SelectRoot(path,rememberEnvironment);
  watchedGameRoot=result.Root;
  if(result.PreferenceError is not null)
   toast.Text="目录已选择，但未能记住路径："+result.PreferenceError;
  folderLabel.Text=result.Root;UpdateGlobalControls();
 }
 async Task LocateGameAsync(bool startup=false)
 {
  if(busy||transactionWriting)return;
  busy=true;locateStatus="正在查找游戏目录…";if(!startup&&!UiThemes.IsNeon)ShowLocations();
  try{
   string? saved=EnvironmentSession.LoadRememberedRoot();
   if(startup&&saved!=null){UseGameRoot(saved);locateStatus="已恢复上次使用的目录。";return;}
   gameCandidates=await EnvironmentSession.DiscoverAsync(saved);
   if(gameCandidates.Count==1){UseGameRoot(gameCandidates[0].Directory);locateStatus="已定位游戏目录："+gameCandidates[0].Directory;}
   else locateStatus=gameCandidates.Count==0?"没有找到可用安装，请手动选择游戏目录。":"发现多个安装目录，请选择本次使用的一个。";
  }catch(Exception e){locateStatus="自动查找未完成："+e.Message;}
  finally
  {
   busy=false;UpdateGlobalControls();
   if(UiThemes.IsNeon&&neonWorkbenchView!=null)
   {
    folderLabel.Text=selectedInstallRoot??"尚未选择游戏目录";
    RefreshNeonWorkbench(preserveViewport:true);
    if(gameCandidates.Count>1)ShowNeonGameRootPickerModal(gameCandidates);
    else if(gameCandidates.Count==0&&selectedInstallRoot==null)neonWorkbenchView.ShowToast(locateStatus,NeonTone.Amber);
   }
   else if(!startup||gameCandidates.Count>1)ShowLocations();
   else{folderLabel.Text=selectedInstallRoot??"尚未选择游戏目录";toast.Text=locateStatus;}
  }
 }
 async void LocateGame()=>await LocateGameAsync();
 void ShowLocations()
 {
  var root=V(Header("FIND YOUR GAME","定位游戏目录","路径供模组管理、补丁安装和启动游戏共同使用。"));Space(root,20);
  root.Children.Add(T(locateStatus,14,muted));Space(root,16);
  var search=Btn("重新自动查找",LocateGame,"Primary");search.IsEnabled=!busy;
  root.Children.Add(H(search,Btn("手动选择目录",ChooseFolder,"Ghost")));Space(root,20);
  foreach(var candidate in gameCandidates){var item=V(T(candidate.Directory,16,ink,true),T(candidate.Source,12,muted));Space(item,12);item.Children.Add(Btn("使用此目录",()=>{if(busy)return;try{UseGameRoot(candidate.Directory);ShowMods();RefreshMods();}catch(Exception e){toast.Text=e.Message;}},"Primary"));root.Children.Add(Card(item));Space(root,10);}
  if(selectedInstallRoot!=null){root.Children.Add(T("当前目录："+selectedInstallRoot,13,gold));Space(root,14);root.Children.Add(H(Btn("管理此目录的模组",()=>{ShowMods();RefreshMods();},"Primary"),Btn("打开游戏目录",()=>OpenUrl(selectedInstallRoot),"Ghost")));}
  SetPage("游戏目录",root,restore:ShowLocations);toast.Text="自动定位不会修改游戏文件；切换目录会清除旧的扫描和操作预览。";
 }
 void LaunchGame()
 {
  if(busy||transactionWriting)return;
  if(selectedInstallRoot==null)
  {
   if(UiThemes.IsNeon&&neonWorkbenchView!=null){neonWorkbenchView.ShowToast("请先选择或自动定位游戏目录。",NeonTone.Amber);_=DiscoverNeonGameRootsAsync();}
   else LocateGame();
   return;
  }
  try
  {
   bool started=GameEnvironment.Launch(selectedInstallRoot);
   string msg=started?"已启动《杀戮尖塔 2》。":"此目录的游戏已经在运行。";
   toast.Text=msg;
   if(UiThemes.IsNeon)neonWorkbenchView?.ShowToast(msg,started?NeonTone.Sage:NeonTone.Amber);
  }
  catch(Exception e)
  {
   if(UiThemes.IsNeon&&neonWorkbenchView!=null)neonWorkbenchView.ShowToast("无法启动游戏："+e.Message,NeonTone.Cinnabar);
   else MessageBox.Show(this,e.Message,"无法启动游戏");
  }
 }
}
