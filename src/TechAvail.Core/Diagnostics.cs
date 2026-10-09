using TechAvail.Core.Parsing;

namespace TechAvail.Core;

public sealed class Check(string id, string group, string title, string severity, string description)
{
    public string Id { get; } = id;
    public string Group { get; } = group;
    public string Title { get; } = title;
    public string Severity { get; } = severity; // "warning" or "info"
    public string Description { get; } = description;
    public bool Available { get; init; } = true;

    // Each row's fields in display order; values are strings, numbers, dates and timestamps.
    public List<OrderedDictionary<string, object?>> Rows { get; set; } = [];
}

// Data-quality checks over one snapshot's blocks. Rows name the job
// or tech to fix; they never carry coordinates.
public static class Diagnostics
{
    public static readonly string[] Groups = ["tech_setup", "scheduling", "stale", "address"];
    static readonly string[] Assigned = ["job", "ticket"];
    static readonly string[] UnassignedKinds = ["job_unassigned", "ticket_unassigned"];
    static readonly string[] ShiftKinds = [.. Availability.Calendars.Values];

    // Trouble tickets may share a slot two at a time, as long as both are in the same region.
    // Any overlap involving a job is a conflict.
    const int TicketsPerSlot = 2;

    // "install", "tc", or "both" for a dual tech whose schedules are on both calendars.
    public static string CalendarOf(IEnumerable<string> shiftKinds)
    {
        var kinds = shiftKinds.ToHashSet();
        var names = Availability.Calendars.Where(c => kinds.Contains(c.Value)).Select(c => c.Key).ToList();
        return names.Count == 1 ? names[0] : "both";
    }

    static int Minutes(IEnumerable<(DateTime Start, DateTime End)> intervals) =>
        (int)Math.Round(intervals.Sum(i => (i.End - i.Start).TotalSeconds) / 60, MidpointRounding.ToEven);

    static OrderedDictionary<string, object?> WorkRow(Block b, params (string, object?)[] extra)
    {
        var row = new OrderedDictionary<string, object?>
        {
            ["ref_id"] = b.RefId,
            ["kind"] = b.Kind,
            ["status"] = b.Status,
            ["work_date"] = b.WorkDate,
            ["starts_at"] = b.StartsAt,
            ["ends_at"] = b.EndsAt,
            ["tech_id"] = b.TechId,
            ["tech_name"] = b.TechName,
            ["region"] = b.Region,
        };
        foreach (var (key, value) in extra)
            row[key] = value;
        return row;
    }

    static IOrderedEnumerable<Block> ByTime(IEnumerable<Block> blocks) =>
        blocks
            .OrderBy(b => b.WorkDate)
            .ThenBy(b => b.StartsAt)
            .ThenBy(b => b.RefId, StringComparer.Ordinal)
            .ThenBy(b => b.TechId, StringComparer.Ordinal);

    static bool In(IReadOnlyDictionary<string, string[]> statuses, string kind, string status) =>
        statuses.TryGetValue(kind, out var list) && list.Contains(status);

    // Not canceled/closed: the same statuses FreeSlots ignores.
    static bool IsLive(Block b) => !In(Availability.NotBusy, Outcomes.BaseKind(b.Kind), b.Status);

    static bool IsDone(Block b)
    {
        var kind = Outcomes.BaseKind(b.Kind);
        return Outcomes.CompletedStatuses[kind].Contains(b.Status) || Outcomes.CanceledStatuses[kind].Contains(b.Status);
    }

