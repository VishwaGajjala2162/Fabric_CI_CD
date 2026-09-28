using System.Text.Json.Serialization;

namespace Fabric.Deployment.Core.Api;

// ---------- Generic ----------

/// <summary>Paged list envelope used by most Fabric "List" APIs.</summary>
public sealed class PagedResponse<T>
{
    [JsonPropertyName("value")] public List<T> Value { get; set; } = new();
    [JsonPropertyName("continuationToken")] public string? ContinuationToken { get; set; }
    [JsonPropertyName("continuationUri")] public string? ContinuationUri { get; set; }
}

public sealed class FabricErrorResponse
{
    [JsonPropertyName("errorCode")] public string? ErrorCode { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("requestId")] public string? RequestId { get; set; }
    [JsonPropertyName("moreDetails")] public List<FabricErrorResponse>? MoreDetails { get; set; }
}

// ---------- Long running operations ----------

public static class OperationStatus
{
    public const string NotStarted = "NotStarted";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Undefined = "Undefined";

    public static bool IsTerminal(string? s) => s is Succeeded or Failed or Undefined;
}

public sealed class OperationState
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("createdTimeUtc")] public DateTimeOffset? CreatedTimeUtc { get; set; }
    [JsonPropertyName("lastUpdatedTimeUtc")] public DateTimeOffset? LastUpdatedTimeUtc { get; set; }
    [JsonPropertyName("percentComplete")] public int? PercentComplete { get; set; }
    [JsonPropertyName("error")] public FabricErrorResponse? Error { get; set; }
}

/// <summary>Result of a call that may be a long running operation (HTTP 202).</summary>
public sealed class LroHandle
{
    public string? OperationId { get; init; }
    public string? Location { get; init; }
    public string? DeploymentId { get; init; }
    public int RetryAfterSeconds { get; init; } = 10;
    /// <summary>Set when the API answered synchronously (HTTP 200/201).</summary>
    public string? ImmediateBody { get; init; }
    public bool IsCompletedImmediately => OperationId is null && Location is null;
}

// ---------- Capacities & workspaces ----------

public sealed class Capacity
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("sku")] public string? Sku { get; set; }
    [JsonPropertyName("region")] public string? Region { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
}

public sealed class Workspace
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("capacityId")] public string? CapacityId { get; set; }
}

public sealed class FabricItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("workspaceId")] public string? WorkspaceId { get; set; }
}

// ---------- Deployment pipelines ----------

public sealed class DeploymentPipeline
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
}

public sealed class DeploymentPipelineStage
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("order")] public int Order { get; set; }
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("workspaceId")] public string? WorkspaceId { get; set; }
    [JsonPropertyName("workspaceName")] public string? WorkspaceName { get; set; }
    [JsonPropertyName("isPublic")] public bool? IsPublic { get; set; }
}

public sealed class StageItem
{
    [JsonPropertyName("itemId")] public string ItemId { get; set; } = "";
    [JsonPropertyName("itemDisplayName")] public string ItemDisplayName { get; set; } = "";
    [JsonPropertyName("itemType")] public string ItemType { get; set; } = "";
    [JsonPropertyName("sourceItemId")] public string? SourceItemId { get; set; }
    [JsonPropertyName("targetItemId")] public string? TargetItemId { get; set; }
    [JsonPropertyName("lastDeploymentTime")] public DateTimeOffset? LastDeploymentTime { get; set; }
}

public sealed class CreatePipelineRequest
{
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("stages")] public List<CreatePipelineStage> Stages { get; set; } = new();
}

public sealed class CreatePipelineStage
{
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("isPublic")] public bool IsPublic { get; set; }
}

public sealed class DeployRequest
{
    [JsonPropertyName("sourceStageId")] public string SourceStageId { get; set; } = "";
    [JsonPropertyName("targetStageId")] public string TargetStageId { get; set; } = "";
    [JsonPropertyName("createdWorkspaceDetails")] public CreatedWorkspaceDetails? CreatedWorkspaceDetails { get; set; }
    [JsonPropertyName("items")] public List<ItemDeploymentRequest>? Items { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
    [JsonPropertyName("options")] public DeploymentOptionsDto? Options { get; set; }
}

public sealed class CreatedWorkspaceDetails
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("capacityId")] public string? CapacityId { get; set; }
}

