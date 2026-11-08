namespace SpirePatch;

// Presentation only. No change to the approved transaction, file list or token.
public sealed class PlanPresentation
{
 public InstallResult Result{get;}
 public bool Undo=>Result.Mode is "RollbackPreview" or "Rollback";
 public InstallFile[] Documents{get;}
 public InstallFile[] Runtime{get;}
 public InstallFile[] Legacy{get;}
 public InstallFile[] Unchanged{get;}
 public int MatchedPatches{get;}
 public bool DocumentsOnly=>Documents.Length>0&&Runtime.Length==0&&Legacy.Length==0;
 public string DocumentVerb=>Documents.All(f=>!f.Existed)?"补齐":"更新";
 public PlanPresentation(InstallResult result)
 {
  Result=result;
  Documents=result.Files.Where(IsDocument).ToArray();
  Legacy=result.Files.Where(f=>!IsDocument(f)&&f.Operation=="remove").ToArray();
  Runtime=result.Files.Where(f=>!IsDocument(f)&&f.Operation!="remove").ToArray();
  var changed=result.Files.Select(f=>Normalize(f.Relative)).ToHashSet(StringComparer.OrdinalIgnoreCase);
  Unchanged=Undo?[]:(result.CheckedFiles??[]).Where(f=>!changed.Contains(Normalize(f.Relative))&&f.Existed&&f.OldHash.Equals(f.NewHash,StringComparison.OrdinalIgnoreCase)).ToArray();
  MatchedPatches=Undo?0:(result.CheckedFiles??[]).Where(f=>!IsDocument(f)).GroupBy(f=>Folder(f),StringComparer.OrdinalIgnoreCase).Count(g=>g.All(f=>Unchanged.Contains(f)));
 }
 static string Normalize(string path)=>path.Replace('\\','/');
 public static bool IsDocument(InstallFile f)
 {
  var parts=Normalize(f.Relative).Split('/');
  return parts.Length>=4&&parts[^2].Equals("patch-info",StringComparison.OrdinalIgnoreCase)&&parts[^1].ToLowerInvariant() is "patch.json" or "patch.schema.json" or "readme.zh-cn.md" or "historical-validation.txt";
 }
 public static string Folder(InstallFile f)
 {
  string path=Normalize(f.Relative);int end=path.LastIndexOf('/');string parent=end<0?"其他文件":path[..end];
  return IsDocument(f)?parent[..parent.LastIndexOf('/')]:parent;
 }
 public static string GroupName(string folder)
 {
  var id=folder.Split('/').Last();return Catalog.All.FirstOrDefault(p=>p.Id.Equals(id,StringComparison.OrdinalIgnoreCase))?.Name??id;
 }
 public string Title=>Result.Changed==0?(Undo?"没有可撤销的操作":Result.SelectedIds?.Length>0?"所选修复与说明均已匹配，无需处理":"修复与说明均已匹配，无需处理"):
  Undo?(DocumentsOnly?"仅撤销上次的说明文件改动":"即将撤销上次操作"):
  DocumentsOnly?$"{(Result.SelectedIds?.Length>0?"所选":"已有")}修复已匹配，仅需{DocumentVerb}说明":"本次需要处理的改动";
 public string Summary=>Undo?$"撤销修复文件 {Runtime.Length} 个 · 说明文件 {Documents.Length} 个":
  $"修复文件 {Runtime.Length} 个 · 说明文件 {Documents.Length} 个 · 旧补丁清理 {Legacy.Length} 个";
 public string Confirm=>Undo?(DocumentsOnly?"确认撤销说明改动":"确认回滚并校验"):
  DocumentsOnly?$"确认{DocumentVerb}说明并校验":"确认备份并应用改动";
 public string Detail=>Undo?(DocumentsOnly?"只撤销这次记录中的说明文件改动，现有修复继续保留。":"按选定备份恢复上次操作前的状态，包括被替换的旧修复。"):
  DocumentsOnly?"本次只处理补丁说明与验证记录；修复程序和运行配置保持原样。不补齐这些说明，也不影响已生效的修复。":
  "只处理下方列出的差异文件。已匹配的文件会跳过；需要替换或移除的文件会先备份，新增文件也会记入回滚记录。";
 public string Completion=>Result.Changed==0?(Undo?"没有可撤销的操作":"文件已匹配，没有重复写入"):
  Undo?(DocumentsOnly?"说明改动已撤销，修复文件未改动":"回滚完成，原文件已恢复"):
  DocumentsOnly?$"说明已{DocumentVerb}，修复文件未改动":"改动已完成，文件校验通过";
 public static string Action(InstallFile file)=>file.Operation switch{
  "restore"=>"恢复原文件","remove-added"=>"移除上次新增文件","remove"=>"备份并移除旧补丁",_=>file.Existed?"备份并更新":IsDocument(file)?"补齐说明":"新增修复文件"};
}
