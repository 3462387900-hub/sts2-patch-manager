using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace SpirePatch;

// A browser-downloaded ZIP is an untrusted local input, not a trusted Steam receipt.
// This importer never downloads, executes, updates, or overwrites an existing MOD.
public sealed record WorkshopZipPlan(
 string Root,string ZipPath,string ArchiveSha256,string ModId,string ModName,
 string FolderName,string Destination,string[] Dependencies,int FileCount,long Bytes);

public static class WorkshopZipImport
{
 const int MaxEntries=10000;
 const long MaxFileBytes=2L*1024*1024*1024;
 const long MaxTotalBytes=4L*1024*1024*1024;
 const long MaxZipBytes=4L*1024*1024*1024;
 const long MaxManifestBytes=256*1024;
 static readonly HashSet<string> ForbiddenExtensions=new(StringComparer.OrdinalIgnoreCase)
  {".exe",".com",".bat",".cmd",".ps1",".vbs",".vbe",".js",".jse",".hta",".scr",".msi",".msp",".lnk",".url"};

 sealed record ZipItem(ZipArchiveEntry Entry,string Path);
 sealed record Inspected(WorkshopZipPlan Plan,ZipItem[] Files);

 static void ValidatePart(string part)
 {
  if(part.Length is 0 or > 120||part is "." or ".."||part.EndsWith('.')||part.EndsWith(' ')||
    part.Any(ch=>ch<32||"<>:\"|?*\\/".Contains(ch)))
   throw new InvalidDataException("ZIP 含有 Windows 不安全路径："+part);
  string stem=part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
  if(stem is "CON" or "PRN" or "AUX" or "NUL" ||
     (stem.Length==4&&stem.StartsWith("COM")&&stem[3]>='1'&&stem[3]<='9')||
     (stem.Length==4&&stem.StartsWith("LPT")&&stem[3]>='1'&&stem[3]<='9'))
   throw new InvalidDataException("ZIP 包含系统保留文件名："+part);
 }

 static string CheckedPath(string original)
 {
  if(string.IsNullOrWhiteSpace(original)||original.StartsWith('/')||original.Contains('\\')||
    original.Contains('\0'))throw new InvalidDataException("ZIP 含有非法路径。");
  string path=original.TrimEnd('/');
  foreach(string part in path.Split('/'))ValidatePart(part);
  if(path.Length>220)throw new InvalidDataException("ZIP 文件路径过长。");
  return path;
 }

 static string Sha256(FileStream stream)
 {
  stream.Position=0;
  string result=Convert.ToHexString(SHA256.HashData(stream));
  stream.Position=0;return result;
 }

