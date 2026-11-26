using System.Globalization;
using FirmB.MockApi;
using SyncHub.Observability;

var builder = WebApplication.CreateBuilder(args);
builder.AddObservability("FirmB.MockApi");
builder.Services.AddSingleton<FirmBStore>();
builder.Services.AddHostedService<ChangeSimulator>();

var app = builder.Build();
var config = app.Configuration;
var log = app.Logger;

/* API key check: the real system would use OAuth client credentials */
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") &&
        ctx.Request.Headers["X-Api-Key"] != config["ApiKey"])
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = "Missing or invalid X-Api-Key" });
        return;
    }
    await next();
});

/* Fault injection: random outages and slow responses, to exercise the worker's retry policy */
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/api"))
    {
        await next();
        return;
    }

    var failureRate = config.GetValue("FaultInjection:FailureRate", 0.10);
    var slowRate = config.GetValue("FaultInjection:SlowRate", 0.03);
    var slowDelayMs = config.GetValue("FaultInjection:SlowDelayMs", 8000);
    var roll = Random.Shared.NextDouble();

    if (roll < failureRate)
    {
        log.LogWarning("Injected 503 for {Path}{Query}", ctx.Request.Path, ctx.Request.QueryString);
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsJsonAsync(new { error = "Simulated outage" });
        return;
    }

    if (roll < failureRate + slowRate)
    {
        log.LogWarning("Injected {Delay}ms delay for {Path}{Query}", slowDelayMs, ctx.Request.Path, ctx.Request.QueryString);
        try
        {
            await Task.Delay(slowDelayMs, ctx.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            return;   // the client gave up (timed out) - nothing to send
        }
    }

    await next();
});

app.MapGet("/", () => Results.Text(
    "Firm B mock practice management API. Endpoints: /api/stats, /api/clients, /api/matters (header X-Api-Key required)."));

app.MapGet("/api/stats", (FirmBStore store) => Results.Ok(store.Stats()));

app.MapGet("/api/clients", (FirmBStore store, string? modifiedSince, int? page, int? pageSize) =>
    TryParseSince(modifiedSince, out var since)
        ? Results.Ok(store.GetClients(since, Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 200, 1, 1000)))
        : Results.BadRequest(new { error = "modifiedSince must be an ISO 8601 date/time" }));

app.MapGet("/api/matters", (FirmBStore store, string? modifiedSince, int? page, int? pageSize) =>
    TryParseSince(modifiedSince, out var since)
        ? Results.Ok(store.GetMatters(since, Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 200, 1, 1000)))
        : Results.BadRequest(new { error = "modifiedSince must be an ISO 8601 date/time" }));

app.MapGet("/api/clients/{id}", (FirmBStore store, string id) =>
    store.GetClient(id) is { } c ? Results.Ok(c) : Results.NotFound());

app.MapGet("/api/matters/{id}", (FirmBStore store, string id) =>
    store.GetMatter(id) is { } m ? Results.Ok(m) : Results.NotFound());

/* Demo helper: make some changes right now instead of waiting for the simulator */
app.MapPost("/admin/simulate-changes", (FirmBStore store, int? count) =>
    Results.Ok(new { changed = store.SimulateChanges(Math.Clamp(count ?? 10, 1, 500)) }));

app.Run();

// Parses the modifiedSince query value as UTC. Absent is fine (all records); malformed is a 400.
static bool TryParseSince(string? value, out DateTime? since)
{
    since = null;
    if (string.IsNullOrWhiteSpace(value)) return true;
    if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        return false;
    since = parsed.UtcDateTime;
    return true;
}

/// <summary>Background "users" editing matters in Firm B every few seconds.</summary>
internal sealed class ChangeSimulator(FirmBStore store, IConfiguration config, ILogger<ChangeSimulator> logger) : BackgroundService
{
    /// <summary>Every <c>ChangeSimulator:IntervalSeconds</c>, edits a few matters, until shutdown.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(config.GetValue("ChangeSimulator:IntervalSeconds", 20));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var changed = store.SimulateChanges(Random.Shared.Next(3, 9));
                logger.LogInformation("Simulated {Count} changes in Firm B", changed);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
