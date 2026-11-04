using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpirePatch;

// Proof of an upstream fix must refer to an exact binary. Changelog text alone cannot enable retirement.
internal record LifecycleEvidence(string PatchId, string TargetModId, string RelativePath,
 string Sha256, string Version, string Decision, string ReviewStatus, string Note, string[] Sources);
internal record LifecycleRegistry(int SchemaVersion, LifecycleEvidence[] Entries);

internal static class LifecycleEvidenceSource
{
 const string Url="https://raw.githubusercontent.com/3462387900-hub/sts2-patches/main/lifecycle-registry.json";
 internal static readonly LifecycleRegistry Empty=new(1,[]);
 static readonly Regex IdRule=new(@"\A[A-Za-z0-9_.-]+\z",RegexOptions.CultureInvariant);
 static readonly Regex HashRule=new(@"\A[0-9a-fA-F]{64}\z",RegexOptions.CultureInvariant);
 static readonly HttpClient Client=new(){Timeout=TimeSpan.FromSeconds(20)};
 static LifecycleEvidenceSource()
 {
  Client.DefaultRequestHeaders.UserAgent.ParseAdd("SpirePatch/"+ManagerUpdates.CurrentVersion);
 }
 static string Required(JsonElement e,string name)
 {
  if(!e.TryGetProperty(name,out var value)||value.ValueKind!=JsonValueKind.String)
   throw new InvalidDataException("补丁退役证据缺少字段："+name);
  return value.GetString()!;
 }
 internal static LifecycleRegistry Parse(string json)
 {
  using var doc=JsonDocument.Parse(json,new JsonDocumentOptions{MaxDepth=12});
  var data=doc.RootElement;
  if(data.ValueKind!=JsonValueKind.Object||!data.TryGetProperty("schema_version",out var schema)||
    schema.GetInt32()!=1||!data.TryGetProperty("entries",out var rows)||
    rows.ValueKind!=JsonValueKind.Array||rows.GetArrayLength()>100)
   throw new InvalidDataException("补丁生命周期记录版本或数量无效。");
  var output=new List<LifecycleEvidence>();
  var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach(var item in rows.EnumerateArray())
  {
   if(item.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("退役证据格式无效。");
   string patch=Required(item,"patch_id"),mod=Required(item,"target_mod_id"),
    relative=Required(item,"relative_path"),hash=Required(item,"sha256").ToUpperInvariant(),
    version=Required(item,"version"),decision=Required(item,"decision"),review=Required(item,"review_status"),
    note=Required(item,"note");
   if(!IdRule.IsMatch(patch)||!IdRule.IsMatch(mod)||
     !Catalog.Bundled.Any(p=>p.Id.Equals(patch,StringComparison.OrdinalIgnoreCase))||
     relative.Length>220||Path.IsPathRooted(relative)||
     relative.Replace('/','\\').Split('\\').Any(p=>p is "" or "." or "..")||
     relative.Any(c=>c<32||":<>\"|?*".Contains(c))||
     !relative.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)||
     !HashRule.IsMatch(hash)||version.Length is <1 or >70||note.Length is <12 or >800||
     decision is not("fixed_upstream" or "suspected_fixed" or "incompatible")||
     review is not("approved" or "candidate"))
    throw new InvalidDataException("存在无效或不完整的补丁退役证据："+patch);
   if(decision=="fixed_upstream"&&review!="approved")
    throw new InvalidDataException("未经审核的上游修复不能作为确认退役依据。");
   if(!item.TryGetProperty("sources",out var sourceJson)||sourceJson.ValueKind!=JsonValueKind.Array||
      sourceJson.GetArrayLength() is <1 or >5)
    throw new InvalidDataException("上游修复缺少可核查的证据链接。");
   var sources=sourceJson.EnumerateArray().Select(x=>x.GetString()??"").ToArray();
   if(sources.Any(x=>x.Length>400||!Uri.TryCreate(x,UriKind.Absolute,out var u)||
       u.Scheme!="https"||u.UserInfo.Length!=0||u.Fragment.Length>100))
    throw new InvalidDataException("退役证据来源链接无效。");
   string key=patch+"/"+mod+"/"+relative.ToLowerInvariant()+"/"+hash;
   if(!seen.Add(key))throw new InvalidDataException("重复的上游修复记录："+key);
   output.Add(new(patch,mod,relative.Replace('/','\\'),hash,version,decision,review,note,sources));
  }
  return new(1,output.ToArray());
 }

 internal static async Task<LifecycleRegistry> FetchAsync(CancellationToken ct=default)
 {
  using var response=await Client.GetAsync(Url,HttpCompletionOption.ResponseHeadersRead,ct);
  if(!response.IsSuccessStatusCode)
   throw new HttpRequestException("补丁库的上游修复记录暂时不可读取（HTTP "+(int)response.StatusCode+"）。");
  if(response.Content.Headers.ContentLength>128*1024)
   throw new InvalidDataException("补丁生命周期记录超过大小限制。");
  await using var stream=await response.Content.ReadAsStreamAsync(ct);
  using var buffer=new MemoryStream();
  var bytes=new byte[8192];int n;
  while((n=await stream.ReadAsync(bytes.AsMemory(),ct))>0)
  {
   if(buffer.Length+n>128*1024)throw new InvalidDataException("退役记录超过大小限制。");
   buffer.Write(bytes,0,n);
  }
  return Parse(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
 }
}
