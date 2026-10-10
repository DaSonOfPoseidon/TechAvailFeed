import { httpResource } from '@angular/common/http';
import { Component, computed, inject } from '@angular/core';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { params } from '../api/feed-api';
import { CapacityKpis, Filters, JobStats, KindStats, OutcomeKpis } from '../api/models';
import { FilterBar } from '../filters/filter-bar';
import { FilterState } from '../filters/filter-state';
import { ChartOptions, EChart, chrome, cssColor } from '../shared/echart';
import { DateField } from '../shared/date-field';
import { ErrorPanel } from '../shared/error-panel';
import { Freshness } from '../shared/freshness';
import { dayLabel, hours, percent, regionName } from '../shared/time';
import { StatsTable, StatsRow } from './stats-table';

// The outcome lines, in the categorical order (slot 1 first).
const outcomeLines: { name: string; rate: (s: KindStats) => number | null | undefined }[] = [
  { name: 'Completed', rate: (s) => s.outcome_rate['completed'] },
  { name: 'Canceled', rate: (s) => s.outcome_rate['canceled'] },
  { name: 'Rescheduled', rate: (s) => s.outcome_rate['rescheduled'] },
  { name: 'Pulled on the day', rate: (s) => (s as JobStats).pulled_d0_rate },
];

export function statsRow(name: string, stats: KindStats): StatsRow {
  return {
    name,
    planned: stats.planned,
    completed: stats.outcome_rate['completed'] ?? null,
    canceled: stats.outcome_rate['canceled'] ?? null,
    rescheduled: stats.outcome_rate['rescheduled'] ?? null,
    unscheduled: stats.outcome_rate['unscheduled'] ?? null,
    pulled: (stats as JobStats).pulled_d0_rate ?? null,
  };
}

// Capacity over the coming days, and what happened to the work planned on past days.
@Component({
  selector: 'app-kpis-page',
  imports: [
    DateField,
    EChart,
    ErrorPanel,
    FilterBar,
    MatButtonToggleModule,
    MatFormFieldModule,
    MatProgressBarModule,
    MatSelectModule,
    StatsTable,
  ],
  templateUrl: './kpis-page.html',
  styleUrl: './kpis-page.scss',
})
export class KpisPage {
  protected readonly state = inject(FilterState);
  private readonly freshness = inject(Freshness);
  protected readonly filters = httpResource<Filters>(() => '/api/v1/filters');
  protected readonly capacity = httpResource<CapacityKpis>(() => {
    this.freshness.snapshotId();
    return { url: '/api/v1/kpis/capacity', params: params(this.state.capacity()) };
  });
  protected readonly outcomes = httpResource<OutcomeKpis>(() => {
    this.freshness.snapshotId();
    return {
      url: '/api/v1/kpis/outcomes',
      params: params({
        start: this.state.get('from'),
        end: this.state.get('to'),
        region: this.state.region(),
        tech: this.state.get('tech'),
      }),
    };
  });
  protected readonly kind = computed(() =>
    this.state.get('kind') === 'ticket' ? 'ticket' : 'job',
  );

  protected readonly capacityChart = computed(() => {
    const series = this.capacity.value()?.series ?? [];
    return (): ChartOptions => {
      const c = chrome();
      const bar = (name: string, token: string, value: (d: (typeof series)[0]) => number) => ({
        name,
        type: 'bar',
        stack: 'hours',
        barMaxWidth: 24,
        itemStyle: {
          color: cssColor(token),
          borderColor: cssColor('--ta-surface'),
          borderWidth: 1,
        },
        data: series.map(value),
      });
      return {
        ...c,
        tooltip: { ...c.tooltip, valueFormatter: (v: number) => hours(v) },
        xAxis: { type: 'category', data: series.map((d) => dayLabel(d.date)), ...c.category },
        yAxis: { type: 'value', ...c.value },
        series: [
          bar('Booked', '--ta-series-1', (d) => d.booked_h),
          bar('Unassigned', '--ta-series-2', (d) => d.unassigned_h),
          bar('Free', '--ta-series-3', (d) => d.free_h),
        ],
      };
    };
  });

  protected readonly utilizationChart = computed(() => {
    const series = this.capacity.value()?.series ?? [];
    return (): ChartOptions => {
      const c = chrome();
      return {
        ...c,
        grid: { ...c.grid, top: 16 },
        legend: { show: false },
        tooltip: { ...c.tooltip, valueFormatter: (v: number) => percent(v) },
        xAxis: { type: 'category', data: series.map((d) => dayLabel(d.date)), ...c.category },
        yAxis: {
          type: 'value',
          min: 0,
          max: 1,
          axisLabel: { ...c.value.axisLabel, formatter: (v: number) => percent(v) },
          splitLine: c.value.splitLine,
        },
        series: [
          {
            name: 'Utilisation',
            type: 'line',
            showSymbol: false,
            lineStyle: { width: 2, color: cssColor('--ta-series-1') },
            itemStyle: { color: cssColor('--ta-series-1') },
            data: series.map((d) => d.utilization),
          },
        ],
      };
    };
  });

  protected readonly outcomeChart = computed(() => {
    const days = this.outcomes.value()?.days ?? [];
    const kind = this.kind();
    const lines = kind === 'job' ? outcomeLines : outcomeLines.slice(0, 3);
    return (): ChartOptions => {
      const c = chrome();
      const provisional = days.filter((d) => d.provisional).map((d) => dayLabel(d.date));
      return {
        ...c,
        tooltip: { ...c.tooltip, valueFormatter: (v: number | null) => percent(v) },
        xAxis: { type: 'category', data: days.map((d) => dayLabel(d.date)), ...c.category },
        yAxis: {
          type: 'value',
          min: 0,
          max: 1,
          axisLabel: { ...c.value.axisLabel, formatter: (v: number) => percent(v) },
          splitLine: c.value.splitLine,
        },
        series: lines.map((line, i) => ({
          name: line.name,
          type: 'line',
          symbolSize: 8,
          showSymbol: days.length <= 31,
          lineStyle: { width: 2, color: cssColor(`--ta-series-${i + 1}`) },
          itemStyle: { color: cssColor(`--ta-series-${i + 1}`) },
          data: days.map((d) => {
            const stats = d[kind];
            return stats ? (line.rate(stats) ?? null) : null;
          }),
          // Days whose outcome can still change are shaded behind the lines.
          markArea:
            i === 0 && provisional.length
              ? {
                  silent: true,
                  itemStyle: { color: cssColor('--ta-grid'), opacity: 0.6 },
                  label: {
                    show: true,
                    position: 'insideTop',
                    color: cssColor('--ta-muted'),
                    formatter: 'provisional',
                  },
                  data: [
                    [{ xAxis: provisional[0] }, { xAxis: provisional[provisional.length - 1] }],
                  ],
                }
              : undefined,
        })),
      };
    };
  });

  protected readonly regionRows = computed(() =>
    (this.outcomes.value()?.by_region ?? []).map((r) =>
      statsRow(regionName(r.region), r[this.kind()]),
    ),
  );
  protected readonly techRows = computed(() =>
    (this.outcomes.value()?.by_tech ?? []).map((t) => statsRow(t.tech_name, t[this.kind()])),
  );
  protected readonly totals = computed(() => {
    const totals = this.outcomes.value()?.totals;
    return totals ? statsRow('All', totals[this.kind()]) : null;
  });
  protected readonly percent = percent;
  protected readonly hours = hours;
  protected readonly regionName = regionName;
}
