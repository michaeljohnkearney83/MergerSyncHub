using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace SyncHub.Observability;

/// <summary>
/// One call per service wires up metrics and logs over OTLP to the OpenTelemetry Collector.
///
/// Design points:
///   * Services only speak OTLP. The collector decides where data goes (Prometheus, Loki today;
///     Azure Monitor tomorrow), so changing back end never means changing service code.
///   * No instrumentation packages are needed for the basics: since .NET 8 ASP.NET Core, Kestrel,
///     HttpClient and the runtime publish their own System.Diagnostics.Metrics meters, so we just
///     subscribe to them by name. Polly (the Firm B resilience pipeline) does the same.
///   * Logs keep going to the console as well, so nothing changes for someone running locally
///     without the monitoring stack. If the collector is down, the exporter drops data after a
///     short timeout; telemetry must never take a service down.
///   * Leave OpenTelemetry:OtlpEndpoint empty to switch export off entirely.
/// </summary>
public static class ObservabilityExtensions
{
    /// <summary>Built-in meters worth collecting from every service.</summary>
    private static readonly string[] PlatformMeters =
    [
        "Microsoft.AspNetCore.Hosting",          // http.server.request.duration
        "Microsoft.AspNetCore.Server.Kestrel",   // active connections
        "Microsoft.AspNetCore.Diagnostics",      // unhandled exceptions
        "System.Net.Http",                       // http.client.request.duration (Firm B, directory keys)
        "System.Runtime",                        // GC, thread pool, CPU, memory (.NET 9+)
        "Polly"                                  // retries, circuit breaker state changes
    ];

    /// <summary>
    /// Sends this service's metrics and logs over OTLP to the collector at <c>OpenTelemetry:OtlpEndpoint</c>.
    /// Does nothing when that setting is empty.
    /// </summary>
    /// <param name="builder">The host builder for the service.</param>
    /// <param name="serviceName">Becomes <c>service.name</c>: the Prometheus job and the Loki <c>service_name</c> label.</param>
    /// <param name="extraMeters">The service's own meters, e.g. <c>SyncHub.Worker</c>, added to the built-in ASP.NET Core, HttpClient, runtime and Polly meters.</param>
    public static IHostApplicationBuilder AddObservability(
        this IHostApplicationBuilder builder, string serviceName, params string[] extraMeters)
    {
        var endpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
            return builder;

        var otlpEndpoint = new Uri(endpoint);
        void ConfigureExporter(OtlpExporterOptions o)
        {
            o.Endpoint = otlpEndpoint;
            o.Protocol = OtlpExportProtocol.Grpc;
            o.TimeoutMilliseconds = 5000;
        }

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(serviceName, serviceVersion: typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString())
                .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName)]))
            .WithMetrics(m => m
                .AddMeter(PlatformMeters)
                .AddMeter(extraMeters)
                .AddOtlpExporter((exporter, reader) =>
                {
                    ConfigureExporter(exporter);
                    // 15s matches the Prometheus scrape interval.
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 15_000;
                }));

        // Logs: scopes become attributes, so Source and CorrelationId from the worker's
        // BeginScope arrive in Loki as structured metadata you can filter on.
        builder.Logging.AddOpenTelemetry(o =>
        {
            o.IncludeScopes = true;
            o.IncludeFormattedMessage = true;
            o.ParseStateValues = true;
            o.AddOtlpExporter(ConfigureExporter);
        });

        return builder;
    }
}
