using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace SyncHub.IntegrationTests;

/// <summary>
/// One throwaway SQL Server container per test run, with the real SyncHub schema and stored
/// procedures from /database. Tests run against the same T-SQL that production runs, rather than
/// mocks, because most of the risky logic (MERGE, newer-wins, reconciliation) lives in SQL.
///
/// Shared by every test class in the "sql" collection: starting SQL Server takes ~15s, so it is
/// paid once. Tests stay independent by using their own Source value (see <see cref="NewSource"/>)
/// instead of resetting the database between tests.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    // Only the scripts the tests need. 02 (75k Firm A rows) and 06 (directory) add start-up time
    // without being exercised here.
    private static readonly string[] Scripts =
        ["01_create_databases.sql", "03_synchub_schema.sql", "04_synchub_procedures.sql", "05_reporting.sql"];

    // Same image as docker-compose.yml, so the tests run on the SQL Server version the app uses.
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string SyncHubConnectionString { get; private set; } = "";

    /// <summary>Starts SQL Server and runs the schema and procedure scripts. Runs once per test run.</summary>
    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var master = _container.GetConnectionString();
        foreach (var script in Scripts)
            await RunScriptAsync(master, Path.Combine(DatabaseFolder(), script));

        SyncHubConnectionString = new SqlConnectionStringBuilder(master) { InitialCatalog = "SyncHub" }.ConnectionString;
    }

    /// <summary>Stops and removes the container.</summary>
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>A Source value no other test uses, so tests can share one database safely.</summary>
    public static string NewSource() => "T" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// Runs a sqlcmd-style script through SqlClient: split on GO lines, run each batch in order.
    /// SqlClient connects with QUOTED_IDENTIFIER ON, which the filtered indexes need.
    /// </summary>
    private static async Task RunScriptAsync(string connectionString, string path)
    {
        var batches = Regex.Split(await File.ReadAllTextAsync(path), @"^\s*GO\s*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        foreach (var batch in batches.Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 300 };
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // The .csproj copies /database/*.sql into the test output.
    private static string DatabaseFolder() => Path.Combine(AppContext.BaseDirectory, "database");
}

[CollectionDefinition("sql")]
public sealed class SqlCollection : ICollectionFixture<SqlServerFixture>;
