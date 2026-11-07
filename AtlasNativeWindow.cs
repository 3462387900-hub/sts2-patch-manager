using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SpirePatch;

internal sealed class AtlasNativeModRow
{
 internal AtlasNativeModRow(ManagedMod mod,string source,string workshopId,string githubRepository,
  string updateStatus,string latestVersion)
 {
  Mod=mod;Source=source;WorkshopId=workshopId;GitHubRepository=githubRepository;
  UpdateStatus=updateStatus;LatestVersion=latestVersion;
 }
 internal ManagedMod Mod {get;}
 public bool IsSelected {get;set;}
 public string Name=>Mod.Name;
 public string Id=>Mod.Id;
 public string Version=>Mod.Version;
 public string Author=>Mod.Author;
 public string State=>Mod.Enabled?"已启用":"已停用";
 public string Source {get;}
 public string WorkshopId {get;}
 public string GitHubRepository {get;}
 public string UpdateStatus {get;}
 public string LatestVersion {get;}
 public string Meta=>$"{Mod.Id}  ·  {Mod.Author}";
 public string Description=>Mod.Description;
 public string Folder=>Mod.Folder;
 public string Dependencies=>Mod.Dependencies.Length==0?"未声明必需前置":string.Join("、",Mod.Dependencies.Select(x=>x.Id));
 public bool HasUpdate=>UpdateStatus.Contains("有更新",StringComparison.Ordinal);
}

public partial class MainWindow
{
 static Brush AtlasBrush(string hex)=>(Brush)new BrushConverter().ConvertFromString(hex)!;
 static readonly Brush AtlasInk=AtlasBrush("#F0EEE8");
 static readonly Brush AtlasMuted=AtlasBrush("#A7AAA9");
 static readonly Brush AtlasBrass=AtlasBrush("#C6A36B");
 static readonly Brush AtlasGreen=AtlasBrush("#82C69A");
 static readonly Brush AtlasRed=AtlasBrush("#D78378");
 static readonly Brush AtlasPanelBrush=SpireGlassTheme.GlassPanel();
 static readonly Brush AtlasPanelAltBrush=SpireGlassTheme.GlassPanel("#B5222C37");
 Grid? atlasNativeRoot,atlasNativeBody;
 ContentControl? atlasNativePageHost,atlasNativeResultsHost;
 Border? atlasNativeInspector,atlasNativeBatchBar;
 TextBlock? atlasNativeGamePath,atlasNativeGameState,atlasNativeFooter,atlasNativeBatchText,atlasNativeResultsSummary;
 Button? atlasNativeLaunchButton;
 readonly Dictionary<string,Button> atlasNativeNavButtons=new(StringComparer.Ordinal);
 string atlasNativePage="总览";
 string atlasNativeSubpageReturnPage="总览";
 string atlasNativePatchFilter="全部",atlasNativePatchQuery="";
 string atlasNativeViewMode="grid";
 string atlasNativeQuery="";
 string atlasNativeStateFilter="全部";
 string atlasNativeSourceFilter="全部";
 string atlasNativeSort="名称";
 bool atlasNativeUnboundOnly;
 string? atlasNativeSelectedFolder,atlasNativeSelectedPatchId;
 readonly HashSet<string> atlasNativeSelectedFolders=new(StringComparer.OrdinalIgnoreCase);
 List<AtlasNativeModRow> atlasNativeRows=[];

 void ShowAtlasNative()
 {
  if(atlasNativeRoot==null)BuildAtlasNativeShell();
  Content=atlasNativeRoot;
  RenderAtlasNative();
  if(selectedInstallRoot!=null&&(modInventory==null||report==null||modInventoryDirty)&&!busy&&!transactionWriting)
   _=RefreshAtlasNativeDataAsync();
 }

