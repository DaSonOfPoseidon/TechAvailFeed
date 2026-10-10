using System.Net;
using System.Text.Json.Nodes;
using TechAvail.Core.Parsing;
using TechAvail.Data;
using TechAvail.Data.Tests;

namespace TechAvail.Api.Tests;

public class ApiTests
{
    static readonly DateOnly Day = new(2026, 10, 6);
    static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero); // 07:00 in Chicago
    static readonly DateTimeOffset Generated = new(2026, 10, 6, 11, 5, 0, TimeSpan.Zero); // 06:05: the morning plan

    static Block B(string kind, string start, string end, string tech = "a", string region = "", string skills = "", string reference = "", string status = "A") =>
        new()
        {
            Kind = kind,
            WorkDate = Day,
            TechId = tech,
            TechName = tech.ToUpperInvariant(),
            StartsAt = Day.ToDateTime(TimeOnly.Parse(start)),
            EndsAt = Day.ToDateTime(TimeOnly.Parse(end)),
            RefId = reference,
            Status = status,
            Department = "FIELD",
            Region = region,
            Skills = skills,
            TaskType = kind == "job" ? "3" : "",
        };

    static readonly Block[] Blocks =
    [
        B("shift", "08:00", "17:00", "a", "North", "INS, RECO"),
        B("shift", "08:00", "17:00", "b", "South", "INS"),
        B("job", "08:00", "10:00", "a", reference: "j1"),
        B("job_unassigned", "09:00", "11:00", "", "North", reference: "u1"),
    ];

    sealed class Api : IDisposable
    {
        readonly TestDatabase db = new();
        readonly ApiFactory factory;
        public HttpClient Client { get; }

        public Api(Block[] blocks, string apiKey = "", bool exact = false)
        {
            if (blocks.Length > 0)
            {
                var feed = new ParsedFeed { Sha256 = "x", Format = "blocks", GeneratedAt = Generated };
                feed.Blocks.AddRange(blocks);
                new FeedStore(db.ConnectionString).Save("<1>", "email", null, null, null, Generated, feed);
            }
            factory = new ApiFactory(db, Now, apiKey, exact);
            Client = factory.CreateClient();
        }

        public async Task<JsonNode> Get(string path) => JsonNode.Parse(await Client.GetStringAsync(path))!;

        public async Task<HttpStatusCode> Status(string path) => (await Client.GetAsync(path)).StatusCode;

        public void Dispose()
        {
            factory.Dispose();
            db.Dispose();
        }
    }

    static List<string?> Strings(JsonNode? array, string key) => [.. array!.AsArray().Select(n => n![key]!.GetValue<string>())];

    [DbFact]
    public async Task Health_reports_snapshot_age_without_auth()
    {
        using var api = new Api(Blocks, apiKey: "secret");
        var body = await api.Get("/health");
        Assert.True(body["ok"]!.GetValue<bool>());
        Assert.Equal(55, body["snapshot"]!["age_min"]!.GetValue<double>());
        Assert.True(body["snapshot"]!["stale"]!.GetValue<bool>());
    }

    [DbFact]
    public async Task Api_key_is_required_when_set()
    {
        using var api = new Api(Blocks, apiKey: "secret");
        Assert.Equal(HttpStatusCode.Unauthorized, await api.Status("/api/v1/filters"));
        api.Client.DefaultRequestHeaders.Add("X-API-Key", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, await api.Status("/api/v1/filters"));
        api.Client.DefaultRequestHeaders.Remove("X-API-Key");
        api.Client.DefaultRequestHeaders.Add("X-API-Key", "secret");
        Assert.Equal(HttpStatusCode.OK, await api.Status("/api/v1/filters"));
    }

    [DbFact]
    public async Task Not_found_before_the_first_snapshot()
    {
        using var api = new Api([]);
        Assert.Equal(HttpStatusCode.NotFound, await api.Status("/api/v1/calendar"));
        Assert.Null((await api.Get("/health"))["snapshot"]);
    }

    [DbFact]
    public async Task Filters()
    {
        using var api = new Api(Blocks);
        var body = await api.Get("/api/v1/filters");
        Assert.Equal(["North", "South"], body["regions"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["INS", "RECO"], body["skills"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["a", "b"], Strings(body["techs"], "tech_id"));
        Assert.Equal(TechAvail.Core.StatusUpdate.RegionNames, body["vp_regions"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [DbFact]
    public async Task Calendar_starts_today_in_local_time()
    {
        using var api = new Api(Blocks);
        var body = await api.Get("/api/v1/calendar?days=3");
        Assert.Equal(["2026-10-06", "2026-10-07", "2026-10-08"], Strings(body["days"], "date"));
        var today = body["days"]![0]!;
        Assert.Equal(2, today["totals"]!["techs_on"]!.GetValue<int>());
        Assert.Equal(1, today["totals"]!["unassigned_jobs"]!.GetValue<int>());
        Assert.Equal(["North", "South"], Strings(today["by_region"], "region"));
    }

    [DbFact]
    public async Task Calendar_rejects_too_long_a_range()
    {
        using var api = new Api(Blocks);
        Assert.Equal((HttpStatusCode)422, await api.Status("/api/v1/calendar?days=400"));
        Assert.Equal((HttpStatusCode)422, await api.Status("/api/v1/calendar?start=nope"));
    }

    [DbFact]
    public async Task Calendar_day_drill_down()
    {
        using var api = new Api(Blocks);
        var body = await api.Get("/api/v1/calendar/2026-10-06?region=North");
        var tech = Assert.Single(body["techs"]!.AsArray())!;
        Assert.Equal("a", tech["tech_id"]!.GetValue<string>());
        Assert.Equal(["j1"], Strings(tech["work"], "ref_id"));
        // Now is 07:00, so the lead time doesn't clip the 10:00 start.
        Assert.Equal("2026-10-06T10:00:00", tech["free"]![0]!["open_from"]!.GetValue<string>());
        Assert.Equal(["u1"], Strings(body["unassigned"], "ref_id"));
        Assert.Equal(1, body["totals"]!["techs_on"]!.GetValue<int>());
    }

    [DbFact]
    public async Task Skill_filter_narrows_unassigned_work_with_a_known_skill()
    {
        using var api = new Api(
            [
                B("shift", "08:00", "17:00", "a", "North", "VIP, GP"),
                B("job_unassigned", "09:00", "11:00", "", "North", "VIP", "u1"),
                B("job_unassigned", "09:00", "11:00", "", "North", "MDU, VIP", "u2"),
                B("job_unassigned", "09:00", "11:00", "", "North", "MDU", "u3"),
                B("job_unassigned", "09:00", "11:00", "", "North", reference: "u4"),
            ]
        );
        var vip = await api.Get("/api/v1/calendar/2026-10-06?skill=vip");
        Assert.Equal(["u1", "u2", "u4"], Strings(vip["unassigned"], "ref_id"));
        Assert.Equal(["VIP", "MDU, VIP", "MDU", ""], Strings((await api.Get("/api/v1/calendar/2026-10-06"))["unassigned"], "skills"));
        var days = await api.Get("/api/v1/calendar?days=1&skill=MDU");
        Assert.Equal(3, days["days"]![0]!["totals"]!["unassigned_jobs"]!.GetValue<int>());
    }

    [DbFact]
    public async Task Capacity_kpis()
    {
        using var api = new Api(Blocks);
        var body = await api.Get("/api/v1/kpis/capacity?days=2");
        Assert.Equal(2, body["series"]!.AsArray().Count);
        Assert.Equal(Math.Round(2.0 / 16, 3), body["series"]![0]!["utilization"]!.GetValue<double>());
        Assert.Equal(["North", "South"], Strings(body["by_region"], "region"));
    }

    [DbFact]
    public async Task Outcome_kpis()
    {
        using var api = new Api(Blocks);
        var body = await api.Get("/api/v1/kpis/outcomes?start=2026-10-06&end=2026-10-06");
        var day = Assert.Single(body["days"]!.AsArray())!;
        Assert.True(day["provisional"]!.GetValue<bool>());
        Assert.Equal(1, day["job"]!["planned"]!.GetValue<int>());
        Assert.Equal("North", body["by_region"]![0]!["region"]!.GetValue<string>());
        Assert.Equal((HttpStatusCode)422, await api.Status("/api/v1/kpis/outcomes?start=2026-10-06&end=2026-10-01"));
    }

    static Block Located(Block b, double? lat, double? lon, string issue = "") => b with { Latitude = lat, Longitude = lon, AddressIssue = issue };

    static readonly Block[] Mapped =
    [
        B("shift", "08:00", "17:00", "a", "North", "INS"),
        Located(B("job", "08:00", "10:00", "a", reference: "j1"), 40.123456, -100.654321),
        Located(B("job", "08:00", "10:00", "b", reference: "j1"), 40.123456, -100.654321),
        Located(B("job_unassigned", "09:00", "11:00", "", "South", reference: "u1"), null, null, "no_gps"),
        Located(B("job", "12:00", "13:00", "a", reference: "j2", status: "X"), 38.9, -92.3),
    ];

    [DbFact]
    public async Task Map_rounds_coordinates_and_merges_two_tech_jobs()
    {
        using var api = new Api(Mapped);
        var body = await api.Get("/api/v1/map");
        var point = Assert.Single(body["points"]!.AsArray())!;
        Assert.Equal(("j1", 40.123, -100.654), (point["ref_id"]!.GetValue<string>(), point["lat"]!.GetValue<double>(), point["lon"]!.GetValue<double>()));
        Assert.Equal("North", point["region"]!.GetValue<string>());
        Assert.Equal(["a", "b"], Strings(point["techs"], "tech_id"));
        Assert.False(body["exact"]!.GetValue<bool>());
        var unmapped = Assert.Single(body["unmapped"]!.AsArray())!;
        Assert.Equal(("u1", "no_gps"), (unmapped["ref_id"]!.GetValue<string>(), unmapped["address_issue"]!.GetValue<string>()));
        Assert.False(unmapped.AsObject().ContainsKey("lat"));
    }

    [DbFact]
    public async Task Map_serves_exact_coordinates_only_when_configured()
    {
        using var api = new Api(Mapped, exact: true);
        Assert.Equal(40.123456, (await api.Get("/api/v1/map"))["points"]![0]!["lat"]!.GetValue<double>());
    }

    [DbFact]
    public async Task Map_filters()
    {
        using var api = new Api(Mapped);
        Assert.Empty((await api.Get("/api/v1/map?region=South"))["points"]!.AsArray());
        Assert.Empty((await api.Get("/api/v1/map?kind=ticket"))["unmapped"]!.AsArray());
        Assert.Equal((HttpStatusCode)422, await api.Status("/api/v1/map?kind=bogus"));
    }

    [DbFact]
    public async Task Map_shows_completed_work_unless_hidden()
    {
        using var api = new Api(
            [
                Located(B("job", "08:00", "09:00", reference: "j1", status: "C"), 40, -100),
                Located(B("ticket", "09:00", "10:00", reference: "t1", status: "C"), 40, -100),
                Located(B("ticket", "10:00", "11:00", reference: "t2", status: "R"), 40, -100),
                Located(B("ticket", "11:00", "12:00", reference: "t3", status: "O"), 40, -100),
                Located(B("ticket", "12:00", "13:00", reference: "t4", status: "D"), 40, -100),
                Located(B("job", "13:00", "14:00", reference: "j2", status: "X"), 40, -100),
            ]
        );
        Assert.Equal(["j1", "t1", "t2", "t3"], Strings((await api.Get("/api/v1/map"))["points"], "ref_id"));
        var open = await api.Get("/api/v1/map?completed=false");
        Assert.Equal(["t3"], Strings(open["points"], "ref_id"));
        Assert.False(open["filters"]!["completed"]!.GetValue<bool>());
    }

    [DbFact]
    public async Task Diagnostics()
    {
        using var api = new Api(Blocks);
        var body = await api.Get("/api/v1/diagnostics");
        var summary = body["summary"]!.AsArray().ToDictionary(c => c!["id"]!.GetValue<string>());
        Assert.Equal(0, summary["work_without_shift"]!["count"]!.GetValue<int>());
        Assert.False(summary["work_address_issue"]!["available"]!.GetValue<bool>());
        Assert.Equal(body["summary"]!.AsArray().Count, body["checks"]!.AsArray().Count);
        var tech = await api.Get("/api/v1/diagnostics?group=tech_setup");
        Assert.Equal(["tech_setup"], Strings(tech["checks"], "group").Distinct());
        Assert.Equal(["double_booked"], Strings((await api.Get("/api/v1/diagnostics?check=double_booked"))["checks"], "id"));
        Assert.Equal((HttpStatusCode)422, await api.Status("/api/v1/diagnostics?group=nope"));
        Assert.Equal((HttpStatusCode)422, await api.Status("/api/v1/diagnostics?check=nope"));
    }

    [DbFact]
    public async Task Tc_calendar_is_separate_from_install()
    {
        Block[] blocks =
        [
            .. Blocks,
            B("shift_tc", "08:00", "17:00", "t", "North", "TC"),
            B("shift_tc", "08:00", "17:00", "b", "South", "INS"), // b is a dual tech
            B("ticket", "09:00", "10:00", "t", reference: "tk1"),
        ];
        using var api = new Api(blocks);
        var install = (await api.Get("/api/v1/calendar?days=1"))["days"]![0]!["totals"]!;
        var tc = (await api.Get("/api/v1/calendar?days=1&calendar=tc"))["days"]![0]!["totals"]!;
        Assert.Equal(2, install["techs_on"]!.GetValue<int>()); // a and b only
        Assert.Equal((2, 1), (tc["techs_on"]!.GetValue<int>(), tc["tickets"]!.GetValue<int>()));
        Assert.Equal(["b", "t"], Strings((await api.Get("/api/v1/calendar/2026-10-06?calendar=tc"))["techs"], "tech_id"));
        Assert.Equal((HttpStatusCode)422, await api.Status("/api/v1/calendar?calendar=bogus"));
        var techs = (await api.Get("/api/v1/filters"))["techs"]!.AsArray()
            .ToDictionary(t => t!["tech_id"]!.GetValue<string>(), t => t!["calendar"]!.GetValue<string>());
        Assert.Equal(new Dictionary<string, string> { ["a"] = "install", ["b"] = "both", ["t"] = "tc" }, techs);
    }
}
