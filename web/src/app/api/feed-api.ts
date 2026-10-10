import { HttpClient, HttpParams, HttpResourceRef, HttpResponse } from '@angular/common/http';
import { Injectable, Signal, inject, linkedSignal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type Query = Record<string, string | number | null | undefined>;

// A resource's latest loaded value, kept while it refetches (each new snapshot refetches it) or
// after a failed refetch, so dropdowns built from it don't empty for a moment.
export function lastValue<T>(resource: HttpResourceRef<T | undefined>): Signal<T | undefined> {
  return linkedSignal<T | undefined, T | undefined>({
    source: () => (resource.hasValue() ? resource.value() : undefined),
    computation: (value, previous) => value ?? previous?.value,
  });
}

// The query string for an API call, without the unset values.
export function params(query: Query): HttpParams {
  let result = new HttpParams();
  for (const [key, value] of Object.entries(query)) {
    if (value !== null && value !== undefined && value !== '') result = result.set(key, value);
  }
  return result;
}

// The filename from a Content-Disposition header (the API sends filename="...").
export function filenameFrom(header: string | null, fallback: string): string {
  const match = header?.match(/filename="?([^";]+)"?/);
  return match ? match[1] : fallback;
}

@Injectable({ providedIn: 'root' })
export class FeedApi {
  private readonly http = inject(HttpClient);

  // Fetches a workbook (through HttpClient, so the API key is sent) and saves it.
  async download(path: string, query: Query): Promise<void> {
    const response: HttpResponse<Blob> = await firstValueFrom(
      this.http.get(`/api/v1/${path}`, {
        params: params(query),
        observe: 'response',
        responseType: 'blob',
      }),
    );
    const link = document.createElement('a');
    link.href = URL.createObjectURL(response.body!);
    link.download = filenameFrom(response.headers.get('Content-Disposition'), path);
    link.click();
    URL.revokeObjectURL(link.href);
  }
}

// The {"detail": ...} of an API error, also when it came back as a Blob (a failed download).
export async function errorDetail(error: unknown): Promise<string> {
  const body = (error as { error?: unknown })?.error;
  if (body instanceof Blob) {
    try {
      return JSON.parse(await body.text()).detail ?? 'The download failed.';
    } catch {
      return 'The download failed.';
    }
  }
  return (body as { detail?: string })?.detail ?? 'The API is not reachable.';
}
