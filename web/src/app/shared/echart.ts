import {
  DestroyRef,
  Directive,
  ElementRef,
  Signal,
  afterRenderEffect,
  inject,
  input,
  signal,
} from '@angular/core';
import { BarChart, LineChart } from 'echarts/charts';
import {
  GridComponent,
  LegendComponent,
  MarkAreaComponent,
  TooltipComponent,
} from 'echarts/components';
import * as echarts from 'echarts/core';
import { SVGRenderer } from 'echarts/renderers';

echarts.use([
  BarChart,
  LineChart,
  GridComponent,
  LegendComponent,
  MarkAreaComponent,
  TooltipComponent,
  SVGRenderer,
]);

export type ChartOptions = echarts.EChartsCoreOption;

// A CSS colour token resolved to rgb(), for ECharts (which can't read var() or light-dark()).
export function cssColor(token: string): string {
  const probe = document.createElement('span');
  probe.style.color = `var(${token})`;
  document.body.append(probe);
  const color = getComputedStyle(probe).color;
  probe.remove();
  return color;
}

const scheme = window.matchMedia('(prefers-color-scheme: dark)');

// Whether the OS/browser is in dark mode, updating live. Anything that bakes cssColor() values
// into a canvas or inline styles reads it, so it redraws when the scheme changes. Call it in an
// injection context.
export function darkMode(): Signal<boolean> {
  const dark = signal(scheme.matches);
  const onScheme = () => dark.set(scheme.matches);
  scheme.addEventListener('change', onScheme);
  inject(DestroyRef).onDestroy(() => scheme.removeEventListener('change', onScheme));
  return dark.asReadonly();
}

// The chart chrome shared by every chart: recessive axes, hairline grid, text in ink tokens.
export function chrome() {
  const muted = cssColor('--ta-muted');
  const grid = cssColor('--ta-grid');
  const axis = cssColor('--ta-axis');
  const ink = cssColor('--ta-ink');
  const surface = cssColor('--ta-surface');
  const font = 'Barlow, system-ui, sans-serif';
  return {
    textStyle: { fontFamily: font, color: ink },
    grid: { left: 48, right: 16, top: 40, bottom: 32, containLabel: false },
    legend: {
      top: 0,
      left: 0,
      icon: 'roundRect',
      itemWidth: 12,
      itemHeight: 12,
      textStyle: { color: ink, fontFamily: font },
    },
    tooltip: {
      trigger: 'axis',
      backgroundColor: surface,
      borderColor: axis,
      textStyle: { color: ink, fontFamily: font },
      axisPointer: { type: 'line', lineStyle: { color: axis } },
    },
    category: {
      axisLine: { lineStyle: { color: axis } },
      axisTick: { show: false },
      axisLabel: { color: muted },
    },
    value: {
      splitLine: { lineStyle: { color: grid } },
      axisLabel: { color: muted },
    },
  };
}

// <div [appEchart]="options"> renders an ECharts chart, resizing with its box and re-rendering
// when the colour scheme changes. Options are a function, so the colours are read at render time.
@Directive({ selector: '[appEchart]' })
export class EChart {
  readonly appEchart = input.required<() => ChartOptions>();
  private readonly element = inject(ElementRef<HTMLElement>);
  private readonly dark = darkMode();

  constructor() {
    const chart = echarts.init(this.element.nativeElement, null, { renderer: 'svg' });
    const resize = new ResizeObserver(() => chart.resize());
    resize.observe(this.element.nativeElement);
    afterRenderEffect(() => {
      this.dark();
      chart.setOption(this.appEchart()(), { notMerge: true });
    });
    inject(DestroyRef).onDestroy(() => {
      resize.disconnect();
      chart.dispose();
    });
  }
}
