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
   newTint.GradientStops.All(stop=>stop.Color.A<=120),
   "balanced native acrylic no longer uses a nearly opaque window overlay");
  Check(SpireGlassTheme.Background(true,true) is SolidColorBrush diagnosticTint&&
   diagnosticTint.Color.A<=20,
   "bare acrylic diagnostic can expose the actual desktop composition without decorative color");
  Check(Controls<Canvas>(shell).Any(c=>!c.IsHitTestVisible&&c.Children.Count>=3),
   "decorative glass fallback is rendered behind navigation without stealing pointer input");
  gameCandidates=[new(first,"Steam 库（测试）"),new(second,"另一安装目录（测试）")];locateStatus="发现多个安装目录，请选择本次使用的一个。";ShowLocations();await Shot("01-选择定位结果");
  await Click("使用此目录",true);Check(selectedInstallRoot==first&&modInventory?.Mods.Count==7&&folderLabel.Text==first,"directory choice is shared by mod management and global controls");Check(launchButton?.IsEnabled==true,"launch game button becomes available after directory selection");
  await Click("← 上一步");Check(currentVisit?.Key=="游戏目录"&&selectedInstallRoot==first,"back returns to directory results without undoing chosen game directory");await Click("使用此目录",true);
  Check(navButtons.Keys.Count==7&&navButtons.ContainsKey("修复补丁")&&navButtons.ContainsKey("更新中心")&&navButtons.ContainsKey("工具与设置"),
   "sidebar exposes seven destinations including new patch and MOD update workspaces");
  Navigate("修复补丁");await Idle();
  Check(page=="补丁库"&&Controls<Button>(content).Count(b=>new[]{"补丁库","在线补丁","补丁健康","安装与回滚"}.Contains(b.Content?.ToString()))==4,
   "patch center exposes all four existing patch views");
  ShowLifecycle();await Idle();
  Check(page=="补丁健康"&&navButtons["修复补丁"].Background.ToString()==B("#6983B9F8").ToString(),
   "patch health keeps the patch-center navigation highlighted");
  ShowMods();await Idle();
  Check(Controls<Button>(shell).Any(b=>b.Content?.ToString()=="普通 MOD  (6)")&&
   Controls<Button>(shell).Any(b=>b.Content?.ToString()=="修复补丁  (1)"),
   "mod management exposes regular MOD and independent patch tabs with accurate counts");
  await Click("修复补丁  (1)");
  Check(activeModList?.Items.Count==1&&Controls<Button>(shell).Any(b=>b.Content?.ToString()=="详情")&&
   Controls<TextBlock>(shell).Any(t=>t.Text.Contains("LargeHealDisplayFix")),
   "independent patch tab filters to known patch records");
  var patchMore=Controls<Button>(shell).Single(b=>b.Content?.ToString()=="更多");
  patchMore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Idle();
  Check(patchMore.ContextMenu?.Items.OfType<MenuItem>().Count()==2,
   "on-demand compact patch menu exposes preview toggle and folder action");
  patchMore.ContextMenu!.IsOpen=false;
  Check(Controls<TextBlock>(shell).Any(x=>x.Text.Contains("健康：")),
   "repair patch rows show a descriptive health status even before a scan");
  await Click("详情");
  Check(Controls<Button>(shell).Any(b=>b.Content?.ToString()=="查看依据"),
   "independent patch details expose the evidence viewer");
  await Click("查看依据");
  Check(currentVisit?.Key=="patch-evidence:LargeHealDisplayFix"&&
   Controls<Button>(shell).Any(b=>b.Content?.ToString()=="重新检查健康状态"),
   "evidence viewer explains unscanned state and exposes a rescan");
  await Click("← 上一步");
  Check(currentVisit?.Key.StartsWith("mod:")==true,"back returns from evidence to original patch detail");
  await Click("← 上一步");
  Check(page=="模组管理","back returns to repair patch list");
  await Click("普通 MOD  (6)");
  Check(activeModList?.Items.Count==6,
   "regular MOD tab excludes the catalogued independent patch");
  var search=Controls<TextBox>(shell).Single();search.Text="独立";await Idle();await Click("详情");Check(currentVisit?.Key.StartsWith("mod:")==true,"mod detail opens from actual filtered list button");await Click("← 上一步");Check(modQuery=="独立"&&Controls<TextBox>(shell).Single().Text=="独立","back preserves mod search text");
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
  ShowMods();await Idle();await Click("修复补丁  (1)");
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
  virtualList.ItemsSource=many;await Idle();
  Check(virtualList.Items.Count==160&&VirtualizingPanel.GetIsVirtualizing(virtualList)&&
   VirtualizingPanel.GetVirtualizationMode(virtualList)==VirtualizationMode.Recycling&&
   Controls<Button>(virtualList).Count(b=>b.Content?.ToString()=="详情")<30,
   "160 virtualized MOD rows only materialize the visible WPF controls");
  // Regression for actual Windows clients: long dependent names previously pushed actions off screen.
  string longBlock="这些启用项依赖它，请先停用依赖项："+string.Join("、",
   Enumerable.Range(0,40).Select(i=>"超长依赖模组名称"+i+"测试补丁"));
  virtualList.ItemsSource=new[]{new ModListRow(sample with{Name="BaseLib / 一个非常非常长的中文模组名称 / test"},
   "",longBlock)};
  await Idle();
  double originalWidth=Width,originalHeight=Height;
  Width=1020;Height=720;await Idle();
  var rowView=Controls<ModRowControl>(virtualList).Single();
  var detailsButton=Controls<Button>(rowView).Single(b=>b.Content?.ToString()=="详情");
  var moreButton=rowView.MoreButton;
  bool Fits(Button button)
  {
   var position=button.TransformToAncestor(virtualList).Transform(new Point(0,0));
   return button.ActualWidth>=45&&position.X>=0&&position.X+button.ActualWidth<=virtualList.ActualWidth-4;
  }
  Check(Fits(detailsButton)&&Fits(moreButton)&&
   rowView.ActualWidth<=virtualList.ActualWidth&&virtualList.ActualWidth>500,
   "minimum supported window width visibly keeps both MOD actions inside the row");
  var warningLine=Controls<TextBlock>(rowView).Single(t=>t.Text.Contains("依赖保护"));
  Check(warningLine.Text.Length<85&&warningLine.ToolTip?.ToString()==longBlock&&
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
