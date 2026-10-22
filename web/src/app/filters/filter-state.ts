import { Injectable, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { filter, map } from 'rxjs';
import { CalendarKind } from '../api/models';

export type Patch = Record<string, string | number | null>;

// The region filter value for work and techs with no region (the API's BlankRegionFilter); an
// empty value would mean no filter.
export const noRegion = '(none)';

// Unset values fall back to the API's own defaults. unit and measure are display choices only.
@Injectable({ providedIn: 'root' })
export class FilterState {
  private readonly router = inject(Router);
  private readonly query = toSignal(
    this.router.events.pipe(
      filter((event) => event instanceof NavigationEnd),
      map(() => this.router.routerState.snapshot.root.queryParamMap),
    ),
    { initialValue: this.router.routerState.snapshot.root.queryParamMap },
  );

  get(name: string): string | null {
    return this.query().get(name);
  }

  readonly region = computed(() => this.get('region'));
  readonly skill = computed(() => this.get('skill'));
  readonly calendar = computed<CalendarKind>(() =>
    this.get('calendar') === 'tc' ? 'tc' : 'install',
  );
  readonly unit = computed<'hours' | 'jobs'>(() =>
    this.get('unit') === 'jobs' ? 'jobs' : 'hours',
  );
  readonly measure = computed<'rate' | 'count'>(() =>
    this.get('measure') === 'count' ? 'count' : 'rate',
  );
  readonly start = computed(() => this.get('start'));
  readonly days = computed(() => {
    const days = Number(this.get('days'));
    return Number.isInteger(days) && days >= 1 && days <= 62 ? days : 30;
  });

  readonly capacity = computed(() => ({
    region: this.region(),
    skill: this.skill(),
    calendar: this.calendar() === 'tc' ? 'tc' : null,
    start: this.start(),
    days: this.days() === 30 ? null : this.days(),
  }));

  // Merges into the query string; null (or a default value) removes the key.
  set(patch: Patch): Promise<boolean> {
    const queryParams: Patch = {};
    for (const [key, value] of Object.entries(patch))
      queryParams[key] =
        value === '' ||
        (key === 'calendar' && value === 'install') ||
        (key === 'days' && value === 30) ||
        (key === 'unit' && value === 'hours') ||
        (key === 'measure' && value === 'rate')
          ? null
          : value;
    return this.router.navigate([], { queryParams, queryParamsHandling: 'merge' });
  }
}
