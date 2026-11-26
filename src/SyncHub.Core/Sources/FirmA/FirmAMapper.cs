using SyncHub.Core.Mapping;
using SyncHub.Core.Matching;
using SyncHub.Core.Models;

namespace SyncHub.Core.Sources.FirmA;

/// <summary>Row shapes read from the legacy FirmA_Legacy database (column aliases match these names).</summary>
public sealed class FirmAClientRow
{
    public int ClientId { get; set; }
    public string ClientCode { get; set; } = "";
    public string? ClientName { get; set; }
    public string? Address1 { get; set; }
    public string? Postcode { get; set; }
    public string? Country { get; set; }
    public DateTime? OpenDate { get; set; }
    public string Status { get; set; } = "A";
    public DateTime LastModified { get; set; }   // UK local time
}

public sealed class FirmAMatterRow
{
    public int MatterId { get; set; }
    public int? ClientId { get; set; }
    public string? MatterNo { get; set; }
    public string? Description { get; set; }
    public string? DeptCode { get; set; }
    public string? FeeEarner { get; set; }
    public DateTime? OpenDate { get; set; }
    public DateTime? CloseDate { get; set; }
    public string Status { get; set; } = "O";
    public DateTime LastModified { get; set; }   // UK local time
}

public sealed class FirmAMapper : ISourceMapper
{
    public string Source => SourceSystems.FirmA;

    public MapResult<StagedClient> MapClient(string sourceKey, string rawJson)
    {
        if (!SyncJson.TryDeserialize<FirmAClientRow>(rawJson, out var row, out var error))
            return MapResult<StagedClient>.Failure(sourceKey, MapErrorTypes.MalformedPayload, error);

        if (string.IsNullOrWhiteSpace(row.ClientName))
            return MapResult<StagedClient>.Failure(sourceKey, MapErrorTypes.MissingClientName,
                $"Client {row.ClientCode} (id {row.ClientId}) has no name in the legacy system.");

        var name = Text.Clean(row.ClientName, 200)!;

        return MapResult<StagedClient>.Success(new StagedClient(
            Source: Source,
            SourceClientKey: sourceKey,
            ClientName: name,
            NormalisedName: NameNormaliser.Normalise(name),
            Country: CountryNormaliser.Normalise(row.Country),
            PostCode: Text.Clean(row.Postcode?.ToUpperInvariant(), 12),
            OpenedOn: row.OpenDate?.Date,
            IsActive: !string.Equals(row.Status?.Trim(), "C", StringComparison.OrdinalIgnoreCase),
            SourceModifiedUtc: UkTime.ToUtc(row.LastModified),
            RawPayload: rawJson), sourceKey);
    }

    public MapResult<StagedMatter> MapMatter(string sourceKey, string rawJson)
    {
        if (!SyncJson.TryDeserialize<FirmAMatterRow>(rawJson, out var row, out var error))
            return MapResult<StagedMatter>.Failure(sourceKey, MapErrorTypes.MalformedPayload, error);

        if (row.ClientId is null)
            return MapResult<StagedMatter>.Failure(sourceKey, MapErrorTypes.MissingClientReference,
                $"Matter {row.MatterNo} has no client id.");

        if (string.IsNullOrWhiteSpace(row.MatterNo))
            return MapResult<StagedMatter>.Failure(sourceKey, MapErrorTypes.MissingMatterNumber,
                $"Matter id {row.MatterId} has no matter number.");

        if (row.OpenDate is not null && row.CloseDate is not null && row.CloseDate.Value.Date < row.OpenDate.Value.Date)
            return MapResult<StagedMatter>.Failure(sourceKey, MapErrorTypes.CloseDateBeforeOpenDate,
                $"Matter {row.MatterNo} closes ({row.CloseDate:yyyy-MM-dd}) before it opens ({row.OpenDate:yyyy-MM-dd}).");

        return MapResult<StagedMatter>.Success(new StagedMatter(
            Source: Source,
            SourceMatterKey: sourceKey,
            SourceClientKey: row.ClientId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            MatterNumber: Text.Clean(row.MatterNo, 30)!,
            Description: Text.Clean(row.Description, 500),
            PracticeAreaCode: Text.Clean(row.DeptCode?.ToUpperInvariant(), 30),
            FeeEarner: Text.Clean(row.FeeEarner?.ToUpperInvariant(), 50),
            OpenedOn: row.OpenDate?.Date,
            ClosedOn: row.CloseDate?.Date,
            IsOpen: !string.Equals(row.Status?.Trim(), "C", StringComparison.OrdinalIgnoreCase),
            SourceModifiedUtc: UkTime.ToUtc(row.LastModified),
            RawPayload: rawJson), sourceKey);
    }
}
