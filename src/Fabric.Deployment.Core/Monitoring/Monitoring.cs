using System.Text;
using System.Text.Json;
using Fabric.Deployment.Core.Api;
using Fabric.Deployment.Core.Configuration;
using Fabric.Deployment.Core.Services;

namespace Fabric.Deployment.Core.Monitoring;

public enum HealthLevel { Ok, Info, Warning, Error }

public sealed record HealthFinding(string Stage, string Check, HealthLevel Level, string Message);

public sealed class HealthReport
{
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Pipeline { get; set; } = "";
    public List<HealthFinding> Findings { get; } = new();
    public bool IsHealthy => Findings.All(f => f.Level != HealthLevel.Error);

    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## {(IsHealthy ? "✅" : "❌")} Fabric pipeline health — {Pipeline}");
        sb.AppendLine($"_Checked {CheckedAt:u}_");
        sb.AppendLine();
        sb.AppendLine("| Stage | Check | Status | Detail |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var f in Findings)
        {
            var icon = f.Level switch { HealthLevel.Ok => "✅", HealthLevel.Info => "ℹ️", HealthLevel.Warning => "⚠️", _ => "❌" };
            sb.AppendLine($"| {f.Stage} | {f.Check} | {icon} | {f.Message} |");
        }
        return sb.ToString();
    }
}

/// <summary>
/// Scheduled health check of the whole pipeline (run daily from GitHub Actions):
/// capacity assignment and state per stage, last deployment result, stuck deployments,
/// items changed outside the pipeline (drift) and items waiting to be promoted.
/// </summary>
public sealed class HealthService
{
    private readonly FabricServices _f;
    public HealthService(FabricServices fabric) => _f = fabric;

