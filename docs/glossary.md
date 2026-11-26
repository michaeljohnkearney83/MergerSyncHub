# Glossary

The words this project uses, in plain English. Law-firm terms first, then the integration and support terms
the code, dashboard and alerts use. [Back to the README](../README.md)

- [Law-firm terms](#law-firm-terms)
- [Integration and support terms](#integration-and-support-terms)
- [Dead-letter lifecycle](#dead-letter-lifecycle)
- [Monitoring terms](#monitoring-terms)
- [People and roles](#people-and-roles)
- [How it fits together](#how-it-fits-together)

## Law-firm terms

| Term | What it means | In this project |
|---|---|---|
| **Client** | The person or organisation the firm acts for, e.g. "Acme Holdings Ltd". One client usually has many pieces of work. | `core.Client`: one row per client per source firm, with name, country, postcode and opened date. |
| **Matter** | One piece of legal work for a client: a case, a deal, a contract review. The unit firms bill, staff and run conflict checks against. | `core.Matter`: belongs to one client, with matter number, description, practice area, fee earner, and opened and closed dates. |
| **Matter number** | The firm's reference for a matter, e.g. `ACM001-0003`. | Required: a matter without one is rejected. |
| **Practice area** | The legal specialism: Corporate, Litigation, Real Estate, Private Wealth and so on. | Each firm has its own codes (Firm A department codes, Firm B practice groups); **practice-area mappings** translate them into one firm-wide list. |
| **Fee earner** | The lawyer responsible for the matter, whose time is billed. Firm B calls this the "responsible attorney". | Stored on each matter. |
| **Practice management system (PMS)** | The firm's core system of record for clients, matters, time and billing (real examples: Aderant, Elite 3E, Intapp). | Firm A's legacy database and Firm B's API are the two systems being merged. |
| **Conflict check** | Before taking a new matter, the firm checks it doesn't already act against that party. | The reason duplicate detection matters: after a merger the same client may exist in both firms' systems. |

## Integration and support terms

| Term | What it means | In this project |
|---|---|---|
| **Source** | A system that data comes from. | `FirmA` (legacy UK SQL Server database) and `FirmB` (US REST API). |
| **Hub** | The combined, cleaned copy of both firms' data. | The `SyncHub` database: `core.*` is the register, `ops.*` is operational, `rpt.*` is reporting, `stg.*` is staging. |
| **Sync run** | One pass that reads changes from one source and merges them into the hub. | `ops.SyncRun`, either `Scheduled` (every minute) or `Replay`. Its `CorrelationId` is on every log line for that run. |
| **Watermark** | A bookmark: the newest change already processed for a source and entity, so the next run only reads what changed since. | Moves only after the merge commits, so a failed run simply re-reads the same records. |
| **Look-back** | Re-reading a few minutes before the watermark, to catch rows saved late with an earlier timestamp. | 5 minutes by default; at least 65 minutes for Firm A, whose UK local timestamps repeat an hour when the clocks go back in October. |
| **Mapping** (a record) | Converting a source record into the hub's standard shape and checking its rules: name present, valid dates, a matter doesn't close before it opens. | `FirmAMapper` / `FirmBMapper`. A record that fails becomes a dead letter. Not the same as practice-area mappings. |
| **Staging** | A holding table where a run's mapped records land before being merged in one go. | `stg.Client` and `stg.Matter`, loaded with SqlBulkCopy and purged after 7 days. |
| **Merge** | Applying staged records to the hub: insert new ones, update changed ones. | A set-based SQL `MERGE` in `core.usp_MergeClients` and `core.usp_MergeMatters`. |
| **Idempotent** | Doing it twice has the same effect as doing it once. | Rows whose content hash hasn't changed aren't rewritten, so retries and the look-back are safe. |
| **Newer wins** | An older version of a record must never overwrite a newer one. | Applies to replays only: a scheduled read is always the source's current state. |
| **Dead letter** | A record that couldn't be processed, parked with its original data and the reason, so the rest of the batch carries on. (From the postal service: a letter that can't be delivered.) | `ops.DeadLetter`: one row per failing record, with the raw payload, error type and how often it has failed. |
| **Exception** | The support-facing name for a dead letter: something that needs a person to look at it. | The dashboard's **Exceptions** tab is the dead-letter queue. "Open exceptions" are dead letters in `New` or `ReplayRequested`. |
| **Error type** | The category of failure. | `MissingClientName`, `MissingClientReference`, `MissingMatterNumber`, `InvalidDate`, `CloseDateBeforeOpenDate`, `OrphanMatter`, `MalformedPayload`. Used to group exceptions on screen, in metrics and in alerts. |
| **Orphan matter** | A matter whose client isn't in the hub (yet). | Dead-lettered, then re-queued automatically when its client arrives. |
| **Payload** | The record exactly as the source sent it (JSON). | Stored on each dead letter, so support can correct it field by field and replay it. |
| **Replay** | Running a dead letter's (possibly corrected) payload through the same mapping and merge as live data. | Queued from the dashboard, processed by the worker on its next cycle. **Bulk replay** queues every `New` dead letter matching a filter. |
| **Ignore** | Closing a dead letter as known bad data that won't be fixed. A note is required. | Still counts as explained in reconciliation. |
| **Reconciliation** | Proving nothing was lost: source total = in hub + open exceptions + ignored. | One row per source and entity: `Reconciled`, `ExplainedByExceptions`, or `Investigate` when the numbers don't add up. |
| **Duplicate candidate** | Two client records that are probably the same organisation, within one firm or across both. | Scored by name similarity (Jaro-Winkler, word by word) plus postcode. A data steward confirms or rejects each pair; nothing is merged automatically. |
| **Normalised name** | A name reduced to a standard form for comparison: "The Acme Holdings Limited" becomes "ACME HOLDINGS LTD". | Stored with each client, used only for matching. The displayed name stays as entered. |

## Dead-letter lifecycle

```
New ──(support fixes payload, queues replay)──> ReplayRequested ──(worker replays, merge succeeds)──> Resolved
 │                                                     └──(fails again)──> back to New, with the new error
 └──(support ignores, with a note)──> Ignored
```

A record that later arrives cleanly in a scheduled sync resolves its own dead letter automatically.

## Monitoring terms

| Term | What it means |
|---|---|
| **Metric** | A number tracked over time, e.g. records read per minute (Prometheus). |
| **Log** | A timestamped line of text with fields such as `Source` and `CorrelationId` (Loki). |
| **Gauge** | A metric that goes up and down, e.g. open dead letters right now. |
| **Counter** | A metric that only goes up, e.g. total records rejected. Graphs show its rate. |
| **Alert** | A rule that fires when a metric crosses a line, e.g. `SyncStale` (no successful sync for 10 minutes). |
| **Browser telemetry** | Errors and failed API calls seen in the user's browser, which the server never sees (Grafana Faro). |

## People and roles

| Role | Can do |
|---|---|
| **Viewer** | Read everything in the support dashboard. |
| **Support Analyst** | Viewer, plus fix, replay and ignore exceptions. |
| **Data Steward** | Viewer, plus decide duplicates and maintain practice-area mappings. |
| **Administrator** | Everything in the support dashboard. |
| **Directory admin** | Manage accounts in the directory admin centre: new starters, leavers (disabled accounts), groups, unlocks and password resets. |

Roles come from security groups in the mock corporate directory, as they would from Entra ID.

## How it fits together

A record is **read** from a source, **mapped** (or **dead-lettered**), **staged**, then **merged** into the hub,
and the **watermark** moves on. **Reconciliation** proves source and hub agree. When they don't, support works the
**exceptions**: fix and **replay**, or **ignore** with a reason. Separately, data stewards review **duplicate
candidates**, which matters for conflict checks after the merger.

[Back to the README](../README.md)
