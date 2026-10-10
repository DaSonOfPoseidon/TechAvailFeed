// Formatting for the API's naive local timestamps ("2026-10-10T08:30:00") and dates ("2026-10-10").
// They are sliced, never parsed into a Date with a timezone.

export function hhmm(stamp: string): string {
  return stamp.slice(11, 16);
}

// Minutes since midnight of the stamp's own day.
export function minutes(stamp: string): number {
  return Number(stamp.slice(11, 13)) * 60 + Number(stamp.slice(14, 16));
}

const days = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

// A calendar date's weekday, from its parts (UTC, so the browser's zone can't shift it).
export function weekday(date: string): string {
  return days[new Date(`${date}T00:00:00Z`).getUTCDay()];
}

// "Sat 10 Oct"
export function dayLabel(date: string): string {
  if (!isDay(date)) return date;
  return `${weekday(date)} ${Number(date.slice(8, 10))} ${months[Number(date.slice(5, 7)) - 1]}`;
}

export function isWeekend(date: string): boolean {
  const day = new Date(`${date}T00:00:00Z`).getUTCDay();
  return day === 0 || day === 6;
}

// Today's date where the browser is (the dashboard is used in the feed's own timezone).
export function today(): string {
  const now = new Date();
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

// Whether date is a real YYYY-MM-DD day (a route can carry anything, e.g. /calendar/2026-02-31).
export function isDay(date: string): boolean {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date)) return false;
  const day = new Date(`${date}T00:00:00Z`);
  return !isNaN(day.getTime()) && day.toISOString().slice(0, 10) === date;
}

// date plus count days, or null when date isn't a real day.
export function addDays(date: string, count: number): string | null {
  if (!isDay(date)) return null;
  const day = new Date(`${date}T00:00:00Z`);
  day.setUTCDate(day.getUTCDate() + count);
  return day.toISOString().slice(0, 10);
}

export function percent(rate: number | null | undefined): string {
  return rate === null || rate === undefined ? '—' : `${Math.round(rate * 100)}%`;
}

export function hours(value: number | null | undefined): string {
  return value === null || value === undefined ? '—' : `${Math.round(value * 10) / 10}h`;
}

// Work or techs without a region come back with "".
export function regionName(region: string): string {
  return region || 'No region';
}
