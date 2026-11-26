using System.Security.Claims;
using System.Text.RegularExpressions;
using CorpDirectory;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SyncHub.Observability;

var builder = WebApplication.CreateBuilder(args);
builder.AddObservability("CorpDirectory.Api");

builder.Services.Configure<DirectoryOptions>(builder.Configuration.GetSection("Directory"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SigningKeyStore>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<DirectoryRepository>();
builder.Services.AddProblemDetails();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? new[] { "http://localhost:5174" })
    .AllowAnyHeader()
    .AllowAnyMethod()));

// The admin endpoints accept this service's own tokens, issued for the directory admin app
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<SigningKeyStore, IOptions<DirectoryOptions>>((o, keys, options) =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = options.Value.Issuer,
            ValidAudience = Applications.Directory.Audience,
            IssuerSigningKey = keys.SecurityKey,
            NameClaimType = "name",
            RoleClaimType = "roles",
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("DirectoryAdmin", p => p.RequireRole("Directory.Admin"));

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

await Seeder.SeedAsync(app.Services);

/* ===========================================================================
   Discovery: lets other APIs find and trust our signing keys (OpenID Connect style)
=========================================================================== */
app.MapGet("/.well-known/openid-configuration", (IOptions<DirectoryOptions> o, HttpRequest request) =>
{
    // The issuer is fixed: it's the name tokens are issued under, whoever is asking.
    // The endpoint URLs follow the address the caller actually used, so an API reaching us by
    // another name (http://directory:8080 inside Docker) is sent to keys it can download,
    // instead of to http://localhost:5090, which inside its container is itself.
    var issuer = o.Value.Issuer;
    var baseUrl = $"{request.Scheme}://{request.Host}{request.PathBase}";
    return Results.Json(new Dictionary<string, object>
    {
        ["issuer"] = issuer,
        ["jwks_uri"] = $"{baseUrl}/.well-known/jwks.json",
        ["token_endpoint"] = $"{baseUrl}/api/auth/token",
        ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
        ["response_types_supported"] = new[] { "token" },
        ["subject_types_supported"] = new[] { "public" },
    });
});

app.MapGet("/.well-known/jwks.json", (SigningKeyStore keys) => Results.Json(new { keys = new[] { keys.Jwk } }));

app.MapGet("/", () => Results.Text("Mock corporate directory. Sign in at POST /api/auth/token; admin API under /api/admin."));

/* ===========================================================================
   Sign-in
=========================================================================== */
app.MapPost("/api/auth/token", async (TokenRequest body, DirectoryRepository repo, TokenService tokens,
    IOptions<DirectoryOptions> options) =>
{
    var application = Applications.Find(body.Resource);
    if (application is null) return Error(400, "invalid_resource", "Unknown application.");
    if (string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrEmpty(body.Password))
        return Error(400, "invalid_request", "Enter your username and password.");

    var upn = NormaliseUpn(body.Username, options.Value.EmailDomain);
    var user = await repo.FindByUpnAsync(upn);
    const string badCredentials = "Incorrect username or password.";

    // Same message whether the account exists or not, so the screen can't be used to find valid usernames
    if (user is null)
    {
        await repo.AuditAsync(upn, "SignInFailed", upn, "Unknown account");
        return Error(401, "invalid_grant", badCredentials);
    }

    if (user.LockedUntilUtc > DateTime.UtcNow)
    {
        await repo.AuditAsync(upn, "SignInBlocked", upn, "Account locked");
        return Error(423, "account_locked",
            $"Too many failed attempts. Try again after {user.LockedUntilUtc:HH:mm} UTC, or ask the service desk to unlock your account.");
    }

    if (!Passwords.Verify(body.Password, user.PasswordHash))
    {
        var nowLocked = await repo.RecordSignInFailureAsync(user.UserId, options.Value.MaxFailedSignIns, options.Value.LockoutMinutes);
        await repo.AuditAsync(upn, nowLocked ? "AccountLocked" : "SignInFailed", upn,
            nowLocked ? $"Locked for {options.Value.LockoutMinutes} minutes after repeated failures" : "Wrong password");
        return Error(401, "invalid_grant", badCredentials);
    }

    // Only reveal account state once the password has been proven
    if (!user.AccountEnabled)
    {
        await repo.AuditAsync(upn, "SignInBlocked", upn, "Account disabled");
        return Error(403, "account_disabled", "This account has been disabled. Contact the service desk.");
    }

    if (user.MustChangePassword)
        return Error(409, "password_change_required", "You need to choose a new password before signing in.");

    var roles = (await repo.GetRolesAsync(user.UserId))
        .Where(r => r.StartsWith(application.RolePrefix, StringComparison.Ordinal))
        .ToArray();
    if (roles.Length == 0)
    {
        await repo.AuditAsync(upn, "SignInBlocked", upn, $"No access to {application.DisplayName}");
        return Error(403, "not_assigned",
            $"Your account hasn't been given access to the {application.DisplayName}. Ask your manager to request it from the service desk.");
    }

    var (token, expires) = tokens.Create(user, roles, application);
    await repo.RecordSignInSuccessAsync(user.UserId);
    await repo.AuditAsync(upn, "SignIn", upn, $"{application.DisplayName} ({string.Join(", ", roles)})");

    return Results.Ok(new
    {
        accessToken = token,
        tokenType = "Bearer",
        expiresAtUtc = expires,
        user = new { user.UserId, user.DisplayName, user.UserPrincipalName, user.JobTitle, roles }
    });
});

app.MapPost("/api/auth/change-password", async (ChangePasswordRequest body, DirectoryRepository repo,
    IOptions<DirectoryOptions> options) =>
{
    var upn = NormaliseUpn(body.Username ?? "", options.Value.EmailDomain);
    var user = await repo.FindByUpnAsync(upn);
    if (user is null || !Passwords.Verify(body.CurrentPassword ?? "", user.PasswordHash))
    {
        if (user is not null)
            await repo.RecordSignInFailureAsync(user.UserId, options.Value.MaxFailedSignIns, options.Value.LockoutMinutes);
        return Error(401, "invalid_grant", "Your current password is incorrect.");
    }
    if (user.LockedUntilUtc > DateTime.UtcNow) return Error(423, "account_locked", "This account is locked.");
    if (!user.AccountEnabled) return Error(403, "account_disabled", "This account has been disabled.");

    var newPassword = body.NewPassword ?? "";
    if (Passwords.CheckPolicy(newPassword) is { } problem) return Error(400, "weak_password", problem);
    if (Passwords.Verify(newPassword, user.PasswordHash)) return Error(400, "weak_password", "Choose a password you haven't just used.");

    await repo.SetPasswordAsync(user.UserId, Passwords.Hash(newPassword), mustChange: false);
    await repo.AuditAsync(upn, "PasswordChanged", upn, user.MustChangePassword ? "First sign-in" : null);
    return Results.NoContent();
});

/* ===========================================================================
   Admin centre API (Directory.Admin role only)
=========================================================================== */
var admin = app.MapGroup("/api/admin").RequireAuthorization("DirectoryAdmin");

admin.MapGet("/options", (IOptions<DirectoryOptions> o) =>
    Results.Ok(new { o.Value.Offices, o.Value.Departments, o.Value.EmailDomain }));

admin.MapGet("/users", async (DirectoryRepository repo, string? search, string? status) =>
    Results.Ok(await repo.SearchUsersAsync(string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
        status is "enabled" or "disabled" ? status : "all")));

admin.MapGet("/users/{id:guid}", async (DirectoryRepository repo, Guid id) =>
    await repo.GetUserAsync(id) is { } user ? Results.Ok(user) : Results.NotFound());

// Joiner: create the account, add to groups, hand back a one-time password
admin.MapPost("/users", async (NewHireRequest body, DirectoryRepository repo, IOptions<DirectoryOptions> o, ClaimsPrincipal me) =>
{
    var given = Tidy(body.GivenName);
    var surname = Tidy(body.Surname);
    if (given is null || surname is null) return Error(400, "invalid_request", "First name and surname are required.");

    string upn;
    if (!string.IsNullOrWhiteSpace(body.UserPrincipalName))
    {
        upn = NormaliseUpn(body.UserPrincipalName, o.Value.EmailDomain);
        if (await repo.UpnExistsAsync(upn)) return Error(409, "upn_taken", $"{upn} is already in use.");
    }
    else
    {
        // first.last@domain, then first.last2@domain etc. if that's taken
        var stem = $"{Slug(given)}.{Slug(surname)}";
        upn = $"{stem}@{o.Value.EmailDomain}";
        for (var i = 2; await repo.UpnExistsAsync(upn); i++) upn = $"{stem}{i}@{o.Value.EmailDomain}";
    }

    var temporaryPassword = Passwords.GenerateTemporary();
    var groupIds = body.GroupIds ?? Array.Empty<int>();
    var userId = await repo.CreateUserAsync(new NewUser(upn, given, surname, Tidy(body.JobTitle), Tidy(body.Department),
        Tidy(body.Office), Tidy(body.EmployeeId), Passwords.Hash(temporaryPassword), MustChangePassword: true),
        groupIds, Actor(me));

    var user = await repo.GetUserAsync(userId);
    await repo.AuditAsync(Actor(me), "UserCreated", upn,
        $"{given} {surname}, {Tidy(body.JobTitle) ?? "no job title"}; groups: {groupIds.Length}");

    // The temporary password is returned once and never stored in plain text
    return Results.Ok(new { user, temporaryPassword });
});

// Mover: profile changes
admin.MapPut("/users/{id:guid}", async (Guid id, ProfileRequest body, DirectoryRepository repo, ClaimsPrincipal me) =>
{
    var user = await repo.GetUserAsync(id);
    if (user is null) return Results.NotFound();
    var given = Tidy(body.GivenName);
    var surname = Tidy(body.Surname);
    if (given is null || surname is null) return Error(400, "invalid_request", "First name and surname are required.");

    await repo.UpdateProfileAsync(id, new ProfileUpdate(given, surname, Tidy(body.JobTitle), Tidy(body.Department),
        Tidy(body.Office), Tidy(body.EmployeeId)));
    await repo.AuditAsync(Actor(me), "ProfileUpdated", user.UserPrincipalName,
        $"{Tidy(body.JobTitle) ?? "-"}, {Tidy(body.Department) ?? "-"}, {Tidy(body.Office) ?? "-"}");
    return Results.Ok(await repo.GetUserAsync(id));
});

// Mover: access changes
admin.MapPut("/users/{id:guid}/groups", async (Guid id, GroupsRequest body, DirectoryRepository repo, ClaimsPrincipal me) =>
{
    var user = await repo.GetUserAsync(id);
    if (user is null) return Results.NotFound();

    // Stop an administrator removing their own admin access and locking everyone out
    if (IsSelf(me, user))
    {
        var adminGroup = (await repo.GetGroupIdsByRoleAsync()).GetValueOrDefault("Directory.Admin");
        if (!(body.GroupIds ?? Array.Empty<int>()).Contains(adminGroup))
            return Error(400, "self_lockout", "You can't remove your own Directory Administrators membership.");
    }

    var (added, removed) = await repo.SetGroupsAsync(id, body.GroupIds ?? Array.Empty<int>(), Actor(me));
    if (added.Length + removed.Length > 0)
        await repo.AuditAsync(Actor(me), "GroupsChanged", user.UserPrincipalName,
            $"Added: {(added.Length > 0 ? string.Join(", ", added) : "none")}. Removed: {(removed.Length > 0 ? string.Join(", ", removed) : "none")}.");
    return Results.Ok(await repo.GetUserAsync(id));
});

// Leaver: disable (keeps the account and its history; sign-in is refused)
admin.MapPost("/users/{id:guid}/disable", async (Guid id, ReasonRequest body, DirectoryRepository repo, ClaimsPrincipal me) =>
{
    var user = await repo.GetUserAsync(id);
    if (user is null) return Results.NotFound();
    if (IsSelf(me, user)) return Error(400, "self_lockout", "You can't disable your own account.");

    await repo.SetEnabledAsync(id, false);
    await repo.AuditAsync(Actor(me), "UserDisabled", user.UserPrincipalName, Tidy(body.Reason));
    return Results.Ok(await repo.GetUserAsync(id));
});

admin.MapPost("/users/{id:guid}/enable", async (Guid id, ReasonRequest body, DirectoryRepository repo, ClaimsPrincipal me) =>
{
    var user = await repo.GetUserAsync(id);
    if (user is null) return Results.NotFound();

    await repo.SetEnabledAsync(id, true);
    await repo.AuditAsync(Actor(me), "UserEnabled", user.UserPrincipalName, Tidy(body.Reason));
    return Results.Ok(await repo.GetUserAsync(id));
});

admin.MapPost("/users/{id:guid}/reset-password", async (Guid id, DirectoryRepository repo, ClaimsPrincipal me) =>
{
    var user = await repo.GetUserAsync(id);
    if (user is null) return Results.NotFound();

    var temporaryPassword = Passwords.GenerateTemporary();
    await repo.SetPasswordAsync(id, Passwords.Hash(temporaryPassword), mustChange: true);
    await repo.AuditAsync(Actor(me), "PasswordReset", user.UserPrincipalName, "Temporary password issued; change required at next sign-in");
    return Results.Ok(new { user = await repo.GetUserAsync(id), temporaryPassword });
});

admin.MapPost("/users/{id:guid}/unlock", async (Guid id, DirectoryRepository repo, ClaimsPrincipal me) =>
{
    var user = await repo.GetUserAsync(id);
    if (user is null) return Results.NotFound();

    await repo.UnlockAsync(id);
    await repo.AuditAsync(Actor(me), "UserUnlocked", user.UserPrincipalName, null);
    return Results.Ok(await repo.GetUserAsync(id));
});

admin.MapGet("/groups", async (DirectoryRepository repo) => Results.Ok(await repo.GetGroupsAsync()));

admin.MapGet("/audit", async (DirectoryRepository repo, int? top, string? upn) =>
    Results.Ok(await repo.GetAuditAsync(Math.Clamp(top ?? 200, 1, 1000), string.IsNullOrWhiteSpace(upn) ? null : upn)));

app.Run();

/* ---------------- helpers ---------------- */

static IResult Error(int status, string code, string message) =>
    Results.Json(new { error = code, message }, statusCode: status);

static string NormaliseUpn(string username, string domain)
{
    var trimmed = username.Trim().ToLowerInvariant();
    return trimmed.Contains('@') ? trimmed : $"{trimmed}@{domain}";   // allow "first.last" without the domain
}

static string? Tidy(string? value) =>
    string.IsNullOrWhiteSpace(value) ? null : Regex.Replace(value.Trim(), @"\s+", " ");

static string Slug(string name) =>
    Regex.Replace(name.Normalize(System.Text.NormalizationForm.FormD), @"[^A-Za-z]", "").ToLowerInvariant();

static string Actor(ClaimsPrincipal me) => me.FindFirst("preferred_username")?.Value ?? "unknown";

static bool IsSelf(ClaimsPrincipal me, UserDetail user) =>
    string.Equals(me.FindFirst("sub")?.Value, user.UserId.ToString(), StringComparison.OrdinalIgnoreCase);

/* ---------------- request bodies ---------------- */

public sealed record TokenRequest(string? Username, string? Password, string? Resource);
public sealed record ChangePasswordRequest(string? Username, string? CurrentPassword, string? NewPassword);
public sealed record NewHireRequest(string? GivenName, string? Surname, string? JobTitle, string? Department,
    string? Office, string? EmployeeId, string? UserPrincipalName, int[]? GroupIds);
public sealed record ProfileRequest(string? GivenName, string? Surname, string? JobTitle, string? Department,
    string? Office, string? EmployeeId);
public sealed record GroupsRequest(int[]? GroupIds);
public sealed record ReasonRequest(string? Reason);
