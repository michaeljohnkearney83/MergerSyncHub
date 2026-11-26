namespace SyncHub.Core.Mapping;

/// <summary>
/// The outcome of mapping one source record. Mapping never throws for bad data:
/// a bad record becomes a failure that is dead-lettered, and the batch carries on.
/// </summary>
public sealed class MapResult<T> where T : class
{
    private MapResult(T? value, string sourceKey, string? errorType, string? errorMessage)
    {
        Value = value;
        SourceKey = sourceKey;
        ErrorType = errorType;
        ErrorMessage = errorMessage;
    }

    public T? Value { get; }
    public string SourceKey { get; }
    public string? ErrorType { get; }
    public string? ErrorMessage { get; }
    public bool IsSuccess => Value is not null;

    /// <summary>A successfully mapped record, ready to stage.</summary>
    /// <param name="value">The record in the hub's shape.</param>
    /// <param name="sourceKey">The record's key in its source system.</param>
    public static MapResult<T> Success(T value, string sourceKey) => new(value, sourceKey, null, null);

    /// <summary>A record that failed mapping or validation. It becomes a dead letter for support to fix and replay.</summary>
    /// <param name="sourceKey">The record's key in its source system.</param>
    /// <param name="errorType">One of <see cref="MapErrorTypes"/>; groups failures on the dashboard and in metrics.</param>
    /// <param name="errorMessage">A plain-English explanation for the support analyst.</param>
    public static MapResult<T> Failure(string sourceKey, string errorType, string errorMessage) =>
        new(null, sourceKey, errorType, errorMessage);
}

public static class MapErrorTypes
{
    public const string MalformedPayload = "MalformedPayload";
    public const string MissingClientName = "MissingClientName";
    public const string MissingClientReference = "MissingClientReference";
    public const string MissingMatterNumber = "MissingMatterNumber";
    public const string InvalidDate = "InvalidDate";
    public const string CloseDateBeforeOpenDate = "CloseDateBeforeOpenDate";
}
