using TechAvail.Core.Parsing;

namespace TechAvail.Core.Tests;

public class DiagnosticsTests
{
    static readonly DateOnly Today = new(2026, 10, 6);
    static readonly DateOnly Tomorrow = new(2026, 10, 7);
    static readonly DateOnly Yesterday = new(2026, 10, 5);

    static Block B(
        string kind,
        DateOnly? day = null,
        string start = "08:00",
        string end = "17:00",
        string tech = "a",
        string reference = "",
        string status = "A",
        string region = "",
        string skills = "",
        string? addressIssue = null
    )
    {
        if (kind == "shift")
        {
            region = region.Length > 0 ? region : "North";
            skills = skills.Length > 0 ? skills : "INS";
        }
        var d = day ?? Today;
        return new Block
        {
            Kind = kind,
            WorkDate = d,
            TechId = tech,
            TechName = tech.ToUpperInvariant(),
            StartsAt = d.ToDateTime(TimeOnly.Parse(start)),
            EndsAt = d.ToDateTime(TimeOnly.Parse(end)),
            RefId = reference,
            Status = status,
            Department = "FIELD",
            Region = region,
            Skills = skills,
            AddressIssue = addressIssue,
        };
    }

    static Dictionary<string, Check> Checks(params Block[] blocks) =>
        Diagnostics.Diagnose(blocks, Today).ToDictionary(c => c.Id);

    static List<string> Refs(Check check) => [.. check.Rows.Select(r => (string)r["ref_id"]!)];

    static Block Ticket(string reference, string start, string end, string region, string tech = "a") =>
        B("ticket", start: start, end: end, tech: tech, reference: reference, region: region);

    static List<(string, string, string)> Double(params Block[] blocks) =>
    [
        .. Checks([B("shift"), .. blocks])["double_booked"]
            .Rows.Select(r => ((string)r["ref_id"]!, (string)r["other_ref_id"]!, (string)r["reason"]!)),
    ];

    [Fact]
    public void Every_check_has_a_known_group_and_a_clean_feed_has_no_rows()
    {
        var found = Diagnostics.Diagnose([B("shift"), B("job", start: "09:00", end: "10:00", reference: "1")], Today);
        Assert.All(found, c => Assert.Contains(c.Group, Diagnostics.Groups));
        Assert.DoesNotContain(found, c => c.Rows.Count > 0);
    }

    [Fact]
    public void Tech_without_region_or_skills()
    {
        var found = Checks(B("shift"), B("shift", tech: "b") with { Region = "", Skills = "" });
        Assert.Equal(["b"], found["tech_no_region"].Rows.Select(r => r["tech_id"]));
        Assert.Equal(1, found["tech_no_region"].Rows[0]["days"]);
        Assert.Equal(["b"], found["tech_no_skills"].Rows.Select(r => r["tech_id"]));
    }

    [Fact]
    public void Tech_region_varies_is_info()
    {
        var check = Checks(B("shift"), B("shift", Tomorrow, region: "South"))["tech_region_varies"];
        Assert.Equal("info", check.Severity);
        Assert.Equal("North (1), South (1)", check.Rows[0]["regions"]);
    }

    [Fact]
    public void Work_without_a_shift_that_day() =>
        Assert.Equal(["1"], Refs(Checks(B("shift"), B("job", Tomorrow, "09:00", "10:00", reference: "1"))["work_without_shift"]));

    [Fact]
    public void Dead_and_past_work_is_not_a_scheduling_conflict() =>
        Assert.Empty(
            Checks(
                B("job", Tomorrow, "09:00", "10:00", reference: "1", status: "X"),
                B("ticket", Tomorrow, "09:00", "10:00", reference: "2", status: "C"),
                B("job", Yesterday, "09:00", "10:00", reference: "3", status: "C")
            )["work_without_shift"].Rows
        );

    [Fact]
    public void Work_running_past_the_shift()
    {
        var row = Assert.Single(Checks(B("shift", end: "16:00"), B("job", start: "15:00", end: "17:00", reference: "1"))["work_outside_shift"].Rows);
        Assert.Equal(("1", 60), ((string)row["ref_id"]!, (int)row["minutes_outside"]!));
        Assert.Equal("North", row["region"]); // the tech's shift region
    }

    [Fact]
    public void Work_during_time_off_is_not_also_reported_as_without_shift()
    {
        var found = Checks(B("time_off", start: "00:00", end: "23:59"), B("job", start: "09:00", end: "10:00", reference: "1"));
        Assert.Equal(["1"], Refs(found["work_during_time_off"]));
        Assert.Empty(found["work_without_shift"].Rows);
    }

    [Fact]
    public void Double_booking_needs_two_different_jobs()
    {
        var row = Assert.Single(
            Checks(
                B("shift"),
                B("job", start: "09:00", end: "11:00", reference: "1"),
                B("ticket", start: "10:00", end: "12:00", reference: "2"),
                B("job", start: "13:00", end: "14:00", reference: "3"),
                B("job", start: "13:00", end: "14:00", reference: "3"), // same job listed twice
                B("job", start: "09:00", end: "11:00", reference: "1", tech: "b"), // second tech, same job
                B("shift", tech: "b")
            )["double_booked"].Rows
        );
        Assert.Equal(("1", "2", 60), ((string)row["ref_id"]!, (string)row["other_ref_id"]!, (int)row["overlap_minutes"]!));
    }

