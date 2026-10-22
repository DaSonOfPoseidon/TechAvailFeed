# Business rules

How the rules in `src/TechAvail.Core` read the feed. The code holds the constants; this file holds the
reasoning that spans more than one member. Keep the two in step.

## Availability

`Availability` turns raw calendar blocks into free time, one calendar at a time.

- **Calendars.** A schedule comes as `shift` on the install calendar and `shift_tc` on the TC (trouble call)
  calendar; a dual tech's comes as both. A trouble call blocks a dual tech's install time, but an install
  doesn't block trouble calls.
- **Tech days.** `TechDays` lists every tech and date in the range with a shift on the calendar, or time off.
  The range defaults to the shift dates. A day off counts on every calendar the tech has shifts on; a tech
  with no shifts at all stays on install (as before TC shifts were sent). A tech whose only shifts have no
  region is on no calendar. A day off belongs to the region the tech usually works in.
- **Hours.** For one tech's day, available = shift − lunch − time off; booked is the jobs and tickets inside
  it; free is the usable slots (`FreeSlots`), so today's are clipped to now + the lead time.
  booked + free ≤ available: a gap shorter than `MinMinutes` is neither.
- **Booking windows.** `FreeWindows` and `HourOpenings` count the booking windows a job could still go in,
  out of `BookableWindows`. A window that hasn't started (by the lead time) and lies wholly inside the
  available time is bookable, booked or not, and free when no work is in it. A window holding a single
  1-hour job, with the other hour still available and free, is an opening for another 1-hour job if the
  tech can do one.
- **Saturdays.** From `NewSaturdaysFrom`, Saturdays run the weekday schedule (lunch at 12:00, the weekday
  windows without 5 PM). Earlier Saturdays keep lunch at 13:00 and windows at 9, 11, 2 and 4, so past days
  don't change.

## Install availability (VP update)

`InstallAvailability` builds the VP's "Residential Install Availability" sheet, the first sheet of
`capacity.xlsx`.

- It covers the same day and the next few business days only, so it doesn't promote scheduling further out.
- Availability is the share of an area's bookable booking windows still free (`FreeWindows /
  BookableWindows`). A tech with available time or a bookable window is working, and their share counts even
  when nothing is left, so an all-booked area reads 0%, Full. Share is null when no tech could take work
  (No Techs, Closed).
- The areas are the ones with install techs in the grid's days, so a renamed region leaves no stale row. A
  region-filtered export shows only that region's area, even when it has no techs.
- Exceptions text covers the same day and next day. A grid shorter than that speaks only for the days it
  has, and one that doesn't start today names its dates instead of "same day/next day". A day every area is
  Closed (a Sunday, or no install techs anywhere) is one exception for all areas.

## Window summary (Q778)

`WindowSummary` is MBSReporter's Q778 (`region_availability_summary.sql`) on the feed's tech days.

- A window gets a row when a tech is scheduled in it, has free time in it, or unassigned work falls in it.
  One with no tech left over once the unassigned work is placed reads FULL.
- A tech is scheduled in a window when their shift covers an hour of it that hasn't started (by the lead
  time), and available when a free slot covers an hour of it. An MDU-skilled tech has an MDU opening when a
  free hour starts in the window.
- Unassigned work counts once (the feed can repeat a row), in the first window it overlaps.
- Trouble calls run every day, so the TC calendar's Sunday uses the weekday windows.

## Outcomes

`Outcomes` records what happened to the work planned for each day.

- The morning plan is the first snapshot in [06:00, 07:00) local. Without one the day has no plan; never
  guess.
- Checkpoints d0, d1 and d2 are the last snapshot before the midnight that ends the plan day, the next day
  and the day after. Before a boundary has passed, its checkpoint is the latest snapshot: the outcome so far.
- A day is final once d2 has ended; it is then written once (`outcome_days`, `job_outcomes`) and never
  recomputed.
- A job pulled from the plan day (canceled, unscheduled or rescheduled by d0) is split by how far the tech
  got that day, read from the status trace.

## Arrivals (Q851)

`Arrivals` rebuilds MBSReporter's on-time arrival report (`first_job_arrival_Q851`) from feed snapshots: the
8:00 jobs as of the 8:15 run, the day so far, and a past day's completed jobs.

- The 8:15 run is the first snapshot generated in [08:15, 09:00).
- One row per tech with an 8:00 job. As in Q851, the arrival is the tech's first In Progress mark on any of
  the day's jobs (techs swap job order), and its En Route mark is that job's. Without an arrival, the En Route
  mark is the 8:00 job's, else the latest on a job still En Route.
- A trace mark counts only on the report day and once it had happened, because the feed sends the latest
  mark from the last two days.

## Jeopardy and the status update

- A job is in jeopardy (JIJ) once it still isn't complete `Jeopardy.Lead` (30 minutes) before its scheduled
  end. The population is the arrival report's: FIELD/TC work that was or is being worked.
- `StatusUpdate` builds the VP's status update (10 AM, 1 PM, 3 PM and 5 PM): Green/Yellow/Red per region and
  the areas and techs behind it. The feed's region is the VP's area; `Regions` groups the areas the way
  MBSReporter's multiregion rules do.
- A tech is a concern for a job running past its end that holds up their next one, then for jobs in jeopardy
  that reason doesn't already cover. An area is a concern when a slot that isn't over holds more open jobs
  than techs assigned to them (an unassigned job brings no tech).
