using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpirePatch;

// Only manager-owned, completed update artifacts are eligible. Never touch MODs, save files,
// current application binaries, unrecognized update jobs, or the latest rollback plan.
internal sealed record ManagerCleanupItem(string Path,string Kind,long Bytes,DateTime LastModifiedUtc);
internal sealed record ManagerCleanupPreview(ManagerCleanupItem[] Items)
{
 public long Bytes=>Items.Sum(x=>x.Bytes);
 public int Jobs=>Items.Count(x=>x.Kind=="历史更新任务");
 public int Downloads=>Items.Count(x=>x.Kind=="旧版下载缓存");
}
internal sealed record ManagerCleanupResult(int Removed,long FreedBytes,string[] Errors);

internal static class ManagerUpdateCleanup
{
 static readonly Regex JobName=new(@"\A[a-f0-9]{32}\z",RegexOptions.Compiled|RegexOptions.IgnoreCase);
 static readonly Regex ArchiveName=new(@"\ASpirePatch-v(?<version>[0-9]+\.[0-9]+\.[0-9]+)-win-x64\.zip\z",RegexOptions.Compiled|RegexOptions.IgnoreCase);
 static readonly TimeSpan Grace=TimeSpan.FromHours(24);
 static readonly StringComparison Paths=StringComparison.OrdinalIgnoreCase;

