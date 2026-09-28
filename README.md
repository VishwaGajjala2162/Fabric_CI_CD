# Fabric Deployment Framework (.NET 8)

A reusable .NET framework, CLI (`fabric-deploy`) and set of GitHub Actions workflows. It moves Microsoft Fabric items between workspaces (DEV → STAGE → PROD) and capacities using the **Fabric Deployment Pipelines REST API**, with approvals, **rollback** and **monitoring**.

```
Git repo → CI build/test → fabric-deploy
  Preflight → Plan → Approval → Backup (rollback point) → Deployment Operation → Operation Monitor
  → Validation → [Auto-rollback on failure] → Audit / History → Report → Notification
```

## Documentation: follow in order

| # | Guide | When |
|---|---|---|
| 1 | [docs/SETUP.md](docs/SETUP.md) | Once: service principal, Fabric settings, GitHub secrets and environments, first run |
| 2 | [docs/DEPLOY.md](docs/DEPLOY.md) | Every release: DEV → STAGE → approve → PROD, selective deploys, hotfixes |
| 3 | [docs/ROLLBACK.md](docs/ROLLBACK.md) | Something went wrong: automatic, workflow, CLI, `git revert`, hotfix, data restore |
| 4 | [docs/MONITORING.md](docs/MONITORING.md) | Watching deployments live, reports, alerts, scheduled health check, audit |
| 5 | [docs/REUSE.md](docs/REUSE.md) | Adding the next solution / repository, custom steps, using it as a library |

## Repository contents

```
.github/workflows/
  fabric-deploy.yml       push to main / manual → build → DEV→STAGE → plan → approval → PROD
  _fabric-stage.yml       reusable: one stage hop (bootstrap, git-sync, plan, deploy, reports)
  fabric-rollback.yml     manual: undo a deployment run (dry run first, same approvers)
  fabric-monitor.yml      scheduled: health check + history, alert on problems
docs/                     step-by-step guides (above)
src/Fabric.Deployment.Core/          reusable library, no NuGet dependencies
  Auth/ Api/ Services/ Configuration/ Engine/ Steps/ Rollback/ Monitoring/ Bootstrap/
src/Fabric.Deployment.Cli/           fabric-deploy CLI + deploysettings.json
tests/mock-fabric-api/               local mock of the Fabric API (try everything without a tenant)
```

## CLI

```
SET UP     capacities · bootstrap · git-sync --stage DEV
DEPLOY     plan / deploy --source <stage> --target <stage> [--dry-run] [--approved-plan plan.json]
           [--include-names ..] [--include-types ..] [--no-auto-rollback]
ROLL BACK  rollback --snapshot <file> | --latest --target <stage> [--dry-run] [--delete-created] [--yes] [--force]
MONITOR    status [--operation id] [--watch] · health [--fail-on-warning] · history [--local]
```

Exit codes: `0` ok · `1` failed · `2` rejected · `3` partial · `4` rolled back · `5` unhealthy.

## Key behaviours

- **Capacities:** each stage has a `capacityId`. Empty stages are created on it, and workspaces on the wrong capacity are moved before deploying (and moved back on rollback). Preflight blocks inactive capacities and unintended cross-region moves.
- **Approvals:** PROD deploys only the `plan.json` reviewers approved. If anything changed since the plan was made, the deploy is rejected.
- **Rollback:** item definitions are backed up before every deployment. A failure restores them automatically, and `fabric-rollback` undoes a successful release on demand.
- **Monitoring:** live logs and `status --watch`, a report for each run (kept 90 days), Teams/Slack alerts, and a scheduled health check (capacity, failed or stuck deployments, drift).
- **Safety:** one deployment at a time per pipeline. Rate-limited calls are retried using Fabric's Retry-After header. A deploy request is never re-sent blindly after a network error.

## Try it locally without a tenant

```bash
dotnet build
python3 tests/mock-fabric-api/mock_fabric_api.py 5055 &
export FABRIC_API_BASE_URL=http://localhost:5055/v1 FABRIC_ACCESS_TOKEN=fake
CLI="dotnet src/Fabric.Deployment.Cli/bin/Debug/net8.0/fabric-deploy.dll"
CFG="--config tests/mock-fabric-api/deploysettings.mock.json"

$CLI bootstrap $CFG
$CLI deploy $CFG --source DEV --target STAGE
$CLI deploy $CFG --source DEV --target STAGE --note "FAIL"     # injected failure → automatic rollback (exit 4)
$CLI rollback $CFG --latest --target STAGE --dry-run
$CLI health $CFG
$CLI status $CFG
$CLI history $CFG --local
```

## Limits to know

- Deployment copies item **definitions, not data**. For data rollback see ROLLBACK.md, option F.
- At most 300 items per deploy call. Larger releases are batched in dependency order.
- Use Fabric **deployment rules** or **Variable Libraries** for connections that differ per stage.
- Not every item type supports deployment pipelines or definition export. Anything that can't be backed up is flagged in the report.
