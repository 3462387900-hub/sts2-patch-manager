using System.IO;
using System.Text.Json;

namespace SpirePatch;

internal static class ManagerUpdateCleanupChecks
{
 public static void Run(string output)
 {
  Directory.CreateDirectory(Path.GetDirectoryName(output)!);
  var log=new List<string>();
  void Check(bool condition,string label)
  {
   if(!condition)throw new InvalidOperationException(label);
   log.Add("PASS "+label);File.WriteAllLines(output,log);
  }
  string parent=Path.Combine(Path.GetDirectoryName(output)!,"cleanup-fixture-"+Guid.NewGuid().ToString("N"));
  string app=Path.Combine(parent,"程序 0.11.9");string data=Path.Combine(app,"数据");
  string jobs=Path.Combine(data,"管理器更新"),downloads=Path.Combine(data,"管理器下载");
  Directory.CreateDirectory(jobs);Directory.CreateDirectory(downloads);
  DateTime now=DateTime.UtcNow;
  string Job(string status,string version,DateTime time,bool proof=true)
  {
   string job=Path.Combine(jobs,Guid.NewGuid().ToString("N"));
   Directory.CreateDirectory(job);Directory.CreateDirectory(Path.Combine(job,"backup"));
   Directory.CreateDirectory(Path.Combine(job,"payload"));
   File.WriteAllText(Path.Combine(job,"backup","SpirePatch.dll"),"old binary "+version);
   File.WriteAllText(Path.Combine(job,"payload","SpirePatch.dll"),"new binary "+version);
   File.WriteAllText(Path.Combine(job,"request.json"),JsonSerializer.Serialize(new{Root=app,FromVersion="0.11.5",ToVersion=version,Files=new[]{new{Path="SpirePatch.dll",OldHash="old",NewHash="new"}}}));
   File.WriteAllText(Path.Combine(job,"result.json"),JsonSerializer.Serialize(new{status,job,version,message="test"}));
   File.WriteAllText(Path.Combine(job,"UpdateWorker.ps1"),"test");
   File.WriteAllText(Path.Combine(job,"恢复上一版.cmd"),"test");
   if(proof)File.WriteAllText(Path.Combine(job,"restart-proof.json"),JsonSerializer.Serialize(new{version,pid=42}));
   File.SetLastWriteTimeUtc(Path.Combine(job,"result.json"),time);
   return job;
  }
  try
  {
   var old=Job("complete","0.11.6",now.AddDays(-8));
   var latest=Job("complete","0.11.9",now.AddDays(-3));
   var unfinished=Job("applying","0.11.8",now.AddDays(-12));
   var uncertain=Job("complete","0.11.7",now.AddDays(-12),false);
   var recent=Job("complete","0.11.8",now.AddMinutes(-2));
   // A newer completed job is the preserved rollback, even if it hasn't aged out yet.
   var youngest=Job("complete","0.11.9",now.AddMinutes(-1));
   string zip=Path.Combine(downloads,"SpirePatch-v0.11.7-win-x64.zip");
   string currentZip=Path.Combine(downloads,"SpirePatch-v0.11.9-win-x64.zip");
   string unknownZip=Path.Combine(downloads,"team-mod-backup.zip");
   string part=Path.Combine(downloads,"SpirePatch-v0.11.8-win-x64.zip.a1.part");
   foreach(var p in new[]{zip,currentZip,unknownZip,part})File.WriteAllText(p,"user-safe");
   foreach(var p in new[]{zip,currentZip,unknownZip,part})File.SetLastWriteTimeUtc(p,now.AddDays(-5));
   string personal=Path.Combine(data,"偏好.json");File.WriteAllText(personal,"user preferences");
   string game=Path.Combine(parent,"Slay the Spire 2","mods");Directory.CreateDirectory(game);
   string mod=Path.Combine(game,"ImportantMod.dll");File.WriteAllText(mod,"player mod");
   var preview=ManagerUpdateCleanup.Preview(app,now,"0.11.9");
   Check(preview.Jobs==1&&preview.Downloads==2,"preview includes only stale completed task and known ZIP caches");
   Check(preview.Items.Any(i=>i.Path==old)&&preview.Items.Any(i=>i.Path==zip)&&preview.Items.Any(i=>i.Path==currentZip),"preview identifies precise eligible paths");
   Check(!preview.Items.Any(i=>i.Path==latest||i.Path==youngest||i.Path==unfinished||i.Path==uncertain),"latest, recent, failed and unproven jobs stay protected");
   long bytes=preview.Bytes;
   var result=ManagerUpdateCleanup.Clean(app,preview,now,"0.11.9");
   Check(result.Removed==3&&result.Errors.Length==0&&result.FreedBytes==bytes,"approved cleanup removes only three eligible items");
   Check(!Directory.Exists(old)&&!File.Exists(zip)&&!File.Exists(currentZip)&&Directory.Exists(youngest),"cleanup removes stale artifacts but keeps newest rollback");
   Check(Directory.Exists(latest)&&Directory.Exists(recent)&&Directory.Exists(unfinished)&&Directory.Exists(uncertain)&&
    File.Exists(unknownZip)&&File.Exists(part)&&File.ReadAllText(personal)=="user preferences"&&File.ReadAllText(mod)=="player mod",
    "user files, MOD files, unsupported records and unfinished downloads survive cleanup");
   Check(ManagerUpdateCleanup.Preview(app,now,"0.11.9").Items.Length==0,"cleanup is idempotent");
   var stale=Path.Combine(downloads,"SpirePatch-v0.11.6-win-x64.zip");File.WriteAllText(stale,"before");
   File.SetLastWriteTimeUtc(stale,now.AddDays(-4));
   var pending=ManagerUpdateCleanup.Preview(app,now,"0.11.9");
   File.AppendAllText(stale,"new bytes");
   try{ManagerUpdateCleanup.Clean(app,pending,now,"0.11.9");throw new Exception("stale preview accepted");}
   catch(InvalidOperationException){}
   Check(File.Exists(stale),"changed files require new approval before deletion");
  }
  finally {try{Directory.Delete(parent,true);}catch(IOException){}}
 }
}
