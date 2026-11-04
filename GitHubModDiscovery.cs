using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpirePatch;

internal sealed record GitHubSourceCandidate(string Repository,string Reason,int Score,bool Verified);
internal sealed record GitHubSourceDiscoveryResult(string ModId,GitHubSourceCandidate[] Candidates,string Note,bool Retryable=false);

// Discovery is advisory. Candidates are NEVER bound without explicit user confirmation.
// Public repository search uses GitHub REST; manifest verification uses raw.githubusercontent.com,
// avoiding dozens of extra anonymous REST calls for each MOD.
internal sealed class GitHubModDiscovery : IDisposable
{
 const int MaxManifestBytes=128*1024;
 const int MaxSearchBytes=384*1024;
 const int MaxReadmeBytes=64*1024;
 readonly HttpClient client;
 readonly TimeSpan searchDelay;
 readonly Dictionary<string,(GitHubSourceDiscoveryResult Value,DateTimeOffset Expires)> cache=
  new(StringComparer.OrdinalIgnoreCase);
 DateTimeOffset nextSearch=DateTimeOffset.MinValue;
 DateTimeOffset searchUnavailableUntil=DateTimeOffset.MinValue;
 static readonly Regex ReadmeGitHub=new(@"https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:\.git)?",
  RegexOptions.IgnoreCase|RegexOptions.Compiled);
 static string CacheKey(ManagedMod mod)=>Path.GetFullPath(mod.Manifest)+"|"+mod.Id+"|"+mod.Version;
 public void ClearCache()=>cache.Clear();
 public GitHubModDiscovery(HttpMessageHandler? handler=null,TimeSpan? searchInterval=null)
 {
  client=handler==null?new HttpClient():new HttpClient(handler);
  client.Timeout=TimeSpan.FromSeconds(15);
  client.DefaultRequestHeaders.UserAgent.ParseAdd("SpirePatch/"+ManagerUpdates.CurrentVersion);
  searchDelay=searchInterval??TimeSpan.FromSeconds(7);
 }
 public void Dispose()=>client.Dispose();
 public GitHubSourceDiscoveryResult? Cached(ManagedMod mod)=>
  cache.TryGetValue(CacheKey(mod),out var entry)&&entry.Expires>DateTimeOffset.UtcNow?entry.Value:null;
 static string? Field(JsonElement root,string key)=>
  root.ValueKind==JsonValueKind.Object&&root.TryGetProperty(key,out var value)&&
  value.ValueKind==JsonValueKind.String?value.GetString():null;
 static string Token(string value)=>Regex.Replace(value.ToLowerInvariant(),@"[^a-z0-9]","");
 static bool Relevant(string a,string b)=>a.Equals(b,StringComparison.OrdinalIgnoreCase);
 static string CleanTerm(string value)=>
  Regex.Replace(Regex.Replace(value,@"[^\p{L}\p{N}_.\- ]"," "),@"\s+"," ").Trim();
 internal static string[] SearchTerms(ManagedMod mod)
 {
  var terms=new List<string>();
  foreach(var input in new[]{mod.Id,mod.Name})
  {
   var term=CleanTerm(input);
   if(term.Length<3||term.Length>80||terms.Any(t=>Token(t)==Token(term)))continue;
   terms.Add(term);
  }
  return terms.Take(2).ToArray();
 }
 public static GitHubSourceCandidate[] ManifestCandidates(string manifest)
 {
  using var doc=JsonDocument.Parse(manifest);
  if(doc.RootElement.ValueKind!=JsonValueKind.Object)return [];
  var found=new Dictionary<string,GitHubSourceCandidate>(StringComparer.OrdinalIgnoreCase);
  var keys=new[]{"github","github_url","repository","repository_url","source","source_url",
   "homepage","website","url","project_url"};
  void Collect(JsonElement source,string prefix)
  {
   if(source.ValueKind!=JsonValueKind.Object)return;
   foreach(var key in keys)
   {
    var value=Field(source,key);
    if(value!=null&&GitHubModUpdates.TryNormalize(value,out var repo))
     found[repo]=new(repo,"MOD 清单声明的 GitHub 来源："+prefix+key,100,true);
   }
  }
  Collect(doc.RootElement,"");
  foreach(var section in new[]{"links","metadata","contact"})
   if(doc.RootElement.TryGetProperty(section,out var nested))
    Collect(nested,section+".");
  return found.Values.ToArray();
 }
 // A repository name is only a clue, not proof. Exact remote mod_manifest.id is proof.
 internal static int Similarity(ManagedMod mod,string repository,string? description)
 {
  var parts=repository.Split('/');
  if(parts.Length!=2)return 0;
  var target=Token(parts[1]);var id=Token(mod.Id);var name=Token(mod.Name);
  if(target.Length<3)return 0;
  int score=0;
  if(id.Length>=4&&target==id)score=65;
  else if(name.Length>=4&&target==name)score=60;
  else if(target.Length>=5&&id.Length>=5&&(target.Contains(id)||id.Contains(target)))score=40;
  else if(target.Length>=5&&name.Length>=5&&(target.Contains(name)||name.Contains(target)))score=37;
  string author=Token(mod.Author);
  if(author.Length>=3&&author!="unknown"&&author!="none"&&Token(parts[0])==author)score+=14;
  string desc=Token(description??"");
  if(id.Length>=5&&desc.Contains(id))score=Math.Max(score,26);
  if(name.Length>=5&&desc.Contains(name))score=Math.Max(score,24);
  if(score>0&&description!=null&&(desc.Contains("slaythespire2")||desc.Contains("sts2")))score+=5;
  return Math.Min(90,score);
 }
 static GitHubSourceCandidate[] ReadmeCandidates(ManagedMod mod)
 {
  var found=new Dictionary<string,GitHubSourceCandidate>(StringComparer.OrdinalIgnoreCase);
  foreach(var filename in new[]{"README.md","readme.md","README.txt"})
  {
   var path=Path.Combine(mod.Folder,filename);
   if(!File.Exists(path))continue;
   GameEnvironment.NoLinks(path);
   if(new FileInfo(path).Length>MaxReadmeBytes)continue;
   var contents=File.ReadAllText(path);
   foreach(Match m in ReadmeGitHub.Matches(contents))
   {
    if(!GitHubModUpdates.TryNormalize(m.Value.TrimEnd('.'),out var repository))continue;
    int score=Similarity(mod,repository,null);
    if(score<25)continue;
    found[repository]=new(repository,"本地 README 中提及；仍需核实仓库 MOD ID",Math.Max(score,30),false);
   }
  }
  return found.Values.ToArray();
 }
 static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response,int limit,CancellationToken ct)
 {
  if(response.Content.Headers.ContentLength>limit)throw new InvalidDataException("GitHub 返回的数据过大。");
  await using var input=await response.Content.ReadAsStreamAsync(ct);
  using var output=new MemoryStream();
  var buffer=new byte[8192];int n;
  while((n=await input.ReadAsync(buffer,ct))>0)
  {
   if(output.Length+n>limit)throw new InvalidDataException("GitHub 返回的数据过大。");
   output.Write(buffer,0,n);
  }
  return output.ToArray();
 }
 sealed class SearchLimitedException(string message):HttpRequestException(message);
 async Task<JsonDocument> SearchAsync(string term,CancellationToken ct)
 {
  if(searchUnavailableUntil>DateTimeOffset.UtcNow)
   throw new SearchLimitedException("GitHub 搜索暂时受限，本次批量查找已暂停。");
  var delay=nextSearch-DateTimeOffset.UtcNow;
  if(delay>TimeSpan.Zero)await Task.Delay(delay,ct);
  nextSearch=DateTimeOffset.UtcNow+searchDelay;
  using var request=new HttpRequestMessage(HttpMethod.Get,
   "https://api.github.com/search/repositories?q="+Uri.EscapeDataString(term+" in:name,description")+"&per_page=8");
  request.Headers.Accept.ParseAdd("application/vnd.github+json");
  using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
  if(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
  {
   var rateExhausted=response.Headers.TryGetValues("x-ratelimit-remaining",out var values)&&
    values.FirstOrDefault()=="0";
   if(rateExhausted)
   {
    // Don't issue more search calls in the same batch after quota exhaustion.
    searchUnavailableUntil=DateTimeOffset.UtcNow.AddMinutes(5);
    if(response.Headers.TryGetValues("x-ratelimit-reset",out var resetValues)&&
     long.TryParse(resetValues.FirstOrDefault(),out var timestamp))
    {
     try
     {
      var until=DateTimeOffset.FromUnixTimeSeconds(timestamp);
      if(until>DateTimeOffset.UtcNow&&until<DateTimeOffset.UtcNow.AddHours(2))searchUnavailableUntil=until;
     }
     catch(ArgumentOutOfRangeException){}
    }
   }
   else searchUnavailableUntil=DateTimeOffset.UtcNow.AddSeconds(45);
   throw new SearchLimitedException(rateExhausted?
    "GitHub 搜索额度暂时耗尽，请在限额重置后继续查找。":
    "GitHub 搜索返回 HTTP "+(int)response.StatusCode+"，可能是网络访问限制。");
  }
  response.EnsureSuccessStatusCode();
  return JsonDocument.Parse(await ReadBoundedAsync(response,MaxSearchBytes,ct));
 }
 // null means absent/unreadable, false means a different MOD ID, true is a verified exact match.
 async Task<(bool? Match,string Filename)> VerifyAsync(string repository,string branch,string modId,
  CancellationToken ct)
 {
  if(!GitHubModUpdates.TryNormalize(repository,out var normalized)||normalized!=repository)
   return(null,"");
  if(string.IsNullOrWhiteSpace(branch)||branch.Length>180)return(null,"");
  foreach(var file in new[]{"mod_manifest.json","manifest.json"})
  {
   var url="https://raw.githubusercontent.com/"+repository+"/"+Uri.EscapeDataString(branch)+"/"+file;
   using var request=new HttpRequestMessage(HttpMethod.Get,url);
   using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
   if(response.StatusCode==HttpStatusCode.NotFound)continue;
   if(!response.IsSuccessStatusCode)continue;
   var final=response.RequestMessage?.RequestUri;
   if(final==null||final.Scheme!="https"||!final.Host.Equals("raw.githubusercontent.com",
    StringComparison.OrdinalIgnoreCase))continue;
   var json=Encoding.UTF8.GetString(await ReadBoundedAsync(response,MaxManifestBytes,ct)).TrimStart('\uFEFF');
   try
   {
    using var doc=JsonDocument.Parse(json);
    string? id=Field(doc.RootElement,"id");
    if(string.IsNullOrWhiteSpace(id))continue;
    bool isMod=doc.RootElement.TryGetProperty("has_dll",out _)||
     doc.RootElement.TryGetProperty("has_pck",out _);
    if(!isMod)continue;
    return(Relevant(id,modId),file);
   }
   catch(JsonException){} // A README or unrelated JSON is not valid MOD evidence.
  }
  return(null,"");
 }
 static bool ValidRepositoryResult(string? input,out string repo)=>
  GitHubModUpdates.TryNormalize(input,out repo)&&repo.Equals(input,StringComparison.Ordinal);
 async Task<GitHubSourceCandidate[]> VerifyCandidatesAsync(ManagedMod mod,
  IEnumerable<(GitHubSourceCandidate Candidate,string Branch)> candidates,CancellationToken ct)
 {
  var verified=new List<GitHubSourceCandidate>();
  foreach(var (candidate,branch) in candidates.OrderByDescending(x=>x.Candidate.Score).Take(6))
  {
   bool? match=null;string filename="";
   try{(match,filename)=await VerifyAsync(candidate.Repository,branch,mod.Id,ct);}
   catch(HttpRequestException){}
   catch(OperationCanceledException) when(!ct.IsCancellationRequested){}
   catch(InvalidDataException){}
   if(match==false)continue; // Explicitly different MOD: never offer this candidate.
   verified.Add(match==true?candidate with{
    Verified=true,Score=98,Reason="仓库根目录 "+filename+" 与本地 MOD ID 一致（已核验）"}:candidate);
  }
  return verified.ToArray();
 }
 public async Task<GitHubSourceDiscoveryResult> DiscoverAsync(ManagedMod mod,bool force=false,
  CancellationToken ct=default)
 {
  string key=CacheKey(mod);
  if(!force&&Cached(mod) is { } previous)return previous;
  var candidates=new Dictionary<string,GitHubSourceCandidate>(StringComparer.OrdinalIgnoreCase);
  string note="";bool retryable=false;
  try
  {
   GameEnvironment.NoLinks(mod.Manifest);
   if(new FileInfo(mod.Manifest).Length<=MaxManifestBytes)
    foreach(var candidate in ManifestCandidates(await File.ReadAllTextAsync(mod.Manifest,ct)))
     candidates[candidate.Repository]=candidate;
   foreach(var candidate in ReadmeCandidates(mod))
    candidates.TryAdd(candidate.Repository,candidate);
   if(candidates.Count>0)
   {
    // Explicit source declarations require no REST query. Try raw main/master evidence
    // for README hints; a local manifest declaration remains a visible direct source.
    foreach(var candidate in candidates.Values.ToArray().Where(x=>!x.Verified))
    {
     foreach(var branch in new[]{"main","master"})
     {
      var results=await VerifyCandidatesAsync(mod,[(candidate,branch)],ct);
      if(results.Length==0){candidates.Remove(candidate.Repository);break;}
      if(results[0].Verified){candidates[candidate.Repository]=results[0];break;}
     }
    }
   }
   // Without a direct source, search both MOD ID and display name; second query is
   // skipped as soon as an ID-verified repository is found.
   if(!candidates.Values.Any(c=>c.Verified))
   {
    foreach(var term in SearchTerms(mod))
    {
     using var search=await SearchAsync(term,ct);
     if(!search.RootElement.TryGetProperty("items",out var items)||
      items.ValueKind!=JsonValueKind.Array)continue;
     var found=new List<(GitHubSourceCandidate Candidate,string Branch)>();
     foreach(var item in items.EnumerateArray())
     {
      var fullName=Field(item,"full_name");
      if(!ValidRepositoryResult(fullName,out var repo))continue;
      int score=Similarity(mod,repo,Field(item,"description"));
      if(score<24)continue;
      string branch=Field(item,"default_branch")??"main";
      found.Add((new(repo,"GitHub 仓库名称/描述相似；尚未验证 MOD ID",score,false),branch));
     }
     foreach(var candidate in await VerifyCandidatesAsync(mod,found
      .OrderByDescending(x=>x.Candidate.Score).Take(5),ct))
     {
      if(!candidates.TryGetValue(candidate.Repository,out var existing)||
       candidate.Verified&&!existing.Verified||candidate.Score>existing.Score)
       candidates[candidate.Repository]=candidate;
     }
     if(candidates.Values.Any(c=>c.Verified))break;
    }
   }
  }
  catch(OperationCanceledException) when(!ct.IsCancellationRequested)
  {note="查找超时，请稍后重试。";retryable=true;}
  catch(SearchLimitedException ex){note=ex.Message;retryable=true;}
  catch(HttpRequestException ex){note="GitHub 网络请求失败："+ex.Message;retryable=true;}
  catch(IOException ex){note="读取 MOD 本地信息失败："+ex.Message;}
  catch(UnauthorizedAccessException){note="无法读取 MOD 本地信息。";}
  catch(JsonException){note="本地清单或 GitHub 搜索数据无法解析。";}
  catch(InvalidDataException ex){note=ex.Message;}
  var list=candidates.Values.OrderByDescending(x=>x.Verified).ThenByDescending(x=>x.Score)
   .ThenBy(x=>x.Repository,StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
  if(note.Length==0&&list.Length==0)
   note="未找到可核验的 GitHub 来源；部分 MOD 可能仅在创意工坊发布。";
  var result=new GitHubSourceDiscoveryResult(mod.Id,list,note,retryable);
  if(!retryable)cache[key]=(result,DateTimeOffset.UtcNow.Add(list.Length>0?TimeSpan.FromHours(6):
   TimeSpan.FromMinutes(30)));
  return result;
 }
}