import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { SaveDocumentRequest } from '../models/document.model';

export interface UploadDocumentRequest {
  name: string;
  mimeType: string;
  content: string;
  createdBy?: string;
}

export interface UploadDocumentResult {
  masterId: string;
  versionId: string;
  fileName: string;
  createdAt: string;
}

export interface SaveDocumentVersionRequest {
  content: string;
  createdBy?: string;
}

export interface SaveDocumentVersionResult {
  versionId: string;
  versionNumber: number;
  createdAt: string;
}

export interface UpdateDocumentVersionResult {
  versionId: string;
  versionNumber: number;
  sizeInBytes: number;
  modifiedAt: string;
}

export interface DocumentDto {
  masterId: string;
  name: string;
  mimeType: string;
  createdAt: string;
  activeVersionId: string;
  content: string;
  versionNumber: number;
}

export interface DocumentMetadataDto {
  masterId: string;
  mimeType: string;
  returnUrl: string | null;
  classification: string | null;
  userDownload: boolean;
  showSaveState: boolean;
}

export interface DocumentVersionDto {
  versionId: string;
  versionNumber: number;
  createdAt: string;
  createdBy: string;
  isActive: boolean;
  sizeInBytes: number;
}

export interface RestoreVersionResponse {
  message: string;
  versionId: string;
}

export type DeliveryStatus =
  | 'Pending'
  | 'Sending'
  | 'RetryScheduled'
  | 'Sent'
  | 'FailedPermanently'
  | 'DeadLettered'
  | 'Cancelled';

export interface FinishAndSendResult {
  deliveryId: string;
  status: DeliveryStatus;
  documentStatus: string;
  delivered: boolean;
  error: string | null;
}

export interface AbortSendResult {
  masterId: string;
  documentStatus: string;
  deliveryStatus: string | null;
}

export interface ContinueDeliveryResult {
  masterId: string;
  documentStatus: string;
  deliveryId: string;
  deliveryStatus: DeliveryStatus;
}

export interface DeliveryStatusDto {
  deliveryId: string;
  documentId: string;
  status: DeliveryStatus;
  attemptCount: number;
  lastAttemptAt: string | null;
  nextAttemptAt: string | null;
  lastError: string | null;
  updatedAt: string;
}

export interface DeliveryListItem {
  deliveryId: string;
  documentId: string;
  status: DeliveryStatus;
  attemptCount: number;
  createdAt: string;
  lastAttemptAt: string | null;
  nextAttemptAt: string | null;
  deadlineAt: string;
  lastError: string | null;
  lockedUntil: string | null;
  lockedBy: string | null;
  sourceVersionId: string;
  recipientUrl: string;
  corporateKey: string | null;
}

export interface RequeueDeliveryResult {
  deliveryId: string;
  status: DeliveryStatus;
}

export interface UpdateDeliveryRecipientUrlResult {
  deliveryId: string;
  recipientUrl: string;
  status: DeliveryStatus;
}

@Injectable({
  providedIn: 'root'
})
export class DocumentStorageService {
  private readonly http = inject(HttpClient);
  private readonly apiUrl = `${environment.apiUrl}/documentstorage`;

  uploadDocument(request: UploadDocumentRequest): Observable<UploadDocumentResult> {
    return this.http.post<UploadDocumentResult>(`${this.apiUrl}/upload`, request);
  }

  saveDocumentVersion(
    masterId: string,
    request: SaveDocumentVersionRequest
  ): Observable<SaveDocumentVersionResult> {
    return this.http.post<SaveDocumentVersionResult>(
      `${this.apiUrl}/${masterId}/save`,
      request
    );
  }

  updateDocumentVersion(
    masterId: string,
    versionId: string,
    request: SaveDocumentVersionRequest
  ): Observable<UpdateDocumentVersionResult> {
    return this.http.put<UpdateDocumentVersionResult>(
      `${this.apiUrl}/${masterId}/versions/${versionId}`,
      request
    );
  }

  getDocument(masterId: string): Observable<DocumentDto> {
    return this.http.get<DocumentDto>(`${this.apiUrl}/${masterId}`);
  }

  getDocumentMetadata(masterId: string): Observable<DocumentMetadataDto> {
    return this.http.get<DocumentMetadataDto>(`${this.apiUrl}/${masterId}/metadata`);
  }

  downloadBaseVersion(masterId: string): Observable<Blob> {
    return this.http.get(`${this.apiUrl}/${masterId}/download`, { responseType: 'blob' });
  }

