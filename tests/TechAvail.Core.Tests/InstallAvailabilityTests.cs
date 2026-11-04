using TechAvail.Core.Parsing;

namespace TechAvail.Core.Tests;

public class InstallAvailabilityTests
{
    static readonly DateOnly Friday = new(2026, 10, 16);
    static readonly DateOnly Saturday = new(2026, 10, 17);
    static readonly DateOnly Sunday = new(2026, 10, 18);
    static readonly DateOnly Monday = new(2026, 10, 19);

    static TechDay Day(DateOnly date, string region, int free, int bookable, string tech = "t1", double available = 8, bool onTimeOff = false) =>
        new(date, tech, tech, region, "INS", [], [], [], [], 0, 0, 0, available, 0, 0, 0, 0, onTimeOff, free, 0, bookable);

    static TechDay Off(DateOnly date, string region, string tech = "t1") => Day(date, region, 0, 0, tech, available: 0, onTimeOff: true);

    // A tech whose free booking windows start at the given hours.
    static TechDay Windowed(DateOnly date, string region, string tech, int bookable, params int[] freeHours) =>
        Day(date, region, freeHours.Length, bookable, tech) with
        {
            FreeWindowStarts = [.. freeHours.Select(h => date.ToDateTime(new TimeOnly(h, 0)))],
        };

    static Block Unassigned(DateOnly date, string region, string start, string end, string refId = "u1") =>
        new()
        {
            Kind = "job_unassigned",
            WorkDate = date,
            TechId = "",
            TechName = "",
            StartsAt = date.ToDateTime(TimeOnly.Parse(start)),
            EndsAt = date.ToDateTime(TimeOnly.Parse(end)),
            RefId = refId,
            Status = "A",
            Region = region,
            Skills = "INS",
        };

    static AvailabilityCell Placed(IEnumerable<TechDay> techs, IEnumerable<Block> demand, DateOnly? date = null, string? region = "Carrollton") =>
        InstallAvailability.Build(techs, demand, Dates(date ?? Friday, 1), date ?? Friday, region: region).Rows[0].Days[0];

    static List<DateOnly> Dates(DateOnly start, int days) => [.. Enumerable.Range(0, days).Select(start.AddDays)];

    static AvailabilityRow Row(InstallGrid grid, string area) => grid.Rows.Single(r => r.Area == area);

    [Theory]
    [InlineData("Columbia Core", "Columbia Core", true)]
    [InlineData("Columbia Core", "Central MO", true)]
    [InlineData("Central MO", "Columbia Core", false)]
    [InlineData("Jefferson City Area", "Columbia Core", false)]
    [InlineData("Jefferson City Area", "STL Area", false)]
    [InlineData("Carrollton", "Carrollton", true)]
    [InlineData("Carrollton", "Central MO", false)]
    [InlineData("", "", true)]
    [InlineData("Columbia Core", "", false)]
    [InlineData("Columbia Core", "Nowhere", false)]
    public void A_region_filter_takes_a_territory_or_its_area(string territory, string filter, bool expected) =>
        Assert.Equal(expected, InstallAvailability.InRegion(territory, filter));

    [Theory]
    [InlineData(0, "Full")]
    [InlineData(1, "Limited")]
    [InlineData(14, "Limited")]
    [InlineData(15, "Limited")]
    [InlineData(29, "Limited")]
    [InlineData(30, "Open")]
    [InlineData(100, "Open")]
    public void Status_follows_the_vps_thresholds(int percent, string status) => Assert.Equal(status, InstallAvailability.Status(percent));

    [Theory]
    [InlineData(29, 200, 15)]
    [InlineData(57, 200, 29)]
    [InlineData(1, 8, 13)]
    [InlineData(0, 0, 0)]
    public void Percent_rounds_half_up_exactly(int free, int bookable, int percent) => Assert.Equal(percent, InstallAvailability.Percent(free, bookable));

    [Theory]
    [InlineData(4, 1000, "Full")]
    [InlineData(5, 1000, "Limited")]
    [InlineData(149, 1000, "Limited")]
    [InlineData(29, 200, "Limited")]
    [InlineData(59, 200, "Open")]
    [InlineData(299, 1000, "Open")]
    public void Status_is_classified_on_the_shown_whole_percent(int free, int bookable, string status)
    {
        var cell = InstallAvailability.Build([Day(Friday, "Carrollton", free, bookable)], [], Dates(Friday, 1), Friday, region: "Carrollton").Rows[0].Days[0];
        Assert.Equal(status, cell.Status);
    }

