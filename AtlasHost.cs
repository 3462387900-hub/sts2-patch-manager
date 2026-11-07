using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace SpirePatch;

public partial class MainWindow
{
 WebView2? atlasView;
 bool atlasNavigated,atlasReady,atlasSnapshotBusy;
 readonly JsonSerializerOptions atlasJsonOptions=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};

 async Task<bool> ShowAtlasAsync()
 {
  try
  {
   atlasView??=new WebView2{HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Stretch};
   Content=atlasView;
   if(atlasView.CoreWebView2==null)
   {
    string userData=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SpirePatch","WebView2");
    Directory.CreateDirectory(userData);
    var environment=await CoreWebView2Environment.CreateAsync(null,userData);
    await atlasView.EnsureCoreWebView2Async(environment);
    var core=atlasView.CoreWebView2;
    core.Settings.AreDefaultContextMenusEnabled=false;
    core.Settings.IsStatusBarEnabled=false;
    core.NavigationStarting+=(_,e)=>
    {
     if(!Uri.TryCreate(e.Uri,UriKind.Absolute,out var uri)||
       !uri.Scheme.Equals("https",StringComparison.OrdinalIgnoreCase)||
       !uri.Host.Equals("spire-atlas",StringComparison.OrdinalIgnoreCase))
      e.Cancel=true;
     else atlasReady=false;
    };
    core.NewWindowRequested+=(_,e)=>e.Handled=true;
    core.WebMessageReceived+=async(_,e)=>await HandleAtlasMessageAsync(e.WebMessageAsJson);
    string folder=Path.Combine(AppContext.BaseDirectory,"Ui","Atlas");
    string index=Path.Combine(folder,"index.html");
    if(!File.Exists(index))throw new FileNotFoundException("发布目录缺少 Ui/Atlas/index.html。",index);
    core.SetVirtualHostNameToFolderMapping("spire-atlas",folder,CoreWebView2HostResourceAccessKind.DenyCors);
    core.Navigate("https://spire-atlas/index.html");
    atlasNavigated=true;
   }
   else if(!atlasNavigated)
   {
    string folder=Path.Combine(AppContext.BaseDirectory,"Ui","Atlas");
    if(!File.Exists(Path.Combine(folder,"index.html")))throw new FileNotFoundException("发布目录缺少 Ui/Atlas/index.html。");
    atlasView.CoreWebView2.SetVirtualHostNameToFolderMapping("spire-atlas",folder,CoreWebView2HostResourceAccessKind.DenyCors);
    atlasView.CoreWebView2.Navigate("https://spire-atlas/index.html");
    atlasNavigated=true;
   }
   if(atlasReady)await RefreshAtlasSnapshotAsync();
   return true;
  }
  catch(Exception error)
  {
   Content=shell;
   MessageBox.Show(this,
    "ATLAS Beta 无法启动。请确认已安装 Microsoft Edge WebView2 Runtime，并检查程序目录是否包含 Ui/Atlas/index.html。\n\n"+error.Message,
    "无法启动 ATLAS Beta",MessageBoxButton.OK,MessageBoxImage.Warning);
   return false;
  }
 }

 async Task HandleAtlasMessageAsync(string json)
 {
  try
  {
   using var document=JsonDocument.Parse(json);
   if(document.RootElement.ValueKind!=JsonValueKind.Object||
      !document.RootElement.TryGetProperty("type",out var typeValue)||
      typeValue.ValueKind!=JsonValueKind.String)return;
   switch(typeValue.GetString())
   {
    case "ready":
     atlasReady=true;
     await RefreshAtlasSnapshotAsync();
     break;
    case "refreshSnapshot":
     await RefreshAtlasSnapshotAsync(force:true);
     break;
    case "chooseGameFolder":
     await ChooseAtlasGameFolderAsync();
     break;
    case "launchGame":
     LaunchAtlasGame();
     break;
    case "switchToWpf":
     ChangeUiTheme(UiThemes.Workspace);
     break;
   }
  }
  catch(JsonException)
  {
   PostAtlasToast("界面请求格式无效。","warn");
  }
 }

 async Task ChooseAtlasGameFolderAsync()
 {
  if(busy||transactionWriting)
  {
   PostAtlasToast("管理器正在执行其他操作，请稍后再选择目录。","warn");
   return;
  }
  var dialog=new OpenFolderDialog{Title="选择《杀戮尖塔 2》游戏根目录",Multiselect=false};
  if(dialog.ShowDialog(this)!=true)return;
  try
  {
   UseGameRoot(dialog.FolderName);
   await RefreshAtlasSnapshotAsync(force:true);
  }
  catch(Exception error)
  {
   PostAtlasToast("游戏目录无效："+error.Message,"warn");
  }
 }

 void LaunchAtlasGame()
 {
  if(busy||transactionWriting)
  {
   PostAtlasToast("管理器正在执行其他操作，请稍后再启动游戏。","warn");
   return;
  }
  if(selectedInstallRoot==null)
  {
   PostAtlasToast("请先选择游戏目录。","warn");
   return;
  }
  try
  {
   bool started=GameEnvironment.Launch(selectedInstallRoot);
   PostAtlasToast(started?"游戏已启动。":"此目录的游戏已经在运行。");
  }
  catch(Exception error)
  {
   PostAtlasToast("启动游戏失败："+error.Message,"warn");
  }
 }

 async Task RefreshAtlasSnapshotAsync(bool force=false)
 {
  if(atlasView?.CoreWebView2==null||!atlasReady)return;
  if(atlasSnapshotBusy)return;
  if(transactionWriting)
  {
   PostAtlasToast("文件事务正在进行，暂时无法刷新本地状态。","warn");
   return;
  }
  atlasSnapshotBusy=true;
  PostAtlasMessage("refreshing");
  try
  {
   string? root=selectedInstallRoot;
   ModInventory? inventory=null;
   ScanReport? scan=null;
   if(root!=null)
   {
    int watchVersion=modChangeVersion;
    inventory=modInventory;
    scan=report;
    bool inventoryNeedsScan=force||inventory==null||modInventoryDirty;
    bool patchesNeedScan=force||scan==null||
     !Path.GetFullPath(scan.Directory).Equals(Path.GetFullPath(root),StringComparison.OrdinalIgnoreCase)||
     modInventoryDirty;
    if(inventoryNeedsScan)
     inventory=await Task.Run(()=>ModManager.Scan(root));
    if(patchesNeedScan)
     scan=await Task.Run(()=>Catalog.Scan(root));
    if(!root.Equals(selectedInstallRoot,StringComparison.OrdinalIgnoreCase))return;
    if(watchVersion!=modChangeVersion)
    {
     modInventoryDirty=true;
     PostAtlasToast("扫描期间检测到 MOD 文件变化，请再次刷新。","warn");
     return;
    }
    modInventory=inventory;
    report=scan;
    states=scan.Patches;
    demo=false;
    modInventoryDirty=false;
    modStatus=$"共 {inventory.Mods.Count} 项 · {inventory.Mods.Count(m=>m.Enabled)} 项启用 · {inventory.Mods.Count(m=>!m.Enabled)} 项停用";
   }

   var normalMods=inventory?.Mods
    .Where(m=>!ModPresentation.IsIndependentPatch(m))
    .GroupBy(m=>m.Id,StringComparer.OrdinalIgnoreCase)
    .Select(group=>group.First())
    .OrderBy(m=>m.Name,StringComparer.OrdinalIgnoreCase)
    .ToArray()??[];

   WorkshopModSources? workshopSources=root==null?null:WorkshopSources();
   WorkshopModUpdates? workshopUpdates=root==null?null:WorkshopUpdates();
   GitHubModUpdates? githubUpdates=root==null?null:GitHubUpdates();

   var mods=normalMods.Select(mod=>
   {
    ulong? workshopId=workshopSources?.Get(mod.Id);
    string? githubRepository=githubUpdates?.Repository(mod.Id);
    var githubResult=githubUpdates?.Cached(mod.Id);
    var workshopResult=workshopUpdates?.Cached(mod.Id);
    string? latest=githubResult?.Status=="有更新"?githubResult.LatestVersion:
     workshopResult?.Status=="有更新"?workshopResult.RemoteContentVersion:null;
    string updateStatus=githubResult?.Status=="有更新"?"GitHub：有更新":
     workshopResult?.Status=="有更新"?"创意工坊：有更新":
     githubResult!=null?"GitHub："+githubResult.Status:
     workshopResult!=null?"创意工坊："+workshopResult.Status:"尚未检查";
    long modified=0;
    try{modified=new DateTimeOffset(Directory.GetLastWriteTimeUtc(mod.Folder)).ToUnixTimeMilliseconds();}catch{}
    return new
    {
     id=mod.Id,name=mod.Name,cat="未分类",author=mod.Author,ver=mod.Version,
     latest,updateStatus,enabled=mod.Enabled,
     ws=workshopId?.ToString(System.Globalization.CultureInfo.InvariantCulture)??"",
     gh=githubRepository??"",size=0,sizeLabel="未统计",updated=modified,
     desc=mod.Description,deps=mod.Dependencies.Select(d=>d.Id).ToArray(),
     suggestion=(string?)null,path=mod.Folder,folder=Path.GetFileName(mod.Folder)
    };
   }).ToArray();

   var patches=(scan?.Patches??[])
    .Select(state=>
    {
     var patch=state.Patch;
     string[] targets=patch.Requirements??[];
     string[] targetNames=targets.Select(id=>normalMods.FirstOrDefault(m=>
      m.Id.Equals(id,StringComparison.OrdinalIgnoreCase))?.Name??id).ToArray();
     var installedPatch=inventory?.Mods.FirstOrDefault(m=>
      m.Id.Equals(patch.Id,StringComparison.OrdinalIgnoreCase));
     bool enabled=installedPatch?.Enabled??state.Status=="已安装";
     return new
     {
      id=patch.Id,name=patch.Name,category=patch.Category,version=patch.Version,
      modId=targets.FirstOrDefault()??"",targets,targetNames,enabled,
      status=state.Status,reason=state.Reason,filesMatch=state.FilesMatch,
      file=state.FilesMatch?"ok":"warn",runtime="untested",
      bound=targets.Length==0?"游戏文件":string.Join("、",targets),
      checked=scan!.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
     };
    }).ToArray();

   bool running=false;
   if(root!=null)try{running=GameEnvironment.Running(root);}catch{}
   string gameStatus=root==null?"尚未选择游戏目录":running?"游戏正在运行":"游戏已退出";
   var snapshot=new
   {
    schema=1,managerVersion=ManagerUpdates.CurrentVersion,
    gamePath=root??"",gameSelected=root!=null,gameRunning=running,gameStatus,
    modCount=normalMods.Length,enabledCount=normalMods.Count(m=>m.Enabled),
    inventoryIssueCount=inventory?.Issues.Count??0,
    scanIssueCount=scan?.Issues.Count??0,
    scanTime=scan?.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")??"",
    mods,patches,readOnly=true
   };
   PostAtlasSnapshot(snapshot);
  }
  catch(Exception error)
  {
   PostAtlasToast("读取本地状态失败："+error.Message,"warn");
  }
  finally
  {
   atlasSnapshotBusy=false;
  }
 }

 void PostAtlasMessage(string type)
 {
  if(atlasView?.CoreWebView2==null)return;
  atlasView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{type},atlasJsonOptions));
 }

 void PostAtlasSnapshot(object snapshot)
 {
  if(atlasView?.CoreWebView2==null)return;
  atlasView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{type="snapshot",payload=snapshot},atlasJsonOptions));
 }

 void PostAtlasToast(string message,string kind="")
 {
  if(atlasView?.CoreWebView2==null)return;
  atlasView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{type="toast",message,kind},atlasJsonOptions));
 }
}
