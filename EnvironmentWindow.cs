using System.IO;
using System.Windows;

namespace SpirePatch;
public partial class MainWindow
{
 bool rememberEnvironment;
 string SettingsPath=>Path.Combine(installer.DataRoot,"偏好.json");
 List<GameCandidate> gameCandidates=[];
 string locateStatus="检查 Steam 库、上次使用的目录和常用安装位置。";
 void UseGameRoot(string path)
 {
  string root=GameEnvironment.Root(path);
  if(!root.Equals(selectedInstallRoot,StringComparison.OrdinalIgnoreCase)){
   selectedInstallRoot=root;report=null;states=[];ClearCoop();demo=false;ClearSelection();installPreview=null;lastInstallResult=null;modInventory=null;modTogglePreview=null;modStatus="目录已切换，请刷新模组列表。";InvalidateLifecycle("已切换游戏目录，请重新扫描补丁健康状态。");
  }
  if(rememberEnvironment)try{GameEnvironment.SaveRoot(SettingsPath,root);}catch(Exception e){toast.Text="目录已选择，但未能记住路径："+e.Message;}
  folderLabel.Text=root;UpdateGlobalControls();
 }
 async Task LocateGameAsync(bool startup=false)
 {
  if(busy||transactionWriting)return;
  busy=true;locateStatus="正在查找游戏目录…";if(!startup)ShowLocations();
  try{
   string? saved=GameEnvironment.LoadRoot(SettingsPath);
   if(startup&&saved!=null){UseGameRoot(saved);locateStatus="已恢复上次使用的目录。";return;}
   gameCandidates=await Task.Run(()=>GameEnvironment.Discover(saved));
   if(gameCandidates.Count==1){UseGameRoot(gameCandidates[0].Directory);locateStatus="已定位游戏目录："+gameCandidates[0].Directory;}
   else locateStatus=gameCandidates.Count==0?"没有找到可用安装，请手动选择游戏目录。":"发现多个安装目录，请选择本次使用的一个。";
  }catch(Exception e){locateStatus="自动查找未完成："+e.Message;}
  finally{busy=false;UpdateGlobalControls();if(!startup||gameCandidates.Count>1)ShowLocations();else{folderLabel.Text=selectedInstallRoot??"尚未选择游戏目录";toast.Text=locateStatus;}}
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
  if(busy||transactionWriting)return;if(selectedInstallRoot==null){LocateGame();return;}
  try{bool started=GameEnvironment.Launch(selectedInstallRoot);toast.Text=started?"已启动游戏。":"此目录的游戏已经在运行。";}
  catch(Exception e){MessageBox.Show(this,e.Message,"无法启动游戏");}
 }
}
