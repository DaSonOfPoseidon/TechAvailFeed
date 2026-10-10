import { NavigationError } from '@angular/router';
import { isStaleBundle, reloadOnStaleBundle } from './stale-bundle';

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

describe('reloadOnStaleBundle', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
    sessionStorage.clear();
  });

  it('reloads a stale URL, but not again within the loop window', () => {
    const assign = vi.fn();
    vi.stubGlobal('location', { assign });
    const event = new NavigationError(
      1,
      '/kpis?region=North',
      new TypeError('Failed to fetch dynamically imported module'),
    );
    const now = vi.spyOn(Date, 'now').mockReturnValue(1_000_000);
    reloadOnStaleBundle(event);
    reloadOnStaleBundle(event);
    expect(assign).toHaveBeenCalledOnce();
    expect(assign.mock.calls[0][0]).toBe(new URL('kpis?region=North', document.baseURI).href);
    // A later deploy, after the window, reloads again.
    now.mockReturnValue(1_000_000 + 60_000);
    reloadOnStaleBundle(event);
    expect(assign).toHaveBeenCalledTimes(2);
  });
});
