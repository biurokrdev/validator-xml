import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { EnvelopeType, LetterInspection, ProblemDetails } from './models';

/** Klient REST dla api/letter-inspections. Wywołania idą przez proxy (/api). */
@Injectable({ providedIn: 'root' })
export class LetterInspectionsService {
  private static readonly BASE = '/api/letter-inspections';

  private readonly http = inject(HttpClient);

  /** Wysyła PDF jako multipart/form-data; API sprawdza pierwszą stronę biblioteką Mass.AddressWindow.Pdf. */
  inspect(file: File, envelope: EnvelopeType): Observable<LetterInspection> {
    const form = new FormData();
    form.append('file', file, file.name);
    form.append('envelope', envelope);

    return this.http.post<LetterInspection>(LetterInspectionsService.BASE, form);
  }

  /** Czytelny komunikat z ProblemDetails albo błędu sieci. */
  static errorMessage(err: unknown): string {
    if (err instanceof HttpErrorResponse) {
      if (err.status === 0) return 'Brak połączenia z API.';
      if (err.status === 413) return 'Plik jest za duży.';

      const problem = err.error as ProblemDetails | null;
      if (problem?.errors) {
        return Object.values(problem.errors).flat().join(' ');
      }
      if (problem?.detail || problem?.title) {
        return [problem.title, problem.detail].filter(Boolean).join(' ');
      }
      return `Błąd HTTP ${err.status}.`;
    }
    return 'Nieoczekiwany błąd.';
  }
}
