import { Filters } from '../api/models';
import { regionOptions, withActive } from './filter-bar';

describe('withActive', () => {
  it('leaves the options alone when the active value is among them', () => {
    expect(withActive(['North', 'South'], 'South')).toEqual(['North', 'South']);
    expect(withActive(['North'], null)).toEqual(['North']);
  });

  it('keeps an active value the options no longer have', () => {
    expect(withActive(['North'], 'Gone')).toEqual(['North', 'Gone']);
  });
});

describe('regionOptions', () => {
  const filters = {
    regions: ['Columbia Core', 'North'],
    areas: ['Central MO', 'North'],
  } as Filters;

  it('lists the MBS regions, or the areas when grouped', () => {
    expect(regionOptions(filters, 'mbs')).toEqual(['Columbia Core', 'North']);
    expect(regionOptions(filters, 'grouped')).toEqual(['Central MO', 'North']);
  });

  it('is empty before the filters load', () => {
    expect(regionOptions(undefined, 'grouped')).toEqual([]);
  });
});