  downloadVersion(masterId: string, versionId: string): Observable<Blob> {
    return this.http.get(`${this.apiUrl}/${masterId}/versions/${versionId}/download`, { responseType: 'blob' });
  }

  getDocumentVersions(masterId: string): Observable<DocumentVersionDto[]> {
    return this.http.get<DocumentVersionDto[]>(`${this.apiUrl}/${masterId}/versions`);
  }

  restoreDocumentVersion(
    masterId: string,
    versionId: string
  ): Observable<RestoreVersionResponse> {
    return this.http.post<RestoreVersionResponse>(
      `${this.apiUrl}/${masterId}/restore/${versionId}`,
      {}
    );
  }

  finishAndSend(
    masterId: string,
    versionId: string,
    request: SaveDocumentVersionRequest
  ): Observable<FinishAndSendResult> {
    return this.http.post<FinishAndSendResult>(
      `${this.apiUrl}/${masterId}/versions/${versionId}/finish`,
      request
    );
  }

  abortSend(masterId: string): Observable<AbortSendResult> {
    return this.http.post<AbortSendResult>(`${this.apiUrl}/${masterId}/abort-send`, {});
  }

  continueDelivery(masterId: string): Observable<ContinueDeliveryResult> {
    return this.http.post<ContinueDeliveryResult>(`${this.apiUrl}/${masterId}/continue-delivery`, {});
  }

  getDeliveryStatus(deliveryId: string): Observable<DeliveryStatusDto> {
    return this.http.get<DeliveryStatusDto>(`${this.apiUrl}/deliveries/${deliveryId}`);
  }

  getDeliveries(status: DeliveryStatus | null = null, skip = 0, take = 100): Observable<DeliveryListItem[]> {
    let params = new HttpParams()
      .set('skip', skip)
      .set('take', take);
    if (status) {
      params = params.set('status', status);
    }
    return this.http.get<DeliveryListItem[]>(`${this.apiUrl}/deliveries`, { params });
  }

  retryDelivery(deliveryId: string): Observable<RequeueDeliveryResult> {
    return this.http.post<RequeueDeliveryResult>(`${this.apiUrl}/deliveries/${deliveryId}/retry`, {});
  }

  downloadDeliveryFile(deliveryId: string): Observable<Blob> {
    return this.http.get(`${this.apiUrl}/deliveries/${deliveryId}/download`, { responseType: 'blob' });
  }

  cancelDelivery(deliveryId: string): Observable<RequeueDeliveryResult> {
    return this.http.post<RequeueDeliveryResult>(`${this.apiUrl}/deliveries/${deliveryId}/cancel`, {});
  }

  updateDeliveryRecipientUrl(deliveryId: string, recipientUrl: string): Observable<UpdateDeliveryRecipientUrlResult> {
    return this.http.put<UpdateDeliveryRecipientUrlResult>(
      `${this.apiUrl}/deliveries/${deliveryId}/recipient-url`, { recipientUrl });
  }

  fileToBase64(file: File): Promise<string> {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.readAsDataURL(file);
      reader.onload = () => {
        const base64 = reader.result as string;
        const base64Content = base64.split(',')[1];
        resolve(base64Content);
      };
      reader.onerror = error => reject(error);
    });
  }

  base64ToBlob(base64: string, mimeType: string): Blob {
    const byteCharacters = atob(base64);
    const byteNumbers = new Array(byteCharacters.length);
    for (let i = 0; i < byteCharacters.length; i++) {
      byteNumbers[i] = byteCharacters.charCodeAt(i);
    }
    const byteArray = new Uint8Array(byteNumbers);
    return new Blob([byteArray], { type: mimeType });
  }

  downloadDocument(doc: DocumentDto): void {
    const blob = this.base64ToBlob(doc.content, doc.mimeType);
    const url = window.URL.createObjectURL(blob);
    const link = window.document.createElement('a');
    link.href = url;
    link.download = doc.name;
    link.click();
    window.URL.revokeObjectURL(url);
  }

  downloadEditedDocument(masterId: string, request: SaveDocumentRequest): Observable<Blob> {
    return this.http.post(`${this.apiUrl}/${masterId}/user-download`, request, {
      responseType: 'blob'
    });
  }

  formatFileSize(bytes: number): string {
    if (bytes === 0) return '0 Bytes';
    
    const k = 1024;
    const sizes = ['Bytes', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    
    return Math.round((bytes / Math.pow(k, i)) * 100) / 100 + ' ' + sizes[i];
  }
}
