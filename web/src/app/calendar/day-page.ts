import { httpResource } from '@angular/common/http';
import { Component, computed, inject, input } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { params } from '../api/feed-api';
import { CalendarDay, TechDetail } from '../api/models';
import { FilterBar } from '../filters/filter-bar';
import { FilterState } from '../filters/filter-state';
import { ErrorPanel } from '../shared/error-panel';
import { Freshness } from '../shared/freshness';
import { addDays, dayLabel, hhmm, hours, percent, regionName } from '../shared/time';
import { bars, windowFor } from './timeline';

// One day per technician: shift, time off, booked work and free slots on a shared time axis.
@Component({
  selector: 'app-day-page',
  imports: [
    ErrorPanel,
    FilterBar,
    MatButtonModule,
    MatIconModule,
    MatProgressBarModule,
    MatTableModule,
    MatTooltipModule,
    RouterLink,
  ],
  templateUrl: './day-page.html',
  styleUrl: './day-page.scss',
})
export class DayPage {
  readonly date = input.required<string>();
  protected readonly state = inject(FilterState);
  private readonly freshness = inject(Freshness);
  protected readonly day = httpResource<CalendarDay>(() => {
    this.freshness.snapshotId();
    const { region, skill, calendar } = this.state.capacity();
    return { url: `/api/v1/calendar/${this.date()}`, params: params({ region, skill, calendar }) };
  });

  // Working techs first (by region, then name); those off all day at the end.
  protected readonly techs = computed(() => {
    const techs = [...(this.day.value()?.techs ?? [])];
    return techs.sort(
      (a, b) =>
        Number(a.on_time_off) - Number(b.on_time_off) ||
        a.region.localeCompare(b.region) ||
        a.tech_name.localeCompare(b.tech_name),
    );
  });
  protected readonly window = computed(() => windowFor(this.techs(), this.date()));
  protected readonly ticks = computed(() => {
    const { from, to } = this.window();
    const ticks = [];
    for (let m = from; m <= to; m += 60)
      ticks.push({ left: ((m - from) / (to - from)) * 100, label: `${m / 60}:00` });
    return ticks;
  });

  protected bars(tech: TechDetail) {
    return bars(tech, this.date(), this.window());
  }

  protected readonly unassignedColumns = [
    'kind',
    'ref_id',
    'time',
    'status',
    'skills',
    'region',
    'address_issue',
  ];
  protected readonly previous = computed(() => addDays(this.date(), -1));
  protected readonly next = computed(() => addDays(this.date(), 1));
  protected readonly dayLabel = dayLabel;
  protected readonly hhmm = hhmm;
  protected readonly hours = hours;
  protected readonly percent = percent;
  protected readonly regionName = regionName;
}
