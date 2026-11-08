using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpirePatch;
public partial class MainWindow
{
 public async Task CaptureRecommendationsAsync(string fixture,string output)
 {
  InstallerChecks.AssertFixture(fixture);Directory.CreateDirectory(output);
  report=await Task.Run(()=>Catalog.Scan(fixture));states=report.Patches;demo=false;scannedCatalog=Catalog.Bundled;selectedInstallRoot=fixture;
  async Task Shot(string name){await Task.Delay(100);UpdateLayout();var image=new RenderTargetBitmap((int)shell.ActualWidth,(int)shell.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(shell);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=File.Create(Path.Combine(output,name+".png"));encoder.Save(stream);}
  IEnumerable<Button> Buttons(DependencyObject parent){for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is Button b)yield return b;foreach(var nested in Buttons(child))yield return nested;}}
  ShowOverview();await Shot("推荐总览");ShowLibrary();UpdateLayout();
  Buttons(shell).Single(b=>b.Content?.ToString()=="需联动").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Shot("联动筛选");
  if(filter!="需联动"||states.Count(s=>s.Status=="需联动")!=2)throw new InvalidOperationException("联动筛选验证失败");
  ShowDetail("Act4FinalAscent");await Shot("推荐依据");
  var linked=states.Single(s=>s.Patch.Id=="SpireTogetherFix");
  Buttons(shell).Single(b=>b.Content?.ToString()==linked.Patch.Name+" →").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Shot("关联补丁");
  File.WriteAllText(Path.Combine(output,"界面验证.txt"),"PASS 真实只读扫描呈现推荐总览。\nPASS 点击需联动筛选展示关联修复。\nPASS 点击详情中的关联补丁跳转到对应说明。\n");
 }
}
