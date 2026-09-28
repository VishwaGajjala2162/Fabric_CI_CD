using System.Text.Json;
using System.Text.RegularExpressions;
using Fabric.Deployment.Core.Api;
using Fabric.Deployment.Core.Configuration;
using Fabric.Deployment.Core.Engine;
using Fabric.Deployment.Core.Logging;

namespace Fabric.Deployment.Core.Steps;

/// <summary>
/// Builds the deployment plan: which items move (filtered by type/name), whether each is a
/// create or an update in the target stage, whether the target workspace must be created
/// or moved to another capacity, and how items are batched (max 300 per deploy call).
/// Writes plan.json + a deterministic hash used by the approval gate.
/// </summary>
public sealed class PlanStep : IDeploymentStep
{
    public string Name => "Deployment Plan";

    public async Task ExecuteAsync(DeploymentContext ctx)
    {
        var s = ctx.Settings;
        var src = ctx.SourceStage!;
        var tgt = ctx.TargetStage!;
        var backward = tgt.Order < src.Order;

        var stageItems = await ctx.Fabric.Pipelines.GetStageItemsAsync(ctx.Pipeline!.Id, src.Id, ctx.CancellationToken);

        var plan = new DeploymentPlan
        {
            PipelineId = ctx.Pipeline.Id,
            PipelineName = ctx.Pipeline.DisplayName,
            SourceStageId = src.Id, SourceStageName = src.DisplayName,
            TargetStageId = tgt.Id, TargetStageName = tgt.DisplayName,
            SourceWorkspaceName = ctx.SourceWorkspace?.DisplayName,
            TargetWorkspaceName = ctx.TargetWorkspace?.DisplayName ?? ctx.TargetStageSettings?.WorkspaceName,
            BatchSize = Math.Clamp(s.Deployment.MaxItemsPerRequest, 1, 300)
        };

        foreach (var item in stageItems.OrderBy(i => DependencyRank(i.ItemType)).ThenBy(i => i.ItemDisplayName))
        {
            if (!Matches(item, s.Deployment.Items, out var reason))
            {
                plan.SkippedItems.Add($"{item.ItemType}/{item.ItemDisplayName}: {reason}");
                continue;
            }
            // For a forward deployment the paired item in the next stage is targetItemId;
            // for backward it is sourceItemId.
            var paired = backward ? item.SourceItemId : item.TargetItemId;
            plan.Items.Add(new PlannedItem
            {
                SourceItemId = item.ItemId,
                DisplayName = item.ItemDisplayName,
                ItemType = item.ItemType,
                Action = string.IsNullOrEmpty(paired) ? PlannedAction.Create : PlannedAction.Update,
                ExistingTargetItemId = paired,
                LastDeploymentTime = item.LastDeploymentTime
            });
        }

        // Target workspace: create on the configured capacity, or move to it.
        var cfg = ctx.TargetStageSettings;
        if (ctx.TargetWorkspace is null && cfg is not null)
        {
            plan.CreateTargetWorkspace = new CreatedWorkspaceDetails { Name = cfg.WorkspaceName, CapacityId = cfg.CapacityId };
        }
        else if (ctx.TargetWorkspace is not null && cfg?.CapacityId is { } wanted && cfg.EnforceCapacity
                 && !wanted.Equals(ctx.TargetWorkspace.CapacityId, StringComparison.OrdinalIgnoreCase))
        {
            plan.CapacityMove = new CapacityAction
            {
                WorkspaceId = ctx.TargetWorkspace.Id,
                WorkspaceName = ctx.TargetWorkspace.DisplayName,
                FromCapacityId = ctx.TargetWorkspace.CapacityId,
                ToCapacityId = wanted
            };
        }

        plan.Hash = plan.ComputeHash();
        ctx.Plan = plan;

        Directory.CreateDirectory(ctx.RunDirectory);
        var planPath = Path.Combine(ctx.RunDirectory, "plan.json");
        var planJson = JsonSerializer.Serialize(plan, JsonOpts);
        await File.WriteAllTextAsync(planPath, planJson, ctx.CancellationToken);
        // Stable path for CI: publish this file as the artifact reviewers approve.
        await File.WriteAllTextAsync(Path.Combine(s.Output.Directory, "plan.json"), planJson, ctx.CancellationToken);

        Print(ctx, plan);
        ctx.Log.Info($"Plan written to {planPath} (hash {plan.Hash[..12]})");

        if (plan.Items.Count == 0)
        {
            if (s.Deployment.FailIfNothingToDeploy)
                throw new InvalidOperationException("No items matched the deployment filters.");
            ctx.Stop(RunOutcome.NothingToDeploy, "No items matched the filters");
            return;
        }
        if (plan.BatchCount > 1)
            ctx.Log.Warn($"{plan.Items.Count} items → {plan.BatchCount} deploy calls. Items are ordered so data items " +
                         "(lakehouses, warehouses, semantic models) deploy before reports, but review cross-batch dependencies.");
        if (s.Deployment.DryRun)
            ctx.Stop(RunOutcome.DryRun, "Dry run — nothing deployed");
    }

