using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpirePatch;
public record InstallFile(string Relative,string Operation,bool Existed,string OldHash,string NewHash);
public record InstallResult(bool Success,string Mode,string Root,string Version,string Token,string Backup,string Message,InstallFile[] Files,int Changed,InstallFile[]? CheckedFiles=null,string[]? SelectedIds=null);
public sealed record PreparedPackage(string Directory,IReadOnlyDictionary<string,string> Files);

public sealed class BundleInstaller
{
 public const string SupportedHash="6AD6F7340F3CDB2FE28042CDB3084737A4F7AA798E125EBFDF23AB2EAE8B30FC";
 public const long SupportedSize=695637;
 public const string PackageFolder="STS2-Manager-Payload-20261102-v3";
 public static readonly OnlineBundle SupportedBundle=new(PackageFolder+".zip",SupportedHash,SupportedSize,
  OnlineSource.RepositoryUrl+"/releases/download/v2026.11.02.3/"+PackageFolder+".zip",
  "https://api.github.com/repos/"+OnlineSource.Repository+"/releases/assets/699687356",
  OnlineSource.RepositoryUrl+"/releases/tag/v2026.11.02.3","manager_payload");
 public static bool Supports(OnlineBundle b)=>b.Size==SupportedSize&&b.Sha256.Equals(SupportedHash,StringComparison.OrdinalIgnoreCase);
 public string DataRoot{get;}
 static readonly JsonSerializerOptions JsonOptions=new(){PropertyNameCaseInsensitive=true};
 public BundleInstaller(string dataRoot){DataRoot=Path.GetFullPath(dataRoot);}
 public static string Child(string root,string relative)
 {
  if(Path.IsPathRooted(relative)||relative.Split('/','\\').Any(p=>p is "" or "." or ".."||p.EndsWith('.')||p.EndsWith(' '))||relative.Any(c=>c<32||":<>\"|?*".Contains(c)))throw new InvalidDataException("文件路径超出允许范围。");
  string full=Path.GetFullPath(Path.Combine(root,relative)),prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
  if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("文件路径超出允许范围。");
  for(string? p=full;p!=null;p=Path.GetDirectoryName(p))
   if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("安装缓存不支持目录链接，请将管理器解压到普通文件夹。");
  return full;
 }
 public async Task<PreparedPackage> PrepareAsync(string zipPath,CancellationToken ct=default)
 {
  return await Task.Run(()=>{
   ct.ThrowIfCancellationRequested();
   if(new FileInfo(zipPath).Length!=SupportedSize||Catalog.Hash(zipPath)!=SupportedHash)throw new InvalidDataException("此修复包未通过本版管理器的安装校验，不能执行。");
   Directory.CreateDirectory(DataRoot);
   string cached=Child(DataRoot,"下载/"+SupportedBundle.FileName);Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
   if(!Path.GetFullPath(zipPath).Equals(cached,StringComparison.OrdinalIgnoreCase)&&(!File.Exists(cached)||Catalog.Hash(cached)!=SupportedHash)){
    string temporary=cached+"."+Guid.NewGuid().ToString("N")+".part";
    try{File.Copy(zipPath,temporary);if(Catalog.Hash(temporary)!=SupportedHash)throw new InvalidDataException("导入期间 ZIP 发生变化。");File.Move(temporary,cached,true);}
    finally{if(File.Exists(temporary))File.Delete(temporary);}
   }
   string stage=Child(DataRoot,"准备记录/"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
   ExtractArchive(cached,stage,ct);
   string package=Child(stage,PackageFolder);
   using var checks=JsonDocument.Parse(File.ReadAllText(Child(package,"checksums.json")));
   var files=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
   foreach(var f in checks.RootElement.EnumerateArray()){
    string path=f.GetProperty("path").GetString()!,hash=f.GetProperty("sha256").GetString()!;Child(package,path);
    if(!Regex.IsMatch(hash,@"\A[0-9A-Fa-f]{64}\z")||!files.TryAdd(path,hash))throw new InvalidDataException("包内校验清单无效。");
   }
   files.Add("checksums.json",Catalog.Hash(Child(package,"checksums.json")));
   if(!files.ContainsKey("Common.ps1")||!files.ContainsKey("plan.json"))throw new InvalidDataException("缺少安装器文件。");
   var result=new PreparedPackage(package,files);VerifyStage(result);
   if(Catalog.Hash(cached)!=SupportedHash)throw new InvalidDataException("准备期间修复包发生变化。");
   ct.ThrowIfCancellationRequested();return result;
  },ct);
 }
 internal static void ExtractArchive(string archive,string stage,CancellationToken ct=default)
 {
  using var zip=ZipFile.OpenRead(archive);var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long bytes=0;
  if(zip.Entries.Count>2000)throw new InvalidDataException("压缩包文件数超限。");
  foreach(var entry in zip.Entries){
   ct.ThrowIfCancellationRequested();string name=entry.FullName;
   if(name.Contains('\\')||!name.StartsWith(PackageFolder+"/",StringComparison.Ordinal)||((entry.ExternalAttributes>>16)&0xF000)==0xA000)throw new InvalidDataException("压缩包包含不支持的路径或链接。");
   bool directory=name.EndsWith('/');string path=Child(stage,directory?name.TrimEnd('/'):name);
   if(!names.Add(path))throw new InvalidDataException("压缩包存在重复路径。");
   if(directory){Directory.CreateDirectory(path);continue;}
   bytes+=entry.Length;if(entry.Length>100*1024*1024||bytes>200*1024*1024)throw new InvalidDataException("压缩包展开大小超限。");
   Directory.CreateDirectory(Path.GetDirectoryName(path)!);
   using var input=entry.Open();using var output=new FileStream(path,FileMode.CreateNew);var buffer=new byte[65536];long written=0;int n;
   while((n=input.Read(buffer))>0){ct.ThrowIfCancellationRequested();written+=n;if(written>entry.Length)throw new InvalidDataException("压缩包文件长度异常。");output.Write(buffer,0,n);}
   if(written!=entry.Length)throw new InvalidDataException("压缩包文件不完整。");
  }
 }
 public static void VerifyStage(PreparedPackage package)
 {
  var actual=Directory.EnumerateFiles(package.Directory,"*",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=0}).Select(p=>Path.GetRelativePath(package.Directory,p).Replace('\\','/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
  if(!actual.SetEquals(package.Files.Keys))throw new InvalidDataException("安装缓存文件清单发生变化，请重新准备。");
  foreach(var f in package.Files)if(!Catalog.Hash(Child(package.Directory,f.Key)).Equals(f.Value,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("安装缓存校验失败，请重新准备。");
 }
 public async Task<InstallResult> RunAsync(PreparedPackage package,string gameRoot,string mode,string? token=null,IEnumerable<string>? selectedIds=null)
 {
  if(mode is not("Preview" or "Install" or "RollbackPreview" or "Rollback"))throw new ArgumentException("Unknown installer mode");
  if(mode is "Install" or "Rollback" && !Regex.IsMatch(token??"",@"\A[0-9A-F]{64}\z"))throw new InvalidOperationException("请先检查改动并确认。");
  await Task.Run(()=>VerifyStage(package));
  PatchSelection? selection=null;
  if(selectedIds!=null){if(mode is not("Preview" or "Install"))throw new InvalidOperationException("回滚按最近一次批次执行，不接受拆分选择。");selection=await Task.Run(()=>SelectionPlanner.Resolve(gameRoot,selectedIds));}
  string job=Child(DataRoot,"操作记录/"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(job);
  string bridge=Child(job,"InstallerBridge.ps1"),request=Child(job,"request.json"),result=Child(job,"result.json");
  using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("SpirePatch.InstallerBridge.ps1")!)
  using(var reader=new StreamReader(stream))await File.WriteAllTextAsync(bridge,await reader.ReadToEndAsync(),new UTF8Encoding(true));
  using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("SpirePatch.SelectionBridge.ps1")!)
  using(var reader=new StreamReader(stream))await File.WriteAllTextAsync(Child(job,"SelectionBridge.ps1"),await reader.ReadToEndAsync(),new UTF8Encoding(true));
  await File.WriteAllTextAsync(request,JsonSerializer.Serialize(new{token=token??"",selectedIds=selection?.Included}));
  var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=job};
  foreach(var arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",bridge,"-Mode",mode,"-PackageDirectory",package.Directory,"-GameRoot",Path.GetFullPath(gameRoot),"-ResultPath",result,"-RequestPath",request})start.ArgumentList.Add(arg);
  using var process=Process.Start(start)??throw new InvalidOperationException("无法启动安装器。");
  var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
  // Never kill a transaction on a UI timeout: its rollback must be allowed to finish.
  await process.WaitForExitAsync();string log=await stdout+"\n"+await stderr;await File.WriteAllTextAsync(Child(job,"执行日志.txt"),log);
  if(!File.Exists(result))throw new InvalidOperationException("安装器未返回结果。日志："+job);
  using var json=JsonDocument.Parse(await File.ReadAllTextAsync(result));
  if(process.ExitCode!=0||!json.RootElement.GetProperty("success").GetBoolean())throw new InvalidOperationException(Friendly(json.RootElement.GetProperty("message").GetString()??"未知错误")+"\n\n日志："+job);
  var parsed=JsonSerializer.Deserialize<InstallResult>(json.RootElement.GetRawText(),JsonOptions)??throw new InvalidDataException("安装器返回格式无效。");
  if(!parsed.Root.Equals(Path.GetFullPath(gameRoot).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)||parsed.Mode!=mode)throw new InvalidDataException("安装器返回目录或操作不一致。");
  return parsed;
 }
 static string Friendly(string error)
 {
  (string Key,string Text)[] messages=[("Mod is disabled","此补丁已停用，请先在模组管理中启用，再检查更新。"),("Preview changed","文件状态已变化，请重新检查改动后确认。"),("Close the game","请先完全退出游戏，再安装或回滚。"),("Unsupported existing file","检测到未收录的文件版本，已停止覆盖。"),("Dependency version mismatch","游戏或前置模组版本不匹配，已停止安装。"),("Required dependency mod missing","缺少整合包所需的前置模组。"),("Required original mod missing","缺少对应的原模组。"),("Duplicate mod ID","检测到重复模组 ID，请先处理重复副本。"),("File changed since installation","文件已被后续更新，已停止回滚，避免覆盖新版本。"),("Original backup checksum failed","备份文件缺失或校验失败，已停止回滚。"),("Incomplete previous transaction","发现上次未完成的安装记录，请保留备份并先检查执行日志。"),("Another manager transaction","另一个管理器正在操作该游戏目录，请等待完成。"),("Game executable not found","请选择包含 SlayTheSpire2.exe 的游戏根目录。"),("Mods folder not found","所选目录缺少 mods 文件夹。")];
  return messages.FirstOrDefault(x=>error.Contains(x.Key,StringComparison.OrdinalIgnoreCase)) is var m&&m.Text!=null?m.Text+"\n"+error:error;
 }
}
