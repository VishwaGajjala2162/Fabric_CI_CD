using System.Text.Json;
using Fabric.Deployment.Core;
using Fabric.Deployment.Core.Api;
using Fabric.Deployment.Core.Bootstrap;
using Fabric.Deployment.Core.Configuration;
using Fabric.Deployment.Core.Engine;
using Fabric.Deployment.Core.Logging;
using Fabric.Deployment.Core.Monitoring;
using Fabric.Deployment.Core.Rollback;
using Fabric.Deployment.Core.Steps;

const string Usage = """
fabric-deploy <command> [options]      (global: --config <file> --output <dir> --verbose)

  SET UP
    capacities                         list capacities you can use (IDs for deploysettings.json)
    bootstrap [--all-workspaces]       create/repair pipeline, stage workspaces, capacity assignment
    git-sync [--stage DEV]             update a stage workspace from its Git branch

  DEPLOY
    plan   --source <stage> --target <stage>
    deploy --source <stage> --target <stage> [--dry-run] [--auto-approve] [--approved-plan plan.json]
           [--note "..."] [--include-types A,B] [--include-names "Sales*,X"] [--exclude-names "..."]
           [--no-auto-rollback]

  ROLL BACK
    rollback --snapshot <snapshot.json | folder>  |  --latest --target <stage>
             [--delete-created] [--keep-capacity] [--dry-run] [--yes] [--force]

  MONITOR
    status  [--operation <id>] [--watch]   latest (or given) deployment, per-item results; --watch follows it live
    health  [--fail-on-warning]            capacity, last deployment, stuck runs, drift, pending promotions
    history [--top 20] [--local]           Fabric's deployment history, or the local audit log

Exit codes: 0 ok · 1 failed · 2 rejected · 3 partially succeeded · 4 rolled back · 5 unhealthy
""";

