using System.IO;
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
   try { action(); }
   catch(InvalidOperationException) { Check(true,label); return; }
   Check(false,label);
  }
  string root=WorkspaceChecks.Fixture(home);
  string mods=Path.Combine(root,"mods");
  var original=ModManager.Scan(root).Mods.Single(m=>m.Id=="LargeHealDisplayFix");
  string source=original.Folder;
  string destination=Path.Combine(mods,PatchOrganizer.CollectionName,Path.GetFileName(source));
  var before=ModManager.FileSnapshot(source);
  string data=Path.Combine(home,"整理日志");

  // The current folder layout should not be changed by preview.
  var plan=PatchOrganizer.PreviewFixture(root);
  Check(plan.Moves.Length==1 && plan.Moves.Single().Id=="LargeHealDisplayFix" &&
        Directory.Exists(source) && !Directory.Exists(destination),"preview selects only independent fix and never changes files");

  // Changes after preview invalidate the confirmation.
  string dll=Path.Combine(source,"LargeHealDisplayFix.dll");
  byte[] previous=File.ReadAllBytes(dll);
  File.AppendAllText(dll,"changed");
  Reject(()=>PatchOrganizer.Apply(plan,data),"changed DLL blocks stale preview");
  File.WriteAllBytes(dll,previous);

  // Existing destination cannot be overwritten.
  Directory.CreateDirectory(destination);
  Check(PatchOrganizer.PreviewFixture(root).Moves.Length==0,"occupied collection folder is skipped");
  Directory.Delete(destination);

  // An installed historical backup records file locations, so skip relocation.
  string legacy=Path.Combine(root,"mod_fix_backups","STS2-AllFixes-20261102-test");
  Directory.CreateDirectory(legacy);
  File.WriteAllText(Path.Combine(legacy,"restore.json"),JsonSerializer.Serialize(new{
   status="installed",
   files=new[]{new{relative="mods\\"+Path.GetFileName(source)+"\\LargeHealDisplayFix.dll"}}
  }));
  var guarded=PatchOrganizer.PreviewFixture(root);
  Check(guarded.Moves.Length==0 && guarded.Skipped.Any(s=>s.Contains("历史安装备份")),"active backup keeps its original paths");
  File.WriteAllText(Path.Combine(legacy,"restore.json"),JsonSerializer.Serialize(new{
   status="rolled-back",
   files=new[]{new{relative="mods\\"+Path.GetFileName(source)+"\\LargeHealDisplayFix.dll"}}
  }));

  plan=PatchOrganizer.PreviewFixture(root);
  Check(plan.Moves.Length==1,"rolled back backup permits independent relocation");
  var result=PatchOrganizer.Apply(plan,data);
  Check(result.Moved==1 && File.Exists(result.Journal) && !Directory.Exists(source) &&
        ModManager.FileSnapshot(destination).Count==before.Count &&
        before.All(f=>ModManager.FileSnapshot(destination).TryGetValue(f.Key,out var hash)&&hash==f.Value),
        "confirmed move preserves every file byte and writes journal");
  Check(ModManager.Scan(root).Mods.Single(m=>m.Id=="LargeHealDisplayFix").Folder==destination,
        "existing recursive mod scanner recognizes grouped patch");
  Check(PatchOrganizer.PreviewFixture(root).Moves.Length==0,"repeat organization is a no-op");
  Check(Directory.Exists(Path.Combine(mods,"基础 库")) && Directory.Exists(Path.Combine(mods,"依赖模组")),
        "ordinary MOD directories remain unchanged");
 }
}
