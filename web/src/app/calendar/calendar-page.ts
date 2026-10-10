import { httpResource } from '@angular/common/http';
import { Component, computed, inject } from '@angular/core';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { DecimalPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { params } from '../api/feed-api';
import { CalendarRange, Capacity } from '../api/models';
import { FilterBar } from '../filters/filter-bar';
import { FilterState } from '../filters/filter-state';
import { Freshness } from '../shared/freshness';
import { ErrorPanel } from '../shared/error-panel';
import { dayLabel, hours, isWeekend, percent, regionName, weekday } from '../shared/time';
import { utilizationStep, utilizationSteps } from './utilization';

export interface Row {
  region: string;
  cells: (Capacity | null)[];
  // The region filter a cell opens the day with (null: all regions).
  filter: string | null;
}

// The rows of the grid: one per region seen in the range, then the totals.
export function rows(range: CalendarRange): Row[] {
  const regions = [...new Set(range.days.flatMap((d) => d.by_region.map((r) => r.region)))].sort();
  return [
    ...regions.map((region) => ({
      region: regionName(region),
      cells: range.days.map((d) => d.by_region.find((r) => r.region === region) ?? null),
      filter: region,
    })),
    { region: 'All regions', cells: range.days.map((d) => d.totals), filter: null },
  ];
}

// Free hours per region and day, shaded by utilisation. A column opens that day.
@Component({
  selector: 'app-calendar-page',
  imports: [DecimalPipe, ErrorPanel, FilterBar, MatProgressBarModule, MatTooltipModule, RouterLink],
  templateUrl: './calendar-page.html',
  styleUrl: './calendar-page.scss',
})
export class CalendarPage {
  protected readonly state = inject(FilterState);
  private readonly freshness = inject(Freshness);
  protected readonly range = httpResource<CalendarRange>(() => {
    this.freshness.snapshotId();
    return { url: '/api/v1/calendar', params: params(this.state.capacity()) };
  });
  protected readonly rows = computed(() => {
    const range = this.range.value();
    return range ? rows(range) : [];
  });
  protected readonly steps = utilizationSteps;
  protected readonly step = utilizationStep;
  protected readonly dayLabel = dayLabel;
  protected readonly weekday = weekday;
  protected readonly isWeekend = isWeekend;
  protected readonly hours = hours;
  protected readonly percent = percent;

  protected tooltip(region: string, date: string, cell: Capacity): string {
    return [
      `${region}, ${dayLabel(date)}`,
      `${hours(cell.free_h)} free of ${hours(cell.available_h)} available (${percent(cell.utilization)} booked)`,
      `${cell.techs_on} techs on, ${cell.techs_off} off`,
      `${cell.jobs} jobs, ${cell.tickets} tickets`,
      cell.unassigned_jobs + cell.unassigned_tickets > 0
        ? `${cell.unassigned_jobs} jobs and ${cell.unassigned_tickets} tickets unassigned (${hours(cell.unassigned_h)})`
        : 'Nothing unassigned',
    ].join('\n');
  }
}