    static List<Check> TechSetup(List<Block> shifts)
    {
        var names = new Dictionary<string, string>();
        var kinds = new Dictionary<string, HashSet<string>>();
        foreach (var b in shifts)
        {
            names[b.TechId] = b.TechName;
            if (!kinds.TryGetValue(b.TechId, out var set))
                kinds[b.TechId] = set = [];
            set.Add(b.Kind);
        }
        var calendars = kinds.ToDictionary(k => k.Key, k => CalendarOf(k.Value));
        var noRegion = new Dictionary<string, List<DateOnly>>();
        var regions = new Dictionary<string, Dictionary<string, int>>();
        var noSkills = new Dictionary<string, string>();
        var seen = new HashSet<(string, DateOnly)>();
        foreach (
            var b in shifts.OrderBy(b => b.TechId, StringComparer.Ordinal).ThenBy(b => b.WorkDate).ThenBy(b => b.StartsAt)
        )
        {
            if (!seen.Add((b.TechId, b.WorkDate)))
                continue;
            if (b.Region.Length > 0)
            {
                if (!regions.TryGetValue(b.TechId, out var counts))
                    regions[b.TechId] = counts = [];
                counts[b.Region] = counts.GetValueOrDefault(b.Region) + 1;
            }
            else
            {
                if (!noRegion.TryGetValue(b.TechId, out var days))
                    noRegion[b.TechId] = days = [];
                days.Add(b.WorkDate);
            }
            if (string.IsNullOrWhiteSpace(b.Skills))
                noSkills.TryAdd(b.TechId, b.Region);
        }

        OrderedDictionary<string, object?> Tech(string techId, params (string, object?)[] extra)
        {
            var row = new OrderedDictionary<string, object?>
            {
                ["tech_id"] = techId,
                ["tech_name"] = names[techId],
                ["calendar"] = calendars[techId],
            };
            foreach (var (key, value) in extra)
                row[key] = value;
            return row;
        }

        IEnumerable<string> ByName(IEnumerable<string> ids) =>
            ids.OrderBy(t => names[t], StringComparer.Ordinal).ThenBy(t => t, StringComparer.Ordinal);

        return
        [
            new Check(
                "tech_no_region",
                "tech_setup",
                "Techs with shifts but no primary region",
                "warning",
                "Shift days whose schedule has no primary department/region row, so their capacity "
                    + "lands under no region."
            )
            {
                Rows =
                [
                    .. ByName(noRegion.Keys)
                        .Select(t =>
                            Tech(t, ("days", noRegion[t].Count), ("first_date", noRegion[t][0]), ("last_date", noRegion[t][^1]))
                        ),
                ],
            },
            new Check(
                "tech_no_skills",
                "tech_setup",
                "Techs with no skills",
                "warning",
                "Rostered techs with no proficient skill, so no skill filter ever includes them."
            )
            {
                Rows = [.. ByName(noSkills.Keys).Select(t => Tech(t, ("region", noSkills[t])))],
            },
            new Check(
                "tech_region_varies",
                "tech_setup",
                "Techs whose primary region changes between days",
                "info",
                "Can be a deliberate split schedule; worth a look when it isn't."
            )
            {
                Rows =
                [
                    .. ByName(regions.Keys.Where(t => regions[t].Count > 1))
                        .Select(t =>
                            Tech(
                                t,
                                (
                                    "regions",
                                    string.Join(
                                        ", ",
                                        regions[t]
                                            .OrderBy(r => r.Key, StringComparer.Ordinal)
                                            .ThenBy(r => r.Value)
                                            .Select(r => $"{r.Key} ({r.Value})")
                                    )
                                )
                            )
                        ),
                ],
            },
        ];
    }

    // first starts no later than second, and they overlap. null means the overlap is allowed.
    static string? OverlapReason(Block first, Block second, List<Block> tickets)
    {
        if (first.Kind != "ticket" || second.Kind != "ticket")
            return "job_overlap";
        if (first.Region.Length == 0 || second.Region.Length == 0)
            return "ticket_region_unknown";
        if (first.Region != second.Region)
            return "ticket_regions_differ";
        var at = second.StartsAt;
        var inSlot = tickets.Where(t => t.StartsAt <= at && at < t.EndsAt).Select(t => t.RefId).ToHashSet();
        return inSlot.Count > TicketsPerSlot ? "more_than_2_tickets" : null;
    }

