import { TestBed } from '@angular/core/testing';
import { LocalDateAdapter, fromDay, toDay } from './date-field';

describe('date field', () => {
  let adapter: LocalDateAdapter;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [LocalDateAdapter] });
    adapter = TestBed.inject(LocalDateAdapter);
  });

  it('reads typed ISO dates as local days, not UTC', () => {
    const date = adapter.parse('2026-10-10', null)!;
    expect([date.getFullYear(), date.getMonth(), date.getDate()]).toEqual([2026, 9, 10]);
    expect(toDay(adapter.deserialize('2026-03-01'))).toBe('2026-03-01');
  });

  it('still reads US-style dates', () => {
    expect(toDay(adapter.parse('3/1/2026', null))).toBe('2026-03-01');
  });

  it('round-trips query-string days', () => {
    expect(toDay(fromDay('2026-01-31'))).toBe('2026-01-31');
    expect(fromDay(null)).toBeNull();
    expect(toDay(null)).toBeNull();
    expect(toDay(adapter.parse('nonsense', null))).toBeNull();
  });

  it('rejects impossible days instead of rolling them over', () => {
    expect(fromDay('2026-02-31')).toBeNull();
    expect(fromDay('2026-13-01')).toBeNull();
    expect(toDay(fromDay('2024-02-29'))).toBe('2024-02-29');
    expect(fromDay('0050-01-01')?.getFullYear()).toBe(50);
    expect(toDay(fromDay('0050-01-01'))).toBe('0050-01-01');
    expect(adapter.isValid(adapter.parse('2026-02-31', null)!)).toBe(false);
  });
});
