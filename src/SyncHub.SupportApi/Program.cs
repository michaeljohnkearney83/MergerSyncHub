using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using SyncHub.SupportApi;
using SyncHub.Observability;

var builder = WebApplication.CreateBuilder(args);
builder.AddObservability("SyncHub.SupportApi");

builder.Services.AddSingleton<SupportRepository>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration["Dashboard:Origin"] ?? "http://localhost:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()));

// Tokens come from the corporate directory (CorpDirectory.Api). This API never sees a password
// and holds no secret: it downloads the directory's public signing keys from its discovery
// document and checks every token's signature, issuer, audience and expiry. Swap Authority for
// https://login.microsoftonline.com/{tenant}/v2.0 and the same code works with Entra ID.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.Authority = builder.Configuration["Auth:Authority"] ?? "http://localhost:5090";
        // Optional: where to fetch the discovery document when the authority's public address isn't
        // reachable from here (inside Docker). The token's issuer is still checked against the
        // discovery document's "issuer", which stays the public address.
        if (builder.Configuration["Auth:MetadataAddress"] is { Length: > 0 } metadataAddress)
            o.MetadataAddress = metadataAddress;
        o.Audience = builder.Configuration["Auth:Audience"] ?? "api://synchub";
        o.RequireHttpsMetadata = false;   // local demo only
        o.RefreshInterval = TimeSpan.FromSeconds(30);   // retry key download soon if the directory wasn't up yet
        o.MapInboundClaims = false;       // keep claim names as issued ("roles", "name")
        o.TokenValidationParameters.NameClaimType = "name";
        o.TokenValidationParameters.RoleClaimType = "roles";
        o.TokenValidationParameters.ClockSkew = TimeSpan.FromMinutes(1);
    });

// Roles come from directory groups. Administrators can do everything.
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.View, p => p.RequireRole(Roles.Viewer, Roles.Support, Roles.DataSteward, Roles.Admin))
    .AddPolicy(Policies.FixExceptions, p => p.RequireRole(Roles.Support, Roles.Admin))
    .AddPolicy(Policies.StewardData, p => p.RequireRole(Roles.DataSteward, Roles.Admin));

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }));

// Everything else needs a signed-in user with at least view access
var api = app.MapGroup("/api").RequireAuthorization(Policies.View);

api.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new
{
    name = user.Identity?.Name,
    username = user.FindFirst("preferred_username")?.Value,
    roles = user.FindAll("roles").Select(c => c.Value)
}));

api.MapGet("/summary", async (SupportRepository repo) => Results.Ok(await repo.GetSummaryAsync()));

/* ---------------- Sync runs ---------------- */

api.MapGet("/runs", async (SupportRepository repo, int? top, bool? hideQuiet) =>
    Results.Ok(await repo.GetRunsAsync(Math.Clamp(top ?? 50, 1, 500), hideQuiet ?? true)));

api.MapGet("/runs/{id:long}/entities", async (SupportRepository repo, long id) =>
    Results.Ok(await repo.GetRunEntitiesAsync(id)));

/* ---------------- Dead letters ---------------- */

api.MapGet("/deadletters", async (SupportRepository repo, string? status, string? source, string? errorType) =>
    Results.Ok(await repo.GetDeadLettersAsync(status ?? "Open", NullIfEmpty(source), NullIfEmpty(errorType))));

api.MapGet("/deadletters/summary", async (SupportRepository repo) => Results.Ok(await repo.GetOpenErrorTypesAsync()));

api.MapGet("/deadletters/{id:long}", async (SupportRepository repo, long id) =>
    await repo.GetDeadLetterAsync(id) is { } item ? Results.Ok(item) : Results.NotFound());

api.MapPut("/deadletters/{id:long}/payload", async (SupportRepository repo, long id, PayloadEdit body, ClaimsPrincipal user) =>
{
    try
    {
        using var _ = JsonDocument.Parse(body.Payload ?? "");   // reject anything that isn't valid JSON
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { error = $"Payload is not valid JSON: {ex.Message}" });
    }

    // Who made the change comes from the signed token, never from the request body
    return await repo.UpdatePayloadAsync(id, body.Payload!, Actor(user), NullIfEmpty(body.Note))
        ? Results.NoContent()
        : Results.Conflict(new { error = "Only items with status 'New' can be edited." });
}).RequireAuthorization(Policies.FixExceptions);