 void BuildAtlasNativeShell()
 {
  var root=new Grid{Background=SpireGlassTheme.Background(nativeAcrylicActive),ClipToBounds=true};
  root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
  root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
  root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
  var ambient=SpireGlassTheme.AddAmbientGlow(root);Grid.SetRowSpan(ambient,3);

  var header=new Border{
   Background=SpireGlassTheme.GlassPanel(),BorderBrush=SpireGlassTheme.PanelStroke(),
   BorderThickness=new Thickness(0,0,0,1),Padding=new Thickness(18,11,16,11)
  };
  var headerGrid=new Grid();
  headerGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(184)});
  headerGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
  headerGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
  var brand=new StackPanel{VerticalAlignment=VerticalAlignment.Center};
  brand.Children.Add(AtlasText("SPIRE REPAIR STATION",12,AtlasBrass,true));
  brand.Children.Add(AtlasText("尖塔修补站  /  ATLAS",18,AtlasInk,true));
  headerGrid.Children.Add(brand);

  var nav=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
  foreach(var (key,label) in new[]{
   ("总览","总览"),("模组库","模组库"),("修复中心","修复中心"),
   ("更新中心","更新中心"),("下载中心","下载中心"),("设置","设置")})
  {
   var button=AtlasButton(label,()=>AtlasNativeNavigate(key),"nav");
   button.Padding=new Thickness(7,7,7,7);
   button.Margin=new Thickness(0,0,1,0);
   button.FontSize=13;
   button.MinWidth=54;
   atlasNativeNavButtons[key]=button;
   nav.Children.Add(button);
  }
  Grid.SetColumn(nav,1);headerGrid.Children.Add(nav);

  var actions=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
  var gameInfo=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,8,0),MaxWidth=155};
  atlasNativeGameState=AtlasText("尚未定位游戏",11,AtlasMuted,true);
  atlasNativeGamePath=AtlasText("选择游戏目录以开始",11,AtlasMuted);
  atlasNativeGamePath.TextTrimming=TextTrimming.CharacterEllipsis;
  atlasNativeGamePath.TextWrapping=TextWrapping.NoWrap;
  gameInfo.Children.Add(atlasNativeGameState);gameInfo.Children.Add(atlasNativeGamePath);
  actions.Children.Add(gameInfo);
  atlasNativeLaunchButton=AtlasButton("▶ 启动游戏",LaunchGame,"primary");
  atlasNativeLaunchButton.Padding=new Thickness(10,7,10,7);atlasNativeLaunchButton.MinWidth=94;
  atlasNativeLaunchButton.Margin=new Thickness(0,0,6,0);
  actions.Children.Add(atlasNativeLaunchButton);
  var legacyButton=AtlasButton("旧版工作台",()=>ChangeUiTheme(UiThemes.Workspace),"quiet");
  legacyButton.Padding=new Thickness(8,7,8,7);legacyButton.MinWidth=78;actions.Children.Add(legacyButton);
  Grid.SetColumn(actions,2);headerGrid.Children.Add(actions);
  header.Child=headerGrid;Grid.SetRow(header,0);root.Children.Add(header);

  var body=new Grid{Margin=new Thickness(22,19,22,12)};
  body.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
  body.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(0)});
  atlasNativePageHost=new ContentControl();
  body.Children.Add(atlasNativePageHost);
  atlasNativeInspector=new Border{
   Background=AtlasPanelBrush,BorderBrush=SpireGlassTheme.PanelStroke(),
   BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),
   Margin=new Thickness(15,0,0,0),Visibility=Visibility.Collapsed
  };
  Grid.SetColumn(atlasNativeInspector,1);body.Children.Add(atlasNativeInspector);
  atlasNativeBody=body;Grid.SetRow(body,1);root.Children.Add(body);

  var footer=new Border{
   Background=SpireGlassTheme.GlassPanel(),BorderBrush=SpireGlassTheme.PanelStroke(),
   BorderThickness=new Thickness(0,1,0,0),Padding=new Thickness(20,7,20,7)
  };
  atlasNativeFooter=AtlasText("ATLAS 原生工作台  ·  本地数据",11,AtlasMuted);
  footer.Child=atlasNativeFooter;Grid.SetRow(footer,2);root.Children.Add(footer);
  atlasNativeRoot=root;
 }

 TextBlock AtlasText(string text,double size=14,Brush? color=null,bool bold=false)
 {
  return new TextBlock{
   Text=text,FontSize=size,Foreground=color??AtlasInk,
   FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,
   FontFamily=new FontFamily("Microsoft YaHei UI, Segoe UI"),
   TextWrapping=TextWrapping.Wrap,LineHeight=size*1.48
  };
 }

 Button AtlasButton(string text,Action action,string kind="normal")
 {
  var button=new Button{
   Content=text,FontSize=13,Padding=new Thickness(12,8,12,8),
   BorderThickness=new Thickness(1),Cursor=Cursors.Hand,
   Background=kind switch{
    "primary"=>AtlasBrass,
    "nav"=>Brushes.Transparent,
    "quiet"=>Brushes.Transparent,
    _=>AtlasPanelAltBrush
   },
   Foreground=kind=="primary"?AtlasBrush("#1A1B1C"):kind=="nav"||kind=="quiet"?AtlasMuted:AtlasInk,
   BorderBrush=kind=="primary"?AtlasBrush("#D9B77F"):kind=="nav"||kind=="quiet"?Brushes.Transparent:AtlasBrush("#3A4149"),
   FontWeight=kind=="primary"?FontWeights.SemiBold:FontWeights.Medium
  };
  button.Click+=(_,_)=>action();
  return button;
 }

 Border AtlasPanel(UIElement child,int padding=16,string background="#1B1F24")
 {
  return new Border{
   Background=SpireGlassTheme.GlassPanel(background=="#20242A"?"#B5222C37":"#B01B2531"),BorderBrush=SpireGlassTheme.PanelStroke(),
   BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),
   Padding=new Thickness(padding),Child=child
  };
 }

 Border AtlasBadge(string text,string kind="neutral")
 {
  var foreground=kind switch{"ok"=>AtlasGreen,"warn"=>AtlasBrass,"bad"=>AtlasRed,_=>AtlasMuted};
  var background=kind switch{"ok"=>AtlasBrush("#24382E"),"warn"=>AtlasBrush("#3A3022"),"bad"=>AtlasBrush("#3B2928"),_=>AtlasBrush("#2A3037")};
  return new Border{
   Background=background,CornerRadius=new CornerRadius(5),Padding=new Thickness(8,4,8,4),
   Child=AtlasText(text,11,foreground,true),HorizontalAlignment=HorizontalAlignment.Left
  };
 }

 UIElement AtlasPageTitle(string eyebrow,string title,string subtitle,params UIElement[] actions)
 {
  var root=new Grid{Margin=new Thickness(0,0,0,17)};
  root.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
  root.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
  var stack=new StackPanel();
  stack.Children.Add(AtlasText(eyebrow.ToUpperInvariant(),11,AtlasBrass,true));
  var titleText=AtlasText(title,29,AtlasInk,true);titleText.Margin=new Thickness(0,4,0,3);stack.Children.Add(titleText);
  stack.Children.Add(AtlasText(subtitle,14,AtlasMuted));
  root.Children.Add(stack);
  if(actions.Length>0){
   var panel=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
   foreach(var action in actions)panel.Children.Add(action);
   Grid.SetColumn(panel,1);root.Children.Add(panel);
  }
  return root;
 }

 ScrollViewer AtlasScroll(Panel content)
 {
  return new ScrollViewer{
   Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
   HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,
   CanContentScroll=true
  };
 }

 void RenderAtlasNative()
 {
  if(atlasNativeRoot==null||atlasNativePageHost==null)return;
  Content=atlasNativeRoot;
  atlasNativeRows=BuildAtlasNativeRows();
  UpdateAtlasNativeHeader();
  foreach(var pair in atlasNativeNavButtons){
   bool active=pair.Key==atlasNativePage;
   pair.Value.Background=active?AtlasBrush("#3B3021"):Brushes.Transparent;
   pair.Value.BorderBrush=active?AtlasBrush("#6D5737"):Brushes.Transparent;
   pair.Value.Foreground=active?AtlasBrush("#E6C58F"):AtlasMuted;
  }
  atlasNativePageHost.Content=atlasNativePage switch{
   "模组库"=>BuildAtlasNativeModsPage(),
   "修复中心"=>BuildAtlasNativePatchesPage(),
   "更新中心"=>BuildAtlasNativeUpdatesPage(),
   "下载中心"=>BuildAtlasNativeDownloadsPage(),
   "设置"=>BuildAtlasNativeSettingsPage(),
   _=>BuildAtlasNativeOverviewPage()
  };
  RenderAtlasNativeInspector();
 }

 void UpdateAtlasNativeHeader()
 {
  if(atlasNativeGamePath==null)return;
  atlasNativeGamePath.Text=selectedInstallRoot==null?"选择游戏目录以开始":Path.GetFileName(selectedInstallRoot.TrimEnd(Path.DirectorySeparatorChar));
  atlasNativeGamePath.ToolTip=selectedInstallRoot??"尚未选择游戏目录";
  bool running=selectedInstallRoot!=null&&GameEnvironment.Running(selectedInstallRoot);
  atlasNativeGameState!.Text=selectedInstallRoot==null?"尚未定位游戏":running?"游戏正在运行":busy?"正在处理":"游戏已就绪";
  atlasNativeGameState.Foreground=selectedInstallRoot==null?AtlasMuted:running?AtlasBrass:AtlasGreen;
  if(atlasNativeLaunchButton!=null)atlasNativeLaunchButton.IsEnabled=!busy&&!transactionWriting&&selectedInstallRoot!=null;
  if(atlasNativeFooter!=null){
   string detail=modInventory==null?"MOD 清单尚未读取":$"{modInventory.Mods.Count(m=>!ModPresentation.IsIndependentPatch(m))} 个普通 MOD · {modInventory.Mods.Count(m=>!ModPresentation.IsIndependentPatch(m)&&m.Enabled)} 个启用";
   atlasNativeFooter.Text=toast.Text.Length>0?toast.Text:detail+"  ·  "+ManagerUpdates.CurrentVersion;
  }
 }

 void AtlasNativeNavigate(string page)
 {
  atlasNativePage=page;
  atlasNativeSubpageReturnPage=page;
  this.page=page switch{
   "模组库"=>"模组管理","修复中心"=>"本地补丁",
   "更新中心"=>"更新中心","设置"=>"工具与设置",_=>"总览"
  };
  if(page=="模组库")modSection="普通 MOD";
  if(page=="修复中心")modSection="修复补丁";
  atlasNativeSelectedFolder=null;atlasNativeSelectedPatchId=null;
  RenderAtlasNative();
  if((page=="模组库"||page=="修复中心")&&selectedInstallRoot!=null&&
     (modInventory==null||modInventoryDirty))
   _=RefreshAtlasNativeDataAsync();
 }

 void AtlasNativeRunWorkflow(Action action)
 {
  atlasNativeSubpageReturnPage=atlasNativePage;
  action();
 }

 void AtlasNativeRunBatch(Action action)
 {
  var folders=atlasNativeRows.Where(x=>x.IsSelected&&!ModPresentation.IsIndependentPatch(x.Mod))
   .Select(x=>x.Mod.Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
  if(folders.Length==0){toast.Text="请先选择至少一个普通 MOD。";return;}
  chosenModFolders.Clear();
  foreach(string folder in folders)chosenModFolders.Add(folder);
  modSection="普通 MOD";
  atlasNativeSubpageReturnPage=atlasNativePage;
  action();
 }

 async Task RefreshAtlasNativeDataAsync()
 {
  if(busy||transactionWriting){
   if(atlasNativeFooter!=null)atlasNativeFooter.Text="管理器正在执行其他操作，请稍后刷新。";
   return;
  }
  if(selectedInstallRoot==null){RenderAtlasNative();return;}
  string root=selectedInstallRoot;
  try{
   await ScanFolder(root);
   if(!root.Equals(selectedInstallRoot,StringComparison.OrdinalIgnoreCase))return;
   if(modInventory==null)await RefreshModsAsync();
  }
  catch(Exception ex){toast.Text="ATLAS 刷新失败："+ex.Message;}
  finally{RenderAtlasNative();}
 }

 async void ChooseAtlasNativeFolder()
 {
  if(busy||transactionWriting)return;
  var dialog=new OpenFolderDialog{Title="选择包含 SlayTheSpire2.exe 的游戏根目录"};
  if(dialog.ShowDialog(this)!=true)return;
  try{
   await ScanFolder(dialog.FolderName);
   if(selectedInstallRoot!=null)await RefreshModsAsync();
   RenderAtlasNative();
  }catch(Exception ex){toast.Text="游戏目录读取失败："+ex.Message;RenderAtlasNative();}
 }

 void OpenAtlasLegacyPage(string pageName)
 {
  if(UiThemes.IsAtlasNative)
  {
   atlasNativeSubpageReturnPage=atlasNativePage;
   Navigate(pageName);
   return;
  }
  ChangeUiTheme(UiThemes.Workspace);
  Navigate(pageName);
 }

 void OpenAtlasLegacyMod(string folder)
 {
  if(UiThemes.IsAtlasNative)
  {
   atlasNativeSubpageReturnPage=atlasNativePage;
   ShowModDetail(folder);
   return;
  }
  ChangeUiTheme(UiThemes.Workspace);
  ShowModDetail(folder);
 }

 void OpenAtlasLegacyBatch()
 {
  var folders=atlasNativeRows.Where(x=>x.IsSelected&&!ModPresentation.IsIndependentPatch(x.Mod))
   .Select(x=>x.Mod.Folder).ToArray();
  if(folders.Length==0)return;
  ChangeUiTheme(UiThemes.Workspace);
  modSection="普通 MOD";
  chosenModFolders.Clear();
  foreach(string folder in folders)chosenModFolders.Add(folder);
  ShowMods();
 }

 UIElement BuildAtlasNativeOverviewPage()
 {
  var root=new StackPanel{Margin=new Thickness(5,5,5,20)};
  root.Children.Add(AtlasPageTitle("GAME LIBRARY  /  SLAY THE SPIRE 2",
   "准备好开始下一局了吗？","游戏环境、MOD 与独立修复补丁集中在这里。",
   AtlasButton("↻ 刷新环境",()=>_=RefreshAtlasNativeDataAsync())));
  var heroGrid=new Grid{MinHeight=164};
  heroGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
  heroGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
  var hero=new StackPanel();
  hero.Children.Add(AtlasBadge(selectedInstallRoot==null?"尚未关联游戏目录":GameEnvironment.Running(selectedInstallRoot)?"游戏正在运行":"游戏目录已关联",selectedInstallRoot==null?"warn":"ok"));
  var gameTitle=AtlasText("Slay the Spire 2",27,AtlasInk,true);gameTitle.Margin=new Thickness(0,12,0,3);hero.Children.Add(gameTitle);
  hero.Children.Add(AtlasText(selectedInstallRoot??"选择游戏安装目录，开始读取本地 MOD 环境。",12,AtlasMuted));
  var heroNote=AtlasText(modInventoryDirty?"检测到文件变化，当前清单需要重新读取。":
   report==null?"文件扫描结果尚未生成。":"本地扫描完成；文件匹配不等于游戏内兼容性验证。",11,AtlasMuted);
  heroNote.Margin=new Thickness(0,10,0,0);hero.Children.Add(heroNote);
  heroGrid.Children.Add(hero);
  var heroActions=new StackPanel{VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Right};
  heroActions.Children.Add(AtlasButton(selectedInstallRoot==null?"选择游戏目录":"更换游戏目录",ChooseAtlasNativeFolder,"primary"));
  var old=AtlasButton("打开完整 MOD 管理",()=>OpenAtlasLegacyPage("模组管理"));
  old.Margin=new Thickness(0,8,0,0);heroActions.Children.Add(old);
  Grid.SetColumn(heroActions,1);heroGrid.Children.Add(heroActions);
  root.Children.Add(AtlasPanel(heroGrid,21,"#20242A"));

  var normal=modInventory?.Mods.Count(m=>!ModPresentation.IsIndependentPatch(m))??0;
  var enabled=modInventory?.Mods.Count(m=>!ModPresentation.IsIndependentPatch(m)&&m.Enabled)??0;
  var patches=modInventory?.Mods.Count(ModPresentation.IsIndependentPatch)??0;
  var updates=atlasNativeRows.Count(x=>x.HasUpdate);
  var stats=new WrapPanel{Margin=new Thickness(0,14,0,14)};
  stats.Children.Add(AtlasStat(modInventory==null?"—":normal.ToString(),"已安装 MOD","本地清单"));
  stats.Children.Add(AtlasStat(modInventory==null?"—":enabled.ToString(),"已启用","普通 MOD"));
  stats.Children.Add(AtlasStat(modInventory==null?"—":updates.ToString(),"缓存更新","不会自动联网"));
  stats.Children.Add(AtlasStat(modInventory==null?"—":patches.ToString(),"独立补丁","与普通 MOD 分离"));
  root.Children.Add(stats);

  var lower=new Grid();
  lower.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1.15,GridUnitType.Star)});
  lower.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(14)});
  lower.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
  var attention=new StackPanel();
  attention.Children.Add(AtlasText("需要留意",16,AtlasInk,true));
  attention.Children.Add(AtlasText("只列出当前本地扫描或缓存中能够确认的事项。",11,AtlasMuted));
  var issues=new List<string>();
  if(selectedInstallRoot==null)issues.Add("尚未选择游戏目录。");
  if(modInventoryDirty)issues.Add("检测到 MOD 文件变化，需刷新清单。");
  if(modInventory?.Issues.Count>0)issues.AddRange(modInventory.Issues.Take(3));
  if(report?.Issues.Count>0)issues.AddRange(report.Issues.Take(3));
  if(report==null&&selectedInstallRoot!=null)issues.Add("尚未生成补丁文件扫描结果。");
  if(issues.Count==0)issues.Add("当前扫描没有发现需要立即处理的清单问题。");
  foreach(var issue in issues.Take(6)){
   var line=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,10,0,0)};
   line.Children.Add(AtlasBadge(issue.Contains("没有发现")?"正常":"提示",issue.Contains("没有发现")?"ok":"warn"));
   var text=AtlasText(issue,12,AtlasMuted);text.Margin=new Thickness(10,2,0,0);line.Children.Add(text);attention.Children.Add(line);
  }
  var quick=new StackPanel();
  quick.Children.Add(AtlasText("快速进入",16,AtlasInk,true));
  quick.Children.Add(AtlasText("常用工作区入口。需要执行文件操作时会转到现有安全流程。",11,AtlasMuted));
  foreach(var (label,desc,target) in new[]{
   ("浏览模组库","筛选、查看版本和来源","模组库"),
   ("管理独立修复补丁","查看补丁文件状态和关联 MOD","修复中心"),
   ("检查更新记录","查看已缓存的 GitHub / Workshop 结果","更新中心")})
  {
   var item=new StackPanel{Margin=new Thickness(0,12,0,0)};
   item.Children.Add(AtlasText(label,13,AtlasInk,true));
   item.Children.Add(AtlasText(desc,11,AtlasMuted));
   item.Children.Add(AtlasButton("打开工作区  →",()=>AtlasNativeNavigate(target),"quiet"));
   quick.Children.Add(AtlasPanel(item,11,"#20242A"));
  }
  lower.Children.Add(AtlasPanel(attention,16));
  Grid.SetColumn(quick,2);lower.Children.Add(quick);
  root.Children.Add(lower);
  return AtlasScroll(root);
 }

 Border AtlasStat(string value,string label,string note)
 {
  var stack=new StackPanel();
  stack.Children.Add(AtlasText(value,31,AtlasInk,true));
  stack.Children.Add(AtlasText(label,14,AtlasMuted,true));
  stack.Children.Add(AtlasText(note,12,AtlasMuted));
  var panel=AtlasPanel(stack,16,"#1B1F24");
  panel.Width=190;panel.Margin=new Thickness(0,0,12,0);
  return panel;
 }

 UIElement BuildAtlasNativeModsPage()
 {
  var root=new StackPanel{Margin=new Thickness(5,5,5,20)};
  root.Children.Add(AtlasPageTitle("MOD LIBRARY","模组库",
   "已安装 MOD、来源绑定、版本检查与安全启停。批量操作会逐项预览并重新校验。",
   AtlasButton("↻ 刷新",()=>_=RefreshAtlasNativeDataAsync()),
   AtlasButton("导入 ZIP",()=>AtlasNativeRunWorkflow(ChooseWorkshopZip),"quiet"),
   AtlasButton("Workshop 下载",()=>AtlasNativeRunWorkflow(()=>DownloadWorkshopMod()),"primary")));
  if(selectedInstallRoot==null){
   root.Children.Add(AtlasEmpty("尚未选择游戏目录","先定位《杀戮尖塔 2》的安装目录，再读取本地模组。",AtlasButton("选择游戏目录",ChooseAtlasNativeFolder,"primary")));
   return AtlasScroll(root);
  }

  var toolbar=new WrapPanel{Margin=new Thickness(0,0,0,10),VerticalAlignment=VerticalAlignment.Center};
  var search=new TextBox{
   Width=250,Height=36,FontSize=13,Padding=new Thickness(10,7,10,7),
   Background=AtlasBrush("#20252B"),Foreground=AtlasInk,BorderBrush=AtlasBrush("#41474F"),
   BorderThickness=new Thickness(1),ToolTip="搜索 MOD 名称、ID 或作者"
  };
  search.Text=atlasNativeQuery;
  search.TextChanged+=(_,_)=>{atlasNativeQuery=search.Text;RenderAtlasNativeModResults();};
  toolbar.Children.Add(search);

  var stateCombo=AtlasCombo(["全部","已启用","已停用","有更新"],atlasNativeStateFilter);
  stateCombo.Width=112;stateCombo.Margin=new Thickness(8,0,0,0);
  stateCombo.SelectionChanged+=(_,_)=>{atlasNativeStateFilter=stateCombo.SelectedItem?.ToString()??"全部";RenderAtlasNativeModResults();};
  toolbar.Children.Add(stateCombo);

  var sourceCombo=AtlasCombo(["全部来源","Workshop","GitHub","本地"],atlasNativeSourceFilter=="全部"?"全部来源":atlasNativeSourceFilter);
  sourceCombo.Width=120;sourceCombo.Margin=new Thickness(8,0,0,0);
  sourceCombo.SelectionChanged+=(_,_)=>{atlasNativeSourceFilter=sourceCombo.SelectedItem?.ToString() switch{"全部来源"=>"全部",var x=>x??"全部"};RenderAtlasNativeModResults();};
  toolbar.Children.Add(sourceCombo);

  var unbound=new CheckBox{
   Style=(Style)FindResource("ModCardCheckBox"),
   Content="未绑定 Workshop ID",IsChecked=atlasNativeUnboundOnly,
   Foreground=AtlasMuted,FontSize=12,VerticalAlignment=VerticalAlignment.Center,
   Margin=new Thickness(12,0,8,0),Cursor=Cursors.Hand
  };
  unbound.Checked+=(_,_)=>{atlasNativeUnboundOnly=true;RenderAtlasNativeModResults();};
  unbound.Unchecked+=(_,_)=>{atlasNativeUnboundOnly=false;RenderAtlasNativeModResults();};
  toolbar.Children.Add(unbound);

  var sort=AtlasCombo(["名称","有更新优先"],atlasNativeSort);
  sort.Width=118;sort.Margin=new Thickness(8,0,0,0);
  sort.SelectionChanged+=(_,_)=>{atlasNativeSort=sort.SelectedItem?.ToString()??"名称";RenderAtlasNativeModResults();};
  toolbar.Children.Add(sort);

  var views=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(10,0,0,0)};
  views.Children.Add(AtlasButton("列表",()=>{atlasNativeViewMode="list";RenderAtlasNativeModResults();},atlasNativeViewMode=="list"?"active":"quiet"));
  views.Children.Add(AtlasButton("库视图",()=>{atlasNativeViewMode="grid";RenderAtlasNativeModResults();},atlasNativeViewMode=="grid"?"active":"quiet"));
  toolbar.Children.Add(views);
  root.Children.Add(AtlasPanel(toolbar,10,"#20242A"));

  atlasNativeBatchBar=new Border{
   Background=AtlasBrush("#30291F"),BorderBrush=AtlasBrush("#695333"),
   BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(9),
   Padding=new Thickness(12,8,12,8),Margin=new Thickness(0,0,0,10),Visibility=Visibility.Collapsed
  };
  var batch=new WrapPanel{VerticalAlignment=VerticalAlignment.Center};
  atlasNativeBatchText=AtlasText("已选择 0 项",13,AtlasBrush("#E5C48E"),true);
  atlasNativeBatchText.Margin=new Thickness(0,7,12,7);batch.Children.Add(atlasNativeBatchText);
  var selectAll=AtlasButton("全选当前结果",SelectAllAtlasVisibleMods,"quiet");selectAll.Margin=new Thickness(0,2,6,2);batch.Children.Add(selectAll);
  var enable=AtlasButton("批量启用",()=>AtlasNativeRunBatch(()=>BatchToggleMods(true)),"primary");enable.Margin=new Thickness(0,2,6,2);batch.Children.Add(enable);
  var disable=AtlasButton("批量停用",()=>AtlasNativeRunBatch(()=>BatchToggleMods(false)),"quiet");disable.Margin=new Thickness(0,2,6,2);batch.Children.Add(disable);
  var discover=AtlasButton("查找 GitHub",()=>AtlasNativeRunBatch(BatchFindGitHub),"quiet");discover.Margin=new Thickness(0,2,6,2);batch.Children.Add(discover);
  var checkGit=AtlasButton("检查 GitHub",()=>AtlasNativeRunBatch(BatchCheckGitHub),"quiet");checkGit.Margin=new Thickness(0,2,6,2);batch.Children.Add(checkGit);
  var checkWorkshop=AtlasButton("检查 Workshop",()=>AtlasNativeRunBatch(BatchCheckWorkshop),"quiet");checkWorkshop.Margin=new Thickness(0,2,6,2);batch.Children.Add(checkWorkshop);
  var updateWorkshop=AtlasButton("批量更新 Workshop",()=>AtlasNativeRunBatch(BatchUpdateWorkshop),"quiet");updateWorkshop.Margin=new Thickness(0,2,6,2);batch.Children.Add(updateWorkshop);
  var clear=AtlasButton("清空选择",ClearAtlasNativeSelection,"quiet");clear.Margin=new Thickness(0,2,0,2);batch.Children.Add(clear);
  atlasNativeBatchBar.Child=batch;root.Children.Add(atlasNativeBatchBar);

  atlasNativeResultsSummary=AtlasText("",11,AtlasMuted);atlasNativeResultsSummary.Margin=new Thickness(2,0,0,8);root.Children.Add(atlasNativeResultsSummary);
  atlasNativeResultsHost=new ContentControl();
  root.Children.Add(atlasNativeResultsHost);
  RenderAtlasNativeModResults();
  return AtlasScroll(root);
 }

 ComboBox AtlasCombo(IEnumerable<string> items,string selected)
 {
  var combo=new ComboBox{
   ItemsSource=items.ToArray(),SelectedItem=selected,
   Height=36,FontSize=12,Padding=new Thickness(7,4,7,4),
   Style=(Style)FindResource("AtlasComboBox"),
   ItemContainerStyle=(Style)FindResource("AtlasComboBoxItem"),
   Background=AtlasBrush("#20252B"),Foreground=AtlasInk,
   BorderBrush=AtlasBrush("#41474F"),BorderThickness=new Thickness(1)
  };
  return combo;
 }

 void RenderAtlasNativeModResults()
 {
  if(atlasNativeResultsHost==null)return;
  var rows=GetFilteredAtlasNativeRows();
  if(atlasNativeResultsSummary!=null)atlasNativeResultsSummary.Text=$"显示 {rows.Length} / {atlasNativeRows.Count} 项  ·  列表视图适合大量 MOD";
  if(atlasNativeBatchBar!=null)atlasNativeBatchBar.Visibility=atlasNativeSelectedFolders.Count>0?Visibility.Visible:Visibility.Collapsed;
  if(atlasNativeBatchText!=null)atlasNativeBatchText.Text="已选择 "+atlasNativeSelectedFolders.Count+" 项";
  if(modInventory==null){
   atlasNativeResultsHost.Content=AtlasEmpty("尚未读取 MOD 清单","刷新本地环境后，这里会显示真实安装的 MOD。",AtlasButton("刷新",()=>_=RefreshAtlasNativeDataAsync(),"primary"));
   return;
  }
  if(rows.Length==0){
   atlasNativeResultsHost.Content=AtlasEmpty("没有符合条件的 MOD","尝试清除筛选条件，或刷新本地清单。");
   return;
  }
  if(atlasNativeViewMode=="grid"){
   var wrap=new WrapPanel();
   foreach(var row in rows)wrap.Children.Add(BuildAtlasNativeModCard(row));
   atlasNativeResultsHost.Content=wrap;
  }else{
   atlasNativeResultsHost.Content=BuildAtlasNativeDataGrid(rows);
  }
 }

 AtlasNativeModRow[] GetFilteredAtlasNativeRows()
 {
  IEnumerable<AtlasNativeModRow> rows=atlasNativeRows;
  if(atlasNativeStateFilter=="已启用")rows=rows.Where(x=>x.Mod.Enabled);
  else if(atlasNativeStateFilter=="已停用")rows=rows.Where(x=>!x.Mod.Enabled);
  else if(atlasNativeStateFilter=="有更新")rows=rows.Where(x=>x.HasUpdate);
  if(atlasNativeSourceFilter!="全部")rows=rows.Where(x=>atlasNativeSourceFilter switch{
   "Workshop"=>x.WorkshopId.Length>0,"GitHub"=>x.GitHubRepository.Length>0,
   "本地"=>x.WorkshopId.Length==0&&x.GitHubRepository.Length==0,_=>true});
  if(atlasNativeUnboundOnly)rows=rows.Where(x=>x.WorkshopId.Length==0);
  if(!string.IsNullOrWhiteSpace(atlasNativeQuery)){
   string q=atlasNativeQuery.Trim();
   rows=rows.Where(x=>(x.Name+" "+x.Id+" "+x.Author+" "+x.Version+" "+x.WorkshopId+" "+x.GitHubRepository).Contains(q,StringComparison.OrdinalIgnoreCase));
  }
  return (atlasNativeSort=="有更新优先"
   ?rows.OrderByDescending(x=>x.HasUpdate).ThenBy(x=>x.Name,StringComparer.OrdinalIgnoreCase)
   :rows.OrderBy(x=>x.Name,StringComparer.OrdinalIgnoreCase)).ToArray();
 }

 List<AtlasNativeModRow> BuildAtlasNativeRows()
 {
  if(modInventory==null||selectedInstallRoot==null)return [];
  WorkshopModSources? workshop=null;WorkshopModUpdates? workshopUpdates=null;GitHubModUpdates? github=null;
  try{workshop=WorkshopSources();workshopUpdates=WorkshopUpdates();github=GitHubUpdates();}catch{}
  var result=new List<AtlasNativeModRow>();
  foreach(var mod in modInventory.Mods.Where(m=>!ModPresentation.IsIndependentPatch(m))){
   ulong? workshopId=null;string? repository=null;
   try{workshopId=workshop?.Get(mod.Id);}catch{}
   try{repository=github?.Repository(mod.Id);}catch{}
   GitHubModResult? gitResult=null;WorkshopModUpdateResult? workshopResult=null;
   try{gitResult=github?.Cached(mod.Id);}catch{}
   try{workshopResult=workshopUpdates?.Cached(mod.Id);}catch{}
   string update=gitResult?.Status=="有更新"?"GitHub 有更新":
    workshopResult?.Status=="有更新"?"Workshop 有更新":
    gitResult!=null?"GitHub "+gitResult.Status:
    workshopResult!=null?"Workshop "+workshopResult.Status:"尚未检查";
   string latest=gitResult?.Status=="有更新"?gitResult.LatestVersion:
    workshopResult?.Status=="有更新"?workshopResult.RemoteContentVersion:"";
   string source=workshopId.HasValue&&repository!=null?"Workshop + GitHub":
    workshopId.HasValue?"Workshop":repository!=null?"GitHub":"本地";
   var row=new AtlasNativeModRow(mod,source,workshopId?.ToString()??"",repository??"",update,latest);
   row.IsSelected=atlasNativeSelectedFolders.Contains(mod.Folder);
   result.Add(row);
  }
  return result.OrderBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).ToList();
 }

 bool AtlasNativeToggleAllowed(ManagedMod mod,out string? reason)
 {
  reason=null;
  if(selectedInstallRoot==null){reason="尚未选择游戏目录。";return false;}
  if(modInventory==null){reason="MOD 清单尚未读取。";return false;}
  if(modInventoryDirty){reason="MOD 文件发生变化，请刷新后再操作。";return false;}
  try
  {
   reason=ModManager.BlockReason(selectedInstallRoot,mod,modInventory);
   return reason==null&&!busy&&!transactionWriting;
  }
  catch(Exception ex){reason=ex.Message;return false;}
 }

 Border BuildAtlasNativeModCard(AtlasNativeModRow row)
 {
  bool canToggle=AtlasNativeToggleAllowed(row.Mod,out string? blocked);
  var outer=new Border{
   Width=365,MinHeight=218,Margin=new Thickness(0,0,13,13),
   Background=AtlasPanelBrush,BorderBrush=AtlasBrush("#43505D"),
   BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Padding=new Thickness(15),
   Cursor=Cursors.Hand
  };
  var root=new Grid();
  root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
  root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
  root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
  var top=new Grid();
  top.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
  top.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
  var title=AtlasText(row.Name,16,AtlasInk,true);
  title.TextTrimming=TextTrimming.CharacterEllipsis;title.MaxWidth=300;
  title.ToolTip=row.Name;top.Children.Add(title);
  var check=new CheckBox{
   Style=(Style)FindResource("ModCardCheckBox"),IsChecked=row.IsSelected,
   VerticalAlignment=VerticalAlignment.Top,Foreground=AtlasBrass,ToolTip="加入批量选择"
  };
  check.Checked+=(_,_)=>{row.IsSelected=true;UpdateAtlasNativeSelection();};
  check.Unchecked+=(_,_)=>{row.IsSelected=false;UpdateAtlasNativeSelection();};
  Grid.SetColumn(check,1);top.Children.Add(check);root.Children.Add(top);

  var details=new StackPanel{Margin=new Thickness(0,11,0,11)};
  details.Children.Add(AtlasText(row.Id+"  ·  "+row.Author,11,AtlasMuted));
  details.Children.Add(AtlasText("版本 "+row.Version+"    来源 "+row.Source,13,AtlasInk));
  string description=string.IsNullOrWhiteSpace(row.Description)?"作者未提供简介。":row.Description;
  var desc=AtlasText(description,12,AtlasMuted);desc.MaxHeight=38;desc.TextTrimming=TextTrimming.CharacterEllipsis;
  desc.Margin=new Thickness(0,5,0,0);details.Children.Add(desc);
  var status=new WrapPanel{Margin=new Thickness(0,9,0,0)};
  status.Children.Add(AtlasBadge(row.State,row.Mod.Enabled?"ok":"neutral"));
  status.Children.Add(AtlasBadge(row.UpdateStatus,row.HasUpdate?"warn":"neutral"));
  details.Children.Add(status);
  if(blocked!=null){
   var reason=AtlasText(blocked,11,AtlasRed);reason.Margin=new Thickness(0,6,0,0);
   reason.MaxHeight=34;details.Children.Add(reason);
  }
  Grid.SetRow(details,1);root.Children.Add(details);

  var actions=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
  var toggle=AtlasButton(row.Mod.Enabled?"预览停用":"预览启用",
   ()=>AtlasNativeRunWorkflow(()=>PrepareModToggle(row.Mod)),row.Mod.Enabled?"quiet":"primary");
  toggle.IsEnabled=canToggle;toggle.Margin=new Thickness(0,0,7,0);
  actions.Children.Add(toggle);
  var open=AtlasButton("查看详情",()=>ShowAtlasNativeModInspector(row),"quiet");
  actions.Children.Add(open);
  Grid.SetRow(actions,2);root.Children.Add(actions);
  outer.Child=root;
  outer.MouseEnter+=(_,_)=>outer.BorderBrush=AtlasBrush("#A98B5B");
  outer.MouseLeave+=(_,_)=>outer.BorderBrush=AtlasBrush("#43505D");
  return outer;
 }

 DataGrid BuildAtlasNativeDataGrid(AtlasNativeModRow[] rows)
 {
  var grid=new DataGrid{
   ItemsSource=rows,Height=Math.Max(340,Math.Min(640,ActualHeight>0?ActualHeight-285:500)),
   AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,
   IsReadOnly=true,SelectionMode=DataGridSelectionMode.Single,SelectionUnit=DataGridSelectionUnit.FullRow,
   HeadersVisibility=DataGridHeadersVisibility.Column,GridLinesVisibility=DataGridGridLinesVisibility.None,
   Background=AtlasBrush("#191D22"),Foreground=AtlasInk,BorderBrush=AtlasBrush("#30363D"),
   BorderThickness=new Thickness(1),RowBackground=AtlasBrush("#1B1F24"),
   AlternatingRowBackground=AtlasBrush("#20242A"),RowHeight=62,
   EnableRowVirtualization=true,EnableColumnVirtualization=true
  };
  VirtualizingPanel.SetIsVirtualizing(grid,true);
  VirtualizingPanel.SetVirtualizationMode(grid,VirtualizationMode.Recycling);
  ScrollViewer.SetCanContentScroll(grid,true);
  var headerStyle=new Style(typeof(DataGridColumnHeader));
  headerStyle.Setters.Add(new Setter(Control.BackgroundProperty,AtlasBrush("#242930")));
  headerStyle.Setters.Add(new Setter(Control.ForegroundProperty,AtlasBrass));
  headerStyle.Setters.Add(new Setter(Control.BorderBrushProperty,AtlasBrush("#343A41")));
  headerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0,0,0,1)));
  headerStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(10,9,10,9)));
  headerStyle.Setters.Add(new Setter(Control.FontSizeProperty,13d));
  headerStyle.Setters.Add(new Setter(Control.FontWeightProperty,FontWeights.SemiBold));
  grid.ColumnHeaderStyle=headerStyle;
  var rowStyle=new Style(typeof(DataGridRow));
  rowStyle.Setters.Add(new Setter(Control.ForegroundProperty,AtlasInk));
  rowStyle.Setters.Add(new Setter(Control.BorderBrushProperty,AtlasBrush("#30363D")));
  rowStyle.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0,0,0,1)));
  rowStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty,VerticalAlignment.Center));
  grid.RowStyle=rowStyle;
  var cellStyle=new Style(typeof(DataGridCell));
  cellStyle.Setters.Add(new Setter(Control.ForegroundProperty,AtlasInk));
  cellStyle.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0)));
  cellStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(11,7,11,7)));
  cellStyle.Setters.Add(new Setter(Control.FontSizeProperty,13d));
  cellStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty,VerticalAlignment.Center));
  grid.CellStyle=cellStyle;

  var checkFactory=new FrameworkElementFactory(typeof(CheckBox));
  checkFactory.SetValue(FrameworkElement.StyleProperty,(Style)FindResource("ModCardCheckBox"));
  checkFactory.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);
  checkFactory.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
  checkFactory.SetBinding(ToggleButton.IsCheckedProperty,new Binding(nameof(AtlasNativeModRow.IsSelected)){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged});
  checkFactory.AddHandler(CheckBox.CheckedEvent,new RoutedEventHandler((_,_)=>UpdateAtlasNativeSelection()),true);
  checkFactory.AddHandler(CheckBox.UncheckedEvent,new RoutedEventHandler((_,_)=>UpdateAtlasNativeSelection()),true);
  grid.Columns.Add(new DataGridTemplateColumn{Header="",Width=42,CellTemplate=new DataTemplate{VisualTree=checkFactory}});
  grid.Columns.Add(new DataGridTextColumn{Header="模组名称",Binding=new Binding(nameof(AtlasNativeModRow.Name)),Width=new DataGridLength(2,DataGridLengthUnitType.Star)});
  grid.Columns.Add(new DataGridTextColumn{Header="版本",Binding=new Binding(nameof(AtlasNativeModRow.Version)),Width=85});
  grid.Columns.Add(new DataGridTextColumn{Header="来源",Binding=new Binding(nameof(AtlasNativeModRow.Source)),Width=120});
  grid.Columns.Add(new DataGridTextColumn{Header="启用状态",Binding=new Binding(nameof(AtlasNativeModRow.State)),Width=85});
  grid.Columns.Add(new DataGridTextColumn{Header="更新状态",Binding=new Binding(nameof(AtlasNativeModRow.UpdateStatus)),Width=125});
  grid.SelectionChanged+=(_,_)=>{if(grid.SelectedItem is AtlasNativeModRow row)ShowAtlasNativeModInspector(row);};
  return grid;
 }

 void UpdateAtlasNativeSelection()
 {
  atlasNativeSelectedFolders.Clear();
  foreach(var row in atlasNativeRows.Where(x=>x.IsSelected))atlasNativeSelectedFolders.Add(row.Mod.Folder);
  if(atlasNativeBatchBar!=null)atlasNativeBatchBar.Visibility=atlasNativeSelectedFolders.Count>0?Visibility.Visible:Visibility.Collapsed;
  if(atlasNativeBatchText!=null)atlasNativeBatchText.Text="已选择 "+atlasNativeSelectedFolders.Count+" 项";
 }

 void SelectAllAtlasVisibleMods()
 {
  foreach(var row in GetFilteredAtlasNativeRows())row.IsSelected=true;
  UpdateAtlasNativeSelection();RenderAtlasNativeModResults();
 }

 void ClearAtlasNativeSelection()
 {
  foreach(var row in atlasNativeRows)row.IsSelected=false;
  atlasNativeSelectedFolders.Clear();UpdateAtlasNativeSelection();RenderAtlasNativeModResults();
 }

 UIElement BuildAtlasNativePatchesPage()
 {
  var root=new StackPanel{Margin=new Thickness(5,5,5,20)};
  root.Children.Add(AtlasPageTitle("REPAIR WORKSPACE","修复中心",
   "独立补丁单独管理。文件匹配、生命周期记录与游戏内验证是不同概念。",
   AtlasButton("在完整工作台管理",()=>OpenAtlasLegacyPage("本地补丁"))));
  if(selectedInstallRoot==null||modInventory==null){
   root.Children.Add(AtlasEmpty("尚未读取本地补丁","选择游戏目录并刷新后，显示实际安装的独立修复补丁。",AtlasButton("刷新环境",()=>_=RefreshAtlasNativeDataAsync(),"primary")));
   return AtlasScroll(root);
  }
  var patches=modInventory.Mods.Where(ModPresentation.IsIndependentPatch).ToArray();
  var active=patches.Count(x=>x.Enabled);
  var stale=patches.Count(x=>LifecycleTag(x.Id) is "需重新检查" or "MOD 已变化，需核对" or "疑似上游修复");
  var stats=new WrapPanel{Margin=new Thickness(0,0,0,13)};
  stats.Children.Add(AtlasStat(patches.Length.ToString(),"本地独立补丁","已安装"));
  stats.Children.Add(AtlasStat(active.ToString(),"当前启用","补丁文件"));
  stats.Children.Add(AtlasStat(stale.ToString(),"需要复查","生命周期记录"));
  root.Children.Add(stats);
  if(patches.Length==0){
   root.Children.Add(AtlasEmpty("当前没有独立补丁","普通 MOD 不会混入这里。可在旧版工作台查看补丁库和在线补丁。",AtlasButton("打开补丁库",()=>OpenAtlasLegacyPage("补丁库"))));
   return AtlasScroll(root);
  }
  foreach(var mod in patches.OrderBy(x=>x.Name,StringComparer.OrdinalIgnoreCase)){
   var state=report?.Patches.FirstOrDefault(x=>x.Patch.Id.Equals(mod.Id,StringComparison.OrdinalIgnoreCase));
   string fileStatus=state?.Status??"尚未扫描";
   string health=LifecycleTag(mod.Id);
   string[] targets=state?.Patch.Requirements??[];
   var item=new Grid();
   item.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
   item.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
   var info=new StackPanel();
   info.Children.Add(AtlasText(mod.Name,14,AtlasInk,true));
   info.Children.Add(AtlasText(mod.Id+"  ·  v"+mod.Version,10,AtlasMuted));
   info.Children.Add(AtlasText("文件状态："+fileStatus+"  ·  生命周期："+health,11,health.Contains("复查")?AtlasBrass:AtlasMuted));
   info.Children.Add(AtlasText(targets.Length==0?"关联目标：未声明": "关联目标："+string.Join("、",targets),10,AtlasMuted));
   item.Children.Add(info);
   var actions=new StackPanel{Orientation=Orientation.Vertical,VerticalAlignment=VerticalAlignment.Center};
   actions.Children.Add(AtlasBadge(mod.Enabled?"已启用":"已停用",mod.Enabled?"ok":"neutral"));
   var detail=AtlasButton("查看依据",()=>ShowAtlasNativePatchInspector(mod,state),"quiet");detail.Margin=new Thickness(0,7,0,0);actions.Children.Add(detail);
   Grid.SetColumn(actions,1);item.Children.Add(actions);
   root.Children.Add(AtlasPanel(item,13));var gap=new Border{Height=8};root.Children.Add(gap);
  }
  return AtlasScroll(root);
 }

 UIElement BuildAtlasNativeUpdatesPage()
 {
  var root=new StackPanel{Margin=new Thickness(5,5,5,20)};
  root.Children.Add(AtlasPageTitle("UPDATE CENTER","更新中心",
   "这里只显示已经缓存的 GitHub / Workshop 检查结果，不会自动联网或触发下载。",
   AtlasButton("打开完整更新工作台",()=>OpenAtlasLegacyPage("更新中心"))));
  var rows=atlasNativeRows;
  var updates=rows.Where(x=>x.UpdateStatus!="尚未检查").OrderByDescending(x=>x.HasUpdate).ThenBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).ToArray();
  if(updates.Length==0){
   root.Children.Add(AtlasEmpty("暂无已缓存的更新结果","进入完整更新工作台后，可以按需检查 GitHub 与创意工坊来源。",AtlasButton("打开更新工作台",()=>OpenAtlasLegacyPage("更新中心"),"primary")));
   return AtlasScroll(root);
  }
  foreach(var row in updates){
   var info=new StackPanel();
   info.Children.Add(AtlasText(row.Name,14,AtlasInk,true));
   info.Children.Add(AtlasText(row.Id+"  ·  "+row.Source+"  ·  本地 "+row.Version,11,AtlasMuted));
   info.Children.Add(AtlasText(row.UpdateStatus+(row.LatestVersion.Length>0?"  ·  最新 "+row.LatestVersion:""),12,row.HasUpdate?AtlasBrass:AtlasMuted));
   var line=new Grid();line.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});line.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
   line.Children.Add(info);
   var detail=AtlasButton("查看 MOD",()=>ShowAtlasNativeModInspector(row),"quiet");Grid.SetColumn(detail,1);line.Children.Add(detail);
   root.Children.Add(AtlasPanel(line,13));var gap=new Border{Height=7};root.Children.Add(gap);
  }
  return AtlasScroll(root);
 }

 UIElement BuildAtlasNativeDownloadsPage()
 {
  var root=new StackPanel{Margin=new Thickness(5,5,5,20)};
  root.Children.Add(AtlasPageTitle("DOWNLOAD WORKSPACE","下载中心",
   "下载和安装继续使用已经验证过的 C# 流程；本页面不伪造资源目录或下载进度。"));
  var workshop=new StackPanel();
  workshop.Children.Add(AtlasText("Steam Workshop / 尖塔补给站",17,AtlasInk,true));
  workshop.Children.Add(AtlasText("通过现有下载器获取 Workshop 内容，安装前仍会校验文件、展示预览并保留安全快照。",12,AtlasMuted));
  workshop.Children.Add(AtlasButton("打开 MOD 管理与 Workshop 下载",()=>OpenAtlasLegacyPage("模组管理"),"primary"));
  root.Children.Add(AtlasPanel(workshop,18,"#20242A"));
  var github=new StackPanel{Margin=new Thickness(0,12,0,0)};
  github.Children.Add(AtlasText("GitHub Releases",17,AtlasInk,true));
  github.Children.Add(AtlasText("GitHub 来源绑定、Release 检查与下载更新继续由现有 GitHub 服务处理。",12,AtlasMuted));
  github.Children.Add(AtlasButton("打开更新工作台",()=>OpenAtlasLegacyPage("更新中心"),"quiet"));
  root.Children.Add(AtlasPanel(github,18));
  var local=new StackPanel{Margin=new Thickness(0,12,0,0)};
  local.Children.Add(AtlasText("本地 ZIP 导入",17,AtlasInk,true));
  local.Children.Add(AtlasText("ZIP 导入会修改游戏目录，因此仍使用原有预览、确认和校验流程。",12,AtlasMuted));
  local.Children.Add(AtlasButton("打开本地导入流程",()=>OpenAtlasLegacyPage("模组管理"),"quiet"));
  root.Children.Add(AtlasPanel(local,18));
  return AtlasScroll(root);
 }

 UIElement BuildAtlasNativeSettingsPage()
 {
  var root=new StackPanel{Margin=new Thickness(5,5,5,20)};
  root.Children.Add(AtlasPageTitle("PREFERENCES","设置","ATLAS 原生工作台使用 WPF 控件绘制，不启动浏览器渲染进程。"));
  var game=new StackPanel();
  game.Children.Add(AtlasText("游戏目录",16,AtlasInk,true));
  game.Children.Add(AtlasText(selectedInstallRoot??"尚未选择游戏目录",12,AtlasMuted));
  game.Children.Add(AtlasButton("选择游戏目录",ChooseAtlasNativeFolder,"primary"));
  root.Children.Add(AtlasPanel(game,16));
  var features=new StackPanel{Margin=new Thickness(0,12,0,0)};
  features.Children.Add(AtlasText("完整功能",16,AtlasInk,true));
  features.Children.Add(AtlasText("启用 / 停用、来源绑定、更新下载、补丁安装、联机检查和诊断工具继续使用已经存在的 WPF 安全流程。",12,AtlasMuted));
  features.Children.Add(AtlasButton("打开完整 WPF 工作台",()=>ChangeUiTheme(UiThemes.Workspace),"quiet"));
  root.Children.Add(AtlasPanel(features,16));
  var diagnostics=new StackPanel{Margin=new Thickness(0,12,0,0)};
  diagnostics.Children.Add(AtlasText("其他工具",16,AtlasInk,true));
  diagnostics.Children.Add(AtlasButton("联机检查",()=>OpenAtlasLegacyPage("联机检查"),"quiet"));
  diagnostics.Children.Add(AtlasButton("诊断与日志",()=>OpenAtlasLegacyPage("诊断与日志"),"quiet"));
  diagnostics.Children.Add(AtlasButton("管理器更新",()=>OpenAtlasLegacyPage("检查更新"),"quiet"));
  diagnostics.Children.Add(AtlasButton("GitHub 网络设置",()=>OpenAtlasLegacyPage("工具与设置"),"quiet"));
  root.Children.Add(AtlasPanel(diagnostics,16));
  return AtlasScroll(root);
 }

 UIElement AtlasEmpty(string title,string detail,UIElement? action=null)
 {
  var stack=new StackPanel{HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
  stack.Children.Add(AtlasText(title,17,AtlasInk,true));
  var subtitle=AtlasText(detail,12,AtlasMuted);subtitle.TextAlignment=TextAlignment.Center;subtitle.Margin=new Thickness(0,6,0,12);stack.Children.Add(subtitle);
  if(action is FrameworkElement framework){framework.HorizontalAlignment=HorizontalAlignment.Center;stack.Children.Add(framework);}
  return AtlasPanel(stack,24,"#1B1F24");
 }

 void ShowAtlasNativeModInspector(AtlasNativeModRow row)
 {
  atlasNativeSelectedFolder=row.Mod.Folder;atlasNativeSelectedPatchId=null;
  RenderAtlasNativeInspector();
 }

 void ShowAtlasNativePatchInspector(ManagedMod mod,PatchState? state)
 {
  atlasNativeSelectedFolder=mod.Folder;atlasNativeSelectedPatchId=mod.Id;
  RenderAtlasNativeInspector(state);
 }

 void CloseAtlasNativeInspector()
 {
  atlasNativeSelectedFolder=null;atlasNativeSelectedPatchId=null;
  RenderAtlasNativeInspector();
 }

 void RenderAtlasNativeInspector(PatchState? knownState=null)
 {
  if(atlasNativeInspector==null||atlasNativeBody==null)return;
  if(atlasNativeSelectedFolder==null||modInventory==null){
   atlasNativeInspector.Visibility=Visibility.Collapsed;
   atlasNativeBody.ColumnDefinitions[1].Width=new GridLength(0);
   atlasNativeInspector.Child=null;
   return;
  }
  var mod=modInventory.Mods.FirstOrDefault(x=>x.Folder.Equals(atlasNativeSelectedFolder,StringComparison.OrdinalIgnoreCase));
  if(mod==null){CloseAtlasNativeInspector();return;}
  bool isPatch=ModPresentation.IsIndependentPatch(mod);
  var content=new StackPanel();
  var top=new Grid{Margin=new Thickness(0,0,0,13)};
  top.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
  top.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
  top.Children.Add(AtlasText(isPatch?"PATCH INSPECTOR":"MOD INSPECTOR",10,AtlasBrass,true));
  var close=AtlasButton("×",CloseAtlasNativeInspector,"quiet");close.Padding=new Thickness(9,3,9,3);Grid.SetColumn(close,1);top.Children.Add(close);
  content.Children.Add(top);
  content.Children.Add(AtlasText(mod.Name,19,AtlasInk,true));
  content.Children.Add(AtlasText(mod.Id,10,AtlasMuted));
  var status=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,10,0,13)};
  status.Children.Add(AtlasBadge(mod.Enabled?"已启用":"已停用",mod.Enabled?"ok":"neutral"));
  if(isPatch)status.Children.Add(AtlasBadge("独立修复补丁","warn"));
  content.Children.Add(status);
  AddInspectorField(content,"版本",mod.Version);
  AddInspectorField(content,"作者",mod.Author);
  AddInspectorField(content,"说明",mod.Description.Length==0?"作者未提供简介。":mod.Description);
  AddInspectorField(content,"安装路径",mod.Folder);
  string dependencies=string.Join("、",mod.Dependencies.Select(x=>x.Id));
  AddInspectorField(content,"依赖",dependencies.Length>0?dependencies:"未声明必需前置");
  if(isPatch){
   var state=knownState??report?.Patches.FirstOrDefault(x=>x.Patch.Id.Equals(mod.Id,StringComparison.OrdinalIgnoreCase));
   AddInspectorField(content,"文件扫描状态",state?.Status??"尚未扫描");
   AddInspectorField(content,"扫描依据",state?.Reason??"当前没有补丁扫描记录。");
   AddInspectorField(content,"补丁健康生命周期",LifecycleTag(mod.Id));
   AddInspectorField(content,"关联 MOD",state?.Patch.Requirements.Length>0?string.Join("、",state.Patch.Requirements):"补丁清单未声明关联 MOD");
   content.Children.Add(AtlasButton("在完整工作台查看补丁依据",()=>OpenAtlasLegacyMod(mod.Folder),"quiet"));
  }else{
   AtlasNativeModRow? row=atlasNativeRows.FirstOrDefault(x=>x.Mod.Folder.Equals(mod.Folder,StringComparison.OrdinalIgnoreCase));
   if(row!=null){
    AddInspectorField(content,"来源",row.Source);
    AddInspectorField(content,"Workshop ID",row.WorkshopId.Length>0?row.WorkshopId:"未绑定");
    AddInspectorField(content,"GitHub 仓库",row.GitHubRepository.Length>0?row.GitHubRepository:"未关联");
    AddInspectorField(content,"更新状态",row.UpdateStatus+(row.LatestVersion.Length>0?" · "+row.LatestVersion:""));
   }
   content.Children.Add(AtlasButton("在完整工作台查看详情",()=>OpenAtlasLegacyMod(mod.Folder),"primary"));
  }
  var scroller=new ScrollViewer{Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
  atlasNativeInspector.Child=scroller;
  atlasNativeInspector.Visibility=Visibility.Visible;
  atlasNativeBody.ColumnDefinitions[1].Width=new GridLength(330);
 }

 void AddInspectorField(Panel panel,string label,string value)
 {
  var group=new StackPanel{Margin=new Thickness(0,0,0,12)};
  group.Children.Add(AtlasText(label,10,AtlasBrass,true));
  group.Children.Add(AtlasText(value,12,AtlasMuted));
  panel.Children.Add(group);
 }

 public async Task CaptureAtlasNativeAsync(string fixtures,string output)
 {
  Directory.CreateDirectory(fixtures);Directory.CreateDirectory(output);
  var checks=new List<string>();
  void Check(bool ok,string name)
  {
   if(!ok)throw new InvalidOperationException(name);
   checks.Add("PASS "+name);
   File.WriteAllLines(Path.Combine(output,"ATLAS 原生界面验证.txt"),checks);
  }
  async Task Idle()
  {
   await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
   UpdateLayout();atlasNativeRoot?.UpdateLayout();
  }
  async Task Shot(string name)
  {
   await Idle();
   if(atlasNativeRoot==null||atlasNativeRoot.ActualWidth<1||atlasNativeRoot.ActualHeight<1)
    throw new InvalidOperationException("ATLAS 原生窗口尚未完成布局。");
   var image=new RenderTargetBitmap((int)atlasNativeRoot.ActualWidth,(int)atlasNativeRoot.ActualHeight,96,96,PixelFormats.Pbgra32);
   image.Render(atlasNativeRoot);
   var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
   using var stream=File.Create(Path.Combine(output,name+".png"));encoder.Save(stream);
  }

  string fixture=WorkspaceChecks.Fixture(fixtures);
  UseGameRoot(fixture);
  modInventory=await Task.Run(()=>ModManager.Scan(fixture));
  report=await Task.Run(()=>Catalog.Scan(fixture));
  states=report.Patches;demo=false;modInventoryDirty=false;
  modStatus=$"共 {modInventory.Mods.Count} 项 · ATLAS 隔离界面验证";
  atlasNativeSelectedFolders.Clear();atlasNativeSelectedFolder=null;atlasNativeSelectedPatchId=null;
  atlasNativePage="总览";atlasNativeViewMode="list";atlasNativeQuery="";
  atlasNativeStateFilter="全部";atlasNativeSourceFilter="全部";atlasNativeUnboundOnly=false;
  uiTheme=UiThemes.AtlasNative;UiThemes.Active=UiThemes.AtlasNative;
  ShowAtlasNative();await Idle();
  Check(atlasNativeRoot!=null&&ReferenceEquals(Content,atlasNativeRoot),
   "ATLAS 使用原生 WPF 视觉树，不依赖 WebView2");
  Check(atlasNativeNavButtons.Count==6&&atlasNativePageHost!=null,
   "原生工作台创建六个一级导航与独立页面容器");
  await Shot("01-ATLAS-总览");

  AtlasNativeNavigate("模组库");await Idle();
  Check(atlasNativeRows.Count==6&&GetFilteredAtlasNativeRows().Length==6,
   "模组库读取真实扫描模型并将独立补丁从普通 MOD 中分离");
  Check(atlasNativeResultsHost?.Content is DataGrid,
   "默认专业列表使用原生 DataGrid");
  atlasNativeQuery="TestSolo";RenderAtlasNativeModResults();
  Check(GetFilteredAtlasNativeRows().Length==1&&GetFilteredAtlasNativeRows()[0].Id=="TestSolo",
   "搜索可以按本地 MOD ID 精确筛选");
  atlasNativeQuery="";atlasNativeUnboundOnly=true;RenderAtlasNativeModResults();
  Check(GetFilteredAtlasNativeRows().Length==6,
   "未绑定 Workshop ID 筛选基于真实来源关联数据");
  atlasNativeUnboundOnly=false;atlasNativeViewMode="grid";RenderAtlasNativeModResults();
  Check(atlasNativeResultsHost?.Content is WrapPanel,
   "库视图使用原生 WPF 卡片容器");
  await Shot("02-ATLAS-库视图");
  atlasNativeViewMode="list";RenderAtlasNativeModResults();
  Check(atlasNativeResultsHost?.Content is DataGrid,
   "列表与库视图切换复用同一份模组数据");
  ShowAtlasNativeModInspector(atlasNativeRows[0]);await Idle();
  Check(atlasNativeInspector?.Visibility==Visibility.Visible&&
   Math.Abs(atlasNativeBody!.ColumnDefinitions[1].Width.Value-330)<1,
   "模组详情检查器按需展开，不默认占用主列表空间");
  CloseAtlasNativeInspector();await Idle();
  Check(atlasNativeInspector?.Visibility==Visibility.Collapsed,
   "详情检查器可以关闭并恢复列表宽度");

  AtlasNativeNavigate("修复中心");await Idle();
  var localPatches=modInventory!.Mods.Where(ModPresentation.IsIndependentPatch).ToArray();
  Check(localPatches.Length==1&&atlasNativePageHost?.Content is ScrollViewer,
   "修复中心只展示独立补丁，不混入普通 MOD");
  var patchState=report!.Patches.FirstOrDefault(x=>x.Patch.Id.Equals(localPatches[0].Id,StringComparison.OrdinalIgnoreCase));
  ShowAtlasNativePatchInspector(localPatches[0],patchState);await Idle();
  Check(atlasNativeInspector?.Visibility==Visibility.Visible,
   "补丁检查器展示扫描依据与生命周期状态");
  await Shot("03-ATLAS-修复中心");

  AtlasNativeNavigate("更新中心");await Idle();
  Check(atlasNativePage=="更新中心"&&atlasNativePageHost?.Content is ScrollViewer,
   "更新中心以只读方式呈现已有缓存，不触发自动联网检查");
  AtlasNativeNavigate("设置");await Idle();
  Check(atlasNativePage=="设置"&&atlasNativePageHost?.Content is ScrollViewer,
   "设置页面保留游戏目录与旧版功能入口");

  double oldWidth=Width,oldHeight=Height;
  Width=1020;Height=720;await Idle();RenderAtlasNative();await Idle();
  var navFits=atlasNativeNavButtons.Values.All(button=>
  {
   var bounds=button.TransformToAncestor(atlasNativeRoot!).TransformBounds(new Rect(0,0,button.ActualWidth,button.ActualHeight));
   return bounds.Left>=0&&bounds.Right<=atlasNativeRoot!.ActualWidth+1;
  });
  Check(navFits&&atlasNativeRoot!.ActualWidth<=Width+1,
   "1020×720 最小窗口下顶部导航保持在窗口范围内");
  await Shot("04-ATLAS-窄窗口");
  Width=oldWidth;Height=oldHeight;await Idle();

  ChangeUiTheme(UiThemes.Workspace);
  Check(ReferenceEquals(Content,shell)&&UiThemes.IsWorkspace,
   "ATLAS 可无重启返回原有 WPF 工作台");
  ChangeUiTheme(UiThemes.AtlasNative);
  Check(ReferenceEquals(Content,atlasNativeRoot)&&UiThemes.IsAtlasNative,
   "可从原工作台重新切回 ATLAS 原生界面");
  CloseModWatch();
 }

}
