using SyncHub.Core.Mapping;
using SyncHub.Core.Matching;
using SyncHub.Core.Models;

namespace SyncHub.Core.Sources.FirmB;

/// <summary>
/// Shapes returned by the Firm B REST API. Dates are strings because the API
/// does not guarantee they are valid: we validate them ourselves rather than
/// letting the JSON deserialiser fail the whole page.
/// </summary>
public sealed class FirmBClientDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Country { get; set; }
    public string? PostalCode { get; set; }
    public string? OpenedDate { get; set; }
    public string? Status { get; set; }
    public DateTime? LastModifiedUtc { get; set; }
}

public sealed class FirmBMatterDto
{
    public string? Id { get; set; }
    public string? ClientId { get; set; }
    public string? MatterNumber { get; set; }
    public string? Title { get; set; }
    public string? PracticeGroup { get; set; }
    public string? ResponsibleAttorney { get; set; }
    public string? OpenedDate { get; set; }
    public string? ClosedDate { get; set; }
    public string? Status { get; set; }
    public DateTime? LastModifiedUtc { get; set; }
}

public sealed class FirmBMapper : ISourceMapper
{
    public string Source => SourceSystems.FirmB;

    /// <summary>Firm B client: needs a name and a lastModifiedUtc; dates arrive as strings and are validated here.</summary>
    /// <inheritdoc cref="ISourceMapper.MapClient"/>
    public MapResult<StagedClient> MapClient(string sourceKey, string rawJson)
    {
        if (!SyncJson.TryDeserialize<FirmBClientDto>(rawJson, out var dto, out var error))
            return MapResult<StagedClient>.Failure(sourceKey, MapErrorTypes.MalformedPayload, error);

        if (dto.LastModifiedUtc is null)
            return MapResult<StagedClient>.Failure(sourceKey, MapErrorTypes.MalformedPayload, "lastModifiedUtc is missing.");

        if (string.IsNullOrWhiteSpace(dto.Name))
            return MapResult<StagedClient>.Failure(sourceKey, MapErrorTypes.MissingClientName,
                $"Client {sourceKey} has no name.");

        if (!Text.TryParseIsoDate(dto.OpenedDate, out var opened))
            return MapResult<StagedClient>.Failure(sourceKey, MapErrorTypes.InvalidDate,
                $"Client {sourceKey} has an invalid openedDate '{dto.OpenedDate}' (expected yyyy-MM-dd).");

        var name = Text.Clean(dto.Name, 200)!;

        return MapResult<StagedClient>.Success(new StagedClient(
            Source: Source,
            SourceClientKey: sourceKey,
            ClientName: name,
            NormalisedName: NameNormaliser.Normalise(name),
            Country: CountryNormaliser.Normalise(dto.Country),
            PostCode: Text.Clean(dto.PostalCode?.ToUpperInvariant(), 12),
            OpenedOn: opened,
            IsActive: !string.Equals(dto.Status, "inactive", StringComparison.OrdinalIgnoreCase),
            SourceModifiedUtc: AsUtc(dto.LastModifiedUtc.Value),
            RawPayload: rawJson), sourceKey);
    }

    /// <summary>Firm B matter: needs a client reference, matter number and valid dates, and must not close before it opens.</summary>
    /// <inheritdoc cref="ISourceMapper.MapMatter"/>
    public MapResult<StagedMatter> MapMatter(string sourceKey, string rawJson)
    {
        if (!SyncJson.TryDeserialize<FirmBMatterDto>(rawJson, out var dto, out var error))
            return MapResult<StagedMatter>.Failure(sourceKey, MapErrorTypes.MalformedPayload, error);

        if (dto.LastModifiedUtc is null)
            return MapResult<StagedMatter>.Failure(sourceKey, MapErrorTypes.MalformedPayload, "lastModifiedUtc is missing.");

        if (string.IsNullOrWhiteSpace(dto.ClientId))
            return MapResult<StagedMatter>.Failure(sourceKey, MapErrorTypes.MissingClientReference,
                $"Matter {dto.MatterNumber ?? sourceKey} has no clientId.");

        if (string.IsNullOrWhiteSpace(dto.MatterNumber))
            return MapResult<StagedMatter>.Failure(sourceKey, MapErrorTypes.MissingMatterNumber,
                $"Matter {sourceKey} has no matterNumber.");

        if (!Text.TryParseIsoDate(dto.OpenedDate, out var opened))
            return MapResult<StagedMatter>.Failure(sourceKey, MapErrorTypes.InvalidDate,
                $"Matter {dto.MatterNumber} has an invalid openedDate '{dto.OpenedDate}' (expected yyyy-MM-dd).");

        if (!Text.TryParseIsoDate(dto.ClosedDate, out var closed))
            return MapResult<StagedMatter>.Failure(sourceKey, MapErrorTypes.InvalidDate,
                $"Matter {dto.MatterNumber} has an invalid closedDate '{dto.ClosedDate}' (expected yyyy-MM-dd).");

        if (opened is not null && closed is not null && closed < opened)
            return MapResult<StagedMatter>.Failure(sourceKey, MapErrorTypes.CloseDateBeforeOpenDate,
                $"Matter {dto.MatterNumber} closes ({closed:yyyy-MM-dd}) before it opens ({opened:yyyy-MM-dd}).");

        return MapResult<StagedMatter>.Success(new StagedMatter(
            Source: Source,
            SourceMatterKey: sourceKey,
            SourceClientKey: dto.ClientId.Trim(),
            MatterNumber: Text.Clean(dto.MatterNumber, 30)!,
            Description: Text.Clean(dto.Title, 500),
            PracticeAreaCode: Text.Clean(dto.PracticeGroup?.ToUpperInvariant(), 30),
            FeeEarner: Text.Clean(dto.ResponsibleAttorney, 50),
            OpenedOn: opened,
            ClosedOn: closed,
            IsOpen: !string.Equals(dto.Status, "closed", StringComparison.OrdinalIgnoreCase),
            SourceModifiedUtc: AsUtc(dto.LastModifiedUtc.Value),
            RawPayload: rawJson), sourceKey);
    }

    /// <summary>The API documents UTC; treat a timestamp without an offset as UTC rather than server-local time.</summary>
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
