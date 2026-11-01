using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpirePatch;
public partial class MainWindow
{
 public async Task CaptureSelectionAsync(string zip,string fixture,string output)
 {
  InstallerChecks.AssertFixture(fixture);Directory.CreateDirectory(output);selectedInstallRoot=fixture;localPackage=zip;
  var lines=new List<string>();void Check(bool ok,string label){if(!ok)throw new InvalidOperationException(label);lines.Add("PASS "+label);File.WriteAllLines(Path.Combine(output,"选择安装界面验证.txt"),lines);}
  Dictionary<string,string> Snapshot()=>Directory.EnumerateFiles(Path.Combine(fixture,"mods"),"*",SearchOption.AllDirectories).ToDictionary(f=>f,Catalog.Hash);
  var before=Snapshot();
  IEnumerable<T> Controls<T>(DependencyObject parent) where T:DependencyObject {for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T control)yield return control;foreach(var nested in Controls<T>(child))yield return nested;}}
  async Task Click(string name){UpdateLayout();Controls<Button>(shell).Single(b=>b.Content?.ToString()==name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));while(busy)await Task.Delay(70);UpdateLayout();}
  async Task Shot(string name){await Task.Delay(100);UpdateLayout();var bitmap=new RenderTargetBitmap((int)shell.ActualWidth,(int)shell.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(shell);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(output,name+".png"));encoder.Save(stream);}
  ShowInstaller();await Click("选择要处理的补丁");Check(selectionReport!=null&&activeSelection!=null,"actual selection button scans and opens supported patch choices");
  Check(Controls<CheckBox>(shell).Count()==17&&CatalogV3Checks.Added.All(id=>Controls<CheckBox>(shell).Any(c=>c.Content?.ToString()==Catalog.All.Single(p=>p.Id==id).Name)),"selection UI exposes all 17 patches including the new six");
  await Click("清空选择");Check(activeSelection==null&&!Controls<Button>(shell).Single(b=>b.Content?.ToString()=="检查所选补丁方案").IsEnabled,"empty UI selection cannot start installation");
  UpdateLayout();Controls<CheckBox>(shell).Single(c=>c.Content?.ToString()==Catalog.All.Single(p=>p.Id=="Act4FinalAscent").Name).IsChecked=true;
  Check(activeSelection?.Included.ToHashSet().SetEquals(["Act4FinalAscent","SpireTogetherFix"])==true,"checking Act4 automatically selects only its linked Spire update");
  UpdateLayout();var dependency=Controls<CheckBox>(shell).Single(c=>c.Content?.ToString()==Catalog.All.Single(p=>p.Id=="SpireTogetherFix").Name);
  Check(dependency.IsChecked==true&&!dependency.IsEnabled,"automatic dependency is visibly selected and cannot be detached");await Shot("01-选择与自动联动");
  await Click("检查所选补丁方案");Check(installPreview?.SelectedIds?.Length==2,"actual preview button retains two-component scope");await Shot("02-本次改动预览");
  await Click(new PlanPresentation(installPreview!).Confirm);Check(lastInstallResult?.Message=="Installed"&&lastInstallResult.SelectedIds?.Length==2,"confirmation button installs selected linked batch");await Shot("03-所选补丁完成");
  await Click("检查最近一次回滚");Check(installPreview?.Mode=="RollbackPreview","rollback preview opens for the exact latest batch");await Shot("04-撤销本次批次");
  await Click(new PlanPresentation(installPreview!).Confirm);var after=Snapshot();Check(lastInstallResult?.Message=="RolledBack"&&before.Count==after.Count&&before.All(f=>after.TryGetValue(f.Key,out var hash)&&hash==f.Value),"UI rollback restores byte-exact fixture and leaves unselected patches unchanged");await Shot("05-本次批次已撤销");
 }
}
