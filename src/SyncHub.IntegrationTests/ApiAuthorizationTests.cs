using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Dapper;
using Microsoft.IdentityModel.Tokens;

namespace SyncHub.IntegrationTests;

/// <summary>
/// Who can do what in the Support API. These pin down the security model: authentication
/// (is the token genuine?) and authorisation (does this role allow this action?).
/// A wrong answer here is a data-protection incident, so each rule has its own test.
/// </summary>
[Collection("sql")]
public sealed class ApiAuthorizationTests(SqlServerFixture sql) : IAsyncLifetime
{
    private const string Viewer = "SyncHub.Viewer", Support = "SyncHub.Support",
                         Steward = "SyncHub.DataSteward", Admin = "SyncHub.Admin";

    private readonly SupportApiFactory _api = new(sql);

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _api.DisposeAsync();

    /* ---------------- Authentication: is the token genuine? ---------------- */

    [Fact]
    public async Task No_token_is_401()
    {
        var response = await _api.ClientFor(null).GetAsync("/api/summary");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_check_needs_no_token()
    {
        var response = await _api.ClientFor(null).GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public static TheoryData<string, string> BadTokens()
    {
        var otherKey = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "attacker" };
        var future = DateTime.UtcNow.AddMinutes(30);
        string[] admin = [Admin];
        return new()
        {
            { "expired", TestTokens.Create("Eve", admin, TestTokens.Issuer, TestTokens.Audience, DateTime.UtcNow.AddMinutes(-10), TestTokens.SigningKey) },
            { "wrong issuer", TestTokens.Create("Eve", admin, "http://evil.example", TestTokens.Audience, future, TestTokens.SigningKey) },
            { "wrong audience (a directory token)", TestTokens.Create("Eve", admin, TestTokens.Issuer, "api://directory", future, TestTokens.SigningKey) },
            { "signed with another key", TestTokens.Create("Eve", admin, TestTokens.Issuer, TestTokens.Audience, future, otherKey) },
            { "not a JWT", "not-a-token" },
        };
    }

    [Theory]
    [MemberData(nameof(BadTokens))]
    public async Task Invalid_tokens_are_401_even_with_admin_role(string reason, string token)
    {
        var response = await _api.ClientFor(token).GetAsync("/api/summary");
        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{reason}: got {(int)response.StatusCode}");
    }

    /* ---------------- Authorisation: what may each role do? ---------------- */

    [Fact]
    public async Task Signed_in_user_without_a_SyncHub_role_is_403()
    {
        var response = await _api.ClientFor(TestTokens.For("Jordan Lee")).GetAsync("/api/summary");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(Viewer)]
    [InlineData(Support)]
    [InlineData(Steward)]
    [InlineData(Admin)]
    public async Task Every_role_can_read(string role)
    {
        var client = _api.ClientFor(TestTokens.For("Reader", role));
        foreach (var path in new[] { "/api/summary", "/api/deadletters", "/api/duplicates", "/api/reconciliation" })
        {
            var response = await client.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{role} GET {path}: {(int)response.StatusCode}");
        }
    }

    // Replaying and ignoring exceptions belongs to Support (and Admin).
    // 409 means "allowed, but there's no such open item", which is what we want to see: past the policy.
    [Theory]
    [InlineData(Viewer, HttpStatusCode.Forbidden)]
    [InlineData(Steward, HttpStatusCode.Forbidden)]
    [InlineData(Support, HttpStatusCode.Conflict)]
    [InlineData(Admin, HttpStatusCode.Conflict)]
    public async Task Only_support_and_admin_can_replay(string role, HttpStatusCode expected)
    {
        var response = await _api.ClientFor(TestTokens.For("User", role)).PostAsync("/api/deadletters/999999999/replay", null);
        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData(Viewer, HttpStatusCode.Forbidden)]
    [InlineData(Steward, HttpStatusCode.Forbidden)]
    [InlineData(Support, HttpStatusCode.OK)]
    public async Task Only_support_and_admin_can_bulk_replay(string role, HttpStatusCode expected)
    {
        // A source nothing uses, so an allowed call queues nothing.
        var body = new { source = SqlServerFixture.NewSource(), errorType = (string?)null };
        var response = await _api.ClientFor(TestTokens.For("User", role)).PostAsJsonAsync("/api/deadletters/replay-bulk", body);
        Assert.Equal(expected, response.StatusCode);
    }

    // Duplicate decisions and practice-area mappings are data stewardship.
    // 404 means "allowed, but no such candidate".
    [Theory]
    [InlineData(Viewer, HttpStatusCode.Forbidden)]
    [InlineData(Support, HttpStatusCode.Forbidden)]
    [InlineData(Steward, HttpStatusCode.NotFound)]
    [InlineData(Admin, HttpStatusCode.NotFound)]
    public async Task Only_stewards_and_admin_can_decide_duplicates(string role, HttpStatusCode expected)
    {
        var response = await _api.ClientFor(TestTokens.For("User", role))
            .PostAsJsonAsync("/api/duplicates/999999999/decision", new { decision = "NotDuplicate" });
        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData(Viewer)]
    [InlineData(Support)]
    public async Task Only_stewards_and_admin_can_map_practice_areas(string role)
    {
        var response = await _api.ClientFor(TestTokens.For("User", role))
            .PostAsJsonAsync("/api/practice-areas/map", new { source = "FirmA", sourceCode = "X", practiceArea = "Y" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /* ---------------- Identity comes from the token, not the request ---------------- */

    [Fact]
    public async Task Audit_trail_records_the_token_user_even_if_the_body_claims_someone_else()
    {
        var id = await InsertOpenDeadLetterAsync();

        // A crafted body trying to put a different name in the audit trail.
        var response = await _api.ClientFor(TestTokens.For("Alex Morgan", Support))
            .PostAsJsonAsync($"/api/deadletters/{id}/ignore", new { note = "Known test record", user = "Mallory" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await using var conn = new SqlConnection(sql.SyncHubConnectionString);
        var note = await conn.QuerySingleAsync<string>(
            "SELECT ResolutionNote FROM ops.DeadLetter WHERE DeadLetterId = @id", new { id });
        Assert.Contains("Ignored by Alex Morgan", note);
        Assert.DoesNotContain("Mallory", note);
    }

    [Fact]
    public async Task Ignoring_needs_a_note()
    {
        var id = await InsertOpenDeadLetterAsync();
        var response = await _api.ClientFor(TestTokens.For("Alex Morgan", Support))
            .PostAsJsonAsync($"/api/deadletters/{id}/ignore", new { note = " " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<long> InsertOpenDeadLetterAsync()
    {
        await using var conn = new SqlConnection(sql.SyncHubConnectionString);
        return await conn.QuerySingleAsync<long>("""
            INSERT ops.DeadLetter (FirstSyncRunId, LastSyncRunId, Source, Entity, SourceKey, ErrorType, ErrorMessage, Payload)
            OUTPUT inserted.DeadLetterId
            VALUES (0, 0, @Source, 'Client', 'K1', 'MissingClientName', N'test', N'{}');
            """, new { Source = SqlServerFixture.NewSource() });
    }
}
