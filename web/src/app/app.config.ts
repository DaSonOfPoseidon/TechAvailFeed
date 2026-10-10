import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import { MatIconRegistry } from '@angular/material/icon';
import {
  provideRouter,
  withComponentInputBinding,
  withNavigationErrorHandler,
} from '@angular/router';
import { apiKeyInterceptor } from './api/api-key';
import { routes } from './app.routes';
import { reloadOnStaleBundle } from './shared/stale-bundle';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // index.html loads Material Symbols Outlined. MatIcon picks it up by sniffing loaded fonts,
    // which misses a slow stylesheet, so name it.
    provideAppInitializer(() => {
      inject(MatIconRegistry).setDefaultFontSetClass(
        'material-symbols-outlined',
        'mat-ligature-font',
      );
    }),
    provideHttpClient(withInterceptors([apiKeyInterceptor])),
    provideRouter(
      routes,
      withComponentInputBinding(),
      withNavigationErrorHandler(reloadOnStaleBundle),
    ),
  ],
};
