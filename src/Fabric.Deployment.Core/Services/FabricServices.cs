using Fabric.Deployment.Core.Api;

namespace Fabric.Deployment.Core.Services;

/// <summary>Deployment Pipelines API (https://learn.microsoft.com/rest/api/fabric/core/deployment-pipelines).</summary>
public sealed class DeploymentPipelineService
{
    private readonly FabricApiClient _api;
    public DeploymentPipelineService(FabricApiClient api) => _api = api;

    public Task<List<DeploymentPipeline>> ListAsync(CancellationToken ct)
        => _api.GetAllPagesAsync<DeploymentPipeline>("deploymentPipelines", ct);

    public async Task<DeploymentPipeline?> FindByNameAsync(string displayName, CancellationToken ct)
        => (await ListAsync(ct)).FirstOrDefault(p => p.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase));

    public Task<DeploymentPipeline> CreateAsync(CreatePipelineRequest request, CancellationToken ct)
        => _api.PostAsync<DeploymentPipeline>("deploymentPipelines", request, ct);

    public async Task<List<DeploymentPipelineStage>> GetStagesAsync(string pipelineId, CancellationToken ct)
        => (await _api.GetAllPagesAsync<DeploymentPipelineStage>($"deploymentPipelines/{pipelineId}/stages", ct))
            .OrderBy(s => s.Order).ToList();

    public Task<List<StageItem>> GetStageItemsAsync(string pipelineId, string stageId, CancellationToken ct)
        => _api.GetAllPagesAsync<StageItem>($"deploymentPipelines/{pipelineId}/stages/{stageId}/items", ct);

    public Task AssignWorkspaceAsync(string pipelineId, string stageId, string workspaceId, CancellationToken ct)
        => _api.PostAsync($"deploymentPipelines/{pipelineId}/stages/{stageId}/assignWorkspace",
            new { workspaceId }, ct);

    public Task<LroHandle> DeployAsync(string pipelineId, DeployRequest request, CancellationToken ct)
        => _api.StartLongRunningAsync($"deploymentPipelines/{pipelineId}/deploy", request, ct);

    /// <summary>GET /deploymentPipelines/{id}/operations/{operationId} — full details incl. per-item execution plan.</summary>
    public Task<DeploymentOperationResult> GetOperationAsync(string pipelineId, string operationId, CancellationToken ct)
        => _api.GetAsync<DeploymentOperationResult>($"deploymentPipelines/{pipelineId}/operations/{operationId}", ct);

    public Task<List<DeploymentPipelineOperation>> ListOperationsAsync(string pipelineId, CancellationToken ct)
        => _api.GetAllPagesAsync<DeploymentPipelineOperation>($"deploymentPipelines/{pipelineId}/operations", ct);
}

/// <summary>Core Workspaces + Items + Git APIs.</summary>
public sealed class WorkspaceService
{
    private readonly FabricApiClient _api;
    public WorkspaceService(FabricApiClient api) => _api = api;

    public Task<Workspace> GetAsync(string workspaceId, CancellationToken ct)
        => _api.GetAsync<Workspace>($"workspaces/{workspaceId}", ct);

    public async Task<Workspace?> FindByNameAsync(string displayName, CancellationToken ct)
        => (await _api.GetAllPagesAsync<Workspace>("workspaces", ct))
            .FirstOrDefault(w => w.DisplayName.Equals(displayName, StringComparison.OrdinalIgnoreCase));

    public Task<Workspace> CreateAsync(string displayName, string? capacityId, string? description, CancellationToken ct)
        => _api.PostAsync<Workspace>("workspaces", new { displayName, capacityId, description }, ct);

    /// <summary>Moves a workspace to another capacity (POST /workspaces/{id}/assignToCapacity).</summary>
    public Task AssignToCapacityAsync(string workspaceId, string capacityId, CancellationToken ct)
        => _api.PostAsync($"workspaces/{workspaceId}/assignToCapacity", new { capacityId }, ct);

    public Task<List<FabricItem>> ListItemsAsync(string workspaceId, CancellationToken ct)
        => _api.GetAllPagesAsync<FabricItem>($"workspaces/{workspaceId}/items", ct);

    /// <summary>POST /workspaces/{ws}/items/{id}/getDefinition (may be long running).</summary>
    public async Task<ItemDefinition?> GetItemDefinitionAsync(string workspaceId, string itemId, TimeSpan timeout, CancellationToken ct)
        => (await _api.PostAndWaitAsync<ItemDefinitionResponse>(
            $"workspaces/{workspaceId}/items/{itemId}/getDefinition", null, timeout, ct))?.Definition;

    /// <summary>POST /workspaces/{ws}/items/{id}/updateDefinition — restores a saved definition (incl. .platform metadata).</summary>
    public Task UpdateItemDefinitionAsync(string workspaceId, string itemId, ItemDefinition definition, TimeSpan timeout, CancellationToken ct)
    {
        var hasPlatform = definition.Parts.Any(p => p.Path.Equals(".platform", StringComparison.OrdinalIgnoreCase));
        return _api.PostAndWaitAsync<object>(
            $"workspaces/{workspaceId}/items/{itemId}/updateDefinition{(hasPlatform ? "?updateMetadata=true" : "")}",
            new { definition }, timeout, ct);
    }

    public Task DeleteItemAsync(string workspaceId, string itemId, CancellationToken ct)
        => _api.DeleteAsync($"workspaces/{workspaceId}/items/{itemId}", ct);

    public Task<GitStatus> GetGitStatusAsync(string workspaceId, CancellationToken ct)
        => _api.GetAsync<GitStatus>($"workspaces/{workspaceId}/git/status", ct);

    /// <summary>Pulls the latest commit from the connected Git branch into the workspace.</summary>
    public Task<LroHandle> UpdateFromGitAsync(string workspaceId, GitStatus status, bool preferRemote, CancellationToken ct)
        => _api.StartLongRunningAsync($"workspaces/{workspaceId}/git/updateFromGit", new
        {
            remoteCommitHash = status.RemoteCommitHash,
            workspaceHead = status.WorkspaceHead,
            conflictResolution = new
            {
                conflictResolutionType = "Workspace",
                conflictResolutionPolicy = preferRemote ? "PreferRemote" : "PreferWorkspace"
            },
            options = new { allowOverrideItems = true }
        }, ct);
}

public sealed class CapacityService
{
    private readonly FabricApiClient _api;
    public CapacityService(FabricApiClient api) => _api = api;

    /// <summary>Capacities the caller has access to.</summary>
    public Task<List<Capacity>> ListAsync(CancellationToken ct)
        => _api.GetAllPagesAsync<Capacity>("capacities", ct);
}

/// <summary>Convenience bundle so steps can reach every service.</summary>
public sealed class FabricServices
{
    public FabricApiClient Api { get; }
    public DeploymentPipelineService Pipelines { get; }
    public WorkspaceService Workspaces { get; }
    public CapacityService Capacities { get; }

    public FabricServices(FabricApiClient api)
    {
        Api = api;
        Pipelines = new DeploymentPipelineService(api);
        Workspaces = new WorkspaceService(api);
        Capacities = new CapacityService(api);
    }
}
