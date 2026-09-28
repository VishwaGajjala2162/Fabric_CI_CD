using Fabric.Deployment.Core.Api;
using Fabric.Deployment.Core.Engine;
using Fabric.Deployment.Core.Logging;

namespace Fabric.Deployment.Core.Steps;

/// <summary>
/// Resolves pipeline/stages/workspaces/capacities and fails fast on anything that would
/// make the deployment fail half-way: missing stages, wrong direction, inactive or
/// cross-region capacities, or a deployment already running on the pipeline.
/// </summary>
public sealed class PreflightStep : IDeploymentStep
{
    public string Name => "Preflight";

    public async Task ExecuteAsync(DeploymentContext ctx)
    {
        var s = ctx.Settings;
        var f = ctx.Fabric;
        var ct = ctx.CancellationToken;

        // 1. Pipeline
        ctx.Pipeline = !string.IsNullOrWhiteSpace(s.Pipeline.Id)
            ? new DeploymentPipeline { Id = s.Pipeline.Id!, DisplayName = s.Pipeline.DisplayName }
            : await f.Pipelines.FindByNameAsync(s.Pipeline.DisplayName, ct);
        Check(ctx, "Pipeline exists", ctx.Pipeline is not null,
            ctx.Pipeline is null ? $"Deployment pipeline '{s.Pipeline.DisplayName}' not found (run 'bootstrap' first?)"
                                 : $"{ctx.Pipeline.DisplayName} ({ctx.Pipeline.Id})");
        ThrowIfBlocked(ctx);

        // 2. Stages
        ctx.Stages = await f.Pipelines.GetStagesAsync(ctx.Pipeline!.Id, ct);
        ctx.SourceStage = FindStage(ctx, s.Deployment.SourceStage);
        ctx.TargetStage = FindStage(ctx, s.Deployment.TargetStage);
        Check(ctx, "Source stage", ctx.SourceStage is not null, ctx.SourceStage?.DisplayName ?? $"'{s.Deployment.SourceStage}' not in pipeline");
        Check(ctx, "Target stage", ctx.TargetStage is not null, ctx.TargetStage?.DisplayName ?? $"'{s.Deployment.TargetStage}' not in pipeline");
        ThrowIfBlocked(ctx);

        var src = ctx.SourceStage!;
        var tgt = ctx.TargetStage!;
        Check(ctx, "Stages differ", src.Id != tgt.Id, src.Id != tgt.Id ? $"{src.DisplayName} (order {src.Order}) → {tgt.DisplayName} (order {tgt.Order})" : "Source and target stage must be different");
        Check(ctx, "Source has workspace", !string.IsNullOrEmpty(src.WorkspaceId),
            src.WorkspaceName ?? "Source stage has no workspace assigned");
        ThrowIfBlocked(ctx);

        var backward = tgt.Order < src.Order;
        if (backward)
            Check(ctx, "Backward deployment", string.IsNullOrEmpty(tgt.WorkspaceId),
                "Backward deployment is only supported into an empty stage");
        if (Math.Abs(tgt.Order - src.Order) > 1)
            Check(ctx, "Adjacent stages", true, $"Deploying across {Math.Abs(tgt.Order - src.Order)} stages — make sure that is intended", false);

        ctx.TargetStageSettings = s.FindStage(tgt.DisplayName);

        // 3. Workspaces
        ctx.SourceWorkspace = await f.Workspaces.GetAsync(src.WorkspaceId!, ct);
        if (!string.IsNullOrEmpty(tgt.WorkspaceId))
        {
            ctx.TargetWorkspace = await f.Workspaces.GetAsync(tgt.WorkspaceId!, ct);
        }
        else
        {
            var cfg = ctx.TargetStageSettings;
            Check(ctx, "Target workspace", cfg is not null && !string.IsNullOrWhiteSpace(cfg.WorkspaceName),
                cfg is null
                    ? $"Target stage '{tgt.DisplayName}' is empty and has no entry in settings.pipeline.stages to create a workspace from"
                    : $"Target stage is empty — deployment will create workspace '{cfg.WorkspaceName}'");
        }

        // 4. Capacities (region + state)
        var capacities = await f.Capacities.ListAsync(ct);
        Capacity? Cap(string? id) => id is null ? null : capacities.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

        ctx.SourceCapacity = Cap(ctx.SourceWorkspace.CapacityId);
        var desiredTargetCapId = ctx.TargetStageSettings?.CapacityId ?? ctx.TargetWorkspace?.CapacityId;
        ctx.TargetCapacity = Cap(desiredTargetCapId);

        Check(ctx, "Source on capacity", ctx.SourceWorkspace.CapacityId is not null,
            ctx.SourceCapacity is null ? "Source workspace is not on a Fabric/Premium capacity"
                                       : $"{ctx.SourceCapacity.DisplayName} ({ctx.SourceCapacity.Sku}, {ctx.SourceCapacity.Region})");
        if (desiredTargetCapId is null)
            Check(ctx, "Target capacity", false, "No capacity for target: set pipeline.stages[].capacityId");
        else if (ctx.TargetCapacity is null)
            Check(ctx, "Target capacity", false, $"Capacity {desiredTargetCapId} not found or caller has no access to it");
        else
        {
            Check(ctx, "Target capacity active", string.Equals(ctx.TargetCapacity.State, "Active", StringComparison.OrdinalIgnoreCase),
                $"{ctx.TargetCapacity.DisplayName} is {ctx.TargetCapacity.State}");
            var crossRegion = ctx.SourceCapacity?.Region is { } r1 && ctx.TargetCapacity.Region is { } r2
                              && !r1.Equals(r2, StringComparison.OrdinalIgnoreCase);
            if (crossRegion)
                Check(ctx, "Capacity region", s.Deployment.AllowCrossRegionDeployment,
                    $"Source is in {ctx.SourceCapacity!.Region}, target in {ctx.TargetCapacity.Region}. " +
                    (s.Deployment.AllowCrossRegionDeployment ? "Cross-region deployment allowed." : "Set deployment.allowCrossRegionDeployment=true to allow."));
        }

        // 5. No other deployment in flight (pipelines run one deployment at a time)
        await CheckRunningDeploymentsAsync(ctx);

        foreach (var c in ctx.Checks.Where(c => c.Passed || !c.IsBlocking)) // blocking failures are logged by ThrowIfBlocked
            ctx.Log.Log(c.Passed && c.IsBlocking ? LogLevel.Success : LogLevel.Warning, $"{c.Name}: {c.Message}");
        ThrowIfBlocked(ctx);
    }