    public async Task<HealthReport> CheckAsync(DeploymentSettings s, CancellationToken ct)
    {
        var report = new HealthReport { Pipeline = s.Pipeline.DisplayName };
        void Add(string stage, string check, HealthLevel lvl, string msg) => report.Findings.Add(new(stage, check, lvl, msg));

        var pipeline = !string.IsNullOrWhiteSpace(s.Pipeline.Id)
            ? new DeploymentPipeline { Id = s.Pipeline.Id!, DisplayName = s.Pipeline.DisplayName }
            : await _f.Pipelines.FindByNameAsync(s.Pipeline.DisplayName, ct);
        if (pipeline is null) { Add("-", "Pipeline", HealthLevel.Error, "Pipeline not found"); return report; }

        var stages = await _f.Pipelines.GetStagesAsync(pipeline.Id, ct);
        var capacities = await _f.Capacities.ListAsync(ct);
        var ops = await _f.Pipelines.ListOperationsAsync(pipeline.Id, ct);

        foreach (var stage in stages)
        {
            var cfg = s.FindStage(stage.DisplayName);
            if (string.IsNullOrEmpty(stage.WorkspaceId))
            {
                Add(stage.DisplayName, "Workspace", stage.Order == 0 ? HealthLevel.Error : HealthLevel.Info, "No workspace assigned yet");
                continue;
            }

            // Capacity
            var ws = await _f.Workspaces.GetAsync(stage.WorkspaceId!, ct);
            var cap = capacities.FirstOrDefault(c => c.Id.Equals(ws.CapacityId ?? "", StringComparison.OrdinalIgnoreCase));
            if (cap is null)
                Add(stage.DisplayName, "Capacity", HealthLevel.Error, $"'{ws.DisplayName}' is not on an accessible capacity");
            else
            {
                var active = string.Equals(cap.State, "Active", StringComparison.OrdinalIgnoreCase);
                Add(stage.DisplayName, "Capacity", active ? HealthLevel.Ok : HealthLevel.Error, $"{cap.DisplayName} ({cap.Sku}, {cap.Region}) is {cap.State}");
                if (cfg?.CapacityId is { } wanted && !wanted.Equals(cap.Id, StringComparison.OrdinalIgnoreCase))
                    Add(stage.DisplayName, "Capacity drift", HealthLevel.Warning, $"On {cap.DisplayName}, settings expect {wanted}");
            }

            // Last deployment into this stage
            var last = ops.Where(o => o.TargetStageId == stage.Id).OrderByDescending(o => o.ExecutionStartTime).FirstOrDefault();
            if (last is not null)
            {
                var lvl = last.Status switch
                {
                    OperationStatus.Succeeded => HealthLevel.Ok,
                    OperationStatus.Failed => HealthLevel.Error,
                    _ => HealthLevel.Info
                };
                var age = last.ExecutionStartTime is { } st ? $"{(DateTimeOffset.UtcNow - st).TotalHours:0}h ago" : "";
                Add(stage.DisplayName, "Last deployment", lvl, $"{last.Status} {age} by {last.PerformedBy?.DisplayName}");
                if (last.Status is OperationStatus.Running or OperationStatus.NotStarted && last.ExecutionStartTime is { } started
                    && DateTimeOffset.UtcNow - started > TimeSpan.FromMinutes(s.Monitor.TimeoutMinutes))
                    Add(stage.DisplayName, "Stuck deployment", HealthLevel.Error, $"Running for {(DateTimeOffset.UtcNow - started).TotalMinutes:0} min");
            }

            // Drift & pending promotion
            var items = await _f.Pipelines.GetStageItemsAsync(pipeline.Id, stage.Id, ct);
            if (stage.Order > 0)
            {
                var unpaired = items.Where(i => string.IsNullOrEmpty(i.SourceItemId)).ToList();
                Add(stage.DisplayName, "Drift", unpaired.Count == 0 ? HealthLevel.Ok : HealthLevel.Warning,
                    unpaired.Count == 0 ? $"{items.Count} item(s), all deployed through the pipeline"
                        : $"{unpaired.Count} item(s) not from the previous stage (created manually?): " +
                          string.Join(", ", unpaired.Take(10).Select(i => i.ItemDisplayName)));
            }
            var next = stages.FirstOrDefault(x => x.Order == stage.Order + 1);
            if (next is not null && !string.IsNullOrEmpty(next.WorkspaceId))
            {
                var pending = items.Where(i => string.IsNullOrEmpty(i.TargetItemId)).ToList();
                if (pending.Count > 0)
                    Add(stage.DisplayName, "Pending promotion", HealthLevel.Info,
                        $"{pending.Count} item(s) not yet in {next.DisplayName}: {string.Join(", ", pending.Take(10).Select(i => i.ItemDisplayName))}");
            }
        }
        return report;
    }
}

/// <summary>Formats a deployment operation (live or historical) for the console.</summary>
public static class OperationPrinter
{
    public static void Print(DeploymentOperationResult op, IReadOnlyDictionary<string, string> stageNames, TextWriter w)
    {
        string Stage(string? id) => id is not null && stageNames.TryGetValue(id, out var n) ? n : id ?? "?";
        w.WriteLine($"Operation {op.Id}: {op.Status}  {Stage(op.SourceStageId)} → {Stage(op.TargetStageId)}");
        w.WriteLine($"  started {op.ExecutionStartTime:u}  ended {op.ExecutionEndTime:u}  by {op.PerformedBy?.DisplayName}");
        if (op.PreDeploymentDiffInformation is { } d)
            w.WriteLine($"  new {d.NewItemsCount}, changed {d.DifferentItemsCount}, unchanged {d.NoDifferenceItemsCount}");
        foreach (var s in op.ExecutionPlan?.Steps ?? new())
            w.WriteLine($"  [{s.Status,-9}] {s.SourceAndTarget?.ItemType,-16} {s.SourceAndTarget?.SourceItemDisplayName} ({s.PreDeploymentDiffState})" +
                        (s.Error is null ? "" : $"  ← {s.Error.ErrorCode}: {s.Error.Message}"));
        if (op.Error is not null) w.WriteLine($"  error: {op.Error.ErrorCode}: {op.Error.Message}");
    }
}

/// <summary>Reads the local JSONL audit history written by AuditStep.</summary>
public static class LocalHistory
{
    public static List<JsonElement> Read(string path, int top)
    {
        if (!File.Exists(path)) return new();
        return File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .Reverse().Take(top).ToList();
    }
}
