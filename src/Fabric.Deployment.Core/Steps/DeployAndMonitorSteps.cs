using Fabric.Deployment.Core.Api;
using Fabric.Deployment.Core.Engine;
using Fabric.Deployment.Core.Logging;

namespace Fabric.Deployment.Core.Steps;

/// <summary>
/// "Deployment Operation": moves the target workspace to the right capacity if needed, then
/// calls POST /deploymentPipelines/{id}/deploy once per batch. Batches run sequentially
/// (a pipeline only accepts one deployment at a time), each waited on by <see cref="OperationMonitor"/>.
/// </summary>
public sealed class DeployStep : IDeploymentStep
{
    public string Name => "Deployment Operation";

    public async Task ExecuteAsync(DeploymentContext ctx)
    {
        var plan = ctx.Plan!;
        var s = ctx.Settings;
        var ct = ctx.CancellationToken;
        var monitor = new OperationMonitor(ctx);

        // Capacity migration of the target workspace
        if (plan.CapacityMove is { } move)
        {
            ctx.Log.Info($"Assigning workspace '{move.WorkspaceName}' to capacity {move.ToCapacityId}");
            await ctx.Fabric.Workspaces.AssignToCapacityAsync(move.WorkspaceId, move.ToCapacityId, ct);
            await WaitForCapacityAsync(ctx, move.WorkspaceId, move.ToCapacityId);
            ctx.Log.Success("Capacity assignment complete");
        }

        var note = BuildNote(ctx);
        var batches = plan.Items.Chunk(plan.BatchSize).ToList();
        try
        {
            for (var b = 0; b < batches.Count; b++)
            {
                var batch = batches[b];
                var request = new DeployRequest
                {
                    SourceStageId = plan.SourceStageId,
                    TargetStageId = plan.TargetStageId,
                    Items = batch.Select(i => new ItemDeploymentRequest { SourceItemId = i.SourceItemId, ItemType = i.ItemType }).ToList(),
                    Note = note,
                    // Only needed for the very first deployment into an empty stage.
                    CreatedWorkspaceDetails = b == 0 ? plan.CreateTargetWorkspace : null,
                    Options = s.Deployment.AllowCrossRegionDeployment ? new DeploymentOptionsDto { AllowCrossRegionDeployment = true } : null
                };

                var record = new OperationRecord { Batch = b + 1, ItemCount = batch.Length, SubmittedAt = DateTimeOffset.UtcNow };
                ctx.Operations.Add(record);
                ctx.Log.Info($"Batch {b + 1}/{batches.Count}: deploying {batch.Length} item(s)");

                var handle = await ctx.Fabric.Pipelines.DeployAsync(plan.PipelineId, request, ct);
                record.OperationId = handle.OperationId;
                record.DeploymentId = handle.DeploymentId;
                record.Status = OperationStatus.Running;
                ctx.Log.Info($"  operation {handle.OperationId ?? "(synchronous)"} deployment {handle.DeploymentId}");

                await monitor.WaitAsync(record, handle);
                if (record.Status != OperationStatus.Succeeded)
                    throw new InvalidOperationException($"Batch {b + 1} {record.Status}: {record.Error}");
            }
        }
        finally
        {
            // Complete the rollback point with the items this run created (needed to undo them later).
            if (ctx.Snapshot is not null && ctx.SnapshotPath is not null)
            {
                try
                {
                    await new Rollback.RollbackService(ctx.Fabric, ctx.Log, s.Rollback).RecordCreatedItemsAsync(ctx, ctx.Snapshot);
                    Rollback.SnapshotStore.Save(ctx.Snapshot, Path.GetDirectoryName(ctx.SnapshotPath)!);
                }
                catch (Exception ex) { ctx.Log.Warn($"Could not record created items for rollback: {ex.Message}"); }
            }
        }
    }

    private static string BuildNote(DeploymentContext ctx)
    {
        var build = Environment.GetEnvironmentVariable("BUILD_BUILDNUMBER")
                    ?? Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER");
        var sha = Environment.GetEnvironmentVariable("BUILD_SOURCEVERSION")
                  ?? Environment.GetEnvironmentVariable("GITHUB_SHA");
        var note = $"{ctx.Settings.Deployment.Note} [run {ctx.RunId}; by {ctx.InitiatedBy}" +
                   (build is null ? "" : $"; build {build}") +
                   (sha is null ? "" : $"; commit {sha[..Math.Min(8, sha.Length)]}") + "]";
        return note.Trim().Length > 1024 ? note.Trim()[..1024] : note.Trim();
    }

