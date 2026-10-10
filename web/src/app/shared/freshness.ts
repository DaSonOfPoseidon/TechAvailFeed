import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Health, SnapshotInfo } from '../api/models';

// The served snapshot, from /health once a minute. Pages read reload() in their requests so they
// refetch when a new snapshot is served, and every few minutes regardless: some answers move with
// the clock on an unchanged snapshot (today's free time is clipped to now, a default start date
// rolls over at midnight).
export const refreshMs = 5 * 60_000;

@Injectable({ providedIn: 'root' })
export class Freshness {
  private readonly http = inject(HttpClient);
  readonly snapshot = signal<SnapshotInfo | null>(null);
  readonly reachable = signal(true);
  readonly snapshotId = computed(() => this.snapshot()?.id ?? 0);
  private readonly tick = signal(0);
  readonly reload = computed(() => `${this.snapshotId()}:${this.tick()}`);

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
