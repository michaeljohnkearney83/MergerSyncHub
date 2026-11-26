using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace SyncHub.IntegrationTests;

/// <summary>
/// Runs the real Support API in memory (routes, policies, JWT validation, repository) against the
/// test SQL Server. Only the key the tokens are checked against is swapped: instead of downloading
/// the directory's keys, the API trusts <see cref="TestTokens"/>, so tests can mint any user.
/// Everything else (issuer, audience, expiry, role claims) is validated exactly as in production.
/// </summary>
public sealed class SupportApiFactory(SqlServerFixture sql) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");                             // no user secrets
        builder.UseSetting("ConnectionStrings:SyncHub", sql.SyncHubConnectionString);
        builder.UseSetting("OpenTelemetry:OtlpEndpoint", "");          // no telemetry from tests

        builder.ConfigureServices(services =>
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                // A fixed configuration means no discovery request to a directory that isn't running.
                o.Authority = null;
                o.MetadataAddress = null!;
                o.Configuration = new OpenIdConnectConfiguration { Issuer = TestTokens.Issuer };
                o.TokenValidationParameters.ValidIssuer = TestTokens.Issuer;
                o.TokenValidationParameters.IssuerSigningKey = TestTokens.SigningKey;
            }));
    }

    public HttpClient ClientFor(string? token)
    {
        var client = CreateClient();
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

/// <summary>Mints tokens shaped like the directory's (name, preferred_username, roles).</summary>
public static class TestTokens
{
    public const string Issuer = "http://localhost:5090";
    public const string Audience = "api://synchub";

    public static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "test-key" };

    public static string For(string name, params string[] roles) =>
        Create(name, roles, Issuer, Audience, DateTime.UtcNow.AddMinutes(30), SigningKey);

    public static string Create(string name, string[] roles, string issuer, string audience,
        DateTime expires, SecurityKey key)
    {
        var claims = new List<Claim> { new("name", name), new("preferred_username", $"{name}@test") };
        claims.AddRange(roles.Select(r => new Claim("roles", r)));

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = expires.AddHours(-1),
            Expires = expires,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        });
    }
}
