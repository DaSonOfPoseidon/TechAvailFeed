import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { catchError, from, switchMap, throwError } from 'rxjs';
import { ApiKeyDialog } from './api-key-dialog';

const storageKey = 'techavail.apiKey';

// The API key (when the API has API_KEY set), kept in this browser's localStorage.
@Injectable({ providedIn: 'root' })
export class ApiKey {
  private readonly dialog = inject(MatDialog);
  private pending: Promise<string | null> | null = null;

  get value(): string {
    try {
      return localStorage.getItem(storageKey) ?? '';
    } catch {
      return '';
    }
  }

  set value(key: string) {
    try {
      localStorage.setItem(storageKey, key);
    } catch {
      // Private mode: the key lasts until the page reloads.
    }
  }

  // Asks once for a key, however many requests were refused meanwhile.
  ask(): Promise<string | null> {
    this.pending ??= new Promise<string | null>((resolve) =>
      this.dialog
        .open(ApiKeyDialog, { disableClose: true })
        .afterClosed()
        .subscribe((key?: string) => {
          this.pending = null;
          if (key) this.value = key;
          resolve(key || null);
        }),
    );
    return this.pending;
  }
}

// Sends X-API-Key on /api calls; a 401 asks for the key and retries once.
export const apiKeyInterceptor: HttpInterceptorFn = (request, next) => {
  if (!request.url.startsWith('/api/')) return next(request);
  const keys = inject(ApiKey);
  const send = (key: string) =>
    next(key ? request.clone({ setHeaders: { 'X-API-Key': key } }) : request);
  return send(keys.value).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse) || error.status !== 401)
        return throwError(() => error);
      return from(keys.ask()).pipe(switchMap((key) => (key ? send(key) : throwError(() => error))));
    }),
  );
};
