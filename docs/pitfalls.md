# Pitfalls

Classes of bug that reviews have caught here. Check every change against them, and add an entry when a review finds a new class.

- **Classifying a rounded double.** `Math.Round(share * 100)` on a ratio like 29/200 sees 14.499…, not 14.5, so a
  cell shown as 15% can be classified below 15%. When a rule must match a displayed rounded value, round the exact
  integers (`(200 * free + bookable) / (2 * bookable)`), not the double.

- **Cloudflare caches by file extension.** A URL ending in `.xlsx` (or another static-looking extension) is cached
  at the edge unless the response says otherwise, so an export can go stale behind the tunnel while localhost looks
  fine. Everything under `/api` sends `Cache-Control: no-store`; keep it that way for new routes.

- **Clipping a container hides its focus ring.** `overflow: hidden` on an element whose link fills it cuts off the
  global `:focus-visible` outline, so keyboard users lose the focus indicator. Round or shape decorations themselves
  instead of clipping their container (calendar level bar, #32).

- **Install rules applied to trouble calls.** Install has no Sunday booking windows, but TC shifts and tickets
  run every day. A rule that drops or zeroes a day because nothing happens then has to hold for the TC calendar
  and for tickets too, or depend on the data rather than the date (the Trends Sunday filter, #34).

- **Dropping a renamed region from a mapping.** Reports serve past snapshots too, so an old region name still
  arrives after MBS renames it. When a hardcoded grouping (`StatusUpdate.Regions`, `InstallAvailability.Areas`)
  follows a rename, keep the old name as an alias beside the new one, or older reports fall into Unmapped (#39).

- **Counting a repeated feed row twice.** The feed can send the same row more than once. Anything that counts
  work (jobs, tickets, unassigned demand) dedupes by `(Kind, RefId, StartsAt, EndsAt)` first, as
  `Availability.Windows` does, or one job reads as two (#45).

- **Filtering by range when the dates have gaps.** The capacity export skips an unworked Sunday, so
  `WorkDate <= to` still lets that Sunday's rows in. Filter by the export's date list, not its bounds (#45).
