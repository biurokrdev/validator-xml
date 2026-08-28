import { Component, computed, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Subscription, interval } from 'rxjs';
import {
  DeliveryListItem,
  DeliveryStatus,
  DocumentStorageService
} from '../../../services/document-storage.service';

@Component({
  selector: 'd2-admin-deliveries',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './admin-deliveries.html',
  styleUrl: './admin-deliveries.scss'
})
export class AdminDeliveriesComponent implements OnInit, OnDestroy {
  private storage = inject(DocumentStorageService);
  private router = inject(Router);

  readonly statuses: DeliveryStatus[] = [
    'DeadLettered', 'FailedPermanently', 'RetryScheduled', 'Sending', 'Pending', 'Sent', 'Cancelled'
  ];

  private static readonly RefreshIntervalMs = 3000;
  private refreshSub?: Subscription;
  autoRefresh = signal(true);

  selectedStatus = signal<DeliveryStatus | 'all'>('all');

  private allDeliveries = signal<DeliveryListItem[]>([]);
  filterId       = signal('');
  filterDoc      = signal('');
  filterAtt      = signal('');
  filterCreated  = signal('');
  filterLock     = signal('');
  filterModifiedBy = signal('');
  currentPage = signal(0);
  readonly pageSize = 10;

  isLoading = signal(true);
  error = signal<string | null>(null);
  actionError = signal<string | null>(null);
  retryingId = signal<string | null>(null);
  cancelingId = signal<string | null>(null);
  downloadingId = signal<string | null>(null);
  notice = signal<string | null>(null);
  expandedId = signal<string | null>(null);

  editingId = signal<string | null>(null);
  editUrlValue = signal('');
  editError = signal<string | null>(null);
  savingEdit = signal(false);

  private filtered = computed(() => {
    const id       = this.filterId().toLowerCase().trim();
    const doc      = this.filterDoc().toLowerCase().trim();
    const att      = this.filterAtt().toLowerCase().trim();
    const created  = this.filterCreated().toLowerCase().trim();
    const lock     = this.filterLock().toLowerCase().trim();
    const modifiedBy = this.filterModifiedBy().toLowerCase().trim();
    return this.allDeliveries().filter(d => {
      if (id       && !d.deliveryId.toLowerCase().includes(id))                          return false;
      if (doc      && !d.documentId.toLowerCase().includes(doc))                         return false;
      if (att      && !String(d.attemptCount).includes(att))                             return false;
      if (created  && !this.formatDate(d.createdAt).toLowerCase().includes(created))     return false;
      if (lock     && !(d.lockedBy ?? '').toLowerCase().includes(lock))                  return false;
      if (modifiedBy && !(d.corporateKey ?? '').toLowerCase().includes(modifiedBy))      return false;
      return true;
    });
  });

  totalFiltered = computed(() => this.filtered().length);
  totalPages    = computed(() => Math.max(1, Math.ceil(this.totalFiltered() / this.pageSize)));

  deliveries = computed(() => {
    const start = this.currentPage() * this.pageSize;
    return this.filtered().slice(start, start + this.pageSize);
  });

  ngOnInit(): void {
    this.load();
    this.startAutoRefresh();
  }

  ngOnDestroy(): void {
    this.refreshSub?.unsubscribe();
  }

  load(): void {
    this.isLoading.set(true);
    this.error.set(null);
    this.actionError.set(null);
    this.notice.set(null);
    this.currentPage.set(0);
    this.fetch( false);
  }

  private startAutoRefresh(): void {
    this.refreshSub?.unsubscribe();
    this.refreshSub = interval(AdminDeliveriesComponent.RefreshIntervalMs).subscribe(() => {
      if (this.autoRefresh() && !this.isLoading() && !this.retryingId()
          && !this.cancelingId() && !this.editingId()) {
        this.fetch( true);
      }
    });
  }

  toggleAutoRefresh(): void {
    this.autoRefresh.update(v => !v);
  }

  private fetch(silent: boolean): void {
    const status = this.selectedStatus();
    this.storage.getDeliveries(status === 'all' ? null : status).subscribe({
      next: (items) => {
        this.allDeliveries.set(items);
        if (!silent) this.isLoading.set(false);
        if (this.currentPage() > this.totalPages() - 1) {
          this.currentPage.set(Math.max(0, this.totalPages() - 1));
        }
      },
      error: () => {
        if (!silent) {
          this.error.set('Nie udało się załadować listy plików do wysłania.');
          this.isLoading.set(false);
        }
      }
    });
  }

