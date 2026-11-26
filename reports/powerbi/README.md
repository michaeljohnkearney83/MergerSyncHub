# Power BI: Combined Firm Overview

An interactive report on the combined client and matter register: the view leadership wants after a
merger ("how many shared clients do we have?", "what does the combined practice mix look like?"),
plus a data-quality page showing the integration is under control.

Build time: about 45–60 minutes in Power BI Desktop.

---

## 1. Connect

1. **Home → Get data → SQL Server**
   - Server: `localhost` · Database: `SyncHub`
   - Data connectivity mode: **Import**
2. Under the `rpt` schema select: `DimDate`, `DimFirm`, `DimPracticeArea`, `DimClient`, `FactMatter`,
   `FactDataQualityDaily`, `UserFirmAccess`.
3. **Transform data:** rename each query to drop the `rpt ` prefix (so `rpt FactMatter` becomes `FactMatter`).
   That's all the shaping needed: the SQL views already do the work, which keeps Power Query simple
   and lets query folding push everything back to SQL Server.

> **Import vs DirectQuery:** Import is faster and supports all DAX. The data is small and only changes
> every few minutes, so a scheduled refresh is fine. DirectQuery makes sense when data must be live or is too big to import.

## 2. Model (Model view)

Build a star schema: dimensions filter facts, single direction.

| From (many)                     | To (one)                    | Active | Notes |
|---------------------------------|-----------------------------|--------|-------|
| `FactMatter[OpenedDateKey]`     | `DimDate[DateKey]`          | Yes    | |
| `FactMatter[ClosedDateKey]`     | `DimDate[DateKey]`          | **No** | used via `USERELATIONSHIP` |
| `FactMatter[ClientKey]`         | `DimClient[ClientKey]`      | Yes    | |
| `FactMatter[FirmKey]`           | `DimFirm[FirmKey]`          | Yes    | |
| `FactMatter[PracticeAreaKey]`   | `DimPracticeArea[PracticeAreaKey]` | Yes | |
| `FactDataQualityDaily[FirmKey]` | `DimFirm[FirmKey]`          | Yes    | |

Then:
- Mark `DimDate` as the **date table** (Table tools → Mark as date table → `Date`).
- Sort `DimDate[MonthName]` by `DimDate[Month]`.
- Hide all key columns from report view.
- `UserFirmAccess` stays **unrelated** (used only by row-level security, below).

## 3. Measures

Create a `_Measures` table and paste the measures from [`measures.dax`](measures.dax) one at a time.

## 4. Pages

**Page 1: Combined Firm Overview**
- Cards: `Unique Clients`, `Shared Clients (Both Firms)`, `Open Matters`, `Matters Opened YTD (Financial)`
- Clustered bar: `Total Matters` by `DimPracticeArea[PracticeArea]`, legend `DimFirm[FirmName]`
  → shows how the two firms' practices complement each other
- Line chart: `Matters Opened` by `DimDate[YearMonth]`, legend `DimFirm[FirmName]`
- Table: top 20 `DimClient[MasterClientName]` by `Total Matters`, with `GroupSpansFirms`
- Slicers: `DimFirm[FirmName]`, `DimDate[FinancialYear]`, `DimClient[Country]`

**Page 2: Data Quality**
- Cards: `Data Quality Score`, `Open Exceptions (Latest)`, `Pending Duplicate Reviews (Latest)`, `Unmapped Matters %`
- Line chart: `FactDataQualityDaily[OpenExceptions]` by `SnapshotDate`, legend `FirmName`
  (fills in over the days the worker runs)
- Matrix: `DimPracticeArea[PracticeArea]` × `DimFirm[FirmName]` with `Total Matters`. The `Unmapped`
  row shows exactly what the Mappings tab in the dashboard fixes.

## 5. Row-level security

The firm wouldn't want everyone seeing every client. This demo restricts users by legacy firm.

1. **Modeling → Manage roles → New role** `FirmAccess`
2. Table `DimFirm`, DAX filter:
   ```dax
   [Source] IN
       CALCULATETABLE (
           VALUES ( UserFirmAccess[Source] ),
           UserFirmAccess[UserPrincipalName] = USERPRINCIPALNAME ()
       )
   ```
3. **Test:** Modeling → **View as** → tick `FirmAccess` and **Other user** = `uk.partner@example.com`.
   Only Firm A data appears. `coo@example.com` sees both.

Because `DimFirm` filters `FactMatter`, the restriction flows to every visual. (`DimClient` isn't filtered
by firm through the model. In a real deployment you'd add `Source` to the role filter on `DimClient` as well.)

> In a law firm, RLS ties into **ethical walls**: information barriers that stop a team seeing a
> client's matters when there's a conflict. The pattern is the same (a security table maintained by
> the conflicts or risk team, applied with a DAX filter), with matter-level rather than firm-level rows.

## 6. Publish and refresh

- Publish to a workspace. Because SQL Server is on-premises, the service needs an
  **on-premises data gateway** to refresh. Install it, add the `SyncHub` data source, and schedule the refresh.
- Assign users to the `FirmAccess` role in the dataset's **Security** settings. RLS only applies to
  users with Viewer access; workspace Admins, Members and Contributors bypass it.

---

## Talking points

- **Star schema over one flat table:** smaller model, simpler DAX, faster visuals, and the same
  dimensions serve every fact.
- **Master client:** confirmed duplicates share a `MasterClientKey`, so "unique clients" is correct across
  both firms without deleting any source record. The grouping handles chains (A=B, B=C) in SQL.
- **Practice area mapping at load time:** matters keep their raw department code, so fixing a mapping
  corrects historical reporting on the next refresh, with no re-sync needed.
- **Snapshot measures:** `FactDataQualityDaily` is a daily snapshot, so it must not be summed across
  dates. The "Latest" measures take the most recent day.
