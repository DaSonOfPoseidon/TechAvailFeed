using TechAvail.Core.Parsing;

namespace TechAvail.Core.Tests;

// Expected values are what CPython 3.12 prints for the same input.
public class PyTextTests
{
    [Theory]
    [InlineData("  a b \t\r\n", "a b")]
    [InlineData("\u001c\u001fa\u001e", "a")]
    [InlineData("\u00a0a\u2003", "a")]
    [InlineData("   ", "")]
    public void Strip_matches_python(string value, string expected) =>
        Assert.Equal(expected, PyText.Strip(value));

    [Theory]
    [InlineData("abc", "'abc'")]
    [InlineData("it's", "\"it's\"")]
    [InlineData("'\"", "'\\'\"'")]
    [InlineData("a\\b", "'a\\\\b'")]
    [InlineData("a\tb\nc\r", "'a\\tb\\nc\\r'")]
    [InlineData("\u0001\u007f\u00a0", "'\\x01\\x7f\\xa0'")]
    [InlineData("\u200b\u2028", "'\\u200b\\u2028'")]
    [InlineData("é ü", "'é ü'")]
    public void Repr_matches_python(string value, string expected) =>
        Assert.Equal(expected, PyText.Repr(value));

    [Fact]
    public void Repr_of_a_list_matches_python() =>
        Assert.Equal("['a@x', \"b'c\"]", PyText.Repr(["a@x", "b'c"]));
}
