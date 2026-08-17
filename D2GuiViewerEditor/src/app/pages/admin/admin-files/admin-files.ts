import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { forkJoin, of, catchError, map } from 'rxjs';
import { AdminService, DocumentWithVersions, DocumentVersionListItem } from '../../../services/admin.service';

@Component({
  selector: 'd2-admin-files',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './admin-files.html',
  styleUrl: './admin-files.scss'
})
export class AdminFilesComponent implements OnInit {
  private adminService = inject(AdminService);
  private router = inject(Router);

  private allDocuments = signal<DocumentWithVersions[]>([]);
  filterId     = signal('');
  
  filterExtension = signal('');
  filterDate   = signal('');
  filterStatus = signal('');
  filterModifiedBy = signal('');
  currentPage = signal(0);
  readonly pageSize = 10;
  isLoading = signal(true);
  error = signal<string | null>(null);
  loadingVersionsFor = signal<string | null>(null);
  downloadingId = signal<string | null>(null);
  deletingId = signal<string | null>(null);
  notice = signal<string | null>(null);

  private versionsPreloadState: 'idle' | 'loading' | 'done' = 'idle';

  private filteredDocuments = computed(() => {
    const id     = this.filterId().toLowerCase().trim();
    const ext    = this.filterExtension();
    const date   = this.filterDate().toLowerCase().trim();
    const status = this.filterStatus().toLowerCase().trim();
    const modifiedBy = this.filterModifiedBy().toLowerCase().trim();
    return this.allDocuments().filter(d => {
      
      if (id
          && !d.masterId.toLowerCase().includes(id)
          && !d.activeVersionId.toLowerCase().includes(id)
          && !d.versions.some(v => v.versionId.toLowerCase().includes(id))) return false;
      if (ext    && this.fileExtension(d) !== ext)                              return false;
      if (date   && !this.formatDate(d.createdAt).toLowerCase().includes(date)) return false;
      if (status && !this.statusLabel(d.status).toLowerCase().includes(status)) return false;
      if (modifiedBy && !(d.lastModifiedBy ?? '').toLowerCase().includes(modifiedBy)) return false;
      return true;
    });
  });

  readonly availableExtensions = computed(() => {
    const set = new Set(this.allDocuments().map(d => this.fileExtension(d)));
    return Array.from(set).sort();
  });

  fileExtension(d: { name?: string; mimeType: string }): string {
    const fromName = /\.([a-z0-9]+)$/i.exec(d.name ?? '')?.[1]?.toLowerCase();
    if (fromName) return fromName;
    const byMime: Record<string, string> = {
      'application/pdf': 'pdf',
      'application/msword': 'doc',
      'application/vnd.openxmlformats-officedocument.wordprocessingml.document': 'docx',
    };
    return byMime[d.mimeType] ?? (d.mimeType.split('/')[1] ?? d.mimeType).toLowerCase();
  }

  extensionLabel(d: { name?: string; mimeType: string }): string {
    return this.fileExtension(d).toUpperCase();
  }

  totalFiltered = computed(() => this.filteredDocuments().length);
  totalPages    = computed(() => Math.max(1, Math.ceil(this.totalFiltered() / this.pageSize)));

  documents = computed(() => {
    const start = this.currentPage() * this.pageSize;
    return this.filteredDocuments().slice(start, start + this.pageSize);
  });

  setFilter(field: 'id' | 'extension' | 'date' | 'status' | 'modifiedBy', value: string): void {
    if (field === 'id') {
      this.filterId.set(value);
      
      if (value.trim()) this.preloadAllVersions();
    }
    if (field === 'extension') this.filterExtension.set(value);
    if (field === 'date')   this.filterDate.set(value);
    if (field === 'status') this.filterStatus.set(value);
    if (field === 'modifiedBy') this.filterModifiedBy.set(value);
    this.currentPage.set(0);
  }

