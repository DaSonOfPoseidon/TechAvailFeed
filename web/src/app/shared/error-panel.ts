import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';

// A failed request, in words: the API's own detail when it sent one.
@Component({
  selector: 'app-error-panel',
  imports: [MatButtonModule],
  template: `
    <div class="panel" role="alert">
      <h2>{{ title() }}</h2>
      <p class="muted">{{ detail() }}</p>
      <button mat-stroked-button (click)="retry.emit()">Try again</button>
    </div>
  `,
})
export class ErrorPanel {
  readonly error = input<unknown>();
  readonly retry = output();

  private readonly status = computed(
    () => (this.error() as HttpErrorResponse | undefined)?.status ?? 0,
  );

  protected readonly title = computed(() => {
    switch (this.status()) {
      case 0:
        return 'The API is not reachable';
      case 401:
        return 'This needs the API key';
      case 404:
        return 'No data yet';
      default:
        return 'The request failed';
    }
  });

  protected readonly detail = computed(() => {
    const body = (this.error() as HttpErrorResponse | undefined)?.error as {
      detail?: string;
    } | null;
    if (body?.detail) return body.detail;
    return this.status() === 0
      ? 'Check that the api container is running.'
      : `Status ${this.status()}.`;
  });
}
