# Azure hands-on lab

Learn the Azure side of Merger Sync Hub by doing it yourself, with the repository checking your work. The scripts and
templates in [`infra/`](../infra) can do all of this in seconds; the point here is to know what they do, where it shows
in the portal, and what breaks when a step is missed.

| Lab | What you do | Costs | Needs | Time |
|---|---|---|---|---|
| [1. Build the Entra set-up by hand](#lab-1-build-the-entra-set-up-by-hand) | Two app registrations, roles, a scope, consent, groups, an assignment, in the portal | Free | The tenant's Global Administrator | 45 min |
| [2. Check it with the script](#lab-2-check-your-work) | Run `-Verify` against your hand-built objects; fix what fails | Free | Azure CLI | 10 min |
| [3. Point the Support API at Entra](#lab-3-point-the-support-api-at-entra) | Swap the mock directory for Entra with two settings; prove 200, 401 and 403 | Free | Local SQL Server and the Support API | 30 min |
| [4. Azure resources](#lab-4-azure-resources) | Preview with `what-if`, deploy the first pass, explore it, delete it | Pennies (budget first) | An **enabled** subscription | 1 hour |
| [5. Clean up](#lab-5-clean-up) | Remove the lab objects | Free | | 10 min |

Each lab ends with **Be able to explain**: the questions an interviewer is likely to ask about it.

**Accounts.** In this tenant the Global Administrator is your personal Microsoft account (it shows as `#EXT#` in the
user list); the account you use for `az login` is an ordinary member. Use the personal account in the portal for anything
an admin must do (consent, logs, role assignments). If the portal says *You don't have access*, check the account and
directory under your avatar, top right.

---

## Lab 1: build the Entra set-up by hand

You'll rebuild what `entra-setup.ps1` made, under the name **SyncHubLab**, so the two sets sit side by side and you can
compare them. Use [entra.microsoft.com](https://entra.microsoft.com), signed in as the Global Administrator.

Use these names exactly: Lab 2's check looks them up by name.

### 1.1 The API's app registration

1. **App registrations → New registration.**
   - Name: `SyncHubLab Support API`
   - Supported account types: *Accounts in this organizational directory only* (single tenant)
   - Redirect URI: leave empty (an API never signs anyone in itself)
2. Note the **Application (client) ID** and **Directory (tenant) ID** on the Overview page.
3. **Expose an API.**
   - *Application ID URI* → **Add** → accept `api://<application id>` → Save.
   - **Add a scope**: name `access_as_user`; *Who can consent*: Admins and users; fill the four display texts; **Enabled**.
4. **App roles → Create app role**, four times. *Allowed member types*: **Users/Groups**; tick *Do you want to enable this app role?*

   | Display name | Value (exactly) | Description |
   |---|---|---|
   | Viewer | `SyncHub.Viewer` | Read the support dashboard |
   | Support Analyst | `SyncHub.Support` | Fix, replay and ignore integration exceptions |
   | Data Steward | `SyncHub.DataSteward` | Decide duplicate clients and maintain practice-area mappings |
   | Administrator | `SyncHub.Admin` | Everything in the support dashboard |

   The values, not the display names, go into the token's `roles` claim, and the Support API checks those exact strings.
5. **Manifest.** Find `"requestedAccessTokenVersion"` (under `"api"`) and set it to `2`. Save.
   Without this you get v1 tokens, whose issuer is `https://sts.windows.net/<tenant>/` instead of
   `https://login.microsoftonline.com/<tenant>/v2.0`, and the Support API rejects them.
6. **Expose an API → Add a client application**: client ID `04b07795-8ddb-461a-bbee-02f9e1bf7b46` (the Azure CLI, a
   fixed Microsoft id), tick the `access_as_user` scope. This lets `az account get-access-token` get a token for your API
   without a browser sign-in. It's a test convenience: don't do it in production.

### 1.2 Its enterprise application

The portal created this when you registered the app (App registration → Overview → *Managed application in local directory*).

1. **Properties → Assignment required? → Yes** → Save. Now nobody gets a token unless they're assigned a role.
2. **Users and groups → Add user/group**: select **the account you use for `az login`**, role **Administrator** → Assign.

### 1.3 The dashboard's app registration

1. **New registration**: `SyncHubLab Support Dashboard`, single tenant,
   Redirect URI platform **Single-page application (SPA)**, `http://localhost:5173`.
2. **API permissions → Add a permission → APIs my organization uses** (or *My APIs*) → `SyncHubLab Support API` →
   *Delegated permissions* → tick `access_as_user` → Add.
3. **Grant admin consent for …** → Yes. The status column turns to a green tick. (The button stays; that's normal.)

### 1.4 Groups

**Groups → New group**, four times: *Group type* **Security**, *Membership type* **Assigned**, no members:
`SyncHubLab Viewers`, `SyncHubLab Support Analysts`, `SyncHubLab Data Stewards`, `SyncHubLab Admins`.

Assigning these groups to the app roles needs Entra ID P1. On the free tier you assign people directly, as in 1.2.
With P1 you'd go to Enterprise application → Users and groups and add each group with its role, and never assign people again.

### Be able to explain

- **App registration vs enterprise application:** the app's definition (owned by its developer, one per app) vs its
  instance in a tenant (owned by that tenant's admins), where assignments, consent and sign-in logs live.
- **Scope vs app role:** a scope (`access_as_user`) is what a *client app* may do on a user's behalf, granted by consent;
  an app role (`SyncHub.Support`) is what a *user* may do, granted by an admin's assignment. A token carries both
  (`scp` and `roles`); the API authorises on roles.
- **Why assignment required:** without it, anyone in the tenant can get a token with no roles. With it, Entra refuses
  at sign-in, so "no role, no token" (error AADSTS50105).
- **Admin consent:** agreeing, once for everyone, that the dashboard may call the API as the signed-in user, so users
  aren't prompted. Consent covers only what the app asked for (here, one scope on our own API, nothing in Microsoft Graph).

---

## Lab 2: check your work

The setup script's `-Verify` mode changes nothing. It reads every object back and checks it, matching roles and the scope by
value, so it can check objects you built by hand. Then it gets a real token and decodes it locally (it never prints it).

```powershell
az login --allow-no-subscriptions
```
```powershell
.\infra\entra-setup.ps1 -Verify -Prefix SyncHubLab -AllowAzureCli
```

Aim for **All checks passed**. Entra takes a minute or so to replicate, so run it again if something you've just made isn't found.

| If this fails | It means | Fix |
|---|---|---|
| issues v2 access tokens | The manifest still has `requestedAccessTokenVersion` null or 1 | 1.1 step 5 |
| app role … | A role's **value** is misspelt, disabled, or allowed for applications only | 1.1 step 4 |
| Azure CLI pre-authorised | No authorised client application, or the scope isn't ticked on it | 1.1 step 6 |
| assignment required | Properties still says No | 1.2 step 1 |
| you have SyncHub.Admin | Assigned to a different account (the portal's, not the CLI's), or a different role | 1.2 step 2 |
| admin consent granted | Permission added but consent not granted | 1.3 step 3 |
| Azure CLI can get a token | Usually the pre-authorisation. Read the AADSTS code: 65001 is consent, 50105 is no role assignment | 1.1 step 6, 1.2 |
| issuer is this tenant's v2 endpoint | v1 token | 1.1 step 5 |

To see the error yourself, run the token command it uses:

```powershell
az account get-access-token --scope "api://<API application id>/access_as_user" --query expiresOn
```

Then compare with the scripted set: `.\infra\entra-setup.ps1 -Verify -AllowAzureCli` checks the `SyncHub` objects the same way.

### Be able to explain

- **How you'd test identity configuration:** read it back and assert on it, then prove it end to end with a real
  token; a dry run only shows intent.
- **What's in an access token:** `aud` (which API it's for), `iss` (who issued it), `exp`, `scp`, `roles`, `oid`/`sub`
  (who), `tid` (which tenant). Decode one locally (the middle part is base64url JSON, which is how the script's
  `ConvertFrom-JwtPayload` reads it) rather than pasting a live token into a website.

---

## Lab 3: point the Support API at Entra

The Support API validates standard OIDC tokens. To trust Entra instead of the mock directory, two settings change and
no code does. It reads `name`, `preferred_username` and `roles`, and Entra v2 tokens carry all three.

While it's switched over, the dashboard's demo sign-ins (`it.admin` and the others) stop working, because their tokens come from the mock
directory. Switch back at the end.

1. Start SQL Server (`docker compose up -d`) if it isn't running.
2. Find your tenant ID and the API's application ID (from Lab 1, or the scripted `SyncHub Support API`):
   ```powershell
   az account show --query tenantId -o tsv
   ```
3. Point the Support API at Entra (user secrets: on your machine only, never in Git):
   ```powershell
   dotnet user-secrets set "Auth:Authority" "https://login.microsoftonline.com/<tenant id>/v2.0" --project src/Services/SyncHub.SupportApi
   ```
   ```powershell
   dotnet user-secrets set "Auth:Audience" "<API application id>" --project src/Services/SyncHub.SupportApi
   ```
4. Run it:
   ```powershell
   dotnet run --project src/Services/SyncHub.SupportApi
   ```
5. In a second terminal, get a token and call the API:
   ```powershell
   $token = az account get-access-token --scope "api://<API application id>/access_as_user" --query accessToken -o tsv
   Invoke-RestMethod http://localhost:5081/api/me -Headers @{ Authorization = "Bearer $token" }
   ```
   **Expect 200** with your name, username and `roles: SyncHub.Admin`.
6. **No token → 401:**
   ```powershell
   try { Invoke-RestMethod http://localhost:5081/api/me } catch { $_.Exception.Response.StatusCode }
   ```
7. **Wrong role → 403.** In the portal, change your assignment on the API's enterprise application from Administrator to
   **Viewer**. The CLI caches tokens, so sign in again to get a fresh one (`az logout`, then
   `az login --allow-no-subscriptions`), get a new `$token`, then:
   ```powershell
   Invoke-RestMethod http://localhost:5081/api/me -Headers @{ Authorization = "Bearer $token" }      # 200, roles: SyncHub.Viewer
   try { Invoke-RestMethod -Method Post http://localhost:5081/api/deadletters/999999999/replay -Headers @{ Authorization = "Bearer $token" } } catch { $_.Exception.Response.StatusCode }   # Forbidden
   ```
   Put yourself back to Administrator (and sign in again) and the same replay call returns **409 Conflict** instead:
   the handler ran, found no dead letter with status New to replay, and said so. As a Viewer it never ran, because
   authorisation is checked before the handler looks anything up.
8. Compare the two discovery documents. They have the same shape, which is why no code changed:
   `http://localhost:5090/.well-known/openid-configuration` (the mock, when it's running) and
   `https://login.microsoftonline.com/<tenant id>/v2.0/.well-known/openid-configuration`.
9. **Switch back** to the mock directory:
   ```powershell
   dotnet user-secrets remove "Auth:Authority" --project src/Services/SyncHub.SupportApi
   ```
   ```powershell
   dotnet user-secrets remove "Auth:Audience" --project src/Services/SyncHub.SupportApi
   ```

### Be able to explain

- **How the API trusts a token it never issued:** it downloads the issuer's discovery document and public signing keys
  (JWKS), then checks signature, issuer, audience and expiry on every request. It holds no secret.
- **401 vs 403:** 401 is *who are you?* (no token, or an invalid or expired one); 403 is *I know who you are, and you can't do that*.
- **A user "can't get in" (third-line support):** check their role assignment, then the sign-in log entry and its AADSTS
  code, then consent and Conditional Access. If they get in but see 403, compare the `roles` in their token with the
  endpoint's policy.
- **Still to do for production:** MSAL in the React apps (sign-in in the browser, tokens refreshed silently), and a
  revocation story: Entra tokens last about an hour, so disabling a user takes effect at their next token.

---

## Lab 4: Azure resources

Needs an **enabled** subscription. The design is in [azure-architecture.md](azure-architecture.md) and the full
deployment steps (images, databases, second pass) are in [infra/README.md](../infra/README.md). This lab covers the first pass only,
which creates the platform but no apps.

**Before anything costs money:**

1. Re-enable the subscription (portal → Subscriptions). Check you're on the right one:
   ```powershell
   az login
   ```
   ```powershell
   az account show --query "{name:name, state:state}"
   ```
2. **Estimate the cost** with the [Azure pricing calculator](https://azure.microsoft.com/pricing/calculator/) for the resources
   in [the cost table](azure-architecture.md#cost), or ask Claude to produce one from the Retail Prices API. The template
   also creates a monthly budget alert on the resource group (default 20, in the subscription's currency).

**Preview, deploy, explore, delete:**

3. Set the two parameters the template reads from the environment (your object ID becomes the Azure SQL Entra admin):
   ```powershell
   $env:SQL_ADMIN_OBJECT_ID = az ad signed-in-user show --query id -o tsv
   $env:SQL_ADMIN_NAME = az ad signed-in-user show --query userPrincipalName -o tsv
   ```
4. **Preview** (creates nothing; each resource shows as Create, Modify, Delete or NoChange):
   ```powershell
   az deployment sub what-if --location uksouth --template-file infra/main.bicep --parameters infra/main.bicepparam
   ```
5. **Deploy** pass 1 (`DEPLOY_APPS` unset means no Container Apps yet):
   ```powershell
   az deployment sub create --location uksouth --template-file infra/main.bicep --parameters infra/main.bicepparam
   ```
6. **Run the preview again.** It should show NoChange for everything: the template is idempotent, like the scripts.
7. **Explore `rg-synchub` in the portal:**

   | Resource | Look at | Why it's set that way |
   |---|---|---|
   | SQL server | *Microsoft Entra ID*: **Support only Microsoft Entra authentication** ticked | No SQL logins, so no passwords |
   | SQL databases | *Compute + storage*: serverless, free offer, auto-pause | Pay nothing while idle |
   | Key Vault | *Access configuration*: **Azure role-based access control**; *Secrets*: `firmb-api-key` | Access by role, not vault access policies |
   | Container registry | *Access keys*: **Admin user** off | Pulls use managed identities |
   | Managed identities (×3) | *Azure role assignments* | Least privilege: AcrPull, Key Vault Secrets User, nothing else |
   | Resource group | *Access control (IAM) → Role assignments*; *Cost Management → Budgets* | Who can do what; the alert |
   | Log Analytics | *Usage and estimated costs*: daily cap | 1 GB a day, so a log storm can't run up a bill |

8. **Make a change by hand and watch the preview catch it.** For example, turn the registry's admin user on in the
   portal, then run the `what-if` again: it shows Modify (`adminUserEnabled: true → false`). Deploying again puts it back.
   This is drift, and declarative templates correct it.
9. **Delete everything:**
   ```powershell
   az group delete --name rg-synchub --yes
   ```
   Key Vault uses soft delete: its name stays reserved for 90 days unless you purge it
   (`az keyvault list-deleted`, then `az keyvault purge --name <name>`).

### Be able to explain

- **Declarative vs imperative:** Bicep describes the end state and Azure Resource Manager works out the changes (what-if,
  idempotent, drift corrected). The Entra script is imperative: it checks before each change, so it's re-runnable,
  but it has to code its own dry run and verify.
- **Why subscription scope:** the template creates its own resource group, so `what-if` works before anything exists, and
  deleting one group removes everything.
- **Managed identity:** an identity Azure manages for a resource, with no secret to store or rotate. The app asks the
  platform for a token, so the connection string contains `Authentication=Active Directory Managed Identity` and no password.
- **What you'd add for a real law firm:** private endpoints and a VNet, Front Door with a WAF, failover groups for SQL,
  Azure Policy, Defender for Cloud ([Demo vs production](azure-architecture.md#demo-vs-production)).

---

## Lab 5: clean up

- **Lab 1 objects:** delete the two `SyncHubLab` app registrations (their enterprise applications go with them) and the
  four `SyncHubLab` groups. Deleted app registrations sit under *App registrations → Deleted applications* for 30 days,
  where you can restore them or delete them permanently. Deleted **security** groups can't be restored; only Microsoft 365 groups can.
- **The scripted `SyncHub` objects:** keep them for the interview demo. They cost nothing.
- **Lab 3:** make sure the two `Auth:*` user secrets are removed (step 9), or the dashboard's demo sign-in stays broken.
- **Lab 4:** `az group delete --name rg-synchub`, then check **Cost Management** the next day for any leftover charges.
