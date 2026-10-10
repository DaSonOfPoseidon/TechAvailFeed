import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Health, SnapshotInfo } from '../api/models';

// The served snapshot, from /health once a minute. Pages read snapshotId() in their requests so
// they reload when a new snapshot is served.
@Injectable({ providedIn: 'root' })
export class Freshness {
  private readonly http = inject(HttpClient);
  readonly snapshot = signal<SnapshotInfo | null>(null);
  readonly reachable = signal(true);
  readonly snapshotId = computed(() => this.snapshot()?.id ?? 0);

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
  }
}
