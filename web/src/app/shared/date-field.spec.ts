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
});
