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
  async Task Click(string label,bool firstMatch=false){await Idle();var buttons=Controls<Button>(shell).Where(b=>b.Content?.ToString()==label);(firstMatch?buttons.First():buttons.Single()).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));while(busy)await Task.Delay(60);await Idle();}
  async Task Shot(string name){await Idle();var image=new RenderTargetBitmap((int)shell.ActualWidth,(int)shell.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(shell);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=File.Create(Path.Combine(output,name+".png"));encoder.Save(stream);}
  gameCandidates=[new(first,"Steam 库（测试）"),new(second,"另一安装目录（测试）")];locateStatus="发现多个安装目录，请选择本次使用的一个。";ShowLocations();await Shot("01-选择定位结果");
  await Click("使用此目录",true);Check(selectedInstallRoot==first&&modInventory?.Mods.Count==7&&folderLabel.Text==first,"directory choice is shared by mod management and global controls");Check(launchButton?.IsEnabled==true,"launch game button becomes available after directory selection");
  await Click("← 上一步");Check(currentVisit?.Key=="游戏目录"&&selectedInstallRoot==first,"back returns to directory results without undoing chosen game directory");await Click("使用此目录",true);
  var search=Controls<TextBox>(shell).Single();search.Text="独立";await Idle();await Click("详情");Check(currentVisit?.Key.StartsWith("mod:")==true,"mod detail opens from actual filtered list button");await Click("← 上一步");Check(modQuery=="独立"&&Controls<TextBox>(shell).Single().Text=="独立","back preserves mod search text");
  Controls<TextBox>(shell).Single().Text="";await Idle();pageScroll!.ScrollToVerticalOffset(180);await Idle();double offset=pageScroll.VerticalOffset;
  string soloFolder=modInventory!.Mods.Single(m=>m.Id=="TestSolo").Folder;ShowModDetail(soloFolder);await Idle();await Click("← 上一步");Check(offset>0&&Math.Abs(pageScroll.VerticalOffset-offset)<1,"back restores previous list scroll position");pageScroll.ScrollToTop();await Shot("02-模组管理列表");
  ShowModDetail(soloFolder);await Click("预览停用");Check(modTogglePreview!=null&&Directory.Exists(soloFolder),"mod confirmation preview does not move files");await Shot("03-停用前确认");await Click("确认停用");Check(modInventory!.Mods.Single(m=>m.Id=="TestSolo").Enabled==false&&!Directory.Exists(soloFolder),"actual disable button retains mod outside active directory");
  await Click("← 上一步");Check(modTogglePreview==null&&modInventory!.Mods.Single(m=>m.Id=="TestSolo").Enabled==false,"back after a file operation cannot replay stale confirmation");
  ShowModDetail(modInventory!.Mods.Single(m=>m.Id=="TestSolo").Folder);await Click("预览启用");await Click("确认启用");Check(modInventory!.Mods.Single(m=>m.Id=="TestSolo").Enabled&&Directory.Exists(soloFolder),"actual enable button restores mod to original directory");await Shot("04-已恢复启用");
  ShowModDetail(modInventory.Mods.Single(m=>m.Id=="TestBase").Folder);await Idle();Check(!Controls<Button>(shell).Single(b=>b.Content?.ToString()=="预览停用").IsEnabled,"UI disables unsafe base-mod toggle and shows dependency reason");await Shot("05-前置依赖保护");
  installPreview=new(true,"Preview",first,"test","old-token","","",[],0);UseGameRoot(second);ShowMods();Check(installPreview==null&&report==null&&selectedInstallRoot==second,"switching game directory invalidates old scan and install preview");await Click("← 上一步");Check(selectedInstallRoot==second&&installPreview==null,"back across a directory switch cannot restore old operation scope");
  Check(File.Exists(Path.Combine(first,"mods","独立 模组","TestSolo.dll")),"UI checks end with original mod enabled and never launch a game");
 }
 public async Task CaptureLiveModsAsync(string root,string output)
 {
  UseGameRoot(root);modInventory=await Task.Run(()=>ModManager.Scan(root));modStatus=$"共 {modInventory.Mods.Count} 项 · 只读界面预览";ShowMods();Directory.CreateDirectory(output);await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);UpdateLayout();
  var image=new RenderTargetBitmap((int)shell.ActualWidth,(int)shell.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(shell);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=File.Create(Path.Combine(output,"本机模组列表.png"));encoder.Save(stream);
 }
}
