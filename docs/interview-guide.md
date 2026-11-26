# Using this project in the Winston Taylor interview

The project is built to give you a concrete, honest example for each thing the role asks for.
Present it as **a personal project to explore the merger's integration problem**, not commercial work.
Use it alongside your real work examples, not instead of them.

Opening line, if asked what you've been doing to prepare:
> "I wanted to understand the integration side of the merger properly, so I built a small model of it:
> two practice management systems, one legacy SQL Server and one REST API, feeding a combined client
> and matter register, with SSRS and Power BI on top and a support dashboard for third-line work."

---

## Where each requirement shows up

| They're looking for | Point to | One-line version |
|---|---|---|
| C# / .NET Core, maintainable code | `SyncHub.Worker`, `SyncHub.Core` | Dependency injection, options, background service, pure rules library with unit tests |
| Integrations, APIs, data exchange | `Connectors.cs`, `SourceSyncService.cs` | Incremental watermark sync from both a database and a paged API |
| Reliability | `Program.cs` (resilience), merge procedures | Retries with backoff, circuit breaker, idempotent merge, dead letters, replay |
| SQL Server and T-SQL performance | `04_synchub_procedures.sql`, `database/perf` | Set-based MERGE, SqlBulkCopy, TVPs, covering index for the extract |
| Third-line support | Dashboard Exceptions tab, correlation ids | Find the failing record, see why, fix the data, replay, all audited |
| Data fixes and migrations | Payload edit and replay, Mappings tab | Data fixes without touching production tables by hand |
| SSRS / Power BI | `reports/` | Paginated exceptions report; Power BI project with star schema, DAX, row-level security, least-privilege reporting login |
| React | `dashboard/` | TypeScript, typed API client, polling, simple components |
| Security and access | `CorpDirectory.Api`, `directory-admin/`, Support API policies | Signed tokens, roles from groups, joiner/leaver process, lockout, audit |
| Law-firm awareness | Duplicate detection, RLS notes | Shared clients across both firms means conflicts; ethical walls |
| Challenging process | "Self-healing orphans", reconciliation | Removed a whole class of support tickets; proves the numbers |

---

## STAR answers built from the project

### "Tell us about an integration you designed." (for Paul)

- **Situation:** Two firms merge. Client and matter data sits in a legacy SQL database and a REST API, each with its own codes, time zones and data-quality problems.
- **Task:** Keep a combined register up to date without manual rekeying, and make failures visible and fixable.
- **Action:**
  - Incremental sync using watermarks, staging with SqlBulkCopy, and a set-based, idempotent MERGE.
  - The watermark only moves after the merge commits, so retries are always safe.
  - Bad records go to a dead-letter table with the raw payload. They're never silently dropped and never block the batch.
  - The API connector uses the standard .NET resilience handler: retries with backoff and jitter, timeouts and a circuit breaker.
- **Result:** The integration is designed to ride out the mock API's 10% simulated outage rate without failed runs, and every record is accounted for in a reconciliation (source = hub + exceptions + ignored). *Run it and check the dashboard before you quote any numbers. Use figures you've actually seen.*
- **Why it's built this way:** Idempotency makes retries safe. Dead letters keep one bad row from stopping 75,000 good ones. Staging plus MERGE is set-based, not row-by-row.

### "Describe a complex issue you investigated." (for Dawn)

Use a real work example first if you have one. This one works as a supporting story, or as a fallback:

- **Situation:** Matters were failing as orphans: their client wasn't in the hub.
- **Root cause:** The client record itself had failed validation (no name in the legacy system), so every one of its matters failed after it. It was a cascading failure, and the matter errors were only a symptom.
- **Action:** Fixed the client record through the dashboard and replayed it. Then changed the merge so orphan matters are linked to the client they're waiting for and re-queue themselves automatically when it arrives.
- **Result:** A whole category of repeat tickets disappeared. The lasting fix was also raised with the source system's owners, because a data fix in the hub only unblocks the integration.

### "How do you approach SQL performance?"

- Measure first: `SET STATISTICS IO, TIME ON` and the actual execution plan.
- The legacy extract did a clustered index scan of 75,000 rows on every incremental run. A covering index on `(LAST_MODIFIED, MATTER_ID)` turned it into a seek.
- Mention the trade-off: the extra index costs a little on every write. If it's a vendor database where you can't add indexes, the alternatives are Change Tracking or reading from a replica.
- Other examples in the code:
  - Avoiding `@p IS NULL OR col >= @p` (it prevents seeks)
  - Set-based MERGE instead of loops
  - Chunked deletes in the purge job
  - Dapper's nvarchar parameters against varchar columns causing implicit conversions

### "How do you turn requirements into a solution and keep people informed?"

- The reconciliation table is the communication tool: one table that tells a non-technical stakeholder "everything is accounted for" or "here's exactly what's outstanding".
- Duplicate detection only suggests matches, and people decide. Explain why: a false merge of two clients is a conflicts risk, so the business keeps control.

---

### "How do you handle security and access?"

- Sign-in happens in a separate directory, a mock of Entra ID. It issues RS256-signed tokens; the Support API validates them using the directory's published keys and never sees a password.
- Access is given through **groups mapped to app roles**: Viewer, Support Analyst, Data Steward, Administrator. The API enforces each action, and the dashboard only shows what your role allows.
- Who made a change is taken from the token, so the audit trail can't be faked.
- Joiner, mover and leaver steps are built in:
  - new starters get a one-time password and must set their own at first sign-in;
  - moving teams means changing groups;
  - leavers are disabled, not deleted, so their history stays.
- Be honest about the limits. A disabled user's existing token lasts until it expires (60 minutes). In production you'd use Entra ID, MSAL and token revocation (continuous access evaluation).
- **Law-firm angle:** the same group-to-role pattern is how you'd implement ethical walls, with access restricted per client or matter.

## Questions Paul might push on, and good answers

- **"Why not just call the API directly from the database or use SSIS?"**
  SSIS would work for Firm A. The C# worker gives one place for both a database source and an API source, unit-testable rules, modern resilience, and logs that fit with the rest of the .NET estate. For large pure database-to-database moves, SSIS or Azure Data Factory is reasonable, and I'd say so.
- **"Would you use serverless for this?"**
  Partly. A queue-triggered function suits replays and webhooks. But the full load is long-running and needs on-premises SQL access, so a worker service (or a container) suits the main sync better. It depends on where the target architecture is going, which is a good thing to ask him.
- **"How would this scale?"**
  - Batches are already bounded.
  - Next steps: keyset paging, parallelising sources, incremental loads into reporting using `LastSyncRunId`, and partitioning staging.
  - The merge is set-based, so it scales well with volume.
- **"What about LLMs?"**
  - The combined register is the foundation any AI work needs: clean, de-duplicated, with clear ownership.
  - An obvious first use is suggesting practice-area mappings or explaining a duplicate match for the reviewer.
  - Keep a person approving the result, and keep client data within the firm's tenant.

## Questions to ask them

- Which systems are first in line for integration between the two firms?
- Is there a target integration pattern yet (an integration platform, events, direct APIs), or is that still being designed?
- How do support and development work split across the team, and across UK and US time zones?
- Where do SSRS and Power BI sit today, and is paginated reporting moving to the Power BI service?
- What would a great first six months look like in this role?
