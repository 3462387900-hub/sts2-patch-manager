using System.IO;

namespace SpirePatch;

// UI-only grouping. A matching catalog ID is not proof that a DLL is safe or unmodified.
// All file actions continue through ModManager and the installer checks.
internal static class ModPresentation
{
 static readonly Lazy<HashSet<string>> IndependentPatchIds=new(()=>
 {
  using var plan=Catalog.Plan();
  return plan.RootElement.GetProperty("components").EnumerateArray()
   .Where(x=>!x.GetProperty("required").GetBoolean())
   .Select(x=>x.GetProperty("id").GetString()!)
   .ToHashSet(StringComparer.OrdinalIgnoreCase);
 });

 internal static bool IsIndependentPatch(ManagedMod mod)=>
  IndependentPatchIds.Value.Contains(mod.Id)&&
  Path.GetFileNameWithoutExtension(mod.Manifest).Equals(mod.Id,StringComparison.OrdinalIgnoreCase);
}
