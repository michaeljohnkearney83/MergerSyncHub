using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using SyncHub.Core.Mapping;
using SyncHub.Core.Models;
using SyncHub.Core.Sources.FirmA;
using SyncHub.Worker.Data;

namespace SyncHub.Worker.Sources;

/// <summary>
/// Reads changed records from one source system. Connectors only fetch: they do not
/// validate or transform, so all business rules live in the mappers (and are tested there).
/// </summary>
public interface ISourceConnector
{
    string Source { get; }

    /// <summary>How many records the source holds in total, for reconciliation.</summary>
    Task<int> GetSourceTotalAsync(string entity, CancellationToken ct);

    /// <summary>Records changed at or after <paramref name="since"/> (all records when null), oldest first.</summary>
    IAsyncEnumerable<SourceRecord> ReadChangesAsync(string entity, DateTime? since, CancellationToken ct);
}

/* ===========================================================================
   Firm A: direct, read-only SQL against the legacy database
=========================================================================== */
public sealed class FirmAConnector(SqlConnectionFactory connections) : ISourceConnector
{
    private const string Db = "FirmA";

    // Two versions of each query rather than "WHERE @Since IS NULL OR ...":
    // the OR form stops SQL Server using an index seek on LAST_MODIFIED.
    private const string ClientColumns = """
        SELECT CLIENT_ID AS ClientId, CLIENT_CODE AS ClientCode, CLIENT_NAME AS ClientName,
               ADDRESS_1 AS Address1, POSTCODE AS Postcode, COUNTRY AS Country,
               OPEN_DATE AS OpenDate, STATUS AS Status, LAST_MODIFIED AS LastModified
          FROM dbo.CLIENT
        """;

    private const string MatterColumns = """
        SELECT MATTER_ID AS MatterId, CLIENT_ID AS ClientId, MATTER_NO AS MatterNo,
               DESCRIPTION AS Description, DEPT_CODE AS DeptCode, FEE_EARNER AS FeeEarner,
               OPEN_DATE AS OpenDate, CLOSE_DATE AS CloseDate, STATUS AS Status, LAST_MODIFIED AS LastModified
          FROM dbo.MATTER
        """;

    public string Source => SourceSystems.FirmA;

    /// <inheritdoc/>
    public async Task<int> GetSourceTotalAsync(string entity, CancellationToken ct)
    {
        var sql = entity == Entities.Client ? "SELECT COUNT(*) FROM dbo.CLIENT" : "SELECT COUNT(*) FROM dbo.MATTER";
        await using var conn = connections.Create(Db);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(sql, cancellationToken: ct));
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<SourceRecord> ReadChangesAsync(string entity, DateTime? since, CancellationToken ct) =>
        entity switch
        {
            Entities.Client => ReadAsync<FirmAClientRow>(ClientColumns, "CLIENT_ID", since,
                r => r.ClientId, r => r.LastModified, Entities.Client, ct),
            Entities.Matter => ReadAsync<FirmAMatterRow>(MatterColumns, "MATTER_ID", since,
                r => r.MatterId, r => r.LastModified, Entities.Matter, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(entity), entity, null)
        };

    /// <summary>
    /// Streams rows changed since <paramref name="since"/> (all rows when null), oldest first, each serialised to
    /// JSON so the mapper gets a raw payload it can also store for replay. Two query shapes instead of
    /// "@Since IS NULL OR ..." let SQL Server seek the LAST_MODIFIED index.
    /// </summary>
    private async IAsyncEnumerable<SourceRecord> ReadAsync<TRow>(
        string select, string idColumn, DateTime? since,
        Func<TRow, int> key, Func<TRow, DateTime> modified, string entity,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var sql = since is null
            ? $"{select} ORDER BY LAST_MODIFIED, {idColumn}"
            : $"{select} WHERE LAST_MODIFIED >= @Since ORDER BY LAST_MODIFIED, {idColumn}";

        await using var conn = connections.Create(Db);
        await conn.OpenAsync(ct);

        // Unbuffered: rows stream from SQL Server instead of loading 75k rows into memory first
        await foreach (var row in conn.QueryUnbufferedAsync<TRow>(sql, new { Since = since }).WithCancellation(ct))
        {
            yield return new SourceRecord(
                Source,
                entity,
                key(row).ToString(CultureInfo.InvariantCulture),
                JsonSerializer.Serialize(row, SyncJson.Options),
                modified(row));
        }
    }
}

/* ===========================================================================
   Firm B: paged REST API. Retries, timeouts and the circuit breaker are added
   to the HttpClient in Program.cs, so this class stays focused on paging.
=========================================================================== */
public sealed class FirmBConnector(HttpClient http, IOptions<FirmBOptions> options, ILogger<FirmBConnector> logger)
    : ISourceConnector
{
    public string Source => SourceSystems.FirmB;

    /// <inheritdoc/>
    public async Task<int> GetSourceTotalAsync(string entity, CancellationToken ct)
    {
        var stats = await http.GetFromJsonAsync<FirmBStats>("api/stats", SyncJson.Options, ct)
                    ?? throw new InvalidOperationException("Firm B returned empty stats.");
        return entity == Entities.Client ? stats.Clients : stats.Matters;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<SourceRecord> ReadChangesAsync(string entity, DateTime? since,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var resource = entity == Entities.Client ? "clients" : "matters";
        var pageSize = options.Value.PageSize;
        var page = 1;
        bool hasMore;

        do
        {
            var url = $"api/{resource}?page={page}&pageSize={pageSize}";
            if (since is not null)
            {
                var sinceUtc = DateTime.SpecifyKind(since.Value, DateTimeKind.Utc);
                url += $"&modifiedSince={Uri.EscapeDataString(sinceUtc.ToString("O", CultureInfo.InvariantCulture))}";
            }

            using var response = await http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();   // anything still failing after retries fails the run

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var items = root.GetProperty("items");
            logger.LogDebug("Firm B {Resource} page {Page}: {Count} items", resource, page, items.GetArrayLength());

            foreach (var item in items.EnumerateArray())
            {
                // Keep the raw JSON of each item. If one record is bad, only that record fails.
                var key = item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString()!
                    : "(missing id)";
                var lastModified = item.TryGetProperty("lastModifiedUtc", out var lm) && lm.TryGetDateTime(out var parsed)
                    ? parsed.ToUniversalTime()
                    : DateTime.MinValue;

                yield return new SourceRecord(Source, entity, key, item.GetRawText(), lastModified);
            }

            hasMore = root.TryGetProperty("hasMore", out var more) && more.GetBoolean();
            page++;
        } while (hasMore);
    }

    /// <summary>Shape of Firm B's <c>/api/stats</c> response.</summary>
    private sealed record FirmBStats(int Clients, int Matters);
}
