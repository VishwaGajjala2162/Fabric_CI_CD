using System.Text.Json;
using Fabric.Deployment.Core.Api;
using Fabric.Deployment.Core.Configuration;
using Fabric.Deployment.Core.Engine;
using Fabric.Deployment.Core.Services;

namespace Fabric.Deployment.Core.Rollback;

// Fabric deployment pipelines have no built-in "undo". The framework creates its own rollback
// point: before deploying it saves the definition of every target item that will be overwritten
// (getDefinition), records the target's capacity, and after deploying records the items it newly
// created. Rolling back = updateDefinition with the saved definitions, optionally deleting the
// newly created items and moving the workspace back to its previous capacity.
// NOTE: definitions are code/metadata only — table data in lakehouses/warehouses is not restored.

public sealed class SnapshotManifest
{
    public string RunId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string PipelineId { get; set; } = "";
    public string PipelineName { get; set; } = "";
    public string TargetStageId { get; set; } = "";
    public string TargetStageName { get; set; } = "";
    public string? WorkspaceId { get; set; }
    public string? WorkspaceName { get; set; }
    public string? CapacityIdBefore { get; set; }
    public string? PlanHash { get; set; }
    public List<SnapshotItem> Items { get; set; } = new();
    public List<CreatedItem> CreatedItems { get; set; } = new();

    public int CapturedCount => Items.Count(i => i.Captured);
}

public sealed class SnapshotItem
{
    public string TargetItemId { get; set; } = "";
    public string SourceItemId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ItemType { get; set; } = "";
    public bool Captured { get; set; }
    /// <summary>Relative path of the saved definition file.</summary>
    public string? File { get; set; }
    public string? Error { get; set; }
}

public sealed class CreatedItem
{
    public string TargetItemId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ItemType { get; set; } = "";
}

public sealed class RollbackResult
{
    public string SnapshotRunId { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public bool DryRun { get; set; }
    public List<string> Restored { get; } = new();
    public List<string> Deleted { get; } = new();
    public List<string> Failed { get; } = new();
    public List<string> NotRestorable { get; } = new();
    public string? CapacityRestoredTo { get; set; }
    public bool Succeeded => Failed.Count == 0;
}

public static class SnapshotStore
{
    public const string ManifestFile = "snapshot.json";
    private static readonly JsonSerializerOptions Json = new(FabricApiClient.Json);

    public static string Save(SnapshotManifest m, string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, ManifestFile);
        File.WriteAllText(path, JsonSerializer.Serialize(m, Json));
        return path;
    }

    public static SnapshotManifest Load(string path)
    {
        if (Directory.Exists(path)) path = Path.Combine(path, ManifestFile);
        if (!File.Exists(path)) throw new FileNotFoundException($"Snapshot manifest not found: {path}");
        return JsonSerializer.Deserialize<SnapshotManifest>(File.ReadAllText(path), Json)
               ?? throw new InvalidOperationException("Empty snapshot manifest");
    }

    public static void SaveDefinition(string folder, string relFile, ItemDefinition def)
    {
        var full = Path.Combine(folder, relFile);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, JsonSerializer.Serialize(def, Json));
    }

    public static ItemDefinition LoadDefinition(string folder, string relFile)
        => JsonSerializer.Deserialize<ItemDefinition>(File.ReadAllText(Path.Combine(folder, relFile)), Json)
           ?? throw new InvalidOperationException($"Empty definition {relFile}");

    /// <summary>Most recent snapshot for a stage under an output directory (local runs).</summary>
    public static string? FindLatest(string outputDir, string stageName)
    {
        if (!Directory.Exists(outputDir)) return null;
        return Directory.GetFiles(outputDir, ManifestFile, SearchOption.AllDirectories)
            .Select(p => (Path: p, M: TryLoad(p)))
            .Where(x => x.M is not null && x.M.TargetStageName.Equals(stageName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.M!.CreatedAt)
            .Select(x => x.Path)
            .FirstOrDefault();
    }

    private static SnapshotManifest? TryLoad(string p) { try { return Load(p); } catch { return null; } }
}

