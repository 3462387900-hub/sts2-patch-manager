using System.IO;
using System.Text.Json;
namespace SpirePatch;
internal static class SafetyChecks
{
 public static void Run(string output){
  var home=Path.Combine(Path.GetDirectoryName(output)!,"安全恢复-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(home);
  var lines=new List<string>();
  void Check(bool ok,string label){if(!ok)throw new InvalidOperationException(label);lines.Add("PASS "+label);File.WriteAllLines(output,lines);}
  foreach(var stage in new[]{"empty","prepared-before","prepared-after","enabled-before-record","conflict","modified","missing"}){
   string root=WorkspaceChecks.Fixture(home);var mod=ModManager.Scan(root).Mods.Single(m=>m.Id=="TestSolo");
   string slot=Path.Combine(root,ModManager.StorageName,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(slot);
   string recordPath=Path.Combine(slot,"记录.json"),payload=Path.Combine(slot,"内容");
   var record=new DisabledModRecord(mod.Id,Path.GetRelativePath(root,mod.Folder),ModManager.FileSnapshot(mod.Folder),stage=="enabled-before-record"?"disabled":"prepared");
   if(stage!="empty")File.WriteAllText(recordPath,JsonSerializer.Serialize(record));
   if(stage is "prepared-after" or "conflict")Directory.Move(mod.Folder,payload);
   if(stage=="conflict")Directory.CreateDirectory(mod.Folder);
   if(stage=="modified")File.AppendAllText(Path.Combine(mod.Folder,"TestSolo.dll"),"changed");
   if(stage=="missing")Directory.Move(mod.Folder,Path.Combine(home,"retained-"+Guid.NewGuid().ToString("N")));
   var before=Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Where(p=>p!=recordPath).ToDictionary(p=>p,Catalog.Hash);
   var scan=ModManager.Scan(root);
   if(stage is "conflict" or "modified" or "missing"){
    Check(scan.Issues.Count>0,stage+" fails closed");Check(JsonSerializer.Deserialize<DisabledModRecord>(File.ReadAllText(recordPath))!.Status==record.Status,stage+" journal unchanged");
   }else{
    Check(scan.Issues.Count==0,stage+" recovers without scan error");
    Check(scan.Mods.Single(m=>m.Id==mod.Id).Enabled==(stage!="prepared-after"),stage+" effective state");
    Check(ModManager.Scan(root).Issues.Count==0&&Catalog.Scan(root)!=null,stage+" repeated scans succeed");
    if(stage!="empty")Check(JsonSerializer.Deserialize<DisabledModRecord>(File.ReadAllText(recordPath))!.Status==(stage=="prepared-after"?"disabled":stage=="prepared-before"?"failed-restored":"enabled"),stage+" journal finalized");
   }
   Check(before.All(f=>File.Exists(f.Key)&&Catalog.Hash(f.Key)==f.Value),stage+" payload bytes preserved");
   if(stage is "prepared-before" or "enabled-before-record"){
    ModManager.Apply(ModManager.Preview(root,mod.Folder,false));var disabled=ModManager.Scan(root).Mods.Single(m=>m.Id==mod.Id);ModManager.Apply(ModManager.Preview(root,disabled.Folder,true));Check(ModManager.Scan(root).Issues.Count==0,stage+" subsequent toggle works");
   }
  }
  string report=Path.Combine(home,"report.json");
  void Reject(string json,string label){File.WriteAllText(report,json);bool rejected=false;try{CoopReports.Read(report);}catch(Exception e)when(e is InvalidDataException or JsonException){rejected=true;}Check(rejected,label);}
  File.WriteAllText(report,"{\"patches\":[]}");Check(CoopReports.Read(report).GetProperty("patches").GetArrayLength()==0,"normal report accepted");
  foreach(var encoding in new System.Text.Encoding[]{new System.Text.UTF8Encoding(true),System.Text.Encoding.Unicode}){
   File.WriteAllText(report,"{\"patches\":[]}",encoding);Check(CoopReports.Read(report).GetProperty("patches").GetArrayLength()==0,"BOM report accepted: "+encoding.WebName);
  }
  Reject(new string(' ',CoopReports.MaxReportBytes+1),"oversized file rejected");
  Reject(JsonSerializer.Serialize(new{patches=Enumerable.Range(0,CoopReports.MaxPatches+1).ToArray()}),"too many entries rejected");
  Reject(JsonSerializer.Serialize(new{reason=new string('x',CoopReports.MaxStringLength+1)}),"long string rejected");
  Reject(new string('[',33)+"0"+new string(']',33),"deep JSON rejected");
  Reject("{","malformed JSON rejected");
 }
}
