using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SyncHub.Worker.Pipeline;

/// <summary>
/// Business metrics for the sync engine, published on the "SyncHub.Worker" meter.
///
/// Why these and not more: each one answers a question support is actually asked.
///   * Is data flowing?            synchub.sync.time_since_success (the SyncStale alert)
///   * Are runs failing?           synchub.sync.runs by outcome
///   * Is the data any good?       synchub.records.rejected / synchub.records.read
///   * How much is changing?       synchub.records.merged by operation
///   * Is it getting slower?       synchub.sync.duration
///   * Which step broke the cycle? synchub.cycle.step_failures by step
///
/// Labels are deliberately low-cardinality (source, entity, outcome, error type, step).
/// Record keys, run ids and correlation ids belong in logs, never in metric labels:
/// every distinct label value is a new time series in Prometheus.
/// </summary>
public sealed class SyncMetrics
{
    public const string MeterName = "SyncHub.Worker";

    private readonly Counter<long> _runs;
    private readonly Histogram<double> _runDuration;
    private readonly Counter<long> _read;
    private readonly Counter<long> _merged;
    private readonly Counter<long> _rejected;
    private readonly Counter<long> _stepFailures;
    private readonly Histogram<double> _cycleDuration;

    // Last successful run per source. Seeded with the start time, so a source that has
    // never succeeded since start-up still shows a growing gap and can trip the alert.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSuccess = new();
    private readonly TimeProvider _clock;
    private readonly DateTimeOffset _startedAt;

    public SyncMetrics(IMeterFactory meterFactory, TimeProvider clock)
    {
        _clock = clock;
        _startedAt = clock.GetUtcNow();
        var meter = meterFactory.Create(MeterName);

        _runs = meter.CreateCounter<long>("synchub.sync.runs", "{run}", "Sync runs, by source and outcome.");
        _runDuration = meter.CreateHistogram<double>("synchub.sync.duration", "s", "Time to sync one source.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120] });
        _read = meter.CreateCounter<long>("synchub.records.read", "{record}", "Records read from a source.");
        _merged = meter.CreateCounter<long>("synchub.records.merged", "{record}", "Records inserted or updated in core.");
        _rejected = meter.CreateCounter<long>("synchub.records.rejected", "{record}", "Records sent to the dead-letter queue.");
        _stepFailures = meter.CreateCounter<long>("synchub.cycle.step_failures", "{failure}", "Cycle steps that threw.");
        _cycleDuration = meter.CreateHistogram<double>("synchub.cycle.duration", "s", "Time for one full cycle.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.5, 1, 2.5, 5, 10, 30, 60, 120, 300] });

        meter.CreateObservableGauge("synchub.sync.time_since_success", ObserveTimeSinceSuccess, "s",
            "Seconds since the last successful sync of each source.");
    }

    /// <summary>Call once per source at start-up so its gauge exists before the first run finishes.</summary>
    public void TrackSource(string source) => _lastSuccess.TryAdd(source, _startedAt);

    public void RunCompleted(string source, string outcome, TimeSpan elapsed)
    {
        var tags = new TagList { { "source", source }, { "outcome", outcome } };
        _runs.Add(1, tags);
        _runDuration.Record(elapsed.TotalSeconds, tags);

        // "SucceededWithErrors" still moved the watermark, so data is flowing.
        if (outcome is "Succeeded" or "SucceededWithErrors")
            _lastSuccess[source] = _clock.GetUtcNow();
        else
            _lastSuccess.TryAdd(source, _startedAt);
    }

    public void RecordsRead(string source, string entity, int count) =>
        _read.Add(count, new TagList { { "source", source }, { "entity", entity } });

    public void RecordsMerged(string source, string entity, int inserted, int updated)
    {
        _merged.Add(inserted, new TagList { { "source", source }, { "entity", entity }, { "operation", "insert" } });
        _merged.Add(updated, new TagList { { "source", source }, { "entity", entity }, { "operation", "update" } });
    }

    public void RecordsRejected(string source, string entity, string errorType, int count = 1) =>
        _rejected.Add(count, new TagList { { "source", source }, { "entity", entity }, { "error_type", errorType } });

    public void StepFailed(string step) => _stepFailures.Add(1, new KeyValuePair<string, object?>("step", step));

    public void CycleCompleted(TimeSpan elapsed) => _cycleDuration.Record(elapsed.TotalSeconds);

    private IEnumerable<Measurement<double>> ObserveTimeSinceSuccess()
    {
        var now = _clock.GetUtcNow();
        foreach (var (source, at) in _lastSuccess)
            yield return new Measurement<double>((now - at).TotalSeconds, new KeyValuePair<string, object?>("source", source));
    }
}
