using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SpirePatch;

internal static class ModDependencyGraphPresentation
{
    internal static string Status(ModDependencyStatus status) => status switch
    {
        ModDependencyStatus.Satisfied => "已满足",
        ModDependencyStatus.Missing => "前置未安装",
        ModDependencyStatus.DisabledPrerequisite => "前置已停用",
        ModDependencyStatus.Duplicate => "ID 重复",
        ModDependencyStatus.VersionMismatch => "版本不符",
        ModDependencyStatus.VersionUnknown => "版本无法核对",
        ModDependencyStatus.InvalidConstraint => "版本约束无效",
        _ => "未知状态"
    };
}

public partial class MainWindow
{
    void ShowDependencyGraph(string filter = "全部关系")
    {
        if (selectedInstallRoot is null || modInventory is null)
        {
            ShowMods();
            return;
        }

        var report = ModDependencyGraph.Analyze(modInventory);
        var root = V(Header(
            "MOD DEPENDENCY MAP",
            "依赖关系图",
            "箭头方向：前置模组 → 依赖它的 MOD。只分析清单声明和已收录修复补丁的兼容规则，不推断未声明依赖。"));
        Space(root, 14);
        root.Children.Add(H(
            Btn("← 返回模组管理", ShowMods, "Ghost"),
            Btn("重新扫描", RefreshMods, "Ghost")));
        Space(root, 12);

        int missing = report.Edges.Count(edge => edge.Status == ModDependencyStatus.Missing);
        int disabled = report.Edges.Count(edge => edge.Status == ModDependencyStatus.DisabledPrerequisite);
        int versionProblems = report.Edges.Count(edge =>
            edge.Status is ModDependencyStatus.VersionMismatch or
                ModDependencyStatus.VersionUnknown or ModDependencyStatus.InvalidConstraint);
        root.Children.Add(Card(H(
            DependencyMetric(report.Edges.Length.ToString(), "依赖关系"),
            DependencyMetric(report.ActiveIssueCount.ToString(), "启用异常关系"),
            DependencyMetric(missing.ToString(), "缺失前置"),
            DependencyMetric(disabled.ToString(), "前置已停用"),
            DependencyMetric(versionProblems.ToString(), "版本问题"),
            DependencyMetric(report.Cycles.Length.ToString(), "循环依赖")), padding: 14));
        Space(root, 12);

        root.Children.Add(Card(V(
            T("分析范围与限制", 14, ink, true),
            T("仅读取本地 MOD 清单中的必需 dependencies，以及修复补丁库中明确声明的必需 MOD。optional 前置不会作为阻断项。未声明的运行时依赖、加载顺序要求和作者文档中的隐含条件无法仅凭清单推断。", 12, muted),
            T("本页为只读分析，不会自动安装、启用、停用或移动任何 MOD。", 12, muted)), padding: 14));
        Space(root, 10);

        if (modInventory.Issues.Count > 0)
        {
            root.Children.Add(Card(V(
                T("当前清单存在读取问题", 14, gold, true),
                T(string.Join("\n", modInventory.Issues.Take(6)), 12, muted),
                T("重复 ID 或无法读取的清单可能使部分依赖关系无法准确判定。请先处理这些问题。", 12, gold)), padding: 14));
            Space(root, 10);
        }

        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (string option in new[] { "全部关系", "仅显示异常", "仅显示启用 MOD 异常", "仅显示已满足" })
        {
            string selected = option;
            var button = Btn(option, () => ShowDependencyGraph(selected),
                filter == option ? "SelectedTab" : "Ghost");
            button.Margin = new Thickness(0, 0, 7, 6);
            filters.Children.Add(button);
        }
        root.Children.Add(filters);

        if (report.Cycles.Length > 0)
        {
            var cyclePanel = V(T("循环依赖", 16, ink, true),
                T("以下 MOD 形成闭环。管理器不会擅自决定应该移除哪一条依赖。", 12, muted));
            foreach (var cycle in report.Cycles)
            {
                cyclePanel.Children.Add(Card(V(
                    T(string.Join("  ↔  ", cycle.Names), 14, gold, true),
                    T(string.Join(" · ", cycle.ModIds), 11, muted)), padding: 12));
                Space(cyclePanel, 6);
            }
            root.Children.Add(Card(cyclePanel, padding: 14));
            Space(root, 12);
        }

        IEnumerable<ModDependencyEdge> filtered = report.Edges;
        filtered = filter switch
        {
            "仅显示异常" => filtered.Where(edge => edge.IsIssue),
            "仅显示启用 MOD 异常" => filtered.Where(edge => edge.IsActiveIssue),
            "仅显示已满足" => filtered.Where(edge =>
                edge.Status == ModDependencyStatus.Satisfied && !edge.IsCycle),
            _ => filtered
        };

        var visible = filtered.ToArray();
        root.Children.Add(T($"依赖连线 · 显示 {visible.Length} / {report.Edges.Length} 条", 15, ink, true));
        Space(root, 8);

        if (visible.Length == 0)
        {
            root.Children.Add(Card(V(
                T(report.Edges.Length == 0 ? "没有发现可分析的必需前置关系" : "当前筛选条件下没有关系",
                    16, ink, true),
                T(report.Edges.Length == 0
                    ? "本地清单没有声明必需前置。未声明不代表绝对不存在运行时依赖。"
                    : "可以切换筛选条件查看其他依赖关系。", 12, muted)), padding: 18));
        }

        foreach (var edge in visible)
        {
            string status = edge.IsCycle
                ? "循环依赖"
                : ModDependencyGraphPresentation.Status(edge.Status);
            Brush statusColor = edge.IsIssue ? gold : green;

            var relation = new Grid { Margin = new Thickness(0, 8, 0, 8) };
            relation.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            relation.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            relation.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var prerequisite = edge.Prerequisite;
            var left = Card(V(
                T(prerequisite?.Name ?? (edge.Status == ModDependencyStatus.Duplicate
                    ? "存在多个同 ID 前置" : "缺少前置模组"), 14, ink, true),
                T("MOD ID：" + edge.DependencyId, 11, muted),
                T(prerequisite is null
                    ? "本地没有唯一可用的目标"
                    : "版本号：" + prerequisite.Version + " · " + (prerequisite.Enabled ? "已启用" : "已停用"),
                    11, prerequisite?.Enabled == true ? green : gold),
                T(edge.FromPatchRule ? "来源：修复补丁兼容规则" : "来源：MOD 清单声明", 10, muted)),
                prerequisite is null ? "#B0473030" : "#B51A3550", 12);
            Grid.SetColumn(left, 0);
            relation.Children.Add(left);

            var arrow = V(
                T("→", 27, statusColor, true),
                T("前置", 10, muted));
            arrow.HorizontalAlignment = HorizontalAlignment.Center;
            arrow.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(arrow, 1);
            relation.Children.Add(arrow);

            var dependent = Card(V(
                T(edge.Dependent.Name, 14, ink, true),
                T("MOD ID：" + edge.Dependent.Id, 11, muted),
                T("版本号：" + edge.Dependent.Version + " · " + (edge.Dependent.Enabled ? "已启用" : "已停用"),
                    11, edge.Dependent.Enabled ? green : muted),
                T("状态：" + (edge.Dependent.Enabled ? "当前生效" : "当前未启用"), 10, muted)),
                "#B51A3550", 12);
            Grid.SetColumn(dependent, 2);
            relation.Children.Add(dependent);

            var detail = V(H(Badge(status, statusColor),
                T(edge.Dependent.Enabled ? "影响当前启用环境" : "所属 MOD 当前已停用", 11, muted)),
                T(edge.Message, 12, muted));
            if (edge.IsCycle)
                detail.Children.Add(T("此连线属于上方标出的循环依赖组。", 11, gold));
            var actions = new WrapPanel();
            actions.Children.Add(Btn("查看依赖者详情", () => ShowModDetail(edge.Dependent.Folder), "Ghost"));
            if (prerequisite is not null)
                actions.Children.Add(Btn("查看前置详情", () => ShowModDetail(prerequisite.Folder), "Ghost"));
            detail.Children.Add(actions);

            var card = V(relation, detail);
            root.Children.Add(Card(card, padding: 12));
            Space(root, 5);
        }

        root.Children.Add(Card(V(
            T("状态说明", 14, ink, true),
            T("已满足：前置已安装、启用，且满足清单声明的版本范围。", 12, muted),
            T("前置未安装 / 前置已停用：对已启用 MOD 属于当前环境异常；对停用 MOD 则是启用前待处理项。", 12, muted),
            T("版本不符 / 版本无法核对 / ID 重复：不能确认依赖条件成立。循环依赖单独标出。", 12, muted)), padding: 14));

        SetPage("模组管理", root, "mod-dependencies", () => ShowDependencyGraph(filter));
    }

    StackPanel DependencyMetric(string value, string label)
    {
        var panel = V(T(value, 21, ink, true), T(label, 11, muted));
        panel.Margin = new Thickness(0, 0, 18, 0);
        panel.MinWidth = 90;
        return panel;
    }
}
