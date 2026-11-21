using System;
using System.Collections.Generic;
using System.Linq;

namespace SpirePatch;

internal sealed record ModDependencyDiagnosticEdge(
    ManagedMod Dependent,
    string DependencyId,
    string DependencyName,
    ManagedMod? Prerequisite,
    ModDependencyStatus Status,
    string Message,
    bool FromPatchRule,
    bool IsCycle = false)
{
    internal bool IsIssue => Status != ModDependencyStatus.Satisfied || IsCycle;
    internal bool IsActiveIssue => Dependent.Enabled && IsIssue;
}

internal sealed record ModDependencyDiagnosticCycle(string[] ModIds, string[] Names);

internal sealed record ModDependencyDiagnosticSummary(
    int DependencyCount,
    int UnsatisfiedCount,
    int ActiveIssueCount,
    int CycleEdgeCount)
{
    internal bool HasIssue => UnsatisfiedCount > 0 || CycleEdgeCount > 0;

    internal string Label =>
        DependencyCount == 0 ? "未声明前置" :
        ActiveIssueCount > 0 ? $"依赖异常 · {ActiveIssueCount} 项" :
        UnsatisfiedCount > 0 ? $"前置待满足 · {UnsatisfiedCount} 项" :
        CycleEdgeCount > 0 ? "存在循环依赖" :
        $"依赖正常 · {DependencyCount} 项";
}

internal sealed record ModDependencyDiagnosticsReport(
    ModDependencyDiagnosticEdge[] Edges,
    ModDependencyDiagnosticCycle[] Cycles,
    IReadOnlyDictionary<string, ModDependencyDiagnosticSummary> Summaries,
    int ActiveIssueCount)
{
    internal ModDependencyDiagnosticSummary Summary(string modId) =>
        Summaries.TryGetValue(modId, out var summary)
            ? summary
            : new ModDependencyDiagnosticSummary(0, 0, 0, 0);
}

internal sealed record ModDependencyOrderStep(
    ManagedMod Mod,
    int Position,
    string Explanation);

internal sealed record ModDependencyOrderPlan(
    bool Enable,
    IReadOnlyList<ModDependencyOrderStep> Steps,
    IReadOnlySet<string> CyclicFolders);

internal enum ModToggleBlockKind
{
    None,
    Dependency,
    Inventory,
    FileOrDirectory
}

internal sealed record ModTogglePreflightDiagnostic(
    bool CanProceed,
    string? Message,
    ModToggleBlockKind Kind,
    int RelatedEnabledDependentCount)
{
    internal bool IsDependencyProtection =>
        Kind == ModToggleBlockKind.Dependency && RelatedEnabledDependentCount > 0;
}

/// <summary>
/// Application-facing dependency diagnostics and ordering. The Domain graph remains
/// the single source of truth for manifest rules, patch-required MODs, version parsing
/// and cycle detection. This service only shapes its result for workflows and UI.
/// </summary>
internal sealed class ModDependencyDiagnosticsService
{
    readonly Func<string, ManagedMod, ModInventory, string?> blockReason;

    public ModDependencyDiagnosticsService(
        Func<string, ManagedMod, ModInventory, string?>? blockReason = null)
    {
        this.blockReason = blockReason ?? ModManager.BlockReason;
    }

    public ModDependencyDiagnosticsReport Analyze(ModInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var domain = ModDependencyGraph.Analyze(inventory);
        var edges = domain.Edges.Select(edge => new ModDependencyDiagnosticEdge(
            edge.Dependent,
            edge.DependencyId,
            edge.DependencyName,
            edge.Prerequisite,
            edge.Status,
            edge.Message,
            edge.FromPatchRule,
            edge.IsCycle)).ToArray();
        var cycles = domain.Cycles
            .Select(cycle => new ModDependencyDiagnosticCycle(cycle.ModIds, cycle.Names))
            .ToArray();
        var summaries = domain.Summaries.ToDictionary(
            item => item.Key,
            item => new ModDependencyDiagnosticSummary(
                item.Value.DependencyCount,
                item.Value.UnsatisfiedCount,
                item.Value.ActiveIssueCount,
                item.Value.CycleEdgeCount),
            StringComparer.OrdinalIgnoreCase);

        return new(edges, cycles, summaries, domain.ActiveIssueCount);
    }

