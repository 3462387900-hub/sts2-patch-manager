using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpirePatch;

internal record PatchJournalRewrite(string Journal, string OriginalHash, string UpdatedText, string UpdatedHash);
internal record PatchJournalBackup(string Journal, string BackupFile, string OriginalHash, string UpdatedHash);
internal record PatchOrganizationTransaction(string Root, string Status, PatchFolderMove[] Moves, PatchJournalBackup[] Backups, string Error = "");

/// <summary>Relocates active installer backup paths without discarding or overwriting their originals.</summary>
internal static class PatchJournalRelocator
{
 internal const string TransactionPrefix = "SpirePatch-Organizer-";
 static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

 static string[] BackupDirs(string root)
 {
  string parent=GameEnvironment.Child(root,"mod_fix_backups");
  if(!Directory.Exists(parent))return [];
  GameEnvironment.NoLinks(parent);
  return Directory.EnumerateDirectories(parent,"STS2-AllFixes-20261102-*",SearchOption.TopDirectoryOnly)
   .OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();
 }

 internal static PatchJournalRewrite[] Plan(string root,IReadOnlyCollection<PatchFolderMove> moves)
 {
  var rewrites=new List<PatchJournalRewrite>();
  foreach(string dir in BackupDirs(root))
  {
   GameEnvironment.NoLinks(dir);
   string path=GameEnvironment.Child(dir,"restore.json");
   if(!File.Exists(path))throw new InvalidOperationException("历史安装备份缺少 restore.json：" + dir);
   byte[] bytes=File.ReadAllBytes(path);
   string text=File.ReadAllText(path); // Recognize Windows PowerShell UTF-8 BOM journals.
   using var document=JsonDocument.Parse(text);
   var record=document.RootElement;
   string status=record.GetProperty("status").GetString()??"";
   if(status=="prepared")throw new InvalidOperationException("有未完成的安装事务，请先核对：" + path);
   if(status is not("installed" or "rolled-back" or "install-failed-restored"))
    throw new InvalidOperationException("未知的历史安装记录状态：" + path);
   if(status!="installed")continue;
   if(record.TryGetProperty("version",out var version) &&
      version.GetString() is not("20261102.1" or "20261102.2" or "20261102.3"))
    throw new InvalidOperationException("历史安装记录版本无法迁移：" + path);
   var source=JsonNode.Parse(text)!.AsObject();
   var files=source["files"]?.AsArray()??throw new InvalidDataException("历史回滚记录缺少文件列表：" + path);
   bool affected=false;
   foreach(var item in files)
   {
    var row=item?.AsObject()??throw new InvalidDataException("历史文件记录异常：" + path);
    string relative=row["relative"]?.GetValue<string>()??throw new InvalidDataException("历史文件路径缺失：" + path);
    relative=relative.Replace('/','\\');
    if(!relative.StartsWith("mods\\",StringComparison.OrdinalIgnoreCase))
     throw new InvalidDataException("历史文件超出模组目录：" + relative);
    GameEnvironment.Child(root,relative);
    foreach(var move in moves)
    {
     string oldPrefix=Path.GetRelativePath(root,move.Source).Replace('/','\\')+"\\";
     string newPrefix=Path.GetRelativePath(root,move.Destination).Replace('/','\\')+"\\";
     if(!relative.StartsWith(oldPrefix,StringComparison.OrdinalIgnoreCase))continue;
     row["relative"]=newPrefix+relative[oldPrefix.Length..];
     affected=true;
     if(row["existed"]?.GetValue<bool>()==true)
     {
      string name=row["backup"]?.GetValue<string>()??throw new InvalidDataException("历史原文件备份名缺失：" + path);
      string expected=row["oldHash"]?.GetValue<string>()??throw new InvalidDataException("历史原文件哈希缺失：" + path);
      string backup=GameEnvironment.Child(dir,name);
      if(!File.Exists(backup)||!Catalog.Hash(backup).Equals(expected,StringComparison.OrdinalIgnoreCase))
       throw new InvalidOperationException("历史原始备份损坏或缺失，不可迁移：" + backup);
     }
     break;
    }
   }
   if(!affected)continue;
   string updated=source.ToJsonString(JsonOptions);
   rewrites.Add(new(path,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
    updated,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(updated)))));
  }
  return rewrites.ToArray();
 }

 internal static string Parent(string root)=>GameEnvironment.Child(root,"mod_fix_backups");

 internal static string[] Pending(string root)
 {
  string parent=Parent(root);
  if(!Directory.Exists(parent))return [];
  var found=new List<string>();
  foreach(var dir in Directory.EnumerateDirectories(parent,TransactionPrefix+"*",SearchOption.TopDirectoryOnly))
  {
   string file=GameEnvironment.Child(dir,"transaction.json");
   if(!File.Exists(file))continue; // no game files moved before the record is durable
   var state=JsonSerializer.Deserialize<PatchOrganizationTransaction>(File.ReadAllText(file))
    ??throw new InvalidDataException("整理记录损坏：" + file);
   if(state.Status is not("已完成" or "已撤销"))found.Add(dir);
  }
  return found.ToArray();
 }

 internal static void Save(string transactionDir,PatchOrganizationTransaction state)
 {
  string target=GameEnvironment.Child(transactionDir,"transaction.json");
  string temp=GameEnvironment.Child(transactionDir,Guid.NewGuid().ToString("N")+".part");
  try { File.WriteAllText(temp,JsonSerializer.Serialize(state,JsonOptions));File.Move(temp,target,true); }
  finally { if(File.Exists(temp))File.Delete(temp); }
 }

 internal static void WriteVerified(string target,string text,string originalHash,string updatedHash)
 {
  if(!File.Exists(target)||!Catalog.Hash(target).Equals(originalHash,StringComparison.OrdinalIgnoreCase))
   throw new InvalidOperationException("历史回滚记录在预览后发生变化：" + target);
  string temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
  try
  {
   File.WriteAllText(temp,text,new UTF8Encoding(false));
   if(!Catalog.Hash(temp).Equals(updatedHash,StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("历史回滚记录写入前校验失败：" + target);
   File.Move(temp,target,true);
  }
  finally {if(File.Exists(temp))File.Delete(temp);}
 }

 internal static bool SameFiles(string folder,IReadOnlyDictionary<string,string> expected)
 {
  if(!Directory.Exists(folder))return false;
  var current=ModManager.FileSnapshot(folder);
  return current.Count==expected.Count&&current.All(f=>expected.TryGetValue(f.Key,out var hash)&&hash.Equals(f.Value,StringComparison.OrdinalIgnoreCase));
 }

 internal static void Restore(string directory)
 {
  var file=GameEnvironment.Child(directory,"transaction.json");
  var state=JsonSerializer.Deserialize<PatchOrganizationTransaction>(File.ReadAllText(file))
   ??throw new InvalidDataException("整理事务记录损坏："+file);
  if(state.Status is "已完成" or "已撤销")return;
  string root=GameEnvironment.Root(state.Root);
  if(GameEnvironment.Running(root))throw new InvalidOperationException("请先退出游戏，再恢复上次中断的整理。");
  var errors=new List<string>();
  // Restore old file paths first; original bytes are stored outside the patch directories.
  foreach(var item in state.Backups)
  {
   try
   {
    string original=GameEnvironment.Child(directory,item.BackupFile);
    string journal=Path.GetFullPath(item.Journal);
    if(!journal.StartsWith(Parent(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
     throw new InvalidDataException("历史回滚记录不属于当前游戏目录。");
    if(!File.Exists(original)||!Catalog.Hash(original).Equals(item.OriginalHash,StringComparison.OrdinalIgnoreCase))
     throw new InvalidDataException("历史记录原文备份校验失败。");
    string actual=Catalog.Hash(journal);
    if(!actual.Equals(item.OriginalHash,StringComparison.OrdinalIgnoreCase) &&
       !actual.Equals(item.UpdatedHash,StringComparison.OrdinalIgnoreCase))
     throw new InvalidDataException("历史记录已被外部修改，拒绝覆盖。");
    if(actual.Equals(item.OriginalHash,StringComparison.OrdinalIgnoreCase))continue;
    string temp=journal+"."+Guid.NewGuid().ToString("N")+".tmp";
    try
    {
     File.Copy(original,temp);
     if(!Catalog.Hash(temp).Equals(item.OriginalHash,StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("历史记录恢复校验失败。");
     File.Move(temp,journal,true);
    }
    finally {if(File.Exists(temp))File.Delete(temp);}
   }
   catch(Exception e){errors.Add(item.Journal+"："+e.Message);}
  }
  // Recover in reverse order. Do not overwrite any externally changed content.
  foreach(var move in state.Moves.Reverse())
  {
   try
   {
    string mods=GameEnvironment.Child(root,"mods");
    string oldPrefix=mods.TrimEnd('\\')+"\\",targetPrefix=GameEnvironment.Child(mods,PatchOrganizer.CollectionName)+"\\";
    if(!move.Source.StartsWith(oldPrefix,StringComparison.OrdinalIgnoreCase) ||
       !move.Destination.StartsWith(targetPrefix,StringComparison.OrdinalIgnoreCase))
     throw new InvalidDataException("整理记录中的目录不属于专用范围。");
    if(Directory.Exists(move.Source) && !Directory.Exists(move.Destination))
    {
     if(!SameFiles(move.Source,move.Files))throw new InvalidDataException("原目录文件已变化。");
     continue;
    }
    if(Directory.Exists(move.Source)||!SameFiles(move.Destination,move.Files))
     throw new InvalidDataException("目标目录丢失、被占用或文件已变化。");
    Directory.Move(move.Destination,move.Source);
    if(!SameFiles(move.Source,move.Files))throw new InvalidDataException("恢复后的文件校验失败。");
   }
   catch(Exception e){errors.Add(move.Id+"："+e.Message);}
  }
  state=state with{Status=errors.Count==0?"已撤销":"需人工核对",Error=string.Join("；",errors)};
  Save(directory,state);
  if(errors.Count>0)throw new InvalidOperationException("整理恢复未全部成功：" + state.Error + "；记录：" + directory);
 }

 internal static void RecoverAll(string root)
 {
  foreach(var dir in Pending(root).OrderByDescending(x=>x,StringComparer.OrdinalIgnoreCase))
   Restore(dir);
 }
}
