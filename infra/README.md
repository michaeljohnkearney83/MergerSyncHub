# Azure infrastructure (Bicep)

Templates for running Merger Sync Hub in Azure. The design and its reasoning are in
[docs/azure-architecture.md](../docs/azure-architecture.md).

| File | What it creates |
|---|---|
| `main.bicep` | Subscription scope: the resource group `rg-synchub`, then everything in it via `resources.bicep` |
| `resources.bicep` | Log Analytics + Application Insights, three managed identities, Container Registry, Key Vault, Azure SQL (Entra-only, serverless free offer) with three databases, Container Apps environment and six apps, a monthly budget |
| `entra-setup.ps1` | Microsoft Entra ID (tenant level, free, no subscription needed): API and dashboard app registrations, four app roles, four groups, role assignments |
| `main.bicepparam` | Parameters. The SQL admin comes from environment variables, so no personal id is committed |

Nothing here stores a password: Azure SQL accepts Entra sign-in only, the apps use managed identities, the registry has no
admin user, and the Firm B API key and token signing key live in Key Vault.

## Entra ID: replacing the mock directory (free)

The mock directory imitates Entra: groups grant app roles, and the token carries them. `entra-setup.ps1` sets up the real thing:

| Creates | Detail |
|---|---|
| App registration **SyncHub Support API** | Exposes `api://<appId>/access_as_user` and four app roles (`SyncHub.Viewer`, `.Support`, `.DataSteward`, `.Admin`); v2 tokens; its enterprise app **requires assignment**, so no role means no token |
| App registration **SyncHub Support Dashboard** | Single-page app (MSAL) with redirect URIs; admin consent to call the API |
| Groups | `SyncHub Viewers`, `SyncHub Support Analysts`, `SyncHub Data Stewards`, `SyncHub Admins` |
| Assignments | Groups to roles with `-AssignGroups` (**needs Entra ID P1**); otherwise just you, as Admin (works on the free tier) |
| Azure CLI pre-authorisation | Only with `-AllowAzureCli`: lets `az account get-access-token` get a test token for the API without a browser sign-in. Assignment is still required. Running without the switch removes it, so leave it off in production |

```powershell
az login --allow-no-subscriptions                      # tenant-level sign-in; works without an enabled subscription
.\infra\entra-setup.ps1 -DryRun -AllowAzureCli         # looks everything up, changes nothing, prints the writes it would send
.\infra\entra-setup.ps1 -AllowAzureCli                 # creates or updates; safe to re-run (a second run creates nothing)
.\infra\entra-setup.ps1 -Verify -AllowAzureCli         # read-only: checks every object and setting, then gets a real token
```

`-Verify` reads everything back and checks it against what the script sets up: role ids and values, the scope, assignment
required, redirect URIs, admin consent, groups and assignments. With `-AllowAzureCli` it also gets an access token and checks
(decoded locally, never printed) that its `aud`, `iss` and `roles` are what the Support API expects. It exits 1 if anything
fails. Entra takes a minute or so to replicate, so a `-Verify` straight after creating objects can fail: run it again.
Pass the same switches (`-AssignGroups`, `-SupportWebUrl`) as the setup run.

To learn it by hand instead, [docs/azure-lab.md](../docs/azure-lab.md) walks through building the same objects in the
portal under `-Prefix SyncHubLab`, then checking them with `-Verify -Prefix SyncHubLab`. `-Verify` matches roles and the
scope by value, because the portal generates its own ids. `-Prefix` only changes display names: role values stay
`SyncHub.*`, because the Support API checks those exact strings.

**Admin consent needs an admin role.** An ordinary member can register apps and create groups, so the rest of the script
runs, but granting tenant-wide consent needs Cloud Application Administrator or Global Administrator. Without one, the script
warns and carries on, and an admin grants it in the portal: Enterprise applications → SyncHub Support Dashboard →
Permissions → **Grant admin consent**. `-Verify` reports it until then. In a personal tenant the Global Administrator is
usually the Microsoft account the tenant was created from, which is not necessarily the account you used for `az login`.

It prints the settings the apps need (`Auth:Authority`, `Auth:Audience`, the dashboard's client id and scope). Using them
means switching the Support API to Entra (Authority and Audience settings, no code change) and adding MSAL to the dashboard.

## Preview (free, creates nothing)

```powershell
$env:SQL_ADMIN_OBJECT_ID = az ad signed-in-user show --query id -o tsv
$env:SQL_ADMIN_NAME      = az ad signed-in-user show --query userPrincipalName -o tsv
az bicep build --file infra/main.bicep
az deployment sub what-if --location uksouth --template-file infra/main.bicep --parameters infra/main.bicepparam
```

## Deploy (costs money: check the estimate in the design document first)

1. **Pass 1, infrastructure** (no apps yet: there are no images):
   `az deployment sub create --location uksouth --template-file infra/main.bicep --parameters infra/main.bicepparam`
2. **Images**, built by the registry itself (`az acr build`), named after the compose services
   (`sync-worker`, `support-api`, `directory-api`, `firmb-api`, `support-web`, `directory-web`). The two web images need
   the Azure URLs as build args (`VITE_API_URL`, `VITE_DIRECTORY_URL`), taken from the deployment's `urls` output.
3. **Signing key**: generate an RSA key and store it in Key Vault as `directory-signing-key`.
4. **Databases**: as the Entra admin, run scripts 02–06 against their databases without the `CREATE DATABASE`,
   `ALTER DATABASE` and `USE` lines, then create a contained user for each managed identity
   (`CREATE USER [id-synchub-worker] FROM EXTERNAL PROVIDER`) with only the rights it needs.
5. **Pass 2, apps**: `$env:DEPLOY_APPS = 'true'`, then repeat step 1.
6. **Tear down** after the demo: `az group delete --name rg-synchub` (and purge the soft-deleted Key Vault).

Steps 2–4 will become a script once the deployment has been run end to end.

## Status

- `az bicep build`: compiles with no errors or warnings (27 Nov 2026).
- Azure validation (`what-if` / `validate`): blocked because the subscription is disabled; to be re-run once it is re-enabled.
