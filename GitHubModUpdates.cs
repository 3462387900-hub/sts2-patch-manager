using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpirePatch;

// Read-only GitHub Release checks: never download, install or modify MOD files.
internal sealed record GitHubModBinding(string ModId,string Repository);
internal sealed record GitHubModResult(string Repository,string LocalVersion,string LatestVersion,
 string ReleaseUrl,string Status,string Message,DateTimeOffset CheckedAt);
internal sealed class GitHubModUpdates : IDisposable
{
 readonly string file;
 readonly HttpClient client;
 readonly Dictionary<string,string> bindings=new(StringComparer.OrdinalIgnoreCase);
 readonly Dictionary<string,GitHubModResult> checks=new(StringComparer.OrdinalIgnoreCase);
 readonly SemaphoreSlim gate=new(1,1);
 static readonly TimeSpan CacheAge=TimeSpan.FromMinutes(30);
 static readonly Regex RepoPattern=new(@"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z",RegexOptions.Compiled);
 static readonly Regex VersionPattern=new(@"\Av?(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?\z",RegexOptions.IgnoreCase|RegexOptions.Compiled);
 public GitHubModUpdates(string dataRoot,string gameRoot,HttpMessageHandler? handler=null)
 {
  var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())));
  file=Path.Combine(dataRoot,"GitHub模组更新",key+".json");
  client=handler==null?new HttpClient():new HttpClient(handler);
  client.Timeout=TimeSpan.FromSeconds(15);
  client.DefaultRequestHeaders.UserAgent.ParseAdd("SpirePatch/"+ManagerUpdates.CurrentVersion);
  if(File.Exists(file))
  {
   try
   {
    var saved=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(file));
    if(saved!=null)foreach(var pair in saved)
     if(!string.IsNullOrWhiteSpace(pair.Key)&&TryNormalize(pair.Value,out var repository))
      bindings[pair.Key]=repository;
   }
   catch(JsonException){} // Broken preferences never prevent opening the manager.
   catch(IOException){}
   catch(UnauthorizedAccessException){}
  }
 }
 public void Dispose(){client.Dispose();gate.Dispose();}
 public static bool TryNormalize(string? input,out string repository)
 {
  repository="";
  if(string.IsNullOrWhiteSpace(input))return false;
  string raw=input.Trim().TrimEnd('/');
  if(Uri.TryCreate(raw,UriKind.Absolute,out var uri))
  {
   if(uri.Scheme!="https"||!uri.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase)||
    uri.UserInfo.Length>0||uri.Port!=443||uri.Query.Length>0||uri.Fragment.Length>0)return false;
   raw=uri.AbsolutePath.Trim('/');
  }
  else if(raw.Contains(':')||raw.Contains('?')||raw.Contains('#')||raw.Contains('\\'))return false;
  var parts=raw.Split('/');
  if(parts.Length==3&&parts[2].Equals("releases",StringComparison.OrdinalIgnoreCase))raw=parts[0]+"/"+parts[1];
  if(raw.EndsWith(".git",StringComparison.OrdinalIgnoreCase))raw=raw[..^4];
  if(!RepoPattern.IsMatch(raw)||raw.Split('/').Any(p=>p is "." or ".."||p.StartsWith('.')))return false;
  repository=raw;return true;
 }
 public string? Repository(string id)=>bindings.GetValueOrDefault(id);
 public GitHubModResult? Cached(string id)=>checks.GetValueOrDefault(id);
 public int BindingCount=>bindings.Count;
 public void Bind(string modId,string repositoryInput)
 {
  if(!TryNormalize(repositoryInput,out var repository))throw new ArgumentException("请输入 GitHub 仓库地址，例如 https://github.com/作者/仓库");
  bindings[modId]=repository;checks.Remove(modId);Save();
 }
 public void Unbind(string modId){bindings.Remove(modId);checks.Remove(modId);Save();}
 void Save()
 {
  Directory.CreateDirectory(Path.GetDirectoryName(file)!);
  var temp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
  try {File.WriteAllText(temp,JsonSerializer.Serialize(bindings,new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,file,true);}
  finally {if(File.Exists(temp))File.Delete(temp);}
 }
 static Version? ParseVersion(string value)
 {
  var m=VersionPattern.Match(value.Trim());
  if(!m.Success)return null;
  try
  {
   return new Version(int.Parse(m.Groups[1].Value),int.Parse(m.Groups[2].Value),
    m.Groups[3].Success?int.Parse(m.Groups[3].Value):0,m.Groups[4].Success?int.Parse(m.Groups[4].Value):0);
  }catch(ArgumentException){return null;}catch(OverflowException){return null;}
 }
 static GitHubModResult Interpret(string repository,string local,string tag,string url,DateTimeOffset now)
 {
  var a=ParseVersion(local);var b=ParseVersion(tag);
  if(a==null||b==null)return new(repository,local,tag,url,"待核对","版本格式无法可靠比较，请查看发布页。",now);
  int cmp=a.CompareTo(b);
  return cmp<0?new(repository,local,tag,url,"有更新","GitHub 发布了更高的版本。",now):
   cmp==0?new(repository,local,tag,url,"已是最新","本地版本与最新正式版一致。",now):
   new(repository,local,tag,url,"待核对","本地版本高于 GitHub 最新正式版，可能是测试版。",now);
 }
 public async Task<GitHubModResult> CheckAsync(ManagedMod mod,bool force=false,CancellationToken ct=default)
 {
  var repo=Repository(mod.Id)??throw new InvalidOperationException("尚未绑定 GitHub 仓库。");
  await gate.WaitAsync(ct);
  try
  {
   if(!force&&checks.TryGetValue(mod.Id,out var existing)&&existing.Repository.Equals(repo,StringComparison.OrdinalIgnoreCase)
    &&existing.LocalVersion==mod.Version&&DateTimeOffset.UtcNow-existing.CheckedAt<CacheAge)return existing;
   var now=DateTimeOffset.UtcNow;
   GitHubModResult result;
   try
   {
    using var request=new HttpRequestMessage(HttpMethod.Get,"https://api.github.com/repos/"+repo+"/releases/latest");
    request.Headers.Accept.ParseAdd("application/vnd.github+json");
    using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
    if(response.StatusCode==HttpStatusCode.NotFound)
     result=new(repo,mod.Version,"","","无法检测","未找到最新正式 Release；仓库可能没有发布正式版本。",now);
    else if((int)response.StatusCode is 403 or 429)
     result=new(repo,mod.Version,"","","无法检测","GitHub API 请求受限，请稍后重试。",now);
    else
    {
     response.EnsureSuccessStatusCode();
     if(response.Content.Headers.ContentLength>1024*1024)throw new InvalidDataException("GitHub 响应过大。");
     await using var stream=await response.Content.ReadAsStreamAsync(ct);
     using var buffer=new MemoryStream();
     var chunk=new byte[8192];int read;
     while((read=await stream.ReadAsync(chunk,ct))>0)
     {if(buffer.Length+read>1024*1024)throw new InvalidDataException("GitHub 响应过大。");buffer.Write(chunk,0,read);}
     using var doc=JsonDocument.Parse(buffer.ToArray());var root=doc.RootElement;
     var tag=root.GetProperty("tag_name").GetString()??"";
     var url=root.GetProperty("html_url").GetString()??"";
     if(tag.Length==0||!Uri.TryCreate(url,UriKind.Absolute,out var link)||link.Scheme!="https"||
      !link.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase)||
      !link.AbsolutePath.StartsWith("/"+repo+"/releases/tag/",StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("GitHub Release 数据无效。");
     result=Interpret(repo,mod.Version,tag,url,now);
    }
   }
   catch(OperationCanceledException) when(!ct.IsCancellationRequested)
   {result=new(repo,mod.Version,"","","无法检测","请求超时，请稍后重试。",now);}
   catch(HttpRequestException ex)
   {result=new(repo,mod.Version,"","","无法检测","网络请求失败："+ex.Message,now);}
   catch(JsonException)
   {result=new(repo,mod.Version,"","","无法检测","GitHub 返回了无法解析的数据。",now);}
   catch(InvalidDataException ex)
   {result=new(repo,mod.Version,"","","无法检测",ex.Message,now);}
   catch(KeyNotFoundException)
   {result=new(repo,mod.Version,"","","无法检测","GitHub Release 数据缺少必要字段。",now);}
   catch(InvalidOperationException)
   {result=new(repo,mod.Version,"","","无法检测","GitHub Release 数据结构异常。",now);}
   checks[mod.Id]=result;return result;
  }
  finally{gate.Release();}
 }
 public async Task<int> CheckAllAsync(IEnumerable<ManagedMod> mods,CancellationToken ct=default)
 {
  int checkedCount=0;
  foreach(var mod in mods.Where(m=>Repository(m.Id)!=null&&!ModPresentation.IsIndependentPatch(m))
   .GroupBy(m=>m.Id,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()))
  {ct.ThrowIfCancellationRequested();await CheckAsync(mod,ct:ct);checkedCount++;}
  return checkedCount;
 }
}