    [Fact]
    public void Territories_roll_up_into_the_vps_areas_and_others_get_their_own_row()
    {
        var grid = InstallAvailability.Build(
            [
                Day(Friday, "Columbia Core", 1, 4, "a"),
                Day(Friday, "Jefferson City Area", 3, 4, "b"),
                Day(Friday, "Sedalia-La Monte", 2, 5, "c"),
                Day(Friday, "Springfield Area", 5, 5, "d"),
            ],
            [],
            Dates(Friday, 1),
            Friday
        );
        Assert.Equal(["Central MO", "Springfield Area", "WKLS"], grid.Rows.Select(r => r.Area));
        Assert.Equal((4, 8, "Open"), (Row(grid, "Central MO").Days[0].Free, Row(grid, "Central MO").Days[0].Bookable, Row(grid, "Central MO").Days[0].Status));
        Assert.Equal((2, 5), (Row(grid, "WKLS").Days[0].Free, Row(grid, "WKLS").Days[0].Bookable));
    }

    [Fact]
    public void An_area_with_no_techs_in_the_grid_has_no_row()
    {
        // Lamar-Webb City was renamed Nevada-Lamar: its last tech day is before the grid.
        var grid = InstallAvailability.Build(
            [Day(Friday.AddDays(-1), "Lamar-Webb City", 1, 4, "a"), Day(Friday, "Nevada-Lamar", 1, 4, "a"), Day(Saturday, "Carrollton", 1, 4, "b")],
            [],
            Dates(Friday, 2),
            Friday
        );
        Assert.Equal(["Carrollton", "Nevada-Lamar"], grid.Rows.Select(r => r.Area));
        Assert.Equal("Closed", Row(grid, "Carrollton").Days[0].Status);
    }

    [Fact]
    public void Techs_all_on_time_off_are_no_techs()
    {
        var grid = InstallAvailability.Build([Off(Friday, "Carrollton", "a"), Off(Friday, "Carrollton", "b")], [], Dates(Friday, 1), Friday, region: "Carrollton");
        var cell = Row(grid, "Carrollton").Days[0];
        Assert.Equal(("No Techs", null), (cell.Status, cell.Share));
        Assert.Equal("No Techs", Row(grid, "Carrollton").Total.Status);
    }

    [Fact]
    public void One_working_tech_beside_time_off_counts_their_windows()
    {
        var grid = InstallAvailability.Build([Off(Friday, "Carrollton", "a"), Day(Friday, "Carrollton", 2, 4, "b")], [], Dates(Friday, 1), Friday, region: "Carrollton");
        Assert.Equal((2, 4, "Open"), (Row(grid, "Carrollton").Days[0].Free, Row(grid, "Carrollton").Days[0].Bookable, Row(grid, "Carrollton").Days[0].Status));
    }

    [Fact]
    public void Working_techs_with_nothing_bookable_left_are_full_at_zero()
    {
        // Today after the last window started: the techs work, but no window can be booked.
        var grid = InstallAvailability.Build([Day(Friday, "Carrollton", 0, 0)], [], Dates(Friday, 1), Friday, region: "Carrollton");
        var cell = Row(grid, "Carrollton").Days[0];
        Assert.Equal(("Full", 0.0), (cell.Status, cell.Share));
    }

    [Fact]
    public void A_sunday_is_closed_even_with_time_off()
    {
        var grid = InstallAvailability.Build([Off(Sunday, "Carrollton")], [], Dates(Sunday, 1), Sunday, region: "Carrollton");
        Assert.Equal("Closed", Row(grid, "Carrollton").Days[0].Status);
    }

    [Fact]
    public void A_region_shows_only_its_area()
    {
        var grid = InstallAvailability.Build([Day(Friday, "Columbia Core", 1, 4)], [], Dates(Friday, 1), Friday, region: "Columbia Core");
        Assert.Equal(["Central MO"], grid.Rows.Select(r => r.Area));
    }

