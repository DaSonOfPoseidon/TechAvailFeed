using System.Text;
using TechAvail.Core.Parsing;

namespace TechAvail.Core.Tests;

public class FeedParserTests
{
    const string Header =
        "GENERATED_AT,WORK_DATE,TECH_ID,TECH_NAME,OPEN_FROM,OPEN_UNTIL,OPEN_MINUTES,REGION,SKILLS\n";
    const string Row =
        "\"2026-09-29T09:15:02-05\",\"2026-09-29\",\"jdoe0170\",\"Jane Doe\","
        + "\"2026-09-29 13:00\",\"2026-09-29 15:00\",120,\"North Core\",\"CONN, INS, MDU\"\n";

    static ParsedFeed Parse(string text) => FeedParser.Parse(Encoding.UTF8.GetBytes(text));

    static string ParseError(byte[] data) =>
        Assert.Throws<FeedParseException>(() => FeedParser.Parse(data)).Message;

    static string ParseError(string text) => ParseError(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Parses_mbs_style_export()
    {
        var feed = Parse(Header + Row);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 9, 15, 2, TimeSpan.FromHours(-5)), feed.GeneratedAt);
        var slot = Assert.Single(feed.Slots);
        Assert.Equal(new DateOnly(2026, 9, 29), slot.WorkDate);
        Assert.Equal(new DateTime(2026, 9, 29, 13, 0, 0), slot.OpenFrom);
        Assert.Equal(120, slot.OpenMinutes);
        // The comma-separated skills list survives CSV quoting.
        Assert.Equal("CONN, INS, MDU", slot.Skills);
    }

    [Fact]
    public void Headers_match_case_insensitively_and_in_any_order()
    {
        var header =
            "skills,region,open_minutes,open_until,open_from,tech_name,tech_id,work_date,generated_at\n";
        var row =
            "\"MDU\",\"North\",60.0,\"09-30-2026 04:00 PM\",\"09-30-2026 03:00 PM\","
            + "\"A B\",\"ab01\",\"09-30-2026\",\"2026-09-29T09:15:02+00\"\n";
        var slot = Parse(header + row).Slots[0];
        Assert.Equal(new DateTime(2026, 9, 30, 15, 0, 0), slot.OpenFrom);
        Assert.Equal(60, slot.OpenMinutes);
    }

    [Fact]
    public void Header_only_is_an_empty_snapshot()
    {
        var feed = Parse(Header);
        Assert.Empty(feed.Slots);
        Assert.Null(feed.GeneratedAt);
    }

    [Fact]
    public void Crlf_and_bom_are_accepted() =>
        Assert.Single(Parse(("﻿" + Header + Row).Replace("\n", "\r\n")).Slots);

    [Fact]
    public void Wrong_report_is_rejected() =>
        Assert.StartsWith(
            "missing columns",
            ParseError("TICKET_NUMBER,SCHEDULED_START_TIME\n1,\"09-24-2026 03:00 PM\"\n")
        );

    [Fact]
    public void Truncated_mid_row_is_rejected() =>
        Assert.Throws<FeedParseException>(() => Parse(Header + Row + Row[..40]));

    [Fact]
    public void Empty_file_is_rejected() => Assert.Contains("empty", ParseError(""));

    [Fact]
    public void Non_utf8_file_is_rejected() =>
        Assert.Equal("file is not valid UTF-8", ParseError([.. Encoding.UTF8.GetBytes(Header), 0xff, (byte)'\n']));

    [Fact]
    public void Sha256_is_stable() => Assert.Equal(Parse(Header + Row).Sha256, Parse(Header + Row).Sha256);

    const string BlockHeader =
        "GENERATED_AT,KIND,WORK_DATE,TECH_ID,TECH_NAME,STARTS_AT,ENDS_AT,REF_ID,STATUS,DEPARTMENT,"
        + "REGION,SKILLS\n";
    const string ShiftRow =
        "\"2026-09-29T09:15:02-05\",\"shift\",\"2026-09-29\",\"jdoe0170\",\"Jane Doe\","
        + "\"2026-09-29 08:00\",\"2026-09-29 17:00\",\"\",\"\",\"\",\"North Core\",\"CONN, INS\"\n";
    const string JobRow =
        "\"2026-09-29T09:15:02-05\",\"job\",\"2026-09-29\",\"jdoe0170\",\"Jane Doe\","
        + "\"2026-09-29 09:00\",\"2026-09-29 11:00\",\"123456\",\"A\",\"FIELD\",\"\",\"\"\n";

    // JobRow with its trailing region and skills swapped for tail.
    static string Job(string tail) => JobRow.Replace("\"\",\"\"\n", tail);

    [Fact]
    public void Kind_column_selects_the_blocks_format()
    {
        var feed = Parse(BlockHeader + ShiftRow + JobRow);
        Assert.Equal("blocks", feed.Format);
        Assert.Empty(feed.Slots);
        Assert.Equal(2, feed.RowCount);
        var (shift, job) = (feed.Blocks[0], feed.Blocks[1]);
        Assert.Equal("shift", shift.Kind);
        Assert.Equal("North Core", shift.Region);
        Assert.Equal("123456", job.RefId);
        Assert.Equal("FIELD", job.Department);
        Assert.Equal(new DateTime(2026, 9, 29, 9, 0, 0), job.StartsAt);
    }

    [Fact]
    public void Legacy_header_stays_the_slots_format()
    {
        var feed = Parse(Header + Row);
        Assert.Equal("slots", feed.Format);
        Assert.Equal(1, feed.RowCount);
    }

    [Fact]
    public void Blocks_format_needs_all_its_columns() =>
        Assert.StartsWith("missing columns: tech_name", ParseError("GENERATED_AT,KIND,WORK_DATE,TECH_ID\n"));

    [Fact]
    public void Unknown_block_kind_is_rejected() =>
        Assert.Contains("unknown kind", ParseError(BlockHeader + ShiftRow.Replace("\"shift\"", "\"meeting\"")));

    [Fact]
    public void Moved_rows_keep_the_sentinel_start()
    {
        var row =
            "\"2026-09-29T09:15:02-05\",\"job_moved\",\"9999-12-31\",\"\",\"\","
            + "\"9999-12-31 00:00\",\"9999-12-31 00:00\",\"123456\",\"A\",\"FLDSVCCON\",\"\",\"\"\n";
        var block = Parse(BlockHeader + row).Blocks[0];
        Assert.Equal("job_moved", block.Kind);
        Assert.Equal(new DateTime(9999, 12, 31), block.StartsAt);
        Assert.Equal("", block.TechId);
    }

    [Fact]
    public void Department_is_optional_for_the_first_blocks_release()
    {
        var block = Parse(BlockHeader.Replace("DEPARTMENT,", "") + JobRow.Replace("\"FIELD\",", "")).Blocks[0];
        Assert.Equal("", block.Department);
    }

    [Fact]
    public void Task_type_is_read_when_present()
    {
        var header = BlockHeader.Replace("DEPARTMENT,", "DEPARTMENT,TASK_TYPE,");
        var row = JobRow.Replace("\"FIELD\",", "\"FIELD\",\"3\",");
        Assert.Equal("3", Parse(header + row).Blocks[0].TaskType);
    }

    [Fact]
    public void Trace_columns_are_read_and_blank_ones_are_none()
    {
        var header = BlockHeader.Replace(
            "SKILLS\n",
            "SKILLS,MODIFIED_AT,MODIFIED_BY,ENROUTE_AT,INPROGRESS_AT,Pre-Reqs Status\n"
        );
        var trace =
            "\"2026-09-29 10:15:30\",\"jsmi0171\",\"\",\"2026-09-29 09:05:00\","
            + "\"PreDrop - Legacy: C, PreBury - Legacy: A\"";
        var job = Job($"\"\",\"\",{trace}\n");
        var shift = ShiftRow.Replace("\"CONN, INS\"\n", "\"CONN, INS\",\"\",\"\",\"\",\"\",\"\"\n");
        var blocks = Parse(header + shift + job).Blocks;
        var (shiftBlock, jobBlock) = (blocks[0], blocks[1]);
        Assert.Equal(new DateTime(2026, 9, 29, 10, 15, 30), jobBlock.ModifiedAt);
        Assert.Equal("jsmi0171", jobBlock.ModifiedBy);
        Assert.Null(jobBlock.EnrouteAt);
        Assert.Equal(new DateTime(2026, 9, 29, 9, 5, 0), jobBlock.InprogressAt);
        Assert.Equal("PreDrop - Legacy: C, PreBury - Legacy: A", jobBlock.PrereqsStatus);
        Assert.Null(shiftBlock.ModifiedAt);
        Assert.Equal("", shiftBlock.PrereqsStatus);
    }

    [Fact]
    public void Mbs_rewritten_seconds_timestamp_is_accepted()
    {
        var header = BlockHeader.Replace("SKILLS\n", "SKILLS,MODIFIED_AT\n");
        var job = Job("\"\",\"\",\"09-29-2026 17:03:34\"\n");
        Assert.Equal(new DateTime(2026, 9, 29, 17, 3, 34), Parse(header + job).Blocks[0].ModifiedAt);
    }

    [Theory]
    [InlineData("\"Doe, Jane (jdoe0170)\"")]
    [InlineData("\"jdoe0170\"")]
    public void Modified_by_keeps_only_the_id_from_mbs_display_form(string shown)
    {
        var header = BlockHeader.Replace("SKILLS\n", "SKILLS,MODIFIED_BY\n");
        Assert.Equal("jdoe0170", Parse(header + Job($"\"\",\"\",{shown}\n")).Blocks[0].ModifiedBy);
    }

    [Fact]
    public void Unassigned_rows_carry_no_tech_and_their_own_region()
    {
        var row =
            "\"2026-09-29T09:15:02-05\",\"job_unassigned\",\"2026-09-30\",\"\",\"\","
            + "\"2026-09-30 10:00\",\"2026-09-30 12:00\",\"123456\",\"A\",\"FIELD\",\"North Core\",\"\"\n";
        var block = Parse(BlockHeader + row).Blocks[0];
        Assert.Equal("job_unassigned", block.Kind);
        Assert.Equal("", block.TechId);
        Assert.Equal("North Core", block.Region);
    }

    static readonly string AddressHeader = BlockHeader.Replace(
        "SKILLS\n",
        "SKILLS,ADDRESS_ISSUE,LATITUDE,LONGITUDE,GPS_PRECISION\n"
    );

    [Fact]
    public void Address_columns_are_read()
    {
        var job = Job("\"\",\"\",\"\",\"40.123456\",\"-100.654321\",\"ROOFTOP\"\n");
        var flagged = Job("\"\",\"\",\"no_gps\",\"\",\"\",\"\"\n");
        var shift = ShiftRow.Replace("\"CONN, INS\"\n", "\"CONN, INS\",\"\",\"\",\"\",\"\"\n");
        var blocks = Parse(AddressHeader + shift + job + flagged).Blocks;
        var (shiftBlock, jobBlock, flaggedBlock) = (blocks[0], blocks[1], blocks[2]);
        Assert.Equal((40.123456, -100.654321), (jobBlock.Latitude, jobBlock.Longitude));
        Assert.Equal("", jobBlock.AddressIssue);
        Assert.Equal("ROOFTOP", jobBlock.GpsPrecision);
        Assert.Equal("no_gps", flaggedBlock.AddressIssue);
        Assert.Null(flaggedBlock.Latitude);
        Assert.Equal("", shiftBlock.AddressIssue);
    }

    [Fact]
    public void Set_region_is_read_and_unknown_when_absent()
    {
        var header = BlockHeader.Replace("SKILLS\n", "SKILLS,SET_REGION\n");
        Assert.Equal("South", Parse(header + Job("\"\",\"\",\" South \"\n")).Blocks[0].SetRegion);
        Assert.Null(Parse(BlockHeader + JobRow).Blocks[0].SetRegion);
    }

    [Fact]
    public void Retired_gps_confidence_column_is_ignored()
    {
        var header = AddressHeader.Replace("GPS_PRECISION", "GPS_CONFIDENCE");
        var block = Parse(header + Job("\"\",\"\",\"\",\"40.123456\",\"-100.654321\",\"\"\n")).Blocks[0];
        Assert.Equal("", block.GpsPrecision);
        Assert.Equal(40.123456, block.Latitude);
    }

    [Fact]
    public void Zero_coordinates_mean_none()
    {
        var block = Parse(AddressHeader + Job("\"\",\"\",\"\",\"0\",\"0.0\",\"\"\n")).Blocks[0];
        Assert.Null(block.Latitude);
        Assert.Null(block.Longitude);
    }

    [Fact]
    public void Export_without_address_columns_leaves_the_issue_unknown() =>
        Assert.Null(Parse(BlockHeader + JobRow).Blocks[0].AddressIssue);

    [Fact]
    public void Bad_coordinate_is_rejected() =>
        Assert.Contains(
            "latitude",
            ParseError(AddressHeader + Job("\"\",\"\",\"\",\"north\",\"-92.3\",\"\"\n"))
        );

    [Fact]
    public void Tc_only_shift_kind_is_accepted() =>
        Assert.Equal("shift_tc", Parse(BlockHeader + ShiftRow.Replace("\"shift\"", "\"shift_tc\"")).Blocks[0].Kind);

    [Theory]
    [InlineData(" 1000.5 ", 1000.5)]
    [InlineData("1e2", 100.0)]
    [InlineData("-.5", -0.5)]
    [InlineData("-0.0", null)]
    public void Coordinates_are_invariant_numbers(string value, double? expected)
    {
        var block = Parse(AddressHeader + Job($"\"\",\"\",\"\",\"{value}\",\"\",\"\"\n")).Blocks[0];
        Assert.Equal(expected, block.Latitude);
    }

    [Theory]
    [InlineData("1__0")]
    [InlineData("_1")]
    [InlineData("1.2.3")]
    [InlineData("0x10")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Malformed_or_non_finite_coordinates_are_rejected(string value) =>
        Assert.Equal(
            $"line 2: bad latitude '{value}'",
            ParseError(AddressHeader + Job($"\"\",\"\",\"\",\"{value}\",\"\",\"\"\n"))
        );
}
