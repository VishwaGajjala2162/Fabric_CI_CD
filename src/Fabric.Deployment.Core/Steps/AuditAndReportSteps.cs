using System.Text;
using System.Text.Json;
using Fabric.Deployment.Core.Engine;

namespace Fabric.Deployment.Core.Steps;

/// <summary>Where audit records go. Default is an append-only JSONL file; plug in SQL, a Lakehouse table, Log Analytics…</summary>
public interface IAuditStore
{
    Task AppendAsync(AuditRecord record, CancellationToken ct);
}

public sealed record AuditRecord(
    string RunId, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Outcome, string InitiatedBy,
    string? PipelineId, string? PipelineName, string? SourceStage, string? TargetStage,
    string? SourceWorkspace, string? TargetWorkspace, string? TargetCapacityId,
    int ItemCount, string? PlanHash, string? ApprovedBy, string? ApprovalMethod,
    List<string> OperationIds, List<string> DeploymentIds, List<string> Items, List<string> Errors,
    string? BuildId, string? CommitSha);

public sealed class JsonlAuditStore : IAuditStore
{
    private readonly string _path;
    public JsonlAuditStore(string path) => _path = path;

    public async Task AppendAsync(AuditRecord record, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var line = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = false }) + Environment.NewLine;
        await File.AppendAllTextAsync(_path, line, ct);
    }
}

/// <summary>"Audit / Deployment History": records every run — including failed, rejected and dry runs.</summary>
public sealed class AuditStep : IDeploymentStep
{
    private readonly IAuditStore? _store;
    public AuditStep(IAuditStore? store = null) => _store = store;
    public string Name => "Audit / Deployment History";
    public bool AlwaysRun => true;

    public async Task ExecuteAsync(DeploymentContext ctx)
    {
        var store = _store ?? new JsonlAuditStore(Path.Combine(ctx.Settings.Output.Directory, ctx.Settings.Output.AuditFile));
        var p = ctx.Plan;
        var record = new AuditRecord(
            ctx.RunId, ctx.StartedAt, ctx.FinishedAt, ctx.Outcome.ToString(), ctx.InitiatedBy,
            ctx.Pipeline?.Id, ctx.Pipeline?.DisplayName, ctx.SourceStage?.DisplayName, ctx.TargetStage?.DisplayName,
            ctx.SourceWorkspace?.DisplayName, ctx.TargetWorkspace?.DisplayName ?? p?.TargetWorkspaceName,
            ctx.TargetStageSettings?.CapacityId ?? ctx.TargetWorkspace?.CapacityId,
            p?.Items.Count ?? 0, p?.Hash, ctx.Approval?.By, ctx.Approval?.Method,
            ctx.Operations.Select(o => o.OperationId ?? "").Where(x => x != "").ToList(),
            ctx.Operations.Select(o => o.DeploymentId ?? "").Where(x => x != "").ToList(),
            p?.Items.Select(i => $"{i.Action}:{i.ItemType}:{i.DisplayName}").ToList() ?? new(),
            ctx.Errors.ToList(),
            Environment.GetEnvironmentVariable("BUILD_BUILDID") ?? Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            Environment.GetEnvironmentVariable("BUILD_SOURCEVERSION") ?? Environment.GetEnvironmentVariable("GITHUB_SHA"));
        await store.AppendAsync(record, ctx.CancellationToken);
        ctx.Log.Info($"Audit record appended ({ctx.Outcome})");
    }
}

/// <summary>"Deployment Report": report.json + report.md in the run folder (also added to the CI job summary).</summary>
public sealed class ReportStep : IDeploymentStep
{
    public string Name => "Deployment Report";
    public bool AlwaysRun => true;

    public async Task ExecuteAsync(DeploymentContext ctx)
    {
        Directory.CreateDirectory(ctx.RunDirectory);
        var ct = ctx.CancellationToken;

        var json = new
        {
            ctx.RunId, Outcome = ctx.Outcome.ToString(), ctx.StopReason, ctx.StartedAt, ctx.FinishedAt, ctx.InitiatedBy,
            Pipeline = ctx.Pipeline?.DisplayName, Source = ctx.SourceStage?.DisplayName, Target = ctx.TargetStage?.DisplayName,
            ctx.Checks, ctx.Plan, ctx.Approval, ctx.Operations, ctx.Validations, ctx.StepTimings, ctx.Errors
        };
        await File.WriteAllTextAsync(Path.Combine(ctx.RunDirectory, "report.json"),
            JsonSerializer.Serialize(json, PlanStep.JsonOpts), ct);

        var md = BuildMarkdown(ctx);
        var mdPath = Path.Combine(ctx.RunDirectory, "report.md");
        await File.WriteAllTextAsync(mdPath, md, ct);

        // Surface in CI UIs
        var ghSummary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrEmpty(ghSummary)) await File.AppendAllTextAsync(ghSummary, md, ct);
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TF_BUILD")))
            Console.WriteLine($"##vso[task.uploadsummary]{Path.GetFullPath(mdPath)}");

