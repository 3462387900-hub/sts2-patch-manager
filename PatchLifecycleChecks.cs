using System.IO;
using System.Text.Json;

namespace SpirePatch;
internal static class PatchLifecycleChecks
{
 internal static void Run(string output)
 {
  string home=Path.GetDirectoryName(output)!;
  Directory.CreateDirectory(home);
  var lines=new List<string>();
  void Check(bool good,string title)
  {
   if(!good)throw new InvalidOperationException("PATCH LIFECYCLE TEST FAILED: "+title);
   lines.Add("PASS "+title);
   File.WriteAllLines(output,lines);
  }
  void Reject(Action action,string title)
  {
   bool blocked=false;
   try{action();}catch(Exception ex)when(ex is InvalidDataException or InvalidOperationException or JsonException){blocked=true;}
   Check(blocked,title);
  }

  string root=WorkspaceChecks.Fixture(home),data=Path.Combine(home,"history-data");
  var inventory=ModManager.Scan(root);
  var initial=Directory.EnumerateFiles(Path.Combine(root,"mods"),"*",SearchOption.AllDirectories)
   .ToDictionary(p=>p,Catalog.Hash,StringComparer.OrdinalIgnoreCase);
  var first=ModHistory.CompareAndStore(root,data);
  Check(first.FirstScan&&first.Changes.Length==0,"first scan builds baseline, with no false update");
  var repeated=ModHistory.CompareAndStore(root,data);
  Check(!repeated.FirstScan&&repeated.Changes.Length==0,"unchanged scan is stable");
  string target=Path.Combine(root,"mods","海克斯测试","HextechRunes.dll");
  File.AppendAllText(target," upstream version changed");
  var updated=ModHistory.CompareAndStore(root,data);
  Check(updated.Changes.Any(x=>x.ModId=="HextechRunes"&&x.ChangedFiles>=1&&
    x.NewVersion==x.OldVersion),"DLL mutation is detected even with unchanged declared MOD version");
  var scan=Catalog.Scan(root);
  var empty=LifecycleEvidenceSource.Parse("{\"schema_version\":1,\"entries\":[]}");
  var withoutEvidence=PatchLifecycle.Evaluate(root,updated,empty,"test",scan,ModManager.Scan(root));
  Check(withoutEvidence.Patches.Any(p=>p.Patch.Id=="LargeHealDisplayFix"&&
    p.State==LifecycleState.NeedsReview&&!p.CanRetire),"changed related MOD is never declared fixed without proof");
  var now=ModHistory.CompareAndStore(root,data);
  Check(now.Changes.Length==0,"new scan commits the current local baseline");

  string patchId="local.largehealdisplayfix",hash=Catalog.Hash(target);
  string Json(string status,string decision,string targetMod="HextechRunes",string sha="")
   =>JsonSerializer.Serialize(new {
    schema_version=1,entries=new[]{new{
     patch_id=patchId,target_mod_id=targetMod,relative_path="HextechRunes.dll",
     sha256=sha.Length==0?hash:sha,version="fixture-2",decision,review_status=status,
     note="Fixture-only reviewed upstream fix behavior test",
     sources=new[]{"https://github.com/example/test/issues/1"}
    }}
   });
  Reject(()=>LifecycleEvidenceSource.Parse(Json("candidate","fixed_upstream")),
    "unapproved fixed_upstream evidence is rejected");
  Reject(()=>LifecycleEvidenceSource.Parse(Json("approved","fixed_upstream","HextechRunes","bad")),
    "invalid SHA256 evidence is rejected");
  Reject(()=>LifecycleEvidenceSource.Parse("{\"schema_version\":2,\"entries\":[]}"),
    "unsupported upstream registry schema is rejected");

  var evidence=LifecycleEvidenceSource.Parse(Json("approved","fixed_upstream"));
  var verified=PatchLifecycle.Evaluate(root,now,evidence,"fixture",Catalog.Scan(root),ModManager.Scan(root));
  var item=verified.Patches.Single(p=>p.Patch.Id=="LargeHealDisplayFix");
  Check(item.State==LifecycleState.VerifiedUpstreamFix&&item.CanRetire,
    "exact matching approved upstream hash permits reversible retirement");
  var wrong=LifecycleEvidenceSource.Parse(Json("approved","fixed_upstream","TestSolo"));
  var unrelated=PatchLifecycle.Evaluate(root,now,wrong,"fixture",Catalog.Scan(root),ModManager.Scan(root));
  Check(unrelated.Patches.Single(p=>p.Patch.Id=="LargeHealDisplayFix").State!=LifecycleState.VerifiedUpstreamFix,
    "unrelated MOD hash cannot prove an upstream fix");
  var stale=LifecycleEvidenceSource.Parse(Json("approved","fixed_upstream","HextechRunes",new string('A',64)));
  var unconfirmed=PatchLifecycle.Evaluate(root,now,stale,"fixture",Catalog.Scan(root),ModManager.Scan(root));
  Check(unconfirmed.Patches.Single(p=>p.Patch.Id=="LargeHealDisplayFix").State!=LifecycleState.VerifiedUpstreamFix,
    "unmatched exact hash cannot prove an upstream fix");
  var selected=PatchLifecycle.PreviewRetire(verified,"LargeHealDisplayFix",evidence);
  File.AppendAllText(Path.Combine(selected.Folder,"LargeHealDisplayFix.dll")," tamper");
  Reject(()=>ModManager.Apply(selected),"file changes after retirement preview block write");
  File.WriteAllText(Path.Combine(selected.Folder,"LargeHealDisplayFix.dll"),"LargeHealDisplayFix fixture");
  var confirmed=PatchLifecycle.PreviewRetire(verified,"LargeHealDisplayFix",evidence);
  ModManager.Apply(confirmed);
  var disabled=ModManager.Scan(root).Mods.Single(p=>p.Id=="LargeHealDisplayFix");
  Check(!disabled.Enabled&&!Directory.Exists(confirmed.Folder)&&File.Exists(disabled.Manifest),
    "retirement keeps files and records outside the game MOD loading folder");
  ModManager.Apply(ModManager.Preview(root,disabled.Folder,true));
  Check(ModManager.Scan(root).Mods.Single(p=>p.Id=="LargeHealDisplayFix").Enabled,
    "retired fix can be safely re-enabled from its original location");
  Check(initial.Count==Directory.EnumerateFiles(Path.Combine(root,"mods"),"*",SearchOption.AllDirectories).Count(),
    "all fixture files remain present after retiring and re-enabling");
 }
}
