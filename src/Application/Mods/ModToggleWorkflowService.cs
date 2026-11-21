using System.IO;

namespace SpirePatch;

internal sealed record ModToggleWorkflowProgress(
    string Status,
    bool TransactionWriting = false,
    string? Name = null,
    string? State = null,
    string? Explanation = null,
    int Completed = 0,
    int Total = 0,
    bool ItemStarted = false);

internal sealed record ModToggleExecutionResult(
    bool Confirmed,
    bool Success,
    bool Cancelled,
    string Status,
    string? Error = null);

internal sealed record ModToggleBatchReview(
    int SelectionIndex,
    string Folder,
    string Name,
    string State,
    string Explanation);

internal sealed record ModToggleBatchStep(
    ManagedMod Mod,
    bool PreviewDeferred,
    string OrderExplanation = "");

internal sealed record ModToggleBatchPreview(
    string Root,
    bool Enable,
    int SelectedCount,
    IReadOnlyList<ModToggleBatchReview> Reviews,
    IReadOnlyList<ModToggleBatchStep> ExecutionOrder,
    bool Cancelled,
    string Status);

internal sealed record ModToggleBatchItemResult(
    string Folder,
    string Name,
    string State,
    string Explanation);

internal sealed record ModToggleBatchResult(
    bool Confirmed,
    bool Cancelled,
    IReadOnlyList<ModToggleBatchItemResult> Items,
    int SuccessCount,
    int FailedCount,
    int SkippedCount,
    int CancelledCount,
    string Summary);

internal interface IModToggleWorkflowBackend
{
    string? BlockReason(string root, ManagedMod mod, ModInventory inventory);
    ModTogglePlan Preview(string root, string folder, bool enable);
    void Apply(ModTogglePlan plan);
}

internal sealed class DomainModToggleWorkflowBackend : IModToggleWorkflowBackend
{
    public string? BlockReason(string root, ManagedMod mod, ModInventory inventory) =>
        ModManager.BlockReason(root, mod, inventory);

    public ModTogglePlan Preview(string root, string folder, bool enable) =>
        ModManager.Preview(root, folder, enable);

    public void Apply(ModTogglePlan plan) => ModManager.Apply(plan);
}

/// <summary>
/// Coordinates single and batch MOD enable/disable operations without depending on
/// WPF. Domain remains responsible for preview validation and transactional moves;
/// UI decisions, progress presentation and watcher state are supplied as delegates.
/// </summary>
internal sealed class ModToggleWorkflowService
{
    sealed record Candidate(int SelectionIndex, ManagedMod Mod);

    readonly IModToggleWorkflowBackend backend;
    readonly ModDependencyDiagnosticsService dependencyDiagnostics;

    public ModToggleWorkflowService(
        IModToggleWorkflowBackend? backend = null,
        ModDependencyDiagnosticsService? dependencyDiagnostics = null)
    {
        this.backend = backend ?? new DomainModToggleWorkflowBackend();
        this.dependencyDiagnostics = dependencyDiagnostics ??
            new ModDependencyDiagnosticsService(this.backend.BlockReason);
    }

    public Task<ModTogglePlan> PreviewAsync(
        string root,
        string folder,
        bool enable,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string normalizedRoot = Path.GetFullPath(root);
        return Task.Run(
            () => backend.Preview(normalizedRoot, folder, enable),
            cancellationToken);
    }

