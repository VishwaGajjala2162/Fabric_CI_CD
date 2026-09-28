using System.Diagnostics;
using Fabric.Deployment.Core.Logging;
using Fabric.Deployment.Core.Steps;

namespace Fabric.Deployment.Core.Engine;

/// <summary>A unit of work in the deployment flow.</summary>
public interface IDeploymentStep
{
    string Name { get; }
    /// <summary>Audit/report steps run even when an earlier step failed or stopped the run.</summary>
    bool AlwaysRun => false;
    Task ExecuteAsync(DeploymentContext ctx);
}

/// <summary>
/// Runs the steps in order:
/// Preflight → Plan → Approval → Deployment Operation → Operation Monitor → Validation → Audit → Report.
/// Add, remove or replace steps to customise the flow (e.g. a Teams notification step).
/// </summary>
public sealed class DeploymentOrchestrator
{
    private readonly List<IDeploymentStep> _steps;

    public DeploymentOrchestrator(IEnumerable<IDeploymentStep> steps) => _steps = steps.ToList();

    /// <summary>The full flow from the architecture diagram.</summary>
    public static DeploymentOrchestrator CreateDefault(IApprovalGate gate) => new(new IDeploymentStep[]
    {
        new PreflightStep(),
        new PlanStep(),
        new ApprovalStep(gate),
        new SnapshotStep(),      // rollback point
        new DeployStep(),
        new MonitorStep(),
        new ValidationStep(),
        new RollbackStep(),      // runs only if something above failed and auto-rollback is on
        new AuditStep(),
        new ReportStep(),
        new NotificationStep()
    });

    /// <summary>Plan only — produces plan.json for review, deploys nothing.</summary>
    public static DeploymentOrchestrator CreatePlanOnly() => new(new IDeploymentStep[]
    {
        new PreflightStep(),
        new PlanStep(),
        new ReportStep()
    });

    public async Task<DeploymentContext> RunAsync(DeploymentContext ctx)
    {
        var failed = false;
        var finalised = false;
        foreach (var step in _steps)
        {
            // Settle the outcome before audit/report steps so they record the real result.
            if (step.AlwaysRun && !finalised) { Finalise(ctx, failed); finalised = true; }

            if ((failed || ctx.IsStopped) && !step.AlwaysRun)
            {
                ctx.StepTimings.Add(new StepTiming(step.Name, "Skipped", 0, null));
                continue;
            }

            ctx.Log.Info($"── {step.Name} ──");
            var sw = Stopwatch.StartNew();
            try
            {
                await step.ExecuteAsync(ctx);
                ctx.StepTimings.Add(new StepTiming(step.Name, "Succeeded", sw.Elapsed.TotalSeconds, null));
                continue;
            }
            catch (OperationCanceledException) when (ctx.CancellationToken.IsCancellationRequested)
            {
                failed = ctx.HasFailed = true;
                ctx.Errors.Add($"{step.Name}: cancelled");
                ctx.StepTimings.Add(new StepTiming(step.Name, "Cancelled", sw.Elapsed.TotalSeconds, "cancelled"));
                ctx.Log.Error($"{step.Name} cancelled");
            }
            catch (Exception ex)
            {
                failed = ctx.HasFailed = true;
                ctx.Errors.Add($"{step.Name}: {ex.Message}");
                ctx.StepTimings.Add(new StepTiming(step.Name, "Failed", sw.Elapsed.TotalSeconds, ex.Message));
                ctx.Log.Error($"{step.Name} failed: {ex.Message}");
            }
        }

        if (!finalised) Finalise(ctx, failed);
        // A failing audit/report step after a good deployment is still a failure of the run.
        else if (ctx.StepTimings.Any(t => t.Status == "Failed") && ctx.Outcome == RunOutcome.Succeeded)
            ctx.Outcome = RunOutcome.PartiallySucceeded;
        return ctx;
    }

    private static void Finalise(DeploymentContext ctx, bool failed)
    {
        if (failed && ctx.Outcome is RunOutcome.InProgress or RunOutcome.Succeeded)
            ctx.Outcome = ctx.Operations.Any(o => o.Status == Api.OperationStatus.Succeeded)
                ? RunOutcome.PartiallySucceeded : RunOutcome.Failed;
        else if (ctx.Outcome == RunOutcome.InProgress)
            ctx.Outcome = RunOutcome.Succeeded;
        ctx.FinishedAt ??= DateTimeOffset.UtcNow;
    }
}
