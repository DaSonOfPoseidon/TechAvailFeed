import { techLabels } from './stats-table';

describe('techLabels', () => {
  it('adds the id only where two technicians share a name', () => {
    const labels = techLabels([
      { tech_id: 'jsmi0170', tech_name: 'J Smith' },
      { tech_id: 'jsmi0171', tech_name: 'J Smith' },
      { tech_id: 'adoe0170', tech_name: 'A Doe' },
    ]);
    expect([...labels.values()]).toEqual(['J Smith (jsmi0170)', 'J Smith (jsmi0171)', 'A Doe']);
  });
});
