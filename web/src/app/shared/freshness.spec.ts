import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Freshness, refreshMs } from './freshness';

describe('Freshness', () => {
  afterEach(() => vi.useRealTimers());

  it('signals a reload for a new snapshot and every few minutes without one', () => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const freshness = TestBed.inject(Freshness);
    const backend = TestBed.inject(HttpTestingController);
    freshness.start();
    backend.expectOne('/health').flush({ snapshot: { id: 7 } });
    const first = freshness.reload();
    vi.advanceTimersByTime(60_000);
    backend.expectOne('/health').flush({ snapshot: { id: 7 } });
    expect(freshness.reload()).toBe(first);
    vi.advanceTimersByTime(refreshMs - 60_000);
    backend.match('/health').forEach((r) => r.flush({ snapshot: { id: 7 } }));
    expect(freshness.reload()).not.toBe(first);
  });
});
