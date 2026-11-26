# SSRS: Integration Exceptions report

A paginated operational report for the support team: what failed overnight, how old it is, and whether
the hub reconciles with the source systems. This is classic SSRS territory: a printable, emailable
report delivered on a schedule. The stored procedures already exist (`database/05_reporting.sql`);
these steps build the report on top of them in **Power BI Report Builder** or **SSRS Report Builder**.

Building it yourself (about 30–40 minutes) gives you something concrete to talk through in the interview.

---

## 1. Data source

1. Open Report Builder → **Blank Report**.
2. **Report Data** pane → **Data Sources** → right-click → **Add Data Source**.
   - Name: `SyncHub`
   - Use a connection embedded in my report → type **Microsoft SQL Server**
   - Connection string: `Data Source=localhost;Initial Catalog=SyncHub`
   - Credentials: **Use Windows Authentication (integrated security)**
3. Click **Test Connection**.

> In production you'd use a **shared data source** on the report server, so that a password change or
> server move is fixed once rather than in every report.

## 2. Datasets

### `Exceptions`
- **Query type:** Stored Procedure → `rpt.usp_SsrsExceptions`
- Report Builder creates the `@Source` and `@Status` parameters automatically.

### `Reconciliation`
- **Query type:** Stored Procedure → `ops.usp_GetReconciliation` (no parameters)

### `SourceList` (feeds the parameter drop-down)
- **Query type:** Text
  ```sql
  SELECT 'All' AS Source, 0 AS SortOrder
  UNION ALL SELECT 'FirmA', 1
  UNION ALL SELECT 'FirmB', 2
  ORDER BY SortOrder
  ```

## 3. Parameters

| Parameter | Prompt  | Available values                                   | Default |
|-----------|---------|----------------------------------------------------|---------|
| `@Source` | Source  | From query: `SourceList`, value and label `Source` | `All`   |
| `@Status` | Status  | Specify values: `Open`, `Resolved`, `Ignored`, `All` | `Open`  |

## 4. Layout

**Header:** a text box with `Integration Exceptions`, plus a second text box with this expression:
```
="Source: " & Parameters!Source.Value & "  |  Status: " & Parameters!Status.Value & "  |  Run at " & Format(Globals!ExecutionTime, "dd MMM yyyy HH:mm")
```

**Reconciliation table** (dataset `Reconciliation`): columns Source, Entity, SourceTotal, InHub,
OpenExceptions, Ignored, Unexplained, Status.
- Set **BackgroundColor** on the Status cell:
  ```
  =Switch(Fields!Status.Value = "Reconciled", "#E3F4EA",
          Fields!Status.Value = "ExplainedByExceptions", "#FDF1D8",
          True, "#FDE7E5")
  ```

**Exceptions table** (dataset `Exceptions`):
1. Insert a **Table**. Add columns: Entity, SourceKey, ErrorMessage, OccurrenceCount, AgeBand, LastSeenAtUtc.
2. Add a **parent group** on `Source`, then a child group on `ErrorType`, with group headers.
3. In the `ErrorType` group header, show the count: `=CountRows()`.
4. Colour the AgeBand cell so stale items stand out:
   ```
   =Switch(Fields!AgeBand.Value = "3+ days", "#FDE7E5",
           Fields!AgeBand.Value = "1-3 days", "#FDF1D8",
           True, "White")
   ```
5. Format `LastSeenAtUtc` as `dd/MM/yyyy HH:mm` and label the column "Last seen (UTC)".
6. Set **Repeat header rows on each page** (Tablix properties) so a printed report stays readable.
7. Add a **NoRowsMessage** on the table: `No exceptions. Integration is clean.`

**Footer:** `="Page " & Globals!PageNumber & " of " & Globals!TotalPages`

## 5. Second report (optional): Sync Run Summary

- Dataset: stored procedure `rpt.usp_SsrsSyncRunSummary` with `@Days` (integer, default `7`).
- Group by `SyncRunId`, with Entity rows as details. Highlight `Status = "Failed"` in red.
- Add a column chart of `RecordsInserted + RecordsUpdated` by day for a quick throughput view.

## 6. Deploy and schedule

1. **File → Save As** → your report server (for example `http://localhost/ReportServer`), in a `SyncHub` folder.
2. In the web portal (`http://localhost/Reports`), open the report → **Subscribe**:
   - Delivery: email (needs SMTP configured in Report Server Configuration Manager) or a **file share**
   - Schedule: weekdays 07:30
   - Render format: PDF
   - Parameters: Source = `All`, Status = `Open`
3. A **data-driven subscription** (Enterprise edition) could send each team only its own source's exceptions.

---

## Talking points

- **Why a stored procedure rather than SQL in the report?** The query is version-controlled with the
  database, can be tuned in one place, can be reused by the API, and needs only EXECUTE permission
  rather than SELECT on the tables.
- **Why SSRS for this and not Power BI?** It's an operational list people act on: pixel-perfect,
  printable and pushed out on a schedule. Power BI is better for exploring trends. Power BI now runs
  paginated (.rdl) reports too, so this same report could move to the Power BI service unchanged.
- **Parameter sniffing:** `@Source = 'All'` and a specific source want different plans. At this size it
  doesn't matter. At scale, `OPTION (RECOMPILE)` or separate branches avoid one cached plan serving both badly.
