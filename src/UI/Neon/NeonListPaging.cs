using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SpirePatch;

internal sealed partial class NeonWorkbenchView
{
    internal const int NeonModPageSize = 40;
    int neonModPage = 1;
    int neonListScrollRequest;
    long neonRowBuildCount;
    bool neonExplicitPage;
    readonly Dictionary<string, PageRow> neonPageRows = new(StringComparer.OrdinalIgnoreCase);
    Border? neonPagePager;
    TextBlock? neonPageLabel;
    Button? neonPagePrevious, neonPageNext;
    sealed record PageRow(NeonWorkbenchRow Row, Border Element, bool Running,
        bool Highlighted, NeonInlineToggleStatus? InlineStatus);

    internal int NeonRealizedModRows => neonPageRows.Count;
    internal long NeonModRowBuildCount => neonRowBuildCount;
    internal int NeonCurrentModPage => neonModPage;
    internal void SetNeonPerfNameSort() => sortCombo.SelectedIndex = 1;

    void ResetNeonListChildren()
    {
        deskContentStack.Children.Clear();
        visibleRowVisuals.Clear();
        neonPageRows.Clear();
    }

    // Only visible inputs participate: inspector data is always obtained from the latest snapshot.
    // A reused row's event closures still refer to an equivalent ManagedMod, never a moved path.
    static bool SameMod(ManagedMod a, ManagedMod b) =>
        (a with { Dependencies = b.Dependencies }) == b &&
        a.Dependencies.SequenceEqual(b.Dependencies);

    static bool SameEdge(ModDependencyEdge? a, ModDependencyEdge? b)
    {
        if (a == null || b == null) return a == b;
        return SameMod(a.Dependent, b.Dependent) &&
            (a.Prerequisite == null || b.Prerequisite == null
                ? a.Prerequisite == b.Prerequisite : SameMod(a.Prerequisite, b.Prerequisite)) &&
            (a with { Dependent = b.Dependent, Prerequisite = b.Prerequisite }) == b;
    }

    static bool SameVisualRow(NeonWorkbenchRow a, NeonWorkbenchRow b) =>
        SameMod(a.Mod, b.Mod) && a.Category == b.Category &&
        a.SourceMuted == b.SourceMuted && a.HasDanger == b.HasDanger && a.HasWarn == b.HasWarn &&
        a.HasUpdate == b.HasUpdate && a.IsUnbound == b.IsUnbound &&
        a.GitHubResult == b.GitHubResult && a.WorkshopResult == b.WorkshopResult &&
        a.CandidateSource == b.CandidateSource &&
        a.ToggleBlockReason == b.ToggleBlockReason && a.IsLockedByDependents == b.IsLockedByDependents &&
        SameEdge(a.OwnDisabledPrereq, b.OwnDisabledPrereq) && SameEdge(a.OwnMissing, b.OwnMissing) &&
        SameEdge(a.OwnOtherIssue, b.OwnOtherIssue) && SameEdge(a.CausingDisabledBlock, b.CausingDisabledBlock) &&
        a.InboundActiveBroken.Length == b.InboundActiveBroken.Length &&
        a.InboundActiveBroken.Zip(b.InboundActiveBroken).All(p => SameEdge(p.First, p.Second)) &&
        a.OwnVersionMismatch == b.OwnVersionMismatch && a.CausingVersionMismatch == b.CausingVersionMismatch;

    bool NeonHighlighted(NeonWorkbenchRow row) =>
        string.Equals(highlightedModId, row.Mod.Id, StringComparison.OrdinalIgnoreCase);

    void RememberNeonPageRow(NeonWorkbenchRow row, Border element) =>
        neonPageRows[row.Mod.Manifest] = new(row, element, currentSnapshot?.GameRunning == true,
            NeonHighlighted(row), GetInlineToggleStatus(row.Mod.Id));

    void ReconcileNeonListPage(NeonWorkbenchRow[] filtered, bool cascade)
    {
        int pages = Math.Max(1, (filtered.Length + NeonModPageSize - 1) / NeonModPageSize);
        // If a selected row on the current page moves during sort/filter/toggle, keep it reachable.
        if (!neonExplicitPage && selectedModId != null &&
            neonPageRows.Values.Any(x => x.Row.Mod.Id.Equals(selectedModId, StringComparison.OrdinalIgnoreCase)))
        {
            int selectedIndex = Array.FindIndex(filtered, x => x.Mod.Id.Equals(selectedModId, StringComparison.OrdinalIgnoreCase));
            if (selectedIndex >= 0) neonModPage = selectedIndex / NeonModPageSize + 1;
        }
        neonModPage = Math.Clamp(neonModPage, 1, pages);
        var page = filtered.Skip((neonModPage - 1) * NeonModPageSize).Take(NeonModPageSize).ToArray();
        var keys = page.Select(x => x.Mod.Manifest).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in neonPageRows.Keys.Where(k => !keys.Contains(k)).ToArray())
        {
            deskContentStack.Children.Remove(neonPageRows[key].Element);
            neonPageRows.Remove(key);
        }