  setStatus(status: string): void {
    this.selectedStatus.set(status as DeliveryStatus | 'all');
    this.load();
  }

  setFilter(
    field: 'id' | 'doc' | 'att' | 'created' | 'lock' | 'modifiedBy',
    value: string
  ): void {
    if (field === 'id')       this.filterId.set(value);
    if (field === 'doc')      this.filterDoc.set(value);
    if (field === 'att')      this.filterAtt.set(value);
    if (field === 'created')  this.filterCreated.set(value);
    if (field === 'lock')     this.filterLock.set(value);
    if (field === 'modifiedBy') this.filterModifiedBy.set(value);
    this.currentPage.set(0);
  }

  toggleExpand(deliveryId: string): void {
    this.expandedId.update(curr => (curr === deliveryId ? null : deliveryId));
  }

  isExpanded(deliveryId: string): boolean {
    return this.expandedId() === deliveryId;
  }

  canResume(item: DeliveryListItem): boolean {
    return item.status === 'DeadLettered'
        || item.status === 'FailedPermanently'
        || item.status === 'RetryScheduled'
        || item.status === 'Cancelled';
  }

  canCancel(item: DeliveryListItem): boolean {
    return item.status === 'Pending' || item.status === 'RetryScheduled';
  }

  isBusy(item: DeliveryListItem): boolean {
    return this.retryingId() === item.deliveryId || this.cancelingId() === item.deliveryId;
  }

  downloadFile(item: DeliveryListItem, event: Event): void {
    event.stopPropagation();
    if (this.downloadingId()) return;
    this.downloadingId.set(item.deliveryId);
    this.actionError.set(null);
    this.storage.downloadDeliveryFile(item.deliveryId).subscribe({
      next: (blob) => {
        const mime = blob.type || 'application/vnd.openxmlformats-officedocument.wordprocessingml.document';
        const name = `wyslany_${this.shortId(item.documentId)}_${this.shortId(item.deliveryId)}${this.extensionFor(mime)}`;
        this.triggerDownload(blob, name, mime);
        this.downloadingId.set(null);
      },
      error: (err) => {
        this.downloadingId.set(null);
        const msg = err?.error?.error ?? err?.message ?? 'Nie udało się pobrać wysłanego pliku.';
        this.actionError.set(`Pobieranie wysłanego pliku nie powiodło się: ${msg}`);
      }
    });
  }

  private extensionFor(mimeType: string): string {
    if (mimeType.includes('wordprocessingml')) return '.docx';
    if (mimeType === 'application/msword') return '.doc';
    if (mimeType === 'application/pdf') return '.pdf';
    return '.docx';
  }

  private triggerDownload(blob: Blob, fileName: string, mimeType: string): void {
    const url = URL.createObjectURL(new Blob([blob], { type: mimeType }));
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    a.style.display = 'none';
    document.body.appendChild(a);
    a.click();
    setTimeout(() => {
      document.body.removeChild(a);
      URL.revokeObjectURL(url);
    }, 200);
  }

  resume(item: DeliveryListItem, event: Event): void {
    event.stopPropagation();
    this.retryingId.set(item.deliveryId);
    this.notice.set(null);
    this.actionError.set(null);
    this.storage.retryDelivery(item.deliveryId).subscribe({
      next: () => {
        this.retryingId.set(null);
        this.notice.set(`Zadanie ${this.shortId(item.deliveryId)} wznowione.`);
        this.refreshAfterAction();
      },
      error: (err) => {
        this.retryingId.set(null);
        this.actionError.set(err?.error?.error
          || `Nie udało się wznowić zadania ${this.shortId(item.deliveryId)}.`);
      }
    });
  }

  cancel(item: DeliveryListItem, event: Event): void {
    event.stopPropagation();
    this.cancelingId.set(item.deliveryId);
    this.notice.set(null);
    this.actionError.set(null);
    this.storage.cancelDelivery(item.deliveryId).subscribe({
      next: () => {
        this.cancelingId.set(null);
        this.notice.set(`Zadanie ${this.shortId(item.deliveryId)} anulowane.`);
        this.refreshAfterAction();
      },
      error: (err) => {
        this.cancelingId.set(null);
        this.actionError.set(err?.error?.error
          || `Nie udało się anulować zadania ${this.shortId(item.deliveryId)}.`);
      }
    });
  }

