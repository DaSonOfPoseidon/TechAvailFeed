import { HttpClient } from '@angular/common/http';
import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { HttpResourceRef } from '@angular/common/http';
import { Health, SnapshotInfo } from '../api/models';

// Pages also call refreshEvery(): some answers move with the clock on an unchanged snapshot (today's
// free time is clipped to now, a default start date rolls over at midnight).
export const refreshMs = 5 * 60_000;

@Injectable({ providedIn: 'root' })
export class Freshness {
  private readonly http = inject(HttpClient);
  readonly snapshot = signal<SnapshotInfo | null>(null);
  readonly reachable = signal(true);
  readonly snapshotId = computed(() => this.snapshot()?.id ?? 0);
  readonly tick = signal(0);

  start(): void {
    const poll = () =>
      this.http.get<Health>('/health').subscribe({
        next: (health) => {
          this.reachable.set(true);
          this.snapshot.set(health.snapshot);
        },
        error: () => this.reachable.set(false),
      });
    poll();
    setInterval(poll, 60_000);
    setInterval(() => this.tick.update((n) => n + 1), refreshMs);
  }
}

// reload() keeps the current value on screen while it refetches; changing the request instead would
// drop it and blank the page. Call it in an injection context.
export function refreshEvery(...resources: HttpResourceRef<unknown>[]): void {
  const freshness = inject(Freshness);
  effect(() => {
    if (freshness.tick() === 0) return;
    untracked(() => resources.forEach((resource) => resource.reload()));
  });
}
