using Fabric.Deployment.Core.Engine;
using Fabric.Deployment.Core.Rollback;

namespace Fabric.Deployment.Core.Steps;

/// <summary>"Backup": saves the rollback point before anything in the target changes.</summary>
public sealed class SnapshotStep : IDeploymentStep
{
    public string Name => "Backup (rollback point)";

    public async Task ExecuteAsync(DeploymentContext ctx)
    {
        var cfg = ctx.Settings.Rollback;
        if (!cfg.SnapshotBeforeDeploy) { ctx.Log.Warn("Snapshots disabled (rollback.snapshotBeforeDeploy=false) — no rollback point"); return; }

        var folder = Path.Combine(ctx.RunDirectory, "snapshot");
        var svc = new RollbackService(ctx.Fabric, ctx.Log, cfg);
        ctx.Snapshot = await svc.CaptureAsync(ctx, folder);
        ctx.SnapshotPath = SnapshotStore.Save(ctx.Snapshot, folder);

        var missing = ctx.Snapshot.Items.Where(i => !i.Captured).ToList();
        ctx.Log.Info($"Rollback point: {ctx.Snapshot.CapturedCount}/{ctx.Snapshot.Items.Count} item(s) backed up → {ctx.SnapshotPath}");
        if (missing.Count > 0 && cfg.FailIfSnapshotIncomplete)
            throw new InvalidOperationException($"{missing.Count} item(s) could not be backed up and rollback.failIfSnapshotIncomplete=true: " +
                                                string.Join(", ", missing.Select(m => m.DisplayName)));
    }
}

/// <summary>Automatic rollback when deploy / monitor / validation failed.</summary>
public sealed class RollbackStep : IDeploymentStep
{
    public string Name => "Auto-rollback";
    public bool AlwaysRun => true;

    public async Task ExecuteAsync(DeploymentContext ctx)
    {
        var cfg = ctx.Settings.Rollback;
        var deployStarted = ctx.Operations.Count > 0 || ctx.StepTimings.Any(t => t.Step == "Deployment Operation" && t.Status == "Failed");
        if (!ctx.HasFailed || !deployStarted) return;             // nothing to undo
        if (ctx.Snapshot is null) { ctx.Log.Warn("Deployment failed but there is no rollback point"); return; }
        if (!cfg.AutoRollbackOnFailure)
        {
            ctx.Log.Warn($"Deployment failed. Auto-rollback is off — to roll back run: fabric-deploy rollback --snapshot \"{ctx.SnapshotPath}\"");
            return;
        }

        var svc = new RollbackService(ctx.Fabric, ctx.Log, cfg);
        try { await svc.RecordCreatedItemsAsync(ctx, ctx.Snapshot); } catch (Exception ex) { ctx.Log.Warn($"Could not list created items: {ex.Message}"); }
        SnapshotStore.Save(ctx.Snapshot, Path.GetDirectoryName(ctx.SnapshotPath!)!);

        ctx.Rollback = await svc.RestoreAsync(ctx.Snapshot, Path.GetDirectoryName(ctx.SnapshotPath!)!,
            cfg.DeleteCreatedItems, cfg.RestoreCapacity, dryRun: false, ctx.CancellationToken);

        if (ctx.Rollback.Succeeded)
        {
            ctx.Outcome = RunOutcome.RolledBack;
            ctx.Log.Warn($"Rolled back: {ctx.Rollback.Restored.Count} restored, {ctx.Rollback.Deleted.Count} deleted");
        }
        else
        {
            throw new InvalidOperationException("Rollback incomplete: " + string.Join("; ", ctx.Rollback.Failed));
        }
    }
}