public sealed class ItemDeploymentRequest
{
    [JsonPropertyName("sourceItemId")] public string SourceItemId { get; set; } = "";
    [JsonPropertyName("itemType")] public string ItemType { get; set; } = "";
}

public sealed class DeploymentOptionsDto
{
    [JsonPropertyName("allowCrossRegionDeployment")] public bool AllowCrossRegionDeployment { get; set; }
}

/// <summary>DeploymentPipelineOperationExtendedInfo — returned by GET /operations/{id}/result.</summary>
public sealed class DeploymentOperationResult
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("sourceStageId")] public string? SourceStageId { get; set; }
    [JsonPropertyName("targetStageId")] public string? TargetStageId { get; set; }
    [JsonPropertyName("executionStartTime")] public DateTimeOffset? ExecutionStartTime { get; set; }
    [JsonPropertyName("executionEndTime")] public DateTimeOffset? ExecutionEndTime { get; set; }
    [JsonPropertyName("performedBy")] public Principal? PerformedBy { get; set; }
    [JsonPropertyName("preDeploymentDiffInformation")] public PreDeploymentDiff? PreDeploymentDiffInformation { get; set; }
    [JsonPropertyName("executionPlan")] public ExecutionPlan? ExecutionPlan { get; set; }
    [JsonPropertyName("error")] public FabricErrorResponse? Error { get; set; }
}

public sealed class Principal
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
}

public sealed class PreDeploymentDiff
{
    [JsonPropertyName("newItemsCount")] public int NewItemsCount { get; set; }
    [JsonPropertyName("differentItemsCount")] public int DifferentItemsCount { get; set; }
    [JsonPropertyName("noDifferenceItemsCount")] public int NoDifferenceItemsCount { get; set; }
}

public sealed class ExecutionPlan
{
    [JsonPropertyName("steps")] public List<ExecutionStep> Steps { get; set; } = new();
}

public sealed class ExecutionStep
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("preDeploymentDiffState")] public string? PreDeploymentDiffState { get; set; }
    [JsonPropertyName("sourceAndTarget")] public SourceAndTarget? SourceAndTarget { get; set; }
    [JsonPropertyName("error")] public FabricErrorResponse? Error { get; set; }
}

public sealed class SourceAndTarget
{
    [JsonPropertyName("sourceItemId")] public string? SourceItemId { get; set; }
    [JsonPropertyName("sourceItemDisplayName")] public string? SourceItemDisplayName { get; set; }
    [JsonPropertyName("targetItemId")] public string? TargetItemId { get; set; }
    [JsonPropertyName("targetItemDisplayName")] public string? TargetItemDisplayName { get; set; }
    [JsonPropertyName("itemType")] public string? ItemType { get; set; }
}

/// <summary>Entry from GET /deploymentPipelines/{id}/operations.</summary>
public sealed class DeploymentPipelineOperation
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("sourceStageId")] public string? SourceStageId { get; set; }
    [JsonPropertyName("targetStageId")] public string? TargetStageId { get; set; }
    [JsonPropertyName("executionStartTime")] public DateTimeOffset? ExecutionStartTime { get; set; }
    [JsonPropertyName("executionEndTime")] public DateTimeOffset? ExecutionEndTime { get; set; }
    [JsonPropertyName("performedBy")] public Principal? PerformedBy { get; set; }
    [JsonPropertyName("note")] public OperationNote? Note { get; set; }
}

public sealed class OperationNote
{
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("isTruncated")] public bool IsTruncated { get; set; }
}

// ---------- Git integration (optional DEV sync) ----------

public sealed class GitStatus
{
    [JsonPropertyName("workspaceHead")] public string? WorkspaceHead { get; set; }
    [JsonPropertyName("remoteCommitHash")] public string? RemoteCommitHash { get; set; }
    [JsonPropertyName("changes")] public List<System.Text.Json.JsonElement>? Changes { get; set; }
}

// ---------- Item definitions (used for rollback snapshots) ----------

public sealed class ItemDefinition
{
    [JsonPropertyName("format")] public string? Format { get; set; }
    [JsonPropertyName("parts")] public List<DefinitionPart> Parts { get; set; } = new();
}

public sealed class DefinitionPart
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("payload")] public string Payload { get; set; } = "";
    [JsonPropertyName("payloadType")] public string PayloadType { get; set; } = "InlineBase64";
}

public sealed class ItemDefinitionResponse
{
    [JsonPropertyName("definition")] public ItemDefinition? Definition { get; set; }
}
