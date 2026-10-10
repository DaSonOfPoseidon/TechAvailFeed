import { httpResource, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Injector, runInInjectionContext } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Freshness, refreshEvery, refreshMs } from './freshness';

describe('refreshEvery', () => {
  afterEach(() => vi.useRealTimers());

  // Runs effects, then lets the resource's promise callbacks land.
  async function settle(): Promise<void> {
    TestBed.tick();
    for (let i = 0; i < 5; i++) await Promise.resolve();
    TestBed.tick();
  }

  it('reloads on each tick and keeps the value meanwhile', async () => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const backend = TestBed.inject(HttpTestingController);
    const freshness = TestBed.inject(Freshness);
    const resource = runInInjectionContext(TestBed.inject(Injector), () => {
      const r = httpResource<{ n: number }>(() => '/api/v1/thing');
      refreshEvery(r);
      return r;
    });
    freshness.start();
    backend.expectOne('/health').flush({ snapshot: { id: 7 } });
    await settle();
    backend.expectOne('/api/v1/thing').flush({ n: 1 });
    await settle();
    expect(resource.value()).toEqual({ n: 1 });

    vi.advanceTimersByTime(refreshMs);
    backend.match('/health').forEach((r) => r.flush({ snapshot: { id: 7 } }));
    await settle();
    const again = backend.expectOne('/api/v1/thing');
    expect(resource.value()).toEqual({ n: 1 });
    again.flush({ n: 2 });
    await settle();
    expect(resource.value()).toEqual({ n: 2 });
  });
});
