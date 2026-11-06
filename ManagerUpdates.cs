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
public enum ManagerUpdateChannel { Stable, Beta }

// This source updates the manager itself. It never executes an archive or changes game files.
public sealed class ManagerUpdates : IDisposable
{
 public const string Repository="3462387900-hub/sts2-patch-manager";
 public const string RepositoryUrl="https://github.com/"+Repository;
 public const string Api="https://api.github.com/repos/"+Repository;
 public const string FeedUrl="https://raw.githubusercontent.com/"+Repository+"/main/manager-update.json";
 public static string CurrentVersion=>Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
 // Beta 1 had no assembly informational suffix; only that known historical build uses this fallback.
 public static string CurrentReleaseVersion
 {
  get
  {
   string declared=Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion??"";
   if(Regex.IsMatch(declared,@"\A[0-9]+\.[0-9]+\.[0-9]+-beta\.[1-9][0-9]*\z"))
    return declared;
   return CurrentVersion=="0.14.0"&&declared=="0.14.0"?"0.14.0-beta.1":CurrentVersion;
  }
 }
 public const string ChannelFileName="管理器更新通道.json";
 public static ManagerUpdateChannel LoadChannel(string root)
 {
  try
  {
   string path=Path.Combine(root,ChannelFileName);
   if(!File.Exists(path))return ManagerUpdateChannel.Stable;
   using var json=JsonDocument.Parse(File.ReadAllText(path));
   return json.RootElement.TryGetProperty("channel",out var item)&&item.GetString()=="beta"
    ?ManagerUpdateChannel.Beta:ManagerUpdateChannel.Stable;
  }
  catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException)
  {return ManagerUpdateChannel.Stable;}
 }
 public static void SaveChannel(string root,ManagerUpdateChannel channel)
 {
  Directory.CreateDirectory(root);
  string path=Path.Combine(root,ChannelFileName);
  string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
  try
  {
   File.WriteAllText(temp,JsonSerializer.Serialize(new{schema=1,channel=channel==ManagerUpdateChannel.Beta?"beta":"stable"}));
   File.Move(temp,path,true);
  }
  finally{if(File.Exists(temp))File.Delete(temp);}
 }

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
 public static string CoreVersion(string version)
 {
  if(version.Contains("-beta.",StringComparison.Ordinal))
  {
   Require(Regex.IsMatch(version,@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-beta\.([1-9][0-9]*)\z"),
    "不支持的 Beta 版本号。");
   string core=version[..version.IndexOf("-beta.",StringComparison.Ordinal)];
   StableVersion(core);return core;
  }
  StableVersion(version);return version;
 }
 public static bool IsBeta(string version)=>CoreVersion(version)!=version;
 public static int CompareVersions(string candidate,string installed)
 {
  Version c=StableVersion(CoreVersion(candidate)),i=StableVersion(CoreVersion(installed));
  int core=c.CompareTo(i);
  if(core!=0)return core;
  bool cb=IsBeta(candidate),ib=IsBeta(installed);
  if(cb!=ib)return cb?-1:1; // Same core: stable beats beta.
  if(!cb)return 0;
  static int BetaSequence(string v)=>int.Parse(v[(v.IndexOf("-beta.",StringComparison.Ordinal)+6)..],
   System.Globalization.CultureInfo.InvariantCulture);
  return BetaSequence(candidate).CompareTo(BetaSequence(installed));
 }
 public static bool IsNewer(ManagerRelease release,string current)=>CompareVersions(release.Version,current)>0;
 public static bool IsNewer(ManagerRelease release)=>IsNewer(release,CurrentReleaseVersion);
 public static void Validate(ManagerRelease r)
 {
  CoreVersion(r.Version);
  Require(r.FileName==$"SpirePatch-v{r.Version}-win-x64.zip","更新包文件名与版本不一致。");
  Require(r.Size>0&&r.Size<=250L*1024*1024,"更新包大小超出支持范围。");
  Require(Regex.IsMatch(r.Sha256,@"\A[0-9a-fA-F]{64}\z"),"发布缺少有效的 SHA256 校验值，请到发布页核对。");
  Require(r.ReleaseUrl==RepositoryUrl+"/releases/tag/v"+r.Version,"更新发布页不属于指定管理器仓库。");
  Require(r.DownloadUrl==RepositoryUrl+"/releases/download/v"+r.Version+"/"+r.FileName,"更新包下载地址与发布版本不一致。");
  Require(Regex.IsMatch(r.AssetApiUrl,"\\A"+Regex.Escape(Api)+@"/releases/assets/[0-9]+\z"),"更新附件不属于指定管理器仓库。");
  Require(!string.IsNullOrWhiteSpace(r.Notes)&&r.Notes.Length<=60000,"发布缺少更新内容，或更新说明过长。");
 }
 public static ManagerRelease ParseRelease(string json,ManagerUpdateChannel channel=ManagerUpdateChannel.Stable)
 {
  using var document=JsonDocument.Parse(json.TrimStart('\uFEFF'));
  return ParseReleaseElement(document.RootElement,channel);
 }
 static ManagerRelease ParseReleaseElement(JsonElement r,ManagerUpdateChannel channel)
 {
  Require(!r.GetProperty("draft").GetBoolean(),"草稿发布不能用于更新。");
  bool prerelease=r.GetProperty("prerelease").GetBoolean();
  Require(prerelease==(channel==ManagerUpdateChannel.Beta),
   channel==ManagerUpdateChannel.Beta?"测试通道只接受已发布的 Beta。":"稳定通道不接受测试版。");
  string tag=r.GetProperty("tag_name").GetString()??"";
  Require(tag.StartsWith('v'),"发布标签格式无效。");
  string version=tag[1..];
  bool beta=IsBeta(version);
  Require(beta==prerelease,"发布标签与发布通道不匹配。");
  string name=$"SpirePatch-v{version}-win-x64.zip";
  var assets=r.GetProperty("assets").EnumerateArray()
   .Where(a=>a.GetProperty("name").GetString()==name).ToArray();
  Require(assets.Length==1,"此发布版本缺少唯一的 Windows 更新包。");
  var asset=assets[0];Require(asset.GetProperty("state").GetString()=="uploaded","更新包尚未上传完成。");
  string digest=asset.TryGetProperty("digest",out var d)?d.GetString()??"":"";
  Require(digest.StartsWith("sha256:",StringComparison.Ordinal),"GitHub 尚未提供更新包 SHA256，请稍后重试或查看发布页。");
  var result=new ManagerRelease(version,r.GetProperty("body").GetString()??"",
   r.GetProperty("published_at").GetDateTimeOffset(),name,asset.GetProperty("size").GetInt64(),
   digest[7..].ToUpperInvariant(),asset.GetProperty("browser_download_url").GetString()??"",
   asset.GetProperty("url").GetString()??"",r.GetProperty("html_url").GetString()??"");
  Validate(result);return result;
 }
 static void CheckResponse(HttpResponseMessage response)
 {
  if(response.StatusCode==HttpStatusCode.NotFound)throw new HttpRequestException("管理器仓库尚未提供此通道的可用版本。");
  if((int)response.StatusCode is 403 or 429)throw new HttpRequestException("GitHub 请求受限，请稍后重试，也可打开发布页。");
  response.EnsureSuccessStatusCode();
 }
 public async Task<ManagerRelease> CheckAsync(CancellationToken ct=default)=>
  await CheckAsync(ManagerUpdateChannel.Stable,ct);
 public async Task<ManagerRelease> CheckAsync(ManagerUpdateChannel channel,CancellationToken ct=default)
 {
  if(channel==ManagerUpdateChannel.Beta)
  {
   string json=await ReadTextAsync(Api+"/releases?per_page=30",ct,4*1024*1024);
   using var document=JsonDocument.Parse(json.TrimStart('\uFEFF'));
   Require(document.RootElement.ValueKind==JsonValueKind.Array,"测试通道返回的数据格式错误。");
   var candidates=new List<ManagerRelease>();
   foreach(var entry in document.RootElement.EnumerateArray())
   {
    if(entry.GetProperty("draft").GetBoolean()||!entry.GetProperty("prerelease").GetBoolean())continue;
    string tag=entry.GetProperty("tag_name").GetString()??"";
    if(!Regex.IsMatch(tag,@"\Av[0-9]+\.[0-9]+\.[0-9]+-beta\.[1-9][0-9]*\z"))continue;
    // A release with matching tag but missing/invalid checksum must not silently become "latest".
    candidates.Add(ParseReleaseElement(entry,ManagerUpdateChannel.Beta));
   }
   Require(candidates.Count>0,"仓库目前没有可用的 Beta 更新包。");
   return candidates.Aggregate((best,item)=>CompareVersions(item.Version,best.Version)>0?item:best);
  }
  try{return await ReadReleaseAsync(FeedUrl,ct);}
  catch(HttpRequestException){return await ReadReleaseAsync(Api+"/releases/latest",ct);}
  catch(OperationCanceledException)when(!ct.IsCancellationRequested){return await ReadReleaseAsync(Api+"/releases/latest",ct);}
 }
 async Task<ManagerRelease> ReadReleaseAsync(string url,CancellationToken ct)=>
  ParseRelease(await ReadTextAsync(url,ct,1024*1024));
 async Task<string> ReadTextAsync(string url,CancellationToken ct,int limit)
 {
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
  timeout.CancelAfter(TimeSpan.FromSeconds(15));ct=timeout.Token;
  using var request=new HttpRequestMessage(HttpMethod.Get,url);
  request.Headers.Accept.ParseAdd("application/json");
  request.Headers.CacheControl=new(){NoCache=true};
  using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
  CheckResponse(response);
  if(response.Content.Headers.ContentLength>limit)throw new InvalidDataException("更新源响应过大。");
  await using var input=await response.Content.ReadAsStreamAsync(ct);
  using var output=new MemoryStream();var buffer=new byte[16384];int n;
  while((n=await input.ReadAsync(buffer,ct))>0)
  {
   if(output.Length+n>limit)throw new InvalidDataException("更新源响应过大。");
   await output.WriteAsync(buffer.AsMemory(0,n),ct);
  }
  return System.Text.Encoding.UTF8.GetString(output.ToArray());
 }
 public async Task DownloadAsync(ManagerRelease release,string destination,IProgress<long>? progress=null,CancellationToken ct=default,bool preferAssetApi=false)
 {
  Validate(release);destination=Path.GetFullPath(destination);
  Require(Path.GetExtension(destination).Equals(".zip",StringComparison.OrdinalIgnoreCase),"请保存为 ZIP 更新包。");
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromMinutes(45));ct=timeout.Token;
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
