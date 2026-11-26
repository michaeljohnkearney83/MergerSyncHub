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
| `database/` | Numbered SQL scripts 01–06, `setup.ps1`, `perf/` indexing demo, `demo/` change simulator |
| `src/SyncHub.Core` | Pure logic, no I/O: models, mappers, validation, name normalisation, duplicate matching |
| `src/SyncHub.Worker` | .NET BackgroundService: incremental sync → SqlBulkCopy staging → set-based MERGE |
| `src/SyncHub.SupportApi` | Minimal API behind the dashboard; JWT bearer auth with role policies |
| `src/FirmB.MockApi` | "Other firm" REST API with injected 503s, slow responses and bad records |
| `src/CorpDirectory.Api` | Mock AD / Entra ID: sign-in, RS256 tokens, OIDC discovery + JWKS, joiner/leaver admin API |
| `src/SyncHub.Observability` | `AddObservability()`: OpenTelemetry metrics + logs over OTLP, shared by all four services |
| `src/SyncHub.Tests` | xUnit tests for Core (mappers, matching, UK time conversion) |
| `dashboard/` | React + TS (Vite) support dashboard, port 5173 |
| `directory-admin/` | React + TS (Vite) directory admin centre, port 5174 |
| `reports/` | SSRS and Power BI build guides, `measures.dax` |
| `docker/` | `dotnet.Dockerfile` (all four services, `PROJECT` build arg), `web.Dockerfile` (both React apps), `nginx.conf`, `db-init.sh` |
| `monitoring/` | Collector, Prometheus (+ `alert-rules.yml`), Loki, Grafana provisioning and `dashboards/synchub.json` |
| `docs/interview-guide.md` | Maps the project to interview questions; STAR answers |
| `.claude/agents/sql-reviewer.md` | Read-only subagent that reviews T-SQL and Dapper SQL against the SQL conventions below |
| `.claude/agents/observability-advisor.md` | Read-only subagent that suggests logs, metrics, alerts and Grafana panels |
| `.claude/agents/coding-standards.md` | Read-only gate agent (Sonnet): blocking/advisory rules, last line `VERDICT: PASS` or `VERDICT: FAIL` |
| `.githooks/pre-push` | Runs `claude -p --agent coding-standards` on pushes to `feature/*`; enable with `git config core.hooksPath .githooks` |
| `.githooks/post-commit` | Merges `origin/main` (or local `main`) into `feature/*` after each commit; aborts cleanly on conflict |

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
- **Start order:** CorpDirectory.Api → FirmB.MockApi → SyncHub.Worker → SyncHub.SupportApi → the two React apps.
- **Or all in Docker:** `docker compose --profile app --profile monitoring up -d --build`. Same host ports, so it
  and `dotnet run`/`npm run dev` can't run at once. Containers use environment `Docker` (not Development), so user
  secrets don't apply: connection strings come from compose, built from `MSSQL_SA_PASSWORD` in `.env`, using `sql,1433`.
  `db-init` only runs the scripts when the databases are missing. The signing key is on the `synchub-directory-keys`
  volume (separate from the local `signing-key.pem`, so tokens from one don't work in the other).
- Demo accounts (password `Welcome2SyncHub!`): `it.admin`, `alex.morgan` (Support), `priya.shah`
  (Data Steward), `tom.reid` (Viewer), `jordan.lee` (no access), `sam.carter` (disabled).

```powershell
dotnet build src
dotnet test src
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
- **Logs:** message templates, not interpolation; never log passwords or tokens; scopes (Source, CorrelationId) reach Loki as structured metadata.
- **UK English** in UI text and docs. Keep comments that explain trade-offs.

## Gotchas already hit

- `Login failed for user '...\Guest'` → something is using Windows auth against a Docker SQL Server
  (wrong server/port, or user secrets not loaded because the environment isn't Development).
- `QUOTED_IDENTIFIER` errors on `CREATE INDEX` → script run via sqlcmd without `-I` / `SET` line.
- The Support API caches the directory's signing keys; if the directory wasn't up, it retries after 30s.
- Deleting `src/CorpDirectory.Api/signing-key.pem` invalidates all issued tokens (users sign in again).
- Inside Docker, `localhost` is the container itself. The Support API trusts issuer `http://localhost:5090` but loads
  keys via `Auth:MetadataAddress` (`http://directory:8080/...`); the discovery document builds `jwks_uri` from the
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
- Reports: SSRS and Power BI are build guides only (no .rdl/.pbix committed yet).

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
