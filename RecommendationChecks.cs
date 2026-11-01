using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpirePatch;
public static class RecommendationChecks
{
 public static void Run(string output)
 {
  var lines=new List<string>();var home=Path.Combine(Path.GetDirectoryName(output)!,"场景-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(home);
  void Check(bool ok,string name){if(!ok)throw new InvalidOperationException(name);lines.Add("PASS "+name);File.WriteAllLines(output,lines);}
  string NewRoot(){string r=Path.Combine(home,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(r,"mods"));File.WriteAllText(Path.Combine(r,"SlayTheSpire2.exe"),"");File.WriteAllText(Path.Combine(r,"game.dll"),"game");return r;}
  string Put(string r,string id,string content="base",string? folder=null){string d=Path.Combine(r,"mods",folder??id);Directory.CreateDirectory(d);File.WriteAllText(Path.Combine(d,"manifest.json"),JsonSerializer.Serialize(new{id,has_dll=true}));string path=Path.Combine(d,id+".dll");File.WriteAllText(path,content);return path;}
  string reference=NewRoot();var baseFile=Put(reference,"Target");var fixFile=Put(reference,"Fix","fixed");var depFile=Put(reference,"Base");
  object FileRef(string name,string hash)=>new{relative_path=name,sha256=hash};
  JsonObject Metadata(string id="Fix")=>JsonSerializer.SerializeToNode(new{
   schema_version="1.0",id="test."+id,version="1.0.0",publication_status="published",review_status="approved",title=id,summary="test patch",issue=new{diagnosis=new{conclusion="test evidence"}},limitations=Array.Empty<string>(),
   components=new[]{new{mod_id="Target",display_name="Target",role="affected"}},
   compatibility=new{profiles=new[]{new{profile_id="known",game_files=new[]{FileRef("game.dll",Catalog.Hash(Path.Combine(reference,"game.dll")))},required_mods=new[]{new{mod_id="Target",files=new[]{FileRef("Target.dll",Catalog.Hash(baseFile))}}},excluded_mod_ids=Array.Empty<string>()}}},
   detection=new{installed_fingerprints=new[]{new{mod_id=id,relative_path=id+".dll",sha256=Catalog.Hash(fixFile)},new{mod_id=id,relative_path=id+".json",sha256=Catalog.Hash(Path.Combine(reference,"mods","Fix","manifest.json"))}}},
   relationships=new{requires_patches=Array.Empty<object>(),requires_when_mod_present=Array.Empty<object>(),conflicts_patch_ids=Array.Empty<string>()}
  })!.AsObject();
  JsonObject Make(string id="Fix"){
   var m=Metadata(id);m["compatibility"]!["profiles"]![0]!["excluded_mod_ids"]=new JsonArray();
   // Each mod's real manifest differs by ID.
   string file=Put(reference,id,"fixed");m["detection"]!["installed_fingerprints"]![1]!["sha256"]=Catalog.Hash(Path.Combine(Path.GetDirectoryName(file)!,"manifest.json"));return m;
  }
  OnlinePatch Wrap(JsonObject m)=>Catalog.FromMetadata(m["id"]!.GetValue<string>()[5..],JsonSerializer.SerializeToElement(m));
  ScanReport Scan(string r,params JsonObject[] records){var before=Directory.EnumerateFiles(r,"*",SearchOption.AllDirectories).ToDictionary(p=>p,Catalog.Hash);var result=Catalog.Scan(r,records.Select(Wrap).ToArray(),"test-revision");if(before.Any(x=>!File.Exists(x.Key)||Catalog.Hash(x.Key)!=x.Value)||before.Count!=Directory.EnumerateFiles(r,"*",SearchOption.AllDirectories).Count())throw new InvalidOperationException("scan wrote game files");return result;}
  string State(string r,JsonObject m)=>Scan(r,m).Patches.Single().Status;
  JsonNode Profile(JsonObject m)=>m["compatibility"]!["profiles"]![0]!;
  void Require(JsonObject m,string id,string min="1.0.0")=>m["relationships"]!["requires_patches"]!.AsArray().Add(JsonSerializer.SerializeToNode(new{patch_id="test."+id,minimum_version=min}));
  string root=NewRoot();var patch=Make();
  Check(Catalog.Scan(root).Patches.All(x=>x.Status=="不适用"),"empty mods do not receive universal healing or other fixes");
  Check(State(root,patch)=="不适用","missing affected mod is not applicable");
  Put(root,"Target");Check(State(root,patch)=="待修复","exact game and target hashes recommend absent fix");
  File.WriteAllText(Path.Combine(root,"game.dll"),"new game");Check(State(root,patch)=="需核对","same mod IDs with unknown game build are blocked");File.WriteAllText(Path.Combine(root,"game.dll"),"game");
  File.WriteAllText(Path.Combine(root,"mods","Target","Target.dll"),"unknown");Check(State(root,patch)=="需核对","unknown target DLL is blocked");Put(root,"Target");
  var dependency=Make();Profile(dependency)["required_mods"]!.AsArray().Add(JsonSerializer.SerializeToNode(new{mod_id="Base",files=new[]{FileRef("Base.dll",Catalog.Hash(depFile))}}));
  Check(State(root,dependency)=="需核对","missing runtime dependency differs from missing affected mod");
  Put(root,"Fix","fixed","中文 修复目录");Check(State(root,patch)=="已安装","renamed mod folder and manifest retain exact detection");
  Check(State(root,dependency)=="需核对","installed fingerprints do not hide missing runtime dependency");
  Put(root,"Base");Check(State(root,dependency)=="已安装","installed fix and exact dependencies are healthy");
  File.WriteAllText(Path.Combine(root,"mods","中文 修复目录","Fix.dll"),"unknown");Check(State(root,patch)=="需核对","unknown existing patch DLL is not upgradeable");
  File.Delete(Path.Combine(root,"mods","中文 修复目录","Fix.dll"));Check(State(root,patch)=="需核对","partial patch is blocked");
  root=NewRoot();Put(root,"Target",folder:"自选名称");Check(State(root,patch)=="待修复","renamed target folder is supported");Put(root,"Target",folder:"duplicate");Check(Scan(root,patch).Issues.Any(x=>x.Contains("重复"))&&State(root,patch)=="需核对","duplicate IDs block ambiguous dependency");
  root=NewRoot();Put(root,"Target");var alternative=Make();var good=Profile(alternative).DeepClone();Profile(alternative)["game_files"]![0]!["sha256"]=new string('A',64);alternative["compatibility"]!["profiles"]!.AsArray().Add(good);Check(State(root,alternative)=="待修复","profiles are OR and conditions within them are AND");
  var excluded=Make();Profile(excluded)["excluded_mod_ids"]!.AsArray().Add("Target");Check(State(root,excluded)=="不适用","excluded combination is not recommended");
  var draft=Make();draft["publication_status"]="draft";draft["review_status"]="pending";Check(State(root,draft)=="需核对","new unreviewed draft is not trusted as bundled");
  var withdrawn=Make();withdrawn["publication_status"]="withdrawn";Check(State(root,withdrawn)=="需核对","withdrawn patch is not recommended");
  var invalid=Make();invalid["detection"]!["installed_fingerprints"]![0]!["relative_path"]="../escape.dll";Check(State(root,invalid)=="需核对","metadata path traversal is rejected");
  invalid=Make();invalid.Remove("compatibility");Check(State(root,invalid)=="需核对","malformed metadata produces review state instead of a crash");
  var a=Make("A");var b=Make("B");Require(a,"B");var pair=Scan(root,a,b);Check(pair.Patches[0].Status=="需联动"&&pair.Patches[0].RelatedPatchIds!.Contains("B"),"required absent patch is explicitly linked");
  Check(Scan(root,a).Patches.Single().Status=="需核对","missing patch reference blocks recommendation");
  a=Make("A");Require(a,"B","2.0.0");Check(Scan(root,a,b).Patches[0].Status=="需核对","minimum patch version is enforced");
  a=Make("A");a["relationships"]!["requires_when_mod_present"]!.AsArray().Add(JsonSerializer.SerializeToNode(new{mod_id="Trigger",patch_id="test.Missing",minimum_version="1.0.0"}));Check(State(root,a)=="待修复","conditional relation inactive without trigger");Put(root,"Trigger");Check(State(root,a)=="需核对","conditional relation active with trigger");
  a=Make("A");b=Make("B");Require(a,"B");Require(b,"A");Check(Scan(root,a,b).Patches.All(p=>p.Status=="需核对"),"declared dependency cycle is rejected");
  a=Make("A");b=Make("B");a["relationships"]!["conflicts_patch_ids"]!.AsArray().Add("test.B");Check(Scan(root,a,b).Patches.All(p=>p.Status=="需核对"),"one-sided declared conflict blocks both alternatives");
  a=Make("A");b=Make("B");
  Profile(a)["required_mods"]!.AsArray().Add(JsonSerializer.SerializeToNode(new{mod_id="B",files=new[]{FileRef("B.dll",Catalog.Hash(fixFile))}}));
  Check(Scan(root,a,b).Patches[0].Status=="需联动","eligible provider can supply a future file explicitly");
  Profile(b)["game_files"]![0]!["sha256"]=new string('B',64);Check(Scan(root,a,b).Patches.All(p=>p.Status=="需核对"),"blocked provider cannot mask current mismatch with projected files");
  b=Make("B");Profile(b)["required_mods"]!.AsArray().Add(JsonSerializer.SerializeToNode(new{mod_id="A",files=new[]{FileRef("A.dll",Catalog.Hash(fixFile))}}));Check(Scan(root,a,b).Patches.All(p=>p.Status=="需联动"),"reciprocal compatible file update is distinct from declared cycle");
  File.WriteAllText(Path.Combine(root,"mods","broken.json"),"{");Check(!Scan(root,patch).InventoryComplete&&State(root,patch)=="需核对","incomplete JSON inventory cannot produce confident recommendation");
  Check(Catalog.Bundled.All(Catalog.ReviewedBundle),"exact bundled drafts are recognized as reviewed bundle records");
  var changed=JsonNode.Parse(Catalog.Bundled[0].Metadata.GetRawText())!.AsObject();changed["summary"]="changed";Check(!Catalog.ReviewedBundle(Catalog.FromMetadata(Catalog.Bundled[0].ComponentId,JsonSerializer.SerializeToElement(changed))),"draft trust requires entire reviewed metadata not just DLL fingerprint");
  Check(true,"every synthetic scan preserved all fixture files byte for byte");
 }
}
