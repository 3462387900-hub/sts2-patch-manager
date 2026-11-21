using System.Text.Json;

namespace SpirePatch;

internal sealed record CoopAlignmentWorkflowContext(
    string? GameRoot,
    bool Demo,
    ScanReport? DemoReport,
    IReadOnlyList<PatchState> DemoStates,
    IReadOnlyList<OnlinePatch>? Definitions = null,
    string? Revision = null);

internal sealed record CoopWorkflowFreshReport(
    ScanReport? Report,
    ModInventory? Inventory,
    JsonElement Snapshot);

internal sealed record CoopWorkflowComparison(
    JsonElement Peer,
    CoopWorkflowFreshReport Fresh,
    string Summary,
    IReadOnlyList<CoopDifference>? Rows);

internal sealed record CoopWorkflowPreparedPlan(CoopPlan Plan, PreparedPackage? Package);

internal sealed record CoopWorkflowApplyResult(
    CoopOutcome Outcome,
    CoopWorkflowComparison? Comparison,
    string? RefreshError);

/// <summary>
/// Adapter boundary around the existing Domain algorithms. Production uses the
/// current CoopAlignment / CoopPlanner / CoopReports implementation; tests can
/// substitute a local deterministic backend without a game or network connection.
/// </summary>
internal interface ICoopAlignmentWorkflowBackend
{
    (ScanReport Report, ModInventory Inventory, JsonElement Snapshot) Scan(
        string root, IReadOnlyList<OnlinePatch>? definitions, string? revision);
    JsonElement CreateDemo(ScanReport? report, IReadOnlyList<PatchState> states);
    void WriteReport(string path, JsonElement snapshot);
    JsonElement ReadReport(string path);
    string Compare(JsonElement local, JsonElement peer);
    List<CoopDifference> Analyze(JsonElement local, JsonElement peer, ScanReport report, ModInventory inventory);
    Task<CoopPlan> PrepareAsync(
        BundleInstaller installer, PreparedPackage? package, string root,
        JsonElement peer, IEnumerable<string> selected);
    Task<CoopOutcome> ApplyAsync(BundleInstaller installer, PreparedPackage? package, CoopPlan plan);
}

internal sealed class DomainCoopAlignmentWorkflowBackend : ICoopAlignmentWorkflowBackend
{
    public (ScanReport Report, ModInventory Inventory, JsonElement Snapshot) Scan(
        string root, IReadOnlyList<OnlinePatch>? definitions, string? revision) =>
        CoopAlignment.Scan(root, definitions, revision);

    public JsonElement CreateDemo(ScanReport? report, IReadOnlyList<PatchState> states) =>
        CoopReports.Create(report, states, demo: true);

    public void WriteReport(string path, JsonElement snapshot) => CoopReports.Write(path, snapshot);
    public JsonElement ReadReport(string path) => CoopReports.Read(path);
    public string Compare(JsonElement local, JsonElement peer) => CoopReports.Compare(local, peer);
    public List<CoopDifference> Analyze(JsonElement local, JsonElement peer, ScanReport report, ModInventory inventory) =>
        CoopPlanner.Analyze(local, peer, report, inventory);

    public Task<CoopPlan> PrepareAsync(
        BundleInstaller installer, PreparedPackage? package, string root,
        JsonElement peer, IEnumerable<string> selected) =>
        CoopAlignment.PrepareAsync(installer, package, root, peer, selected);

    public Task<CoopOutcome> ApplyAsync(BundleInstaller installer, PreparedPackage? package, CoopPlan plan) =>
        CoopAlignment.ApplyAsync(installer, package, plan);
}

/// <summary>
/// Coordinates report refresh, comparison, preflight, confirmation and post-action
/// reconciliation. It contains no WPF controls, dispatcher or message boxes.
/// </summary>
internal sealed class CoopAlignmentWorkflowService
{
    readonly BundleInstaller installer;
    readonly ICoopAlignmentWorkflowBackend backend;

