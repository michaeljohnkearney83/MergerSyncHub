# Merger Sync Hub

A working model of a problem every merged law firm faces: **two practice management systems, one firm.**

- **Firm A** is a legacy UK system: an older SQL Server database with messy data, local timestamps and no foreign keys.
- **Firm B** is a US system exposed as a REST API that pages its data and sometimes fails or times out.

The **Sync Hub** pulls clients and matters from both and validates and standardises them. It merges them into
one register, flags clients that look like duplicates (within a firm or across both firms) and reports
on the result in **SSRS** and **Power BI**. A **React support dashboard** lets a third-line analyst see
failures, correct bad records, replay them, review duplicates and fix mappings, without anyone touching the database.

> Built as a personal project to explore the integration challenges of a law-firm merger.
> All data is synthetic.

---

## Architecture

```mermaid
flowchart LR
    subgraph Sources
        A[(Firm A legacy DB<br/>SQL Server)]
        B[Firm B REST API<br/>.NET minimal API<br/>with fault injection]
    end

    subgraph Worker["SyncHub.Worker (.NET 10 BackgroundService)"]
        CA[FirmA connector<br/>streaming SQL]
        CB[FirmB connector<br/>paging + retry/backoff/<br/>circuit breaker]
        M[Mappers + validation<br/>SyncHub.Core]
        D[Duplicate detection<br/>name matching]
    end

    subgraph Hub["SyncHub database"]
        STG[stg.*<br/>staging + raw payload]
        CORE[core.*<br/>combined register]
        OPS[ops.*<br/>runs, watermarks,<br/>dead letters]
        RPT[rpt.*<br/>star schema]
    end

    A --> CA --> M
    B --> CB --> M
    M -- valid --> STG -- set-based MERGE --> CORE
    M -- invalid --> OPS
    CORE -- orphans --> OPS
    CORE --> D --> CORE
    CORE --> RPT

    API[SyncHub.SupportApi] --> OPS & CORE
    UI[React dashboard] --> API
    DIR[CorpDirectory.Api<br/>mock AD / Entra ID] -. signed tokens + roles .-> UI
    DIR -. public signing keys .-> API
    ADM[Directory admin centre<br/>React] --> DIR
    RPT --> SSRS[SSRS paginated report]
    RPT --> PBI[Power BI + RLS]
```

**One sync cycle (every 60 seconds by default):**

1. **Replay** anything queued from the dashboard.
2. For each source: **read changes since the watermark → map and validate → bulk stage → MERGE → move the watermark**.
3. **Detect** possible duplicate clients.
4. **Refresh** the reporting star schema (every 5 minutes by default).

## What's in the repo

| Path | What it is |
|---|---|
| `database/` | SQL scripts: both databases, schemas, stored procedures, reporting layer, plus `setup.ps1` |
| `database/perf/` | Before-and-after indexing demo on the legacy extract query |
| `database/demo/` | Script that simulates users editing records in Firm A |
| `src/SyncHub.Core` | Pure logic: models, mappers, validation, name normalisation, duplicate matching |
| `src/SyncHub.Worker` | The integration engine (.NET 10 worker service) |
| `src/SyncHub.SupportApi` | Minimal API behind the dashboard |
| `src/FirmB.MockApi` | The "other firm's" API, with random outages, slow responses and bad records |
| `src/CorpDirectory.Api` | Mock corporate directory (stands in for Active Directory / Entra ID): sign-in, roles, joiners and leavers |
| `directory-admin/` | React admin centre for the directory: add new starters, manage access, disable leavers, audit log |
| `src/SyncHub.Observability` | Shared OpenTelemetry set-up: every service sends metrics and logs to the collector |
| `src/SyncHub.Tests` | xUnit tests for mapping, validation, time zones and matching |
| `dashboard/` | React + TypeScript (Vite) support dashboard |
| `reports/ssrs/` | Step-by-step build of the SSRS exceptions report |
| `reports/powerbi/` | Power BI model, DAX measures and row-level security guide |
| `monitoring/` | OpenTelemetry Collector, Prometheus (with alert rules), Loki and Grafana (provisioned dashboard) config |
| `docs/interview-guide.md` | How this project maps to the interview questions |
| `.claude/agents/sql-reviewer.md` | Claude Code subagent that reviews the SQL scripts and repository queries (ask Claude to "use the sql-reviewer agent") |
| `.claude/agents/observability-advisor.md` | Claude Code subagent that suggests where to add logs, metrics, alerts and dashboard panels |
| `.claude/agents/coding-standards.md` | Claude Code subagent that checks a diff against the project's coding standards (PASS / FAIL) |
| `.githooks/pre-push` | Runs the coding-standards agent before any push to a `feature/*` branch |
| `.githooks/post-commit` | Merges the latest `main` into a `feature/*` branch after each commit |

