import { Injectable, inject } from '@angular/core';
import { Router } from '@angular/router';

export const MIME_PDF = 'application/pdf';
export const MIME_DOCX = 'application/vnd.openxmlformats-officedocument.wordprocessingml.document';
export const MIME_DOC = 'application/msword';

export const OPENED_FILE_NAME_STATE_KEY = 'openedFileName';

export interface EditableNavigationOptions {
  openedFileName?: string;
}

@Injectable({ providedIn: 'root' })
export class DocumentNavigationService {
  private router = inject(Router);

  navigateToDocument(masterId: string, mimeType: string): void {
    this.router.navigateByUrl(this.documentUrl(masterId, mimeType));
  }

  documentUrl(masterId: string, mimeType: string): string {
    const path = this.isPdf(mimeType) ? '/viewer' : '/editor';
    return this.router.serializeUrl(this.router.createUrlTree([path], { queryParams: { masterId } }));
  }

  navigateToEditableDocument(masterId: string, versionId: string, options?: EditableNavigationOptions): void {
    const state = options?.openedFileName ? { [OPENED_FILE_NAME_STATE_KEY]: options.openedFileName } : undefined;
    this.router.navigate(['/editor'], { queryParams: { masterId, versionId }, state });
  }

  isPdf(mimeType: string): boolean {
    return mimeType === MIME_PDF || mimeType.toLowerCase().includes('pdf');
  }

  isWordDocument(mimeType: string): boolean {
    return mimeType === MIME_DOCX || mimeType === MIME_DOC;
  }
}
