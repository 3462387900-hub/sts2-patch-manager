using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpirePatch;
public record ManagerRelease(string Version,string Notes,DateTimeOffset PublishedAt,string FileName,long Size,string Sha256,string DownloadUrl,string AssetApiUrl,string ReleaseUrl);

// This source updates the manager itself. It never executes an archive or changes game files.
public sealed class ManagerUpdates : IDisposable
{
 public const string Repository="3462387900-hub/sts2-patch-manager";
 public const string RepositoryUrl="https://github.com/"+Repository;
 public const string Api="https://api.github.com/repos/"+Repository;
 public const string FeedUrl="https://raw.githubusercontent.com/"+Repository+"/main/manager-update.json";
 public static string CurrentVersion=>Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
 readonly HttpClient client;
 public ManagerUpdates(HttpMessageHandler? handler=null)
 {
  client=GitHubNetwork.CreateClient(handler);client.Timeout=Timeout.InfiniteTimeSpan;
  client.DefaultRequestHeaders.UserAgent.ParseAdd("SpirePatch/"+CurrentVersion);
 }
 public void Dispose()=>client.Dispose();
 static void Require(bool condition,string reason){if(!condition)throw new InvalidDataException(reason);}
 public static Version StableVersion(string value)
 {
  Require(Regex.IsMatch(value,@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z")&&System.Version.TryParse(value,out _),"不支持的正式版本号。");
  return System.Version.Parse(value);
 }
 public static bool IsNewer(ManagerRelease release,string current)=>StableVersion(release.Version)>StableVersion(current);
 public static void Validate(ManagerRelease r)
 {
  StableVersion(r.Version);
  Require(r.FileName==$"SpirePatch-v{r.Version}-win-x64.zip","更新包文件名与版本不一致。");
  Require(r.Size>0&&r.Size<=250L*1024*1024,"更新包大小超出支持范围。");
  Require(Regex.IsMatch(r.Sha256,@"\A[0-9a-fA-F]{64}\z"),"发布缺少有效的 SHA256 校验值，请到发布页核对。");
  Require(r.ReleaseUrl==RepositoryUrl+"/releases/tag/v"+r.Version,"更新发布页不属于指定管理器仓库。");
  Require(r.DownloadUrl==RepositoryUrl+"/releases/download/v"+r.Version+"/"+r.FileName,"更新包下载地址与发布版本不一致。");
  Require(Regex.IsMatch(r.AssetApiUrl,"\\A"+Regex.Escape(Api)+@"/releases/assets/[0-9]+\z"),"更新附件不属于指定管理器仓库。");
  Require(!string.IsNullOrWhiteSpace(r.Notes)&&r.Notes.Length<=60000,"发布缺少更新内容，或更新说明过长。");
 }
 public static ManagerRelease ParseRelease(string json)
 {
  using var document=JsonDocument.Parse(json.TrimStart('\uFEFF'));var r=document.RootElement;
  Require(!r.GetProperty("draft").GetBoolean()&&!r.GetProperty("prerelease").GetBoolean(),"仅支持正式发布，测试版不参与更新。");
  string tag=r.GetProperty("tag_name").GetString()??"";Require(tag.StartsWith('v'),"发布标签格式无效。");string version=tag[1..];StableVersion(version);
  string name=$"SpirePatch-v{version}-win-x64.zip";
  var assets=r.GetProperty("assets").EnumerateArray().Where(a=>a.GetProperty("name").GetString()==name).ToArray();
  Require(assets.Length==1,"正式发布缺少唯一的 Windows 更新包。");var asset=assets[0];
  Require(asset.GetProperty("state").GetString()=="uploaded","更新包尚未上传完成。");
  string digest=asset.TryGetProperty("digest",out var d)?d.GetString()??"":"";
  Require(digest.StartsWith("sha256:",StringComparison.Ordinal),"GitHub 尚未提供更新包 SHA256，请稍后重试或查看发布页。");
  var result=new ManagerRelease(version,r.GetProperty("body").GetString()??"",r.GetProperty("published_at").GetDateTimeOffset(),name,asset.GetProperty("size").GetInt64(),digest[7..].ToUpperInvariant(),asset.GetProperty("browser_download_url").GetString()??"",asset.GetProperty("url").GetString()??"",r.GetProperty("html_url").GetString()??"");
  Validate(result);return result;
 }
 static void CheckResponse(HttpResponseMessage response)
 {
  if(response.StatusCode==HttpStatusCode.NotFound)throw new HttpRequestException("管理器仓库尚未提供可用的正式发布。");
  if((int)response.StatusCode is 403 or 429)throw new HttpRequestException("GitHub 请求受限，请稍后重试，也可打开发布页。");
  response.EnsureSuccessStatusCode();
 }
 public async Task<ManagerRelease> CheckAsync(CancellationToken ct=default)
 {
  try{return await ReadReleaseAsync(FeedUrl,ct);}
  catch(HttpRequestException){return await ReadReleaseAsync(Api+"/releases/latest",ct);}
  catch(OperationCanceledException)when(!ct.IsCancellationRequested){return await ReadReleaseAsync(Api+"/releases/latest",ct);}
 }
 async Task<ManagerRelease> ReadReleaseAsync(string url,CancellationToken ct)
 {
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(15));ct=timeout.Token;
  using var request=new HttpRequestMessage(HttpMethod.Get,url);request.Headers.Accept.ParseAdd("application/json");
  request.Headers.CacheControl=new(){NoCache=true};
  using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);CheckResponse(response);
  const int limit=1024*1024;if(response.Content.Headers.ContentLength>limit)throw new InvalidDataException("更新源响应过大。");
  await using var input=await response.Content.ReadAsStreamAsync(ct);using var output=new MemoryStream();var buffer=new byte[16384];int n;
  while((n=await input.ReadAsync(buffer,ct))>0){if(output.Length+n>limit)throw new InvalidDataException("更新源响应过大。");await output.WriteAsync(buffer.AsMemory(0,n),ct);}
  return ParseRelease(System.Text.Encoding.UTF8.GetString(output.ToArray()));
 }
 public async Task DownloadAsync(ManagerRelease release,string destination,IProgress<long>? progress=null,CancellationToken ct=default,bool preferAssetApi=false)
 {
  Validate(release);destination=Path.GetFullPath(destination);
  Require(Path.GetExtension(destination).Equals(".zip",StringComparison.OrdinalIgnoreCase),"请保存为 ZIP 更新包。");
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromMinutes(10));ct=timeout.Token;
  string temporary=destination+"."+Guid.NewGuid().ToString("N")+".part";
  try{
   using var response=await OpenDownloadAsync(release,ct,preferAssetApi);
   if(response.RequestMessage?.RequestUri is { Scheme: not "https" })throw new InvalidDataException("更新下载必须使用 HTTPS。");
   if(response.Content.Headers.ContentLength is long size&&size!=release.Size)throw new InvalidDataException("更新包长度与发布记录不一致。");
   await using(var input=await response.Content.ReadAsStreamAsync(ct))
   await using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true)){
    using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);long total=0;var buffer=new byte[65536];int n;
    while((n=await input.ReadAsync(buffer,ct))>0){total+=n;if(total>release.Size)throw new InvalidDataException("更新包超出预期大小。");hash.AppendData(buffer,0,n);await output.WriteAsync(buffer.AsMemory(0,n),ct);progress?.Report(total);}
    Require(total==release.Size&&Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256,StringComparison.OrdinalIgnoreCase),"更新包校验失败，未保留损坏文件。");
   }
   ct.ThrowIfCancellationRequested();File.Move(temporary,destination,true);
  }finally{if(File.Exists(temporary))File.Delete(temporary);}
 }
 async Task<HttpResponseMessage> OpenDownloadAsync(ManagerRelease release,CancellationToken ct,bool preferAssetApi)
 {
  async Task<HttpResponseMessage> Open(string url){
   using var connection=CancellationTokenSource.CreateLinkedTokenSource(ct);connection.CancelAfter(TimeSpan.FromSeconds(30));
   using var request=new HttpRequestMessage(HttpMethod.Get,url);request.Headers.Accept.ParseAdd("application/octet-stream");
   var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,connection.Token);
   try{CheckResponse(response);return response;}catch{response.Dispose();throw;}
  }
  string first=preferAssetApi?release.AssetApiUrl:release.DownloadUrl;
  string second=preferAssetApi?release.DownloadUrl:release.AssetApiUrl;
  try{return await Open(first);}
  catch(HttpRequestException){return await Open(second);}
  catch(OperationCanceledException)when(!ct.IsCancellationRequested){return await Open(second);}
 }
}