  private preloadAllVersions(): void {
    if (this.versionsPreloadState !== 'idle') return;
    const missing = this.allDocuments().filter(d => d.versions.length === 0);
    if (missing.length === 0) {
      this.versionsPreloadState = 'done';
      return;
    }
    this.versionsPreloadState = 'loading';
    forkJoin(
      missing.map(d => this.adminService.getDocumentVersions(d.masterId).pipe(
        map(versions => ({ masterId: d.masterId, versions })),
        
        catchError(() => of({ masterId: d.masterId, versions: [] as DocumentVersionListItem[] })),
      )),
    ).subscribe(results => {
      results.forEach(r => {
        if (r.versions.length > 0) this.updateDoc(r.masterId, { versions: r.versions });
      });
      this.versionsPreloadState = 'done';
    });
  }

  statusLabel(status: string): string {
    const map: Record<string, string> = {
      Saved: 'Zapisany',
      Editing: 'W edycji',
      Sending: 'Wysyłanie do odbiorcy',
      DeliveryFailed: 'Odbiorca nie odpowiada',
      Sent: 'Wysłany'
    };
    return map[status] ?? status ?? '—';
  }

  statusClass(status: string): string {
    const map: Record<string, string> = {
      Saved: 'status-saved',
      Editing: 'status-editing',
      Sending: 'status-sending',
      DeliveryFailed: 'status-failed',
      Sent: 'status-sent'
    };
    return map[status] ?? 'status-saved';
  }

  prevPage(): void { if (this.currentPage() > 0) this.currentPage.update(p => p - 1); }
  nextPage(): void { if (this.currentPage() < this.totalPages() - 1) this.currentPage.update(p => p + 1); }
  pageEnd(): number { return Math.min((this.currentPage() + 1) * this.pageSize, this.totalFiltered()); }
  goToPage(value: string): void {
    const n = parseInt(value, 10);
    if (!isNaN(n)) this.currentPage.set(Math.max(0, Math.min(n - 1, this.totalPages() - 1)));
  }

  ngOnInit(): void {
    this.adminService.getAllDocuments().subscribe({
      next: (docs) => {
        this.allDocuments.set(docs.map(d => ({ ...d, versions: [], expanded: false })));
        this.isLoading.set(false);
      },
      error: () => {
        this.error.set('Nie udało się załadować listy plików.');
        this.isLoading.set(false);
      }
    });
  }

  toggleExpand(doc: DocumentWithVersions): void {
    const docs = this.allDocuments();
    const idx = docs.findIndex(d => d.masterId === doc.masterId);
    if (idx === -1) return;

    const current = docs[idx];
    if (!current.expanded && current.versions.length === 0) {
      this.loadingVersionsFor.set(doc.masterId);
      this.adminService.getDocumentVersions(doc.masterId).subscribe({
        next: (versions) => {
          this.updateDoc(doc.masterId, { versions, expanded: true });
          this.loadingVersionsFor.set(null);
        },
        error: () => this.loadingVersionsFor.set(null)
      });
    } else {
      this.updateDoc(doc.masterId, { expanded: !current.expanded });
    }
  }

  copyEditLink(doc: DocumentWithVersions, event: Event): void {
    event.stopPropagation();
    this.notice.set(null);

    if (doc.versions.length > 0) {
      this.buildAndCopyEditLink(doc, doc.versions);
      return;
    }

    this.adminService.getDocumentVersions(doc.masterId).subscribe({
      next: (versions) => {
        this.updateDoc(doc.masterId, { versions }); 
        this.buildAndCopyEditLink(doc, versions);
      },
      error: () => this.notice.set('Nie udało się ustalić wersji edytowalnej dokumentu.')
    });
  }

  private editableVersionId(doc: DocumentWithVersions, versions: DocumentVersionListItem[]): string {
    const editable = versions
      .filter(v => v.versionNumber > 1)
      .sort((a, b) => b.versionNumber - a.versionNumber)[0];
    return editable?.versionId ?? doc.activeVersionId;
  }

  private buildAndCopyEditLink(doc: DocumentWithVersions, versions: DocumentVersionListItem[]): void {
    const versionId = this.editableVersionId(doc, versions);
    const tree = this.router.createUrlTree(['/editor'], {
      queryParams: { masterId: doc.masterId, versionId }
    });
    const url = `${window.location.origin}${this.router.serializeUrl(tree)}`;

    if (navigator.clipboard?.writeText) {
      navigator.clipboard.writeText(url).then(
        () => this.notice.set('Skopiowano link do edycji do schowka.'),
        () => this.notice.set(`Nie udało się skopiować linku. Skopiuj ręcznie: ${url}`)
      );
    } else {
      this.notice.set(`Kopiowanie niedostępne w tej przeglądarce. Link do edycji: ${url}`);
    }
  }
  
