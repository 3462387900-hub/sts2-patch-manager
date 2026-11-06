using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SpirePatch;
public partial class MainWindow
{
 public async Task CaptureWorkspaceAsync(string fixtures,string output)
 {
  Directory.CreateDirectory(fixtures);Directory.CreateDirectory(output);string first=WorkspaceChecks.Fixture(fixtures),second=WorkspaceChecks.Fixture(fixtures);
  var checks=new List<string>();void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);checks.Add("PASS "+name);File.WriteAllLines(Path.Combine(output,"四项功能界面验证.txt"),checks);}
  IEnumerable<T> Controls<T>(DependencyObject parent) where T:DependencyObject {for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T control)yield return control;foreach(var next in Controls<T>(child))yield return next;}}
  async Task Idle(){await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);UpdateLayout();UpdateGlobalControls();}
  async Task Click(string label,bool firstMatch=false)
  {
   // FileSystemWatcher refreshes can begin between UI assertions. Wait for a usable
   // control before clicking; testing a click while the app is busy is not user input.
   // A control may be visually present while its parent is temporarily disabled.
   // Wait for the actual enabled state, not only the background busy flag.
   Button? ready=null;
   for(int i=0;i<150;i++)
   {
    await Idle();
    var candidates=Controls<Button>(shell).Where(b=>b.Content?.ToString()==label).ToArray();
    ready=firstMatch?candidates.FirstOrDefault(b=>b.IsEnabled):
     candidates.Length==1&&candidates[0].IsEnabled?candidates[0]:null;
    if(!busy&&!transactionWriting&&ready!=null)break;
    ready=null;await Task.Delay(60);
   }
   if(ready==null)throw new InvalidOperationException("界面操作按钮一直不可用："+label);
   // Raise the WPF Click synchronously on the dispatcher: an asynchronous
   // UIAutomation Invoke can arrive *after* the next test navigation and
   // unexpectedly replace its page. The button's own Click handlers and safety
   // guards still run unmodified.
   ready.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,ready));
   await Idle();
   for(int i=0;i<150&&(busy||transactionWriting);i++)
   {
    await Task.Delay(60);
    await Idle();
   }
   if(busy||transactionWriting)throw new InvalidOperationException("UI 操作超时："+label);
   await Idle();
  }
  async Task Shot(string name){await Idle();var image=new RenderTargetBitmap((int)shell.ActualWidth,(int)shell.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(shell);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=File.Create(Path.Combine(output,name+".png"));encoder.Save(stream);}
  // Regression: a visually opaque overlay hid the native acrylic throughout v0.12.0.
  Check(SpireGlassTheme.Background(true) is LinearGradientBrush acrylicTint&&
   acrylicTint.GradientStops.All(stop=>stop.Color.A<190),
   "native acrylic tint remains substantially transparent instead of hiding DWM blur");
  Check(SpireGlassTheme.GlassPanel() is LinearGradientBrush surface&&
   surface.GradientStops.All(stop=>stop.Color.A<235),
   "glass cards allow blurred and ambient backdrop to show through");
  var baseButton=(Style)FindResource(typeof(Button));
  var defaultBorder=(SolidColorBrush)((Setter)baseButton.Setters.Cast<Setter>().Single(x=>x.Property==Button.BorderBrushProperty)).Value;
  Check(defaultBorder.Color.A<=85,
   "normal buttons use soft translucent strokes rather than sharp opaque outlines");
  var primaryStyle=(Style)FindResource("Primary");
  var primaryBorder=(SolidColorBrush)((Setter)primaryStyle.Setters.Cast<Setter>().Single(x=>x.Property==Button.BorderBrushProperty)).Value;
  Check(primaryBorder.Color.A<=100,
   "primary action buttons avoid opaque duplicate fill outlines");
  Check(SpireGlassTheme.Background(true) is LinearGradientBrush newTint&&
   newTint.GradientStops.All(stop=>stop.Color.A<=165&&stop.Color.A>=120),
   "balanced native acrylic keeps a navy tint while preserving translucency");
  Check(SpireGlassTheme.Background(true,true) is SolidColorBrush diagnosticTint&&
   diagnosticTint.Color.A<=20,
   "bare acrylic diagnostic can expose the actual desktop composition without decorative color");
  Check(Controls<Canvas>(shell).Any(c=>!c.IsHitTestVisible&&c.Children.Count>=3),
   "decorative glass fallback is rendered behind navigation without stealing pointer input");
  var navStyle=(Style)FindResource("NavItem");
  Check(navButtons.Values.All(b=>ReferenceEquals(b.Style,navStyle)&&
   b.FocusVisualStyle!=null&&b.BorderThickness.Left==3),
   "borderless navigation keeps a narrow accent and a keyboard-only custom focus visual");
  var selectedTabStyle=(Style)FindResource("SelectedTab");
  Check(((SolidColorBrush)((Setter)selectedTabStyle.Setters.Cast<Setter>().Single(x=>
   x.Property==Button.BackgroundProperty)).Value).Color.A<180,
   "selected filters use a subtle translucent fill rather than primary blue");
  gameCandidates=[new(first,"Steam 库（测试）"),new(second,"另一安装目录（测试）")];locateStatus="发现多个安装目录，请选择本次使用的一个。";ShowLocations();await Shot("01-选择定位结果");
  await Click("使用此目录",true);Check(selectedInstallRoot==first&&modInventory?.Mods.Count==7&&folderLabel.Text==first,"directory choice is shared by mod management and global controls");Check(launchButton?.IsEnabled==true,"launch game button becomes available after directory selection");
  await Click("← 上一步");Check(currentVisit?.Key=="游戏目录"&&selectedInstallRoot==first,"back returns to directory results without undoing chosen game directory");await Click("使用此目录",true);
  Check(navButtons.Keys.Count==7&&navButtons.ContainsKey("修复补丁")&&navButtons.ContainsKey("更新中心")&&navButtons.ContainsKey("工具与设置"),
   "sidebar exposes seven destinations including new patch and MOD update workspaces");
  Navigate("修复补丁");await Idle();
  Check(page=="补丁库"&&Controls<Button>(content).Count(b=>new[]{"补丁库","本地补丁","在线补丁","补丁健康","安装与回滚"}.Contains(b.Content?.ToString()))==5,
   "patch center exposes all five patch views including local fixes");
  ShowLifecycle();await Idle();
  Check(page=="补丁健康"&&navButtons["修复补丁"].Background.ToString()==B("#7540668A").ToString()&&
   navButtons["修复补丁"].BorderBrush.ToString()==B("#9A91BDF3").ToString(),
   "patch health keeps the patch-center navigation highlighted");
  Navigate("模组管理");await Idle();
  Check(Controls<Button>(shell).Any(b=>b.Content?.ToString()=="已安装")&&
   Controls<Button>(shell).Any(b=>b.Content?.ToString()=="创意工坊下载")&&
   activeModList?.Items.OfType<ModCardGroup>().Sum(x=>x.Cards.Length)==6,
   "mod management shows secondary navigation and regular MODs");
  ShowWorkshopPage();await Idle();
  Check(currentVisit?.Key=="mod-workshop"&&
   Controls<TextBox>(shell).Any(x=>System.Windows.Automation.AutomationProperties.GetName(x)=="创意工坊链接或 ID"),
   "Workshop download is an actual MOD subpage with an inline input");
  var workshopInput=Controls<TextBox>(shell).First(x=>
   System.Windows.Automation.AutomationProperties.GetName(x)=="创意工坊链接或 ID");
  workshopInput.Text="invalid-test-link";await Click("获取并预览");
  Check(Controls<TextBlock>(shell).Any(x=>x.Text.Contains("链接或 ID 无效")),
   "invalid Workshop links are rejected inline without starting a download");
  ShowDependencyGraph();await Idle();
  Check(currentVisit?.Key=="mod-dependencies"&&
   Controls<Button>(shell).Any(b=>b.Content?.ToString()=="已安装"),
   "dependency analysis stays in the shared secondary MOD navigation");
  ShowModEnvironmentSnapshots();await Idle();
  Check(currentVisit?.Key=="mod-snapshots"&&
   Controls<Button>(shell).Any(b=>b.Content?.ToString()=="已安装"),
   "environment snapshots stay in the shared secondary MOD navigation");
  Navigate("本地补丁");await Idle();
  Check(activeModList?.Items.OfType<ModCardGroup>().Sum(x=>x.Cards.Length)==1&&Controls<ModCardControl>(shell).Any()&&
   Controls<TextBlock>(shell).Any(t=>t.Text.Contains("LargeHealDisplayFix")),
   "independent patch tab filters to known patch records");
  var patchMore=Controls<ModCardControl>(activeModList!).First().MoreButton;
  patchMore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Idle();
  Check(patchMore.ContextMenu?.Items.OfType<MenuItem>().Count()>=3,
   "on-demand compact patch menu exposes preview toggle and folder action");
  patchMore.ContextMenu!.IsOpen=false;
  Check(Controls<TextBlock>(shell).Any(x=>x.Text.Contains("健康：")),
   "repair patch rows show a descriptive health status even before a scan");
  ShowModDetail(modInventory!.Mods.Single(m=>m.Id=="LargeHealDisplayFix").Folder);await Idle();
  Check(Controls<Button>(shell).Any(b=>b.Content?.ToString()=="查看依据"),
   "independent patch details expose the evidence viewer");
  await Click("查看依据");
  Check(currentVisit?.Key=="patch-evidence:LargeHealDisplayFix"&&
   Controls<Button>(shell).Any(b=>b.Content?.ToString()=="重新检查健康状态"),
   "evidence viewer explains unscanned state and exposes a rescan");
  await Click("← 上一步");
  Check(currentVisit?.Key.StartsWith("mod:")==true,"back returns from evidence to original patch detail");
  await Click("← 上一步");
  Check(page=="本地补丁","back returns to local independent patch list");
  Navigate("模组管理");await Idle();
  Check(activeModList?.Items.OfType<ModCardGroup>().Sum(x=>x.Cards.Length)==6,
   "regular MOD tab excludes the catalogued independent patch");
  Check(ModListView.ColumnCount(600)==1&&ModListView.ColumnCount(790)==2&&ModListView.ColumnCount(1180)==3,
   "MOD cards choose one, two or three columns from usable content width");
  var cardToSelect=Controls<ModCardControl>(activeModList!).First();
  var groupToCheck=Controls<ModCardGroupControl>(activeModList!).First();
  groupToCheck.BindActions();await Idle();
  Check(ReferenceEquals(cardToSelect,Controls<ModCardControl>(activeModList!).First()),
   "attaching card actions never recreates visible MOD controls");
  var glassCheckStyle=(Style)FindResource("ModCardCheckBox");
  var glassActionStyle=(Style)FindResource("ModToolbarAction");
  Check(ReferenceEquals(cardToSelect.SelectBox.Style,glassCheckStyle)&&
   Controls<Button>(cardToSelect).Count(b=>ReferenceEquals(b.Style,glassActionStyle))==1&&
   cardToSelect.MinHeight<=110,
   "compact MOD cards use one quiet overflow button, not permanent action rows");
  Check(Controls<Button>(shell).Any(b=>b.Content?.ToString()=="全部"&&
   ReferenceEquals(b.Style,FindResource("ModSegmentActive"))),
   "status filters render as segmented controls instead of six chunky buttons");
  Check(Controls<Button>(shell).Any(b=>b.Content?.ToString()=="更多操作  ▾"&&!b.IsVisible),
   "batch actions are not visible until a selection exists");
  var checkedFolder=((ModListRow)cardToSelect.DataContext).Mod.Folder;
  cardToSelect.SelectBox.IsChecked=true;await Idle();
  Check(chosenModFolders.Contains(checkedFolder)&&
   Controls<TextBlock>(shell).Any(t=>t.Text.Contains("已选 1 项")),
   "card checkbox updates selected folders and the batch toolbar");
  Check(cardToSelect.BorderThickness.Left==1,
   "card border remains a constant 1px after toggling the selection");
  Check(cardToSelect.SelectBox.ActualWidth>=29,
   "glass checkbox hit target is at least 29px; actual "+cardToSelect.SelectBox.ActualWidth);
  Check(Controls<ModCardControl>(activeModList!).Any(c=>c.DataContext is ModListRow row&&
    row.Mod.Folder==checkedFolder&&row.Selected),
   "checkbox selection remains visible after virtualized layout adjusts");
  await Shot("02-模组卡片勾选状态");
  var selectionSearch=Controls<TextBox>(shell).Single();
  selectionSearch.Text="__筛选不匹配__";await Idle();
  Check(ChosenMods().Length==1&&Controls<TextBlock>(shell).Any(t=>t.Text.Contains("筛选隐藏 1 项")),
   "selection persists and hidden selected MODs are explicitly counted");
  selectionSearch.Text="";await Idle();
  await Click("取消选择");await Idle();
  Check(ChosenMods().Length==0,"clear selection resets batch scope without moving MOD files");
  Check(!Controls<Button>(shell).Any(b=>b.Content?.ToString()=="更多操作  ▾"&&b.IsVisible),
   "clearing selection hides the floating batch-action rail");
  var selectAll=Controls<CheckBox>(shell).Single(c=>c.Content?.ToString()=="全选当前结果");
  selectAll.IsChecked=true;await Idle();
  Check(ChosenMods().Length==6,"select all targets only six visible regular MODs");
  selectAll.IsChecked=false;await Idle();
  Check(ChosenMods().Length==0,"deselect all removes current filtered selections");
  bool? reviewAccepted=null;
  var cancelOperation=Dispatcher.BeginInvoke(new Action(()=>{
   var modal=Application.Current.Windows.OfType<Window>().First(w=>w.Title=="模组批量预览隔离测试");
   var cancel=Controls<Button>(modal).First(b=>b.Content?.ToString()=="取消");
   cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,cancel));
  }),DispatcherPriority.ApplicationIdle);
  reviewAccepted=ModBatchDialogs.Confirm(this,"模组批量预览隔离测试",
   "仅展示预览，不涉及文件修改。",[new ModBatchReview("TestSolo","可处理","测试预览")]);
  await cancelOperation.Task;
  Check(reviewAccepted==false,"batch review can be cancelled before any file operation");
  var search=Controls<TextBox>(shell).Single();search.Text="独立";await Idle();
  var filteredCard=Controls<ModCardControl>(activeModList!).First();
  filteredCard.DetailsRequested?.Invoke((filteredCard.DataContext as ModListRow ?? throw new InvalidOperationException("missing card")));
  await Idle();
  bool sidePanelOpen=Controls<Button>(shell).Any(b=>b.Content?.ToString()=="完整详情");
  Check(sidePanelOpen||currentVisit?.Key.StartsWith("mod:")==true,
   "MOD title opens contextual details or full details on narrow windows");
  if(sidePanelOpen){await Click("完整详情");await Idle();}
  Check(currentVisit?.Key.StartsWith("mod:")==true,"full MOD details remain accessible");
  await Click("← 上一步");
  Check(modQuery=="独立"&&Controls<TextBox>(shell).Single().Text=="独立","back preserves mod search text");
  Controls<TextBox>(shell).Single().Text="";await Idle();
  Check(pageScroll!.VerticalScrollBarVisibility==ScrollBarVisibility.Disabled&&
   ScrollViewer.GetHorizontalScrollBarVisibility(activeModList!)==ScrollBarVisibility.Disabled,
   "MOD list has one vertical scrollbar and no offscreen horizontal scrolling");
  var listScroll=ListScroll(activeModList!)!;
  listScroll.ScrollToEnd();await Idle();double offset=listScroll.VerticalOffset;
  string soloFolder=modInventory!.Mods.Single(m=>m.Id=="TestSolo").Folder;
  CaptureModListOffset();ShowModDetail(soloFolder);await Idle();await Click("← 上一步");
  Check(offset>0&&Math.Abs(ListScroll(activeModList!)!.VerticalOffset-offset)<1,
   "back restores virtualized MOD list scroll position");
  ListScroll(activeModList!)!.ScrollToTop();modListOffset=0;await Idle();await Shot("02-模组管理列表");
  ShowModDetail(soloFolder);await Click("预览停用");Check(modTogglePreview!=null&&Directory.Exists(soloFolder),"mod confirmation preview does not move files; state="+modStatus+"; preview="+(modTogglePreview!=null)+"; exists="+Directory.Exists(soloFolder));await Shot("03-停用前确认");await Click("确认停用");
  for(int retry=0;retry<100&&(modInventory?.Mods.SingleOrDefault(m=>m.Id=="TestSolo")?.Enabled!=false||Directory.Exists(soloFolder));retry++)
   {await Task.Delay(60);await Idle();}
  Check(modInventory!.Mods.Single(m=>m.Id=="TestSolo").Enabled==false&&!Directory.Exists(soloFolder),"actual disable button retains mod outside active directory; status="+modStatus);
  await Click("← 上一步");Check(modTogglePreview==null&&modInventory!.Mods.Single(m=>m.Id=="TestSolo").Enabled==false,"back after a file operation cannot replay stale confirmation");
  ShowModDetail(modInventory!.Mods.Single(m=>m.Id=="TestSolo").Folder);await Click("预览启用");await Click("确认启用");Check(modInventory!.Mods.Single(m=>m.Id=="TestSolo").Enabled&&Directory.Exists(soloFolder),"actual enable button restores mod to original directory");await Shot("04-已恢复启用");
  ShowModDetail(modInventory.Mods.Single(m=>m.Id=="TestBase").Folder);await Idle();Check(!Controls<Button>(shell).Single(b=>b.Content?.ToString()=="预览停用").IsEnabled,"UI disables unsafe base-mod toggle and shows dependency reason");await Shot("05-前置依赖保护");
  var baseline=ModHistory.CompareAndStore(first,Path.Combine(fixtures,"ui-lifecycle-history"));
  lifecycleReport=PatchLifecycle.Evaluate(first,baseline,LifecycleEvidenceSource.Empty,"fixture",Catalog.Scan(first),ModManager.Scan(first));
  Check(lifecycleReport!=null&&HasValidLifecycleReport,"lifecycle report is present before directory change");
  Navigate("本地补丁");await Idle();
  Check(Controls<TextBlock>(shell).Any(x=>x.Text.Contains("健康：")&&!x.Text.Contains("健康：需重新检查")),
   "valid lifecycle reports appear as live labels on the patch list");
  ShowPatchEvidence("LargeHealDisplayFix");await Idle();
  Check(Controls<TextBlock>(shell).Any(x=>x.Text.Contains("检查结论：")),
   "evidence details display the matching lifecycle conclusion");
  ShowMods();modSection="普通 MOD";ShowMods();
  installPreview=new(true,"Preview",first,"test","old-token","","",[],0);UseGameRoot(second);ShowMods();Check(installPreview==null&&report==null&&selectedInstallRoot==second,"switching game directory invalidates old scan and install preview");Check(lifecycleReport==null&&retirePreview==null,"switching game directory invalidates patch-health report");await Click("← 上一步");Check(selectedInstallRoot==second&&installPreview==null,"back across a directory switch cannot restore old operation scope");
  Check(File.Exists(Path.Combine(first,"mods","独立 模组","TestSolo.dll")),"UI checks end with original mod enabled and never launch a game");
  // FileSystemWatcher listens to this fixture game's real mods directory.
  ShowMods();await Idle();int beforeChange=modChangeVersion;
  string external=Path.Combine(second,"mods","外部测试模组");Directory.CreateDirectory(external);
  File.WriteAllText(Path.Combine(external,"ExternalProbe.json"),
   """{"id":"ExternalProbe","name":"External Probe","version":"1.0","has_dll":true}""");
  File.WriteAllText(Path.Combine(external,"ExternalProbe.dll"),"only an isolated fixture");
  for(int i=0;i<45&&modChangeVersion==beforeChange;i++){await Task.Delay(100);await Idle();}
  Check(modChangeVersion>beforeChange&&lifecycleStale&&!HasValidLifecycleReport,
   "actual external file changes invalidate cached patch health");
  for(int i=0;i<45&&(modInventory?.Mods.Any(m=>m.Id=="ExternalProbe")!=true);i++)
   {await Task.Delay(100);await Idle();}
  Check(modInventory?.Mods.Any(m=>m.Id=="ExternalProbe")==true,
   "MOD list reloads automatically once external writes become quiet");
  // Recycled item templates must not eagerly create one WPF card for each MOD.
  ShowMods();await Idle();
  var virtualList=activeModList!;
  var sample=modInventory!.Mods.First();
  var many=Enumerable.Range(0,160).Select(i=>new ModListRow(sample with{Id="UI"+i,Name="测试模组 "+i},"",null)).ToArray();
  ModCardList.SetItems(virtualList,many,2);await Idle();
  Check(virtualList.Items.Count==80&&VirtualizingPanel.GetIsVirtualizing(virtualList)&&
   VirtualizingPanel.GetVirtualizationMode(virtualList)==VirtualizationMode.Recycling&&
   Controls<ModCardControl>(virtualList).Count()<30,
   "160 virtualized MOD rows only materialize the visible WPF controls");
  // Regression for actual Windows clients: long dependent names previously pushed actions off screen.
  string longBlock="这些启用项依赖它，请先停用依赖项："+string.Join("、",
   Enumerable.Range(0,40).Select(i=>"超长依赖模组名称"+i+"测试补丁"));
  ModCardList.SetItems(virtualList,new[]{new ModListRow(sample with{Name="BaseLib / 一个非常非常长的中文模组名称 / test"},
   "",longBlock)},2);
  await Idle();
  double originalWidth=Width,originalHeight=Height;
  Width=1020;Height=720;await Idle();
  var rowView=Controls<ModCardControl>(virtualList).Single();
  var moreButton=rowView.MoreButton;
  bool Fits(Button button)
  {
   var position=button.TransformToAncestor(virtualList).Transform(new Point(0,0));
   return button.ActualWidth>=30&&position.X>=0&&position.X+button.ActualWidth<=virtualList.ActualWidth-4;
  }
  Check(Fits(moreButton)&&
   rowView.ActualWidth<=virtualList.ActualWidth&&virtualList.ActualWidth>500,
   "minimum supported window width keeps the compact overflow action in view");
  var warningLine=Controls<TextBlock>(rowView).Single(t=>t.Text.Contains("被 40 个已启用"));
  Check(warningLine.Text.Length<85&&warningLine.ToolTip?.ToString()?.Contains(longBlock)==true&&
   warningLine.ActualWidth<virtualList.ActualWidth,
   "long dependency warnings are concise but full reasons remain available via tooltip");
  await Shot("06-窄窗口模组按钮可见");
  Width=originalWidth;Height=originalHeight;await Idle();
 }
 public async Task CaptureLiveModsAsync(string root,string output)
 {
  UseGameRoot(root);modInventory=await Task.Run(()=>ModManager.Scan(root));modStatus=$"共 {modInventory.Mods.Count} 项 · 只读界面预览";ShowMods();Directory.CreateDirectory(output);await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);UpdateLayout();
  var image=new RenderTargetBitmap((int)shell.ActualWidth,(int)shell.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(shell);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=File.Create(Path.Combine(output,"本机模组列表.png"));encoder.Save(stream);
 }
}
