using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpirePatch;
public record ManagerFileManifest(int Schema,string Product,string Version,Dictionary<string,string> Files);
public record ManagerUpdateFile(string Path,string? OldHash,string? NewHash);
public record ManagerUpdateRequest(int Schema,string Root,string FromVersion,string ToVersion,int ParentId,long ParentStartTicks,ManagerUpdateFile[] Files,bool Fixture);
public record ReadyManagerUpdate(string Job,string RequestHash,string ScriptHash,ManagerUpdateRequest Request);
public static class ManagerAutoUpdate
{
 public const string ManifestName="manager-files.json";
 public const string FixtureMarker=".spirepatch-update-fixture";
 static readonly JsonSerializerOptions Json=new(){WriteIndented=true,PropertyNameCaseInsensitive=true};
 public static string MutexName(string root)=>"Local\\SpireManager-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd('\\').ToLowerInvariant())));
 static void Require(bool ok,string message){if(!ok)throw new InvalidDataException(message);}
 internal static string Child(string root,string name)
 {
  var path=BundleInstaller.Child(root,name);
  foreach(string part in name.Split('/','\\'))Require(!Regex.IsMatch(part,@"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|\z)",RegexOptions.IgnoreCase),"更新包包含保留文件名。");
  return path;
 }
 static void CheckFileName(string name)
 {
  string first=name.Replace('\\','/').Split('/')[0];
  Require(!name.Contains('\\')&&!first.Equals("数据",StringComparison.OrdinalIgnoreCase)&&!first.StartsWith('.')&&!first.Equals("mods",StringComparison.OrdinalIgnoreCase)&&!first.Equals("mod_fix_backups",StringComparison.OrdinalIgnoreCase)&&!first.Equals("SlayTheSpire2.exe",StringComparison.OrdinalIgnoreCase)&&!first.EndsWith(".part",StringComparison.OrdinalIgnoreCase),"更新包不能写入个人数据或游戏目录。");
 }
 public static ManagerFileManifest ReadManifest(string root,string? expectedVersion=null,bool fixture=false)
 {
  GameEnvironment.NoLinks(root);string file=Child(root,ManifestName);
  Require(File.Exists(file),"当前程序缺少自动更新清单，请先完整解压新版；仍可使用仅下载方式。");
  Require(new FileInfo(file).Length<=1024*1024,"程序文件清单过大。");
  var manifest=JsonSerializer.Deserialize<ManagerFileManifest>(File.ReadAllText(file),Json)??throw new InvalidDataException("程序文件清单无效。");
  Require(manifest.Schema==1&&manifest.Product=="SpirePatch"&&manifest.Files!=null&&manifest.Files.Count is >0 and <2000,"不支持的程序文件清单。");ManagerUpdates.StableVersion(manifest.Version);
  if(expectedVersion!=null)Require(manifest.Version==expectedVersion,"程序清单与版本不一致。");
  var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach(var item in manifest.Files!){CheckFileName(item.Key);Child(root,item.Key);Require(!item.Key.Equals(ManifestName,StringComparison.OrdinalIgnoreCase)&&seen.Add(item.Key)&&Regex.IsMatch(item.Value,@"\A[0-9a-fA-F]{64}\z"),"程序文件清单包含重复或无效记录。");}
  foreach(string required in new[]{"SpirePatch.exe","SpirePatch.dll","SpirePatch.runtimeconfig.json","SpirePatch.deps.json"})Require(manifest.Files.ContainsKey(required),"更新缺少程序运行文件："+required);
  if(!fixture)Require(AssemblyName.GetAssemblyName(Child(root,"SpirePatch.dll")).Version?.ToString(3)==manifest.Version,"程序实际版本与发布记录不一致。");
  return manifest;
 }
 static void VerifyFiles(string root,IReadOnlyDictionary<string,string> files,CancellationToken ct)
 {foreach(var item in files){ct.ThrowIfCancellationRequested();var path=Child(root,item.Key);Require(File.Exists(path)&&Catalog.Hash(path).Equals(item.Value,StringComparison.OrdinalIgnoreCase),"文件已变化或不完整："+item.Key);}}
 internal static void Extract(string archive,string destination,string version,CancellationToken ct)
 {
  string prefix="尖塔修补站-v"+version+"/";using var zip=ZipFile.OpenRead(archive);Require(zip.Entries.Count is >0 and <4000,"更新包条目数异常。");
  long bytes=0;var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach(var entry in zip.Entries){
   ct.ThrowIfCancellationRequested();string name=entry.FullName;Require(name.StartsWith(prefix,StringComparison.Ordinal)&&!name.Contains('\\')&&((entry.ExternalAttributes>>16)&0xF000)!=0xA000&&(entry.ExternalAttributes&0x400)==0,"更新包包含不支持的目录或链接。");
   string relative=name[prefix.Length..].TrimEnd('/');if(relative.Length==0){Require(name==prefix,"更新包根目录无效。");continue;}CheckFileName(relative);string target=Child(destination,relative);Require(paths.Add(relative),"更新包存在重复路径。");
   if(name.EndsWith('/')){Directory.CreateDirectory(target);continue;}
   bytes+=entry.Length;Require(entry.Length<=200L*1024*1024&&bytes<=600L*1024*1024,"更新包展开大小超限。");Directory.CreateDirectory(Path.GetDirectoryName(target)!);
   using var input=entry.Open();using var output=new FileStream(target,FileMode.CreateNew);var buffer=new byte[65536];long copied=0;int n;
   while((n=input.Read(buffer))>0){ct.ThrowIfCancellationRequested();copied+=n;Require(copied<=entry.Length,"更新文件长度异常。");output.Write(buffer,0,n);}Require(copied==entry.Length,"更新文件不完整。");
  }
 }
 public static Task<ReadyManagerUpdate> PrepareAsync(ManagerRelease release,string archive,string root,CancellationToken ct=default,bool fixture=false)=>Task.Run(()=>{
  ManagerUpdates.Validate(release);root=Path.GetFullPath(root).TrimEnd('\\');GameEnvironment.NoLinks(root);
  Require(!File.Exists(Path.Combine(root,"SlayTheSpire2.exe")),"请将管理器单独解压到文件夹后再自动更新。");
  Require(!fixture||File.Exists(Child(root,FixtureMarker)),"自动更新测试只能使用带标记的隔离目录。");
  var previous=ReadManifest(root,fixture?null:ManagerUpdates.CurrentVersion,fixture);
  Require(ManagerUpdates.IsNewer(release,previous.Version),"目标版本不高于当前版本，不能自动更新。");VerifyFiles(root,previous.Files,ct);
  Require(new FileInfo(archive).Length==release.Size&&Catalog.Hash(archive).Equals(release.Sha256,StringComparison.OrdinalIgnoreCase),"更新 ZIP 校验失败，请重新下载。");
  string job=Child(root,"数据/管理器更新/"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(job);string payload=Child(job,"payload");Directory.CreateDirectory(payload);
  try{
   Extract(archive,payload,release.Version,ct);var next=ReadManifest(payload,release.Version,fixture);VerifyFiles(payload,next.Files,ct);
   var actual=Directory.EnumerateFiles(payload,"*",SearchOption.AllDirectories).Select(p=>Path.GetRelativePath(payload,p).Replace('\\','/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
   Require(actual.SetEquals(next.Files.Keys.Append(ManifestName)),"更新包包含未列入清单的文件。");
   var oldFiles=new Dictionary<string,string>(previous.Files,StringComparer.OrdinalIgnoreCase){[ManifestName]=Catalog.Hash(Child(root,ManifestName))};
   var newFiles=new Dictionary<string,string>(next.Files,StringComparer.OrdinalIgnoreCase){[ManifestName]=Catalog.Hash(Child(payload,ManifestName))};
   var files=oldFiles.Keys.Union(newFiles.Keys,StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(name=>new ManagerUpdateFile(name,oldFiles.GetValueOrDefault(name),newFiles.GetValueOrDefault(name))).ToArray();
   foreach(var file in files)if(file.OldHash==null)Require(!File.Exists(Child(root,file.Path))&&!Directory.Exists(Child(root,file.Path)),"新版文件位置被已有内容占用："+file.Path);
   using var current=Process.GetCurrentProcess();var request=new ManagerUpdateRequest(1,root,previous.Version,release.Version,current.Id,current.StartTime.ToUniversalTime().Ticks,files,fixture);
   string requestPath=Child(job,"request.json");File.WriteAllText(requestPath,JsonSerializer.Serialize(request,Json),new UTF8Encoding(false));
   using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("SpirePatch.ManagerUpdateWorker.ps1")!;using var reader=new StreamReader(stream);string script=Child(job,"UpdateWorker.ps1");File.WriteAllText(script,reader.ReadToEnd(),new UTF8Encoding(true));
   string requestHash=Catalog.Hash(requestPath);
   File.WriteAllText(Child(job,"恢复上一版.cmd"),"@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%~dp0UpdateWorker.ps1\" -Request \"%~dp0request.json\" -RequestHash "+requestHash+" -Recover\r\npause\r\n",Encoding.ASCII);
   ct.ThrowIfCancellationRequested();return new ReadyManagerUpdate(job,requestHash,Catalog.Hash(script),request);
  }catch{File.WriteAllText(Child(job,"准备未完成.txt"),"本次准备没有修改程序文件。可重新下载后重试。");throw;}
 },ct);
 public static ProcessStartInfo WorkerStart(ReadyManagerUpdate ready,bool test=false,int failAfter=0,bool restartProbe=false,bool failStartup=false)
 {
  Require(!test||ready.Request.Fixture,"测试更新助手不能用于正式目录。");
  Require(Catalog.Hash(Child(ready.Job,"request.json"))==ready.RequestHash&&Catalog.Hash(Child(ready.Job,"UpdateWorker.ps1"))==ready.ScriptHash,"更新计划或助手已变化，请重新准备。");
  string powershell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe");
  var start=new ProcessStartInfo(powershell){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=ready.Job};
  start.Environment["PSModulePath"]=Path.Combine(Path.GetDirectoryName(powershell)!,"Modules");
  foreach(var arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",Child(ready.Job,"UpdateWorker.ps1"),"-Request",Child(ready.Job,"request.json"),"-RequestHash",ready.RequestHash})start.ArgumentList.Add(arg);
  if(test){start.ArgumentList.Add("-TestMode");start.ArgumentList.Add("-FailAfter");start.ArgumentList.Add(failAfter.ToString());if(restartProbe)start.ArgumentList.Add("-RestartProbe");if(failStartup)start.ArgumentList.Add("-FailStartup");}
  return start;
 }
 public static async Task StartAndWaitReadyAsync(ReadyManagerUpdate ready,CancellationToken ct=default,bool test=false,bool restartProbe=false,bool failStartup=false)
 {
  using var process=Process.Start(WorkerStart(ready,test,restartProbe:restartProbe,failStartup:failStartup))??throw new InvalidOperationException("无法启动更新助手。");
  try{for(int n=0;n<150;n++){ct.ThrowIfCancellationRequested();string signal=Child(ready.Job,"ready");if(File.Exists(signal)&&File.ReadAllText(signal).Trim()==ready.RequestHash)return;if(process.HasExited)throw new InvalidOperationException("更新助手未能就绪，请查看更新记录；当前程序未被替换。");await Task.Delay(100,ct);}throw new TimeoutException("更新助手准备超时，当前程序保持不变。");}
  catch{File.WriteAllText(Child(ready.Job,"cancel"),"cancel");throw;}
 }
}