    public static readonly JsonSerializerOptions JsonOpts = new(FabricApiClient.Json)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>Upstream items first so a batch never deploys a report before its model.</summary>
    private static int DependencyRank(string type) => type switch
    {
        "Environment" or "VariableLibrary" => 0,
        "Lakehouse" or "Warehouse" or "KQLDatabase" or "Eventhouse" or "SQLDatabase" or "MirroredDatabase" or "Datamart" => 1,
        "Notebook" or "SparkJobDefinition" or "DataPipeline" or "Dataflow" or "CopyJob" or "Eventstream" => 2,
        "SemanticModel" => 3,
        "Report" or "PaginatedReport" or "KQLQueryset" or "KQLDashboard" => 4,
        "Dashboard" => 5,
        _ => 3
    };

    private static bool Matches(StageItem item, ItemFilter f, out string reason)
    {
        reason = "";
        if (f.IncludeTypes.Count > 0 && !f.IncludeTypes.Contains(item.ItemType, StringComparer.OrdinalIgnoreCase))
        { reason = "type not in includeTypes"; return false; }
        if (f.ExcludeTypes.Contains(item.ItemType, StringComparer.OrdinalIgnoreCase))
        { reason = "type in excludeTypes"; return false; }
        if (f.IncludeNames.Count > 0 && !f.IncludeNames.Any(p => Wildcard(p, item.ItemDisplayName)))
        { reason = "name not in includeNames"; return false; }
        if (f.ExcludeNames.Any(p => Wildcard(p, item.ItemDisplayName)))
        { reason = "name in excludeNames"; return false; }
        return true;
    }

    private static bool Wildcard(string pattern, string value)
        => Regex.IsMatch(value, "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);

    private static void Print(DeploymentContext ctx, DeploymentPlan p)
    {
        ctx.Log.Info($"{p.SourceStageName} [{p.SourceWorkspaceName}] → {p.TargetStageName} [{p.TargetWorkspaceName}]");
        if (p.CreateTargetWorkspace is not null)
            ctx.Log.Info($"  + create workspace '{p.CreateTargetWorkspace.Name}' on capacity {p.CreateTargetWorkspace.CapacityId}");
        if (p.CapacityMove is not null)
            ctx.Log.Info($"  ~ move workspace '{p.CapacityMove.WorkspaceName}' {p.CapacityMove.FromCapacityId ?? "(none)"} → {p.CapacityMove.ToCapacityId}");
        foreach (var i in p.Items)
            ctx.Log.Info($"  {(i.Action == PlannedAction.Create ? "+" : "~")} {i.ItemType,-16} {i.DisplayName}");
        foreach (var sk in p.SkippedItems)
            ctx.Log.Debug($"  - skipped {sk}");
        ctx.Log.Info($"  {p.Items.Count(i => i.Action == PlannedAction.Create)} to create, " +
                     $"{p.Items.Count(i => i.Action == PlannedAction.Update)} to update, {p.SkippedItems.Count} filtered out");
    }
}
