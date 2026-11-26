using System.Globalization;

namespace FirmB.MockApi;

public sealed record ClientDto(string Id, string? Name, string? Country, string? PostalCode,
    string? OpenedDate, string Status, DateTime LastModifiedUtc);

public sealed record MatterDto(string Id, string? ClientId, string MatterNumber, string Title, string PracticeGroup,
    string ResponsibleAttorney, string? OpenedDate, string? ClosedDate, string Status, DateTime LastModifiedUtc);

public sealed record PageResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount, bool HasMore);

/// <summary>
/// In-memory data for the mock Firm B system. Generated from a fixed seed, so every
/// run produces the same clients and matters, including the same deliberate problems:
///   - clients that also exist in Firm A under slightly different names
///   - duplicate clients within Firm B
///   - a client with no name and one with an impossible date
///   - matters pointing at clients that do not exist, or at no client
///   - matters with invalid dates or closing before they open
///   - a practice group code (LABOR_EMPLOYMENT) that the hub has no mapping for
/// </summary>
public sealed class FirmBStore
{
    private readonly object _sync = new();
    private readonly List<Client> _clients = [];
    private readonly List<Matter> _matters = [];
    private readonly Random _random = new(42);
    private int _nextMatterNumber;

    private static readonly string[] PracticeGroups =
        ["LITIGATION", "M&A", "CAPITAL_MARKETS", "IP", "PRIVATE_WEALTH", "REAL_ESTATE", "TAX", "LABOR_EMPLOYMENT"];
    private static readonly string[] Attorneys =
        ["J. Rivera", "M. Chen", "S. Patel", "A. Okafor", "R. Goldberg", "K. Novak", "D. Alvarez", "L. Brooks"];
    private static readonly string[] MatterTitles =
        ["Securities class action defense", "Series B financing", "Patent infringement suit", "Estate plan restructuring",
         "Commercial lease negotiation", "Merger agreement", "Trade secret dispute", "Tax controversy",
         "Wage and hour class action", "Licensing agreement"];
    private static readonly string[] ZipCodes = ["60601", "10019", "94105", "77002", "20004", "90071", "02110", "33131"];

    /// <summary>Builds the in-memory data set with a fixed random seed, so every start has the same clients, matters and bad records.</summary>
    public FirmBStore()
    {
        Seed();
    }

    /* ---------------- Queries ---------------- */

    /// <summary>Total clients and matters, used by the worker for reconciliation.</summary>
    public object Stats()
    {
        lock (_sync) return new { clients = _clients.Count, matters = _matters.Count };
    }

    /// <summary>One page of clients changed at or after <paramref name="modifiedSinceUtc"/> (all when null), oldest change first.</summary>
    public PageResponse<ClientDto> GetClients(DateTime? modifiedSinceUtc, int page, int pageSize)
    {
        lock (_sync)
        {
            var filtered = _clients
                .Where(c => modifiedSinceUtc is null || c.LastModifiedUtc >= modifiedSinceUtc)
                .OrderBy(c => c.LastModifiedUtc).ThenBy(c => c.Id, StringComparer.Ordinal)
                .ToList();
            var items = filtered.Skip((page - 1) * pageSize).Take(pageSize).Select(c => c.ToDto()).ToList();
            return new PageResponse<ClientDto>(items, page, pageSize, filtered.Count, page * pageSize < filtered.Count);
        }
    }

    /// <summary>One page of matters changed at or after <paramref name="modifiedSinceUtc"/> (all when null), oldest change first.</summary>
    public PageResponse<MatterDto> GetMatters(DateTime? modifiedSinceUtc, int page, int pageSize)
    {
        lock (_sync)
        {
            var filtered = _matters
                .Where(m => modifiedSinceUtc is null || m.LastModifiedUtc >= modifiedSinceUtc)
                .OrderBy(m => m.LastModifiedUtc).ThenBy(m => m.Id, StringComparer.Ordinal)
                .ToList();
            var items = filtered.Skip((page - 1) * pageSize).Take(pageSize).Select(m => m.ToDto()).ToList();
            return new PageResponse<MatterDto>(items, page, pageSize, filtered.Count, page * pageSize < filtered.Count);
        }
    }

    /// <summary>One client by id, or <c>null</c>.</summary>
    public ClientDto? GetClient(string id)
    {
        lock (_sync) return _clients.FirstOrDefault(c => c.Id == id)?.ToDto();
    }

