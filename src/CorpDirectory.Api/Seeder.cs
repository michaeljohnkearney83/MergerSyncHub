using Microsoft.Extensions.Options;

namespace CorpDirectory;

/// <summary>
/// Creates a starter set of accounts the first time the directory runs (when it has no users),
/// covering each role so every part of the dashboard can be demonstrated.
/// All share the demo password in Directory:SeedPassword and don't have to change it.
/// </summary>
public static class Seeder
{
    private sealed record Person(string Given, string Surname, string JobTitle, string Department, string Office,
        string[] Roles, bool Enabled = true);

    private static readonly Person[] People =
    [
        new("IT", "Admin", "Service Desk Lead", "IT", "Liverpool", ["Directory.Admin", "SyncHub.Admin"]),
        new("Alex", "Morgan", "Applications Support Analyst", "IT", "Liverpool", ["SyncHub.Support"]),
        new("Priya", "Shah", "Client Data Steward", "Risk & Compliance", "London", ["SyncHub.DataSteward"]),
        new("Tom", "Reid", "Partner", "Corporate", "London", ["SyncHub.Viewer"]),
        new("Jordan", "Lee", "Software Developer", "IT", "Chicago", []),                                  // no app access yet
        new("Sam", "Carter", "Paralegal", "Litigation", "Liverpool", ["SyncHub.Viewer"], Enabled: false), // a leaver
    ];

    public static async Task SeedAsync(IServiceProvider services)
    {
        var repo = services.GetRequiredService<DirectoryRepository>();
        var options = services.GetRequiredService<IOptions<DirectoryOptions>>().Value;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Seeder");

        try
        {
            if (await repo.CountUsersAsync() > 0) return;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Cannot reach the CorpDirectory database. Run database/06_directory.sql and check ConnectionStrings:Directory.");
            throw;
        }

        var groupsByRole = await repo.GetGroupIdsByRoleAsync();
        var hash = Passwords.Hash(options.SeedPassword);

        foreach (var p in People)
        {
            var upn = $"{p.Given.ToLowerInvariant()}.{p.Surname.ToLowerInvariant()}@{options.EmailDomain}";
            var groupIds = p.Roles.Select(r => groupsByRole[r]).ToArray();
            var id = await repo.CreateUserAsync(
                new NewUser(upn, p.Given, p.Surname, p.JobTitle, p.Department, p.Office, null, hash, MustChangePassword: false),
                groupIds, "system");
            if (!p.Enabled) await repo.SetEnabledAsync(id, false);
            await repo.AuditAsync("system", "UserCreated", upn, "Seed account");
        }

        logger.LogInformation("Seeded {Count} demo accounts (password: {Password})", People.Length, options.SeedPassword);
    }
}
