using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace SyncHub.Core.Mapping;

/// <summary>JSON settings shared by every component, so payloads round-trip consistently.</summary>
public static class SyncJson
{
    // camelCase, case-insensitive on read
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

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

    public static string? Normalise(string? country)
    {
        if (string.IsNullOrWhiteSpace(country)) return null;
        var trimmed = country.Trim();
        return Map.TryGetValue(trimmed, out var canonical) ? canonical : trimmed;
    }
}

public static class Text
{
    public static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

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

    private static TimeZoneInfo FindLondon()
    {
        // IANA id works on Windows 10+ with .NET 6+; fall back to the Windows id just in case
        if (TimeZoneInfo.TryFindSystemTimeZoneById("Europe/London", out var tz)) return tz;
        return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
    }

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
