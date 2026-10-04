import { HttpClient, HttpErrorResponse, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { Observable } from 'rxjs';
import {
  ListQuery,
  PagedResponse,
  PoolStatistics,
  PoolType,
  ProblemDetails,
  RangeCheckResponse,
  RangeImportResponse,
  RangeRequest,
  RegisteredNumber,
  StateAction,
  ValidationResponse,
} from './models';

/** Klient REST dla api/registered-numbers. Wszystkie wywołania idą przez proxy (/api). */
@Injectable({ providedIn: 'root' })
export class RegisteredNumbersService {
  private static readonly BASE = '/api/registered-numbers';
  private static readonly EDITOR_HEADER = 'X-Editor';

  private readonly http = inject(HttpClient);

  /** Autor zmian wysyłany w nagłówku X-Editor (do czasu podpięcia uwierzytelniania). */
  readonly editor = signal<string>('admin-ui');

  list(query: ListQuery): Observable<PagedResponse<RegisteredNumber>> {
    let params = new HttpParams()
      .set('page', String(query.page ?? 1))
      .set('pageSize', String(query.pageSize ?? 50));

    if (query.type) params = params.set('type', query.type);
    if (query.state) params = params.set('state', query.state);
    if (query.search?.trim()) params = params.set('search', query.search.trim());

    return this.http.get<PagedResponse<RegisteredNumber>>(RegisteredNumbersService.BASE, { params });
  }

  get(fullNumber: string): Observable<RegisteredNumber> {
    return this.http.get<RegisteredNumber>(`${RegisteredNumbersService.BASE}/${encodeURIComponent(fullNumber)}`);
  }

  stats(): Observable<PoolStatistics[]> {
    return this.http.get<PoolStatistics[]>(`${RegisteredNumbersService.BASE}/stats`);
  }

  validate(number: string): Observable<ValidationResponse> {
    return this.http.post<ValidationResponse>(`${RegisteredNumbersService.BASE}/validate`, { number });
  }

  /** Weryfikacja przedziału przed zasileniem: co jest nowe, co już mamy. Nie zapisuje. */
  checkRange(request: RangeRequest): Observable<RangeCheckResponse> {
    return this.http.post<RangeCheckResponse>(`${RegisteredNumbersService.BASE}/ranges/check`, request);
  }

  importRange(request: RangeRequest): Observable<RangeImportResponse> {
    return this.http.post<RangeImportResponse>(`${RegisteredNumbersService.BASE}/ranges/import`, request, {
      headers: this.editorHeaders(),
    });
  }

  changeState(fullNumber: string, action: StateAction): Observable<RegisteredNumber> {
    return this.http.post<RegisteredNumber>(
      `${RegisteredNumbersService.BASE}/${encodeURIComponent(fullNumber)}/state`,
      { action },
      { headers: this.editorHeaders() },
    );
  }

  acquireNext(type: PoolType): Observable<RegisteredNumber> {
    return this.http.post<RegisteredNumber>(`${RegisteredNumbersService.BASE}/acquire`, { type }, {
      headers: this.editorHeaders(),
    });
  }

  /** Czytelny komunikat z ProblemDetails albo błędu sieci. */
  static errorMessage(err: unknown): string {
    if (err instanceof HttpErrorResponse) {
      const problem = err.error as ProblemDetails | null;
      if (problem?.errors) {
        return Object.values(problem.errors).flat().join(' ');
      }
      if (problem?.detail || problem?.title) {
        return [problem.title, problem.detail].filter(Boolean).join(' ');
      }
      if (err.status === 0) return 'Brak połączenia z API.';
      return `Błąd HTTP ${err.status}.`;
    }
    return 'Nieoczekiwany błąd.';
  }

  private editorHeaders(): HttpHeaders {
    return new HttpHeaders({ [RegisteredNumbersService.EDITOR_HEADER]: this.editor() });
  }
}
