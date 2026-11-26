using Microsoft.Extensions.Options;
using Polly;
using SyncHub.Core.Mapping;
using SyncHub.Core.Sources.FirmA;
using SyncHub.Core.Sources.FirmB;
using SyncHub.Observability;
using SyncHub.Worker;
using SyncHub.Worker.Data;
using SyncHub.Worker.Pipeline;
using SyncHub.Worker.Sources;

var builder = Host.CreateApplicationBuilder(args);

// Scopes put Source and CorrelationId on every line, and CorrelationId is also stored
// on ops.SyncRun, so a failed run in the dashboard can be matched to its log lines.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.IncludeScopes = true;
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

// Metrics and logs over OTLP to the collector (Prometheus + Loki). See SyncHub.Observability.
builder.AddObservability("SyncHub.Worker", SyncMetrics.MeterName);

builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection(SyncOptions.Section));
builder.Services.Configure<FirmBOptions>(builder.Configuration.GetSection(FirmBOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);

// Data access
builder.Services.AddSingleton<SqlConnectionFactory>();
builder.Services.AddSingleton<SyncHubRepository>();

// Mapping and pipeline
builder.Services.AddSingleton<ISourceMapper, FirmAMapper>();
builder.Services.AddSingleton<ISourceMapper, FirmBMapper>();
builder.Services.AddSingleton<MapperRegistry>();
builder.Services.AddSingleton<SyncMetrics>();
builder.Services.AddSingleton<SourceSyncService>();
builder.Services.AddSingleton<ReplayService>();
builder.Services.AddSingleton<DuplicateDetectionService>();
builder.Services.AddSingleton<HousekeepingService>();
builder.Services.AddScoped<SyncCycle>();

// Source connectors
builder.Services.AddScoped<ISourceConnector, FirmAConnector>();

builder.Services
    .AddHttpClient<FirmBConnector>((sp, http) =>
    {
        var firmB = sp.GetRequiredService<IOptions<FirmBOptions>>().Value;
        http.BaseAddress = new Uri(firmB.BaseUrl);
        http.DefaultRequestHeaders.Add("X-Api-Key", firmB.ApiKey);
    })
    // Retries with exponential backoff and jitter, a per-attempt timeout, an overall timeout,
    // and a circuit breaker so a dead API is not hammered. Retries only happen for transient
    // failures (5xx, 408, 429, network errors, timeouts); a 400 or 401 fails immediately.
    .AddStandardResilienceHandler(o =>
    {
        o.Retry.MaxRetryAttempts = 5;
        o.Retry.BackoffType = DelayBackoffType.Exponential;
        o.Retry.UseJitter = true;
        o.Retry.Delay = TimeSpan.FromSeconds(1);
        o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(5);
        o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(90);
        o.CircuitBreaker.FailureRatio = 0.5;
        o.CircuitBreaker.MinimumThroughput = 10;
        o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);   // must be at least 2x the attempt timeout
        o.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(30);
    });
builder.Services.AddScoped<ISourceConnector>(sp => sp.GetRequiredService<FirmBConnector>());

builder.Services.AddHostedService<SyncWorker>();

builder.Build().Run();