 static bool Link(string path)=>(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0;
 static string Full(string value)=>Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
 static bool Within(string root,string child)=>Full(child).StartsWith(Full(root)+Path.DirectorySeparatorChar,Paths);
 static string? Text(JsonElement element,string property)=>
  element.ValueKind==JsonValueKind.Object&&element.TryGetProperty(property,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
 static string? ReadString(string file,string field)
 {
  if(!File.Exists(file)||Link(file)||new FileInfo(file).Length>128*1024)return null;
  using var json=JsonDocument.Parse(File.ReadAllText(file));
  return Text(json.RootElement,field);
 }
 static bool SafeTree(string root,out long size)
 {
  size=0;
  try
  {
   if(!Directory.Exists(root)||Link(root))return false;
   var stack=new Stack<string>();stack.Push(root);int count=0;
   while(stack.Count>0)
   {
    var dir=stack.Pop();
    foreach(var entry in Directory.EnumerateFileSystemEntries(dir))
    {
     if(++count>15000||!Within(root,entry)||Link(entry))return false;
     if(Directory.Exists(entry))stack.Push(entry);
     else if(File.Exists(entry))size=checked(size+new FileInfo(entry).Length);
     else return false;
    }
   }
   return true;
  }
  catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException){return false;}
 }
 static bool MatchesExpectedTree(string folder,JsonElement files,bool backup)
 {
  if(files.ValueKind!=JsonValueKind.Array||!Directory.Exists(folder)||Link(folder))return false;
  var expectedFiles=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  var expectedDirectories=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  string root=Full(folder);
  foreach(var item in files.EnumerateArray())
  {
   string? relative=Text(item,"Path");
   string? oldHash=Text(item,"OldHash");
   string? newHash=Text(item,"NewHash");
   if(string.IsNullOrWhiteSpace(relative))return false;
   bool include=backup
    ?oldHash!=null&&!string.Equals(oldHash,newHash,StringComparison.OrdinalIgnoreCase)
    :newHash!=null;
   if(!include)continue;
   string hash=backup?oldHash!:newHash!;
   if(!Regex.IsMatch(hash,@"\A[0-9a-f]{64}\z",RegexOptions.IgnoreCase))return false;
   string path=BundleInstaller.Child(folder,relative);
   if(!File.Exists(path)||Link(path)||!Catalog.Hash(path).Equals(hash,StringComparison.OrdinalIgnoreCase))return false;
   expectedFiles.Add(Path.GetFullPath(path));
   for(string? parent=Path.GetDirectoryName(path);parent!=null&&!parent.Equals(root,Paths);parent=Path.GetDirectoryName(parent))
    expectedDirectories.Add(Path.GetFullPath(parent));
  }
  var actualFiles=Directory.EnumerateFiles(folder,"*",SearchOption.AllDirectories)
   .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
  var actualDirectories=Directory.EnumerateDirectories(folder,"*",SearchOption.AllDirectories)
   .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
  return expectedFiles.SetEquals(actualFiles)&&expectedDirectories.SetEquals(actualDirectories);
 }
 static bool IsConfirmedJob(string job,string appRoot,out DateTime timestamp,out string? version)
 {
  timestamp=default;version=null;
  try
  {
   if(!JobName.IsMatch(Path.GetFileName(job))||Link(job))return false;
   string result=Path.Combine(job,"result.json"),request=Path.Combine(job,"request.json"),proof=Path.Combine(job,"restart-proof.json");
   if(ReadString(result,"status")!="complete")return false;
   version=ReadString(result,"version");
   if(version==null||!Version.TryParse(version,out _))return false;
   if(!string.Equals(Full(ReadString(result,"job")??""),Full(job),Paths))return false;
   if(!string.Equals(ReadString(request,"Root") is string path?Full(path):"",Full(appRoot),Paths))return false;
   if(ReadString(request,"ToVersion")!=version||ReadString(proof,"version")!=version)return false;
   using var requestDocument=JsonDocument.Parse(File.ReadAllText(request));
   if(!requestDocument.RootElement.TryGetProperty("Files",out var files)||
      !MatchesExpectedTree(Path.Combine(job,"payload"),files,false)||
      !MatchesExpectedTree(Path.Combine(job,"backup"),files,true))return false;
   // A working rollback needs its original request, worker, and backup payload.
   foreach(var f in new[]{"UpdateWorker.ps1","恢复上一版.cmd"})
    if(!File.Exists(Path.Combine(job,f))||Link(Path.Combine(job,f)))return false;
   if(!SafeTree(Path.Combine(job,"backup"),out var backupBytes)||backupBytes==0)return false;
   // Do not delete task folders that contain notes or other unrecognized user files.
   var knownFiles=new HashSet<string>(StringComparer.OrdinalIgnoreCase)
   {"request.json","result.json","restart-proof.json","UpdateWorker.ps1","恢复上一版.cmd","ready","cancel","error.txt","rollback-proof.json","准备未完成.txt"};
   foreach(var entry in Directory.EnumerateFileSystemEntries(job))
   {
    string name=Path.GetFileName(entry);
    if(Directory.Exists(entry))
    {if(name is not ("backup" or "payload"))return false;}
    else if(!knownFiles.Contains(name))return false;
   }
   if(!SafeTree(job,out _))return false; // Reject junctions and symlinks anywhere in this job.
   timestamp=File.GetLastWriteTimeUtc(result);
   return true;
  }
  catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException){return false;}
 }
 static string DataRoot(string appRoot)
 {
  appRoot=Full(appRoot);
  if(!Directory.Exists(appRoot)||Link(appRoot))throw new InvalidOperationException("管理器目录不存在或包含链接。");
  var data=Path.Combine(appRoot,"数据");
  if(Directory.Exists(data)&&Link(data))throw new InvalidOperationException("数据目录是链接，已停止清理。");
  return data;
 }

