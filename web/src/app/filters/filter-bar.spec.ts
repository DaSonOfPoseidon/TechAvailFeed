import { withActive } from './filter-bar';

describe('withActive', () => {
  it('leaves the options alone when the active value is among them', () => {
    expect(withActive(['North', 'South'], 'South')).toEqual(['North', 'South']);
    expect(withActive(['North'], null)).toEqual(['North']);
  });

  it('keeps an active value the options no longer have', () => {
    expect(withActive(['North'], 'Gone')).toEqual(['North', 'Gone']);
  });
});
