import { Capacity, CalendarRange } from '../api/models';
import { rows } from './calendar-page';
import { utilizationStep } from './utilization';

function cap(free: number, utilization: number | null): Capacity {
  return {
    techs_on: 1,
    techs_off: 0,
    shift_h: 9,
    available_h: 8,
    booked_h: 8 - free,
    free_h: free,
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
    expect(grid[0].filter).toBe('');
    expect(grid[1].cells.map((c) => c?.free_h)).toEqual([4, 2]);
    expect(grid[2].filter).toBeNull();
  });

  it('steps utilisation into the five shades', () => {
    expect([null, 0, 0.29, 0.3, 0.69, 0.84, 0.85, 1.2].map(utilizationStep)).toEqual([
      0, 1, 1, 2, 3, 4, 5, 5,
    ]);
  });
});
