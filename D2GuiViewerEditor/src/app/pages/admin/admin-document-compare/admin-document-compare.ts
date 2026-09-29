import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { finalize } from 'rxjs';
import {
  ComparedPart,
  DifferenceCause,
  DifferenceImpact,
  DifferenceKind,
  DocumentCompareService,
  DocumentComparisonReport,
  DocumentDifference,
} from '../../../services/document-compare.service';
import { DiffSegment, diffWords, leftSide, rightSide } from '../../../core/utils/text-diff.util';
import {
  DifferenceGroup,
  buildCompareNote,
  describeDifference,
  describeGroup,
  describePart,
  groupDifferences,
  groupKeyOf,
  groupTarget,
  impactRank,
} from './diff-summary.util';
import {
  BUCKET_HINTS,
  BUCKET_LABELS,
  BUCKET_ORDER,
  CAUSE_LABELS,
  DifferenceAssessment,
  DifferenceBucket,
  IMPACT_LABELS,
  VERDICT_ORDER,
  assessDifference,
  bucketOf,
} from './diff-assessment.util';

const PAGE_SIZE = 50;

// „Lewy/prawy” nic nie mówi o roli pliku; w praktyce porównujemy ORYGINAŁ (v1, wejście) z wersją
// PORÓWNYWANĄ (zapis z edytora, v2, inny szablon). Backend nadal mówi Left/Right — to tylko etykiety.
const KIND_LABELS: Record<DifferenceKind, string> = {
  PartOnlyInLeft: 'Część tylko w oryginale',
  PartOnlyInRight: 'Część tylko w porównywanym',
  BinaryPartChanged: 'Część binarna różna',
  ElementOnlyInLeft: 'Element tylko w oryginale',
  ElementOnlyInRight: 'Element tylko w porównywanym',
  ElementNameChanged: 'Inny element',
  AttributeOnlyInLeft: 'Atrybut tylko w oryginale',
  AttributeOnlyInRight: 'Atrybut tylko w porównywanym',
  AttributeValueChanged: 'Inna wartość atrybutu',
  TextChanged: 'Inny tekst',
  ElementMoved: 'Element przeniesiony',
  PartRenamed: 'Część pod inną ścieżką',
};

interface ValueDiff {
  left: DiffSegment[];
  right: DiffSegment[];
}

/** Różnica razem z jej indeksem w raporcie (indeks = klucz rozwiniętych wycinków XML). */
export interface IndexedDifference {
  index: number;
  difference: DocumentDifference;
}

