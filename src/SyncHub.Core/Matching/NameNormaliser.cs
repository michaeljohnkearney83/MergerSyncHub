using System.Text;

namespace SyncHub.Core.Matching;

/// <summary>
/// Puts company names into a standard form so they can be compared.
///   "The St. James's Family Office Ltd"         -> "ST JAMESS FAMILY OFFICE LTD"
///   "MERSEY LOGISTICS PUBLIC LIMITED COMPANY"   -> "MERSEY LOGISTICS PLC"
///   "Hudson &amp; Pike Media, Inc."             -> "HUDSON AND PIKE MEDIA INC"
/// </summary>
public static class NameNormaliser
{
    private static readonly Dictionary<string, string> TokenMap = new(StringComparer.Ordinal)
    {
        ["LIMITED"] = "LTD",
        ["INCORPORATED"] = "INC",
        ["CORPORATION"] = "CORP",
        ["COMPANY"] = "CO",
        ["&"] = "AND",
    };

    private static readonly HashSet<string> LegalSuffixes = new(StringComparer.Ordinal)
    {
        "LTD", "PLC", "LLP", "LLC", "INC", "CORP", "CO", "LP", "GMBH", "AG", "SA", "BV", "NV"
    };

    /// <summary>Trims and collapses whitespace, keeping the original casing for display.</summary>
    public static string Tidy(string name) =>
        string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static string Normalise(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.ToUpperInvariant())
        {
            if (ch is '\'' or '’' or '.') continue;          // ST. JAMES'S -> ST JAMESS, L.L.C. -> LLC
            if (ch == '&') { sb.Append(" & "); continue; }
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }

        var tokens = sb.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => TokenMap.TryGetValue(t, out var mapped) ? mapped : t)
            .ToList();

        // "PUBLIC LTD CO" (from PUBLIC LIMITED COMPANY) -> PLC
        for (var i = 0; i + 2 < tokens.Count; i++)
        {
            if (tokens[i] == "PUBLIC" && tokens[i + 1] == "LTD" && tokens[i + 2] == "CO")
            {
                tokens.RemoveRange(i, 3);
                tokens.Insert(i, "PLC");
            }
        }

        if (tokens.Count > 1 && tokens[0] == "THE") tokens.RemoveAt(0);

        return string.Join(' ', tokens);
    }

    /// <summary>
    /// Removes trailing legal-form words, so "ACME HOLDINGS LTD" and "ACME HOLDINGS CORP"
    /// compare as the same core name. They may well be different legal entities (a UK
    /// company and its US affiliate), which is exactly what a conflicts reviewer wants to see.
    /// </summary>
    public static string CoreName(string normalisedName)
    {
        var tokens = normalisedName.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 1 && LegalSuffixes.Contains(tokens[^1]))
            tokens.RemoveAt(tokens.Count - 1);
        return string.Join(' ', tokens);
    }
}
