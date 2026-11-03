using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpirePatch;

internal record PatchFolderMove(string Id, string Name, string Source, string Destination, Dictionary<string, string> Files);
internal record PatchOrganizationPlan(string Root, PatchFolderMove[] Moves, string[] Skipped, string Token, bool Fixture = false);
internal record PatchOrganizationResult(int Moved, string Journal);

/// <summary>
/// Only moves recognized, independent fix directories under mods into one collection.
/// Never rewrites MOD files, moves original in-place patches, or changes existing backup journals.
/// </summary>
internal static class PatchOrganizer
{
 public const string CollectionName = "_SpirePatch_Fixes";
 static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

 static bool ApprovedStandalone(ManagedMod mod)
 {
  using var plan = Catalog.Plan();
  var component = plan.RootElement.GetProperty("components").EnumerateArray()
   .FirstOrDefault(p => string.Equals(p.GetProperty("id").GetString(), mod.Id, StringComparison.OrdinalIgnoreCase));
  if(component.ValueKind == JsonValueKind.Undefined || component.GetProperty("required").GetBoolean())return false;
  // A matching ID alone is not enough to relocate a possibly unrelated or modified MOD.
  foreach(var f in component.GetProperty("files").EnumerateArray())
  {
   string name = f.GetProperty("name").GetString()!;
   bool manifest = f.TryGetProperty("isManifest", out var isManifest) && isManifest.GetBoolean();
   if(!manifest && !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))continue;
   string path = manifest ? mod.Manifest : GameEnvironment.Child(mod.Folder, name);
   if(!File.Exists(path))return false;
   string actual = Catalog.Hash(path);
   if(!f.GetProperty("allowedExisting").EnumerateArray().Any(x => string.Equals(x.GetString(), actual, StringComparison.OrdinalIgnoreCase)))return false;
  }
  return true;
 }

 static HashSet<string> ProtectedFolders(string root)
 {
  var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  string parent = GameEnvironment.Child(root, "mod_fix_backups");
  if(!Directory.Exists(parent))return result;
  GameEnvironment.NoLinks(parent);
  foreach(var dir in Directory.EnumerateDirectories(parent, "STS2-AllFixes-20261102-*", SearchOption.TopDirectoryOnly))
  {
   GameEnvironment.NoLinks(dir);
   string journal = GameEnvironment.Child(dir, "restore.json");
   // A partially written or missing journal must not be silently bypassed.
   if(!File.Exists(journal))throw new InvalidOperationException("发现没有恢复记录的历史备份，请先核对：" + dir);
   using var document = JsonDocument.Parse(File.ReadAllText(journal));
   var record = document.RootElement;
   string status = record.GetProperty("status").GetString() ?? "";
   if(status == "prepared")throw new InvalidOperationException("发现未完成的补丁安装事务，先处理历史备份：" + dir);
   if(status is not("installed" or "rolled-back" or "install-failed-restored"))
    throw new InvalidOperationException("无法识别补丁安装记录状态：" + journal);
   if(status != "installed")continue;
   foreach(var f in record.GetProperty("files").EnumerateArray())
   {
    string relative = (f.GetProperty("relative").GetString() ?? "").Replace('/', '\\');
    var parts = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
    if(parts.Length >= 3 && parts[0].Equals("mods", StringComparison.OrdinalIgnoreCase))
     result.Add(parts[1]);
   }
  }
  return result;
 }

 static bool SameFiles(string path, IReadOnlyDictionary<string, string> expected)
 {
  if(!Directory.Exists(path))return false;
  var current = ModManager.FileSnapshot(path);
  return current.Count == expected.Count && current.All(f => expected.TryGetValue(f.Key, out var hash) && hash == f.Value);
 }

 static PatchOrganizationPlan PreviewCore(string path, bool fixture)
 {
  string root = GameEnvironment.Root(path);
  if(fixture && (!File.Exists(Path.Combine(root, ".spirepatch-fixture")) ||
     new FileInfo(Path.Combine(root, "SlayTheSpire2.exe")).Length != 0))
   throw new InvalidOperationException("测试专用方案只能使用带标记的隔离目录。");
  if(GameEnvironment.Running(root))throw new InvalidOperationException("请先退出游戏，再整理独立补丁。");
  var inventory = ModManager.Scan(root);
  if(inventory.Issues.Count > 0)throw new InvalidOperationException("MOD 清单不完整或存在重复 ID，先核对模组管理页面的问题。");
  var protectedFolders = ProtectedFolders(root);
  string mods = GameEnvironment.Child(root, "mods");
  string collection = GameEnvironment.Child(mods, CollectionName);
  var moves = new List<PatchFolderMove>();
  var skipped = new List<string>();
  using var plan = Catalog.Plan();
  var standalone = plan.RootElement.GetProperty("components").EnumerateArray()
   .Where(x => !x.GetProperty("required").GetBoolean())
   .Select(x => x.GetProperty("id").GetString()!)
   .ToHashSet(StringComparer.OrdinalIgnoreCase);

  foreach(var mod in inventory.Mods.Where(m => m.Enabled && standalone.Contains(m.Id)))
  {
   string folder = mod.Folder;
   if(folder.StartsWith(collection + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))continue;
   if(!string.Equals(Path.GetDirectoryName(folder)?.TrimEnd('\\'), mods.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
   {
    skipped.Add(mod.Name + "：不在 mods 根目录下的独立文件夹中。");
    continue;
   }
   if(inventory.Mods.Any(x => x != mod && (
      x.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
      x.Folder.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
   {
    skipped.Add(mod.Name + "：文件夹中还包含其他 MOD 清单。");
    continue;
   }
   if(protectedFolders.Contains(Path.GetFileName(folder)))
   {
    skipped.Add(mod.Name + "：历史安装备份仍引用原路径；为保护回滚，暂不移动。");
    continue;
   }
   if(!fixture && !ApprovedStandalone(mod))
   {
    skipped.Add(mod.Name + "：DLL 或清单不属于本版已审核的文件指纹，暂不移动。");
    continue;
   }
   string destination = GameEnvironment.Child(collection, Path.GetFileName(folder));
   if(Directory.Exists(destination) || File.Exists(destination))
   {
    skipped.Add(mod.Name + "：专用目录已存在同名文件夹或文件，拒绝覆盖。");
    continue;
   }
   moves.Add(new(mod.Id, mod.Name, folder, destination, ModManager.FileSnapshot(folder)));
  }
  moves = moves.OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();
  var tokenPayload = new { root, revision = Catalog.Revision, fixture, moves = moves.Select(m => new { m.Id, m.Source, m.Destination, m.Files }) };
  string token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokenPayload))));
  return new(root, moves.ToArray(), skipped.ToArray(), token, fixture);
 }

 public static PatchOrganizationPlan Preview(string root) =>
  GameEnvironment.WithLock(GameEnvironment.Root(root), () => PreviewCore(root, false));

 internal static PatchOrganizationPlan PreviewFixture(string root) =>
  GameEnvironment.WithLock(GameEnvironment.Root(root), () => PreviewCore(root, true));

 public static PatchOrganizationResult Apply(PatchOrganizationPlan plan, string dataRoot) =>
  GameEnvironment.WithLock(GameEnvironment.Root(plan.Root), () =>
  {
   var current = PreviewCore(plan.Root, plan.Fixture);
   if(current.Token != plan.Token || current.Moves.Length != plan.Moves.Length)
    throw new InvalidOperationException("文件、回滚记录或目录状态已经变化，请重新预览。");
   if(current.Moves.Length == 0)throw new InvalidOperationException("没有需要整理的独立补丁。");
   string logParent = BundleInstaller.Child(Path.GetFullPath(dataRoot), "补丁整理记录");
   Directory.CreateDirectory(logParent);
   string journal = BundleInstaller.Child(logParent, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
   var completed = new List<PatchFolderMove>();
   string phase = "准备执行", error = "";
   void Save()
   {
    string temp = journal + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
     File.WriteAllText(temp, JsonSerializer.Serialize(new {
      time = DateTimeOffset.Now, root = plan.Root, phase, error,
      moves = current.Moves.Select(m => new { m.Id, m.Source, m.Destination, m.Files }),
      completed = completed.Select(m => m.Id)
     }, JsonOptions));
     File.Move(temp, journal, true);
    }
    finally { if(File.Exists(temp))File.Delete(temp); }
   }
   Save();
   try
   {
    foreach(var move in current.Moves)
    {
     phase = "移动中：" + move.Id; Save();
     if(!SameFiles(move.Source, move.Files) || Directory.Exists(move.Destination) || File.Exists(move.Destination))
      throw new InvalidOperationException("操作前文件或目标目录发生变化：" + move.Id);
     Directory.CreateDirectory(Path.GetDirectoryName(move.Destination)!);
     Directory.Move(move.Source, move.Destination);
     completed.Add(move); Save();
     if(!SameFiles(move.Destination, move.Files))
      throw new InvalidOperationException("移动后文件校验未通过：" + move.Id);
    }
    phase = "已完成"; Save();
    return new(completed.Count, journal);
   }
   catch(Exception ex)
   {
    error = ex.Message; var undoErrors = new List<string>();
    phase = "尝试撤销本批已移动项目"; Save();
    foreach(var move in completed.AsEnumerable().Reverse())
    {
     try
     {
      if(!SameFiles(move.Destination, move.Files) || Directory.Exists(move.Source) || File.Exists(move.Source))
       throw new InvalidOperationException("目录占用或文件校验失败");
      Directory.Move(move.Destination, move.Source);
      if(!SameFiles(move.Source, move.Files))throw new InvalidOperationException("撤销后校验失败");
     }
     catch(Exception undo) { undoErrors.Add(move.Id + "：" + undo.Message); }
    }
    phase = undoErrors.Count == 0 ? "失败，已撤销本批移动" : "失败，部分项目需要人工核对";
    error = ex.Message + (undoErrors.Count == 0 ? "" : "；恢复失败：" + string.Join("；", undoErrors));
    Save();
    throw new InvalidOperationException(error + "。操作记录：" + journal, ex);
   }
  });
}