    static List<Check> Scheduling(List<Block> blocks, DateOnly today)
    {
        var shifts = new Dictionary<(string, DateOnly), List<(DateTime, DateTime)>>();
        var leave = new Dictionary<string, List<(DateTime, DateTime)>>();
        var work = new OrderedDictionary<string, List<Block>>();
        foreach (var b in blocks)
        {
            // Either calendar's shift covers the tech's work: TC tickets on a TC shift are fine.
            if (ShiftKinds.Contains(b.Kind))
                Append(shifts, (b.TechId, b.WorkDate), (b.StartsAt, b.EndsAt));
            else if (b.Kind == "time_off")
                Append(leave, b.TechId, (b.StartsAt, b.EndsAt));
            else if (Assigned.Contains(b.Kind) && b.WorkDate >= today && Availability.IsBusy(b))
            {
                if (!work.TryGetValue(b.TechId, out var list))
                    work.Add(b.TechId, list = []);
                list.Add(b);
            }
        }

        // Who is on each job, so a co-assigned person without a schedule (a trainee riding along)
        // isn't reported as work nobody is scheduled for.
        var crew = new Dictionary<(string, string, DateOnly), List<Block>>();
        foreach (var items in work.Values)
            foreach (var b in items)
                Append(crew, (b.Kind, b.RefId, b.WorkDate), b);

        List<Block> ScheduledPartners(Block b) =>
            [.. crew[(b.Kind, b.RefId, b.WorkDate)].Where(p => p.TechId != b.TechId && shifts.ContainsKey((p.TechId, b.WorkDate)))];

        List<OrderedDictionary<string, object?>> without = [], rideAlong = [], outside = [], during = [], doubles = [];
        foreach (var (techId, techWork) in work)
        {
            var off = Availability.Merge(leave.GetValueOrDefault(techId, []));
            // Repeated copies of the same work count once: the first one's place, the last one's values.
            var unique = new OrderedDictionary<(string, DateTime, DateTime), Block>();
            foreach (var b in techWork)
                unique[(b.RefId, b.StartsAt, b.EndsAt)] = b;
            var items = ByTime(unique.Values).ToList();
            foreach (var b in items)
            {
                List<(DateTime Start, DateTime End)> span = [(b.StartsAt, b.EndsAt)];
                var onLeave = Minutes(Availability.Subtract(span, Availability.Subtract(span, off)));
                var working = Availability.Merge(shifts.GetValueOrDefault((techId, b.WorkDate), []));
                if (onLeave != 0)
                    during.Add(WorkRow(b, ("minutes_on_time_off", onLeave)));
                else if (working.Count == 0)
                {
                    var partners = ScheduledPartners(b);
                    if (partners.Count > 0)
                    {
                        var with = string.Join(", ", partners.Select(p => p.TechName).Distinct().Order(StringComparer.Ordinal));
                        rideAlong.Add(WorkRow(b, ("with", with)));
                    }
                    else
                        without.Add(WorkRow(b));
                }
                else if (Minutes(Availability.Subtract(span, working)) is var outsideMinutes and not 0)
                    outside.Add(WorkRow(b, ("minutes_outside", outsideMinutes)));
            }
            var tickets = items.Where(b => b.Kind == "ticket").ToList();
            for (int i = 0; i < items.Count; i++)
            {
                var first = items[i];
                foreach (var second in items.Skip(i + 1))
                {
                    if (second.StartsAt >= first.EndsAt)
                        break;
                    if (second.RefId == first.RefId)
                        continue;
                    var reason = OverlapReason(first, second, tickets);
                    if (reason is null)
                        continue;
                    var overlap = (first.EndsAt < second.EndsAt ? first.EndsAt : second.EndsAt) - second.StartsAt;
                    doubles.Add(
                        WorkRow(
                            first,
                            ("other_ref_id", second.RefId),
                            ("other_kind", second.Kind),
                            ("other_starts_at", second.StartsAt),
                            ("other_ends_at", second.EndsAt),
                            ("other_region", second.Region),
                            ("overlap_minutes", (int)Math.Round(overlap.TotalSeconds / 60, MidpointRounding.ToEven)),
                            ("reason", reason)
                        )
                    );
                }
            }
        }

        static List<OrderedDictionary<string, object?>> Ordered(List<OrderedDictionary<string, object?>> rows) =>
        [
            .. rows.OrderBy(r => (DateOnly)r["work_date"]!)
                .ThenBy(r => (DateTime)r["starts_at"]!)
                .ThenBy(r => (string)r["ref_id"]!, StringComparer.Ordinal),
        ];

        return
        [
            new Check(
                "work_without_shift",
                "scheduling",
                "Work on a day the tech has no shift",
                "warning",
                "Live jobs and tickets, today on, booked to a tech with no shift that day (and no "
                    + "time off over it), with no scheduled tech on the same job. They take no capacity, so "
                    + "the calendar overstates free time."
            )
            {
                Rows = Ordered(without),
            },
            new Check(
                "ride_along",
                "scheduling",
                "Riding along without a schedule",
                "info",
                "Work for a tech with no shift that day, on a job a scheduled tech is also assigned to "
                    + "(e.g. a trainee). Expected; listed so it stays visible."
            )
            {
                Rows = Ordered(rideAlong),
            },
            new Check(
                "work_outside_shift",
                "scheduling",
                "Work running outside the tech's shift",
                "warning",
                "Live work, today on, that starts before or ends after the tech's shift."
            )
            {
                Rows = Ordered(outside),
            },
            new Check(
                "work_during_time_off",
                "scheduling",
                "Work during the tech's time off",
                "warning",
                "Live work, today on, overlapping the tech's time off."
            )
            {
                Rows = Ordered(during),
            },
            new Check(
                "double_booked",
                "scheduling",
                "Double-booked techs",
                "warning",
                "Two different live jobs or tickets for one tech whose times overlap. Two tickets may "
                    + "share a slot if both are in the same region; a third, or any job, is a conflict."
            )
            {
                Rows = Ordered(doubles),
            },
        ];
    }

