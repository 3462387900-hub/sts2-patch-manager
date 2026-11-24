namespace SpirePatch;

internal sealed partial class NeonWorkbenchView
{
    // Do not display a previously verified health result while an asynchronous projection
    // is being built. On root switch no prior-root data can remain actionable.
    internal void WithdrawObsoleteNeonHealth(string? root, bool dirty, bool healthStale, string status, bool scanning)
    {
        var previous = currentSnapshot;
        if (previous == null) return;
        if (!string.Equals(previous.GameRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            ApplySnapshot(previous with
            {
                GameRoot = root, GameRunning = false, HasInventory = false,
                ModInventoryDirty = true, LifecycleStale = true, LifecycleScanning = false,
                LifecycleStatus = status, IsPatchScanValid = false, IsLifecycleHealthValid = false,
                IsScanning = scanning, ScanStageText = null, LastScanTime = null,
                GateMode = root == null ? NeonGateMode.NoGameRoot : NeonGateMode.Ready,
                AllRows = [], AllCatalogPatches = [], Faults = [], DependencyGraph = null,
                HardBlockCount = 0, EnabledModsCount = 0, PendingPatchesCount = 0,
                VerifiedInstalledPatchesCount = 0, RetirablePatchesCount = 0, AvailableUpdatesCount = 0,
                FilterCounts = previous.FilterCounts.ToDictionary(kv => kv.Key, _ => 0),
                Recovery = new([], null, null, [], []),
                Coop = new(false, null, null, null, false, null, [], null, "")
            }, preserveViewport: false);
            return;
        }
        if (!previous.HasInventory && previous.IsScanning != scanning)
        {
            ApplySnapshot(previous with { IsScanning = scanning,
                ScanStageText = scanning ? "正在读取本地 MOD 清单与依赖关系…" : null }, preserveViewport: true);
            previous = currentSnapshot!;
        }
        if ((!dirty || previous.ModInventoryDirty) &&
            (!healthStale || !previous.IsLifecycleHealthValid) &&
            (!dirty || !previous.IsPatchScanValid)) return;

        NeonRelatedPatch Stale(NeonRelatedPatch p) => p with
        {
            IsVerifiedHealthy = false, CanRetire = false, LifecycleItem = null, ScanState = null,
            StatusLabel = p.InstalledMod is { Enabled: false } ? "已停用" : "需重新检查",
            StatusTone = NeonTone.Amber, VerdictText = status, RetireBlockReason = status
        };
        var patches = previous.AllCatalogPatches.Select(Stale).ToArray();
        var faults = previous.Faults.Where(x => x.LeadPatch == null).ToArray();
        ApplySnapshot(previous with
        {
            ModInventoryDirty = dirty || previous.ModInventoryDirty,
            LifecycleStale = true, LifecycleStatus = status,
            IsPatchScanValid = !dirty && previous.IsPatchScanValid, IsLifecycleHealthValid = false,
            VerifiedInstalledPatchesCount = 0, RetirablePatchesCount = 0, PendingPatchesCount = 0,
            AllCatalogPatches = patches, Faults = faults,
            GateMode = previous.HardBlockCount > 0 ? NeonGateMode.Blocked : faults.Length > 0 ? NeonGateMode.Advisory : NeonGateMode.Ready,
            AllRows = previous.AllRows.Select(row => row with
            {
                RelatedPatches = row.RelatedPatches.Select(Stale).ToArray(),
                ToggleBlockReason = dirty ? "MOD 文件发生变化，请刷新后再操作。" : row.ToggleBlockReason
            }).ToArray()
        }, preserveViewport: true);
    }
}