    private static async Task WaitForCapacityAsync(DeploymentContext ctx, string workspaceId, string capacityId)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var ws = await ctx.Fabric.Workspaces.GetAsync(workspaceId, ctx.CancellationToken);
            if (capacityId.Equals(ws.CapacityId, StringComparison.OrdinalIgnoreCase)) return;
            await Task.Delay(TimeSpan.FromSeconds(5), ctx.CancellationToken);
        }
        throw new TimeoutException($"Workspace {workspaceId} was not assigned to capacity {capacityId} within 10 minutes");
    }
}

/// <summary>"Operation Monitor": polls the long running operation and fetches the extended result.</summary>
public sealed class OperationMonitor
{
    private readonly DeploymentContext _ctx;
    public OperationMonitor(DeploymentContext ctx) => _ctx = ctx;

    public async Task WaitAsync(OperationRecord record, LroHandle handle)
    {
        var ct = _ctx.CancellationToken;
        if (handle.IsCompletedImmediately)
        {
            record.Status = OperationStatus.Succeeded;
            record.CompletedAt = DateTimeOffset.UtcNow;
            return;
        }

        int? lastPct = null;
        var state = await _ctx.Fabric.Api.WaitForOperationAsync(
            handle.OperationId!,
            TimeSpan.FromMinutes(_ctx.Settings.Monitor.TimeoutMinutes),
            Math.Max(_ctx.Settings.Monitor.PollIntervalSeconds, 1),
            s =>
            {
                if (s.PercentComplete != lastPct)
                {
                    _ctx.Log.Info($"  {s.Status} {s.PercentComplete ?? 0}%");
                    lastPct = s.PercentComplete;
                }
            }, ct);

        record.Status = state.Status ?? OperationStatus.Undefined;
        record.CompletedAt = DateTimeOffset.UtcNow;
        record.Error = state.Error is null ? null : $"{state.Error.ErrorCode}: {state.Error.Message}";

        // Extended info (per-item execution plan) — kept for 24h by Fabric.
        try
        {
            record.Result = await _ctx.Fabric.Api.GetOperationResultAsync<DeploymentOperationResult>(handle.OperationId!, ct);
        }
        catch (FabricApiException ex)
        {
            _ctx.Log.Warn($"  could not read operation result: {ex.Message}");
        }

        if (record.Status == OperationStatus.Succeeded) _ctx.Log.Success($"  batch {record.Batch} succeeded");
        else _ctx.Log.Error($"  batch {record.Batch} {record.Status}: {record.Error}");
    }
}

/// <summary>Summarises per-item results collected by the monitor and flags item-level failures.</summary>
public sealed class MonitorStep : IDeploymentStep
{
    public string Name => "Operation Monitor";

    public Task ExecuteAsync(DeploymentContext ctx)
    {
        foreach (var op in ctx.Operations)
        {
            var steps = op.Result?.ExecutionPlan?.Steps ?? new List<ExecutionStep>();
            var failed = steps.Where(s => s.Status == OperationStatus.Failed).ToList();
            var diff = op.Result?.PreDeploymentDiffInformation;
            ctx.Log.Info($"Batch {op.Batch}: {op.Status}, {steps.Count} execution step(s)" +
                         (diff is null ? "" : $" — new {diff.NewItemsCount}, changed {diff.DifferentItemsCount}, unchanged {diff.NoDifferenceItemsCount}") +
                         (op.CompletedAt is { } done ? $", {(done - op.SubmittedAt).TotalSeconds:0}s" : ""));
            foreach (var f in failed)
                ctx.Log.Error($"  {f.SourceAndTarget?.ItemType} {f.SourceAndTarget?.SourceItemDisplayName}: {f.Error?.ErrorCode} {f.Error?.Message}");
            if (failed.Count > 0)
                throw new InvalidOperationException($"{failed.Count} item(s) failed in batch {op.Batch}");
        }
        return Task.CompletedTask;
    }
}
