using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpirePatch;

public static class GitHubModInstallChecks
{
 sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
 {
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct)
  {
   ct.ThrowIfCancellationRequested();
   var response=respond(req);response.RequestMessage=req;
   return Task.FromResult(response);
  }
 }
 static byte[] PackageZip(string id,string version,string dll="new dll")
 {
  using var stream=new MemoryStream();
  using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))
  {
   using(var writer=new StreamWriter(zip.CreateEntry("RitsuLib/mod_manifest.json").Open()))
    writer.Write(JsonSerializer.Serialize(new{id,name="RitsuLib",version,has_dll=true,has_pck=false}));
   using(var writer=new StreamWriter(zip.CreateEntry("RitsuLib/ritsulib.dll").Open()))writer.Write(dll);
  }
  return stream.ToArray();
 }
 static byte[] BrokenZip(string name)
 {
  using var output=new MemoryStream();
  using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))
   using(var writer=new StreamWriter(zip.CreateEntry(name).Open()))writer.Write("bad");
  return output.ToArray();
 }
 static HttpResponseMessage Reply(byte[] bytes)=>new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};
 static string Metadata(string repo,string tag,string file,byte[] bytes,string? digest=null,string? second=null)
 {
  object Asset(string name)=>new{
   name,state="uploaded",size=bytes.LongLength,
   digest=digest??"sha256:"+Convert.ToHexString(SHA256.HashData(bytes)),
   browser_download_url="https://github.com/"+repo+"/releases/download/"+tag+"/"+Uri.EscapeDataString(name)};
  return JsonSerializer.Serialize(new{
   draft=false,prerelease=false,tag_name=tag,
   html_url="https://github.com/"+repo+"/releases/tag/"+tag,
   assets=second==null?new[]{Asset(file)}:new[]{Asset(file),Asset(second),Asset("release.variant-pack.zip")}});
 }
 static void WriteMod(string folder,string version,string dll)
 {
  Directory.CreateDirectory(folder);
  File.WriteAllText(Path.Combine(folder,"mod_manifest.json"),
   JsonSerializer.Serialize(new{id="STS2-RitsuLib",name="RitsuLib",version,has_dll=true,has_pck=false}));
  File.WriteAllText(Path.Combine(folder,"ritsulib.dll"),dll);
 }
 static ManagedMod Local(string root)=>ModManager.Scan(root).Mods.Single();
 public static async Task RunAsync(string output)
 {
  Directory.CreateDirectory(Path.GetDirectoryName(output)!);
  var lines=new List<string>();
  void Check(bool valid,string description)
  {
   if(!valid)throw new InvalidOperationException(description);
   lines.Add("PASS "+description);
   File.WriteAllLines(output,lines);
  }
  string root=Path.Combine(Path.GetDirectoryName(output)!,"github-mod-fixture-"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(root);
  File.WriteAllText(Path.Combine(root,"SlayTheSpire2.exe"),"fixture");
  File.WriteAllText(Path.Combine(root,".spirepatch-fixture"),"never launch");
  string modPath=Path.Combine(root,"mods","ritsulib");
  WriteMod(modPath,"0.6.7","old dll");
  var mod=Local(root);
  const string repo="BAKAOLC/STS2-RitsuLib",tag="v0.6.10",file="STS2.RitsuLib.0.6.10.github.zip";
  var zip=PackageZip(mod.Id,"0.6.10");
  var metadata=Metadata(repo,tag,file,zip,second:"STS2.RitsuLib.Compat.0.110.0.0.6.10.github.zip");
  int apiReads=0,downloads=0;
  using var installer=new GitHubModInstaller(new Handler(req=>{
   if(req.RequestUri!.Host=="api.github.com"){apiReads++;return Reply(Encoding.UTF8.GetBytes(metadata));}
   downloads++;return Reply(zip);
  }));
  var assets=await installer.PackagesAsync(mod,repo);
  Check(assets.Length==2&&assets.All(p=>p.FileName.EndsWith(".github.zip"))&&apiReads==1&&downloads==0,
   "only verified single-MOD GitHub ZIPs are offered, never variant packs or downloads during discovery");
  Check(assets.Select(a=>a.FileName).Distinct().Count()==2,"compatibility variants remain separately selectable");
  var selected=assets.Single(a=>a.FileName==file);
  var installed=await installer.InstallAsync(root,mod,selected);
  Check(installed.Version==tag&&Local(root).Version=="0.6.10"&&
   File.ReadAllText(Path.Combine(modPath,"ritsulib.dll"))=="new dll"&&downloads==1,
   "verified ZIP installs matching newer MOD without launching the game");
  Check(Directory.Exists(installed.BackupFolder)&&
   File.ReadAllText(Path.Combine(installed.BackupFolder,"ritsulib.dll"))=="old dll",
   "old MOD folder is preserved in a separate, persistent backup");
  var moved=GitHubModInstaller.Restore(root,Local(root),installed.BackupFolder);
  Check(Local(root).Version=="0.6.7"&&
   File.ReadAllText(Path.Combine(modPath,"ritsulib.dll"))=="old dll"&&
   Directory.Exists(moved)&&File.ReadAllText(Path.Combine(moved,"ritsulib.dll"))=="new dll",
   "backup restore preserves the replaced version as another backup");
  var invalid=selected with{Sha256=new string('0',64)};
  try{await installer.InstallAsync(root,Local(root),invalid);throw new Exception("invalid digest accepted");}
  catch(InvalidDataException){}
  Check(Local(root).Version=="0.6.7"&&File.ReadAllText(Path.Combine(modPath,"ritsulib.dll"))=="old dll",
   "wrong downloaded SHA-256 cannot change installed files");
  foreach(var bad in new[]{BrokenZip("../outside.txt"),BrokenZip("C:/windows/unsafe.txt"),PackageZip("another-mod","0.6.10")})
  {
   var folder=Path.Combine(root,"test-extract-"+Guid.NewGuid().ToString("N"));
   var archive=Path.Combine(root,Guid.NewGuid().ToString("N")+".zip");
   File.WriteAllBytes(archive,bad);
   try{GitHubModInstaller.ExtractAndVerify(archive,folder,mod.Id,tag);throw new Exception("unsafe ZIP accepted");}
   catch(InvalidDataException){}
  }
  Check(!File.Exists(Path.Combine(root,"outside.txt"))&&Local(root).Version=="0.6.7",
   "path traversal, absolute names, and wrong-MOD archive are rejected");
  var original=ModManager.FileSnapshot(modPath);
  string staging=Path.Combine(root,"nonexistent-stage");
  try{GitHubModInstaller.Commit(root,Local(root),staging,tag,original);throw new Exception("missing stage installed");}
  catch(DirectoryNotFoundException){}
  Check(Directory.Exists(modPath)&&Local(root).Version=="0.6.7",
   "failed directory swap automatically rolls back old MOD");
  string realStage=Path.Combine(root,"isolated-stage");
  WriteMod(realStage,"0.6.10","staged");
  File.AppendAllText(Path.Combine(modPath,"ritsulib.dll")," local edit");
  try{GitHubModInstaller.Commit(root,Local(root),realStage,tag,original);throw new Exception("modified MOD overwritten");}
  catch(InvalidDataException){}
  Check(File.ReadAllText(Path.Combine(modPath,"ritsulib.dll"))=="old dll local edit",
   "local MOD changes after review prevent installation");
  var missingDigest=Metadata(repo,tag,file,zip,digest:"");
  using var noDigest=new GitHubModInstaller(new Handler(_=>Reply(Encoding.UTF8.GetBytes(missingDigest))));
  Check((await noDigest.PackagesAsync(Local(root),repo)).Length==0,
   "releases without GitHub SHA-256 digests cannot be silently installed");
  try{await installer.PackagesAsync(Local(root),"not/a/valid/repository");throw new Exception("bad repo accepted");}
  catch(InvalidDataException){}
  Check(true,"invalid GitHub repository identifiers are rejected");
  // Cleanup may only affect explicitly selected, older manager-created backup slots.
  var backupParent=Path.GetDirectoryName(moved)!;
  string oldBackup=Path.Combine(backupParent,"20200101-000000000-test-old");
  WriteMod(oldBackup,"0.5.0","older dll");
  var candidates=GitHubModInstaller.CleanupCandidates(root,Local(root));
  Check(candidates.Length==1&&candidates[0]==oldBackup,
   "old backup cleanup never includes the most recent recovery slot");
  try{GitHubModInstaller.CleanupBackups(root,Local(root),new[]{moved});throw new Exception("latest backup deleted");}
  catch(InvalidDataException){}
  Check(Directory.Exists(moved)&&Directory.Exists(oldBackup),
   "cleanup refuses deleting the newest backup without touching old versions");
  try{GitHubModInstaller.CleanupBackups(root,Local(root),new[]{modPath});throw new Exception("active MOD deleted");}
  catch(InvalidDataException){}
  Check(Directory.Exists(modPath),"cleanup cannot escape the manager-owned backup directory");
  int deleted=GitHubModInstaller.CleanupBackups(root,Local(root),new[]{oldBackup});
  Check(deleted==1&&!Directory.Exists(oldBackup)&&Directory.Exists(moved)&&Directory.Exists(modPath),
   "explicit cleanup deletes only selected old versions and preserves current MOD and latest backup");
  Directory.Delete(root,true);
 }
}
