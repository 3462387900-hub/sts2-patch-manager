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
 internal static ManagerRelease Sample(string version="0.9.0")=>new(version,"新增：模组批量查看。\n改进：更新说明展示。\n修复：示例问题。\n\n此页面为隔离测试，不是已发布版本。",DateTimeOffset.Parse("2026-11-02T00:00:00Z"),$"SpirePatch-v{version}-win-x64.zip",Payload.Length,Convert.ToHexString(SHA256.HashData(Payload)),ManagerUpdates.RepositoryUrl+$"/releases/download/v{version}/SpirePatch-v{version}-win-x64.zip",ManagerUpdates.Api+"/releases/assets/1",ManagerUpdates.RepositoryUrl+"/releases/tag/v"+version);
 internal static string Json(ManagerRelease release,bool draft=false,bool prerelease=false,string? digest=null)=>JsonSerializer.Serialize(new{draft,prerelease,tag_name="v"+release.Version,body=release.Notes,published_at=release.PublishedAt,html_url=release.ReleaseUrl,assets=new[]{new{name=release.FileName,state="uploaded",size=release.Size,digest=digest??"sha256:"+release.Sha256,browser_download_url=release.DownloadUrl,url=release.AssetApiUrl}}});
 internal static HttpResponseMessage Reply(string json)=>new(HttpStatusCode.OK){Content=new StringContent(json)};
 public static async Task RunAsync(string output)
 {
  Directory.CreateDirectory(Path.GetDirectoryName(output)!);var lines=new List<string>();void Check(bool yes,string text){if(!yes)throw new InvalidOperationException(text);lines.Add("PASS "+text);File.WriteAllLines(output,lines);}
  var sample=Sample();var parsed=ManagerUpdates.ParseRelease(Json(sample));Check(parsed==sample,"release metadata retains version, Chinese notes, date and checksum");
  Check(ManagerUpdates.ParseRelease("\uFEFF"+Json(sample))==sample,"public index accepts UTF-8 BOM from Windows PowerShell");
  Check(ManagerUpdates.IsNewer(Sample("0.10.0"),"0.9.0"),"version comparison is numeric rather than lexical");
  Check(!ManagerUpdates.IsNewer(sample,sample.Version)&&!ManagerUpdates.IsNewer(sample,"1.0.0"),"equal and older releases never request downgrade");
  foreach(var version in new[]{"0.8.0-beta","0.8","../0.8.0","v0.8.0","00.8.0"}){try{ManagerUpdates.StableVersion(version);throw new Exception("bad version accepted");}catch(InvalidDataException){}}
  Check(true,"invalid, prerelease and path-like versions rejected");
  foreach(var json in new[]{Json(sample,true),Json(sample,false,true),Json(sample,digest:""),Json(sample with{Notes=""})}){try{ManagerUpdates.ParseRelease(json);throw new Exception("bad release accepted");}catch(InvalidDataException){}}
  Check(true,"draft, prerelease, missing checksum and missing release notes rejected");
  var betaOne=Sample("0.14.0-beta.1");
  var betaTwo=Sample("0.14.1-beta.1");
  var latestBeta=Sample("0.14.2-beta.1");
  Check(ManagerUpdates.ParseRelease(Json(betaOne,prerelease:true),ManagerUpdateChannel.Beta)==betaOne,
   "Beta prereleases are accepted only when explicitly selecting the Beta channel");
  foreach(var badBeta in new[]{Json(betaOne),Json(betaOne,draft:true,prerelease:true)})
  {try{ManagerUpdates.ParseRelease(badBeta,ManagerUpdateChannel.Beta);throw new Exception("invalid beta accepted");}catch(InvalidDataException){}}
  Check(true,"Beta channel rejects stable releases and drafts");
  Check(ManagerUpdates.CompareVersions(betaTwo.Version,betaOne.Version)>0&&
   ManagerUpdates.CompareVersions("0.14.1","0.14.1-beta.1")>0&&
   ManagerUpdates.CompareVersions("0.14.0-beta.2",betaOne.Version)>0&&
   ManagerUpdates.CompareVersions("0.13.1",betaOne.Version)<0,
   "beta/stable ordering follows numeric core and beta sequence");
  Check(ManagerUpdates.CoreVersion(betaTwo.Version)=="0.14.1"&&
   ManagerUpdates.IsBeta(betaTwo.Version)&&!ManagerUpdates.IsBeta("0.14.1"),
   "Beta labels have a trusted three-part assembly core");
  string betaList="["+Json(betaTwo,prerelease:true)+","+Json(sample)+","+
   Json(latestBeta,prerelease:true)+","+Json(betaOne,prerelease:true)+"]";
  int betaRequests=0;
  using(var betaSource=new ManagerUpdates(new Handler(request=>{
   betaRequests++;Check(request.RequestUri!.AbsolutePath.EndsWith("/releases")&&
    request.RequestUri.Query.Contains("per_page=",StringComparison.Ordinal)&&
    request.Headers.Authorization==null,"Beta discovery uses unauthenticated official Release list");
   return Reply(betaList);
  })))
  {
   Check(await betaSource.CheckAsync(ManagerUpdateChannel.Beta)==latestBeta&&betaRequests==1,
    "Beta channel chooses the highest valid prerelease version, not stable/latest endpoint");
  }
  string preferenceRoot=Path.Combine(Path.GetDirectoryName(output)!,"channel-fixture-"+Guid.NewGuid().ToString("N"));
  Check(ManagerUpdates.LoadChannel(preferenceRoot)==ManagerUpdateChannel.Stable,
   "fresh manager uses stable update channel by default");
  ManagerUpdates.SaveChannel(preferenceRoot,ManagerUpdateChannel.Beta);
  Check(ManagerUpdates.LoadChannel(preferenceRoot)==ManagerUpdateChannel.Beta,
   "Beta update channel persists independently from game and MOD files");
  ManagerUpdates.SaveChannel(preferenceRoot,ManagerUpdateChannel.Stable);
  Check(ManagerUpdates.LoadChannel(preferenceRoot)==ManagerUpdateChannel.Stable,
   "switching back to stable saves the preferred channel");
  foreach(var bad in new[]{"0.14.1-beta.0","0.14.1-beta.01","0.14.1-rc.1","0.14.1-beta.99999999999999999"})
  {try{ManagerUpdates.CoreVersion(bad);throw new Exception("bad Beta tag accepted");}catch(InvalidDataException){}}
  Check(true,"invalid prerelease labels and oversized numeric segments are rejected");
  foreach(var bad in new[]{sample with{ReleaseUrl="https://example.com/release"},sample with{AssetApiUrl=ManagerUpdates.Api+"/../assets/1"},sample with{DownloadUrl="https://example.com/other.zip"},sample with{FileName="../bad.zip"},sample with{Size=300_000_000},sample with{Sha256="bad"}}){try{ManagerUpdates.Validate(bad);throw new Exception("bad source accepted");}catch(InvalidDataException){}}
  Check(true,"foreign sources, changed filenames, invalid hashes and oversized assets rejected");
  int checks=0,downloads=0;bool anonymous=true;
  using(var source=new ManagerUpdates(new Handler(r=>{anonymous&=r.Headers.Authorization==null;if((r.RequestUri!.AbsolutePath.EndsWith("/latest")||r.RequestUri.AbsolutePath.EndsWith("/manager-update.json"))){checks++;return Reply(Json(sample));}downloads++;return new(HttpStatusCode.OK){Content=new ByteArrayContent(Payload)};}))){await source.CheckAsync();Check(checks==1&&downloads==0&&anonymous,"checking is anonymous and never downloads the update package");}
  foreach(var code in new[]{HttpStatusCode.NotFound,HttpStatusCode.Forbidden,HttpStatusCode.TooManyRequests}){using var source=new ManagerUpdates(new Handler(_=>new(code)));try{await source.CheckAsync();throw new Exception("HTTP error accepted");}catch(HttpRequestException){}}
  Check(true,"no release and GitHub rate limits are errors rather than latest-version claims");
  using(var source=new ManagerUpdates(new Handler(_=>Reply(new string('x',1024*1024+1))))){try{await source.CheckAsync();throw new Exception("oversized response accepted");}catch(InvalidDataException){}}
  Check(true,"oversized metadata rejected before parsing");
  using(var source=new ManagerUpdates(new Handler(r=>r.RequestUri!.AbsolutePath.EndsWith("/manager-update.json")?new(HttpStatusCode.NotFound):Reply(Json(sample))))){Check(await source.CheckAsync()==sample,"unavailable public index falls back to official release API");}
  int fallbackRequests=0;using(var source=new ManagerUpdates(new Handler(_=>{fallbackRequests++;return Reply(Json(sample with{Sha256="invalid"}));}))){try{await source.CheckAsync();throw new Exception("invalid feed accepted");}catch(InvalidDataException){}Check(fallbackRequests==1,"invalid public index is rejected rather than masked by another source");}
  string directory=Path.Combine(Path.GetDirectoryName(output)!,"更新隔离测试-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);string destination=Path.Combine(directory,"更新包.zip");
  using(var source=new ManagerUpdates(new Handler(r=>{anonymous&=r.Headers.Authorization==null;return new(HttpStatusCode.OK){Content=new ByteArrayContent(Payload)};}))){await source.DownloadAsync(sample,destination);Check(File.ReadAllBytes(destination).SequenceEqual(Payload)&&anonymous,"explicit download saves exact bytes after checksum verification without login");}
  int downloadRequests=0;using(var source=new ManagerUpdates(new Handler(r=>{downloadRequests++;return r.RequestUri!.Host=="github.com"?new(HttpStatusCode.Forbidden):new(HttpStatusCode.OK){Content=new ByteArrayContent(Payload)};}))){await source.DownloadAsync(sample,destination);Check(downloadRequests==2&&File.ReadAllBytes(destination).SequenceEqual(Payload),"unavailable direct download falls back to asset API and verifies bytes");}
  var downloadHosts=new List<string>();
  using(var alternate=new ManagerUpdates(new Handler(r=>{
   downloadHosts.Add(r.RequestUri!.Host);
   return new(HttpStatusCode.OK){Content=new ByteArrayContent(Payload)};
  })))
  {
   await alternate.DownloadAsync(sample,destination,ct:default,preferAssetApi:true);
   Check(downloadHosts.SequenceEqual(["api.github.com"])&&File.ReadAllBytes(destination).SequenceEqual(Payload),
    "manual alternate channel starts with official asset API and verifies the same SHA256");
  }
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
  int downloads=0;bool failure=false;string version="0.9.0";
  // Test source is injected only into the offscreen window, never the normal app.
  using var source=new ManagerUpdates(new UpdateChecks.Handler(r=>{if(failure)return new(System.Net.HttpStatusCode.Forbidden);if((r.RequestUri!.AbsolutePath.EndsWith("/latest")||r.RequestUri.AbsolutePath.EndsWith("/manager-update.json")))return UpdateChecks.Reply(UpdateChecks.Json(UpdateChecks.Sample(version)));downloads++;return new(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.ByteArrayContent(UpdateChecks.Payload)};}));
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
  ShowManagerUpdates();await Idle();Check(Controls<Button>(shell).Any(b=>b.Content?.ToString()=="下载并更新")&&Controls<TextBlock>(shell).Any(t=>t.Text.Contains("新增：模组批量查看")),"new release displays notes and an explicit download choice");
  Controls<Button>(shell).Single(b=>b.Content?.ToString()=="暂不更新").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(currentVisit?.Key=="总览"&&downloads()==0,"declining update returns without downloads or writes");
  ShowManagerUpdates();await DownloadManagerUpdateToAsync(managerRelease!,Path.Combine(output,"界面隔离下载.zip"));Check(managerDownload!=null&&downloads()==1&&File.ReadAllBytes(managerDownload).SequenceEqual(UpdateChecks.Payload),"download workflow saves verified package and never executes it");
  setVersion(ManagerUpdates.CurrentVersion);await CheckManagerUpdatesAsync(true);await Idle();Check(managerUpdateStatus.Contains("最新正式版本")&&!Controls<Button>(shell).Any(b=>b.Content?.ToString()=="下载并更新"),"current release hides update download action");
  setVersion("0.6.0");await CheckManagerUpdatesAsync(true);Check(managerUpdateStatus.Contains("无需降级"),"older release cannot offer downgrade");
  fail();await CheckManagerUpdatesAsync(true);await Idle();Check(managerRelease==null&&managerUpdateStatus.Contains("未完成")&&!Controls<Button>(shell).Any(b=>b.Content?.ToString()=="下载并更新"),"failed refresh clears stale release and disables downloads");
 }
}
