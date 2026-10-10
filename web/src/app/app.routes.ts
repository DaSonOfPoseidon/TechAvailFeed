import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'calendar' },
  {
    path: 'calendar',
    title: 'Capacity',
    loadComponent: () => import('./calendar/calendar-page').then((m) => m.CalendarPage),
  },
  { path: '**', redirectTo: 'calendar' },
];
