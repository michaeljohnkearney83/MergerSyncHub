using SyncHub.Core.Matching;

namespace SyncHub.Tests;

public class NameNormaliserTests
{
    [Theory]
    [InlineData("ACME HOLDINGS LTD", "ACME HOLDINGS LTD")]
    [InlineData("Acme Holdings Limited ", "ACME HOLDINGS LTD")]
    [InlineData("MERSEY LOGISTICS PUBLIC LIMITED COMPANY", "MERSEY LOGISTICS PLC")]
    [InlineData("The St. James's Family Office Ltd", "ST JAMESS FAMILY OFFICE LTD")]
    [InlineData("Hudson & Pike Media, Inc.", "HUDSON AND PIKE MEDIA INC")]
    [InlineData("Lakeshore Ventures, L.L.C.", "LAKESHORE VENTURES LLC")]
    [InlineData("Prairie Wind Energy Corporation", "PRAIRIE WIND ENERGY CORP")]
    [InlineData("The", "THE")]   // a name that is only "The" is left alone
    public void Normalise_produces_standard_form(string input, string expected) =>
        Assert.Equal(expected, NameNormaliser.Normalise(input));

    [Theory]
    [InlineData("ACME HOLDINGS LTD", "ACME HOLDINGS")]
    [InlineData("KESTREL AEROSPACE INC", "KESTREL AEROSPACE")]
    [InlineData("SOMETHING CO LTD", "SOMETHING")]
    [InlineData("LTD", "LTD")]   // never strip a name down to nothing
    public void CoreName_strips_trailing_legal_suffixes(string input, string expected) =>
        Assert.Equal(expected, NameNormaliser.CoreName(input));
}

public class JaroWinklerTests
{
    [Fact]
    public void Identical_strings_score_one() => Assert.Equal(1.0, JaroWinkler.Similarity("ACME", "ACME"));

    [Fact]
    public void Completely_different_strings_score_zero() => Assert.Equal(0.0, JaroWinkler.Similarity("ABC", "XYZ"));

    [Fact]
    public void Matches_published_reference_value()
    {
        // MARTHA / MARHTA is the textbook example: 0.961
        Assert.Equal(0.961, JaroWinkler.Similarity("MARTHA", "MARHTA"), 3);
    }

    [Fact]
    public void Is_symmetric() =>
        Assert.Equal(JaroWinkler.Similarity("DWAYNE", "DUANE"), JaroWinkler.Similarity("DUANE", "DWAYNE"), 10);
}

public class DuplicateDetectorTests
{
    private static ClientForMatching Client(int id, string source, string name, string? postcode = null) =>
        new(id, source, NameNormaliser.Normalise(name), postcode);

    [Fact]
    public void Finds_same_client_with_different_legal_suffix_across_firms()
    {
        var candidates = new DuplicateDetector().FindCandidates(
        [
            Client(1, "FirmA", "Kestrel Aerospace Ltd"),
            Client(2, "FirmB", "Kestrel Aerospace Inc."),
        ]);

        var match = Assert.Single(candidates);
        Assert.Equal(1.0m, match.Score);
        Assert.Contains("both legacy firms", match.Reason);
    }

    [Fact]
    public void Does_not_match_companies_that_only_share_a_first_word()
    {
        // Whole-string Jaro-Winkler scores these about 0.94; word-level matching rejects them
        var candidates = new DuplicateDetector().FindCandidates(
        [
            Client(1, "FirmA", "Meridian Healthcare Limited"),
            Client(2, "FirmA", "Meridian Ventures LLP"),
        ]);

        Assert.Empty(candidates);
    }

    [Fact]
    public void Tolerates_a_small_spelling_difference()
    {
        var candidates = new DuplicateDetector().FindCandidates(
        [
            Client(1, "FirmA", "The St James's Family Office"),
            Client(2, "FirmA", "St. James Family Office Ltd"),
        ]);

        Assert.Single(candidates);
    }

    [Fact]
    public void Same_postcode_lifts_a_borderline_score_over_the_threshold()
    {
        var withoutPostcode = new DuplicateDetector(0.99).FindCandidates(
        [
            Client(1, "FirmA", "Acme Holdngs Ltd"),   // typo: scores 0.9875 on its own
            Client(2, "FirmA", "Acme Holdings Ltd"),
        ]);
        var withPostcode = new DuplicateDetector(0.99).FindCandidates(
        [
            Client(1, "FirmA", "Acme Holdngs Ltd", "L3 1BP"),
            Client(2, "FirmA", "Acme Holdings Ltd", "l31bp"),   // formatting differences ignored
        ]);

        Assert.Empty(withoutPostcode);
        Assert.Contains("same postcode", Assert.Single(withPostcode).Reason);
    }

    [Fact]
    public void Always_stores_the_lower_id_first()
    {
        var candidates = new DuplicateDetector().FindCandidates(
        [
            Client(9, "FirmB", "Lakeshore Ventures, L.L.C."),
            Client(3, "FirmB", "Lakeshore Ventures LLC"),
        ]);

        var match = Assert.Single(candidates);
        Assert.Equal(3, match.ClientIdA);
        Assert.Equal(9, match.ClientIdB);
    }
}
