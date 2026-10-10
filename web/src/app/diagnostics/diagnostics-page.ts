import { httpResource } from '@angular/common/http';
import { Component, computed, inject } from '@angular/core';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { params } from '../api/feed-api';
import { DiagnosticCheck, DiagnosticsResponse } from '../api/models';
import { FilterState } from '../filters/filter-state';
import { ErrorPanel } from '../shared/error-panel';
import { Freshness } from '../shared/freshness';

export const groups: { id: string; label: string }[] = [
  { id: 'tech_setup', label: 'Tech setup' },
  { id: 'scheduling', label: 'Scheduling' },
  { id: 'stale', label: 'Stale work' },
  { id: 'address', label: 'Addresses' },
];

// A check's table columns: the keys of its rows, in first-seen order.
export function columns(check: DiagnosticCheck): string[] {
  return [...new Set(check.rows.flatMap((row) => Object.keys(row)))];
}

export function heading(key: string): string {
  const text = key.replaceAll('_', ' ');
  return text.charAt(0).toUpperCase() + text.slice(1);
}

export function cell(value: unknown): string {
  if (value === null || value === undefined || value === '') return '—';
  if (typeof value === 'string' && /^\d{4}-\d\d-\d\dT\d\d:\d\d/.test(value))
    return value.slice(0, 16).replace('T', ' ');
  if (Array.isArray(value)) return value.join(', ');
  return String(value);
}

// The data-quality checks, findings first; each opens to its rows.
@Component({
  selector: 'app-diagnostics-page',
  imports: [
    ErrorPanel,
    MatButtonToggleModule,
    MatExpansionModule,
    MatIconModule,
    MatProgressBarModule,
  ],
  templateUrl: './diagnostics-page.html',
  styleUrl: './diagnostics-page.scss',
})
export class DiagnosticsPage {
  protected readonly state = inject(FilterState);
  private readonly freshness = inject(Freshness);
  protected readonly groups = groups;
  protected readonly group = computed(() => this.state.get('group'));
  protected readonly result = httpResource<DiagnosticsResponse>(() => {
    this.freshness.snapshotId();
    return { url: '/api/v1/diagnostics', params: params({ group: this.group() }) };
  });
  protected readonly checks = computed(() =>
    [...(this.result.value()?.checks ?? [])].sort(
      (a, b) =>
        Number(b.available) - Number(a.available) ||
        Number(b.count > 0) - Number(a.count > 0) ||
        Number(b.severity === 'warning') - Number(a.severity === 'warning'),
    ),
  );
  protected readonly findings = computed(() => this.checks().reduce((sum, c) => sum + c.count, 0));
  protected readonly columns = columns;
  protected readonly heading = heading;
  protected readonly cell = cell;
}
