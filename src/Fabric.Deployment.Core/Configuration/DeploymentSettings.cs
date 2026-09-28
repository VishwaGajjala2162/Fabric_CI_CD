using System.Text.Json;
using System.Text.Json.Serialization;
using Fabric.Deployment.Core.Api;

namespace Fabric.Deployment.Core.Configuration;

/// <summary>Root of deploysettings.json — one file per solution / pipeline.</summary>
public sealed class DeploymentSettings
{
    public FabricConnectionSettings Fabric { get; set; } = new();
    public PipelineSettings Pipeline { get; set; } = new();
    public DeploymentRunSettings Deployment { get; set; } = new();
    public ApprovalSettings Approval { get; set; } = new();
    public MonitorSettings Monitor { get; set; } = new();
    public OutputSettings Output { get; set; } = new();
    public RollbackSettings Rollback { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();

    public static DeploymentSettings Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Settings file not found: {path}");
        var opts = new JsonSerializerOptions(FabricApiClient.Json)
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        opts.Converters.Add(new JsonStringEnumConverter());
        var settings = JsonSerializer.Deserialize<DeploymentSettings>(File.ReadAllText(path), opts)
                       ?? throw new InvalidOperationException("Settings file is empty");
        settings.ApplyEnvironmentOverrides();
        return settings;
    }

    /// <summary>Secrets and CI values come from env vars, never from the JSON file.</summary>
    public void ApplyEnvironmentOverrides()
    {
        Fabric.Auth.TenantId = Env("FABRIC_TENANT_ID") ?? Fabric.Auth.TenantId;
        Fabric.Auth.ClientId = Env("FABRIC_CLIENT_ID") ?? Fabric.Auth.ClientId;
        Fabric.BaseUrl = Env("FABRIC_API_BASE_URL") ?? Fabric.BaseUrl;
    }

    public StageSettings? FindStage(string name)
        => Pipeline.Stages.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? Env(string n) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(n))
        ? null : Environment.GetEnvironmentVariable(n);
}

public sealed class FabricConnectionSettings
{
    public string BaseUrl { get; set; } = "https://api.fabric.microsoft.com/v1";
    public AuthSettings Auth { get; set; } = new();
}

public enum AuthMode { ClientSecret, EnvironmentToken }

public sealed class AuthSettings
{
    public AuthMode Mode { get; set; } = AuthMode.ClientSecret;
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    /// <summary>Name of the env var holding the secret (never put the secret in JSON).</summary>
    public string ClientSecretEnvVar { get; set; } = "FABRIC_CLIENT_SECRET";
    public string TokenEnvVar { get; set; } = "FABRIC_ACCESS_TOKEN";
}

public sealed class PipelineSettings
{
    /// <summary>Pipeline display name (used when Id is empty).</summary>
    public string DisplayName { get; set; } = "";
    public string? Id { get; set; }
    public string? Description { get; set; }
    /// <summary>Ordered stages, e.g. Development → Test → Production.</summary>
    public List<StageSettings> Stages { get; set; } = new();
}

public sealed class StageSettings
{
    /// <summary>Stage display name in the deployment pipeline.</summary>
    public string Name { get; set; } = "";
    /// <summary>Workspace to create/assign for this stage.</summary>
    public string WorkspaceName { get; set; } = "";
    /// <summary>Capacity the stage's workspace must live on (Fabric capacity GUID).</summary>
    public string? CapacityId { get; set; }
    /// <summary>If the workspace is on a different capacity, move it before deploying.</summary>
    public bool EnforceCapacity { get; set; } = true;
    public bool IsPublic { get; set; }
    /// <summary>Deployments into this stage need an explicit approval.</summary>
    public bool RequiresApproval { get; set; }
}

public sealed class DeploymentRunSettings
{
    public string SourceStage { get; set; } = "";
    public string TargetStage { get; set; } = "";
    public string? Note { get; set; }
    public ItemFilter Items { get; set; } = new();
    public bool AllowCrossRegionDeployment { get; set; }
    /// <summary>Fabric limit is 300 items per deploy call.</summary>
    public int MaxItemsPerRequest { get; set; } = 300;
    /// <summary>Fail the run (instead of skipping) when the filters select no items.</summary>
    public bool FailIfNothingToDeploy { get; set; }
    /// <summary>Wait for a deployment already running on the pipeline instead of failing preflight.</summary>
    public bool WaitForRunningDeployments { get; set; } = true;
    public bool DryRun { get; set; }
}

public sealed class ItemFilter
{
    /// <summary>Only these item types (empty = all). e.g. ["SemanticModel","Report","Notebook"].</summary>
    public List<string> IncludeTypes { get; set; } = new();
    public List<string> ExcludeTypes { get; set; } = new();
    /// <summary>Name patterns with * wildcard. Empty = all.</summary>
    public List<string> IncludeNames { get; set; } = new();
    public List<string> ExcludeNames { get; set; } = new();
}

public enum ApprovalMode
{
    /// <summary>No human gate (DEV/TEST, or approval handled entirely by CI before this runs).</summary>
    Auto,
    /// <summary>Prompt on the console (local runs).</summary>
    Interactive,
    /// <summary>Deploy only if a previously approved plan file matches the fresh plan (CI gate).</summary>
    PlanFile
}

public sealed class ApprovalSettings
{
    public ApprovalMode Mode { get; set; } = ApprovalMode.Interactive;
    public string? ApprovedPlanPath { get; set; }
}

public sealed class MonitorSettings
{
    public int PollIntervalSeconds { get; set; } = 10;
    public int TimeoutMinutes { get; set; } = 60;
}

public sealed class OutputSettings
{
    public string Directory { get; set; } = "./deployment-output";
    /// <summary>Append-only audit log (JSON lines).</summary>
    public string AuditFile { get; set; } = "deployment-history.jsonl";
}

public sealed class RollbackSettings
{
    /// <summary>Back up the definition of every target item that will be overwritten (the rollback point).</summary>
    public bool SnapshotBeforeDeploy { get; set; } = true;
    /// <summary>Stop the deployment if any item cannot be backed up (strict mode for PROD).</summary>
    public bool FailIfSnapshotIncomplete { get; set; }
    /// <summary>Restore the snapshot automatically when deploy / monitor / validation fails.</summary>
    public bool AutoRollbackOnFailure { get; set; } = true;
    /// <summary>On rollback, delete items that this deployment newly created in the target.</summary>
    public bool DeleteCreatedItems { get; set; }
    /// <summary>On rollback, move the workspace back to its previous capacity if the deployment moved it.</summary>
    public bool RestoreCapacity { get; set; } = true;
    /// <summary>Item types not to snapshot (no public definition API, or data-bearing).</summary>
    public List<string> SkipItemTypes { get; set; } = new() { "Dashboard", "Datamart", "Warehouse", "SQLEndpoint" };
    public int DefinitionTimeoutMinutes { get; set; } = 10;
}

public enum NotificationFormat { Slack, Teams }

public enum NotifyWhen { Always, OnFailure, Never }

public sealed class NotificationSettings
{
    /// <summary>Env var holding an incoming-webhook URL (Teams Workflows / Slack). Empty = no notifications.</summary>
    public string WebhookUrlEnvVar { get; set; } = "FABRIC_DEPLOY_WEBHOOK_URL";
    public NotificationFormat Format { get; set; } = NotificationFormat.Teams;
    public NotifyWhen When { get; set; } = NotifyWhen.Always;
    /// <summary>Link included in the message, e.g. the GitHub run URL (auto-detected in Actions).</summary>
    public string? RunUrl { get; set; }
}
