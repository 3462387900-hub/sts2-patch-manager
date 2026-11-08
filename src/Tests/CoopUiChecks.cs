using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SpirePatch;
public partial class MainWindow
{
 public async Task CaptureCoopActionsAsync(string zip,string root,string peerPath,string output)
 {
  InstallerChecks.AssertFixture(root);Directory.CreateDirectory(output);var checks=new List<string>();
  void Check(bool value,string label){if(!value)throw new InvalidOperationException(label);checks.Add("PASS "+label);File.WriteAllLines(Path.Combine(output,"界面验证.txt"),checks);}
  IEnumerable<T> Controls<T>(DependencyObject parent) where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var c=VisualTreeHelper.GetChild(parent,i);if(c is T t)yield return t;foreach(var nested in Controls<T>(c))yield return nested;}}
  async Task Click(string name){await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);UpdateLayout();Controls<Button>(shell).Single(b=>b.Content?.ToString()==name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));while(busy)await Task.Delay(60);await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);UpdateLayout();}
  async Task Shot(string name){await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);UpdateLayout();var bitmap=new RenderTargetBitmap((int)shell.ActualWidth,(int)shell.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(shell);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(output,name+".png"));encoder.Save(stream);}
  UseGameRoot(root);localPackage=zip;await CompareFreshReportAsync(peerPath);ShowCoop();UpdateLayout();
  Check(coopRows!.Count(r=>r.Selectable)==2&&coopSelected.Count==0,"differences render unselected by default");await Shot("01-差异列表");
  var first=Controls<CheckBox>(shell).First(c=>c.IsEnabled);first.IsChecked=true;Check(coopSelected.Count==1,"single checkbox selects one difference");
  await Click("全选可处理项");Check(coopSelected.Count==2,"select-all selects actionable differences");
  await Click("清空选择");Check(coopSelected.Count==0,"clear selection removes choices");await Click("全选可处理项");
  await Click("预览一键处理（2）");Check(coopPlan?.Install!=null&&coopPlan.Moves.Length==1,"preview button prepares actual install and move");
  Controls<Button>(shell).Single(b=>b.Content?.ToString()=="确认一键处理").BringIntoView();await Shot("02-处理预览");
  await Click("取消本次方案");Check(coopPlan==null&&!ModManager.Scan(root).Mods.Any(m=>m.Id=="LargeHealDisplayFix"&&m.Enabled),"cancel preview writes no patch files");
  await Click("预览一键处理（2）");await Click("确认一键处理");
  Check(coopOutcome?.Success==true&&coopOutcome.Completed.Length==2,"confirmation button executes both approved actions");
  Check(coopSelected.Count==0&&!coopRows!.Any(r=>r.Selectable),"post-operation rescan clears selection and repeat actions");pageScroll!.ScrollToTop();await Shot("03-处理完成");
  Width=1040;Height=760;UpdateLayout();await Shot("04-窄窗口结果");
 }
}