    /// <summary>
    /// The UI confirmation delegate is called after the preview is shown. Once
    /// Apply starts, cancellation is deliberately not passed into the Domain
    /// transaction; it must finish or roll itself back before returning.
    /// </summary>
    public async Task<ModToggleExecutionResult> ConfirmAndApplyAsync(
        ModTogglePlan plan,
        Func<ModTogglePlan, CancellationToken, Task<bool>> confirm,
        Action<ModToggleWorkflowProgress>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(confirm);

        if (cancellationToken.IsCancellationRequested)
            return new(false, false, true, "操作已取消，文件没有移动。");

        bool confirmed;
        try
        {
            confirmed = await confirm(plan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, false, true, "操作已取消，文件没有移动。");
        }
        catch (Exception error)
        {
            return new(false, false, false, "确认操作失败：" + error.Message, error.Message);
        }

        if (!confirmed)
            return new(false, false, true, "已取消操作，文件没有移动。");

        if (cancellationToken.IsCancellationRequested)
            return new(true, false, true, "操作已取消，文件没有移动。");

        bool writing = false;
        try
        {
            writing = true;
            reportProgress?.Invoke(new(
                "正在安全移动模组文件。",
                TransactionWriting: true,
                Name: plan.Name));

            // Do not provide a CancellationToken here. ModManager.Apply owns the
            // file transaction and must finish its rollback/recovery boundary.
            await Task.Run(() => backend.Apply(plan));
            return new(true, true, false,
                plan.Name + (plan.Enable ? " 已启用。" : " 已停用，文件已完整保留。"));
        }
        catch (Exception error)
        {
            return new(true, false, false, "操作未完成：" + error.Message, error.Message);
        }
        finally
        {
            if (writing)
                reportProgress?.Invoke(new("模组文件操作已结束。", TransactionWriting: false, Name: plan.Name));
        }
    }

