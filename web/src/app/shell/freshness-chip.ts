import { Component, computed, inject } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Freshness } from '../shared/freshness';

// How old the served snapshot is. Stale (no delivery for 45 minutes) is shown with an icon and words.
@Component({
  selector: 'app-freshness-chip',
  imports: [MatIconModule, MatTooltipModule],
  template: `
    <span class="chip" [class.stale]="state() !== 'fresh'" [matTooltip]="tooltip()">
      <mat-icon aria-hidden="true">{{ state() === 'fresh' ? 'schedule' : 'warning' }}</mat-icon>
      {{ label() }}
    </span>
  `,
  styles: `
    .chip {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 4px 10px;
      border-radius: 999px;
      border: 1px solid var(--ta-ring);
      font-variant-numeric: tabular-nums;
      white-space: nowrap;
    }
    mat-icon {
      font-size: 18px;
      width: 18px;
      height: 18px;
    }
    .stale mat-icon {
      color: var(--ta-warning);
    }
  `,
})
export class FreshnessChip {
  private readonly freshness = inject(Freshness);

  protected readonly state = computed(() => {
    if (!this.freshness.reachable()) return 'down';
    const snapshot = this.freshness.snapshot();
    if (!snapshot) return 'none';
    return snapshot.stale ? 'stale' : 'fresh';
  });

  protected readonly label = computed(() => {
    const snapshot = this.freshness.snapshot();
    switch (this.state()) {
      case 'down':
        return 'API not reachable';
      case 'none':
        return 'No feed yet';
      default: {
        const age = Math.round(snapshot?.age_min ?? 0);
        const text = age < 60 ? `${age} min old` : `${Math.floor(age / 60)} h ${age % 60} min old`;
        return snapshot?.stale ? `Stale: ${text}` : `Feed ${text}`;
      }
    }
  });

  protected readonly tooltip = computed(() => {
    const at = this.freshness.snapshot()?.generated_at;
    return at ? `Snapshot generated ${new Date(at).toLocaleString()}` : '';
  });
}
