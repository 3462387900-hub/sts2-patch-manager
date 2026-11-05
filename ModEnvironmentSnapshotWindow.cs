using System.Windows;
using System.Windows.Controls;

namespace SpirePatch;

public partial class MainWindow
{
    void ShowModEnvironmentSnapshots()
    {
        if (selectedInstallRoot is null)
        {
            ShowMods();
            return;
        }

        ModEnvironmentSnapshotEntry[] entries = [];
        string? listWarning = null;
        try { entries = ModEnvironmentSnapshots.List(selectedInstallRoot, installer.DataRoot); }
        catch (Exception error) { listWarning = error.Message; }

        var root = V(Header(
            "MOD ENVIRONMENT SNAPSHOTS",
            "MOD 环境快照",
            "保存整个 MOD 目录和已停用模组保留区。恢复前会先校验快照，并自动保存当前环境。"));
        Space(root, 14);
        root.Children.Add(H(
            Btn("← 返回模组管理", ShowMods, "Ghost"),
            Btn("刷新快照列表", ShowModEnvironmentSnapshots, "Ghost")));

        Space(root, 12);
        int totalMods = modInventory?.Mods.Count ?? 0;
        int enabledMods = modInventory?.Mods.Count(mod => mod.Enabled) ?? 0;
        int disabledMods = totalMods - enabledMods;
        root.Children.Add(Card(V(
            T("当前游戏目录", 14, ink, true),
            T(selectedInstallRoot, 12, muted),
            T($"当前扫描：{totalMods} 个 MOD · {enabledMods} 个启用 · {disabledMods} 个停用", 12, muted),
            T("快照范围：整个 mods 目录 + 已停用模组保留区。不会包含游戏本体、存档、日志或管理器设置。", 12, muted)), padding: 14));
        Space(root, 10);

        if (modInventoryDirty)
        {
            root.Children.Add(Card(V(
                T("MOD 环境尚未完成重新扫描", 14, gold, true),
                T("检测到文件变化。请先刷新模组列表，确认扫描稳定后再创建或恢复快照。", 12, muted),
                Btn("刷新模组列表", RefreshMods, "Ghost")), padding: 14));
            Space(root, 10);
        }

        var create = Btn("创建完整环境快照", CreateModEnvironmentSnapshot, "Primary");
        create.IsEnabled = !busy && !transactionWriting && !modInventoryDirty;
        root.Children.Add(Card(V(
            T("创建快照", 16, ink, true),
            T("复制当前两个 MOD 目录中的全部文件，并记录 SHA-256、文件大小和目录结构。快照保存在管理器的数据目录，不会改动游戏文件。", 12, muted),
            create,
            T("单个快照最多 50,000 个文件、30 GB；创建前会检查磁盘空间。建议在大型 MOD 更新或批量调整前创建。", 11, muted)), padding: 14));
        Space(root, 14);

        root.Children.Add(T($"已有快照 · {entries.Length}", 17, ink, true));
        Space(root, 8);

        if (listWarning is not null)
        {
            root.Children.Add(Card(V(
                T("无法读取快照目录", 14, gold, true),
                T(listWarning, 12, muted)), padding: 14));
        }
        else if (entries.Length == 0)
        {
            root.Children.Add(Card(V(
                T("还没有环境快照", 16, ink, true),
                T("创建第一份快照后，更新 MOD 或批量调整前就可以保留一个可恢复的完整环境。", 12, muted)), padding: 18));
        }

        foreach (var entry in entries)
        {
            var body = V(
                H(T(entry.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), 15, ink, true),
                    Badge(entry.Purpose, muted)),
                T($"编号：{entry.Id}", 10, muted),
                T($"文件：{entry.FileCount:N0} 个 · 占用：{ModEnvironmentSnapshots.FormatBytes(entry.TotalBytes)}", 12, muted));

            if (entry.IsValid)
                body.Children.Add(T("索引正常 · 恢复前仍会逐文件校验内容", 11, green));
            else
                body.Children.Add(T("快照索引异常：" + (entry.Warning ?? "无法读取"), 11, gold));

            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var preview = Btn("预览恢复", () => PreviewModEnvironmentRestore(entry.Id), "Ghost");
            preview.IsEnabled = entry.IsValid && !busy && !transactionWriting && !modInventoryDirty;
            actions.Children.Add(preview);
            var delete = Btn("删除快照", () => DeleteModEnvironmentSnapshot(entry.Id), "Ghost");
            delete.IsEnabled = !busy && !transactionWriting;
            actions.Children.Add(delete);
            body.Children.Add(actions);
            root.Children.Add(Card(body, padding: 14));
            Space(root, 8);
        }

        root.Children.Add(Card(V(
            T("恢复保护", 14, ink, true),
            T("恢复时会完整替换当前 mods 和已停用模组保留区。开始替换前，管理器会自动创建一份「恢复前自动保护」快照；即使恢复后发现问题，也可以再恢复到操作前的状态。", 12, muted),
            T("快照不包含游戏存档。若某个存档依赖特定 MOD，请确保恢复后的 MOD 组合与该存档兼容。", 12, gold)), padding: 14));

