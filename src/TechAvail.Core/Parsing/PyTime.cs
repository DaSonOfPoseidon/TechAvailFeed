using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TechAvail.Core.Parsing;

// datetime.strptime and datetime.fromisoformat as Python 3.12 apply them to feed values, so the
// port accepts and rejects exactly what the Python ingest did.
internal static class PyTime
{
    // _strptime's patterns for the directives the feed formats use. Python matches from the start
    // and, without backtracking into an earlier alternative, fails if anything is left over.
    static readonly Dictionary<char, string> Directives = new()
    {
        ['Y'] = @"\d\d\d\d",
        ['m'] = @"1[0-2]|0[1-9]|[1-9]",
        ['d'] = @"3[01]|[12]\d|0[1-9]|[1-9]| [1-9]",
        ['H'] = @"2[0-3]|[0-1]\d|\d",
        ['I'] = @"1[0-2]|0[1-9]|[1-9]",
        ['M'] = @"[0-5]\d|\d",
        ['S'] = @"6[0-1]|[0-5]\d|\d",
        ['p'] = "am|pm",
    };

    static readonly Dictionary<string, Regex> Compiled = [];

    static Regex Pattern(string format)
    {
        lock (Compiled)
        {
            if (Compiled.TryGetValue(format, out var regex))
                return regex;
            var pattern = new StringBuilder(@"\G");
            for (int i = 0; i < format.Length; i++)
            {
                if (format[i] == '%')
                {
                    var name = format[++i];
                    pattern.Append($"(?<{name}>{Directives[name]})");
                }
                else if (char.IsWhiteSpace(format[i]))
                {
                    while (i + 1 < format.Length && char.IsWhiteSpace(format[i + 1]))
                        i++;
                    pattern.Append(@"\s+");
                }
                else
                    pattern.Append(Regex.Escape(format[i].ToString()));
            }
            regex = new Regex(pattern.ToString(), RegexOptions.IgnoreCase);
            Compiled[format] = regex;
            return regex;
        }
    }

    public static bool TryStrptime(string value, string format, out DateTime result)
    {
        result = default;
        var match = Pattern(format).Match(value);
        if (!match.Success || match.Length != value.Length)
            return false;
        int Get(string name, int fallback) =>
            match.Groups[name].Success ? Number(match.Groups[name].Value) : fallback;
        var hour = Get("H", 0);
        if (match.Groups["I"].Success)
        {
            hour = Get("I", 0);
            var pm = match.Groups["p"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase);
            hour = pm ? (hour == 12 ? 12 : hour + 12) : (hour == 12 ? 0 : hour);
        }
        try
        {
            result = new DateTime(Get("Y", 1900), Get("m", 1), Get("d", 1), hour, Get("M", 0), Get("S", 0));
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    // \d also matches non-ASCII digits, which int() accepts too.
    static int Number(string digits)
    {
        int value = 0;
        foreach (var c in digits.Trim())
            value = value * 10 + (int)char.GetNumericValue(c);
        return value;
    }

    // The forms of datetime.fromisoformat a report timestamp can take: a date, optionally followed by
    // any one separator character and HH[:MM[:SS[.f]]] (or HHMM[SS]), then Z or +HH[:MM] / +HHMM.
    // A value without an offset is naive in Python; it's returned as UTC, which is what Postgres
    // stored it as. Week dates, offsets with seconds and other rarer ISO forms are not accepted.
    static readonly Regex Iso = new(
        @"^(?<date>\d{4}-\d{2}-\d{2}|\d{8})"
            + @"(?:.(?<time>(?<h>\d{2})(?::?(?<mi>\d{2})(?::?(?<s>\d{2})(?:[.,](?<f>\d+))?)?)?)"
            + @"(?<tz>Z|[+-]\d{2}(?::?\d{2})?)?)?$",
        RegexOptions.Singleline
    );

    public static bool TryFromIsoFormat(string value, out DateTimeOffset result)
    {
        result = default;
        var match = Iso.Match(value);
        if (!match.Success || value.Any(c => c > '\x7f' && char.IsDigit(c)))
            return false;
        var date = match.Groups["date"].Value.Replace("-", "");
        int Part(string text, int start, int length) =>
            int.Parse(text.AsSpan(start, length), CultureInfo.InvariantCulture);
        int Group(string name) => match.Groups[name].Success ? int.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture) : 0;
        var fraction = match.Groups["f"].Success ? match.Groups["f"].Value : "";
        // Python keeps microseconds and truncates anything finer.
        var micros = fraction.Length == 0 ? 0 : int.Parse(fraction.PadRight(6, '0')[..6], CultureInfo.InvariantCulture);
        try
        {
            var local = new DateTime(
                Part(date, 0, 4),
                Part(date, 4, 2),
                Part(date, 6, 2),
                Group("h"),
                Group("mi"),
                Group("s")
            ).AddTicks(micros * 10L);
            result = new DateTimeOffset(local, Offset(match.Groups["tz"].Value));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    static TimeSpan Offset(string tz)
    {
        if (tz is "" or "Z")
            return TimeSpan.Zero;
        var digits = tz[1..].Replace(":", "");
        var hours = int.Parse(digits[..2], CultureInfo.InvariantCulture);
        var minutes = digits.Length >= 4 ? int.Parse(digits[2..4], CultureInfo.InvariantCulture) : 0;
        if (hours > 23 || minutes > 59)
            throw new ArgumentException("offset out of range");
        var offset = new TimeSpan(hours, minutes, 0);
        return tz[0] == '-' ? -offset : offset;
    }
}
