using Fabric.Deployment.Core.Engine;
using Fabric.Deployment.Core.Logging;

namespace Fabric.Deployment.Core.Steps;

/// <summary>
/// Post-deployment checks against the live tenant:
///  * target stage now has a workspace, on the intended capacity
///  * every planned item is paired to an item in the target stage
///  * each paired item actually exists in the target workspace
/// Extend with your own checks (e.g. trigger a semantic model refresh, run a smoke-test notebook).
/// </summary>
public sealed class ValidationStep : IDeploymentStep
{
    public string Name => "Validation";

    public async Task ExecuteAsync(DeploymentContext ctx)
    {
        var f = ctx.Fabric;
        var ct = ctx.CancellationToken;
        var plan = ctx.Plan!;
        var backward = ctx.TargetStage!.Order < ctx.SourceStage!.Order;

        // Refresh target stage (it may have been created by this deployment)
        var stages = await f.Pipelines.GetStagesAsync(plan.PipelineId, ct);
        var target = stages.First(s => s.Id == plan.TargetStageId);
        Add(ctx, "Target stage has workspace", !string.IsNullOrEmpty(target.WorkspaceId), target.WorkspaceName ?? "none");
        if (string.IsNullOrEmpty(target.WorkspaceId)) { Finish(ctx); return; }

        var ws = await f.Workspaces.GetAsync(target.WorkspaceId!, ct);
        ctx.TargetWorkspace = ws;
        var wantedCap = ctx.TargetStageSettings?.CapacityId;
        if (wantedCap is not null)
            Add(ctx, "Target capacity", wantedCap.Equals(ws.CapacityId, StringComparison.OrdinalIgnoreCase),
                $"workspace on {ws.CapacityId}, expected {wantedCap}");

        // Pairing: source stage items now point at a target item
        var sourceItems = await f.Pipelines.GetStageItemsAsync(plan.PipelineId, plan.SourceStageId, ct);
        var targetItems = (await f.Workspaces.ListItemsAsync(ws.Id, ct)).Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = new List<string>();
        foreach (var planned in plan.Items)
        {
            var now = sourceItems.FirstOrDefault(i => i.ItemId == planned.SourceItemId);
            var paired = backward ? now?.SourceItemId : now?.TargetItemId;
            if (string.IsNullOrEmpty(paired) || !targetItems.Contains(paired))
                missing.Add($"{planned.ItemType}/{planned.DisplayName}");
        }
        Add(ctx, "All items deployed", missing.Count == 0,
            missing.Count == 0 ? $"{plan.Items.Count} item(s) present in '{ws.DisplayName}'"
                               : $"missing in target: {string.Join(", ", missing.Take(20))}{(missing.Count > 20 ? "…" : "")}");

        Finish(ctx);
    }

    private static void Add(DeploymentContext ctx, string name, bool ok, string msg)
    {
        ctx.Validations.Add(new CheckResult(name, ok, msg));
        ctx.Log.Log(ok ? LogLevel.Success : LogLevel.Error, $"{name}: {msg}");
    }

    private static void Finish(DeploymentContext ctx)
    {
        var failed = ctx.Validations.Where(v => !v.Passed).ToList();
        if (failed.Count > 0)
            throw new InvalidOperationException("Validation failed: " + string.Join("; ", failed.Select(v => v.Name)));
    }
}
