using Dapper;
using Microsoft.Data.SqlClient;

namespace CorpDirectory;

/* ---------------- Shapes ---------------- */

public sealed class UserRecord
{
    public Guid UserId { get; set; }
    public string UserPrincipalName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string GivenName { get; set; } = "";
    public string Surname { get; set; } = "";
    public string? JobTitle { get; set; }
    public string? Department { get; set; }
    public string? Office { get; set; }
    public string? EmployeeId { get; set; }
    public bool AccountEnabled { get; set; }
    public string PasswordHash { get; set; } = "";
    public bool MustChangePassword { get; set; }
    public int FailedSignInCount { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public DateTime? LastSignInUtc { get; set; }
    public DateTime? PasswordChangedUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class UserSummary
{
    public Guid UserId { get; set; }
    public string UserPrincipalName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? JobTitle { get; set; }
    public string? Department { get; set; }
    public string? Office { get; set; }
    public bool AccountEnabled { get; set; }
    public bool MustChangePassword { get; set; }
    public bool IsLocked { get; set; }
    public DateTime? LastSignInUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? Groups { get; set; }
}

public sealed class UserDetail
{
    public Guid UserId { get; set; }
    public string UserPrincipalName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string GivenName { get; set; } = "";
    public string Surname { get; set; } = "";
    public string? JobTitle { get; set; }
    public string? Department { get; set; }
    public string? Office { get; set; }
    public string? EmployeeId { get; set; }
    public bool AccountEnabled { get; set; }
    public bool MustChangePassword { get; set; }
    public int FailedSignInCount { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public bool IsLocked { get; set; }
    public DateTime? LastSignInUtc { get; set; }
    public DateTime? PasswordChangedUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime UpdatedAtUtc { get; set; }
    public int[] GroupIds { get; set; } = [];
}

public sealed class GroupRow
{
    public int GroupId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string AppRole { get; set; } = "";
    public int Members { get; set; }
}

public sealed class AuditRow
{
    public long AuditId { get; set; }
    public DateTime AtUtc { get; set; }
    public string Actor { get; set; } = "";
    public string Action { get; set; } = "";
    public string? TargetUpn { get; set; }
    public string? Detail { get; set; }
}

public sealed record NewUser(string UserPrincipalName, string GivenName, string Surname, string? JobTitle,
    string? Department, string? Office, string? EmployeeId, string PasswordHash, bool MustChangePassword);

public sealed record ProfileUpdate(string GivenName, string Surname, string? JobTitle, string? Department,
    string? Office, string? EmployeeId);

/* ---------------- Data access ---------------- */

public sealed class DirectoryRepository(IConfiguration configuration)
{
    private SqlConnection Open() =>
        new(configuration.GetConnectionString("Directory")
            ?? throw new InvalidOperationException("Connection string 'Directory' is missing."));

    private const string IsLockedSql = "CAST(CASE WHEN u.LockedUntilUtc > SYSUTCDATETIME() THEN 1 ELSE 0 END AS bit)";

    /* ----- sign-in ----- */

    public async Task<UserRecord?> FindByUpnAsync(string upn)
    {
        await using var conn = Open();
        return await conn.QuerySingleOrDefaultAsync<UserRecord>(
            "SELECT * FROM dbo.DirectoryUser WHERE UserPrincipalName = @Upn", new { Upn = upn });
    }

    public async Task<IReadOnlyList<string>> GetRolesAsync(Guid userId)
    {
        await using var conn = Open();
        var roles = await conn.QueryAsync<string>("""
            SELECT g.AppRole
              FROM dbo.GroupMember m JOIN dbo.SecurityGroup g ON g.GroupId = m.GroupId
             WHERE m.UserId = @UserId
             ORDER BY g.AppRole
            """, new { UserId = userId });
        return roles.AsList();
    }

    public async Task RecordSignInSuccessAsync(Guid userId)
    {
        await using var conn = Open();
        await conn.ExecuteAsync("""
            UPDATE dbo.DirectoryUser
               SET LastSignInUtc = SYSUTCDATETIME(), FailedSignInCount = 0, LockedUntilUtc = NULL
             WHERE UserId = @UserId
            """, new { UserId = userId });
    }

    /// <summary>Counts a failed attempt and locks the account once the limit is reached. Returns true if now locked.</summary>
    public async Task<bool> RecordSignInFailureAsync(Guid userId, int maxFailures, int lockoutMinutes)
    {
        await using var conn = Open();
        return await conn.QuerySingleAsync<bool>("""
            UPDATE dbo.DirectoryUser
               SET FailedSignInCount = FailedSignInCount + 1,
                   LockedUntilUtc = CASE WHEN FailedSignInCount + 1 >= @Max
                                         THEN DATEADD(MINUTE, @Minutes, SYSUTCDATETIME()) ELSE LockedUntilUtc END
             OUTPUT CAST(CASE WHEN inserted.LockedUntilUtc > SYSUTCDATETIME() THEN 1 ELSE 0 END AS bit)
             WHERE UserId = @UserId
            """, new { UserId = userId, Max = maxFailures, Minutes = lockoutMinutes });
    }

    public async Task SetPasswordAsync(Guid userId, string passwordHash, bool mustChange)
    {
        await using var conn = Open();
        await conn.ExecuteAsync("""
            UPDATE dbo.DirectoryUser
               SET PasswordHash = @Hash, MustChangePassword = @MustChange,
                   PasswordChangedUtc = CASE WHEN @MustChange = 0 THEN SYSUTCDATETIME() ELSE PasswordChangedUtc END,
                   FailedSignInCount = 0, LockedUntilUtc = NULL, UpdatedAtUtc = SYSUTCDATETIME()
             WHERE UserId = @UserId
            """, new { UserId = userId, Hash = passwordHash, MustChange = mustChange });
    }

    /* ----- admin: users ----- */

    public async Task<int> CountUsersAsync()
    {
        await using var conn = Open();
        return await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.DirectoryUser");
    }

    public async Task<bool> UpnExistsAsync(string upn)
    {
        await using var conn = Open();
        return await conn.ExecuteScalarAsync<bool>(
            "SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.DirectoryUser WHERE UserPrincipalName = @Upn) THEN 1 ELSE 0 END AS bit)",
            new { Upn = upn });
    }

    public async Task<IEnumerable<UserSummary>> SearchUsersAsync(string? search, string status)
    {
        var pattern = search is null ? null : $"%{search.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]")}%";
        await using var conn = Open();
        return await conn.QueryAsync<UserSummary>($"""
            SELECT TOP (500)
                   u.UserId, u.UserPrincipalName, u.DisplayName, u.JobTitle, u.Department, u.Office,
                   u.AccountEnabled, u.MustChangePassword, IsLocked = {IsLockedSql}, u.LastSignInUtc, u.CreatedAtUtc,
                   Groups = (SELECT STRING_AGG(g.Name, N', ') WITHIN GROUP (ORDER BY g.Name)
                               FROM dbo.GroupMember m JOIN dbo.SecurityGroup g ON g.GroupId = m.GroupId
                              WHERE m.UserId = u.UserId)
              FROM dbo.DirectoryUser u
             WHERE (@Pattern IS NULL OR u.DisplayName LIKE @Pattern OR u.UserPrincipalName LIKE @Pattern
                    OR u.Department LIKE @Pattern OR u.JobTitle LIKE @Pattern OR u.Office LIKE @Pattern)
               AND (@Status = 'all'
                    OR (@Status = 'enabled' AND u.AccountEnabled = 1)
                    OR (@Status = 'disabled' AND u.AccountEnabled = 0))
             ORDER BY u.DisplayName
            """, new { Pattern = pattern, Status = status });
    }

    public async Task<UserDetail?> GetUserAsync(Guid userId)
    {
        await using var conn = Open();
        var user = await conn.QuerySingleOrDefaultAsync<UserDetail>($"""
            SELECT u.UserId, u.UserPrincipalName, u.DisplayName, u.GivenName, u.Surname, u.JobTitle, u.Department,
                   u.Office, u.EmployeeId, u.AccountEnabled, u.MustChangePassword, u.FailedSignInCount,
                   u.LockedUntilUtc, IsLocked = {IsLockedSql}, u.LastSignInUtc, u.PasswordChangedUtc,
                   u.CreatedAtUtc, u.CreatedBy, u.UpdatedAtUtc
              FROM dbo.DirectoryUser u WHERE u.UserId = @UserId
            """, new { UserId = userId });
        if (user is null) return null;

        user.GroupIds = (await conn.QueryAsync<int>(
            "SELECT GroupId FROM dbo.GroupMember WHERE UserId = @UserId ORDER BY GroupId", new { UserId = userId })).ToArray();
        return user;
    }

    public async Task<Guid> CreateUserAsync(NewUser user, IReadOnlyCollection<int> groupIds, string actor)
    {
        await using var conn = Open();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var userId = await conn.QuerySingleAsync<Guid>("""
            INSERT dbo.DirectoryUser (UserPrincipalName, DisplayName, GivenName, Surname, JobTitle, Department, Office,
                                      EmployeeId, PasswordHash, MustChangePassword, CreatedBy)
            OUTPUT inserted.UserId
            VALUES (@UserPrincipalName, CONCAT(@GivenName, N' ', @Surname), @GivenName, @Surname, @JobTitle, @Department,
                    @Office, @EmployeeId, @PasswordHash, @MustChangePassword, @Actor)
            """, new
        {
            user.UserPrincipalName, user.GivenName, user.Surname, user.JobTitle, user.Department, user.Office,
            user.EmployeeId, user.PasswordHash, user.MustChangePassword, Actor = actor
        }, tx);

        await InsertMembershipsAsync(conn, tx, userId, groupIds, actor);
        await tx.CommitAsync();
        return userId;
    }

    public async Task UpdateProfileAsync(Guid userId, ProfileUpdate p)
    {
        await using var conn = Open();
        await conn.ExecuteAsync("""
            UPDATE dbo.DirectoryUser
               SET GivenName = @GivenName, Surname = @Surname, DisplayName = CONCAT(@GivenName, N' ', @Surname),
                   JobTitle = @JobTitle, Department = @Department, Office = @Office, EmployeeId = @EmployeeId,
                   UpdatedAtUtc = SYSUTCDATETIME()
             WHERE UserId = @UserId
            """, new { UserId = userId, p.GivenName, p.Surname, p.JobTitle, p.Department, p.Office, p.EmployeeId });
    }

    /// <summary>Replaces the user's group memberships. Returns the names of groups added and removed.</summary>
    public async Task<(string[] Added, string[] Removed)> SetGroupsAsync(Guid userId, IReadOnlyCollection<int> groupIds, string actor)
    {
        await using var conn = Open();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        var current = (await conn.QueryAsync<int>("SELECT GroupId FROM dbo.GroupMember WHERE UserId = @UserId",
            new { UserId = userId }, tx)).ToHashSet();
        var wanted = groupIds.ToHashSet();
        var toAdd = wanted.Except(current).ToArray();
        var toRemove = current.Except(wanted).ToArray();

        if (toRemove.Length > 0)
            await conn.ExecuteAsync("DELETE dbo.GroupMember WHERE UserId = @UserId AND GroupId IN @Ids",
                new { UserId = userId, Ids = toRemove }, tx);
        await InsertMembershipsAsync(conn, tx, userId, toAdd, actor);

        var names = (await conn.QueryAsync<(int GroupId, string Name)>("SELECT GroupId, Name FROM dbo.SecurityGroup", transaction: tx))
            .ToDictionary(g => g.GroupId, g => g.Name);
        await tx.CommitAsync();

        return (toAdd.Select(id => names.GetValueOrDefault(id, $"#{id}")).ToArray(),
                toRemove.Select(id => names.GetValueOrDefault(id, $"#{id}")).ToArray());
    }

    private static async Task InsertMembershipsAsync(SqlConnection conn, System.Data.Common.DbTransaction tx,
        Guid userId, IEnumerable<int> groupIds, string actor)
    {
        foreach (var groupId in groupIds)
        {
            await conn.ExecuteAsync("""
                INSERT dbo.GroupMember (UserId, GroupId, AddedBy)
                SELECT @UserId, GroupId, @Actor FROM dbo.SecurityGroup WHERE GroupId = @GroupId
                """, new { UserId = userId, GroupId = groupId, Actor = actor }, tx);
        }
    }

    public async Task SetEnabledAsync(Guid userId, bool enabled)
    {
        await using var conn = Open();
        await conn.ExecuteAsync(
            "UPDATE dbo.DirectoryUser SET AccountEnabled = @Enabled, UpdatedAtUtc = SYSUTCDATETIME() WHERE UserId = @UserId",
            new { UserId = userId, Enabled = enabled });
    }

    public async Task UnlockAsync(Guid userId)
    {
        await using var conn = Open();
        await conn.ExecuteAsync(
            "UPDATE dbo.DirectoryUser SET FailedSignInCount = 0, LockedUntilUtc = NULL, UpdatedAtUtc = SYSUTCDATETIME() WHERE UserId = @UserId",
            new { UserId = userId });
    }

    /* ----- groups and audit ----- */

    public async Task<IEnumerable<GroupRow>> GetGroupsAsync()
    {
        await using var conn = Open();
        return await conn.QueryAsync<GroupRow>("""
            SELECT g.GroupId, g.Name, g.Description, g.AppRole,
                   Members = (SELECT COUNT(*) FROM dbo.GroupMember m
                                JOIN dbo.DirectoryUser u ON u.UserId = m.UserId AND u.AccountEnabled = 1
                               WHERE m.GroupId = g.GroupId)
              FROM dbo.SecurityGroup g
             ORDER BY g.Name
            """);
    }

    public async Task<IReadOnlyDictionary<string, int>> GetGroupIdsByRoleAsync()
    {
        await using var conn = Open();
        var rows = await conn.QueryAsync<(string AppRole, int GroupId)>("SELECT AppRole, GroupId FROM dbo.SecurityGroup");
        return rows.ToDictionary(r => r.AppRole, r => r.GroupId);
    }

    public async Task AuditAsync(string actor, string action, string? targetUpn, string? detail)
    {
        await using var conn = Open();
        await conn.ExecuteAsync(
            "INSERT dbo.AuditEvent (Actor, Action, TargetUpn, Detail) VALUES (@Actor, @Action, @TargetUpn, @Detail)",
            new { Actor = actor, Action = action, TargetUpn = targetUpn,
                  Detail = detail is { Length: > 1000 } ? detail[..1000] : detail });
    }

    public async Task<IEnumerable<AuditRow>> GetAuditAsync(int top, string? targetUpn)
    {
        await using var conn = Open();
        return await conn.QueryAsync<AuditRow>("""
            SELECT TOP (@Top) AuditId, AtUtc, Actor, Action, TargetUpn, Detail
              FROM dbo.AuditEvent
             WHERE @TargetUpn IS NULL OR TargetUpn = @TargetUpn OR Actor = @TargetUpn
             ORDER BY AuditId DESC
            """, new { Top = top, TargetUpn = targetUpn });
    }
}
