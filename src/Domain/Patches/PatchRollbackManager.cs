using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpirePatch;

internal sealed record PatchRollbackPlan(string Root,string Version,string Backup,string Journal,PatchJournalFile[] Entries,InstallFile[] Files,string Token);

/// <summary>Reads legacy PowerShell journals and safely restores the latest installed batch.</summary>
internal static class PatchRollbackManager
{
 static readonly HashSet<string> SupportedVersions=new(["20261102.1","20261102.2","20261102.3"],StringComparer.Ordinal);
 static string Text(JsonElement value,string name,string fallback="") =>
  value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(name,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString()??fallback:fallback;
 static bool Bool(JsonElement value,string name,bool fallback=false) =>
  value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(name,out var p)&&p.ValueKind is JsonValueKind.True or JsonValueKind.False?p.GetBoolean():fallback;

 internal static void EnsureCanInstall(string gameRoot)
 {
  string root=GameEnvironment.Root(gameRoot);
  if(!Directory.Exists(BundleInstaller.Child(root,"mods")))throw new InvalidOperationException("Mods folder not found.");
  _=Latest(root);
 }

 internal static PatchRollbackPlan Preview(string gameRoot)
 {
  string root=GameEnvironment.Root(gameRoot);
  if(!Directory.Exists(BundleInstaller.Child(root,"mods")))throw new InvalidOperationException("Mods folder not found.");
  if(GameEnvironment.Running(root))throw new InvalidOperationException("Close the game before installing or rolling back.");
  var latest=Latest(root);
  if(latest==null)return Finish(root,"","","",[],[]);
  var (backup,journal,version,record)=latest.Value;
  EnsureDisabledModsAreEnabled(root,record);
  var files=record.GetProperty("files");
  var entries=new List<PatchJournalFile>();
  var rows=new List<InstallFile>();
  var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach(var file in files.EnumerateArray())
  {
   string relative=Text(file,"relative").Replace('/','\\');
   string operation=Text(file,"operation");
   bool existed=Bool(file,"existed");
   string oldHash=Text(file,"oldHash"),newHash=Text(file,"newHash"),backupName=Text(file,"backup");
   if(!relative.StartsWith("mods\\",StringComparison.OrdinalIgnoreCase)||operation is not("write" or "remove"))
    throw new InvalidDataException("Invalid backup target or operation.");
   if(!seen.Add(relative))throw new InvalidDataException("Duplicate backup target.");
   string target=BundleInstaller.Child(root,relative);
   if(operation=="remove")
   {
    if(File.Exists(target)||Directory.Exists(target))throw new InvalidOperationException("Retired file reappeared; refusing rollback: "+target);
   }
   else if(!File.Exists(target)||!Catalog.Hash(target).Equals(newHash,StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("File changed since installation; refusing rollback: "+target);
   if(existed)
   {
    if(backupName.Length==0)throw new InvalidDataException("Original backup name is missing.");
    string original=BundleInstaller.Child(backup,backupName);
    if(!File.Exists(original)||!Catalog.Hash(original).Equals(oldHash,StringComparison.OrdinalIgnoreCase))
     throw new InvalidOperationException("Original backup checksum failed.");
   }
   entries.Add(new(relative,existed,oldHash,newHash,operation,backupName));
   rows.Add(new(relative,existed?"restore":"remove-added",existed,newHash,oldHash));
  }
  return Finish(root,version,backup,journal,entries.ToArray(),rows.ToArray());
 }

 static PatchRollbackPlan Finish(string root,string version,string backup,string journal,PatchJournalFile[] entries,InstallFile[] rows)
 {
  var payload=new{root,backup,files=rows.Select(x=>new{
   relative=x.Relative,operation=x.Operation,existed=x.Existed,oldHash=x.OldHash,newHash=x.NewHash
  }).ToArray()};
  string token=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload))));
  return new(root,version,backup,journal,entries,rows,token);
 }

 internal static InstallResult Apply(PatchRollbackPlan reviewed,string suppliedToken)
 {
  string root=GameEnvironment.Root(reviewed.Root);
  var current=Preview(root);
  if(!current.Token.Equals(reviewed.Token,StringComparison.Ordinal)||!suppliedToken.Equals(current.Token,StringComparison.Ordinal))
   throw new InvalidOperationException("Preview changed: check the new plan before confirming again.");
  if(current.Backup.Length==0)
   return new(true,"Rollback",root,"","", "", "NothingToRollback",[],0);
  if(GameEnvironment.Running(root))throw new InvalidOperationException("Close the game before installing or rolling back.");

  var recovery=new List<(PatchJournalFile Entry,string Target,bool Present,string Copy)>();
  try
  {
   // Stage every currently installed byte before restoring any original file.
   foreach(var entry in current.Entries)
   {
    string target=BundleInstaller.Child(root,entry.relative);
    bool present=File.Exists(target);
    string copy=BundleInstaller.Child(current.Backup,"undo-current-"+Guid.NewGuid().ToString("N")+".bak");
    if(present)
    {
     if(!Catalog.Hash(target).Equals(entry.newHash,StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("Rollback staging failed.");
     File.Copy(target,copy);
     if(!Catalog.Hash(copy).Equals(entry.newHash,StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("Rollback staging failed.");
    }
    recovery.Add((entry,target,present,copy));
   }

   foreach(var item in recovery)
   {
    if(item.Entry.existed)
    {
     string original=BundleInstaller.Child(current.Backup,item.Entry.backup);
     PatchFileTransaction.WriteVerified(original,item.Target,item.Entry.oldHash,current.Backup);
    }
    else if(File.Exists(item.Target))File.Delete(item.Target);
   }

   foreach(var item in recovery)
   {
    if(item.Entry.existed)
    {
     if(!File.Exists(item.Target)||!Catalog.Hash(item.Target).Equals(item.Entry.oldHash,StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("Restored file verification failed.");
    }
    else if(File.Exists(item.Target))throw new InvalidOperationException("Newly added file remains after rollback.");
   }
   PatchFileTransaction.SetJournalStatus(current.Journal,"rolled-back");
   return new(true,"Rollback",root,current.Version,current.Token,current.Backup,"RolledBack",current.Files,current.Files.Length);
  }
  catch(Exception rollbackError)
  {
   var recoveryErrors=new List<string>();
   foreach(var item in recovery.AsEnumerable().Reverse())
   {
    try
    {
     if(item.Present)PatchFileTransaction.WriteVerified(item.Copy,item.Target,item.Entry.newHash,current.Backup);
     else if(File.Exists(item.Target))File.Delete(item.Target);
    }
    catch(Exception e){recoveryErrors.Add(item.Entry.relative+": "+e.Message);}
   }
   if(recoveryErrors.Count>0)
    throw new InvalidOperationException("回滚失败，恢复当前文件也未全部完成。请保留备份并检查："+rollbackError.Message+"；恢复："+string.Join("；",recoveryErrors),rollbackError);
   throw new InvalidOperationException("回滚失败，已恢复回滚前的文件状态："+rollbackError.Message,rollbackError);
  }
 }

 static (string Backup,string Journal,string Version,JsonElement Record)? Latest(string root)
 {
  string parent=BundleInstaller.Child(root,"mod_fix_backups");
  if(!Directory.Exists(parent))return null;
  GameEnvironment.NoLinks(parent);
  foreach(string directory in Directory.EnumerateDirectories(parent,"STS2-AllFixes-20261102-*",SearchOption.TopDirectoryOnly)
   .OrderByDescending(x=>Path.GetFileName(x),StringComparer.OrdinalIgnoreCase))
  {
   GameEnvironment.NoLinks(directory);
   string journal=BundleInstaller.Child(directory,"restore.json");
   if(!File.Exists(journal))continue;
   using var document=JsonDocument.Parse(File.ReadAllText(journal));
   var record=document.RootElement.Clone();
   string status=Text(record,"status");
   if(status=="prepared")throw new InvalidOperationException("Incomplete previous transaction: inspect the retained backup before continuing.");
   if(status=="installed")
   {
    string version=Text(record,"version");
    if(!SupportedVersions.Contains(version))throw new InvalidOperationException("Unsupported backup version.");
    return(directory,journal,version,record);
   }
   if(status is not("rolled-back" or "install-failed-restored"))
    throw new InvalidDataException("未知的历史安装记录状态："+journal);
  }
  return null;
 }

 static void EnsureDisabledModsAreEnabled(string root,JsonElement journal)
 {
  string disabledRoot=BundleInstaller.Child(root,ModManager.StorageName);
  if(!Directory.Exists(disabledRoot))return;
  foreach(string slot in Directory.EnumerateDirectories(disabledRoot, "*", SearchOption.TopDirectoryOnly))
  {
   string recordPath=BundleInstaller.Child(slot,"记录.json");
   if(!File.Exists(recordPath))continue;
   using var document=JsonDocument.Parse(File.ReadAllText(recordPath));
   var record=document.RootElement;
   string status=Text(record,"Status");
   if(status!="disabled")continue;
   string prefix=Text(record,"Original").TrimEnd('\\')+"\\";
   foreach(var file in journal.GetProperty("files").EnumerateArray())
   {
    string relative=Text(file,"relative").Replace('/','\\');
    if(relative.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))
     throw new InvalidOperationException("Rollback requires disabled MOD "+Text(record,"ModId")+" to be re-enabled in 模组管理 first.");
   }
  }
 }
}
