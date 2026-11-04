import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Capacity, CalendarRange } from '../api/models';
import { CalendarPage, rows } from './calendar-page';
import { utilizationStep } from './utilization';

function cap(free: number, utilization: number | null): Capacity {
  return {
    techs_on: 1,
    techs_off: 0,
    shift_h: 9,
    available_h: 8,
    booked_h: 8 - free,
    free_h: free,
    free_slots: Math.floor(free / 2),
    free_hour_slots: 0,
    jobs: 1,
    tickets: 0,
    unassigned_jobs: 0,
    unassigned_tickets: 0,
    unassigned_h: 0,
    utilization,
    net_h: free,
  };
}

describe('calendar rows', () => {
  const range: CalendarRange = {
    snapshot: null,
    start: '2026-10-10',
    end: '2026-10-11',
    days: [
      {
        date: '2026-10-10',
        totals: cap(10, 0.4),
        by_region: [
          { ...cap(4, 0.5), region: 'South' },
          { ...cap(6, 0.2), region: '' },
        ],
      },
      { date: '2026-10-11', totals: cap(2, 0.9), by_region: [{ ...cap(2, 0.9), region: 'South' }] },
    ],
  };

  it('has a row per region, then the totals, with gaps where a region has no capacity', () => {
    const grid = rows(range);
    expect(grid.map((r) => r.region)).toEqual(['No region', 'South', 'All regions']);
    expect(grid[0].cells[1]).toBeNull();
    expect(grid[0].filter).toBe('(none)');
    expect(grid[1].cells.map((c) => c?.free_h)).toEqual([4, 2]);
    expect(grid[2].filter).toBeNull();
  });

  it('steps utilisation into the five shades', () => {
    expect([null, 0, 0.29, 0.3, 0.69, 0.84, 0.85, 1.2].map(utilizationStep)).toEqual([
      0, 1, 1, 2, 3, 4, 5, 5,
    ]);
  });
});

@Component({ template: '' })
class Blank {}

describe('calendar page', () => {
  it('asks for grouped rows and opens the day for the area', async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: '**', component: Blank }]),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    await TestBed.inject(Router).navigateByUrl('/calendar?regions=grouped');
    const page = TestBed.createComponent(CalendarPage);
    TestBed.tick();

    const request = TestBed.inject(HttpTestingController).expectOne(
      (r) => r.url === '/api/v1/calendar',
    );
    expect(request.request.params.get('regions')).toBe('grouped');
    request.flush({
      snapshot: null,
      start: '2026-10-10',
      end: '2026-10-10',
      days: [
        {
          date: '2026-10-10',
          totals: cap(10, 0.4),
          by_region: [{ ...cap(10, 0.4), region: 'Central MO' }],
        },
      ],
    } satisfies CalendarRange);
    for (let i = 0; i < 5; i++) await Promise.resolve();
    TestBed.tick();

    const cell = (page.nativeElement as HTMLElement).querySelector('a[href*="region="]');
    expect(cell?.getAttribute('href')).toBe(
      '/calendar/2026-10-10?regions=grouped&region=Central%20MO',
    );
  });
});