/// <summary>Creates the rollback point and restores it.</summary>
public sealed class RollbackService
{
    private readonly FabricServices _f;
    private readonly IDeployLogger _log;
    private readonly RollbackSettings _settings;

    public RollbackService(FabricServices fabric, IDeployLogger log, RollbackSettings settings)
    {
        _f = fabric; _log = log; _settings = settings;
    }

    private TimeSpan Timeout => TimeSpan.FromMinutes(Math.Max(1, _settings.DefinitionTimeoutMinutes));

    /// <summary>Backs up every target item the plan will overwrite.</summary>
    public async Task<SnapshotManifest> CaptureAsync(DeploymentContext ctx, string folder)
    {
        var plan = ctx.Plan!;
        var m = new SnapshotManifest
        {
            RunId = ctx.RunId, PipelineId = plan.PipelineId, PipelineName = plan.PipelineName,
            TargetStageId = plan.TargetStageId, TargetStageName = plan.TargetStageName,
            WorkspaceId = ctx.TargetWorkspace?.Id, WorkspaceName = ctx.TargetWorkspace?.DisplayName,
            CapacityIdBefore = ctx.TargetWorkspace?.CapacityId, PlanHash = plan.Hash
        };

        if (ctx.TargetWorkspace is null)
        {
            _log.Info("Target stage is empty — nothing to back up (created items will be tracked for rollback)");
            return m;
        }

        foreach (var item in plan.Items.Where(i => i.Action == PlannedAction.Update && i.ExistingTargetItemId is not null))
        {
            var s = new SnapshotItem
            {
                TargetItemId = item.ExistingTargetItemId!, SourceItemId = item.SourceItemId,
                DisplayName = item.DisplayName, ItemType = item.ItemType
            };
            m.Items.Add(s);

            if (_settings.SkipItemTypes.Contains(item.ItemType, StringComparer.OrdinalIgnoreCase))
            {
                s.Error = "type excluded from snapshots";
                continue;
            }
            try
            {
                var def = await _f.Workspaces.GetItemDefinitionAsync(ctx.TargetWorkspace.Id, s.TargetItemId, Timeout, ctx.CancellationToken);
                if (def is null || def.Parts.Count == 0) { s.Error = "empty definition returned"; continue; }
                s.File = Path.Combine("items", $"{s.ItemType}-{s.TargetItemId}.json");
                SnapshotStore.SaveDefinition(folder, s.File, def);
                s.Captured = true;
                _log.Debug($"  saved {s.ItemType} {s.DisplayName} ({def.Parts.Count} parts)");
            }
            catch (FabricApiException ex)
            {
                s.Error = ex.ErrorCode ?? ex.Message;
                _log.Warn($"  cannot back up {s.ItemType} {s.DisplayName}: {s.Error}");
            }
        }
        return m;
    }

    /// <summary>After deploying: note which target items were newly created (and the workspace, if it was created).</summary>
    public async Task RecordCreatedItemsAsync(DeploymentContext ctx, SnapshotManifest m)
    {
        var plan = ctx.Plan!;
        var creates = plan.Items.Where(i => i.Action == PlannedAction.Create).ToList();
        if (creates.Count == 0) return;
        var backward = ctx.TargetStage!.Order < ctx.SourceStage!.Order;

        var stages = await _f.Pipelines.GetStagesAsync(plan.PipelineId, ctx.CancellationToken);
        var target = stages.FirstOrDefault(s => s.Id == plan.TargetStageId);
        m.WorkspaceId ??= target?.WorkspaceId;
        m.WorkspaceName ??= target?.WorkspaceName;

        var items = await _f.Pipelines.GetStageItemsAsync(plan.PipelineId, plan.SourceStageId, ctx.CancellationToken);
        foreach (var c in creates)
        {
            var now = items.FirstOrDefault(i => i.ItemId == c.SourceItemId);
            var paired = backward ? now?.SourceItemId : now?.TargetItemId;
            if (!string.IsNullOrEmpty(paired) && m.CreatedItems.All(x => x.TargetItemId != paired))
                m.CreatedItems.Add(new CreatedItem { TargetItemId = paired, DisplayName = c.DisplayName, ItemType = c.ItemType });
        }
    }

