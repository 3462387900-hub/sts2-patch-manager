using System.IO;
using System.Text.Json;

namespace SpirePatch;
public static class CoreChecks
{
 public static void Run(string output)
 {
  var lines=new List<string>();
  void Check(bool yes,string label){if(!yes)throw new InvalidOperationException(label);lines.Add("PASS "+label);}
  Check(Catalog.All.Length==17,"catalog contains the 17 reviewed fix components");
  Check(Catalog.Demo().Count(s=>s.Status=="待修复")==2,"demo has two related fixes pending");
  Check(Catalog.Demo(true).All(s=>s.Status=="已安装"),"demo completion state updates without game access");
  var root=Path.Combine(Path.GetDirectoryName(output)!,"scan-test-中文 空格-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"mods"));File.WriteAllText(Path.Combine(root,"SlayTheSpire2.exe"),"");
  var empty=Catalog.Scan(root);Check(empty.ModCount==0,"empty mod directory scans without errors");Check(empty.Patches.Single(p=>p.Patch.Id=="Act4FinalAscent").Status=="不适用","missing original mod is not recommended");
  var mod=Path.Combine(root,"mods","renamed folder");Directory.CreateDirectory(mod);File.WriteAllText(Path.Combine(mod,"manifest.json"),"{\"id\":\"Act4FinalAscent\",\"has_dll\":true}");File.WriteAllText(Path.Combine(mod,"Act4FinalAscent.dll"),"unknown build");
  var changed=Catalog.Scan(root);Check(changed.Patches.Single(p=>p.Patch.Id=="Act4FinalAscent").Status=="需核对","unknown DLL is not marked compatible");
  var duplicate=Path.Combine(root,"mods","duplicate");Directory.CreateDirectory(duplicate);File.Copy(Path.Combine(mod,"manifest.json"),Path.Combine(duplicate,"manifest.json"));Check(Catalog.Scan(root).Issues.Any(i=>i.Contains("重复")),"duplicate mod IDs are reported");
  Check(File.ReadAllText(Path.Combine(mod,"Act4FinalAscent.dll"))=="unknown build","scanner leaves game files unchanged");
  var settings=Path.Combine(root,"manager-data");
  using(var github=new GitHubModUpdates(settings,root))
  {
   Check(GitHubModUpdates.TryNormalize("https://github.com/example/mod/releases",out var normalized)&&normalized=="example/mod","GitHub repository URL normalization");
   Check(!GitHubModUpdates.TryNormalize("http://github.com/example/mod",out _),"GitHub rejects insecure repository URLs");
   Check(!GitHubModUpdates.TryNormalize("https://example.com/example/mod",out _),"GitHub rejects unrelated hosts");
   github.Bind("example.mod","https://github.com/example/mod");
   Check(github.Repository("example.mod")=="example/mod","GitHub repository binding saved");
  }
  using(var reloaded=new GitHubModUpdates(settings,root))
  {
   Check(reloaded.Repository("example.mod")=="example/mod","GitHub binding survives restart");
   reloaded.Unbind("example.mod");
   Check(reloaded.Repository("example.mod")==null,"GitHub binding can be removed");
  }
  var sourced=GitHubModDiscovery.ManifestCandidates("""{"id":"example.mod","github_url":"https://github.com/example/mod"}""");
  Check(sourced.Length==1&&sourced[0].Repository=="example/mod"&&sourced[0].Verified,"GitHub MOD discovery reads explicit manifest source");
  Check(GitHubModDiscovery.ManifestCandidates("""{"id":"example.mod","homepage":"https://example.org"}""").Length==0,"GitHub discovery ignores non-GitHub sites");
  File.WriteAllLines(output,lines);
 }
}
