using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace SpirePatch;

public static class GitHubModDiscoveryChecks
{
 sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> reply):HttpMessageHandler
 {
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct)
  {
   ct.ThrowIfCancellationRequested();
   var response=reply(req);response.RequestMessage=req;
   return Task.FromResult(response);
  }
 }
 static HttpResponseMessage Reply(string json)=>new(HttpStatusCode.OK)
  {Content=new StringContent(json,Encoding.UTF8,"application/json")};
 static string Search(params (string repo,string branch,string desc)[] repos)=>
  JsonSerializer.Serialize(new{items=repos.Select(r=>new{
   full_name=r.repo,default_branch=r.branch,description=r.desc}).ToArray()});
 static string Manifest(string id,string name="RitsuLib",string version="0.6.7")=>
  JsonSerializer.Serialize(new{id,name,version,author="OLC",has_dll=true,has_pck=false});
 static ManagedMod Mod(string folder,string id,string name,string? manifest=null)
 {
  Directory.CreateDirectory(folder);
  string path=Path.Combine(folder,"mod_manifest.json");
  File.WriteAllText(path,manifest??Manifest(id,name));
  return new(id,name,"0.6.7","OLC","description",folder,path,true,[]);
 }
 static HttpResponseMessage NotFound()=>new(HttpStatusCode.NotFound);
 public static async Task RunAsync(string output)
 {
  Directory.CreateDirectory(Path.GetDirectoryName(output)!);
  var lines=new List<string>();
  void Check(bool ok,string reason)
  {
   if(!ok)throw new InvalidOperationException(reason);
   lines.Add("PASS "+reason);File.WriteAllLines(output,lines);
  }
  var folder=Path.Combine(Path.GetDirectoryName(output)!,
   "github-discovery-fixture-"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(folder);
  try
  {
   var mod=Mod(Path.Combine(folder,"source-id"),"STS2-RitsuLib","RitsuLib");
   var terms=GitHubModDiscovery.SearchTerms(mod);
   Check(terms.Length==2&&terms[0]=="STS2-RitsuLib"&&terms[1]=="RitsuLib",
    "source lookup tries MOD ID and different displayed name");
   Check(GitHubModDiscovery.Similarity(mod,"BAKAOLC/STS2-RitsuLib",null)>
    GitHubModDiscovery.Similarity(mod,"Random/SomeUnrelatedMod",null),
    "matching ID outranks unrelated repositories");
   var explicitJson=JsonSerializer.Serialize(new{
    id="STS2-RitsuLib",has_dll=true,
    links=new{github="https://github.com/BAKAOLC/STS2-RitsuLib"},
    website="https://example.com/untrusted"
   });
   var parsed=GitHubModDiscovery.ManifestCandidates(explicitJson);
   Check(parsed.Length==1&&parsed[0].Repository=="BAKAOLC/STS2-RitsuLib",
    "nested local manifest source is extracted, foreign URLs are ignored");
   int searches=0,raws=0;
   using(var source=new GitHubModDiscovery(new Handler(req=>
   {
    if(req.RequestUri!.Host=="api.github.com")
    {
     searches++;
     var q=Uri.UnescapeDataString(req.RequestUri.Query);
     return Reply(q.Contains("STS2-RitsuLib",StringComparison.Ordinal)
      ?Search():Search(("BAKAOLC/STS2-RitsuLib","main","Slay the Spire 2 shared library")));
    }
    raws++;
    return req.RequestUri.AbsolutePath.EndsWith("/mod_manifest.json")
     ?Reply(Manifest("STS2-RitsuLib")):NotFound();
   }),TimeSpan.Zero))
   {
    var found=await source.DiscoverAsync(mod);
    Check(found.Candidates.Length==1&&found.Candidates[0].Verified&&
     found.Candidates[0].Repository=="BAKAOLC/STS2-RitsuLib"&&searches==2&&raws>=1,
     "alternative name search verifies exact remote mod_manifest ID via raw GitHub");
    int before=searches;
    var cached=await source.DiscoverAsync(mod);
    Check(cached.Candidates[0].Verified&&searches==before,
     "successful lookup is cached and avoids repeat API requests");
    await source.DiscoverAsync(mod,true);
    Check(searches>before,"explicit recheck refreshes the source result");
   }
   int api=0,raw=0;
   var explicitlyDeclared=Mod(Path.Combine(folder,"explicit"),"STS2-RitsuLib","RitsuLib",
    JsonSerializer.Serialize(new{id="STS2-RitsuLib",name="RitsuLib",author="OLC",
     version="0.6.7",has_dll=true,github_url="https://github.com/BAKAOLC/STS2-RitsuLib"}));
   using(var source=new GitHubModDiscovery(new Handler(req=>{
    if(req.RequestUri!.Host=="api.github.com")api++;
    else raw++;
    return NotFound();
   }),TimeSpan.Zero))
   {
    var result=await source.DiscoverAsync(explicitlyDeclared);
    Check(result.Candidates.Length==1&&result.Candidates[0].Verified&&api==0,
     "explicit local source is offered without consuming REST search quota");
   }
   var readme=Mod(Path.Combine(folder,"readme"),"STS2-RitsuLib","RitsuLib");
   File.WriteAllText(Path.Combine(readme.Folder,"README.md"),
    "Project: https://github.com/BAKAOLC/STS2-RitsuLib");
   int readmeQueries=0;
   using(var source=new GitHubModDiscovery(new Handler(req=>{
    if(req.RequestUri!.Host=="api.github.com"){readmeQueries++;return Reply(Search());}
    return req.RequestUri.AbsolutePath.EndsWith("/mod_manifest.json")
     ?Reply(Manifest("STS2-RitsuLib")):NotFound();
   }),TimeSpan.Zero))
   {
    var result=await source.DiscoverAsync(readme);
    Check(result.Candidates.Length==1&&result.Candidates[0].Verified&&readmeQueries==0,
     "README repository hint can be verified without a REST repository search");
   }
   int wrongChecks=0;
   using(var source=new GitHubModDiscovery(new Handler(req=>{
    if(req.RequestUri!.Host=="api.github.com")
     return Reply(Search(("BAKAOLC/STS2-RitsuLib","main","sts2 ritsulib")));
    wrongChecks++;
    return req.RequestUri.AbsolutePath.EndsWith("/mod_manifest.json")
     ?Reply(Manifest("other-mod")):NotFound();
   }),TimeSpan.Zero))
   {
    var result=await source.DiscoverAsync(mod);
    Check(result.Candidates.Length==0&&wrongChecks>=1,
     "repository with mismatched remote MOD ID must not be offered as a candidate");
   }
   using(var source=new GitHubModDiscovery(new Handler(req=>{
    if(req.RequestUri!.Host=="api.github.com")
     return Reply(Search(("BAKAOLC/STS2-RitsuLib","master","sts2 shared library")));
    return req.RequestUri.AbsolutePath.EndsWith("/manifest.json")
     ?Reply(Manifest("STS2-RitsuLib")):NotFound();
   }),TimeSpan.Zero))
   {
    var result=await source.DiscoverAsync(mod);
    Check(result.Candidates.Length==1&&result.Candidates[0].Verified&&
     result.Candidates[0].Reason.Contains("manifest.json"),
     "legacy manifest.json and repository default branch remain supported");
   }
   using(var source=new GitHubModDiscovery(new Handler(req=>
   {
    return new HttpResponseMessage(HttpStatusCode.Forbidden);
   }),TimeSpan.Zero))
   {
    var result=await source.DiscoverAsync(mod);
    Check(result.Retryable&&result.Candidates.Length==0&&source.Cached(mod)==null,
     "HTTP 403 is shown as a retryable search failure, not a cached negative match");
   }
   using(var source=new GitHubModDiscovery(new Handler(req=>{
    if(req.RequestUri!.Host=="api.github.com")
     return Reply(Search(("Random/NothingRelevant","main","some unrelated game")));
    return NotFound();
   }),TimeSpan.Zero))
   {
    var result=await source.DiscoverAsync(mod);
    Check(!result.Retryable&&result.Candidates.Length==0&&source.Cached(mod)!=null,
     "genuinely empty searches are cached briefly instead of claiming GitHub was blocked");
   }
  }
  finally {Directory.Delete(folder,true);}
 }
}