---

## Getting started

### Prerequisites

- .NET 10 SDK
- Node.js 20+
- SQL Server 2019+ (local install, or Docker using the included compose file: see step 1)
- `sqlcmd` (installed with SSMS or the SQL Server command-line tools)
- Optional: Power BI Desktop, Power BI Report Builder / SSRS

### 1. Create the databases

**Using Docker?** Start SQL Server first. Copy `.env.example` to `.env` (Git ignores it), set your own
`sa` password, then:

```powershell
docker compose up -d
```

Then create the databases:

```powershell
cd database
.\setup.ps1                                   # local SQL Server, Windows auth
# .\setup.ps1 -Server "localhost\SQLEXPRESS"  # named instance
# .\setup.ps1 -Server "localhost,14333" -User sa -Password '<your sa password>'   # Docker
```

Or open the six numbered scripts in SSMS and run them in order. Firm A ends up with about 375 clients and 75,000 matters.

**Using Docker or SQL authentication?** Store your connection strings as .NET user secrets (outside the project,
so no password is ever committed):

```powershell
.\set-local-secrets.ps1 -Password '<your sa password>'
```

The committed `appsettings.json` files contain only password-free defaults (Windows authentication).

### 2. Run the tests

```powershell
cd src
dotnet test
```

### 3. Start everything (six terminals)

Start the directory first: the Support API downloads its signing keys from it.

```powershell
dotnet run --project src/CorpDirectory.Api    # http://localhost:5090  (seeds demo accounts on first run)
dotnet run --project src/FirmB.MockApi        # http://localhost:5070
dotnet run --project src/SyncHub.Worker       # starts syncing immediately
dotnet run --project src/SyncHub.SupportApi   # http://localhost:5081/swagger
cd dashboard; npm install; npm run dev        # http://localhost:5173  (SyncHub dashboard)
cd directory-admin; npm install; npm run dev  # http://localhost:5174  (Directory admin centre)
```

### 4. Sign in

Demo accounts are created on the directory's first start. They all use the password **`Welcome2SyncHub!`**.
You can sign in with just the part before the `@`.

| Username | Role | Can do |
|---|---|---|
| `it.admin` | Directory admin + SyncHub admin | Everything in both apps |
| `alex.morgan` | SyncHub Support Analyst | Fix, replay and ignore exceptions |
| `priya.shah` | SyncHub Data Steward | Decide duplicates, map practice areas |
| `tom.reid` | SyncHub Viewer | Read-only |
| `jordan.lee` | (no groups) | Gets "not given access to this application" |
| `sam.carter` | Viewer, **disabled** (a leaver) | Gets "account disabled" |

Access works as in Entra ID: users are put in **security groups**, each group grants one **app role**, and the
sign-in token for an app carries only that app's roles. The Support API checks the token's signature
against the directory's published keys and enforces the roles. Five wrong passwords lock an account for 5 minutes.

The first cycle does a full load (75k Firm A matters take a few seconds with SqlBulkCopy). Later cycles
are incremental. In the worker console you'll see Firm B's injected 503s and timeouts being retried
(`Polly` warnings) without the run failing.

### 5. Git hooks for feature branches (optional)

Two Git hooks live in `.githooks/`. Hooks aren't copied by `git clone`, so turn them on once per clone:

```powershell
git config core.hooksPath .githooks
```

