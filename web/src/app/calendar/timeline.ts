import { Interval, TechDetail, WorkRow } from '../api/models';
import { hhmm, minutes } from '../shared/time';

// A bar on a tech's day, positioned in percent of the visible window.
export interface Bar {
  left: number;
  width: number;
  kind: 'shift' | 'off' | 'job' | 'ticket' | 'free';
  label: string;
}

export interface Window {
  from: number; // minutes since midnight
  to: number;
}

// Whole hours from the earliest shift start to the latest end, at least 8:00 to 18:00.
export function windowFor(techs: TechDetail[], date: string): Window {
  let from = 8 * 60;
  let to = 18 * 60;
  for (const tech of techs) {
    for (const s of tech.shifts) {
      from = Math.min(from, clamp(s.start, date));
      to = Math.max(to, clamp(s.end, date));
    }
  }
  return { from: Math.floor(from / 60) * 60, to: Math.ceil(to / 60) * 60 };
}

// Minutes since midnight of date, clamped to that day (time off can span days).
function clamp(stamp: string, date: string): number {
  const day = stamp.slice(0, 10);
  return day < date ? 0 : day > date ? 24 * 60 : minutes(stamp);
}

function bar(
  window: Window,
  date: string,
  start: string,
  end: string,
  kind: Bar['kind'],
  label: string,
): Bar | null {
  const from = Math.max(window.from, clamp(start, date));
  const to = Math.min(window.to, clamp(end, date));
  if (to <= from) return null;
  const span = window.to - window.from;
  return {
    left: ((from - window.from) / span) * 100,
    width: ((to - from) / span) * 100,
    kind,
    label,
  };
}

function workLabel(w: WorkRow): string {
  return `${w.kind === 'ticket' ? 'Ticket' : 'Job'} ${w.ref_id}: ${w.status}, ${hhmm(w.starts_at)}–${hhmm(w.ends_at)}`;
}

// A tech's bars, back to front: shifts, time off, free slots, then work.
export function bars(tech: TechDetail, date: string, window: Window): Bar[] {
  const span = (i: Interval) => `${hhmm(i.start)}–${hhmm(i.end)}`;
  return [
    ...tech.shifts.map((s) => bar(window, date, s.start, s.end, 'shift', `Shift ${span(s)}`)),
    ...tech.time_off.map((s) => bar(window, date, s.start, s.end, 'off', `Time off ${span(s)}`)),
    ...tech.free.map((f) =>
      bar(
        window,
        date,
        f.open_from,
        f.open_until,
        'free',
        `Free ${hhmm(f.open_from)}–${hhmm(f.open_until)} (${f.open_minutes} min)`,
      ),
    ),
    ...tech.work.map((w) =>
      bar(
        window,
        date,
        w.starts_at,
        w.ends_at,
        w.kind === 'ticket' ? 'ticket' : 'job',
        workLabel(w),
      ),
    ),
  ].filter((b): b is Bar => b !== null);
}
