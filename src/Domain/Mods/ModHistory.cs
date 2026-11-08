using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpirePatch;

internal record ModBaselineFile(string ModId,string Version,string RelativePath,string Hash);
internal record ModBaseline(string Root,DateTimeOffset Time,ModBaselineFile[] Files,
 ModBaselineChange[]? PendingReview=null);
internal record ModBaselineChange(string ModId,string OldVersion,string NewVersion,int ChangedFiles);
internal record ModHistoryResult(bool FirstScan,ModBaseline Current,ModBaselineChange[] Changes)
{
 // Persistent review items do not disappear just because a second scan found no new changes.
 internal ModBaselineChange[] PendingReview=>Current.PendingReview??[];
}

/// <summary>Private, local-only snapshots of mod metadata and DLL/PCK hashes; never copies mod files.</summary>
internal static class ModHistory
{
 static string HistoryPath(string root,string dataRoot)
 {
  string key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())));
  return BundleInstaller.Child(Path.GetFullPath(dataRoot),"MOD更新历史/"+key+".json");
 }
 internal static ModBaseline Capture(string root)
 {
  root=GameEnvironment.Root(root);
  var inventory=ModManager.Scan(root);
  if(inventory.Issues.Count>0)throw new InvalidDataException("模组清单存在问题，不能记录更新历史："+string.Join("；",inventory.Issues.Take(3)));
  var files=new List<ModBaselineFile>();
  var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach(var mod in inventory.Mods.Where(m=>m.Enabled))
  {
   if(!ids.Add(mod.Id))throw new InvalidDataException("MOD ID 重复："+mod.Id);
   GameEnvironment.NoLinks(mod.Folder);
   var modsRoot=GameEnvironment.Child(root,"mods");
   bool loose=mod.Folder.TrimEnd('\\').Equals(modsRoot.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase);
   var options=new EnumerationOptions{RecurseSubdirectories=!loose,AttributesToSkip=FileAttributes.ReparsePoint,IgnoreInaccessible=false};
   string manifestBase=Path.GetFileNameWithoutExtension(mod.Manifest);
   // A manifest directly inside Mods does not own the other MOD subfolders or unrelated loose DLLs.
   var otherFolders=inventory.Mods.Where(m=>m.Enabled&&m!=mod&&
     m.Folder.StartsWith(mod.Folder.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase))
     .Select(m=>m.Folder.TrimEnd('\\')+"\\").ToArray();
   var monitored=Directory.EnumerateFiles(mod.Folder,"*",options)
    .Where(p=>!otherFolders.Any(other=>p.StartsWith(other,StringComparison.OrdinalIgnoreCase)))
    .Where(p=>p.Equals(mod.Manifest,StringComparison.OrdinalIgnoreCase)||
     ((p.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)||p.EndsWith(".pck",StringComparison.OrdinalIgnoreCase)) &&
      (!loose||Path.GetFileNameWithoutExtension(p).Equals(manifestBase,StringComparison.OrdinalIgnoreCase)||
       Path.GetFileNameWithoutExtension(p).Equals(mod.Id,StringComparison.OrdinalIgnoreCase))))
    .OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).Take(501).ToArray();
   if(monitored.Length>500)throw new InvalidDataException("MOD 文件过多，暂停生成历史："+mod.Id);
   foreach(string p in monitored)
   {
    GameEnvironment.NoLinks(p);
    string relative=Path.GetRelativePath(mod.Folder,p).Replace('\\','/');
    files.Add(new(mod.Id,mod.Version,relative,Catalog.Hash(p)));
    if(files.Count>6000)throw new InvalidDataException("模组文件清单过大，无法安全记录历史。");
   }
  }
  return new(root,DateTimeOffset.UtcNow,files.OrderBy(f=>f.ModId,StringComparer.OrdinalIgnoreCase)
   .ThenBy(f=>f.RelativePath,StringComparer.OrdinalIgnoreCase).ToArray());
 }
 static ModBaseline? Read(string root,string path)
 {
  if(!File.Exists(path))return null;
  var file=new FileInfo(path);
  if(file.Length>2*1024*1024)throw new InvalidDataException("本地 MOD 历史记录过大。");
  var result=JsonSerializer.Deserialize<ModBaseline>(File.ReadAllText(path))
    ??throw new InvalidDataException("MOD 历史记录无法读取。");
  if(!string.Equals(result.Root,root,StringComparison.OrdinalIgnoreCase)||
    result.Files.Length>6000||result.Files.Any(f=>f.ModId.Length>160||f.RelativePath.Length>400||f.Hash.Length!=64)||
   (result.PendingReview?.Length??0)>6000||
   (result.PendingReview?.Any(x=>string.IsNullOrWhiteSpace(x.ModId)||x.ModId.Length>160||
    x.OldVersion.Length>200||x.NewVersion.Length>200||x.ChangedFiles<0)??false))
   throw new InvalidDataException("MOD 历史记录不属于此游戏或格式异常。");
  return result;
 }
 static string Key(ModBaselineFile f)=>f.ModId.ToLowerInvariant()+"\n"+f.RelativePath.ToLowerInvariant();
 static void Save(string path,ModBaseline data)
 {
  // Write only the manager-owned record, atomically replacing the old baseline.
  Directory.CreateDirectory(Path.GetDirectoryName(path)!);
  string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
  try{File.WriteAllText(temp,JsonSerializer.Serialize(data));File.Move(temp,path,true);}
  finally{if(File.Exists(temp))File.Delete(temp);}
 }
 internal static void AcknowledgeReview(string root,string dataRoot,string modId)
 {
  root=GameEnvironment.Root(root);
  GameEnvironment.WithLock(root,()=>{
   string path=HistoryPath(root,dataRoot);
   var previous=Read(root,path)??throw new InvalidOperationException("尚未生成扫描基线，请先扫描。");
   if(previous.PendingReview?.Any(x=>x.ModId.Equals(modId,StringComparison.OrdinalIgnoreCase))!=true)
    throw new InvalidOperationException("此 MOD 没有待核对的更新记录。");
   var current=Capture(root);
   if(!previous.Files.SequenceEqual(current.Files))
    throw new InvalidOperationException("MOD 文件已发生变化，请先重新扫描，再标记已核对。");
   Save(path,previous with{PendingReview=previous.PendingReview!
    .Where(x=>!x.ModId.Equals(modId,StringComparison.OrdinalIgnoreCase)).ToArray()});
   return true;
  });
 }
 internal static ModHistoryResult CompareAndStore(string root,string dataRoot)
 {
  root=GameEnvironment.Root(root);
  return GameEnvironment.WithLock<ModHistoryResult>(root,()=>
  {
   var current=Capture(root);
   string path=HistoryPath(root,dataRoot);
   var previous=Read(root,path);
   var changes=new List<ModBaselineChange>();
   if(previous!=null)
   {
    var old=previous.Files.ToDictionary(Key,StringComparer.OrdinalIgnoreCase);
    var now=current.Files.ToDictionary(Key,StringComparer.OrdinalIgnoreCase);
    var groups=previous.Files.Select(f=>f.ModId).Concat(current.Files.Select(f=>f.ModId))
      .Distinct(StringComparer.OrdinalIgnoreCase);
    foreach(var id in groups)
    {
     var entries=old.Where(e=>e.Value.ModId.Equals(id,StringComparison.OrdinalIgnoreCase)).ToArray();
     var currentEntries=now.Where(e=>e.Value.ModId.Equals(id,StringComparison.OrdinalIgnoreCase)).ToArray();
     int count=entries.Count(x=>!now.TryGetValue(x.Key,out var right)||
      !right.Hash.Equals(x.Value.Hash,StringComparison.OrdinalIgnoreCase))+
      currentEntries.Count(x=>!old.ContainsKey(x.Key));
     var oldVersion=entries.FirstOrDefault().Value?.Version??"(未安装)";
     var newVersion=currentEntries.FirstOrDefault().Value?.Version??"(已移除)";
     if(count>0||oldVersion!=newVersion)changes.Add(new(id,oldVersion,newVersion,count));
    }
   }
   // Keep pending reviews until the user explicitly acknowledges them. A second scan
   // alone cannot turn a previously changed MOD into "already reviewed".
   var pending=(previous?.PendingReview??[])
    .ToDictionary(x=>x.ModId,StringComparer.OrdinalIgnoreCase);
   foreach(var change in changes)
   {
    if(pending.TryGetValue(change.ModId,out var old))
     pending[change.ModId]=change with{OldVersion=old.OldVersion,
      ChangedFiles=Math.Max(old.ChangedFiles,change.ChangedFiles)};
    else pending[change.ModId]=change;
   }
   current=current with{PendingReview=pending.Values
    .OrderBy(x=>x.ModId,StringComparer.OrdinalIgnoreCase).ToArray()};
   Save(path,current);
   return new(previous==null,current,changes.OrderBy(x=>x.ModId,StringComparer.OrdinalIgnoreCase).ToArray());
  });
 }
}
