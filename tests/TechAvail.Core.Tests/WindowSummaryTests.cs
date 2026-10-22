using TechAvail.Core.Parsing;

namespace TechAvail.Core.Tests;

public class WindowSummaryTests
{
    static readonly DateOnly Thursday = new(2026, 10, 22);
    static readonly DateOnly Sunday = new(2026, 10, 25);
    static readonly DateTime Early = new(2026, 10, 22, 6, 0, 0);

    static DateTime At(DateOnly day, string hhmm) => day.ToDateTime(TimeOnly.Parse(hhmm));

    static Block B(string kind, string start, string end, string tech = "t1", string region = "Carrollton", string skills = "INS", DateOnly? day = null) =>
        new()
        {
            Kind = kind,
            WorkDate = day ?? Thursday,
            TechId = tech,
            TechName = tech.ToUpperInvariant(),
            StartsAt = At(day ?? Thursday, start),
            EndsAt = At(day ?? Thursday, end),
            RefId = $"{kind}-{tech}-{start}",
            Status = "A",
            Region = region,
            Skills = skills,
        };

    static List<WindowRow> Summary(List<Block> blocks, DateTime? now = null, DateOnly? day = null)
    {
        var date = day ?? Thursday;
        var days = Availability.TechDays(blocks, now ?? Early, date, date);
        var demand = blocks.Where(b => b.Kind.EndsWith("_unassigned", StringComparison.Ordinal)).ToList();
        return WindowSummary.Build(days, demand, [date], now ?? Early);
    }

    static WindowRow Row(List<WindowRow> rows, string area, string hhmm) => rows.Single(r => r.Area == area && r.Window == At(r.Date, hhmm));

    [Fact]
    public void A_tech_free_all_morning_is_available_in_both_morning_windows()
    {
        var rows = Summary([B("shift", "08:00", "12:00")]);
        Assert.Equal([At(Thursday, "08:00"), At(Thursday, "10:00")], rows.Select(r => r.Window));
        Assert.All(rows, r => Assert.Equal((1, 0, 1), (r.Available, r.Unassigned, r.Net)));
    }

    [Fact]
    public void Unassigned_work_takes_the_first_window_it_overlaps()
    {
        var rows = Summary([B("shift", "08:00", "12:00"), B("job_unassigned", "09:00", "11:00", tech: "")]);
        Assert.Equal((1, 1, 0), (Row(rows, "Carrollton", "08:00").Available, Row(rows, "Carrollton", "08:00").Unassigned, Row(rows, "Carrollton", "08:00").Net));
        Assert.Equal(1, Row(rows, "Carrollton", "10:00").Net);
    }

    [Fact]
    public void A_scheduled_tech_with_no_free_time_still_gets_a_row()
    {
        var rows = Summary([B("shift", "08:00", "10:00"), B("job", "08:00", "10:00")]);
        Assert.Equal((0, 0), (Row(rows, "Carrollton", "08:00").Available, Row(rows, "Carrollton", "08:00").Net));
    }

    [Fact]
    public void Unassigned_work_with_no_tech_gets_a_row()
    {
        var rows = Summary([B("job_unassigned", "13:00", "15:00", tech: "", region: "Lebanon-Rolla")]);
        Assert.Equal((0, 1, -1), (Row(rows, "Lebanon-Rolla", "13:00").Available, Row(rows, "Lebanon-Rolla", "13:00").Unassigned, Row(rows, "Lebanon-Rolla", "13:00").Net));
    }

    [Fact]
    public void A_window_that_has_started_gets_no_row()
    {
        var rows = Summary([B("shift", "08:00", "12:00")], now: At(Thursday, "09:00"));
        Assert.Equal([At(Thursday, "10:00")], rows.Select(r => r.Window));
    }

