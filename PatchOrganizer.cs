using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpirePatch;

internal record PatchFolderMove(string Id, string Name, string Source, string Destination, Dictionary<string, string> Files);
internal record PatchOrganizationPlan(string Root, PatchFolderMove[] Moves, string[] Skipped, string Token, bool Fixture = false, PatchJournalRewrite[]? Journals = null);
internal record PatchOrganizationResult(int Moved, string Journal);

/// <summary>
/// Only moves recognized, independent fix directories under mods into one collection.
/// Never rewrites MOD payloads or original in-place patches. Active rollback paths are transactionally migrated.
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
  if(PatchJournalRelocator.Pending(root).Length > 0)
   throw new InvalidOperationException("上次补丁整理尚未结束，请点击恢复上次整理后重试。");
  var inventory = ModManager.Scan(root);
  if(inventory.Issues.Count > 0)throw new InvalidOperationException("MOD 清单不完整或存在重复 ID，先核对模组管理页面的问题。");
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
  var journals=PatchJournalRelocator.Plan(root,moves);
  var tokenPayload = new { root, revision = Catalog.Revision, fixture, moves = moves.Select(m => new { m.Id, m.Source, m.Destination, m.Files }), journals = journals.Select(j => new { j.Journal, j.OriginalHash, j.UpdatedHash }) };
  string token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokenPayload))));
  return new(root, moves.ToArray(), skipped.ToArray(), token, fixture, journals);
 }

 public static PatchOrganizationPlan Preview(string root) =>
  GameEnvironment.WithLock(GameEnvironment.Root(root), () => PreviewCore(root, false));

 internal static PatchOrganizationPlan PreviewFixture(string root) =>
  GameEnvironment.WithLock(GameEnvironment.Root(root), () => PreviewCore(root, true));

 public static void Recover(string root) =>
  GameEnvironment.WithLock(GameEnvironment.Root(root), () =>
  {
   PatchJournalRelocator.RecoverAll(GameEnvironment.Root(root));
   return true;
  });

 public static PatchOrganizationResult Apply(PatchOrganizationPlan plan, string dataRoot) =>
  GameEnvironment.WithLock<PatchOrganizationResult>(GameEnvironment.Root(plan.Root), () =>
  {
   var current = PreviewCore(plan.Root, plan.Fixture);
   if(current.Token != plan.Token || current.Moves.Length != plan.Moves.Length)
    throw new InvalidOperationException("文件、回滚记录或目录状态已经变化，请重新预览。");
   if(current.Moves.Length == 0)throw new InvalidOperationException("没有需要整理的独立补丁。");
   var journals=current.Journals??[];
   string folder=GameEnvironment.Child(PatchJournalRelocator.Parent(plan.Root),
    PatchJournalRelocator.TransactionPrefix+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));
   Directory.CreateDirectory(folder);
   var backups=new List<PatchJournalBackup>();
   // All original journals are copied and verified before moving any game directory.
   foreach(var item in journals)
   {
    if(!File.Exists(item.Journal) || !Catalog.Hash(item.Journal).Equals(item.OriginalHash,StringComparison.OrdinalIgnoreCase))
     throw new InvalidOperationException("历史回滚记录已被修改：" + item.Journal);
    string relative="original-"+backups.Count+".json";
    string copy=GameEnvironment.Child(folder,relative);
    File.Copy(item.Journal,copy);
    if(!Catalog.Hash(copy).Equals(item.OriginalHash,StringComparison.OrdinalIgnoreCase))
     throw new InvalidOperationException("历史回滚记录备份校验失败：" + item.Journal);
    backups.Add(new(item.Journal,relative,item.OriginalHash,item.UpdatedHash));
   }
   var state=new PatchOrganizationTransaction(plan.Root,"执行中",current.Moves,backups.ToArray());
   PatchJournalRelocator.Save(folder,state);
   try
   {
    foreach(var move in current.Moves)
    {
     if(!PatchJournalRelocator.SameFiles(move.Source,move.Files) ||
        Directory.Exists(move.Destination) || File.Exists(move.Destination))
      throw new InvalidOperationException("补丁文件或目标目录已变化：" + move.Id);
     Directory.CreateDirectory(Path.GetDirectoryName(move.Destination)!);
     Directory.Move(move.Source,move.Destination);
     if(!PatchJournalRelocator.SameFiles(move.Destination,move.Files))
      throw new InvalidOperationException("移动后文件校验失败：" + move.Id);
    }
    foreach(var item in journals)
     PatchJournalRelocator.WriteVerified(item.Journal,item.UpdatedText,item.OriginalHash,item.UpdatedHash);
    foreach(var item in journals)
     if(!Catalog.Hash(item.Journal).Equals(item.UpdatedHash,StringComparison.OrdinalIgnoreCase))
      throw new InvalidOperationException("历史回滚路径校验失败：" + item.Journal);
    state=state with { Status="已完成" };
    PatchJournalRelocator.Save(folder,state);
    return new(current.Moves.Length,GameEnvironment.Child(folder,"transaction.json"));
   }
   catch(Exception e)
   {
    try{PatchJournalRelocator.Restore(folder);}
    catch(Exception recovery)
    {
     throw new InvalidOperationException("整理失败，恢复也未全部成功。原始备份已保留，请勿自行移动或删除文件。"
      +" 整理原因："+e.Message+"；恢复原因："+recovery.Message+"；记录："+folder,e);
    }
    throw new InvalidOperationException("整理失败，已恢复移动与历史回滚记录："+e.Message+"；记录："+folder,e);
   }
  });
}