    [Fact]
    public void A_region_with_no_techs_scheduled_still_shows_its_area_as_closed()
    {
        var grid = InstallAvailability.Build([], [], Dates(Friday, 1), Friday, region: "Lebanon-Rolla");
        Assert.Equal((0, 0, "Closed"), (Row(grid, "Lebanon-Rolla").Days[0].Free, Row(grid, "Lebanon-Rolla").Days[0].Bookable, Row(grid, "Lebanon-Rolla").Days[0].Status));
    }

    [Fact]
    public void A_grid_ending_on_the_last_date_builds()
    {
        var grid = InstallAvailability.Build([Day(DateOnly.MaxValue, "Carrollton", 1, 4)], [], Dates(DateOnly.MaxValue, 1), DateOnly.MaxValue);
        Assert.Equal(1, Row(grid, "Carrollton").Days[0].Free);
    }

    [Fact]
    public void A_sunday_is_closed_and_the_total_sums_the_week()
    {
        var grid = InstallAvailability.Build(
            [Day(Saturday, "Carrollton", 1, 4), Day(Sunday, "Carrollton", 0, 0), Day(Friday, "Carrollton", 0, 6)],
            [],
            Dates(Friday, 3),
            Friday
        );
        var row = Row(grid, "Carrollton");
        Assert.Equal(["Full", "Limited", "Closed"], row.Days.Select(c => c.Status));
        Assert.Equal((1, 10, "Limited"), (row.Total.Free, row.Total.Bookable, row.Total.Status));
        Assert.Equal([Friday, Saturday, Sunday], grid.Days);
    }

    [Fact]
    public void Exceptions_list_each_unavailable_area_with_its_reason()
    {
        var grid = InstallAvailability.Build(
            [
                Day(Friday, "Carrollton", 0, 4, "a"),
                Off(Friday, "Lebanon-Rolla", "b"),
                Day(Friday, "Clinton-Butler", 4, 4, "c"),
                Day(Saturday, "Carrollton", 4, 4, "a"),
                Day(Saturday, "Lebanon-Rolla", 4, 4, "b"),
                Day(Saturday, "Clinton-Butler", 4, 4, "c"),
                Day(Monday, "Carrollton", 0, 4, "a"),
            ],
            [],
            Dates(Friday, 3),
            Friday
        );
        var friday = grid.Exceptions.SkipWhile(l => l != "Friday 10/16").TakeWhile(l => l != "").ToList();
        Assert.Equal("All areas have same day/next day availability with the following notes/exceptions:", grid.Exceptions[0]);
        Assert.Equal(["Friday 10/16", "Carrollton - Full", "Lebanon-Rolla - No Techs"], friday);
        Assert.DoesNotContain(friday, l => l.StartsWith("Clinton-Butler -"));
        // Every area is open on Saturday.
        Assert.DoesNotContain("Saturday 10/17", grid.Exceptions);
        Assert.DoesNotContain(grid.Exceptions, l => l.StartsWith("Monday"));
    }

    [Fact]
    public void Exceptions_say_so_when_nothing_is_closed()
    {
        var grid = InstallAvailability.Build([Day(Friday, "Carrollton", 4, 4), Day(Saturday, "Carrollton", 4, 4)], [], Dates(Friday, 2), Friday, region: "Carrollton");
        Assert.Equal(["All areas have same day/next day availability."], grid.Exceptions);
    }

    [Fact]
    public void A_day_every_area_is_closed_is_one_exception_for_all_areas()
    {
        var grid = InstallAvailability.Build([Day(Saturday, "Carrollton", 4, 4), Day(Sunday, "Carrollton", 0, 0)], [], Dates(Saturday, 2), Saturday, region: "Carrollton");
        Assert.Equal(
            ["All areas have same day/next day availability with the following notes/exceptions:", "", "Sunday 10/18", "All areas - Closed"],
            grid.Exceptions
        );
    }

    [Fact]
    public void A_grid_with_no_install_techs_is_closed_everywhere()
    {
        var grid = InstallAvailability.Build([], [], [Saturday, Sunday], Saturday);
        Assert.Empty(grid.Rows);
        Assert.Equal(
            [
                "All areas have same day/next day availability with the following notes/exceptions:",
                "",
                "Saturday 10/17",
                "All areas - Closed",
                "",
                "Sunday 10/18",
                "All areas - Closed",
            ],
            grid.Exceptions
        );
    }

