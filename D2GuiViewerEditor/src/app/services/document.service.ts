import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Observable, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { 
  DocumentContent, 
  SaveDocumentRequest, 
  SignDocumentRequest,
  DocumentTemplate, 
  ImageUploadResponse,
  DigitalSignatureInfo
} from '../models/document.model';
import { ApiConfigService } from '../core/services/api-config.service';

export type OpenDocumentErrorCode =
  | 'PASSWORD_REQUIRED'
  | 'WRONG_PASSWORD'
  | 'UNSUPPORTED_LEGACY_DOC'
  | 'DOCUMENT_CONTENT_EMPTY';

export class OpenDocumentError extends Error {
  constructor(message: string, public readonly code?: OpenDocumentErrorCode) {
    super(message);
    this.name = 'OpenDocumentError';
  }
}

@Injectable({
  providedIn: 'root'
})
export class DocumentService {
  private http = inject(HttpClient);
  private apiConfig = inject(ApiConfigService);

  private get apiUrl(): string {
    return this.apiConfig.documentUrl;
  }

  openDocument(file: File, password?: string): Observable<DocumentContent> {
    const formData = new FormData();
    formData.append('file', file);
    if (password) formData.append('password', password);

    return this.http.post<DocumentContent>(`${this.apiUrl}/open`, formData)
      .pipe(catchError((err: HttpErrorResponse) => {
        const code: OpenDocumentErrorCode | undefined = err.error?.code;
        const message = err.error?.error ?? 'Nie udało się otworzyć dokumentu.';
        return throwError(() => new OpenDocumentError(message, code));
      }));
  }

  saveDocument(request: SaveDocumentRequest): Observable<Blob> {
    return this.http.post(`${this.apiUrl}/save`, request, {
      responseType: 'blob'
    }).pipe(catchError(this.handleError));
  }

  generatePdf(request: SaveDocumentRequest): Observable<ArrayBuffer> {
    return this.http.post(`${this.apiUrl}/generate-pdf`, request, {
      responseType: 'arraybuffer'
    }).pipe(catchError(this.handleError));
  }

  newDocument(): Observable<DocumentContent> {
    return this.http.get<DocumentContent>(`${this.apiUrl}/new`)
      .pipe(catchError(this.handleError));
  }

  getTemplates(): Observable<DocumentTemplate[]> {
    return this.http.get<DocumentTemplate[]>(`${this.apiUrl}/templates`)
      .pipe(catchError(this.handleError));
  }

  getTemplate(templateId: string): Observable<DocumentContent> {
    return this.http.get<DocumentContent>(`${this.apiUrl}/templates/${templateId}`)
      .pipe(catchError(this.handleError));
  }

  uploadImage(file: File): Observable<ImageUploadResponse> {
    const formData = new FormData();
    formData.append('file', file);

    return this.http.post<ImageUploadResponse>(`${this.apiUrl}/upload-image`, formData)
      .pipe(catchError(this.handleError));
  }

  downloadDocument(request: SaveDocumentRequest, filename: string): void {
    this.saveDocument(request).subscribe({
      next: (blob) => {
        const url = window.URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename.endsWith('.docx') ? filename : `${filename}.docx`;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        window.URL.revokeObjectURL(url);
      },
      error: (error) => {
        console.error('Błąd podczas pobierania dokumentu:', error);
      }
    });
  }

  signDocument(request: SignDocumentRequest): Observable<Blob> {
    return this.http.post(`${this.apiUrl}/sign`, request, {
      responseType: 'blob'
    }).pipe(catchError((error) => {
      if (error.error instanceof Blob) {
        return new Observable<never>(observer => {
          const reader = new FileReader();
          reader.onload = () => {
            try {
              const json = JSON.parse(reader.result as string);
              observer.error(new Error(json.error || 'Błąd podpisywania dokumentu'));
            } catch {
              observer.error(new Error('Błąd podpisywania dokumentu'));
            }
          };
          reader.readAsText(error.error);
        });
      }
      return this.handleError(error);
    }));
  }

  downloadSignedDocument(request: SignDocumentRequest, filename: string): void {
    this.signDocument(request).subscribe({
      next: (blob) => {
        const url = window.URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = filename.endsWith('.docx') ? filename : `${filename}.docx`;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        window.URL.revokeObjectURL(url);
      },
      error: (error) => {
        console.error('Błąd podczas podpisywania dokumentu:', error);
      }
    });
  }

  verifySignatures(file: File): Observable<DigitalSignatureInfo[]> {
    const formData = new FormData();
    formData.append('file', file);
    return this.http.post<DigitalSignatureInfo[]>(`${this.apiUrl}/verify-signatures`, formData)
      .pipe(catchError(this.handleError));
  }

  private handleError(error: HttpErrorResponse): Observable<never> {
    let errorMessage = 'Wystąpił nieznany błąd';

    if (error.error instanceof ErrorEvent) {
      errorMessage = `Błąd: ${error.error.message}`;
    } else {
      if (error.error?.error) {
        errorMessage = error.error.error;
      } else {
        errorMessage = `Błąd serwera: ${error.status} - ${error.statusText}`;
      }
    }

    console.error('DocumentService Error:', errorMessage);
    return throwError(() => new Error(errorMessage));
  }
}
