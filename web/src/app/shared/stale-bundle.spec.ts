import { isStaleBundle } from './stale-bundle';

describe('isStaleBundle', () => {
  it('recognises each browser wording of a missing lazy bundle', () => {
    expect(
      isStaleBundle(new TypeError('Failed to fetch dynamically imported module: /chunk-x.js')),
    ).toBe(true);
    expect(isStaleBundle(new TypeError('error loading dynamically imported module'))).toBe(true);
    expect(isStaleBundle(new TypeError('Importing a module script failed.'))).toBe(true);
    expect(isStaleBundle(new Error('Cannot match any routes'))).toBe(false);
  });
});