    public async Task<ModToggleBatchPreview> PrepareBatchAsync(
        string root,
        ModInventory inventory,
        IReadOnlyList<ManagedMod> selected,
        bool enable,
        Action<ModToggleWorkflowProgress>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(selected);

        string normalizedRoot = Path.GetFullPath(root);
        var reviews = new ModToggleBatchReview?[selected.Count];
        var candidates = new List<Candidate>();
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < selected.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
                return CancelledPreview(normalizedRoot, enable, selected, reviews);
            ManagedMod selectedMod = selected[index];

            if (!seenFolders.Add(selectedMod.Folder))
            {
                reviews[index] = Review(index, selectedMod, "跳过", "同一个模组被重复选中，本次只保留首次选择。");
                continue;
            }

            var matches = inventory.Mods
                .Where(mod => mod.Folder.Equals(selectedMod.Folder, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matches.Length != 1)
            {
                reviews[index] = Review(index, selectedMod, "跳过",
                    "选中项已不在当前清单或目录不唯一，请刷新列表后重试。");
                continue;
            }

            ManagedMod current = matches[0];
            if (!current.Id.Equals(selectedMod.Id, StringComparison.OrdinalIgnoreCase) ||
                current.Enabled != selectedMod.Enabled)
            {
                reviews[index] = Review(index, selectedMod, "跳过",
                    "模组状态已变化，请刷新列表后重新选择。");
                continue;
            }

            if (current.Enabled == enable)
            {
                reviews[index] = Review(index, current, "跳过",
                    "模组已经" + (enable ? "启用" : "停用") + "。");
                continue;
            }

            candidates.Add(new(index, current));
        }

        if (cancellationToken.IsCancellationRequested)
            return CancelledPreview(normalizedRoot, enable, selected, reviews);

        var graph = dependencyDiagnostics.Analyze(inventory);
        var order = dependencyDiagnostics.CreateToggleOrder(
            inventory,
            candidates.Select(candidate => candidate.Mod).ToArray(),
            enable,
            graph);

        var remaining = new List<Candidate>();
        foreach (var candidate in candidates)
        {
            if (order.CyclicFolders.Contains(candidate.Mod.Folder))
                reviews[candidate.SelectionIndex] = Review(
                    candidate.SelectionIndex, candidate.Mod, "跳过",
                    "此模组参与循环依赖，无法安全确定启停顺序。");
            else
                remaining.Add(candidate);
        }

        var candidateByFolder = remaining.ToDictionary(
            candidate => candidate.Mod.Folder,
            StringComparer.OrdinalIgnoreCase);
        var orderByFolder = order.Steps.ToDictionary(
            step => step.Mod.Folder,
            StringComparer.OrdinalIgnoreCase);
        var ordered = order.Steps
            .Where(step => candidateByFolder.ContainsKey(step.Mod.Folder))
            .Select(step => candidateByFolder[step.Mod.Folder])
            .ToList();

        var projectedMods = inventory.Mods.ToList();
        var execution = new List<ModToggleBatchStep>();
        var processedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in ordered)
        {
            if (cancellationToken.IsCancellationRequested)
                return CancelledPreview(normalizedRoot, enable, selected, reviews);

            int projectedIndex = projectedMods.FindIndex(mod =>
                mod.Folder.Equals(candidate.Mod.Folder, StringComparison.OrdinalIgnoreCase));
            if (projectedIndex < 0)
            {
                reviews[candidate.SelectionIndex] = Review(
                    candidate.SelectionIndex, candidate.Mod, "跳过", "预览期间模组清单发生变化。");
                continue;
            }

            ManagedMod projectedMod = projectedMods[projectedIndex];
            var projectedInventory = new ModInventory(projectedMods, inventory.Issues);
            string? blockReason;
            try
            {
                blockReason = dependencyDiagnostics
                    .DiagnoseToggle(normalizedRoot, projectedMod, projectedInventory)
                    .Message;
            }
            catch (Exception error)
            {
                reviews[candidate.SelectionIndex] = Review(
                    candidate.SelectionIndex, candidate.Mod, "跳过", "依赖检查失败：" + error.Message);
                continue;
            }

            if (blockReason is not null)
            {
                reviews[candidate.SelectionIndex] = Review(
                    candidate.SelectionIndex, candidate.Mod, "跳过", blockReason);
                continue;
            }

            if (cancellationToken.IsCancellationRequested)
                return CancelledPreview(normalizedRoot, enable, selected, reviews);

            Exception? previewError = null;
            try
            {
                _ = await Task.Run(
                    () => backend.Preview(normalizedRoot, candidate.Mod.Folder, enable),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CancelledPreview(normalizedRoot, enable, selected, reviews);
            }
            catch (Exception error)
            {
                previewError = error;
            }

            bool deferred = false;
            if (previewError is not null)
            {
                deferred = dependencyDiagnostics.IsDependencyBlockMessage(previewError.Message) &&
                    dependencyDiagnostics.HasEarlierRelatedDependency(candidate.Mod, graph, processedFolders, enable);

                if (!deferred)
                {
                    reviews[candidate.SelectionIndex] = Review(
                        candidate.SelectionIndex, candidate.Mod, "跳过", previewError.Message);
                    continue;
                }
            }

            string orderExplanation = orderByFolder[candidate.Mod.Folder].Explanation;
            execution.Add(new(candidate.Mod, deferred, orderExplanation));
            processedFolders.Add(candidate.Mod.Folder);
            projectedMods[projectedIndex] = projectedMod with { Enabled = enable };

            reviews[candidate.SelectionIndex] = Review(
                candidate.SelectionIndex,
                candidate.Mod,
                deferred ? "需复核" : "可处理",
                (deferred
                    ? "依赖项已排在前序步骤；前置操作完成后会再次预览并核对文件，再决定是否执行。"
                    : "预览与依赖检查通过；写入前仍会重新预览，防止文件或清单变化。") +
                " " + orderExplanation);

            reportProgress?.Invoke(new(
                "已完成预览 " + (execution.Count + reviews.Count(review => review?.State == "跳过")) +
                " / " + selected.Count + " 项。",
                Name: candidate.Mod.Name,
                State: deferred ? "需复核" : "可处理",
                Explanation: reviews[candidate.SelectionIndex]!.Explanation,
                Completed: execution.Count + reviews.Count(review => review?.State == "跳过"),
                Total: selected.Count));
        }

        if (cancellationToken.IsCancellationRequested)
            return CancelledPreview(normalizedRoot, enable, selected, reviews);

        for (int index = 0; index < reviews.Length; index++)
        {
            if (reviews[index] is null)
                reviews[index] = Review(index, selected[index], "跳过", "未能生成安全的执行方案。");
        }

        return new ModToggleBatchPreview(
            normalizedRoot,
            enable,
            selected.Count,
            reviews.Select(review => review!).ToArray(),
            execution,
            false,
            execution.Count == 0
                ? "没有可执行项；所有选中项均已跳过或被阻止。"
                : "批量预览完成：可处理 " + execution.Count + " 项，其余项目已列明原因。");
    }