**post-commit: keep feature branches up to date with main.** After each commit on a `feature/*` branch it
fetches `origin/main` (or uses the local `main` if there's no remote) and merges it in, so conflicts show up
while the change is small.

- Nothing happens if `main` has no new commits, or during a rebase, cherry-pick or merge.
- On a conflict it aborts the merge and lists the clashing files; your commit is untouched. Run
  `git merge origin/main` when you're ready to resolve.
- Skip it once with `SKIP_AUTO_MERGE=1 git commit ...`. `AUTO_MERGE_BASE` changes the source branch.

**pre-push: coding standards.** Runs the `coding-standards` Claude agent over the commits you are pushing to
any `feature/*` branch. It needs the `claude` CLI on your PATH.

- Blocking findings (for example SQL built from input, a secret, a non-re-runnable script, a run/config change
  without a README update) stop the push; advisory ones are printed and the push continues.
- Skip it once with `git push --no-verify`.
- If the `claude` CLI is missing or fails, the push goes ahead with a warning. Set `STANDARDS_FAIL_CLOSED=1` to
  block instead. `STANDARDS_BRANCHES` changes which branches are checked (default `feature/*`).
- The rules live in `.claude/agents/coding-standards.md`; edit that file to change them.

### 6. Monitoring (optional): Prometheus, Loki and Grafana

```powershell
docker compose --profile monitoring up -d
```

| What | Where |
|---|---|
| Grafana, with the **Merger Sync Hub** dashboard as the home page | http://localhost:3033 (anonymous view; `admin` / `admin` to edit) |
| Prometheus, including the alerts in `monitoring/alert-rules.yml` | http://localhost:9090/alerts |
| Loki (queried through Grafana's Explore page) | http://localhost:3100 |
| OpenTelemetry Collector (OTLP in) | `localhost:14317` gRPC, `localhost:14318` HTTP |

How it fits together:

```
.NET services --OTLP--> OpenTelemetry Collector --+--> Prometheus (metrics) --+--> Grafana
                                                  +--> Loki (logs) ----------+
```

- Every service calls `builder.AddObservability(...)` from `src/SyncHub.Observability`. It collects the
  platform's built-in meters (HTTP server and client, runtime, Polly) and the worker's business metrics
  (`SyncMetrics`: records read, merged and rejected, run outcomes and durations, time since last success).
- Logs still go to the console, and also to Loki with their scopes, so you can filter on `Source` or paste a
  run's `CorrelationId` into the dashboard to see that run's log lines.
- Services only know the collector's address (`OpenTelemetry:OtlpEndpoint` in `appsettings.json`). Leave it
  empty to switch telemetry off. If the collector isn't running, the services carry on as normal.
- Ports are offset (3033, 14317/14318) because 3000 and 4317/4318 are commonly used by other local stacks.

---

## A 10-minute demo

0. **Joiner.** Sign in to the **Directory admin centre** as `it.admin` and add yourself as a new starter
   in the *SyncHub Support Analysts* group. Note the one-time password. Sign in to the dashboard as the new
   user: you're made to choose a new password, and then you see only what a Support Analyst can do.
   Sign in as `tom.reid` to show the read-only view.
1. **Overview tab.** Tiles show what's in the hub. The **Reconciliation** table proves that
   *source total = in hub + open exceptions + ignored* for every source and entity.
2. **Show resilience.** Point at the worker log: injected 503s and slow responses are retried with
   exponential backoff and jitter. The run still succeeds.
3. **Exceptions tab.** Exceptions are grouped by type (impossible dates, close-before-open, orphan matters, missing names).
   - Open the Firm A **MissingClientName** item (client `ALDR001`). Its two matters are also failing as
     **OrphanMatter**, waiting for it.
   - Add a `"clientName"` to the payload, then click **Save and replay**.
   - On the next cycle the client is merged, **its orphan matters are queued for replay automatically**,
     and the cycle after that resolves them. Every step is recorded in the item's history.
4. **Duplicate clients tab.** Review the 13 suggested pairs, for example *ACME HOLDINGS LTD* (Firm A) and
   *ACME Holdings Corp* (Firm B). Confirm a few. After the next reporting refresh, Power BI's **Unique Clients**
   and **Shared Clients** measures reflect your decisions.
5. **Mappings tab.** Firm A's `MISC` and Firm B's `LABOR_EMPLOYMENT` codes don't map to a practice area.
   Map `LABOR_EMPLOYMENT` to `Employment`. The "Unmapped" bar in Power BI shrinks on the next refresh,
   with no re-sync needed.
6. **Incremental sync.** Run `database/demo/simulate_firmA_changes.sql`. The next Firm A run reads only
   the changed rows, and the new "keyed in wrong" matter appears as an exception.
7. **Performance.** Run `database/perf/firmA_incremental_extract.sql` with the actual execution plan on:
   a clustered index scan becomes an index seek.
8. **Reporting.** Show the SSRS exceptions report and the Power BI overview, including **View as** a
   UK-only user for row-level security.

---

## Design decisions (the "why")

| Decision | Why |
|---|---|
| **Staging table, then a set-based `MERGE`** | One round trip per batch instead of row-by-row. SqlBulkCopy is much faster than individual inserts. The staged rows are kept as an audit trail. |
| **Idempotent merge (row hash) and a watermark that moves only after commit** | If anything fails halfway, the next run re-reads the same records, and re-applying them changes nothing. Retrying is always safe. |
| **Overlap window on the watermark** | Rows committed late with an earlier timestamp would otherwise be missed. Re-reading a few minutes costs little because of idempotency. |
| **Newer-wins check (`SourceModifiedUtc`), on replays only** | A replay of an old record must never overwrite newer data. Scheduled reads skip it: they always return the source's current state, and Firm A's UK local timestamps repeat an hour when the clocks go back, so comparing them could silently reject a real later edit. |
| **Dead letters with the raw payload** | One bad record shouldn't stop 75,000 good ones. Keeping the payload means a person can correct and replay it without asking the source team to re-send. |
| **One open dead letter per record** | Repeated failures increase a counter instead of flooding the queue. |
| **Self-healing orphans** | Matters that arrive before their client are queued again automatically when the client lands. That removes a common source of support tickets. |
| **Mapping and validation in a separate, pure library** | Business rules can be unit tested without a database or network. Replays use exactly the same rules as live data. |
| **Standard resilience handler** (retry, backoff, jitter, timeouts, circuit breaker) | Transient failures are retried. A down API is not hammered. Permanent errors (400, 401) fail fast. |
| **Sign-in through a separate directory with signed tokens** | The Support API never sees a password and holds no shared secret: it checks the token's RS256 signature using the directory's published keys (OIDC discovery + JWKS), exactly as it would with Entra ID. |
| **Access through groups mapped to app roles** | Access is managed in one place and reviewed by group, not person by person. Each token carries only the roles for the app it was issued to. |
| **Identity from the token, never the request body** | The audit trail can't be faked by sending a different name. |
| **Each source and step isolated** | Firm B being down doesn't stop Firm A syncing or reporting refreshing. |
| **Correlation id on every log line and every run row** | A failed run in the dashboard can be traced to its exact log lines. |
| **UTC everywhere in the hub** | Firm A stores UK local time. Conversion handles the clock-change hour that doesn't exist and the one that happens twice. |
| **Duplicate detection suggests; people decide** | Wrongly merging two different clients is far worse than missing a match, especially for conflict checks. |
| **Raw practice area codes kept; mapped at reporting time** | Fixing a mapping corrects history on the next refresh without re-syncing. |
| **Star schema for reporting** | Simple DAX, fast visuals, and the same dimensions serve SSRS and Power BI. |

## Known limitations and what I'd do next

- **Deletes** aren't handled. The sources only soft-close records. Next step: SQL Server Change Tracking on Firm A, and a deletions feed or periodic key comparison for Firm B.
- **Paging during changes.** Firm B's page-number paging can skip a record that moves between pages mid-read. The overlap window covers it here; keyset paging (`after=lastModified,id`) would be better.
- **Nvarchar parameters against varchar columns.** Dapper sends strings as `nvarchar`, which can force an implicit conversion and a scan on large `varchar` indexes. Fix with `DbString { IsAnsi = true }` on hot paths.
- **Security.**
  - Replace the mock directory with Entra ID. The Support API only needs its `Auth:Authority` changed, because it already validates standard signed tokens.
  - Use MSAL in the React apps instead of keeping tokens in sessionStorage.
  - Keep secrets in Key Vault or user secrets.
  - Use a least-privilege SQL login per app.
  - Add token revocation so disabling a leaver takes effect within minutes rather than at token expiry.
- **Observability.** Metrics and logs are in place (see step 6). Next: traces (Tempo, or Application Insights in Azure), an Alertmanager route for the alert rules, and a gauge for open dead letters and the reconciliation "Investigate" status, which today live only in SQL.
- **Hosting.** The worker could run as a Windows service, a container, or as Azure Functions (a timer trigger for the sync and a queue trigger for replays). The trade-offs are worth discussing: long-running full loads and on-premises SQL access favour a service.
- **Matching.** Add a second blocking key (postcode) to catch typos in the first letters of a name, and give reviewers a "merge" action that writes the decision back to a master data service.
