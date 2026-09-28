using System.Text.Json;
using Fabric.Deployment.Core.Configuration;
using Fabric.Deployment.Core.Engine;

namespace Fabric.Deployment.Core.Steps;

/// <summary>Decides whether a plan may be deployed.</summary>
public interface IApprovalGate
{
    Task<ApprovalRecord> RequestAsync(DeploymentContext ctx, DeploymentPlan plan);
}

/// <summary>Approves automatically — use when the CI system (environment approvals) is the gate.</summary>
public sealed class AutoApprovalGate : IApprovalGate
{
    private readonly string _method;
    public AutoApprovalGate(string method = "Auto") => _method = method;
    public Task<ApprovalRecord> RequestAsync(DeploymentContext ctx, DeploymentPlan plan)
        => Task.FromResult(new ApprovalRecord(true, ctx.InitiatedBy, DateTimeOffset.UtcNow, _method, null));
}

/// <summary>Asks on the console. Refuses when input is redirected (non-interactive CI).</summary>
public sealed class InteractiveApprovalGate : IApprovalGate
{
    public Task<ApprovalRecord> RequestAsync(DeploymentContext ctx, DeploymentPlan plan)
    {
        if (Console.IsInputRedirected)
            return Task.FromResult(new ApprovalRecord(false, "n/a", DateTimeOffset.UtcNow, "Interactive",
                "No interactive console — use approval.mode=PlanFile or Auto in CI"));

        Console.Write($"Deploy {plan.Items.Count} item(s) to '{plan.TargetStageName}'? Type the target stage name to confirm: ");
        var answer = Console.ReadLine()?.Trim();
        var ok = string.Equals(answer, plan.TargetStageName, StringComparison.OrdinalIgnoreCase);
        return Task.FromResult(new ApprovalRecord(ok, Environment.UserName, DateTimeOffset.UtcNow, "Interactive",
            ok ? null : "Confirmation text did not match"));
    }
}

/// <summary>
/// CI pattern: job 1 runs `plan` and publishes plan.json; a human approves the environment;
/// job 2 runs `deploy --approved-plan plan.json`. The fresh plan must hash-match the approved one,
/// so nothing that changed after approval can slip into production.
/// </summary>
public sealed class PlanFileApprovalGate : IApprovalGate
{
    private readonly string _path;
    public PlanFileApprovalGate(string path) => _path = path;

    public async Task<ApprovalRecord> RequestAsync(DeploymentContext ctx, DeploymentPlan plan)
    {
        if (!File.Exists(_path))
            return new ApprovalRecord(false, "n/a", DateTimeOffset.UtcNow, "PlanFile", $"Approved plan not found: {_path}");

        var approved = JsonSerializer.Deserialize<DeploymentPlan>(await File.ReadAllTextAsync(_path), PlanStep.JsonOpts);
        var approvedHash = approved?.ComputeHash();
        var ok = approvedHash == plan.Hash;
        var approver = Environment.GetEnvironmentVariable("FABRIC_DEPLOY_APPROVER") ?? ctx.InitiatedBy;
        return new ApprovalRecord(ok, approver, DateTimeOffset.UtcNow, "PlanFile",
            ok ? $"Matches approved plan {plan.Hash[..12]}"
               : $"Plan drifted since approval (approved {approvedHash?[..12]}, now {plan.Hash[..12]}). Re-run plan and re-approve.");
    }
}

public sealed class ApprovalStep : IDeploymentStep
{
    private readonly IApprovalGate _gate;
    public ApprovalStep(IApprovalGate gate) => _gate = gate;
    public string Name => "Approval";

    public async Task ExecuteAsync(DeploymentContext ctx)
    {
        var plan = ctx.Plan ?? throw new InvalidOperationException("No plan");
        var needsApproval = ctx.TargetStageSettings?.RequiresApproval ?? false;

        if (!needsApproval && _gate is not PlanFileApprovalGate)
        {
            ctx.Approval = new ApprovalRecord(true, ctx.InitiatedBy, DateTimeOffset.UtcNow, "NotRequired", null);
            ctx.Log.Info($"Stage '{plan.TargetStageName}' does not require approval");
            return;
        }

        ctx.Approval = await _gate.RequestAsync(ctx, plan);
        if (ctx.Approval.Approved)
        {
            ctx.Log.Success($"Approved by {ctx.Approval.By} via {ctx.Approval.Method}. {ctx.Approval.Comment}");
        }
        else
        {
            ctx.Log.Error($"Not approved: {ctx.Approval.Comment}");
            ctx.Stop(RunOutcome.Rejected, ctx.Approval.Comment ?? "Rejected");
        }
    }

    public static IApprovalGate CreateGate(ApprovalSettings settings) => settings.Mode switch
    {
        ApprovalMode.Auto => new AutoApprovalGate(),
        ApprovalMode.PlanFile => new PlanFileApprovalGate(settings.ApprovedPlanPath
                                  ?? throw new InvalidOperationException("approval.approvedPlanPath (or --approved-plan) is required for PlanFile mode")),
        _ => new InteractiveApprovalGate()
    };
}
