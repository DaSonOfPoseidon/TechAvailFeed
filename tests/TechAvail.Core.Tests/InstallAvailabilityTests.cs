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

    static List<DateOnly> Dates(DateOnly start, int days) => [.. Enumerable.Range(0, days).Select(start.AddDays)];

    static AvailabilityRow Row(InstallGrid grid, string area) => grid.Rows.Single(r => r.Area == area);

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
        var cell = InstallAvailability.Build([Day(Friday, "Carrollton", free, bookable)], Dates(Friday, 1), Friday, region: "Carrollton").Rows[0].Days[0];
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
            Dates(Friday, 2),
            Friday
        );
        Assert.Equal(["Carrollton", "Nevada-Lamar"], grid.Rows.Select(r => r.Area));
        Assert.Equal("Closed", Row(grid, "Carrollton").Days[0].Status);
    }

    [Fact]
    public void Techs_all_on_time_off_are_no_techs()
    {
        var grid = InstallAvailability.Build([Off(Friday, "Carrollton", "a"), Off(Friday, "Carrollton", "b")], Dates(Friday, 1), Friday, region: "Carrollton");
        var cell = Row(grid, "Carrollton").Days[0];
        Assert.Equal(("No Techs", null), (cell.Status, cell.Share));
        Assert.Equal("No Techs", Row(grid, "Carrollton").Total.Status);
    }

    [Fact]
    public void One_working_tech_beside_time_off_counts_their_windows()
    {
        var grid = InstallAvailability.Build([Off(Friday, "Carrollton", "a"), Day(Friday, "Carrollton", 2, 4, "b")], Dates(Friday, 1), Friday, region: "Carrollton");
        Assert.Equal((2, 4, "Open"), (Row(grid, "Carrollton").Days[0].Free, Row(grid, "Carrollton").Days[0].Bookable, Row(grid, "Carrollton").Days[0].Status));
    }

    [Fact]
    public void Working_techs_with_nothing_bookable_left_are_full_at_zero()
    {
        // Today after the last window started: the techs work, but no window can be booked.
        var grid = InstallAvailability.Build([Day(Friday, "Carrollton", 0, 0)], Dates(Friday, 1), Friday, region: "Carrollton");
        var cell = Row(grid, "Carrollton").Days[0];
        Assert.Equal(("Full", 0.0), (cell.Status, cell.Share));
    }

    [Fact]
    public void A_sunday_is_closed_even_with_time_off()
    {
        var grid = InstallAvailability.Build([Off(Sunday, "Carrollton")], Dates(Sunday, 1), Sunday, region: "Carrollton");
        Assert.Equal("Closed", Row(grid, "Carrollton").Days[0].Status);
    }

    [Fact]
    public void A_region_shows_only_its_area()
    {
        var grid = InstallAvailability.Build([Day(Friday, "Columbia Core", 1, 4)], Dates(Friday, 1), Friday, region: "Columbia Core");
        Assert.Equal(["Central MO"], grid.Rows.Select(r => r.Area));
    }

    [Fact]
    public void A_region_with_no_techs_scheduled_still_shows_its_area_as_closed()
    {
        var grid = InstallAvailability.Build([], Dates(Friday, 1), Friday, region: "Lebanon-Rolla");
        Assert.Equal((0, 0, "Closed"), (Row(grid, "Lebanon-Rolla").Days[0].Free, Row(grid, "Lebanon-Rolla").Days[0].Bookable, Row(grid, "Lebanon-Rolla").Days[0].Status));
    }

    [Fact]
    public void A_grid_ending_on_the_last_date_builds()
    {
        var grid = InstallAvailability.Build([Day(DateOnly.MaxValue, "Carrollton", 1, 4)], Dates(DateOnly.MaxValue, 1), DateOnly.MaxValue);
        Assert.Equal(1, Row(grid, "Carrollton").Days[0].Free);
    }

    [Fact]
    public void A_sunday_is_closed_and_the_total_sums_the_week()
    {
        var grid = InstallAvailability.Build(
            [Day(Saturday, "Carrollton", 1, 4), Day(Sunday, "Carrollton", 0, 0), Day(Friday, "Carrollton", 0, 6)],
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
        var grid = InstallAvailability.Build([Day(Friday, "Carrollton", 4, 4), Day(Saturday, "Carrollton", 4, 4)], Dates(Friday, 2), Friday, region: "Carrollton");
        Assert.Equal(["All areas have same day/next day availability."], grid.Exceptions);
    }

    [Fact]
    public void A_day_every_area_is_closed_is_one_exception_for_all_areas()
    {
        var grid = InstallAvailability.Build([Day(Saturday, "Carrollton", 4, 4), Day(Sunday, "Carrollton", 0, 0)], Dates(Saturday, 2), Saturday, region: "Carrollton");
        Assert.Equal(
            ["All areas have same day/next day availability with the following notes/exceptions:", "", "Sunday 10/18", "All areas - Closed"],
            grid.Exceptions
        );
    }

    [Fact]
    public void A_grid_with_no_install_techs_is_closed_everywhere()
    {
        var grid = InstallAvailability.Build([], [Saturday, Sunday], Saturday);
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
        var grid = InstallAvailability.Build([Day(Saturday, "Carrollton", 4, 4), Day(Monday, "Carrollton", 0, 4)], [Saturday, Monday], Saturday);
        Assert.Equal([Saturday, Monday], grid.Days);
        Assert.Equal(
            ["All areas have same day/next day availability with the following notes/exceptions:", "", "Monday 10/19", "Carrollton - Full"],
            grid.Exceptions
        );
    }

    [Fact]
    public void A_one_day_grid_speaks_only_for_same_day()
    {
        var grid = InstallAvailability.Build([Day(Friday, "Carrollton", 4, 4)], Dates(Friday, 1), Friday, region: "Carrollton");
        Assert.Equal(["All areas have same day availability."], grid.Exceptions);
    }

    [Fact]
    public void A_grid_not_starting_today_names_its_dates()
    {
        var grid = InstallAvailability.Build([Day(Friday, "Carrollton", 4, 4), Day(Saturday, "Carrollton", 4, 4)], Dates(Friday, 2), Friday.AddDays(-3), region: "Carrollton");
        Assert.Equal(["All areas have availability on Fri 10/16/Sat 10/17."], grid.Exceptions);
    }
}