    [Fact]
    public void Past_work_still_open() =>
        Assert.Equal(
            ["1", "4"],
            Refs(
                Checks(
                    B("job", Yesterday, reference: "1", status: "A"),
                    B("job", Yesterday, reference: "2", status: "C"),
                    B("ticket", Yesterday, reference: "3", status: "R"),
                    B("job_unassigned", Yesterday, tech: "", reference: "4", region: "North"),
                    B("job", Today, reference: "5")
                )["past_open_work"]
            )
        );

    [Fact]
    public void Unassigned_work_without_region() =>
        Assert.Equal(
            ["1"],
            Refs(
                Checks(
                    B("job_unassigned", tech: "", reference: "1"),
                    B("ticket_unassigned", tech: "", reference: "2", region: "South"),
                    B("job_unassigned", tech: "", reference: "3", status: "X")
                )["unassigned_no_region"]
            )
        );

    [Fact]
    public void Address_issue_is_unavailable_until_the_feed_sends_it() =>
        Assert.False(Checks(B("shift"), B("job", reference: "1"))["work_address_issue"].Available);

    [Fact]
    public void Address_issues_on_live_work()
    {
        var check = Checks(
            B("shift", addressIssue: ""),
            B("job", start: "09:00", end: "10:00", reference: "1", addressIssue: "no_gps"),
            B("job", start: "10:00", end: "11:00", reference: "2", addressIssue: ""),
            B("job_unassigned", tech: "", reference: "3", region: "N", addressIssue: "no_address"),
            B("job", Yesterday, reference: "4", status: "C", addressIssue: "no_gps"),
            B("job", reference: "5", status: "X", addressIssue: "no_city")
        )["work_address_issue"];
        Assert.True(check.Available);
        Assert.Equal([("3", "no_address"), ("1", "no_gps")], check.Rows.Select(r => ((string)r["ref_id"]!, (string)r["issue"]!)));
    }

    [Fact]
    public void Tc_shift_covers_tc_work_and_tech_rows_name_their_calendar()
    {
        var found = Checks(B("shift_tc", tech: "t") with { Region = "" }, B("ticket", start: "09:00", end: "10:00", tech: "t", reference: "1"));
        Assert.Empty(found["work_without_shift"].Rows);
        var row = Assert.Single(found["tech_no_region"].Rows);
        Assert.Equal(("t", "tc"), ((string)row["tech_id"]!, (string)row["calendar"]!));
    }

    [Fact]
    public void Two_tickets_in_one_slot_and_region_are_allowed() =>
        Assert.Empty(Double(Ticket("1", "10:00", "12:00", "North"), Ticket("2", "10:00", "12:00", "North")));

    [Fact]
    public void Two_tickets_in_one_slot_but_different_regions_conflict() =>
        Assert.Equal(
            [("1", "2", "ticket_regions_differ")],
            Double(Ticket("1", "10:00", "12:00", "North"), Ticket("2", "10:00", "12:00", "South"))
        );

    [Fact]
    public void A_third_ticket_in_the_slot_conflicts() =>
        Assert.Equal(
            ["more_than_2_tickets"],
            Double(
                    Ticket("1", "10:00", "12:00", "North"),
                    Ticket("2", "10:00", "12:00", "North"),
                    Ticket("3", "11:00", "12:00", "North")
                )
                .Select(r => r.Item3)
                .Distinct()
        );

    [Fact]
    public void Ticket_without_its_own_region_is_not_assumed_to_match() =>
        // A snapshot from before the feed query sent the ticket's own region.
        Assert.Equal(
            [("1", "2", "ticket_region_unknown")],
            Double(Ticket("1", "10:00", "12:00", ""), Ticket("2", "10:00", "12:00", ""))
        );

    [Fact]
    public void A_job_overlapping_anything_is_still_a_conflict() =>
        Assert.Equal(
            [("1", "9", "job_overlap")],
            Double(B("job", start: "10:00", end: "12:00", reference: "9"), Ticket("1", "10:00", "12:00", "North"))
        );

    [Fact]
    public void Conflict_rows_show_each_tickets_own_region() =>
        Assert.Equal(
            [("North", "South")],
            Checks(B("shift"), Ticket("1", "10:00", "12:00", "North"), Ticket("2", "10:00", "12:00", "South"))["double_booked"]
                .Rows.Select(r => ((string)r["region"]!, (string)r["other_region"]!))
        );

    [Fact]
    public void A_job_keeps_its_own_region_and_borrows_the_shifts_only_without_one()
    {
        var own = B("job", start: "15:00", end: "18:00", reference: "1", region: "South");
        var bare = B("job", start: "16:00", end: "18:00", reference: "2", tech: "b");
        var rows = Checks(B("shift", end: "16:00"), B("shift", tech: "b", end: "16:00"), own, bare)["work_outside_shift"].Rows;
        Assert.Equal(
            new Dictionary<string, string> { ["1"] = "South", ["2"] = "North" },
            rows.ToDictionary(r => (string)r["ref_id"]!, r => (string)r["region"]!)
        );
    }

    [Fact]
    public void A_ride_along_without_a_schedule_is_info_not_a_conflict()
    {
        var found = Checks(
            B("shift", tech: "lead"),
            B("job", start: "09:00", end: "10:00", tech: "lead", reference: "1"),
            B("job", start: "09:00", end: "10:00", tech: "trainee", reference: "1"),
            B("job", start: "11:00", end: "12:00", tech: "solo", reference: "2")
        );
        Assert.Equal(["2"], Refs(found["work_without_shift"]));
        var row = Assert.Single(found["ride_along"].Rows);
        Assert.Equal(("trainee", "1", "LEAD"), ((string)row["tech_id"]!, (string)row["ref_id"]!, (string)row["with"]!));
        Assert.Equal("info", found["ride_along"].Severity);
    }
}
