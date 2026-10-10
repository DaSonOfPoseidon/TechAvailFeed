import { addDays, dayLabel, isDay } from './time';

describe('day helpers', () => {
  it('step real days and refuse malformed or impossible ones', () => {
    expect(addDays('2026-02-28', 1)).toBe('2026-03-01');
    expect(addDays('2026-12-31', 1)).toBe('2027-01-01');
    for (const bad of ['foo', '2026-13-01', '2026-02-31', '2026-2-1']) {
      expect(isDay(bad)).toBe(false);
      expect(addDays(bad, 1)).toBeNull();
      expect(dayLabel(bad)).toBe(bad);
    }
  });
});
