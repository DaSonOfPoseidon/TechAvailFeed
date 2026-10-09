using System.Globalization;
using TechAvail.Core.Parsing;

namespace TechAvail.Core.Tests;

public class JeopardyTests
{
    static readonly DateOnly Day = new(2026, 10, 6);

    static DateTime T(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture);

    static Block Work(
        string reference,
        string status = "I",
        string end = "2026-10-06 12:00",
        string kind = "job",
        string tech = "t1",
        string department = "FIELD"
    )
    {
        var endsAt = T(end);
        return new Block
        {
            Kind = kind,
            WorkDate = DateOnly.FromDateTime(endsAt),
            TechId = tech,
            TechName = tech.ToUpperInvariant(),
            StartsAt = endsAt.AddHours(-2),
            EndsAt = endsAt,
            RefId = reference,
            Status = status,
            Department = department,
            Region = "North",
            Skills = "",
        };
    }

    static List<string> Refs(IEnumerable<Block> blocks, string asOf) => [.. Jeopardy.Find(blocks, Day, T(asOf)).Select(r => r.Job.RefId)];

    [Fact]
    public void A_job_is_in_jeopardy_from_30_minutes_before_its_end()
    {
        Block[] jobs = [Work("1")];
        Assert.Empty(Refs(jobs, "2026-10-06 11:29"));
        var row = Assert.Single(Jeopardy.Find(jobs, Day, T("2026-10-06 11:30")));
        Assert.Equal(T("2026-10-06 11:30"), row.JijAt);
        Assert.Equal(0, row.MinutesPast);
        Assert.Equal(45, Jeopardy.Find(jobs, Day, T("2026-10-06 12:15:30")).Single().MinutesPast);
    }

    [Fact]
    public void Only_unfinished_work_on_the_day_counts()
    {
        Block[] jobs =
        [
            Work("active", "A"),
            Work("enroute", "E"),
            Work("incomplete", "N"),
            Work("open", "O", kind: "ticket"),
            Work("complete", "C"),
            Work("closed", "C", kind: "ticket"),
            Work("cleared", "R", kind: "ticket"),
            Work("canceled", "X"),
            Work("unassigned", tech: ""),
            Work("other_dept", department: "CONSTRUCTION"),
            Work("tomorrow", end: "2026-10-07 12:00"),
        ];
        Assert.Equal(["active", "enroute", "incomplete", "open"], Refs(jobs, "2026-10-06 13:00").Order());
    }

    [Fact]
    public void Rows_are_sorted_by_jij_time_then_tech_then_ref()
    {
        Block[] jobs = [Work("3", tech: "b"), Work("2", tech: "a", end: "2026-10-06 12:30"), Work("1", tech: "b"), Work("4", tech: "a")];
        Assert.Equal(["4", "1", "3", "2"], Refs(jobs, "2026-10-06 13:00"));
    }
}
