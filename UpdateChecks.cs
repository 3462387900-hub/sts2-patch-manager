using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SpirePatch;
public static class UpdateChecks
{
 internal sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
 {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){ct.ThrowIfCancellationRequested();return Task.FromResult(respond(request));}}
 internal static readonly byte[] Payload=Encoding.UTF8.GetBytes("isolated updater download test; never executed");
 internal static ManagerRelease Sample(string version="0.8.0")=>new(version,"新增：模组批量查看。\n改进：更新说明展示。\n修复：示例问题。\n\n此页面为隔离测试，不是已发布版本。",DateTimeOffset.Parse("2026-11-02T00:00:00Z"),$"SpirePatch-v{version}-win-x64.zip",Payload.Length,Convert.ToHexString(SHA256.HashData(Payload)),ManagerUpdates.RepositoryUrl+$"/releases/download/v{version}/SpirePatch-v{version}-win-x64.zip",ManagerUpdates.Api+"/releases/assets/1",ManagerUpdates.RepositoryUrl+"/releases/tag/v"+version);
 internal static string Json(ManagerRelease release,bool draft=false,bool prerelease=false,string? digest=null)=>JsonSerializer.Serialize(new{draft,prerelease,tag_name="v"+release.Version,body=release.Notes,published_at=release.PublishedAt,html_url=release.ReleaseUrl,assets=new[]{new{name=release.FileName,state="uploaded",size=release.Size,digest=digest??"sha256:"+release.Sha256,browser_download_url=release.DownloadUrl,url=release.AssetApiUrl}}});
 internal static HttpResponseMessage Reply(string json)=>new(HttpStatusCode.OK){Content=new StringContent(json)};
 public static async Task RunAsync(string output)
 {
  Directory.CreateDirectory(Path.GetDirectoryName(output)!);var lines=new List<string>();void Check(bool yes,string text){if(!yes)throw new InvalidOperationException(text);lines.Add("PASS "+text);File.WriteAllLines(output,lines);}
  var sample=Sample();var parsed=ManagerUpdates.ParseRelease(Json(sample));Check(parsed==sample,"release metadata retains version, Chinese notes, date and checksum");
  Check(ManagerUpdates.IsNewer(Sample("0.10.0"),"0.9.0"),"version comparison is numeric rather than lexical");
  Check(!ManagerUpdates.IsNewer(sample,"0.8.0")&&!ManagerUpdates.IsNewer(sample,"0.9.0"),"equal and older releases never request downgrade");
  foreach(var version in new[]{"0.8.0-beta","0.8","../0.8.0","v0.8.0","00.8.0"}){try{ManagerUpdates.StableVersion(version);throw new Exception("bad version accepted");}catch(InvalidDataException){}}
  Check(true,"invalid, prerelease and path-like versions rejected");
  foreach(var json in new[]{Json(sample,true),Json(sample,false,true),Json(sample,digest:""),Json(sample with{Notes=""})}){try{ManagerUpdates.ParseRelease(json);throw new Exception("bad release accepted");}catch(InvalidDataException){}}
  Check(true,"draft, prerelease, missing checksum and missing release notes rejected");
  foreach(var bad in new[]{sample with{ReleaseUrl="https://example.com/release"},sample with{AssetApiUrl=ManagerUpdates.Api+"/../assets/1"},sample with{DownloadUrl="https://example.com/other.zip"},sample with{FileName="../bad.zip"},sample with{Size=300_000_000},sample with{Sha256="bad"}}){try{ManagerUpdates.Validate(bad);throw new Exception("bad source accepted");}catch(InvalidDataException){}}
  Check(true,"foreign sources, changed filenames, invalid hashes and oversized assets rejected");
  int checks=0,downloads=0;bool anonymous=true;
  using(var source=new ManagerUpdates(new Handler(r=>{anonymous&=r.Headers.Authorization==null;if(r.RequestUri!.AbsolutePath.EndsWith("/latest")){checks++;return Reply(Json(sample));}downloads++;return new(HttpStatusCode.OK){Content=new ByteArrayContent(Payload)};}))){await source.CheckAsync();Check(checks==1&&downloads==0&&anonymous,"checking is anonymous and never downloads the update package");}
  foreach(var code in new[]{HttpStatusCode.NotFound,HttpStatusCode.Forbidden,HttpStatusCode.TooManyRequests}){using var source=new ManagerUpdates(new Handler(_=>new(code)));try{await source.CheckAsync();throw new Exception("HTTP error accepted");}catch(HttpRequestException){}}
  Check(true,"no release and GitHub rate limits are errors rather than latest-version claims");
  using(var source=new ManagerUpdates(new Handler(_=>Reply(new string('x',1024*1024+1))))){try{await source.CheckAsync();throw new Exception("oversized response accepted");}catch(InvalidDataException){}}
  Check(true,"oversized metadata rejected before parsing");
  string directory=Path.Combine(Path.GetDirectoryName(output)!,"更新隔离测试-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);string destination=Path.Combine(directory,"更新包.zip");
  using(var source=new ManagerUpdates(new Handler(r=>{anonymous&=r.Headers.Authorization==null;return new(HttpStatusCode.OK){Content=new ByteArrayContent(Payload)};}))){await source.DownloadAsync(sample,destination);Check(File.ReadAllBytes(destination).SequenceEqual(Payload)&&anonymous,"explicit download saves exact bytes after checksum verification without login");}
  File.WriteAllText(destination,"keep existing file");var corrupt=(byte[])Payload.Clone();corrupt[0]^=1;
  foreach(var bytes in new[]{corrupt,Payload[..^1],Payload.Concat(new byte[]{0}).ToArray()}){using var source=new ManagerUpdates(new Handler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)}));try{await source.DownloadAsync(sample,destination);throw new Exception("bad download accepted");}catch(InvalidDataException){}Check(File.ReadAllText(destination)=="keep existing file"&&!Directory.EnumerateFiles(directory,"*.part").Any(),"corrupt or wrong-length download preserves existing destination and removes partial data");}
  using(var source=new ManagerUpdates(new Handler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(Payload)}))){using var cancellation=new CancellationTokenSource();cancellation.Cancel();try{await source.DownloadAsync(sample,destination,ct:cancellation.Token);throw new Exception("cancel ignored");}catch(OperationCanceledException){}Check(File.ReadAllText(destination)=="keep existing file"&&!Directory.EnumerateFiles(directory,"*.part").Any(),"cancellation preserves existing file and removes temporary download");}
  using(var source=new ManagerUpdates(new Handler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(Payload)}))){try{await source.DownloadAsync(sample,Path.Combine(directory,"program.exe"));throw new Exception("non-zip accepted");}catch(InvalidDataException){}Check(!File.Exists(Path.Combine(directory,"program.exe")),"download cannot be saved as an executable");}
 }
}