 static Inspected Inspect(string root,string zipPath,FileStream stream,ZipArchive archive)
 {
  if(GameEnvironment.Running(root))throw new InvalidOperationException("请先退出游戏再导入 MOD。");
  if(archive.Entries.Count==0||archive.Entries.Count>MaxEntries)throw new InvalidDataException("ZIP 条目数量超出允许范围。");
  var allPaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  var files=new List<ZipItem>();
  long total=0;
  foreach(var entry in archive.Entries)
  {
   string path=CheckedPath(entry.FullName);
   bool directory=entry.FullName.EndsWith('/');
   int unixType=(entry.ExternalAttributes>>16)&0xF000;
   if(unixType==0xA000||(entry.ExternalAttributes&(int)FileAttributes.ReparsePoint)!=0)
    throw new InvalidDataException("ZIP 包含链接文件，拒绝导入。");
   if(!allPaths.Add(path))throw new InvalidDataException("ZIP 含大小写重复的路径："+path);
   if(directory)continue;
   string extension=Path.GetExtension(path);
   if(ForbiddenExtensions.Contains(extension))throw new InvalidDataException("ZIP 包含不支持的脚本或程序："+path);
   if(entry.Length<0||entry.Length>MaxFileBytes||total>MaxTotalBytes-entry.Length)
    throw new InvalidDataException("ZIP 解压文件超出大小上限。");
   if(entry.Length>1024*1024&&entry.CompressedLength>0&&entry.Length/entry.CompressedLength>1000)
    throw new InvalidDataException("ZIP 压缩比例异常，拒绝导入。");
   total+=entry.Length;
   files.Add(new(entry,path));
  }
  if(files.Count==0)throw new InvalidDataException("ZIP 未包含 MOD 文件。");
  var fileNames=files.Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
  foreach(var file in files)
  {
   var parts=file.Path.Split('/');
   for(int i=1;i<parts.Length;i++)
    if(fileNames.Contains(string.Join("/",parts.Take(i))))
     throw new InvalidDataException("ZIP 中的文件与目录发生冲突。");
  }
  var manifestFiles=new List<(ZipItem Item,string Id,string Name,string[] Dependencies,bool Dll,bool Pck)>();
  foreach(var item in files.Where(x=>x.Path.EndsWith(".json",StringComparison.OrdinalIgnoreCase)&&x.Entry.Length<=MaxManifestBytes))
  {
   try
   {
    using var reader=item.Entry.Open();
    using var doc=JsonDocument.Parse(reader);
    var obj=doc.RootElement;
    if(obj.ValueKind!=JsonValueKind.Object||!obj.TryGetProperty("id",out var id)||id.ValueKind!=JsonValueKind.String||
       !(obj.TryGetProperty("has_dll",out _)||obj.TryGetProperty("has_pck",out _)))continue;
    string modId=id.GetString()??"";
    if(string.IsNullOrWhiteSpace(modId)||modId.Length>120)throw new InvalidDataException("MOD 清单 ID 无效。");
    bool dll=obj.TryGetProperty("has_dll",out var d)&&d.ValueKind==JsonValueKind.True;
    bool pck=obj.TryGetProperty("has_pck",out var p)&&p.ValueKind==JsonValueKind.True;
    if(!dll&&!pck)throw new InvalidDataException("MOD 清单没有启用 DLL 或 PCK 声明。");
    string name=obj.TryGetProperty("name",out var n)&&n.ValueKind==JsonValueKind.String?n.GetString()??modId:modId;
    var deps=new List<string>();
    if(obj.TryGetProperty("dependencies",out var array)&&array.ValueKind==JsonValueKind.Array)
     foreach(var dep in array.EnumerateArray())
     {
      if(dep.ValueKind==JsonValueKind.String){if(!string.IsNullOrWhiteSpace(dep.GetString()))deps.Add(dep.GetString()!);}
      else if(dep.ValueKind==JsonValueKind.Object&&
              !(dep.TryGetProperty("optional",out var optional)&&optional.ValueKind==JsonValueKind.True)&&
              dep.TryGetProperty("id",out var depId)&&depId.ValueKind==JsonValueKind.String)
       {if(!string.IsNullOrWhiteSpace(depId.GetString()))deps.Add(depId.GetString()!);}
     }
    manifestFiles.Add((item,modId,name,deps.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),dll,pck));
   }
   catch(JsonException) { if(item.Path.EndsWith("mod_manifest.json",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("MOD 清单 JSON 无法解析。"); }
  }
  if(manifestFiles.Count!=1)throw new InvalidDataException("ZIP 必须恰好包含一个有效 MOD 清单，不能批量导入或猜测目录。");
  var manifest=manifestFiles[0];
  string prefix=manifest.Item.Path.Contains('/')?manifest.Item.Path[..(manifest.Item.Path.LastIndexOf('/')+1)]:"";
  if(files.Any(f=>!f.Path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)))
   throw new InvalidDataException("ZIP 有不属于此 MOD 目录的文件，拒绝混合导入。");
  var relativeFiles=files.Select(f=>new ZipItem(f.Entry,f.Path[prefix.Length..])).ToArray();
  if(relativeFiles.Any(f=>f.Path.Length==0))throw new InvalidDataException("ZIP 缺少有效的 MOD 文件名。");
  if(manifest.Dll&&!relativeFiles.Any(f=>f.Path.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))||
     manifest.Pck&&!relativeFiles.Any(f=>f.Path.EndsWith(".pck",StringComparison.OrdinalIgnoreCase)))
   throw new InvalidDataException("MOD 清单声明的 DLL/PCK 文件不存在。");
  string folder=prefix.Length==0?manifest.Id:prefix.TrimEnd('/').Split('/').Last();
  ValidatePart(folder);
  if(folder.Equals("_SpirePatch_Fixes",StringComparison.OrdinalIgnoreCase)||folder.StartsWith(".spirepatch",StringComparison.OrdinalIgnoreCase))
   throw new InvalidDataException("ZIP 使用了管理器保留目录名。");
  string target=GameEnvironment.Child(root,"mods/"+folder);
  if(File.Exists(target)||Directory.Exists(target))throw new InvalidOperationException("目标 MOD 目录已存在，禁止自动覆盖："+target);
  var inventory=ModManager.Scan(root);
  if(inventory.Issues.Count!=0)throw new InvalidOperationException("现有 MOD 清单存在异常，请先解决后导入。");
  if(inventory.Mods.Any(m=>m.Id.Equals(manifest.Id,StringComparison.OrdinalIgnoreCase)))
   throw new InvalidOperationException("已存在相同 MOD ID（包含停用区），请先手动核对："+manifest.Id);
  string hash=Sha256(stream);
  var plan=new WorkshopZipPlan(root,zipPath,hash,manifest.Id,manifest.Name,folder,target,
   manifest.Dependencies,relativeFiles.Length,total);
  return new(plan,relativeFiles);
 }

 static Inspected OpenAndInspect(string root,string zipPath,FileStream stream,ZipArchive archive)=>
  Inspect(root,zipPath,stream,archive);

 public static WorkshopZipPlan Preview(string gameRoot,string zipPath)=>GameEnvironment.WithLock(GameEnvironment.Root(gameRoot),()=>{
  string root=GameEnvironment.Root(gameRoot);
  string zip=Path.GetFullPath(zipPath);
  GameEnvironment.NoLinks(zip);
  if(!File.Exists(zip)||new FileInfo(zip).Length>MaxZipBytes)throw new InvalidDataException("ZIP 不存在或体积超出上限。");
  using var stream=new FileStream(zip,FileMode.Open,FileAccess.Read,FileShare.Read);
  using var archive=new ZipArchive(stream,ZipArchiveMode.Read,true);
  return OpenAndInspect(root,zip,stream,archive).Plan;
 });

 public static void Apply(WorkshopZipPlan approved)=>GameEnvironment.WithLock(GameEnvironment.Root(approved.Root),()=>{
  string root=GameEnvironment.Root(approved.Root),zip=Path.GetFullPath(approved.ZipPath);
  GameEnvironment.NoLinks(zip);
  if(!File.Exists(zip)||new FileInfo(zip).Length>MaxZipBytes)throw new InvalidDataException("ZIP 已消失或体积异常。");
  using var stream=new FileStream(zip,FileMode.Open,FileAccess.Read,FileShare.Read);
  using var archive=new ZipArchive(stream,ZipArchiveMode.Read,true);
  var checkedImport=OpenAndInspect(root,zip,stream,archive);
  var now=checkedImport.Plan;
  if(!now.ArchiveSha256.Equals(approved.ArchiveSha256,StringComparison.OrdinalIgnoreCase)||
     !now.ModId.Equals(approved.ModId,StringComparison.Ordinal)||
     !now.Destination.Equals(approved.Destination,StringComparison.OrdinalIgnoreCase)||
     now.FileCount!=approved.FileCount||now.Bytes!=approved.Bytes)
   throw new InvalidOperationException("ZIP 或目标目录已发生变化，请重新预览。");

  string stage=GameEnvironment.Child(root,".spirepatch-import-"+Guid.NewGuid().ToString("N"));
  string mods=GameEnvironment.Child(root,"mods");
  bool moved=false;
  try
  {
   Directory.CreateDirectory(stage);
   foreach(var item in checkedImport.Files)
   {
    string target=GameEnvironment.Child(stage,item.Path);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    using var source=item.Entry.Open();
    using var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None);
    byte[] buffer=new byte[128*1024];long written=0;int count;
    while((count=source.Read(buffer))>0)
    {
     written+=count;
     if(written>item.Entry.Length||written>MaxFileBytes)throw new InvalidDataException("ZIP 实际解压长度异常。");
     output.Write(buffer,0,count);
    }
    if(written!=item.Entry.Length)throw new InvalidDataException("ZIP 文件长度不符，已停止导入。");
   }
   if(!Sha256(stream).Equals(approved.ArchiveSha256,StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("导入期间 ZIP 发生变化，已停止安装。");
   if(File.Exists(now.Destination)||Directory.Exists(now.Destination))
    throw new InvalidOperationException("导入期间目标文件夹被占用，禁止覆盖。");
   Directory.CreateDirectory(mods);
   Directory.Move(stage,now.Destination);
   moved=true;
  }
  finally {if(!moved&&Directory.Exists(stage))Directory.Delete(stage,true);}
  return true;
 });
}
