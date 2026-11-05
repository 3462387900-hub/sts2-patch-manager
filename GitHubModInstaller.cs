using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpirePatch;

// Each installation requires an explicitly bound repository and a user-selected asset.
// Only stable releases with a GitHub-provided SHA256 digest are installable.
internal sealed record GitHubModPackage(
 string Repository,string Tag,string FileName,long Size,string Sha256,string DownloadUrl,string ReleaseUrl)
{
 public override string ToString()=>FileName+"  ("+(Size/1048576.0).ToString("F1")+" MB)";
}
internal sealed record GitHubModInstallResult(string ModId,string Version,string BackupFolder);

internal sealed class GitHubModInstaller : IDisposable
{
 const long MaxArchive=120L*1024*1024;
 const long MaxExtracted=300L*1024*1024;
 const int MaxEntries=800;
 const int MaxManifestBytes=128*1024;
 static readonly Regex HashPattern=new(@"\A[0-9a-fA-F]{64}\z",RegexOptions.Compiled);
 static readonly Regex FileNamePattern=new(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,180}\.zip\z",RegexOptions.IgnoreCase|RegexOptions.Compiled);
 static readonly Regex VersionPattern=new(@"\Av?([0-9]+)\.([0-9]+)(?:\.([0-9]+))?(?:\.([0-9]+))?\z",RegexOptions.IgnoreCase|RegexOptions.Compiled);
 static readonly Regex UnsafeWindowsName=new(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)",RegexOptions.IgnoreCase|RegexOptions.Compiled);
 readonly HttpClient client;
 public GitHubModInstaller(HttpMessageHandler? handler=null)
 {
  client=GitHubNetwork.CreateClient(handler);
  client.Timeout=Timeout.InfiniteTimeSpan;
  client.DefaultRequestHeaders.UserAgent.ParseAdd("SpirePatch/"+ManagerUpdates.CurrentVersion);
 }
 public void Dispose()=>client.Dispose();
 static void Require(bool condition,string message)
 {
  if(!condition)throw new InvalidDataException(message);
 }
 static Version? VersionOf(string text)
 {
  var match=VersionPattern.Match(text.Trim());
  if(!match.Success||!Version.TryParse(text.TrimStart('v','V'),out var version))return null;
  return version;
 }
 static bool IsNewer(string remote,string local)
 {
  var a=VersionOf(remote);var b=VersionOf(local);
  return a!=null&&b!=null&&a>b;
 }
 static string OfficialUrl(string repository,string tag,string file)=>
  "https://github.com/"+repository+"/releases/download/"+Uri.EscapeDataString(tag)+"/"+Uri.EscapeDataString(file);
 static void ValidatePackage(GitHubModPackage p)
 {
  Require(GitHubModUpdates.TryNormalize(p.Repository,out var normalized)&&normalized==p.Repository,
   "发布来源不是有效的 GitHub 仓库。");
  Require(VersionOf(p.Tag)!=null,"发布标签不是受支持的正式版本。");
  Require(FileNamePattern.IsMatch(p.FileName)&&!p.FileName.Contains("variant-pack",StringComparison.OrdinalIgnoreCase),
   "暂不支持此安装包类型；请从发布页手动处理组合包。");
  Require(p.Size>0&&p.Size<=MaxArchive,"模组安装包超出大小限制。");
  Require(HashPattern.IsMatch(p.Sha256),"安装包缺少可信的 SHA-256 摘要。");
  Require(p.DownloadUrl==OfficialUrl(p.Repository,p.Tag,p.FileName),"安装包下载链接与 GitHub 发布信息不一致。");
  Require(p.ReleaseUrl=="https://github.com/"+p.Repository+"/releases/tag/"+p.Tag,"安装包发布来源不一致。");
 }
 static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response,int limit,CancellationToken ct)
 {
  Require(response.Content.Headers.ContentLength is not long size||size<=limit,"GitHub 返回数据过大。");
  await using var input=await response.Content.ReadAsStreamAsync(ct);
  using var output=new MemoryStream();
  var buffer=new byte[8192];int count;
  while((count=await input.ReadAsync(buffer,ct))>0)
  {
   Require(output.Length+count<=limit,"GitHub 返回数据过大。");
   output.Write(buffer,0,count);
  }
  return output.ToArray();
 }
 public async Task<GitHubModPackage[]> PackagesAsync(ManagedMod mod,string repository,CancellationToken ct=default)
 {
  Require(GitHubModUpdates.TryNormalize(repository,out var normalized)&&normalized==repository,
   "请先手动确认正确的 GitHub 仓库。");
  Require(mod.Enabled,"暂只支持更新已启用的普通 MOD。");
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
  timeout.CancelAfter(TimeSpan.FromSeconds(25));
  using var request=new HttpRequestMessage(HttpMethod.Get,
   "https://api.github.com/repos/"+repository+"/releases/latest");
  request.Headers.Accept.ParseAdd("application/vnd.github+json");
  using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
  if((int)response.StatusCode is 403 or 429)
   throw new InvalidOperationException("GitHub API 当前受限，无法取得安装包校验信息。请稍后重试，或手动从作者发布页下载。");
  if(response.StatusCode==HttpStatusCode.NotFound)
   throw new InvalidOperationException("仓库尚未发布可自动安装的正式 Release。");
  response.EnsureSuccessStatusCode();
  using var doc=JsonDocument.Parse(await ReadBoundedAsync(response,1024*1024,timeout.Token));
  var data=doc.RootElement;
  if(data.ValueKind!=JsonValueKind.Object||!data.TryGetProperty("assets",out var assets)||
   assets.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("GitHub Release 数据格式无效。");
  Require(!data.GetProperty("draft").GetBoolean()&&!data.GetProperty("prerelease").GetBoolean(),
   "不自动安装草稿或预发布版本。");
  var tag=data.GetProperty("tag_name").GetString()??"";
  var releaseUrl=data.GetProperty("html_url").GetString()??"";
  Require(IsNewer(tag,mod.Version),"GitHub 正式版本未高于本地版本，或版本号无法可靠比较。");
  Require(releaseUrl=="https://github.com/"+repository+"/releases/tag/"+tag,
   "最新 Release 发布页与关联仓库不一致。");
  var packages=new List<GitHubModPackage>();
  foreach(var asset in assets.EnumerateArray())
  {
   if(asset.ValueKind!=JsonValueKind.Object)continue;
   var name=asset.TryGetProperty("name",out var nameField)?nameField.GetString()??"":"";
   if(!FileNamePattern.IsMatch(name)||name.Contains("variant-pack",StringComparison.OrdinalIgnoreCase))continue;
   if(asset.TryGetProperty("state",out var state)&&state.GetString()!="uploaded")continue;
   if(!asset.TryGetProperty("size",out var sizeField)||!sizeField.TryGetInt64(out long size)||size<=0||size>MaxArchive)continue;
   var digest=asset.TryGetProperty("digest",out var hashField)?hashField.GetString()??"":"";
   if(!digest.StartsWith("sha256:",StringComparison.OrdinalIgnoreCase)||!HashPattern.IsMatch(digest[7..]))continue;
   var url=asset.TryGetProperty("browser_download_url",out var urlField)?urlField.GetString()??"":"";
   var package=new GitHubModPackage(repository,tag,name,size,digest[7..].ToUpperInvariant(),url,releaseUrl);
   if(url!=OfficialUrl(repository,tag,name))continue;
   packages.Add(package);
  }
  return packages.OrderBy(p=>p.FileName,StringComparer.OrdinalIgnoreCase).ToArray();
 }
 async Task DownloadAsync(GitHubModPackage package,string archive,CancellationToken ct)
 {
  ValidatePackage(package);
  using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
  timeout.CancelAfter(TimeSpan.FromMinutes(8));
  using var request=new HttpRequestMessage(HttpMethod.Get,package.DownloadUrl);
  using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
  response.EnsureSuccessStatusCode();
  var destination=response.RequestMessage?.RequestUri;
  Require(destination!=null&&destination.Scheme=="https","只允许通过 HTTPS 下载 GitHub 发布附件。");
  Require(response.Content.Headers.ContentLength is not long size||size==package.Size,"下载文件大小与 GitHub 发布记录不一致。");
  await using var input=await response.Content.ReadAsStreamAsync(timeout.Token);
  await using var output=new FileStream(archive,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true);
  using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
  var buffer=new byte[65536];long received=0;int n;
  while((n=await input.ReadAsync(buffer,timeout.Token))>0)
  {
   received+=n;
   Require(received<=package.Size,"下载数据超出发布记录的大小。");
   hash.AppendData(buffer,0,n);
   await output.WriteAsync(buffer.AsMemory(0,n),timeout.Token);
  }
  Require(received==package.Size&&
   Convert.ToHexString(hash.GetHashAndReset()).Equals(package.Sha256,StringComparison.OrdinalIgnoreCase),
   "下载的安装包 SHA-256 与 GitHub 发布记录不一致，已取消安装。");
 }
 static string[] SafeSegments(string name)
 {
  Require(name.Length>0&&name.Length<=600&&!name.StartsWith('/')&&!name.Contains('\\')&&
   !name.Contains(':')&&!name.Contains('\0'),"ZIP 包含非法路径。");
  string clean=name.EndsWith('/')?name[..^1]:name;
  var parts=clean.Split('/');
  Require(parts.Length<=12&&parts.All(p=>p.Length>0&&p.Length<=120&&p!="."&&p!=".."&&
   !p.EndsWith('.')&&!p.EndsWith(' ')&&!UnsafeWindowsName.IsMatch(p)&&
   p.IndexOfAny(['<','>','"','|','?','*'])<0),"ZIP 包含不安全的 Windows 文件名。");
  return parts;
 }
 static string ManifestId(string path,out string version,out bool hasDll,out bool hasPck)
 {
  version="";hasDll=false;hasPck=false;
  Require(new FileInfo(path).Length<=MaxManifestBytes,"模组清单过大。");
  using var doc=JsonDocument.Parse(File.ReadAllText(path));
  var root=doc.RootElement;
  Require(root.ValueKind==JsonValueKind.Object,"MOD 清单不是 JSON 对象。");
  var id=root.TryGetProperty("id",out var key)?key.GetString()??"":"";
  version=root.TryGetProperty("version",out var ver)?ver.GetString()??"":"";
  hasDll=root.TryGetProperty("has_dll",out var dll)&&dll.ValueKind==JsonValueKind.True;
  hasPck=root.TryGetProperty("has_pck",out var pck)&&pck.ValueKind==JsonValueKind.True;
  return id;
 }
 internal static string ExtractAndVerify(string archive,string output,string modId,string tag)
 {
  Directory.CreateDirectory(output);
  var manifests=new List<string>();
  var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  long total=0;int count=0;
  using(var zip=ZipFile.OpenRead(archive))
  foreach(var entry in zip.Entries)
  {
   count++;Require(count<=MaxEntries,"ZIP 文件数量超过安全限制。");
   var parts=SafeSegments(entry.FullName);
   var kind=(entry.ExternalAttributes>>16)&0xF000;
   Require(kind is 0 or 0x8000 or 0x4000,"ZIP 包含链接或特殊文件，拒绝安装。");
   Require((entry.ExternalAttributes&0x400)==0,"ZIP 包含重解析点，拒绝安装。");
   bool directory=entry.FullName.EndsWith('/');
   Require(directory||kind!=0x4000,"ZIP 目录记录格式不正确。");
   string relative=Path.Combine(parts);
   string path=Path.Combine(output,relative);
   GameEnvironment.NoLinks(path);
   if(directory){Directory.CreateDirectory(path);continue;}
   Require(seen.Add(relative),"ZIP 包含重复文件名。");
   Require(entry.Length>=0&&entry.Length<=MaxExtracted&&total+entry.Length<=MaxExtracted,
    "ZIP 解压内容超出安全大小。");
   Directory.CreateDirectory(Path.GetDirectoryName(path)!);
   using(var input=entry.Open())
   using(var dest=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
   {
    var buffer=new byte[65536];long written=0;int n;
    while((n=input.Read(buffer,0,buffer.Length))>0)
    {
     written+=n;total+=n;
     Require(written<=entry.Length&&total<=MaxExtracted,"ZIP 实际解压内容超过安全限制。");
     dest.Write(buffer,0,n);
    }
    Require(written==entry.Length,"ZIP 文件长度与记录不一致。");
   }
   if(Path.GetFileName(path) is "mod_manifest.json" or "manifest.json")manifests.Add(path);
  }
  var found=new List<string>();
  foreach(var manifest in manifests)
  {
   string id;string version;bool hasDll,hasPck;
   try{id=ManifestId(manifest,out version,out hasDll,out hasPck);}
   catch(JsonException){continue;}
   if(string.IsNullOrWhiteSpace(id)||!(hasDll||hasPck))continue;
   Require(id.Equals(modId,StringComparison.OrdinalIgnoreCase),"ZIP 包含其他 MOD 的安装清单，不能直接替换当前 MOD。");
   Require(VersionOf(version)!=null&&VersionOf(tag)==VersionOf(version),
    "安装包中的 MOD 版本与所选 Release 不一致。");
   found.Add(manifest);
  }
  Require(found.Count==1,"ZIP 未包含唯一且匹配的 MOD 清单；可能是源码、整合包或多版本包。");
  string payload=Path.GetDirectoryName(found[0])!;
  string[] payloadFiles=Directory.GetFiles(payload,"*",SearchOption.AllDirectories);
  string dll=payloadFiles.FirstOrDefault(p=>p.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))??"";
  string pck=payloadFiles.FirstOrDefault(p=>p.EndsWith(".pck",StringComparison.OrdinalIgnoreCase))??"";
  ManifestId(found[0],out _,out var requiresDll,out var requiresPck);
  Require((!requiresDll||dll.Length>0)&&(!requiresPck||pck.Length>0),
   "安装包缺少清单要求的 DLL 或 PCK 文件。");
  return payload;
 }
 static string BackupRoot(string root,string id)
 {
  var safe=Regex.IsMatch(id,@"\A[A-Za-z0-9._-]{1,80}\z")&&!id.StartsWith('.')?id:
   Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..20];
  return GameEnvironment.Child(root,"尖塔修补站_MOD备份/"+safe);
 }
 internal static string[] Backups(string root,ManagedMod mod)
 {
  string parent=BackupRoot(GameEnvironment.Root(root),mod.Id);
  if(!Directory.Exists(parent))return [];
  return Directory.GetDirectories(parent).OrderByDescending(p=>p,StringComparer.OrdinalIgnoreCase).ToArray();
 }
 // Explicit, conservative cleanup: only named direct children of the manager's own MOD backup folder.
 // The most recent backup is always retained for recovery.
 internal static string[] CleanupCandidates(string root,ManagedMod mod)
 {
  var backups=Backups(root,mod);
  if(backups.Length<=1)return [];
  return backups.Skip(1).ToArray();
 }
 static void VerifyBackupTreeHasNoLinks(string path)
 {
  GameEnvironment.NoLinks(path);
  foreach(var entry in Directory.EnumerateFileSystemEntries(path))
  {
   GameEnvironment.NoLinks(entry);
   if(Directory.Exists(entry))VerifyBackupTreeHasNoLinks(entry);
  }
 }
 internal static int CleanupBackups(string gameRoot,ManagedMod mod,IReadOnlyCollection<string> selected)
 {
  string root=GameEnvironment.Root(gameRoot);
  return GameEnvironment.WithLock(root,()=>
  {
   if(GameEnvironment.Running(root))throw new InvalidOperationException("请先退出游戏，再清理旧 MOD 备份。");
   if(selected.Count==0)return 0;
   string parent=BackupRoot(root,mod.Id);
   GameEnvironment.NoLinks(parent);
   var allowed=CleanupCandidates(root,mod).ToHashSet(StringComparer.OrdinalIgnoreCase);
   Require(selected.Count==selected.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
    "旧版本清理清单存在重复项。");
   foreach(var candidate in selected)
   {
    string full=Path.GetFullPath(candidate);
    Require(Path.GetDirectoryName(full)?.Equals(Path.GetFullPath(parent),StringComparison.OrdinalIgnoreCase)==true &&
     allowed.Contains(full) && Directory.Exists(full),"待清理的备份不在安全范围内，或最近一次备份已改变。");
    VerifyBackupTreeHasNoLinks(full);
   }
   int removed=0;
   foreach(var candidate in selected)
   {
    Directory.Delete(candidate,true);
    removed++;
   }
   return removed;
  });
 }
 static void CheckInstalledLocation(string root,ManagedMod mod)
 {
  Require(mod.Enabled&&mod.Storage==null,"暂只支持更新已启用的普通 MOD。");
  string mods=Path.Combine(root,"mods");
  Require(Path.GetFullPath(Path.GetDirectoryName(mod.Folder)!)==Path.GetFullPath(mods),
   "该 MOD 不位于独立的 mods 子文件夹，无法安全自动替换。");
  Require(Directory.Exists(mod.Folder)&&
   Path.GetFullPath(Path.GetDirectoryName(mod.Manifest)!)==Path.GetFullPath(mod.Folder),
   "原 MOD 目录或清单位置异常。");
  GameEnvironment.NoLinks(mod.Folder);
  GameEnvironment.NoLinks(mod.Manifest);
  string version;bool dll,pck;
  Require(ManifestId(mod.Manifest,out version,out dll,out pck).Equals(mod.Id,StringComparison.OrdinalIgnoreCase),
   "原 MOD 清单 ID 已变化。");
 }
 static bool SameSnapshot(IReadOnlyDictionary<string,string> first,IReadOnlyDictionary<string,string> second)=>
  first.Count==second.Count&&first.All(pair=>second.TryGetValue(pair.Key,out var value)&&value==pair.Value);
 internal static GitHubModInstallResult Commit(string root,ManagedMod mod,string payload,string tag,
  IReadOnlyDictionary<string,string> expected)
 {
  return GameEnvironment.WithLock(root,()=>
  {
   CheckInstalledLocation(root,mod);
   if(GameEnvironment.Running(root))throw new InvalidOperationException("游戏正在运行；请关闭游戏后再安装 MOD。");
   Require(SameSnapshot(expected,ModManager.FileSnapshot(mod.Folder)),
    "下载期间原 MOD 文件发生变化，已取消替换；请刷新后重试。");
   GameEnvironment.NoLinks(payload);
   string folder=BackupRoot(root,mod.Id);
   Directory.CreateDirectory(folder);
   string backup=Path.Combine(folder,DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")+"-"+Guid.NewGuid().ToString("N"));
   Directory.Move(mod.Folder,backup);
   try{Directory.Move(payload,mod.Folder);}
   catch
   {
    if(!Directory.Exists(mod.Folder))
    {
     try{Directory.Move(backup,mod.Folder);}
     catch(Exception ex){throw new IOException("安装失败且自动回滚失败。原 MOD 仍保存在："+backup,ex);}
    }
    throw;
   }
   return new GitHubModInstallResult(mod.Id,tag,backup);
  });
 }
 public async Task<GitHubModInstallResult> InstallAsync(string gameRoot,ManagedMod mod,
  GitHubModPackage package,CancellationToken ct=default)
 {
  ValidatePackage(package);
  Require(IsNewer(package.Tag,mod.Version),"只允许安装高于本地版本的正式版本。");
  string root=GameEnvironment.Root(gameRoot);
  CheckInstalledLocation(root,mod);
  if(GameEnvironment.Running(root))throw new InvalidOperationException("请先退出游戏，再更新 MOD。");
  var snapshot=ModManager.FileSnapshot(mod.Folder);
  string parent=GameEnvironment.Child(root,"尖塔修补站_更新暂存");
  Directory.CreateDirectory(parent);
  string workspace=Path.Combine(parent,Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(workspace);
  try
  {
   string archive=Path.Combine(workspace,"download.zip");
   await DownloadAsync(package,archive,ct);
   ct.ThrowIfCancellationRequested();
   string payload=ExtractAndVerify(archive,Path.Combine(workspace,"extract"),mod.Id,package.Tag);
   ct.ThrowIfCancellationRequested();
   return Commit(root,mod,payload,package.Tag,snapshot);
  }
  finally
  {
   // A cleanup error must not mask a completed, backed-up installation.
   try{if(Directory.Exists(workspace))Directory.Delete(workspace,true);}
   catch(IOException){}
   catch(UnauthorizedAccessException){}
  }
 }
 internal static string Restore(string gameRoot,ManagedMod mod,string backup)
 {
  string root=GameEnvironment.Root(gameRoot);
  return GameEnvironment.WithLock(root,()=>
  {
   CheckInstalledLocation(root,mod);
   if(GameEnvironment.Running(root))throw new InvalidOperationException("请先退出游戏，再恢复 MOD。");
   string backupRoot=BackupRoot(root,mod.Id);
   Require(Path.GetFullPath(Path.GetDirectoryName(backup)!)==Path.GetFullPath(backupRoot)&&
    Directory.Exists(backup),"备份目录无效或不属于当前 MOD。");
   GameEnvironment.NoLinks(backup);
   // Refuse backup payloads containing links or replaced files.
   var backupFiles=ModManager.FileSnapshot(backup);
   Require(backupFiles.Count>0,"备份目录没有可恢复的文件。");
   var manifests=Directory.EnumerateFiles(backup,"*.json",SearchOption.TopDirectoryOnly)
    .Where(path=>{
     try{
      string id=ManifestId(path,out _,out bool dll,out bool pck);
      return (dll||pck)&&id.Equals(mod.Id,StringComparison.OrdinalIgnoreCase);
     }catch(JsonException){return false;}
    }).ToArray();
   Require(manifests.Length==1,"备份 MOD 的 ID 不匹配，或没有唯一的顶层 MOD 清单。");
   bool requiresDll,requiresPck;
   ManifestId(manifests[0],out _,out requiresDll,out requiresPck);
   var backupPaths=Directory.GetFiles(backup,"*",SearchOption.AllDirectories);
   Require((!requiresDll||backupPaths.Any(path=>path.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)))&&
    (!requiresPck||backupPaths.Any(path=>path.EndsWith(".pck",StringComparison.OrdinalIgnoreCase))),
    "备份缺少清单要求的 DLL 或 PCK 文件，拒绝恢复。");
   var current=Path.Combine(backupRoot,DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")+"-restore-"+Guid.NewGuid().ToString("N"));
   Directory.Move(mod.Folder,current);
   try{Directory.Move(backup,mod.Folder);}
   catch
   {
    try{Directory.Move(current,mod.Folder);}
    catch(Exception ex){throw new IOException("恢复失败且回滚失败。更新前文件仍位于："+current,ex);}
    throw;
   }
   return current;
  });
 }
}
