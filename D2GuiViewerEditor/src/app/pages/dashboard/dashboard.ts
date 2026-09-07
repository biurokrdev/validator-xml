import { Component, inject, signal, computed, ChangeDetectionStrategy } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { switchMap, map, from, Subscription } from 'rxjs';
import { MsalService } from '@azure/msal-angular';
import type { AccountInfo } from '@azure/msal-browser';
import { DocumentService } from '../../services/document.service';
import { DocumentStorageService } from '../../services/document-storage.service';
import { DocumentNavigationService } from '../../core/services/document-navigation.service';
import { ResourceAccessService } from '../../core/services/resource-access.service';
import { documentDefectMessage } from '../../core/errors/document-error.util';

@Component({
  selector: 'd2-dashboard',
  standalone: true,
  imports: [],
  templateUrl: './dashboard.html',
  changeDetection: ChangeDetectionStrategy.Eager,
  styleUrl: './dashboard.scss'
})
export class DashboardComponent {
  private documentService = inject(DocumentService);
  private documentStorageService = inject(DocumentStorageService);
  private documentNavigation = inject(DocumentNavigationService);
  private readonly resourceAccess = inject(ResourceAccessService);
  private readonly msal = inject(MsalService);

  isLoading = signal(false);
  errorMessage = signal<string | null>(null);
  readonly currentYear = new Date().getFullYear();

  readonly canUseEditor = toSignal(this.resourceAccess.hasAccessToResource('editor'), {
    initialValue: false,
  });

  readonly userFirstName = signal<string>(this.resolveFirstName());
  readonly greeting = computed(() => {
    const name = this.userFirstName();
    return name ? `Witaj ${name} w Doc2` : 'Witaj w Doc2';
  });

  private activeSubscription: Subscription | null = null;


  private ensureEditorAccess(): boolean {
    if (this.canUseEditor()) {
      return true;
    }
    this.errorMessage.set('Nie masz uprawnień do edycji dokumentów.');
    return false;
  }

  newDocument(): void {
    if (!this.ensureEditorAccess()) {
      return;
    }
    this.isLoading.set(true);
    this.errorMessage.set(null);

    this.activeSubscription = this.documentService.newDocument().pipe(
      switchMap(content =>
        this.documentService.saveDocument({ html: content.html, metadata: content.metadata }).pipe(
          switchMap(blob =>
            from(this.blobToBase64(blob)).pipe(
              switchMap(base64 =>
                this.documentStorageService.uploadDocument({
                  name: 'Nowy dokument.docx',
                  mimeType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
                  content: base64
                }).pipe(
                  switchMap(result =>
                    this.documentStorageService.saveDocumentVersion(result.masterId, { content: base64 }).pipe(
                      map(saved => ({ masterId: result.masterId, versionId: saved.versionId }))
                    )
                  )
                )
              )
            )
          )
        )
      )
    ).subscribe({
      next: ({ masterId, versionId }) => {
        this.documentNavigation.navigateToEditableDocument(masterId, versionId);
      },
      error: () => {
        this.isLoading.set(false);
        this.errorMessage.set('Błąd podczas tworzenia dokumentu. Spróbuj ponownie.');
      }
    });
  }

  cancelLoading(): void {
    this.activeSubscription?.unsubscribe();
    this.activeSubscription = null;
    this.isLoading.set(false);
  }

  openFile(): void {
    if (!this.ensureEditorAccess()) {
      return;
    }
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = '.docx,.doc,.pdf';

    input.onchange = async (e) => {
      const file = (e.target as HTMLInputElement).files?.[0];
      if (!file) return;

      const lowerName = file.name.toLowerCase();
      const isDoc = lowerName.endsWith('.doc');

      if (!lowerName.endsWith('.docx') && !isDoc && !lowerName.endsWith('.pdf')) {
        this.errorMessage.set('Obsługiwane są pliki DOCX, DOC i PDF.');
        return;
      }

      if (lowerName.endsWith('.pdf')) {
        this.isLoading.set(true);
        this.errorMessage.set(null);
        try {
          const base64 = await this.documentStorageService.fileToBase64(file);
      this.activeSubscription = this.documentStorageService.uploadDocument({
            name: file.name,
            mimeType: 'application/pdf',
            content: base64,
          }).subscribe({
            next: (result) => {
              this.documentNavigation.navigateToDocument(result.masterId, 'application/pdf');
            },
            error: (err) => {
              this.isLoading.set(false);
              this.errorMessage.set(documentDefectMessage(err)
                ?? 'Błąd podczas wczytywania pliku PDF. Spróbuj ponownie.');
            },
          });
        } catch {
          this.isLoading.set(false);
          this.errorMessage.set('Błąd podczas odczytu pliku PDF.');
        }
        return;
      }

      this.isLoading.set(true);
      this.errorMessage.set(null);

      try {
        const base64 = await this.documentStorageService.fileToBase64(file);
        const mimeType = isDoc
          ? 'application/msword'
          : (file.type || 'application/vnd.openxmlformats-officedocument.wordprocessingml.document');
        this.activeSubscription = this.documentStorageService.uploadDocument({
          name: file.name,
          mimeType,
          content: base64
        }).pipe(
          switchMap(result =>
            this.documentStorageService.saveDocumentVersion(result.masterId, { content: base64 }).pipe(
              map(saved => ({ masterId: result.masterId, versionId: saved.versionId }))
            )
          )
        ).subscribe({
          next: ({ masterId, versionId }) => {
            this.documentNavigation.navigateToEditableDocument(masterId, versionId);
          },
          error: (err) => {
            this.isLoading.set(false);
            this.errorMessage.set(documentDefectMessage(err)
              ?? 'Błąd podczas wczytywania dokumentu. Spróbuj ponownie.');
          }
        });
      } catch {
        this.isLoading.set(false);
        this.errorMessage.set('Błąd podczas odczytu pliku.');
      }
    };

    input.click();
  }

  private blobToBase64(blob: Blob): Promise<string> {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.readAsDataURL(blob);
      reader.onload = () => {
        const base64 = reader.result as string;
        resolve(base64.split(',')[1]);
      };
      reader.onerror = reject;
    });
  }

  private resolveFirstName(): string {
    const account: AccountInfo | null =
      this.msal.instance.getActiveAccount() ?? this.msal.instance.getAllAccounts()[0] ?? null;
    if (!account) return '';

    const fullName = account.name?.trim() ?? '';
    const parenthesized = fullName.match(/\(([^)]+)\)/)?.[1]?.trim();
    if (parenthesized) return parenthesized;

    const givenName = (account.idTokenClaims as { given_name?: unknown } | undefined)?.given_name;
    if (typeof givenName === 'string' && givenName.trim()) return givenName.trim();

    return fullName ? fullName.split(' ')[0] : '';
  }
}