api.MapPost("/deadletters/{id:long}/replay", async (SupportRepository repo, long id, ClaimsPrincipal user) =>
    await repo.RequestReplayAsync(id, null, null, Actor(user)) == 1
        ? Results.Accepted()
        : Results.Conflict(new { error = "Only items with status 'New' can be replayed." }))
    .RequireAuthorization(Policies.FixExceptions);

api.MapPost("/deadletters/replay-bulk", async (SupportRepository repo, BulkReplay body, ClaimsPrincipal user) =>
    Results.Ok(new { queued = await repo.RequestReplayAsync(null, NullIfEmpty(body.Source), NullIfEmpty(body.ErrorType), Actor(user)) }))
    .RequireAuthorization(Policies.FixExceptions);

api.MapPost("/deadletters/{id:long}/ignore", async (SupportRepository repo, long id, NoteBody body, ClaimsPrincipal user) =>
{
    if (string.IsNullOrWhiteSpace(body.Note)) return Results.BadRequest(new { error = "A note explaining why is required." });
    return await repo.IgnoreAsync(id, Actor(user), body.Note)
        ? Results.NoContent()
        : Results.Conflict(new { error = "Item is already resolved or ignored." });
}).RequireAuthorization(Policies.FixExceptions);

/* ---------------- Duplicate clients ---------------- */

api.MapGet("/duplicates", async (SupportRepository repo, string? status) =>
    Results.Ok(await repo.GetDuplicatesAsync(status ?? "Pending")));

api.MapPost("/duplicates/{id:int}/decision", async (SupportRepository repo, int id, DuplicateDecision body, ClaimsPrincipal user) =>
{
    string[] allowed = ["ConfirmedDuplicate", "NotDuplicate", "Pending"];
    if (body.Decision is null || !allowed.Contains(body.Decision))
        return Results.BadRequest(new { error = $"decision must be one of: {string.Join(", ", allowed)}" });

    return await repo.DecideDuplicateAsync(id, body.Decision, Actor(user)) ? Results.NoContent() : Results.NotFound();
}).RequireAuthorization(Policies.StewardData);

/* ---------------- Client lookup (for the correction form) ---------------- */

api.MapGet("/clients", async (SupportRepository repo, string? source, string? search) =>
    string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(search) || search.Trim().Length < 2
        ? Results.BadRequest(new { error = "source and a search of at least 2 characters are required" })
        : Results.Ok(await repo.SearchClientsAsync(source, search.Trim())));

/* ---------------- Reconciliation and mappings ---------------- */

api.MapGet("/reconciliation", async (SupportRepository repo) => Results.Ok(await repo.GetReconciliationAsync()));

api.MapGet("/practice-areas", async (SupportRepository repo) => Results.Ok(await repo.GetPracticeAreasAsync()));

api.MapGet("/practice-areas/unmapped", async (SupportRepository repo) =>
    Results.Ok(await repo.GetUnmappedPracticeAreasAsync()));

api.MapPost("/practice-areas/map", async (SupportRepository repo, PracticeAreaMapping body) =>
{
    if (string.IsNullOrWhiteSpace(body.Source) || string.IsNullOrWhiteSpace(body.SourceCode) ||
        string.IsNullOrWhiteSpace(body.PracticeArea))
        return Results.BadRequest(new { error = "source, sourceCode and practiceArea are required" });

    await repo.AddPracticeAreaMappingAsync(body.Source, body.SourceCode.Trim().ToUpperInvariant(), body.PracticeArea.Trim());
    return Results.NoContent();
}).RequireAuthorization(Policies.StewardData);

app.Run();

// Blank query or body values mean "no filter".
static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

// Who is doing this, for audit notes: always from the validated token, never the request body.
static string Actor(ClaimsPrincipal user) =>
    user.Identity?.Name ?? user.FindFirst("preferred_username")?.Value ?? "unknown";

static class Roles
{
    public const string Viewer = "SyncHub.Viewer";
    public const string Support = "SyncHub.Support";
    public const string DataSteward = "SyncHub.DataSteward";
    public const string Admin = "SyncHub.Admin";
}

static class Policies
{
    public const string View = "View";
    public const string FixExceptions = "FixExceptions";
    public const string StewardData = "StewardData";
}

// Bodies no longer carry a user name: identity comes from the token. Extra fields are ignored.
public sealed record PayloadEdit(string? Payload, string? Note);
public sealed record NoteBody(string? Note);
public sealed record BulkReplay(string? Source, string? ErrorType);
public sealed record DuplicateDecision(string? Decision);
public sealed record PracticeAreaMapping(string? Source, string? SourceCode, string? PracticeArea);

// Public so the integration tests can host the API in memory (WebApplicationFactory<Program>).
public partial class Program;
