import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';
import { ApiKey, apiKeyInterceptor } from './api-key';

describe('apiKeyInterceptor', () => {
  let key: { value: string; ask: ReturnType<typeof vi.fn> };

  beforeEach(() => {
    key = { value: '', ask: vi.fn().mockResolvedValue('secret') };
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiKeyInterceptor])),
        provideHttpClientTesting(),
        { provide: ApiKey, useValue: key },
      ],
    });
  });

  it('asks for the key on a 401 and retries with it', async () => {
    const http = TestBed.inject(HttpClient);
    const backend = TestBed.inject(HttpTestingController);
    const result = firstValueFrom(http.get('/api/v1/filters'));
    const first = backend.expectOne('/api/v1/filters');
    expect(first.request.headers.has('X-API-Key')).toBe(false);
    first.flush(
      { detail: 'missing or wrong X-API-Key' },
      { status: 401, statusText: 'Unauthorized' },
    );
    // The retry carries the key the dialog returned.
    const retry = await vi.waitFor(() => backend.expectOne('/api/v1/filters'));
    expect(retry.request.headers.get('X-API-Key')).toBe('secret');
    retry.flush({ ok: true });
    expect(key.ask).toHaveBeenCalledOnce();
    expect(await result).toEqual({ ok: true });
  });

  it('sends a saved key and leaves other URLs alone', () => {
    key.value = 'saved';
    const http = TestBed.inject(HttpClient);
    const backend = TestBed.inject(HttpTestingController);
    http.get('/api/v1/calendar').subscribe();
    http.get('/health').subscribe();
    expect(backend.expectOne('/api/v1/calendar').request.headers.get('X-API-Key')).toBe('saved');
    expect(backend.expectOne('/health').request.headers.has('X-API-Key')).toBe(false);
  });
});

describe('ApiKey', () => {
  afterEach(() => vi.restoreAllMocks());

  it('keeps the key in memory when storage is blocked', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('blocked', 'SecurityError');
    });
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('blocked', 'SecurityError');
    });
    TestBed.configureTestingModule({ providers: [{ provide: MatDialog, useValue: {} }] });
    const key = TestBed.inject(ApiKey);
    key.value = 'secret';
    expect(key.value).toBe('secret');
  });
});