    /// <summary>Restores a snapshot. Safe to run more than once.</summary>
    public async Task<RollbackResult> RestoreAsync(SnapshotManifest m, string folder, bool deleteCreated, bool restoreCapacity,
        bool dryRun, CancellationToken ct)
    {
        var r = new RollbackResult { SnapshotRunId = m.RunId, DryRun = dryRun };
        if (m.WorkspaceId is null)
        {
            r.Failed.Add("Snapshot has no target workspace — nothing to roll back");
            r.FinishedAt = DateTimeOffset.UtcNow;
            return r;
        }
        var prefix = dryRun ? "[dry-run] would " : "";
        _log.Info($"Rolling back '{m.TargetStageName}' ({m.WorkspaceName}) to the state before run {m.RunId}");

        // 1. Restore overwritten items (upstream first: the snapshot keeps plan dependency order)
        foreach (var s in m.Items)
        {
            var label = $"{s.ItemType}/{s.DisplayName}";
            if (!s.Captured || s.File is null) { r.NotRestorable.Add($"{label}: {s.Error}"); continue; }
            try
            {
                if (!dryRun)
                {
                    var def = SnapshotStore.LoadDefinition(folder, s.File);
                    await _f.Workspaces.UpdateItemDefinitionAsync(m.WorkspaceId, s.TargetItemId, def, Timeout, ct);
                }
                r.Restored.Add(label);
                _log.Success($"  {prefix}restore {label}");
            }
            catch (Exception ex) when (ex is FabricApiException or IOException or InvalidOperationException)
            {
                r.Failed.Add($"{label}: {ex.Message}");
                _log.Error($"  restore {label} failed: {ex.Message}");
            }
        }

        // 2. Remove items this deployment created (downstream first)
        if (deleteCreated)
        {
            foreach (var c in Enumerable.Reverse(m.CreatedItems))
            {
                var label = $"{c.ItemType}/{c.DisplayName}";
                try
                {
                    if (!dryRun) await _f.Workspaces.DeleteItemAsync(m.WorkspaceId, c.TargetItemId, ct);
                    r.Deleted.Add(label);
                    _log.Success($"  {prefix}delete new item {label}");
                }
                catch (FabricApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    r.Deleted.Add($"{label} (already gone)");
                }
                catch (FabricApiException ex)
                {
                    r.Failed.Add($"delete {label}: {ex.Message}");
                    _log.Error($"  delete {label} failed: {ex.Message}");
                }
            }
        }
        else if (m.CreatedItems.Count > 0)
        {
            _log.Warn($"  {m.CreatedItems.Count} newly created item(s) left in place (use --delete-created to remove)");
        }

        // 3. Capacity
        if (restoreCapacity && m.CapacityIdBefore is not null)
        {
            var ws = await _f.Workspaces.GetAsync(m.WorkspaceId, ct);
            if (!m.CapacityIdBefore.Equals(ws.CapacityId, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (!dryRun) await _f.Workspaces.AssignToCapacityAsync(m.WorkspaceId, m.CapacityIdBefore, ct);
                    r.CapacityRestoredTo = m.CapacityIdBefore;
                    _log.Success($"  {prefix}move workspace back to capacity {m.CapacityIdBefore}");
                }
                catch (FabricApiException ex)
                {
                    r.Failed.Add($"capacity: {ex.Message}");
                }
            }
        }

        foreach (var n in r.NotRestorable) _log.Warn($"  not restorable: {n}");
        r.FinishedAt = DateTimeOffset.UtcNow;
        return r;
    }
}
