using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SpirePatch;
public partial class MainWindow
{
 public async Task CheckCoopAsync(string output,bool capture=false)
 {
  var lines=new List<string>();string home=Path.Combine(Path.GetDirectoryName(output)!,"联机检查-中文 空格-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(home);
  void Check(bool ok,string label){if(!ok)throw new InvalidOperationException(label);lines.Add("PASS "+label);File.WriteAllLines(output,lines);}
  string root=Path.Combine(home,"游戏"),mods=Path.Combine(root,"mods"),fix=Path.Combine(mods,"测试修复");Directory.CreateDirectory(mods);File.WriteAllText(Path.Combine(root,"SlayTheSpire2.exe"),"");
  string Put(string id,string value,string folder){Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"manifest.json"),JsonSerializer.Serialize(new{id,has_dll=true}));string file=Path.Combine(folder,id+".dll");File.WriteAllText(file,value);return file;}
  string target=Put("Target","original",Path.Combine(mods,"原模组"));string dll=Put("Fix","patched",fix);
  var metadata=JsonSerializer.SerializeToElement(new{
   schema_version="1.0",id="test.Fix",version="1.0.0",publication_status="published",review_status="approved",title="联机测试补丁",summary="测试",issue=new{diagnosis=new{conclusion="测试"}},limitations=Array.Empty<string>(),
   components=new[]{new{mod_id="Target",display_name="Target",role="affected"}},
   compatibility=new{profiles=new[]{new{profile_id="known",game_files=new[]{new{relative_path="SlayTheSpire2.exe",sha256=Catalog.Hash(Path.Combine(root,"SlayTheSpire2.exe"))}},required_mods=new[]{new{mod_id="Target",files=new[]{new{relative_path="Target.dll",sha256=Catalog.Hash(target)}}}},excluded_mod_ids=Array.Empty<string>()}}},
   detection=new{installed_fingerprints=new[]{new{mod_id="Fix",relative_path="Fix.dll",sha256=Catalog.Hash(dll)},new{mod_id="Fix",relative_path="Fix.json",sha256=Catalog.Hash(Path.Combine(fix,"manifest.json"))}}},
   relationships=new{requires_patches=Array.Empty<object>(),requires_when_mod_present=Array.Empty<object>(),conflicts_patch_ids=Array.Empty<string>()}
  });
  onlineCatalog=new("coop-test",[Catalog.FromMetadata("Fix",metadata)],new("","",0,"","",""),DateTimeOffset.Now);sourceCurrent=true;UseGameRoot(root);
  string original=Path.Combine(home,"删除前.json"),latest=Path.Combine(home,"删除后.json");
  var before=Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).ToDictionary(p=>p,Catalog.Hash);
  await ExportFreshReportAsync(original);var exported=CoopReports.Read(original);
  Check(states.Single().Status=="已安装","export scans selected root even with no previous report");
  Check(exported.GetProperty("time").GetDateTimeOffset()==new DateTimeOffset(report!.Time),"export time is actual scan time");
  Check(before.All(x=>Catalog.Hash(x.Key)==x.Value)&&before.Count==Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Count(),"export does not write game files");
  await CompareFreshReportAsync(original);Check(coopComparison!.StartsWith("已收录补丁的扫描状态一致"),"unchanged complete reports match");
  // Move the whole synthetic patch out of mods, equivalent to deleting it without destructive IO.
  Directory.Move(fix,Path.Combine(home,"移除的测试补丁"));
  Check(states.Single().Status=="已安装","fixture reproduces stale UI state before import");
  await CompareFreshReportAsync(original);
  Check(states.Single().Status=="待修复"&&coopComparison!.Contains("本机 待修复 / 队友清单 已安装"),"import rescans after a whole patch folder was removed");
  Check(coopComparison!.Contains("本机扫描：")&&coopComparison.Contains("队友清单扫描："),"comparison displays both scan times");
  if(capture){ShowCoop();toast.Text="已与本机最新扫描比对。";await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);UpdateLayout();var bitmap=new RenderTargetBitmap((int)shell.ActualWidth,(int)shell.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(shell);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(Path.GetDirectoryName(output)!,"联机检查-缺失补丁.png"));encoder.Save(stream);}
  Directory.Move(Path.Combine(home,"移除的测试补丁"),fix);
  await ExportFreshReportAsync(latest);Check(CoopReports.Read(latest).GetProperty("patches")[0].GetProperty("status").GetString()=="已安装","export rescans after restoration instead of using missing state");
  File.Delete(dll);await ExportFreshReportAsync(latest);
  Check(CoopReports.Read(latest).GetProperty("patches")[0].GetProperty("status").GetString()=="需核对","export rescans after patch DLL deletion");
  await CompareFreshReportAsync(latest);Check(coopComparison!.StartsWith("暂时无法确认"),"two unknown states cannot claim consistency");
  File.WriteAllText(dll,"patched");File.WriteAllText(Path.Combine(mods,"broken.json"),"{");await ExportFreshReportAsync(latest);await CompareFreshReportAsync(latest);
  Check(coopComparison!.StartsWith("暂时无法确认")&&coopComparison.Contains("扫描不完整"),"incomplete scans cannot claim consistency");File.Delete(Path.Combine(mods,"broken.json"));
  JsonElement Edit(Action<JsonObject> change){var node=JsonNode.Parse(exported.GetRawText())!.AsObject();change(node);return JsonSerializer.SerializeToElement(node);}
  void Rejected(JsonElement peer,string label){try{CoopReports.Compare(exported,peer);}catch{Check(true,label);return;}Check(false,label);}
  Rejected(Edit(n=>n["mode"]="demo"),"demo and real reports cannot mix");
  Rejected(Edit(n=>n["catalogVersion"]="different"),"different catalogs cannot match");
  Rejected(Edit(n=>n["schema"]=99),"unsupported schema rejected");
  Rejected(Edit(n=>n["patches"]!.AsArray().Add(n["patches"]![0]!.DeepClone())),"duplicate patch IDs rejected");
  Rejected(Edit(n=>n.Remove("inventoryComplete")),"missing completeness field rejected");
  var extra=Edit(n=>{var p=n["patches"]![0]!.DeepClone();p["id"]="OnlyPeer";n["patches"]!.AsArray().Add(p);});
  Check(CoopReports.Compare(exported,extra).Contains("本机 未记录"),"peer-only patch records are included in comparison");
  Check(CoopReports.Compare(exported,Edit(n=>n["patches"]!.AsArray().Clear())).Contains("队友清单 未记录"),"missing peer record is a difference");
  Check(CoopReports.Compare(exported,Edit(n=>n["patches"]![0]!["filesMatch"]=false)).Contains("文件校验结果不同"),"same labels with different file verification differ");
  File.WriteAllText(latest,"{");await RunReportActionAsync(()=>CompareFreshReportAsync(latest));Check(coopComparison!.StartsWith("本次操作未完成")&&!busy&&content.IsEnabled,"invalid import clears previous success and unlocks UI");
  Directory.Move(root,Path.Combine(home,"移走的游戏"));
  string failed=Path.Combine(home,"失败不应导出.json");await RunReportActionAsync(()=>ExportFreshReportAsync(failed));
  Check(!File.Exists(failed)&&report==null&&states.Count==0&&!busy&&content.IsEnabled,"failed rescan never exports cached data and unlocks UI");
 }
}
