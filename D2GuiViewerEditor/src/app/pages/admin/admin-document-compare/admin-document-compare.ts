import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { finalize } from 'rxjs';
import {
  ComparedPart,
  ComparedPartStatus,
  DifferenceKind,
  DocumentCompareService,
  DocumentComparisonReport,
  DocumentDifference,
} from '../../../services/document-compare.service';
import { DiffSegment, diffWords, leftSide, rightSide } from '../../../core/utils/text-diff.util';

const ALL = 'All';
const PAGE_SIZE = 100;

const KIND_LABELS: Record<DifferenceKind, string> = {
  PartOnlyInLeft: 'Część tylko w lewym',
  PartOnlyInRight: 'Część tylko w prawym',
  BinaryPartChanged: 'Część binarna różna',
  ElementOnlyInLeft: 'Element tylko w lewym',
  ElementOnlyInRight: 'Element tylko w prawym',
  ElementNameChanged: 'Inny element',
  AttributeOnlyInLeft: 'Atrybut tylko w lewym',
  AttributeOnlyInRight: 'Atrybut tylko w prawym',
  AttributeValueChanged: 'Inna wartość atrybutu',
  TextChanged: 'Inny tekst',
  ElementMoved: 'Element przeniesiony',
};

const STATUS_LABELS: Record<ComparedPartStatus, string> = {
  Identical: 'identyczna',
  Changed: 'różni się',
  OnlyInLeft: 'tylko w lewym',
  OnlyInRight: 'tylko w prawym',
  Unreadable: 'nieczytelna',
};

interface ValueDiff {
  left: DiffSegment[];
  right: DiffSegment[];
}

