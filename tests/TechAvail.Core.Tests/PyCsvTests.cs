using System.Text.Json;
using TechAvail.Core.Parsing;

namespace TechAvail.Core.Tests;

// Expected rows (as JSON) and error messages are CPython 3.12's output for
// csv.reader(io.StringIO(text, newline=""), strict=True).
public class PyCsvTests
{
    [Theory]
    [InlineData("a,b\nc,d\n", "[[\"a\", \"b\"], [\"c\", \"d\"]]")]
    [InlineData("a,b", "[[\"a\", \"b\"]]")]
    [InlineData("a,b\r\nc\r\n", "[[\"a\", \"b\"], [\"c\"]]")]
    [InlineData("a\rb\r", "[[\"a\"], [\"b\"]]")]
    [InlineData("\n\na\n\n", "[[], [], [\"a\"], []]")]
    [InlineData("", "[]")]
    [InlineData("\"x,y\",z\n", "[[\"x,y\", \"z\"]]")]
    [InlineData("\"multi\nline\",2\n", "[[\"multi\\nline\", \"2\"]]")]
    [InlineData("\"multi\r\nline\"\r\n", "[[\"multi\\r\\nline\"]]")]
    [InlineData("\"a\"\"b\"\n", "[[\"a\\\"b\"]]")]
    [InlineData("a\"b,c\n", "[[\"a\\\"b\", \"c\"]]")]
    [InlineData(",\n", "[[\"\", \"\"]]")]
    [InlineData(",,\n", "[[\"\", \"\", \"\"]]")]
    [InlineData(" \"a\",b\n", "[[\" \\\"a\\\"\", \"b\"]]")]
    [InlineData("\"\"\n", "[[\"\"]]")]
    [InlineData("a,b\n\r\n", "[[\"a\", \"b\"], []]")]
    public void Rows_match_python(string text, string expected) =>
        Assert.Equal(
            JsonSerializer.Deserialize<List<List<string>>>(expected),
            PyCsv.Read(text).ToList()
        );

    [Theory]
    [InlineData("\"unterminated\n", "unexpected end of data")]
    [InlineData("\"a\"b\n", "',' expected after '\"'")]
    [InlineData("a,\"b\"\"\n", "unexpected end of data")]
    public void Errors_match_python(string text, string message) =>
        Assert.Equal(message, Assert.Throws<CsvException>(() => PyCsv.Read(text).ToList()).Message);

    [Fact]
    public void Field_size_limit_matches_python()
    {
        var at = new string('x', PyCsv.FieldSizeLimit);
        Assert.Single(PyCsv.Read(at + "\n"));
        var error = Assert.Throws<CsvException>(() => PyCsv.Read(at + "x\n").ToList());
        Assert.Equal("field larger than field limit (131072)", error.Message);
    }

    [Fact]
    public void Rows_before_an_error_are_still_yielded()
    {
        using var rows = PyCsv.Read("a\n\"b").GetEnumerator();
        Assert.True(rows.MoveNext());
        Assert.Equal(["a"], rows.Current);
        Assert.Throws<CsvException>(() => rows.MoveNext());
    }
}