    /// <summary>One matter by id, or <c>null</c>.</summary>
    public MatterDto? GetMatter(string id)
    {
        lock (_sync) return _matters.FirstOrDefault(m => m.Id == id)?.ToDto();
    }

    /* ---------------- Simulated activity ---------------- */

    /// <summary>Edits a few matters, closes some, and opens a new one, as if people were working.</summary>
    public int SimulateChanges(int count)
    {
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            var changed = 0;
            for (var i = 0; i < count; i++)
            {
                var matter = _matters[_random.Next(_matters.Count)];
                if (matter.Status == "open" && _random.NextDouble() < 0.3)
                {
                    matter.Status = "closed";
                    matter.ClosedDate = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }
                else
                {
                    matter.Title = matter.Title.Split(" (rev")[0] + $" (rev {now:HHmmss})";
                }
                matter.LastModifiedUtc = now;
                changed++;
            }

            var client = _clients[_random.Next(_clients.Count)];
            _matters.Add(NewMatter(client.Id, now, isNew: true));
            return changed + 1;
        }
    }

    /* ---------------- Seed data ---------------- */

    /// <summary>Generates the clients and matters, deliberately including some bad records (missing names, invalid dates, unknown clients) for the dead-letter demo.</summary>
    private void Seed()
    {
        var now = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var clientNumber = 10000;

        // Hand-picked clients, each showing a matching or data-quality case
        var named = new (string? Name, string Country, string Zip, string? Opened)[]
        {
            ("Kestrel Aerospace Inc.",          "US",             "98108",  "2018-04-02"),   // also Firm A client (UK affiliate)
            ("Vantage BioTech, Inc.",           "USA",            "02139",  "2019-09-15"),   // also Firm A client
            ("ACME Holdings Corp",              "United States",  "60601",  "2016-01-11"),   // also Firm A client
            ("Granite Bay Partners LLP",        "United Kingdom", "EC1Y 4UP","2021-06-01"),  // also Firm A, same postcode
            ("Atlas Capital Inc",               "US",             "10019",  "2017-03-03"),   // name also in Firm A
            ("Meridian Software Inc",           "US",             "94105",  "2020-02-20"),   // name also in Firm A
            ("Lakeshore Ventures LLC",          "US",             "60611",  "2015-07-07"),
            ("Lakeshore Ventures, L.L.C.",      "USA",            "60611",  "2022-01-10"),   // duplicate within Firm B
            ("Prairie Wind Energy Corp",        "US",             "73102",  "2016-05-05"),
            ("Prairie Wind Energy Corporation", "US",             "73102",  "2023-03-30"),   // duplicate within Firm B
            ("Hudson & Pike Media Inc",         "US",             "10013",  "2018-10-10"),
            ("Hudson and Pike Media",           "US",             "10013",  "2024-02-02"),   // duplicate: & vs and
            ("Redwood Biologics Inc",           "US",             "94080",  "2019-12-12"),   // similar to a generated name; a human should reject
            ("Sterling Row Capital LP",         "US",             "10022",  "2017-08-08"),
            ("Copper State Mining Co",          "US",             "85004",  "2016-11-11"),
            ("Empire Fintech Inc",              "US",             "10004",  "2021-04-04"),
            (null,                              "US",             "60601",  "2022-02-02"),   // no name -> rejected
            ("Bluewater Shipping LLC",          "US",             "77002",  "2021-02-30"),   // impossible date -> rejected
        };

        foreach (var n in named)
        {
            _clients.Add(new Client
            {
                Id = $"C-{clientNumber++}",
                Name = n.Name,
                Country = n.Country,
                PostalCode = n.Zip,
                OpenedDate = n.Opened,
                Status = "active",
                LastModifiedUtc = RandomPast(now, 700)
            });
        }

        // Generated clients
        string[] first = ["Apex", "Bluegrass", "Cardinal", "Desert", "Evergreen", "Frontier", "Golden Gate", "Heartland",
                          "Liberty", "Magnolia", "Northstar", "Pacific", "Redwood", "Silverline", "Tidewater", "Westbrook"];
        string[] second = ["Analytics", "Bancorp", "Biosciences", "Brands", "Capital Partners", "Energy", "Foods",
                           "Health Systems", "Industries", "Logistics", "Media", "Pharmaceuticals", "Realty", "Robotics", "Semiconductors"];
        string[] suffix = ["Inc.", "LLC", "Corp.", "Holdings, Inc.", "L.P."];
        string[] countries = ["US", "USA", "United States"];

        var i = 0;
        foreach (var f in first)
        foreach (var s in second)
        {
            _clients.Add(new Client
            {
                Id = $"C-{clientNumber++}",
                Name = $"{f} {s} {suffix[i % suffix.Length]}",
                Country = countries[i % countries.Length],
                PostalCode = ZipCodes[i % ZipCodes.Length],
                OpenedDate = RandomDate(new DateTime(2012, 1, 1), new DateTime(2026, 6, 30)),
                Status = i % 17 == 0 ? "inactive" : "active",
                LastModifiedUtc = RandomPast(now, 700)
            });
            i++;
        }

        // Matters: 5 to 30 per client, with a small share of bad records
        _nextMatterNumber = 500000;
        foreach (var client in _clients)
        {
            var count = _random.Next(5, 31);
            for (var m = 0; m < count; m++)
                _matters.Add(NewMatter(client.Id, now, isNew: false));
        }
    }

    /// <summary>A random matter for a client; <paramref name="isNew"/> gives it a recent open date.</summary>
    private Matter NewMatter(string clientId, DateTime now, bool isNew)
    {
        var id = _nextMatterNumber++;
        var opened = isNew ? now.Date : DateTime.Parse(RandomDate(new DateTime(2015, 1, 1), now.AddDays(-30)), CultureInfo.InvariantCulture);
        var closed = !isNew && _random.NextDouble() < 0.55 ? opened.AddDays(_random.Next(30, 1200)) : (DateTime?)null;
        if (closed > now) closed = null;

        var matter = new Matter
        {
            Id = $"M-{id}",
            ClientId = clientId,
            MatterNumber = $"{clientId[2..]}-{id % 1000:000}",
            Title = MatterTitles[_random.Next(MatterTitles.Length)],
            PracticeGroup = PracticeGroups[_random.Next(PracticeGroups.Length)],
            ResponsibleAttorney = Attorneys[_random.Next(Attorneys.Length)],
            OpenedDate = opened.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ClosedDate = closed?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Status = closed is null ? "open" : "closed",
            LastModifiedUtc = isNew ? now : RandomPast(now, 700)
        };

        if (isNew) return matter;

        // Deliberate data problems (about 1.5% of matters)
        var roll = _random.NextDouble();
        if (roll < 0.005) matter.ClientId = "C-99999";                     // client does not exist -> orphan
        else if (roll < 0.007) matter.ClientId = null;                     // no client at all
        else if (roll < 0.010) matter.OpenedDate = "2023-02-30";           // impossible date
        else if (roll < 0.012) matter.OpenedDate = "31/12/2022";           // wrong format
        else if (roll < 0.015 && matter.ClosedDate is not null)            // closes before it opens
        {
            matter.ClosedDate = opened.AddDays(-14).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return matter;
    }

    /// <summary>A random time up to <paramref name="maxDays"/> days before <paramref name="now"/>.</summary>
    private DateTime RandomPast(DateTime now, int maxDays) =>
        now.AddMinutes(-_random.Next(1, maxDays * 24 * 60));

    /// <summary>A random date between two dates, as <c>yyyy-MM-dd</c> text (the API sends dates as strings).</summary>
    private string RandomDate(DateTime from, DateTime to) =>
        from.AddDays(_random.Next(0, (int)(to - from).TotalDays)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed class Client
    {
        public required string Id { get; init; }
        public string? Name { get; set; }
        public string? Country { get; set; }
        public string? PostalCode { get; set; }
        public string? OpenedDate { get; set; }
        public string Status { get; set; } = "active";
        public DateTime LastModifiedUtc { get; set; }

        /// <summary>The client as the API returns it (dates as strings, as the real system does).</summary>
        public ClientDto ToDto() => new(Id, Name, Country, PostalCode, OpenedDate, Status, LastModifiedUtc);
    }

    private sealed class Matter
    {
        public required string Id { get; init; }
        public string? ClientId { get; set; }
        public string MatterNumber { get; set; } = "";
        public string Title { get; set; } = "";
        public string PracticeGroup { get; set; } = "";
        public string ResponsibleAttorney { get; set; } = "";
        public string? OpenedDate { get; set; }
        public string? ClosedDate { get; set; }
        public string Status { get; set; } = "open";
        public DateTime LastModifiedUtc { get; set; }

        /// <summary>The matter as the API returns it (dates as strings, as the real system does).</summary>
        public MatterDto ToDto() => new(Id, ClientId, MatterNumber, Title, PracticeGroup, ResponsibleAttorney,
            OpenedDate, ClosedDate, Status, LastModifiedUtc);
    }
}