public partial class MainWindow
{
 public async Task CaptureUpdateChecksAsync(string output)
 {
  Directory.CreateDirectory(output);var lines=new List<string>();void Check(bool ok,string text){if(!ok)throw new InvalidOperationException(text);lines.Add("PASS "+text);File.WriteAllLines(Path.Combine(output,"更新界面验证.txt"),lines);}
  IEnumerable<TControl> Controls<TControl>(DependencyObject parent) where TControl:DependencyObject {for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is TControl control)yield return control;foreach(var nested in Controls<TControl>(child))yield return nested;}}
  async Task Idle(){await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);UpdateLayout();}
  async Task Shot(string name){await Idle();var image=new RenderTargetBitmap((int)shell.ActualWidth,(int)shell.ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(shell);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(output,name+".png"));encoder.Save(file);}
  int downloads=0;bool failure=false;string version="0.8.0";
  // Test source is injected only into the offscreen window, never the normal app.
  using var source=new ManagerUpdates(new UpdateChecks.Handler(r=>{if(failure)return new(System.Net.HttpStatusCode.Forbidden);if(r.RequestUri!.AbsolutePath.EndsWith("/latest"))return UpdateChecks.Reply(UpdateChecks.Json(UpdateChecks.Sample(version)));downloads++;return new(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.ByteArrayContent(UpdateChecks.Payload)};}));
  // Use a test window so production source remains readonly and fixed.
  var test=new MainWindow(false,source){Left=-15000,Top=-15000,ShowActivated=false};test.Show();
  try{await test.RunUpdateUiScenario(output,()=>downloads,v=>version=v,()=>failure=true);}finally{test.Close();}
  Check(File.Exists(Path.Combine(output,"更新场景验证.txt")),"offscreen update scenarios completed without launching a game");
  managerRelease=UpdateChecks.Sample();managerUpdateStatus="发现新版本，可以先查看更新内容，再决定是否下载。";ShowManagerUpdates();await Shot("01-发现新版与更新内容");
 }
 async Task RunUpdateUiScenario(string output,Func<int> downloads,Action<string> setVersion,Action fail)
 {
  var lines=new List<string>();void Check(bool ok,string text){if(!ok)throw new InvalidOperationException(text);lines.Add("PASS "+text);File.WriteAllLines(Path.Combine(output,"更新场景验证.txt"),lines);}
  IEnumerable<TControl> Controls<TControl>(DependencyObject parent) where TControl:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var c=VisualTreeHelper.GetChild(parent,i);if(c is TControl control)yield return control;foreach(var nested in Controls<TControl>(c))yield return nested;}}
  async Task Idle(){await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);UpdateLayout();}
  ShowOverview();await CheckManagerUpdatesAsync(false);Check(currentVisit?.Key=="总览"&&downloads()==0,"startup check stays on current page and does not download");
  ShowManagerUpdates();await Idle();Check(Controls<Button>(shell).Any(b=>b.Content?.ToString()=="下载更新包")&&Controls<TextBlock>(shell).Any(t=>t.Text.Contains("新增：模组批量查看")),"new release displays notes and an explicit download choice");
  Controls<Button>(shell).Single(b=>b.Content?.ToString()=="暂不更新").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(currentVisit?.Key=="总览"&&downloads()==0,"declining update returns without downloads or writes");
  ShowManagerUpdates();await DownloadManagerUpdateToAsync(managerRelease!,Path.Combine(output,"界面隔离下载.zip"));Check(managerDownload!=null&&downloads()==1&&File.ReadAllBytes(managerDownload).SequenceEqual(UpdateChecks.Payload),"download workflow saves verified package and never executes it");
  setVersion(ManagerUpdates.CurrentVersion);await CheckManagerUpdatesAsync(true);await Idle();Check(managerUpdateStatus.Contains("最新正式版本")&&!Controls<Button>(shell).Any(b=>b.Content?.ToString()=="下载更新包"),"current release hides update download action");
  setVersion("0.6.0");await CheckManagerUpdatesAsync(true);Check(managerUpdateStatus.Contains("无需降级"),"older release cannot offer downgrade");
  fail();await CheckManagerUpdatesAsync(true);await Idle();Check(managerRelease==null&&managerUpdateStatus.Contains("未完成")&&!Controls<Button>(shell).Any(b=>b.Content?.ToString()=="下载更新包"),"failed refresh clears stale release and disables downloads");
 }
}
