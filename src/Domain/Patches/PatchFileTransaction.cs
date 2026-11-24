using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SpirePatch;

internal sealed record PatchJournalFile(string relative,bool existed,string oldHash,string newHash,string operation,string backup);

/// <summary>Performs verified file writes and preserves the legacy backup/journal layout.</summary>
internal static class PatchFileTransaction
{
 static readonly JsonSerializerOptions JournalOptions=new(){WriteIndented=true};
 internal static Action<int>? BeforeApplyForTests;

 internal static string Apply(PatchInstallPlan plan)
 {
  if(plan.Changes.Length==0)return "";
  string root=GameEnvironment.Root(plan.Root);
  if(GameEnvironment.Running(root))throw new InvalidOperationException("Close the game before installing or rolling back.");
  string parent=BundleInstaller.Child(root,"mod_fix_backups");
  Directory.CreateDirectory(parent);
  string backup=BundleInstaller.Child(parent,"STS2-AllFixes-20261102-"+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff",CultureInfo.InvariantCulture)+"-"+Guid.NewGuid().ToString("N"));
  Directory.CreateDirectory(backup);
  var entries=plan.Changes.Select((file,index)=>new PatchJournalFile(
   file.Relative,file.Existed,file.OldHash,file.NewHash,file.Operation,"original-"+index+".bak")).ToArray();
  string journal=BundleInstaller.Child(backup,"restore.json");
  bool journalCreated=false;
  try
  {
   // Stage every original before the durable journal or the first game-file write.
   for(int i=0;i<plan.Changes.Length;i++)
   {
    var file=plan.Changes[i];var entry=entries[i];
    string target=BundleInstaller.Child(root,file.Relative);
    if(file.Existed)
    {
     if(!File.Exists(target)||!Catalog.Hash(target).Equals(file.OldHash,StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("Target changed since preflight.");
     string copy=BundleInstaller.Child(backup,entry.backup);
     File.Copy(target,copy);
     if(!Catalog.Hash(copy).Equals(file.OldHash,StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("Backup verification failed; no game files changed.");
    }
    else if(File.Exists(target))throw new InvalidOperationException("Target appeared since preflight.");
   }
   SaveJournal(journal,plan.Version,"prepared",entries);
   journalCreated=true;
   var attempted=new List<PatchJournalFile>();
   try
   {
    for(int i=0;i<plan.Changes.Length;i++)
    {
     var file=plan.Changes[i];var entry=entries[i];
     string target=BundleInstaller.Child(root,file.Relative);
     if(file.Existed)
     {
      if(!File.Exists(target)||!Catalog.Hash(target).Equals(file.OldHash,StringComparison.OrdinalIgnoreCase))
       throw new InvalidOperationException("File changed during install.");
     }
     else if(File.Exists(target))throw new InvalidOperationException("File appeared during install.");
     attempted.Add(entry);
     BeforeApplyForTests?.Invoke(i);
     if(file.Operation=="remove")File.Delete(target);
     else WriteVerified(file.Source,target,file.NewHash,backup);
    }
    foreach(var file in plan.All)
    {
     string target=BundleInstaller.Child(root,file.Relative);
     if(!File.Exists(target)||!Catalog.Hash(target).Equals(file.NewHash,StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("Final payload verification failed.");
    }
    SaveJournal(journal,plan.Version,"installed",entries);
    return backup;
   }
   catch(Exception installError)
   {
    var recoveryErrors=new List<string>();
    foreach(var entry in attempted.AsEnumerable().Reverse())
    {
     try
     {
      string target=BundleInstaller.Child(root,entry.relative);
      if(entry.existed)
      {
       string original=BundleInstaller.Child(backup,entry.backup);
       WriteVerified(original,target,entry.oldHash,backup);
      }
      else if(File.Exists(target))File.Delete(target);
     }
     catch(Exception recoveryError){recoveryErrors.Add(entry.relative+": "+recoveryError.Message);}
    }
    if(recoveryErrors.Count==0)SaveJournal(journal,plan.Version,"install-failed-restored",entries);
    if(recoveryErrors.Count>0)
     throw new InvalidOperationException("安装失败，自动恢复未全部完成。原始备份已保留，请勿删除。原因："+installError.Message+"；恢复："+string.Join("；",recoveryErrors)+"；备份："+backup,installError);
    throw new InvalidOperationException("安装失败，已恢复所有已修改文件："+installError.Message+"；备份："+backup,installError);
   }
  }
  catch
  {
   // A failed staging pass has not changed the game. Do not leave an orphan folder
   // that PatchJournalRelocator would mistake for an active historical transaction.
   if(!journalCreated&&Directory.Exists(backup))Directory.Delete(backup,true);
   throw;
  }
 }

 internal static void WriteVerified(string source,string target,string expectedHash,string backupFolder)
 {
  string parent=Path.GetDirectoryName(target)??throw new InvalidDataException("目标文件缺少父目录。");
  Directory.CreateDirectory(parent);
  string temporary=BundleInstaller.Child(parent,".sts2-fix-"+Guid.NewGuid().ToString("N")+".tmp");
  try
  {
   File.Copy(source,temporary);
   if(!Catalog.Hash(temporary).Equals(expectedHash,StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("Staged file checksum mismatch.");
   if(File.Exists(target))
   {
    string swap=BundleInstaller.Child(backupFolder,"swap-"+Guid.NewGuid().ToString("N")+".bak");
    File.Replace(temporary,target,swap);
   }
   else File.Move(temporary,target);
   if(!File.Exists(target)||!Catalog.Hash(target).Equals(expectedHash,StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("Write verification failed: "+target);
  }
  finally{if(File.Exists(temporary))File.Delete(temporary);}
 }

 internal static void SaveJournal(string path,string version,string status,IReadOnlyList<PatchJournalFile> files)
 {
  var record=new{version,status,files=files.Select(x=>new{
   relative=x.relative.Replace('/','\\'),existed=x.existed,oldHash=x.oldHash,newHash=x.newHash,operation=x.operation,backup=x.backup
  }).ToArray()};
  File.WriteAllText(path,JsonSerializer.Serialize(record,JournalOptions),new UTF8Encoding(true));
 }

 internal static void SetJournalStatus(string path,string status)
 {
  var node=JsonNode.Parse(File.ReadAllText(path))?.AsObject()??throw new InvalidDataException("安装回滚记录无效："+path);
  node["status"]=status;
  File.WriteAllText(path,node.ToJsonString(JournalOptions),new UTF8Encoding(true));
 }
}
