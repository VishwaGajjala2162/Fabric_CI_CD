# 2 · Deploying (every time)

## Normal release: DEV → STAGE → PROD

1. **Build and test the change in DEV** (the Fabric DEV workspace).
2. **Commit it to Git.** In the DEV workspace, open **Source control → Commit** (this commits your items to `/fabric` on `main`). Or merge a pull request into `main`.
3. **The pipeline starts automatically** on the push to `main`. You can also run it by hand: **Actions → fabric-deploy → Run workflow**.
4. **DEV → STAGE runs by itself:**
   - bootstrap (checks the pipeline, workspaces and capacities)
   - optional Git sync
   - preflight → plan → **backup (rollback point)** → deploy → monitor → validate → report
   - on failure, STAGE is **rolled back automatically**
5. **Test in STAGE**, the way you would test in the real world: open the reports, run the notebooks and pipelines.
6. **Review the PROD plan.** Open the run → job **Plan STAGE → PROD** → *Summary* lists every item that will be created or updated in PROD.
7. **Approve.** The run shows *"Waiting for review: fabric-prod"* → **Review deployments → fabric-prod → Approve and deploy.** (Reject it if the plan isn't what you expected.)
8. **PROD deploys only the approved plan.** If anything changed in STAGE after the plan was made, the job refuses to deploy (exit code 2): re-run the workflow and approve the new plan.
9. **Check the result:** the job summary, the Teams/Slack message, and the `report-PROD` artifact. For more, see [MONITORING.md](MONITORING.md).

## Other ways to deploy

| I want to… | Do this |
|---|---|
| Deploy only some items | **Run workflow** → `include_names` = `Sales Model,Sales Report` (wildcards like `Sales*` work), or `include_types` = `Report,SemanticModel` |
| Deploy to STAGE only | **Run workflow** → untick `deploy_prod` |
| See what would happen without changing anything | Locally: `fabric-deploy deploy --source DEV --target STAGE --dry-run` |
| Hotfix one broken item | Fix it in DEV → commit → **Run workflow** with `include_names` = that item → approve PROD |
| Deploy another solution | **Run workflow** → `config` = `deploysettings.finance.json` (see [REUSE.md](REUSE.md)) |
| Move a stage to another capacity | Change that stage's `capacityId` in the settings file → push → the next run moves the workspace |
| Undo a deployment | See [ROLLBACK.md](ROLLBACK.md) |

## Running from your own machine

```bash
dotnet publish src/Fabric.Deployment.Cli -c Release -o out && cd out
export FABRIC_TENANT_ID=<tenant> FABRIC_CLIENT_ID=<app-id> FABRIC_CLIENT_SECRET=<secret>

dotnet fabric-deploy.dll plan   --source DEV --target STAGE
dotnet fabric-deploy.dll deploy --source DEV --target STAGE --note "Sprint 42"
dotnet fabric-deploy.dll deploy --source STAGE --target PROD   # asks you to type PROD to confirm
```

## What happens inside one deployment

| # | Step | On failure |
|---|---|---|
| 1 | **Preflight**: pipeline, stages, workspaces, capacity active and in the right region, no other deployment running | Stops. Nothing has changed. |
| 2 | **Plan**: filtered items, create/update for each, capacity move, batches of 300, `plan.json` + hash | Stops. Nothing has changed. |
| 3 | **Approval**: PROD only (environment reviewers + plan-hash match) | Stops (rejected). Nothing has changed. |
| 4 | **Backup**: saves the current definition of every item that will be overwritten | Warns, or stops if `failIfSnapshotIncomplete` |
| 5 | **Deploy**: capacity move if needed, then the Fabric deploy API for each batch | → auto-rollback |
| 6 | **Monitor**: polls until done, then reads per-item results | → auto-rollback |
| 7 | **Validate**: every item is present in the target, and the workspace is on the right capacity | → auto-rollback |
| 8 | **Auto-rollback**: restores the backup if 5–7 failed | Reports what couldn't be restored |
| 9 | **Audit, report, notify**: always run | — |

**Exit codes:** `0` ok · `1` failed · `2` rejected / plan changed · `3` partially succeeded · `4` failed **and rolled back** · `5` health check unhealthy.

## Before each PROD release

- [ ] STAGE tested, and the plan summary reviewed
- [ ] Deployment rules / Variable Library values correct for PROD (connections, lakehouse bindings)
- [ ] The *Rollback point* section of the STAGE report shows no ⚠️ *not backed up* items (or you accept them)
- [ ] Data changes (tables, schemas) planned separately. Deployment moves item definitions, not data.
