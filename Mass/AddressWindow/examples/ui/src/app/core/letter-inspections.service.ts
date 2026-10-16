import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { EnvelopeType, FormCheck, LetterInspection, LetterOrder, ProblemDetails } from './models';

// Klient REST dla api/letter-inspections. Wywołania idą przez proxy (/api).
@Injectable({ providedIn: 'root' })
export class LetterInspectionsService {
  private static readonly BASE = '/api/letter-inspections';

  private readonly http = inject(HttpClient);

  // Wariant 1: dokument wybrany przez użytkownika (PDF albo DOCX; DOCX konwertuje API).
  upload(file: File, envelope: EnvelopeType): Observable<LetterInspection> {
    const form = new FormData();
    form.append('file', file, file.name);
    form.append('envelope', envelope);
    return this.http.post<LetterInspection>(`${LetterInspectionsService.BASE}/upload`, form);
  }

  // Wariant 2: dokument z aplikacji zewnętrznej (mock) wygenerowany z podanych danych.
  fromRecipient(order: LetterOrder): Observable<LetterInspection> {
    return this.http.post<LetterInspection>(`${LetterInspectionsService.BASE}/from-recipient`, order);
  }

  // Wariant 3: dokument formularza o podanym identyfikatorze, pobrany z aplikacji zewnętrznej (mock).
  fromForm(check: FormCheck): Observable<LetterInspection> {
    return this.http.post<LetterInspection>(`${LetterInspectionsService.BASE}/from-form`, check);
  }

  static errorMessage(err: unknown): string {
    if (err instanceof HttpErrorResponse) {
      if (err.status === 0) return 'Brak połączenia z API.';
      const problem = err.error as ProblemDetails | null;
      if (problem?.errors) return Object.values(problem.errors).flat().join(' ');
      if (problem?.title || problem?.detail) return [problem.title, problem.detail].filter(Boolean).join(' ');
      return `Błąd HTTP ${err.status}.`;
    }
    return 'Nieoczekiwany błąd.';
  }
}