    static void Append<TKey, TValue>(Dictionary<TKey, List<TValue>> groups, TKey key, TValue value)
        where TKey : notnull
    {
        if (!groups.TryGetValue(key, out var list))
            groups[key] = list = [];
        list.Add(value);
    }

    static List<Check> Stale(List<Block> blocks, DateOnly today)
    {
        var past = blocks.Where(b =>
            (Assigned.Contains(b.Kind) || UnassignedKinds.Contains(b.Kind)) && b.WorkDate < today && !IsDone(b)
        );
        var noRegion = blocks.Where(b => UnassignedKinds.Contains(b.Kind) && IsLive(b) && b.Region.Length == 0);
        return
        [
            new Check(
                "past_open_work",
                "stale",
                "Past work still open",
                "warning",
                "Jobs and tickets dated before today that were neither completed nor canceled."
            )
            {
                Rows = [.. ByTime(past).Select(b => WorkRow(b))],
            },
            new Check(
                "unassigned_no_region",
                "stale",
                "Unassigned work with no region",
                "warning",
                "Live work with no tech and no region, so it counts against no region's capacity."
            )
            {
                Rows = [.. ByTime(noRegion).Select(b => WorkRow(b))],
            },
        ];
    }

    static Check Address(List<Block> blocks, DateOnly today)
    {
        var work = blocks.Where(b => Assigned.Contains(b.Kind) || UnassignedKinds.Contains(b.Kind)).ToList();
        var flagged = work.Where(b => b.WorkDate >= today && IsLive(b) && !string.IsNullOrEmpty(b.AddressIssue));
        return new Check(
            "work_address_issue",
            "address",
            "Work with an address problem",
            "warning",
            "Live work, today on, whose service address is missing, not found, has no city or GPS, "
                + "or has a geocode no operating region covers."
        )
        {
            Available = work.Any(b => b.AddressIssue is not null),
            Rows =
            [
                .. flagged
                    .OrderBy(b => b.AddressIssue, StringComparer.Ordinal)
                    .ThenBy(b => b.WorkDate)
                    .ThenBy(b => b.StartsAt)
                    .ThenBy(b => b.RefId, StringComparer.Ordinal)
                    .ThenBy(b => b.TechId, StringComparer.Ordinal)
                    .Select(b => WorkRow(b, ("issue", b.AddressIssue))),
            ],
        };
    }

    // today is local. Scheduling and address checks look from today on; stale ones before it.
    public static List<Check> Diagnose(IReadOnlyList<Block> blocks, DateOnly today)
    {
        var shifts = blocks.Where(b => ShiftKinds.Contains(b.Kind)).ToList();
        // The install shift's region wins when a tech has both.
        var regions = new Dictionary<(string, DateOnly), string>();
        foreach (var b in shifts.Where(b => b.Kind == "shift_tc"))
            regions[(b.TechId, b.WorkDate)] = b.Region;
        foreach (var b in shifts.Where(b => b.Kind == "shift"))
            regions[(b.TechId, b.WorkDate)] = b.Region;
        // A job without its own region (unmapped, or an older snapshot) falls back to the tech's
        // shift region that day; a ticket doesn't, so the slot rule never trusts a borrowed region.
        var resolved = blocks
            .Select(b =>
                b.Kind == "job" && b.Region.Length == 0 ? b with { Region = regions.GetValueOrDefault((b.TechId, b.WorkDate), "") } : b
            )
            .ToList();
        return [.. TechSetup(shifts), .. Scheduling(resolved, today), .. Stale(resolved, today), Address(resolved, today)];
    }
}