    private static async Task CheckRunningDeploymentsAsync(DeploymentContext ctx)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(ctx.Settings.Monitor.TimeoutMinutes);
        while (true)
        {
            var ops = await ctx.Fabric.Pipelines.ListOperationsAsync(ctx.Pipeline!.Id, ctx.CancellationToken);
            var running = ops.FirstOrDefault(o => o.Status is OperationStatus.Running or OperationStatus.NotStarted);
            if (running is null) { Check(ctx, "No deployment in progress", true, "Pipeline is idle"); return; }

            if (!ctx.Settings.Deployment.WaitForRunningDeployments || DateTimeOffset.UtcNow > deadline)
            {
                Check(ctx, "No deployment in progress", false, $"Operation {running.Id} is {running.Status} on this pipeline");
                return;
            }
            ctx.Log.Warn($"Deployment {running.Id} is still {running.Status}; waiting…");
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, ctx.Settings.Monitor.PollIntervalSeconds)), ctx.CancellationToken);
        }
    }

    private static DeploymentPipelineStage? FindStage(DeploymentContext ctx, string nameOrOrder)
        => ctx.Stages.FirstOrDefault(s => s.DisplayName.Equals(nameOrOrder, StringComparison.OrdinalIgnoreCase)
                                          || s.Id.Equals(nameOrOrder, StringComparison.OrdinalIgnoreCase)
                                          || s.Order.ToString() == nameOrOrder);

    private static void Check(DeploymentContext ctx, string name, bool ok, string message, bool blocking = true)
    {
        ctx.Checks.RemoveAll(c => c.Name == name);
        ctx.Checks.Add(new CheckResult(name, ok, message, blocking));
    }

    private static void ThrowIfBlocked(DeploymentContext ctx)
    {
        var blocked = ctx.Checks.Where(c => !c.Passed && c.IsBlocking).ToList();
        if (blocked.Count == 0) return;
        foreach (var b in blocked) ctx.Log.Error($"{b.Name}: {b.Message}");
        throw new InvalidOperationException("blocking checks failed: " + string.Join("; ", blocked.Select(b => b.Name)));
    }
}
