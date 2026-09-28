using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Fabric.Deployment.Core.Api;
using Fabric.Deployment.Core.Configuration;
using Fabric.Deployment.Core.Logging;
using Fabric.Deployment.Core.Services;

namespace Fabric.Deployment.Core.Engine;

public enum RunOutcome { InProgress, Succeeded, PartiallySucceeded, Failed, RolledBack, Rejected, DryRun, NothingToDeploy }

/// <summary>Shared state that flows through every step of a run.</summary>
public sealed class DeploymentContext
{
    public string RunId { get; } = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }

    public DeploymentSettings Settings { get; }
    public FabricServices Fabric { get; }
    public IDeployLogger Log { get; }
    public CancellationToken CancellationToken { get; }
    public string InitiatedBy { get; set; } = Environment.GetEnvironmentVariable("BUILD_REQUESTEDFOR")
                                            ?? Environment.GetEnvironmentVariable("GITHUB_ACTOR")
                                            ?? Environment.UserName;

    // Resolved by Preflight
    public DeploymentPipeline? Pipeline { get; set; }
    public List<DeploymentPipelineStage> Stages { get; set; } = new();
    public DeploymentPipelineStage? SourceStage { get; set; }
    public DeploymentPipelineStage? TargetStage { get; set; }
    public Workspace? SourceWorkspace { get; set; }
    public Workspace? TargetWorkspace { get; set; }
    public Capacity? SourceCapacity { get; set; }
    public Capacity? TargetCapacity { get; set; }
    public StageSettings? TargetStageSettings { get; set; }

    public List<CheckResult> Checks { get; } = new();
    public DeploymentPlan? Plan { get; set; }
    public ApprovalRecord? Approval { get; set; }
    public List<OperationRecord> Operations { get; } = new();
    public List<CheckResult> Validations { get; } = new();
    public List<StepTiming> StepTimings { get; } = new();
    public List<string> Errors { get; } = new();

    /// <summary>Rollback point taken before deployment (see SnapshotStep).</summary>
    public Rollback.SnapshotManifest? Snapshot { get; set; }
    public string? SnapshotPath { get; set; }
    public Rollback.RollbackResult? Rollback { get; set; }
    /// <summary>Set by the orchestrator when any step failed.</summary>
    public bool HasFailed { get; set; }

    public RunOutcome Outcome { get; set; } = RunOutcome.InProgress;
    public bool IsStopped { get; private set; }
    public string? StopReason { get; private set; }

    public DeploymentContext(DeploymentSettings settings, FabricServices fabric, IDeployLogger log, CancellationToken ct)
    {
        Settings = settings; Fabric = fabric; Log = log; CancellationToken = ct;
    }

    /// <summary>End the run early (dry-run, rejection, nothing to do). Always-run steps still execute.</summary>
    public void Stop(RunOutcome outcome, string reason)
    {
        Outcome = outcome; IsStopped = true; StopReason = reason;
    }

    public string RunDirectory => Path.Combine(Settings.Output.Directory, RunId);
}

public sealed record CheckResult(string Name, bool Passed, string Message, bool IsBlocking = true);

public sealed record StepTiming(string Step, string Status, double Seconds, string? Error);

public sealed record ApprovalRecord(bool Approved, string By, DateTimeOffset At, string Method, string? Comment);

public enum PlannedAction { Create, Update }

public sealed class PlannedItem
{
    public string SourceItemId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ItemType { get; set; } = "";
    public PlannedAction Action { get; set; }
    public string? ExistingTargetItemId { get; set; }
    public DateTimeOffset? LastDeploymentTime { get; set; }
}

public sealed class CapacityAction
{
    public string WorkspaceId { get; set; } = "";
    public string WorkspaceName { get; set; } = "";
    public string? FromCapacityId { get; set; }
    public string ToCapacityId { get; set; } = "";
}

/// <summary>What will happen — serialised to plan.json so it can be reviewed/approved in CI.</summary>
public sealed class DeploymentPlan
{
    public string PipelineId { get; set; } = "";
    public string PipelineName { get; set; } = "";
    public string SourceStageId { get; set; } = "";
    public string SourceStageName { get; set; } = "";
    public string TargetStageId { get; set; } = "";
    public string TargetStageName { get; set; } = "";
    public string? SourceWorkspaceName { get; set; }
    public string? TargetWorkspaceName { get; set; }
    public CreatedWorkspaceDetails? CreateTargetWorkspace { get; set; }
    public CapacityAction? CapacityMove { get; set; }
    public List<PlannedItem> Items { get; set; } = new();
    public List<string> SkippedItems { get; set; } = new();
    public int BatchSize { get; set; } = 300;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Hash { get; set; } = "";

    [JsonIgnore] public int BatchCount => Items.Count == 0 ? 0 : (int)Math.Ceiling(Items.Count / (double)BatchSize);

    /// <summary>Deterministic fingerprint (no timestamps) — approvals are bound to it.</summary>
    public string ComputeHash()
    {
        var sb = new StringBuilder();
        sb.Append(PipelineId).Append('|').Append(SourceStageId).Append('|').Append(TargetStageId).Append('|');
        sb.Append(CreateTargetWorkspace?.Name).Append('|').Append(CreateTargetWorkspace?.CapacityId).Append('|');
        sb.Append(CapacityMove?.WorkspaceId).Append('>').Append(CapacityMove?.ToCapacityId).Append('|');
        foreach (var i in Items.OrderBy(i => i.SourceItemId, StringComparer.Ordinal))
            sb.Append(i.SourceItemId).Append(':').Append(i.ItemType).Append(':').Append(i.Action).Append(';');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}

public sealed class OperationRecord
{
    public int Batch { get; set; }
    public int ItemCount { get; set; }
    public string? OperationId { get; set; }
    public string? DeploymentId { get; set; }
    public string Status { get; set; } = OperationStatus.NotStarted;
    public DateTimeOffset SubmittedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
    public DeploymentOperationResult? Result { get; set; }
}
