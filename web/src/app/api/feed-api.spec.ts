import { filenameFrom, params } from './feed-api';

describe('params', () => {
  it('drops unset values', () => {
    expect(
      params({ region: 'North', skill: null, days: 7, start: '', tech: undefined }).toString(),
    ).toBe('region=North&days=7');
  });
});

describe('filenameFrom', () => {
  it('reads the quoted filename', () => {
    expect(filenameFrom('attachment; filename="jeopardy_2026-10-10_1300.xlsx"', 'x')).toBe(
      'jeopardy_2026-10-10_1300.xlsx',
    );
  });

  it('falls back without a header', () => {
    expect(filenameFrom(null, 'capacity.xlsx')).toBe('capacity.xlsx');
  });
});
