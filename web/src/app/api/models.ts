// The API's JSON (snake_case, as served). Timestamps without an offset are naive local
// wall-clock times ("2026-10-10T08:00:00"): display them as they are, never convert them.

export interface SnapshotInfo {
  id: number;
  generated_at: string | null;
  age_min: number | null;
  stale: boolean;
}

export interface Health {
  ok: boolean;
  snapshot: SnapshotInfo | null;
}

export type CalendarKind = 'install' | 'tc';

export interface Filters {
  snapshot: SnapshotInfo | null;
  regions: string[];
  skills: string[];
  techs: { tech_id: string; tech_name: string; region: string; calendar: string }[];
  vp_regions: string[];
}

export interface Capacity {
  techs_on: number;
  techs_off: number;
  shift_h: number;
  available_h: number;
  booked_h: number;
  free_h: number;
  jobs: number;
  tickets: number;
  unassigned_jobs: number;
  unassigned_tickets: number;
  unassigned_h: number;
  utilization: number | null;
  net_h: number;
}

export interface RegionCapacity extends Capacity {
  region: string;
}

export interface CalendarEntry {
  date: string;
  totals: Capacity;
  by_region: RegionCapacity[];
}

export interface CalendarRange {
  snapshot: SnapshotInfo | null;
  start: string;
  end: string;
  days: CalendarEntry[];
}

export interface Interval {
  start: string;
  end: string;
}

export interface WorkRow {
  kind: string;
  ref_id: string;
  status: string;
  task_type: string;
  skills: string;
  region: string;
  starts_at: string;
  ends_at: string;
  address_issue: string | null;
}

export interface FreeSlot {
  open_from: string;
  open_until: string;
  open_minutes: number;
}

export interface TechDetail {
  tech_id: string;
  tech_name: string;
  region: string;
  skills: string;
  shift_h: number;
  lunch_h: number;
  time_off_h: number;
  available_h: number;
  booked_h: number;
  free_h: number;
  jobs: number;
  tickets: number;
  on_time_off: boolean;
  shifts: Interval[];
  time_off: Interval[];
  work: WorkRow[];
  free: FreeSlot[];
}

export interface CalendarDay {
  snapshot: SnapshotInfo | null;
  date: string;
  totals: Capacity;
  by_region: RegionCapacity[];
  techs: TechDetail[];
  unassigned: WorkRow[];
}

export interface RegionTotal {
  region: string;
  shift_h: number;
  available_h: number;
  booked_h: number;
  free_h: number;
  jobs: number;
  tickets: number;
  unassigned_jobs: number;
  unassigned_tickets: number;
  unassigned_h: number;
  utilization: number | null;
  net_h: number;
  tech_days_off: number;
}

export interface CapacityKpis {
  snapshot: SnapshotInfo | null;
  series: (Capacity & { date: string })[];
  by_region: RegionTotal[];
}

export type Rates = Record<string, number | null>;

export interface KindStats {
  planned: number;
  completed: Record<string, number>;
  completion_rate: Rates;
  outcome: Record<string, number>;
  outcome_rate: Rates;
}

export interface JobStats extends KindStats {
  pulled_d0: Record<string, number>;
  pulled_d0_rate: number | null;
}

export interface DayKpis {
  date: string;
  status: string;
  provisional: boolean;
  job?: JobStats;
  ticket?: KindStats;
}

export interface OutcomeKpis {
  snapshot: SnapshotInfo | null;
  start: string;
  end: string;
  days: DayKpis[];
  totals: { job: JobStats; ticket: KindStats };
  by_region: { region: string; job: JobStats; ticket: KindStats }[];
  by_tech: { tech_id: string; tech_name: string; job: JobStats; ticket: KindStats }[];
}

export interface DiagnosticCheck {
  id: string;
  group: string;
  title: string;
  severity: 'warning' | 'info';
  description: string;
  available: boolean;
  count: number;
  rows: Record<string, unknown>[];
}

export interface DiagnosticsResponse {
  snapshot: SnapshotInfo | null;
  checks: DiagnosticCheck[];
}

export interface MapPoint {
  ref_id: string;
  kind: 'job' | 'ticket';
  assigned: boolean;
  status: string;
  work_date: string;
  starts_at: string;
  ends_at: string;
  region: string;
  techs: { tech_id: string; tech_name: string }[];
  lat?: number;
  lon?: number;
  gps_precision: string;
  address_issue: string | null;
}

export interface MapResponse {
  snapshot: SnapshotInfo | null;
  start: string;
  end: string;
  exact: boolean;
  points: MapPoint[];
  unmapped: MapPoint[];
}
