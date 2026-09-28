# 1 · One-time setup

Do this once per tenant/repository. After that, every deployment is just a `git push` (see [DEPLOY.md](DEPLOY.md)).

## Step 1 — Create the service principal (Microsoft Entra ID)

1. Azure portal → **Microsoft Entra ID → App registrations → New registration**, for example `sp-fabric-deploy`.
2. Copy the **Application (client) ID** and the **Directory (tenant) ID**.
3. **Certificates & secrets → New client secret** → copy the **Value** now (it is shown only once). Set a reminder before it expires.
4. **Groups → New group** `sg-fabric-automation` (Security) → add `sp-fabric-deploy` as a member.

## Step 2 — Allow the service principal in Fabric

In the **Fabric admin portal** (a Fabric admin must do this):

1. **Tenant settings → Developer settings → Service principals can use Fabric APIs** → *Enabled* for `sg-fabric-automation`.
2. **Capacity settings →** each capacity used by DEV, STAGE and PROD **→ Contributor permissions →** add `sg-fabric-automation`. This lets the framework put workspaces on that capacity.
3. Write down each capacity's **ID**. It's in the capacity settings URL, or run `fabric-deploy capacities` after Step 5.

## Step 3 — Prepare the DEV workspace

1. Open the DEV workspace → **Manage access → Add people or groups** → `sp-fabric-deploy` as **Admin**.
2. *(Recommended)* **Workspace settings → Git integration** → connect DEV to this repository, branch `main`, folder `/fabric`. Your Fabric items (notebooks, reports, models…) are then versioned next to the framework, and the pipeline can pull the latest commit into DEV.

   > The pipeline's optional `git-sync` step calls Fabric's Git "update from Git" API as the service principal. Service-principal support for the Git APIs depends on your Git provider and on how the Git connection was set up, so check Microsoft's current Git integration docs for your provider. If the service principal can't sync in your tenant, leave `FABRIC_GIT_SYNC` unset and update DEV in the Fabric UI instead (**Source control → Update all**) before running the workflow.

You do **not** need to create the STAGE and PROD workspaces. The first deployment creates them on their capacities.

## Step 4 — Create the GitHub repository

1. Unzip the framework and push it:
   ```bash
   cd FabricDeploymentFramework
   git init && git add . && git commit -m "Fabric deployment framework"
   git branch -M main
   git remote add origin https://github.com/<org>/<repo>.git
   git push -u origin main
   ```
2. *(Optional)* **Settings → General → Template repository** ✔. New solutions can then start from it with *Use this template* (see [REUSE.md](REUSE.md)).

## Step 5 — Fill in `src/Fabric.Deployment.Cli/deploysettings.json`

| Setting | Set it to |
|---|---|
| `pipeline.displayName` | Your pipeline name, for example `Sales Analytics` |
| `pipeline.stages[].name` | `DEV`, `STAGE`, `PROD`. These must match the stage names the workflows use. |
| `pipeline.stages[].workspaceName` | `Sales-DEV` (must match the existing DEV workspace), `Sales-STAGE`, `Sales-PROD` |
| `pipeline.stages[].capacityId` | The capacity ID for each stage (from Step 2.3) |
| `pipeline.stages[PROD].requiresApproval` | `true` |
| `deployment.items.excludeNames` | Items that must never leave DEV, for example `Scratch*` |
| `rollback.*` | Keep the defaults. Set `failIfSnapshotIncomplete: true` if PROD must always be restorable. |
| `notifications.format` | `Teams` or `Slack` |

Leave `tenantId` and `clientId` as placeholders: the GitHub secrets override them. **Never put the secret in this file.** Commit and push.

## Step 6 — GitHub secrets, variables and environments

**Settings → Secrets and variables → Actions:**

| Type | Name | Value |
|---|---|---|
| Secret | `FABRIC_TENANT_ID` | Tenant ID |
| Secret | `FABRIC_CLIENT_ID` | Application (client) ID |
| Secret | `FABRIC_CLIENT_SECRET` | Client secret value |
| Secret *(optional)* | `FABRIC_DEPLOY_WEBHOOK_URL` | Teams: channel → **Workflows → "Post to a channel when a webhook request is received"** → copy the URL. Slack: Incoming Webhook URL. |
| Variable *(optional)* | `FABRIC_GIT_SYNC` | `true` if you did Step 3.2 |

**Settings → Environments:**

| Environment | Protection |
|---|---|
| `fabric-stage` | None, or a deployment-branch rule for `main` |
| `fabric-prod` | **Required reviewers** = your approvers, **Prevent self-review** ✔, deployment branches = `main` |

> For more isolation, put a *different* service principal's secrets on each environment (environment secrets override repository secrets).

## Step 7 — First run

1. **Actions → fabric-deploy → Run workflow** (branch `main`) → keep the defaults → **Run**.
2. It will: build → bootstrap (create the pipeline and assign DEV) → deploy DEV → STAGE (creating `Sales-STAGE` on its capacity) → plan PROD → **wait for approval** → deploy STAGE → PROD (creating `Sales-PROD`).
3. Open the run's **Summary**. Each job shows its report, and **Artifacts** holds `report-STAGE` and `report-PROD`.
4. In Fabric, open **Deployment pipelines → Sales Analytics** to see the three stages and their workspaces.
5. Run **Actions → fabric-monitor → Run workflow** once to confirm the health check is green.

Setup is done. From now on, follow [DEPLOY.md](DEPLOY.md).
