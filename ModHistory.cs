using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpirePatch;

internal record ModBaselineFile(string ModId,string Version,string RelativePath,string Hash);
internal record ModBaseline(string Root,DateTimeOffset Time,ModBaselineFile[] Files);
internal record ModBaselineChange(string ModId,string OldVersion,string NewVersion,int ChangedFiles);
internal record ModHistoryResult(bool FirstScan,ModBaseline Current,ModBaselineChange[] Changes);

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
    result.Files.Length>6000||result.Files.Any(f=>f.ModId.Length>160||f.RelativePath.Length>400||f.Hash.Length!=64))
   throw new InvalidDataException("MOD 历史记录不属于此游戏或格式异常。");
  return result;
 }
 static string Key(ModBaselineFile f)=>f.ModId.ToLowerInvariant()+"\n"+f.RelativePath.ToLowerInvariant();
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
   // No game writes. Atomic replace of manager-owned history only after a complete scan.
   Directory.CreateDirectory(Path.GetDirectoryName(path)!);
   string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
   try{File.WriteAllText(temp,JsonSerializer.Serialize(current));File.Move(temp,path,true);}
   finally{if(File.Exists(temp))File.Delete(temp);}
   return new(previous==null,current,changes.OrderBy(x=>x.ModId,StringComparer.OrdinalIgnoreCase).ToArray());
  });
 }
}
