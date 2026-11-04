using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpirePatch;
public record OnlinePatch(string Id,string ComponentId,string Title,string Summary,string Detail,string[] Limits,JsonElement Metadata);
public record OnlineBundle(string FileName,string Sha256,long Size,string DownloadUrl,string AssetApiUrl,string ReleaseUrl,string InstallMode="manual_bundle");
public record OnlineCatalog(string Commit,IReadOnlyList<OnlinePatch> Patches,OnlineBundle Bundle,DateTimeOffset RetrievedAt);

public sealed class OnlineSource : IDisposable
{
 public const string Repository="3462387900-hub/sts2-patches";
 public const string RepositoryUrl="https://github.com/"+Repository;
 const string Api="https://api.github.com/repos/"+Repository;
 readonly HttpClient client;
 public OnlineSource(HttpMessageHandler? handler=null)
 {
  client=handler==null?new HttpClient():new HttpClient(handler);
  client.Timeout=TimeSpan.FromSeconds(30);
  client.DefaultRequestHeaders.UserAgent.ParseAdd("SpirePatch/"+ManagerUpdates.CurrentVersion);
  // Public repository: no embedded or locally harvested access token.
 }
 public void Dispose()=>client.Dispose();
 static string Str(JsonElement x,string key)=>x.GetProperty(key).GetString()??throw new InvalidDataException("缺少字段："+key);
 static void Require(bool condition,string reason){if(!condition)throw new InvalidDataException(reason);}
 public static void ValidateBundle(OnlineBundle b)
 {
  Require(Regex.IsMatch(b.FileName,@"^STS2-[A-Za-z0-9._-]+\.zip$"),"不支持的下载文件名。");
  Require(Regex.IsMatch(b.Sha256,@"\A[0-9a-fA-F]{64}\z"),"下载校验值无效。");
  Require(b.Size>0&&b.Size<=50*1024*1024,"下载大小超出支持范围。");
  var release=new Uri(b.ReleaseUrl);var url=new Uri(b.DownloadUrl);
  Require(release.Scheme=="https"&&release.Host=="github.com"&&release.AbsolutePath.StartsWith("/"+Repository+"/releases/tag/",StringComparison.Ordinal)&&release.Query==""&&release.Fragment=="","发布页不属于指定补丁仓库。");
  string tag=release.AbsolutePath.Split('/').Last();
  Require(Regex.IsMatch(tag,@"\A[A-Za-z0-9._-]+\z")&&b.DownloadUrl==RepositoryUrl+"/releases/download/"+tag+"/"+b.FileName,"下载地址与发布版本不一致。");
  Require(Regex.IsMatch(b.AssetApiUrl,"\\A"+Regex.Escape(Api)+@"/releases/assets/[0-9]+\z"),"附件地址不属于指定补丁仓库。");
 }
 async Task<byte[]> GetBytes(string url,int limit,CancellationToken ct,string accept="application/vnd.github+json")
 {
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(30));ct=timeout.Token;
  using var request=new HttpRequestMessage(HttpMethod.Get,url);request.Headers.Accept.ParseAdd(accept);
  using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
  if((int)response.StatusCode is 403 or 429)throw new HttpRequestException("GitHub 请求受限，请稍后重试；仍可打开仓库网页下载。");
  response.EnsureSuccessStatusCode();
  if(response.Content.Headers.ContentLength>limit)throw new InvalidDataException("补丁源响应过大。");
  await using var input=await response.Content.ReadAsStreamAsync(ct);using var output=new MemoryStream();
  var buffer=new byte[16384];int n;
  while((n=await input.ReadAsync(buffer,ct))>0){if(output.Length+n>limit)throw new InvalidDataException("补丁源响应过大。");await output.WriteAsync(buffer.AsMemory(0,n),ct);}
  return output.ToArray();
 }
 async Task<JsonElement> Contents(string path,string reference,CancellationToken ct)
 {
  try
  {
   var raw=await GetBytes("https://raw.githubusercontent.com/"+Repository+"/"+reference+"/"+path,1024*1024,ct,"application/json");
   using var rawDocument=JsonDocument.Parse(raw);return rawDocument.RootElement.Clone();
  }
  catch(HttpRequestException){}
  catch(OperationCanceledException) when(!ct.IsCancellationRequested){}
  var bytes=await GetBytes(Api+"/contents/"+path+"?ref="+reference,2*1024*1024,ct);
  using var envelope=JsonDocument.Parse(bytes);var e=envelope.RootElement;
  Require(Str(e,"encoding")=="base64","不支持的仓库文件编码。");
  using var document=JsonDocument.Parse(Convert.FromBase64String(Str(e,"content")));return document.RootElement.Clone();
 }
 public async Task<OnlineCatalog> LoadAsync(CancellationToken ct=default,bool candidate=false)
 {
  var feed=await Contents(candidate?"manager-feed-v3.json":"manager-feed.json","main",ct);
  Require(feed.GetProperty("schema_version").GetInt32()==1&&Str(feed,"repository")==Repository,"不支持的补丁源。");
  string commit=Str(feed,"catalog_commit");Require(Regex.IsMatch(commit,@"\A[0-9a-f]{40}\z"),"补丁目录必须固定到提交版本。");
  var b=feed.GetProperty("bundle");var bundle=new OnlineBundle(Str(b,"file_name"),Str(b,"sha256"),b.GetProperty("size").GetInt64(),Str(b,"download_url"),Str(b,"asset_api_url"),Str(b,"release_url"),Str(b,"install_mode"));ValidateBundle(bundle);
  Require(Str(b,"install_mode") is "manual_bundle" or "manager_payload","补丁源使用了本版不支持的安装协议，请更新管理器。");
  var index=await Contents("index.json",commit,ct);Require(Str(index,"schema_version")=="1.0","不支持的索引格式。");
  var entries=index.GetProperty("entries").EnumerateArray().ToArray();Require(entries.Length>0&&entries.Length<=100,"补丁目录条目数异常。");
  var ids=new HashSet<string>();var components=new HashSet<string>();
  foreach(var entry in entries)
  {
   string cid=Str(entry,"component_id"),id=Str(entry,"id");
   Require(Regex.IsMatch(cid,@"\A[A-Za-z0-9._-]+\z")&&cid!="."&&cid!=".."&&components.Add(cid)&&ids.Add(id),"重复或无效的补丁标识。");
   Require(Str(entry,"metadata")==$"patches/{cid}/patch-info/patch.json","补丁说明路径无效。");
  }
  using var gate=new SemaphoreSlim(3);
  var tasks=entries.Select(async entry=>{
   await gate.WaitAsync(ct);
   try{
    var m=await Contents(Str(entry,"metadata"),commit,ct);
    Require(Str(m,"schema_version")=="1.0"&&Str(m,"id")==Str(entry,"id"),"补丁说明与索引不一致。");
    Require(Str(m,"publication_status") is "draft" or "published" or "withdrawn","无效的发布状态。");
    ValidateDisplayMetadata(m);
    string detail=Str(m.GetProperty("issue").GetProperty("diagnosis"),"conclusion");
    string[] limits=m.GetProperty("limitations").EnumerateArray().Select(x=>x.GetString()??"").ToArray();
    return new OnlinePatch(Str(entry,"id"),Str(entry,"component_id"),Str(m,"title"),Str(m,"summary"),detail,limits,m);
   }finally{gate.Release();}
  });
  return new(commit,await Task.WhenAll(tasks),bundle,DateTimeOffset.Now);
 }
 static void ValidateDisplayMetadata(JsonElement m)
 {
  foreach(var c in m.GetProperty("components").EnumerateArray()){Str(c,"display_name");Str(c,"role");Str(c,"explanation");}
  foreach(var p in m.GetProperty("compatibility").GetProperty("profiles").EnumerateArray())
  {Str(p,"game_version_display");foreach(var r in p.GetProperty("required_mods").EnumerateArray()){Str(r,"mod_id");Str(r,"version_display");}}
  foreach(var f in m.GetProperty("detection").GetProperty("installed_fingerprints").EnumerateArray()){Str(f,"mod_id");Str(f,"relative_path");Require(Regex.IsMatch(Str(f,"sha256"),@"\A[0-9a-fA-F]{64}\z"),"补丁文件指纹无效。");}
  foreach(var t in m.GetProperty("validation").EnumerateArray()){Str(t,"kind");Str(t,"result");Str(t,"summary");}
  Str(m.GetProperty("changes"),"gameplay_impact");Str(m.GetProperty("multiplayer"),"explanation");Str(m.GetProperty("saved_runs"),"explanation");Str(m.GetProperty("rollback"),"description");
 }
 public async Task DownloadAsync(OnlineBundle bundle,string destination,IProgress<long>? progress=null,CancellationToken ct=default)
 {
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromMinutes(5));ct=timeout.Token;
  ValidateBundle(bundle);destination=Path.GetFullPath(destination);
  string temp=destination+"."+Guid.NewGuid().ToString("N")+".part";
  try
  {
   using var response=await OpenDownload(bundle,ct);
   if(response.RequestMessage?.RequestUri?.Scheme!="https")throw new InvalidDataException("下载重定向必须使用 HTTPS。");
   if(response.Content.Headers.ContentLength is long length&&length!=bundle.Size)throw new InvalidDataException("附件大小与补丁源不一致。");
   await using(var input=await response.Content.ReadAsStreamAsync(ct))
   await using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true))
   using(var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
   {
    var buffer=new byte[65536];long received=0;int n;
    while((n=await input.ReadAsync(buffer,ct))>0){received+=n;if(received>bundle.Size)throw new InvalidDataException("附件超过声明大小。");hash.AppendData(buffer,0,n);await output.WriteAsync(buffer.AsMemory(0,n),ct);progress?.Report(received);}
    Require(received==bundle.Size&&Convert.ToHexString(hash.GetHashAndReset()).Equals(bundle.Sha256,StringComparison.OrdinalIgnoreCase),"附件校验失败，未保留损坏的下载。");
   }
   ct.ThrowIfCancellationRequested();File.Move(temp,destination,true);
  }
  finally{if(File.Exists(temp))File.Delete(temp);}
 }
 async Task<HttpResponseMessage> OpenDownload(OnlineBundle bundle,CancellationToken ct)
 {
  async Task<HttpResponseMessage> Open(string url)
  {
   using var request=new HttpRequestMessage(HttpMethod.Get,url);request.Headers.Accept.ParseAdd("application/octet-stream");
   var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
   if(!response.IsSuccessStatusCode){var status=response.StatusCode;response.Dispose();throw new HttpRequestException("下载服务器返回 "+(int)status);}
   return response;
  }
  try{return await Open(bundle.DownloadUrl);}
  catch(HttpRequestException){return await Open(bundle.AssetApiUrl);}
  catch(OperationCanceledException) when(!ct.IsCancellationRequested){return await Open(bundle.AssetApiUrl);}
 }
 public static bool MatchesBuiltIn(OnlinePatch patch)
 {
  using var plan=Catalog.Plan();var items=plan.RootElement.GetProperty("components").EnumerateArray();
  var c=items.FirstOrDefault(x=>Str(x,"id")==patch.ComponentId);if(c.ValueKind==JsonValueKind.Undefined)return false;
  var expected=c.GetProperty("files").EnumerateArray().Select(f=>Str(f,"name")+":"+Str(f,"sha256").ToUpperInvariant()).Order().ToArray();
  var actual=patch.Metadata.GetProperty("detection").GetProperty("installed_fingerprints").EnumerateArray().Select(f=>Str(f,"mod_id")==patch.ComponentId?Str(f,"relative_path")+":"+Str(f,"sha256").ToUpperInvariant():"other").Order().ToArray();
  return expected.SequenceEqual(actual);
 }
}
