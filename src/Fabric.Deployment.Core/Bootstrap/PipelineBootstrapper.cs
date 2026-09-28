using Fabric.Deployment.Core.Api;
using Fabric.Deployment.Core.Configuration;
using Fabric.Deployment.Core.Logging;
using Fabric.Deployment.Core.Services;

namespace Fabric.Deployment.Core.Bootstrap;

/// <summary>
/// Idempotently creates the landing zone described in settings:
///   pipeline (with stages) → one workspace per stage on its capacity → workspace assigned to stage.
/// Safe to run on every CI build: existing resources are reused, capacity drift is corrected.
/// </summary>
public sealed class PipelineBootstrapper
{
    private readonly FabricServices _f;
    private readonly IDeployLogger _log;

    public PipelineBootstrapper(FabricServices fabric, IDeployLogger log) { _f = fabric; _log = log; }

    public async Task<DeploymentPipeline> EnsureAsync(DeploymentSettings s, bool createEmptyStageWorkspaces, CancellationToken ct)
    {
        if (s.Pipeline.Stages.Count < 2) throw new InvalidOperationException("pipeline.stages needs at least 2 stages");

        // 1. Pipeline
        var pipeline = !string.IsNullOrWhiteSpace(s.Pipeline.Id)
            ? new DeploymentPipeline { Id = s.Pipeline.Id!, DisplayName = s.Pipeline.DisplayName }
            : await _f.Pipelines.FindByNameAsync(s.Pipeline.DisplayName, ct);
        if (pipeline is null)
        {
            _log.Info($"Creating deployment pipeline '{s.Pipeline.DisplayName}' with stages {string.Join(" → ", s.Pipeline.Stages.Select(x => x.Name))}");
            pipeline = await _f.Pipelines.CreateAsync(new CreatePipelineRequest
            {
                DisplayName = s.Pipeline.DisplayName,
                Description = s.Pipeline.Description,
                Stages = s.Pipeline.Stages.Select(x => new CreatePipelineStage { DisplayName = x.Name, IsPublic = x.IsPublic }).ToList()
            }, ct);
            _log.Success($"Pipeline created: {pipeline.Id}");
        }
        else _log.Info($"Pipeline exists: {pipeline.DisplayName} ({pipeline.Id})");

        // 2. Stages ↔ workspaces ↔ capacities
        var stages = await _f.Pipelines.GetStagesAsync(pipeline.Id, ct);
        for (var i = 0; i < s.Pipeline.Stages.Count; i++)
        {
            var cfg = s.Pipeline.Stages[i];
            var stage = stages.FirstOrDefault(x => x.DisplayName.Equals(cfg.Name, StringComparison.OrdinalIgnoreCase))
                        ?? (i < stages.Count ? stages[i] : null);
            if (stage is null) { _log.Warn($"Stage '{cfg.Name}' not found in pipeline — skipped"); continue; }

            if (!string.IsNullOrEmpty(stage.WorkspaceId))
            {
                _log.Info($"[{stage.DisplayName}] assigned to '{stage.WorkspaceName}'");
                await EnsureCapacityAsync(stage.WorkspaceId!, cfg, ct);
                continue;
            }

            // Stage is empty. Leave later stages empty unless asked: the first deploy creates
            // them with createdWorkspaceDetails and keeps item bindings intact.
            if (i > 0 && !createEmptyStageWorkspaces)
            {
                _log.Info($"[{stage.DisplayName}] empty — will be created on first deployment ('{cfg.WorkspaceName}')");
                continue;
            }

            var ws = await _f.Workspaces.FindByNameAsync(cfg.WorkspaceName, ct);
            if (ws is null)
            {
                _log.Info($"[{stage.DisplayName}] creating workspace '{cfg.WorkspaceName}' on capacity {cfg.CapacityId}");
                ws = await _f.Workspaces.CreateAsync(cfg.WorkspaceName, cfg.CapacityId, $"{s.Pipeline.DisplayName} – {cfg.Name}", ct);
            }
            else await EnsureCapacityAsync(ws.Id, cfg, ct);

            await _f.Pipelines.AssignWorkspaceAsync(pipeline.Id, stage.Id, ws.Id, ct);
            _log.Success($"[{stage.DisplayName}] assigned workspace '{ws.DisplayName}' ({ws.Id})");
        }
        return pipeline;
    }

    private async Task EnsureCapacityAsync(string workspaceId, StageSettings cfg, CancellationToken ct)
    {
        if (cfg.CapacityId is null || !cfg.EnforceCapacity) return;
        var ws = await _f.Workspaces.GetAsync(workspaceId, ct);
        if (cfg.CapacityId.Equals(ws.CapacityId, StringComparison.OrdinalIgnoreCase)) return;
        _log.Info($"  moving '{ws.DisplayName}' from capacity {ws.CapacityId ?? "(none)"} to {cfg.CapacityId}");
        await _f.Workspaces.AssignToCapacityAsync(workspaceId, cfg.CapacityId, ct);
    }
}

/// <summary>Optional first hop in the flow: bring the DEV workspace up to date with its Git branch.</summary>
public sealed class GitSyncService
{
    private readonly FabricServices _f;
    private readonly IDeployLogger _log;
    public GitSyncService(FabricServices fabric, IDeployLogger log) { _f = fabric; _log = log; }

    public async Task SyncAsync(string workspaceId, bool preferRemote, TimeSpan timeout, CancellationToken ct)
    {
        var status = await _f.Workspaces.GetGitStatusAsync(workspaceId, ct);
        if (status.RemoteCommitHash == status.WorkspaceHead)
        {
            _log.Info($"Workspace already at commit {status.RemoteCommitHash}");
            return;
        }
        _log.Info($"Updating workspace from Git: {status.WorkspaceHead ?? "(none)"} → {status.RemoteCommitHash}");
        var handle = await _f.Workspaces.UpdateFromGitAsync(workspaceId, status, preferRemote, ct);
        if (handle.IsCompletedImmediately) { _log.Success("Git update complete"); return; }

        var state = await _f.Api.WaitForOperationAsync(handle.OperationId!, timeout, handle.RetryAfterSeconds, null, ct);
        if (state.Status != OperationStatus.Succeeded)
            throw new InvalidOperationException($"Update from Git {state.Status}: {state.Error?.ErrorCode} {state.Error?.Message}");
        _log.Success("Git update complete");
    }
}
