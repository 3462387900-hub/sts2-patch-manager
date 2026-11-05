using System;
using System.Collections.Generic;
using System.Linq;

namespace SpirePatch;

internal enum ModDependencyStatus
{
    Satisfied,
    Missing,
    DisabledPrerequisite,
    Duplicate,
    VersionMismatch,
    VersionUnknown,
    InvalidConstraint
}

internal sealed record ModDependencyEdge(
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

internal sealed record ModDependencyCycle(string[] ModIds, string[] Names);

internal sealed record ModDependencySummary(
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

internal sealed record ModDependencyGraphReport(
    ModDependencyEdge[] Edges,
    ModDependencyCycle[] Cycles,
    IReadOnlyDictionary<string, ModDependencySummary> Summaries,
    int ActiveIssueCount)
{
    internal ModDependencySummary Summary(string modId) =>
        Summaries.TryGetValue(modId, out var summary)
            ? summary
            : new ModDependencySummary(0, 0, 0, 0);
}

/// <summary>
/// Read-only analysis of required MOD dependencies. It never changes files or MOD state.
/// Optional dependencies are already excluded by ModManager's manifest parser.
/// </summary>
internal static class ModDependencyGraph
{
    internal static ModDependencyGraphReport Analyze(ModInventory inventory)
    {
        var byId = inventory.Mods
            .GroupBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var edges = new List<ModDependencyEdge>();
        foreach (var dependent in inventory.Mods)
        {
            var declared = dependent.Dependencies
                .Select(dependency => (Dependency: dependency, FromPatchRule: false))
                .Concat(ModManager.RequiredByPatch(dependent.Id)
                    .Select(id => (Dependency: new ModDependency(id), FromPatchRule: true)))
                .GroupBy(item => item.Dependency.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First());

            foreach (var item in declared)
            {
                var dependency = item.Dependency;
                if (!byId.TryGetValue(dependency.Id, out var candidates) || candidates.Length == 0)
                {
                    edges.Add(new ModDependencyEdge(
                        dependent, dependency.Id, dependency.Id, null,
                        ModDependencyStatus.Missing,
                        dependent.Enabled
                            ? "此 MOD 已启用，但必需前置未安装。"
                            : "必需前置尚未安装；启用此 MOD 前需要先安装。",
                        item.FromPatchRule));
                    continue;
                }

                if (candidates.Length > 1)
                {
                    edges.Add(new ModDependencyEdge(
                        dependent, dependency.Id, dependency.Id, null,
                        ModDependencyStatus.Duplicate,
                        "本地存在多个相同 ID 的 MOD，无法确定依赖目标。",
                        item.FromPatchRule));
                    continue;
                }

                ManagedMod prerequisite = candidates[0];
                if (!prerequisite.Enabled)
                {
                    edges.Add(new ModDependencyEdge(
                        dependent, dependency.Id, prerequisite.Name, prerequisite,
                        ModDependencyStatus.DisabledPrerequisite,
                        dependent.Enabled
                            ? "此 MOD 已启用，但它依赖的前置模组当前已停用。"
                            : "前置模组当前已停用；启用此 MOD 前需要先启用前置。",
                        item.FromPatchRule));
                    continue;
                }

                var (status, message) = CheckVersion(dependency, prerequisite);
                edges.Add(new ModDependencyEdge(
                    dependent, dependency.Id, prerequisite.Name, prerequisite,
                    status, message, item.FromPatchRule));
            }
        }

        var cycles = FindCycles(inventory.Mods, edges);
        var cyclePairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cycle in cycles)
        {
            var members = cycle.ModIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var edge in edges)
            {
                if (members.Contains(edge.Dependent.Id) && members.Contains(edge.DependencyId))
                    cyclePairs.Add(Pair(edge.Dependent.Id, edge.DependencyId));
            }
        }

        for (int index = 0; index < edges.Count; index++)
        {
            var edge = edges[index];
            if (cyclePairs.Contains(Pair(edge.Dependent.Id, edge.DependencyId)))
                edges[index] = edge with { IsCycle = true };
        }

        var summaries = new Dictionary<string, ModDependencySummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in inventory.Mods.GroupBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase))
        {
            var related = edges.Where(edge => edge.Dependent.Id.Equals(group.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
            summaries[group.Key] = new ModDependencySummary(
                related.Length,
                related.Count(edge => edge.Status != ModDependencyStatus.Satisfied),
                related.Count(edge => edge.IsActiveIssue),
                related.Count(edge => edge.IsCycle));
        }

        int activeIssues = edges.Count(edge => edge.IsActiveIssue);
        return new ModDependencyGraphReport(
            edges.OrderBy(edge => edge.Dependent.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(edge => edge.DependencyName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            cycles,
            summaries,
            activeIssues);
    }

    static (ModDependencyStatus Status, string Message) CheckVersion(
        ModDependency dependency,
        ManagedMod prerequisite)
    {
        bool hasMinimum = !string.IsNullOrWhiteSpace(dependency.Minimum);
        bool hasMaximum = !string.IsNullOrWhiteSpace(dependency.Maximum);
        if (!hasMinimum && !hasMaximum)
            return (ModDependencyStatus.Satisfied, "前置已安装并启用；清单未声明版本范围。");

        if (!TryVersion(prerequisite.Version, out var actual))
            return (ModDependencyStatus.VersionUnknown,
                "前置已安装并启用，但版本号无法解析，不能确认是否满足要求。");

        Version? minimum = null;
        Version? maximum = null;
        if ((hasMinimum && !TryVersion(dependency.Minimum, out minimum)) ||
            (hasMaximum && !TryVersion(dependency.Maximum, out maximum)) ||
            (minimum is not null && maximum is not null && minimum > maximum))
            return (ModDependencyStatus.InvalidConstraint,
                "依赖清单中的最低 / 最高版本约束无效。");

        if (minimum is not null && actual < minimum)
            return (ModDependencyStatus.VersionMismatch,
                $"前置版本过低：当前 {prerequisite.Version}，最低要求 {dependency.Minimum}。");

        if (maximum is not null && actual > maximum)
            return (ModDependencyStatus.VersionMismatch,
                $"前置版本过高：当前 {prerequisite.Version}，最高允许 {dependency.Maximum}。");

        return (ModDependencyStatus.Satisfied,
            "前置已安装并启用，版本满足声明范围。");
    }

    static bool TryVersion(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value))
            return false;
        string normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
            normalized = normalized[1..];
        if (!Version.TryParse(normalized, out var parsed) || parsed is null)
            return false;
        version = parsed;
        return true;
    }

    static ModDependencyCycle[] FindCycles(
        IReadOnlyList<ManagedMod> mods,
        IReadOnlyList<ModDependencyEdge> edges)
    {
        var adjacency = mods
            .Select(mod => mod.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                id => id,
                id => edges.Where(edge =>
                        edge.Dependent.Id.Equals(id, StringComparison.OrdinalIgnoreCase) &&
                        edge.Prerequisite is not null &&
                        edge.Status != ModDependencyStatus.Duplicate)
                    .Select(edge => edge.DependencyId)
                    .Where(dependencyId => mods.Any(mod =>
                        mod.Id.Equals(dependencyId, StringComparison.OrdinalIgnoreCase)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lowLinks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ModDependencyCycle>();
        int nextIndex = 0;

        void Visit(string id)
        {
            indexes[id] = nextIndex;
            lowLinks[id] = nextIndex;
            nextIndex++;
            stack.Push(id);
            onStack.Add(id);

            foreach (string dependencyId in adjacency[id])
            {
                if (!indexes.ContainsKey(dependencyId))
                {
                    Visit(dependencyId);
                    lowLinks[id] = Math.Min(lowLinks[id], lowLinks[dependencyId]);
                }
                else if (onStack.Contains(dependencyId))
                {
                    lowLinks[id] = Math.Min(lowLinks[id], indexes[dependencyId]);
                }
            }

            if (lowLinks[id] != indexes[id])
                return;

            var component = new List<string>();
            string current;
            do
            {
                current = stack.Pop();
                onStack.Remove(current);
                component.Add(current);
            } while (!current.Equals(id, StringComparison.OrdinalIgnoreCase));

            bool selfLoop = component.Count == 1 &&
                adjacency[component[0]].Contains(component[0], StringComparer.OrdinalIgnoreCase);
            if (component.Count <= 1 && !selfLoop)
                return;

            string[] ids = component
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] names = ids.Select(value =>
                mods.FirstOrDefault(mod => mod.Id.Equals(value, StringComparison.OrdinalIgnoreCase))?.Name ?? value)
                .ToArray();
            result.Add(new ModDependencyCycle(ids, names));
        }

        foreach (string id in adjacency.Keys)
            if (!indexes.ContainsKey(id))
                Visit(id);

        return result
            .OrderBy(cycle => string.Join(", ", cycle.Names), StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    static string Pair(string dependentId, string dependencyId) =>
        dependentId + "\0" + dependencyId;
}
