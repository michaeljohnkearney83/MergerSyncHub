using SyncHub.Core.Models;

namespace SyncHub.Core.Mapping;

/// <summary>
/// Turns one source system's raw JSON into the hub's canonical shape.
/// The same mapper is used for scheduled syncs and for replaying dead letters,
/// so a fixed payload goes through exactly the same rules as live data.
/// </summary>
public interface ISourceMapper
{
    string Source { get; }

    MapResult<StagedClient> MapClient(string sourceKey, string rawJson);

    MapResult<StagedMatter> MapMatter(string sourceKey, string rawJson);
}
