namespace TechAvail.Data.Tests;

// Port of the diff_blocks tests in tests/test_store.py.
public class BlockDiffTests
{
    static BlockKey Block(string reference, string status = "A") =>
        new(
            "job",
            new DateOnly(2026, 10, 6),
            "t1",
            "T1",
            new DateTime(2026, 10, 6, 9, 0, 0),
            new DateTime(2026, 10, 6, 11, 0, 0),
            reference,
            status,
            "FIELD",
            "North",
            "",
            "",
            null,
            "",
            null,
            null,
            "",
            null,
            null,
            null,
            ""
        );

    static (List<BlockKey>, List<long>) Diff((long, BlockKey)[] current, params BlockKey[] next) =>
        BlockDiff.Diff(current, next);

    [Fact]
    public void Unchanged_snapshot_writes_nothing()
    {
        var (insert, close) = Diff([(1, Block("a")), (2, Block("b"))], Block("b"), Block("a"));
        Assert.Empty(insert);
        Assert.Empty(close);
    }

    [Fact]
    public void Changed_field_closes_old_row_and_inserts_new()
    {
        var done = Block("a", status: "C");
        var (insert, close) = Diff([(1, Block("a")), (2, Block("b"))], done, Block("b"));
        Assert.Equal([done], insert);
        Assert.Equal([1L], close);
    }

    [Fact]
    public void Removed_and_added_rows()
    {
        var (insert, close) = Diff([(1, Block("a"))], Block("b"));
        Assert.Equal([Block("b")], insert);
        Assert.Equal([1L], close);
    }

    [Fact]
    public void First_snapshot_inserts_everything()
    {
        var (insert, close) = Diff([], Block("a"), Block("b"));
        Assert.Equal([Block("a"), Block("b")], insert);
        Assert.Empty(close);
    }

    [Fact]
    public void Duplicate_rows_count_as_copies()
    {
        var a = Block("a");
        var (insert, close) = Diff([(1, a)], a, a);
        Assert.Equal([a], insert);
        Assert.Empty(close);
        (insert, close) = Diff([(1, a), (2, a)], a);
        Assert.Empty(insert);
        Assert.Equal([2L], close);
    }

    [Fact]
    public void None_and_default_differ()
    {
        // A row read back raw must match exactly, so a NULL never silently equals "".
        var stored = Block("a") with { Department = null };
        var (insert, close) = Diff([(1, stored)], Block("a"));
        Assert.Equal([Block("a")], insert);
        Assert.Equal([1L], close);
    }
}