        SetPage("模组管理", root, "mod-snapshots", ShowModEnvironmentSnapshots);
    }

    async void CreateModEnvironmentSnapshot()
    {
        if (busy || transactionWriting || modInventoryDirty || selectedInstallRoot is null) return;
        string gameRoot = selectedInstallRoot;
        busy = true;
        transactionWriting = true;
        toast.Text = "正在复制并校验完整 MOD 环境，请勿关闭管理器…";
        ShowModEnvironmentSnapshots();
        try
        {
            var snapshot = await Task.Run(() =>
                ModEnvironmentSnapshots.Create(gameRoot, installer.DataRoot, "手动快照"));
            if (gameRoot == selectedInstallRoot)
                toast.Text = $"环境快照已创建：{snapshot.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · " +
                    $"{snapshot.Files.Length:N0} 个文件 · {ModEnvironmentSnapshots.FormatBytes(snapshot.TotalBytes)}";
        }
        catch (Exception error)
        {
            if (gameRoot == selectedInstallRoot)
                toast.Text = "创建快照失败：" + error.Message;
        }
        finally
        {
            transactionWriting = false;
            busy = false;
        }
        if (gameRoot == selectedInstallRoot) ShowModEnvironmentSnapshots();
    }

    async void PreviewModEnvironmentRestore(string snapshotId)
    {
        if (busy || transactionWriting || modInventoryDirty || selectedInstallRoot is null) return;
        string gameRoot = selectedInstallRoot;
        ModEnvironmentSnapshotPreview? preview = null;
        string? errorMessage = null;
        busy = true;
        toast.Text = "正在校验快照文件并比较当前 MOD 环境…";
        try
        {
            preview = await Task.Run(() =>
                ModEnvironmentSnapshots.PreviewRestore(gameRoot, installer.DataRoot, snapshotId));
        }
        catch (Exception error) { errorMessage = error.Message; }
        finally { busy = false; }

        if (gameRoot != selectedInstallRoot) return;
        if (preview is not null) ShowModEnvironmentRestorePreview(preview);
        else
        {
            toast.Text = "快照校验或预览失败：" + errorMessage;
            ShowModEnvironmentSnapshots();
        }
    }

    void ShowModEnvironmentRestorePreview(ModEnvironmentSnapshotPreview preview)
    {
        var root = V(Header(
            "REVIEW ENVIRONMENT RESTORE",
            "预览环境恢复",
            "先确认目标快照和变化数量。此页面不会修改任何 MOD 文件。"));
        Space(root, 14);
        root.Children.Add(Btn("← 返回快照列表", ShowModEnvironmentSnapshots, "Ghost"));
        Space(root, 12);

        root.Children.Add(Card(V(
            T("目标快照", 16, ink, true),
            T(preview.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + " · " + preview.Purpose, 13, muted),
            T("快照编号：" + preview.SnapshotId, 11, muted),
            T($"目标环境：{preview.TargetFiles:N0} 个文件 · {ModEnvironmentSnapshots.FormatBytes(preview.TargetBytes)}", 12, muted)), padding: 15));
        Space(root, 10);

        root.Children.Add(Card(V(
            T("文件变化预览", 16, ink, true),
            T($"当前文件：{preview.CurrentFiles:N0} 个", 12, muted),
            T($"恢复后文件：{preview.TargetFiles:N0} 个", 12, muted),
            H(
                SnapshotMetric(preview.AddedFiles.ToString("N0"), "恢复新增"),
                SnapshotMetric(preview.ChangedFiles.ToString("N0"), "内容替换"),
                SnapshotMetric(preview.RemovedFiles.ToString("N0"), "当前移除")),
            T($"目录变化：新增 {preview.AddedDirectories:N0} 个 · 移除 {preview.RemovedDirectories:N0} 个", 12, muted)), padding: 15));
        Space(root, 10);

        root.Children.Add(Card(V(
            T("恢复将执行的操作", 15, ink, true),
            T("1. 再次核对当前文件是否与预览时一致。", 12, muted),
            T("2. 自动创建一份「恢复前自动保护」快照。", 12, muted),
            T("3. 将当前 mods 和已停用模组保留区整体替换为目标快照。", 12, muted),
            T("4. 校验恢复后的文件；若替换或校验失败，会尝试将原目录移回。", 12, muted),
            T("不会修改游戏本体或存档。恢复前请退出游戏。", 12, gold)), padding: 15));
        Space(root, 14);

        var restore = Btn("确认并恢复此环境", () => ApplyModEnvironmentRestore(preview), "Primary");
        restore.IsEnabled = !busy && !transactionWriting && !modInventoryDirty;
        root.Children.Add(H(restore, Btn("取消", ShowModEnvironmentSnapshots, "Ghost")));

        SetPage("模组管理", root, "mod-snapshot-preview", ShowModEnvironmentSnapshots);
    }

    StackPanel SnapshotMetric(string value, string label)
    {
        var panel = V(T(value, 20, ink, true), T(label, 11, muted));
        panel.MinWidth = 92;
        panel.Margin = new Thickness(0, 8, 18, 4);
        return panel;
    }

    async void ApplyModEnvironmentRestore(ModEnvironmentSnapshotPreview preview)
    {
        if (busy || transactionWriting || modInventoryDirty || selectedInstallRoot is null) return;
        string gameRoot = selectedInstallRoot;
        string warning =
            $"即将恢复到 {preview.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} 的 MOD 环境。\n\n" +
            $"目标文件：{preview.TargetFiles:N0}\n" +
            $"新增：{preview.AddedFiles:N0} · 替换：{preview.ChangedFiles:N0} · 移除：{preview.RemovedFiles:N0}\n\n" +
            "当前 mods 目录和已停用模组保留区都会被整体替换。开始前会自动保存当前环境。\n" +
            "请确认游戏已经退出。是否继续？";
        if (MessageBox.Show(this, warning, "最终确认 · 恢复 MOD 环境",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        string[] previousIds = modInventory?.Mods.Select(mod => mod.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        busy = true;
        transactionWriting = true;
        toast.Text = "正在准备恢复，当前环境会先自动保存…";
        ModEnvironmentSnapshotRestoreResult? result = null;
        string? restoreError = null;
        try
        {
            result = await Task.Run(() => ModEnvironmentSnapshots.Restore(
                gameRoot, installer.DataRoot, preview.SnapshotId, preview.Token));
        }
        catch (Exception error) { restoreError = error.Message; }
        finally
        {
            if (result is null)
            {
                transactionWriting = false;
                busy = false;
            }
        }

        if (gameRoot != selectedInstallRoot)
        {
            transactionWriting = false;
            busy = false;
            return;
        }
        if (result is null)
        {
            toast.Text = "环境恢复未完成：" + restoreError;
            ShowModEnvironmentSnapshots();
            return;
        }

        InvalidateLifecycle("MOD 环境已恢复，修复补丁健康结果需要重新扫描。");
        report = null;
        states = [];
        ClearCoop();
        ClearSelection();
        installPreview = null;
        lastInstallResult = null;
        modTogglePreview = null;

        try
        {
            var refreshed = await Task.Run(() => ModManager.Scan(gameRoot));
            modInventory = refreshed;
            modInventoryDirty = false;
            var allIds = previousIds.Concat(refreshed.Mods.Select(mod => mod.Id))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string cacheWarning = "";
            try
            {
                var sources = WorkshopSources();
                var updates = WorkshopUpdates();
                foreach (string id in allIds)
                {
                    sources.ClearBaseline(id);
                    updates.Invalidate(id);
                }
            }
            catch (Exception error)
            {
                cacheWarning = "；部分创意工坊版本基线未能清理：" + error.Message;
            }

            modStatus = $"恢复完成 · 共 {refreshed.Mods.Count} 个 MOD · {refreshed.Mods.Count(mod => mod.Enabled)} 个启用 · {refreshed.Mods.Count(mod => !mod.Enabled)} 个停用";
            toast.Text = $"环境恢复完成。恢复前自动保护快照：{result.SafetySnapshotId}。" +
                cacheWarning + (refreshed.Issues.Count > 0
                    ? "；恢复后的清单仍有 " + refreshed.Issues.Count + " 项问题，请查看模组管理。"
                    : "");
        }
        catch (Exception error)
        {
            modInventoryDirty = true;
            toast.Text = $"环境恢复已完成，恢复前自动保护快照：{result.SafetySnapshotId}。但刷新 MOD 清单失败：{error.Message}";
        }
        finally
        {
            transactionWriting = false;
            busy = false;
        }

        if (gameRoot == selectedInstallRoot) ShowModEnvironmentSnapshots();
    }

    async void DeleteModEnvironmentSnapshot(string snapshotId)
    {
        if (busy || transactionWriting || selectedInstallRoot is null) return;
        if (MessageBox.Show(this,
            "确定永久删除这份 MOD 环境快照？\n删除后无法再从该快照恢复。当前游戏目录和其他快照不会受影响。",
            "删除环境快照", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        string gameRoot = selectedInstallRoot;
        busy = true;
        transactionWriting = true;
        try
        {
            await Task.Run(() => ModEnvironmentSnapshots.Delete(gameRoot, installer.DataRoot, snapshotId));
            if (gameRoot == selectedInstallRoot) toast.Text = "已删除指定环境快照。";
        }
        catch (Exception error)
        {
            if (gameRoot == selectedInstallRoot) toast.Text = "删除快照失败：" + error.Message;
        }
        finally
        {
            transactionWriting = false;
            busy = false;
        }
        if (gameRoot == selectedInstallRoot) ShowModEnvironmentSnapshots();
    }
}
