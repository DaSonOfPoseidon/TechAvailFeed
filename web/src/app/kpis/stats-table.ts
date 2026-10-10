import { Component, computed, input, signal } from '@angular/core';
import { percent } from '../shared/time';

export interface StatsRow {
  name: string;
  planned: number;
  completed: number | null;
  canceled: number | null;
  rescheduled: number | null;
  unscheduled: number | null;
  pulled: number | null;
}

type Column = keyof StatsRow;

// Outcome rates per region or technician; click a heading to sort by it.
@Component({
  selector: 'app-stats-table',
  template: `
    <table class="plain num">
      <thead>
        <tr>
          @for (c of columns(); track c.key) {
            <th
              [attr.aria-sort]="
                sort().key === c.key ? (sort().desc ? 'descending' : 'ascending') : null
              "
            >
              <button class="sort" (click)="sortBy(c.key)">
                {{ c.label }}{{ sort().key === c.key ? (sort().desc ? ' ↓' : ' ↑') : '' }}
              </button>
            </th>
          }
        </tr>
      </thead>
      <tbody>
        @for (row of sorted(); track row.name) {
          <tr>
            @for (c of columns(); track c.key) {
              <td>
                {{
                  c.key === 'name' || c.key === 'planned' ? row[c.key] : percent($any(row[c.key]))
                }}
              </td>
            }
          </tr>
        } @empty {
          <tr>
            <td [attr.colspan]="columns().length" class="muted">
              Nothing was planned in this range.
            </td>
          </tr>
        }
      </tbody>
      @if (totals(); as t) {
        <tfoot>
          <tr>
            @for (c of columns(); track c.key) {
              <td>
                {{ c.key === 'name' || c.key === 'planned' ? t[c.key] : percent($any(t[c.key])) }}
              </td>
            }
          </tr>
        </tfoot>
      }
    </table>
  `,
  styles: `
    .sort {
      all: unset;
      cursor: pointer;
      font-weight: 500;
    }
    .sort:focus-visible {
      outline: 2px solid var(--mat-sys-primary);
    }
  `,
})
export class StatsTable {
  readonly rows = input.required<StatsRow[]>();
  readonly totals = input<StatsRow | null>(null);
  readonly jobs = input(true);
  protected readonly sort = signal<{ key: Column; desc: boolean }>({ key: 'planned', desc: true });
  protected readonly percent = percent;

  protected readonly columns = computed(() => {
    const columns: { key: Column; label: string }[] = [
      { key: 'name', label: 'Name' },
      { key: 'planned', label: 'Planned' },
      { key: 'completed', label: 'Completed' },
      { key: 'canceled', label: 'Canceled' },
      { key: 'rescheduled', label: 'Rescheduled' },
      { key: 'unscheduled', label: 'Unscheduled' },
    ];
    return this.jobs()
      ? [...columns, { key: 'pulled' as Column, label: 'Pulled on the day' }]
      : columns;
  });

  protected readonly sorted = computed(() => {
    const { key, desc } = this.sort();
    const value = (row: StatsRow) => row[key] ?? -1;
    return [...this.rows()].sort((a, b) => {
      const order =
        key === 'name' ? a.name.localeCompare(b.name) : (value(a) as number) - (value(b) as number);
      return desc ? -order : order;
    });
  });

  protected sortBy(key: Column): void {
    this.sort.update((s) => ({ key, desc: s.key === key ? !s.desc : key !== 'name' }));
  }
}
