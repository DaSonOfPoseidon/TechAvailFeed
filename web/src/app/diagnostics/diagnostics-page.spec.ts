import { DiagnosticCheck } from '../api/models';
import { cell, columns, heading } from './diagnostics-page';

describe('diagnostic tables', () => {
  it('takes its columns from the rows, in first-seen order', () => {
    const check = {
      rows: [
        { tech_id: 'a', days: 2 },
        { tech_id: 'b', first_date: '2026-10-10' },
      ],
    } as unknown as DiagnosticCheck;
    expect(columns(check)).toEqual(['tech_id', 'days', 'first_date']);
    expect(heading('first_date')).toBe('First date');
  });

  it('formats cells', () => {
    expect(cell('2026-10-10T08:30:00')).toBe('2026-10-10 08:30');
    expect(cell(null)).toBe('—');
    expect(cell(['a', 'b'])).toBe('a, b');
  });
});