  deleteDocument(doc: DocumentWithVersions, event: Event): void {
    event.stopPropagation();
    const confirmed = confirm(
      `Usunąć trwale dokument „${doc.name}"?

` +
      'Z magazynu zostaną skasowane pliki WSZYSTKICH wersji, a z bazy wpis dokumentu ' +
      'wraz z wersjami i zadaniami wysyłki. Tej operacji nie można cofnąć.');
    if (!confirmed) return;

    this.deletingId.set(doc.masterId);
    this.notice.set(null);
    this.adminService.deleteDocument(doc.masterId).subscribe({
      next: () => {
        this.deletingId.set(null);
        this.allDocuments.update(list => list.filter(d => d.masterId !== doc.masterId));
        
        if (this.currentPage() >= this.totalPages()) {
          this.currentPage.set(Math.max(0, this.totalPages() - 1));
        }
        this.notice.set(`Usunięto dokument „${doc.name}".`);
      },
      error: (err) => {
        this.deletingId.set(null);
        this.notice.set(err?.error?.error ?? 'Nie udało się usunąć dokumentu.');
      },
    });
  }

  downloadActive(doc: DocumentWithVersions, event: Event): void {
    event.stopPropagation();
    this.downloadingId.set(doc.masterId);
    this.adminService.downloadDocumentVersion(doc.masterId, doc.activeVersionId).subscribe({
      next: (blob) => {
        this.triggerDownload(blob, doc.name, doc.mimeType);
        this.downloadingId.set(null);
      },
      error: () => this.downloadingId.set(null)
    });
  }

  downloadVersion(masterId: string, versionId: string, fileName: string, mimeType: string, event: Event): void {
    event.stopPropagation();
    const key = `${masterId}_${versionId}`;
    this.downloadingId.set(key);
    this.adminService.downloadDocumentVersion(masterId, versionId).subscribe({
      next: (blob) => {
        this.triggerDownload(blob, fileName, mimeType);
        this.downloadingId.set(null);
      },
      error: () => this.downloadingId.set(null)
    });
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

  private updateDoc(masterId: string, patch: Partial<DocumentWithVersions>): void {
    const docs = this.allDocuments();
    const idx = docs.findIndex(d => d.masterId === masterId);
    if (idx === -1) return;
    const updated = [...docs];
    updated[idx] = { ...updated[idx], ...patch };
    this.allDocuments.set(updated);
  }

  friendlyType(mimeType: string): string {
    const map: Record<string, string> = {
      'application/pdf': 'PDF',
      'application/msword': 'Word',
      'application/vnd.openxmlformats-officedocument.wordprocessingml.document': 'Word',
      'application/vnd.ms-excel': 'Excel',
      'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet': 'Excel',
      'application/vnd.ms-powerpoint': 'PowerPoint',
      'application/vnd.openxmlformats-officedocument.presentationml.presentation': 'PowerPoint',
      'application/zip': 'ZIP',
      'application/json': 'JSON',
      'application/xml': 'XML',
      'text/xml': 'XML',
      'text/plain': 'TXT',
      'text/csv': 'CSV',
      'text/html': 'HTML',
      'image/png': 'PNG',
      'image/jpeg': 'JPG',
      'image/gif': 'GIF',
      'image/svg+xml': 'SVG',
    };
    return map[mimeType] ?? mimeType.split('/')[1]?.toUpperCase() ?? mimeType;
  }

  formatBytes(bytes: number): string {
    if (!bytes || bytes === 0) return '—';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return `${parseFloat((bytes / Math.pow(k, i)).toFixed(1))} ${sizes[i]}`;
  }

  formatDate(dateStr: string): string {
    return new Date(dateStr).toLocaleDateString('pl-PL', {
      day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit'
    });
  }
}
