# 3 · Rollback options

Fabric deployment pipelines have **no built-in undo**. The framework adds one: before every deployment it saves a **rollback point** (`snapshot/` in the run's report artifact). The rollback point contains:

- the definition of every target item that deployment overwrites (`getDefinition`)
- the list of items that deployment newly created
- the target workspace's capacity before the run

A rollback puts those definitions back (`updateDefinition`), optionally deletes the new items, and moves the workspace back to its previous capacity.

> ⚠️ **Definitions only, not data.** Rows in lakehouse tables and warehouses are not part of a snapshot. See *Option F* for data.

## Which option?

| Situation | Option |
|---|---|
| The deployment itself failed (error, timeout, validation) | **A. Automatic.** It has already happened. |
| The deployment succeeded, but PROD is broken (bad report, wrong logic) and you need it back **now** | **B. Rollback workflow** (GitHub) |
| Same, but you're working from your machine | **C. CLI rollback** |
| The bad change should be permanently undone, and PROD can wait for a normal release | **D. Roll forward with `git revert`** (cleanest history) |
| Only one or two items are wrong | **E. Hotfix / selective redeploy** |
| Data was damaged | **F. Data restore** (Delta time travel / warehouse restore points) |

---

## A. Automatic rollback (default)

If the deploy, monitor or validation step fails, the framework restores the rollback point immediately.

- The job ends with **exit code 4 / outcome `RolledBack`**, and the report has an *↩️ Automatic rollback* section.
- Controlled by `rollback.autoRollbackOnFailure` (default `true`), `rollback.deleteCreatedItems` (default `false`: new items are left in place) and `rollback.restoreCapacity` (default `true`).
- To switch it off for one run: `fabric-deploy deploy … --no-auto-rollback`. The log then prints the exact rollback command to use later.

**What to do next:** read the report's *Errors* section, fix the cause in DEV, and deploy again.

## B. Rollback workflow (GitHub Actions)

Use this to undo a deployment that succeeded.

1. Find the **run ID** of the deployment to undo: **Actions → fabric-deploy →** open the run → the number in the URL `…/actions/runs/`**`1234567890`**.
2. **Actions → fabric-rollback → Run workflow:**
   - `run_id`: `1234567890`
   - `stage`: `PROD` (or `STAGE`)
   - `delete_created`: tick it to also remove items that release added
   - `dry_run`: **leave it ticked the first time**
3. The dry run lists every item it *would* restore or delete. Check it.
4. Run it again with `dry_run` **unticked**. A PROD rollback waits for the same `fabric-prod` reviewers. Approve it.
5. The workflow restores the items, then runs a **health check**. The `rollback-PROD` artifact holds `rollback-report.md` and the audit entry.

**Rules built in:**
- It refuses if the stage now points to a different workspace.
- If **newer** deployments reached that stage after the run you picked, it stops. Rolling back an older snapshot also undoes the newer releases for those items. Either pick the most recent run, or use the CLI with `--force` if that's really what you want.
- Rollback points are kept **90 days** (`retention-days` in `_fabric-stage.yml`).

## C. CLI rollback (from your machine)

```bash
cd out   # the published CLI; export FABRIC_* variables first (see DEPLOY.md)

# latest deployment to PROD recorded in this folder's deployment-output
dotnet fabric-deploy.dll rollback --latest --target PROD --dry-run
dotnet fabric-deploy.dll rollback --latest --target PROD

# a specific rollback point (e.g. from a downloaded report-PROD artifact)
dotnet fabric-deploy.dll rollback --snapshot ./report-PROD/<run-id>/snapshot/snapshot.json --delete-created

# options: --dry-run  --delete-created  --keep-capacity  --yes (no prompt)  --force (older snapshot)
```

Without `--yes` it asks you to type the stage name to confirm.

## D. Roll forward with `git revert` (recommended for bad code)

This keeps Git, DEV, STAGE and PROD consistent, and it goes through the normal approvals.

1. Find the bad commit: `git log --oneline -- fabric/`
2. Revert it: `git revert <commit-sha>` → `git push`. Or use a pull request with GitHub's **Revert** button.
3. The push starts **fabric-deploy**: DEV is updated from Git → STAGE → approve → PROD.
4. If you had used option B/C to get PROD back quickly, do this afterwards so DEV and STAGE match again.

## E. Hotfix / selective redeploy

1. Fix the broken item in DEV (or revert just that file in Git).
2. **Actions → fabric-deploy → Run workflow** → `include_names` = `Sales Report` → approve PROD.
3. Only that item is backed up, deployed and validated.

## F. Data (not covered by snapshots)

- **Lakehouse Delta tables:** in a notebook, run `DESCRIBE HISTORY my_table` to find the version, then `RESTORE TABLE my_table TO VERSION AS OF <n>` (or `TO TIMESTAMP AS OF '…'`).
- **Warehouse:** Warehouse → **Restore points → restore in place** to a system or user-defined restore point. Create a user-defined restore point before risky releases.
- **Semantic models:** refresh after a rollback so the data matches the restored definition.

## What can't be restored

- Item types listed in `rollback.skipItemTypes`, and items whose definition Fabric can't export. The report lists them as ⚠️ *not backed up* and the rollback lists them as *not restorable*. For these, use option D or E.
- Set `rollback.failIfSnapshotIncomplete: true` to **block** a deployment when anything can't be backed up.
- Permissions, schedules and item-level settings outside the definition aren't part of the snapshot.
