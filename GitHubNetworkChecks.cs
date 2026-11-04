using System.IO;
using System.Net;
using System.Net.Http;

namespace SpirePatch;

public static class GitHubNetworkChecks
{
 sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> reply):HttpMessageHandler
 {
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
  {
   ct.ThrowIfCancellationRequested();
   var r=reply(request);r.RequestMessage=request;
   return Task.FromResult(r);
  }
 }
 public static async Task RunAsync(string output)
 {
  Directory.CreateDirectory(Path.GetDirectoryName(output)!);
  var lines=new List<string>();
  void Check(bool yes,string reason)
  {
   if(!yes)throw new InvalidOperationException(reason);
   lines.Add("PASS "+reason);File.WriteAllLines(output,lines);
  }
  string folder=Path.Combine(Path.GetDirectoryName(output)!,"network-settings-fixture-"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(folder);
  try
  {
   Check(GitHubNetwork.Load(folder).Mode=="system","default uses Windows/system proxy settings");
   GitHubNetwork.Save(new(1,"local","http://127.0.0.1:7890"),folder);
   Check(GitHubNetwork.Load(folder).ProxyUrl=="http://127.0.0.1:7890","explicit loopback proxy persists");
   foreach(string input in new[]{
    "https://127.0.0.1:7890","http://192.168.0.10:7890",
    "http://user:password@127.0.0.1:7890","http://127.0.0.1:7890/some/path",
    "file:///C:/private", "http://example.com:7890"
   })
   {
    try{GitHubNetwork.Save(new(1,"local",input),folder);throw new Exception("unsafe proxy accepted");}
    catch(InvalidDataException){}
   }
   Check(GitHubNetwork.Load(folder).ProxyUrl=="http://127.0.0.1:7890",
    "remote/credentialed/invalid proxy URL is rejected without overwriting saved settings");
   GitHubNetwork.Save(new(1,"system",""),folder);
   Check(GitHubNetwork.Load(folder).Mode=="system","system proxy mode can be restored");
   int calls=0;
   var data=await GitHubNetwork.DiagnoseAsync(new Handler(req=>
   {
    calls++;
    return new(HttpStatusCode.OK){Content=new StringContent("small test")};
   }),folder);
   Check(data.Length==3&&calls==3&&data.All(x=>x.Success)&&
    data.Any(x=>x.Host=="api.github.com")&&
    data.Any(x=>x.Host=="raw.githubusercontent.com")&&
    data.Any(x=>x.Host=="github.com"),
    "diagnostics check the three distinct official GitHub endpoints without downloading archives");
   var failures=await GitHubNetwork.DiagnoseAsync(new Handler(req=>
    req.RequestUri!.Host=="raw.githubusercontent.com"?new(HttpStatusCode.Forbidden):
     new(HttpStatusCode.OK)),folder);
   Check(failures.Length==3&&failures.Count(x=>!x.Success)==1&&
    failures.Single(x=>!x.Success).Host=="raw.githubusercontent.com",
    "diagnostics identify an individual host failure instead of claiming all GitHub is down");
   File.WriteAllText(GitHubNetwork.SettingsPath(folder),"invalid JSON");
   Check(GitHubNetwork.Load(folder).Mode=="system",
    "corrupt settings safely revert to system defaults");
  }
  finally{Directory.Delete(folder,true);}
 }
}