    [Fact]
    public void Next_day_is_the_next_date_in_the_grid()
    {
        // An empty Sunday is left out of the export, so Saturday's next day is Monday.
        var grid = InstallAvailability.Build([Day(Saturday, "Carrollton", 4, 4), Day(Monday, "Carrollton", 0, 4)], [], [Saturday, Monday], Saturday);
        Assert.Equal([Saturday, Monday], grid.Days);
        Assert.Equal(
            ["All areas have same day/next day availability with the following notes/exceptions:", "", "Monday 10/19", "Carrollton - Full"],
            grid.Exceptions
        );
    }

    [Fact]
    public void A_one_day_grid_speaks_only_for_same_day()
    {
        var grid = InstallAvailability.Build([Day(Friday, "Carrollton", 4, 4)], [], Dates(Friday, 1), Friday, region: "Carrollton");
        Assert.Equal(["All areas have same day availability."], grid.Exceptions);
    }

    [Fact]
    public void A_grid_not_starting_today_names_its_dates()
    {
        var grid = InstallAvailability.Build([Day(Friday, "Carrollton", 4, 4), Day(Saturday, "Carrollton", 4, 4)], [], Dates(Friday, 2), Friday.AddDays(-3), region: "Carrollton");
        Assert.Equal(["All areas have availability on Fri 10/16/Sat 10/17."], grid.Exceptions);
    }

    [Fact]
    public void An_unassigned_job_uses_the_window_it_is_scheduled_in()
    {
        var cell = Placed([Windowed(Friday, "Carrollton", "a", 5, 8, 10, 13, 15, 17)], [Unassigned(Friday, "Carrollton", "08:30", "09:30")]);
        Assert.Equal((4, 5), (cell.Free, cell.Bookable));
    }

    [Fact]
    public void A_job_scheduled_across_two_windows_uses_both()
    {
        var cell = Placed([Windowed(Friday, "Carrollton", "a", 5, 8, 10, 13, 15, 17)], [Unassigned(Friday, "Carrollton", "09:00", "11:00")]);
        Assert.Equal(3, cell.Free);
    }

    [Fact]
    public void More_jobs_than_free_windows_floor_at_zero_and_read_full()
    {
        var jobs = Enumerable.Range(1, 3).Select(i => Unassigned(Friday, "Carrollton", "08:00", "09:00", $"u{i}"));
        var cell = Placed([Windowed(Friday, "Carrollton", "a", 5, 8)], jobs);
        Assert.Equal((0, 5, InstallAvailability.Full), (cell.Free, cell.Bookable, cell.Status));
    }

    [Theory]
    [InlineData("19:00", "20:00")]
    [InlineData("06:00", "08:00")]
    public void A_job_outside_every_booking_window_uses_nothing(string start, string end)
    {
        var cell = Placed([Windowed(Friday, "Carrollton", "a", 5, 8, 10, 13, 15, 17)], [Unassigned(Friday, "Carrollton", start, end)]);
        Assert.Equal(5, cell.Free);
    }

    [Fact]
    public void A_sunday_job_uses_nothing_and_the_day_stays_closed()
    {
        var cell = Placed([Windowed(Sunday, "Carrollton", "a", 0)], [Unassigned(Sunday, "Carrollton", "09:00", "11:00")], Sunday);
        Assert.Equal((0, 0, InstallAvailability.Closed), (cell.Free, cell.Bookable, cell.Status));
    }

    [Fact]
    public void A_window_that_is_not_free_cannot_be_used()
    {
        // The 8 and 10 windows are past the lead time or booked, so the tech has only 13 and 15 free.
        var cell = Placed([Windowed(Friday, "Carrollton", "a", 5, 13, 15)], [Unassigned(Friday, "Carrollton", "08:30", "09:30")]);
        Assert.Equal(2, cell.Free);
    }

    [Fact]
    public void A_repeated_row_counts_once()
    {
        var job = Unassigned(Friday, "Carrollton", "08:30", "09:30");
        var cell = Placed([Windowed(Friday, "Carrollton", "a", 5, 8, 10, 13)], [job, job with { }]);
        Assert.Equal(2, cell.Free);
    }

