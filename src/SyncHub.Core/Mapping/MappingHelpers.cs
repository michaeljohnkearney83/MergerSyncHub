using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace SyncHub.Core.Mapping;

/// <summary>JSON settings shared by every component, so payloads round-trip consistently.</summary>
public static class SyncJson
{
    // camelCase, case-insensitive on read
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Deserialises a payload without throwing, so a malformed record becomes a dead letter
    /// instead of failing the whole batch.
    /// </summary>
    /// <returns><c>true</c> with <paramref name="value"/> set, or <c>false</c> with a readable <paramref name="error"/>.</returns>
    public static bool TryDeserialize<T>(string json, [NotNullWhen(true)] out T? value, out string error) where T : class
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(json, Options);
            if (value is null)
            {
                error = "Payload was empty or null.";
                return false;
            }
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            value = null;
            error = $"Payload is not valid JSON for {typeof(T).Name}: {ex.Message}";
            return false;
        }
    }
}

public static class CountryNormaliser
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UK"] = "United Kingdom",
        ["GB"] = "United Kingdom",
        ["Great Britain"] = "United Kingdom",
        ["England"] = "United Kingdom",
        ["Scotland"] = "United Kingdom",
        ["Wales"] = "United Kingdom",
        ["United Kingdom"] = "United Kingdom",
        ["US"] = "United States",
        ["USA"] = "United States",
        ["U.S."] = "United States",
        ["U.S.A."] = "United States",
        ["United States"] = "United States",
        ["United States of America"] = "United States",
    };

    /// <summary>
    /// Maps the many spellings of the same country ("UK", "GB", "England") to one name, so reports and
    /// duplicate matching aren't split by spelling. Unknown values are kept as entered.
    /// </summary>
    /// <returns>The canonical name, the trimmed input if unknown, or <c>null</c> if blank.</returns>
    public static string? Normalise(string? country)
    {
        if (string.IsNullOrWhiteSpace(country)) return null;
        var trimmed = country.Trim();
        return Map.TryGetValue(trimmed, out var canonical) ? canonical : trimmed;
    }
}

public static class Text
{
    /// <summary>
    /// Trims, collapses repeated spaces and truncates to the hub column's length, so a long source
    /// value can't fail the bulk insert.
    /// </summary>
    /// <returns>The cleaned text, or <c>null</c> if blank.</returns>
    public static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    /// <summary>Parses a <c>yyyy-MM-dd</c> date. A missing date is allowed; a present but malformed one is not.</summary>
    /// <returns><c>true</c> if blank or valid (with <paramref name="date"/> set when present); <c>false</c> if malformed.</returns>
    public static bool TryParseIsoDate(string? value, out DateTime? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(value)) return true;   // absent is allowed; malformed is not
        if (DateTime.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            date = parsed;
            return true;
        }
        return false;
    }
}

/// <summary>
/// The legacy UK system stores local (UK) time. The hub stores UTC.
/// Converting needs care around the clock changes.
/// </summary>
public static class UkTime
{
    private static readonly TimeZoneInfo London = FindLondon();

    /// <summary>UK time zone, by IANA id where available and the Windows id otherwise.</summary>
    private static TimeZoneInfo FindLondon()
    {
        // IANA id works on Windows 10+ with .NET 6+; fall back to the Windows id just in case
        if (TimeZoneInfo.TryFindSystemTimeZoneById("Europe/London", out var tz)) return tz;
        return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
    }

    /// <summary>
    /// Converts a UK local timestamp from the legacy system to UTC. Times that don't exist (the skipped
    /// hour in March) move forward an hour; repeated times (the extra hour in October) are read as GMT.
    /// That October ambiguity is why scheduled merges don't use newer-wins (see usp_MergeClients).
    /// </summary>
    public static DateTime ToUtc(DateTime ukLocal)
    {
        var local = DateTime.SpecifyKind(ukLocal, DateTimeKind.Unspecified);

        // 01:00-01:59 on the last Sunday in March does not exist in UK time.
        // A legacy system can still store it; shift forward rather than throw.
        if (London.IsInvalidTime(local))
            local = local.AddHours(1);

        // Ambiguous times (the repeated hour in October) resolve to standard time (GMT).
        return TimeZoneInfo.ConvertTimeToUtc(local, London);
    }
}