/**
 * Narzędzie administracyjne „Porównanie dokumentów": oryginał (z Worda) ↔ ten sam dokument po zapisie
 * z naszego edytora. Backend porównuje literalnie (pakiet, XML, tekst) i analizuje przyczynę oraz skutek;
 * ekran odpowiada na trzy pytania po kolei: czy to to samo → co jest do naprawy u nas / do sprawdzenia /
 * nieistotne → gdzie dokładnie i dlaczego (rozwijane wystąpienia z wartościami i wycinkiem XML).
 */
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
  readonly copiedNote = signal(false);
  readonly showHelp = signal(false);
  readonly showOptions = signal(false);

  readonly buckets = BUCKET_ORDER;

  /** Wybrany kubełek; null = pierwszy niepusty (do naprawy u nas → do sprawdzenia → nieistotne). */
  readonly bucket = signal<DifferenceBucket | null>(null);
  readonly search = signal('');

  private readonly expandedGroups = signal<ReadonlySet<string>>(new Set<string>());
  private readonly groupLimits = signal<Readonly<Record<string, number>>>({});
  private readonly expandedExcerpts = signal<ReadonlySet<number>>(new Set<number>());
  private leftFile: File | null = null;
  private rightFile: File | null = null;

  readonly canCompare = computed(() => !!this.leftFileName() && !!this.rightFileName() && !this.isComparing());

  /** Ocena każdej różnicy (indeks = pozycja w raporcie) — liczona raz per raport. */
  readonly assessments = computed<DifferenceAssessment[]>(() => (this.report()?.differences ?? []).map(assessDifference));

  /** Różnice zgrupowane wg przyczyny (rodzaj + część + element + nazwa), z kubełkiem, przyczyną i skutkiem. */
  readonly groups = computed<DifferenceGroup[]>(() =>
    groupDifferences(this.report()?.differences ?? [], {
      assess: assessDifference,
      verdictOrder: VERDICT_ORDER,
      bucket: (difference, assessment) => bucketOf(difference, assessment as DifferenceAssessment),
    }),
  );

  /** Wystąpienia per grupa, z indeksem w raporcie. */
  readonly differencesByGroup = computed<ReadonlyMap<string, IndexedDifference[]>>(() => {
    const map = new Map<string, IndexedDifference[]>();

    (this.report()?.differences ?? []).forEach((difference, index) => {
      const key = groupKeyOf(difference);
      const list = map.get(key);

      if (list) {
        list.push({ index, difference });
      } else {
        map.set(key, [{ index, difference }]);
      }
    });

    return map;
  });

  /** Liczba RÓŻNIC w każdym kubełku (po różnicach, nie po grupach). */
  readonly bucketCounts = computed<Record<DifferenceBucket, number>>(() => {
    const counts: Record<DifferenceBucket, number> = { fix: 0, review: 0, noise: 0 };
    const differences = this.report()?.differences ?? [];
    const assessments = this.assessments();

    differences.forEach((difference, index) => {
      counts[bucketOf(difference, assessments[index])]++;
    });

    return counts;
  });

  /** Liczba GRUP (przyczyn) w każdym kubełku. */
  readonly bucketGroupCounts = computed<Record<DifferenceBucket, number>>(() => {
    const counts: Record<DifferenceBucket, number> = { fix: 0, review: 0, noise: 0 };

    for (const group of this.groups()) {
      if (group.bucket) {
        counts[group.bucket]++;
      }
    }

    return counts;
  });

  /** Dopowiedzenie do liczby różnic — jedno zdanie, które mówi, czy jest co robić. */
  readonly headline = computed<string>(() => {
    const counts = this.bucketCounts();

    if (counts.fix > 0) {
      return `z czego ${counts.fix} do naprawy u nas`;
    }

    if (counts.review > 0) {
      return `nic do naprawy u nas, ${counts.review} do sprawdzenia`;
    }

    return 'wszystkie nieistotne';
  });

  readonly activeBucket = computed<DifferenceBucket>(
    () => this.bucket() ?? BUCKET_ORDER.find((bucket) => this.bucketGroupCounts()[bucket] > 0) ?? 'fix',
  );

  /** Grupy aktywnego kubełka po wyszukiwaniu: najpoważniejszy skutek pierwszy, potem najliczniejsze. */
  readonly visibleGroups = computed<DifferenceGroup[]>(() => {
    const bucket = this.activeBucket();
    const needle = this.search().trim().toLowerCase();

    return this.groups()
      .filter((group) => group.bucket === bucket && (needle === '' || this.groupMatches(group, needle)))
      .sort((a, b) => impactRank(a.worstImpact) - impactRank(b.worstImpact) || b.count - a.count || a.key.localeCompare(b.key));
  });

  readonly visibleCount = computed(() => this.visibleGroups().reduce((sum, group) => sum + group.count, 0));

  readonly changedParts = computed<ComparedPart[]>(() =>
    (this.report()?.parts ?? []).filter((part) => part.status !== 'Identical'),
  );

  // ── Pliki i porównanie ─────────────────────────────────────────────────────

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
    this.copiedNote.set(false);
    this.bucket.set(null);
    this.search.set('');
    this.expandedGroups.set(new Set<string>());
    this.groupLimits.set({});
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

  // ── Kubełki, wyszukiwanie, grupy ───────────────────────────────────────────

  selectBucket(bucket: DifferenceBucket): void {
    this.bucket.set(bucket);
  }

  setSearch(value: string): void {
    this.search.set(value);
  }

  isGroupExpanded(group: DifferenceGroup): boolean {
    return this.expandedGroups().has(group.key);
  }

  toggleGroup(group: DifferenceGroup): void {
    const next = new Set(this.expandedGroups());

    if (next.has(group.key)) {
      next.delete(group.key);
    } else {
      next.add(group.key);
    }

    this.expandedGroups.set(next);
  }

  /** Wystąpienia grupy do pokazania (stronicowane per grupa). */
  itemsOf(group: DifferenceGroup): IndexedDifference[] {
    const all = this.differencesByGroup().get(group.key) ?? [];
    return all.slice(0, this.groupLimits()[group.key] ?? PAGE_SIZE);
  }

  hasMoreIn(group: DifferenceGroup): boolean {
    return (this.differencesByGroup().get(group.key)?.length ?? 0) > (this.groupLimits()[group.key] ?? PAGE_SIZE);
  }

  showMoreIn(group: DifferenceGroup): void {
    const current = this.groupLimits()[group.key] ?? PAGE_SIZE;
    this.groupLimits.set({ ...this.groupLimits(), [group.key]: current + PAGE_SIZE });
  }

  assessmentAt(index: number): DifferenceAssessment {
    return this.assessments()[index];
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

  /** Podświetlenie zmienionych słów w wartościach (tekst, atrybut); dla jednostronnych — bez podświetlenia. */
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

  // ── Kopiowanie ─────────────────────────────────────────────────────────────

  async copyReport(): Promise<void> {
    const report = this.report();

    if (!report) {
      return;
    }

    await this.copyToClipboard(JSON.stringify(report, null, 2), this.copied);
  }

  /** Notatka Markdown: kubełki → grupy z przyczyną, skutkiem, wskaźnikiem do kodu i przykładami. */
  async copySummary(): Promise<void> {
    const report = this.report();

    if (!report) {
      return;
    }

    await this.copyToClipboard(buildCompareNote(report, this.groups(), (kind) => this.kindLabel(kind)), this.copiedNote);
  }

  private async copyToClipboard(text: string, flag: ReturnType<typeof signal<boolean>>): Promise<void> {
    try {
      await navigator.clipboard.writeText(text);
      flag.set(true);
      setTimeout(() => flag.set(false), 2500);
    } catch {
      this.error.set('Nie udało się skopiować do schowka.');
    }
  }

  // ── Etykiety ───────────────────────────────────────────────────────────────

  bucketLabel(bucket: DifferenceBucket): string {
    return BUCKET_LABELS[bucket];
  }

  bucketHint(bucket: DifferenceBucket): string {
    return BUCKET_HINTS[bucket];
  }

  causeLabel(cause: string | null | undefined): string {
    return cause ? (CAUSE_LABELS[cause as DifferenceCause] ?? cause) : '';
  }

  impactLabel(impact: string | null | undefined): string {
    return impact ? (IMPACT_LABELS[impact as DifferenceImpact] ?? impact) : '';
  }

  kindLabel(kind: DifferenceKind): string {
    return KIND_LABELS[kind] ?? kind;
  }

  describePart(path: string): string {
    return describePart(path);
  }

  describeGroup(group: DifferenceGroup): string {
    return describeGroup(group);
  }

  groupTarget(group: DifferenceGroup): string {
    return groupTarget(group);
  }

  describeDifference(difference: DocumentDifference): string {
    return describeDifference(difference);
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

  private groupMatches(group: DifferenceGroup, needle: string): boolean {
    return [
      describeGroup(group),
      describePart(group.partPath),
      group.partPath,
      group.element,
      group.name,
      group.category,
      group.sampleContext,
      group.dominantReason,
      group.codePointer,
      ...group.valueSamples.flatMap((sample) => [sample.left, sample.right]),
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
