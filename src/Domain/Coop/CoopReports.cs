using System.IO;
using System.Text.Json;

namespace SpirePatch;

// Reports remain schema 2 so existing teammates can exchange them.
internal static class CoopReports
{
 public static JsonElement Create(ScanReport? report,IReadOnlyList<PatchState> states,bool demo,ModInventory? inventory=null)
 {
  if(!demo&&report==null)throw new InvalidOperationException("请先选择游戏目录并完成扫描。");
  string Presence(PatchState s){
   if(demo||inventory==null||inventory.Issues.Count>0)return "unknown";
   var mods=inventory.Mods.Where(m=>m.Id.Equals(s.Patch.Id,StringComparison.OrdinalIgnoreCase)).ToArray();
   if(mods.Length==1)return mods[0].Enabled?"active":"disabled";
   string folder=Path.Combine(report!.Directory,"mods",s.Patch.Id);
   if(mods.Length>1||(Directory.Exists(folder)&&Directory.EnumerateFiles(folder,"*",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint}).Any()))return "unknown";
   return "absent";
  }
  return JsonSerializer.SerializeToElement(new{schema=2,presenceEvidenceVersion=inventory!=null&&!demo?1:0,catalogVersion=demo?"demo-0.4":report!.CatalogRevision,inventoryComplete=demo||report!.InventoryComplete,mode=demo?"demo":"read-only",time=demo?DateTimeOffset.Now:new DateTimeOffset(report!.Time),modCount=demo?51:report!.ModCount,patches=states.Select(s=>new{id=s.Patch.Id,name=s.Patch.Name,status=s.Status,reason=s.Reason,filesMatch=s.FilesMatch,presence=Presence(s),patchVersion=s.Patch.Version,relatedPatchIds=s.RelatedPatchIds,evidence=s.Evidence}),issues=demo?[]:report!.Issues});
 }
 public static void Write(string path,JsonElement snapshot)=>File.WriteAllText(path,JsonSerializer.Serialize(snapshot,new JsonSerializerOptions{WriteIndented=true}));
 internal const int MaxReportBytes=2*1024*1024,MaxPatches=512,MaxStringLength=16384;
 public static JsonElement Read(string path){
  using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
  if(input.Length>MaxReportBytes)throw new InvalidDataException("联机清单不能超过 2 MB，请让队友重新导出。");
  // Bounded reads also protect against a file growing after the length check.
  using var buffer=new MemoryStream();var bytes=new byte[8192];int count;
  while((count=input.Read(bytes,0,bytes.Length))>0){if(buffer.Length+count>MaxReportBytes)throw new InvalidDataException("联机清单超过大小限制。");buffer.Write(bytes,0,count);}
  buffer.Position=0;using var reader=new StreamReader(buffer,System.Text.Encoding.UTF8,detectEncodingFromByteOrderMarks:true);
  using var doc=JsonDocument.Parse(reader.ReadToEnd(),new JsonDocumentOptions{MaxDepth=32});
  ValidateLimits(doc.RootElement);return doc.RootElement.Clone();
 }
 static void ValidateLimits(JsonElement value){
  if(value.ValueKind==JsonValueKind.String&&value.GetString()!.Length>MaxStringLength)throw new InvalidDataException("联机清单文字过长。");
  if(value.ValueKind==JsonValueKind.Array){if(value.GetArrayLength()>MaxPatches)throw new InvalidDataException("联机清单条目过多。");foreach(var item in value.EnumerateArray())ValidateLimits(item);}
  if(value.ValueKind==JsonValueKind.Object)foreach(var property in value.EnumerateObject()){if(property.Name.Length>MaxStringLength)throw new InvalidDataException("联机清单字段名过长。");ValidateLimits(property.Value);}
 }
 internal static Dictionary<string,JsonElement> Patches(JsonElement report)
 {
  var result=new Dictionary<string,JsonElement>(StringComparer.OrdinalIgnoreCase);
  foreach(var p in report.GetProperty("patches").EnumerateArray()){
   string id=p.GetProperty("id").GetString()??"",status=p.GetProperty("status").GetString()??"";
   if(string.IsNullOrWhiteSpace(id)||!new[]{"已安装","待修复","需联动","已停用","不适用","需核对"}.Contains(status)||!result.TryAdd(id,p))throw new InvalidDataException("报告包含无效或重复的补丁记录，请重新导出。");
   _=p.GetProperty("filesMatch").GetBoolean();
  }
  return result;
 }
 public static string Compare(JsonElement local,JsonElement peer)
 {
  foreach(var r in new[]{local,peer})if(r.GetProperty("schema").GetInt32()!=2)throw new InvalidDataException("报告格式不支持，请更新管理器并重新导出。");
  if(local.GetProperty("mode").GetString()!=peer.GetProperty("mode").GetString())throw new InvalidDataException("不能将演示报告与真实扫描报告混合比较。");
  if(local.GetProperty("catalogVersion").GetString()!=peer.GetProperty("catalogVersion").GetString())throw new InvalidDataException("补丁目录版本不同，需要先刷新为同一目录版本再重新导出。");
  var mine=Patches(local);var theirs=Patches(peer);var differences=new List<string>();
  foreach(string id in mine.Keys.Union(theirs.Keys,StringComparer.OrdinalIgnoreCase)){
   bool a=mine.TryGetValue(id,out var me),b=theirs.TryGetValue(id,out var them);
   string name=(a?me:them).GetProperty("name").GetString()??id;
   string ms=a?me.GetProperty("status").GetString()!:"未记录",ts=b?them.GetProperty("status").GetString()!:"未记录";
   if(!a||!b||ms!=ts||me.GetProperty("filesMatch").GetBoolean()!=them.GetProperty("filesMatch").GetBoolean()||(me.TryGetProperty("presence",out var mp)&&them.TryGetProperty("presence",out var tp)&&mp.GetString()!=tp.GetString())){
    string detail=ms==ts?"（文件校验结果不同）":"";
    differences.Add($"{name}：本机 {ms} / 队友清单 {ts}{detail}");
   }
  }
  var warnings=new List<string>();
  foreach(var (r,label,patches) in new[]{(local,"本机",mine),(peer,"队友清单",theirs)}){
   if(!r.GetProperty("inventoryComplete").GetBoolean()||r.GetProperty("issues").GetArrayLength()>0)warnings.Add(label+"扫描不完整或有扫描问题");
   if(patches.Count==0||patches.Values.Any(p=>p.GetProperty("status").GetString()=="需核对"))warnings.Add(label+"仍有待核对信息");
  }
  string title=differences.Count>0?$"发现 {differences.Count} 项补丁差异":warnings.Count>0?"暂时无法确认补丁状态一致":"已收录补丁的扫描状态一致";
  string times=$"本机扫描：{local.GetProperty("time").GetDateTimeOffset().ToLocalTime():yyyy-MM-dd HH:mm:ss}\n队友清单扫描：{peer.GetProperty("time").GetDateTimeOffset().ToLocalTime():yyyy-MM-dd HH:mm:ss}";
  string details=string.Join("\n\n",differences);
  if(warnings.Count>0)details+="\n\n需要核对："+string.Join("；",warnings)+"。请处理后重新扫描导出。";
  return title+"\n\n"+times+(details.Length>0?"\n\n"+details.Trim():"")+"\n\n队友清单是导出时的快照，无法刷新队友电脑；有改动请让队友重新导出。这里只比较已收录补丁状态，不代表全部 MOD、配置或联机条件完全相同。";
 }
}