 public static ManagerCleanupPreview Preview(string applicationRoot,DateTime? utcNow=null,string? currentVersion=null)
 {
  var now=(utcNow??DateTime.UtcNow).ToUniversalTime();
  var current=ManagerUpdates.StableVersion(currentVersion??ManagerUpdates.CurrentVersion);
  string appRoot=Full(applicationRoot),data=DataRoot(appRoot);
  var items=new List<ManagerCleanupItem>();
  string updates=Path.Combine(data,"管理器更新");
  var confirmed=new List<(string Path,DateTime Time,string Version)>();
  if(Directory.Exists(updates)&&!Link(updates))
  {
   foreach(var path in Directory.EnumerateDirectories(updates))
    if(IsConfirmedJob(path,appRoot,out var time,out var version)&&version!=null&&
      Version.TryParse(version,out var jobVersion)&&jobVersion<=current)
     confirmed.Add((path,time,version));
   // The newest completed rollback must never be removed, regardless of age.
   var keep=confirmed.OrderByDescending(x=>x.Time).FirstOrDefault().Path;
   foreach(var task in confirmed)
   {
    if(string.Equals(task.Path,keep,Paths)||now-task.Time<Grace)continue;
    if(!SafeTree(task.Path,out var bytes))continue;
    items.Add(new(task.Path,"历史更新任务",bytes,task.Time));
   }
  }
  string downloads=Path.Combine(data,"管理器下载");
  if(Directory.Exists(downloads)&&!Link(downloads))
  {
   foreach(var file in Directory.EnumerateFiles(downloads,"*",SearchOption.TopDirectoryOnly))
   {
    try
    {
     if(Link(file))continue;
     var match=ArchiveName.Match(Path.GetFileName(file));
     if(!match.Success||!Version.TryParse(match.Groups["version"].Value,out var version))continue;
     bool oldVersion=version<current;
     bool installedVersion=version==current&&confirmed.Any(x=>x.Version==version.ToString());
     if(!oldVersion&&!installedVersion)continue;
     var info=new FileInfo(file);
     if(now-info.LastWriteTimeUtc<Grace)continue;
     items.Add(new(file,"旧版下载缓存",info.Length,info.LastWriteTimeUtc));
    }
    catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){continue;}
   }
  }
  return new(items.OrderBy(x=>x.Kind).ThenBy(x=>x.Path,StringComparer.OrdinalIgnoreCase).ToArray());
 }

 public static ManagerCleanupResult Clean(string appRoot,ManagerCleanupPreview approved,DateTime? utcNow=null,string? currentVersion=null)
 {
  var refreshed=Preview(appRoot,utcNow,currentVersion);
  // A scan is a preview, not a lasting delete authorization. Refuse if the set changed.
  if(approved.Items.Length!=refreshed.Items.Length||
     approved.Items.Where((x,i)=>x!=refreshed.Items[i]).Any())
   throw new InvalidOperationException("更新文件在预览后发生变化，请重新扫描后确认。");
  long freed=0;int count=0;var errors=new List<string>();
  string data=DataRoot(appRoot),updates=Path.Combine(data,"管理器更新"),downloads=Path.Combine(data,"管理器下载");
  if((Directory.Exists(updates)&&Link(updates))||(Directory.Exists(downloads)&&Link(downloads)))
   throw new InvalidOperationException("更新缓存目录被替换为链接，已停止清理。");
  foreach(var item in refreshed.Items)
  {
   try
   {
    if(item.Kind=="历史更新任务"&&Path.GetDirectoryName(item.Path)!.Equals(updates,Paths)&&
       JobName.IsMatch(Path.GetFileName(item.Path))&&
       IsConfirmedJob(item.Path,Full(appRoot),out var date,out _)&&date==item.LastModifiedUtc&&
       SafeTree(item.Path,out var bytes)&&bytes==item.Bytes)
      Directory.Delete(item.Path,true);
    else if(item.Kind=="旧版下载缓存"&&Path.GetDirectoryName(item.Path)!.Equals(downloads,Paths)&&
       ArchiveName.IsMatch(Path.GetFileName(item.Path))&&File.Exists(item.Path)&&!Link(item.Path)&&
       new FileInfo(item.Path).Length==item.Bytes&&File.GetLastWriteTimeUtc(item.Path)==item.LastModifiedUtc)
      File.Delete(item.Path);
    else throw new InvalidOperationException("文件状态改变，已跳过。");
    count++;freed+=item.Bytes;
   }
   catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or InvalidOperationException)
   {errors.Add(Path.GetFileName(item.Path)+"："+ex.Message);}
  }
  return new(count,freed,errors.ToArray());
 }
}
