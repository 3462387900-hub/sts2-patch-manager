using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SpirePatch;
public partial class MainWindow : Window
{
 readonly Brush ink=B("#F0F5FC"),muted=B("#CBD8E9"),gold=B("#83B9F8"),green=B("#70D8AD");
 readonly Grid shell=new(); readonly StackPanel navigation=new(); readonly ContentControl content=new();
 readonly TextBlock modeLabel=new(),folderLabel=new(),toast=new();
 readonly Dictionary<string,Button> navButtons=[];
 List<PatchState> states=Catalog.Demo(); ScanReport? report; bool demo=true,busy,installed;
 IReadOnlyList<OnlinePatch> scannedCatalog=Catalog.Bundled;
 string page="总览",filter="全部",query=""; string? lastDiagnosis;
 public MainWindow(bool onlineStartup=true,ManagerUpdates? updates=null)
 {
  rememberEnvironment=onlineStartup;if(updates!=null)managerUpdates=updates;
  Title="尖塔修补站 · "+ManagerUpdates.CurrentVersion;Width=1280;Height=850;MinWidth=1020;MinHeight=720;
  Closing+=(_,e)=>{if(transactionWriting){e.Cancel=true;MessageBox.Show(this,"文件操作尚未结束，请等待校验或恢复完成后关闭。","正在处理文件");}else {preparationCancellation?.Cancel();managerUpdateCancellation?.Cancel();}};
  Background=Brushes.Transparent;Foreground=ink;FontFamily=new("Microsoft YaHei UI, Segoe UI");FontSize=14;
  shell.Background=SpireGlassTheme.Background(false);UseLayoutRounding=true;TextOptions.SetTextFormattingMode(this,TextFormattingMode.Display);TextOptions.SetTextRenderingMode(this,TextRenderingMode.Grayscale);
  WindowStartupLocation=WindowStartupLocation.CenterScreen;
  SourceInitialized+=(_,_)=>shell.Background=SpireGlassTheme.Background(SpireGlassBackdrop.Enable(this));
  shell.ColumnDefinitions.Add(new(){Width=new(205)});shell.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});
  Content=shell;SpireGlassTheme.AddAmbientGlow(shell);BuildSidebar();
  var main=new Grid{Margin=new(25,18,25,15)};Grid.SetColumn(main,1);shell.Children.Add(main);
  main.RowDefinitions.Add(new(){Height=GridLength.Auto});main.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});main.RowDefinitions.Add(new(){Height=GridLength.Auto});
  var top=new DockPanel{LastChildFill=false,Margin=new(0,0,0,15)};
  backButton=Btn("← 上一步",GoBack,"Ghost");backButton.Margin=new(0,0,10,0);DockPanel.SetDock(backButton,Dock.Left);top.Children.Add(backButton);
  var mode=Badge("v"+ManagerUpdates.CurrentVersion,muted);DockPanel.SetDock(mode,Dock.Left);top.Children.Add(mode);
  modeLabel.Foreground=muted;modeLabel.FontSize=12;modeLabel.VerticalAlignment=VerticalAlignment.Center;modeLabel.Margin=new(12,0,0,0);top.Children.Add(modeLabel);
  launchButton=Btn("▶ 启动游戏",LaunchGame,"Primary");DockPanel.SetDock(launchButton,Dock.Right);top.Children.Add(launchButton);main.Children.Add(top);
  pageScroll=new ScrollViewer{Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetRow(pageScroll,1);main.Children.Add(pageScroll);
  toast.Foreground=muted;toast.FontSize=12;toast.Margin=new(0,14,0,0);Grid.SetRow(toast,2);main.Children.Add(toast);
  var controlsTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(400)};controlsTimer.Tick+=(_,_)=>UpdateGlobalControls();controlsTimer.Start();Closed+=(_,_)=>{controlsTimer.Stop();CloseModWatch();githubModUpdates?.Dispose();githubDiscovery?.Dispose();};
  if(onlineStartup){ShowOverview();Loaded+=async(_,_)=>{_=CheckManagerUpdatesAsync(false);await LocateGameAsync(true);if(page=="在线补丁")RefreshOnline();if(selectedInstallRoot!=null&&modInventory==null)RefreshMods();if(page=="总览")ShowOverview();};}else ShowOverview();
 }
 static SolidColorBrush B(string hex)=>(SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
 TextBlock T(string text,double size=14,Brush? color=null,bool bold=false)=>new(){Text=text,FontSize=size,Foreground=color??ink,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,LineHeight=size*1.55};
 StackPanel V(params UIElement[] children){var p=new StackPanel();foreach(var c in children)p.Children.Add(c);return p;}
 StackPanel H(params UIElement[] children){var p=new StackPanel{Orientation=Orientation.Horizontal};foreach(var c in children){if(c is FrameworkElement f)f.Margin=new(0,0,10,0);p.Children.Add(c);}return p;}
 Button Btn(string text,Action action,string? style=null){var b=new Button{Content=text};if(style!=null)b.Style=(Style)FindResource(style);if(text.StartsWith("←"))b.HorizontalAlignment=HorizontalAlignment.Left;b.Click+=(_,_)=>action();return b;}
 Border Card(UIElement child,string color="#D5182942",int padding=20)=>new(){Background=SpireGlassTheme.GlassPanel(color=="#D5182942"?null:color),BorderBrush=SpireGlassTheme.PanelStroke(),BorderThickness=new(1),CornerRadius=new(12),Padding=new(padding),Child=child};
 Border Badge(string text,Brush? color=null)=>new(){HorizontalAlignment=HorizontalAlignment.Left,Background=B("#233F60"),CornerRadius=new(6),Padding=new(9,4,9,4),Child=T(text,11,color??muted)};
 Border Rule()=>new(){Background=B("#344862"),Height=1,Margin=new(0,20,0,20)};
 void Space(Panel p,int height=18)=>p.Children.Add(new Border{Height=height});
 Grid Columns(UIElement left,UIElement right,double rightWidth=300)
 {var g=new Grid();g.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});g.ColumnDefinitions.Add(new(){Width=new(24)});g.ColumnDefinitions.Add(new(){Width=new(rightWidth)});g.Children.Add(left);Grid.SetColumn(right,2);g.Children.Add(right);return g;}
 void BuildSidebar()
 {
  var border=new Border{Background=B("#B0162A45"),BorderBrush=B("#587397"),BorderThickness=new(0,0,1,0),Padding=new(15,22,15,15)};shell.Children.Add(border);
  var layout=new DockPanel{LastChildFill=true};border.Child=layout;
  var brand=V(T("尖塔修补站",20,ink,true),T("MOD 与补丁管理",12,muted));brand.Margin=new(4,0,0,30);DockPanel.SetDock(brand,Dock.Top);layout.Children.Add(brand);
  folderLabel.TextWrapping=TextWrapping.Wrap;folderLabel.Foreground=muted;folderLabel.FontSize=11;folderLabel.LineHeight=18;folderLabel.MaxHeight=56;
  var bottom=V(T("当前游戏",11,muted));Space(bottom,8);bottom.Children.Add(folderLabel);Space(bottom,10);bottom.Children.Add(Btn("选择游戏目录  ↗",ChooseFolder,"Ghost"));bottom.Children.Add(Btn("自动定位目录",LocateGame,"Ghost"));managerUpdateButton=Btn("检查更新",()=>{if(busy)return;ShowManagerUpdates();if(!managerUpdateChecked)_=CheckManagerUpdatesAsync(true);},"Ghost");bottom.Children.Add(managerUpdateButton);bottom.Children.Add(Rule());bottom.Children.Add(T("MOD WORKSPACE  "+ManagerUpdates.CurrentVersion,10,muted));DockPanel.SetDock(bottom,Dock.Bottom);layout.Children.Add(bottom);
  navigation.Children.Add(T("工作区",11,muted));Space(navigation,12);
  foreach(var (name,icon) in new[]{("总览","⌂"),("模组管理","▦"),("修复补丁","⚒"),("更新中心","⇩"),("联机检查","⇄"),("诊断与日志","☷")}){
   var button=Btn(icon+"    "+name,()=>Navigate(name),"Ghost");button.HorizontalContentAlignment=HorizontalAlignment.Left;button.Margin=new(0,0,0,5);button.Padding=new(14,11,8,11);navButtons[name]=button;navigation.Children.Add(button);
  }
  Space(navigation,18);navigation.Children.Add(T("工具",11,muted));Space(navigation,12);
  var toolsButton=Btn("⌁    工具与设置",()=>Navigate("工具与设置"),"Ghost");
  toolsButton.HorizontalContentAlignment=HorizontalAlignment.Left;toolsButton.Padding=new(14,11,8,11);navButtons["工具与设置"]=toolsButton;navigation.Children.Add(toolsButton);
  layout.Children.Add(new ScrollViewer{Content=navigation,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
 }
 static bool IsPatchCenterPage(string name)=>name is "补丁库" or "在线补丁" or "补丁健康" or "安装与回滚";
 UIElement PatchCenterTabs(string active)
 {
  var tabs=new WrapPanel{Margin=new(0,0,0,16)};
  foreach(var name in new[]{"补丁库","在线补丁","补丁健康","安装与回滚"}){
   var tab=Btn(name,()=>Navigate(name),name==active?"Primary":"Ghost");
   tab.Margin=new(0,8,8,0);tab.IsEnabled=!busy&&!transactionWriting;tabs.Children.Add(tab);
  }
  return tabs;
 }
 void ShowTools()
 {
  var root=V(Header("TOOLS","工具与设置","常用的故障诊断、游戏目录和管理器更新入口。"));Space(root,20);
  root.Children.Add(Card(V(T("诊断游戏日志",17,ink,true),T("查看已收录的错误特征，不会自动修改游戏文件。",13,muted),Btn("打开诊断",ShowDiagnosis,"Primary"))));Space(root,12);
  root.Children.Add(Card(V(T("游戏目录",17,ink,true),T("选择当前游戏安装位置；切换后旧扫描与操作预览会失效。",13,muted),Btn("定位游戏目录",ShowLocations,"Ghost"))));Space(root,12);
  root.Children.Add(Card(V(T("管理器更新",17,ink,true),T("检查更新和查看历史记录，不会自动安装版本。",13,muted),Btn("查看管理器更新",ShowManagerUpdates,"Ghost"))));
  Space(root,12);
  root.Children.Add(Card(V(T("Spire Glass · 材质诊断",17,ink,true),
   T(SpireGlassBackdrop.Status,12,muted),
   T("Windows 11 22H2+ 可尝试真正的桌面 Acrylic；Windows 10、关闭系统透明效果或远程桌面环境会显示应用内渐变玻璃回退。",12,muted))));
  Space(root,12);
  var net=GitHubNetwork.Load(installer.DataRoot);
  root.Children.Add(Card(V(T("GitHub 网络",17,ink,true),
   T("当前："+(net.Mode=="local"?"本机 HTTP 代理 "+net.ProxyUrl:"跟随系统网络")+"；仓库连接失败或更新下载缓慢时可先诊断。",13,muted),
   H(Btn("设置代理",ConfigureGitHubNetwork,"Ghost"),Btn("诊断 GitHub 连接",DiagnoseGitHubNetwork,"Ghost")))));
  SetPage("工具与设置",root,restore:ShowTools);
 }
 void SetPage(string active,UIElement element,string? key=null,Action? restore=null)
 {
  TrackPage(key??active,restore??(()=>Navigate(active)));page=active;
  // The virtualized MOD list owns scrolling; other pages keep the normal page scrollbar.
  pageScroll!.VerticalScrollBarVisibility=active=="模组管理"&&(key??active)=="模组管理"
   ?ScrollBarVisibility.Disabled:ScrollBarVisibility.Auto;
  if(IsPatchCenterPage(active)){
   var group=V(PatchCenterTabs(active),element);
   content.Content=group;
  }else content.Content=element;
  string section=IsPatchCenterPage(active)?"修复补丁":active is "诊断"?"诊断与日志":active is "游戏目录" or "检查更新"?"工具与设置":active;
  foreach(var kv in navButtons){kv.Value.Background=kv.Key==section?B("#6983B9F8"):Brushes.Transparent;kv.Value.Foreground=kv.Key==section?ink:muted;}
  modeLabel.Text=active=="检查更新"?"管理器更新 · 自愿下载":active=="模组管理"?"本地模组 · 保留文件 · 可恢复启停":active=="安装与回滚"?"真实操作 · 检查后确认 · 保留备份":active=="在线补丁"?"公开仓库 · 下载校验 · 整合包安装":demo?"演示环境 · 安装流程不会修改游戏":"本地扫描 · 文件状态";
  folderLabel.Text=selectedInstallRoot??(demo?"联机小队 · 示例配置\n尚未选择游戏目录":"尚未选择游戏目录");folderLabel.ToolTip=selectedInstallRoot;
 }
 void Navigate(string name){if(busy)return;switch(name){case "检查更新":ShowManagerUpdates();break;case "模组管理":ShowMods();if(modInventory==null||modInventoryDirty)RefreshMods();break;case "修复中心":case "修复补丁":ShowLibrary();break;case "更新中心":ShowModUpdatesCenter();break;case "诊断与日志":ShowDiagnosis();break;case "工具与设置":ShowTools();break;case "补丁健康":ShowLifecycle();if(lifecycleReport==null)RefreshLifecycle();break;case "游戏目录":ShowLocations();break;case "在线补丁":ShowOnline();if(onlineCatalog==null&&!onlineBusy)RefreshOnline();break;case "安装与回滚":ShowInstaller();break;case "总览":ShowOverview();break;case "补丁库":ShowLibrary();break;case "诊断":ShowDiagnosis();break;case "联机检查":ShowCoop();break;}}
 UIElement Header(string eyebrow,string title,string subtitle,UIElement? action=null)
 {var txt=V(T(title,24,ink,true),T(subtitle,13,muted));var g=new Grid();g.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});g.ColumnDefinitions.Add(new(){Width=GridLength.Auto});g.Children.Add(txt);if(action!=null){Grid.SetColumn(action,1);g.Children.Add(action);if(action is FrameworkElement f)f.VerticalAlignment=VerticalAlignment.Center;}return g;}
 void ShowOverview()
 {
  if(!demo&&report==null)
  {
   var home=V(Header("YOUR GAME","环境总览","MOD、更新与修复状态，从这里开始。"));Space(home,15);
   var intro=V(T(selectedInstallRoot==null?"尚未选择游戏目录":"已定位游戏目录",20,ink,true),
    T(selectedInstallRoot??"选择游戏目录以开始本地检查。",12,muted));
   Space(intro,15);
   intro.Children.Add(H(Btn("扫描游戏修复",()=>{
    if(selectedInstallRoot!=null)_=ScanFolder(selectedInstallRoot);else ChooseFolder();
   },"Primary"),Btn("MOD 管理",()=>{ShowMods();RefreshMods();},"Ghost")));
   home.Children.Add(Card(intro,"#CD203B5C",21));Space(home,15);
   if(modInventory!=null&&selectedInstallRoot!=null)
   {
    var updates=GitHubUpdates();
    int normal=modInventory.Mods.Count(m=>!ModPresentation.IsIndependentPatch(m));
    int newer=modInventory.Mods.Count(m=>!ModPresentation.IsIndependentPatch(m)&&updates.Cached(m.Id)?.Status=="有更新");
    var chips=new WrapPanel{Margin=new Thickness(0,0,0,14)};
    foreach(var (count,label,open) in new (int,string,Action)[]
    {(normal,"普通 MOD",ShowMods),(newer,"可更新 MOD",ShowModUpdatesCenter),
      (modInventory.Issues.Count,"清单待核对",ShowMods)})
    {
     var card=V(T(count.ToString(),25,ink,true),T(label,12,muted));
     var b=Btn("查看 "+label+" →",open,"Ghost");
     card.Children.Add(b);
     var item=Card(card,"#D9182942",13);item.Width=188;item.Margin=new Thickness(0,10,10,0);chips.Children.Add(item);
    }
    home.Children.Add(chips);
   }
   home.Children.Add(Card(V(T("下一步",16,ink,true),
    T("先检查 MOD 更新或修复补丁；扫描结果不代表游戏没有未知问题。",12,muted),
    H(Btn("查看更新中心",ShowModUpdatesCenter,"Ghost"),Btn("浏览修复补丁",ShowLibrary,"Ghost")))));
   SetPage("总览",home);return;
  }

  int pending=states.Count(s=>s.Status is "待修复" or "需联动"),done=states.Count(s=>s.Status=="已安装");
  var root=V(Header("YOUR NEXT RUN","下一局，安心出发。",demo?"修复已经定位，剩下的交给修补站。":"已读取本地文件。先核对推荐，再决定如何处理。",Btn("↻  检查修复",()=>Rescan())));Space(root,26);
  int uncertain=states.Count(s=>s.Status=="需核对");
  var hero=V(Badge(pending>0?$"{pending} 项修复待处理":uncertain>0?$"{uncertain} 项需要核对":done>0?"已安装修复的文件条件匹配":"没有匹配的修复需求",pending>0||uncertain>0?gold:green));Space(hero,14);
  hero.Children.Add(T(pending>0?(demo?"解决卡死，继续攀塔。":"发现适用的修复项。"):uncertain>0?"先核对这些文件。":done>0?"已收录修复的文件检查完成。":"当前组合没有可推荐的补丁。",25,ink,true));Space(hero,8);
  hero.Children.Add(T(pending>0?"查看适用原因和改动范围。关联补丁会一起列出。":"文件扫描不能判断游戏是否无故障。可在补丁库查看不适用或待核对的具体原因。",13,muted));Space(hero,22);
  hero.Children.Add(H(Btn(pending>0?"查看修复方案  →":"查看全部补丁  →",()=>{if(pending>0)ShowReview();else ShowLibrary();},"Primary"),Btn("浏览补丁库",ShowLibrary,"Ghost")));
  var stats=V(T("这套环境",12,muted));Space(stats,13);
  stats.Children.Add(Stat(demo?"51":(report?.ModCount??0).ToString(),demo?"示例模组":"检测到的模组"));Space(stats,17);stats.Children.Add(Stat(done.ToString(),demo?"示例已安装修复":"文件一致的修复"));Space(stats,17);stats.Children.Add(T(demo?"数据模式：演示":"扫描于 "+report?.Time.ToString("HH:mm:ss"),11,muted));
  root.Children.Add(Columns(Card(hero,"#CD203B5C",26),Card(stats),225));Space(root,18);
  // Keep actionable MOD updates and stale patch evidence visible on the start page.
  if(!demo&&modInventory!=null&&selectedInstallRoot!=null)
  {
   var updates=GitHubUpdates();
   int available=modInventory.Mods.Count(m=>!ModPresentation.IsIndependentPatch(m)&&
    updates.Cached(m.Id)?.Status=="有更新");
   var tasks=new WrapPanel{Margin=new Thickness(0,0,0,13)};
   if(available>0)
   {
    var b=Btn("⇩ "+available+" 个 MOD 有更新 · 查看",ShowModUpdatesCenter,"Ghost");
    b.Margin=new Thickness(0,0,10,8);tasks.Children.Add(b);
   }
   if(lifecycleStale)
   {
    var b=Btn("◇ 修复健康需要复核 · 查看",ShowLifecycle,"Ghost");
    b.Margin=new Thickness(0,0,10,8);tasks.Children.Add(b);
   }
   if(tasks.Children.Count>0)root.Children.Add(Card(tasks,"#D21C3048",12));
  }

  if(!demo){var counts=new WrapPanel();foreach(var label in new[]{"待修复","需联动","已安装","不适用","需核对"}){var button=Btn(label+"  "+states.Count(s=>s.Status==label),()=>{filter=label;ShowLibrary();},"Ghost");button.Margin=new(0,0,8,0);counts.Children.Add(button);}root.Children.Add(counts);Space(root,6);root.Children.Add(T("依据补丁说明核对实际文件 · 适配匹配不等于已确认发生故障",11,muted));Space(root,18);}
  if(report?.Issues.Count>0&&!demo){root.Children.Add(Card(V(T("有 "+report.Issues.Count+" 项扫描问题需要核对",15,gold,true),T(report.Issues[0],12,muted),Btn("查看扫描报告",ExportReport,"Ghost")),"#DD27364A",16));Space(root,20);}
  var section=new DockPanel();var more=Btn($"全部 {states.Count} 项  ↗",ShowLibrary,"Ghost");DockPanel.SetDock(more,Dock.Right);section.Children.Add(more);section.Children.Add(V(T(pending>0?"为你推荐":"最近修复",19,ink,true),T(demo?"根据示例模组组合匹配":"根据已收录文件版本匹配；未知依赖需进一步核对",12,muted)));root.Children.Add(section);Space(root,12);
  foreach(var s in states.Where(s=>s.Status is "待修复" or "需联动").Concat(states.Where(s=>s.Status=="需核对")).Concat(states.Where(s=>s.Status is "已安装" or "不适用")).Take(2)){root.Children.Add(PatchRow(s));Space(root,10);}
  SetPage("总览",root);toast.Text=demo?"演示数据仅供体验。真实安装请使用安装与回滚页。":"扫描完成。安装与回滚页会再次核对文件，确认后才执行操作。";
 }
 UIElement Stat(string value,string label)=>H(T(value,30,ink,true),new TextBlock{Text=label,Foreground=muted,VerticalAlignment=VerticalAlignment.Center,FontSize=12});
 UIElement QuietLink(string title,string description,string action,Action fn){var p=V(T(title,14,ink,true),T(description,12,muted));Space(p,7);p.Children.Add(Btn(action,fn,"Ghost"));return p;}
 UIElement PatchRow(PatchState s)
 {
  var row=new Grid();row.ColumnDefinitions.Add(new(){Width=new(46)});row.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=new(100)});row.ColumnDefinitions.Add(new(){Width=new(88)});
  var glyph=new Border{Width=34,Height=36,Background=B("#223C5D"),CornerRadius=new(7),Child=new TextBlock{Text=s.Patch.Id=="Act4FinalAscent"?"Ⅳ":"◇",FontSize=20,Foreground=gold,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}};row.Children.Add(glyph);
  var title=V(T(s.Patch.Name,15,ink,true),T(demo?s.Patch.Summary:s.Reason,12,muted));Grid.SetColumn(title,1);row.Children.Add(title);
  var status=Badge(s.Status,s.Status=="已安装"?green:s.Status is "待修复" or "需联动"?gold:muted);status.HorizontalAlignment=HorizontalAlignment.Left;status.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(status,2);row.Children.Add(status);
  var detail=Btn("详情  →",()=>ShowDetail(s.Patch.Id),"Ghost");detail.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(detail,3);row.Children.Add(detail);
  return Card(row,"#D9182942",18);
 }
 void ShowDetail(string id)
 {
  var s=states.Single(x=>x.Patch.Id==id);var p=s.Patch;var root=V(Btn("←  返回补丁库",ShowLibrary,"Ghost"));Space(root,16);
  root.Children.Add(Header(p.Category.ToUpperInvariant(),p.Name,p.Summary));Space(root,26);
  var left=V(H(Badge(s.Status,s.Status=="已安装"?green:gold),Badge("当前收录版本 "+p.Version)));Space(left,26);
  left.Children.Add(T("它解决什么",18,ink,true));Space(left,9);left.Children.Add(T(p.Detail,14,muted));left.Children.Add(Rule());
  left.Children.Add(T("推荐依据",18,ink,true));Space(left,10);left.Children.Add(T(s.Reason,14,muted));
  foreach(var line in s.Evidence??[]) {Space(left,8);left.Children.Add(T("• "+line,13,muted));}
  if(s.RelatedPatchIds?.Length>0){Space(left,18);left.Children.Add(T("关联补丁",17,ink,true));foreach(var related in s.RelatedPatchIds){var item=states.FirstOrDefault(x=>x.Patch.Id==related);if(item!=null)left.Children.Add(Btn(item.Patch.Name+" →",()=>ShowDetail(related),"Ghost"));}}
  left.Children.Add(Rule());left.Children.Add(T("涉及文件与验证边界",18,ink,true));Space(left,8);
  var definition=(demo?Catalog.Bundled:scannedCatalog).FirstOrDefault(x=>x.ComponentId==id);
  if(definition!=null){
   try{left.Children.Add(T(string.Join("\n",definition.Metadata.GetProperty("detection").GetProperty("installed_fingerprints").EnumerateArray().Select(f=>f.GetProperty("mod_id").GetString()+" / "+f.GetProperty("relative_path").GetString())),12,muted));}catch(JsonException){}catch(InvalidOperationException){}catch(KeyNotFoundException){}
   Space(left,12);left.Children.Add(T(string.Join("\n",definition.Limits),12,muted));
  }
  var right=V(T("适用性",12,muted));Space(right,8);right.Children.Add(T(s.Status=="需核对"?"文件版本待核对":s.Status=="不适用"?"当前组合不适用":s.Status=="需联动"?"需要关联更新":s.Status=="已安装"?"修复文件一致":"识别到已收录版本",18,s.Status=="已安装"?green:gold,true));Space(right,12);right.Children.Add(T(s.Reason,12,muted));right.Children.Add(Rule());
  right.Children.Add(T("已验证",13,ink,true));Space(right,6);right.Children.Add(T("扫描核对文件与适配条件。\n完整真人联机回归仍待验证。",12,muted));Space(right,23);
  right.Children.Add(Btn(!demo?"选择本项修复":s.Status=="已安装"?"查看安装状态":"查看修复方案",()=>{if(!demo)SelectPatchForInstallation(id);else if(s.Status=="已安装")ShowResult();else ShowReview();},"Primary"));
  Space(right,12);right.Children.Add(T(demo?"本次操作仅演示界面流程。":"安装页可选择本版已审核补丁，并自动加入必要关联项；安装前仍需单独预检。",11,muted));
  root.Children.Add(Columns(left,Card(right),290));SetPage("补丁库",root,"patch:"+id,()=>{if(states.Any(s=>s.Patch.Id==id))ShowDetail(id);else ShowOverview();});
 }
 void ShowLibrary()
 {
  var root=V(Header("PATCH LIBRARY","补丁库",$"当前目录 {states.Count} 项修复。按状态筛选，并查看每项适用原因。"));Space(root,22);
  var search=new TextBox{Text=query,Width=260,ToolTip="按修复名、模组 ID 或问题搜索"};search.SetValue(System.Windows.Automation.AutomationProperties.NameProperty,"搜索补丁");
  var tabs=new WrapPanel{Margin=new(0,0,0,18)};
  var list=new StackPanel();
  void Render(){list.Children.Clear();var found=states.Where(s=>(filter=="全部"||s.Status==filter)&&(s.Patch.Name+s.Patch.Id+s.Patch.Summary+string.Join(" ",s.Patch.Requirements)).Contains(query,StringComparison.OrdinalIgnoreCase)).ToList();foreach(var s in found){list.Children.Add(PatchRow(s));Space(list,9);}if(found.Count==0)list.Children.Add(Card(V(T("没有找到匹配的补丁",18),T("换个关键词，或查看全部修复。",13,muted))));}
  foreach(var f in new[]{"全部","待修复","需联动","已安装","已停用","不适用","需核对"}){var b=Btn(f,()=>{filter=f;ShowLibrary();},f==filter?"Primary":"Ghost");b.Margin=new(0,0,8,0);tabs.Children.Add(b);}
  search.TextChanged+=(_,_)=>{query=search.Text;Render();string q=query,f=filter;RememberPage("补丁库",()=>{query=q;filter=f;ShowLibrary();});};
  var bar=new DockPanel{Margin=new(0,0,0,16)};DockPanel.SetDock(search,Dock.Right);bar.Children.Add(search);bar.Children.Add(T("筛选修复",12,muted));root.Children.Add(bar);root.Children.Add(tabs);root.Children.Add(list);Render();string savedFilter=filter,savedQuery=query;SetPage("补丁库",root,restore:()=>{filter=savedFilter;query=savedQuery;ShowLibrary();});
 }
 void ShowReview()
 {
  if(!demo){filter="全部";ShowLibrary();return;}
  var root=V(Btn("←  返回总览",ShowOverview,"Ghost"));Space(root,16);root.Children.Add(Header("REVIEW CHANGES","先看清，再修复。",demo?"本次为安装演示，不下载、不替换游戏文件。":"本次为只读检查，不会执行安装。"));Space(root,25);
  var pending=states.Where(s=>s.Status=="待修复").ToList();var left=V();
  if(pending.Count==0)left.Children.Add(Card(V(T("没有待安装的已收录修复",18),T("你可以返回补丁库查看所有状态。",13,muted))));
  foreach(var s in pending){left.Children.Add(PatchRow(s));Space(left,12);}
  Space(left,8);left.Children.Add(T("关联修复与校验",16,ink,true));Space(left,8);left.Children.Add(T("第四幕 DLL 更新后，联机兼容补丁也需要对应的新校验值。两项都需要检查；已安装的项目无需重复处理。",13,muted));
  if(!demo&&report?.Issues.Count>0){Space(left,16);left.Children.Add(T("前置检查提醒",15,gold,true));foreach(var issue in report.Issues.Take(5))left.Children.Add(T("• "+issue,12,muted));}
  var side=V(T("操作计划",17,ink,true));Space(side,18);
  foreach(var (num,label,desc) in new[]{("01","校验文件","确认原模组与修复适用版本"),("02","保存备份","记录安装前的文件状态"),("03","应用修复","同时处理关联校验更新"),("04","验证结果","核对文件完整性与修复状态")}){side.Children.Add(T(num+"   "+label,14,ink,true));side.Children.Add(T("       "+desc,12,muted));Space(side,18);}
  side.Children.Add(Rule());side.Children.Add(T(demo?"演示环境 · 2 项修复":"真实目录 · 只读检查",12,gold));Space(side,14);
  side.Children.Add(Btn(demo?"预览安装过程  →":"导出检查报告",()=>{if(demo)StartDemoInstall();else ExportReport();},"Primary"));
  root.Children.Add(Columns(left,Card(side),290));SetPage("补丁库",root);
 }
 async void StartDemoInstall()
 {
  if(busy)return;busy=true;
  var title=T("正在演示安装流程",29,ink,true);var sub=T("仅更新原型中的状态，不访问或修改游戏文件。",13,muted);
  var progress=new ProgressBar{Minimum=0,Maximum=100,Height=5,Foreground=gold,Background=B("#EAF0E5"),BorderThickness=new(0)};
  var stage=T("校验适用版本…",15);var percent=T("0%",18,gold,true);
  var p=V(Badge("演示进行中",gold));Space(p,28);p.Children.Add(title);p.Children.Add(sub);Space(p,36);p.Children.Add(H(stage,percent));Space(p,16);p.Children.Add(progress);Space(p,28);p.Children.Add(T("校验 → 备份 → 应用 → 验证",12,muted));
  SetPage("补丁库",Card(p,"#D9182942",36));toast.Text="演示期间请稍候。真实安装请使用安装与回滚页。";
  var labels=new[]{"校验适用版本…","生成备份记录（演示）…","应用关联修复（演示）…","验证文件状态（演示）…"};
  for(int i=0;i<4;i++){stage.Text=labels[i];for(int j=0;j<5;j++){progress.Value=i*25+(j+1)*5;percent.Text=progress.Value+"%";await Task.Delay(110);}}
  installed=true;states=Catalog.Demo(true);busy=false;ShowResult();
 }
 void ShowResult()
 {
  if(!demo){ShowInstaller();return;}
  if(!installed){ShowReview();return;}
  var root=V(Header("READY FOR THE NEXT RUN","修复就绪，准备继续。","安装结果页 · 演示状态"));Space(root,26);
  var summary=V(Badge("✓  演示安装完成",green));Space(summary,20);summary.Children.Add(T("2 项修复已在演示中应用",26,ink,true));Space(summary,9);summary.Children.Add(T("第四幕环境特效循环与联机校验已同步。真实游戏文件未发生变化。",14,muted));Space(summary,26);
  foreach(var name in new[]{"建筑家 BOSS 入场卡死","联机兼容校验更新"}){summary.Children.Add(H(T("✓",16,green,true),T(name,15)));Space(summary,12);}
  summary.Children.Add(Rule());summary.Children.Add(T("接下来",16,ink,true));Space(summary,8);summary.Children.Add(T("实际安装后，需要和队友一起重启游戏，再进入 BOSS 战验证首回合。",14,muted));Space(summary,24);summary.Children.Add(H(Btn("返回总览",ShowOverview,"Primary"),Btn("和队友比较",ShowCoop,"Ghost")));
  var backup=V(T("备份与撤销",17,ink,true));Space(backup,14);backup.Children.Add(T("原文件备份",12,muted));backup.Children.Add(T("演示：未创建真实备份",14,gold));Space(backup,20);backup.Children.Add(T("不符合预期时，可恢复安装前的状态。真实回滚会先检查后续更新。",13,muted));Space(backup,22);backup.Children.Add(Btn("预览回滚",()=>{installed=false;states=Catalog.Demo();ShowOverview();toast.Text="演示已恢复到安装前。没有真实文件需要回滚。";}));
  root.Children.Add(Columns(Card(summary,"#CD203B5C",30),Card(backup),285));SetPage("补丁库",root);toast.Text="演示结束。你可以继续比较补丁状态，或预览回滚流程。";
 }
 void ResetDemo(){if(busy)return;demo=true;report=null;installed=false;states=Catalog.Demo();filter="全部";query="";ShowOverview();}
 async void ChooseFolder()
 {if(busy)return;var dialog=new OpenFolderDialog{Title="选择包含 SlayTheSpire2.exe 的游戏根目录"};if(dialog.ShowDialog(this)==true)await ScanFolder(dialog.FolderName);}
 async Task ScanFolder(string folder)
 {
  if(busy)return;busy=true;ClearCoop();toast.Text="正在读取模组清单与文件校验值…";
  try {UseGameRoot(folder);coopComparison=null;var catalog=sourceCurrent?onlineCatalog:null;var definitions=catalog?.Patches??Catalog.Bundled;report=await Task.Run(()=>Catalog.Scan(folder,definitions,catalog?.Commit));scannedCatalog=definitions;states=report.Patches;demo=false;selectedInstallRoot=report.Directory;ClearSelection();installPreview=null;lastInstallResult=null;modInventory=null;ShowOverview();}
  catch(Exception ex){MessageBox.Show(this,ex.Message,"扫描未完成",MessageBoxButton.OK,MessageBoxImage.Information);toast.Text="扫描未完成，未修改任何文件。";if(report==null)ShowOverview();}
  finally{busy=false;}
 }
 async void Rescan(){if(busy)return;if(!demo&&selectedInstallRoot!=null){await ScanFolder(selectedInstallRoot);return;}toast.Text="正在检查内置演示目录…";busy=true;await Task.Delay(600);busy=false;ShowOverview();toast.Text="演示目录已刷新。真实仓库信息请查看在线补丁页。";}
 async void ExportReport()
 {
  if(busy||transactionWriting)return;
  var save=new SaveFileDialog{Title="导出补丁状态报告",FileName="SpirePatch-report.json",Filter="JSON 报告|*.json"};if(save.ShowDialog(this)!=true)return;
  await RunReportActionAsync(async()=>{await ExportFreshReportAsync(save.FileName);toast.Text=demo?"已导出演示清单，不代表真实游戏文件。":"已重新扫描并导出当前补丁状态，不包含存档和日志内容。";});
 }
 void ShowDiagnosis()
 {
  var root=V(Header("DIAGNOSTICS","从异常，找到线索。","选择一次游戏日志，匹配我们已经定位的故障。"));Space(root,26);
  var p=V(T("检查最近一次报错",23,ink,true));Space(p,9);p.Children.Add(T("可识别第四幕兼容校验失败、超大回血溢出及部分模组异常。日志没有报错时，不会据此判断游戏正常。",14,muted));Space(p,22);p.Children.Add(Btn("选择 godot.log  ↗",ChooseLog,"Primary"));root.Children.Add(Card(p));Space(root,20);
  if(lastDiagnosis!=null){root.Children.Add(Card(V(T("诊断结果",18,ink,true),T(lastDiagnosis,14,muted))));Space(root,18);}
  root.Children.Add(Columns(QuietLink("游戏正在无响应","日志可能没有留下异常，通常还需要卡住时的线程堆栈。","查看采集说明",()=>MessageBox.Show(this,"此原型尚未接入线程采集。\n\n保留未响应进程时，可以取得更完整的调用堆栈。不要仅凭最后一条日志判断责任模组。","未响应诊断")),QuietLink("只匹配已知问题","找不到对应特征时，会明确显示未匹配，不自动推荐不相关补丁。","查看已收录修复",ShowLibrary),340));SetPage("诊断",root);
 }
 async void ChooseLog()
 {
  var dialog=new OpenFileDialog{Title="选择本次游戏日志",Filter="日志文件|*.log;*.txt|所有文件|*.*",InitialDirectory=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"SlayTheSpire2","logs")};if(dialog.ShowDialog(this)!=true)return;
  try {var text=await Task.Run(()=>{using var f=new FileStream(dialog.FileName,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);using var r=new StreamReader(f);var lines=new Queue<string>();while(r.ReadLine() is {} line){lines.Enqueue(line);if(lines.Count>12000)lines.Dequeue();}return string.Join('\n',lines);});
   var found=new List<string>();if(text.Contains("Version mismatch: Act4FinalAscent"))found.Add("明确匹配：第四幕文件校验不一致。对应「联机兼容校验更新」。仍需核对本机文件版本后再安装。");
   if(text.Contains("OverflowException")&&text.Contains("NHealNumVfx"))found.Add("匹配：超大回血飘字溢出。对应「超大回血数字溢出」。");
   if(text.Contains("MutableModelException"))found.Add("检测到可变模型异常。目前没有针对这条异常的已验证修复，不能自动归因到某个模组。");
   lastDiagnosis=found.Count>0?string.Join("\n\n",found):"最近 12,000 行未匹配到已收录的故障特征。这不代表没有故障；未响应或闪退可能需要其他诊断证据。";ShowDiagnosis();
  }catch(Exception ex){MessageBox.Show(this,ex.Message,"无法读取日志");}
 }
 void ShowCoop()
 {
  var root=V(Header("PARTY CHECK","联机检查","导入队友清单，查看差异，按需对齐本机补丁。"));Space(root,20);
  var summary=V(T(demo?"演示配置 · 不读取游戏文件":report==null?"尚未扫描；导出或导入时会扫描当前目录":$"本机 {states.Count(s=>s.Status=="已安装")} 项修复已匹配 · 上次扫描 {report.Time:yyyy-MM-dd HH:mm:ss}",13,muted));Space(summary,10);
  summary.Children.Add(H(Btn("导出我的清单",ExportReport,"Ghost"),Btn("导入队友清单  ↗",ImportPeer,"Primary")));Space(summary,8);summary.Children.Add(T("每次导出、比对都会重新扫描本机。队友文件有变化时，需要重新导出清单。",12,muted));root.Children.Add(Card(summary,"#D9182942",18));Space(root,18);
  AddCoopResults(root);root.IsEnabled=!busy;
  root.Children.Add(T("比较范围",15,ink,true));Space(root,8);root.Children.Add(T("此版本只比较已收录修复的状态，不包含全部原模组文件及玩法配置。状态一致不能单独证明所有联机条件完全相同。演示报告与真实报告会区分显示。",13,muted));SetPage("联机检查",root);
 }
 async void ImportPeer()
 {
  if(busy||transactionWriting)return;
  var open=new OpenFileDialog{Title="选择队友的补丁报告",Filter="补丁报告|*.json"};if(open.ShowDialog(this)!=true)return;
  await RunReportActionAsync(async()=>{await CompareFreshReportAsync(open.FileName);toast.Text="已与本机最新扫描比对。";});
 }
 public async Task CaptureAsync(string directory)
 {
  Directory.CreateDirectory(directory);await Task.Delay(250);
  async Task Shot(string name){await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);UpdateLayout();var image=new RenderTargetBitmap((int)shell.ActualWidth,(int)shell.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(shell);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=File.Create(System.IO.Path.Combine(directory,name+".png"));encoder.Save(stream);}
  ShowOverview();await Shot("01-overview");ShowDetail("Act4FinalAscent");await Shot("02-detail");ShowReview();await Shot("03-review");
  IEnumerable<Button> Buttons(DependencyObject parent){for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var c=VisualTreeHelper.GetChild(parent,i);if(c is Button b)yield return b;foreach(var nested in Buttons(c))yield return nested;}}
  Buttons(shell).Single(b=>b.Content?.ToString()=="预览安装过程  →").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  while(busy)await Task.Delay(80);
  if(!installed||states.Any(s=>s.Status!="已安装"))throw new InvalidOperationException("Demo install interaction failed.");
  await Shot("04-result");
  Buttons(shell).Single(b=>b.Content?.ToString()=="预览回滚").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  if(installed||states.Count(s=>s.Status=="待修复")!=2)throw new InvalidOperationException("Demo rollback interaction failed.");
  File.WriteAllText(System.IO.Path.Combine(directory,"interaction-checks.txt"),"PASS actual preview-install button transitions through progress into completed state.\nPASS actual preview-rollback button restores two pending fixes.\n");
  Width=1040;Height=760;ShowOverview();await Shot("05-compact");
 }
}
