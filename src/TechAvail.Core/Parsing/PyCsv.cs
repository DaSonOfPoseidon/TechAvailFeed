using System.Text;

namespace TechAvail.Core.Parsing;

public sealed class CsvException(string message) : Exception(message);

// CPython's csv.reader(io.StringIO(text, newline=""), strict=True) with the default excel dialect,
// ported state for state so rows, blank lines and errors (with their messages) come out the same.
internal static class PyCsv
{
    public const int FieldSizeLimit = 131072;

    enum State
    {
        StartRecord,
        StartField,
        InField,
        InQuotedField,
        QuoteInQuotedField,
        EatCrnl,
    }

    public static IEnumerable<List<string>> Read(string text)
    {
        var state = State.StartRecord;
        var fields = new List<string>();
        var field = new StringBuilder();
        foreach (var line in Lines(text))
        {
            foreach (var c in line)
                state = Process(c, state, fields, field);
            // CPython feeds an end-of-line marker after each line's own characters.
            state = Process(null, state, fields, field);
            if (state == State.StartRecord)
            {
                yield return fields;
                fields = [];
            }
        }
        if (state != State.StartRecord)
            throw new CsvException("unexpected end of data");
    }

    // io.StringIO(newline="") splits after "\n", "\r\n" or a lone "\r" and keeps the ending.
    static IEnumerable<string> Lines(string text)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' || (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')))
            {
                yield return text[start..(i + 1)];
                start = i + 1;
            }
        }
        if (start < text.Length)
            yield return text[start..];
    }

    // c is null for the end-of-line marker.
    static State Process(char? c, State state, List<string> fields, StringBuilder field)
    {
        switch (state)
        {
            case State.StartRecord:
                if (c is null)
                    return State.StartRecord;
                if (c is '\n' or '\r')
                    return State.EatCrnl;
                return Process(c, State.StartField, fields, field);

            case State.StartField:
                if (c is null or '\n' or '\r')
                {
                    Save(fields, field);
                    return c is null ? State.StartRecord : State.EatCrnl;
                }
                if (c == '"')
                    return State.InQuotedField;
                if (c == ',')
                {
                    Save(fields, field);
                    return State.StartField;
                }
                Add(field, c.Value);
                return State.InField;

            case State.InField:
                if (c is null or '\n' or '\r')
                {
                    Save(fields, field);
                    return c is null ? State.StartRecord : State.EatCrnl;
                }
                if (c == ',')
                {
                    Save(fields, field);
                    return State.StartField;
                }
                Add(field, c.Value);
                return State.InField;

            case State.InQuotedField:
                // A newline inside quotes was already added as an ordinary character.
                if (c is null)
                    return State.InQuotedField;
                if (c == '"')
                    return State.QuoteInQuotedField;
                Add(field, c.Value);
                return State.InQuotedField;

            case State.QuoteInQuotedField:
                if (c == '"')
                {
                    Add(field, '"');
                    return State.InQuotedField;
                }
                if (c == ',')
                {
                    Save(fields, field);
                    return State.StartField;
                }
                if (c is null or '\n' or '\r')
                {
                    Save(fields, field);
                    return c is null ? State.StartRecord : State.EatCrnl;
                }
                throw new CsvException("',' expected after '\"'");

            case State.EatCrnl:
                if (c is '\n' or '\r')
                    return State.EatCrnl;
                if (c is null)
                    return State.StartRecord;
                throw new CsvException(
                    "new-line character seen in unquoted field - do you need to open the file with newline=''?"
                );
        }
        throw new InvalidOperationException($"unknown state {state}");
    }

    static void Add(StringBuilder field, char c)
    {
        if (field.Length >= FieldSizeLimit)
            throw new CsvException($"field larger than field limit ({FieldSizeLimit})");
        field.Append(c);
    }

    static void Save(List<string> fields, StringBuilder field)
    {
        fields.Add(field.ToString());
        field.Clear();
    }
}
