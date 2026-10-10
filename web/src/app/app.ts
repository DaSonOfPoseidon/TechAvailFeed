import { Component, inject } from '@angular/core';
import { MatToolbarModule } from '@angular/material/toolbar';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Freshness } from './shared/freshness';
import { ExportMenu } from './shell/export-menu';
import { FreshnessChip } from './shell/freshness-chip';

@Component({
  selector: 'app-root',
  imports: [
    ExportMenu,
    FreshnessChip,
    MatToolbarModule,
    RouterLink,
    RouterLinkActive,
    RouterOutlet,
  ],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {
  protected readonly links = [
    { path: '/calendar', label: 'Capacity' },
    { path: '/kpis', label: 'Trends' },
    { path: '/diagnostics', label: 'Data quality' },
    { path: '/map', label: 'Map' },
  ];

  constructor() {
    inject(Freshness).start();
  }
}
