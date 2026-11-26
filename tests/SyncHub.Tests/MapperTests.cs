using System.Text.Json;
using SyncHub.Core.Mapping;
using SyncHub.Core.Sources.FirmA;
using SyncHub.Core.Sources.FirmB;

namespace SyncHub.Tests;

public class FirmBMapperTests
{
    private readonly FirmBMapper _mapper = new();

    private static string Matter(string? clientId = "C-10001", string? opened = "2024-01-15", string? closed = null,
        string? matterNumber = "10001-001") =>
        JsonSerializer.Serialize(new
        {
            id = "M-1", clientId, matterNumber, title = "Merger agreement", practiceGroup = "m&a",
            responsibleAttorney = "M. Chen", openedDate = opened, closedDate = closed,
            status = closed is null ? "open" : "closed", lastModifiedUtc = "2026-11-20T10:00:00Z"
        });

    [Fact]
    public void Maps_a_valid_matter()
    {
        var result = _mapper.MapMatter("M-1", Matter(closed: "2024-06-01"));

        Assert.True(result.IsSuccess);
        var m = result.Value!;
        Assert.Equal("C-10001", m.SourceClientKey);
        Assert.Equal("M&A", m.PracticeAreaCode);
        Assert.Equal(new DateTime(2024, 1, 15), m.OpenedOn);
        Assert.False(m.IsOpen);
        Assert.Equal(DateTimeKind.Utc, m.SourceModifiedUtc.Kind);
    }

    [Theory]
    [InlineData("2023-02-30")]   // impossible date
    [InlineData("31/12/2022")]   // wrong format
    public void Rejects_invalid_open_dates(string opened)
    {
        var result = _mapper.MapMatter("M-1", Matter(opened: opened));

        Assert.False(result.IsSuccess);
        Assert.Equal(MapErrorTypes.InvalidDate, result.ErrorType);
        Assert.Contains(opened, result.ErrorMessage);
    }

    [Fact]
    public void Rejects_a_matter_that_closes_before_it_opens()
    {
        var result = _mapper.MapMatter("M-1", Matter(opened: "2024-05-01", closed: "2024-04-01"));
        Assert.Equal(MapErrorTypes.CloseDateBeforeOpenDate, result.ErrorType);
    }

    [Fact]
    public void Rejects_a_matter_with_no_client()
    {
        var result = _mapper.MapMatter("M-1", Matter(clientId: null));
        Assert.Equal(MapErrorTypes.MissingClientReference, result.ErrorType);
    }

    [Fact]
    public void Rejects_malformed_json_without_throwing()
    {
        var result = _mapper.MapMatter("M-1", "{ this is not json");
        Assert.Equal(MapErrorTypes.MalformedPayload, result.ErrorType);
        Assert.Equal("M-1", result.SourceKey);
    }

    [Fact]
    public void Normalises_country_names()
    {
        var json = JsonSerializer.Serialize(new
        {
            id = "C-1", name = "Kestrel Aerospace Inc.", country = "USA", postalCode = "98108",
            openedDate = "2018-04-02", status = "active", lastModifiedUtc = "2026-11-20T10:00:00Z"
        });

        var result = _mapper.MapClient("C-1", json);

        Assert.Equal("United States", result.Value!.Country);
        Assert.Equal("KESTREL AEROSPACE INC", result.Value.NormalisedName);
    }
}

public class FirmAMapperTests
{
    private readonly FirmAMapper _mapper = new();

    private static string Client(string? name, DateTime lastModifiedUkLocal) =>
        JsonSerializer.Serialize(new FirmAClientRow
        {
            ClientId = 1000, ClientCode = "ACME001", ClientName = name, Postcode = "l3 1bp", Country = "GB",
            OpenDate = new DateTime(2015, 3, 2), Status = "A", LastModified = lastModifiedUkLocal
        }, SyncJson.Options);

    [Fact]
    public void Rejects_a_client_with_no_name()
    {
        var result = _mapper.MapClient("1000", Client(null, new DateTime(2024, 1, 10, 9, 15, 0)));
        Assert.Equal(MapErrorTypes.MissingClientName, result.ErrorType);
    }

    [Fact]
    public void Tidies_the_name_and_postcode()
    {
        var result = _mapper.MapClient("1000", Client("  Acme   Holdings Limited ", new DateTime(2024, 1, 10, 9, 15, 0)));

        Assert.Equal("Acme Holdings Limited", result.Value!.ClientName);
        Assert.Equal("ACME HOLDINGS LTD", result.Value.NormalisedName);
        Assert.Equal("L3 1BP", result.Value.PostCode);
        Assert.Equal("United Kingdom", result.Value.Country);
    }

    [Fact]
    public void Converts_UK_winter_time_to_UTC_unchanged()
    {
        var result = _mapper.MapClient("1000", Client("Acme", new DateTime(2024, 1, 10, 9, 15, 0)));
        Assert.Equal(new DateTime(2024, 1, 10, 9, 15, 0, DateTimeKind.Utc), result.Value!.SourceModifiedUtc);
    }

    [Fact]
    public void Converts_UK_summer_time_to_UTC_one_hour_earlier()
    {
        var result = _mapper.MapClient("1000", Client("Acme", new DateTime(2024, 7, 7, 7, 7, 0)));
        Assert.Equal(new DateTime(2024, 7, 7, 6, 7, 0, DateTimeKind.Utc), result.Value!.SourceModifiedUtc);
    }

    [Fact]
    public void Handles_a_time_that_does_not_exist_when_the_clocks_go_forward()
    {
        // 01:30 on 31 March 2024 never happened in the UK, but a legacy system can still store it
        var result = _mapper.MapClient("1000", Client("Acme", new DateTime(2024, 3, 31, 1, 30, 0)));
        Assert.True(result.IsSuccess);
    }
}
