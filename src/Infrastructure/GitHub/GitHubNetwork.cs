using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace SpirePatch;

internal sealed record GitHubNetworkSettings(int Schema,string Mode,string ProxyUrl);
internal sealed record GitHubNetworkTestResult(string Name,string Host,string Status,long ElapsedMilliseconds,bool Success);

// Settings only influence outbound HTTPS to the manager's existing official GitHub endpoints.
// No credentials, GitHub tokens, URL rewrites or TLS validation bypasses are supported.
internal static class GitHubNetwork
{
 const string FileName="GitHub网络.json";
 internal static GitHubNetworkSettings Default=>new(1,"system","");
 internal static string SettingsPath(string? root=null)=>
  Path.Combine(root??Path.Combine(AppContext.BaseDirectory,"数据"),FileName);
 internal static GitHubNetworkSettings Validate(GitHubNetworkSettings options)
 {
  if(options.Schema!=1||options.Mode is not("system" or "local"))
   throw new InvalidDataException("不支持的 GitHub 网络设置。");
  if(options.Mode=="system")return Default;
  if(!Uri.TryCreate(options.ProxyUrl,UriKind.Absolute,out var proxy)||
   proxy.Scheme!="http"||proxy.UserInfo.Length>0||proxy.Port is <1 or >65535||
   proxy.AbsolutePath!="/"||proxy.Query.Length>0||proxy.Fragment.Length>0||
   !(proxy.Host.Equals("localhost",StringComparison.OrdinalIgnoreCase)||
     proxy.Host.Equals("127.0.0.1",StringComparison.OrdinalIgnoreCase)||
     proxy.Host.Equals("::1",StringComparison.OrdinalIgnoreCase)))
   throw new InvalidDataException("仅支持本机 HTTP 代理，例如 http://127.0.0.1:7890；不接受账号密码和远程地址。");
  return new(1,"local","http://"+(proxy.Host=="::1"?"[::1]":proxy.Host)+":"+proxy.Port);
 }
 internal static GitHubNetworkSettings Load(string? root=null)
 {
  try
  {
   var file=SettingsPath(root);
   if(!File.Exists(file))return Default;
   var saved=JsonSerializer.Deserialize<GitHubNetworkSettings>(File.ReadAllText(file));
   return saved==null?Default:Validate(saved);
  }
  catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
  {return Default;} // Invalid settings must not block the manager from starting.
 }
 internal static void Save(GitHubNetworkSettings options,string? root=null)
 {
  var verified=Validate(options);
  string file=SettingsPath(root);
  Directory.CreateDirectory(Path.GetDirectoryName(file)!);
  var temp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
  try
  {
   File.WriteAllText(temp,JsonSerializer.Serialize(verified,new JsonSerializerOptions{WriteIndented=true}));
   File.Move(temp,file,true);
  }
  finally{if(File.Exists(temp))File.Delete(temp);}
 }
 internal static HttpClient CreateClient(HttpMessageHandler? handler=null,string? root=null)
 {
  if(handler!=null)return new HttpClient(handler);
  var configuration=Load(root);
  if(configuration.Mode=="system")return new HttpClient(); // Windows/system proxy defaults.
  var httpHandler=new HttpClientHandler{
   UseProxy=true,
   Proxy=new WebProxy(configuration.ProxyUrl)
  };
  return new HttpClient(httpHandler);
 }
 internal static async Task<GitHubNetworkTestResult[]> DiagnoseAsync(HttpMessageHandler? handler=null,
  string? root=null,CancellationToken ct=default)
 {
  using var client=CreateClient(handler,root);
  client.Timeout=Timeout.InfiniteTimeSpan;
  client.DefaultRequestHeaders.UserAgent.ParseAdd("SpirePatch/"+ManagerUpdates.CurrentVersion);
  var results=new List<GitHubNetworkTestResult>();
  var checks=new[]{
   ("GitHub API","api.github.com","https://api.github.com/repos/"+ManagerUpdates.Repository),
   ("GitHub 原始文件","raw.githubusercontent.com",ManagerUpdates.FeedUrl),
   ("GitHub 发布页","github.com",ManagerUpdates.RepositoryUrl+"/releases/latest")
  };
  foreach(var (name,host,url) in checks)
  {
   ct.ThrowIfCancellationRequested();
   using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
   timeout.CancelAfter(TimeSpan.FromSeconds(8));
   var clock=Stopwatch.StartNew();
   try
   {
    using var request=new HttpRequestMessage(HttpMethod.Get,url);
    using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
    var final=response.RequestMessage?.RequestUri;
    bool success=response.IsSuccessStatusCode&&final!=null&&final.Scheme=="https"&&
     final.Host.Equals(host,StringComparison.OrdinalIgnoreCase);
    results.Add(new(name,host,success?"可访问（HTTP "+(int)response.StatusCode+"）":
     "返回 HTTP "+(int)response.StatusCode+" 或发生意外重定向",
     clock.ElapsedMilliseconds,success));
   }
   catch(OperationCanceledException) when(!ct.IsCancellationRequested)
   {results.Add(new(name,host,"连接超时（8 秒）",clock.ElapsedMilliseconds,false));}
   catch(HttpRequestException ex)
   {results.Add(new(name,host,"网络连接失败："+ex.Message,clock.ElapsedMilliseconds,false));}
  }
  return results.ToArray();
 }
}
