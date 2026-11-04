using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpirePatch;

// Actual health and possible coordinated updates are evaluated separately.
internal sealed class RecommendationScanner
{
 record Fingerprint(string Mod,string Path,string Hash);
 record Profile(string Name,Fingerprint[] Files,string[] Required,string[] Excluded);
 record Requirement(string Id,Version Minimum);
 sealed class Entry(OnlinePatch patch)
 {
  public OnlinePatch Patch=patch;
  public Fingerprint[] Files=[];
  public Profile[] Profiles=[];
  public Requirement[] Requires=[];
  public string[] Conflicts=[];
  public string Error="",Status="需核对";
  public bool Reviewed,Present,Same,Eligible;
 }
 readonly string root,revision;
 readonly IReadOnlyList<OnlinePatch> source;
 readonly Dictionary<string,List<string>> mods=new(StringComparer.OrdinalIgnoreCase);
 readonly Dictionary<string,string> hashes=new(StringComparer.OrdinalIgnoreCase);
 readonly List<string> issues=[];
 readonly List<Entry> entries=[];
 bool complete=true;
 static string S(JsonElement e,string key)=>e.GetProperty(key).GetString()??throw new InvalidDataException("说明字段为空："+key);
 static JsonElement[] A(JsonElement e,string key)=>e.GetProperty(key).EnumerateArray().ToArray();
 static string Id(string value){if(!Regex.IsMatch(value,@"\A[A-Za-z0-9_.-]+\z")||value is "." or "..")throw new InvalidDataException("无效模组标识");return value;}
 static string Relative(string value)
 {
  if(string.IsNullOrWhiteSpace(value)||Path.IsPathRooted(value)||value.Contains(':')||value.Contains('\\')||value.Split('/').Any(s=>s is "" or "." or ".."||s.EndsWith('.')||s.EndsWith(' ')||s.IndexOfAny(Path.GetInvalidFileNameChars())>=0))throw new InvalidDataException("说明含不安全文件路径："+value);
  return value;
 }
 static Fingerprint F(JsonElement e,string? mod=null)
 {
  string hash=S(e,"sha256").ToUpperInvariant();if(!Regex.IsMatch(hash,@"\A[0-9A-F]{64}\z"))throw new InvalidDataException("文件指纹无效");
  return new(mod??Id(S(e,"mod_id")),Relative(S(e,"relative_path")),hash);
 }
 public RecommendationScanner(string directory,IReadOnlyList<OnlinePatch> patches,string catalogRevision){root=Path.GetFullPath(directory);source=patches;revision=catalogRevision;}
 static void NoLinks(string path)
 {
  for(string? p=path;p!=null;p=Path.GetDirectoryName(p))if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("跳过链接路径："+p);
 }
 void Incomplete(string message){complete=false;if(!issues.Contains(message))issues.Add(message);}
 void Inventory(string dir)
 {
  try{
   NoLinks(dir);
   foreach(var item in Directory.EnumerateFileSystemEntries(dir)){
    try{
     if((File.GetAttributes(item)&FileAttributes.ReparsePoint)!=0)throw new IOException("跳过链接路径："+item);
     NoLinks(item);if(Directory.Exists(item)){Inventory(item);continue;}
     if(!item.EndsWith(".json",StringComparison.OrdinalIgnoreCase))continue;
     using var d=JsonDocument.Parse(File.ReadAllText(item));var e=d.RootElement;
     if(e.ValueKind!=JsonValueKind.Object||!e.TryGetProperty("id",out var id)||!(e.TryGetProperty("has_dll",out _)||e.TryGetProperty("has_pck",out _)))continue;
     string key=Id(id.GetString()??"");if(!mods.TryGetValue(key,out var files))mods[key]=files=[];files.Add(item);
    }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException){Incomplete("无法完整读取 "+Path.GetRelativePath(root,item)+"："+ex.Message);}
   }
  }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){Incomplete(ex.Message);}
 }
 string Locate(Fingerprint f)
 {
  if(f.Mod.Length==0)return Path.Combine(root,f.Path.Replace('/',Path.DirectorySeparatorChar));
  if(mods.TryGetValue(f.Mod,out var manifests)){
   if(manifests.Count!=1)throw new IOException("重复模组 ID："+f.Mod);
   if(f.Path.Equals(f.Mod+".json",StringComparison.OrdinalIgnoreCase))return manifests[0];
   return Path.Combine(Path.GetDirectoryName(manifests[0])!,f.Path.Replace('/',Path.DirectorySeparatorChar));
  }
  return Path.Combine(root,"mods",f.Mod,f.Path.Replace('/',Path.DirectorySeparatorChar));
 }
 string Actual(Fingerprint f)
 {
  try{
   string path=Locate(f);NoLinks(path);if(hashes.TryGetValue(path,out var cached))return cached;
   if(!File.Exists(path))return hashes[path]="";
   var before=new FileInfo(path);var size=before.Length;var time=before.LastWriteTimeUtc;
   string hash=Catalog.Hash(path);var after=new FileInfo(path);
   if(size!=after.Length||time!=after.LastWriteTimeUtc)throw new IOException("扫描时文件发生变化："+path);
   return hashes[path]=hash;
  }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){Incomplete(ex.Message);return "unreadable";}
 }
 void Parse(Entry e)
 {
  var m=e.Patch.Metadata;Id(e.Patch.ComponentId);
  if(S(m,"schema_version")!="1.0"||S(m,"id")!=e.Patch.Id||!Version.TryParse(S(m,"version"),out _))throw new InvalidDataException("不支持的补丁说明版本");
  e.Reviewed=Catalog.ReviewedBundle(e.Patch);
  e.Files=A(m.GetProperty("detection"),"installed_fingerprints").Select(x=>F(x)).ToArray();
  if(e.Files.Length==0||e.Files.Any(f=>f.Mod!=e.Patch.ComponentId)||e.Files.Select(f=>f.Mod+"/"+f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=e.Files.Length)throw new InvalidDataException("安装指纹为空、重复或跨模组；本版需人工核对");
  e.Profiles=A(m.GetProperty("compatibility"),"profiles").Select(p=>{
   var required=A(p,"required_mods");
   if(required.Any(r=>A(r,"files").Length==0)||A(p,"game_files").Length==0)throw new InvalidDataException("适配说明缺少精确文件条件");
   return new Profile(S(p,"profile_id"),A(p,"game_files").Select(f=>F(f,"")).Concat(required.SelectMany(r=>A(r,"files").Select(f=>F(f,Id(S(r,"mod_id")))))).ToArray(),required.Select(r=>Id(S(r,"mod_id"))).ToArray(),A(p,"excluded_mod_ids").Select(x=>Id(x.GetString()!)).ToArray());
  }).ToArray();
  if(e.Profiles.Length==0)throw new InvalidDataException("没有适配构建");
  var rel=m.GetProperty("relationships");
  e.Requires=A(rel,"requires_patches").Concat(A(rel,"requires_when_mod_present").Where(r=>mods.ContainsKey(Id(S(r,"mod_id"))))).Select(r=>new Requirement(S(r,"patch_id"),Version.Parse(S(r,"minimum_version")))).ToArray();
  e.Conflicts=A(rel,"conflicts_patch_ids").Select(x=>x.GetString()!).ToArray();
  e.Present=mods.ContainsKey(e.Patch.ComponentId)||(Directory.Exists(Path.Combine(root,"mods",e.Patch.ComponentId))&&Directory.EnumerateFiles(Path.Combine(root,"mods",e.Patch.ComponentId),"*",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint}).Any())||e.Files.Any(f=>Actual(f).Length>0);
  e.Same=mods.TryGetValue(e.Patch.ComponentId,out var manifests)&&manifests.Count==1&&e.Files.All(f=>Actual(f)==f.Hash);
  if(S(m,"publication_status")=="withdrawn")throw new InvalidDataException("此补丁已撤回；请人工核对，不自动移除文件");
  if(!e.Reviewed&&(S(m,"publication_status")!="published"||S(m,"review_status")!="approved"))throw new InvalidDataException("此说明尚未经审核，不能据此推荐安装");
  string[] targets=A(m,"components").Where(c=>S(c,"role") is "affected" or "trigger").Select(c=>Id(S(c,"mod_id"))).Where(id=>e.Profiles.Any(p=>p.Required.Contains(id))).Distinct().ToArray();
  bool relevant=targets.Length>0?e.Profiles.Any(p=>targets.Where(p.Required.Contains).All(mods.ContainsKey)):e.Profiles.Any(p=>p.Required.All(mods.ContainsKey));
  if(!relevant&&!e.Present){e.Status="不适用";e.Error="未检测到适用的模组组合："+string.Join("、",targets.Length>0?targets:e.Profiles[0].Required);return;}
  if(e.Profiles.All(p=>p.Excluded.Any(mods.ContainsKey))&&!e.Present){e.Status="不适用";e.Error="检测到说明中排除的模组组合";return;}
  if(!relevant)throw new InvalidDataException("发现修复残留，但缺少它对应的原模组或触发组合");
  if(e.Files.Any(f=>mods.TryGetValue(f.Mod,out var mf)&&mf.Count!=1))throw new InvalidDataException("补丁模组 ID 重复，不能判断加载了哪一份");
  if(e.Present&&!e.Same){
   if(!e.Reviewed)throw new InvalidDataException("现有修复文件不同于收录指纹，需核对后再升级");
   using var plan=Catalog.Plan();var component=A(plan.RootElement,"components").Single(c=>S(c,"id")==e.Patch.ComponentId);
   if(!mods.ContainsKey(e.Patch.ComponentId)||e.Files.Any(f=>{
    var old=A(component,"files").FirstOrDefault(x=>S(x,"name")==f.Path);
    return old.ValueKind==JsonValueKind.Undefined||!A(old,"allowedExisting").Any(h=>string.Equals(h.GetString(),Actual(f),StringComparison.OrdinalIgnoreCase));
   }))throw new InvalidDataException("修复文件不完整或版本未知，不能自动覆盖");
  }
  e.Eligible=true;
 }
 bool Cycle(Entry current,HashSet<string> trail,HashSet<string>? done=null)
 {
  done??=[];if(done.Contains(current.Patch.Id))return false;
  if(!trail.Add(current.Patch.Id))return true;
  foreach(var r in current.Requires){var target=entries.FirstOrDefault(e=>e.Patch.Id==r.Id);if(target!=null&&Cycle(target,trail,done))return true;}
  trail.Remove(current.Patch.Id);done.Add(current.Patch.Id);return false;
 }
 (bool Ok,string Reason,HashSet<Entry> Related) Evaluate(Entry e,HashSet<Entry> candidates,bool future)
 {
  var related=new HashSet<Entry>();
  foreach(var r in e.Requires){
   var dependency=entries.FirstOrDefault(x=>x.Patch.Id==r.Id);
   if(dependency==null)return(false,"缺少关联补丁说明："+r.Id,related);
   if(!candidates.Contains(dependency)||(!future&&!dependency.Same))return(false,"关联修复尚未满足："+dependency.Patch.Title,related);
   if(!Version.TryParse(S(dependency.Patch.Metadata,"version"),out var version)||version<r.Minimum)return(false,"关联补丁版本不足："+dependency.Patch.Title+"，要求 "+r.Minimum,related);
   if(!dependency.Same)related.Add(dependency);
  }
  string failure="没有匹配的适配构建";
  foreach(var p in e.Profiles.OrderBy(p=>p.Files.Count(f=>Actual(f)!=f.Hash))){
   var required=new HashSet<Entry>(related);string error="";
   if(p.Excluded.Any(mods.ContainsKey)){failure="存在不兼容模组："+string.Join("、",p.Excluded.Where(mods.ContainsKey));continue;}
   foreach(var id in p.Required){if(mods.TryGetValue(id,out var found)&&found.Count!=1){error="重复模组 ID："+id;break;}if(!mods.ContainsKey(id)&&(!future||!candidates.Any(x=>x.Patch.ComponentId==id))){error="缺少运行前置："+id;break;}}
   if(error.Length==0)foreach(var f in p.Files){
    if(Actual(f)==f.Hash)continue;
    var provider=future?candidates.FirstOrDefault(x=>x.Files.Any(y=>y==f)):null;
    if(provider==null){error="文件缺失或版本未匹配："+(f.Mod.Length==0?"游戏":f.Mod)+" / "+f.Path;break;}
    if(provider!=e)required.Add(provider);
   }
   if(error.Length==0)return(true,"匹配适配构建："+p.Name,required);
   failure=error;
  }
  return(false,failure,related);
 }
 public ScanReport Run()
 {
  NoLinks(root);
  if(!File.Exists(Path.Combine(root,"SlayTheSpire2.exe"))||!Directory.Exists(Path.Combine(root,"mods")))throw new InvalidOperationException("请选择包含 SlayTheSpire2.exe 和 mods 文件夹的游戏根目录。");
  Inventory(Path.Combine(root,"mods"));foreach(var m in mods.Where(m=>m.Value.Count>1))Incomplete("重复模组 ID："+m.Key);
  foreach(var patch in source){var e=new Entry(patch);entries.Add(e);try{Parse(e);}catch(Exception ex) when(ex is InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException){e.Eligible=false;e.Error="适配需核对："+ex.Message;}}
  var duplicate=entries.GroupBy(e=>e.Patch.Id).Where(g=>g.Count()>1).SelectMany(g=>g).Concat(entries.GroupBy(e=>e.Patch.ComponentId,StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()>1).SelectMany(g=>g)).ToHashSet();
  foreach(var e in entries.Where(e=>e.Eligible)){
   if(duplicate.Contains(e)){e.Eligible=false;e.Error="补丁源存在重复标识";}
   else if(Cycle(e,[])){e.Eligible=false;e.Error="补丁声明存在循环依赖，需维护者核对";}
  }
  var conflictBlocked=new HashSet<Entry>();
  foreach(var e in entries.Where(e=>e.Eligible))foreach(var other in entries.Where(o=>o!=e&&(o.Present||o.Eligible))){
   bool conflict=e.Conflicts.Contains(other.Patch.Id)||other.Conflicts.Contains(e.Patch.Id)||e.Files.Any(f=>other.Files.Any(g=>f.Mod==g.Mod&&f.Path==g.Path&&f.Hash!=g.Hash));
   if(conflict){conflictBlocked.Add(e);e.Error="关联补丁存在冲突："+other.Patch.Title;}
  }
  foreach(var e in conflictBlocked)e.Eligible=false;
  var candidates=entries.Where(e=>e.Eligible).ToHashSet();bool changed;
  do{changed=false;foreach(var e in candidates.ToArray()){var check=Evaluate(e,candidates,true);if(!check.Ok){candidates.Remove(e);e.Error=check.Reason;changed=true;}}}while(changed);
  var healthy=candidates.Where(e=>e.Same).ToHashSet();
  do{changed=false;foreach(var e in healthy.ToArray())if(!Evaluate(e,healthy,false).Ok){healthy.Remove(e);changed=true;}}while(changed);
  var results=new List<PatchState>();
  foreach(var e in entries){
   var related=new HashSet<Entry>();var evidence=new List<string>();string reason=e.Error,status=e.Status;
   if(candidates.Contains(e)){
    void Collect(Entry current,HashSet<Entry> seen){if(!seen.Add(current))return;foreach(var d in Evaluate(current,candidates,true).Related.Concat(current.Requires.Select(r=>entries.First(x=>x.Patch.Id==r.Id)).Where(x=>!healthy.Contains(x)))){if(d!=e)related.Add(d);Collect(d,seen);}}
    Collect(e,[]);var evaluation=Evaluate(e,candidates,true);evidence.Add(evaluation.Reason);
    if(healthy.Contains(e)){status="已安装";reason="修复指纹、当前适配文件及关联修复均匹配（仅文件状态）";}
    else if(related.Count>0){status="需联动";reason="需要一起核对或更新："+string.Join("、",related.Select(x=>x.Patch.Title));}
    else{status="待修复";reason=e.Present?"识别到可更新的已收录旧文件":"现有模组与精确版本匹配，尚未安装此修复";}
    evidence.Add(e.Same?"本项修复文件已匹配":"本项修复文件尚未对齐");
    if(!healthy.Contains(e))evidence.Add("当前条件："+Evaluate(e,healthy,false).Reason);
    evidence.Add(e.Reviewed?"适配说明来自本版已审核整合包；可选择本项及必要关联修复，安装前仍需预检":"适配说明来自已发布且审核通过的补丁记录；安装仍需单独预检");
   }
   if(!complete){status="需核对";reason="扫描不完整，无法可靠推荐；请先处理扫描报告中的读取问题";}
   PatchInfo info;try{info=Catalog.Info(e.Patch);}catch{info=new(e.Patch.ComponentId,e.Patch.Title,"模组适配",e.Patch.Summary,e.Patch.Detail,"未知",[]);}
   results.Add(new(info,status,reason,e.Same,related.Select(x=>x.Patch.ComponentId).ToArray(),evidence.ToArray()));
  }
  return new(root,mods.Count,results,issues,DateTime.Now,revision,complete);
 }
}