    public CoopAlignmentWorkflowService(
        BundleInstaller installer,
        ICoopAlignmentWorkflowBackend? backend = null)
    {
        this.installer = installer;
        this.backend = backend ?? new DomainCoopAlignmentWorkflowBackend();
    }

    public async Task<CoopWorkflowFreshReport> CreateFreshReportAsync(
        CoopAlignmentWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Demo)
        {
            var demo = backend.CreateDemo(context.DemoReport, context.DemoStates);
            return new(null, null, demo);
        }

        if (string.IsNullOrWhiteSpace(context.GameRoot))
            throw new InvalidOperationException("请先选择游戏目录。");

        cancellationToken.ThrowIfCancellationRequested();
        var scan = await Task.Run(
            () => backend.Scan(context.GameRoot, context.Definitions, context.Revision),
            cancellationToken);
        return new(scan.Report, scan.Inventory, scan.Snapshot);
    }

    public async Task ExportFreshReportAsync(
        string path,
        CoopAlignmentWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var fresh = await CreateFreshReportAsync(context, cancellationToken);
        await Task.Run(() => backend.WriteReport(path, fresh.Snapshot), cancellationToken);
    }

    public Task<JsonElement> ReadPeerReportAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => backend.ReadReport(path), cancellationToken);

    public async Task<CoopWorkflowComparison> CompareAsync(
        JsonElement peer,
        CoopAlignmentWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var fresh = await CreateFreshReportAsync(context, cancellationToken);
        string summary = await Task.Run(
            () => backend.Compare(fresh.Snapshot, peer),
            cancellationToken);

        IReadOnlyList<CoopDifference>? rows = null;
        if (!context.Demo && fresh.Report is not null && fresh.Inventory is not null)
        {
            rows = await Task.Run(
                () => backend.Analyze(fresh.Snapshot, peer, fresh.Report, fresh.Inventory),
                cancellationToken);
        }

        return new(peer.Clone(), fresh, summary, rows);
    }

    public async Task<CoopWorkflowComparison> CompareReportFileAsync(
        string path,
        CoopAlignmentWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var peer = await ReadPeerReportAsync(path, cancellationToken);
        return await CompareAsync(peer, context, cancellationToken);
    }

    public async Task<CoopWorkflowPreparedPlan> PrepareAsync(
        string root,
        JsonElement peer,
        IEnumerable<string> selected,
        IReadOnlyList<CoopDifference> currentRows,
        Func<CancellationToken, Action<string>?, Task<PreparedPackage>>? preparePackage,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        string[] chosen = selected
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (chosen.Length == 0)
            throw new InvalidOperationException("请至少勾选一项可处理差异。");

        bool needsPackage = currentRows.Any(row =>
            chosen.Contains(row.Id, StringComparer.OrdinalIgnoreCase) &&
            row.Action == CoopAction.Install);

        PreparedPackage? package = null;
        if (needsPackage)
        {
            if (preparePackage is null)
                throw new InvalidOperationException("需要先准备本版已验证的修复包。");
            package = await preparePackage(cancellationToken, progress);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var plan = await backend.PrepareAsync(installer, package, root, peer, chosen);
        cancellationToken.ThrowIfCancellationRequested();
        return new(plan, package);
    }

    public async Task<CoopWorkflowApplyResult> ApplyAsync(
        CoopWorkflowPreparedPlan prepared,
        CoopAlignmentWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Domain Apply owns the transaction and stops the remaining steps on failure.
        // Regardless of its outcome, the workflow performs a fresh local reconciliation.
        var outcome = await backend.ApplyAsync(installer, prepared.Package, prepared.Plan);

        try
        {
            var postActionContext = context with
            {
                GameRoot = prepared.Plan.Root,
                Demo = false
            };
            var comparison = await CompareAsync(prepared.Plan.Peer, postActionContext);
            return new(outcome, comparison, null);
        }
        catch (Exception error)
        {
            return new(outcome, null, error.Message);
        }
    }
}
