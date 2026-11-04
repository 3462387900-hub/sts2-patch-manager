using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpirePatch;
public static class OnlineChecks
{
 sealed class FakeHandler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
 {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){ct.ThrowIfCancellationRequested();var r=respond(request);r.RequestMessage=request;return Task.FromResult(r);}}
 public static async Task RunAsync(string output)
 {
  var lines=new List<string>();void Check(bool yes,string s){if(!yes)throw new Exception(s);lines.Add("PASS "+s);}
  byte[] payload=Encoding.UTF8.GetBytes("test package content");string hash=Convert.ToHexString(SHA256.HashData(payload));
  var bundle=new OnlineBundle("STS2-test.zip",hash,payload.Length,OnlineSource.RepositoryUrl+"/releases/download/v1/STS2-test.zip","https://api.github.com/repos/"+OnlineSource.Repository+"/releases/assets/1",OnlineSource.RepositoryUrl+"/releases/tag/v1");
  OnlineSource.ValidateBundle(bundle);Check(true,"fixed repository release accepted");
  foreach(var bad in new[]{bundle with{FileName="../bad.zip"},bundle with{DownloadUrl="https://example.com/evil.zip"},bundle with{Sha256="bad"},bundle with{Size=100_000_000},bundle with{AssetApiUrl="https://api.github.com/repos/other/repo/releases/assets/1"}})
  {try{OnlineSource.ValidateBundle(bad);throw new Exception("invalid bundle accepted");}catch(InvalidDataException){}}
  Check(true,"foreign source, invalid checksum, path traversal and oversized download rejected");
  var folder=Path.Combine(Path.GetDirectoryName(output)!,"online-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);string dest=Path.Combine(folder,"result.zip");
  using(var source=new OnlineSource(new FakeHandler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(payload)})))
  {await source.DownloadAsync(bundle,dest);Check(File.ReadAllBytes(dest).SequenceEqual(payload),"verified download saved");}
  File.WriteAllText(dest,"existing file");
  var corrupt=(byte[])payload.Clone();corrupt[0]^=1;
  using(var source=new OnlineSource(new FakeHandler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(corrupt)})))
  {try{await source.DownloadAsync(bundle,dest);throw new Exception("corrupt payload accepted");}catch(InvalidDataException){}Check(File.ReadAllText(dest)=="existing file"&&!Directory.EnumerateFiles(folder,"*.part").Any(),"bad download preserves existing file and removes temporary data");}
  using(var source=new OnlineSource(new FakeHandler(_=>new(HttpStatusCode.OK){Content=new ByteArrayContent(payload)})))
  {using var ct=new CancellationTokenSource();ct.Cancel();try{await source.DownloadAsync(bundle,dest,ct:ct.Token);throw new Exception("cancellation ignored");}catch(OperationCanceledException){}Check(File.ReadAllText(dest)=="existing file","canceled download leaves destination unchanged");}
  object feed=new{schema_version=1,repository=OnlineSource.Repository,catalog_commit=new string('a',40),bundle=new{file_name=bundle.FileName,sha256=bundle.Sha256,size=bundle.Size,download_url=bundle.DownloadUrl,asset_api_url=bundle.AssetApiUrl,release_url=bundle.ReleaseUrl,install_mode="manual_bundle"}};
  object index=new{schema_version="1.0",entries=new[]{new{id="local.test",component_id="Test",metadata="patches/Test/patch-info/patch.json"}}};
  object metadata=new{schema_version="1.0",id="local.test",publication_status="draft",title="测试补丁",summary="测试说明",issue=new{diagnosis=new{conclusion="部分确认"}},limitations=new[]{"没有实机测试"},components=Array.Empty<object>(),compatibility=new{profiles=Array.Empty<object>()},detection=new{installed_fingerprints=Array.Empty<object>()},validation=Array.Empty<object>(),changes=new{gameplay_impact="测试"},multiplayer=new{explanation="测试"},saved_runs=new{explanation="未知"},rollback=new{description="测试"}};
  HttpResponseMessage Envelope(object o)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(o))};
  using(var source=new OnlineSource(new FakeHandler(r=>Envelope(r.RequestUri!.AbsolutePath.EndsWith("manager-feed.json")?feed:r.RequestUri.AbsolutePath.EndsWith("index.json")?index:metadata))))
  {var catalog=await source.LoadAsync();Check(catalog.Patches.Single().Title=="测试补丁"&&catalog.Patches.Single().Limits[0]=="没有实机测试","reads individual patch metadata and preserves test limits");}
  var payloadFeed=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(feed))!;
  payloadFeed["bundle"]!["install_mode"]="manager_payload";
  using(var source=new OnlineSource(new FakeHandler(r=>Envelope(r.RequestUri!.AbsolutePath.EndsWith("manager-feed.json")?payloadFeed:r.RequestUri.AbsolutePath.EndsWith("index.json")?index:metadata))))
  {var catalog=await source.LoadAsync();Check(catalog.Bundle.InstallMode=="manager_payload","manager payload protocol loads and retains installation guidance");}
  payloadFeed["bundle"]!["install_mode"]="execute_arbitrary_script";
  using(var source=new OnlineSource(new FakeHandler(_=>Envelope(payloadFeed))))
  {try{await source.LoadAsync();throw new Exception("unknown protocol accepted");}catch(InvalidDataException){}Check(true,"unknown installation protocol requests manager update");}
  var badIndex=new{schema_version="1.0",entries=new[]{new{id="local.test",component_id="Test",metadata="../../private.json"}}};
  using(var source=new OnlineSource(new FakeHandler(r=>Envelope(r.RequestUri!.AbsolutePath.EndsWith("manager-feed.json")?feed:badIndex))))
  {try{await source.LoadAsync();throw new Exception("bad metadata path accepted");}catch(InvalidDataException){}Check(true,"metadata path must stay inside its patch directory");}
  using(var source=new OnlineSource(new FakeHandler(_=>new(HttpStatusCode.Forbidden))))
  {try{await source.LoadAsync();throw new Exception("rate limit ignored");}catch(HttpRequestException e){Check(e.Message.Contains("受限"),"GitHub rate limit has an actionable error");}}
  File.WriteAllLines(output,lines);
 }
}