  dismissActionError(): void {
    this.actionError.set(null);
  }

  private refreshAfterAction(): void {
    this.fetch( true);
  }

  copyEditLink(item: DeliveryListItem, event: Event): void {
    event.stopPropagation();
    const masterId = item.documentId;
    const versionId = item.sourceVersionId;
    const tree = this.router.createUrlTree(['/editor'], {
      queryParams: versionId ? { masterId, versionId } : { masterId }
    });
    const url = `${window.location.origin}${this.router.serializeUrl(tree)}`;

    this.notice.set(null);
    this.actionError.set(null);

    if (navigator.clipboard?.writeText) {
      navigator.clipboard.writeText(url).then(
        () => this.notice.set('Skopiowano link do edycji do schowka.'),
        () => this.actionError.set(`Nie udało się skopiować linku. Skopiuj ręcznie: ${url}`)
      );
    } else {
      this.actionError.set(`Kopiowanie niedostępne w tej przeglądarce. Link do edycji: ${url}`);
    }
  }

  canEdit(item: DeliveryListItem): boolean {
    return item.status !== 'Sent' && item.status !== 'Sending';
  }

  startEdit(item: DeliveryListItem, event: Event): void {
    event.stopPropagation();
    this.editError.set(null);
    this.editUrlValue.set(item.recipientUrl ?? '');
    this.editingId.set(item.deliveryId);
  }

  cancelEdit(): void {
    this.editingId.set(null);
    this.editUrlValue.set('');
    this.editError.set(null);
    this.savingEdit.set(false);
  }

  saveEdit(): void {
    const deliveryId = this.editingId();
    if (!deliveryId) return;
    const url = this.editUrlValue().trim();
    if (!/^https?:\/\/.+/i.test(url)) {
      this.editError.set('Podaj poprawny adres http(s).');
      return;
    }

    this.savingEdit.set(true);
    this.editError.set(null);
    this.storage.updateDeliveryRecipientUrl(deliveryId, url).subscribe({
      next: () => {
        this.savingEdit.set(false);
        const id = this.shortId(deliveryId);
        this.cancelEdit();
        this.notice.set(`Zmieniono adres odbiorcy zadania ${id}.`);
        this.refreshAfterAction();
      },
      error: (err) => {
        this.savingEdit.set(false);
        this.editError.set(err?.error?.error || 'Nie udało się zmienić adresu odbiorcy.');
      }
    });
  }

  statusLabel(status: DeliveryStatus): string {
    const map: Record<DeliveryStatus, string> = {
      Pending: 'Oczekuje',
      Sending: 'Wysyłanie',
      RetryScheduled: 'Zaplanowano',
      Sent: 'Wysłano',
      FailedPermanently: 'Błąd',
      DeadLettered: 'Porzucone',
      Cancelled: 'Anulowano'
    };
    return map[status] ?? status;
  }

  statusClass(status: DeliveryStatus): string {
    const map: Record<DeliveryStatus, string> = {
      Pending: 'status-saved',
      Sending: 'status-sending',
      RetryScheduled: 'status-editing',
      Sent: 'status-sent',
      FailedPermanently: 'status-failed',
      DeadLettered: 'status-failed',
      Cancelled: 'status-cancelled'
    };
    return map[status] ?? 'status-saved';
  }

  shortId(id: string): string {
    return id ? id.slice(0, 8) : '—';
  }

  prevPage(): void { if (this.currentPage() > 0) this.currentPage.update(p => p - 1); }
  nextPage(): void { if (this.currentPage() < this.totalPages() - 1) this.currentPage.update(p => p + 1); }
  pageEnd(): number { return Math.min((this.currentPage() + 1) * this.pageSize, this.totalFiltered()); }
  goToPage(value: string): void {
    const n = parseInt(value, 10);
    if (!isNaN(n)) this.currentPage.set(Math.max(0, Math.min(n - 1, this.totalPages() - 1)));
  }

  formatDate(dateStr: string | null): string {
    if (!dateStr) return '—';
    return new Date(dateStr).toLocaleDateString('pl-PL', {
      day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit'
    });
  }
}
