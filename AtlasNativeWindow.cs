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
  string updateStatus,string latestVersion,string githubStatus,string githubLatestVersion,
  string workshopStatus,string workshopLatestVersion)
 {
  Mod=mod;Source=source;WorkshopId=workshopId;GitHubRepository=githubRepository;
  UpdateStatus=updateStatus;LatestVersion=latestVersion;
  GitHubStatus=githubStatus;GitHubLatestVersion=githubLatestVersion;
  WorkshopStatus=workshopStatus;WorkshopLatestVersion=workshopLatestVersion;
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
 public string GitHubStatus {get;}
 public string GitHubLatestVersion {get;}
 public string WorkshopStatus {get;}
 public string WorkshopLatestVersion {get;}
 public string Meta=>$"{Mod.Id}  ·  {Mod.Author}";
 public string Description=>Mod.Description;
 public string Folder=>Mod.Folder;
 public string Dependencies=>Mod.Dependencies.Length==0?"未声明必需前置":string.Join("、",Mod.Dependencies.Select(x=>x.Id));
 public bool HasUpdate=>GitHubStatus=="有更新"||WorkshopStatus=="有更新";
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
 ContentControl? atlasNativePageHost,atlasNativeResultsHost,atlasNativePagingHost,atlasNativePagingFooterHost;
 Border? atlasNativeInspector,atlasNativeBatchBar;
 TextBlock? atlasNativeGamePath,atlasNativeGameState,atlasNativeFooter,atlasNativeBatchText,atlasNativeResultsSummary;
 Button? atlasNativeLaunchButton;
 readonly Dictionary<string,Button> atlasNativeNavButtons=new(StringComparer.Ordinal);
 string atlasNativePage="总览";
 string? atlasNativeRenderedPage;
 bool atlasNativeEmbeddedWorkflow;
 string atlasNativeSubpageReturnPage="总览";
 string atlasNativePatchFilter="全部",atlasNativePatchQuery="";
 string atlasNativeUpdateFilter="全部",atlasNativeUpdateQuery="";
 const int AtlasNativeUpdatePageSize=24;
 int atlasNativeUpdatePage;
 DispatcherTimer? atlasNativeUpdateSearchDebounce;
 string atlasNativeWorkshopInput="";
 string atlasNativeViewMode="grid";
 const int AtlasNativeGridPageSize=24;
 int atlasNativeGridPage,atlasNativeVisibleRowsCount;
 DispatcherTimer? atlasNativeModSearchDebounce;
 string atlasNativeQuery="";
 string atlasNativeStateFilter="全部";
 string atlasNativeSourceFilter="全部";
 string atlasNativeSort="名称";
 bool atlasNativeUnboundOnly;
 string? atlasNativeSelectedFolder,atlasNativeSelectedPatchId;
 readonly HashSet<string> atlasNativeSelectedFolders=new(StringComparer.OrdinalIgnoreCase);
 List<AtlasNativeModRow> atlasNativeRows=[];
 Action? atlasNativeResetModFilters;

 void ShowAtlasNative()
 {
  if(atlasNativeRoot==null)BuildAtlasNativeShell();
  atlasNativeEmbeddedWorkflow=false;
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
  atlasNativeFooter=AtlasText("",11,AtlasMuted);
  BindingOperations.SetBinding(atlasNativeFooter,TextBlock.TextProperty,
   new Binding(nameof(TextBlock.Text)){Source=toast,Mode=BindingMode.OneWay});
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
   // In a vertical StackPanel, a default Stretch button looks like an invisible
   // full-width hit target. Keep standalone actions visibly anchored to the left.
   HorizontalAlignment=HorizontalAlignment.Left,
   Background=kind switch{
    "primary"=>AtlasBrass,
    "active"=>AtlasBrush("#3B3021"),
    "nav"=>Brushes.Transparent,
    "quiet"=>Brushes.Transparent,
    _=>AtlasPanelAltBrush
   },
   Foreground=kind switch{
    "primary"=>AtlasBrush("#1A1B1C"),
    "active"=>AtlasBrass,
    "nav" or "quiet"=>AtlasMuted,
    _=>AtlasInk
   },
   BorderBrush=kind switch{
    "primary"=>AtlasBrush("#D9B77F"),
    "active"=>AtlasBrush("#6D5737"),
    "nav" or "quiet"=>Brushes.Transparent,
    _=>AtlasBrush("#3A4149")
   },
   FontWeight=kind is "primary" or "active"?FontWeights.SemiBold:FontWeights.Medium
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
  bool compact=ActualWidth>0&&ActualWidth<=1150;
  var root=new Grid{Margin=new Thickness(0,0,0,compact?12:17)};
  var stack=new StackPanel();
  stack.Children.Add(AtlasText(eyebrow.ToUpperInvariant(),11,AtlasBrass,true));
  var titleText=AtlasText(title,compact?26:29,AtlasInk,true);
  titleText.Margin=new Thickness(0,4,0,3);stack.Children.Add(titleText);
  stack.Children.Add(AtlasText(subtitle,compact?12:14,AtlasMuted));
  root.Children.Add(stack);
  if(actions.Length>0){
   var panel=new WrapPanel{
    VerticalAlignment=VerticalAlignment.Center,
    HorizontalAlignment=compact?HorizontalAlignment.Left:HorizontalAlignment.Right
   };
   foreach(var action in actions){
    if(action is FrameworkElement item)item.Margin=new Thickness(0,compact?5:3,6,compact?2:3);
    panel.Children.Add(action);
   }
   if(compact){
    root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
    root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
    Grid.SetRow(panel,1);
   }else{
    root.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
    root.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
    Grid.SetColumn(panel,1);
   }
   root.Children.Add(panel);
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

 // Calculate columns from the actual WPF viewport, not the desktop window width:
 // the inspector can be opened without resizing the window.
 static int AtlasNativeCardColumns(double availableWidth,double minCardWidth,double gap=13)
 {
  if(!double.IsFinite(availableWidth)||availableWidth<=0)return 1;
  return Math.Max(1,(int)Math.Floor((availableWidth+gap)/(minCardWidth+gap)));
 }

 void AtlasNativeFitCards(WrapPanel panel,double minCardWidth,double gap=13)
 {
  void Fit()
  {
   double available=panel.ActualWidth;
   if(!double.IsFinite(available)||available<=0)return;
   int cardCount=panel.Children.OfType<Border>().Count();
   int columns=Math.Min(Math.Max(1,cardCount),AtlasNativeCardColumns(available,minCardWidth,gap));
   // A lone patch/update result should not occupy half a page. Cap the
   // expanded width so long text does not turn into an unreadable line.
   double width=Math.Max(180,Math.Floor((available-columns*gap)/columns));
   if(cardCount==1)width=Math.Min(width,720);
   foreach(var card in panel.Children.OfType<Border>())
    if(Math.Abs(card.Width-width)>0.5)card.Width=width;
  }
  panel.Loaded+=(_,_)=>Fit();
  panel.SizeChanged+=(_,_)=>Fit();
 }

 void RenderAtlasNative()
 {
  if(atlasNativeRoot==null||atlasNativePageHost==null)return;
  // A source refresh should not force the user to start reading the same
  // workspace from the top. Actual section navigation still starts at 0.
  double savedOffset=atlasNativeRenderedPage==atlasNativePage &&
   atlasNativePageHost.Content is ScrollViewer previousScroll
    ?previousScroll.VerticalOffset:0;
  Content=atlasNativeRoot;
  atlasNativeRows=BuildAtlasNativeRows();
  // A folder removed by FileSystemWatcher must not stay in a batch selection.
  // Do not prune while the inventory is still loading.
  if(modInventory!=null)
   atlasNativeSelectedFolders.IntersectWith(atlasNativeRows.Select(x=>x.Mod.Folder));
  UpdateAtlasNativeHeader();
  foreach(var pair in atlasNativeNavButtons){
   bool active=pair.Key==atlasNativePage;
   pair.Value.Background=active?AtlasBrush("#3B3021"):Brushes.Transparent;
   pair.Value.BorderBrush=active?AtlasBrush("#6D5737"):Brushes.Transparent;
   pair.Value.Foreground=active?AtlasBrush("#E6C58F"):AtlasMuted;
  }
  // Scans and update checks may complete while an existing WPF confirmation or
  // detail page is embedded. Refresh counts/header, not the user's active workflow.
  if(atlasNativeEmbeddedWorkflow&&atlasNativePageHost.Content is Grid)
  {
   // SetPage already closes its inspector; do not reopen a stale sidebar over
   // the user's active confirmation or detailed workflow.
   return;
  }
  atlasNativePageHost.Content=atlasNativePage switch{
   "模组库"=>BuildAtlasNativeModsPage(),
   "修复中心"=>BuildAtlasNativePatchesPage(),
   "更新中心"=>BuildAtlasNativeUpdatesPage(),
   "下载中心"=>BuildAtlasNativeDownloadsPage(),
   "设置"=>BuildAtlasNativeSettingsPage(),
   _=>BuildAtlasNativeOverviewPage()
  };
  atlasNativeRenderedPage=atlasNativePage;
  if(savedOffset>0&&atlasNativePageHost.Content is ScrollViewer restoredScroll)
   Dispatcher.BeginInvoke(new Action(()=>{
    if(ReferenceEquals(atlasNativePageHost.Content,restoredScroll))
     restoredScroll.ScrollToVerticalOffset(Math.Min(savedOffset,restoredScroll.ScrollableHeight));
   }),DispatcherPriority.Loaded);
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
  if(atlasNativeFooter!=null&&toast.Text.Length==0){
   string detail=modInventory==null?"MOD 清单尚未读取":$"{modInventory.Mods.Count(m=>!ModPresentation.IsIndependentPatch(m))} 个普通 MOD · {modInventory.Mods.Count(m=>!ModPresentation.IsIndependentPatch(m)&&m.Enabled)} 个启用";
   toast.Text=detail+"  ·  "+ManagerUpdates.CurrentVersion;
  }
 }

 void AtlasNativeNavigate(string page)
 {
  atlasNativeEmbeddedWorkflow=false;
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
   toast.Text="管理器正在执行其他操作，请稍后刷新。";
   return;
  }
  if(selectedInstallRoot==null){RenderAtlasNative();return;}
  string root=selectedInstallRoot;
  try{
   // Explicit refreshes include patch catalog and file hashes. The watcher
   // uses the lightweight inventory-only path below instead.
   await ScanFolder(root);
   if(!root.Equals(selectedInstallRoot,StringComparison.OrdinalIgnoreCase))return;
   if(modInventory==null)await RefreshModsAsync();
  }
  catch(Exception ex){toast.Text="ATLAS 刷新失败："+ex.Message;}
  finally{RenderAtlasNative();}
 }

 // A watcher has already invalidated patch health/file scan conclusions.
 // Refresh the MOD inventory (including safe orphan-patch reconciliation)
 // without silently recreating a patch health report or navigating away.
 async Task RefreshAtlasNativeInventoryFromWatchAsync()
 {
  if(busy||transactionWriting){
   modWatchDebounce?.Stop();
   modWatchDebounce?.Start();
   return;
  }
  if(selectedInstallRoot==null)return;
  try{
   // RefreshModsAsync already updates the native surface in its finally block.
   // Avoid a second full visual-tree rebuild after every watcher burst.
   await RefreshModsAsync();
  }
  catch(Exception ex)
  {
   modInventoryDirty=true;
   toast.Text="ATLAS MOD 清单自动刷新失败："+ex.Message+"。请手动刷新。";
   RenderAtlasNative();
  }
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
  var gamePath=AtlasText(selectedInstallRoot==null?"选择游戏安装目录，开始读取本地 MOD 环境。":Path.GetFileName(selectedInstallRoot.TrimEnd(Path.DirectorySeparatorChar)),13,AtlasMuted);
  gamePath.TextTrimming=TextTrimming.CharacterEllipsis;gamePath.TextWrapping=TextWrapping.NoWrap;
  gamePath.ToolTip=selectedInstallRoot??"尚未选择游戏目录";hero.Children.Add(gamePath);
  var heroNote=AtlasText(modInventoryDirty?"检测到文件变化，当前清单需要重新读取。":
   report==null?"文件扫描结果尚未生成。":"本地扫描完成；文件匹配不等于游戏内兼容性验证。",11,AtlasMuted);
  heroNote.Margin=new Thickness(0,10,0,0);hero.Children.Add(heroNote);
  heroGrid.Children.Add(hero);
  var heroActions=new StackPanel{VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Right};
  heroActions.Children.Add(AtlasButton(selectedInstallRoot==null?"选择游戏目录":"更换游戏目录",ChooseAtlasNativeFolder,"primary"));
  var old=AtlasButton("进入模组库",()=>AtlasNativeNavigate("模组库"));
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

 Border AtlasCompactStat(string value,string label,string note)
 {
  var stack=new StackPanel();
  stack.Children.Add(AtlasText(value,25,AtlasInk,true));
  stack.Children.Add(AtlasText(label,12,AtlasMuted,true));
  stack.Children.Add(AtlasText(note,10,AtlasMuted));
  var panel=AtlasPanel(stack,10,"#1B1F24");
  panel.Width=128;panel.Margin=new Thickness(0,0,8,0);
  return panel;
 }

 UIElement BuildAtlasNativeModsPage()
 {
  var root=new StackPanel{Margin=new Thickness(5,5,5,20)};
  root.Children.Add(AtlasPageTitle("MOD LIBRARY","模组库",
   "已安装 MOD、来源绑定、版本检查与安全启停。批量操作会逐项预览并重新校验。",
   AtlasButton("↻ 刷新",()=>_=RefreshAtlasNativeDataAsync()),
   AtlasButton("导入 ZIP",()=>AtlasNativeRunWorkflow(ChooseWorkshopZip),"quiet"),
   AtlasButton("Workshop 下载",()=>AtlasNativeRunWorkflow(()=>DownloadWorkshopMod()),"quiet"),
   AtlasButton("检查全部更新",()=>AtlasNativeRunWorkflow(CheckAllModUpdates),"primary")));
  if(selectedInstallRoot==null){
   root.Children.Add(AtlasEmpty("尚未选择游戏目录","先定位《杀戮尖塔 2》的安装目录，再读取本地模组。",AtlasButton("选择游戏目录",ChooseAtlasNativeFolder,"primary")));
   return AtlasScroll(root);
  }

  // Cancel the previous page's pending search when a refresh rebuilds the view.
  atlasNativeModSearchDebounce?.Stop();
  var searchDebounce=new DispatcherTimer(DispatcherPriority.Background){
   Interval=TimeSpan.FromMilliseconds(180)
  };
  atlasNativeModSearchDebounce=searchDebounce;
  searchDebounce.Tick+=(_,_)=>{
   searchDebounce.Stop();
   if(atlasNativePage=="模组库"&&ReferenceEquals(atlasNativeModSearchDebounce,searchDebounce))
    RenderAtlasNativeModResults();
  };
  var toolbar=new WrapPanel{Margin=new Thickness(0,0,0,10),VerticalAlignment=VerticalAlignment.Center};
  var search=new TextBox{
   Width=250,Height=36,FontSize=13,Padding=new Thickness(10,7,10,7),
   Background=AtlasBrush("#20252B"),Foreground=AtlasInk,BorderBrush=AtlasBrush("#41474F"),
   BorderThickness=new Thickness(1),ToolTip="搜索 MOD 名称、ID 或作者"
  };
  search.Text=atlasNativeQuery;
  var searchHost=new Grid{Width=250,Height=36};
  searchHost.Children.Add(search);
  var searchHint=AtlasText("⌕  搜索 MOD 名称、ID 或作者",12,AtlasMuted);
  searchHint.Margin=new Thickness(11,0,0,0);searchHint.VerticalAlignment=VerticalAlignment.Center;
  searchHint.IsHitTestVisible=false;searchHint.Visibility=search.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
  searchHost.Children.Add(searchHint);
  search.TextChanged+=(_,_)=>{
   // Keep the logical filter current immediately; defer expensive WPF cards.
   atlasNativeQuery=search.Text;atlasNativeGridPage=0;
   searchHint.Visibility=search.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
   searchDebounce.Stop();searchDebounce.Start();
  };
  toolbar.Children.Add(searchHost);

  var stateCombo=AtlasCombo(["全部","已启用","已停用","有更新"],atlasNativeStateFilter);
  stateCombo.Width=112;stateCombo.Margin=new Thickness(8,0,0,0);
  stateCombo.SelectionChanged+=(_,_)=>{atlasNativeStateFilter=stateCombo.SelectedItem?.ToString()??"全部";atlasNativeGridPage=0;RenderAtlasNativeModResults();};
  toolbar.Children.Add(stateCombo);

  var sourceCombo=AtlasCombo(["全部来源","Workshop","GitHub","本地"],atlasNativeSourceFilter=="全部"?"全部来源":atlasNativeSourceFilter);
  sourceCombo.Width=120;sourceCombo.Margin=new Thickness(8,0,0,0);
  sourceCombo.SelectionChanged+=(_,_)=>{atlasNativeSourceFilter=sourceCombo.SelectedItem?.ToString() switch{"全部来源"=>"全部",var x=>x??"全部"};atlasNativeGridPage=0;RenderAtlasNativeModResults();};
  toolbar.Children.Add(sourceCombo);

  var unbound=new CheckBox{
   Style=(Style)FindResource("ModCardCheckBox"),
   Content="未绑定 Workshop ID",IsChecked=atlasNativeUnboundOnly,
   Foreground=AtlasMuted,FontSize=12,VerticalAlignment=VerticalAlignment.Center,
   Margin=new Thickness(12,0,8,0),Cursor=Cursors.Hand
  };
  unbound.Checked+=(_,_)=>{atlasNativeUnboundOnly=true;atlasNativeGridPage=0;RenderAtlasNativeModResults();};
  unbound.Unchecked+=(_,_)=>{atlasNativeUnboundOnly=false;atlasNativeGridPage=0;RenderAtlasNativeModResults();};
  toolbar.Children.Add(unbound);

  var sort=AtlasCombo(["名称","有更新优先"],atlasNativeSort);
  sort.Width=118;sort.Margin=new Thickness(8,0,0,0);
  sort.SelectionChanged+=(_,_)=>{atlasNativeSort=sort.SelectedItem?.ToString()??"名称";atlasNativeGridPage=0;RenderAtlasNativeModResults();};
  toolbar.Children.Add(sort);

  var views=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(10,0,0,0)};
  // Initialize both captured references before wiring the callbacks (CS0165).
  Button listButton=new(),gridButton=new();
  listButton=AtlasButton("列表",()=>SwitchView("list"),atlasNativeViewMode=="list"?"active":"quiet");
  gridButton=AtlasButton("库视图",()=>SwitchView("grid"),atlasNativeViewMode=="grid"?"active":"quiet");
  views.Children.Add(listButton);views.Children.Add(gridButton);
  toolbar.Children.Add(views);
  var allVisible=AtlasButton("全选当前结果",SelectAllAtlasVisibleMods,"quiet");
  allVisible.Margin=new Thickness(8,0,0,0);toolbar.Children.Add(allVisible);
  // Reset the live native controls in-place: rebuilding the page on reset
  // discards keyboard focus and the current ScrollViewer position.
  atlasNativeResetModFilters=()=>{
   searchDebounce.Stop();
   atlasNativeQuery="";atlasNativeStateFilter="全部";atlasNativeSourceFilter="全部";
   atlasNativeUnboundOnly=false;atlasNativeSort="名称";atlasNativeGridPage=0;
   if(search.Text.Length>0)search.Text="";
   stateCombo.SelectedItem="全部";
   sourceCombo.SelectedItem="全部来源";
   unbound.IsChecked=false;
   sort.SelectedItem="名称";
   RenderAtlasNativeModResults();
  };
  var reset=AtlasButton("清除筛选",()=>atlasNativeResetModFilters?.Invoke(),"quiet");
  reset.Margin=new Thickness(8,0,0,0);toolbar.Children.Add(reset);
  root.Children.Add(AtlasPanel(toolbar,10,"#20242A"));

  void SwitchView(string mode)
  {
   searchDebounce.Stop();
   atlasNativeViewMode=mode;
   SetViewStyle(listButton,mode=="list");SetViewStyle(gridButton,mode=="grid");
   RenderAtlasNativeModResults();
  }
  void SetViewStyle(Button button,bool active)
  {
   button.Background=active?AtlasBrush("#3B3021"):Brushes.Transparent;
   button.Foreground=active?AtlasBrass:AtlasMuted;
   button.BorderBrush=active?AtlasBrush("#6D5737"):Brushes.Transparent;
   button.FontWeight=active?FontWeights.SemiBold:FontWeights.Medium;
  }

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
  atlasNativePagingHost=new ContentControl();root.Children.Add(atlasNativePagingHost);
  atlasNativeResultsHost=new ContentControl();
  root.Children.Add(atlasNativeResultsHost);
  atlasNativePagingFooterHost=new ContentControl();root.Children.Add(atlasNativePagingFooterHost);
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
  atlasNativeVisibleRowsCount=rows.Length;
  if(atlasNativePagingHost!=null)atlasNativePagingHost.Content=null;
  if(atlasNativePagingFooterHost!=null)atlasNativePagingFooterHost.Content=null;
  if(atlasNativeResultsSummary!=null)atlasNativeResultsSummary.Text=$"显示 {rows.Length} / {atlasNativeRows.Count} 项  ·  已选择 {atlasNativeSelectedFolders.Count} 项  ·  列表视图适合大量 MOD";
  if(atlasNativeBatchBar!=null)atlasNativeBatchBar.Visibility=atlasNativeSelectedFolders.Count>0?Visibility.Visible:Visibility.Collapsed;
  if(atlasNativeBatchText!=null)atlasNativeBatchText.Text="已选择 "+atlasNativeSelectedFolders.Count+" 项";
  if(modInventory==null){
   atlasNativeResultsHost.Content=AtlasEmpty("尚未读取 MOD 清单","刷新本地环境后，这里会显示真实安装的 MOD。",AtlasButton("刷新",()=>_=RefreshAtlasNativeDataAsync(),"primary"));
   return;
  }
  if(rows.Length==0){
   atlasNativeResultsHost.Content=AtlasEmpty("没有符合条件的 MOD","尝试清除筛选条件，或刷新本地清单。",
    AtlasButton("清除筛选",()=>atlasNativeResetModFilters?.Invoke(),"quiet"));
   return;
  }
  if(atlasNativeViewMode=="grid"){
   int pages=Math.Max(1,(rows.Length+AtlasNativeGridPageSize-1)/AtlasNativeGridPageSize);
   atlasNativeGridPage=Math.Clamp(atlasNativeGridPage,0,pages-1);
   int start=atlasNativeGridPage*AtlasNativeGridPageSize;
   int count=Math.Min(AtlasNativeGridPageSize,rows.Length-start);
   var wrap=new WrapPanel();
   // Keep heavy buttons, tooltips and card layout bounded even for 1,000+ MODs.
   foreach(var row in rows.Skip(start).Take(count))wrap.Children.Add(BuildAtlasNativeModCard(row));
   AtlasNativeFitCards(wrap,315);
   atlasNativeResultsHost.Content=wrap;
   if(pages>1&&atlasNativePagingHost!=null)
   {
    void ChangePage(int delta)
    {
     atlasNativeGridPage=Math.Clamp(atlasNativeGridPage+delta,0,pages-1);
     RenderAtlasNativeModResults();
     // After clicking the bottom pager, start at the first card of the next
     // page instead of retaining the old page's bottom scroll position.
     Dispatcher.BeginInvoke(new Action(()=>atlasNativeResultsHost?.BringIntoView()),
      DispatcherPriority.Loaded);
    }
    StackPanel Pager(bool footer)
    {
     var controls=new StackPanel{Orientation=Orientation.Horizontal,
      Margin=footer?new Thickness(0,11,0,14):new Thickness(0,2,0,10)};
     var previous=AtlasButton("上一页",()=>ChangePage(-1),"quiet");
     previous.IsEnabled=atlasNativeGridPage>0;controls.Children.Add(previous);
     var counter=AtlasText($"第 {atlasNativeGridPage+1} / {pages} 页  ·  本页 {count} 项",12,AtlasMuted);
     counter.Margin=new Thickness(13,9,13,0);controls.Children.Add(counter);
     var next=AtlasButton("下一页",()=>ChangePage(1),"quiet");
     next.IsEnabled=atlasNativeGridPage<pages-1;controls.Children.Add(next);
     return controls;
    }
    atlasNativePagingHost.Content=Pager(false);
    if(atlasNativePagingFooterHost!=null)atlasNativePagingFooterHost.Content=Pager(true);
   }
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
   var updateParts=new List<string>();
   if(repository!=null)updateParts.Add("GitHub "+(gitResult?.Status??"尚未检查"));
   if(workshopId.HasValue)updateParts.Add("Workshop "+(workshopResult?.Status??"尚未检查"));
   string update=updateParts.Count==0?"尚未绑定来源":string.Join(" · ",updateParts);
   var latestParts=new List<string>();
   if(gitResult?.Status=="有更新"&&gitResult.LatestVersion.Length>0)latestParts.Add("GitHub "+gitResult.LatestVersion);
   if(workshopResult?.Status=="有更新"&&workshopResult.RemoteContentVersion.Length>0)latestParts.Add("Workshop "+workshopResult.RemoteContentVersion);
   string latest=string.Join(" / ",latestParts);
   string source=workshopId.HasValue&&repository!=null?"Workshop + GitHub":
    workshopId.HasValue?"Workshop":repository!=null?"GitHub":"本地";
   var row=new AtlasNativeModRow(mod,source,workshopId?.ToString()??"",repository??"",update,latest,
    gitResult?.Status??(repository==null?"未关联":"尚未检查"),gitResult?.LatestVersion??"",
    workshopResult?.Status??(workshopId.HasValue?"尚未检查":"未绑定"),workshopResult?.RemoteContentVersion??"");
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
   Width=390,MinHeight=225,Margin=new Thickness(0,0,13,13),
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
  title.HorizontalAlignment=HorizontalAlignment.Left;
  title.ToolTip=row.Name;top.Children.Add(title);
  var check=new CheckBox{
   Style=(Style)FindResource("ModCardCheckBox"),IsChecked=row.IsSelected,
   VerticalAlignment=VerticalAlignment.Top,Foreground=AtlasBrass,ToolTip="加入批量选择"
  };
  check.Checked+=(_,_)=>{row.IsSelected=true;UpdateAtlasNativeSelection();};
  check.Unchecked+=(_,_)=>{row.IsSelected=false;UpdateAtlasNativeSelection();};
  Grid.SetColumn(check,1);top.Children.Add(check);root.Children.Add(top);

  var details=new StackPanel{Margin=new Thickness(0,11,0,11)};
  details.Children.Add(AtlasText(row.Id+"  ·  "+row.Author,12,AtlasMuted));
  details.Children.Add(AtlasText("版本 "+row.Version+"    来源 "+row.Source,14,AtlasInk));
  if(row.WorkshopId.Length>0)details.Children.Add(AtlasText("Workshop ID  "+row.WorkshopId,11,AtlasMuted));
  if(row.GitHubRepository.Length>0)details.Children.Add(AtlasText("GitHub  "+row.GitHubRepository,11,AtlasMuted));
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
  grid.Columns.Add(new DataGridTextColumn{Header="更新状态",Binding=new Binding(nameof(AtlasNativeModRow.UpdateStatus)),Width=220});
  var actionPanel=new FrameworkElementFactory(typeof(StackPanel));
  actionPanel.SetValue(StackPanel.OrientationProperty,Orientation.Horizontal);
  var detailButton=new FrameworkElementFactory(typeof(Button));
  detailButton.SetValue(ContentControl.ContentProperty,"详情");
  detailButton.SetValue(FrameworkElement.StyleProperty,FindResource("ModToolbarAction"));
  detailButton.SetValue(Control.PaddingProperty,new Thickness(7,4,7,4));
  detailButton.AddHandler(Button.ClickEvent,new RoutedEventHandler((sender,_)=>{
   if(sender is FrameworkElement element&&element.DataContext is AtlasNativeModRow row)ShowAtlasNativeModInspector(row);
  }));
  actionPanel.AppendChild(detailButton);
  var toggleButton=new FrameworkElementFactory(typeof(Button));
  toggleButton.SetValue(ContentControl.ContentProperty,"启停预览");
  toggleButton.SetValue(FrameworkElement.StyleProperty,FindResource("ModToolbarPrimary"));
  toggleButton.SetValue(Control.PaddingProperty,new Thickness(7,4,7,4));
  toggleButton.SetValue(FrameworkElement.MarginProperty,new Thickness(5,0,0,0));
  toggleButton.AddHandler(Button.ClickEvent,new RoutedEventHandler((sender,_)=>{
   if(sender is not FrameworkElement element||element.DataContext is not AtlasNativeModRow row)return;
   // A virtualized row can outlive the scan that first supplied it. Recheck the
   // same file/transaction guards used by card actions before opening a preview.
   if(!AtlasNativeToggleAllowed(row.Mod,out string? blocked)){
    toast.Text=blocked??"当前无法预览启停，请等待扫描或文件操作结束。";return;
   }
   AtlasNativeRunWorkflow(()=>PrepareModToggle(row.Mod));
  }));
  actionPanel.AppendChild(toggleButton);
  grid.Columns.Add(new DataGridTemplateColumn{Header="操作",Width=155,CellTemplate=new DataTemplate{VisualTree=actionPanel}});
  grid.SelectionChanged+=(_,_)=>{if(grid.SelectedItem is AtlasNativeModRow row)ShowAtlasNativeModInspector(row);};
  return grid;
 }

 void UpdateAtlasNativeSelection()
 {
  atlasNativeSelectedFolders.Clear();
  foreach(var row in atlasNativeRows.Where(x=>x.IsSelected))atlasNativeSelectedFolders.Add(row.Mod.Folder);
  if(atlasNativeBatchBar!=null)atlasNativeBatchBar.Visibility=atlasNativeSelectedFolders.Count>0?Visibility.Visible:Visibility.Collapsed;
  if(atlasNativeBatchText!=null)atlasNativeBatchText.Text="已选择 "+atlasNativeSelectedFolders.Count+" 项";
  if(atlasNativeResultsSummary!=null)
   atlasNativeResultsSummary.Text=$"显示 {atlasNativeVisibleRowsCount} / {atlasNativeRows.Count} 项  ·  已选择 {atlasNativeSelectedFolders.Count} 项  ·  列表视图适合大量 MOD";
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

 bool AtlasNativePatchNeedsReview(string tag)
 {
  return tag is "需重新检查" or "未检查" or "未收录" or "暂无确认证据" or "已确认不兼容" ||
   tag.Contains("需核对",StringComparison.Ordinal) ||
   tag.Contains("疑似上游修复",StringComparison.Ordinal) ||
   tag.Contains("复查",StringComparison.Ordinal) ||
   tag.Contains("失效",StringComparison.Ordinal) ||
   tag.Contains("过期",StringComparison.Ordinal);
 }

 UIElement BuildAtlasNativePatchesPage()
 {
  var root=new StackPanel{Margin=new Thickness(5,5,5,20)};
  root.Children.Add(AtlasPageTitle("REPAIR WORKSPACE","修复中心",
   "独立补丁、文件匹配、健康生命周期和安全停用依据。普通 MOD 不会混入补丁列表。",
   AtlasButton("刷新文件",()=>_=RefreshAtlasNativeDataAsync()),
   AtlasButton("重新检查健康",AtlasNativeRefreshLifecycle,"primary"),
   AtlasButton("整理补丁",()=>AtlasNativeRunWorkflow(PreparePatchOrganization),"quiet")));
  var repairTabs=new WrapPanel{Margin=new Thickness(0,0,0,10)};
  repairTabs.Children.Add(AtlasButton("本地补丁",()=>AtlasNativeNavigate("修复中心"),"primary"));
  repairTabs.Children.Add(AtlasButton("补丁库",()=>AtlasNativeRunWorkflow(()=>Navigate("补丁库")),"quiet"));
  repairTabs.Children.Add(AtlasButton("在线补丁",()=>AtlasNativeRunWorkflow(()=>Navigate("在线补丁")),"quiet"));
  repairTabs.Children.Add(AtlasButton("补丁健康",()=>AtlasNativeRunWorkflow(()=>Navigate("补丁健康")),"quiet"));
  repairTabs.Children.Add(AtlasButton("安装与回滚",()=>AtlasNativeRunWorkflow(()=>Navigate("安装与回滚")),"quiet"));
  root.Children.Add(repairTabs);
  if(selectedInstallRoot==null||modInventory==null)
  {
   root.Children.Add(AtlasEmpty("尚未读取本地补丁","选择游戏目录并刷新后，显示实际安装的独立修复补丁。",
    AtlasButton("刷新环境",()=>_=RefreshAtlasNativeDataAsync(),"primary")));
   return AtlasScroll(root);
  }

  var patches=modInventory.Mods.Where(ModPresentation.IsIndependentPatch)
   .OrderBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).ToArray();
  var active=patches.Count(x=>x.Enabled);
  var needsReview=patches.Count(x=>AtlasNativePatchNeedsReview(LifecycleTag(x.Id)));
  var retireable=HasValidLifecycleReport&&!lifecycleScanning
   ?patches.Count(mod=>lifecycleReport!.Patches.Any(item=>
     item.CanRetire&&item.Patch.Id.Equals(mod.Id,StringComparison.OrdinalIgnoreCase))):0;
  var stats=new WrapPanel{Margin=new Thickness(0,0,0,9)};
  stats.Children.Add(AtlasCompactStat(patches.Length.ToString(),"本地独立补丁","不含普通 MOD"));
  stats.Children.Add(AtlasCompactStat(active.ToString(),"当前启用","文件完整保留"));
  stats.Children.Add(AtlasCompactStat(needsReview.ToString(),"需要复查","最近检查结论"));
  stats.Children.Add(AtlasCompactStat(retireable.ToString(),"有依据可退役","仍需预览校验"));
  root.Children.Add(stats);

  var statusGrid=new Grid();
  statusGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
  statusGrid.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
  var status=new StackPanel();
  status.Children.Add(AtlasText("补丁健康状态",13,AtlasInk,true));
  status.Children.Add(AtlasText(HasValidLifecycleReport
   ?"最近检查："+lifecycleReport!.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")+" · "+lifecycleStatus
   :lifecycleStale?"文件变化后，旧健康结论已失效。请重新检查。":"尚未生成健康报告。首次检查会建立 MOD 文件指纹基线。",
   11,AtlasMuted));
  if(report?.Issues.Count>0)
   status.Children.Add(AtlasText("扫描问题："+string.Join("；",report.Issues.Take(2)),11,AtlasRed));
  statusGrid.Children.Add(status);
  bool autoStop=OrphanPatchAutoStop.Enabled(installer.DataRoot);
  var autoButton=AtlasButton("孤立补丁自动停用："+(autoStop?"开启":"关闭"),()=>{
   if(busy||transactionWriting){
    toast.Text="正在扫描或执行文件操作，请完成后再修改孤立补丁保护设置。";
    return;
   }
   try{
    // Read the current persisted preference at click time. A background page
    // refresh must not toggle a stale snapshot of the previous setting.
    bool enabled=OrphanPatchAutoStop.Enabled(installer.DataRoot);
    OrphanPatchAutoStop.SetEnabled(installer.DataRoot,!enabled);
    orphanPatchNotice=enabled?"已关闭孤立补丁自动停用。":"已开启孤立补丁自动停用；文件仍会保留。";
    toast.Text=orphanPatchNotice;RenderAtlasNative();
   }catch(Exception ex){toast.Text="设置保存失败："+ex.Message;}
  },"quiet");
  autoButton.VerticalAlignment=VerticalAlignment.Center;
  Grid.SetColumn(autoButton,1);statusGrid.Children.Add(autoButton);
  root.Children.Add(AtlasPanel(statusGrid,10));

  if(patches.Length==0)
  {
   root.Children.Add(AtlasEmpty("当前没有独立补丁","普通 MOD 不会显示在这里。可前往在线补丁库查看已审核的修复方案。",
    AtlasButton("打开在线补丁库",()=>AtlasNativeRunWorkflow(()=>Navigate("在线补丁")),"primary")));
   return AtlasScroll(root);
  }

  // Filter only the result host so the page scroll offset and search focus stay intact.
  var resultsHost=new ContentControl();
  var resultSummary=AtlasText("",11,AtlasMuted);
  var search=new TextBox{
   Text=atlasNativePatchQuery,Height=38,FontSize=14,Padding=new Thickness(12,7,12,7),
   Background=AtlasBrush("#20252B"),Foreground=AtlasInk,BorderBrush=AtlasBrush("#59636E"),
   BorderThickness=new Thickness(1),ToolTip="搜索补丁名称、ID 或关联 MOD"
  };
  var filterButtons=new Dictionary<string,Button>(StringComparer.Ordinal);
  void SetPatchFilter(string selected)
  {
   atlasNativePatchFilter=selected;
   foreach(var pair in filterButtons)
   {
    bool active=pair.Key==selected;
    pair.Value.Background=active?AtlasBrush("#3B3021"):Brushes.Transparent;
    pair.Value.BorderBrush=active?AtlasBrush("#6D5737"):Brushes.Transparent;
    pair.Value.Foreground=active?AtlasBrass:AtlasMuted;
    pair.Value.FontWeight=active?FontWeights.SemiBold:FontWeights.Medium;
   }
   RenderAtlasNativePatchResults(resultsHost,patches,resultSummary);
  }
  var filters=new WrapPanel{Margin=new Thickness(0,10,0,6)};
  foreach(string filter in new[]{"全部","已启用","已停用","需要复查","可安全退役"})
  {
   string selected=filter;
   var button=AtlasButton(filter,()=>SetPatchFilter(selected),
    atlasNativePatchFilter==filter?"active":"quiet");
   button.Margin=new Thickness(0,0,7,4);
   filterButtons[selected]=button;filters.Children.Add(button);
  }
  var reset=AtlasButton("清除筛选",()=>{
   atlasNativePatchQuery="";search.Text="";
   SetPatchFilter("全部");
  },"quiet");
  reset.Margin=new Thickness(0,0,7,4);filters.Children.Add(reset);
  root.Children.Add(filters);

  var searchHost=new Grid{Height=38,Margin=new Thickness(0,0,0,2)};
  searchHost.Children.Add(search);
  var searchHint=AtlasText("⌕  搜索补丁名称、ID 或关联 MOD",12,AtlasMuted);
  searchHint.Margin=new Thickness(12,0,0,0);searchHint.VerticalAlignment=VerticalAlignment.Center;
  searchHint.IsHitTestVisible=false;searchHint.Visibility=search.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
  searchHost.Children.Add(searchHint);
  search.TextChanged+=(_,_)=>{
   atlasNativePatchQuery=search.Text;
   searchHint.Visibility=search.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
   RenderAtlasNativePatchResults(resultsHost,patches,resultSummary);
  };
  root.Children.Add(searchHost);
  resultSummary.Margin=new Thickness(2,5,0,0);
  root.Children.Add(resultSummary);
  root.Children.Add(resultsHost);
  RenderAtlasNativePatchResults(resultsHost,patches,resultSummary);
  return AtlasScroll(root);
 }

 void RenderAtlasNativePatchResults(ContentControl host,ManagedMod[] patches,TextBlock? summary=null)
 {
  bool Matches(ManagedMod mod)
  {
   string tag=LifecycleTag(mod.Id);
   var life=lifecycleReport?.Patches.FirstOrDefault(x=>x.Patch.Id.Equals(mod.Id,StringComparison.OrdinalIgnoreCase));
   bool review=AtlasNativePatchNeedsReview(tag);
   bool filter=atlasNativePatchFilter switch{
    "已启用"=>mod.Enabled,"已停用"=>!mod.Enabled,"需要复查"=>review,
    "可安全退役"=>HasValidLifecycleReport&&!lifecycleScanning&&life?.CanRetire==true,_=>true
   };
   string query=atlasNativePatchQuery.Trim();
   var state=report?.Patches.FirstOrDefault(x=>x.Patch.Id.Equals(mod.Id,StringComparison.OrdinalIgnoreCase));
   string targets=state?.Patch.Requirements.Length>0?string.Join(" ",state.Patch.Requirements):"";
   return filter&&(query.Length==0||(mod.Name+" "+mod.Id+" "+targets).Contains(query,StringComparison.OrdinalIgnoreCase));
  }
  var shown=patches.Where(Matches).ToArray();
  if(summary!=null)summary.Text=$"显示 {shown.Length} / {patches.Length} 项独立补丁";
  var wrap=new WrapPanel{Margin=new Thickness(0,9,0,10)};
  foreach(var mod in shown)
  {
   var state=report?.Patches.FirstOrDefault(x=>x.Patch.Id.Equals(mod.Id,StringComparison.OrdinalIgnoreCase));
   var lifecycle=lifecycleReport?.Patches.FirstOrDefault(x=>x.Patch.Id.Equals(mod.Id,StringComparison.OrdinalIgnoreCase));
   string health=LifecycleTag(mod.Id);
   string fileStatus=state?.Status??"尚未扫描";
   string[] targets=state?.Patch.Requirements??[];
   var card=new Border{
    Width=450,MinHeight=225,Margin=new Thickness(0,0,12,12),
    Background=AtlasPanelBrush,BorderBrush=AtlasBrush("#46515D"),
    BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Padding=new Thickness(15)
   };
   var body=new StackPanel();
   var heading=new Grid();
   heading.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
   heading.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
   var name=AtlasText(mod.Name,16,AtlasInk,true);name.MaxWidth=280;name.TextTrimming=TextTrimming.CharacterEllipsis;
   name.HorizontalAlignment=HorizontalAlignment.Left;
   name.ToolTip=mod.Name;heading.Children.Add(name);
   var enabled=AtlasBadge(mod.Enabled?"已启用":"已停用",mod.Enabled?"ok":"neutral");
   Grid.SetColumn(enabled,1);heading.Children.Add(enabled);body.Children.Add(heading);
   body.Children.Add(AtlasText(mod.Id+"  ·  v"+mod.Version,11,AtlasMuted));
   var healthKind=AtlasNativePatchNeedsReview(health)?"warn":
    health is "已知构建匹配" or "上游修复已验证"?"ok":"neutral";
   var badges=new WrapPanel{Margin=new Thickness(0,8,0,7)};
   badges.Children.Add(AtlasBadge("文件："+fileStatus,fileStatus.Contains("一致")||fileStatus=="已安装"?"ok":"neutral"));
   badges.Children.Add(AtlasBadge("健康："+health,healthKind));
   body.Children.Add(badges);
   body.Children.Add(AtlasText(targets.Length==0?"关联目标：补丁清单未声明目标 MOD":"关联目标："+string.Join("、",targets),12,AtlasMuted));
   string reason=lifecycle?.Message??state?.Reason??"暂无可展示的详细扫描依据。";
   var explanation=AtlasText(reason,12,AtlasMuted);explanation.Margin=new Thickness(0,6,0,7);
   explanation.MaxHeight=50;explanation.TextTrimming=TextTrimming.CharacterEllipsis;explanation.ToolTip=reason;
   body.Children.Add(explanation);
   var actions=new WrapPanel();
   actions.Children.Add(AtlasButton("查看依据",()=>ShowAtlasNativePatchInspector(mod,state),"quiet"));
   var toggle=AtlasButton(mod.Enabled?"预览停用":"预览启用",
    ()=>AtlasNativeRunWorkflow(()=>PrepareModToggle(mod)),mod.Enabled?"quiet":"primary");
   toggle.IsEnabled=AtlasNativeToggleAllowed(mod,out _);actions.Children.Add(toggle);
   if(HasValidLifecycleReport&&!lifecycleScanning&&lifecycle?.CanRetire==true)
    actions.Children.Add(AtlasButton("预览安全退役",()=>AtlasNativeRunWorkflow(()=>PrepareRetirement(lifecycle)),"quiet"));
   body.Children.Add(actions);
   card.Child=body;wrap.Children.Add(card);
  }
  AtlasNativeFitCards(wrap,370,12);
  host.Content=shown.Length==0
   ?AtlasEmpty("没有符合条件的补丁","清除筛选条件，或重新检查补丁健康状态。",
    AtlasButton("清除筛选",()=>{
     atlasNativePatchFilter="全部";atlasNativePatchQuery="";RenderAtlasNative();
    },"quiet"))
   :wrap;
 }

 UIElement BuildAtlasNativeUpdatesPage()
 {
  var root=new StackPanel{Margin=new Thickness(5,5,5,20)};
  root.Children.Add(AtlasPageTitle("UPDATE CENTER","更新中心",
   "GitHub 与 Steam Workshop 更新检查。检查在后台运行，不会自动下载或覆盖 MOD。",
   AtlasButton("检查全部更新",()=>AtlasNativeRunWorkflow(CheckAllModUpdates),"primary"),
   AtlasButton("模组库",()=>AtlasNativeNavigate("模组库"),"quiet")));
  if(selectedInstallRoot==null||modInventory==null)
  {
   root.Children.Add(AtlasEmpty("尚未读取 MOD 清单","先选择游戏目录并刷新本地环境。",
    AtlasButton("刷新环境",()=>_=RefreshAtlasNativeDataAsync(),"primary")));
   return AtlasScroll(root);
  }
  var rows=atlasNativeRows.Where(x=>x.GitHubRepository.Length>0||x.WorkshopId.Length>0)
   .OrderByDescending(x=>x.HasUpdate).ThenBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).ToArray();
  int available=rows.Count(x=>x.HasUpdate);
  int githubBound=rows.Count(x=>x.GitHubRepository.Length>0);
  int workshopBound=rows.Count(x=>x.WorkshopId.Length>0);
  var stats=new WrapPanel{Margin=new Thickness(0,0,0,13)};
  stats.Children.Add(AtlasStat(available.ToString(),"发现更新","来自已缓存检查结果"));
  stats.Children.Add(AtlasStat(githubBound.ToString(),"GitHub 已关联","公开仓库"));
  stats.Children.Add(AtlasStat(workshopBound.ToString(),"Workshop 已关联","创意工坊来源"));
  root.Children.Add(stats);
  root.Children.Add(AtlasText(modUpdateCheckRunning
   ?"正在检查来源更新。你可以切换其他页面，检查完成后结果会保存在这里。"
   :available>0?"有更新的项目排在前面。下载前仍会核对来源、版本与文件完整性。"
   :"进入页面不会自动联网；点击“检查全部更新”后才会向已绑定来源查询。",
   12,AtlasMuted));
  if(rows.Length==0)
  {
   root.Children.Add(AtlasEmpty("没有已关联来源的 MOD","可以在模组详情中查找或手动关联 GitHub，也可以关联 Workshop ID。",
    AtlasButton("前往模组库",()=>AtlasNativeNavigate("模组库"),"primary")));
   return AtlasScroll(root);
  }

  // Initialize captured controls before wiring a timer that invokes
  // RenderResults(); otherwise C# definite assignment rejects the callback.
  var resultsHost=new ContentControl();
  var topPager=new ContentControl();
  var bottomPager=new ContentControl();
  var resultSummary=AtlasText("",11,AtlasMuted);
  atlasNativeUpdateSearchDebounce?.Stop();
  var updateDebounce=new DispatcherTimer(DispatcherPriority.Background){
   Interval=TimeSpan.FromMilliseconds(180)
  };
  atlasNativeUpdateSearchDebounce=updateDebounce;
  updateDebounce.Tick+=(_,_)=>{
   updateDebounce.Stop();
   if(atlasNativePage=="更新中心"&&ReferenceEquals(atlasNativeUpdateSearchDebounce,updateDebounce))
    RenderResults();
  };
  var filters=new WrapPanel{Margin=new Thickness(0,10,0,7)};
  var filterButtons=new Dictionary<string,Button>(StringComparer.Ordinal);
  foreach(string filter in new[]{"全部","有更新","GitHub","Workshop"})
  {
   string selected=filter;
   var button=AtlasButton(filter,()=>SetFilter(selected),
    atlasNativeUpdateFilter==filter?"primary":"quiet");
   button.Margin=new Thickness(0,0,7,4);filters.Children.Add(button);
   filterButtons[selected]=button;
  }
  root.Children.Add(filters);

  var search=new TextBox{
   Text=atlasNativeUpdateQuery,Height=38,FontSize=14,Padding=new Thickness(12,7,12,7),
   Background=AtlasBrush("#20252B"),Foreground=AtlasInk,BorderBrush=AtlasBrush("#59636E"),
   BorderThickness=new Thickness(1),ToolTip="搜索 MOD 名称、ID、GitHub 仓库或 Workshop ID"
  };
  var searchHost=new Grid{Height=38,Margin=new Thickness(0,0,0,5)};
  searchHost.Children.Add(search);
  var searchHint=AtlasText("⌕  搜索 MOD 名称、ID、GitHub 仓库或 Workshop ID",12,AtlasMuted);
  searchHint.Margin=new Thickness(12,0,0,0);searchHint.VerticalAlignment=VerticalAlignment.Center;
  searchHint.IsHitTestVisible=false;searchHint.Visibility=search.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
  searchHost.Children.Add(searchHint);
  search.TextChanged+=(_,_)=>{
   atlasNativeUpdateQuery=search.Text;atlasNativeUpdatePage=0;
   searchHint.Visibility=search.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
   updateDebounce.Stop();updateDebounce.Start();
  };
  root.Children.Add(searchHost);
  resultSummary.Margin=new Thickness(2,2,0,5);root.Children.Add(resultSummary);
  root.Children.Add(topPager);
  root.Children.Add(resultsHost);
  root.Children.Add(bottomPager);
  RenderResults();
  return AtlasScroll(root);

  void SetFilter(string selected)
  {
   updateDebounce.Stop();
   atlasNativeUpdateFilter=selected;atlasNativeUpdatePage=0;
   foreach(var pair in filterButtons)
   {
    bool active=pair.Key==selected;
    pair.Value.Background=active?AtlasBrass:Brushes.Transparent;
    pair.Value.BorderBrush=active?AtlasBrush("#D9B77F"):Brushes.Transparent;
    pair.Value.Foreground=active?AtlasBrush("#1A1B1C"):AtlasMuted;
    pair.Value.FontWeight=active?FontWeights.SemiBold:FontWeights.Medium;
   }
   RenderResults();
  }

  void RenderResults()
  {
   topPager.Content=null;bottomPager.Content=null;
   IEnumerable<AtlasNativeModRow> filtered=rows;
   if(atlasNativeUpdateFilter=="有更新")filtered=filtered.Where(x=>x.HasUpdate);
   else if(atlasNativeUpdateFilter=="GitHub")filtered=filtered.Where(x=>x.GitHubRepository.Length>0);
   else if(atlasNativeUpdateFilter=="Workshop")filtered=filtered.Where(x=>x.WorkshopId.Length>0);
   string query=atlasNativeUpdateQuery.Trim();
   if(query.Length>0)
    filtered=filtered.Where(x=>(x.Name+" "+x.Id+" "+x.GitHubRepository+" "+x.WorkshopId+
     " "+x.GitHubStatus+" "+x.WorkshopStatus).Contains(query,StringComparison.OrdinalIgnoreCase));
   var visible=filtered.ToArray();
   resultSummary.Text=$"显示 {visible.Length} / {rows.Length} 项";
   if(visible.Length==0)
   {
    resultsHost.Content=AtlasEmpty("没有符合条件的 MOD","尝试切换来源筛选，或清空搜索条件。");
    return;
   }
   int pages=(visible.Length+AtlasNativeUpdatePageSize-1)/AtlasNativeUpdatePageSize;
   atlasNativeUpdatePage=Math.Clamp(atlasNativeUpdatePage,0,pages-1);
   int start=atlasNativeUpdatePage*AtlasNativeUpdatePageSize;
   var pageRows=visible.Skip(start).Take(AtlasNativeUpdatePageSize).ToArray();
   if(pages>1)
   {
    void ChangePage(int delta)
    {
     atlasNativeUpdatePage=Math.Clamp(atlasNativeUpdatePage+delta,0,pages-1);
     RenderResults();
     Dispatcher.BeginInvoke(new Action(()=>resultsHost.BringIntoView()),DispatcherPriority.Loaded);
    }
    StackPanel Pager(bool footer)
    {
     var controls=new StackPanel{Orientation=Orientation.Horizontal,
      Margin=footer?new Thickness(0,9,0,14):new Thickness(0,9,0,3)};
     var previous=AtlasButton("上一页",()=>ChangePage(-1),"quiet");
     previous.IsEnabled=atlasNativeUpdatePage>0;controls.Children.Add(previous);
     var label=AtlasText($"第 {atlasNativeUpdatePage+1} / {pages} 页  ·  已筛选 {visible.Length} 项",12,AtlasMuted);
     label.Margin=new Thickness(13,9,13,0);controls.Children.Add(label);
     var next=AtlasButton("下一页",()=>ChangePage(1),"quiet");
     next.IsEnabled=atlasNativeUpdatePage<pages-1;controls.Children.Add(next);
     return controls;
    }
    topPager.Content=Pager(false);
    bottomPager.Content=Pager(true);
   }
   var cards=new WrapPanel{Margin=new Thickness(0,10,0,0)};
   foreach(var row in pageRows)
   {
    var card=new Border{
     Tag="AtlasUpdateCard",Width=430,MinHeight=185,Margin=new Thickness(0,0,12,12),
     Background=AtlasPanelBrush,BorderBrush=AtlasBrush("#46515D"),
     BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Padding=new Thickness(15)
    };
    var body=new StackPanel();
    var heading=new Grid();
    heading.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
    heading.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
    var name=AtlasText(row.Name,16,AtlasInk,true);
    name.TextTrimming=TextTrimming.CharacterEllipsis;name.ToolTip=row.Name;
    heading.Children.Add(name);
    if(row.HasUpdate){var badge=AtlasBadge("发现更新","warn");Grid.SetColumn(badge,1);heading.Children.Add(badge);}
    body.Children.Add(heading);
    body.Children.Add(AtlasText(row.Id+"  ·  本地版本 "+row.Version,11,AtlasMuted));
    var sources=new StackPanel{Margin=new Thickness(0,9,0,9)};
    if(row.GitHubRepository.Length>0)
    {
     var status=row.GitHubStatus+(row.GitHubLatestVersion.Length>0?" · 最新 "+row.GitHubLatestVersion:"");
     sources.Children.Add(AtlasText("GitHub  ·  "+status,12,row.GitHubStatus=="有更新"?AtlasBrass:AtlasMuted));
     var repoText=AtlasText(row.GitHubRepository,10,AtlasMuted);
     repoText.TextTrimming=TextTrimming.CharacterEllipsis;repoText.ToolTip=row.GitHubRepository;
     sources.Children.Add(repoText);
    }
    if(row.WorkshopId.Length>0)
    {
     var status=row.WorkshopStatus+(row.WorkshopLatestVersion.Length>0?" · 最新 "+row.WorkshopLatestVersion:"");
     sources.Children.Add(AtlasText("Workshop  ·  "+status,12,row.WorkshopStatus=="有更新"?AtlasBrass:AtlasMuted));
     sources.Children.Add(AtlasText("Workshop ID  "+row.WorkshopId,10,AtlasMuted));
    }
    body.Children.Add(sources);
    var actions=new WrapPanel();
    actions.Children.Add(AtlasButton("查看详情",()=>ShowAtlasNativeModInspector(row),"quiet"));
    if(row.GitHubRepository.Length>0)
    {
     actions.Children.Add(AtlasButton("检查 GitHub",()=>AtlasNativeRunWorkflow(()=>CheckGitHubMod(row.Mod)),"quiet"));
     var update=AtlasButton("下载 GitHub 更新",()=>AtlasNativeRunWorkflow(()=>UpdateGitHubMod(row.Mod)),"primary");
     update.IsEnabled=row.GitHubStatus=="有更新"&&!busy&&!transactionWriting&&!modInventoryDirty;
     actions.Children.Add(update);
    }
    if(row.WorkshopId.Length>0)
    {
     actions.Children.Add(AtlasButton("检查 Workshop",()=>AtlasNativeRunWorkflow(()=>CheckWorkshopMod(row.Mod)),"quiet"));
     var update=AtlasButton("下载 Workshop 更新",()=>AtlasNativeRunWorkflow(()=>DownloadWorkshopMod(row.Mod)),"primary");
     update.IsEnabled=row.WorkshopStatus=="有更新"&&!busy&&!transactionWriting&&!modInventoryDirty;
     actions.Children.Add(update);
    }
    body.Children.Add(actions);
    card.Child=body;cards.Children.Add(card);
   }
   AtlasNativeFitCards(cards,355,12);
   resultsHost.Content=cards;
  }
 }

 UIElement BuildAtlasNativeDownloadsPage()
 {
  var root=new StackPanel{Margin=new Thickness(5,5,5,20)};
  root.Children.Add(AtlasPageTitle("DOWNLOAD WORKSPACE","下载中心",
   "输入创意工坊链接、检查 GitHub 更新或导入本地 ZIP。下载安装继续复用原有安全预检与确认流程。"));

  var activity=new StackPanel();
  activity.Children.Add(AtlasText("操作状态",13,AtlasInk,true));
  var activityText=AtlasText(toast.Text,12,AtlasMuted);
  activityText.Margin=new Thickness(0,4,0,0);
  BindingOperations.SetBinding(activityText,TextBlock.TextProperty,
   new Binding(nameof(TextBlock.Text)){Source=toast,Mode=BindingMode.OneWay});
  activity.Children.Add(activityText);
  var activityPanel=AtlasPanel(activity,11);
  activityPanel.Margin=new Thickness(0,0,0,13);
  root.Children.Add(activityPanel);

  var workshop=new StackPanel();
  workshop.Children.Add(AtlasText("Steam Workshop / 尖塔补给站",19,AtlasInk,true));
  var workshopNote=AtlasText("支持纯数字 Workshop ID、Steam 分享链接及尖塔补给站链接。输入仅解析 ID，不会自动联网下载。",12,AtlasMuted);
  workshopNote.Margin=new Thickness(0,5,0,10);workshop.Children.Add(workshopNote);
  var workshopInput=new TextBox{
   Text=atlasNativeWorkshopInput,Height=40,FontSize=14,
   Padding=new Thickness(12,8,12,8),
   Background=AtlasBrush("#20252B"),Foreground=AtlasInk,
   BorderBrush=AtlasBrush("#59636E"),BorderThickness=new Thickness(1),
   ToolTip="输入 Workshop ID 或分享链接，按获取并预览后才会请求内容"
  };
  System.Windows.Automation.AutomationProperties.SetName(workshopInput,"ATLAS 创意工坊链接或 ID");
  var inputGrid=new Grid{Height=40};
  inputGrid.Children.Add(workshopInput);
  var hint=AtlasText("⌕  粘贴 Workshop ID / Steam 链接",12,AtlasMuted);
  hint.Margin=new Thickness(12,0,0,0);hint.VerticalAlignment=VerticalAlignment.Center;
  hint.IsHitTestVisible=false;inputGrid.Children.Add(hint);
  workshop.Children.Add(inputGrid);
  var validation=AtlasText("",12,AtlasMuted);
  validation.Margin=new Thickness(0,8,0,7);workshop.Children.Add(validation);
  void ValidateInput()
  {
   atlasNativeWorkshopInput=workshopInput.Text;
   hint.Visibility=workshopInput.Text.Length==0?Visibility.Visible:Visibility.Collapsed;
   if(string.IsNullOrWhiteSpace(workshopInput.Text))
   {
    validation.Foreground=AtlasMuted;
    validation.Text="尚未输入来源；不会自动请求创意工坊。";
   }
   else if(WorkshopModInstaller.TryParseWorkshopId(workshopInput.Text,out ulong id))
   {
    validation.Foreground=AtlasGreen;
    validation.Text="已识别 Workshop ID："+id+"。仅完成链接格式验证，内容仍需下载后核对。";
   }
   else
   {
    validation.Foreground=AtlasBrass;
    validation.Text="链接或 ID 无效。请使用数字 ID、Steam 分享链接或尖塔补给站链接。";
   }
  }
  workshopInput.TextChanged+=(_,_)=>ValidateInput();
  ValidateInput();
  var workshopActions=new WrapPanel{Margin=new Thickness(0,5,0,0)};
  var get=AtlasButton("获取并预览",()=>{
   if(!WorkshopModInstaller.TryParseWorkshopId(workshopInput.Text,out ulong id))
   {
    validation.Foreground=AtlasRed;
    validation.Text="链接或 ID 无效，未发起网络请求。";
    return;
   }
   if(selectedInstallRoot==null)
   {
    validation.Foreground=AtlasBrass;
    validation.Text="请先选择游戏目录，再获取 Workshop MOD。";
    return;
   }
   if(busy||transactionWriting||modInventoryDirty)
   {
    validation.Foreground=AtlasBrass;
    validation.Text=modInventoryDirty
     ?"MOD 文件已变化，请刷新本地清单后再下载。"
     :"其他文件操作正在进行，请完成后重试。";
    return;
   }
   AtlasNativeRunWorkflow(()=>DownloadWorkshopMod(presetWorkshopId:id));
  },"primary");
  get.Margin=new Thickness(0,0,9,5);workshopActions.Children.Add(get);
  var refresh=AtlasButton("刷新本地清单",()=>_=RefreshAtlasNativeDataAsync(),"quiet");
  refresh.Margin=new Thickness(0,0,9,5);workshopActions.Children.Add(refresh);
  workshop.Children.Add(workshopActions);
  workshop.Children.Add(AtlasText("完整流程：准备内容 → 校验 Manifest / Chunk、MOD ID 与版本 → 预览 → 确认写入；不自动安装。",11,AtlasMuted));
  var workshopPanel=AtlasPanel(workshop,19,"#20242A");
  root.Children.Add(workshopPanel);

  var github=new StackPanel{Margin=new Thickness(0,13,0,0)};
  github.Children.Add(AtlasText("GitHub Releases",19,AtlasInk,true));
  int githubLinked=atlasNativeRows.Count(row=>row.GitHubRepository.Length>0);
  github.Children.Add(AtlasText(modInventory==null
   ?"尚未读取本地来源。先定位游戏目录并刷新模组清单。"
   :$"已关联 GitHub 来源：{githubLinked} 项 · 有更新：{atlasNativeRows.Count(row=>row.GitHubRepository.Length>0&&row.GitHubStatus=="有更新")} 项（仅已缓存结果）。",
   12,AtlasMuted));
  github.Children.Add(AtlasText("仅检查公开仓库的正式 Release；更新安装仍由现有下载、SHA-256 和回滚流程处理。",12,AtlasMuted));
  var githubActions=new WrapPanel{Margin=new Thickness(0,10,0,0)};
  var updates=AtlasButton("查看 GitHub 更新",()=>{
   atlasNativeUpdateFilter="GitHub";AtlasNativeNavigate("更新中心");
  },"primary");
  updates.Margin=new Thickness(0,0,9,5);githubActions.Children.Add(updates);
  var bind=AtlasButton("管理来源关联",()=>AtlasNativeNavigate("模组库"),"quiet");
  githubActions.Children.Add(bind);
  github.Children.Add(githubActions);
  root.Children.Add(AtlasPanel(github,19));

  var local=new StackPanel{Margin=new Thickness(0,13,0,0)};
  local.Children.Add(AtlasText("本地 ZIP 导入",19,AtlasInk,true));
  local.Children.Add(AtlasText("导入已下载的 MOD ZIP。会检查路径、文件类型、大小、清单和目标冲突；不直接覆盖已安装 MOD。",12,AtlasMuted));
  var zip=AtlasButton("选择 ZIP 并预检",()=>AtlasNativeRunWorkflow(ChooseWorkshopZip),"primary");
  zip.Margin=new Thickness(0,10,0,0);
  local.Children.Add(zip);
  root.Children.Add(AtlasPanel(local,19));

  var patches=new StackPanel{Margin=new Thickness(0,13,0,0)};
  patches.Children.Add(AtlasText("在线修复补丁库",19,AtlasInk,true));
  patches.Children.Add(AtlasText("查看公开补丁的适用范围与验证依据。整合包不会自动安装，安装与回滚仍需明确确认。",12,AtlasMuted));
  patches.Children.Add(AtlasButton("浏览在线补丁库",()=>AtlasNativeRunWorkflow(()=>Navigate("在线补丁")),"quiet"));
  root.Children.Add(AtlasPanel(patches,19));
  return AtlasScroll(root);
 }

 UIElement BuildAtlasNativeSettingsPage()
 {
  var root=new StackPanel{Margin=new Thickness(5,5,5,20)};
  root.Children.Add(AtlasPageTitle("PREFERENCES","设置",
   "游戏目录、界面样式、孤立补丁保护与 GitHub 网络连接。设置仅调用已有持久化及安全流程。"));

  var game=new StackPanel();
  game.Children.Add(AtlasText("游戏目录",17,AtlasInk,true));
  var selectedPath=selectedInstallRoot??"尚未选择游戏目录";
  var pathLabel=AtlasText(selectedPath,13,AtlasMuted);
  pathLabel.TextWrapping=TextWrapping.Wrap;pathLabel.ToolTip=selectedPath;
  pathLabel.Margin=new Thickness(0,5,0,0);
  game.Children.Add(pathLabel);
  game.Children.Add(AtlasText("切换游戏目录将使旧 MOD 扫描与补丁健康结论失效；不会自动迁移或删除文件。",11,AtlasMuted));
  var gameActions=new WrapPanel{Margin=new Thickness(0,10,0,0)};
  gameActions.Children.Add(AtlasButton("选择游戏目录",ChooseAtlasNativeFolder,"primary"));
  gameActions.Children.Add(AtlasButton("重新定位游戏",()=>AtlasNativeRunWorkflow(LocateGame),"quiet"));
  var copy=AtlasButton("复制目录路径",()=>{
   if(selectedInstallRoot==null){toast.Text="尚未选择游戏目录。";return;}
   try{Clipboard.SetText(selectedInstallRoot);toast.Text="已复制当前游戏目录。";}
   catch(Exception ex){toast.Text="复制目录失败："+ex.Message;}
  },"quiet");
  copy.IsEnabled=selectedInstallRoot!=null;gameActions.Children.Add(copy);
  game.Children.Add(gameActions);
  root.Children.Add(AtlasPanel(game,17));

  var theme=new StackPanel{Margin=new Thickness(0,13,0,0)};
  theme.Children.Add(AtlasText("界面风格",17,AtlasInk,true));
  theme.Children.Add(AtlasText("启用环境记忆后会保存主题偏好；切换不会修改 MOD、来源绑定或补丁文件。",12,AtlasMuted));
  var themes=new WrapPanel{Margin=new Thickness(0,10,0,0)};
  themes.Children.Add(AtlasButton("ATLAS 原生 · 当前",()=>ChangeUiTheme(UiThemes.AtlasNative),"active"));
  themes.Children.Add(AtlasButton("新版工作台",()=>ChangeUiTheme(UiThemes.Workspace),"quiet"));
  themes.Children.Add(AtlasButton("经典 Beta",()=>ChangeUiTheme(UiThemes.Classic),"quiet"));
  theme.Children.Add(themes);
  root.Children.Add(AtlasPanel(theme,17));

  var patch=new StackPanel{Margin=new Thickness(0,13,0,0)};
  patch.Children.Add(AtlasText("孤立补丁自动停用",17,AtlasInk,true));
  patch.Children.Add(AtlasText("关联 MOD 被删除时，可按原有保护逻辑自动停用孤立补丁并显示提示；不会删除补丁文件。",12,AtlasMuted));
  bool autoStop=OrphanPatchAutoStop.Enabled(installer.DataRoot);
  var autoStatus=AtlasText("当前状态："+(autoStop?"已开启":"已关闭"),12,autoStop?AtlasGreen:AtlasMuted,true);
  autoStatus.Margin=new Thickness(0,7,0,7);patch.Children.Add(autoStatus);
  // Initialize the captured button before assigning its callback (C# definite assignment).
  Button autoToggle=new();
  autoToggle=AtlasButton(autoStop?"关闭自动停用":"开启自动停用",()=>{
   if(busy||transactionWriting)
   {
    toast.Text="文件操作期间不能修改补丁自动停用设置。";return;
   }
   try
   {
    bool next=!OrphanPatchAutoStop.Enabled(installer.DataRoot);
    OrphanPatchAutoStop.SetEnabled(installer.DataRoot,next);
    autoStatus.Text="当前状态："+(next?"已开启":"已关闭");
    autoStatus.Foreground=next?AtlasGreen:AtlasMuted;
    autoToggle.Content=next?"关闭自动停用":"开启自动停用";
    toast.Text=next?"孤立补丁自动停用已开启；不会删除补丁文件。":"孤立补丁自动停用已关闭。";
   }
   catch(Exception ex){toast.Text="设置保存失败："+ex.Message;}
  },"quiet");
  autoToggle.Margin=new Thickness(0,0,9,0);patch.Children.Add(autoToggle);
  root.Children.Add(AtlasPanel(patch,17));

  var network=new StackPanel{Margin=new Thickness(0,13,0,0)};
  network.Children.Add(AtlasText("GitHub 网络",17,AtlasInk,true));
  var config=GitHubNetwork.Load(installer.DataRoot);
  string networkMode=config.Mode=="local"
   ?"本机 HTTP 代理："+config.ProxyUrl:"跟随 Windows 系统代理";
  network.Children.Add(AtlasText("当前："+networkMode,12,AtlasMuted));
  network.Children.Add(AtlasText("网络设置保存后需重启管理器才能应用到所有连接；诊断只测试连通性，不代表下载速度。",11,AtlasMuted));
  var networkActions=new WrapPanel{Margin=new Thickness(0,10,0,0)};
  var proxy=AtlasButton("配置 GitHub 网络",()=>{
   AtlasNativeRunWorkflow(ConfigureGitHubNetwork);
   // This dialog performs the actual persistence; refresh the displayed mode.
   if(UiThemes.IsAtlasNative&&atlasNativePage=="设置")RenderAtlasNative();
  },"primary");
  proxy.Margin=new Thickness(0,0,9,5);networkActions.Children.Add(proxy);
  networkActions.Children.Add(AtlasButton("诊断 GitHub 连接",()=>AtlasNativeRunWorkflow(DiagnoseGitHubNetwork),"quiet"));
  network.Children.Add(networkActions);
  root.Children.Add(AtlasPanel(network,17));

  var diagnostics=new StackPanel{Margin=new Thickness(0,13,0,0)};
  diagnostics.Children.Add(AtlasText("维护与诊断",17,AtlasInk,true));
  diagnostics.Children.Add(AtlasText("这些操作复用现有 WPF 业务页及校验逻辑，并保留 ATLAS 工作台外框。",12,AtlasMuted));
  var tools=new WrapPanel{Margin=new Thickness(0,10,0,0)};
  tools.Children.Add(AtlasButton("联机检查",()=>AtlasNativeRunWorkflow(()=>Navigate("联机检查")),"quiet"));
  tools.Children.Add(AtlasButton("诊断与日志",()=>AtlasNativeRunWorkflow(()=>Navigate("诊断与日志")),"quiet"));
  tools.Children.Add(AtlasButton("管理器更新",()=>AtlasNativeRunWorkflow(()=>Navigate("检查更新")),"quiet"));
  tools.Children.Add(AtlasButton("补丁健康报告",()=>AtlasNativeRunWorkflow(()=>Navigate("补丁健康")),"quiet"));
  diagnostics.Children.Add(tools);
  root.Children.Add(AtlasPanel(diagnostics,17));
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
  if(atlasNativeSelectedFolder==null){
   atlasNativeInspector.Visibility=Visibility.Collapsed;
   atlasNativeBody.ColumnDefinitions[1].Width=new GridLength(0);
   atlasNativeInspector.Child=null;
   return;
  }
  if(modInventory==null)
  {
   var loading=new StackPanel{Margin=new Thickness(2)};
   var loadingHeader=new Grid{Margin=new Thickness(0,0,0,13)};
   loadingHeader.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
   loadingHeader.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
   loadingHeader.Children.Add(AtlasText("MOD INSPECTOR",11,AtlasBrass,true));
   var closeLoading=AtlasButton("×",CloseAtlasNativeInspector,"quiet");
   closeLoading.Padding=new Thickness(9,3,9,3);Grid.SetColumn(closeLoading,1);loadingHeader.Children.Add(closeLoading);
   loading.Children.Add(loadingHeader);
   loading.Children.Add(AtlasText("正在刷新本地清单",17,AtlasInk,true));
   loading.Children.Add(AtlasText("已保留当前选择。扫描完成后会自动显示最新的 MOD 与补丁依据。",12,AtlasMuted));
   atlasNativeInspector.Child=loading;
   atlasNativeInspector.Visibility=Visibility.Visible;
   atlasNativeBody.ColumnDefinitions[1].Width=new GridLength(Math.Clamp(ActualWidth<=1150?310:ActualWidth<=1400?330:350,300,350));
   return;
  }
  var mod=modInventory.Mods.FirstOrDefault(x=>x.Folder.Equals(atlasNativeSelectedFolder,StringComparison.OrdinalIgnoreCase));
  if(mod==null){CloseAtlasNativeInspector();return;}
  bool isPatch=ModPresentation.IsIndependentPatch(mod);
  var content=new StackPanel{Margin=new Thickness(2)};
  var top=new Grid{Margin=new Thickness(0,0,0,13)};
  top.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
  top.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
  top.Children.Add(AtlasText(isPatch?"PATCH INSPECTOR":"MOD INSPECTOR",11,AtlasBrass,true));
  var close=AtlasButton("×",CloseAtlasNativeInspector,"quiet");close.Padding=new Thickness(9,3,9,3);Grid.SetColumn(close,1);top.Children.Add(close);
  content.Children.Add(top);
  content.Children.Add(AtlasText(mod.Name,21,AtlasInk,true));
  content.Children.Add(AtlasText(mod.Id,11,AtlasMuted));
  var status=new WrapPanel{Margin=new Thickness(0,10,0,13)};
  status.Children.Add(AtlasBadge(mod.Enabled?"已启用":"已停用",mod.Enabled?"ok":"neutral"));
  if(isPatch)status.Children.Add(AtlasBadge("独立修复补丁","warn"));
  content.Children.Add(status);
  AddInspectorField(content,"版本",mod.Version);
  AddInspectorField(content,"作者",mod.Author);
  AddInspectorField(content,"说明",mod.Description.Length==0?"作者未提供简介。":mod.Description);
  AddInspectorField(content,"安装路径",Path.GetFileName(mod.Folder),mod.Folder);
  string dependencies=string.Join("、",mod.Dependencies.Select(x=>x.Id));
  AddInspectorField(content,"依赖",dependencies.Length>0?dependencies:"未声明必需前置");

  if(isPatch)
  {
   var state=knownState??report?.Patches.FirstOrDefault(x=>x.Patch.Id.Equals(mod.Id,StringComparison.OrdinalIgnoreCase));
   var lifecycle=lifecycleReport?.Patches.FirstOrDefault(x=>x.Patch.Id.Equals(mod.Id,StringComparison.OrdinalIgnoreCase));
   AddInspectorField(content,"文件扫描状态",state?.Status??"尚未扫描");
   AddInspectorField(content,"扫描依据",state?.Reason??"当前没有补丁扫描记录。");
   AddInspectorField(content,"补丁健康生命周期",LifecycleTag(mod.Id));
   AddInspectorField(content,"关联 MOD",state?.Patch.Requirements.Length>0?string.Join("、",state.Patch.Requirements):"补丁清单未声明关联 MOD");
   content.Children.Add(AtlasButton("查看完整健康依据",()=>AtlasNativeRunWorkflow(()=>ShowPatchEvidence(mod.Id)),"quiet"));
   var toggle=AtlasButton(mod.Enabled?"预览停用补丁":"预览启用补丁",
    ()=>AtlasNativeRunWorkflow(()=>PrepareModToggle(mod)),mod.Enabled?"quiet":"primary");
   toggle.IsEnabled=AtlasNativeToggleAllowed(mod,out _);
   toggle.Margin=new Thickness(0,7,0,0);content.Children.Add(toggle);
   if(HasValidLifecycleReport&&!lifecycleScanning&&lifecycle?.CanRetire==true)
   {
    var retire=AtlasButton("预览安全退役",()=>AtlasNativeRunWorkflow(()=>PrepareRetirement(lifecycle)),"primary");
    retire.Margin=new Thickness(0,7,0,0);content.Children.Add(retire);
   }
   var scan=AtlasButton("重新检查补丁健康",AtlasNativeRefreshLifecycle,"quiet");
   scan.Margin=new Thickness(0,7,0,0);content.Children.Add(scan);
  }
  else
  {
   AtlasNativeModRow? row=atlasNativeRows.FirstOrDefault(x=>x.Mod.Folder.Equals(mod.Folder,StringComparison.OrdinalIgnoreCase));
   if(row!=null)
   {
    AddInspectorField(content,"来源",row.Source);
    AddInspectorField(content,"Workshop ID",row.WorkshopId.Length>0?row.WorkshopId:"未绑定");
    AddInspectorField(content,"GitHub 仓库",row.GitHubRepository.Length>0?row.GitHubRepository:"未关联");
    AddInspectorField(content,"更新状态",row.UpdateStatus+(row.LatestVersion.Length>0?" · 最新 "+row.LatestVersion:""));
   }
   var actionHeader=AtlasText("常用操作",13,AtlasInk,true);actionHeader.Margin=new Thickness(0,4,0,8);content.Children.Add(actionHeader);
   var toggle=AtlasButton(mod.Enabled?"预览停用":"预览启用",
    ()=>AtlasNativeRunWorkflow(()=>PrepareModToggle(mod)),mod.Enabled?"quiet":"primary");
   toggle.IsEnabled=AtlasNativeToggleAllowed(mod,out _);toggle.Margin=new Thickness(0,0,6,6);
   var actions=new WrapPanel();actions.Children.Add(toggle);
   if(row!=null)
   {
    if(row.GitHubRepository.Length==0)
     actions.Children.Add(AtlasButton("自动查找 GitHub",()=>AtlasNativeRunWorkflow(()=>DiscoverGitHubMod(mod)),"quiet"));
    actions.Children.Add(AtlasButton(row.GitHubRepository.Length==0?"关联 GitHub":"修改 GitHub 关联",
     ()=>AtlasNativeRunWorkflow(()=>BindGitHubMod(mod)),"quiet"));
    if(row.GitHubRepository.Length>0)
    {
     actions.Children.Add(AtlasButton("检查 GitHub",()=>AtlasNativeRunWorkflow(()=>CheckGitHubMod(mod)),"quiet"));
     var update=AtlasButton("下载并更新",()=>AtlasNativeRunWorkflow(()=>UpdateGitHubMod(mod)),"primary");
     update.IsEnabled=row.GitHubStatus=="有更新"&&!busy&&!transactionWriting&&!modInventoryDirty;
     actions.Children.Add(update);
    }
    if(row.WorkshopId.Length>0)
     actions.Children.Add(AtlasButton("检查 Workshop",()=>AtlasNativeRunWorkflow(()=>CheckWorkshopMod(mod)),"quiet"));
    actions.Children.Add(AtlasButton(row.WorkshopId.Length>0?"下载 / 更新 Workshop":"从 Workshop 下载",
     ()=>AtlasNativeRunWorkflow(()=>DownloadWorkshopMod(mod)),"quiet"));
   }
   content.Children.Add(actions);
   var secondary=new WrapPanel{Margin=new Thickness(0,7,0,0)};
   secondary.Children.Add(AtlasButton("完整详情",()=>OpenAtlasLegacyMod(mod.Folder),"quiet"));
   secondary.Children.Add(AtlasButton("依赖关系图",()=>AtlasNativeRunWorkflow(()=>ShowDependencyGraph()),"quiet"));
   secondary.Children.Add(AtlasButton("打开 MOD 文件夹",()=>OpenUrl(mod.Folder),"quiet"));
   content.Children.Add(secondary);
  }
  var scroller=new ScrollViewer{Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
  atlasNativeInspector.Child=scroller;
  atlasNativeInspector.Visibility=Visibility.Visible;
  atlasNativeBody.ColumnDefinitions[1].Width=new GridLength(Math.Clamp(ActualWidth<=1150?310:ActualWidth<=1400?330:350,300,350));
 }

 void AtlasNativeRefreshLifecycle()
 {
  atlasNativeSubpageReturnPage="修复中心";
  _=RefreshLifecycleAsync(()=>{
   atlasNativePage="修复中心";
   RenderAtlasNative();
  });
 }

 void AddInspectorField(Panel panel,string label,string value,string? fullValue=null)
 {
  var group=new StackPanel{Margin=new Thickness(0,0,0,12)};
  group.Children.Add(AtlasText(label,11,AtlasBrass,true));
  var text=AtlasText(value,13,AtlasMuted);
  if(fullValue!=null){
   text.TextWrapping=TextWrapping.NoWrap;
   text.TextTrimming=TextTrimming.CharacterEllipsis;
   text.ToolTip=fullValue;
  }
  group.Children.Add(text);
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
  async Task WaitForStableInventory()
  {
   for(int attempt=0;attempt<180;attempt++)
   {
    await Idle();
    if(modInventory!=null&&!modInventoryDirty&&!busy&&!transactionWriting&&modWatchDebounce?.IsEnabled!=true)return;
    await Task.Delay(50);
   }
   throw new InvalidOperationException("本地 MOD 清单未能稳定："+ 
    "inventory="+(modInventory!=null)+"; dirty="+modInventoryDirty+
    "; busy="+busy+"; writing="+transactionWriting+"; status="+modStatus+
    "; changes="+modChangeVersion+"; watchers="+modWatchers.Count);
  }
  IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
  {
   for(int index=0;index<VisualTreeHelper.GetChildrenCount(parent);index++)
   {
    var child=VisualTreeHelper.GetChild(parent,index);
    if(child is T match)yield return match;
    foreach(var nested in Descendants<T>(child))yield return nested;
   }
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
  atlasNativePage="总览";atlasNativeViewMode="list";atlasNativeGridPage=0;atlasNativeQuery="";atlasNativeWorkshopInput="";
  atlasNativeStateFilter="全部";atlasNativeSourceFilter="全部";atlasNativeUnboundOnly=false;
  ChangeUiTheme(UiThemes.AtlasNative);
  ShowAtlasNative();await Idle();
  Check(atlasNativeRoot!=null&&ReferenceEquals(Content,atlasNativeRoot),
   "ATLAS 使用原生 WPF 视觉树，不依赖 WebView2");
  Check(Application.Current.Resources["AccentPrimary"] is SolidColorBrush atlasAccent&&
   atlasAccent.Color.R==198&&atlasAccent.Color.G==163&&atlasAccent.Color.B==107,
   "ATLAS 页面与嵌入式 WPF 工作流统一使用黄铜强调色");
  Check(atlasNativeNavButtons.Count==6&&atlasNativePageHost!=null,
   "原生工作台创建六个一级导航与独立页面容器");
  Check(atlasNativeFooter?.GetBindingExpression(TextBlock.TextProperty)!=null,
   "ATLAS 状态栏绑定到现有下载、更新和安装任务状态");
  await Shot("01-ATLAS-总览");
  Check(File.Exists(Path.Combine(output,"01-ATLAS-总览.png")),
   "ATLAS 总览截图已生成");

  AtlasNativeNavigate("模组库");
  Check(ReferenceEquals(Content,atlasNativeRoot)&&atlasNativePage=="模组库",
   "从总览同步切换到原生模组库");
  await Idle();
  Check(atlasNativePageHost?.Content is ScrollViewer,
   "模组库页面完成布局");
  Check(atlasNativeRows.Count==6&&GetFilteredAtlasNativeRows().Length==6,
   "模组库读取真实扫描模型并将独立补丁从普通 MOD 中分离");
  Check(atlasNativeResultsHost?.Content is DataGrid,
   "默认专业列表使用原生 DataGrid");
  Check(!ShouldRenderLegacyModAfterWatch(),
   "原生模组库不会被 MOD 目录监控的旧版 ShowMods 回调替换");
  var currentLibraryPage=atlasNativePageHost?.Content;
  var nativeModSearch=Descendants<TextBox>(atlasNativePageHost!).Single();
  nativeModSearch.Text="not-a-mod-123";
  Check(GetFilteredAtlasNativeRows().Length==0,
   "搜索关键词在输入后立即更新逻辑条件，不等待卡片重建");
  await Task.Delay(240);await Idle();
  Check(atlasNativeResultsSummary?.Text.StartsWith("显示 0 / ",StringComparison.Ordinal)==true&&
   GetFilteredAtlasNativeRows().Length==0&&
   ReferenceEquals(atlasNativePageHost?.Content,currentLibraryPage),
   "原生搜索在同一页面内更新无结果提示");
  var clearMod=Descendants<Button>(atlasNativePageHost!).First(x=>x.Content?.ToString()=="清除筛选");
  clearMod.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,clearMod));await Idle();
  Check(atlasNativeQuery==""&&nativeModSearch.Text==""&&GetFilteredAtlasNativeRows().Length==6&&
   ReferenceEquals(atlasNativePageHost?.Content,currentLibraryPage),
   "原生清除筛选恢复全部 MOD，不重建页面或丢失输入组件");
  nativeModSearch.Text="T";nativeModSearch.Text="Te";nativeModSearch.Text="TestSolo";
  Check(atlasNativeModSearchDebounce?.IsEnabled==true&&atlasNativeQuery=="TestSolo"&&
   atlasNativeResultsSummary?.Text.StartsWith("显示 6 / ",StringComparison.Ordinal)==true,
   "快速输入仅安排一次筛选，输入期间不逐字创建 WPF 卡片");
  await Task.Delay(240);await Idle();
  Check(atlasNativeResultsSummary?.Text.StartsWith("显示 1 / ",StringComparison.Ordinal)==true&&
   GetFilteredAtlasNativeRows().Single().Id=="TestSolo",
   "输入停止后自动应用最终搜索条件");
  nativeModSearch.Text="";atlasNativeModSearchDebounce?.Stop();
  RenderAtlasNativeModResults();
  atlasNativeQuery="TestSolo";RenderAtlasNativeModResults();
  Check(GetFilteredAtlasNativeRows().Length==1&&GetFilteredAtlasNativeRows()[0].Id=="TestSolo",
   "搜索可以按本地 MOD ID 精确筛选");
  atlasNativeQuery="";atlasNativeUnboundOnly=true;RenderAtlasNativeModResults();
  Check(GetFilteredAtlasNativeRows().Length==6,
   "未绑定 Workshop ID 筛选基于真实来源关联数据");
  atlasNativeUnboundOnly=false;
  var gridModeButton=Descendants<Button>(atlasNativePageHost!).Single(x=>x.Content?.ToString()=="库视图");
  gridModeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,gridModeButton));await Idle();
  Check(atlasNativeViewMode=="grid"&&atlasNativeResultsHost?.Content is WrapPanel,
   "库视图按钮通过真实 Click 切换原生卡片容器");
  Check(gridModeButton.Background.ToString()==AtlasBrush("#3B3021").ToString(),
   "库视图显示时，库视图按钮正确高亮");
  Check(Descendants<TextBlock>(atlasNativeResultsHost!).Where(t=>atlasNativeRows.Any(r=>r.Name==t.Text))
   .All(t=>t.HorizontalAlignment==HorizontalAlignment.Left),
   "MOD 卡片标题与下方信息左对齐，而非居中漂移");
  Check(AtlasNativeCardColumns(620,315)==1&&AtlasNativeCardColumns(650,315)==2&&
   AtlasNativeCardColumns(990,315)==3,
   "库视图卡片列数根据可用宽度变化，而非固定 390px");
  await Idle();
  if(atlasNativeResultsHost?.Content is WrapPanel modCards&&modCards.ActualWidth>0)
  {
   int columns=AtlasNativeCardColumns(modCards.ActualWidth,315);
   double expected=Math.Max(180,Math.Floor((modCards.ActualWidth-columns*13)/columns));
   Check(modCards.Children.OfType<Border>().All(card=>Math.Abs(card.Width-expected)<1),
    "MOD 卡片在实际可用宽度内自动排列");
  }
  await Shot("02-ATLAS-库视图");
  // A details sidebar changes the actual card viewport; resize cards rather
  // than leaving clipped fixed-width content behind.
  ShowAtlasNativeModInspector(atlasNativeRows[0]);await Idle();
  if(atlasNativeResultsHost?.Content is WrapPanel narrowedCards&&narrowedCards.ActualWidth>0)
  {
   int narrowCols=Math.Min(narrowedCards.Children.Count,AtlasNativeCardColumns(narrowedCards.ActualWidth,315));
   double expectedWidth=Math.Floor((narrowedCards.ActualWidth-narrowCols*13)/narrowCols);
   Check(narrowedCards.Children.OfType<Border>().All(card=>Math.Abs(card.Width-expectedWidth)<1),
    "展开 MOD 详情后卡片重新适配剩余宽度");
  }
  CloseAtlasNativeInspector();await Idle();
  // Background watcher scans may replace an in-flight result surface.
  // Wait for a settled inventory, then interact with the current native view.
  await WaitForStableInventory();
  RenderAtlasNative();await Idle();
  var listModeButton=Descendants<Button>(atlasNativePageHost!).Single(x=>x.Content?.ToString()=="列表");
  listModeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,listModeButton));await Idle();
  Check(atlasNativeViewMode=="list"&&atlasNativeResultsHost?.Content is DataGrid,
   "列表与库视图切换复用同一份模组数据；mode="+atlasNativeViewMode+
   "; host="+(atlasNativeResultsHost?.Content?.GetType().Name??"null")+
   "; page="+atlasNativePage+"; busy="+busy+"; dirty="+modInventoryDirty+
   "; inventory="+(modInventory!=null)+"; key="+(currentVisit?.Key??"null"));
  Check(listModeButton.Background.ToString()==AtlasBrush("#3B3021").ToString(),
   "返回列表视图后列表按钮正确高亮");
  ShowAtlasNativeModInspector(atlasNativeRows[0]);await Idle();
  Check(atlasNativeInspector?.Visibility==Visibility.Visible&&
   atlasNativeBody!.ColumnDefinitions[1].Width.Value>=300&&atlasNativeBody.ColumnDefinitions[1].Width.Value<=350,
   "模组详情检查器按需展开，不默认占用主列表空间");
  CloseAtlasNativeInspector();await Idle();
  Check(atlasNativeInspector?.Visibility==Visibility.Collapsed,
   "详情检查器可以关闭并恢复列表宽度");

  var solo=modInventory.Mods.First(x=>x.Id=="TestSolo");
  ShowModDetail(solo.Folder);await Idle();
  Check(ReferenceEquals(Content,atlasNativeRoot)&&atlasNativePageHost?.Content is Grid,
   "现有 MOD 完整详情可嵌入 ATLAS 外壳，不切换到旧版主界面");
  var activeDetailContent=atlasNativePageHost?.Content;
  RenderAtlasNative();await Idle();
  Check(atlasNativeEmbeddedWorkflow&&ReferenceEquals(activeDetailContent,atlasNativePageHost?.Content),
   "后台扫描刷新不会把嵌入式 MOD 详情替换为模组列表");
  AtlasNativeNavigate("模组库");
  Check(ReferenceEquals(Content,atlasNativeRoot)&&atlasNativePage=="模组库",
   "从完整 MOD 详情返回原生模组库时同步切换页面状态");
  await Idle();
  Check(ReferenceEquals(Content,atlasNativeRoot)&&atlasNativePageHost?.Content is ScrollViewer,
   "模组库返回后的异步布局稳定");

  // Simulate a large installed list in memory. The real fixture/files are
  // untouched; the production card builder and view controls are exercised.
  var actualModRows=atlasNativeRows;
  var actualSelection=atlasNativeSelectedFolders.ToArray();
  var template=actualModRows[0];
  atlasNativeRows=Enumerable.Range(1,121).Select(index=>{
   var model=template.Mod with{
    Id="AtlasLoad"+index.ToString("D3"),
    Name="Atlas Load "+index.ToString("D3"),
    Folder=Path.Combine(fixture,"synthetic-"+index.ToString("D3"))
   };
   return new AtlasNativeModRow(model,template.Source,template.WorkshopId,
    template.GitHubRepository,template.UpdateStatus,template.LatestVersion,
    template.GitHubStatus,template.GitHubLatestVersion,
    template.WorkshopStatus,template.WorkshopLatestVersion);
  }).ToList();
  atlasNativeQuery="";atlasNativeStateFilter="全部";atlasNativeSourceFilter="全部";
  atlasNativeUnboundOnly=false;atlasNativeGridPage=0;atlasNativeViewMode="grid";
  RenderAtlasNativeModResults();await Idle();
  Check(atlasNativeResultsHost?.Content is WrapPanel firstPage&&
   firstPage.Children.Count==AtlasNativeGridPageSize&&
   atlasNativePagingHost?.Content is StackPanel&&atlasNativePagingFooterHost?.Content is StackPanel,
   "121 项模拟 MOD 的库视图只创建单页 24 张原生卡片，顶部和底部均可翻页");
  var nextPageButton=((StackPanel)atlasNativePagingFooterHost!.Content).Children.OfType<Button>()
   .Single(button=>button.Content?.ToString()=="下一页");
  nextPageButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,nextPageButton));await Idle();
  Check(atlasNativeGridPage==1&&atlasNativeResultsHost?.Content is WrapPanel secondPage&&
   secondPage.Children.Count==24,
   "原生卡片翻页仅创建当前页控件");
  atlasNativeRows[25].IsSelected=true;UpdateAtlasNativeSelection();
  Check(atlasNativeSelectedFolders.Contains(atlasNativeRows[25].Folder),
   "不同卡片页的勾选记录仍使用全局 MOD 选择集");
  SelectAllAtlasVisibleMods();
  Check(atlasNativeSelectedFolders.Count==121&&atlasNativeResultsHost?.Content is WrapPanel selectedPage&&
   selectedPage.Children.Count==24,
   "全选当前筛选结果跨全部分页，不遗漏不可见卡片");
  ClearAtlasNativeSelection();
  atlasNativeGridPage=5;atlasNativeQuery="Atlas Load 12";
  RenderAtlasNativeModResults();await Idle();
  Check(atlasNativeGridPage==0&&GetFilteredAtlasNativeRows().Length==2&&
   atlasNativeResultsHost?.Content is WrapPanel narrowedPage&&narrowedPage.Children.Count==2,
   "筛选缩小结果时自动夹紧页码，不出现越界空页");
  atlasNativeQuery="";atlasNativeGridPage=0;atlasNativeViewMode="list";
  RenderAtlasNativeModResults();await Idle();
  Check(atlasNativeResultsHost?.Content is DataGrid largeGrid&&
   largeGrid.Items.Count==121&&VirtualizingPanel.GetIsVirtualizing(largeGrid),
   "大量 MOD 列表仍显示全部结果并保留 DataGrid 虚拟化");
  atlasNativeRows=actualModRows;
  atlasNativeSelectedFolders.Clear();
  foreach(var folder in actualSelection)atlasNativeSelectedFolders.Add(folder);
  atlasNativeGridPage=0;atlasNativeViewMode="list";
  RenderAtlasNativeModResults();await Idle();
  Check(GetFilteredAtlasNativeRows().Length==6&&atlasNativeResultsHost?.Content is DataGrid,
   "负载测试仅使用内存行数据，测试完成后恢复真实 MOD 列表");

  // currentVisit may still name the previously opened legacy MOD detail.
  // A background update completion must respect the active native section.
  RefreshAtlasNativeModSurface();await Idle();
  Check(!atlasNativeEmbeddedWorkflow&&atlasNativePage=="模组库"&&
   atlasNativePageHost?.Content is ScrollViewer,
   "后台 MOD 操作结束不会利用过期详情记录重新打开旧版页面");

  AtlasNativeNavigate("修复中心");await WaitForStableInventory();
  // FileSystemWatcher may settle after the page was constructed. Render the
  // settled inventory through the real production path before inspecting controls.
  RenderAtlasNative();await Idle();
  var localPatches=modInventory!.Mods.Where(ModPresentation.IsIndependentPatch).ToArray();
  Check(localPatches.Length==1&&atlasNativePageHost?.Content is ScrollViewer,
   "修复中心只展示独立补丁，不混入普通 MOD");
  Check(AtlasNativePatchNeedsReview("需重新检查")&&AtlasNativePatchNeedsReview("已确认不兼容")&&
   !AtlasNativePatchNeedsReview("已知构建匹配"),
   "补丁健康待复查统计覆盖失效、未检查和已确认不兼容状态");
  // The ScrollViewer may not have realized its WPF visual descendants yet.
  // Verify the actual logical controls as a fallback, without inventing test widgets.
  var repairFilter=Descendants<Button>(atlasNativePageHost!).FirstOrDefault(button=>
   button.Content?.ToString()=="需要复查");
  var repairRoot=(atlasNativePageHost?.Content as ScrollViewer)?.Content as StackPanel;
  repairFilter??=repairRoot?.Children.OfType<WrapPanel>().SelectMany(panel=>panel.Children.OfType<Button>())
   .FirstOrDefault(button=>button.Content?.ToString()=="需要复查");
  Check(repairFilter!=null,
   "修复中心筛选控件已创建；page="+atlasNativePage+
   "; host="+(atlasNativePageHost?.Content?.GetType().Name??"null")+
   "; inventory="+modInventory?.Mods.Count+"; root="+selectedInstallRoot+
   "; logicalChildren="+repairRoot?.Children.Count+
   "; logicalButtons="+string.Join(",",repairRoot?.Children.OfType<WrapPanel>()
      .SelectMany(panel=>panel.Children.OfType<Button>()).Select(button=>button.Content?.ToString())??[]));
  repairFilter!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,repairFilter));await Idle();
  Check(atlasNativePatchFilter=="需要复查"&&atlasNativePageHost?.Content is ScrollViewer,
   "修复中心切换过滤条件时保留原生页面容器");
  var repairSearch=Descendants<TextBox>(atlasNativePageHost!).Single();
  repairSearch.Text="not-a-patch-id-123";await Idle();
  Check(atlasNativePatchQuery=="not-a-patch-id-123"&&
   Descendants<TextBlock>(atlasNativePageHost!).Any(x=>x.Text.StartsWith("显示 0 / ",StringComparison.Ordinal)),
   "补丁搜索实时更新结果计数");
  var clearRepairFilter=Descendants<Button>(atlasNativePageHost!).First(button=>
   button.Content?.ToString()=="清除筛选");
  clearRepairFilter.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,clearRepairFilter));await Idle();
  Check(atlasNativePatchFilter=="全部"&&atlasNativePatchQuery==""&&repairSearch.Text==""&&
   Descendants<TextBlock>(atlasNativePageHost!).Any(x=>x.Text=="显示 1 / 1 项独立补丁"),
   "清除条件恢复修复中心结果，不重建整个页面");
  var patchState=report!.Patches.FirstOrDefault(x=>x.Patch.Id.Equals(localPatches[0].Id,StringComparison.OrdinalIgnoreCase));
  await Shot("03-ATLAS-修复中心");
  ShowAtlasNativePatchInspector(localPatches[0],patchState);await Idle();
  string patchInspectorDebug="folder="+(atlasNativeSelectedFolder??"null")+
   "; inventory="+(modInventory!=null)+"; inspector="+(atlasNativeInspector?.Visibility.ToString()??"null")+
   "; child="+(atlasNativeInspector?.Child?.GetType().Name??"null")+
   "; body="+(atlasNativeBody!=null);
  Check(atlasNativeInspector?.Visibility==Visibility.Visible,
   "补丁检查器展示扫描依据与生命周期状态（"+patchInspectorDebug+"）");
  await Shot("04-ATLAS-补丁检查器");
  ShowPatchEvidence(localPatches[0].Id);await Idle();
  Check(ReferenceEquals(Content,atlasNativeRoot)&&atlasNativePageHost?.Content is Grid,
   "完整补丁证据与生命周期处理页面保留在 ATLAS 外壳内");
  AtlasNativeNavigate("修复中心");await Idle();

  ShowLibrary();await Idle();
  Check(ReferenceEquals(Content,atlasNativeRoot)&&atlasNativePageHost?.Content is Grid,
   "补丁库页面保留 ATLAS 外壳与修复二级导航");
  await Shot("10-ATLAS-补丁库");
  ShowOnline();await Idle();
  Check(ReferenceEquals(Content,atlasNativeRoot)&&atlasNativePageHost?.Content is FrameworkElement,
   "在线补丁页面可在 ATLAS 外壳内浏览，不自动触发网络请求");
  await Shot("11-ATLAS-在线补丁");
  ShowLifecycle();await Idle();
  string lifecyclePageDebug="theme="+UiThemes.Active+"; window="+(Content?.GetType().Name??"null")+
   "; rootMatch="+ReferenceEquals(Content,atlasNativeRoot)+"; host="+(atlasNativePageHost?.Content?.GetType().FullName??"null")+
   "; page="+atlasNativePage+"; busy="+busy+"; report="+(lifecycleReport!=null);
  Check(ReferenceEquals(Content,atlasNativeRoot)&&atlasNativePageHost?.Content is FrameworkElement,
   "补丁健康页面保留 ATLAS 外壳与安全退役流程（"+lifecyclePageDebug+"）");
  await Shot("12-ATLAS-补丁健康");
  ShowInstaller();await Idle();
  Check(ReferenceEquals(Content,atlasNativeRoot)&&atlasNativePageHost?.Content is FrameworkElement,
   "安装与回滚页面保留原有预览、快照和确认流程");
  await Shot("13-ATLAS-安装与回滚");
  AtlasNativeNavigate("修复中心");await Idle();

  AtlasNativeNavigate("更新中心");await Idle();
  string updatePageDebug="page="+atlasNativePage+"; content="+(atlasNativePageHost?.Content?.GetType().FullName??"null");
  Check(atlasNativePage=="更新中心"&&atlasNativePageHost?.Content is FrameworkElement,
   "更新中心以只读方式呈现已有缓存，不触发自动联网检查（"+updatePageDebug+"）");
  var githubMod=modInventory!.Mods.First(x=>x.Id=="TestSolo");
  var workshopMod=modInventory.Mods.First(x=>x.Id=="TestBase");
  atlasNativeRows=[
   new AtlasNativeModRow(githubMod,"GitHub","","example/atlas-test","GitHub 有更新","1.1.0","有更新","1.1.0","未绑定",""),
   new AtlasNativeModRow(workshopMod,"Workshop","1234567890","","Workshop 已是最新","1.0.0","未关联","","已是最新","1.0.0")
  ];
  atlasNativeUpdateFilter="全部";atlasNativeUpdateQuery="";atlasNativeUpdatePage=0;
  atlasNativePageHost!.Content=BuildAtlasNativeUpdatesPage();await Idle();
  int UpdateCardCount()=>Descendants<Border>(atlasNativePageHost!).Count(border=>
   Equals(border.Tag,"AtlasUpdateCard"));
  Check(UpdateCardCount()==2&&Descendants<Button>(atlasNativePageHost!).Any(button=>button.Content?.ToString()=="GitHub")&&
   Descendants<Button>(atlasNativePageHost!).Any(button=>button.Content?.ToString()=="Workshop"),
   "更新中心按来源显示 GitHub 与 Workshop 卡片及筛选入口");
  var githubFilter=Descendants<Button>(atlasNativePageHost!).Single(button=>button.Content?.ToString()=="GitHub");
  githubFilter.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,githubFilter));await Idle();
  Check(atlasNativeUpdateFilter=="GitHub"&&UpdateCardCount()==1,
   "更新中心 GitHub 来源筛选只保留已绑定 GitHub 的 MOD");
  var updateFilter=Descendants<Button>(atlasNativePageHost!).Single(button=>button.Content?.ToString()=="有更新");
  updateFilter.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,updateFilter));await Idle();
  Check(atlasNativeUpdateFilter=="有更新"&&UpdateCardCount()==1,
   "更新中心有更新筛选基于来源检查状态");
  var updateSearch=Descendants<TextBox>(atlasNativePageHost!).Single();
  updateSearch.Text="TestBase";
  Check(atlasNativeUpdateQuery=="TestBase"&&atlasNativeUpdateSearchDebounce?.IsEnabled==true,
   "更新中心立即记住搜索内容，延迟重建卡片");
  await Task.Delay(240);await Idle();
  Check(UpdateCardCount()==0,"更新中心搜索与来源筛选可以组合使用");
  updateSearch.Text="";await Task.Delay(240);await Idle();
  var allFilter=Descendants<Button>(atlasNativePageHost!).Single(button=>button.Content?.ToString()=="全部");
  allFilter.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,allFilter));await Idle();
  Check(UpdateCardCount()==2,"清除更新筛选后恢复全部已关联 MOD");

  var updateFixtureRows=atlasNativeRows;
  var updateTemplate=updateFixtureRows[0];
  atlasNativeRows=Enumerable.Range(1,73).Select(index=>{
   var model=updateTemplate.Mod with{
    Id="UpdateLoad"+index.ToString("D3"),
    Name="Update Load "+index.ToString("D3"),
    Folder=Path.Combine(fixture,"update-synthetic-"+index.ToString("D3"))
   };
   return new AtlasNativeModRow(model,"GitHub","","example/atlas-stress",
    "GitHub 有更新","2.0.0","有更新","2.0.0","未绑定","");
  }).ToList();
  atlasNativeUpdateFilter="全部";atlasNativeUpdateQuery="";atlasNativeUpdatePage=0;
  atlasNativePageHost!.Content=BuildAtlasNativeUpdatesPage();await Idle();
  Check(UpdateCardCount()==24&&
   Descendants<Button>(atlasNativePageHost!).Count(x=>x.Content?.ToString()=="下一页")==2,
   "更新中心 73 条缓存记录只创建 24 张卡片，提供上下两处翻页");
  var lowerNext=Descendants<Button>(atlasNativePageHost!).Last(x=>x.Content?.ToString()=="下一页");
  lowerNext.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,lowerNext));await Idle();
  Check(atlasNativeUpdatePage==1&&UpdateCardCount()==24,
   "更新中心从底部翻页后只实例化第二页卡片");
  atlasNativeUpdatePage=3;
  var updateAll=Descendants<Button>(atlasNativePageHost!).Single(x=>x.Content?.ToString()=="有更新");
  updateAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,updateAll));await Idle();
  Check(atlasNativeUpdatePage==0&&UpdateCardCount()==24,
   "更新中心筛选变化会重置翻页索引");
  atlasNativeRows=updateFixtureRows;atlasNativeUpdateFilter="全部";
  atlasNativeUpdateQuery="";atlasNativeUpdatePage=0;
  atlasNativePageHost!.Content=BuildAtlasNativeUpdatesPage();await Idle();
  Check(UpdateCardCount()==2,"更新中心模拟负载结束后恢复原缓存数据");
  await Shot("06-ATLAS-更新中心");
  AtlasNativeNavigate("下载中心");await Idle();
  Check(atlasNativePage=="下载中心"&&atlasNativePageHost?.Content is ScrollViewer,
   "下载中心提供 Workshop、GitHub 与本地 ZIP 的真实流程入口");
  var workshopInput=Descendants<TextBox>(atlasNativePageHost!).Single(x=>
   System.Windows.Automation.AutomationProperties.GetName(x)=="ATLAS 创意工坊链接或 ID");
  workshopInput.Text="invalid-test-link";await Idle();
  Check(Descendants<TextBlock>(atlasNativePageHost!).Any(x=>x.Text.Contains("链接或 ID 无效")),
   "下载中心输入非法 Workshop 链接时显示本地校验提示");
  var getWorkshop=Descendants<Button>(atlasNativePageHost!).Single(x=>x.Content?.ToString()=="获取并预览");
  getWorkshop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,getWorkshop));await Idle();
  Check(Descendants<TextBlock>(atlasNativePageHost!).Any(x=>x.Text.Contains("未发起网络请求"))&&
   !busy&&!transactionWriting,
   "非法 Workshop 链接不会启动下载或进入文件事务");
  workshopInput.Text="3737335127";await Idle();
  Check(atlasNativeWorkshopInput=="3737335127"&&
   Descendants<TextBlock>(atlasNativePageHost!).Any(x=>x.Text.Contains("已识别 Workshop ID：3737335127")),
   "有效 Workshop ID 仅作本地格式识别，不自动联网");
  await Shot("07-ATLAS-下载中心");
  AtlasNativeNavigate("设置");await Idle();
  Check(atlasNativePage=="设置"&&atlasNativePageHost?.Content is ScrollViewer,
   "设置页面保留游戏目录、界面主题和诊断工具入口");
  Check(Descendants<Button>(atlasNativePageHost!).Any(x=>x.Content?.ToString()=="配置 GitHub 网络")&&
   Descendants<Button>(atlasNativePageHost!).Any(x=>x.Content?.ToString()=="诊断 GitHub 连接")&&
   Descendants<Button>(atlasNativePageHost!).Any(x=>x.Content?.ToString()=="复制目录路径"),
   "原生设置提供直接 GitHub 网络配置、诊断及目录复制入口");
  Check(Descendants<Button>(atlasNativePageHost!).Any(x=>
   x.Content?.ToString() is "关闭自动停用" or "开启自动停用")&&
   Descendants<TextBlock>(atlasNativePageHost!).Any(x=>x.Text.Contains("当前状态：")),
   "原生设置显示已有孤立补丁自动停用配置，未修改真实补丁文件");
  await Shot("08-ATLAS-设置");
  var settingsScroll=(ScrollViewer)atlasNativePageHost!.Content;
  settingsScroll.ScrollToVerticalOffset(155);await Idle();
  double settingsOffset=settingsScroll.VerticalOffset;
  Check(settingsOffset>0,"原生设置在窄工作区支持正常纵向滚动");
  RenderAtlasNative();await Idle();
  var preservedSettingsScroll=(ScrollViewer)atlasNativePageHost!.Content;
  Check(Math.Abs(preservedSettingsScroll.VerticalOffset-settingsOffset)<3,
   "相同页面刷新后保留设置滚动位置");
  preservedSettingsScroll.ScrollToTop();await Idle();
  AtlasNativeNavigate("下载中心");await Idle();
  Check(Descendants<TextBox>(atlasNativePageHost!).Any(x=>x.Text=="3737335127"),
   "离开并重新进入下载中心时保留未提交的 Workshop ID");
  AtlasNativeNavigate("设置");await Idle();

  double oldWidth=Width,oldHeight=Height;
  Width=1020;Height=720;await Idle();RenderAtlasNative();await Idle();
  var navFits=atlasNativeNavButtons.Values.All(button=>
  {
   var bounds=button.TransformToAncestor(atlasNativeRoot!).TransformBounds(new Rect(0,0,button.ActualWidth,button.ActualHeight));
   return bounds.Left>=0&&bounds.Right<=atlasNativeRoot!.ActualWidth+1;
  });
  Check(navFits&&atlasNativeRoot!.ActualWidth<=Width+1,
   "1020×720 最小窗口下顶部导航保持在窗口范围内");
  await Shot("09-ATLAS-窄窗口");
  Width=1540;Height=890;await Idle();
  AtlasNativeNavigate("模组库");await Idle();
  var wideGridButton=Descendants<Button>(atlasNativePageHost!).Single(x=>x.Content?.ToString()=="库视图");
  wideGridButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,wideGridButton));await Idle();
  var wideCards=atlasNativeResultsHost?.Content as WrapPanel;
  // Hosted CI desktops may clamp a 1540px window to their virtual screen.
  // Assert real measured layout separately from the 1540px column algorithm.
  Check(AtlasNativeCardColumns(1450,315)>=4,
   "宽桌面视口计算至少四列 MOD 卡片");
  double wideAvailable=wideCards?.ActualWidth??0;
  int wideCount=wideCards?.Children.OfType<Border>().Count()??0;
  int wideColumns=Math.Min(wideCount,AtlasNativeCardColumns(wideAvailable,315));
  double cardWidth=wideColumns>0?Math.Floor((wideAvailable-wideColumns*13)/wideColumns):0;
  Check(wideCards!=null&&wideAvailable>500&&wideCount==6&&
   wideCards.Children.OfType<Border>().All(card=>Math.Abs(card.Width-cardWidth)<1),
   "请求 1540px 时卡片跟随实际视口宽度适配（虚拟屏幕可能限制窗口）；actual="+
   wideAvailable+"; root="+atlasNativeRoot?.ActualWidth);
  await Shot("14-ATLAS-宽窗口模组库");
  Width=oldWidth;Height=oldHeight;await Idle();

  ChangeUiTheme(UiThemes.Workspace);
  Check(ReferenceEquals(Content,shell)&&UiThemes.IsWorkspace,
   "ATLAS 可无重启返回原有 WPF 工作台");
  Check(Application.Current.Resources["AccentPrimary"] is SolidColorBrush workspaceAccent&&
   workspaceAccent.Color.R==131&&workspaceAccent.Color.G==185&&workspaceAccent.Color.B==248,
   "切回新版工作台后恢复原有蓝色强调色");
  ChangeUiTheme(UiThemes.AtlasNative);
  Check(ReferenceEquals(Content,atlasNativeRoot)&&UiThemes.IsAtlasNative,
   "可从原工作台重新切回 ATLAS 原生界面");
  Check(Application.Current.Resources["AccentPrimary"] is SolidColorBrush restoredAccent&&
   restoredAccent.Color.R==198&&restoredAccent.Color.G==163&&restoredAccent.Color.B==107,
   "切回 ATLAS 后重新应用黄铜强调色");

  // Exercise the actual common scan entry, not a mocked navigation callback:
  // the old implementation unexpectedly called ShowOverview() here.
  AtlasNativeNavigate("修复中心");await WaitForStableInventory();
  await ScanFolder(fixture);await Idle();
  Check(atlasNativePage=="修复中心"&&!atlasNativeEmbeddedWorkflow&&
   atlasNativePageHost?.Content is ScrollViewer&&ReferenceEquals(Content,atlasNativeRoot),
   "通用游戏扫描完成后仍停留在当前原生修复中心，不跳到旧版总览");
  await RefreshModsAsync();await WaitForStableInventory();
  RenderAtlasNative();await Idle();
  Check(modInventory?.Mods.Count(ModPresentation.IsIndependentPatch)==1&&
   atlasNativePageHost?.Content is ScrollViewer,
   "通用扫描后重新读取独立补丁，原生修复页面可继续使用");

  // A watcher notification invalidates patch conclusions. Scanning the MOD
  // inventory must not silently recreate those conclusions or replace the
  // user's active native page. Selection of an unchanged MOD is preserved.
  string keptFolder=atlasNativeRows.First().Folder;
  atlasNativeSelectedFolders.Add(keptFolder);
  report=null;states=[];modInventoryDirty=true;
  await RefreshAtlasNativeInventoryFromWatchAsync();await WaitForStableInventory();
  Check(!modInventoryDirty&&modInventory?.Mods.Count==7&&report==null&&states.Count==0,
   "MOD 自动监控只刷新清单，不触发全局文件扫描或恢复已失效补丁报告");
  Check(atlasNativePage=="修复中心"&&atlasNativePageHost?.Content is ScrollViewer&&
   atlasNativeSelectedFolders.Contains(keptFolder)&&atlasNativeRows.Any(row=>
    row.Folder==keptFolder&&row.IsSelected),
   "自动刷新保留原生工作区及仍存在的 MOD 选择");
  atlasNativeSelectedFolders.Clear();
  CloseModWatch();
 }

}