    [Fact]
    public void An_mdu_tech_with_a_free_hour_marks_the_window()
    {
        var rows = Summary([B("shift", "08:00", "12:00", "a", skills: "MDU"), B("shift", "13:00", "17:00", "b")]);
        Assert.True(Row(rows, "Carrollton", "08:00").Mdu);
        Assert.True(Row(rows, "Carrollton", "10:00").Mdu);
        Assert.False(Row(rows, "Carrollton", "13:00").Mdu);
    }

    [Fact]
    public void An_mdu_hour_before_the_first_window_doesnt_mark_it()
    {
        var rows = Summary([B("shift", "07:00", "08:00", "a", skills: "MDU"), B("shift", "08:00", "10:00", "b")]);
        Assert.False(Row(rows, "Carrollton", "08:00").Mdu);
    }

    [Fact]
    public void A_repeated_unassigned_row_is_one_piece_of_work()
    {
        var job = B("job_unassigned", "08:00", "10:00", tech: "");
        var rows = Summary([B("shift", "08:00", "10:00", "a"), B("shift", "08:00", "10:00", "b"), job, job]);
        Assert.Equal(1, Row(rows, "Carrollton", "08:00").Net);
    }

    [Fact]
    public void An_mdu_slot_starting_off_the_hour_still_fits_an_hour_in_the_window()
    {
        var rows = Summary([B("shift", "07:45", "09:00", "a", skills: "MDU"), B("shift", "08:00", "10:00", "b")]);
        Assert.True(Row(rows, "Carrollton", "08:00").Mdu);
    }

    [Fact]
    public void Unassigned_work_in_a_started_window_gets_no_row()
    {
        var rows = Summary([B("job_unassigned", "08:00", "10:00", tech: "")], now: At(Thursday, "09:00"));
        Assert.Empty(rows);
    }

    [Fact]
    public void Territories_roll_up_into_the_vps_areas()
    {
        var rows = Summary([B("shift", "08:00", "10:00", "a", "Columbia Core"), B("shift", "08:00", "10:00", "b", "Jefferson City Area")]);
        Assert.Equal(2, Row(rows, "Central MO", "08:00").Available);
    }

    [Fact]
    public void A_sunday_has_no_install_windows() =>
        Assert.Empty(Summary([B("shift", "08:00", "17:00", day: Sunday)], day: Sunday));

    [Fact]
    public void A_tc_sunday_uses_the_weekday_windows()
    {
        var days = Availability.TechDays([B("shift_tc", "08:00", "17:00", day: Sunday)], Early, Sunday, Sunday, "tc");
        var rows = WindowSummary.Build(days, [], [Sunday], Early, "tc");
        Assert.Equal(["08:00", "10:00", "13:00", "15:00"], rows.Select(r => r.Window.ToString("HH:mm")));
    }

    static TechDay Shift(DateOnly day) => Availability.TechDays([B("shift", "08:00", "17:00", day: day)], Early, day, day).Single();

    [Fact]
    public void Export_dates_are_five_business_days_without_an_empty_sunday() =>
        Assert.Equal(
            [Thursday, Thursday.AddDays(1), Thursday.AddDays(2), Thursday.AddDays(4), Thursday.AddDays(5)],
            Availability.ExportDates(Thursday, [Shift(Thursday)])
        );

    [Fact]
    public void Export_dates_skip_a_sunday_everyone_has_off()
    {
        var off = Availability.TechDays([B("shift", "08:00", "17:00", day: Sunday), B("time_off", "00:00", "23:59", day: Sunday)], Early, Sunday, Sunday).Single();
        Assert.DoesNotContain(Sunday, Availability.ExportDates(Thursday, [off]));
    }

    [Fact]
    public void Export_dates_keep_a_sunday_someone_works() =>
        Assert.Equal(
            [Thursday, Thursday.AddDays(1), Thursday.AddDays(2), Sunday, Thursday.AddDays(4), Thursday.AddDays(5)],
            Availability.ExportDates(Thursday, [Shift(Thursday), Shift(Sunday)])
        );
}