    public async Task<ModToggleBatchResult> ConfirmAndApplyBatchAsync(
        ModToggleBatchPreview preview,
        Func<ModToggleBatchPreview, CancellationToken, Task<bool>> confirm,
        Action<ModToggleWorkflowProgress>? reportProgress = null,
        Func<bool>? cancellationRequested = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(confirm);

        var results = new List<ModToggleBatchItemResult>();
        int success = 0, failed = 0, skipped = 0, cancelled = 0;
        bool writing = false;

        void AddResult(ManagedMod mod, string state, string explanation)
        {
            results.Add(new(mod.Folder, mod.Name, state, explanation));
            if (state == "成功") success++;
            else if (state == "失败") failed++;
            else if (state == "已取消") cancelled++;
            else skipped++;

            reportProgress?.Invoke(new(
                "已处理 " + results.Count + " / " + preview.SelectedCount + " 项。",
                TransactionWriting: writing,
                Name: mod.Name,
                State: state,
                Explanation: explanation,
                Completed: results.Count,
                Total: preview.SelectedCount));
        }

        bool IsCancellationRequested() =>
            cancellationToken.IsCancellationRequested ||
            (cancellationRequested?.Invoke() ?? false);

        void AddRemainingAsCancelled(int startIndex)
        {
            for (int index = startIndex; index < preview.ExecutionOrder.Count; index++)
            {
                var mod = preview.ExecutionOrder[index].Mod;
                if (results.Any(item => item.Folder.Equals(mod.Folder, StringComparison.OrdinalIgnoreCase)))
                    continue;
                AddResult(mod, "已取消", "用户停止了剩余任务；当前文件事务已安全结束。");
            }
        }

        foreach (var review in preview.Reviews.Where(review => review.State is "跳过" or "已取消"))
        {
            results.Add(new(review.Folder, review.Name, review.State, review.Explanation));
            if (review.State == "已取消") cancelled++;
            else skipped++;
            reportProgress?.Invoke(new(
                "预览阶段" + review.State + "：" + review.Name + "。",
                Name: review.Name,
                State: review.State,
                Explanation: review.Explanation,
                Completed: results.Count,
                Total: preview.SelectedCount));
        }

        if (preview.Cancelled)
        {
            return BuildResult(false, true, results, success, failed, skipped, cancelled,
                "批量预览已取消，未移动任何文件。");
        }

        if (preview.ExecutionOrder.Count == 0)
            return BuildResult(false, false, results, success, failed, skipped, cancelled, preview.Status);

        if (IsCancellationRequested())
        {
            AddRemainingAsCancelled(0);
            return BuildResult(false, true, results, success, failed, skipped, cancelled,
                "批量操作已取消，未开始文件写入。");
        }

        bool confirmed;
        try
        {
            confirmed = await confirm(preview, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            confirmed = false;
        }

        if (!confirmed)
        {
            AddRemainingAsCancelled(0);
            return BuildResult(false, true, results, success, failed, skipped, cancelled,
                "用户取消了批量操作，未移动任何文件。");
        }

        if (IsCancellationRequested())
        {
            AddRemainingAsCancelled(0);
            return BuildResult(true, true, results, success, failed, skipped, cancelled,
                "批量操作已取消，未开始文件写入。");
        }

        try
        {
            for (int index = 0; index < preview.ExecutionOrder.Count; index++)
            {
                ModToggleBatchStep step = preview.ExecutionOrder[index];
                ManagedMod mod = step.Mod;

                if (IsCancellationRequested())
                {
                    AddRemainingAsCancelled(index);
                    break;
                }

                reportProgress?.Invoke(new(
                    "正在重新预览 " + mod.Name + "。",
                    TransactionWriting: writing,
                    Name: mod.Name,
                    State: "进行中",
                    Explanation: "写入前再次核对文件、清单和依赖。",
                    Completed: results.Count,
                    Total: preview.SelectedCount,
                    ItemStarted: true));

                ModTogglePlan plan;
                try
                {
                    // Each earlier move changes manifest paths and the Domain token.
                    // Re-preview immediately before Apply; no stale preflight token is reused.
                    plan = await Task.Run(
                        () => backend.Preview(preview.Root, mod.Folder, preview.Enable));
                }
                catch (Exception error)
                {
                    AddResult(mod,
                        IsDependencyBlock(error.Message) ? "跳过" : "失败",
                        IsDependencyBlock(error.Message)
                            ? "前置操作未能完成，当前依赖条件仍不满足：" + error.Message
                            : "执行前重新预览失败：" + error.Message);
                    continue;
                }

                if (IsCancellationRequested())
                {
                    AddRemainingAsCancelled(index);
                    break;
                }

                try
                {
                    if (!writing)
                    {
                        writing = true;
                        reportProgress?.Invoke(new(
                            "批量文件写入阶段已开始。",
                            TransactionWriting: true,
                            Name: mod.Name,
                            State: "进行中",
                            Explanation: "当前文件事务会安全完成；停止操作只影响后续项目。"));
                    }

                    // Deliberately do not pass cancellation to Apply. Domain owns the
                    // move/record/rollback transaction and must return at a safe boundary.
                    await Task.Run(() => backend.Apply(plan));
                    AddResult(mod, "成功",
                        "已" + (preview.Enable ? "启用" : "停用") + "；文件由管理器安全移动和记录。");
                }
                catch (Exception error)
                {
                    AddResult(mod, "失败", error.Message);
                }
            }
        }
        finally
        {
            if (writing)
            {
                writing = false;
                reportProgress?.Invoke(new(
                    "批量文件写入阶段已结束。",
                    TransactionWriting: false,
                    Explanation: "文件事务已安全结束，可衔接文件监测安静期。"));
            }
        }

        // Any execution item not recorded above was still pending when cancellation
        // was observed. It is safe to stop these because no transaction has started.
        foreach (var step in preview.ExecutionOrder)
        {
            if (results.Any(item => item.Folder.Equals(step.Mod.Folder, StringComparison.OrdinalIgnoreCase)))
                continue;
            AddResult(step.Mod, "已取消", "前序操作结束后停止了尚未开始的项目。");
        }

        bool wasCancelled = cancelled > 0 || IsCancellationRequested();
        string action = preview.Enable ? "启用" : "停用";
        string summary = $"批量{action}结束：成功 {success} 项，失败 {failed} 项，跳过 {skipped} 项，取消 {cancelled} 项。";
        return BuildResult(true, wasCancelled, results, success, failed, skipped, cancelled, summary);
    }

    static ModToggleBatchReview Review(
        int index, ManagedMod mod, string state, string explanation) =>
        new(index, mod.Folder, mod.Name, state, explanation);

    static ModToggleBatchPreview CancelledPreview(
        string root,
        bool enable,
        IReadOnlyList<ManagedMod> selected,
        ModToggleBatchReview?[] reviews)
    {
        for (int index = 0; index < reviews.Length; index++)
        {
            if (reviews[index] is null)
                reviews[index] = Review(
                    index, selected[index], "已取消", "预览阶段已取消，未开始文件写入。");
            else if (reviews[index]!.State is "可处理" or "需复核")
                reviews[index] = reviews[index]! with
                {
                    State = "已取消",
                    Explanation = "预览阶段已取消，未开始文件写入。"
                };
        }

        return new(
            root, enable, selected.Count,
            reviews.Select(review => review!).ToArray(),
            Array.Empty<ModToggleBatchStep>(),
            true,
            "批量预览已取消，未移动任何文件。");
    }

    static ModToggleBatchResult BuildResult(
        bool confirmed,
        bool wasCancelled,
        IReadOnlyList<ModToggleBatchItemResult> items,
        int success,
        int failed,
        int skipped,
        int cancelled,
        string summary) =>
        new(confirmed, wasCancelled, items, success, failed, skipped, cancelled, summary);
}
