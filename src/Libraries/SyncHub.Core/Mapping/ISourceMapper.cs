using SyncHub.Core.Models;

namespace SyncHub.Core.Mapping;

/// <summary>
/// Turns one source system's raw JSON into the hub's canonical shape.
/// The same mapper is used for scheduled syncs and for replaying dead letters,
/// so a fixed payload goes through exactly the same rules as live data.
/// </summary>
public interface ISourceMapper
{
    /// <summary>The source system this mapper handles, e.g. <see cref="Models.SourceSystems.FirmA"/>.</summary>
    string Source { get; }

    /// <summary>Maps one client from its raw source JSON. Never throws for bad data: returns a failure instead.</summary>
    /// <param name="sourceKey">The client's key in the source system.</param>
    /// <param name="rawJson">The record exactly as read from the source, or as edited by support before a replay.</param>
    MapResult<StagedClient> MapClient(string sourceKey, string rawJson);

    /// <summary>Maps one matter from its raw source JSON. Never throws for bad data: returns a failure instead.</summary>
    /// <param name="sourceKey">The matter's key in the source system.</param>
    /// <param name="rawJson">The record exactly as read from the source, or as edited by support before a replay.</param>
    MapResult<StagedMatter> MapMatter(string sourceKey, string rawJson);
}
