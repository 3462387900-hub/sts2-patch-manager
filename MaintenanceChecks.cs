using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpirePatch;
public partial class MainWindow
{
 // Real UI transaction on a marked, isolated fixture with installed payloads
 // and missing documentation. Never accepts a real game executable.
 public async Task CaptureMaintenanceAsync(string zip,string fixture,string output)
 {
  InstallerChecks.AssertFixture(fixture);Directory.CreateDirectory(output);selectedInstallRoot=fixture;
  var checks=new List<string>();void Check(bool value,string label){if(!value)throw new InvalidOperationException(label);checks.Add("PASS "+label);File.WriteAllLines(Path.Combine(output,"说明维护验证.txt"),checks);}
  Dictionary<string,string> Snapshot()=>Directory.EnumerateFiles(Path.Combine(fixture,"mods"),"*",SearchOption.AllDirectories).ToDictionary(f=>Path.GetRelativePath(fixture,f),Catalog.Hash);
  bool Equal(Dictionary<string,string> a,Dictionary<string,string> b)=>a.Count==b.Count&&a.All(x=>b.TryGetValue(x.Key,out var value)&&value==x.Value);
  async Task Shot(string name){await Task.Delay(100);UpdateLayout();var bitmap=new RenderTargetBitmap((int)shell.ActualWidth,(int)shell.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(shell);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(output,name+".png"));encoder.Save(stream);}
  IEnumerable<Button> Buttons(DependencyObject parent){for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is Button b)yield return b;foreach(var nested in Buttons(child))yield return nested;}}
  async Task Click(string label){UpdateLayout();Buttons(shell).Single(b=>b.Content?.ToString()==label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));while(busy)await Task.Delay(60);}
  var before=Snapshot();preparedPackage=await installer.PrepareAsync(zip);
  installPreview=await installer.RunAsync(preparedPackage,fixture,"Preview");var initial=installPreview;var view=new PlanPresentation(initial);
  Check(initial.Changed==40&&view.Documents.Length==40&&view.Runtime.Length==0&&view.Legacy.Length==0,"actual plan contains exactly 40 documentation additions and no payload changes");
  Check(view.MatchedPatches==17&&view.Unchanged.Length==38,"all 17 runtime patches and 38 runtime files are explicitly skipped");
  Check(Equal(before,Snapshot()),"preview leaves every fixture file unchanged");
  installStatus="检查完成：仅有说明文件需要处理。";ShowInstaller();await Shot("01-仅需补齐说明");
  await Click(view.Confirm);Check(lastInstallResult?.Message=="Installed"&&new PlanPresentation(lastInstallResult).DocumentsOnly,"documentation confirmation performs the actual verified transaction");await Shot("02-说明补齐完成");
  var after=Snapshot();Check(before.All(f=>after.TryGetValue(f.Key,out var hash)&&hash==f.Value)&&after.Count==before.Count+40,"all prior runtime files remain byte-identical after adding documentation");
  Check(Catalog.Scan(fixture).Patches.All(p=>p.Status=="已安装"),"documentation maintenance preserves all patch recommendations");
  using(var journal=JsonDocument.Parse(File.ReadAllText(Path.Combine(lastInstallResult!.Backup,"restore.json"))))
   Check(journal.RootElement.GetProperty("files").GetArrayLength()==40,"backup journal records only the 40 actual changes");
  await Click("检查安装方案");Check(installPreview?.Changed==0&&new PlanPresentation(installPreview).Unchanged.Length==78,"recheck skips every payload and documentation file without rewriting");await Shot("03-全部匹配无需处理");
  Check(!Buttons(shell).Any(b=>b.Content?.ToString()?.StartsWith("确认")==true),"no-op preview offers no misleading apply button");
  await Click("检查最近一次回滚");Check(installPreview?.Changed==40&&new PlanPresentation(installPreview).DocumentsOnly,"rollback identifies a documentation-only transaction");await Shot("04-仅撤销说明改动");
  await Click(new PlanPresentation(installPreview!).Confirm);Check(lastInstallResult?.Message=="RolledBack"&&Equal(before,Snapshot()),"documentation rollback restores exact original fixture files and preserves fixes");await Shot("05-说明撤销完成");
  // A runtime configuration is still a functional change even though it is JSON.
  var runtime=initial with{Files=[new("mods/Fix/runtime-baseline.json","write",true,"old","new")],Changed=1};
  Check(!new PlanPresentation(runtime).DocumentsOnly&&new PlanPresentation(runtime).Runtime.Length==1,"runtime JSON is never mistaken for a player document");
  var mixed=initial with{Files=initial.Files.Concat(runtime.Files).Append(new("mods/OldFix/OldFix.dll","remove",true,"old","")).ToArray(),Changed=42};
  var mixedView=new PlanPresentation(mixed);Check(!mixedView.DocumentsOnly&&mixedView.Runtime.Length==1&&mixedView.Legacy.Length==1,"mixed maintenance retains runtime and legacy removal warnings");
 }
}
