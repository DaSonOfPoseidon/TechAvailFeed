namespace TechAvail.Core.Tests;

public class InstallAvailabilityTests
{
    static readonly DateOnly Friday = new(2026, 10, 16);
    static readonly DateOnly Saturday = new(2026, 10, 17);
    static readonly DateOnly Sunday = new(2026, 10, 18);

    static TechDay Day(DateOnly date, string region, int free, int bookable, string tech = "t1") =>
        new(date, tech, tech, region, "INS", [], [], [], [], 0, 0, 0, 0, 0, 0, 0, 0, false, free, 0, bookable);

    static AvailabilityRow Row(InstallGrid grid, string area) => grid.Rows.Single(r => r.Area == area);

    [Theory]
    [InlineData(0, 10, "Closed")]
    [InlineData(149, 1000, "Closed")]
    [InlineData(15, 100, "Limited")]
    [InlineData(299, 1000, "Limited")]
    [InlineData(30, 100, "Open")]
    [InlineData(10, 10, "Open")]
    public void Status_follows_the_vps_thresholds(int free, int bookable, string status) =>
        Assert.Equal(status, InstallAvailability.Status(free, bookable, daysHaveSlots: true));

    [Fact]
    public void An_area_with_nothing_bookable_is_closed_unless_no_area_has_slots()
    {
        Assert.Equal("Closed", InstallAvailability.Status(0, 0, daysHaveSlots: true));
        Assert.Equal("No slots", InstallAvailability.Status(0, 0, daysHaveSlots: false));
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
            days: 1
        );
        Assert.Equal(InstallAvailability.Areas.Length + 1, grid.Rows.Count);
        Assert.Equal("Springfield Area", grid.Rows[^1].Area);
        Assert.Equal((4, 8, "Open"), (Row(grid, "Central MO").Days[0].Free, Row(grid, "Central MO").Days[0].Bookable, Row(grid, "Central MO").Days[0].Status));
        Assert.Equal((2, 5), (Row(grid, "WKLS").Days[0].Free, Row(grid, "WKLS").Days[0].Bookable));
        // No tech in Carrollton while other areas have slots: closed.
        Assert.Equal("Closed", Row(grid, "Carrollton").Days[0].Status);
    }

    [Fact]
    public void A_region_shows_only_its_area()
    {
        var grid = InstallAvailability.Build([Day(Friday, "Columbia Core", 1, 4)], Friday, days: 1, region: "Columbia Core");
        Assert.Equal(["Central MO"], grid.Rows.Select(r => r.Area));
    }

    [Fact]
    public void A_region_with_no_techs_still_shows_its_area()
    {
        var grid = InstallAvailability.Build([], Friday, days: 1, region: "Lebanon-Rolla");
        Assert.Equal((0, 0, "No slots"), (Row(grid, "Lebanon-Rolla").Days[0].Free, Row(grid, "Lebanon-Rolla").Days[0].Bookable, Row(grid, "Lebanon-Rolla").Days[0].Status));
    }

    [Fact]
    public void A_grid_ending_on_the_last_date_builds()
    {
        var grid = InstallAvailability.Build([Day(DateOnly.MaxValue, "Carrollton", 1, 4)], DateOnly.MaxValue, days: 1);
        Assert.Equal(1, Row(grid, "Carrollton").Days[0].Free);
    }

    [Fact]
    public void A_day_no_area_can_book_is_no_slots_and_the_total_sums_the_week()
    {
        var grid = InstallAvailability.Build(
            [Day(Saturday, "Carrollton", 1, 4), Day(Sunday, "Carrollton", 0, 0), Day(Friday, "Carrollton", 0, 6)],
            Friday,
            days: 3
        );
        var row = Row(grid, "Carrollton");
        Assert.Equal(["Closed", "Limited", "No slots"], row.Days.Select(c => c.Status));
        Assert.Equal((1, 10, "Closed"), (row.Total.Free, row.Total.Bookable, row.Total.Status));
        Assert.Equal([Friday, Saturday, Sunday], grid.Days);
    }

    [Fact]
    public void Exceptions_list_the_closed_areas_for_same_day_and_next_day()
    {
        var grid = InstallAvailability.Build(
            [
                Day(Friday, "Carrollton", 4, 4, "a"),
                Day(Friday, "Lebanon-Rolla", 0, 4, "b"),
                Day(Saturday, "Carrollton", 4, 4, "a"),
                Day(DateOnly.FromDateTime(new DateTime(2026, 10, 19)), "Carrollton", 0, 4, "a"),
            ],
            Friday,
            days: 3,
            region: "Lebanon-Rolla"
        );
        Assert.Equal(
            [
                "All areas have same day/next day availability with the following notes/exceptions:",
                "",
                "Friday 10/16",
                "Lebanon-Rolla - Closed",
                "",
                "Saturday 10/17",
                "Lebanon-Rolla - Closed",
            ],
            grid.Exceptions
        );
    }

    [Fact]
    public void Exceptions_say_so_when_nothing_is_closed()
    {
        var grid = InstallAvailability.Build([Day(Friday, "Carrollton", 4, 4), Day(Saturday, "Carrollton", 4, 4)], Friday, days: 2, region: "Carrollton");
        Assert.Equal(["All areas have same day/next day availability."], grid.Exceptions);
    }

    [Fact]
    public void A_day_no_area_can_book_is_an_exception_for_all_areas()
    {
        var grid = InstallAvailability.Build([Day(Saturday, "Carrollton", 4, 4), Day(Sunday, "Carrollton", 0, 0)], Saturday, days: 2, region: "Carrollton");
        Assert.Equal(
            ["All areas have same day/next day availability with the following notes/exceptions:", "", "Sunday 10/18", "All areas - No slots"],
            grid.Exceptions
        );
    }

    [Fact]
    public void A_one_day_grid_speaks_only_for_same_day()
    {
        var grid = InstallAvailability.Build([Day(Friday, "Carrollton", 4, 4)], Friday, days: 1, region: "Carrollton");
        Assert.Equal(["All areas have same day availability."], grid.Exceptions);
    }
}
