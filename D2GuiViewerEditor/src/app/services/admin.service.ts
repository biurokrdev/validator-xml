import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, forkJoin, map } from 'rxjs';
import { environment } from '../../environments/environment';

export interface DocumentListItem {
  masterId: string;
  name: string;
  mimeType: string;
  createdAt: string;
  activeVersionId: string;
  versionNumber: number;
  
  status: string;
  
  lastModifiedBy?: string | null;
}

export interface DocumentVersionListItem {
  versionId: string;
  versionNumber: number;
  createdAt: string;
  createdBy: string;
  isActive: boolean;
  sizeInBytes: number;
}

export interface DocumentWithVersions extends DocumentListItem {
  versions: DocumentVersionListItem[];
  expanded: boolean;
}

export interface AdminStats {
  totalDocuments: number;
  totalVersions: number;
  totalSizeBytes: number;
  documentsThisMonth: number;
  latestDocuments: DocumentListItem[];
}

@Injectable({
  providedIn: 'root'
})
export class AdminService {
  private readonly http = inject(HttpClient);
  private readonly apiUrl = `${environment.apiUrl}/documentstorage`;

  getAllDocuments(): Observable<DocumentListItem[]> {
    return this.http.get<DocumentListItem[]>(`${this.apiUrl}`);
  }

  getDocumentVersions(masterId: string): Observable<DocumentVersionListItem[]> {
    return this.http.get<DocumentVersionListItem[]>(`${this.apiUrl}/${masterId}/versions`);
  }

  deleteDocument(masterId: string): Observable<void> {
    return this.http.delete<void>(`${this.apiUrl}/${masterId}`);
  }

  getAdminStats(): Observable<AdminStats> {
    return this.getAllDocuments().pipe(
      map(docs => {
        const now = new Date();
        const thisMonth = docs.filter(d => {
          const created = new Date(d.createdAt);
          return created.getFullYear() === now.getFullYear() && created.getMonth() === now.getMonth();
        });

        const sorted = [...docs].sort(
          (a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime()
        );

        return {
          totalDocuments: docs.length,
          totalVersions: docs.reduce((sum, d) => sum + d.versionNumber, 0),
          totalSizeBytes: 0,
          documentsThisMonth: thisMonth.length,
          latestDocuments: sorted.slice(0, 5)
        };
      })
    );
  }

  downloadDocument(masterId: string): Observable<Blob> {
    return this.http.get(`${this.apiUrl}/${masterId}/download`, { responseType: 'blob' });
  }

  downloadDocumentVersion(masterId: string, versionId: string): Observable<Blob> {
    return this.http.get(`${this.apiUrl}/${masterId}/versions/${versionId}/download`, { responseType: 'blob' });
  }

}
