import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  ViewChild,
  computed,
  inject,
  signal,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { finalize, forkJoin, switchMap, tap } from 'rxjs';
import {
  DocumentSection,
  PackageDiagnostics,
  SchemaIssue,
  SchemaIssues,
  StructureElement,
  StructureElementDetails,
  StructureInspectionSummary,
  StructureValidatorService,
} from '../../../services/structure-validator.service';
import { StructureXmlViewerComponent } from './structure-xml-viewer/structure-xml-viewer';

const ROW_HEIGHT = 46;

const OVERSCAN_ROWS = 8;

const ALL = 'All';

interface XmlViewerContent {
  title: string;
  meta: string;
  xml: string;
  highlightLine: number | null;
}

@Component({
  selector: 'd2-admin-structure-validator',
  standalone: true,
  imports: [CommonModule, FormsModule, StructureXmlViewerComponent],
  templateUrl: './admin-structure-validator.html',
  styleUrl: './admin-structure-validator.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AdminStructureValidatorComponent {
  private readonly api = inject(StructureValidatorService);

  @ViewChild('elementList') private elementListRef?: ElementRef<HTMLElement>;

  readonly rowHeight = ROW_HEIGHT;

  readonly selectedFileName = signal<string | null>(null);
  readonly isAnalyzing = signal(false);
  readonly error = signal<string | null>(null);
  readonly summary = signal<StructureInspectionSummary | null>(null);
  readonly elements = signal<StructureElement[]>([]);
  readonly sections = signal<DocumentSection[]>([]);
  readonly packageDiagnostics = signal<PackageDiagnostics | null>(null);
  readonly schemaIssues = signal<SchemaIssues | null>(null);
  readonly schemaTarget = signal('Microsoft365');

  readonly selectedId = signal<string | null>(null);
  readonly details = signal<StructureElementDetails | null>(null);
  readonly detailsLoading = signal(false);

  readonly xmlContent = signal<XmlViewerContent | null>(null);
  readonly xmlLoading = signal(false);

  readonly search = signal('');
  readonly severityFilter = signal(ALL);
  readonly categoryFilter = signal(ALL);
  readonly partFilter = signal(ALL);
  readonly problemsOnly = signal(false);
  readonly activeTab = signal<'sections' | 'package' | 'schema'>('sections');

  private readonly collapsed = signal<ReadonlySet<string>>(new Set<string>());
  private readonly scrollTop = signal(0);
  private readonly viewportHeight = signal(900);

  private selectedFile: File | null = null;
  private searchDebounce: ReturnType<typeof setTimeout> | null = null;

  readonly hasFilters = computed(
    () =>
      this.search().trim().length > 0 ||
      this.severityFilter() !== ALL ||
      this.categoryFilter() !== ALL ||
      this.partFilter() !== ALL ||
      this.problemsOnly(),
  );

  readonly visibleElements = computed<StructureElement[]>(() => {
    const all = this.elements();

    if (!this.hasFilters()) {
      return this.applyCollapse(all);
    }

    const byId = new Map(all.map((element) => [element.id, element]));
    const included = new Set<string>();

    for (const element of all) {
      if (!this.matchesFilters(element)) {
        continue;
      }

      included.add(element.id);

      let parentId = element.parentId;
      while (parentId && !included.has(parentId)) {
        included.add(parentId);
        parentId = byId.get(parentId)?.parentId ?? null;
      }
    }

    return all.filter((element) => included.has(element.id));
  });

  readonly firstVisibleIndex = computed(() =>
    Math.max(0, Math.floor(this.scrollTop() / ROW_HEIGHT) - OVERSCAN_ROWS),
  );

  readonly windowedElements = computed(() => {
    const start = this.firstVisibleIndex();
    const count = Math.ceil(this.viewportHeight() / ROW_HEIGHT) + OVERSCAN_ROWS * 2;
    return this.visibleElements().slice(start, start + count);
  });

  readonly totalListHeight = computed(() => this.visibleElements().length * ROW_HEIGHT);
  readonly topSpacerHeight = computed(() => this.firstVisibleIndex() * ROW_HEIGHT);

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.item(0) ?? null;
    input.value = '';

    if (!file) {
      return;
    }

    if (!file.name.toLowerCase().endsWith('.docx')) {
      this.error.set('Walidator struktury obsługuje wyłącznie pliki DOCX.');
      return;
    }

    this.selectedFile = file;
    this.selectedFileName.set(file.name);
    this.error.set(null);
  }

  analyze(): void {
    const file = this.selectedFile;

    if (!file || this.isAnalyzing()) {
      return;
    }

    this.releasePreviousInspection();
    this.resetResults();
    this.isAnalyzing.set(true);

    this.api
      .analyze(file)
      .pipe(
        tap((summary) => this.summary.set(summary)),
        switchMap((summary) =>
          forkJoin({
            elements: this.api.getElements(summary.inspectionId),
            sections: this.api.getSections(summary.inspectionId),
            packageDiagnostics: this.api.getPackageDiagnostics(summary.inspectionId),
            schemaIssues: this.api.getSchemaIssues(summary.inspectionId),
          }),
        ),
        finalize(() => this.isAnalyzing.set(false)),
      )
      .subscribe({
        next: (result) => {
          this.elements.set(result.elements);
          this.sections.set(result.sections);
          this.packageDiagnostics.set(result.packageDiagnostics);
          this.schemaIssues.set(result.schemaIssues);
          this.schemaTarget.set(result.schemaIssues.targetVersion || 'Microsoft365');
        },
        error: (error) => {
          this.summary.set(null);
          this.error.set(this.readError(error));
        },
      });
  }

  selectElement(element: StructureElement | string): void {
    const inspectionId = this.summary()?.inspectionId;
    const elementId = typeof element === 'string' ? element : element.id;

    if (!inspectionId || this.selectedId() === elementId) {
      return;
    }

    this.selectedId.set(elementId);
    this.details.set(null);
    this.detailsLoading.set(true);

    this.api
      .getElementDetails(inspectionId, elementId)
      .pipe(finalize(() => this.detailsLoading.set(false)))
      .subscribe({
        next: (details) => {
          if (this.selectedId() === elementId) {
            this.details.set(details);
          }
        },
        error: (error) => this.error.set(this.readError(error)),
      });
  }

  goToElement(elementId: string | null): void {
    const target = elementId ? this.elements().find((element) => element.id === elementId) : undefined;

    if (!target) {
      return;
    }

    this.clearFilters();
    this.expandAncestors(target);
    this.selectElement(target);
    this.scrollToRow(target.id);
    this.showPartXml(target.partPath, target.id);
  }

  showElementXml(elementId: string, event?: Event): void {
    event?.stopPropagation();
    const inspectionId = this.summary()?.inspectionId;

    if (!inspectionId) {
      return;
    }

    this.xmlLoading.set(true);

    this.api
      .getElementXml(inspectionId, elementId)
      .pipe(finalize(() => this.xmlLoading.set(false)))
      .subscribe({
        next: (result) =>
          this.xmlContent.set({
            title: result.displayPath.split('/').pop() ?? result.elementId,
            meta: `${result.partPath} · ${result.displayPath}${
              result.sourceLine ? ` · wiersz ${result.sourceLine}` : ''
            }`,
            xml: result.xml,
            highlightLine: null,
          }),
        error: (error) => this.error.set(this.readError(error)),
      });
  }

  showPartXml(partPath: string, highlightElementId?: string): void {
    const inspectionId = this.summary()?.inspectionId;

    if (!inspectionId) {
      return;
    }

    this.xmlLoading.set(true);

    this.api
      .getPartXml(inspectionId, partPath, highlightElementId)
      .pipe(finalize(() => this.xmlLoading.set(false)))
      .subscribe({
        next: (result) =>
          this.xmlContent.set({
            title: result.partPath,
            meta: result.highlightLine
              ? `pełny XML części · zaznaczony element od wiersza ${result.highlightLine}`
              : 'pełny, surowy XML części pakietu',
            xml: result.xml,
            highlightLine: result.highlightLine,
          }),
        error: (error) => this.error.set(this.readError(error)),
      });
  }

  showSelectedPartXml(): void {
    const selected = this.details();

    if (selected) {
      this.showPartXml(selected.partPath, selected.id);
    }
  }

  changeSchemaTarget(targetVersion: string): void {
    const inspectionId = this.summary()?.inspectionId;

    if (!inspectionId || targetVersion === this.schemaTarget()) {
      return;
    }

    this.schemaTarget.set(targetVersion);

    this.api.getSchemaIssues(inspectionId, targetVersion).subscribe({
      next: (issues) => this.schemaIssues.set(issues),
      error: (error) => this.error.set(this.readError(error)),
    });
  }

  toggleCollapse(element: StructureElement, event: Event): void {
    event.stopPropagation();
    const collapsed = new Set(this.collapsed());

    if (!collapsed.delete(element.id)) {
      collapsed.add(element.id);
    }

    this.collapsed.set(collapsed);
  }

  isCollapsed(element: StructureElement): boolean {
    return this.collapsed().has(element.id);
  }

  collapseAll(): void {
    this.collapsed.set(
      new Set(
        this.elements()
          .filter((element) => element.hasChildren && element.depth > 0)
          .map((element) => element.id),
      ),
    );
  }

  expandAll(): void {
    this.collapsed.set(new Set<string>());
  }

  onRowKeydown(event: KeyboardEvent, element: StructureElement): void {
    switch (event.key) {
      case 'Enter':
      case ' ':
        event.preventDefault();
        this.selectElement(element);
        break;
      case 'ArrowRight':
        if (element.hasChildren && this.isCollapsed(element)) {
          event.preventDefault();
          this.toggleCollapse(element, event);
        }
        break;
      case 'ArrowLeft':
        if (element.hasChildren && !this.isCollapsed(element)) {
          event.preventDefault();
          this.toggleCollapse(element, event);
        }
        break;
    }
  }

  onListScroll(event: Event): void {
    const viewport = event.target as HTMLElement;
    this.scrollTop.set(viewport.scrollTop);
    this.viewportHeight.set(viewport.clientHeight);
  }

  setSearch(value: string): void {
    if (this.searchDebounce) {
      clearTimeout(this.searchDebounce);
      this.searchDebounce = null;
    }

    this.search.set(value);
  }

  onSearchInput(value: string): void {
    if (this.searchDebounce) {
      clearTimeout(this.searchDebounce);
    }

    this.searchDebounce = setTimeout(() => {
      this.searchDebounce = null;
      this.search.set(value);
    }, 200);
  }

  setSeverity(value: string): void {
    this.severityFilter.set(value);
  }

  toggleSeverityBadge(severity: string): void {
    this.severityFilter.set(this.severityFilter() === severity ? ALL : severity);
  }

  setCategory(value: string): void {
    this.categoryFilter.set(value);
  }

  setPart(value: string): void {
    this.partFilter.set(value);
  }

  toggleProblemsOnly(): void {
    this.problemsOnly.update((value) => !value);
  }

  clearFilters(): void {
    this.setSearch('');
    this.severityFilter.set(ALL);
    this.categoryFilter.set(ALL);
    this.partFilter.set(ALL);
    this.problemsOnly.set(false);
  }

  setTab(tab: 'sections' | 'package' | 'schema'): void {
    this.activeTab.set(tab);
  }

  indent(depth: number): number {
    return 8 + Math.min(depth, 12) * 12;
  }

  formatBytes(bytes: number): string {
    if (!bytes) {
      return '—';
    }

    const units = ['B', 'KB', 'MB', 'GB'];
    const unitIndex = Math.floor(Math.log(bytes) / Math.log(1024));
    return `${parseFloat((bytes / Math.pow(1024, unitIndex)).toFixed(1))} ${units[unitIndex]}`;
  }

  issueKey(index: number, issue: { code: string }): string {
    return `${index}-${issue.code}`;
  }

  schemaIssueKey(index: number, issue: SchemaIssue): string {
    return `${index}-${issue.code}`;
  }

  private matchesFilters(element: StructureElement): boolean {
    if (this.partFilter() !== ALL && element.partPath !== this.partFilter()) {
      return false;
    }

    if (this.categoryFilter() !== ALL && element.category !== this.categoryFilter()) {
      return false;
    }

    if (this.severityFilter() !== ALL && element.severity !== this.severityFilter()) {
      return false;
    }

    if (this.problemsOnly() && element.issueCount === 0) {
      return false;
    }

    const search = this.search().trim().toLocaleLowerCase();

    return !search || element.searchText.includes(search);
  }

  private applyCollapse(elements: StructureElement[]): StructureElement[] {
    const collapsed = this.collapsed();

    if (collapsed.size === 0) {
      return elements;
    }

    const visible: StructureElement[] = [];
    let hiddenBelowDepth: number | null = null;

    for (const element of elements) {
      if (hiddenBelowDepth !== null && element.depth > hiddenBelowDepth) {
        continue;
      }

      hiddenBelowDepth = null;
      visible.push(element);

      if (element.hasChildren && collapsed.has(element.id)) {
        hiddenBelowDepth = element.depth;
      }
    }

    return visible;
  }

  private expandAncestors(element: StructureElement): void {
    const byId = new Map(this.elements().map((item) => [item.id, item]));
    const collapsed = new Set(this.collapsed());
    let parentId = element.parentId;

    while (parentId) {
      collapsed.delete(parentId);
      parentId = byId.get(parentId)?.parentId ?? null;
    }

    this.collapsed.set(collapsed);
  }

  private scrollToRow(elementId: string): void {
    const index = this.visibleElements().findIndex((element) => element.id === elementId);

    if (index < 0) {
      return;
    }

    const viewport = this.elementListRef?.nativeElement;
    const viewportHeight = viewport?.clientHeight || this.viewportHeight();
    const top = Math.max(0, index * ROW_HEIGHT - Math.floor(viewportHeight / 2));

    this.scrollTop.set(top);
    this.viewportHeight.set(viewportHeight);

    if (viewport) {
      viewport.scrollTop = top;
    }
  }

  private releasePreviousInspection(): void {
    const inspectionId = this.summary()?.inspectionId;

    if (inspectionId) {
      this.api.deleteInspection(inspectionId).subscribe({ error: () => undefined });
    }
  }

  private resetResults(): void {
    this.error.set(null);
    this.summary.set(null);
    this.elements.set([]);
    this.sections.set([]);
    this.packageDiagnostics.set(null);
    this.schemaIssues.set(null);
    this.selectedId.set(null);
    this.details.set(null);
    this.xmlContent.set(null);
    this.activeTab.set('sections');
    this.collapsed.set(new Set<string>());
    this.scrollTop.set(0);
    this.clearFilters();
  }

  private readError(error: unknown): string {
    if (error instanceof HttpErrorResponse) {
      const body = error.error as { detail?: string; error?: string; title?: string } | null;
      return body?.detail ?? body?.error ?? body?.title ?? 'Nie udało się wykonać operacji.';
    }

    return 'Nie udało się wykonać operacji.';
  }
}
