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

    static AvailabilityRow Row(InstallGrid grid, string area) => grid.Rows.Single(r => r.Area == area);

    [Theory]
    [InlineData(0.0, "Closed")]
    [InlineData(0.144, "Closed")]
    [InlineData(0.15, "Limited")]
    [InlineData(0.294, "Limited")]
    [InlineData(0.30, "Open")]
    [InlineData(1.0, "Open")]
    public void Status_follows_the_vps_thresholds(double share, string status) => Assert.Equal(status, InstallAvailability.Status(share));

    [Theory]
    [InlineData(149, 1000, "Limited")]
    [InlineData(299, 1000, "Open")]
    public void Status_is_classified_on_the_shown_whole_percent(int free, int bookable, string status)
    {
        var cell = InstallAvailability.Build([Day(Friday, "Carrollton", free, bookable)], Friday, Friday, days: 1, region: "Carrollton").Rows[0].Days[0];
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
            Friday,
            Friday,
            days: 1
        );
        Assert.Equal(InstallAvailability.Areas.Length + 1, grid.Rows.Count);
        Assert.Equal("Springfield Area", grid.Rows[^1].Area);
        Assert.Equal((4, 8, "Open"), (Row(grid, "Central MO").Days[0].Free, Row(grid, "Central MO").Days[0].Bookable, Row(grid, "Central MO").Days[0].Status));
        Assert.Equal((2, 5), (Row(grid, "WKLS").Days[0].Free, Row(grid, "WKLS").Days[0].Bookable));
        // No tech scheduled in Carrollton: not open.
        Assert.Equal("Not Open", Row(grid, "Carrollton").Days[0].Status);
    }

    [Fact]
    public void Techs_all_on_time_off_are_no_techs()
    {
        var grid = InstallAvailability.Build([Off(Friday, "Carrollton", "a"), Off(Friday, "Carrollton", "b")], Friday, Friday, days: 1, region: "Carrollton");
        var cell = Row(grid, "Carrollton").Days[0];
        Assert.Equal(("No Techs", null), (cell.Status, cell.Share));
        Assert.Equal("No Techs", Row(grid, "Carrollton").Total.Status);
    }

    [Fact]
    public void One_working_tech_beside_time_off_counts_their_windows()
    {
        var grid = InstallAvailability.Build([Off(Friday, "Carrollton", "a"), Day(Friday, "Carrollton", 2, 4, "b")], Friday, Friday, days: 1, region: "Carrollton");
        Assert.Equal((2, 4, "Open"), (Row(grid, "Carrollton").Days[0].Free, Row(grid, "Carrollton").Days[0].Bookable, Row(grid, "Carrollton").Days[0].Status));
    }

    [Fact]
    public void Working_techs_with_nothing_bookable_left_are_closed_at_zero()
    {
        // Today after the last window started: the techs work, but no window can be booked.
        var grid = InstallAvailability.Build([Day(Friday, "Carrollton", 0, 0)], Friday, Friday, days: 1, region: "Carrollton");
        var cell = Row(grid, "Carrollton").Days[0];
        Assert.Equal(("Closed", 0.0), (cell.Status, cell.Share));
    }

    [Fact]
    public void A_sunday_is_not_open_even_with_time_off()
    {
        var grid = InstallAvailability.Build([Off(Sunday, "Carrollton")], Sunday, Sunday, days: 1, region: "Carrollton");
        Assert.Equal("Not Open", Row(grid, "Carrollton").Days[0].Status);
    }

    [Fact]
    public void A_region_shows_only_its_area()
    {
        var grid = InstallAvailability.Build([Day(Friday, "Columbia Core", 1, 4)], Friday, Friday, days: 1, region: "Columbia Core");
        Assert.Equal(["Central MO"], grid.Rows.Select(r => r.Area));
    }

    [Fact]
    public void A_region_with_no_techs_still_shows_its_area()
    {
        var grid = InstallAvailability.Build([], Friday, Friday, days: 1, region: "Lebanon-Rolla");
        Assert.Equal((0, 0, "Not Open"), (Row(grid, "Lebanon-Rolla").Days[0].Free, Row(grid, "Lebanon-Rolla").Days[0].Bookable, Row(grid, "Lebanon-Rolla").Days[0].Status));
    }

    [Fact]
    public void A_grid_ending_on_the_last_date_builds()
    {
        var grid = InstallAvailability.Build([Day(DateOnly.MaxValue, "Carrollton", 1, 4)], DateOnly.MaxValue, DateOnly.MaxValue, days: 1);
        Assert.Equal(1, Row(grid, "Carrollton").Days[0].Free);
    }

    [Fact]
    public void A_sunday_is_not_open_and_the_total_sums_the_week()
    {
        var grid = InstallAvailability.Build(
            [Day(Saturday, "Carrollton", 1, 4), Day(Sunday, "Carrollton", 0, 0), Day(Friday, "Carrollton", 0, 6)],
            Friday,
            Friday,
            days: 3
        );
        var row = Row(grid, "Carrollton");
        Assert.Equal(["Closed", "Limited", "Not Open"], row.Days.Select(c => c.Status));
        Assert.Equal((1, 10, "Closed"), (row.Total.Free, row.Total.Bookable, row.Total.Status));
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
            Friday,
            Friday,
            days: 3
        );
        var friday = grid.Exceptions.SkipWhile(l => l != "Friday 10/16").TakeWhile(l => l != "").ToList();
        Assert.Equal("All areas have same day/next day availability with the following notes/exceptions:", grid.Exceptions[0]);
        Assert.Equal(["Friday 10/16", "Carrollton - Closed", "Central MO - Not Open"], friday.Take(3));
        Assert.Contains("Lebanon-Rolla - No Techs", friday);
        Assert.DoesNotContain(friday, l => l.StartsWith("Clinton-Butler -"));
        Assert.Contains("Saturday 10/17", grid.Exceptions);
        Assert.DoesNotContain(grid.Exceptions, l => l.StartsWith("Monday"));
    }

    [Fact]
    public void Exceptions_say_so_when_nothing_is_closed()
    {
        var grid = InstallAvailability.Build([Day(Friday, "Carrollton", 4, 4), Day(Saturday, "Carrollton", 4, 4)], Friday, Friday, days: 2, region: "Carrollton");
        Assert.Equal(["All areas have same day/next day availability."], grid.Exceptions);
    }

    [Fact]
    public void A_day_every_area_is_not_open_is_one_exception_for_all_areas()
    {
        var grid = InstallAvailability.Build([Day(Saturday, "Carrollton", 4, 4), Day(Sunday, "Carrollton", 0, 0)], Saturday, Saturday, days: 2, region: "Carrollton");
        Assert.Equal(
            ["All areas have same day/next day availability with the following notes/exceptions:", "", "Sunday 10/18", "All areas - Not Open"],
            grid.Exceptions
        );
    }

    [Fact]
    public void A_one_day_grid_speaks_only_for_same_day()
    {
        var grid = InstallAvailability.Build([Day(Friday, "Carrollton", 4, 4)], Friday, Friday, days: 1, region: "Carrollton");
        Assert.Equal(["All areas have same day availability."], grid.Exceptions);
    }

    [Fact]
    public void A_grid_not_starting_today_names_its_dates()
    {
        var grid = InstallAvailability.Build([Day(Friday, "Carrollton", 4, 4), Day(Saturday, "Carrollton", 4, 4)], Friday, Friday.AddDays(-3), days: 2, region: "Carrollton");
        Assert.Equal(["All areas have availability on Fri 10/16/Sat 10/17."], grid.Exceptions);
    }
}
