import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'calendar' },
  {
    path: 'calendar',
    title: 'Capacity',
    loadComponent: () => import('./calendar/calendar-page').then((m) => m.CalendarPage),
  },
  {
    path: 'calendar/:date',
    title: 'Day',
    loadComponent: () => import('./calendar/day-page').then((m) => m.DayPage),
  },
  {
    path: 'kpis',
    title: 'Trends',
    loadComponent: () => import('./kpis/kpis-page').then((m) => m.KpisPage),
  },
  { path: '**', redirectTo: 'calendar' },
];