    [Fact]
    public void A_job_never_uses_another_areas_tech_or_another_days_window()
    {
        var grid = InstallAvailability.Build(
            [
                Windowed(Friday, "Columbia Core", "a", 5, 8, 10),
                Windowed(Friday, "Carrollton", "b", 5, 8, 10),
                Windowed(Saturday, "Carrollton", "c", 4, 8, 10),
            ],
            [Unassigned(Friday, "Carrollton", "08:30", "09:30"), Unassigned(Saturday, "Springfield Area", "08:30", "09:30", "u2")],
            Dates(Friday, 2),
            Friday
        );
        Assert.Equal(1, Row(grid, "Carrollton").Days[0].Free);
        Assert.Equal(2, Row(grid, "Carrollton").Days[1].Free);
        Assert.Equal(2, Row(grid, "Central MO").Days[0].Free);
    }

    [Fact]
    public void A_job_uses_a_tech_in_any_territory_of_its_area()
    {
        var grid = InstallAvailability.Build(
            [Windowed(Friday, "Jefferson City Area", "a", 5, 8)],
            [Unassigned(Friday, "Columbia Core", "08:30", "09:30")],
            Dates(Friday, 1),
            Friday
        );
        Assert.Equal(0, Row(grid, "Central MO").Days[0].Free);
    }

    [Fact]
    public void A_blank_region_uses_nothing()
    {
        var cell = Placed([Windowed(Friday, "Carrollton", "a", 5, 8)], [Unassigned(Friday, "", "08:30", "09:30")]);
        Assert.Equal(1, cell.Free);
    }

    [Fact]
    public void A_two_window_job_uses_the_one_window_that_is_free()
    {
        var cell = Placed([Windowed(Friday, "Carrollton", "a", 5, 10, 13)], [Unassigned(Friday, "Carrollton", "09:00", "11:00")]);
        Assert.Equal(1, cell.Free);
    }

    [Fact]
    public void One_job_uses_one_tech_not_two()
    {
        // a has the 8 window free and b the 10: the job takes a's, not both.
        var cell = Placed(
            [Windowed(Friday, "Carrollton", "a", 5, 8), Windowed(Friday, "Carrollton", "b", 5, 10)],
            [Unassigned(Friday, "Carrollton", "09:00", "11:00")]
        );
        Assert.Equal(1, cell.Free);
    }

    [Fact]
    public void The_longest_job_is_placed_first_whatever_the_input_order()
    {
        // Placing the short job first would put it on a and leave the long job only one of its two windows.
        TechDay[] techs = [Windowed(Friday, "Carrollton", "a", 5, 8, 10), Windowed(Friday, "Carrollton", "b", 5, 8, 13, 15)];
        Block[] jobs = [Unassigned(Friday, "Carrollton", "08:30", "09:30", "short"), Unassigned(Friday, "Carrollton", "09:00", "11:00", "long")];
        Assert.Equal(2, Placed(techs, jobs).Free);
        Assert.Equal(2, Placed(techs, jobs.Reverse()).Free);
    }

    [Fact]
    public void A_region_filter_places_only_that_regions_work()
    {
        TechDay[] techs = [Windowed(Friday, "Carrollton", "a", 5, 8, 10), Windowed(Friday, "Lebanon-Rolla", "b", 5, 8, 10)];
        Block[] carrollton = [Unassigned(Friday, "Carrollton", "08:30", "09:30")];
        Assert.Equal(1, Placed(techs, carrollton).Free);
        Assert.Equal(2, Placed(techs, carrollton, region: "Lebanon-Rolla").Free);
    }

    [Fact]
    public void The_total_sums_the_cells_after_placement()
    {
        var row = Row(
            InstallAvailability.Build(
                [Windowed(Friday, "Carrollton", "a", 5, 8, 10), Windowed(Saturday, "Carrollton", "a", 4, 8)],
                [Unassigned(Friday, "Carrollton", "08:30", "09:30"), Unassigned(Saturday, "Carrollton", "08:30", "09:30", "u2")],
                Dates(Friday, 2),
                Friday
            ),
            "Carrollton"
        );
        Assert.Equal((1, 0, 9, 1), (row.Days[0].Free, row.Days[1].Free, row.Total.Bookable, row.Total.Free));
    }
}
