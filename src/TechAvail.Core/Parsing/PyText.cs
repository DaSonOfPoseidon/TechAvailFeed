using System.Globalization;
using System.Text;

namespace TechAvail.Core.Parsing;

// Python str semantics where the port has to match the original exactly: what counts as
// whitespace for strip(), and repr() of a value quoted in an error message.
internal static class PyText
{
    // str.isspace() also counts the ASCII separators \x1c-\x1f, which char.IsWhiteSpace doesn't.
    public static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';

    public static string Strip(string value)
    {
        int start = 0, end = value.Length;
        while (start < end && IsSpace(value[start]))
            start++;
        while (end > start && IsSpace(value[end - 1]))
            end--;
        return value[start..end];
    }

    public static string Repr(string value)
    {
        var quote = value.Contains('\'') && !value.Contains('"') ? '"' : '\'';
        var text = new StringBuilder().Append(quote);
        foreach (var rune in value.EnumerateRunes())
        {
            var c = rune.Value;
            if (c == '\\' || c == quote)
                text.Append('\\').Append((char)c);
            else if (c == '\t')
                text.Append("\\t");
            else if (c == '\n')
                text.Append("\\n");
            else if (c == '\r')
                text.Append("\\r");
            else if (IsPrintable(rune))
                text.Append(rune.ToString());
            else if (c <= 0xff)
                text.Append($"\\x{c:x2}");
            else if (c <= 0xffff)
                text.Append($"\\u{c:x4}");
            else
                text.Append($"\\U{c:x8}");
        }
        return text.Append(quote).ToString();
    }

    public static string Repr(IEnumerable<string> values) =>
        "[" + string.Join(", ", values.Select(Repr)) + "]";

    // str.isprintable(): everything but control, format, surrogate, private-use, unassigned and
    // separator characters, except the plain space.
    static bool IsPrintable(Rune rune) =>
        rune.Value == ' '
        || Rune.GetUnicodeCategory(rune)
            is not (
                UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.Surrogate
                or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned
                or UnicodeCategory.SpaceSeparator
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator
            );
}
