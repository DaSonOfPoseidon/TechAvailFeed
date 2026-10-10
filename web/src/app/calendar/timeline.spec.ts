import { TechDetail } from '../api/models';
import { bars, windowFor } from './timeline';

const tech: TechDetail = {
  tech_id: 'a',
  tech_name: 'A',
  region: 'North',
  skills: 'INS',
  shift_h: 9,
  lunch_h: 1,
  time_off_h: 0,
  available_h: 8,
  booked_h: 2,
  free_h: 6,
  jobs: 1,
  tickets: 0,
  on_time_off: false,
  shifts: [{ start: '2026-10-10T07:30:00', end: '2026-10-10T16:30:00' }],
  time_off: [{ start: '2026-10-09T00:00:00', end: '2026-10-10T09:00:00' }],
  work: [
    {
      kind: 'job',
      ref_id: 'j1',
      status: 'A',
      task_type: '3',
      skills: 'VIP',
      region: 'North',
      starts_at: '2026-10-10T10:00:00',
      ends_at: '2026-10-10T12:00:00',
      address_issue: null,
    },
  ],
  free: [],
};

describe('timeline', () => {
  it('spans whole hours around the shifts, at least 8 to 18', () => {
    expect(windowFor([tech], '2026-10-10')).toEqual({ from: 7 * 60, to: 18 * 60 });
  });

  it('places bars in percent of the window, clipping time off that started the day before', () => {
    const found = bars(tech, '2026-10-10', { from: 7 * 60, to: 17 * 60 });
    expect(found.map((b) => [b.kind, b.left, b.width])).toEqual([
      ['shift', 5, 90],
      ['off', 0, 20],
      ['job', 30, 20],
    ]);
    expect(found[2].label).toBe('Job j1: A, 10:00–12:00');
  });
});
