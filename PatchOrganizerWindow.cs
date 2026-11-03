using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SpirePatch;

public partial class MainWindow
{
 PatchOrganizationPlan? patchOrganizationPlan;
 string patchOrganizationStatus = "";

 async void PreparePatchOrganization()
 {
  if(busy || transactionWriting || selectedInstallRoot == null)return;
  busy = true;
  patchOrganizationPlan = null;
  patchOrganizationStatus = "正在核对已收录的独立修复补丁与历史回滚记录…";
  ShowPatchOrganization();
  try
  {
   patchOrganizationPlan = await Task.Run(() => PatchOrganizer.Preview(selectedInstallRoot));
   patchOrganizationStatus = patchOrganizationPlan.Moves.Length == 0 ? "当前没有可安全移动的独立补丁。" : "预览完成，确认前不会修改游戏文件。";
  }
  catch(Exception ex) { patchOrganizationStatus = "预检未通过：" + ex.Message; }
  finally { busy = false; ShowPatchOrganization(); }
 }

 void ShowPatchOrganization()
 {
  var root = V(Header("PATCH FOLDER", "整理独立修复补丁",
   "普通 MOD 和修复过的原 MOD 保持原位。只将已审核的独立修复补丁集中到 Mods\\_SpirePatch_Fixes。"));
  Space(root, 18);
  root.Children.Add(Card(V(
   T("整理范围与安全限制", 17, ink, true),
   T("仅移动整个独立补丁文件夹，不拆分 DLL、清单或其他文件。游戏运行中禁止整理；文件指纹、重复 MOD、共用目录、目标占用及历史回滚冲突均会核对。", 13, muted),
   T("历史备份将先保存原始记录，再同步更新与已移动补丁有关的回滚路径。备份异常时停止操作；未知第三方 MOD 不会移动。", 12, muted)
  )));
  Space(root, 12);
  if(patchOrganizationStatus.Length > 0)root.Children.Add(T(patchOrganizationStatus, 13, muted));
  Space(root, 12);
  var plan = patchOrganizationPlan;
  if(plan != null)
  {
   root.Children.Add(T($"本次可整理 {plan.Moves.Length} 项，跳过 {plan.Skipped.Length} 项；同步更新 {plan.Journals?.Length ?? 0} 条历史回滚记录", 17, ink, true));
   Space(root, 12);
   foreach(var move in plan.Moves)
   {
    root.Children.Add(Card(V(T(move.Name, 15, ink, true),
     T("原位置：" + move.Source + "\n新位置：" + move.Destination + $"\n涉及 {move.Files.Count} 个文件", 12, muted)), "#FFFFFF", 14));
    Space(root, 8);
   }
   if(plan.Skipped.Length > 0)
   {
    root.Children.Add(Card(V(T("未整理的项目（安全跳过）", 15, ink, true),
      T(string.Join("\n", plan.Skipped), 12, muted)), "#FFFFFF", 14));
    Space(root, 12);
   }
   if(plan.Moves.Length > 0)
   {
    var confirm = Btn("确认整理这 " + plan.Moves.Length + " 项", ConfirmPatchOrganization, "Primary");
    confirm.IsEnabled = !busy;
    root.Children.Add(H(confirm, Btn("取消", () => { patchOrganizationPlan = null; ShowMods(); }, "Ghost")));
   }
   else root.Children.Add(Btn("返回模组管理", ShowMods, "Ghost"));
  }
  else if(!busy)
  {
   if(patchOrganizationStatus.Contains("尚未结束", StringComparison.Ordinal))
    root.Children.Add(Btn("恢复上次中断的整理", RecoverPatchOrganization, "Primary"));
   root.Children.Add(Btn("返回模组管理", ShowMods, "Ghost"));
  }
  SetPage("模组管理", root, "patch-organize", ShowPatchOrganization);
 }

 async void RecoverPatchOrganization()
 {
  if(busy || transactionWriting || selectedInstallRoot == null)return;
  busy = true; transactionWriting = true;
  try
  {
   await Task.Run(() => PatchOrganizer.Recover(selectedInstallRoot));
   patchOrganizationStatus = "已核对并恢复未完成的整理。可重新预览。";
  }
  catch(Exception ex){patchOrganizationStatus = "恢复未完成，请保留现场：" + ex.Message;}
  finally {busy = false;transactionWriting = false;patchOrganizationPlan = null;ShowPatchOrganization();}
 }

 async void ConfirmPatchOrganization()
 {
  if(busy || transactionWriting || patchOrganizationPlan == null)return;
  var plan = patchOrganizationPlan;
  patchOrganizationPlan = null;
  busy = true; transactionWriting = true;
  patchOrganizationStatus = "正在按预览方案整理，操作期间请勿关闭管理器。";
  ShowPatchOrganization();
  try
  {
   var result = await Task.Run(() => PatchOrganizer.Apply(plan, installer.DataRoot));
   installPreview = null; lastInstallResult = null; ClearSelection(); ClearCoop();
   report = null; states = []; demo = false;
   modInventory = await Task.Run(() => ModManager.Scan(plan.Root));
   modStatus = $"整理完成：移动 {result.Moved} 项独立补丁。记录：" + result.Journal;
  }
  catch(Exception ex)
  {
   patchOrganizationStatus = "整理未成功：" + ex.Message;
  }
  finally
  {
   InvalidateLifecycle();busy = false; transactionWriting = false;
   if(patchOrganizationStatus.StartsWith("整理未成功：", StringComparison.Ordinal))ShowPatchOrganization();
   else ShowMods();
  }
 }
}