var cmd = args.FirstOrDefault(a => !a.StartsWith("--"))?.ToLowerInvariant() ?? "help";
string? Opt(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
bool Flag(string name) => args.Contains(name);
List<string> ListOpt(string name) => Opt(name)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() ?? new();

if (cmd is "help" or "-h") { Console.WriteLine(Usage); return 0; }

var log = new ConsoleDeployLogger(Flag("--verbose"));
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    var settings = DeploymentSettings.Load(Opt("--config") ?? "deploysettings.json");
    ApplyOverrides(settings);
    var fabric = FabricDeploymentFactory.CreateServices(settings, log);
    var ct = cts.Token;

    switch (cmd)
    {
        // ---------------------------------------------------------------- set up
        case "capacities":
            foreach (var c in await fabric.Capacities.ListAsync(ct))
                Console.WriteLine($"{c.Id}  {c.DisplayName,-30} {c.Sku,-6} {c.Region,-15} {c.State}");
            return 0;

        case "bootstrap":
            await new PipelineBootstrapper(fabric, log).EnsureAsync(settings, Flag("--all-workspaces"), ct);
            return 0;

        case "git-sync":
        {
            var (pipeline, stages) = await ResolvePipelineAsync();
            var stageName = Opt("--stage") ?? settings.Pipeline.Stages.First().Name;
            var stage = stages.FirstOrDefault(s => s.DisplayName.Equals(stageName, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Stage '{stageName}' not found");
            await new GitSyncService(fabric, log).SyncAsync(stage.WorkspaceId!, !Flag("--prefer-workspace"),
                TimeSpan.FromMinutes(settings.Monitor.TimeoutMinutes), ct);
            return 0;
        }

        // ---------------------------------------------------------------- deploy
        case "plan":
            return ExitCode(await FabricDeploymentFactory.PlanAsync(settings, fabric, log, ct));

        case "deploy":
        {
            var ctx = await FabricDeploymentFactory.DeployAsync(settings, fabric, log, ct);
            var ok = ctx.Outcome is RunOutcome.Succeeded or RunOutcome.DryRun or RunOutcome.NothingToDeploy;
            log.Log(ok ? LogLevel.Success : LogLevel.Error, $"Outcome: {ctx.Outcome}{(ctx.StopReason is null ? "" : $" — {ctx.StopReason}")}");
            if (ctx.SnapshotPath is not null && !ok && ctx.Outcome != RunOutcome.RolledBack)
                log.Warn($"Rollback point: fabric-deploy rollback --snapshot \"{ctx.SnapshotPath}\"");
            return ExitCode(ctx);
        }

        // ---------------------------------------------------------------- roll back
        case "rollback":
        {
            var path = Opt("--snapshot");
            if (path is null && Flag("--latest"))
            {
                var stage = Opt("--target") ?? throw new InvalidOperationException("--latest needs --target <stage>");
                path = SnapshotStore.FindLatest(settings.Output.Directory, stage)
                       ?? throw new InvalidOperationException($"No snapshot for stage '{stage}' under {settings.Output.Directory}");
            }
            if (path is null) throw new InvalidOperationException("Specify --snapshot <path> or --latest --target <stage>");

            var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path))!;
            var manifest = SnapshotStore.Load(path);
            log.Info($"Snapshot {manifest.RunId} taken {manifest.CreatedAt:u} of '{manifest.TargetStageName}' ({manifest.WorkspaceName})");
            log.Info($"  {manifest.CapturedCount} item(s) to restore, {manifest.CreatedItems.Count} item(s) created by that run" +
                     (manifest.CapacityIdBefore is null ? "" : $", capacity before: {manifest.CapacityIdBefore}"));

            // Safety: the stage must still point at the same workspace.
            var (_, stages) = await ResolvePipelineAsync(manifest.PipelineId);
            var current = stages.FirstOrDefault(s => s.Id == manifest.TargetStageId)
                          ?? throw new InvalidOperationException("Target stage from the snapshot no longer exists in the pipeline");
            if (manifest.WorkspaceId is not null && current.WorkspaceId != manifest.WorkspaceId)
                throw new InvalidOperationException($"Stage '{current.DisplayName}' is now assigned to a different workspace — refusing to roll back");

            // Safety: rolling back an old snapshot also discards every later deployment of those items.
            var later = (await fabric.Pipelines.ListOperationsAsync(manifest.PipelineId, ct))
                .Where(o => o.TargetStageId == manifest.TargetStageId && o.Status == OperationStatus.Succeeded
                            && o.ExecutionStartTime > manifest.CreatedAt)
                .ToList();
            if (later.Count > 1)
            {
                log.Warn($"{later.Count - 1} newer deployment(s) reached '{manifest.TargetStageName}' after run {manifest.RunId}; " +
                         "rolling back will undo those too for the backed-up items.");
                if (!Flag("--force") && !Flag("--dry-run"))
                    throw new InvalidOperationException("Use the snapshot of the most recent deployment, or add --force to roll back further.");
            }

            var dry = Flag("--dry-run");
            if (!dry && !Flag("--yes"))
            {
                if (Console.IsInputRedirected) throw new InvalidOperationException("Non-interactive: add --yes to confirm the rollback");
                Console.Write($"Roll back '{manifest.TargetStageName}' to before run {manifest.RunId}? Type the stage name to confirm: ");
                if (!string.Equals(Console.ReadLine()?.Trim(), manifest.TargetStageName, StringComparison.OrdinalIgnoreCase))
                { log.Error("Not confirmed"); return 2; }
            }

            var svc = new RollbackService(fabric, log, settings.Rollback);
            var result = await svc.RestoreAsync(manifest, folder, Flag("--delete-created"), !Flag("--keep-capacity"), dry, ct);
            await WriteRollbackRecordAsync(manifest, result);
            log.Log(result.Succeeded ? LogLevel.Success : LogLevel.Error,
                $"Rollback {(dry ? "dry run " : "")}{(result.Succeeded ? "complete" : "INCOMPLETE")}: {result.Restored.Count} restored, " +
                $"{result.Deleted.Count} deleted, {result.NotRestorable.Count} not restorable, {result.Failed.Count} failed");
            return result.Succeeded ? 0 : 1;
        }

        // ---------------------------------------------------------------- monitor
        case "status":
        {
            var (pipeline, stages) = await ResolvePipelineAsync();
            var names = stages.ToDictionary(s => s.Id, s => s.DisplayName);
            var opId = Opt("--operation")
                       ?? (await fabric.Pipelines.ListOperationsAsync(pipeline.Id, ct)).OrderByDescending(o => o.ExecutionStartTime).FirstOrDefault()?.Id
                       ?? throw new InvalidOperationException("No deployments recorded for this pipeline yet");
            while (true)
            {
                var op = await fabric.Pipelines.GetOperationAsync(pipeline.Id, opId, ct);
                if (Flag("--watch") && !OperationStatus.IsTerminal(op.Status))
                {
                    var done = op.ExecutionPlan?.Steps.Count(s => s.Status == OperationStatus.Succeeded) ?? 0;
                    log.Info($"{op.Status}: {done}/{op.ExecutionPlan?.Steps.Count ?? 0} item(s) done");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, settings.Monitor.PollIntervalSeconds)), ct);
                    continue;
                }
                OperationPrinter.Print(op, names, Console.Out);
                return op.Status == OperationStatus.Failed ? 1 : 0;
            }
        }

        case "health":
        {
            var report = await new HealthService(fabric).CheckAsync(settings, ct);
            foreach (var f in report.Findings)
                log.Log(f.Level switch { HealthLevel.Ok => LogLevel.Success, HealthLevel.Info => LogLevel.Info, HealthLevel.Warning => LogLevel.Warning, _ => LogLevel.Error },
                    $"[{f.Stage}] {f.Check}: {f.Message}");
            Directory.CreateDirectory(settings.Output.Directory);
            var md = report.ToMarkdown();
            await File.WriteAllTextAsync(Path.Combine(settings.Output.Directory, "health.md"), md, ct);
            await File.WriteAllTextAsync(Path.Combine(settings.Output.Directory, "health.json"),
                JsonSerializer.Serialize(report, PlanStep.JsonOpts), ct);
            if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
                await File.AppendAllTextAsync(summary, md, ct);
            var unhealthy = !report.IsHealthy || (Flag("--fail-on-warning") && report.Findings.Any(f => f.Level == HealthLevel.Warning));
            log.Log(unhealthy ? LogLevel.Error : LogLevel.Success, unhealthy ? "Pipeline UNHEALTHY" : "Pipeline healthy");
            return unhealthy ? 5 : 0;
        }

        case "history":
        {
            var top = int.TryParse(Opt("--top"), out var n) ? n : 20;
            if (Flag("--local"))
            {
                foreach (var r in LocalHistory.Read(Path.Combine(settings.Output.Directory, settings.Output.AuditFile), top))
                    Console.WriteLine($"{Str(r, "StartedAt")[..16]}  {Str(r, "Outcome"),-18} {Str(r, "SourceStage")} → {Str(r, "TargetStage")}  " +
                                      $"{Str(r, "ItemCount")} items  by {Str(r, "InitiatedBy")}  run {Str(r, "RunId")}");
                return 0;
            }
            var (pipeline, stages) = await ResolvePipelineAsync();
            var names = stages.ToDictionary(s => s.Id, s => s.DisplayName);
            foreach (var op in (await fabric.Pipelines.ListOperationsAsync(pipeline.Id, ct)).OrderByDescending(o => o.ExecutionStartTime).Take(top))
                Console.WriteLine($"{op.ExecutionStartTime:yyyy-MM-dd HH:mm}  {op.Status,-10} " +
                                  $"{names.GetValueOrDefault(op.SourceStageId ?? "", "?")} → {names.GetValueOrDefault(op.TargetStageId ?? "", "?")}  " +
                                  $"{op.PerformedBy?.DisplayName}  {op.Note?.Content}  [{op.Id}]");
            return 0;
        }

        default:
            log.Error($"Unknown command '{cmd}'");
            Console.WriteLine(Usage);
            return 1;
    }

    // ---------------------------------------------------------------- helpers
    async Task<(DeploymentPipeline, List<DeploymentPipelineStage>)> ResolvePipelineAsync(string? id = null)
    {
        id ??= settings.Pipeline.Id;
        var p = !string.IsNullOrWhiteSpace(id)
            ? new DeploymentPipeline { Id = id!, DisplayName = settings.Pipeline.DisplayName }
            : await fabric.Pipelines.FindByNameAsync(settings.Pipeline.DisplayName, cts.Token)
              ?? throw new InvalidOperationException($"Pipeline '{settings.Pipeline.DisplayName}' not found (run bootstrap)");
        return (p, await fabric.Pipelines.GetStagesAsync(p.Id, cts.Token));
    }

    async Task WriteRollbackRecordAsync(SnapshotManifest m, RollbackResult r)
    {
        var record = new AuditRecord(
            $"rollback-{DateTime.UtcNow:yyyyMMdd-HHmmss}", r.StartedAt, r.FinishedAt,
            r.DryRun ? "RollbackDryRun" : r.Succeeded ? "ManualRollback" : "ManualRollbackIncomplete",
            Environment.GetEnvironmentVariable("GITHUB_ACTOR") ?? Environment.UserName,
            m.PipelineId, m.PipelineName, null, m.TargetStageName, null, m.WorkspaceName, m.CapacityIdBefore,
            r.Restored.Count + r.Deleted.Count, m.PlanHash, null, "Rollback", new(), new(),
            r.Restored.Select(x => "Restored:" + x).Concat(r.Deleted.Select(x => "Deleted:" + x)).ToList(),
            r.Failed.ToList(), Environment.GetEnvironmentVariable("GITHUB_RUN_ID"), Environment.GetEnvironmentVariable("GITHUB_SHA"));
        await new JsonlAuditStore(Path.Combine(settings.Output.Directory, settings.Output.AuditFile)).AppendAsync(record, cts.Token);

        var md = $"## {(r.Succeeded ? "↩️" : "❌")} Fabric rollback of {m.TargetStageName} to before run `{m.RunId}`{(r.DryRun ? " (dry run)" : "")}\n\n" +
                 string.Join("\n", r.Restored.Select(x => $"- ✅ restored {x}")
                     .Concat(r.Deleted.Select(x => $"- 🗑️ deleted {x}"))
                     .Concat(r.NotRestorable.Select(x => $"- ⚠️ not restorable {x}"))
                     .Concat(r.Failed.Select(x => $"- ❌ {x}"))) + "\n";
        Directory.CreateDirectory(settings.Output.Directory);
        await File.WriteAllTextAsync(Path.Combine(settings.Output.Directory, "rollback-report.md"), md, cts.Token);
        if (Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY") is { Length: > 0 } summary)
            await File.AppendAllTextAsync(summary, md, cts.Token);
    }
}
catch (Exception ex)
{
    log.Error(ex.Message);
    if (Flag("--verbose")) Console.Error.WriteLine(ex);
    return 1;
}

