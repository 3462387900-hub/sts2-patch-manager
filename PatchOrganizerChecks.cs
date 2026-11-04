using System.IO;
using System.Text;
using System.Text.Json;

namespace SpirePatch;

internal static class PatchOrganizerChecks
{
 public static void Run(string output)
 {
  string home=Path.GetDirectoryName(output)!;
  Directory.CreateDirectory(home);
  var lines=new List<string>();
  void Check(bool ok,string label)
  {
   if(!ok)throw new InvalidOperationException(label);
   lines.Add("PASS " + label);
   File.WriteAllLines(output,lines);
  }
  void Reject(Action action,string label)
  {
   try{action();}
   catch(Exception ex)when(ex is InvalidOperationException or InvalidDataException)
   {Check(true,label);return;}
   Check(false,label);
  }
  string root=WorkspaceChecks.Fixture(home);
  string mods=Path.Combine(root,"mods");
  var original=ModManager.Scan(root).Mods.Single(m=>m.Id=="LargeHealDisplayFix");
  string source=original.Folder;
  string destination=Path.Combine(mods,PatchOrganizer.CollectionName,Path.GetFileName(source));
  var before=ModManager.FileSnapshot(source);
  string data=Path.Combine(home,"整理日志");

  var plan=PatchOrganizer.PreviewFixture(root);
  Check(plan.Moves.Length==1 && plan.Moves.Single().Id=="LargeHealDisplayFix" &&
        Directory.Exists(source) && !Directory.Exists(destination),"preview is read-only and selects independent fix");

  string dll=Path.Combine(source,"LargeHealDisplayFix.dll");
  byte[] previous=File.ReadAllBytes(dll);
  File.AppendAllText(dll,"changed");
  Reject(()=>PatchOrganizer.Apply(plan,data),"changed DLL blocks stale preview");
  File.WriteAllBytes(dll,previous);

  Directory.CreateDirectory(destination);
  Check(PatchOrganizer.PreviewFixture(root).Moves.Length==0,"occupied destination is skipped");
  Directory.Delete(destination);

  string legacy=Path.Combine(root,"mod_fix_backups","STS2-AllFixes-20261102-test");
  Directory.CreateDirectory(legacy);
  string journal=Path.Combine(legacy,"restore.json");
  string oldRelative="mods\\"+Path.GetFileName(source)+"\\LargeHealDisplayFix.dll";
  string newRelative="mods\\"+PatchOrganizer.CollectionName+"\\"+Path.GetFileName(source)+"\\LargeHealDisplayFix.dll";
  string oldHash=Catalog.Hash(dll);
  void WriteLegacy(string status)=>File.WriteAllText(journal,JsonSerializer.Serialize(new{
    version="20261102.3",status,
    files=new[]{new{relative=oldRelative,operation="write",existed=false,oldHash="",newHash=oldHash,backup=""}}
  }),new UTF8Encoding(true));
  WriteLegacy("installed");
  string originalJournalHash=Catalog.Hash(journal);

  plan=PatchOrganizer.PreviewFixture(root);
  Check(plan.Moves.Length==1&&plan.Journals?.Length==1&&Catalog.Hash(journal)==originalJournalHash,
    "active rollback history is safely planned, without modifying records");

  // A mutated journal must invalidate a previously confirmed organization plan.
  WriteLegacy("rolled-back");
  Reject(()=>PatchOrganizer.Apply(plan,data),"journal changes between preview and confirmation reject the operation");
  WriteLegacy("installed");

  // An unfinished installer batch must block organization.
  WriteLegacy("prepared");
  Reject(()=>PatchOrganizer.PreviewFixture(root),"prepared installer journal blocks migration");
  WriteLegacy("installed");

  // Main user case: installed fixes with live historical rollback references can move.
  plan=PatchOrganizer.PreviewFixture(root);
  var result=PatchOrganizer.Apply(plan,data);
  Check(result.Moved==1&&File.Exists(result.Journal)&&!Directory.Exists(source) &&
    PatchJournalRelocator.SameFiles(destination,before),"installed patch relocates with exact file integrity");
  using(var migrated=JsonDocument.Parse(File.ReadAllText(journal)))
   Check(migrated.RootElement.GetProperty("files")[0].GetProperty("relative").GetString()==newRelative,
     "live rollback journal follows relocated DLL path");
  Check(ModManager.Scan(root).Mods.Single(m=>m.Id=="LargeHealDisplayFix").Folder==destination,
    "recursive mod scanner recognizes grouped patch");
  Check(PatchOrganizer.PreviewFixture(root).Moves.Length==0,"repeat organization is a no-op");
  Check(Directory.Exists(Path.Combine(mods,"基础 库"))&&Directory.Exists(Path.Combine(mods,"依赖模组")),
    "ordinary mods were never moved");

  // Simulate interrupted transaction using the original record saved in the organizer backup.
  var transactionDir=Path.GetDirectoryName(result.Journal)!;
  var committed=JsonSerializer.Deserialize<PatchOrganizationTransaction>(File.ReadAllText(result.Journal))!;
  var pending=committed with{Status="执行中"};
  PatchJournalRelocator.Save(transactionDir,pending);
  Reject(()=>PatchOrganizer.PreviewFixture(root),"pending organization blocks further modifications");
  PatchOrganizer.Recover(root);
  Check(Directory.Exists(source)&&!Directory.Exists(destination)&&PatchJournalRelocator.SameFiles(source,before),
    "recovery restores every moved patch byte");
  Check(Catalog.Hash(journal)==originalJournalHash,"recovery restores original UTF-8 BOM backup journal bytes");
  Check(PatchJournalRelocator.Pending(root).Length==0,"recovery clears pending organization state");

  // Corrupt original installer backup for an installed replacement: refuse instead of discarding.
  File.WriteAllText(journal,JsonSerializer.Serialize(new{
   version="20261102.3",status="installed",
   files=new[]{new{relative=oldRelative,operation="write",existed=true,oldHash="DEADBEEF",newHash=oldHash,backup="missing.bak"}}
  }));
  Reject(()=>PatchOrganizer.PreviewFixture(root),"missing original replacement backup blocks migration");
 }
}
