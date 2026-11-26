namespace SyncHub.Core.Models;

public static class Entities
{
    public const string Client = "Client";
    public const string Matter = "Matter";
}

public static class SourceSystems
{
    public const string FirmA = "FirmA";
    public const string FirmB = "FirmB";
}

/// <summary>
/// A record exactly as read from a source system. RawJson is kept so that a failed
/// record can be stored, corrected by a person, and replayed through the same mapping.
/// SourceModified is in the source system's own time basis and drives the watermark.
/// </summary>
public sealed record SourceRecord(
    string Source,
    string Entity,
    string SourceKey,
    string RawJson,
    DateTime SourceModified);

/// <summary>A client after mapping and validation, ready for stg.Client.</summary>
public sealed record StagedClient(
    string Source,
    string SourceClientKey,
    string ClientName,
    string NormalisedName,
    string? Country,
    string? PostCode,
    DateTime? OpenedOn,
    bool IsActive,
    DateTime SourceModifiedUtc,
    string RawPayload);

/// <summary>A matter after mapping and validation, ready for stg.Matter.</summary>
public sealed record StagedMatter(
    string Source,
    string SourceMatterKey,
    string SourceClientKey,
    string MatterNumber,
    string? Description,
    string? PracticeAreaCode,
    string? FeeEarner,
    DateTime? OpenedOn,
    DateTime? ClosedOn,
    bool IsOpen,
    DateTime SourceModifiedUtc,
    string RawPayload);
