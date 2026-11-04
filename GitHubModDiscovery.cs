using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpirePatch;

internal sealed record GitHubSourceCandidate(string Repository,string Reason,int Score,bool Verified);
internal sealed record GitHubSourceDiscoveryResult(string ModId,GitHubSourceCandidate[] Candidates,string Note);

// Discovery is advisory. Nothing is bound without an explicit user confirmation.
internal sealed class GitHubModDiscovery : IDisposable
{
 readonly HttpClient client;
 readonly Dictionary<string,GitHubSourceDiscoveryResult> cache=new(StringComparer.OrdinalIgnoreCase);
 public GitHubModDiscovery(HttpMessageHandler? handler=null)
 {
  client=handler==null?new HttpClient():new HttpClient(handler);
  client.Timeout=TimeSpan.FromSeconds(12);
  client.DefaultRequestHeaders.UserAgent.ParseAdd("SpirePatch/"+ManagerUpdates.CurrentVersion);
 }
 public void Dispose()=>client.Dispose();
 public GitHubSourceDiscoveryResult? Cached(string id)=>cache.GetValueOrDefault(id);
 static string? Field(JsonElement value,string field)=>
  value.TryGetProperty(field,out var property)&&property.ValueKind==JsonValueKind.String?property.GetString():null;
 static bool Relevant(string name,string id)=>
  !string.IsNullOrWhiteSpace(name)&&name.Equals(id,StringComparison.OrdinalIgnoreCase);
 public static GitHubSourceCandidate[] ManifestCandidates(string manifest)
 {
  using var doc=JsonDocument.Parse(manifest);
  if(doc.RootElement.ValueKind!=JsonValueKind.Object)return [];
  var root=doc.RootElement;
  var found=new Dictionary<string,GitHubSourceCandidate>(StringComparer.OrdinalIgnoreCase);
  foreach(var key in new[]{"github","github_url","repository","repository_url","source","source_url","homepage","website","url"})
  {
   var value=Field(root,key);
   if(value!=null&&GitHubModUpdates.TryNormalize(value,out var repo))
    found[repo]=new(repo,"MOD 清单明确声明的 GitHub 来源："+key,100,true);
  }
  return found.Values.ToArray();
 }
 static string Token(string value)=>Regex.Replace(value.ToLowerInvariant(),@"[^a-z0-9]","");
 static int Similarity(ManagedMod mod,string repository,string? description)
 {
  var project=repository.Split('/').Last();
  var target=Token(project);
  if(target.Length<3)return 0;
  var id=Token(mod.Id);var name=Token(mod.Name);
  int score=target==id||target==name?55:0;
  if(score==0&&target.Length>=5&&((id.Length>=5&&(id.Contains(target)||target.Contains(id)))||
     (name.Length>=5&&(name.Contains(target)||target.Contains(name)))))score=30;
  var owner=repository.Split('/')[0];
  if(mod.Author!="未声明"&&Token(mod.Author).Length>=3&&Token(owner)==Token(mod.Author))score+=15;
  if(score==0&&description!=null&&id.Length>=5&&Token(description).Contains(id))score=20;
  return score;
 }
 async Task<JsonDocument?> GetJsonAsync(string url,CancellationToken ct)
 {
  using var request=new HttpRequestMessage(HttpMethod.Get,url);
  request.Headers.Accept.ParseAdd("application/vnd.github+json");
  using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
  if(response.StatusCode==HttpStatusCode.NotFound)return null;
  if((int)response.StatusCode is 403 or 429)throw new HttpRequestException("GitHub 搜索受限，请稍后重试。");
  response.EnsureSuccessStatusCode();
  if(response.Content.Headers.ContentLength>128*1024)throw new InvalidDataException("GitHub 搜索响应过大。");
  await using var input=await response.Content.ReadAsStreamAsync(ct);
  using var bytes=new MemoryStream();var buffer=new byte[8192];int n;
  while((n=await input.ReadAsync(buffer,ct))>0)
  {if(bytes.Length+n>128*1024)throw new InvalidDataException("GitHub 搜索响应过大。");bytes.Write(buffer,0,n);}
  return JsonDocument.Parse(bytes.ToArray());
 }
 static bool ValidManifest(string content,string modId)
 {
  try
  {
   var bytes=Convert.FromBase64String(content.Replace("\n",""));
   using var doc=JsonDocument.Parse(bytes);
   return Relevant(Field(doc.RootElement,"id")??"",modId)&&
    (doc.RootElement.TryGetProperty("has_dll",out _)||doc.RootElement.TryGetProperty("has_pck",out _));
  }
  catch(FormatException){return false;}
  catch(JsonException){return false;}
 }
 public async Task<GitHubSourceDiscoveryResult> DiscoverAsync(ManagedMod mod,bool force=false,CancellationToken ct=default)
 {
  if(!force&&cache.TryGetValue(mod.Id,out var saved))return saved;
  var candidates=new Dictionary<string,GitHubSourceCandidate>(StringComparer.OrdinalIgnoreCase);
  string note="";
  try
  {
   // Only a known, local manifest: no recursive file scanning or execution.
   if(new FileInfo(mod.Manifest).Length<=128*1024)
    foreach(var candidate in ManifestCandidates(await File.ReadAllTextAsync(mod.Manifest,ct)))
     candidates[candidate.Repository]=candidate;
   // If the MOD declares its own source, avoid spending API search quota.
   if(candidates.Count==0)
   {
    string query=mod.Id.Trim();
    if(query.Length>=3&&query.Length<=90)
    {
     using var search=await GetJsonAsync("https://api.github.com/search/repositories?q="+
      Uri.EscapeDataString(query+" in:name,description")+"&per_page=5",ct);
     if(search!=null&&search.RootElement.TryGetProperty("items",out var items))
     foreach(var item in items.EnumerateArray())
     {
      string? repo=Field(item,"full_name");
      if(repo==null||!GitHubModUpdates.TryNormalize(repo,out var normalized))continue;
      int score=Similarity(mod,normalized,Field(item,"description"));
      if(score<20)continue;
      candidates[normalized]=new(normalized,"GitHub 搜索候选；尚未验证 MOD ID",score,false);
     }
     foreach(var candidate in candidates.Values.ToArray().OrderByDescending(x=>x.Score).Take(3))
     {
      // Read only the root manifest. No match means this is still a suggestion.
      using var remote=await GetJsonAsync("https://api.github.com/repos/"+candidate.Repository+"/contents/manifest.json",ct);
      if(remote!=null&&Field(remote.RootElement,"encoding")=="base64"&&
       ValidManifest(Field(remote.RootElement,"content")??"",mod.Id))
       candidates[candidate.Repository]=candidate with {Score=95,Verified=true,Reason="GitHub 仓库根目录的 manifest.json 与本地 MOD ID 一致"};
     }
    }
    else note="MOD ID 太短或过长，无法安全搜索。";
   }
  }
  catch(OperationCanceledException) when(!ct.IsCancellationRequested){note="GitHub 搜索超时。";}
  catch(HttpRequestException ex){note=ex.Message;}
  catch(IOException ex){note="读取 MOD 清单失败："+ex.Message;}
  catch(UnauthorizedAccessException){note="无法读取 MOD 清单。";}
  catch(JsonException){note="MOD 清单或 GitHub 搜索数据无法解析。";}
  catch(InvalidDataException ex){note=ex.Message;}
  var list=candidates.Values.OrderByDescending(x=>x.Score).ThenBy(x=>x.Repository,StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
  if(list.Length==0&&note=="")note="未找到可信候选；该 MOD 可能没有公开的 GitHub 仓库。";
  var result=new GitHubSourceDiscoveryResult(mod.Id,list,note);
  cache[mod.Id]=result;return result;
 }
}