void ApplyOverrides(DeploymentSettings s)
{
    if (Opt("--source") is { } src) s.Deployment.SourceStage = src;
    if (Opt("--target") is { } tgt) s.Deployment.TargetStage = tgt;
    if (Opt("--note") is { } note) s.Deployment.Note = note;
    if (Opt("--output") is { } outDir) s.Output.Directory = outDir;
    if (ListOpt("--include-types") is { Count: > 0 } it) s.Deployment.Items.IncludeTypes = it;
    if (ListOpt("--include-names") is { Count: > 0 } inames) s.Deployment.Items.IncludeNames = inames;
    if (ListOpt("--exclude-names") is { Count: > 0 } enames) s.Deployment.Items.ExcludeNames.AddRange(enames);
    if (Flag("--dry-run")) s.Deployment.DryRun = true;
    if (Flag("--no-auto-rollback")) s.Rollback.AutoRollbackOnFailure = false;
    if (Flag("--auto-approve")) s.Approval.Mode = ApprovalMode.Auto;
    if (Opt("--approved-plan") is { } approved) { s.Approval.Mode = ApprovalMode.PlanFile; s.Approval.ApprovedPlanPath = approved; }
}

static string Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) ? v.ToString() : "";

static int ExitCode(DeploymentContext ctx) => ctx.Outcome switch
{
    RunOutcome.Succeeded or RunOutcome.DryRun or RunOutcome.NothingToDeploy => 0,
    RunOutcome.Rejected => 2,
    RunOutcome.PartiallySucceeded => 3,
    RunOutcome.RolledBack => 4,
    _ => 1
};
