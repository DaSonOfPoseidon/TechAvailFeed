import { httpResource } from '@angular/common/http';
import {
  Component,
  ElementRef,
  afterRenderEffect,
  computed,
  inject,
  viewChild,
  DestroyRef,
  untracked,
} from '@angular/core';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import * as L from 'leaflet';
import { params } from '../api/feed-api';
import { MapPoint, MapResponse } from '../api/models';
import { FilterBar } from '../filters/filter-bar';
import { FilterState } from '../filters/filter-state';
import { cssColor } from '../shared/echart';
import { ErrorPanel } from '../shared/error-panel';
import { Freshness } from '../shared/freshness';
import { hhmm } from '../shared/time';

function escape(text: string): string {
  return text.replace(
    /[&<>"]/g,
    (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]!,
  );
}

export function popup(p: MapPoint): string {
  const techs = p.techs.length ? p.techs.map((t) => escape(t.tech_name)).join(', ') : 'Unassigned';
  return [
    `<strong>${p.kind === 'ticket' ? 'Ticket' : 'Job'} ${escape(p.ref_id)}</strong>`,
    `${escape(p.status)}, ${p.work_date} ${hhmm(p.starts_at)}–${hhmm(p.ends_at)}`,
    techs,
    escape(p.region),
  ].join('<br>');
}

// One day's jobs and tickets: filled dots are assigned, rings unassigned.
@Component({
  selector: 'app-map-page',
  imports: [
    ErrorPanel,
    FilterBar,
    MatButtonToggleModule,
    MatIconModule,
    MatProgressBarModule,
    MatSlideToggleModule,
  ],
  templateUrl: './map-page.html',
  styleUrl: './map-page.scss',
})
export class MapPage {
  protected readonly state = inject(FilterState);
  private readonly freshness = inject(Freshness);
  private readonly host = viewChild.required<ElementRef<HTMLElement>>('map');
  protected readonly kind = computed(() => this.state.get('kind'));
  // Completed work is shown unless the query string says completed=hide.
  protected readonly completed = computed(() => this.state.get('completed') !== 'hide');
  protected readonly result = httpResource<MapResponse>(() => {
    this.freshness.snapshotId();
    return {
      url: '/api/v1/map',
      params: params({
        start: this.state.start(),
        region: this.state.region(),
        kind: this.kind(),
        completed: this.completed() ? null : 'false',
      }),
    };
  });
  protected readonly hhmm = hhmm;

  constructor() {
    let map: L.Map | null = null;
    let layer: L.LayerGroup | null = null;
    // The filters the view was last fitted to: a new date, region or kind refits, a fresher
    // snapshot of the same filters keeps the user's pan and zoom.
    let fittedFor: string | null = null;
    afterRenderEffect(() => {
      const points = this.result.value()?.points;
      if (!map) {
        map = L.map(this.host().nativeElement, { center: [38.9, -92.3], zoom: 8 });
        L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
          maxZoom: 17,
          attribution: '&copy; OpenStreetMap contributors',
        }).addTo(map);
        layer = L.layerGroup().addTo(map);
      }
      // While a request is in flight, value() can still hold the previous filters' points.
      if (!points || this.result.isLoading()) return;
      layer!.clearLayers();
      const colors = { job: cssColor('--ta-series-1'), ticket: cssColor('--ta-series-2') };
      const surface = cssColor('--ta-surface');
      for (const p of points) {
        L.circleMarker([p.lat!, p.lon!], {
          radius: 6,
          color: p.assigned ? surface : colors[p.kind],
          weight: 2,
          fillColor: p.assigned ? colors[p.kind] : surface,
          fillOpacity: 1,
        })
          .bindPopup(popup(p))
          .addTo(layer!);
      }
      const filters = untracked(() =>
        [this.state.start(), this.state.region(), this.kind(), this.completed()].join('|'),
      );
      if (!points.length) fittedFor = null;
      else if (fittedFor !== filters) {
        map.fitBounds(L.latLngBounds(points.map((p) => [p.lat!, p.lon!] as L.LatLngTuple)), {
          padding: [24, 24],
        });
        fittedFor = filters;
      }
    });
    inject(DestroyRef).onDestroy(() => map?.remove());
  }
}
