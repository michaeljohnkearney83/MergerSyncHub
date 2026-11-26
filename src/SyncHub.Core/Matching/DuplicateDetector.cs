namespace SyncHub.Core.Matching;

public sealed record ClientForMatching(int ClientId, string Source, string NormalisedName, string? PostCode);

public sealed record MatchCandidate(int ClientIdA, int ClientIdB, decimal Score, string Reason);

/// <summary>
/// Finds pairs of clients that are probably the same organisation, within one firm
/// (duplicate records) or across the two firms (a shared client, which matters for
/// conflicts and relationship management after a merger).
///
/// It only suggests. A person confirms or rejects each pair in the support dashboard,
/// because a false merge of two different clients is far worse than a missed match.
/// </summary>
public sealed class DuplicateDetector
{
    private const double PostcodeBonus = 0.05;
    private readonly double _threshold;

    /// <summary>Creates a detector that suggests pairs scoring at or above <paramref name="threshold"/>.</summary>
    /// <param name="threshold">Minimum score (0-1) to suggest a pair. 0.92 keeps false positives rare;
    /// a false merge of two clients is worse than a missed one.</param>
    public DuplicateDetector(double threshold = 0.92)
    {
        if (threshold is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(threshold));
        _threshold = threshold;
    }

    /// <summary>
    /// Scores every pair of clients that share the first three letters of their core name and returns
    /// the pairs at or above the threshold, each with a human-readable reason.
    /// </summary>
    /// <returns>Pairs with the lower client id first, so the same pair is stored once.</returns>
    public IReadOnlyList<MatchCandidate> FindCandidates(IEnumerable<ClientForMatching> clients)
    {
        var prepared = clients
            .Select(c => new Prepared(c, NameNormaliser.CoreName(c.NormalisedName), NormalisePostcode(c.PostCode)))
            .Where(p => p.CoreName.Length > 0)
            .ToList();

        // Blocking: only compare names that share their first three characters.
        // Comparing every pair is O(n^2) (two million comparisons for 2,000 clients);
        // blocking cuts that to a few thousand. The cost: a typo in the first three
        // letters will not be caught. A second blocking key (such as postcode) would fix that.
        var candidates = new List<MatchCandidate>();
        foreach (var block in prepared.GroupBy(p => p.CoreName[..Math.Min(3, p.CoreName.Length)]))
        {
            var members = block.ToList();
            for (var i = 0; i < members.Count; i++)
            for (var j = i + 1; j < members.Count; j++)
            {
                var candidate = Score(members[i], members[j]);
                if (candidate is not null) candidates.Add(candidate);
            }
        }
        return candidates;
    }

    /// <summary>
    /// Scores one pair: exact core-name match, else word-level similarity, with a small bonus for the
    /// same postcode. Returns <c>null</c> below the threshold.
    /// </summary>
    private MatchCandidate? Score(Prepared a, Prepared b)
    {
        var exact = a.CoreName == b.CoreName;
        var score = exact ? 1.0 : TokenSimilarity(a.Tokens, b.Tokens);
        if (score < _threshold - PostcodeBonus) return null;

        var samePostcode = a.Postcode is not null && a.Postcode == b.Postcode;
        if (samePostcode) score = Math.Min(1.0, score + PostcodeBonus);
        if (score < _threshold) return null;

        var reasons = new List<string>
        {
            exact ? "Same name once punctuation and legal suffixes are ignored" : $"Similar names ({score:P0})"
        };
        if (samePostcode) reasons.Add("same postcode");
        if (a.Client.Source != b.Client.Source) reasons.Add("appears in both legacy firms");

        var (first, second) = a.Client.ClientId < b.Client.ClientId ? (a, b) : (b, a);
        return new MatchCandidate(
            first.Client.ClientId,
            second.Client.ClientId,
            Math.Round((decimal)score, 4),
            string.Join("; ", reasons));
    }

    /// <summary>
    /// Word-level similarity (a symmetric Monge-Elkan score). Every word in one name must
    /// find a close match in the other, in both directions, and the weaker direction wins.
    ///
    /// Comparing whole strings instead gave many false positives such as
    /// "MERIDIAN HEALTHCARE" vs "MERIDIAN VENTURES": the long shared first word
    /// dominates Jaro-Winkler. Comparing word by word stops that, while still catching
    /// typos and spelling variants inside a word ("JAMESS" vs "JAMES").
    /// </summary>
    public static double TokenSimilarity(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0.0;
        return Math.Min(Directional(a, b), Directional(b, a));

        static double Directional(IReadOnlyList<string> from, IReadOnlyList<string> to) =>
            from.Average(word => to.Max(other => JaroWinkler.Similarity(word, other)));
    }

    /// <summary>Postcode without spaces, upper case, so "l1 1aa" and "L11AA" compare equal.</summary>
    private static string? NormalisePostcode(string? postcode) =>
        string.IsNullOrWhiteSpace(postcode) ? null : postcode.Replace(" ", string.Empty).ToUpperInvariant();

    private sealed record Prepared(ClientForMatching Client, string CoreName, string? Postcode)
    {
        public string[] Tokens { get; } = CoreName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
