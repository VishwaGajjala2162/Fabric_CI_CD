# 4 · Monitoring deployments

## During a deployment

| Where | What you see |
|---|---|
| **GitHub → Actions →** the running job | Live log: each step (`── Preflight ──`, `── Deployment Operation ──`…), progress `Running 50%`, and every retry or warning |
| **Fabric → Deployment pipelines → your pipeline → Deployment history** | The deployment appears as it runs, with a note like `run <id>; by <user>; commit <sha>` so you can match it to GitHub |
| **CLI** `status --watch` | Follows the current deployment until it finishes, then prints per-item results |

```bash
dotnet fabric-deploy.dll status --watch                # latest deployment, follow it live
dotnet fabric-deploy.dll status --operation <id>       # any past deployment, item by item
```

## After a deployment

1. **Job summary** (open the run → the job) shows the outcome, route, capacity, approver, preflight checks, every item with its result, the rollback point, validation and step timings.
2. **Artifacts** `report-STAGE` / `report-PROD` (kept 90 days) contain:
   - `report.md`, `report.json`: everything above, plus Fabric's per-item execution plan (Fabric itself keeps that for only 24 hours)
   - `plan.json`: what was approved
   - `snapshot/`: the rollback point
   - `deployment-history.jsonl`: the audit record
3. **Teams/Slack message** for each run (set `FABRIC_DEPLOY_WEBHOOK_URL`). It shows ✅ / ⚠️ / ↩️ / ❌, the item counts, errors and a link to the run. Use `notifications.when: OnFailure` to be told only about problems.
4. **GitHub notifications:** failed workflows email whoever triggered them (Settings → Notifications → Actions).

## Scheduled health check (`fabric-monitor.yml`)

Runs on weekdays (edit the `cron:`) or on demand from **Actions → fabric-monitor**. It posts the last 15 deployments and a health table to the run summary, and **alerts the webhook if it fails**.

| Check | Level | Meaning / what to do |
|---|---|---|
| Capacity | ❌ if not Active or not accessible | The capacity is paused or the workspace lost it. Resume or reassign the capacity. |
| Capacity drift | ⚠️ | The workspace is on a different capacity than the settings say. Run a deployment or `bootstrap` to fix it. |
| Last deployment | ❌ if the last one Failed | Look at that run's report, then fix or roll back. |
| Stuck deployment | ❌ | Running longer than `monitor.timeoutMinutes`. Check the Fabric UI. |
| Drift | ⚠️ | Items in STAGE/PROD that didn't come from the previous stage, meaning someone edited PROD directly. Bring the change back into DEV/Git, or delete it. |
| Pending promotion | ℹ️ | Items in a stage that aren't in the next stage yet |

Locally: `dotnet fabric-deploy.dll health` (exit code 5 = unhealthy, `--fail-on-warning` makes warnings fail too). It writes `health.md` and `health.json`.

## History and audit

```bash
dotnet fabric-deploy.dll history --top 30     # Fabric's own history (kept by Fabric, all stages)
dotnet fabric-deploy.dll history --local      # this machine's audit log, incl. rollbacks, rejections, dry runs
```

For **long-term audit** across all runs, implement `IAuditStore` (for example, writing to an Azure SQL table or a Fabric Lakehouse) and pass it to `new AuditStep(store)`. See [REUSE.md](REUSE.md). Each record has: run ID, outcome, who ran it, the approver, the plan hash, stages, workspaces, capacity, items, operation and deployment IDs, errors, the GitHub run ID and the commit.

## Capacity load

Deployments run on the target capacity. Watch the **Microsoft Fabric Capacity Metrics** app during big PROD releases. Throttled calls (HTTP 429) are retried automatically and appear as `[warn] … returned 429; retry …` in the log.
