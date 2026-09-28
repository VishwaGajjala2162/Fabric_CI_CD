# 5 · Reusing the framework for future deployments

The code is generic. Everything specific to one solution lives in **one settings file** and **one small caller workflow**.

## Option 1 — Another solution in the same repository (quickest)

1. Copy the settings file:
   ```bash
   cp src/Fabric.Deployment.Cli/deploysettings.json src/Fabric.Deployment.Cli/deploysettings.finance.json
   ```
2. Edit the copy: `pipeline.displayName` (for example `Finance Reporting`), the workspace names and capacity IDs for each stage, and the item filters.
3. Add `.github/workflows/fabric-deploy-finance.yml`:
   ```yaml
   name: fabric-deploy-finance
   on:
     push:
       branches: [ main ]
       paths: [ "fabric/finance/**", "src/Fabric.Deployment.Cli/deploysettings.finance.json" ]
     workflow_dispatch:
   concurrency: { group: fabric-deploy-deploysettings.finance.json, cancel-in-progress: false }
   jobs:
     build:
       runs-on: ubuntu-latest
       steps:
         - uses: actions/checkout@v4
         - uses: actions/setup-dotnet@v4
           with: { dotnet-version: 8.0.x }
         - run: dotnet publish src/Fabric.Deployment.Cli -c Release -o cli
         - uses: actions/upload-artifact@v4
           with: { name: cli, path: cli }
     stage:
       needs: build
       uses: ./.github/workflows/_fabric-stage.yml
       secrets: inherit
       with: { config: deploysettings.finance.json, source: DEV, target: STAGE, environment: fabric-stage }
     prod:
       needs: stage
       uses: ./.github/workflows/_fabric-stage.yml
       secrets: inherit
       with: { config: deploysettings.finance.json, source: STAGE, target: PROD, environment: fabric-prod, plan_first: true }
   ```
4. Do Setup steps 2–3 for the new DEV workspace (service principal access, capacity permissions). Push, then run it.
5. Rollback and monitoring work the same way: pass `config: deploysettings.finance.json` to **fabric-rollback** / **fabric-monitor**.

> Also limit the default `fabric-deploy.yml` with `paths:` so a Finance commit doesn't redeploy Sales.

## Option 2 — One repository per solution (template)

1. Mark this repository as a **Template repository** (Settings → General).
2. For each new solution: **Use this template → Create a new repository**.
3. In the new repository, do [SETUP.md](SETUP.md) steps 3, 5, 6 and 7. The service principal and Fabric tenant settings from steps 1–2 are reused.

## Option 3 — Share one framework repository across many solution repositories

Keep the framework in one repository (for example `org/fabric-deploy-framework`, tagged `v1.0.0`). Each solution repository keeps only its own settings file and this workflow:

```yaml
name: fabric-deploy
on: { push: { branches: [ main ] }, workflow_dispatch: {} }
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4                       # this solution repo (holds deploysettings.json)
      - uses: actions/checkout@v4
        with: { repository: org/fabric-deploy-framework, ref: v1.0.0, path: framework }
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 8.0.x }
      - run: dotnet publish framework/src/Fabric.Deployment.Cli -c Release -o cli
      - run: cp deploysettings.json cli/                # this solution's settings
      - uses: actions/upload-artifact@v4
        with: { name: cli, path: cli }
  stage:
    needs: build
    uses: org/fabric-deploy-framework/.github/workflows/_fabric-stage.yml@v1.0.0
    secrets: inherit
    with: { source: DEV, target: STAGE, environment: fabric-stage }
  prod:
    needs: stage
    uses: org/fabric-deploy-framework/.github/workflows/_fabric-stage.yml@v1.0.0
    secrets: inherit
    with: { source: STAGE, target: PROD, environment: fabric-prod, plan_first: true }
```

If the framework repository is private: framework repo → **Settings → Actions → General → Access →** *Accessible from repositories in the organization*. Pin to a tag so a framework change never alters running releases unexpectedly. Release updates as `v1.1.0` and so on.

## Option 4 — Different stages (2, 4 or custom names)

1. List the stages in order in `pipeline.stages` (for example `DEV`, `TEST`, `UAT`, `PROD`). A new pipeline is created with them. For an existing pipeline, use its current stage names (or rename the stages in Fabric first).
2. Add one `_fabric-stage.yml` call per hop in the workflow (`DEV→TEST`, `TEST→UAT`, `UAT→PROD`), each with its own environment.

## Option 5 — Use the library in your own .NET code

```csharp
var settings = DeploymentSettings.Load("deploysettings.json");
settings.Deployment.SourceStage = "STAGE";
settings.Deployment.TargetStage = "PROD";

var log    = new ConsoleDeployLogger();
var fabric = FabricDeploymentFactory.CreateServices(settings, log);
var ctx    = await FabricDeploymentFactory.DeployAsync(settings, fabric, log, ct);   // full flow incl. backup + auto-rollback

// Manual rollback
var manifest = SnapshotStore.Load(ctx.SnapshotPath!);
var result   = await new RollbackService(fabric, log, settings.Rollback)
                   .RestoreAsync(manifest, Path.GetDirectoryName(ctx.SnapshotPath!)!, deleteCreated: true,
                                 restoreCapacity: true, dryRun: false, ct);

// Health
var health = await new HealthService(fabric).CheckAsync(settings, ct);
```

### Add your own step (e.g. smoke test, refresh, approval ticket)

```csharp
public sealed class SmokeTestStep : IDeploymentStep
{
    public string Name => "Smoke test";
    public async Task ExecuteAsync(DeploymentContext ctx)
    {
        var items = await ctx.Fabric.Workspaces.ListItemsAsync(ctx.TargetWorkspace!.Id, ctx.CancellationToken);
        if (!items.Any(i => i.Type == "SemanticModel"))
            throw new InvalidOperationException("No semantic model in target");   // throwing = failure → auto-rollback
    }
}

var orchestrator = new DeploymentOrchestrator(new IDeploymentStep[]
{
    new PreflightStep(), new PlanStep(), new ApprovalStep(new AutoApprovalGate()),
    new SnapshotStep(), new DeployStep(), new MonitorStep(), new ValidationStep(),
    new SmokeTestStep(),                                   // ← yours; placed before RollbackStep so a failure rolls back
    new RollbackStep(), new AuditStep(/* new MySqlAuditStore(...) */), new ReportStep(), new NotificationStep()
});
var result = await orchestrator.RunAsync(new DeploymentContext(settings, fabric, log, ct));
```

### Plug in your own storage or sign-in

| Interface | Default | Replace with |
|---|---|---|
| `IAuditStore` | `JsonlAuditStore` (file) | SQL table, Lakehouse, Log Analytics |
| `IApprovalGate` | Interactive / PlanFile / Auto | ServiceNow or Jira change ticket check |
| `ITokenProvider` | Client secret / env token | `DelegateTokenProvider` + Azure.Identity (managed identity, OIDC) |
| `IDeployLogger` | Console | `ILogger`, Serilog, Application Insights |

## Checklist for each new solution

- [ ] Settings file copied and edited (pipeline name, workspaces, capacity IDs, filters)
- [ ] Service principal is Admin on the new DEV workspace, and Contributor on the new capacities
- [ ] Caller workflow added (or the `config` input used)
- [ ] `fabric-prod` environment has reviewers
- [ ] First run done, and `fabric-monitor` is green
