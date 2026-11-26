# CLAUDE.md

Guidance for Claude Code working in this repository.

## What this is

**Merger Sync Hub**: an interview portfolio project modelling a law-firm merger (Winston Taylor,
formed from Winston & Strawn and Taylor Wessing UK). Two practice management systems feed one
combined client and matter register, with a support dashboard, a mock corporate directory, and
SSRS / Power BI reporting. All data is synthetic. The owner is preparing for a Software Developer
interview (C#/.NET, SQL Server, integrations, third-line support, SSRS/Power BI, React), so code
should be clean, explainable and idiomatic. Design decisions matter as much as features:
keep the "why" comments.

## Layout

| Path | Purpose |
|---|---|
| `database/` | Numbered SQL scripts 01–07 (07: read-only reporting login), `setup.ps1`, `perf/` indexing demo, `demo/` change simulator |
| `src/Libraries/SyncHub.Core` | Pure logic, no I/O: models, mappers, validation, name normalisation, duplicate matching |
| `src/Services/SyncHub.Worker` | .NET BackgroundService: incremental sync → SqlBulkCopy staging → set-based MERGE |
| `src/Services/SyncHub.SupportApi` | Minimal API behind the dashboard; JWT bearer auth with role policies |
| `src/Mocks/FirmB.MockApi` | "Other firm" REST API with injected 503s, slow responses and bad records |
| `src/Mocks/CorpDirectory.Api` | Mock AD / Entra ID: sign-in, RS256 tokens, OIDC discovery + JWKS, joiner/leaver admin API |
| `src/Libraries/SyncHub.Observability` | `AddObservability()`: OpenTelemetry metrics + logs over OTLP, shared by all four services |
| `tests/SyncHub.Tests` | xUnit tests for Core (mappers, matching, UK time conversion) |
| `tests/SyncHub.IntegrationTests` | Needs Docker: Support API in memory (`WebApplicationFactory`, test-signed JWTs) + real procs in a Testcontainers SQL Server |
| `dashboard/` | React + TS (Vite) support dashboard, port 5173 |
| `directory-admin/` | React + TS (Vite) directory admin centre, port 5174 |
| `reports/powerbi/` | Power BI project `MergerSyncHub.pbip`: model as TMDL (`MergerSyncHub.SemanticModel/definition/`), 17 measures, `FirmAccess` RLS |
| `reports/ssrs/` | SSRS build guide (exceptions and sync-run reports) |
| `docker/` | `dotnet.Dockerfile` (all four services, `PROJECT` build arg), `web.Dockerfile` (both React apps), `nginx.conf`, `db-init.sh` |
| `monitoring/` | Collector, Prometheus (+ `alert-rules.yml`), Loki, Alloy (`alloy.alloy`, Faro receiver), Grafana provisioning and `dashboards/synchub.json` |
| `docs/interview-guide.md` | Maps the project to interview questions; STAR answers |
| `docs/glossary.md` | Plain-English glossary: law-firm terms (client, matter...) and integration terms (dead letter, replay, reconciliation...) |
| `.claude/agents/sql-reviewer.md` | Read-only subagent that reviews T-SQL and Dapper SQL against the SQL conventions below |
| `.claude/agents/observability-advisor.md` | Read-only subagent that suggests logs, metrics, alerts and Grafana panels |
| `.claude/agents/coding-standards.md` | Read-only gate agent (Sonnet): blocking/advisory rules, last line `VERDICT: PASS` or `VERDICT: FAIL` |
| `.githooks/pre-push` | Runs `claude -p --agent coding-standards` on pushes to `feature/*`; enable with `git config core.hooksPath .githooks` |
| `.githooks/post-commit` | Merges `origin/main` (or local `main`) into `feature/*` after each commit; aborts cleanly on conflict |

**Solution layout:** projects are grouped on disk and in `src/MergerSyncHub.sln` (matching solution folders):
`src/Libraries/` (Core, Observability), `src/Services/` (Worker, SupportApi), `src/Mocks/` (CorpDirectory.Api,
FirmB.MockApi: stand-ins for Entra ID and Firm B's real system, never deployed) and `tests/`. Tests import
`src/Directory.Build.props` through `tests/Directory.Build.props`. The Docker build takes the group as a build arg
(`GROUP` + `PROJECT` in compose). A new project goes in the right folder on disk and in the solution
(`dotnet sln src/MergerSyncHub.sln add --solution-folder <Group> src/<Group>/<Project>/<Project>.csproj`).
**Close VS Code before moving projects:** the C# extension rewrites `.csproj` files and recreates old ones mid-move.

## Running locally (this machine)

- **SQL Server runs in Docker** on `localhost,14333` (`docker compose up -d`; sa password in `.env`).
  Port 1433 belongs to an unrelated project's container (QuickCover). Never point anything at it.
- **Connection strings come from .NET user secrets** (set by `set-local-secrets.ps1`). The committed
  `appsettings.json` files deliberately hold password-free Windows-auth defaults. Each project's
  `Properties/launchSettings.json` sets the Development environment so user secrets load.
- **Target framework is net10.0** (`src/Directory.Build.props`); only .NET 10/11 runtimes are installed.
- Ports: FirmB mock 5070, **Support API 5081** (5080 is taken by another container; the dashboard's
  `.env.local` points at 5081), directory 5090, dashboard 5173, directory admin 5174.
- **Monitoring** is the compose profile `monitoring` (`docker compose --profile monitoring up -d`):
  Grafana **3033**, Prometheus 9090, Loki 3100, collector OTLP **14317** (gRPC) / 14318. Ports 3000 and
  4317/4318 belong to the video downloader's `otel-lgtm` container. Never point anything at them.
  Grafana Alloy (Faro receiver for browser telemetry) is on **12347**.
- **Start order:** CorpDirectory.Api → FirmB.MockApi → SyncHub.Worker → SyncHub.SupportApi → the two React apps.
- **Or all in Docker:** `docker compose --profile app --profile monitoring up -d --build`. Same host ports, so it
  and `dotnet run`/`npm run dev` can't run at once. Containers use environment `Docker` (not Development), so user
  secrets don't apply: connection strings come from compose, built from `MSSQL_SA_PASSWORD` in `.env`, using `sql,1433`.
  `db-init` only runs the scripts when the databases are missing. The signing key is on the `synchub-directory-keys`
  volume (separate from the local `signing-key.pem`, so tokens from one don't work in the other).
- **Docker names** follow `<area>-<kind>`: `directory-api`, `directory-web` (admin centre, 5174), `support-api`,
  `support-web` (dashboard, 5173), `firmb-api`, `sync-worker`, `db-init`; containers are `synchub-<service>`.
  Service names are the in-network hostnames, so renaming one means updating the URLs that point at it in compose.
- Demo accounts (password `Welcome2SyncHub!`): `it.admin`, `alex.morgan` (Support), `priya.shah`
  (Data Steward), `tom.reid` (Viewer), `jordan.lee` (no access), `sam.carter` (disabled).

```powershell
dotnet build src
dotnet test src                    # unit + integration (integration needs Docker)
dotnet test tests/SyncHub.Tests    # unit only
cd database; .\setup.ps1 -Server "localhost,14333" -User sa -Password '<sa password>'   # rebuilds ALL databases
```

Re-running `setup.ps1` drops and recreates everything, including synced data and directory users.
To add one script, run it alone: `sqlcmd -S "localhost,14333" -U sa -P '<pw>' -C -I -i database\0N_x.sql`.

## Conventions

- **C#:** primary constructors, records for data, file-scoped namespaces, nullable enabled, Dapper with
  parameterised SQL (no string-built SQL from input), stored procedures for set-based work. String parameters
  compared with `varchar` columns go through `Ansi(value, length)` (a `DbString`), or SQL Server scans instead of seeking.
- **Identity comes from the token**, never from request bodies (`ClaimsPrincipal`, `name` claim).
- **Mapping never throws for bad data**: return `MapResult.Failure`, which becomes a dead letter.
  Replay uses the same mapper as live sync.
- **Merges must stay idempotent** (row hash compare). **Newer-wins** (`SourceModifiedUtc`) applies to replays
  only (`@IsReplay = 1`); scheduled reads always win, because Firm A's local-time stamps repeat an hour in October.
  Firm A's look-back is at least 65 minutes for the same reason.
  The watermark only moves after the merge commits.
- **SQL scripts:** re-runnable (drop/create or `CREATE OR ALTER`), unambiguous date literals
  (`'YYYYMMDD'`, since British-English logins misread `'YYYY-MM-DD'`), and `SET QUOTED_IDENTIFIER ON`
  at the top (filtered indexes need it; procedures capture it at creation; sqlcmd defaults it OFF).
- **React:** function components, typed API client per app (`api.ts`), `usePolling` for refresh,
  no UI library. Dates from the API are UTC without `Z`; use `parseUtc` / `formatDateTime`.
- **Metrics:** business metrics go in `SyncMetrics` (meter `SyncHub.Worker`); a new meter must be passed to
  `AddObservability(name, meter)`. Labels stay low-cardinality (source, entity, outcome, error type); keys,
  run ids and correlation ids go in logs. Prometheus names: dots become `_`, counters get `_total`, unit `s`
  adds `_seconds`. If you rename a metric, update `monitoring/alert-rules.yml` and the dashboard JSON.
- **SQL-backed gauges** (dead letters, reconciliation, stuck runs) come from `ops.usp_GetHealthSnapshot` +
  `usp_GetReconciliation`, read by `HealthSnapshotService` and held in `SyncMetrics`. Gauge callbacks never query SQL.
  A snapshot older than 3 intervals is dropped, so the gauges vanish rather than report stale values.
- **Replays** have their own counters (`synchub.replay.*`); never route them through `RunCompleted`, which would
  reset `time_since_success` and hide a stopped scheduled sync.
- **Dashboard:** edit `monitoring/grafana/make_dashboard.py`, then regenerate `dashboards/synchub.json` from it.
- **Browser telemetry:** each React app's `src/telemetry.ts` (Faro). Report through `reportApiUnreachable` /
  `reportApiError`, never raw URLs or request bodies. Never attach user identity; keep performance (resource timing)
  instrumentation off, because it sends full fetch URLs and client searches put client names in the query string.
- **Logs:** message templates, not interpolation; never log passwords or tokens; scopes (Source, CorrelationId) reach Loki as structured metadata.
- **Tests:** a new Support API endpoint gets a role test in `ApiAuthorizationTests` (who gets 403). A change to a
  merge procedure gets a case in `MergeTests`. Integration tests share one SQL container: isolate each test with
  `SqlServerFixture.NewSource()` rather than resetting the database.
- **Method comments:** public/internal C# methods get `///` XML docs (summary, plus `<param>`/`<returns>` where the
  meaning isn't obvious); exported TS functions, API client calls and components get JSDoc (`/** */`, `@param`,
  `@returns`, `@throws`). Both feed IntelliSense. Say what it does and why, not the name again. Test methods are
  self-describing and need none. `GenerateDocumentationFile` is on (`src/Directory.Build.props`) so malformed docs and
  wrong `<param>` names are build warnings; CS1591 (missing docs) is off. Top-level `Program.cs` helpers are local
  functions, which can't take `///`, so they get `//`.
- **Versions:** when you upgrade a package, SDK or image, update the Versions table and badges at the top of README.
- **README navigation:** the Contents menu links to headings by GitHub's generated anchors (lower case, punctuation
  dropped, spaces to hyphens: `### 3. Start everything` is `#3-start-everything`). Renaming or adding a heading means
  updating the menu. New domain or support terms go in `docs/glossary.md`; the README's Key terms stays at about seven.
- **UK English** in UI text and docs. Keep comments that explain trade-offs.

## Reporting

- Power BI and SSRS connect as **`synchub_reporting`** (`database/07_reporting_login.sql`): SELECT on `rpt`, EXECUTE on
  `rpt.usp_SsrsExceptions` / `usp_SsrsSyncRunSummary` only. Its password is `REPORTING_SQL_PASSWORD` in `.env`, passed
  to sqlcmd as a variable by `setup.ps1` and `db-init`; it never appears in the repo or in the Power BI files.
- The Power BI model's source of truth is the **TMDL** under `reports/powerbi/MergerSyncHub.SemanticModel/definition/`.
  `.pbi/cache.abf` (data) and `.pbi/localSettings.json` and any `.pbix` are git-ignored. Edit the model in Desktop
  (with the `.pbip` open, Ctrl+S) or with the Power BI modelling MCP; close Desktop before editing TMDL by hand.
- Ctrl+S saves whichever file is open: saving while a `.pbix` is open does not update the `.pbip`.
- Check measure changes against SQL with the reporting login, as was done for every headline figure.

## Gotchas already hit

- `Login failed for user '...\Guest'` → something is using Windows auth against a Docker SQL Server
  (wrong server/port, or user secrets not loaded because the environment isn't Development).
- `QUOTED_IDENTIFIER` errors on `CREATE INDEX` → script run via sqlcmd without `-I` / `SET` line.
- The Support API caches the directory's signing keys; if the directory wasn't up, it retries after 30s.
- Deleting `src/Mocks/CorpDirectory.Api/signing-key.pem` invalidates all issued tokens (users sign in again).
- Inside Docker, `localhost` is the container itself. The Support API trusts issuer `http://localhost:5090` but loads
  keys via `Auth:MetadataAddress` (`http://directory-api:8080/...`); the discovery document builds `jwks_uri` from the
  request's host for this reason. React apps get `localhost` URLs at build time, because the browser runs on the host.
- `docker/*.sh` must keep LF line endings (`.gitattributes`); CRLF breaks them in Linux containers.

## Status and known gaps

- Everything was written without a local compiler; the owner has built and run most of it.
  The mock directory, role-based auth and `directory-admin` app are the newest and least exercised:
  verify with a build and a sign-in as each demo account before relying on them.
- Not handled: deletes from sources, keyset paging for Firm B, token revocation for disabled users
  (tokens live 60 min), tracing (metrics and logs only), Alertmanager, Entra ID/MSAL (tokens are in sessionStorage).
- Monitoring was verified end to end on 2026-11-26: all four services report to Prometheus and Loki, and every
  dashboard query runs. Panels for rejections, step failures and Polly retries stay empty until one happens.
  The OTLP exporter's own calls show up as HttpClient metrics to port 14317; Firm B panels filter on port 5070.
- Reports: the Power BI semantic model is built and verified against SQL (26 Nov 2026); its report pages are a
  hand-built step (guide section 4). SSRS is still a build guide (no .rdl yet).

## Git workflow

- Remote: `origin` = https://github.com/michaeljohnkearney83/MergerSyncHub (**public**: never commit
  secrets or real client data). Work on `feature/*` branches
  and merge into `main`; pushing a `feature/*` branch runs the coding-standards check (needs the `claude` CLI).
- `core.hooksPath` is set to `.githooks` in this clone; every new clone needs `git config core.hooksPath .githooks`.
- The post-commit hook auto-merges main into feature branches. Don't be surprised by "Merge origin/main into
  feature/..." commits, and don't run it during a rebase (it skips itself). `SKIP_AUTO_MERGE=1` turns it off once.
- On the first commit, stage the hooks as executable: `git add --chmod=+x .githooks/*`.

## Coding standards gate

- The rules in `.claude/agents/coding-standards.md` are the written form of the conventions above. When a
  convention here changes, change the agent's rules too, and keep its last output line `VERDICT: PASS|FAIL`
  (the hook greps for it).
- The hook fails open (warns and pushes) if `claude` is missing or errors; `STANDARDS_FAIL_CLOSED=1` blocks.
- `.gitattributes` forces LF on `.githooks/*`: CRLF breaks bash hooks on Windows.

## Never commit

`.env`, `.env.local`, `signing-key.pem`, user secrets, `bin/`, `obj/`, `node_modules/`.
Check with `git status --ignored` and `git grep -n "Password="` before pushing.
