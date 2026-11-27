# Azure infrastructure (Bicep)

Templates for running Merger Sync Hub in Azure. The design and its reasoning are in
[docs/azure-architecture.md](../docs/azure-architecture.md).

| File | What it creates |
|---|---|
| `main.bicep` | Subscription scope: the resource group `rg-synchub`, then everything in it via `resources.bicep` |
| `resources.bicep` | Log Analytics + Application Insights, three managed identities, Container Registry, Key Vault, Azure SQL (Entra-only, serverless free offer) with three databases, Container Apps environment and six apps, a monthly budget |
| `main.bicepparam` | Parameters. The SQL admin comes from environment variables, so no personal id is committed |

Nothing here stores a password: Azure SQL accepts Entra sign-in only, the apps use managed identities, the registry has no
admin user, and the Firm B API key and token signing key live in Key Vault.

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
