# Power BI: Combined Firm Overview

An interactive report on the combined client and matter register: the view leadership wants after a
merger ("how many shared clients do we have?", "what does the combined practice mix look like?"),
plus a data-quality page showing the integration is under control.

The **semantic model is built** and committed as a Power BI project (`MergerSyncHub.pbip`). The model is stored
as text (TMDL) under `MergerSyncHub.SemanticModel/definition/`, so every table, relationship, measure and
security rule can be read and reviewed in a diff. The **report pages** are designed by hand in Power BI
Desktop (section 4).

---

## 1. Open it

1. Make sure SQL Server is running and the reporting login exists (`REPORTING_SQL_PASSWORD` in `.env`, then
   `docker compose up -d` or `database\setup.ps1`; see the main README).
2. Open **`reports/powerbi/MergerSyncHub.pbip`** in Power BI Desktop.
3. **Home → Refresh.** When asked for credentials for `localhost,14333;SyncHub`, choose **Database** and enter
   `synchub_reporting` with the password from `.env`. Power BI keeps credentials in your Windows profile,
   never in these files.

To point at another server, change the `SqlServer` / `SqlDatabase` parameters (**Transform data → Edit parameters**).

| Login | Can do | Can't do |
|---|---|---|
| `synchub_reporting` (`database/07_reporting_login.sql`) | Read the `rpt` schema; run the two SSRS procedures | Read `core`/`ops`, write anything, reload the reporting tables |

## 2. What's in the model

**Import mode.** The data is small and changes every few minutes, so a scheduled refresh is fine and every DAX
feature is available. DirectQuery makes sense when data must be live or is too big to import.

**Star schema.** Dimensions filter facts, single direction:

| From (many)                     | To (one)                    | Active | Notes |
|---------------------------------|-----------------------------|--------|-------|
| `FactMatter[OpenedDateKey]`     | `DimDate[DateKey]`          | Yes    | |
| `FactMatter[ClosedDateKey]`     | `DimDate[DateKey]`          | **No** | switched on by `Matters Closed` (`USERELATIONSHIP`) |
| `FactMatter[ClientKey]`         | `DimClient[ClientKey]`      | Yes    | |
| `FactMatter[FirmKey]`           | `DimFirm[FirmKey]`          | Yes    | |
| `FactMatter[PracticeAreaKey]`   | `DimPracticeArea[PracticeAreaKey]` | Yes | |
| `FactDataQualityDaily[FirmKey]` | `DimFirm[FirmKey]`          | Yes    | |

- `DimDate` is marked as the date table; month names sort by month number; key columns are hidden.
- `UserFirmAccess` is hidden and unrelated: only the security role reads it.
- Model settings: **implicit measures off** (report builders use the defined measures, not raw columns) and
  **auto date/time off** (`DimDate` does that job, without a hidden date table per date column).

**17 measures** in `_Measures` ([`_Measures.tmdl`](MergerSyncHub.SemanticModel/definition/tables/_Measures.tmdl)),
in three display folders: **Volume** (matters, open, opened/closed, financial YTD, vs prior year, days open),
**Clients** (records, unique after de-duplication, duplicate %, shared across both firms, top client) and
**Data quality** (unmapped, latest exceptions and duplicate reviews, a 0-100 score). Each has a description.

Two measures differ from a first draft, for reasons worth knowing:
- **Client counts respect the firm filter.** `DimClient` has no relationship to `DimFirm` (firm reaches matters only),
  so a plain `DISTINCTCOUNT` showed every client under every firm. `Client Records` and `Unique Clients` apply the
  firm through `DimClient[Source]` with `TREATAS`.
- **Financial YTD doesn't use `TOTALYTD(..., "31/3")`.** That year-end string is read through the model's culture
  (`en-US` here), where `31/3` isn't a date. The measure uses `DimDate[FinancialYear]` instead, so it works in any locale.

## 3. Row-level security

Role **`FirmAccess`** restricts each user to the legacy firms listed for them in `UserFirmAccess`, which the risk
team would maintain. It filters both `DimFirm` (which flows to every fact) and `DimClient` (so the other firm's
client list isn't visible either):

```dax
[Source]
    IN CALCULATETABLE (
        VALUES ( UserFirmAccess[Source] ),
        UserFirmAccess[UserPrincipalName] = USERPRINCIPALNAME ()
    )
```

**Test it:** Modeling → **View as** → tick `FirmAccess` and **Other user**:

| User | Sees | Matters | Clients |
|---|---|---|---|
| `uk.partner@example.com` | Firm A | 75,219 | 376 |
| `us.partner@example.com` | Firm B | 5,039 | 256 |
| `coo@example.com` | both | 80,258 | 632 |
| anyone not listed | nothing | – | – |

(Figures from the demo data on 26 Nov 2026; they grow as the worker syncs.)

> In a law firm, RLS ties into **ethical walls**: information barriers that stop a team seeing a
> client's matters when there's a conflict. The pattern is the same (a security table maintained by
> the conflicts or risk team, applied with a DAX filter), with matter-level rather than firm-level rows.

## 4. Pages (build in Power BI Desktop)

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

`Shared Clients (Both Firms)` stays blank until a data steward confirms a cross-firm duplicate pair in the
support dashboard and the reporting tables reload.

Save with **Ctrl+S** while the `.pbip` is open. (A `.pbix` saved alongside is ignored by git: it's a binary
copy of the same model with the data inside.)

## 5. Publish and refresh

- Publish to a workspace. Because SQL Server is on-premises, the service needs an
  **on-premises data gateway** to refresh. Install it, add the `SyncHub` data source with the
  `synchub_reporting` login, and schedule the refresh.
- Assign users to the `FirmAccess` role in the semantic model's **Security** settings. RLS only applies to
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
- **Model as code (PBIP/TMDL):** the model is reviewed like code, and a measure change shows as a one-line diff.
- **Least privilege:** Power BI reads through a login that can see only the reporting schema.
- **Verified against the source:** every headline figure was checked against SQL with the same login, which is
  how the client-count bug above was found.