@Component({
  selector: 'd2-admin-document-compare',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './admin-document-compare.html',
  styleUrl: './admin-document-compare.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AdminDocumentCompareComponent {
  private readonly api = inject(DocumentCompareService);

  readonly leftFileName = signal<string | null>(null);
  readonly rightFileName = signal<string | null>(null);
  readonly ignoreRevisionIds = signal(true);
  readonly ignoreDocumentProperties = signal(true);
  readonly isComparing = signal(false);
  readonly error = signal<string | null>(null);
  readonly report = signal<DocumentComparisonReport | null>(null);
  readonly copied = signal(false);

  readonly kindFilter = signal<DifferenceKind | typeof ALL>(ALL);
  readonly categoryFilter = signal<string>(ALL);
  readonly partFilter = signal<string>(ALL);
  readonly search = signal('');
  readonly pageLimit = signal(PAGE_SIZE);

  private readonly expandedExcerpts = signal<ReadonlySet<number>>(new Set<number>());
  private leftFile: File | null = null;
  private rightFile: File | null = null;

  readonly canCompare = computed(() => !!this.leftFileName() && !!this.rightFileName() && !this.isComparing());

  readonly hasFilters = computed(
    () => this.kindFilter() !== ALL || this.categoryFilter() !== ALL || this.partFilter() !== ALL || this.search().trim() !== '',
  );

  readonly kinds = computed<DifferenceKind[]>(() =>
    (Object.keys(KIND_LABELS) as DifferenceKind[]).filter((kind) => (this.report()?.countsByKind[kind] ?? 0) > 0),
  );

  readonly categories = computed<string[]>(() =>
    Object.keys(this.report()?.countsByCategory ?? {}).sort((a, b) => a.localeCompare(b, 'pl')),
  );

  readonly changedParts = computed<ComparedPart[]>(() =>
    (this.report()?.parts ?? []).filter((part) => part.status !== 'Identical'),
  );

  readonly visibleDifferences = computed<DocumentDifference[]>(() => {
    const differences = this.report()?.differences ?? [];
    const kind = this.kindFilter();
    const category = this.categoryFilter();
    const part = this.partFilter();
    const needle = this.search().trim().toLowerCase();

    return differences.filter(
      (difference) =>
        (kind === ALL || difference.kind === kind) &&
        (category === ALL || difference.category === category) &&
        (part === ALL || difference.partPath === part) &&
        (needle === '' || this.matches(difference, needle)),
    );
  });

  readonly pagedDifferences = computed(() => this.visibleDifferences().slice(0, this.pageLimit()));
  readonly hasMore = computed(() => this.visibleDifferences().length > this.pageLimit());

  onFileSelected(side: 'left' | 'right', event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.item(0) ?? null;
    input.value = '';

    if (!file) {
      return;
    }

    if (side === 'left') {
      this.leftFile = file;
      this.leftFileName.set(file.name);
    } else {
      this.rightFile = file;
      this.rightFileName.set(file.name);
    }

    this.error.set(null);
  }

  swapFiles(): void {
    [this.leftFile, this.rightFile] = [this.rightFile, this.leftFile];
    const left = this.leftFileName();
    this.leftFileName.set(this.rightFileName());
    this.rightFileName.set(left);
  }

  compare(): void {
    const left = this.leftFile;
    const right = this.rightFile;

    if (!left || !right || this.isComparing()) {
      return;
    }

    this.report.set(null);
    this.error.set(null);
    this.copied.set(false);
    this.clearFilters();
    this.expandedExcerpts.set(new Set<number>());
    this.isComparing.set(true);

    this.api
      .compare(left, right, {
        ignoreRevisionIds: this.ignoreRevisionIds(),
        ignoreDocumentProperties: this.ignoreDocumentProperties(),
      })
      .pipe(finalize(() => this.isComparing.set(false)))
      .subscribe({
        next: (report) => this.report.set(report),
        error: (error) => this.error.set(this.readError(error)),
      });
  }

  setKind(kind: DifferenceKind | typeof ALL): void {
    this.kindFilter.set(kind);
    this.pageLimit.set(PAGE_SIZE);
  }

  setCategory(category: string): void {
    this.categoryFilter.set(category);
    this.pageLimit.set(PAGE_SIZE);
  }

  togglePart(part: ComparedPart): void {
    this.partFilter.set(this.partFilter() === part.path ? ALL : part.path);
    this.pageLimit.set(PAGE_SIZE);
  }

  setSearch(value: string): void {
    this.search.set(value);
    this.pageLimit.set(PAGE_SIZE);
  }

  clearFilters(): void {
    this.kindFilter.set(ALL);
    this.categoryFilter.set(ALL);
    this.partFilter.set(ALL);
    this.search.set('');
    this.pageLimit.set(PAGE_SIZE);
  }

  showMore(): void {
    this.pageLimit.set(this.pageLimit() + PAGE_SIZE);
  }

  isExcerptExpanded(index: number): boolean {
    return this.expandedExcerpts().has(index);
  }

  toggleExcerpt(index: number): void {
    const next = new Set(this.expandedExcerpts());

    if (next.has(index)) {
      next.delete(index);
    } else {
      next.add(index);
    }

    this.expandedExcerpts.set(next);
  }

  valueDiff(difference: DocumentDifference): ValueDiff | null {
    if (difference.leftValue === null || difference.rightValue === null) {
      return null;
    }

    const segments = diffWords(difference.leftValue, difference.rightValue);
    return { left: leftSide(segments), right: rightSide(segments) };
  }

  hasValues(difference: DocumentDifference): boolean {
    return difference.leftValue !== null || difference.rightValue !== null;
  }

  async copyReport(): Promise<void> {
    const report = this.report();

    if (!report) {
      return;
    }

    try {
      await navigator.clipboard.writeText(JSON.stringify(report, null, 2));
      this.copied.set(true);
      setTimeout(() => this.copied.set(false), 2500);
    } catch {
      this.error.set('Nie udało się skopiować raportu do schowka.');
    }
  }

  kindLabel(kind: DifferenceKind): string {
    return KIND_LABELS[kind] ?? kind;
  }

  statusLabel(status: ComparedPartStatus): string {
    return STATUS_LABELS[status] ?? status;
  }

  isOneSided(kind: DifferenceKind): boolean {
    return kind.endsWith('InLeft') || kind.endsWith('InRight');
  }

  formatBytes(bytes: number | null): string {
    if (bytes === null) {
      return '—';
    }

    if (!bytes) {
      return '0 B';
    }

    const units = ['B', 'KB', 'MB', 'GB'];
    const unitIndex = Math.min(units.length - 1, Math.floor(Math.log(bytes) / Math.log(1024)));
    return `${parseFloat((bytes / Math.pow(1024, unitIndex)).toFixed(1))} ${units[unitIndex]}`;
  }

  formatDuration(ms: number): string {
    return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(1)} s`;
  }

  private matches(difference: DocumentDifference, needle: string): boolean {
    return [
      difference.partPath,
      difference.leftPath,
      difference.rightPath,
      difference.name,
      difference.leftValue,
      difference.rightValue,
      difference.leftContext,
      difference.rightContext,
      difference.category,
    ].some((value) => value?.toLowerCase().includes(needle));
  }

  private readError(error: unknown): string {
    if (error instanceof HttpErrorResponse) {
      const body = error.error as { error?: string; detail?: string } | string | null;

      if (typeof body === 'string' && body.trim()) {
        return body;
      }

      if (body && typeof body === 'object') {
        return body.error ?? body.detail ?? `Błąd ${error.status}: ${error.statusText}`;
      }

      return error.status === 0 ? 'Brak połączenia z backendem.' : `Błąd ${error.status}: ${error.statusText}`;
    }

    return error instanceof Error ? error.message : 'Nieznany błąd porównania.';
  }
}