    public ModDependencyOrderPlan CreateToggleOrder(
        ModInventory inventory,
        IReadOnlyList<ManagedMod> selected,
        bool enable,
        ModDependencyDiagnosticsReport? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(selected);
        diagnostics ??= Analyze(inventory);

        var unique = selected
            .GroupBy(mod => mod.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var selectedByFolder = unique.ToDictionary(
            mod => mod.Folder,
            StringComparer.OrdinalIgnoreCase);
        var cycleIds = diagnostics.Cycles
            .SelectMany(cycle => cycle.ModIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cyclicFolders = unique
            .Where(mod => cycleIds.Contains(mod.Id))
            .Select(mod => mod.Folder)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = unique
            .Where(mod => !cyclicFolders.Contains(mod.Folder))
            .ToArray();

        var outgoing = remaining.ToDictionary(
            mod => mod.Folder,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        var indegree = remaining.ToDictionary(
            mod => mod.Folder,
            _ => 0,
            StringComparer.OrdinalIgnoreCase);

        // Domain edges point dependent -> prerequisite. Enabling reverses the
        // relation; disabling retains it so dependents are stopped first.
        foreach (var edge in diagnostics.Edges)
        {
            if (edge.Prerequisite is null ||
                !selectedByFolder.ContainsKey(edge.Dependent.Folder) ||
                !selectedByFolder.ContainsKey(edge.Prerequisite.Folder) ||
                !outgoing.ContainsKey(edge.Dependent.Folder) ||
                !outgoing.ContainsKey(edge.Prerequisite.Folder))
                continue;

            string from = enable ? edge.Prerequisite.Folder : edge.Dependent.Folder;
            string to = enable ? edge.Dependent.Folder : edge.Prerequisite.Folder;
            if (outgoing[from].Add(to))
                indegree[to]++;
        }

        var selectionIndex = unique
            .Select((mod, index) => (mod.Folder, index))
            .ToDictionary(item => item.Folder, item => item.index, StringComparer.OrdinalIgnoreCase);
        var ready = remaining
            .Where(mod => indegree[mod.Folder] == 0)
            .OrderBy(mod => selectionIndex[mod.Folder])
            .ToList();
        var ordered = new List<ManagedMod>(remaining.Length);

        while (ready.Count > 0)
        {
            ManagedMod next = ready[0];
            ready.RemoveAt(0);
            ordered.Add(next);

            foreach (string target in outgoing[next.Folder])
            {
                indegree[target]--;
                if (indegree[target] == 0)
                {
                    ready.Add(selectedByFolder[target]);
                    ready.Sort((left, right) =>
                        selectionIndex[left.Folder].CompareTo(selectionIndex[right.Folder]));
                }
            }
        }

        var orderedFolders = ordered
            .Select(mod => mod.Folder)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in remaining.Where(mod => !orderedFolders.Contains(mod.Folder)))
            cyclicFolders.Add(mod.Folder);

        var steps = new List<ModDependencyOrderStep>(ordered.Count);
        for (int index = 0; index < ordered.Count; index++)
        {
            ManagedMod mod = ordered[index];
            string[] relatedNames = enable
                ? diagnostics.Edges
                    .Where(edge => edge.Dependent.Folder.Equals(mod.Folder, StringComparison.OrdinalIgnoreCase) &&
                        edge.Prerequisite is not null &&
                        selectedByFolder.ContainsKey(edge.Prerequisite.Folder))
                    .Select(edge => edge.Prerequisite!.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : diagnostics.Edges
                    .Where(edge => edge.Prerequisite?.Folder.Equals(mod.Folder, StringComparison.OrdinalIgnoreCase) == true &&
                        selectedByFolder.ContainsKey(edge.Dependent.Folder))
                    .Select(edge => edge.Dependent.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

            string explanation = relatedNames.Length == 0
                ? $"第 {index + 1} 项：本次选中项之间没有需要优先处理的直接依赖，按选择顺序处理。"
                : enable
                    ? $"第 {index + 1} 项：其前置项（{string.Join("、", relatedNames)}）已安排在前面，随后启用 {mod.Name}。"
                    : $"第 {index + 1} 项：先停用依赖它的项目（{string.Join("、", relatedNames)}），再停用前置 {mod.Name}。";
            steps.Add(new(mod, index + 1, explanation));
        }

        return new(enable, steps, cyclicFolders);
    }

    public ModTogglePreflightDiagnostic DiagnoseToggle(
        string root,
        ManagedMod mod,
        ModInventory inventory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(inventory);

        var report = Analyze(inventory);
        int relatedEnabledDependents = report.Edges
            .Where(edge => edge.DependencyId.Equals(mod.Id, StringComparison.OrdinalIgnoreCase) &&
                edge.Dependent.Enabled)
            .Select(edge => edge.Dependent.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        bool hasUnsatisfiedPrerequisite = report.Edges.Any(edge =>
            edge.Dependent.Folder.Equals(mod.Folder, StringComparison.OrdinalIgnoreCase) &&
            edge.Status != ModDependencyStatus.Satisfied);
        bool isDependencyBlock = mod.Enabled
            ? relatedEnabledDependents > 0
            : hasUnsatisfiedPrerequisite;

        string? message = blockReason(root, mod, inventory);
        ModToggleBlockKind kind = message is null
            ? ModToggleBlockKind.None
            : isDependencyBlock
                ? ModToggleBlockKind.Dependency
                : inventory.Issues.Count > 0
                    ? ModToggleBlockKind.Inventory
                    : ModToggleBlockKind.FileOrDirectory;

        return new(message is null, message, kind, relatedEnabledDependents);
    }

    public bool IsDependencyBlockMessage(string message) =>
        message.StartsWith("请先启用前置模组：", StringComparison.Ordinal) ||
        message.StartsWith("这些启用项依赖它，请先停用依赖项：", StringComparison.Ordinal);

    public bool HasEarlierRelatedDependency(
        ManagedMod mod,
        ModDependencyDiagnosticsReport diagnostics,
        IReadOnlySet<string> processedFolders,
        bool enable)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(processedFolders);

        return enable
            ? diagnostics.Edges.Any(edge =>
                edge.Dependent.Folder.Equals(mod.Folder, StringComparison.OrdinalIgnoreCase) &&
                edge.Prerequisite is not null &&
                processedFolders.Contains(edge.Prerequisite.Folder))
            : diagnostics.Edges.Any(edge =>
                edge.Prerequisite?.Folder.Equals(mod.Folder, StringComparison.OrdinalIgnoreCase) == true &&
                processedFolders.Contains(edge.Dependent.Folder));
    }
}
