using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CorpDirectory;

public sealed class DirectoryOptions
{
    public string Issuer { get; set; } = "http://localhost:5090";
    public string EmailDomain { get; set; } = "winstontaylor.example";
    public int TokenLifetimeMinutes { get; set; } = 60;
    public int MaxFailedSignIns { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 5;
    public string SigningKeyPath { get; set; } = "signing-key.pem";
    public string SeedPassword { get; set; } = "Welcome2SyncHub!";
    public string[] Offices { get; set; } = [];
    public string[] Departments { get; set; } = [];
}

/// <summary>
/// The applications that can request tokens. Each has its own audience and role prefix,
/// so a token for the SyncHub dashboard cannot be used against the directory admin API
/// and only carries SyncHub roles (as with app registrations in Entra ID).
/// </summary>
public static class Applications
{
    public sealed record App(string Resource, string Audience, string RolePrefix, string DisplayName);

    public static readonly App SyncHub = new("synchub", "api://synchub", "SyncHub.", "SyncHub Support dashboard");
    public static readonly App Directory = new("directory", "api://directory", "Directory.", "Directory admin centre");

    /// <summary>The application a sign-in is for, by its resource name (<c>synchub</c> or <c>directory</c>); <c>null</c> if unknown.</summary>
    public static App? Find(string? resource) =>
        string.Equals(resource, SyncHub.Resource, StringComparison.OrdinalIgnoreCase) ? SyncHub
        : string.Equals(resource, Directory.Resource, StringComparison.OrdinalIgnoreCase) ? Directory
        : null;
}

/// <summary>
/// Passwords are stored as salted PBKDF2-SHA256 hashes: a unique random salt per user,
/// many iterations to slow down guessing, and a constant-time comparison.
/// Format: PBKDF2-SHA256$iterations$salt$hash
/// </summary>
public static class Passwords
{
    private const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>Hashes a password with a new random salt.</summary>
    /// <returns>The stored form: <c>PBKDF2-SHA256$iterations$salt$hash</c>.</returns>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"PBKDF2-SHA256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Checks a password against a stored hash using its salt and iteration count, with a constant-time comparison. <c>false</c> for a malformed hash.</summary>
    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "PBKDF2-SHA256" || !int.TryParse(parts[1], out var iterations))
            return false;

        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Returns a reason the password is not acceptable, or null if it is fine.</summary>
    public static string? CheckPolicy(string password)
    {
        if (password.Length < 10) return "Use at least 10 characters.";
        if (!password.Any(char.IsUpper)) return "Include at least one capital letter.";
        if (!password.Any(char.IsLower)) return "Include at least one lower-case letter.";
        if (!password.Any(char.IsDigit)) return "Include at least one number.";
        return null;
    }

    /// <summary>A one-time password for a new starter or a reset, e.g. "Kpw7-Tzr4-Hmd9". Avoids look-alike characters.</summary>
    public static string GenerateTemporary()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnpqrstuvwxyz";
        const string digits = "23456789";

        string Block() => string.Concat(
            upper[RandomNumberGenerator.GetInt32(upper.Length)],
            lower[RandomNumberGenerator.GetInt32(lower.Length)],
            lower[RandomNumberGenerator.GetInt32(lower.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)]);

        return $"{Block()}-{Block()}-{Block()}";
    }
}

/// <summary>
/// The RSA key used to sign tokens. The private key never leaves this service; the public
/// half is published at /.well-known/jwks.json so other APIs can verify tokens without
/// sharing a secret. The key is saved to a file so restarts don't sign everyone out.
/// </summary>
public sealed class SigningKeyStore
{
    public SigningKeyStore(IOptions<DirectoryOptions> options, IWebHostEnvironment env, ILogger<SigningKeyStore> logger)
    {
        var path = Path.Combine(env.ContentRootPath, options.Value.SigningKeyPath);
        RSA rsa;
        if (File.Exists(path))
        {
            rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(path));
        }
        else
        {
            rsa = RSA.Create(2048);
            File.WriteAllText(path, rsa.ExportRSAPrivateKeyPem());
            // Warning: a new key invalidates every token issued so far, so all users must sign in again.
            logger.LogWarning("Created a new token signing key at {Path}; previously issued tokens are no longer valid", path);
        }

        var publicKey = rsa.ExportParameters(false);
        var keyId = Base64UrlEncoder.Encode(SHA256.HashData(publicKey.Modulus!))[..16];

        SecurityKey = new RsaSecurityKey(rsa) { KeyId = keyId };
        Credentials = new SigningCredentials(SecurityKey, SecurityAlgorithms.RsaSha256);
        Jwk = new Dictionary<string, string>
        {
            ["kty"] = "RSA",
            ["use"] = "sig",
            ["alg"] = "RS256",
            ["kid"] = keyId,
            ["n"] = Base64UrlEncoder.Encode(publicKey.Modulus!),
            ["e"] = Base64UrlEncoder.Encode(publicKey.Exponent!),
        };
    }

    public RsaSecurityKey SecurityKey { get; }
    public SigningCredentials Credentials { get; }
    public IReadOnlyDictionary<string, string> Jwk { get; }
}

public sealed class TokenService(SigningKeyStore keys, IOptions<DirectoryOptions> options, TimeProvider clock)
{
    private readonly JsonWebTokenHandler _handler = new();

    public (string Token, DateTime ExpiresAtUtc) Create(UserRecord user, IReadOnlyCollection<string> roles, Applications.App app)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(options.Value.TokenLifetimeMinutes);

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Value.Issuer,
            Audience = app.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            SigningCredentials = keys.Credentials,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = user.UserId.ToString(),
                ["name"] = user.DisplayName,
                ["preferred_username"] = user.UserPrincipalName,
                ["roles"] = roles.ToArray(),
                ["jti"] = Guid.NewGuid().ToString("N"),
            }
        });

        return (token, expires);
    }
}