        ctx.Log.Info($"Report: {Path.GetFullPath(mdPath)}");
    }

    private static string BuildMarkdown(DeploymentContext ctx)
    {
        var sb = new StringBuilder();
        var p = ctx.Plan;
        var icon = ctx.Outcome switch
        {
            RunOutcome.Succeeded => "✅", RunOutcome.DryRun or RunOutcome.NothingToDeploy => "ℹ️",
            RunOutcome.PartiallySucceeded => "⚠️", RunOutcome.RolledBack => "↩️", _ => "❌"
        };
        sb.AppendLine($"## {icon} Fabric deployment {ctx.Outcome}");
        sb.AppendLine();
        sb.AppendLine("| | |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Run | `{ctx.RunId}` |");
        sb.AppendLine($"| Pipeline | {ctx.Pipeline?.DisplayName} |");
        sb.AppendLine($"| Route | {ctx.SourceStage?.DisplayName} ({ctx.SourceWorkspace?.DisplayName}) → {ctx.TargetStage?.DisplayName} ({ctx.TargetWorkspace?.DisplayName ?? p?.TargetWorkspaceName}) |");
        sb.AppendLine($"| Target capacity | {ctx.TargetCapacity?.DisplayName ?? ctx.TargetStageSettings?.CapacityId} {ctx.TargetCapacity?.Region} |");
        sb.AppendLine($"| Initiated by | {ctx.InitiatedBy} |");
        if (ctx.Approval is not null) sb.AppendLine($"| Approval | {(ctx.Approval.Approved ? "approved" : "rejected")} by {ctx.Approval.By} ({ctx.Approval.Method}) |");
        if (p is not null) sb.AppendLine($"| Plan hash | `{p.Hash[..12]}` |");
        if (ctx.FinishedAt is { } fin) sb.AppendLine($"| Duration | {(fin - ctx.StartedAt).TotalSeconds:0}s |");
        if (ctx.StopReason is not null) sb.AppendLine($"| Note | {ctx.StopReason} |");
        sb.AppendLine();

        if (ctx.Checks.Count > 0)
        {
            sb.AppendLine("### Preflight");
            foreach (var c in ctx.Checks) sb.AppendLine($"- {(c.Passed ? "✅" : c.IsBlocking ? "❌" : "⚠️")} **{c.Name}** — {c.Message}");
            sb.AppendLine();
        }
        if (p is not null)
        {
            sb.AppendLine($"### Items ({p.Items.Count})");
            if (p.CreateTargetWorkspace is not null) sb.AppendLine($"_Creates workspace **{p.CreateTargetWorkspace.Name}** on capacity `{p.CreateTargetWorkspace.CapacityId}`._\n");
            if (p.CapacityMove is not null) sb.AppendLine($"_Moves **{p.CapacityMove.WorkspaceName}** to capacity `{p.CapacityMove.ToCapacityId}`._\n");
            var results = ctx.Operations.SelectMany(o => o.Result?.ExecutionPlan?.Steps ?? new())
                .Where(s => s.SourceAndTarget?.SourceItemId is not null)
                .GroupBy(s => s.SourceAndTarget!.SourceItemId!).ToDictionary(g => g.Key, g => g.Last());
            sb.AppendLine("| Action | Type | Item | Result |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var i in p.Items)
            {
                var r = results.TryGetValue(i.SourceItemId, out var st) ? st.Status : (ctx.Operations.Count == 0 ? "not run" : "—");
                sb.AppendLine($"| {i.Action} | {i.ItemType} | {i.DisplayName} | {r} |");
            }
            sb.AppendLine();
        }
        if (ctx.Snapshot is { } snap)
        {
            sb.AppendLine("### Rollback point");
            sb.AppendLine($"- {snap.CapturedCount}/{snap.Items.Count} overwritten item(s) backed up, {snap.CreatedItems.Count} newly created item(s) tracked");
            foreach (var miss in snap.Items.Where(i => !i.Captured)) sb.AppendLine($"- ⚠️ not backed up: {miss.ItemType}/{miss.DisplayName} ({miss.Error})");
            sb.AppendLine($"- Roll back with: `fabric-deploy rollback --snapshot {Path.GetRelativePath(ctx.Settings.Output.Directory, ctx.SnapshotPath ?? "")}`");
            sb.AppendLine();
        }
        if (ctx.Rollback is { } rb)
        {
            sb.AppendLine("### ↩️ Automatic rollback");
            foreach (var x in rb.Restored) sb.AppendLine($"- ✅ restored {x}");
            foreach (var x in rb.Deleted) sb.AppendLine($"- 🗑️ deleted {x}");
            foreach (var x in rb.NotRestorable) sb.AppendLine($"- ⚠️ not restorable {x}");
            foreach (var x in rb.Failed) sb.AppendLine($"- ❌ {x}");
            if (rb.CapacityRestoredTo is not null) sb.AppendLine($"- capacity restored to `{rb.CapacityRestoredTo}`");
            sb.AppendLine();
        }
        if (ctx.Validations.Count > 0)
        {
            sb.AppendLine("### Validation");
            foreach (var v in ctx.Validations) sb.AppendLine($"- {(v.Passed ? "✅" : "❌")} **{v.Name}** — {v.Message}");
            sb.AppendLine();
        }
        sb.AppendLine("### Steps");
        foreach (var t in ctx.StepTimings) sb.AppendLine($"- {t.Step}: {t.Status} ({t.Seconds:0.0}s){(t.Error is null ? "" : $" — {t.Error}")}");
        if (ctx.Errors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Errors");
            foreach (var e in ctx.Errors) sb.AppendLine($"- {e}");
        }
        return sb.ToString();
    }
}
