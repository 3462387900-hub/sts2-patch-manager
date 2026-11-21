using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpirePatch;

public record InstallFile(string Relative,string Operation,bool Existed,string OldHash,string NewHash);
public record InstallResult(bool Success,string Mode,string Root,string Version,string Token,string Backup,string Message,InstallFile[] Files,int Changed,InstallFile[]? CheckedFiles=null,string[]? SelectedIds=null);
public sealed record PreparedPackage(string Directory,IReadOnlyDictionary<string,string> Files);

/// <summary>Prepares and verifies the approved archive, then delegates all game writes to native C#.</summary>
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
  if(Path.IsPathRooted(relative)||relative.Split('/','\\').Any(p=>p is "" or "." or ".."||p.EndsWith('.')||p.EndsWith(' '))||relative.Any(c=>c<32||":<>\"|?*".Contains(c)))
   throw new InvalidDataException("文件路径超出允许范围。");
  string full=Path.GetFullPath(Path.Combine(root,relative));
  string prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
  if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("文件路径超出允许范围。");
  for(string? p=full;p!=null;p=Path.GetDirectoryName(p))
   if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)
    throw new InvalidDataException("安装缓存不支持目录链接，请将管理器解压到普通文件夹。");
  return full;
 }

 public async Task<PreparedPackage> PrepareAsync(string zipPath,CancellationToken ct=default)
 {
  return await Task.Run(()=>{
   ct.ThrowIfCancellationRequested();
   var info=new FileInfo(zipPath);
   if(!info.Exists||info.Length!=SupportedSize||Catalog.Hash(zipPath)!=SupportedHash)
    throw new InvalidDataException("此修复包未通过本版管理器的安装校验，不能执行。");
   Directory.CreateDirectory(DataRoot);
   string cached=Child(DataRoot,"下载/"+SupportedBundle.FileName);
   Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
   if(!Path.GetFullPath(zipPath).Equals(cached,StringComparison.OrdinalIgnoreCase)&&
      (!File.Exists(cached)||Catalog.Hash(cached)!=SupportedHash))
   {
    string temporary=cached+"."+Guid.NewGuid().ToString("N")+".part";
    try
    {
     File.Copy(zipPath,temporary);
     if(Catalog.Hash(temporary)!=SupportedHash)throw new InvalidDataException("导入期间 ZIP 发生变化。");
     File.Move(temporary,cached,true);
    }
    finally{if(File.Exists(temporary))File.Delete(temporary);}
   }
   string stage=Child(DataRoot,"准备记录/"+Guid.NewGuid().ToString("N"));
   Directory.CreateDirectory(stage);
   ExtractArchive(cached,stage,ct);
   string package=Child(stage,PackageFolder);
   using var checks=JsonDocument.Parse(File.ReadAllText(Child(package,"checksums.json")));
   var files=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
   var listed=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   foreach(var f in checks.RootElement.EnumerateArray())
   {
    string path=f.GetProperty("path").GetString()!,hash=f.GetProperty("sha256").GetString()!;
    Child(package,path);
    if(!Regex.IsMatch(hash,@"\A[0-9A-Fa-f]{64}\z")||!listed.Add(path))
     throw new InvalidDataException("包内校验清单无效。");
    // Common.ps1 is checked directly against its ZIP entry and is never written to disk.
    if(path.Equals("Common.ps1",StringComparison.OrdinalIgnoreCase))continue;
    files.Add(path,hash);
   }
   files.Add("checksums.json",Catalog.Hash(Child(package,"checksums.json")));
   // The legacy Common.ps1 ZIP member is checksummed in-memory, not extracted or executed.
   // Its archive entry remains unchanged for compatibility with existing patch bundles.
   var result=new PreparedPackage(package,files);
   VerifyStage(result);
   if(Catalog.Hash(cached)!=SupportedHash)throw new InvalidDataException("准备期间修复包发生变化。");
   ct.ThrowIfCancellationRequested();
   return result;
  },ct);
 }

 internal static void ExtractArchive(string archive,string stage,CancellationToken ct=default)
 {
  using var zip=ZipFile.OpenRead(archive);
  var checksEntry=zip.GetEntry(PackageFolder+"/checksums.json")??throw new InvalidDataException("压缩包缺少校验清单。");
  var expected=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  using(var checkStream=checksEntry.Open())
  using(var checkReader=new StreamReader(checkStream,Encoding.UTF8,true))
  using(var checks=JsonDocument.Parse(checkReader.ReadToEnd()))
  {
   foreach(var item in checks.RootElement.EnumerateArray())
   {
    string relative=item.GetProperty("path").GetString()!,hash=item.GetProperty("sha256").GetString()!;
    Child(stage,PackageFolder+"/"+relative);
    if(!Regex.IsMatch(hash,@"\A[0-9A-Fa-f]{64}\z")||!expected.TryAdd(relative,hash))
     throw new InvalidDataException("包内校验清单无效。");
   }
  }
  var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  var opaqueVerified=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  long bytes=0;
  if(zip.Entries.Count>2000)throw new InvalidDataException("压缩包文件数超限。");
  foreach(var entry in zip.Entries)
  {
   ct.ThrowIfCancellationRequested();
   string name=entry.FullName;
   if(name.Contains('\\')||!name.StartsWith(PackageFolder+"/",StringComparison.Ordinal)||
      ((entry.ExternalAttributes>>16)&0xF000)==0xA000)
    throw new InvalidDataException("压缩包包含不支持的路径或链接。");
   bool directory=name.EndsWith('/');
   string relativeEntry=name[(PackageFolder.Length+1)..];
   string path=Child(stage,directory?name.TrimEnd('/'):name);
   if(!names.Add(path))throw new InvalidDataException("压缩包存在重复路径。");
   if(directory){Directory.CreateDirectory(path);continue;}
   bytes+=entry.Length;
   if(entry.Length>100*1024*1024||bytes>200*1024*1024)throw new InvalidDataException("压缩包展开大小超限。");
   if(relativeEntry.Equals("Common.ps1",StringComparison.OrdinalIgnoreCase))
   {
    if(!expected.TryGetValue(relativeEntry,out var expectedHash))
     throw new InvalidDataException("Common.ps1 缺少可信校验值。");
    using var input=entry.Open();
    string actual=Convert.ToHexString(SHA256.HashData(input));
    if(!actual.Equals(expectedHash,StringComparison.OrdinalIgnoreCase))
     throw new InvalidDataException("旧版安装脚本 ZIP 成员校验失败。");
    opaqueVerified.Add(relativeEntry);
    continue;
   }
   Directory.CreateDirectory(Path.GetDirectoryName(path)!);
   using var source=entry.Open();using var output=new FileStream(path,FileMode.CreateNew);
   var buffer=new byte[65536];long written=0;int n;
   while((n=source.Read(buffer))>0)
   {
    ct.ThrowIfCancellationRequested();written+=n;
    if(written>entry.Length)throw new InvalidDataException("压缩包文件长度异常。");
    output.Write(buffer,0,n);
   }
   if(written!=entry.Length)throw new InvalidDataException("压缩包文件不完整。");
  }
  foreach(string opaque in expected.Keys.Where(x=>x.Equals("Common.ps1",StringComparison.OrdinalIgnoreCase)))
   if(!opaqueVerified.Contains(opaque))throw new InvalidDataException("压缩包缺少 Common.ps1 校验成员。");
 }
 public static void VerifyStage(PreparedPackage package)
 {
  var actual=Directory.EnumerateFiles(package.Directory,"*",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=0})
   .Select(p=>Path.GetRelativePath(package.Directory,p).Replace('\\','/'))
   .ToHashSet(StringComparer.OrdinalIgnoreCase);
  if(!actual.SetEquals(package.Files.Keys))throw new InvalidDataException("安装缓存文件清单发生变化，请重新准备。");
  foreach(var file in package.Files)
   if(!Catalog.Hash(Child(package.Directory,file.Key)).Equals(file.Value,StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("安装缓存校验失败，请重新准备。");
 }

 public async Task<InstallResult> RunAsync(PreparedPackage package,string gameRoot,string mode,string? token=null,
  IEnumerable<string>? selectedIds=null)
 {
  if(mode is not("Preview" or "Install" or "RollbackPreview" or "Rollback"))throw new ArgumentException("Unknown installer mode");
  if(mode is "Install" or "Rollback" && !Regex.IsMatch(token??"",@"\A[0-9A-F]{64}\z"))
   throw new InvalidOperationException("请先检查改动并确认。");
  if(mode is "RollbackPreview" or "Rollback")
  {
   if(selectedIds?.Any()==true)throw new InvalidOperationException("回滚按最近一次批次执行，不接受拆分选择。");
  }
  await Task.Run(()=>VerifyStage(package));
  string root=GameEnvironment.Root(gameRoot);
  if(!Directory.Exists(Child(root,"mods")))throw new InvalidOperationException("Mods folder not found.");
  PatchSelection? selection=null;
  if(selectedIds!=null)
  {
   if(mode is not("Preview" or "Install"))throw new InvalidOperationException("回滚按最近一次批次执行，不接受拆分选择。");
   selection=await Task.Run(()=>SelectionPlanner.Resolve(root,selectedIds));
  }
  string[] selected=selection?.Included??[];
  string packageVersion;
  using(var packageDocument=JsonDocument.Parse(File.ReadAllText(Child(package.Directory,"plan.json"))))
   packageVersion=packageDocument.RootElement.GetProperty("version").GetString()??throw new InvalidDataException("补丁包缺少版本号。");
  string job=Child(DataRoot,"操作记录/"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(job);
  string request=Child(job,"request.json"),resultPath=Child(job,"result.json"),logPath=Child(job,"执行日志.txt");
  await File.WriteAllTextAsync(request,JsonSerializer.Serialize(new{token=token??"",selectedIds=selected}));
  try
  {
   var result=await Task.Run(()=>GameEnvironment.WithLock(root,()=>{
    if(GameEnvironment.Running(root))throw new InvalidOperationException("Close the game before installing or rolling back.");
    // Revalidate the staged archive after acquiring the game-folder lock, immediately before planning or rollback.
    VerifyStage(package);
    if(mode is "Preview" or "Install")
    {
     PatchRollbackManager.EnsureCanInstall(root);
     var plan=PatchInstallPlanner.Create(package,root,selected);
     var changed=plan.Changes.Select(ToInstallFile).ToArray();
     var checkedFiles=plan.All.Select(ToInstallFile).ToArray();
     if(mode=="Preview")
      return new InstallResult(true,mode,root,plan.Version,plan.Token,"","ReadyToInstall",changed,changed.Length,checkedFiles,selected);
     if(!string.Equals(token,plan.Token,StringComparison.Ordinal))
      throw new InvalidOperationException("Preview changed: check the new plan before confirming again.");
     string backup=PatchFileTransaction.Apply(plan);
     var after=PatchInstallPlanner.Create(package,root,selected);
     if(after.Changes.Length!=0)throw new InvalidOperationException("Final verification failed.");
     return new InstallResult(true,mode,root,plan.Version,plan.Token,backup,
      changed.Length==0?"AlreadyInstalled":"Installed",changed,changed.Length,checkedFiles,selected);
    }
    var rollback=PatchRollbackManager.Preview(root);
    if(mode=="RollbackPreview")
     return new InstallResult(true,mode,root,packageVersion,rollback.Token,rollback.Backup,
      rollback.Backup.Length==0?"NothingToRollback":"ReadyToRollback",rollback.Files,rollback.Files.Length,[],[]);
    return PatchRollbackManager.Apply(rollback,token??"") with{Version=packageVersion};
   }));
   await WriteResultAsync(resultPath,result);
   await File.WriteAllTextAsync(logPath,FormatLog(result));
   return result;
  }
  catch(Exception error)
  {
   string message=Friendly(error.Message);
   await File.WriteAllTextAsync(resultPath,JsonSerializer.Serialize(new{success=false,mode,message},new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(true));
   await File.WriteAllTextAsync(logPath,"C# 原生补丁安装器\n操作："+mode+"\n失败："+error+"\n");
   throw new InvalidOperationException(message+"\n\n日志："+job,error);
  }
 }

 static InstallFile ToInstallFile(PatchPlannedFile file)=>new(file.Relative,file.Operation,file.Existed,file.OldHash,file.NewHash);

 static async Task WriteResultAsync(string path,InstallResult result)
 {
  var payload=new
  {
   success=result.Success,mode=result.Mode,root=result.Root,version=result.Version,token=result.Token,backup=result.Backup,
   message=result.Message,files=result.Files.Select(ToJson).ToArray(),changed=result.Changed,
   checkedFiles=(result.CheckedFiles??[]).Select(ToJson).ToArray(),selectedIds=result.SelectedIds??[]
  };
  await File.WriteAllTextAsync(path,JsonSerializer.Serialize(payload,new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(true));
  static object ToJson(InstallFile file)=>new{relative=file.Relative,operation=file.Operation,existed=file.Existed,oldHash=file.OldHash,newHash=file.NewHash};
 }

 static string FormatLog(InstallResult result)=>
  "C# 原生补丁安装器\n操作："+result.Mode+"\n结果："+result.Message+"\n版本："+result.Version+
  "\n变更文件："+result.Changed+"\n备份："+result.Backup+"\n";

 static string Friendly(string error)
 {
  (string Key,string Text)[] messages=[
   ("Mod is disabled","此补丁已停用，请先在模组管理中启用，再检查更新。"),
   ("Preview changed","文件状态已变化，请重新检查改动后确认。"),
   ("Close the game","请先完全退出游戏，再安装或回滚。"),
   ("Unsupported existing file","检测到未收录的文件版本，已停止覆盖。"),
   ("Dependency version mismatch","游戏或前置模组版本不匹配，已停止安装。"),
   ("Required dependency mod missing","缺少整合包所需的前置模组。"),
   ("Required original mod missing","缺少对应的原模组。"),
   ("Duplicate mod ID","检测到重复模组 ID，请先处理重复副本。"),
   ("File changed since installation","文件已被后续更新，已停止回滚，避免覆盖新版本。"),
   ("Original backup checksum failed","备份文件缺失或校验失败，已停止回滚。"),
   ("Incomplete previous transaction","发现上次未完成的安装记录，请保留备份并先检查执行日志。"),
   ("Another manager transaction","另一个管理器正在操作该游戏目录，请等待完成。"),
   ("Game executable not found","请选择包含 SlayTheSpire2.exe 的游戏根目录。"),
   ("Mods folder not found","所选目录缺少 mods 文件夹。")];
  var match=messages.FirstOrDefault(x=>error.Contains(x.Key,StringComparison.OrdinalIgnoreCase));
  return match.Text==null?error:match.Text+"\n"+error;
 }
}
