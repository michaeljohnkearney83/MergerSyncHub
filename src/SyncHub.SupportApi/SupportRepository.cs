using Dapper;
using Microsoft.Data.SqlClient;

namespace SyncHub.SupportApi;

/* ---------------- Response shapes ---------------- */

public sealed class Summary
{
    public int Clients { get; set; }
    public int Matters { get; set; }
    public int OpenExceptions { get; set; }
    public int PendingDuplicates { get; set; }
    public int FailedRuns24h { get; set; }
    public DateTime? LastSuccessfulSyncUtc { get; set; }
}

public sealed class RunRow
{
    public long SyncRunId { get; set; }
    public string Source { get; set; } = "";
    public string RunType { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public double? DurationSeconds { get; set; }
    public string? ErrorMessage { get; set; }
    public Guid CorrelationId { get; set; }
    public int RecordsRead { get; set; }
    public int RecordsInserted { get; set; }
    public int RecordsUpdated { get; set; }
    public int RecordsFailed { get; set; }
}

public sealed class RunEntityRow
{
    public string Entity { get; set; } = "";
    public int? SourceTotal { get; set; }
    public int RecordsRead { get; set; }
    public int RecordsStaged { get; set; }
    public int RecordsInserted { get; set; }
    public int RecordsUpdated { get; set; }
    public int RecordsFailed { get; set; }
}

public sealed class DeadLetterRow
{
    public long DeadLetterId { get; set; }
    public string Source { get; set; } = "";
    public string Entity { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public string? RelatedClientKey { get; set; }
    public string ErrorType { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public string Status { get; set; } = "";
    public int OccurrenceCount { get; set; }
    public int ReplayAttempts { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
}

public sealed class DeadLetterDetail
{
    public long DeadLetterId { get; set; }
    public long FirstSyncRunId { get; set; }
    public long LastSyncRunId { get; set; }
    public string Source { get; set; } = "";
    public string Entity { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public string? RelatedClientKey { get; set; }
    public string ErrorType { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public string Payload { get; set; } = "";
    public string Status { get; set; } = "";
    public int OccurrenceCount { get; set; }
    public int ReplayAttempts { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
    public string? PayloadEditedBy { get; set; }
    public DateTime? PayloadEditedAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public string? ResolutionNote { get; set; }
}

public sealed class ErrorTypeCount
{
    public string Source { get; set; } = "";
    public string ErrorType { get; set; } = "";
    public int Count { get; set; }
}

public sealed class DuplicateRow
{
    public int CandidateId { get; set; }
    public decimal Score { get; set; }
    public string MatchReason { get; set; } = "";
    public string Status { get; set; } = "";
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    public int ClientIdA { get; set; }
    public string SourceA { get; set; } = "";
    public string KeyA { get; set; } = "";
    public string NameA { get; set; } = "";
    public string? CountryA { get; set; }
    public string? PostCodeA { get; set; }
    public int MattersA { get; set; }
    public int ClientIdB { get; set; }
    public string SourceB { get; set; } = "";
    public string KeyB { get; set; } = "";
    public string NameB { get; set; } = "";
    public string? CountryB { get; set; }
    public string? PostCodeB { get; set; }
    public int MattersB { get; set; }
}

public sealed class ReconciliationRow
{
    public string Source { get; set; } = "";
    public string Entity { get; set; } = "";
    public int SourceTotal { get; set; }
    public int InHub { get; set; }
    public int OpenExceptions { get; set; }
    public int Ignored { get; set; }
    public int Unexplained { get; set; }
    public string Status { get; set; } = "";
    public long AsOfSyncRunId { get; set; }
    public DateTime AsOfUtc { get; set; }
}

public sealed class ClientLookup
{
    public int ClientId { get; set; }
    public string SourceClientKey { get; set; } = "";
    public string ClientName { get; set; } = "";
    public string? PostCode { get; set; }
}

public sealed class UnmappedCode
{
    public string Source { get; set; } = "";
    public string? PracticeAreaCode { get; set; }
    public int Matters { get; set; }
}

/* ---------------- Data access ---------------- */

public sealed class SupportRepository(IConfiguration configuration)
{
    private SqlConnection Open() =>
        new(configuration.GetConnectionString("SyncHub")
            ?? throw new InvalidOperationException("Connection string 'SyncHub' is missing."));

    private const string OpenStatuses = "('New','ReplayRequested')";

    public async Task<Summary> GetSummaryAsync()
    {
        await using var conn = Open();
        return await conn.QuerySingleAsync<Summary>($"""
            SELECT
                Clients               = (SELECT COUNT(*) FROM core.Client),
                Matters               = (SELECT COUNT(*) FROM core.Matter),
                OpenExceptions        = (SELECT COUNT(*) FROM ops.DeadLetter WHERE Status IN {OpenStatuses}),
                PendingDuplicates     = (SELECT COUNT(*) FROM core.ClientMatchCandidate WHERE Status = 'Pending'),
                FailedRuns24h         = (SELECT COUNT(*) FROM ops.SyncRun WHERE Status = 'Failed'
                                           AND StartedAtUtc >= DATEADD(HOUR, -24, SYSUTCDATETIME())),
                LastSuccessfulSyncUtc = (SELECT MAX(FinishedAtUtc) FROM ops.SyncRun
                                          WHERE RunType = 'Scheduled' AND Status IN ('Succeeded','SucceededWithErrors'))
            """);
    }

    public async Task<IEnumerable<RunRow>> GetRunsAsync(int top, bool hideQuiet)
    {
        await using var conn = Open();
        return await conn.QueryAsync<RunRow>("""
            SELECT TOP (@Top)
                   r.SyncRunId, r.Source, r.RunType, r.Status, r.StartedAtUtc, r.FinishedAtUtc,
                   DurationSeconds = CAST(DATEDIFF(MILLISECOND, r.StartedAtUtc, r.FinishedAtUtc) / 1000.0 AS float),
                   r.ErrorMessage, r.CorrelationId,
                   RecordsRead     = ISNULL(SUM(e.RecordsRead), 0),
                   RecordsInserted = ISNULL(SUM(e.RecordsInserted), 0),
                   RecordsUpdated  = ISNULL(SUM(e.RecordsUpdated), 0),
                   RecordsFailed   = ISNULL(SUM(e.RecordsFailed), 0)
              FROM ops.SyncRun r
              LEFT JOIN ops.SyncRunEntity e ON e.SyncRunId = r.SyncRunId
             GROUP BY r.SyncRunId, r.Source, r.RunType, r.Status, r.StartedAtUtc, r.FinishedAtUtc, r.ErrorMessage, r.CorrelationId
            HAVING @HideQuiet = 0
                OR r.Status <> 'Succeeded'
                OR ISNULL(SUM(e.RecordsInserted + e.RecordsUpdated + e.RecordsFailed), 0) > 0
             ORDER BY r.SyncRunId DESC
            """, new { Top = top, HideQuiet = hideQuiet });
    }

    public async Task<IEnumerable<RunEntityRow>> GetRunEntitiesAsync(long syncRunId)
    {
        await using var conn = Open();
        return await conn.QueryAsync<RunEntityRow>("""
            SELECT Entity, SourceTotal, RecordsRead, RecordsStaged, RecordsInserted, RecordsUpdated, RecordsFailed
              FROM ops.SyncRunEntity WHERE SyncRunId = @SyncRunId ORDER BY Entity
            """, new { SyncRunId = syncRunId });
    }

    /// <summary>
    /// A varchar parameter. Dapper sends C# strings as nvarchar(4000) by default; compared with a varchar
    /// column, SQL Server converts the column side (CONVERT_IMPLICIT) and can no longer seek the index.
    /// Matching the column's type and length keeps the seek and gives one cached plan per query.
    /// </summary>
    private static DbString Ansi(string? value, int length) => new() { Value = value, IsAnsi = true, Length = length };

    public async Task<IEnumerable<DeadLetterRow>> GetDeadLettersAsync(string status, string? source, string? errorType)
    {
        await using var conn = Open();
        return await conn.QueryAsync<DeadLetterRow>($"""
            SELECT TOP (500)
                   DeadLetterId, Source, Entity, SourceKey, RelatedClientKey, ErrorType, ErrorMessage, Status,
                   OccurrenceCount, ReplayAttempts, CreatedAtUtc, LastSeenAtUtc, ResolvedAtUtc
              FROM ops.DeadLetter
             WHERE (@Status = 'All' OR (@Status = 'Open' AND Status IN {OpenStatuses}) OR Status = @Status)
               AND (@Source IS NULL OR Source = @Source)
               AND (@ErrorType IS NULL OR ErrorType = @ErrorType)
             ORDER BY LastSeenAtUtc DESC, DeadLetterId DESC
            """, new { Status = Ansi(status, 20), Source = Ansi(source, 20), ErrorType = Ansi(errorType, 50) });
    }

    public async Task<IEnumerable<ErrorTypeCount>> GetOpenErrorTypesAsync()
    {
        await using var conn = Open();
        return await conn.QueryAsync<ErrorTypeCount>($"""
            SELECT Source, ErrorType, Count = COUNT(*)
              FROM ops.DeadLetter WHERE Status IN {OpenStatuses}
             GROUP BY Source, ErrorType ORDER BY COUNT(*) DESC
            """);
    }

    public async Task<DeadLetterDetail?> GetDeadLetterAsync(long id)
    {
        await using var conn = Open();
        return await conn.QuerySingleOrDefaultAsync<DeadLetterDetail>(
            "SELECT * FROM ops.DeadLetter WHERE DeadLetterId = @Id", new { Id = id });
    }

    public async Task<bool> UpdatePayloadAsync(long id, string payload, string user, string? note)
    {
        await using var conn = Open();
        var rows = await conn.ExecuteAsync("""
            UPDATE ops.DeadLetter
               SET Payload = @Payload, PayloadEditedBy = @User, PayloadEditedAtUtc = SYSUTCDATETIME(),
                   ResolutionNote = LEFT(CONCAT(ResolutionNote + N' | ', N'Payload edited by ', @User,
                                                CASE WHEN @Note IS NULL THEN N'' ELSE CONCAT(N': ', @Note) END), 1000)
             WHERE DeadLetterId = @Id AND Status = 'New'
            """, new { Id = id, Payload = payload, User = user, Note = note });
        return rows == 1;
    }

    public async Task<int> RequestReplayAsync(long? id, string? source, string? errorType, string user)
    {
        await using var conn = Open();
        return await conn.ExecuteAsync("""
            UPDATE ops.DeadLetter
               SET Status = 'ReplayRequested',
                   ResolutionNote = LEFT(CONCAT(ResolutionNote + N' | ', N'Replay requested by ', @User), 1000)
             WHERE Status = 'New'
               AND (@Id IS NULL OR DeadLetterId = @Id)
               AND (@Source IS NULL OR Source = @Source)
               AND (@ErrorType IS NULL OR ErrorType = @ErrorType)
            """, new { Id = id, Source = Ansi(source, 20), ErrorType = Ansi(errorType, 50), User = user });
    }

    public async Task<bool> IgnoreAsync(long id, string user, string note)
    {
        await using var conn = Open();
        var rows = await conn.ExecuteAsync("""
            UPDATE ops.DeadLetter
               SET Status = 'Ignored', ResolvedAtUtc = SYSUTCDATETIME(),
                   ResolutionNote = LEFT(CONCAT(ResolutionNote + N' | ', N'Ignored by ', @User, N': ', @Note), 1000)
             WHERE DeadLetterId = @Id AND Status IN ('New','ReplayRequested')
            """, new { Id = id, User = user, Note = note });
        return rows == 1;
    }

    public async Task<IEnumerable<DuplicateRow>> GetDuplicatesAsync(string status)
    {
        await using var conn = Open();
        return await conn.QueryAsync<DuplicateRow>("""
            SELECT TOP (500)
                   c.CandidateId, c.Score, c.MatchReason, c.Status, c.ReviewedBy, c.ReviewedAtUtc,
                   ClientIdA = a.ClientId, SourceA = a.Source, KeyA = a.SourceClientKey, NameA = a.ClientName,
                   CountryA = a.Country, PostCodeA = a.PostCode,
                   MattersA = (SELECT COUNT(*) FROM core.Matter m WHERE m.ClientId = a.ClientId),
                   ClientIdB = b.ClientId, SourceB = b.Source, KeyB = b.SourceClientKey, NameB = b.ClientName,
                   CountryB = b.Country, PostCodeB = b.PostCode,
                   MattersB = (SELECT COUNT(*) FROM core.Matter m WHERE m.ClientId = b.ClientId)
              FROM core.ClientMatchCandidate c
              JOIN core.Client a ON a.ClientId = c.ClientIdA
              JOIN core.Client b ON b.ClientId = c.ClientIdB
             WHERE @Status = 'All' OR c.Status = @Status
             ORDER BY c.Score DESC, c.CandidateId
            """, new { Status = Ansi(status, 20) });
    }

    public async Task<bool> DecideDuplicateAsync(int candidateId, string decision, string user)
    {
        await using var conn = Open();
        var rows = await conn.ExecuteAsync("""
            UPDATE core.ClientMatchCandidate
               SET Status = @Decision,
                   ReviewedBy    = CASE WHEN @Decision = 'Pending' THEN NULL ELSE @User END,
                   ReviewedAtUtc = CASE WHEN @Decision = 'Pending' THEN NULL ELSE SYSUTCDATETIME() END
             WHERE CandidateId = @Id
            """, new { Id = candidateId, Decision = decision, User = user });
        return rows == 1;
    }

    /// <summary>Lookup for the correction form: find a client in the hub by name or key, within one source.</summary>
    public async Task<IEnumerable<ClientLookup>> SearchClientsAsync(string source, string search)
    {
        // Escape LIKE wildcards so a search for "100%" means the text, not a pattern
        var escaped = search.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
        await using var conn = Open();
        return await conn.QueryAsync<ClientLookup>("""
            SELECT TOP (20) ClientId, SourceClientKey, ClientName, PostCode
              FROM core.Client
             WHERE Source = @Source
               AND (ClientName LIKE @Pattern OR SourceClientKey = @Search)
             ORDER BY CASE WHEN SourceClientKey = @Search THEN 0 ELSE 1 END, ClientName
            """, // Pattern stays Unicode: it is compared with ClientName, which is nvarchar.
            new { Source = Ansi(source, 20), Search = Ansi(search, 50), Pattern = $"%{escaped}%" });
    }

    public async Task<IEnumerable<ReconciliationRow>> GetReconciliationAsync()
    {
        await using var conn = Open();
        return await conn.QueryAsync<ReconciliationRow>("ops.usp_GetReconciliation",
            commandType: System.Data.CommandType.StoredProcedure);
    }

    public async Task<IEnumerable<UnmappedCode>> GetUnmappedPracticeAreasAsync()
    {
        await using var conn = Open();
        return await conn.QueryAsync<UnmappedCode>("""
            SELECT m.Source, m.PracticeAreaCode, Matters = COUNT(*)
              FROM core.Matter m
              LEFT JOIN core.PracticeAreaMap pm ON pm.Source = m.Source AND pm.SourceCode = m.PracticeAreaCode
             WHERE pm.SourceCode IS NULL
             GROUP BY m.Source, m.PracticeAreaCode
             ORDER BY COUNT(*) DESC
            """);
    }

    public async Task<IEnumerable<string>> GetPracticeAreasAsync()
    {
        await using var conn = Open();
        return await conn.QueryAsync<string>("SELECT DISTINCT PracticeArea FROM core.PracticeAreaMap ORDER BY PracticeArea");
    }

    public async Task AddPracticeAreaMappingAsync(string source, string sourceCode, string practiceArea)
    {
        await using var conn = Open();
        await conn.ExecuteAsync("""
            IF NOT EXISTS (SELECT 1 FROM core.PracticeAreaMap WHERE Source = @Source AND SourceCode = @SourceCode)
                INSERT core.PracticeAreaMap (Source, SourceCode, PracticeArea) VALUES (@Source, @SourceCode, @PracticeArea);
            ELSE
                UPDATE core.PracticeAreaMap SET PracticeArea = @PracticeArea WHERE Source = @Source AND SourceCode = @SourceCode;
            """, new { Source = Ansi(source, 20), SourceCode = Ansi(sourceCode, 30), PracticeArea = practiceArea });
    }
}