        var desired = new List<UIElement>(page.Length + 1);
        // Maintain the existing ID-keyed visual map only for the currently realized rows.
        var oldVisuals = visibleRowVisuals.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        visibleRowVisuals.Clear();
        for (int i = 0; i < page.Length; i++)
        {
            var row = page[i];
            Border element;
            if (neonPageRows.TryGetValue(row.Mod.Manifest, out var old) &&
                old.Running == (currentSnapshot?.GameRunning == true) &&
                old.Highlighted == NeonHighlighted(row) &&
                old.InlineStatus == GetInlineToggleStatus(row.Mod.Id) &&
                SameVisualRow(old.Row, row) &&
                oldVisuals.TryGetValue(row.Mod.Id, out var visuals) && ReferenceEquals(visuals.Outer, old.Element))
            {
                element = old.Element;
                visuals.Check.IsChecked = checkedModFolders.Contains(row.Mod.Folder);
                visibleRowVisuals[row.Mod.Id] = visuals;
            }
            else
            {
                if (old != null) deskContentStack.Children.Remove(old.Element);
                element = BuildModRowElement(row);
                if (cascade) NeonMotion.PlayCascadeReveal(this, element, i);
            }
            RememberNeonPageRow(row, element);
            desired.Add(element);
        }
        if (pages > 1)
        {
            EnsureNeonPagePager();
            neonPageLabel!.Text = $"第 {neonModPage} / {pages} 页（每页 {NeonModPageSize} 项）";
            neonPagePrevious!.IsEnabled = neonModPage > 1;
            neonPageNext!.IsEnabled = neonModPage < pages;
            desired.Add(neonPagePager!);
            listSummaryMeta.Text += $" · 第 {neonModPage}/{pages} 页";
        }

        var keep = desired.ToHashSet();
        for (int i = deskContentStack.Children.Count - 1; i >= 0; i--)
            if (!keep.Contains(deskContentStack.Children[i])) deskContentStack.Children.RemoveAt(i);
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < deskContentStack.Children.Count && ReferenceEquals(deskContentStack.Children[i], desired[i])) continue;
            int oldIndex = deskContentStack.Children.IndexOf(desired[i]);
            if (oldIndex >= 0) deskContentStack.Children.RemoveAt(oldIndex);
            deskContentStack.Children.Insert(i, desired[i]);
        }
        UpdateRowSelectionVisualsOnly();
    }

    void EnsureNeonPagePager()
    {
        if (neonPagePager != null) return;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        neonPageLabel = NeonWidgets.Text("", 12, NeonPalette.InkSecondary);
        grid.Children.Add(neonPageLabel);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        neonPagePrevious = NeonWidgets.CreateStyledButton(this, "上一页", "NeonSecondaryButton",
            () => ChangeNeonModPage(neonModPage - 1), "xs");
        neonPagePrevious.Margin = new Thickness(0, 0, 6, 0);
        neonPageNext = NeonWidgets.CreateStyledButton(this, "下一页", "NeonSecondaryButton",
            () => ChangeNeonModPage(neonModPage + 1), "xs");
        buttons.Children.Add(neonPagePrevious);
        buttons.Children.Add(neonPageNext);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        neonPagePager = new Border { Background = NeonPalette.BgGround, BorderBrush = NeonPalette.BorderSubtle,
            BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(14, 10, 14, 10), Child = grid };
    }

    internal void ChangeNeonModPage(int page)
    {
        neonModPage = Math.Max(1, page);
        neonExplicitPage = true;
        try { RenderWorkbenchContent(preserveScroll: false); }
        finally { neonExplicitPage = false; }
    }

    void EnsureNeonPageForMod(string modId)
    {
        if (currentSnapshot == null || workbenchMode != "list") return;
        var rows = NeonWorkbenchBuilder.FilterAndSort(currentSnapshot.AllRows, activeFilter, searchBox.Text, sortBy);
        int index = Array.FindIndex(rows, r => r.Mod.Id.Equals(modId, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        int page = index / NeonModPageSize + 1;
        if (page != neonModPage) ChangeNeonModPage(page);
    }
}
