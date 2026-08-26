import {
  ChangeDetectorRef,
  Component,
  ElementRef,
  EventEmitter,
  Input,
  Output,
  ViewChild,
  AfterViewInit,
  OnDestroy,
  inject,
  signal,
  computed,
  ViewChildren,
  QueryList,
  ViewEncapsulation,
  input
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import {
  EditorCommand,
  EditorState,
  HeadingLevel,
  TextFormatting,
  ParagraphStyle,
  PageMargins,
  PageSize,
  HeaderFooterContent,
  SectionHeaderFooter,
  Footnote,
  Endnote
} from '../../models/document.model';
import { normalizeWhitespace, resolvePlainText } from '../../core/utils/paste-text.util';
import {
  applyListLabels,
  bulletGlyphFromContract,
  ensureBulletMarkers,
  stripListLabelAttributes,
  synthesizeBulletMarker,
} from '../../core/utils/list-label.util';
import {
  applyColumnWidths,
  buildTableGrid,
  columnBoundaryForCell,
  readColumnWidths,
  writeColgroupWidths,
  type ColumnEdge,
  type TableGrid,
} from '../../core/utils/table-grid.util';
import { wordSingleFactor } from '../../core/utils/word-line-spacing.util';
import { CSS_PX_PER_CM } from '../../core/utils/units.util';
import {
  EMU_PER_PX,
  HfBandGeometry,
  bandToContract,
  computeAnchorBadgePosition,
  contractToBand,
  findAnchorParagraph,
  isFloatingElement,
  isPointerOnEdge,
  viewportDeltaToLayout,
} from '../../core/utils/floating-anchor.util';
import {
  decodeShapeXml,
  encodeShapeXml,
  setAnchorPositionPageEmu,
  scaleShapeExtent,
  rescaleShapePreview,
} from '../../core/utils/shape-xml.util';

const ANCHOR_BADGE_SVG =
  '<svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg" focusable="false" aria-hidden="true">'
  + '<path d="M17 15l1.55 1.55c-.96 1.69-3.33 3.04-5.55 3.37V11h3V9h-3V7.82C14.16 7.4 15 6.3 15 5'
  + 'c0-1.65-1.35-3-3-3S9 3.35 9 5c0 1.3.84 2.4 2 2.82V9H8v2h3v8.92c-2.22-.33-4.59-1.68-5.55-3.37'
  + 'L7 15l-4-3v3c0 3.88 4.92 7 9 7s9-3.12 9-7v-3l-4 3zM12 4c.55 0 1 .45 1 1s-.45 1-1 1-1-.45-1-1'
  + ' .45-1 1-1z"/></svg>';

export interface ColumnLayoutGeo {
  count: number;
  equalWidth: boolean;
  spaceCm: number;
  separator: boolean;
  widthsCm?: number[];
  spacesCm?: number[];
}

export interface PageGeometry {
  widthCm: number;
  heightCm: number;
  orientation: 'portrait' | 'landscape';
  margins: PageMargins;
  headerDistanceCm?: number;
  footerDistanceCm?: number;
  columns?: ColumnLayoutGeo;
}

export interface EndnotePageRegion {
  pageIndex: number;
  topPx: number;
  ids: string[];
  continuation: boolean;
}

export interface FootnotePageRegion {
  pageIndex: number;
  bottomPx: number;
  ids: string[];
  continuation: boolean;
}

const TWIPS_PER_CM = 1440 / 2.54;
export function parseColumnDataAttributes(el: Element): ColumnLayoutGeo | undefined {
  const count = parseInt(el.getAttribute('data-col-count') ?? '', 10);
  if (!Number.isFinite(count) || count <= 1) return undefined;
  const spaceTw = parseInt(el.getAttribute('data-col-space-tw') ?? '', 10);
  const equal = el.getAttribute('data-col-equal') !== '0';
  const geo: ColumnLayoutGeo = {
    count,
    equalWidth: equal,
    spaceCm: Number.isFinite(spaceTw) ? spaceTw / TWIPS_PER_CM : 720 / TWIPS_PER_CM,
    separator: el.getAttribute('data-col-sep') === '1',
  };
  if (!equal) {
    const parseCsv = (name: string): number[] | undefined => {
      const raw = el.getAttribute(name);
      if (!raw) return undefined;
      const vals = raw.split(',').map(s => parseInt(s, 10)).filter(n => Number.isFinite(n));
      return vals.length > 0 ? vals.map(tw => tw / TWIPS_PER_CM) : undefined;
    };
    geo.widthsCm = parseCsv('data-col-widths-tw');
    geo.spacesCm = parseCsv('data-col-spaces-tw');
  }
  return geo;
}

interface PageColumnBand {
  start: number;
  columns: ColumnLayoutGeo;
  heightPx: number;
}

@Component({
  selector: 'd2-wysiwyg-editor',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './wysiwyg-editor.html',
  styleUrl: './wysiwyg-editor.scss',
  encapsulation: ViewEncapsulation.None
})
export class WysiwygEditorComponent implements AfterViewInit, OnDestroy {
  editorContent!: ElementRef<HTMLDivElement>;

  @ViewChildren('pageEditor') pageEditorRefs!: QueryList<ElementRef<HTMLDivElement>>;
  @ViewChild('headerContent') headerContentEl?: ElementRef<HTMLDivElement>;
  @ViewChild('footerContent') footerContentEl?: ElementRef<HTMLDivElement>;

  private getActiveEditor(): HTMLDivElement | null {
    const section = this.editingSection();
    if (section === 'header' && this.headerContentEl?.nativeElement) {
      return this.headerContentEl.nativeElement;
    }
    if (section === 'footer' && this.footerContentEl?.nativeElement) {
      return this.footerContentEl.nativeElement;
    }
    return this.editorContent?.nativeElement ?? null;
  }
  
  @Input() set content(value: string) {
    if (this._content() === value) {
      return;
    }

    this._content.set(value);
    if (!this._isInternalUpdate) {
      const unwrapped = this._captureDocumentDefaults(value);
      const splitPages = this._splitHtmlIntoPages(unwrapped ?? value ?? '<p></p>');
      const pages = splitPages.length ? splitPages : ['<p></p>'];
      this.pageContents.set(pages);
      this.pageGeometries.set(this._deriveGeometriesForPages(pages));
      this._schedulePaginate('content-input');
      setTimeout(() => {
        this.refreshListLabels();
        this._syncInitialFormatting();
        this.syncFootnotesWithBody();
        this.syncEndnotesWithBody();
      }, 0);
    }
  }
  
  pageMargins = input<PageMargins>({ top: 2.5, bottom: 2.5, left: 2.5, right: 2.5 });
  private readonly _orientationSig = signal<'portrait' | 'landscape'>('portrait');
  @Input() set pageOrientation(value: 'portrait' | 'landscape') {
    this._orientationSig.set(value === 'landscape' ? 'landscape' : 'portrait');
  }
  get pageOrientation(): 'portrait' | 'landscape' {
    return this._orientationSig();
  }
  private readonly _pageSizeSig = signal<PageSize | null>(null);
  @Input() set pageSize(value: PageSize | undefined) {
    this._pageSizeSig.set(value ?? null);
  }

  readonly baseGeometry = computed<PageGeometry>(() => {
    const size = this._pageSizeSig();
    const landscape = this._orientationSig() === 'landscape';
    let widthCm = size?.widthCm ?? (landscape ? 29.7 : 21);
    let heightCm = size?.heightCm ?? (landscape ? 21 : 29.7);
    if (widthCm > 0 && heightCm > 0 && landscape !== widthCm >= heightCm) {
      [widthCm, heightCm] = [heightCm, widthCm];
    }
    return {
      widthCm,
      heightCm,
      orientation: landscape ? 'landscape' : 'portrait',
      margins: this.pageMargins(),
      columns: this._baseColumns() ?? undefined,
    };
  });

  private readonly _baseColumns = signal<ColumnLayoutGeo | null>(null);

  readonly pageGeometries = signal<PageGeometry[]>([]);

  readonly pageBodyHeights = signal<number[]>([]);

  pageBodyHeightPx(index: number): number | null {
    if (this.pageColumnCount(index) === null) return null;
    return this.pageBodyHeights()[index] ?? null;
  }

  pageBodyFlex(index: number): string | null {
    return this.pageBodyHeightPx(index) !== null ? 'none' : null;
  }

  readonly pageSectionIndexes = signal<number[]>([]);

  geometryFor(index: number): PageGeometry {
    return this.pageGeometries()[index] ?? this.baseGeometry();
  }

  pageWidthPx(index: number): number {
    return this.geometryFor(index).widthCm * CSS_PX_PER_CM;
  }
  pageHeightPx(index: number): number {
    return this.geometryFor(index).heightCm * CSS_PX_PER_CM;
  }
  isLandscapePage(index: number): boolean {
    return this.geometryFor(index).orientation === 'landscape';
  }
  pageMarginPx(index: number, side: 'top' | 'bottom' | 'left' | 'right'): number {
    return this.geometryFor(index).margins[side] * CSS_PX_PER_CM;
  }
  pageColumnCount(index: number): number | null {
    const c = this.geometryFor(index).columns;
    return c && c.count > 1 ? c.count : null;
  }
  pageColumnGapPx(index: number): number | null {
    const c = this.geometryFor(index).columns;
    return c && c.count > 1 ? Math.max(0, c.spaceCm) * CSS_PX_PER_CM : null;
  }
  pageColumnRule(index: number): string | null {
    const c = this.geometryFor(index).columns;
    return c && c.count > 1 && c.separator ? '1px solid #bbb' : null;
  }
  private _bandCmFor(geo: PageGeometry, side: 'header' | 'footer'): number {
    const margin = side === 'header' ? geo.margins.top : geo.margins.bottom;
    const dist = side === 'header' ? geo.headerDistanceCm : geo.footerDistanceCm;
    if (dist == null) return side === 'header' ? this._headerHeight() : this._footerHeight();
    const band = margin - dist;
    return Math.max(0.3, Math.min(8, band > 0 ? band : margin));
  }
  private _distanceCmFor(geo: PageGeometry, side: 'header' | 'footer'): number {
    const explicit = side === 'header' ? geo.headerDistanceCm : geo.footerDistanceCm;
    if (explicit != null) return explicit;
    const margin = side === 'header' ? geo.margins.top : geo.margins.bottom;
    return Math.max(0, margin - this._bandCmFor(geo, side));
  }
  headerOffsetPx(index: number): number { return this._distanceCmFor(this.geometryFor(index), 'header') * CSS_PX_PER_CM; }
  footerOffsetPx(index: number): number { return this._distanceCmFor(this.geometryFor(index), 'footer') * CSS_PX_PER_CM; }
  headerBandPx(index: number): number { return this._bandCmFor(this.geometryFor(index), 'header') * CSS_PX_PER_CM; }
  footerBandPx(index: number): number { return this._bandCmFor(this.geometryFor(index), 'footer') * CSS_PX_PER_CM; }
  @Input() readOnly = false;
  @Input() showMarginGuides = false;
  
  @Input() set headerContent(value: HeaderFooterContent | undefined) {
    if (value) {
      this._headerHtml.set(value.html || '');
      this._headerHeight.set(value.height || 1.27);
      if (value.differentFirstPage !== undefined) {
        this._headerDifferentFirstPage.set(value.differentFirstPage ?? false);
      }
      if (value.firstPageHtml !== undefined) {
        this._headerFirstPageHtml.set(value.firstPageHtml ?? '');
      }
      if (value.differentOddEven !== undefined) {
        this._headerDifferentOddEven.set(value.differentOddEven ?? false);
      }
      if (value.oddHtml !== undefined) {
        this._headerOddHtml.set(value.oddHtml ?? '');
      }
      if (value.evenHtml !== undefined) {
        this._headerEvenHtml.set(value.evenHtml ?? '');
      }
      this.invalidateHeaderFooterCache();
    }
  }

  @Input() set footerContent(value: HeaderFooterContent | undefined) {
    if (value) {
      this._footerHtml.set(value.html || '');
      this._footerHeight.set(value.height || 1.27);
      if (value.differentFirstPage !== undefined) {
        this._footerDifferentFirstPage.set(value.differentFirstPage ?? false);
      }
      if (value.firstPageHtml !== undefined) {
        this._footerFirstPageHtml.set(value.firstPageHtml ?? '');
      }
      if (value.differentOddEven !== undefined) {
        this._footerDifferentOddEven.set(value.differentOddEven ?? false);
      }
      if (value.oddHtml !== undefined) {
        this._footerOddHtml.set(value.oddHtml ?? '');
      }
      if (value.evenHtml !== undefined) {
        this._footerEvenHtml.set(value.evenHtml ?? '');
      }
      this.invalidateHeaderFooterCache();
    }
  }
  
  @Input() set sectionHeadersFooters(value: SectionHeaderFooter[] | undefined) {
    this._sectionHF.set(value ?? []);
    this.invalidateHeaderFooterCache();
  }
  private readonly _sectionHF = signal<SectionHeaderFooter[]>([]);
  @Output() sectionHeadersFootersChange = new EventEmitter<SectionHeaderFooter[]>();

  @Output() contentChange = new EventEmitter<string>();
  @Output() stateChange = new EventEmitter<EditorState>();
  @Output() selectionChange = new EventEmitter<Selection | null>();
  @Output() pagesChange = new EventEmitter<number>();
  @Output() headerChange = new EventEmitter<HeaderFooterContent>();
  @Output() footerChange = new EventEmitter<HeaderFooterContent>();
  @Output() editingSectionChange = new EventEmitter<'header' | 'footer' | 'body'>();
  @Output() imageSelectionChange = new EventEmitter<{
    widthPx: number;
    heightPx: number;
    aspectRatio: number;
    alignment: 'left' | 'center' | 'right' | null;
    positionMode: 'inline' | 'square' | 'topBottom' | 'front' | 'behind';
    border: { enabled: boolean; color: string; widthPx: number; style: 'solid' | 'dashed' | 'dotted' };
    crop: { left: number; right: number; top: number; bottom: number };
  } | null>();
  @Output() sectionGeometryChange = new EventEmitter<{ section: 'header' | 'footer'; topCm: number; bottomCm: number }>();

  private _sectionResizeObserver?: ResizeObserver;
  @Output() openHeaderFooterSettings = new EventEmitter<{
    headerMargin: number;
    footerMargin: number;
    differentFirstPage: boolean;
    differentOddEven: boolean;
  }>();

  private _content = signal<string>('');
  private _isInternalUpdate = false;
  private _isDirty = false;
  private _persistTimer: ReturnType<typeof setTimeout> | null = null;
  private undoStack: string[] = [];
  private redoStack: { html: string; caret: { block: number; offset: number } | null }[] = [];
  private lastSavedContent = '';
  private pageCheckInterval?: ReturnType<typeof setInterval>;
  private selectedImageWrapper: HTMLElement | null = null;
  private draggedImageWrapper: HTMLElement | null = null;
  private imageDragCaret: HTMLElement | null = null;
  private imageMoveState: {
    wrapper: HTMLElement;
    startX: number;
    startY: number;
    isDragging: boolean;
  } | null = null;
  private imageResizeState: {
    wrapper: HTMLElement;
    startX: number;
    startY: number;
    startWidth: number;
    startHeight: number;
    axis: 'x' | 'y' | 'both';
  } | null = null;

  private selectedTextBox: HTMLElement | null = null;
  private edgeCursorTextBox: HTMLElement | null = null;
  private anchorBadge: HTMLElement | null = null;
  private anchorBadgeTarget: HTMLElement | null = null;
  private _anchorBadgeRafHandle: number | null = null;

  private tableResizeState: {
    type: 'col' | 'row' | 'table';
    table: HTMLTableElement;
    startX: number;
    startY: number;
    boundaryCol: number;
    rowIndex: number;
    grid: TableGrid;
    startColumnWidths: number[];
    columnWidths: number[];
    startHeight: number;
    startTableWidth: number;
    moved: boolean;
  } | null = null;

  pageContents = signal<string[]>(['<p></p>']);
  activePageIndex = signal<number>(0);

  private _sanitizer = inject(DomSanitizer);
  private _hostRef = inject(ElementRef<HTMLElement>);
  private _cdr = inject(ChangeDetectorRef);
  private _safeHtmlCache: Array<{ html: string; safe: SafeHtml }> = [];
  getPageContentSafe(index: number): SafeHtml {
    const html = this.pageContents()[index] ?? '';
    const cached = this._safeHtmlCache[index];
    if (cached && cached.html === html) {
      return cached.safe;
    }
    const safe = this._sanitizer.bypassSecurityTrustHtml(html);
    this._safeHtmlCache[index] = { html, safe };
    return safe;
  }

  private _safeHeaderCache = new Map<number, { html: string; safe: SafeHtml }>();
  private _safeFooterCache = new Map<number, { html: string; safe: SafeHtml }>();

  getHeaderContentSafe(pageIndex: number): SafeHtml {
    const html = this._positionBandAnchors(this.getHeaderContent(pageIndex) ?? '', pageIndex, 'header');
    const cached = this._safeHeaderCache.get(pageIndex);
    if (cached && cached.html === html) return cached.safe;
    const safe = this._sanitizer.bypassSecurityTrustHtml(html);
    this._safeHeaderCache.set(pageIndex, { html, safe });
    return safe;
  }

  getFooterContentSafe(pageIndex: number): SafeHtml {
    const html = this._positionBandAnchors(this.getFooterContent(pageIndex) ?? '', pageIndex, 'footer');
    const cached = this._safeFooterCache.get(pageIndex);
    if (cached && cached.html === html) return cached.safe;
    const safe = this._sanitizer.bypassSecurityTrustHtml(html);
    this._safeFooterCache.set(pageIndex, { html, safe });
    return safe;
  }

  private _measuredBandHeights:
    { headerFirst: number; headerRest: number; footerFirst: number; footerRest: number } | null = null;

  private _bandGeoFor(pageIndex: number, band: 'header' | 'footer'): HfBandGeometry {
    const measured = this._measuredBandHeights;
    const footerBandActualPx = Math.max(
      this.footerBandPx(pageIndex),
      (pageIndex === 0 ? measured?.footerFirst : measured?.footerRest) ?? 0);
    const bandTopPx = band === 'header'
      ? this.headerOffsetPx(pageIndex)
      : this.pageHeightPx(pageIndex) - this.footerOffsetPx(pageIndex) - footerBandActualPx;
    return {
      band,
      marginLeftPx: this.pageMarginPx(pageIndex, 'left'),
      marginTopPx: this.pageMarginPx(pageIndex, 'top'),
      bandTopPx,
    };
  }

  private _bandGeoForElement(el: HTMLElement): HfBandGeometry | null {
    if (el.closest('.header-editor-content')) return this._bandGeoFor(this.editingHfPageIndex(), 'header');
    if (el.closest('.footer-editor-content')) return this._bandGeoFor(this.editingHfPageIndex(), 'footer');
    return null;
  }

  private _positionBandAnchors(html: string, pageIndex: number, band: 'header' | 'footer'): string {
    if (!html) return html;
    const mayHaveAnchors = html.includes('data-pos-mode')
      || html.includes('docx-shape') || html.includes('docx-textbox');
    if (!mayHaveAnchors) return html;
    const tpl = document.createElement('template');
    tpl.innerHTML = html;
    const anchored = tpl.content.querySelectorAll<HTMLElement>(
      'img[data-pos-mode="front"], img[data-pos-mode="behind"]');
    const floated = Array.from(
      tpl.content.querySelectorAll<HTMLElement>('.docx-shape, .docx-textbox'),
    ).filter(el => el.style.position === 'absolute');
    if (anchored.length === 0 && floated.length === 0) return html;

    const geo = this._bandGeoFor(pageIndex, band);
    anchored.forEach(img => {
      const xPx = Math.round(Number(img.getAttribute('data-x-emu') ?? 0) / EMU_PER_PX);
      const yPx = Math.round(Number(img.getAttribute('data-y-emu') ?? 0) / EMU_PER_PX);
      const { leftPx, topPx } = contractToBand(xPx, yPx, geo);
      img.style.position = 'absolute';
      img.style.left = `${Math.round(leftPx)}px`;
      img.style.top = `${Math.round(topPx)}px`;
      img.style.margin = '0';
      img.style.zIndex = img.dataset['posMode'] === 'behind' ? '-1' : '10';
    });
    floated.forEach(el => {
      const xPx = parseFloat(el.style.left) || 0;
      const yPx = parseFloat(el.style.top) || 0;
      const { leftPx, topPx } = contractToBand(xPx, yPx, geo);
      el.style.left = `${Math.round(leftPx)}px`;
      el.style.top = `${Math.round(topPx)}px`;
    });
    return tpl.innerHTML;
  }

  private invalidateHeaderFooterCache(): void {
    this._safeHeaderCache.clear();
    this._safeFooterCache.clear();
  }

  private readonly _footnotes = signal<Footnote[]>([]);
  readonly footnoteList = computed(() => this._footnotes());

  private readonly _footnoteNumberFormat = signal<string | undefined>(undefined);
  private readonly _endnoteNumberFormat = signal<string | undefined>(undefined);

  @Input() set footnoteNumberFormat(value: string | undefined) {
    this._footnoteNumberFormat.set(value || undefined);
  }
  @Input() set endnoteNumberFormat(value: string | undefined) {
    this._endnoteNumberFormat.set(value || undefined);
  }

  private _formatNoteLabel(n: number, fmt: string | undefined, isEndnote: boolean): string {
    switch (fmt) {
      case 'decimal': return String(n);
      case 'lowerRoman': return this._toLowerRoman(n);
      case 'upperRoman': return this._toLowerRoman(n).toUpperCase();
      case 'lowerLetter': return this._toWordLetters(n);
      case 'upperLetter': return this._toWordLetters(n).toUpperCase();
      default: return isEndnote ? this._toLowerRoman(n) : String(n);
    }
  }

  private _toWordLetters(n: number): string {
    if (!Number.isFinite(n) || n <= 0) return String(n);
    const k = Math.floor(n);
    const letter = String.fromCharCode(97 + (k - 1) % 26);
    const count = Math.floor((k - 1) / 26) + 1;
    return letter.repeat(count);
  }

  private readonly _footnoteLayout = signal<FootnotePageRegion[]>([]);

  footnoteRegionFor(pageIndex: number): FootnotePageRegion | null {
    const region = this._footnoteLayout().find(r => r.pageIndex === pageIndex);
    if (!region) return null;
    return this.footnoteEntriesFor(region).length > 0 ? region : null;
  }

  footnoteEntriesFor(region: FootnotePageRegion): { fn: Footnote; number: number; label: string }[] {
    const list = this.footnoteList();
    const indexById = new Map(list.map((f, i) => [f.id, i]));
    const entries: { fn: Footnote; number: number; label: string }[] = [];
    for (const id of region.ids) {
      const idx = indexById.get(id);
      if (idx === undefined) continue;
      entries.push({ fn: list[idx], number: idx + 1, label: this._formatNoteLabel(idx + 1, this._footnoteNumberFormat(), false) });
    }
    return entries;
  }

  @Input() set footnotes(value: Footnote[] | undefined) {
    this._footnotes.set(value ? value.map(f => ({ ...f })) : []);
    this._schedulePaginate('footnotes-input');
  }

  @Output() footnotesChange = new EventEmitter<Footnote[]>();

  private _footnoteHtmlCache = new Map<string, SafeHtml>();

  footnoteSafeHtml(fn: Footnote): SafeHtml {
    const key = `${fn.id}\x00${fn.html}`;
    let safe = this._footnoteHtmlCache.get(key);
    if (!safe) {
      safe = this._sanitizer.bypassSecurityTrustHtml(fn.html || '<p></p>');
      this._footnoteHtmlCache.set(key, safe);
    }
    return safe;
  }

  getFootnotes(): Footnote[] {
    return this._footnotes().map(f => ({ ...f }));
  }

  private _normalizeEditedNbsp(root: HTMLElement): void {
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const value = node.nodeValue ?? '';
      if (!value.includes('\u00A0')) continue;
      const cleaned = value.replace(/(?<=\S)\u00A0|\u00A0(?=\S)/g, ' ');
      if (cleaned !== value) node.nodeValue = cleaned;
    }
  }

  private _isSameRenderedHtml(stored: string, live: HTMLElement): boolean {
    if (stored === live.innerHTML) return true;
    const liveClone = live.cloneNode(true) as HTMLElement;
    this._stripFmtTrailingBr(liveClone);
    if (stored === liveClone.innerHTML) return true;
    const probe = document.createElement('div');
    probe.innerHTML = stored;
    return probe.innerHTML === liveClone.innerHTML;
  }

  commitFootnoteContent(id: string, event: Event): void {
    if (this.readOnly) return;
    const el = event.target as HTMLElement | null;
    if (!el) return;
    const current = this._footnotes();
    const idx = current.findIndex(f => f.id === id);
    if (idx < 0 || this._isSameRenderedHtml(current[idx].html, el)) return;

    this._normalizeEditedNbsp(el);
    const clean = el.cloneNode(true) as HTMLElement;
    this._stripFmtTrailingBr(clean);
    const html = clean.innerHTML;
    const updated = current.map(f => (f.id === id ? { ...f, html } : f));
    this._footnotes.set(updated);
    this.footnotesChange.emit(this.getFootnotes());
    this._schedulePaginate('footnote-edit');
  }

  private _footnoteIdsIn(block: HTMLElement): string[] {
    const ids: string[] = [];
    if (block.matches?.('sup.footnote-ref[data-footnote-id]')) {
      const id = block.getAttribute('data-footnote-id');
      if (id) ids.push(id);
    }
    block.querySelectorAll<HTMLElement>('sup.footnote-ref[data-footnote-id]').forEach(el => {
      const id = el.getAttribute('data-footnote-id');
      if (id) ids.push(id);
    });
    return ids;
  }

  private _footnoteReferenceElements(): HTMLElement[] {
    const refs: HTMLElement[] = [];
    for (const page of this.pageEditorRefs?.toArray() ?? []) {
      page.nativeElement
        .querySelectorAll<HTMLElement>('sup.footnote-ref[data-footnote-id]')
        .forEach(el => refs.push(el));
    }
    return refs;
  }

  syncFootnotesWithBody(): void {
    const refEls = this._footnoteReferenceElements();

    const order: string[] = [];
    const numberById = new Map<string, number>();
    for (const el of refEls) {
      const id = el.getAttribute('data-footnote-id') ?? '';
      if (!id) continue;
      if (!numberById.has(id)) {
        numberById.set(id, order.length + 1);
        order.push(id);
      }
    }

    for (const el of refEls) {
      const id = el.getAttribute('data-footnote-id') ?? '';
      const number = numberById.get(id);
      if (!number) continue;
      const label = this._formatNoteLabel(number, this._footnoteNumberFormat(), false);
      if (el.textContent !== label) el.textContent = label;
      el.setAttribute('aria-label', `Przypis ${label}`);
    }

    const byId = new Map(this._footnotes().map(f => [f.id, f]));
    const reordered: Footnote[] = order.map(id => byId.get(id) ?? { id, html: '<p></p>' });

    if (this._footnotesChanged(reordered)) {
      this._footnotes.set(reordered);
      this.footnotesChange.emit(this.getFootnotes());
      this._schedulePaginate('footnotes-sync');
    }
  }

  private _footnotesChanged(next: Footnote[]): boolean {
    const cur = this._footnotes();
    if (cur.length !== next.length) return true;
    for (let i = 0; i < cur.length; i++) {
      if (cur[i].id !== next[i].id || cur[i].html !== next[i].html) return true;
    }
    return false;
  }

  addFootnoteAtCursor(): string | null {
    if (this.readOnly) return null;
    const editor = this.getActiveEditor();
    if (!editor) return null;

    const id = `fn-${Date.now()}-${Math.floor(Math.random() * 100000)}`;
    const sup = document.createElement('sup');
    sup.className = 'footnote-ref';
    sup.setAttribute('data-footnote-id', id);
    sup.setAttribute('aria-label', 'Przypis');
    sup.textContent = '?';

    const selection = window.getSelection();
    if (selection && selection.rangeCount > 0 && editor.contains(selection.anchorNode)) {
      const range = selection.getRangeAt(0);
      range.deleteContents();
      range.insertNode(sup);
      range.setStartAfter(sup);
      range.collapse(true);
      selection.removeAllRanges();
      selection.addRange(range);
    } else {
      editor.appendChild(sup);
    }

    this._footnotes.set([...this._footnotes(), { id, html: '<p></p>' }]);
    this.syncFootnotesWithBody();
    this.contentChange.emit(this.getContent());
    return id;
  }

  removeFootnote(id: string): void {
    if (this.readOnly) return;

    let removedFromDom = false;
    for (const page of this.pageEditorRefs?.toArray() ?? []) {
      page.nativeElement
        .querySelectorAll<HTMLElement>(`sup.footnote-ref[data-footnote-id="${id}"]`)
        .forEach(el => { el.remove(); removedFromDom = true; });
    }

    this.syncFootnotesWithBody();
    if (removedFromDom) this.contentChange.emit(this.getContent());
  }

  private readonly _endnotes = signal<Endnote[]>([]);
  readonly endnoteList = computed(() => this._endnotes());

  private readonly _endnoteLayout = signal<EndnotePageRegion[]>([]);

  endnoteRegionFor(pageIndex: number): EndnotePageRegion | null {
    const region = this._endnoteLayout().find(r => r.pageIndex === pageIndex);
    if (!region) return null;
    return this.endnoteEntriesFor(region).length > 0 ? region : null;
  }

  endnoteEntriesFor(region: EndnotePageRegion): { en: Endnote; number: number; label: string }[] {
    const list = this.endnoteList();
    const indexById = new Map(list.map((e, i) => [e.id, i]));
    const entries: { en: Endnote; number: number; label: string }[] = [];
    for (const id of region.ids) {
      const idx = indexById.get(id);
      if (idx === undefined) continue;
      entries.push({ en: list[idx], number: idx + 1, label: this._formatNoteLabel(idx + 1, this._endnoteNumberFormat(), true) });
    }
    return entries;
  }

  @Input() set endnotes(value: Endnote[] | undefined) {
    this._endnotes.set(value ? value.map(e => ({ ...e })) : []);
    this._schedulePaginate('endnotes-input');
  }

  @Output() endnotesChange = new EventEmitter<Endnote[]>();

  private _endnoteHtmlCache = new Map<string, SafeHtml>();

  endnoteSafeHtml(en: Endnote): SafeHtml {
    const key = `${en.id}\x00${en.html}`;
    let safe = this._endnoteHtmlCache.get(key);
    if (!safe) {
      safe = this._sanitizer.bypassSecurityTrustHtml(en.html || '<p></p>');
      this._endnoteHtmlCache.set(key, safe);
    }
    return safe;
  }

  getEndnotes(): Endnote[] {
    return this._endnotes().map(e => ({ ...e }));
  }

  commitEndnoteContent(id: string, event: Event): void {
    if (this.readOnly) return;
    const el = event.target as HTMLElement | null;
    if (!el) return;
    const current = this._endnotes();
    const idx = current.findIndex(e => e.id === id);
    if (idx < 0 || this._isSameRenderedHtml(current[idx].html, el)) return;

    this._normalizeEditedNbsp(el);
    const clean = el.cloneNode(true) as HTMLElement;
    this._stripFmtTrailingBr(clean);
    const html = clean.innerHTML;
    const updated = current.map(e => (e.id === id ? { ...e, html } : e));
    this._endnotes.set(updated);
    this.endnotesChange.emit(this.getEndnotes());
    this._schedulePaginate('endnote-edit');
  }

  private _endnoteReferenceElements(): HTMLElement[] {
    const refs: HTMLElement[] = [];
    for (const page of this.pageEditorRefs?.toArray() ?? []) {
      page.nativeElement
        .querySelectorAll<HTMLElement>('sup.endnote-ref[data-endnote-id]')
        .forEach(el => refs.push(el));
    }
    return refs;
  }

  syncEndnotesWithBody(): void {
    const refEls = this._endnoteReferenceElements();

    const order: string[] = [];
    const numberById = new Map<string, number>();
    for (const el of refEls) {
      const id = el.getAttribute('data-endnote-id') ?? '';
      if (!id) continue;
      if (!numberById.has(id)) {
        numberById.set(id, order.length + 1);
        order.push(id);
      }
    }

    for (const el of refEls) {
      const id = el.getAttribute('data-endnote-id') ?? '';
      const number = numberById.get(id);
      if (!number) continue;
      const label = this._formatNoteLabel(number, this._endnoteNumberFormat(), true);
      if (el.textContent !== label) el.textContent = label;
      el.setAttribute('aria-label', `Przypis końcowy ${label}`);
    }

    const byId = new Map(this._endnotes().map(e => [e.id, e]));
    const reordered: Endnote[] = order.map(id => byId.get(id) ?? { id, html: '<p></p>' });

    if (this._endnotesChanged(reordered)) {
      this._endnotes.set(reordered);
      this.endnotesChange.emit(this.getEndnotes());
      this._schedulePaginate('endnotes-sync');
    }
  }

  private _endnotesChanged(next: Endnote[]): boolean {
    const cur = this._endnotes();
    if (cur.length !== next.length) return true;
    for (let i = 0; i < cur.length; i++) {
      if (cur[i].id !== next[i].id || cur[i].html !== next[i].html) return true;
    }
    return false;
  }

  private _toLowerRoman(n: number): string {
    if (!Number.isFinite(n) || n <= 0) return String(n);
    const table: readonly [number, string][] = [
      [1000, 'm'], [900, 'cm'], [500, 'd'], [400, 'cd'],
      [100, 'c'], [90, 'xc'], [50, 'l'], [40, 'xl'],
      [10, 'x'], [9, 'ix'], [5, 'v'], [4, 'iv'], [1, 'i'],
    ];
    let rem = Math.floor(n);
    let out = '';
    for (const [value, symbol] of table) {
      while (rem >= value) {
        out += symbol;
        rem -= value;
      }
    }
    return out;
  }

  addEndnoteAtCursor(): string | null {
    if (this.readOnly) return null;
    const editor = this.getActiveEditor();
    if (!editor) return null;

    const id = `en-${Date.now()}-${Math.floor(Math.random() * 100000)}`;
    const sup = document.createElement('sup');
    sup.className = 'endnote-ref';
    sup.setAttribute('data-endnote-id', id);
    sup.setAttribute('aria-label', 'Przypis końcowy');
    sup.textContent = '?';

    const selection = window.getSelection();
    if (selection && selection.rangeCount > 0 && editor.contains(selection.anchorNode)) {
      const range = selection.getRangeAt(0);
      range.deleteContents();
      range.insertNode(sup);
      range.setStartAfter(sup);
      range.collapse(true);
      selection.removeAllRanges();
      selection.addRange(range);
    } else {
      editor.appendChild(sup);
    }

    this._endnotes.set([...this._endnotes(), { id, html: '<p></p>' }]);
    this.syncEndnotesWithBody();
    this.contentChange.emit(this.getContent());
    return id;
  }

  removeEndnote(id: string): void {
    if (this.readOnly) return;

    let removedFromDom = false;
    for (const page of this.pageEditorRefs?.toArray() ?? []) {
      page.nativeElement
        .querySelectorAll<HTMLElement>(`sup.endnote-ref[data-endnote-id="${id}"]`)
        .forEach(el => { el.remove(); removedFromDom = true; });
    }

    this.syncEndnotesWithBody();
    if (removedFromDom) this.contentChange.emit(this.getContent());
  }

  onEditorClick(ev: MouseEvent): void {
    this._navigateToNoteFromRef(ev);
    this._navigateFromInternalAnchor(ev);
    this.stopEditingHeaderFooter();
  }

  private _navigateFromInternalAnchor(ev: MouseEvent): void {
    if (!ev.ctrlKey && !ev.metaKey) return;
    const target = ev.target as HTMLElement | null;
    const anchor = target?.closest?.('a[data-anchor]') as HTMLElement | null;
    if (!anchor) return;
    const name = anchor.getAttribute('data-anchor');
    if (!name) return;
    ev.preventDefault();
    const escaped = typeof CSS !== 'undefined' && CSS.escape ? CSS.escape(name) : name.replace(/"/g, '\\"');
    for (const page of this.pageEditorRefs?.toArray() ?? []) {
      const bookmark = page.nativeElement.querySelector<HTMLElement>(
        `span.docx-bookmark[data-bm-name="${escaped}"]`
      );
      if (!bookmark) continue;
      const block =
        (bookmark.closest('p, h1, h2, h3, h4, h5, h6, li, td') as HTMLElement | null) ?? bookmark;
      block.scrollIntoView?.({ behavior: 'smooth', block: 'center' });
      if (!this.readOnly) {
        (bookmark.closest('.editor-content') as HTMLElement | null)?.focus?.({ preventScroll: true });
        this._placeCaretAfterElement(bookmark);
      }
      return;
    }
  }

  private _navigateToNoteFromRef(ev: MouseEvent): void {
    const target = ev.target as HTMLElement | null;
    const ref = target?.closest?.(
      'sup.endnote-ref[data-endnote-id], sup.footnote-ref[data-footnote-id]'
    ) as HTMLElement | null;
    if (!ref) return;
    const endnoteId = ref.getAttribute('data-endnote-id');
    const footnoteId = ref.getAttribute('data-footnote-id');
    const selector = endnoteId
      ? `.footnote-item[data-endnote-id="${endnoteId}"]`
      : `.footnote-item[data-footnote-id="${footnoteId}"]`;
    const item = (this._hostRef.nativeElement as HTMLElement).querySelector<HTMLElement>(selector);
    if (!item) return;
    item.scrollIntoView?.({ behavior: 'smooth', block: 'center' });
    this._flashNoteItem(item);
    item.querySelector<HTMLElement>('.footnote-item-content')?.focus?.({ preventScroll: true });
  }

  scrollToNoteReference(kind: 'footnote' | 'endnote', id: string): void {
    const sel = kind === 'endnote'
      ? `sup.endnote-ref[data-endnote-id="${id}"]`
      : `sup.footnote-ref[data-footnote-id="${id}"]`;
    for (const page of this.pageEditorRefs?.toArray() ?? []) {
      const el = page.nativeElement.querySelector<HTMLElement>(sel);
      if (!el) continue;
      el.scrollIntoView?.({ behavior: 'smooth', block: 'center' });
      if (!this.readOnly) {
        (el.closest('.editor-content') as HTMLElement | null)?.focus?.({ preventScroll: true });
        this._placeCaretAfterElement(el);
      }
      return;
    }
  }

  private _placeCaretAfterElement(el: HTMLElement): void {
    const selection = window.getSelection();
    if (!selection) return;
    const range = document.createRange();
    range.setStartAfter(el);
    range.collapse(true);
    selection.removeAllRanges();
    selection.addRange(range);
  }

  private _flashNoteItem(item: HTMLElement): void {
    item.classList.remove('note-item-flash');
    void item.offsetWidth;
    item.classList.add('note-item-flash');
    setTimeout(() => item.classList.remove('note-item-flash'), 1300);
  }

  private _paginateTimer: ReturnType<typeof setTimeout> | null = null;
  private _paginateRafHandle: number | null = null;
  private _isRepaginating = false;
  private _isDestroyed = false;
  private _resourceRepaginateGen = 0;

  private currentFontSize = 11;
  private currentFontFamily = 'Calibri';

  private _pendingInlineStyle: { fontFamily?: string; fontSize?: string } | null = null;
  private _pendingStyleAnchor: { node: Node; offset: number } | null = null;
  private _pendingDeleteCapture: { fontFamily?: string; fontSize?: string } | null = null;
  private _pendingStyleHoldUntil = 0;

  private _headerHtml = signal<string>('');
  private _footerHtml = signal<string>('');
  private _headerFirstPageHtml = signal<string>('');
  private _footerFirstPageHtml = signal<string>('');
  private _headerOddHtml = signal<string>('');
  private _headerEvenHtml = signal<string>('');
  private _footerOddHtml = signal<string>('');
  private _footerEvenHtml = signal<string>('');
  private _headerHeight = signal<number>(1.27);
  private _footerHeight = signal<number>(1.27);
  documentDefaultFontSize = signal<string | null>(null);
  documentDefaultFontFamily = signal<string | null>(null);
  documentDefaultLineHeight = signal<string | null>(null);
  documentDefaultParagraphSpacing = signal<string | null>(null);
  documentDefaultParagraphSpacingBefore = signal<string | null>(null);
  documentParagraphSpacingSum = signal<boolean>(false);
  documentDefaultLineTw = signal<string | null>(null);

  documentDefaultLineSingle(): string {
    return `${wordSingleFactor(this.documentDefaultFontFamily())}em`;
  }

  private _documentContainerAttrs: { name: string; value: string }[] | null = null;
  private _headerDifferentFirstPage = signal<boolean>(false);
  private _footerDifferentFirstPage = signal<boolean>(false);
  private _headerDifferentOddEven = signal<boolean>(false);
  private _footerDifferentOddEven = signal<boolean>(false);
  editingSection = signal<'header' | 'footer' | 'body'>('body');
  editingHfPageIndex = signal<number>(0);
  
  showHeaderOptionsMenu = signal<boolean>(false);
  showFooterOptionsMenu = signal<boolean>(false);
  
  headerHeight = computed(() => this._headerHeight());
  footerHeight = computed(() => this._footerHeight());
  differentFirstPage = computed(() => this._headerDifferentFirstPage() || this._footerDifferentFirstPage());
  differentOddEven = computed(() => this._headerDifferentOddEven() || this._footerDifferentOddEven());

  headerContents = computed(() => {
    const pagesArr = this.pageContents();
    return pagesArr.map((_, i) => this._computeHeaderContent(i));
  });
  footerContents = computed(() => {
    const pagesArr = this.pageContents();
    return pagesArr.map((_, i) => this._computeFooterContent(i));
  });

  editorState = signal<EditorState>({
    isModified: false,
    canUndo: false,
    canRedo: false,
    wordCount: 0,
    currentFormatting: {
      bold: false,
      italic: false,
      underline: false,
      strikethrough: false,
      subscript: false,
      superscript: false
    },
    currentStyle: {}
  });

  private lastEmittedPageCount = 1;

  ngAfterViewInit(): void {
    const syncActiveEditor = () => {
      const refs = this.pageEditorRefs?.toArray() ?? [];
      const idx = Math.min(this.activePageIndex(), Math.max(0, refs.length - 1));
      if (refs[idx]) {
        this.editorContent = refs[idx];
      }
    };
    syncActiveEditor();
    this.pageEditorRefs?.changes.subscribe(() => {
      syncActiveEditor();
      this.setupEventListeners();
      this.refreshListLabels();
    });

    this.initializeEditor();
    this.setupEventListeners();
    
    setTimeout(() => {
      this.calculatePages();
      this._schedulePaginate('init');
      this._repaginateAfterResources();
    }, 100);
    
    this.pageCheckInterval = setInterval(() => {
      this.calculatePages();
    }, 500);
  }

  ngOnDestroy(): void {
    this._isDestroyed = true;
    if (this.pageCheckInterval) {
      clearInterval(this.pageCheckInterval);
    }
    if (this._persistTimer) {
      clearTimeout(this._persistTimer);
      this._persistTimer = null;
    }
    if (this._paginateRafHandle !== null) {
      cancelAnimationFrame(this._paginateRafHandle);
      this._paginateRafHandle = null;
    }
    if (this._anchorBadgeRafHandle !== null) {
      cancelAnimationFrame(this._anchorBadgeRafHandle);
      this._anchorBadgeRafHandle = null;
    }
    this.hideAnchorBadge();
    this._sectionResizeObserver?.disconnect();
  }

  private observeActiveSectionGeometry(): void {
    this._sectionResizeObserver?.disconnect();
    const section = this.editingSection();
    if (section !== 'header' && section !== 'footer') return;

    const inner = section === 'header'
      ? this.headerContentEl?.nativeElement
      : this.footerContentEl?.nativeElement;
    const band = inner?.closest(section === 'header' ? '.page-header' : '.page-footer') as HTMLElement | null;
    if (!band) return;

    const emit = () => this.emitSectionGeometry(section, band);
    emit();
    this._sectionResizeObserver = new ResizeObserver(() => emit());
    this._sectionResizeObserver.observe(band);
  }

  private emitSectionGeometry(section: 'header' | 'footer', band: HTMLElement): void {
    const page = band.closest('.page') as HTMLElement | null;
    if (!page) return;
    const pr = page.getBoundingClientRect();
    const br = band.getBoundingClientRect();
    const pageIndex = Math.max(0, Number(page.getAttribute('data-page-number') ?? '1') - 1);
    const expectedWidthPx = this.pageWidthPx(pageIndex);
    const scale = pr.width > 0 ? pr.width / expectedWidthPx : 1;
    const topCm = ((br.top - pr.top) / scale) / CSS_PX_PER_CM;
    const bottomCm = ((br.bottom - pr.top) / scale) / CSS_PX_PER_CM;
    this.sectionGeometryChange.emit({ section, topCm, bottomCm });
  }

  private stopObservingSectionGeometry(): void {
    this._sectionResizeObserver?.disconnect();
    this._sectionResizeObserver = undefined;
  }

  private calculatePages(): void {
    const pageCount = Math.max(1, this.pageContents().length);
    if (pageCount !== this.lastEmittedPageCount) {
      this.lastEmittedPageCount = pageCount;
      this.pagesChange.emit(pageCount);
    }
  }

  private _getCleanEditorHtml(editor: HTMLElement): string {
    return editor.innerHTML;
  }

  private initializeEditor(): void {
    const editor = this.editorContent?.nativeElement;
    if (!editor) return;

    this.wrapExistingImages();
    this.lastSavedContent = this._getCleanEditorHtml(editor);
    this.saveToUndoStack();
    this.updateState();
  }

  private setupEventListeners(): void {
    const refs = this.pageEditorRefs?.toArray() ?? [];
    if (!(document as any).__wysiwygSelListener) {
      document.addEventListener('selectionchange', () => {
        this.onSelectionChange();
      });
      (document as any).__wysiwygSelListener = true;
    }

    for (const ref of refs) {
      this.attachEditorListeners(ref.nativeElement);
    }
  }

  private attachEditorListeners(editorEl: HTMLDivElement | null | undefined): void {
    const editor = editorEl as (HTMLDivElement & { __wysiwygBound?: boolean }) | null | undefined;
    if (!editor || editor.__wysiwygBound) return;
    editor.__wysiwygBound = true;

    editor.addEventListener('input', () => {
      this.onContentChange();
    });
    editor.addEventListener('paste', (e) => {
      this.handlePaste(e);
    });
    editor.addEventListener('copy', (e) => {
      this.handleCopyOrCut(e, false);
    });
    editor.addEventListener('cut', (e) => {
      this.handleCopyOrCut(e, true);
    });
    editor.addEventListener('keydown', (e) => {
      this.handleKeyboard(e);
    });
    editor.addEventListener('blur', () => {
      this.saveSelection();
      this._clearPendingInlineStyle();
    });
    editor.addEventListener('drop', (e) => {
      this.handleDrop(e);
    });
    editor.addEventListener('click', (e) => {
      this.handleEditorClick(e);
    });
    editor.addEventListener('mousedown', (e) => {
      this.handleEditorMouseDown(e);
    });
    editor.addEventListener('dragstart', (e) => {
      this.handleEditorDragStart(e);
    });
    editor.addEventListener('dragover', (e) => {
      this.handleEditorDragOver(e);
    });
    editor.addEventListener('dragend', () => {
      this.draggedImageWrapper = null;
    });
    editor.addEventListener('mousemove', (e) => {
      this.handleTableResizeCursor(e);
      this.updateTextBoxEdgeCursor(e);
    });
  }

  private handleEditorClick(event: MouseEvent): void {
    const target = event.target as HTMLElement;
    const imageWrapper = target.closest('.editor-image-wrapper') as HTMLElement | null;

    if (imageWrapper) {
      return;
    }

    this.clearSelectedImage();

    if (!target.closest('.docx-textbox')) {
      this.clearSelectedTextBox();
    }
  }

  private handleEditorMouseDown(event: MouseEvent): void {
    const target = event.target as HTMLElement;

    let tableHit = event.detail >= 2 ? null : this.detectTableResizeHit(event);
    if (tableHit) {
      const cell = (event.target as HTMLElement | null)?.closest?.('td, th') as HTMLTableCellElement | null;
      if (cell && !cell.textContent?.trim()) {
        tableHit = this.detectTableResizeHit(event, this.EMPTY_CELL_EDGE_THRESHOLD);
      }
    }
    if (tableHit) {
      event.preventDefault();
      event.stopPropagation();
      this.startTableResize(tableHit, event);
      return;
    }

    if (target.classList.contains('shape-resize-handle')) {
      const shapeOfHandle = target.closest('.docx-shape[data-docx-xml]') as HTMLElement | null;
      if (shapeOfHandle) {
        event.preventDefault();
        event.stopPropagation();
        this.startShapeResize(event, shapeOfHandle);
        return;
      }
    }
    const shape = target.closest('.docx-shape[data-docx-xml]') as HTMLElement | null;
    if (shape) {
      event.preventDefault();
      this.selectShape(shape);
      if (shape.style.position === 'absolute') this.startShapeDrag(event, shape);
      return;
    }
    this.clearSelectedShape();

    const wrapper = target.closest('.editor-image-wrapper') as HTMLElement | null;
    if (!wrapper) {
      this.handleTextBoxMouseDown(event, target);
      return;
    }

    if (target.classList.contains('image-resize-handle')) {
      event.preventDefault();
      event.stopPropagation();

      this.selectImageWrapper(wrapper);
      wrapper.setAttribute('draggable', 'false');

      const rect = wrapper.getBoundingClientRect();
      const img = wrapper.querySelector('img') as HTMLImageElement | null;
      let axis: 'x' | 'y' | 'both' = 'both';
      if (target.classList.contains('resize-handle-right')) {
        axis = 'x';
      } else if (target.classList.contains('resize-handle-bottom')) {
        axis = 'y';
      }

      this.imageResizeState = {
        wrapper,
        startX: event.clientX,
        startY: event.clientY,
        startWidth: rect.width,
        startHeight: rect.height,
        axis
      };

      const onMouseMove = (moveEvent: MouseEvent) => {
        if (!this.imageResizeState) return;

        const editor = wrapper.closest('.editor-content, .header-editor-content, .footer-editor-content') as HTMLElement | null;
        const editorMaxWidth = (editor?.clientWidth || 900) - 30;
        const st = this.imageResizeState;
        const currentImg = st.wrapper.querySelector('img') as HTMLImageElement | null;
        if (!currentImg) return;

        if (st.axis === 'x') {
          const deltaX = moveEvent.clientX - st.startX;
          const newWidth = Math.max(60, Math.min(editorMaxWidth, st.startWidth + deltaX));
          st.wrapper.style.width = `${newWidth}px`;
          st.wrapper.style.maxWidth = '100%';
          currentImg.style.width = '100%';
          currentImg.style.height = `${st.startHeight}px`;
        } else if (st.axis === 'y') {
          const deltaY = moveEvent.clientY - st.startY;
          const newHeight = Math.max(30, st.startHeight + deltaY);
          st.wrapper.style.width = `${st.startWidth}px`;
          st.wrapper.style.maxWidth = '100%';
          currentImg.style.width = '100%';
          currentImg.style.height = `${newHeight}px`;
        } else {
          const deltaX = moveEvent.clientX - st.startX;
          const newWidth = Math.max(60, Math.min(editorMaxWidth, st.startWidth + deltaX));
          st.wrapper.style.width = `${newWidth}px`;
          st.wrapper.style.maxWidth = '100%';
          currentImg.style.width = '100%';
          currentImg.style.height = 'auto';
        }
      };

      const onMouseUp = () => {
        if (this.imageResizeState?.wrapper) {
          this.imageResizeState.wrapper.setAttribute('draggable', 'true');

          const finalWrapper = this.imageResizeState.wrapper;
          const finalImg = finalWrapper.querySelector('img') as HTMLImageElement | null;
          if (finalImg) {
            const rect = finalImg.getBoundingClientRect();
            const widthPx = Math.round(rect.width);
            const heightPx = Math.round(rect.height);
            if (widthPx > 0 && heightPx > 0) {
              finalImg.style.width = `${widthPx}px`;
              finalImg.style.height = `${heightPx}px`;
              const EMU_PER_PX = 9525;
              finalImg.setAttribute('data-width-emu', String(widthPx * EMU_PER_PX));
              finalImg.setAttribute('data-height-emu', String(heightPx * EMU_PER_PX));
            }
          }
        }

        this.imageResizeState = null;
        document.removeEventListener('mousemove', onMouseMove);
        document.removeEventListener('mouseup', onMouseUp);
        this.onContentChange();
        this.emitImageSelectionState();
      };

      document.addEventListener('mousemove', onMouseMove);
      document.addEventListener('mouseup', onMouseUp);
      return;
    }

    event.preventDefault();
    this.selectImageWrapper(wrapper);

    const startX = event.clientX;
    const startY = event.clientY;
    const isFloating = wrapper.dataset['posMode'] === 'front' || wrapper.dataset['posMode'] === 'behind';
    this.imageMoveState = { wrapper, startX, startY, isDragging: false };

    if (isFloating) {
      const startLeft = parseInt(wrapper.style.left || '0', 10);
      const startTop = parseInt(wrapper.style.top || '0', 10);
      const floatPage = wrapper.closest('.page') as HTMLElement | null;
      const floatScale = floatPage ? this._pageVisualScale(floatPage) : 1;
      const onFloatingMove = (moveEvent: MouseEvent) => {
        const dx = viewportDeltaToLayout(moveEvent.clientX - startX, floatScale);
        const dy = viewportDeltaToLayout(moveEvent.clientY - startY, floatScale);
        if (!this.imageMoveState!.isDragging && Math.hypot(dx, dy) > 3) {
          this.imageMoveState!.isDragging = true;
          wrapper.classList.add('image-dragging');
        }
        if (this.imageMoveState!.isDragging) {
          const newLeft = Math.max(0, startLeft + dx);
          const newTop = Math.max(0, startTop + dy);
          wrapper.style.left = `${newLeft}px`;
          wrapper.style.top = `${newTop}px`;
        }
      };
      const onFloatingUp = () => {
        if (this.imageMoveState?.isDragging) {
          const xPx = parseInt(wrapper.style.left || '0', 10);
          const yPx = parseInt(wrapper.style.top || '0', 10);
          wrapper.dataset['xPx'] = String(xPx);
          wrapper.dataset['yPx'] = String(yPx);
          const img = wrapper.querySelector('img') as HTMLImageElement | null;
          if (img) {
            const EMU_PER_PX = 9525;
            const bandGeo = this._bandGeoForElement(wrapper);
            const c = bandGeo ? bandToContract(xPx, yPx, bandGeo) : { xPx, yPx };
            img.setAttribute('data-x-emu', String(Math.round(c.xPx) * EMU_PER_PX));
            img.setAttribute('data-y-emu', String(Math.round(c.yPx) * EMU_PER_PX));
          }
          wrapper.classList.remove('image-dragging');
          this.onContentChange();
          this.emitImageSelectionState();
        }
        this.imageMoveState = null;
        document.removeEventListener('mousemove', onFloatingMove);
        document.removeEventListener('mouseup', onFloatingUp);
      };
      document.addEventListener('mousemove', onFloatingMove);
      document.addEventListener('mouseup', onFloatingUp);
      return;
    }

    const onImageMouseMove = (moveEvent: MouseEvent) => {
      if (!this.imageMoveState) return;
      const dx = moveEvent.clientX - this.imageMoveState.startX;
      const dy = moveEvent.clientY - this.imageMoveState.startY;
      if (!this.imageMoveState.isDragging && Math.hypot(dx, dy) > 5) {
        this.imageMoveState.isDragging = true;
        document.body.classList.add('image-moving');
        wrapper.classList.add('image-dragging');
        wrapper.style.pointerEvents = 'none';
        this.imageDragCaret = document.createElement('div');
        this.imageDragCaret.className = 'image-drop-caret';
        document.body.appendChild(this.imageDragCaret);
      }

      if (this.imageMoveState.isDragging && this.imageDragCaret) {
        const editor = wrapper.closest('.editor-content, .header-editor-content, .footer-editor-content') as HTMLElement | null;
        const range = editor ? this.getRangeFromPoint(moveEvent.clientX, moveEvent.clientY) : null;
        if (range && editor && editor.contains(range.startContainer) && !wrapper.contains(range.startContainer)) {
          const rect = range.getBoundingClientRect();
          if (rect.height > 0) {
            this.imageDragCaret.style.display = 'block';
            this.imageDragCaret.style.left = `${rect.left}px`;
            this.imageDragCaret.style.top = `${rect.top}px`;
            this.imageDragCaret.style.height = `${rect.height}px`;
          } else {
            this.imageDragCaret.style.display = 'none';
          }
        } else {
          this.imageDragCaret.style.display = 'none';
        }
      }
    };

    const onImageMouseUp = (upEvent: MouseEvent) => {
      if (this.imageMoveState?.isDragging) {
        wrapper.style.pointerEvents = '';
        this.imageDragCaret?.remove();
        this.imageDragCaret = null;

        const editor = wrapper.closest('.editor-content, .header-editor-content, .footer-editor-content') as HTMLElement | null;
        if (editor) {
          wrapper.classList.remove('image-dragging');
          const dropRange = this.getRangeFromPoint(upEvent.clientX, upEvent.clientY);
          if (dropRange && editor.contains(dropRange.startContainer) && !wrapper.contains(dropRange.startContainer)) {
            wrapper.remove();
            dropRange.insertNode(wrapper);
            this.selectImageWrapper(wrapper);
            this.onContentChange();
            this.emitImageSelectionState();
          }
        }
      }
      document.body.classList.remove('image-moving');
      wrapper.classList.remove('image-dragging');
      wrapper.style.pointerEvents = '';
      this.imageDragCaret?.remove();
      this.imageDragCaret = null;
      this.imageMoveState = null;
      document.removeEventListener('mousemove', onImageMouseMove);
      document.removeEventListener('mouseup', onImageMouseUp);
    };

    document.addEventListener('mousemove', onImageMouseMove);
    document.addEventListener('mouseup', onImageMouseUp);
  }

  private readonly TABLE_EDGE_THRESHOLD = 6;
  private readonly EMPTY_CELL_EDGE_THRESHOLD = 2;

  private detectTableResizeHit(event: MouseEvent, threshold: number = this.TABLE_EDGE_THRESHOLD): {
    type: 'col' | 'row' | 'table';
    table: HTMLTableElement;
    colIndex: number;
    rowIndex: number;
  } | null {
    const target = event.target as HTMLElement;
    const td = target.closest('td, th') as HTMLTableCellElement | null;
    const table = target.closest('table') as HTMLTableElement | null;

    if (!table) return null;

    const t = threshold;

    const tableRect = table.getBoundingClientRect();
    if (
      Math.abs(event.clientX - tableRect.right) < t + 2 &&
      Math.abs(event.clientY - tableRect.bottom) < t + 2
    ) {
      return { type: 'table', table, colIndex: -1, rowIndex: -1 };
    }

    if (!td) return null;

    if (this._pointOverText(event.clientX, event.clientY)) return null;

    const cellRect = td.getBoundingClientRect();
    const rowIndex = (td.parentElement as HTMLTableRowElement).rowIndex;

    const edgeBoundary = (edge: ColumnEdge): number | null =>
      columnBoundaryForCell(buildTableGrid(table), td, edge);

    if (Math.abs(event.clientX - cellRect.right) < t) {
      const boundary = edgeBoundary('right');
      if (boundary !== null) return { type: 'col', table, colIndex: boundary, rowIndex };
    }

    if (Math.abs(event.clientX - cellRect.left) < t) {
      const boundary = edgeBoundary('left');
      if (boundary !== null) return { type: 'col', table, colIndex: boundary, rowIndex };
    }

    if (Math.abs(event.clientY - cellRect.bottom) < t) {
      return { type: 'row', table, colIndex: td.cellIndex, rowIndex };
    }

    return null;
  }

  private _pointOverText(x: number, y: number): boolean {
    const caret = document.caretRangeFromPoint?.(x, y);
    if (!caret || caret.startContainer.nodeType !== Node.TEXT_NODE) return false;
    const node = caret.startContainer as Text;
    const start = Math.max(0, caret.startOffset - 1);
    const end = Math.min(node.length, caret.startOffset + 1);
    if (start === end) return false;
    const r = document.createRange();
    r.setStart(node, start);
    r.setEnd(node, end);
    for (const rect of Array.from(r.getClientRects())) {
      if (x >= rect.left - 2 && x <= rect.right + 2 && y >= rect.top && y <= rect.bottom) return true;
    }
    return false;
  }

  private handleTableResizeCursor(event: MouseEvent): void {
    if (this.tableResizeState || this.imageResizeState || this.imageMoveState) return;

    const target = event.target as HTMLElement;
    const td = target.closest('td, th') as HTMLTableCellElement | null;
    const table = target.closest('table') as HTMLTableElement | null;

    if (!table || !td) {
      if (this._lastCursorCell) {
        this._lastCursorCell.style.cursor = '';
        this._lastCursorCell = null;
      }
      return;
    }

    const hit = this.detectTableResizeHit(event);

    if (this._lastCursorCell && this._lastCursorCell !== td) {
      this._lastCursorCell.style.cursor = '';
    }

    if (hit) {
      if (hit.type === 'col') {
        td.style.cursor = 'col-resize';
      } else if (hit.type === 'row') {
        td.style.cursor = 'row-resize';
      } else {
        td.style.cursor = 'nwse-resize';
      }
      this._lastCursorCell = td;
    } else {
      td.style.cursor = '';
      this._lastCursorCell = null;
    }
  }

  private _lastCursorCell: HTMLElement | null = null;

  private ensureTableColWidths(table: HTMLTableElement): void {
    const firstRow = table.rows[0];
    if (!firstRow) return;

    const hasWidths = Array.from(firstRow.cells).every(c => !!c.style.width);
    if (hasWidths) return;

    const cells = Array.from(firstRow.cells);
    const widths = cells.map(c => c.getBoundingClientRect().width);
    cells.forEach((c, i) => {
      c.style.width = `${widths[i]}px`;
    });

    table.style.tableLayout = 'fixed';
  }

  private startTableResize(
    hit: { type: 'col' | 'row' | 'table'; table: HTMLTableElement; colIndex: number; rowIndex: number },
    event: MouseEvent
  ): void {
    const table = hit.table;
    this.ensureTableColWidths(table);

    const grid = buildTableGrid(table);
    const startColumnWidths = readColumnWidths(table, grid);
    const startTableWidth = table.getBoundingClientRect().width;

    let startHeight = 0;
    if (hit.type === 'row' && table.rows[hit.rowIndex]) {
      startHeight = table.rows[hit.rowIndex].getBoundingClientRect().height;
    }

    this.tableResizeState = {
      type: hit.type,
      table,
      startX: event.clientX,
      startY: event.clientY,
      boundaryCol: hit.colIndex,
      rowIndex: hit.rowIndex,
      grid,
      startColumnWidths,
      columnWidths: [...startColumnWidths],
      startHeight,
      startTableWidth,
      moved: false
    };

    document.body.classList.add('table-resizing');
    const cursorType = hit.type === 'col' ? 'col-resize' : hit.type === 'row' ? 'row-resize' : 'nwse-resize';
    document.body.style.cursor = cursorType;

    const onMouseMove = (moveEvent: MouseEvent) => {
      moveEvent.preventDefault();
      if (!this.tableResizeState) return;
      const st = this.tableResizeState;
      if (moveEvent.clientX !== st.startX || moveEvent.clientY !== st.startY) st.moved = true;

      if (st.type === 'col') {
        this.resizeTableColumn(st, moveEvent);
      } else if (st.type === 'row') {
        this.resizeTableRow(st, moveEvent);
      } else {
        this.resizeWholeTable(st, moveEvent);
      }
    };

    const onMouseUp = () => {
      const finished = this.tableResizeState;
      this.tableResizeState = null;
      document.body.classList.remove('table-resizing');
      document.body.style.cursor = '';
      if (this._lastCursorCell) {
        this._lastCursorCell.style.cursor = '';
        this._lastCursorCell = null;
      }
      document.removeEventListener('mousemove', onMouseMove);
      document.removeEventListener('mouseup', onMouseUp);
      if (!finished?.moved) return;
      if (finished.type === 'col' || finished.type === 'table') {
        writeColgroupWidths(
          finished.table,
          finished.grid,
          finished.startColumnWidths,
          finished.columnWidths
        );
      }
      this.onContentChange();
    };

    document.addEventListener('mousemove', onMouseMove);
    document.addEventListener('mouseup', onMouseUp);
  }

  private resizeTableColumn(
    st: NonNullable<typeof this.tableResizeState>,
    moveEvent: MouseEvent
  ): void {
    const deltaX = moveEvent.clientX - st.startX;
    const b = st.boundaryCol;
    const columnCount = st.grid.columnCount;
    const minW = 40;

    const widths = [...st.startColumnWidths];

    if (b < columnCount - 1) {
      const available = st.startColumnWidths[b] + st.startColumnWidths[b + 1];
      const newLeft = Math.min(Math.max(minW, st.startColumnWidths[b] + deltaX), available - minW);
      widths[b] = newLeft;
      widths[b + 1] = available - newLeft;
    } else {
      widths[b] = Math.max(minW, st.startColumnWidths[b] + deltaX);
    }

    st.columnWidths = widths;
    applyColumnWidths(st.table, st.grid, widths);
  }

  private resizeTableRow(
    st: NonNullable<typeof this.tableResizeState>,
    moveEvent: MouseEvent
  ): void {
    const deltaY = moveEvent.clientY - st.startY;
    const newHeight = Math.max(20, Math.round(st.startHeight + deltaY));
    const row = st.table.rows[st.rowIndex];
    if (row) {
      row.style.height = `${newHeight}px`;
      row.removeAttribute('data-row-height-tw');
      row.removeAttribute('data-row-hrule');
    }
  }

  private resizeWholeTable(
    st: NonNullable<typeof this.tableResizeState>,
    moveEvent: MouseEvent
  ): void {
    const deltaX = moveEvent.clientX - st.startX;
    const editor = this.editorContent?.nativeElement;
    const maxW = (editor?.clientWidth || 900) - 20;
    const newTableWidth = Math.max(200, Math.min(maxW, st.startTableWidth + deltaX));
    const ratio = st.startTableWidth > 0 ? newTableWidth / st.startTableWidth : 1;

    const widths = st.startColumnWidths.map(w => (w > 0 ? w * ratio : 0));
    st.columnWidths = widths;

    if (widths.some(w => w > 0)) {
      applyColumnWidths(st.table, st.grid, widths);
    } else {
      st.table.style.width = `${newTableWidth}px`;
    }
  }

  private handleEditorDragStart(event: DragEvent): void {
    const rawTarget = event.target as Node | null;
    const target: HTMLElement | null = rawTarget instanceof HTMLElement
      ? rawTarget
      : (rawTarget?.parentElement ?? null);
    const wrapper = target?.closest('.editor-image-wrapper') as HTMLElement | null;
    if (!wrapper) {
      return;
    }

    this.draggedImageWrapper = wrapper;
    this.selectImageWrapper(wrapper);

    if (event.dataTransfer) {
      event.dataTransfer.effectAllowed = 'move';
      event.dataTransfer.setData('text/editor-image', '1');
    }
  }

  private handleEditorDragOver(event: DragEvent): void {
    if (!this.draggedImageWrapper) {
      return;
    }

    event.preventDefault();
    if (event.dataTransfer) {
      event.dataTransfer.dropEffect = 'move';
    }
  }

  private selectImageWrapper(wrapper: HTMLElement): void {
    if (this.selectedImageWrapper && this.selectedImageWrapper !== wrapper) {
      this.selectedImageWrapper.classList.remove('selected');
    }
    this.clearSelectedTextBox();

    this.selectedImageWrapper = wrapper;
    this.selectedImageWrapper.classList.add('selected');
    this.emitImageSelectionState();

    const mode = wrapper.dataset['posMode'];
    if (mode === 'front' || mode === 'behind') {
      this.showAnchorBadgeFor(wrapper);
    } else {
      this.hideAnchorBadge();
    }
  }

  private clearSelectedImage(): void {
    if (this.selectedImageWrapper) {
      if (this.anchorBadgeTarget === this.selectedImageWrapper) this.hideAnchorBadge();
      this.selectedImageWrapper.classList.remove('selected');
      this.selectedImageWrapper = null;
      this.imageSelectionChange.emit(null);
    }
  }

  private handleTextBoxMouseDown(event: MouseEvent, target: HTMLElement): void {
    const textbox = target.closest('.docx-textbox') as HTMLElement | null;
    if (!textbox) return;

    this.selectTextBox(textbox);
    if (!isFloatingElement(textbox)) return;

    const rect = textbox.getBoundingClientRect();
    if (!isPointerOnEdge(event.clientX, event.clientY, rect)) return;

    event.preventDefault();
    this.startTextBoxDrag(event, textbox);
  }

  private selectTextBox(textbox: HTMLElement): void {
    if (this.selectedTextBox && this.selectedTextBox !== textbox) {
      this.selectedTextBox.classList.remove('tb-selected');
    }
    this.clearSelectedImage();

    this.selectedTextBox = textbox;
    textbox.classList.add('tb-selected');

    if (isFloatingElement(textbox)) {
      this.showAnchorBadgeFor(textbox);
    } else {
      this.hideAnchorBadge();
    }
  }

  private clearSelectedTextBox(): void {
    if (this.selectedTextBox) {
      if (this.anchorBadgeTarget === this.selectedTextBox) this.hideAnchorBadge();
      this.selectedTextBox.classList.remove('tb-selected');
      this.selectedTextBox = null;
    }
  }

  private selectedShape: HTMLElement | null = null;

  private selectShape(shape: HTMLElement): void {
    if (this.selectedShape === shape) return;
    this.clearSelectedShape();
    this.clearSelectedTextBox();
    this.clearSelectedImage();
    this.selectedShape = shape;
    shape.classList.add('shape-selected');
    const handle = document.createElement('span');
    handle.className = 'shape-resize-handle';
    handle.setAttribute('contenteditable', 'false');
    shape.appendChild(handle);
  }

  private clearSelectedShape(): void {
    if (!this.selectedShape) return;
    this.selectedShape.classList.remove('shape-selected');
    this.selectedShape.querySelectorAll('.shape-resize-handle').forEach(h => h.remove());
    this.selectedShape = null;
  }

  private startShapeDrag(event: MouseEvent, shape: HTMLElement): void {
    const page = shape.closest('.page') as HTMLElement | null;
    const scale = page ? this._pageVisualScale(page) : 1;
    const startX = event.clientX;
    const startY = event.clientY;
    const startLeft = parseFloat(shape.style.left || '0');
    const startTop = parseFloat(shape.style.top || '0');
    let dragging = false;

    const onMove = (moveEvent: MouseEvent) => {
      const dx = viewportDeltaToLayout(moveEvent.clientX - startX, scale);
      const dy = viewportDeltaToLayout(moveEvent.clientY - startY, scale);
      if (!dragging && Math.hypot(dx, dy) > 3) dragging = true;
      if (dragging) {
        shape.style.left = `${Math.round(startLeft + dx)}px`;
        shape.style.top = `${Math.round(startTop + dy)}px`;
      }
    };
    const onUp = () => {
      document.removeEventListener('mousemove', onMove);
      document.removeEventListener('mouseup', onUp);
      if (!dragging) return;
      this._syncShapeXmlPosition(shape);
      this._notifyShapeContainerChanged(shape);
    };
    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup', onUp);
  }

  private startShapeResize(event: MouseEvent, shape: HTMLElement): void {
    const page = shape.closest('.page') as HTMLElement | null;
    const scale = page ? this._pageVisualScale(page) : 1;
    const startX = event.clientX;
    const startW = parseFloat(shape.style.width || '0') || shape.getBoundingClientRect().width / scale;
    const startH = parseFloat(shape.style.height || '0') || shape.getBoundingClientRect().height / scale;
    if (startW <= 0 || startH <= 0) return;
    const snapshot = shape.cloneNode(true) as HTMLElement;
    let factor = 1;

    const applyFactor = (f: number) => {
      const restored = snapshot.cloneNode(true) as HTMLElement;
      shape.replaceChildren(...Array.from(restored.childNodes));
      shape.setAttribute('style', restored.getAttribute('style') ?? '');
      rescaleShapePreview(shape, f, f);
    };
    const onMove = (moveEvent: MouseEvent) => {
      const dx = viewportDeltaToLayout(moveEvent.clientX - startX, scale);
      factor = Math.max(0.05, (startW + dx) / startW);
      applyFactor(factor);
    };
    const onUp = () => {
      document.removeEventListener('mousemove', onMove);
      document.removeEventListener('mouseup', onUp);
      if (factor === 1) return;
      this._syncShapeXmlScale(shape, factor, factor);
      this._notifyShapeContainerChanged(shape);
    };
    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup', onUp);
  }

  private _shapeContext(shape: HTMLElement): { band: 'header' | 'footer' | null; pageIndex: number } {
    if (shape.closest('.header-editor-content')) return { band: 'header', pageIndex: this.editingHfPageIndex() };
    if (shape.closest('.footer-editor-content')) return { band: 'footer', pageIndex: this.editingHfPageIndex() };
    const page = shape.closest('.page') as HTMLElement | null;
    const n = Number(page?.getAttribute('data-page-number') ?? '1');
    return { band: null, pageIndex: Number.isFinite(n) && n >= 1 ? n - 1 : this.activePageIndex() };
  }

  private _syncShapeXmlPosition(shape: HTMLElement): void {
    const { band, pageIndex } = this._shapeContext(shape);
    let contractLeft = parseFloat(shape.style.left || '0');
    let contractTop = parseFloat(shape.style.top || '0');
    if (band) {
      const c = bandToContract(contractLeft, contractTop, this._bandGeoFor(pageIndex, band));
      contractLeft = c.xPx;
      contractTop = c.yPx;
      shape.setAttribute('data-band-orig-left', `${Math.round(contractLeft)}px`);
      shape.setAttribute('data-band-orig-top', `${Math.round(contractTop)}px`);
    }
    const b64 = shape.getAttribute('data-docx-xml');
    const doc = b64 ? decodeShapeXml(b64) : null;
    if (!doc) return;
    const xPageEmu = Math.round(contractLeft * EMU_PER_PX);
    const yPageEmu = Math.round((contractTop + this.pageMarginPx(pageIndex, 'top')) * EMU_PER_PX);
    if (setAnchorPositionPageEmu(doc, xPageEmu, yPageEmu)) {
      shape.setAttribute('data-docx-xml', encodeShapeXml(doc));
    }
  }

  private _syncShapeXmlScale(shape: HTMLElement, fx: number, fy: number): void {
    const b64 = shape.getAttribute('data-docx-xml');
    const doc = b64 ? decodeShapeXml(b64) : null;
    if (!doc) return;
    if (scaleShapeExtent(doc, fx, fy)) {
      shape.setAttribute('data-docx-xml', encodeShapeXml(doc));
    }
  }

  private _notifyShapeContainerChanged(shape: HTMLElement): void {
    const { band } = this._shapeContext(shape);
    if (band === 'header') {
      const el = this.headerContentEl?.nativeElement;
      if (el) this._applyEditedHfHtml(el.innerHTML, 'header');
      this.invalidateHeaderFooterCache();
      this.emitHeaderFooterChanges();
      return;
    }
    if (band === 'footer') {
      const el = this.footerContentEl?.nativeElement;
      if (el) this._applyEditedHfHtml(el.innerHTML, 'footer');
      this.invalidateHeaderFooterCache();
      this.emitHeaderFooterChanges();
      return;
    }
    this.onContentChange();
  }

  private startTextBoxDrag(event: MouseEvent, textbox: HTMLElement): void {
    const page = textbox.closest('.page') as HTMLElement | null;
    const scale = page ? this._pageVisualScale(page) : 1;
    const startX = event.clientX;
    const startY = event.clientY;
    const startLeft = parseInt(textbox.style.left || '0', 10);
    const startTop = parseInt(textbox.style.top || '0', 10);
    let dragging = false;

    const onMove = (moveEvent: MouseEvent) => {
      const dx = viewportDeltaToLayout(moveEvent.clientX - startX, scale);
      const dy = viewportDeltaToLayout(moveEvent.clientY - startY, scale);
      if (!dragging && Math.hypot(dx, dy) > 3) {
        dragging = true;
        textbox.classList.add('tb-dragging');
      }
      if (dragging) {
        textbox.style.left = `${Math.max(0, Math.round(startLeft + dx))}px`;
        textbox.style.top = `${Math.max(0, Math.round(startTop + dy))}px`;
      }
    };
    const onUp = () => {
      document.removeEventListener('mousemove', onMove);
      document.removeEventListener('mouseup', onUp);
      if (!dragging) return;
      textbox.classList.remove('tb-dragging');

      const xPx = parseInt(textbox.style.left || '0', 10);
      const yPx = parseInt(textbox.style.top || '0', 10);
      textbox.setAttribute('data-x-emu', String(xPx * EMU_PER_PX));
      textbox.setAttribute('data-y-emu', String(yPx * EMU_PER_PX));
      if (!textbox.dataset['posMode']) textbox.dataset['posMode'] = 'front';

      this.onContentChange();
      this._scheduleAnchorBadgeRefresh();
    };
    document.addEventListener('mousemove', onMove);
    document.addEventListener('mouseup', onUp);
  }

  private updateTextBoxEdgeCursor(event: MouseEvent): void {
    const target = event.target as HTMLElement | null;
    const textbox = (target?.closest?.('.docx-textbox') ?? null) as HTMLElement | null;

    if (this.edgeCursorTextBox && this.edgeCursorTextBox !== textbox) {
      this.edgeCursorTextBox.classList.remove('tb-edge');
      this.edgeCursorTextBox = null;
    }
    if (!textbox || !isFloatingElement(textbox)) return;

    const onEdge = isPointerOnEdge(event.clientX, event.clientY, textbox.getBoundingClientRect());
    textbox.classList.toggle('tb-edge', onEdge);
    this.edgeCursorTextBox = textbox;
  }

  private _pageVisualScale(page: HTMLElement): number {
    const rect = page.getBoundingClientRect();
    return page.offsetWidth > 0 && rect.width > 0 ? rect.width / page.offsetWidth : 1;
  }

  private showAnchorBadgeFor(el: HTMLElement): void {
    const editorRoot = el.closest(
      '.editor-content, .header-editor-content, .footer-editor-content, .header-display, .footer-display'
    ) as HTMLElement | null;
    if (!editorRoot) { this.hideAnchorBadge(); return; }

    const paragraph = findAnchorParagraph(el, editorRoot);
    const page = (paragraph ?? el).closest('.page') as HTMLElement | null;
    if (!paragraph || !page) { this.hideAnchorBadge(); return; }

    if (!this.anchorBadge) {
      const badge = document.createElement('div');
      badge.className = 'anchor-badge';
      badge.setAttribute('role', 'img');
      badge.setAttribute('aria-label', 'Kotwica: element jest przypięty do tego akapitu');
      badge.setAttribute('contenteditable', 'false');
      badge.innerHTML = ANCHOR_BADGE_SVG;
      this.anchorBadge = badge;
    }
    if (this.anchorBadge.parentElement !== page) page.appendChild(this.anchorBadge);
    this.anchorBadgeTarget = el;

    const pos = computeAnchorBadgePosition(
      paragraph.getBoundingClientRect(), page.getBoundingClientRect(), page.offsetWidth);
    this.anchorBadge.style.left = `${pos.leftPx}px`;
    this.anchorBadge.style.top = `${pos.topPx}px`;
  }

  private hideAnchorBadge(): void {
    this.anchorBadge?.remove();
    this.anchorBadgeTarget = null;
  }

  private _scheduleAnchorBadgeRefresh(): void {
    if (this._anchorBadgeRafHandle !== null || !this.anchorBadgeTarget) return;
    this._anchorBadgeRafHandle = requestAnimationFrame(() => {
      this._anchorBadgeRafHandle = null;
      this.refreshAnchorBadge();
    });
  }

  private refreshAnchorBadge(): void {
    const target = this.anchorBadgeTarget;
    if (!target) return;
    if (!target.isConnected) {
      this.hideAnchorBadge();
      return;
    }
    this.showAnchorBadgeFor(target);
  }

  private emitImageSelectionState(): void {
    const wrapper = this.selectedImageWrapper;
    if (!wrapper) {
      this.imageSelectionChange.emit(null);
      return;
    }
    const img = wrapper.querySelector('img') as HTMLImageElement | null;
    if (!img) {
      this.imageSelectionChange.emit(null);
      return;
    }
    const rect = img.getBoundingClientRect();
    const widthPx = Math.max(1, Math.round(rect.width));
    const heightPx = Math.max(1, Math.round(rect.height));
    const aspectRatio = heightPx > 0 ? widthPx / heightPx : 1;
    const para = wrapper.closest('p, div, h1, h2, h3, h4, h5, h6') as HTMLElement | null;
    const align = para?.style.textAlign as 'left' | 'center' | 'right' | '' | undefined;
    const rawMode = (wrapper.dataset['posMode'] ?? 'inline');
    const allowed = new Set(['inline', 'square', 'topBottom', 'front', 'behind']);
    const positionMode = (allowed.has(rawMode) ? rawMode : 'inline') as
      'inline' | 'square' | 'topBottom' | 'front' | 'behind';

    const borderWidthPx = parseInt(img.dataset['borderWidth'] ?? '0', 10);
    const borderColor = img.dataset['borderColor'] || '#000000';
    const borderStyleAttr = img.dataset['borderStyle'] as 'solid' | 'dashed' | 'dotted' | undefined;
    const border = {
      enabled: borderWidthPx > 0,
      color: borderColor,
      widthPx: borderWidthPx > 0 ? borderWidthPx : 1,
      style: borderStyleAttr ?? 'solid',
    };

    const crop = {
      left: parseFloat(img.dataset['cropL'] ?? '0') || 0,
      right: parseFloat(img.dataset['cropR'] ?? '0') || 0,
      top: parseFloat(img.dataset['cropT'] ?? '0') || 0,
      bottom: parseFloat(img.dataset['cropB'] ?? '0') || 0,
    };

    this.imageSelectionChange.emit({
      widthPx,
      heightPx,
      aspectRatio,
      alignment: align === 'left' || align === 'center' || align === 'right' ? align : null,
      positionMode,
      border,
      crop,
    });
  }

  setSelectedImagePositionMode(mode: 'inline' | 'square' | 'topBottom' | 'front' | 'behind'): void {
    const wrapper = this.selectedImageWrapper;
    if (!wrapper) return;
    const img = wrapper.querySelector('img') as HTMLImageElement | null;
    if (!img) return;

    delete wrapper.dataset['xPx'];
    delete wrapper.dataset['yPx'];
    img.removeAttribute('data-x-emu');
    img.removeAttribute('data-y-emu');
    wrapper.style.removeProperty('position');
    wrapper.style.removeProperty('left');
    wrapper.style.removeProperty('top');
    wrapper.style.removeProperty('z-index');
    wrapper.style.removeProperty('pointer-events');
    wrapper.style.removeProperty('float');
    wrapper.style.removeProperty('clear');
    wrapper.style.removeProperty('display');
    wrapper.style.removeProperty('margin');

    if (mode === 'inline') {
      delete wrapper.dataset['posMode'];
      delete img.dataset['posMode'];
    } else if (mode === 'square') {
      wrapper.dataset['posMode'] = 'square';
      img.dataset['posMode'] = 'square';
      wrapper.style.float = 'left';
      wrapper.style.margin = '0 12px 8px 0';
    } else if (mode === 'topBottom') {
      wrapper.dataset['posMode'] = 'topBottom';
      img.dataset['posMode'] = 'topBottom';
      wrapper.style.display = 'block';
      wrapper.style.clear = 'both';
      wrapper.style.margin = '8px auto';
    } else {
      const page = wrapper.closest('.page, .editor-content, .header-editor-content, .footer-editor-content') as HTMLElement | null;
      const pageRect = page?.getBoundingClientRect();
      const rect = wrapper.getBoundingClientRect();
      const xPx = Math.max(0, Math.round(rect.left - (pageRect?.left ?? 0)));
      const yPx = Math.max(0, Math.round(rect.top - (pageRect?.top ?? 0)));
      const bandGeo = this._bandGeoForElement(img);
      const contract = bandGeo ? bandToContract(xPx, yPx, bandGeo) : undefined;
      this.applyFloatingPosition(wrapper, img, mode as 'front' | 'behind', xPx, yPx,
        contract ? { xPx: Math.round(contract.xPx), yPx: Math.round(contract.yPx) } : undefined);
    }
    this.onContentChange();
    this.emitImageSelectionState();
  }

  setSelectedImageBorder(border: {
    enabled: boolean; color: string; widthPx: number; style: 'solid' | 'dashed' | 'dotted';
  }): void {
    const wrapper = this.selectedImageWrapper;
    if (!wrapper) return;
    const img = wrapper.querySelector('img') as HTMLImageElement | null;
    if (!img) return;
    if (!border.enabled || border.widthPx <= 0) {
      img.style.removeProperty('border');
      img.style.removeProperty('border-width');
      img.style.removeProperty('border-style');
      img.style.removeProperty('border-color');
      delete img.dataset['borderWidth'];
      delete img.dataset['borderColor'];
      delete img.dataset['borderStyle'];
    } else {
      const w = Math.max(1, Math.min(20, Math.round(border.widthPx)));
      const color = /^#[0-9a-fA-F]{6}$/.test(border.color) ? border.color : '#000000';
      img.style.border = `${w}px ${border.style} ${color}`;
      img.dataset['borderWidth'] = String(w);
      img.dataset['borderColor'] = color;
      img.dataset['borderStyle'] = border.style;
    }
    this.onContentChange();
    this.emitImageSelectionState();
  }

  setSelectedImageCrop(crop: { left: number; right: number; top: number; bottom: number }): void {
    const wrapper = this.selectedImageWrapper;
    if (!wrapper) return;
    const img = wrapper.querySelector('img') as HTMLImageElement | null;
    if (!img) return;
    const clamp = (v: number) => Math.max(0, Math.min(95, Math.round(v)));
    const l = clamp(crop.left), r = clamp(crop.right), t = clamp(crop.top), b = clamp(crop.bottom);
    if (l === 0 && r === 0 && t === 0 && b === 0) {
      img.style.removeProperty('clip-path');
      delete img.dataset['cropL'];
      delete img.dataset['cropR'];
      delete img.dataset['cropT'];
      delete img.dataset['cropB'];
    } else {
      img.style.clipPath = `inset(${t}% ${r}% ${b}% ${l}%)`;
      img.dataset['cropL'] = String(l);
      img.dataset['cropR'] = String(r);
      img.dataset['cropT'] = String(t);
      img.dataset['cropB'] = String(b);
    }
    this.onContentChange();
    this.emitImageSelectionState();
  }

  resetSelectedImageCrop(): void {
    this.setSelectedImageCrop({ left: 0, right: 0, top: 0, bottom: 0 });
  }

  private applyFloatingPosition(
    wrapper: HTMLElement, img: HTMLImageElement,
    mode: 'front' | 'behind', xPx: number, yPx: number,
    contract?: { xPx: number; yPx: number },
  ): void {
    wrapper.dataset['posMode'] = mode;
    img.dataset['posMode'] = mode;
    wrapper.dataset['xPx'] = String(xPx);
    wrapper.dataset['yPx'] = String(yPx);
    const EMU_PER_PX = 9525;
    const c = contract ?? { xPx, yPx };
    img.setAttribute('data-x-emu', String(c.xPx * EMU_PER_PX));
    img.setAttribute('data-y-emu', String(c.yPx * EMU_PER_PX));
    wrapper.style.position = 'absolute';
    wrapper.style.left = `${xPx}px`;
    wrapper.style.top = `${yPx}px`;
    if (mode === 'behind') {
      wrapper.style.zIndex = '-1';
      wrapper.style.pointerEvents = 'auto';
    } else {
      wrapper.style.zIndex = '10';
      wrapper.style.pointerEvents = 'auto';
    }
  }

  setSelectedImageWidth(widthPx: number, lockAspect = true): void {
    const wrapper = this.selectedImageWrapper;
    if (!wrapper) return;
    const img = wrapper.querySelector('img') as HTMLImageElement | null;
    if (!img) return;
    const safeWidth = Math.max(16, Math.round(widthPx));
    const aspect = img.naturalWidth > 0 && img.naturalHeight > 0
      ? img.naturalWidth / img.naturalHeight
      : (img.clientWidth > 0 && img.clientHeight > 0 ? img.clientWidth / img.clientHeight : 1);
    const newHeight = lockAspect ? Math.max(16, Math.round(safeWidth / aspect)) : img.clientHeight;
    this.applyImageSize(img, wrapper, safeWidth, newHeight);
  }

  setSelectedImageHeight(heightPx: number, lockAspect = true): void {
    const wrapper = this.selectedImageWrapper;
    if (!wrapper) return;
    const img = wrapper.querySelector('img') as HTMLImageElement | null;
    if (!img) return;
    const safeHeight = Math.max(16, Math.round(heightPx));
    const aspect = img.naturalWidth > 0 && img.naturalHeight > 0
      ? img.naturalWidth / img.naturalHeight
      : (img.clientWidth > 0 && img.clientHeight > 0 ? img.clientWidth / img.clientHeight : 1);
    const newWidth = lockAspect ? Math.max(16, Math.round(safeHeight * aspect)) : img.clientWidth;
    this.applyImageSize(img, wrapper, newWidth, safeHeight);
  }

  resetSelectedImageAspect(): void {
    const wrapper = this.selectedImageWrapper;
    if (!wrapper) return;
    const img = wrapper.querySelector('img') as HTMLImageElement | null;
    if (!img || img.naturalWidth <= 0 || img.naturalHeight <= 0) return;
    const aspect = img.naturalWidth / img.naturalHeight;
    const widthPx = Math.max(16, Math.round(img.clientWidth));
    const heightPx = Math.max(16, Math.round(widthPx / aspect));
    this.applyImageSize(img, wrapper, widthPx, heightPx);
  }

  setSelectedImageAlignment(value: 'left' | 'center' | 'right' | null): void {
    const wrapper = this.selectedImageWrapper;
    if (!wrapper) return;
    const para = wrapper.closest('p, div, h1, h2, h3, h4, h5, h6') as HTMLElement | null;
    if (!para) return;
    if (value === null) {
      para.style.removeProperty('text-align');
    } else {
      para.style.textAlign = value;
    }
    this.onContentChange();
    this.emitImageSelectionState();
  }

  removeSelectedImage(): void {
    const wrapper = this.selectedImageWrapper;
    if (!wrapper) return;
    wrapper.remove();
    this.selectedImageWrapper = null;
    this.imageSelectionChange.emit(null);
    this.onContentChange();
  }

  private applyImageSize(
    img: HTMLImageElement, wrapper: HTMLElement, widthPx: number, heightPx: number,
  ): void {
    img.style.width = `${widthPx}px`;
    img.style.height = `${heightPx}px`;
    const EMU_PER_PX = 9525;
    img.setAttribute('data-width-emu', String(widthPx * EMU_PER_PX));
    img.setAttribute('data-height-emu', String(heightPx * EMU_PER_PX));
    wrapper.style.width = `${widthPx}px`;
    this.onContentChange();
    this.emitImageSelectionState();
  }

  private getRangeFromPoint(x: number, y: number): Range | null {
    const docWithCaret = document as Document & {
      caretRangeFromPoint?: (x: number, y: number) => Range | null;
      caretPositionFromPoint?: (x: number, y: number) => { offsetNode: Node; offset: number } | null;
    };

    if (docWithCaret.caretRangeFromPoint) {
      return docWithCaret.caretRangeFromPoint(x, y);
    }

    if (docWithCaret.caretPositionFromPoint) {
      const pos = docWithCaret.caretPositionFromPoint(x, y);
      if (pos) {
        const range = document.createRange();
        range.setStart(pos.offsetNode, pos.offset);
        range.collapse(true);
        return range;
      }
    }

    return null;
  }

  triggerContentChange(): void {
    this.onContentChange();
  }

  private onContentChange(): void {
    const section = this.editingSection();

    if (section === 'header' && this.headerContentEl?.nativeElement) {
      const html = this.headerContentEl.nativeElement.innerHTML;
      this._applyEditedHeaderHtml(html);
      this.emitHeaderFooterChanges();
      this.updateFormattingState();
      return;
    }
    if (section === 'footer' && this.footerContentEl?.nativeElement) {
      const html = this.footerContentEl.nativeElement.innerHTML;
      this._applyEditedFooterHtml(html);
      this.emitHeaderFooterChanges();
      this.updateFormattingState();
      return;
    }

    const editor = this.editorContent?.nativeElement;
    if (!editor) return;

    const html = editor.innerHTML;
    this._isInternalUpdate = true;
    this._content.set(html);
    this.contentChange.emit(html);
    this._isInternalUpdate = false;
    this.saveToUndoStack();
    this.updateState();
    this.updateFormattingState();
    this._scheduleAnchorBadgeRefresh();
  }

  private onSelectionChange(): void {
    const selection = window.getSelection();

    if (selection && this.isSelectionInEditor(selection)) {
      if (
        this._pendingInlineStyle &&
        Date.now() >= this._pendingStyleHoldUntil &&
        !this._caretMatchesPendingAnchor(selection)
      ) {
        this._clearPendingInlineStyle();
      }
      this.saveSelection();
      this.updateFormattingState();
      this.selectionChange.emit(selection);
    }
  }

  private isSelectionInEditor(selection: Selection): boolean {
    if (!selection.anchorNode) return false;
    const refs = this.pageEditorRefs?.toArray() ?? [];
    for (const ref of refs) {
      if (ref.nativeElement.contains(selection.anchorNode)) return true;
    }
    const body = this.editorContent?.nativeElement;
    if (body && body.contains(selection.anchorNode)) return true;
    const header = this.headerContentEl?.nativeElement;
    if (header && header.contains(selection.anchorNode)) return true;
    const footer = this.footerContentEl?.nativeElement;
    if (footer && footer.contains(selection.anchorNode)) return true;
    return false;
  }

  findEditorContentContaining(node: Node | null | undefined): HTMLElement | null {
    if (!node) return null;
    for (const ref of this.pageEditorRefs?.toArray() ?? []) {
      if (ref.nativeElement.contains(node)) return ref.nativeElement;
    }
    return null;
  }

  private plainTextPasteUntil = 0;

  private consumePlainTextPasteRequest(): boolean {
    const requested = Date.now() < this.plainTextPasteUntil;
    this.plainTextPasteUntil = 0;
    return requested;
  }

  private handlePaste(e: ClipboardEvent): void {
    e.preventDefault();

    const clipboardData = e.clipboardData;
    if (!clipboardData) return;

    const html = clipboardData.getData('text/html');
    const plain = clipboardData.getData('text/plain');

    if (this.consumePlainTextPasteRequest()) {
      this.insertText(resolvePlainText(plain, html));
      return;
    }

    if (html) {
      const fragment = this.prepareClipboardFragment(html);
      if (fragment) {
        this.insertClipboardFragment(fragment);
        return;
      }
    }
    this.insertText(normalizeWhitespace(plain));
  }

  private handleCopyOrCut(e: ClipboardEvent, cut: boolean): void {
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0 || sel.isCollapsed) return;
    if (!this.isSelectionInEditor(sel) || !e.clipboardData) return;

    const range = sel.getRangeAt(0);
    e.preventDefault();
    e.clipboardData.setData('text/html', this.buildClipboardHtml(range));
    e.clipboardData.setData('text/plain', sel.toString());

    if (cut && !this.readOnly) {
      range.deleteContents();
      sel.removeAllRanges();
      sel.addRange(range);
      this.savedSelection = range.cloneRange();
      this.onContentChange();
    }
  }

  private buildClipboardHtml(range: Range): string {
    const holder = document.createElement('div');
    holder.appendChild(range.cloneContents());

    this.rewrapClipboardOrphans(holder, range);
    this.stripClipboardArtifacts(holder);

    const wrapper = document.createElement('div');
    wrapper.setAttribute('data-d2-clip', '1');
    while (holder.firstChild) wrapper.appendChild(holder.firstChild);
    return wrapper.outerHTML;
  }

  private rewrapClipboardOrphans(holder: HTMLElement, range: Range): void {
    const anchorNode = range.commonAncestorContainer;
    const anchor = anchorNode instanceof Element ? anchorNode : anchorNode.parentElement;

    const groupConsecutive = (tagNames: Set<string>, makeShell: () => HTMLElement) => {
      const kids = Array.from(holder.children);
      let run: HTMLElement[] = [];
      const flush = () => {
        if (run.length === 0) return;
        const shell = makeShell();
        holder.insertBefore(shell, run[0]);
        run.forEach(el => shell.appendChild(el));
        run = [];
      };
      for (const kid of kids) {
        if (tagNames.has(kid.tagName)) run.push(kid as HTMLElement);
        else flush();
      }
      flush();
    };

    groupConsecutive(new Set(['TD', 'TH']), () => document.createElement('tr'));
    groupConsecutive(new Set(['TR', 'TBODY', 'THEAD', 'TFOOT']), () => {
      const table = anchor?.closest('table');
      if (!table) return document.createElement('table');
      const shell = table.cloneNode(false) as HTMLElement;
      const colgroup = table.querySelector(':scope > colgroup');
      if (colgroup) shell.appendChild(colgroup.cloneNode(true));
      return shell;
    });
    groupConsecutive(new Set(['LI']), () => {
      const list = anchor?.closest('ul,ol')
        ?? this._closestList(range.startContainer)
        ?? this._closestList(range.endContainer);
      return list ? (list.cloneNode(false) as HTMLElement) : document.createElement('ul');
    });
  }

  private _closestList(node: Node): HTMLElement | null {
    const el = node instanceof Element ? node : node.parentElement;
    return el?.closest('ul,ol') ?? null;
  }

  private stripClipboardArtifacts(root: ParentNode): void {
    root.querySelectorAll('tr[data-repeated-header]').forEach(el => el.remove());
    root.querySelectorAll('[data-split-table-id],[data-split-row-id],[data-split-cont]').forEach(el => {
      el.removeAttribute('data-split-table-id');
      el.removeAttribute('data-split-row-id');
      el.removeAttribute('data-split-cont');
    });
    root.querySelectorAll('.docx-bookmark').forEach(el => el.remove());
  }

  private prepareClipboardFragment(html: string): DocumentFragment | null {
    const tpl = document.createElement('template');
    tpl.innerHTML = html;

    tpl.content.querySelectorAll('script,style,link,meta,title,iframe,object,embed,base,form,input,button,textarea,select')
      .forEach(n => n.remove());

    const internal = tpl.content.querySelector('[data-d2-clip]');
    if (!internal) {
      this.sanitizeExternalClipboardHtml(tpl.content);
    }
    this.stripClipboardArtifacts(tpl.content);

    const source: ParentNode = internal ?? tpl.content;
    const out = document.createDocumentFragment();
    while (source.firstChild) out.appendChild(source.firstChild);
    return out.childNodes.length > 0 ? out : null;
  }

  private sanitizeExternalClipboardHtml(root: DocumentFragment): void {
    const ALLOWED_TAGS = new Set([
      'P', 'DIV', 'SPAN', 'B', 'STRONG', 'I', 'EM', 'U', 'S', 'STRIKE', 'SUB', 'SUP', 'A',
      'UL', 'OL', 'LI', 'TABLE', 'THEAD', 'TBODY', 'TFOOT', 'TR', 'TD', 'TH', 'COL', 'COLGROUP',
      'BR', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'IMG', 'BLOCKQUOTE',
    ]);
    const KEEP_STYLES = new Set([
      'font-weight', 'font-style', 'text-decoration', 'text-decoration-line', 'color',
      'background-color', 'font-size', 'font-family', 'text-align', 'vertical-align',
    ]);
    const TABLE_TAGS = new Set(['TABLE', 'TD', 'TH', 'COL', 'COLGROUP', 'TR']);
    const KEEP_TABLE_STYLES = new Set([
      'width', 'border', 'border-top', 'border-right', 'border-bottom', 'border-left',
      'border-collapse', 'padding',
    ]);

    for (const el of Array.from(root.querySelectorAll('*'))) {
      if (!root.contains(el)) continue;

      if (!ALLOWED_TAGS.has(el.tagName)) {
        el.replaceWith(...Array.from(el.childNodes));
        continue;
      }

      if (el.tagName === 'IMG') {
        const src = el.getAttribute('src') ?? '';
        if (!src.startsWith('data:image/')) { el.remove(); continue; }
      }
      if (el.tagName === 'A') {
        const href = el.getAttribute('href') ?? '';
        if (!/^(https?:|mailto:|#)/i.test(href)) el.removeAttribute('href');
      }

      const keepAttrs = new Set(['src', 'alt', 'href', 'colspan', 'rowspan', 'span']);
      for (const attr of Array.from(el.attributes)) {
        if (attr.name === 'style' || keepAttrs.has(attr.name)) continue;
        el.removeAttribute(attr.name);
      }

      const style = el.getAttribute('style');
      if (style) {
        const kept: string[] = [];
        for (const decl of style.split(';')) {
          const idx = decl.indexOf(':');
          if (idx < 0) continue;
          const prop = decl.slice(0, idx).trim().toLowerCase();
          const value = decl.slice(idx + 1).trim();
          if (!value) continue;
          if (KEEP_STYLES.has(prop) || (TABLE_TAGS.has(el.tagName) && KEEP_TABLE_STYLES.has(prop))) {
            kept.push(`${prop}:${value}`);
          }
        }
        if (kept.length > 0) el.setAttribute('style', kept.join(';') + ';');
        else el.removeAttribute('style');
      }
    }

    root.querySelectorAll('span').forEach(span => {
      if (span.attributes.length === 0) span.replaceWith(...Array.from(span.childNodes));
    });

    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const value = node.nodeValue ?? '';
      if (!value.includes('\u00A0')) continue;
      const cleaned = value.replace(/(?<=\S)\u00A0|\u00A0(?=\S)/g, ' ');
      if (cleaned !== value) node.nodeValue = cleaned;
    }
  }

  private insertClipboardFragment(fragment: DocumentFragment): void {
    const editor = this.getActiveEditor();
    if (!editor) return;

    let range: Range | null = null;
    const live = window.getSelection();
    if (live && live.rangeCount > 0 && this.isSelectionInEditor(live)) {
      range = live.getRangeAt(0);
    } else if (this.savedSelection && editor.contains(this.savedSelection.startContainer)) {
      range = this.savedSelection.cloneRange();
    }
    if (!range) return;
    range.deleteContents();

    const BLOCK_TAGS = new Set(['P', 'DIV', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'UL', 'OL', 'TABLE', 'BLOCKQUOTE']);
    const isBlock = (n: Node): n is HTMLElement =>
      n.nodeType === Node.ELEMENT_NODE && BLOCK_TAGS.has((n as Element).tagName);
    const nodes = Array.from(fragment.childNodes);

    let last: Node | null = null;
    if (!nodes.some(isBlock)) {
      last = nodes[nodes.length - 1] ?? null;
      range.insertNode(fragment);
    } else {
      last = this.insertBlockNodesAtCaret(range, nodes, isBlock, editor);
    }

    if (last) {
      const caret = document.createRange();
      caret.setStartAfter(last);
      caret.collapse(true);
      const sel = window.getSelection();
      sel?.removeAllRanges();
      sel?.addRange(caret);
      this.savedSelection = caret.cloneRange();
    }
    this.onContentChange();
  }

  private insertBlockNodesAtCaret(
    range: Range,
    nodes: Node[],
    isBlock: (n: Node) => boolean,
    editor: HTMLElement,
  ): Node | null {
    const block = this._nearestBlockElement(range.startContainer, editor);
    let last: Node | null = null;

    if (block?.tagName === 'LI' && block.parentElement
        && (block.parentElement.tagName === 'UL' || block.parentElement.tagName === 'OL')
        && nodes.filter(isBlock).every(n => (n as Element).tagName === 'UL' || (n as Element).tagName === 'OL')) {
      return this.mergeListNodesIntoHostList(range, nodes, block, isBlock);
    }

    const SPLITTABLE = new Set(['P', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'BLOCKQUOTE', 'PRE']);
    if (!block || block === editor || !block.parentNode || !SPLITTABLE.has(block.tagName)) {
      for (const n of nodes) {
        range.insertNode(n);
        range.setStartAfter(n);
        range.collapse(true);
        last = n;
      }
      return last;
    }

    const parent = block.parentNode;
    const after = block.cloneNode(false) as HTMLElement;
    const tail = document.createRange();
    tail.setStart(range.startContainer, range.startOffset);
    tail.setEnd(block, block.childNodes.length);
    after.appendChild(tail.extractContents());
    parent.insertBefore(after, block.nextSibling);

    let leading = true;
    let afterCursor: Node | null = after.firstChild;
    for (const n of nodes) {
      if (leading && !isBlock(n)) {
        block.appendChild(n);
        last = n;
        continue;
      }
      leading = false;
      if (isBlock(n)) {
        parent.insertBefore(n, after);
        last = n;
      } else {
        after.insertBefore(n, afterCursor);
        last = n;
      }
    }

    const isEmpty = (el: HTMLElement) => (el.textContent ?? '').length === 0 && el.children.length === 0;
    if (isEmpty(after)) after.remove();
    if (isEmpty(block)) block.remove();

    return last;
  }

  private mergeListNodesIntoHostList(
    range: Range,
    nodes: Node[],
    li: HTMLElement,
    isBlock: (n: Node) => boolean,
  ): Node | null {
    const list = li.parentElement!;
    const after = li.cloneNode(false) as HTMLElement;
    const tail = document.createRange();
    tail.setStart(range.startContainer, range.startOffset);
    tail.setEnd(li, li.childNodes.length);
    after.appendChild(tail.extractContents());
    list.insertBefore(after, li.nextSibling);

    let leading = true;
    let last: Node | null = null;
    const afterCursor: Node | null = after.firstChild;
    for (const n of nodes) {
      if (leading && !isBlock(n)) {
        li.appendChild(n);
        last = n;
        continue;
      }
      leading = false;
      if (isBlock(n)) {
        for (const item of Array.from(n.childNodes)) {
          if (!(item instanceof Element)) continue;
          list.insertBefore(item, after);
          last = item;
        }
      } else {
        after.insertBefore(n, afterCursor);
        last = n;
      }
    }

    const isEmptyItem = (el: HTMLElement) => {
      const nonMarkerKids = Array.from(el.children).filter(c => !c.classList.contains('list-marker'));
      const text = Array.from(el.childNodes)
        .filter(n => !(n instanceof Element && n.classList.contains('list-marker')))
        .map(n => n.textContent ?? '')
        .join('');
      return text.length === 0 && nonMarkerKids.length === 0;
    };
    if (isEmptyItem(after)) after.remove();
    if (isEmptyItem(li)) li.remove();

    return last;
  }

  private handleKeyboard(e: KeyboardEvent): void {
    if ((e.key === 'Delete' || e.key === 'Backspace') && this.selectedImageWrapper) {
      e.preventDefault();
      this.selectedImageWrapper.remove();
      this.selectedImageWrapper = null;
      this.onContentChange();
      return;
    }

    if (e.key === 'Escape' && this.selectedImageWrapper) {
      e.preventDefault();
      this.clearSelectedImage();
      return;
    }

    if (e.key === 'Backspace' && !e.shiftKey && !e.ctrlKey && !e.metaKey && !e.altKey
        && (this._tryDeletePageBreakBackwards() || this._tryMergeAcrossPageBackwards())) {
      e.preventDefault();
      return;
    }

    if ((e.key === 'ArrowDown' || e.key === 'ArrowUp')
        && !e.shiftKey && !e.ctrlKey && !e.metaKey && !e.altKey
        && this._tryMoveCaretAcrossPages(e.key === 'ArrowDown' ? 'down' : 'up')) {
      e.preventDefault();
      return;
    }

    if (e.ctrlKey || e.metaKey) {
      if (e.shiftKey && e.key.toLowerCase() === 'v') {
        this.plainTextPasteUntil = Date.now() + 1000;
        return;
      }

      if (e.shiftKey && e.code === 'Digit8') {
        e.preventDefault();
        this.toggleFormattingMarks();
        return;
      }

      switch (e.key.toLowerCase()) {
        case 'b':
          e.preventDefault();
          this.executeCommand('bold');
          break;
        case 'i':
          e.preventDefault();
          this.executeCommand('italic');
          break;
        case 'u':
          e.preventDefault();
          this.executeCommand('underline');
          break;
        case 'z':
          e.preventDefault();
          if (e.shiftKey) {
            this.redo();
          } else {
            this.undo();
          }
          break;
        case 'y':
          e.preventDefault();
          this.redo();
          break;
        case 's':
          e.preventDefault();
          break;
      }
    }

    if (e.key === 'Tab') {
      e.preventDefault();
      if (this._handleTableTab(e.shiftKey)) return;
      if (e.shiftKey) {
        this.executeCommand('outdent');
        return;
      }
      const sel = window.getSelection();
      const collapsed = !!sel && sel.rangeCount > 0 && sel.isCollapsed;
      if (collapsed && !this._isCaretAtBlockStart()) {
        this._insertTabAtCaret();
      } else {
        this.executeCommand('indent');
      }
    }

    if (e.key === 'Enter' && !e.ctrlKey && !e.metaKey && !e.altKey) {
      const carry = this.readOnly ? null : this._captureInlineTextStyleAtCaret();
      this._flushPaginateSoon();
      if (carry) {
        requestAnimationFrame(() =>
          requestAnimationFrame(() => this._continueInlineStyleAfterEnter(carry))
        );
      }
    }
  }

  private _handleTableTab(backwards: boolean): boolean {
    const editor = this.getActiveEditor();
    const sel = window.getSelection();
    if (!editor || !sel || sel.rangeCount === 0) return false;
    const start = sel.getRangeAt(0).startContainer;
    const node = start.nodeType === Node.TEXT_NODE ? start.parentElement : (start as HTMLElement);
    const cell = (node?.closest?.('td, th') ?? null) as HTMLTableCellElement | null;
    if (!cell || !editor.contains(cell)) return false;
    const row = cell.parentElement as HTMLTableRowElement | null;
    const table = cell.closest('table');
    if (!row || !table) return false;

    const cells = Array.from(row.cells);
    const idx = cells.indexOf(cell);

    if (!backwards) {
      if (idx < cells.length - 1) { this._focusTableCell(cells[idx + 1]); return true; }
      const nextRow = table.rows[row.rowIndex + 1];
      if (nextRow?.cells.length) { this._focusTableCell(nextRow.cells[0]); return true; }
      const nextFragment = this._siblingTableFragment(table, +1);
      if (nextFragment?.rows[0]?.cells.length) {
        this._focusTableCell(nextFragment.rows[0].cells[0]);
        return true;
      }
      const newRow = this._appendTableRowLike(table);
      this._focusTableCell(newRow.cells[0]);
      this.onContentChange();
      return true;
    }

    if (idx > 0) { this._focusTableCell(cells[idx - 1]); return true; }
    const prevRow = table.rows[row.rowIndex - 1];
    if (prevRow?.cells.length) { this._focusTableCell(prevRow.cells[prevRow.cells.length - 1]); return true; }
    const prevFragment = this._siblingTableFragment(table, -1);
    const lastRow = prevFragment?.rows[prevFragment.rows.length - 1];
    if (lastRow?.cells.length) { this._focusTableCell(lastRow.cells[lastRow.cells.length - 1]); return true; }
    return true;
  }

  private _isCaretAtBlockStart(): boolean {
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0 || !sel.isCollapsed) return false;
    const range = sel.getRangeAt(0);
    const isZeroWidthOnly = (text: string | null) =>
      (text ?? '').replace(/[​‌‍﻿]/g, '') === '';
    const editor = this.getActiveEditor();
    const isBlock = (el: HTMLElement) =>
      /^(P|H[1-6]|LI)$/.test(el.tagName) || el === editor || el.getAttribute('contenteditable') === 'true';

    let node: Node = range.startContainer;

    if (node.nodeType === Node.TEXT_NODE) {
      if (!isZeroWidthOnly((node.textContent ?? '').slice(0, range.startOffset))) return false;
    } else if (node.nodeType === Node.ELEMENT_NODE) {
      const el = node as HTMLElement;
      for (let i = 0; i < range.startOffset; i++) {
        const child = el.childNodes[i];
        if (child.nodeName === 'BR' || child.nodeName === 'IMG' || !isZeroWidthOnly(child.textContent)) return false;
      }
      if (isBlock(el)) return true;
    }

    while (node.parentNode) {
      for (let sib = node.previousSibling; sib; sib = sib.previousSibling) {
        if (sib.nodeName === 'BR' || sib.nodeName === 'IMG' || !isZeroWidthOnly(sib.textContent)) return false;
      }
      const parent = node.parentNode;
      if (parent.nodeType === Node.ELEMENT_NODE && isBlock(parent as HTMLElement)) return true;
      node = parent;
    }
    return true;
  }

  private _insertTabAtCaret(): void {
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0) return;
    const range = sel.getRangeAt(0);
    const span = document.createElement('span');
    span.setAttribute('style', 'display:inline-block;min-width:2em');
    span.setAttribute('contenteditable', 'false');
    span.textContent = '\t';
    range.insertNode(span);

    const caret = document.createRange();
    caret.setStartAfter(span);
    caret.collapse(true);
    sel.removeAllRanges();
    sel.addRange(caret);
    this.savedSelection = caret.cloneRange();
    this.onContentChange();
  }

  private _siblingTableFragment(table: HTMLTableElement, direction: 1 | -1): HTMLTableElement | null {
    const id = table.getAttribute('data-split-table-id');
    if (!id) return null;
    const fragments = Array.from(document.querySelectorAll<HTMLTableElement>(
      `table[data-split-table-id="${CSS.escape(id)}"]`));
    const i = fragments.indexOf(table);
    if (i < 0) return null;
    return fragments[i + direction] ?? null;
  }

  private _appendTableRowLike(table: HTMLTableElement): HTMLTableRowElement {
    const last = table.rows[table.rows.length - 1];
    const newRow = last.cloneNode(false) as HTMLTableRowElement;
    for (const ref of Array.from(last.cells)) {
      const td = ref.cloneNode(false) as HTMLTableCellElement;
      td.removeAttribute('rowspan');
      td.removeAttribute('data-split-row-id');
      td.removeAttribute('data-split-row-cont');
      td.innerHTML = '<br>';
      newRow.appendChild(td);
    }
    last.parentElement!.appendChild(newRow);
    return newRow;
  }

  private _focusTableCell(cell: HTMLTableCellElement): void {
    (cell.closest('[contenteditable="true"]') as HTMLElement | null)?.focus();
    const range = document.createRange();
    if ((cell.textContent ?? '').trim().length === 0) {
      range.setStart(cell, 0);
      range.collapse(true);
    } else {
      range.selectNodeContents(cell);
    }
    const sel = window.getSelection();
    sel?.removeAllRanges();
    sel?.addRange(range);
    this.savedSelection = range.cloneRange();
  }

  private _captureInlineTextStyleAtCaret(): { fontFamily?: string; fontSize?: string } | null {
    const editor = this.getActiveEditor();
    const sel = window.getSelection();
    if (!editor || !sel || sel.rangeCount === 0) return null;
    const range = sel.getRangeAt(0);
    if (!editor.contains(range.startContainer)) return null;

    let node: Node | null = range.startContainer;
    if (node.nodeType === Node.ELEMENT_NODE) {
      const el = node as HTMLElement;
      node = range.startOffset > 0 ? el.childNodes[range.startOffset - 1] ?? el : el;
    }
    if (node && node.nodeType === Node.ELEMENT_NODE) {
      let deepest = node as HTMLElement;
      while (deepest.lastElementChild) deepest = deepest.lastElementChild as HTMLElement;
      node = deepest;
    }

    let fontFamily: string | undefined;
    let fontSize: string | undefined;
    let cur: HTMLElement | null =
      node && node.nodeType === Node.TEXT_NODE ? node.parentElement : (node as HTMLElement | null);
    while (cur && cur !== editor && editor.contains(cur)) {
      if (!fontFamily && cur.style.fontFamily) fontFamily = cur.style.fontFamily;
      if (!fontSize && cur.style.fontSize) fontSize = cur.style.fontSize;
      if (fontFamily && fontSize) break;
      cur = cur.parentElement;
    }
    return fontFamily || fontSize ? { fontFamily, fontSize } : null;
  }

  private _continueInlineStyleAfterEnter(carry: { fontFamily?: string; fontSize?: string }): void {
    const editor = this.getActiveEditor();
    const sel = window.getSelection();
    if (!editor || !sel || sel.rangeCount === 0) return;
    const range = sel.getRangeAt(0);
    if (!range.collapsed || !editor.contains(range.startContainer)) return;

    let cur: HTMLElement | null =
      range.startContainer.nodeType === Node.TEXT_NODE
        ? range.startContainer.parentElement
        : (range.startContainer as HTMLElement);
    while (cur && cur !== editor) {
      if (cur.style.fontFamily || cur.style.fontSize) {
        const sameFamily = !carry.fontFamily || cur.style.fontFamily === carry.fontFamily;
        const sameSize = !carry.fontSize || cur.style.fontSize === carry.fontSize;
        if (sameFamily && sameSize) return;
        break;
      }
      cur = cur.parentElement;
    }

    const block = this._nearestBlockElement(range.startContainer, editor);
    if (block && (block.textContent ?? '').split(String.fromCharCode(0x200b)).join('').trim().length > 0) return;

    const span = document.createElement('span');
    if (carry.fontFamily) span.style.fontFamily = carry.fontFamily;
    if (carry.fontSize) span.style.fontSize = carry.fontSize;
    span.appendChild(document.createTextNode(String.fromCharCode(0x200b)));
    range.insertNode(span);

    const caretRange = document.createRange();
    caretRange.setStart(span.firstChild!, 1);
    caretRange.setEnd(span.firstChild!, 1);
    sel.removeAllRanges();
    sel.addRange(caretRange);
    this.savedSelection = caretRange.cloneRange();
  }

  private _nearestBlockElement(node: Node | null, editor: HTMLElement): HTMLElement | null {
    const blockTags = new Set(['P', 'DIV', 'LI', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'TD', 'TH', 'BLOCKQUOTE', 'PRE']);
    let el: HTMLElement | null =
      node && node.nodeType === Node.TEXT_NODE ? node.parentElement : (node as HTMLElement | null);
    while (el && el !== editor) {
      if (blockTags.has(el.tagName)) return el;
      el = el.parentElement;
    }
    return null;
  }

  private handleDrop(e: DragEvent): void {
    e.preventDefault();

    if (this.draggedImageWrapper) {
      const editor = this.editorContent?.nativeElement;
      if (!editor) return;

      const dropRange = this.getRangeFromPoint(e.clientX, e.clientY);
      if (dropRange && editor.contains(dropRange.startContainer) && !this.draggedImageWrapper.contains(dropRange.startContainer)) {
        this.draggedImageWrapper.remove();
        dropRange.insertNode(this.draggedImageWrapper);
        this.selectImageWrapper(this.draggedImageWrapper);
        this.onContentChange();
      }

      this.draggedImageWrapper = null;
      return;
    }

    const files = e.dataTransfer?.files;
    if (!files || files.length === 0) return;

    Array.from(files).forEach(file => {
      if (file.type.startsWith('image/')) {
        this.insertImageFromFile(file);
      }
    });
  }

  private insertImageFromFile(file: File): void {
    const reader = new FileReader();
    reader.onload = (e) => {
      const base64 = e.target?.result as string;
      if (base64) {
        this.insertImage(base64);
      }
    };
    reader.readAsDataURL(file);
  }

  executeCommand(command: EditorCommand, value?: string): void {
    if (command === 'toggleFormattingMarks') {
      this.toggleFormattingMarks();
      return;
    }

    const editor = this.getActiveEditor();
    if (!editor) return;

    editor.focus();

    switch (command) {
      case 'bold':
        document.execCommand('bold', false);
        break;
      case 'italic':
        document.execCommand('italic', false);
        break;
      case 'underline':
        document.execCommand('underline', false);
        break;
      case 'strikethrough':
        document.execCommand('strikeThrough', false);
        break;
      case 'subscript':
        document.execCommand('subscript', false);
        break;
      case 'superscript':
        document.execCommand('superscript', false);
        break;
      case 'alignLeft':
      case 'justifyLeft':
        document.execCommand('justifyLeft', false);
        break;
      case 'alignCenter':
      case 'justifyCenter':
        document.execCommand('justifyCenter', false);
        break;
      case 'alignRight':
      case 'justifyRight':
        document.execCommand('justifyRight', false);
        break;
      case 'alignJustify':
      case 'justifyFull':
        document.execCommand('justifyFull', false);
        break;
      case 'indent':
        this._changeParagraphIndent(1);
        break;
      case 'outdent':
        this._changeParagraphIndent(-1);
        break;
      case 'bulletList':
      case 'insertUnorderedList':
        document.execCommand('insertUnorderedList', false);
        break;
      case 'toggleCheckboxBullet':
        this.toggleCheckboxBullet();
        break;
      case 'numberedList':
      case 'insertOrderedList':
        document.execCommand('insertOrderedList', false);
        break;
      case 'removeFormat':
        document.execCommand('removeFormat', false);
        break;
      case 'selectAll':
        this.selectAllContent();
        break;
      case 'undo':
        this.undo();
        return;
      case 'redo':
        this.redo();
        return;
      case 'heading1':
      case 'heading2':
      case 'heading3':
      case 'heading4':
      case 'heading5':
      case 'heading6':
        const level = command.replace('heading', '');
        document.execCommand('formatBlock', false, `h${level}`);
        break;
      case 'paragraph':
        document.execCommand('formatBlock', false, 'p');
        break;
      case 'insertLink':
        if (value) {
          document.execCommand('createLink', false, value);
        }
        break;
      case 'insertImage':
        if (value) {
          this.insertImage(value);
        }
        break;
      case 'insertTable':
        if (value) {
          this.insertTable(value);
        }
        break;
    }

    this.onContentChange();
    this.updateFormattingState();
  }

  private static readonly INDENT_STEP_PX = 48;

  private _changeParagraphIndent(direction: 1 | -1): void {
    const editor = this.getActiveEditor();
    if (!editor) return;

    const live = window.getSelection();
    const range: Range | null = live && live.rangeCount > 0 && this.isSelectionInEditor(live)
      ? live.getRangeAt(0)
      : this.savedSelection;
    if (!range) return;

    const startEl = range.startContainer instanceof Element
      ? range.startContainer
      : range.startContainer.parentElement;
    if (startEl?.closest('li')) {
      document.execCommand(direction > 0 ? 'indent' : 'outdent', false);
      return;
    }

    const step = WysiwygEditorComponent.INDENT_STEP_PX;
    for (const block of this._indentableBlocksInRange(editor, range)) {
      const inline = parseFloat(block.style.marginLeft);
      const current = Number.isFinite(inline)
        ? inline
        : parseFloat(getComputedStyle(block).marginLeft) || 0;
      const parent = block.parentElement;
      let maxMargin = Number.POSITIVE_INFINITY;
      if (parent && parent.clientWidth > 0) {
        const cs = getComputedStyle(parent);
        const columnWidth = parent.clientWidth
          - (parseFloat(cs.paddingLeft) || 0) - (parseFloat(cs.paddingRight) || 0);
        maxMargin = Math.max(0, columnWidth - step);
      }
      const next = direction > 0
        ? Math.min(current + step, maxMargin)
        : Math.max(current - step, 0);
      if (next === current) continue;
      if (next > 0) {
        block.style.marginLeft = `${Math.round(next)}px`;
      } else {
        block.style.removeProperty('margin-left');
      }
    }
  }

  private _indentableBlocksInRange(editor: HTMLElement, range: Range): HTMLElement[] {
    const SELECTOR = 'p, h1, h2, h3, h4, h5, h6, blockquote, pre';
    if (range.collapsed) {
      const el = range.startContainer instanceof Element
        ? range.startContainer
        : range.startContainer.parentElement;
      const block = el?.closest<HTMLElement>(SELECTOR) ?? null;
      return block && editor.contains(block) ? [block] : [];
    }
    const hits = Array.from(editor.querySelectorAll<HTMLElement>(SELECTOR))
      .filter(b => range.intersectsNode(b));
    return hits.filter(b => !hits.some(other => other !== b && b.contains(other)));
  }

  setFontSize(size: number): void {
    const editor = this.getActiveEditor();
    if (!editor) return;

    if (!Number.isFinite(size) || size < 1 || size > 400) return;

    this.currentFontSize = size;

    const live = window.getSelection();
    const liveInEditor = !!live && live.rangeCount > 0 && this.isSelectionInEditor(live);
    if (!liveInEditor && this.savedSelection) {
      this.restoreSelection();
    }

    editor.focus();

    let selection = window.getSelection();
    if ((!selection || selection.rangeCount === 0) && this.savedSelection) {
      this.restoreSelection();
      selection = window.getSelection();
    }

    if (!selection || selection.rangeCount === 0) {
      return;
    }

    const range = selection.getRangeAt(0);

    if (range.collapsed) {
      const containerEl = range.startContainer.nodeType === Node.TEXT_NODE
        ? range.startContainer.parentElement
        : range.startContainer as HTMLElement;
      const isInZwsSpan = containerEl instanceof HTMLSpanElement
        && containerEl.textContent === '\u200B'
        && containerEl.style.fontSize !== '';

      if (isInZwsSpan && containerEl) {
        containerEl.style.fontSize = `${size}pt`;
        const newRange = document.createRange();
        newRange.setStart(containerEl.firstChild!, Math.min(1, containerEl.firstChild!.textContent!.length));
        newRange.setEnd(containerEl.firstChild!, Math.min(1, containerEl.firstChild!.textContent!.length));
        selection.removeAllRanges();
        selection.addRange(newRange);
        this.savedSelection = newRange.cloneRange();
        this._setPendingInlineStyle({ fontSize: `${size}pt` }, newRange);
        this.updateFormattingState();
        return;
      }

      const span = document.createElement('span');
      span.style.fontSize = `${size}pt`;
      span.innerHTML = '\u200B';

      range.insertNode(span);

      this.removeStaleZwsSpans(span);

      const newRange = document.createRange();
      newRange.setStart(span.firstChild!, 1);
      newRange.setEnd(span.firstChild!, 1);
      selection.removeAllRanges();
      selection.addRange(newRange);

      this.savedSelection = newRange.cloneRange();
      this._setPendingInlineStyle({ fontSize: `${size}pt` }, newRange);
      this.updateFormattingState();
      return;
    }

    this.applyFontSizeToSelection(size, selection, range);
    this.onContentChange();

    const after = window.getSelection();
    if (after && after.rangeCount > 0 && this.isSelectionInEditor(after)) {
      this.savedSelection = after.getRangeAt(0).cloneRange();
    }
    this.updateFormattingState();
  }

  private applyFontSizeToSelection(size: number, selection: Selection, range: Range): void {
    this._applyInlineStyleToRange(range, span => {
      span.style.fontSize = `${size}pt`;
    });
  }

  private _applyInlineStyleToRange(range: Range, apply: (span: HTMLElement) => void): void {
    if (range.collapsed) return;

    let startContainer: Node = range.startContainer;
    let startOffset = range.startOffset;
    let endContainer: Node = range.endContainer;
    const endOffset = range.endOffset;

    if (endContainer.nodeType === Node.TEXT_NODE && endOffset < (endContainer as Text).length) {
      (endContainer as Text).splitText(endOffset);
    }
    if (startContainer.nodeType === Node.TEXT_NODE && startOffset > 0) {
      const right = (startContainer as Text).splitText(startOffset);
      if (endContainer === startContainer) endContainer = right;
      startContainer = right;
      startOffset = 0;
    }

    const norm = document.createRange();
    if (startContainer.nodeType === Node.TEXT_NODE) norm.setStartBefore(startContainer);
    else norm.setStart(startContainer, startOffset);
    if (endContainer.nodeType === Node.TEXT_NODE) norm.setEndAfter(endContainer);
    else norm.setEnd(endContainer, endOffset);

    const root = norm.commonAncestorContainer;
    const rootEl = root.nodeType === Node.ELEMENT_NODE ? root as Element : root.parentElement;
    if (!rootEl) return;

    const targets: Text[] = [];
    const walker = document.createTreeWalker(rootEl, NodeFilter.SHOW_TEXT);
    for (let n = walker.nextNode(); n; n = walker.nextNode()) {
      if (!norm.intersectsNode(n)) continue;
      if (!(n.textContent ?? '').length) continue;
      const el = n.parentElement;
      if (!el) continue;
      if (el.closest('[contenteditable="false"]')) continue;
      if (el.closest('.page-header, .page-footer, .footnotes-region, .endnotes-region')
        && !el.closest('.header-editor-content, .footer-editor-content, .footnote-item-content')) continue;
      targets.push(n as Text);
    }

    for (const n of targets) {
      const parent = n.parentElement;
      if (!parent) continue;
      if (parent.tagName === 'SPAN' && parent.childNodes.length === 1) {
        apply(parent);
        continue;
      }
      const span = document.createElement('span');
      apply(span);
      parent.insertBefore(span, n);
      span.appendChild(n);
    }

    rootEl.normalize();
    const sel = window.getSelection();
    if (sel) {
      sel.removeAllRanges();
      sel.addRange(norm);
    }
  }

  setFontFamily(fontFamily: string): void {
    const editor = this.getActiveEditor();
    if (!editor) return;

    this.currentFontFamily = fontFamily;

    const live = window.getSelection();
    const liveInEditor = !!live && live.rangeCount > 0 && this.isSelectionInEditor(live);
    if (!liveInEditor && this.savedSelection) {
      this.restoreSelection();
    }

    editor.focus();

    let selection = window.getSelection();
    if ((!selection || selection.rangeCount === 0) && this.savedSelection) {
      this.restoreSelection();
      selection = window.getSelection();
    }

    if (!selection || selection.rangeCount === 0) {
      return;
    }

    const range = selection.getRangeAt(0);

    if (range.collapsed) {
      const zwsChar = String.fromCharCode(0x200b);
      const zwsContainer = range.startContainer.nodeType === Node.TEXT_NODE
        ? range.startContainer.parentElement
        : (range.startContainer as HTMLElement);
      if (
        zwsContainer instanceof HTMLSpanElement &&
        zwsContainer.firstChild &&
        zwsContainer.style.fontFamily !== '' &&
        zwsContainer.textContent === zwsChar
      ) {
        zwsContainer.style.fontFamily = fontFamily;
        const updRange = document.createRange();
        updRange.setStart(zwsContainer.firstChild, 1);
        updRange.setEnd(zwsContainer.firstChild, 1);
        selection.removeAllRanges();
        selection.addRange(updRange);
        this.savedSelection = updRange.cloneRange();
        this._setPendingInlineStyle({ fontFamily }, updRange);
        this.updateFormattingState();
        return;
      }
      
      const span = document.createElement('span');
      span.style.fontFamily = fontFamily;
      span.innerHTML = '\u200B';
      
      range.insertNode(span);

      const newRange = document.createRange();
      newRange.setStart(span.firstChild!, 1);
      newRange.setEnd(span.firstChild!, 1);
      selection.removeAllRanges();
      selection.addRange(newRange);
      this.savedSelection = newRange.cloneRange();
      this._setPendingInlineStyle({ fontFamily }, newRange);
      this.updateFormattingState();

      return;
    }

    this.applyFontFamilyToSelection(fontFamily, selection, range);
    this.onContentChange();

    const after = window.getSelection();
    if (after && after.rangeCount > 0 && this.isSelectionInEditor(after)) {
      this.savedSelection = after.getRangeAt(0).cloneRange();
    }
    this.updateFormattingState();
  }

  private removeStaleZwsSpans(keepSpan: HTMLElement): void {
    let block: HTMLElement | null = keepSpan.parentElement;
    while (block && !['P', 'LI', 'DIV', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'TD', 'TH'].includes(block.tagName)) {
      block = block.parentElement;
    }
    if (!block) return;

    const stale: HTMLElement[] = [];
    block.querySelectorAll<HTMLElement>('span[style*="font-size"]').forEach(el => {
      if (el === keepSpan) return;
      if (el.textContent === '\u200B' && (el.childNodes.length === 0 || (el.childNodes.length === 1 && el.firstChild!.nodeType === Node.TEXT_NODE))) {
        stale.push(el);
      }
    });
    stale.forEach(el => el.remove());
  }

  private applyFontFamilyToSelection(fontFamily: string, selection: Selection, range: Range): void {
    this._applyInlineStyleToRange(range, span => {
      span.style.fontFamily = fontFamily;
    });
  }

  setTextColor(color: string): void {
    const editor = this.getActiveEditor();
    if (!editor) return;

    editor.focus();
    
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) return;
    
    const range = selection.getRangeAt(0);
    if (range.collapsed) return;
    
    this.applyColorToSelection(color, selection, range);
    this.onContentChange();
  }

  private applyColorToSelection(color: string, selection: Selection, range: Range): void {
    this._applyInlineStyleToRange(range, span => {
      span.style.color = color;
    });
  }

  setBackgroundColor(color: string): void {
    const editor = this.getActiveEditor();
    if (!editor) return;

    editor.focus();
    document.execCommand('hiliteColor', false, color);
    this.onContentChange();
  }

  private savedSelection: Range | null = null;

  focus(): void {
    const editor = this.getActiveEditor();
    editor?.focus();
  }

  saveSelection(): void {
    const selection = window.getSelection();
    if (selection && selection.rangeCount > 0) {
      const range = selection.getRangeAt(0);
      if (this.isSelectionInEditor(selection) && this.editorHasFocus()) {
        this.savedSelection = range.cloneRange();
      }
    }
  }

  private editorHasFocus(): boolean {
    const ae = document.activeElement;
    if (!ae) return false;
    const refs = this.pageEditorRefs?.toArray() ?? [];
    if (refs.some(r => r.nativeElement === ae || r.nativeElement.contains(ae))) return true;
    const body = this.editorContent?.nativeElement;
    if (body && (body === ae || body.contains(ae))) return true;
    const header = this.headerContentEl?.nativeElement;
    if (header && (header === ae || header.contains(ae))) return true;
    const footer = this.footerContentEl?.nativeElement;
    if (footer && (footer === ae || footer.contains(ae))) return true;
    return false;
  }

  restoreSelection(): boolean {
    if (!this.savedSelection) {
      console.warn('[restoreSelection] Brak zapisanej selekcji');
      return false;
    }
    
    const selection = window.getSelection();
    if (selection) {
      selection.removeAllRanges();
      selection.addRange(this.savedSelection);
      console.log('[restoreSelection] Przywrócono selekcję:', this.savedSelection.toString());
      return true;
    }
    return false;
  }

  applyDocumentStyle(style: {
    id: string;
    name: string;
    fontFamily?: string;
    fontSize?: number;
    color?: string;
    isBold?: boolean;
    isItalic?: boolean;
    isUnderline?: boolean;
    alignment?: string;
    outlineLevel?: number;
  }): void {
    const editor = this.editorContent?.nativeElement;
    if (!editor) {
      console.warn('[applyDocumentStyle] Brak elementu edytora');
      return;
    }

    console.log('[applyDocumentStyle] Otrzymany styl:', JSON.stringify(style, null, 2));

    let selection = window.getSelection();
    let range: Range | null = null;
    
    if (selection && selection.rangeCount > 0) {
      range = selection.getRangeAt(0);
      if (!editor.contains(range.commonAncestorContainer) || range.collapsed) {
        range = null;
      }
    }
    
    if (!range && this.savedSelection) {
      console.log('[applyDocumentStyle] Używam zapisanej selekcji');
      this.restoreSelection();
      selection = window.getSelection();
      if (selection && selection.rangeCount > 0) {
        range = selection.getRangeAt(0);
      }
    }
    
    if (!range || range.collapsed) {
      console.warn('[applyDocumentStyle] Brak zaznaczonego tekstu');
      return;
    }

    if (!editor.contains(range.commonAncestorContainer)) {
      console.warn('[applyDocumentStyle] Selekcja poza edytorem');
      return;
    }

    editor.focus();

    const selectedText = range.toString();
    if (!selectedText || selectedText.trim().length === 0) {
      console.warn('[applyDocumentStyle] Pusty zaznaczony tekst');
      return;
    }

    console.log('[applyDocumentStyle] Zaznaczony tekst:', selectedText);

    const fragment = range.extractContents();
    
    const flattenFragment = (node: Node): string => {
      if (node.nodeType === Node.TEXT_NODE) {
        return node.textContent || '';
      }
      let text = '';
      node.childNodes.forEach(child => {
        text += flattenFragment(child);
      });
      return text;
    };
    const plainText = flattenFragment(fragment);
    
    const styledSpan = document.createElement('span');
    
    const styles: string[] = [];
    
    if (style.fontFamily) {
      styles.push(`font-family: "${style.fontFamily}"`);
    }
    
    if (style.fontSize) {
      styles.push(`font-size: ${style.fontSize}pt`);
      console.log('[applyDocumentStyle] Ustawiam font-size:', style.fontSize + 'pt');
    }
    
    if (style.color) {
      styles.push(`color: ${style.color}`);
      console.log('[applyDocumentStyle] Ustawiam color:', style.color);
    }
    
    if (style.isBold === true) {
      styles.push('font-weight: bold');
      console.log('[applyDocumentStyle] Ustawiam bold');
    } else if (style.isBold === false) {
      styles.push('font-weight: normal');
    }
    
    if (style.isItalic === true) {
      styles.push('font-style: italic');
      console.log('[applyDocumentStyle] Ustawiam italic');
    } else if (style.isItalic === false) {
      styles.push('font-style: normal');
    }
    
    if (style.isUnderline === true) {
      styles.push('text-decoration: underline');
      console.log('[applyDocumentStyle] Ustawiam underline');
    } else if (style.isUnderline === false) {
      styles.push('text-decoration: none');
    }
    
    if (styles.length > 0) {
      styledSpan.setAttribute('style', styles.join('; '));
      console.log('[applyDocumentStyle] Finalne style:', styles.join('; '));
    }
    
    styledSpan.textContent = plainText;
    
    range.insertNode(styledSpan);
    
    const newRange = document.createRange();
    newRange.selectNodeContents(styledSpan);
    newRange.collapse(false);
    selection!.removeAllRanges();
    selection!.addRange(newRange);
    
    this.savedSelection = null;

    console.log('[applyDocumentStyle] Styl został zastosowany');
    this.onContentChange();
  }

  insertText(text: string): void {
    const editor = this.getActiveEditor();
    if (editor) {
      const live = window.getSelection();
      const liveInEditor = !!live && live.rangeCount > 0 && this.isSelectionInEditor(live);
      if (!liveInEditor && this.savedSelection) {
        this.restoreSelection();
      }
      editor.focus();
    }
    document.execCommand('insertText', false, text);
    this.onContentChange();
  }

  captureSelectionBookmark(): Range | null {
    const sel = window.getSelection();
    if (sel && sel.rangeCount > 0 && this.isSelectionInEditor(sel)) {
      return sel.getRangeAt(0).cloneRange();
    }
    return this.savedSelection ? this.savedSelection.cloneRange() : null;
  }

  pastePlainTextAt(bookmark: Range | null, rawText: string): void {
    const text = normalizeWhitespace(rawText);
    if (!text) return;

    const editor = this.getActiveEditor();
    if (!editor || !bookmark || !editor.contains(bookmark.startContainer)) return;

    const range = bookmark.cloneRange();
    range.deleteContents();

    const lines = text.split('\n');
    let lastNode: Node | null;
    if (lines.length > 1) {
      lastNode = this._pastePlainLinesAsParagraphs(range, lines);
    } else {
      const fragment = this.buildPlainTextFragment(text);
      lastNode = fragment.lastChild;
      this.insertFragmentOutsideInlineFormatting(range, fragment);
    }

    const sel = window.getSelection();
    if (sel && lastNode) {
      const caret = document.createRange();
      caret.setStartAfter(lastNode);
      caret.collapse(true);
      sel.removeAllRanges();
      sel.addRange(caret);
      this.savedSelection = caret.cloneRange();
    }

    editor.focus();
    this.onContentChange();
  }

  private buildPlainTextFragment(text: string): DocumentFragment {
    const fragment = document.createDocumentFragment();
    const lines = text.split('\n');
    lines.forEach((line, index) => {
      if (index > 0) {
        fragment.appendChild(document.createElement('br'));
      }
      fragment.appendChild(document.createTextNode(line));
    });
    return fragment;
  }

  private insertFragmentOutsideInlineFormatting(range: Range, fragment: DocumentFragment): void {
    const { container, offset } = this._liftRangeToBlockLevel(range);
    const insertAt = document.createRange();
    insertAt.setStart(container, Math.max(0, offset));
    insertAt.collapse(true);
    insertAt.insertNode(fragment);
  }

  private _liftRangeToBlockLevel(range: Range): { container: Node; offset: number } {
    const editor = this.getActiveEditor();
    const BLOCK_TAGS = ['P', 'DIV', 'LI', 'TD', 'TH', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'BLOCKQUOTE', 'PRE'];
    const isBlock = (node: Node | null): boolean =>
      !!node && (node === editor ||
        (node.nodeType === Node.ELEMENT_NODE && BLOCK_TAGS.includes((node as Element).tagName)));

    let container: Node = range.startContainer;
    let offset = range.startOffset;

    if (container.nodeType === Node.TEXT_NODE) {
      const text = container as Text;
      const right = text.splitText(offset);
      container = text.parentNode!;
      offset = Array.prototype.indexOf.call(container.childNodes, right);
    }

    let guard = 0;
    while (container !== editor && !isBlock(container) && container.parentNode && guard++ < 100) {
      const host = container as Element;
      const parent = host.parentNode!;
      const rightClone = host.cloneNode(false) as Element;
      while (host.childNodes.length > offset) {
        rightClone.appendChild(host.childNodes[offset]);
      }
      parent.insertBefore(rightClone, host.nextSibling);

      let idx = Array.prototype.indexOf.call(parent.childNodes, rightClone);
      if (!rightClone.firstChild) rightClone.remove();
      if (!host.firstChild) { host.remove(); idx--; }
      container = parent;
      offset = idx;
    }

    return { container, offset };
  }

  private _pastePlainLinesAsParagraphs(range: Range, lines: string[]): Node | null {
    const editor = this.getActiveEditor();
    if (!editor) return null;
    const { container, offset } = this._liftRangeToBlockLevel(range);

    const PARA_TAGS = ['P', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'LI', 'BLOCKQUOTE', 'PRE'];
    const host = container !== editor && container.nodeType === Node.ELEMENT_NODE
      && PARA_TAGS.includes((container as Element).tagName) ? container as Element : null;

    const shell = (): Element => {
      const el = host ? host.cloneNode(false) as Element : document.createElement('p');
      el.removeAttribute('id');
      for (const attr of Array.from(el.attributes)) {
        if (attr.name.startsWith('data-split')) el.removeAttribute(attr.name);
      }
      el.classList.remove('fmt-trailing-br');
      el.classList.remove('fmt-page-top');
      if (!el.getAttribute('class')) el.removeAttribute('class');
      return el;
    };
    const fill = (el: Element, line: string) => {
      el.appendChild(line ? document.createTextNode(line) : document.createElement('br'));
    };

    if (!host) {
      const frag = document.createDocumentFragment();
      let lastText: Node | null = null;
      for (const line of lines) {
        const p = shell();
        fill(p, line);
        lastText = p.lastChild;
        frag.appendChild(p);
      }
      const at = document.createRange();
      at.setStart(container, Math.max(0, offset));
      at.collapse(true);
      at.insertNode(frag);
      return lastText;
    }

    const parent = host.parentNode!;
    const tail = shell();
    while (host.childNodes.length > offset) {
      tail.appendChild(host.childNodes[offset]);
    }
    parent.insertBefore(tail, host.nextSibling);

    if (lines[0]) host.appendChild(document.createTextNode(lines[0]));
    if (!host.firstChild) host.appendChild(document.createElement('br'));

    for (let i = 1; i < lines.length - 1; i++) {
      const el = shell();
      fill(el, lines[i]);
      parent.insertBefore(el, tail);
    }

    const caretNode = document.createTextNode(lines[lines.length - 1]);
    tail.insertBefore(caretNode, tail.firstChild);
    if (!tail.textContent && !tail.querySelector('*')) tail.appendChild(document.createElement('br'));
    return caretNode;
  }

  insertHtml(html: string): void {
    document.execCommand('insertHTML', false, html);
    this.onContentChange();
  }

  insertDateField(): void {
    const editor = this.getActiveEditor();
    if (!editor) return;

    let range: Range | null = null;
    const live = window.getSelection();
    if (live && live.rangeCount > 0 && this.isSelectionInEditor(live)) {
      range = live.getRangeAt(0);
    } else if (this.savedSelection && editor.contains(this.savedSelection.startContainer)) {
      range = this.savedSelection.cloneRange();
    }
    if (!range) return;

    const now = new Date();
    const pad = (v: number) => String(v).padStart(2, '0');
    const span = document.createElement('span');
    span.className = 'field-date';
    span.setAttribute('data-fld-instr', 'TIME \\@ "dd-MM-yyyy"');
    span.setAttribute('contenteditable', 'false');
    span.textContent = `${pad(now.getDate())}-${pad(now.getMonth() + 1)}-${now.getFullYear()}`;

    range.deleteContents();
    range.insertNode(span);

    const caret = document.createRange();
    caret.setStartAfter(span);
    caret.collapse(true);
    const sel = window.getSelection();
    sel?.removeAllRanges();
    sel?.addRange(caret);
    this.savedSelection = caret.cloneRange();

    editor.focus();
    this.onContentChange();
  }

  insertColumnBreak(): void {
    this.insertHtml('<div class="docx-column-break"></div>');
    this._schedulePaginate('columns-change');
  }

  setBaseColumns(count: number, options?: { spaceCm?: number; separator?: boolean }): void {
    const n = Math.max(1, Math.floor(count || 1));
    const prev = this._baseColumns();
    const spaceCm = options?.spaceCm ?? prev?.spaceCm ?? 720 / TWIPS_PER_CM;
    const separator = options?.separator ?? prev?.separator ?? false;

    this._baseColumns.set(n <= 1 ? null : { count: n, equalWidth: true, spaceCm, separator });
    this._setContainerColumnAttrs(n, spaceCm, separator);

    this._schedulePaginate('columns-change');
    this.contentChange.emit(this.getContent());
  }

  getBaseColumnCount(): number {
    return this._baseColumns()?.count ?? 1;
  }

  private _setContainerColumnAttrs(count: number, spaceCm: number, separator: boolean): void {
    let attrs = this._documentContainerAttrs ? [...this._documentContainerAttrs] : [];
    attrs = attrs.filter(a => !a.name.startsWith('data-col-'));
    const classIdx = attrs.findIndex(a => a.name === 'class');
    if (classIdx < 0) {
      attrs.unshift({ name: 'class', value: 'document-content' });
    } else if (!/\bdocument-content\b/.test(attrs[classIdx].value)) {
      attrs[classIdx] = { name: 'class', value: `${attrs[classIdx].value} document-content`.trim() };
    }
    if (count > 1) {
      attrs.push({ name: 'data-col-count', value: String(count) });
      attrs.push({ name: 'data-col-space-tw', value: String(Math.round(spaceCm * TWIPS_PER_CM)) });
      attrs.push({ name: 'data-col-equal', value: '1' });
      if (separator) attrs.push({ name: 'data-col-sep', value: '1' });
    }
    this._documentContainerAttrs = attrs;
  }

  insertImage(src: string, alt: string = ''): void {
    const editor = this.getActiveEditor();
    if (!editor) return;

    const imageId = `img-${Date.now()}-${Math.floor(Math.random() * 10000)}`;

    const wrapper = document.createElement('span');
    wrapper.className = 'editor-image-wrapper';
    wrapper.setAttribute('data-image-id', imageId);
    wrapper.setAttribute('contenteditable', 'false');
    wrapper.setAttribute('draggable', 'true');
    wrapper.style.maxWidth = '100%';

    const img = document.createElement('img');
    img.src = src;
    img.alt = alt;
    img.style.maxWidth = '100%';
    img.style.height = 'auto';
    img.setAttribute('draggable', 'false');
    wrapper.appendChild(img);

    ['right', 'bottom', 'corner'].forEach(type => {
      const h = document.createElement('span');
      h.className = `image-resize-handle resize-handle-${type}`;
      h.title = type === 'right' ? 'Zmień szerokość' : type === 'bottom' ? 'Zmień wysokość' : 'Zmień rozmiar';
      wrapper.appendChild(h);
    });

    let inserted = false;

    const selection = window.getSelection();
    if (selection && selection.rangeCount > 0) {
      const range = selection.getRangeAt(0);
      if (editor.contains(range.commonAncestorContainer)) {
        range.deleteContents();
        range.insertNode(wrapper);
        range.setStartAfter(wrapper);
        range.collapse(true);
        selection.removeAllRanges();
        selection.addRange(range);
        inserted = true;
      }
    }

    if (!inserted && this.savedSelection) {
      editor.focus();
      const sel = window.getSelection();
      if (sel) {
        sel.removeAllRanges();
        sel.addRange(this.savedSelection);
        const range = sel.getRangeAt(0);
        if (editor.contains(range.commonAncestorContainer)) {
          range.deleteContents();
          range.insertNode(wrapper);
          range.setStartAfter(wrapper);
          range.collapse(true);
          sel.removeAllRanges();
          sel.addRange(range);
          inserted = true;
        }
      }
    }

    if (!inserted) {
      editor.focus();
      const p = document.createElement('p');
      p.appendChild(wrapper);
      editor.appendChild(p);
    }

    this.selectImageWrapper(wrapper);
    this.onContentChange();
  }

  insertBarcodeWithValue(src: string, valueText: string): void {
    const editor = this.editorContent?.nativeElement;
    if (!editor) return;

    const imageId = `img-${Date.now()}-${Math.floor(Math.random() * 10000)}`;

    const container = document.createElement('div');
    container.className = 'barcode-container';
    container.setAttribute('contenteditable', 'false');
    container.style.display = 'inline-block';
    container.style.textAlign = 'center';

    const wrapper = document.createElement('span');
    wrapper.className = 'editor-image-wrapper';
    wrapper.setAttribute('data-image-id', imageId);
    wrapper.setAttribute('contenteditable', 'false');
    wrapper.setAttribute('draggable', 'true');
    wrapper.style.maxWidth = '100%';
    wrapper.style.display = 'block';

    const img = document.createElement('img');
    img.src = src;
    img.alt = 'barcode';
    img.style.maxWidth = '100%';
    img.style.height = 'auto';
    img.setAttribute('draggable', 'false');
    wrapper.appendChild(img);

    ['right', 'bottom', 'corner'].forEach(type => {
      const h = document.createElement('span');
      h.className = `image-resize-handle resize-handle-${type}`;
      h.title = type === 'right' ? 'Zmień szerokość' : type === 'bottom' ? 'Zmień wysokość' : 'Zmień rozmiar';
      wrapper.appendChild(h);
    });

    container.appendChild(wrapper);

    const valueDiv = document.createElement('div');
    valueDiv.className = 'barcode-value-text';
    valueDiv.style.cssText = 'font-size: 12px; font-family: monospace; color: #333; margin-top: 4px; text-align: center; word-break: break-all;';
    valueDiv.textContent = valueText;
    container.appendChild(valueDiv);

    let inserted = false;
    const selection = window.getSelection();
    if (selection && selection.rangeCount > 0) {
      const range = selection.getRangeAt(0);
      if (editor.contains(range.commonAncestorContainer)) {
        range.deleteContents();
        range.insertNode(container);
        range.setStartAfter(container);
        range.collapse(true);
        selection.removeAllRanges();
        selection.addRange(range);
        inserted = true;
      }
    }

    if (!inserted && this.savedSelection) {
      editor.focus();
      const sel = window.getSelection();
      if (sel) {
        sel.removeAllRanges();
        sel.addRange(this.savedSelection);
        const range = sel.getRangeAt(0);
        if (editor.contains(range.commonAncestorContainer)) {
          range.deleteContents();
          range.insertNode(container);
          range.setStartAfter(container);
          range.collapse(true);
          sel.removeAllRanges();
          sel.addRange(range);
          inserted = true;
        }
      }
    }

    if (!inserted) {
      editor.focus();
      const p = document.createElement('p');
      p.appendChild(container);
      editor.appendChild(p);
    }

    this.selectImageWrapper(wrapper);
    this.onContentChange();
  }

  insertTable(config: string): void {
    const [rows, cols] = config.split('x').map(Number);
    const editor = this.getActiveEditor();
    if (!editor || rows <= 0 || cols <= 0) return;

    const colWidth = Math.floor(100 / cols);

    const table = document.createElement('table');
    table.style.cssText = 'border-collapse:collapse;width:100%;margin:10px 0;table-layout:fixed;position:relative;';

    for (let i = 0; i < rows; i++) {
      const tr = document.createElement('tr');
      for (let j = 0; j < cols; j++) {
        const td = document.createElement('td');
        td.style.cssText = `border:1px solid #ccc;padding:8px;min-width:30px;width:${colWidth}%;`;
        td.innerHTML = '<br>';
        tr.appendChild(td);
      }
      table.appendChild(tr);
    }

    const afterParagraph = document.createElement('p');
    afterParagraph.innerHTML = '&nbsp;';

    const fragment = document.createDocumentFragment();
    fragment.appendChild(table);
    fragment.appendChild(afterParagraph);

    let inserted = false;

    const selection = window.getSelection();
    if (selection && selection.rangeCount > 0) {
      const range = selection.getRangeAt(0);
      if (editor.contains(range.commonAncestorContainer)) {
        range.deleteContents();
        range.insertNode(fragment);
        const newRange = document.createRange();
        newRange.setStartAfter(afterParagraph);
        newRange.collapse(true);
        selection.removeAllRanges();
        selection.addRange(newRange);
        inserted = true;
      }
    }

    if (!inserted && this.savedSelection) {
      editor.focus();
      const sel = window.getSelection();
      if (sel) {
        sel.removeAllRanges();
        sel.addRange(this.savedSelection);
        const range = sel.getRangeAt(0);
        if (editor.contains(range.commonAncestorContainer)) {
          range.deleteContents();
          range.insertNode(fragment);
          const newRange = document.createRange();
          newRange.setStartAfter(afterParagraph);
          newRange.collapse(true);
          sel.removeAllRanges();
          sel.addRange(newRange);
          inserted = true;
        }
      }
    }

    if (!inserted) {
      editor.focus();
      editor.appendChild(table);
      editor.appendChild(afterParagraph);
    }

    this.savedSelection = null;
    const firstCell = table.rows[0]?.cells[0];
    if (firstCell) this._focusTableCell(firstCell);
    this.onContentChange();
  }

  insertLink(url: string, text?: string): void {
    const editor = this.getActiveEditor();
    if (!editor) return;

    const normalized = this.normalizeLinkUrl(url);
    if (!normalized) return;

    const live = window.getSelection();
    const liveInEditor = !!live && live.rangeCount > 0 && this.isSelectionInEditor(live);
    if (!liveInEditor && this.savedSelection) {
      this.restoreSelection();
    }
    editor.focus();

    const selection = window.getSelection();
    const hasEditorSelection = !!selection && selection.rangeCount > 0
      && !selection.isCollapsed && this.isSelectionInEditor(selection);

    if (hasEditorSelection) {
      document.execCommand('createLink', false, normalized);
    } else {
      const label = (text && text.trim().length > 0) ? text : normalized;
      const safeUrl = normalized.replace(/"/g, '&quot;');
      this.insertHtml(`<a href="${safeUrl}" target="_blank" rel="noopener">${this.escapeHtml(label)}</a>`);
    }

    this.onContentChange();
  }

  private normalizeLinkUrl(url: string): string | null {
    const trimmed = (url ?? '').trim();
    if (!trimmed) return null;
    if (/^(https?:|mailto:|tel:|#|\/)/i.test(trimmed)) return trimmed;
    if (/^[\w.-]+\.[a-z]{2,}([\/?#]|$)/i.test(trimmed)) return `https://${trimmed}`;
    return trimmed;
  }

  private escapeHtml(value: string): string {
    return value
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  insertHorizontalRule(): void {
    document.execCommand('insertHorizontalRule', false);
    this.onContentChange();
  }

  insertPageBreak(): void {
    this.insertHtml('<div class="page-break" contenteditable="false"></div>');
  }

  readonly showFormattingMarks = signal(false);

  toggleFormattingMarks(force?: boolean): void {
    this.showFormattingMarks.update(v => (force === undefined ? !v : force));
    this.editorState.update(s => ({ ...s, formattingMarks: this.showFormattingMarks() }));
    this.stateChange.emit(this.editorState());
    this._scheduleFormattingMarksRender();
  }

  undo(): void {
    this._clearPendingInlineStyle();
    this._flushPendingPersist();
    if (this.undoStack.length > 1) {
      const caret = this._captureCaretForHistory();
      const current = this.undoStack.pop()!;
      this.redoStack.push({ html: current, caret });

      const previous = this.undoStack[this.undoStack.length - 1];
      const pages = this._splitHtmlIntoPages(previous);
      this.pageContents.set(pages);
      this._content.set(previous);
      this.contentChange.emit(previous);
      this._schedulePaginate('undo');

      this.updateState();
      setTimeout(() => {
        this.refreshListLabels();
        this._restoreGlobalCaret(
          caret ?? { block: Number.MAX_SAFE_INTEGER, offset: Number.MAX_SAFE_INTEGER });
        this.updateFormattingState();
      }, 0);
    }
  }

  redo(): void {
    this._clearPendingInlineStyle();
    this._flushPendingPersist();
    if (this.redoStack.length > 0) {
      const entry = this.redoStack.pop()!;
      const next = entry.html;
      this.undoStack.push(next);

      const pages = this._splitHtmlIntoPages(next);
      this.pageContents.set(pages);
      this._content.set(next);
      this.contentChange.emit(next);
      this._schedulePaginate('redo');

      this.updateState();
      setTimeout(() => {
        this.refreshListLabels();
        this._restoreGlobalCaret(
          entry.caret ?? { block: Number.MAX_SAFE_INTEGER, offset: Number.MAX_SAFE_INTEGER });
        this.updateFormattingState();
      }, 0);
    }
  }

  private _captureCaretForHistory(): { block: number; offset: number } | null {
    const refs = this.pageEditorRefs?.toArray() ?? [];
    const sel = window.getSelection();
    if (sel && sel.rangeCount > 0) {
      const fromLive = this._globalCaretFromRange(sel.getRangeAt(0), refs);
      if (fromLive) return fromLive;
    }
    return this.savedSelection ? this._globalCaretFromRange(this.savedSelection, refs) : null;
  }

  private saveToUndoStack(): void {
    const html = this.getContent();
    if (!html) return;
    
    if (this.undoStack.length > 0 && this.undoStack[this.undoStack.length - 1] === html) {
      return;
    }

    this.undoStack.push(html);
    
    if (this.undoStack.length > 100) {
      this.undoStack.shift();
    }

    this.redoStack = [];
  }

  private updateFormattingState(): void {
    const selection = window.getSelection();
    let fontSize = 11;
    let fontFamily = 'Calibri';
    let textColor = '#000000';
    let currentBlockFormat = 'p';
    let alignment: 'left' | 'center' | 'right' | 'justify' = 'left';
    let bulletList = false;
    let numberedList = false;

    const refs = this.pageEditorRefs?.toArray() ?? [];
    const hasEditors = refs.length > 0;
    const selInEditor =
      !!selection && selection.rangeCount > 0 && this.isSelectionInEditor(selection);
    const fromSelection =
      !!selection && selection.rangeCount > 0 && (selInEditor || !hasEditors);

    let element: HTMLElement | null = null;
    if (fromSelection) {
      const range = selection!.getRangeAt(0);
      let node: Node | null = range.startContainer;
      if (node && node.nodeType === Node.ELEMENT_NODE) {
        const el = node as HTMLElement;
        const idx = Math.min(range.startOffset, el.childNodes.length - 1);
        node = el.childNodes[Math.max(idx, 0)] ?? el.lastChild ?? el;
        while (node && node.nodeType === Node.ELEMENT_NODE && (node as HTMLElement).firstChild) {
          node = (node as HTMLElement).firstChild;
        }
      }
      if (node?.nodeType === Node.TEXT_NODE) {
        element = node.parentElement;
      } else if (node instanceof HTMLElement) {
        element = node;
      }
    } else {
      element = this._firstEditableContextElement();
    }

    if (element) {
      const computedStyle = window.getComputedStyle(element);

      const fontSizePx = parseFloat(computedStyle.fontSize);
      fontSize = Math.round(fontSizePx * 0.75);

      fontFamily = computedStyle.fontFamily.replace(/['"]/g, '').split(',')[0].trim();

      textColor = this.rgbToHex(computedStyle.color);

      let blockElement = element;
      while (blockElement && !['P', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'DIV', 'LI'].includes(blockElement.tagName)) {
        blockElement = blockElement.parentElement!;
      }
      if (blockElement) {
        currentBlockFormat = blockElement.tagName.toLowerCase();
        const ta = window.getComputedStyle(blockElement).textAlign;
        alignment = ta === 'center' ? 'center'
          : (ta === 'right' || ta === 'end') ? 'right'
          : ta === 'justify' ? 'justify'
          : 'left';
      }

      const li = element.closest('li');
      const listTag = li?.parentElement?.tagName;
      bulletList = listTag === 'UL';
      numberedList = listTag === 'OL';
    }

    if (this._pendingInlineStyle && selection && this._caretMatchesPendingAnchor(selection)) {
      if (this._pendingInlineStyle.fontFamily) {
        fontFamily = this._pendingInlineStyle.fontFamily;
      }
      const pendingPt = parseFloat(this._pendingInlineStyle.fontSize ?? '');
      if (Number.isFinite(pendingPt)) {
        fontSize = Math.round(pendingPt);
      }
    }

    const formatting: TextFormatting = {
      ...(fromSelection
        ? {
            bold: document.queryCommandState('bold'),
            italic: document.queryCommandState('italic'),
            underline: document.queryCommandState('underline'),
            strikethrough: document.queryCommandState('strikeThrough'),
            subscript: document.queryCommandState('subscript'),
            superscript: document.queryCommandState('superscript'),
          }
        : this._readInlineFormattingFromElement(element)),
      alignment,
      bulletList,
      numberedList
    };

    const fontMixed = this.computeFontMixed(selection);

    this.editorState.update(state => ({
      ...state,
      fontMixed,
      currentFormatting: formatting,
      currentStyle: {
        fontFamily: fontFamily,
        fontSize: fontSize,
        textColor: textColor,
        blockFormat: currentBlockFormat
      }
    }));

    this.stateChange.emit(this.editorState());
  }

  private _firstEditableContextElement(): HTMLElement | null {
    const refs = this.pageEditorRefs?.toArray() ?? [];
    const editor = refs[0]?.nativeElement ?? this.editorContent?.nativeElement ?? null;
    if (!editor) return null;

    const firstBlock =
      editor.querySelector('p, h1, h2, h3, h4, h5, h6, li, td, th, div') as HTMLElement | null;

    const walker = document.createTreeWalker(firstBlock ?? editor, NodeFilter.SHOW_TEXT, {
      acceptNode: (n: Node) =>
        (n.textContent ?? '').replace(/[\u200B\uFEFF\u00A0]/g, '').trim().length > 0
          ? NodeFilter.FILTER_ACCEPT
          : NodeFilter.FILTER_REJECT,
    });
    const textNode = walker.nextNode();
    return (textNode?.parentElement as HTMLElement | null) ?? firstBlock;
  }

  private _readInlineFormattingFromElement(
    element: HTMLElement | null,
  ): Pick<TextFormatting, 'bold' | 'italic' | 'underline' | 'strikethrough' | 'subscript' | 'superscript'> {
    const empty = {
      bold: false, italic: false, underline: false,
      strikethrough: false, subscript: false, superscript: false,
    };
    if (!element) return empty;
    try {
      const cs = window.getComputedStyle(element);
      const weight = parseInt(cs.fontWeight, 10);
      const decoration = `${cs.textDecorationLine || cs.textDecoration || ''}`;
      return {
        bold: (Number.isFinite(weight) && weight >= 600)
          || cs.fontWeight === 'bold' || cs.fontWeight === 'bolder',
        italic: cs.fontStyle === 'italic' || cs.fontStyle === 'oblique',
        underline: decoration.includes('underline'),
        strikethrough: decoration.includes('line-through'),
        subscript: !!element.closest('sub') || cs.verticalAlign === 'sub',
        superscript: !!element.closest('sup') || cs.verticalAlign === 'super',
      };
    } catch {
      return empty;
    }
  }

  private _syncInitialFormatting(): void {
    if (this._isDestroyed) return;
    this.updateFormattingState();
  }

  private computeFontMixed(selection: Selection | null): boolean {
    if (!selection || selection.rangeCount === 0) return false;
    const range = selection.getRangeAt(0);
    if (range.collapsed) return false;

    try {
      const root = range.commonAncestorContainer;
      const walker = document.createTreeWalker(
        root.nodeType === Node.ELEMENT_NODE ? root : root.parentNode ?? root,
        NodeFilter.SHOW_TEXT,
      );
      const families = new Set<string>();
      let scanned = 0;
      let current = walker.nextNode();
      while (current && scanned < 400) {
        if (
          (current.textContent ?? '').trim().length > 0 &&
          range.intersectsNode(current) &&
          current.parentElement
        ) {
          const family = window
            .getComputedStyle(current.parentElement)
            .fontFamily.replace(/['"]/g, '')
            .split(',')[0]
            .trim()
            .toLowerCase();
          if (family) families.add(family);
          if (families.size > 1) return true;
          scanned++;
        }
        current = walker.nextNode();
      }
      return families.size > 1;
    } catch {
      return false;
    }
  }

  private updateState(): void {
    const refs = this.pageEditorRefs?.toArray() ?? [];
    const fallback = this.editorContent?.nativeElement;
    if (refs.length === 0 && !fallback) return;

    let text = '';
    if (refs.length > 0) {
      text = refs.map(r => r.nativeElement.innerText || '').join('\n');
    } else if (fallback) {
      text = fallback.innerText || '';
    }
    const headerText = this.headerContentEl?.nativeElement?.innerText || '';
    const footerText = this.footerContentEl?.nativeElement?.innerText || '';
    if (headerText) text += '\n' + headerText;
    if (footerText) text += '\n' + footerText;

    const normalized = text
      .replace(/[\u200B-\u200D\uFEFF]/g, '')
      .replace(/\{page\}|\{pages\}/g, ' ');
    const matches = normalized.match(/[\p{L}\p{N}]+(?:[\-_'\u2019][\p{L}\p{N}]+|[.,]\p{N}+)*/gu);
    const wordCount = matches ? matches.length : 0;

    try {
      const w = window as unknown as Record<string, unknown>;
      w['__wcDebug'] = () => {
        const lines = normalized.split('\n');
        const perLine = lines.map((line, i) => {
          const m = line.match(/[\p{L}\p{N}]+(?:[\-_'\u2019][\p{L}\p{N}]+|[.,]\p{N}+)*/gu);
          return { i, count: m ? m.length : 0, text: line };
        });
        console.table(perLine.filter(p => p.count > 0));
        console.log('TOTAL:', wordCount);
        console.log('TEXT LENGTH:', normalized.length);
        return { wordCount, totalLines: lines.length, perLine, text: normalized };
      };
      w['__wcCopy'] = async () => {
        await navigator.clipboard.writeText(normalized);
        console.log('Skopiowano', normalized.length, 'znaków do schowka.');
      };
    } catch { }

    this.editorState.update(state => ({
      ...state,
      isModified: this._isDirty,
      canUndo: this.undoStack.length > 1 || (this._persistTimer !== null && this.undoStack.length >= 1),
      canRedo: this.redoStack.length > 0,
      wordCount
    }));

    this.stateChange.emit(this.editorState());
  }

  setActivePage(index: number, _ev: Event): void {
    const refs = this.pageEditorRefs?.toArray() ?? [];
    if (refs[index]) {
      this.editorContent = refs[index];
      this.activePageIndex.set(index);
    }
  }

  onPageInput(index: number, _ev: Event): void {
    if (this._isRepaginating) return;
    this._consumePendingDeleteCapture();
    this._isDirty = true;
    this._schedulePaginate('input');
    this._schedulePersist();
    this.updateState();
    this.updateFormattingState();
  }

  onEditorBeforeInput(event: InputEvent): void {
    if (event.inputType === 'deleteContentBackward' || event.inputType === 'deleteContentForward') {
      this._captureStyleBeforeDelete();
      return;
    }
    if (event.inputType !== 'insertText' && event.inputType !== 'insertFromPaste') return;
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0 || !sel.isCollapsed) return;
    if (event.inputType === 'insertText' && this._pendingInlineStyle && this._caretMatchesPendingAnchor(sel)) {
      this._materializePendingStyleSpan(sel);
    }
    const anchor = sel.anchorNode;
    if (!anchor) return;
    const el = anchor.nodeType === Node.ELEMENT_NODE ? (anchor as Element) : anchor.parentElement;
    const block = el?.closest('p, h1, h2, h3, h4, h5, h6, li, td, th');
    if (!block || block.textContent !== '\u00A0') return;
    const walker = document.createTreeWalker(block, NodeFilter.SHOW_TEXT);
    let nbspNode: Text | null = null;
    for (let n = walker.nextNode(); n; n = walker.nextNode()) {
      if ((n as Text).data.includes('\u00A0')) { nbspNode = n as Text; break; }
    }
    if (!nbspNode) return;
    const range = document.createRange();
    range.selectNodeContents(nbspNode);
    sel.removeAllRanges();
    sel.addRange(range);
  }

  private _setPendingInlineStyle(
    patch: { fontFamily?: string; fontSize?: string },
    caret: Range,
  ): void {
    const sameAnchor =
      this._pendingStyleAnchor?.node === caret.startContainer &&
      this._pendingStyleAnchor?.offset === caret.startOffset;
    this._pendingInlineStyle = { ...(sameAnchor ? this._pendingInlineStyle : null), ...patch };
    this._pendingStyleAnchor = { node: caret.startContainer, offset: caret.startOffset };
  }

  private _clearPendingInlineStyle(): void {
    this._pendingInlineStyle = null;
    this._pendingStyleAnchor = null;
    this._pendingDeleteCapture = null;
  }

  private _caretMatchesPendingAnchor(selection: Selection): boolean {
    const anchor = this._pendingStyleAnchor;
    if (!anchor || !anchor.node.isConnected) return false;
    if (selection.rangeCount === 0 || !selection.isCollapsed) return false;
    const range = selection.getRangeAt(0);
    return range.startContainer === anchor.node && range.startOffset === anchor.offset;
  }

  private _captureStyleBeforeDelete(): void {
    this._pendingDeleteCapture = null;
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0 || !sel.isCollapsed) return;
    if (!this.isSelectionInEditor(sel)) return;
    const node = sel.anchorNode;
    const el = node instanceof Element ? node : node?.parentElement;
    const span = (el?.closest?.('span') as HTMLElement | null) ?? null;
    if (!span || (span.style.fontFamily === '' && span.style.fontSize === '')) return;
    const visible = (span.textContent ?? '').replace(/[\u200B\uFEFF]/g, '');
    if (visible.length !== 1) return;
    try {
      const cs = window.getComputedStyle(span);
      const fontFamily = cs.fontFamily.replace(/['"]/g, '').split(',')[0].trim();
      const sizePx = parseFloat(cs.fontSize);
      const capture: { fontFamily?: string; fontSize?: string } = {};
      if (fontFamily) capture.fontFamily = fontFamily;
      if (Number.isFinite(sizePx)) capture.fontSize = `${Math.round(sizePx * 0.75)}pt`;
      if (capture.fontFamily || capture.fontSize) this._pendingDeleteCapture = capture;
    } catch {
    }
  }

  private _consumePendingDeleteCapture(): void {
    const capture = this._pendingDeleteCapture;
    if (!capture) return;
    this._pendingDeleteCapture = null;
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0 || !sel.isCollapsed || !sel.anchorNode) return;
    if (!this.isSelectionInEditor(sel)) return;
    this._pendingInlineStyle = capture;
    this._pendingStyleAnchor = { node: sel.anchorNode, offset: sel.anchorOffset };
    this._pendingStyleHoldUntil = Date.now() + 150;
  }

  private _materializePendingStyleSpan(sel: Selection): void {
    const pending = this._pendingInlineStyle;
    if (!pending) return;
    const range = sel.getRangeAt(0);
    const zws = '\u200B';
    const containerEl = range.startContainer.nodeType === Node.TEXT_NODE
      ? range.startContainer.parentElement
      : range.startContainer as HTMLElement;
    if (
      containerEl instanceof HTMLSpanElement &&
      containerEl.textContent === zws &&
      (containerEl.style.fontFamily !== '' || containerEl.style.fontSize !== '')
    ) {
      if (pending.fontFamily) containerEl.style.fontFamily = pending.fontFamily;
      if (pending.fontSize) containerEl.style.fontSize = pending.fontSize;
      this._clearPendingInlineStyle();
      return;
    }
    const span = document.createElement('span');
    if (pending.fontFamily) span.style.fontFamily = pending.fontFamily;
    if (pending.fontSize) span.style.fontSize = pending.fontSize;
    span.textContent = zws;
    range.insertNode(span);
    const newRange = document.createRange();
    newRange.setStart(span.firstChild!, 1);
    newRange.collapse(true);
    sel.removeAllRanges();
    sel.addRange(newRange);
    this.savedSelection = newRange.cloneRange();
    this._clearPendingInlineStyle();
  }

  private static readonly UNCHECKED_BULLET_CODES = new Set([0x71, 0xa8, 0x6f, 0x72]);
  private static readonly CHECKED_BULLET_CODES = new Set([0xfe, 0xfd, 0xfc]);
  private static readonly UNCHECKED_BULLET_GLYPHS = new Set(['☐', '❑', '□', '❒', '◻']);
  private static readonly CHECKED_BULLET_GLYPHS = new Set(['☑', '☒', '✔', '✓']);

  private _checkboxListSeq = 0;

  toggleCheckboxBullet(): void {
    if (this.readOnly) return;
    const editor = this.getActiveEditor();
    if (!editor) return;

    const sel = window.getSelection();
    let node: Node | null = sel && sel.rangeCount > 0 && this.isSelectionInEditor(sel)
      ? sel.getRangeAt(0).startContainer
      : this.savedSelection?.startContainer ?? null;
    const li = (node instanceof Element ? node : node?.parentElement)?.closest('li') ?? null;
    const container = li?.parentElement instanceof HTMLElement ? li.parentElement : null;
    if (!li || !container || !editor.contains(li)) return;
    if (!/^(ul|ol)$/i.test(container.tagName) || !container.hasAttribute('data-num-id')) return;

    const state = this._checkboxBulletState(container);
    if (!state) return;

    const solo = this._isolateListItem(container, li);
    const targetLvlText = state.checked ? state.uncheckedLvlText : state.checkedLvlText;
    solo.setAttribute('data-lvl-text', targetLvlText);
    if (state.bulletFont) solo.setAttribute('data-bullet-font', state.bulletFont);
    solo.setAttribute('data-lvl-override', '1');
    solo.setAttribute('data-unchecked-lvl-text', state.uncheckedLvlText);

    const glyph = bulletGlyphFromContract(targetLvlText, state.bulletFont);
    const marker = li.querySelector<HTMLElement>(':scope > span.list-marker');
    if (marker) {
      marker.textContent = glyph;
    } else {
      const synthesized = synthesizeBulletMarker(solo);
      if (synthesized) li.insertBefore(synthesized, li.firstChild);
    }

    const caret = document.createRange();
    caret.selectNodeContents(li);
    caret.collapse(false);
    sel?.removeAllRanges();
    sel?.addRange(caret);
    this.savedSelection = caret.cloneRange();

    this.onContentChange();
  }

  private _checkboxBulletState(container: HTMLElement): {
    checked: boolean;
    checkedLvlText: string;
    uncheckedLvlText: string;
    bulletFont: string | null;
  } | null {
    if ((container.getAttribute('data-num-fmt') ?? '') !== 'bullet') return null;
    const lvlText = container.getAttribute('data-lvl-text') ?? '';
    if (lvlText.length === 0) return null;
    const code = lvlText.codePointAt(0) ?? 0;
    const isPua = code >= 0xf000 && code <= 0xf0ff;
    const low = isPua ? code & 0xff : code;
    const font = container.getAttribute('data-bullet-font');
    const storedUnchecked = container.getAttribute('data-unchecked-lvl-text');

    const isUnchecked = isPua
      ? WysiwygEditorComponent.UNCHECKED_BULLET_CODES.has(low)
      : WysiwygEditorComponent.UNCHECKED_BULLET_GLYPHS.has(lvlText);
    const isChecked = isPua
      ? WysiwygEditorComponent.CHECKED_BULLET_CODES.has(low)
      : WysiwygEditorComponent.CHECKED_BULLET_GLYPHS.has(lvlText);
    if (!isUnchecked && !isChecked) return null;

    return {
      checked: isChecked,
      checkedLvlText: isPua ? String.fromCharCode(0xf0fe) : String.fromCharCode(0x2611),
      uncheckedLvlText: isUnchecked
        ? lvlText
        : storedUnchecked ?? (isPua ? String.fromCharCode(0xf0a8) : String.fromCharCode(0x2610)),
      bulletFont: isPua ? (font ?? 'Wingdings') : font,
    };
  }

  private _isolateListItem(container: HTMLElement, li: HTMLElement): HTMLElement {
    const kids = Array.from(container.children);
    const otherItems = kids.filter(k => k !== li && k.tagName === 'LI');
    if (otherItems.length === 0) {
      container.setAttribute('data-num-id', this._uniqueListInstanceId());
      return container;
    }

    const parent = container.parentNode!;
    const after = kids.slice(kids.indexOf(li) + 1);
    if (after.length > 0) {
      const tail = container.cloneNode(false) as HTMLElement;
      after.forEach(k => tail.appendChild(k));
      parent.insertBefore(tail, container.nextSibling);
    }
    const solo = container.cloneNode(false) as HTMLElement;
    solo.setAttribute('data-num-id', this._uniqueListInstanceId());
    solo.appendChild(li);
    parent.insertBefore(solo, container.nextSibling);
    if (!container.querySelector(':scope > li')) container.remove();
    return solo;
  }

  private _uniqueListInstanceId(): string {
    let id: string;
    do {
      id = `chk-${++this._checkboxListSeq}`;
    } while (document.querySelector(`[data-num-id="${id}"]`));
    return id;
  }

  refreshListLabels(): void {
    const roots = (this.pageEditorRefs?.toArray() ?? []).map(r => r.nativeElement);
    if (roots.length === 0 && this.editorContent?.nativeElement) {
      roots.push(this.editorContent.nativeElement);
    }
    if (roots.length === 0) return;
    applyListLabels(roots);
    ensureBulletMarkers(roots);
  }

  private _schedulePersist(): void {
    if (this._persistTimer) clearTimeout(this._persistTimer);
    this._persistTimer = setTimeout(() => {
      this._persistTimer = null;
      this._persistNow();
    }, 500);
  }

  private _flushPendingPersist(): void {
    if (!this._persistTimer) return;
    clearTimeout(this._persistTimer);
    this._persistTimer = null;
    this._persistNow();
  }

  private _persistNow(): void {
    this.syncFootnotesWithBody();
    this.syncEndnotesWithBody();
    this.refreshListLabels();
    const html = this.getContent();
    this._isInternalUpdate = true;
    this._content.set(html);
    this.contentChange.emit(html);
    this._isInternalUpdate = false;
    if (this.undoStack.length === 0 || this.undoStack[this.undoStack.length - 1] !== html) {
      this.undoStack.push(html);
      if (this.undoStack.length > 100) this.undoStack.shift();
      this.redoStack = [];
    }
  }

  private _captureDocumentDefaults(html: string): string | null {
    if (!html) return null;
    const tmp = document.createElement('div');
    tmp.innerHTML = html;
    const container = tmp.querySelector('.document-content') as HTMLElement | null;
    if (!container) {
      this._documentContainerAttrs = null;
      this._baseColumns.set(null);
      this.documentDefaultFontSize.set(null);
      this.documentDefaultFontFamily.set(null);
      this.documentDefaultLineHeight.set(null);
      this.documentDefaultParagraphSpacing.set(null);
      this.documentDefaultParagraphSpacingBefore.set(null);
      this.documentParagraphSpacingSum.set(false);
      this.documentDefaultLineTw.set(null);
      return null;
    }
    this.documentParagraphSpacingSum.set(container.getAttribute('data-para-spacing-sum') === '1');
    if (container.style.fontSize) this.documentDefaultFontSize.set(container.style.fontSize);
    if (container.style.fontFamily) this.documentDefaultFontFamily.set(container.style.fontFamily);
    this.documentDefaultLineHeight.set(container.style.lineHeight || null);
    const afterTw = parseInt(container.getAttribute('data-default-after-tw') ?? '', 10);
    this.documentDefaultParagraphSpacing.set(
      Number.isFinite(afterTw) && afterTw >= 0 ? `${afterTw / 20}pt` : null
    );
    const beforeTw = parseInt(container.getAttribute('data-default-before-tw') ?? '', 10);
    this.documentDefaultParagraphSpacingBefore.set(
      Number.isFinite(beforeTw) && beforeTw >= 0 ? `${beforeTw / 20}pt` : null
    );
    const lineTw = parseInt(container.getAttribute('data-default-line') ?? '', 10);
    const lineRule = container.getAttribute('data-default-line-rule');
    this.documentDefaultLineTw.set(
      Number.isFinite(lineTw) && lineTw > 0 && (!lineRule || lineRule === 'auto')
        ? String(lineTw)
        : null
    );
    this._baseColumns.set(parseColumnDataAttributes(container) ?? null);

    this._documentContainerAttrs = Array.from(container.attributes).map(a => ({
      name: a.name,
      value: a.value,
    }));
    if (container.parentElement === tmp && tmp.children.length === 1) {
      return container.innerHTML;
    }
    return null;
  }

  private _wrapWithDocumentContainer(html: string): string {
    if (!this._documentContainerAttrs || !html) return html;
    const div = document.createElement('div');
    for (const { name, value } of this._documentContainerAttrs) {
      try {
        div.setAttribute(name, value);
      } catch {
      }
    }
    div.innerHTML = html;
    return div.outerHTML;
  }

  private _splitHtmlIntoPages(html: string): string[] {
    if (!html) return ['<p></p>'];
    const marker = '<div class="page-break"></div>';
    const parts = html.split(/<div[^>]*class=["'][^"']*\bpage-break\b[^"']*["'][^>]*>\s*<\/div>/gi);
    const pages = parts
      .map((p, i) => (i < parts.length - 1 ? p + marker : p))
      .map(p => p.trim())
      .filter(p => p.length > 0);
    return pages.length ? pages : ['<p></p>'];
  }

  private _measureBandHeightsPx(refs: ElementRef<HTMLDivElement>[]):
    { headerFirst: number; headerRest: number; footerFirst: number; footerRest: number } {
    const container = refs[0]?.nativeElement.closest('.pages-container');
    const pageEls = container ? (Array.from(container.querySelectorAll('.page')) as HTMLElement[]) : [];
    const headers = pageEls.map(p => (p.querySelector('.page-header') as HTMLElement | null)?.offsetHeight ?? 0);
    const footers = pageEls.map(p => (p.querySelector('.page-footer') as HTMLElement | null)?.offsetHeight ?? 0);
    const restMax = (arr: number[]) => arr.length > 1 ? Math.max(...arr.slice(1)) : (arr[0] ?? 0);
    return {
      headerFirst: headers[0] ?? 0,
      headerRest: restMax(headers),
      footerFirst: footers[0] ?? 0,
      footerRest: restMax(footers),
    };
  }

  private _isSectionBreakMarker(el: Element | null): boolean {
    return !!el && el.nodeType === 1 && el.classList?.contains('docx-section-break');
  }

  private _parseSectionGeometry(el: HTMLElement, current: PageGeometry): PageGeometry {
    const dim = (name: string): number | null => {
      const v = parseFloat(el.getAttribute(name) ?? '');
      return Number.isFinite(v) && v > 0 ? v : null;
    };
    const margin = (name: string): number | null => {
      const v = parseFloat(el.getAttribute(name) ?? '');
      return Number.isFinite(v) && v >= 0 ? v : null;
    };
    const rawOrientation = el.getAttribute('data-orientation');
    return {
      widthCm: dim('data-page-width-cm') ?? current.widthCm,
      heightCm: dim('data-page-height-cm') ?? current.heightCm,
      orientation: rawOrientation === 'landscape' || rawOrientation === 'portrait'
        ? rawOrientation
        : current.orientation,
      margins: {
        top: margin('data-margin-top-cm') ?? current.margins.top,
        bottom: margin('data-margin-bottom-cm') ?? current.margins.bottom,
        left: margin('data-margin-left-cm') ?? current.margins.left,
        right: margin('data-margin-right-cm') ?? current.margins.right,
      },
      headerDistanceCm: margin('data-header-distance-cm') ?? current.headerDistanceCm,
      footerDistanceCm: margin('data-footer-distance-cm') ?? current.footerDistanceCm,
      columns: parseColumnDataAttributes(el),
    };
  }

  private _deriveGeometriesForPages(pages: string[]): PageGeometry[] {
    let current = this.baseGeometry();
    let sectionCounter = 0;
    const sectionIndexes: number[] = [];
    const probe = document.createElement('div');
    const result = pages.map(html => {
      probe.innerHTML = html;
      const markers = Array.from(probe.querySelectorAll('.docx-section-break')) as HTMLElement[];
      let geo = current;
      let pageSection = sectionCounter;
      if (markers.length > 0) {
        let idx = 0;
        if (this._isSectionBreakMarker(probe.firstElementChild)) {
          current = this._parseSectionGeometry(markers[0], current);
          geo = current;
          sectionCounter++;
          pageSection = sectionCounter;
          idx = 1;
        }
        for (; idx < markers.length; idx++) {
          current = this._parseSectionGeometry(markers[idx], current);
          sectionCounter++;
        }
      }
      sectionIndexes.push(pageSection);
      return geo;
    });
    this.pageSectionIndexes.set(sectionIndexes);
    return result;
  }

  private static readonly PAGINATE_DEBOUNCE_MS = 250;
  private static readonly PAGINATE_MAX_WAIT_MS = 600;
  private _paginateFirstScheduledAt: number | null = null;

  private _schedulePaginate(_reason: string): void {
    if (this._paginateTimer) clearTimeout(this._paginateTimer);
    const now = Date.now();
    if (this._paginateFirstScheduledAt === null) this._paginateFirstScheduledAt = now;
    const waited = now - this._paginateFirstScheduledAt;
    const delay = Math.min(
      WysiwygEditorComponent.PAGINATE_DEBOUNCE_MS,
      Math.max(0, WysiwygEditorComponent.PAGINATE_MAX_WAIT_MS - waited)
    );
    this._paginateTimer = setTimeout(() => {
      this._paginateTimer = null;
      this._paginateFirstScheduledAt = null;
      this._repaginateNow();
    }, delay);
  }

  private _flushPaginateSoon(): void {
    if (this._paginateRafHandle !== null) return;
    this._paginateRafHandle = requestAnimationFrame(() => {
      this._paginateRafHandle = null;
      this._flushPaginateNow();
    });
  }

  private _flushPaginateNow(): void {
    if (this._paginateTimer) {
      clearTimeout(this._paginateTimer);
      this._paginateTimer = null;
    }
    this._paginateFirstScheduledAt = null;
    this._repaginateNow();
  }

  private _repaginateAfterResources(): void {
    const gen = ++this._resourceRepaginateGen;
    const fontSet = (document as unknown as { fonts?: { ready?: Promise<unknown> } }).fonts;
    const fontsReady: Promise<unknown> =
      fontSet && typeof fontSet.ready?.then === 'function' ? fontSet.ready : Promise.resolve();
    Promise.all([fontsReady, this._pendingImagesSettled()])
      .then(() => new Promise<void>(resolve => setTimeout(resolve, 0)))
      .then(() => {
        if (this._isDestroyed || gen !== this._resourceRepaginateGen) return;
        this._tableMeasureCache.clear();
        this._blockRunMeasureCache.clear();
        this._flushPaginateNow();
      })
      .catch(() => {
      });
  }

  private _pendingImagesSettled(): Promise<void> {
    const refs = this.pageEditorRefs?.toArray() ?? [];
    const pending: Promise<void>[] = [];
    for (const ref of refs) {
      const imgs = ref.nativeElement.querySelectorAll('img');
      imgs.forEach((img: HTMLImageElement) => {
        if (img.complete) return;
        pending.push(
          new Promise<void>(resolve => {
            const done = () => resolve();
            img.addEventListener('load', done, { once: true });
            img.addEventListener('error', done, { once: true });
            setTimeout(done, 3000);
          })
        );
      });
    }
    return pending.length ? Promise.all(pending).then(() => undefined) : Promise.resolve();
  }

  private _repaginateNow(): void {
    if (this._isRepaginating) return;
    const refs = this.pageEditorRefs?.toArray() ?? [];
    if (refs.length === 0) {
      return;
    }

    this._isRepaginating = true;
    try {
      const caret = this._saveGlobalCaret(refs);

      const allBlocks: HTMLElement[] = [];
      const livePageContents: string[] = [];
      const liveTmp = document.createElement('div');
      for (const ref of refs) {
        const kids = this._flattenTopBlocks(ref.nativeElement);
        kids.forEach(child => {
          allBlocks.push(child.cloneNode(true) as HTMLElement);
        });
        liveTmp.innerHTML = '';
        (Array.from(ref.nativeElement.children) as HTMLElement[]).forEach(child => {
          liveTmp.appendChild(child.cloneNode(true));
        });
        livePageContents.push(liveTmp.innerHTML);
      }
      const mergedInput: HTMLElement[] = [];
      for (const b of allBlocks) {
        this._stripFmtPageTop(b);
        const prev = mergedInput[mergedInput.length - 1];
        if (b.getAttribute('data-split-para') === 'cont' && prev && prev.tagName === b.tagName) {
          while (b.firstChild) prev.appendChild(b.firstChild);
          continue;
        }
        if (b.getAttribute('data-split-para') === 'cont') {
          b.removeAttribute('data-split-para');
          b.style.marginTop = '';
        }
        mergedInput.push(b);
      }
      const premerged: HTMLElement[] = [];
      for (const b of mergedInput) {
        const prev = premerged[premerged.length - 1];
        if (
          b.tagName === 'TABLE' && prev && prev.tagName === 'TABLE' &&
          b.getAttribute('data-split-table-id') &&
          b.getAttribute('data-split-table-id') === prev.getAttribute('data-split-table-id')
        ) {
          const targetBody = prev.querySelector('tbody') ?? prev;
          this._tableSourceRows(b as HTMLTableElement).forEach(tr => targetBody.appendChild(tr));
          continue;
        }
        premerged.push(b);
      }
      for (const b of premerged) {
        if (b.tagName === 'TABLE') this._mergeSplitRowsIn(b as HTMLTableElement, true);
      }
      allBlocks.length = 0;
      allBlocks.push(...premerged);
      if (allBlocks.length === 0) {
        allBlocks.push(document.createElement('p'));
      }

      const measuredBands = this._measureBandHeightsPx(refs);
      const prevBands = this._measuredBandHeights;
      this._measuredBandHeights = measuredBands;
      if ((prevBands?.footerFirst ?? 0) !== measuredBands.footerFirst
        || (prevBands?.footerRest ?? 0) !== measuredBands.footerRest) {
        this._cdr.markForCheck();
      }
      const availableFor = (geo: PageGeometry, pageIdx: number): number => {
        const headerBand = Math.max(this._bandCmFor(geo, 'header') * CSS_PX_PER_CM,
          pageIdx === 0 ? measuredBands.headerFirst : measuredBands.headerRest);
        const footerBand = Math.max(this._bandCmFor(geo, 'footer') * CSS_PX_PER_CM,
          pageIdx === 0 ? measuredBands.footerFirst : measuredBands.footerRest);
        const offsets = (this._distanceCmFor(geo, 'header') + this._distanceCmFor(geo, 'footer')) * CSS_PX_PER_CM;
        return Math.max(100, geo.heightCm * CSS_PX_PER_CM - headerBand - footerBand - offsets);
      };
      const contentWidthPx = (geo: PageGeometry): number =>
        Math.max(2, geo.widthCm - geo.margins.left - geo.margins.right) * CSS_PX_PER_CM;

      const columnCountFor = (geo: PageGeometry): number =>
        geo.columns && geo.columns.count > 1 ? geo.columns.count : 1;
      const columnWidthPx = (geo: PageGeometry): number => {
        const count = columnCountFor(geo);
        if (count <= 1) return contentWidthPx(geo);
        const gapPx = Math.max(0, geo.columns?.spaceCm ?? 0) * CSS_PX_PER_CM;
        return Math.max(2, (contentWidthPx(geo) - gapPx * (count - 1)) / count);
      };

      const baseGeo = this.baseGeometry();
      let curGeo = baseGeo;

      const probeEd = refs[0].nativeElement;
      const cs = getComputedStyle(probeEd);
      const innerW = probeEd.clientWidth - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight);
      const baseContentPx = contentWidthPx(baseGeo);
      const page0 = probeEd.closest('.page') as HTMLElement | null;
      const domGeometryStale = !!page0 && page0.clientWidth > 0
        && Math.abs(page0.clientWidth - baseGeo.widthCm * CSS_PX_PER_CM) > 1;
      if (domGeometryStale && !this._staleGeometryRerun) {
        this._staleGeometryRerun = true;
        this._flushPaginateSoon();
      } else if (!domGeometryStale) {
        this._staleGeometryRerun = false;
      }
      const widthScale = !domGeometryStale && innerW > 0 && baseContentPx > 0 ? innerW / baseContentPx : 1;
      const measurerWidthFor = (geo: PageGeometry): number => columnWidthPx(geo) * widthScale;
      const lineHeightPx =
        parseFloat(cs.lineHeight) || parseFloat(cs.fontSize) * 1.2 || 16;
      const capacityFor = (geo: PageGeometry, pageIdx: number): number => {
        const count = columnCountFor(geo);
        const column = availableFor(geo, pageIdx);
        return count <= 1 ? column : count * column - (count - 1) * lineHeightPx;
      };
      const measurer = this._createBlockMeasurer(cs, measurerWidthFor(curGeo));
      document.body.appendChild(measurer);

      const measureBlock = (block: HTMLElement): number =>
        this._measureBlockRunHeights(measurer, [block])[0] ?? 0;

      const measureRun = (blocks: HTMLElement[]): number[] =>
        this._measureBlockRunHeights(measurer, blocks);

      const pages: HTMLElement[][] = [[]];
      const pageGeos: PageGeometry[] = [curGeo];
      let curSection = 0;
      const pageSections: number[] = [0];
      const pageBands: (PageColumnBand | null)[] = [null];
      let currentHeight = 0;
      let columnBase = 0;
      let columnHeight = availableFor(curGeo, 0);
      let availableHeight = capacityFor(curGeo, 0);

      const footnotesForLayout = this._footnotes();
      const fnItemHById = new Map<string, number>();
      let fnSepH = 0;
      if (footnotesForLayout.length > 0) {
        const fnSepProbe = document.createElement('div');
        fnSepProbe.className = 'footnotes-separator';
        const fnItemProbes = footnotesForLayout.map((fn, i) => {
          const item = document.createElement('div');
          item.className = 'footnote-item footnote-entry';
          const num = document.createElement('span');
          num.className = 'footnote-item-number';
          num.textContent = String(i + 1);
          const content = document.createElement('div');
          content.className = 'footnote-item-content';
          content.innerHTML = fn.html || '<p></p>';
          item.append(num, content);
          return item;
        });
        const measuredFn = measureRun([fnSepProbe, ...fnItemProbes]);
        fnSepH = measuredFn[0] ?? 0;
        footnotesForLayout.forEach((fn, i) => fnItemHById.set(fn.id, measuredFn[i + 1] ?? 0));
      }

      const pageFnIds: string[][] = [[]];
      let fnIdsOnPage = new Set<string>();
      let fnReservePx = 0;

      const openPage = () => {
        pages.push([]);
        pageGeos.push(curGeo);
        pageSections.push(curSection);
        pageBands.push(null);
        pageFnIds.push([]);
        currentHeight = 0;
        columnBase = 0;
        columnHeight = availableFor(curGeo, pages.length - 1);
        availableHeight = capacityFor(curGeo, pages.length - 1);
        fnIdsOnPage = new Set<string>();
        fnReservePx = 0;
      };

      const fnProspectiveReserve = (ids: string[]): number => {
        if (ids.length === 0) return 0;
        let extra = 0;
        const seen = new Set(fnIdsOnPage);
        for (const id of ids) {
          if (!seen.has(id) && fnItemHById.has(id)) {
            extra += fnItemHById.get(id)!;
            seen.add(id);
          }
        }
        const sep = fnIdsOnPage.size === 0 && extra > 0 ? fnSepH : 0;
        return extra + sep;
      };

      const fnEffAvail = (extra: number): number =>
        availableHeight - columnCountFor(curGeo) * (fnReservePx + extra);

      const commitFootnotes = (frag: HTMLElement): void => {
        for (const id of this._footnoteIdsIn(frag)) {
          if (fnIdsOnPage.has(id) || !fnItemHById.has(id)) continue;
          if (fnIdsOnPage.size === 0) fnReservePx += fnSepH;
          fnReservePx += fnItemHById.get(id)!;
          fnIdsOnPage.add(id);
          pageFnIds[pages.length - 1].push(id);
        }
      };

      const suppressTopMarginHere = (blk: HTMLElement): boolean => {
        if (pages.length === 1) return false;
        const page = pages[pages.length - 1];
        if (page.some(b => this._isSectionBreakMarker(b))) return false;
        if (page.some(b => !this._isSectionBreakMarker(b))) return false;
        return this._isPageTopSpacingBlock(blk) && !this._hasPageBreakBeforeStyle(blk);
      };

      const placedHeights = new Map<HTMLElement, number>();
      const pullKeepNextChain = (): HTMLElement[] => {
        const page = pages[pages.length - 1];
        const pulled: HTMLElement[] = [];
        while (page.length > 1) {
          const last = page[page.length - 1];
          if (!this._keepsWithNext(last) || this._footnoteIdsIn(last).length > 0) break;
          page.pop();
          currentHeight -= placedHeights.get(last) ?? 0;
          placedHeights.delete(last);
          this._stripFmtPageTop(last);
          pulled.unshift(last);
        }
        return pulled;
      };

      const pushMeasured = (block: HTMLElement, h: number) => {
        let blk = block;
        let bh = h;
        let topCut = 0;
        const applyPageTop = () => {
          topCut = 0;
          if (!suppressTopMarginHere(blk)) return;
          topCut = this._blockMarginTopPx(blk, measurer);
          if (topCut > 0) bh = Math.max(0, bh - topCut);
        };
        applyPageTop();
        let blkExtra = fnProspectiveReserve(this._footnoteIdsIn(blk));
        while (currentHeight + bh > fnEffAvail(blkExtra)) {
          const pageHasContent = pages[pages.length - 1].length > 0;
          const parts = this._keepsLinesTogether(blk) ? null : this._splitBlockAtBudget(
            blk, fnEffAvail(blkExtra) - currentHeight + topCut, measurer, lineHeightPx);
          if (!parts) {
            if (!pageHasContent) break;
            const pulled = pullKeepNextChain();
            openPage();
            for (const pb of pulled) pushMeasured(pb, measureBlock(pb));
            blkExtra = fnProspectiveReserve(this._footnoteIdsIn(blk));
            applyPageTop();
            continue;
          }
          if (topCut > 0) parts[0].classList.add('fmt-page-top');
          commitFootnotes(parts[0]);
          pages[pages.length - 1].push(parts[0]);
          placedHeights.set(parts[0], Math.max(0, fnEffAvail(blkExtra) - currentHeight));
          openPage();
          blk = parts[1];
          bh = measureBlock(blk);
          topCut = 0;
          blkExtra = fnProspectiveReserve(this._footnoteIdsIn(blk));
        }
        if (topCut > 0) blk.classList.add('fmt-page-top');
        commitFootnotes(blk);
        pages[pages.length - 1].push(blk);
        placedHeights.set(blk, bh);
        currentHeight += bh;
      };

      const placeTable = (tableBlock: HTMLElement, runHeightHint: number | null) => {
          const marginBottomPx = parseFloat(getComputedStyle(tableBlock).marginBottom) || 0;
          const tableMargins = Math.max(0, this._measureTableVerticalMargins(
            tableBlock as HTMLTableElement, measurer) - marginBottomPx);
          const freshColumnBudget = Math.max(80, columnHeight - tableMargins);
          const usedInColumn = columnHeight > 0 ? (currentHeight - columnBase) % columnHeight : 0;
          let split = this._splitTableForPagination(
            tableBlock as HTMLTableElement,
            Math.max(80, columnHeight - usedInColumn - tableMargins),
            freshColumnBudget,
            measurer,
            lineHeightPx
          );
          const advanceColumnOrPage = () => {
            const nextColumnTop = columnBase
              + (Math.floor((currentHeight - columnBase) / columnHeight) + 1) * columnHeight;
            if (nextColumnTop < availableHeight) {
              currentHeight = nextColumnTop;
            } else {
              openPage();
            }
          };
          for (let i = 0; i < split.length; i++) {
            let frag = split[i];
            let h = i === 0 && split.length === 1 && runHeightHint !== null
              ? runHeightHint
              : measureBlock(frag);
            if (i > 0) {
              advanceColumnOrPage();
            } else {
              const used = columnHeight > 0 ? (currentHeight - columnBase) % columnHeight : 0;
              const remaining = columnHeight - used;
              if (h > remaining + 0.5 + marginBottomPx && pages[pages.length - 1].length > 0) {
                advanceColumnOrPage();
                if (split.length > 1) {
                  split = this._splitTableForPagination(
                    tableBlock as HTMLTableElement,
                    freshColumnBudget,
                    freshColumnBudget,
                    measurer,
                    lineHeightPx
                  );
                  frag = split[0];
                  h = measureBlock(frag);
                }
              }
            }
            pages[pages.length - 1].push(frag);
            commitFootnotes(frag);
            currentHeight += h;
          }
      };

      let bi = 0;
      while (bi < allBlocks.length) {
        const block = allBlocks[bi];
        if (this._isSectionBreakMarker(block)) {
          curGeo = this._parseSectionGeometry(block, curGeo);
          curSection++;
          if (pages[pages.length - 1].length === 0) {
            pages[pages.length - 1].push(block);
            pageGeos[pageGeos.length - 1] = curGeo;
            pageSections[pageSections.length - 1] = curSection;
            columnBase = 0;
            columnHeight = availableFor(curGeo, pages.length - 1);
            availableHeight = capacityFor(curGeo, pages.length - 1);
          } else {
            const newCols = columnCountFor(curGeo);
            const prevCols = columnCountFor(pageGeos[pageGeos.length - 1]);
            const remainingPx = columnHeight - (currentHeight - columnBase);
            if (
              newCols > 1 && prevCols === 1 && !pageBands[pageBands.length - 1] &&
              remainingPx >= 2 * lineHeightPx
            ) {
              pages[pages.length - 1].push(block);
              pageBands[pageBands.length - 1] = {
                start: pages[pages.length - 1].length,
                columns: curGeo.columns!,
                heightPx: remainingPx,
              };
              columnBase = currentHeight;
              columnHeight = remainingPx;
              availableHeight = currentHeight + newCols * remainingPx - (newCols - 1) * lineHeightPx;
            } else if (newCols !== prevCols || pageBands[pageBands.length - 1]) {
              openPage();
              pages[pages.length - 1].push(block);
            } else {
              pages[pages.length - 1].push(block);
            }
          }
          measurer.style.width = `${measurerWidthFor(curGeo)}px`;
          bi++;
          continue;
        }
        if (this._isPageBreakBlock(block)) {
          pages[pages.length - 1].push(block);
          openPage();
          bi++;
          continue;
        }
        if (this._hasPageBreakBeforeStyle(block)) {
          if (pages[pages.length - 1].length > 0 || currentHeight > 0) {
            openPage();
          }
          pushMeasured(block, measureBlock(block));
          bi++;
          continue;
        }
        if (this._isColumnBreakBlock(block)) {
          const nextColumnTop = columnBase
            + (Math.floor((currentHeight - columnBase) / columnHeight) + 1) * columnHeight;
          if (nextColumnTop >= availableHeight && pages[pages.length - 1].length > 0) {
            openPage();
          } else if (nextColumnTop < availableHeight) {
            currentHeight = nextColumnTop;
          }
          pages[pages.length - 1].push(block);
          bi++;
          continue;
        }
        let runEnd = bi;
        while (
          runEnd < allBlocks.length &&
          !this._isSectionBreakMarker(allBlocks[runEnd]) &&
          !this._isPageBreakBlock(allBlocks[runEnd]) &&
          !this._hasPageBreakBeforeStyle(allBlocks[runEnd]) &&
          !this._isColumnBreakBlock(allBlocks[runEnd])
        ) {
          runEnd++;
        }
        const run = allBlocks.slice(bi, runEnd);
        const heights = measureRun(run);
        for (let k = 0; k < run.length; k++) {
          if (run[k].tagName === 'TABLE') placeTable(run[k], heights[k]);
          else pushMeasured(run[k], heights[k]);
        }
        bi = runEnd;
      }

      const endnoteRegions: EndnotePageRegion[] = [];
      const endnotesForLayout = this._endnotes();
      let endnoteExtraPages = 0;
      if (endnotesForLayout.length > 0) {
        const sepProbe = document.createElement('div');
        sepProbe.className = 'endnotes-separator';
        const contSepProbe = document.createElement('div');
        contSepProbe.className = 'endnotes-separator endnotes-separator-continuation';
        const itemProbes = endnotesForLayout.map((en, i) => {
          const item = document.createElement('div');
          item.className = 'footnote-item endnote-entry';
          const num = document.createElement('span');
          num.className = 'footnote-item-number';
          num.textContent = this._formatNoteLabel(i + 1, this._endnoteNumberFormat(), true);
          const content = document.createElement('div');
          content.className = 'footnote-item-content';
          content.innerHTML = en.html || '<p></p>';
          item.append(num, content);
          return item;
        });
        const measuredEn = measureRun([sepProbe, contSepProbe, ...itemProbes]);
        const sepH = measuredEn[0] ?? 0;
        const contSepH = measuredEn[1] ?? 0;
        const itemHs = measuredEn.slice(2);

        const regionTopFor = (geo: PageGeometry, pageIdx: number, usedPx: number): number => {
          const headerBand = Math.max(this._bandCmFor(geo, 'header') * CSS_PX_PER_CM,
            pageIdx === 0 ? measuredBands.headerFirst : measuredBands.headerRest);
          return this._distanceCmFor(geo, 'header') * CSS_PX_PER_CM + headerBand + usedPx;
        };

        let enPageIdx = pages.length - 1;
        let enUsed = Math.min(currentHeight, columnHeight);
        let enAvail = columnHeight;
        let curRegion: EndnotePageRegion = {
          pageIndex: enPageIdx,
          topPx: regionTopFor(curGeo, enPageIdx, enUsed),
          ids: [],
          continuation: false,
        };
        let sepPending = sepH;

        const openEndnotePage = () => {
          if (curRegion.ids.length) endnoteRegions.push(curRegion);
          enPageIdx++;
          endnoteExtraPages++;
          pageGeos.push(curGeo);
          pageSections.push(curSection);
          enUsed = 0;
          enAvail = availableFor(curGeo, enPageIdx);
          curRegion = {
            pageIndex: enPageIdx,
            topPx: regionTopFor(curGeo, enPageIdx, 0),
            ids: [],
            continuation: true,
          };
          sepPending = contSepH;
        };

        for (let i = 0; i < endnotesForLayout.length; i++) {
          const needed = sepPending + (itemHs[i] ?? 0);
          if (enUsed + needed > enAvail && (curRegion.ids.length > 0 || !curRegion.continuation)) {
            openEndnotePage();
            i--;
            continue;
          }
          enUsed += needed;
          sepPending = 0;
          curRegion.ids.push(endnotesForLayout[i].id);
        }
        if (curRegion.ids.length) endnoteRegions.push(curRegion);
      }

      if (footnotesForLayout.length > 0) {
        const assigned = new Set<string>(pageFnIds.flat());
        const lastContentPage = pages.length - 1;
        for (const fn of footnotesForLayout) {
          if (!assigned.has(fn.id)) pageFnIds[lastContentPage].push(fn.id);
        }
      }

      const footnoteRegions: FootnotePageRegion[] = [];
      for (let pi = 0; pi < pageFnIds.length; pi++) {
        const ids = pageFnIds[pi];
        if (ids.length === 0) continue;
        const geo = pageGeos[pi] ?? curGeo;
        const footerBand = Math.max(this._bandCmFor(geo, 'footer') * CSS_PX_PER_CM,
          pi === 0 ? measuredBands.footerFirst : measuredBands.footerRest);
        const footerDist = this._distanceCmFor(geo, 'footer') * CSS_PX_PER_CM;
        footnoteRegions.push({
          pageIndex: pi,
          bottomPx: footerBand + footerDist,
          ids,
          continuation: false,
        });
      }

      measurer.remove();

      const newPageContents = pages.map((blocks, pi) => {
        const tmp = document.createElement('div');
        const band = pageBands[pi];
        const bandStart = band ? Math.min(band.start, blocks.length) : blocks.length;
        blocks.slice(0, bandStart).forEach(b => tmp.appendChild(b));
        if (band && blocks.length > bandStart) {
          const wrap = document.createElement('div');
          wrap.className = 'docx-col-band';
          const round2 = (v: number) => Math.round(v * 100) / 100;
          const gapPx = round2(Math.max(0, band.columns.spaceCm) * CSS_PX_PER_CM);
          wrap.setAttribute('style',
            `column-count:${band.columns.count};column-gap:${gapPx}px;` +
            (band.columns.separator ? 'column-rule:1px solid #bbb;' : '') +
            `height:${round2(band.heightPx)}px;`);
          blocks.slice(bandStart).forEach(b => wrap.appendChild(b));
          tmp.appendChild(wrap);
        }
        return tmp.innerHTML || '<p></p>';
      });
      for (let i = 0; i < endnoteExtraPages; i++) newPageContents.push('');

      this._endnoteLayout.set(endnoteRegions);
      this._footnoteLayout.set(footnoteRegions);
      this.pageGeometries.set(pageGeos);
      this.pageBodyHeights.set(pageGeos.map((g, i) => availableFor(g, i)));
      this.pageSectionIndexes.set(pageSections);
      const domAlreadyCorrect = newPageContents.length === this.pageContents().length
        && newPageContents.length === livePageContents.length
        && newPageContents.every((v, i) => v === livePageContents[i]);
      if (!domAlreadyCorrect) {
        this.pageContents.set(newPageContents);
        this._cdr.detectChanges();
        this._syncPageEditorDom(newPageContents);
        this._restoreGlobalCaret(caret);
      }
      this.calculatePages();
      this._scheduleAnchorBadgeRefresh();
    } finally {
      this._isRepaginating = false;
      this._scheduleFormattingMarksRender();
    }
  }

  private _fmtMarksRaf: number | null = null;

  private _scheduleFormattingMarksRender(): void {
    if (this._fmtMarksRaf !== null) cancelAnimationFrame(this._fmtMarksRaf);
    this._fmtMarksRaf = requestAnimationFrame(() => {
      this._fmtMarksRaf = null;
      this._renderFormattingMarksOverlay();
    });
  }

  private _renderFormattingMarksOverlay(): void {
    const host: HTMLElement = this._hostRef.nativeElement;
    host.querySelectorAll('.fmt-marks-layer').forEach((el: Element) => el.remove());
    this._stripFmtTrailingBr(host);
    if (!this.showFormattingMarks()) return;

    const pages = Array.from(host.querySelectorAll<HTMLElement>('.page'));
    let budget = 20000;

    const CONTAINERS = '.editor-content, .page-overflow-content, .header-display, .footer-display,'
      + ' .header-editor-content, .footer-editor-content, .footnote-item-content';

    for (const page of pages) {
      if (budget <= 0) break;
      const scale = this._pageVisualScale(page) || 1;
      const pageRect = page.getBoundingClientRect();

      const layer = document.createElement('div');
      layer.className = 'fmt-marks-layer';
      layer.setAttribute('contenteditable', 'false');
      layer.setAttribute('aria-hidden', 'true');

      const styleCache = new Map<Element, { color: string; fontSize: string }>();
      const styleOf = (el: Element | null) => {
        if (!el) return { color: '#444444', fontSize: '11pt' };
        let cached = styleCache.get(el);
        if (!cached) {
          const cs = getComputedStyle(el);
          cached = { color: cs.color, fontSize: cs.fontSize };
          styleCache.set(el, cached);
        }
        return cached;
      };

      const addMark = (rect: DOMRect, glyph: string, cls: string, src: Element | null) => {
        if (budget <= 0 || (rect.width === 0 && rect.height === 0)) return;
        const mark = document.createElement('span');
        mark.className = `fmt-mark ${cls}`;
        mark.textContent = glyph;
        const { color, fontSize } = styleOf(src);
        mark.style.left = `${(rect.left - pageRect.left) / scale}px`;
        mark.style.top = `${(rect.top - pageRect.top) / scale}px`;
        mark.style.width = `${Math.max(rect.width / scale, 4)}px`;
        mark.style.height = `${rect.height / scale}px`;
        mark.style.color = color;
        mark.style.fontSize = fontSize;
        layer.appendChild(mark);
        budget--;
      };

      const range = document.createRange();
      for (const container of Array.from(page.querySelectorAll<HTMLElement>(CONTAINERS))) {
        if (budget <= 0) break;

        const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT);
        for (let node = walker.nextNode(); node && budget > 0; node = walker.nextNode()) {
          const text = node.nodeValue ?? '';
          if (!/[ \u00A0\t\u00AD]/.test(text)) continue;
          const parent = node.parentElement;
          for (let i = 0; i < text.length && budget > 0; i++) {
            const ch = text[i];
            if (ch !== ' ' && ch !== '\u00A0' && ch !== '\t' && ch !== '\u00AD') continue;
            range.setStart(node, i);
            range.setEnd(node, i + 1);
            const rect = range.getBoundingClientRect();
            if (ch === ' ') addMark(rect, '·', 'fmt-space', parent);
            else if (ch === '\u00A0') addMark(rect, '°', 'fmt-nbsp', parent);
            else if (ch === '\u00AD') addMark(rect, '¬', 'fmt-shy', parent);
            else addMark(rect, '→', 'fmt-tab', parent);
          }
        }

        container.querySelectorAll('br').forEach(br => {
          if (budget <= 0) return;
          const rect = br.getBoundingClientRect();
          addMark(rect, '↵', 'fmt-br', br.parentElement);
          const block = this._trailingBrBlock(br);
          if (block) {
            block.classList.add('fmt-trailing-br');
            addMark(new DOMRect(rect.left + 9 * scale, rect.top, rect.width, rect.height), '¶', 'fmt-pilcrow', br.parentElement);
          }
        });

        container.querySelectorAll<HTMLTableCellElement>('td, th').forEach(cell => {
          if (budget <= 0 || cell.hasAttribute('data-grid-spacer')) return;
          range.selectNodeContents(cell);
          const rects = range.getClientRects();
          const last = rects.length ? rects[rects.length - 1] : null;
          const cr = cell.getBoundingClientRect();
          const x = last ? last.right : cr.left + 3;
          const top = last ? last.top : cr.top + 3;
          const h = last ? last.height : Math.min(cr.height - 6, 18);
          addMark(new DOMRect(x, top, 10, h), '¤', 'fmt-cell', cell);
        });
        container.querySelectorAll<HTMLTableRowElement>('tr').forEach(row => {
          if (budget <= 0) return;
          const r = row.getBoundingClientRect();
          addMark(new DOMRect(r.right + 2, r.top, 12, r.height), '¤', 'fmt-cell', row);
        });

        container.querySelectorAll<HTMLElement>(
          '.editor-image-wrapper[data-pos-mode], .docx-textbox, .docx-shape',
        ).forEach(obj => {
          if (budget <= 0) return;
          if (getComputedStyle(obj).position !== 'absolute') return;
          const r = obj.getBoundingClientRect();
          addMark(new DOMRect(r.left - 16, r.top, 14, 16), '⚓', 'fmt-anchor', obj);
        });
      }

      page.appendChild(layer);
    }
  }

  private _trailingBrBlock(br: HTMLElement): HTMLElement | null {
    const block = br.closest<HTMLElement>('p, h1, h2, h3, h4, h5, h6, li, blockquote, pre');
    if (!block) return null;
    const tail = document.createRange();
    tail.selectNodeContents(block);
    tail.setStartAfter(br);
    if (tail.toString().replace(/[\s\u200B\uFEFF]/g, '').length > 0) return null;
    const frag = tail.cloneContents();
    return frag.querySelector(
      'br, img, svg, table, sup, .docx-tab-seg, .docx-tab-leader, .docx-textbox, .docx-shape, .editor-image-wrapper',
    ) ? null : block;
  }

  private _stripFmtTrailingBr(root: ParentNode): void {
    root.querySelectorAll('.fmt-trailing-br').forEach(el => {
      el.classList.remove('fmt-trailing-br');
      if (!el.getAttribute('class')) el.removeAttribute('class');
    });
  }

  private _createBlockMeasurer(cs: CSSStyleDeclaration, widthPx: number): HTMLElement {
    const measurer = document.createElement('div');
    measurer.className = this.documentParagraphSpacingSum()
      ? 'editor-content para-spacing-sum'
      : 'editor-content';
    const lineHeight = this.documentDefaultLineHeight() || cs.lineHeight;
    const vars = [
      ['--doc-par-margin', this.documentDefaultParagraphSpacing()],
      ['--doc-par-margin-top', this.documentDefaultParagraphSpacingBefore()],
      ['--w-line-tw', this.documentDefaultLineTw()],
      ['--w-line-single', this.documentDefaultLineSingle()],
    ].filter(([, v]) => v != null && v !== '').map(([k, v]) => `${k}:${v};`).join('');
    measurer.style.cssText =
      `position:absolute;left:-99999px;top:0;width:${widthPx}px;padding:0;border:0;` +
      `font-family:${cs.fontFamily};font-size:${cs.fontSize};line-height:${lineHeight};visibility:hidden;` +
      vars;
    return measurer;
  }

  private _measureBlockRunHeights(measurer: HTMLElement, blocks: HTMLElement[]): number[] {
    let key = measurer.style.cssText + '|';
    for (const b of blocks) key += b.outerHTML;
    const cached = this._blockRunMeasureCache.get(key);
    if (cached !== undefined) return cached;
    measurer.innerHTML = '';
    for (const b of blocks) measurer.appendChild(b.cloneNode(true));
    const sentinel = document.createElement('div');
    sentinel.style.cssText = 'margin:0;padding:0;border:0;height:0;clear:both;';
    measurer.appendChild(sentinel);
    const kids = Array.from(measurer.children) as HTMLElement[];
    const tops = kids.map(k => k.getBoundingClientRect().top);
    const base = measurer.getBoundingClientRect().top;
    const out: number[] = [];
    for (let i = 0; i < blocks.length; i++) {
      let h = tops[i + 1] - tops[i];
      if (i === 0) h += Math.max(0, tops[0] - base);
      out.push(Math.max(0, h));
    }
    if (this._blockRunMeasureCache.size >= 500) this._blockRunMeasureCache.clear();
    this._blockRunMeasureCache.set(key, out);
    return out;
  }

  private _blockMarginTopPx(block: HTMLElement, measurer: HTMLElement): number {
    const key = measurer.style.cssText + '|mt|' + block.outerHTML;
    const cached = this._blockRunMeasureCache.get(key);
    if (cached !== undefined) return cached[0] ?? 0;
    measurer.innerHTML = '';
    const clone = block.cloneNode(true) as HTMLElement;
    measurer.appendChild(clone);
    const mt = parseFloat(getComputedStyle(clone).marginTop) || 0;
    if (this._blockRunMeasureCache.size >= 500) this._blockRunMeasureCache.clear();
    this._blockRunMeasureCache.set(key, [mt]);
    return mt;
  }

  private _isPageTopSpacingBlock(el: HTMLElement): boolean {
    return /^(P|H[1-6]|BLOCKQUOTE|PRE)$/.test(el.tagName);
  }

  private _stripFmtPageTop(root: Element | ParentNode): void {
    const strip = (el: Element) => {
      el.classList.remove('fmt-page-top');
      if (!el.getAttribute('class')) el.removeAttribute('class');
    };
    if ((root as Element).classList?.contains('fmt-page-top')) strip(root as Element);
    root.querySelectorAll('.fmt-page-top').forEach(strip);
  }

  private _splitBlockAtBudget(
    block: HTMLElement,
    budgetPx: number,
    measurer: HTMLElement,
    lineHeightPx: number
  ): [HTMLElement, HTMLElement] | null {
    if (budgetPx < lineHeightPx) return null;
    if (block.tagName === 'UL' || block.tagName === 'OL') {
      return this._splitListBetweenItems(block, budgetPx, measurer);
    }
    if (block.tagName === 'DIV' && block.classList.contains('sdt-block')) {
      return this._splitSdtBetweenChildren(block, budgetPx, measurer, lineHeightPx);
    }
    if (block.querySelector('.docx-tab-seg, .docx-tab-leader, .docx-textbox, [data-pos-mode], [data-docx-xml]')) return null;
    if (block.tagName !== 'P' && !/^H[1-6]$/.test(block.tagName)) return null;

    measurer.innerHTML = '';
    measurer.appendChild(block);
    try {
      const limitY = measurer.getBoundingClientRect().top + budgetPx;
      const split = this._findLineSplitPoint(block, limitY);
      if (!split) return null;

      const head = document.createRange();
      head.selectNodeContents(block);
      head.setEnd(split.node, split.offset);
      const headFrag = head.cloneContents();
      const hasContent = (frag: DocumentFragment) =>
        (frag.textContent ?? '').trim().length > 0 || !!frag.querySelector('img, .editor-image-wrapper');
      if (!hasContent(headFrag)) return null;

      const tailRange = document.createRange();
      tailRange.selectNodeContents(block);
      tailRange.setStart(split.node, split.offset);
      const tail = tailRange.extractContents();
      if (!hasContent(tail)) {
        block.appendChild(tail);
        return null;
      }

      const cont = block.cloneNode(false) as HTMLElement;
      cont.appendChild(tail);
      cont.setAttribute('data-split-para', 'cont');
      cont.style.marginTop = '0';
      return [block, cont];
    } finally {
      if (block.parentNode === measurer) measurer.removeChild(block);
    }
  }

  private _measureListItemBottoms(list: HTMLElement, measurer: HTMLElement): number[] {
    measurer.innerHTML = '';
    measurer.appendChild(list);
    const base = measurer.getBoundingClientRect().top;
    const bottoms = (Array.from(list.children) as HTMLElement[])
      .map(li => li.getBoundingClientRect().bottom - base);
    measurer.removeChild(list);
    return bottoms;
  }

  private _splitListBetweenItems(
    list: HTMLElement,
    budgetPx: number,
    measurer: HTMLElement
  ): [HTMLElement, HTMLElement] | null {
    const items = Array.from(list.children) as HTMLElement[];
    if (items.length < 2) return null;

    const bottoms = this._measureListItemBottoms(list, measurer);
    const EPS = 0.5;
    let lastFitting = -1;
    for (let i = 0; i < items.length; i++) {
      if ((bottoms[i] ?? 0) > 0 && bottoms[i] <= budgetPx + EPS) lastFitting = i;
      else if ((bottoms[i] ?? 0) > budgetPx + EPS) break;
    }
    if (lastFitting < 0 || lastFitting >= items.length - 1) return null;

    const cont = list.cloneNode(false) as HTMLElement;
    for (let i = lastFitting + 1; i < items.length; i++) cont.appendChild(items[i]);
    cont.setAttribute('data-split-para', 'cont');
    cont.style.marginTop = '0';
    if (list.tagName === 'OL' && !list.hasAttribute('data-num-id')) {
      const start = parseInt(list.getAttribute('start') ?? '1', 10) || 1;
      cont.setAttribute('start', String(start + lastFitting + 1));
    }
    return [list, cont];
  }

  private _measureChildEdges(container: HTMLElement, measurer: HTMLElement): { top: number; bottom: number }[] {
    measurer.innerHTML = '';
    measurer.appendChild(container);
    const base = measurer.getBoundingClientRect().top;
    const edges = (Array.from(container.children) as HTMLElement[]).map(ch => {
      const r = ch.getBoundingClientRect();
      return { top: r.top - base, bottom: r.bottom - base };
    });
    measurer.removeChild(container);
    return edges;
  }

  private _splitSdtBetweenChildren(
    sdt: HTMLElement,
    budgetPx: number,
    measurer: HTMLElement,
    lineHeightPx: number
  ): [HTMLElement, HTMLElement] | null {
    const children = Array.from(sdt.children) as HTMLElement[];
    if (children.length === 0) return null;

    const edges = this._measureChildEdges(sdt, measurer);
    const EPS = 0.5;
    let lastFitting = -1;
    for (let i = 0; i < children.length; i++) {
      const bottom = edges[i]?.bottom ?? 0;
      if (bottom > 0 && bottom <= budgetPx + EPS) lastFitting = i;
      else if (bottom > budgetPx + EPS) break;
    }
    if (lastFitting >= children.length - 1) return null;

    const boundary = children[lastFitting + 1];
    const boundaryBudget = budgetPx - (edges[lastFitting + 1]?.top ?? 0);
    let boundaryParts: [HTMLElement, HTMLElement] | null = null;
    if (boundaryBudget >= lineHeightPx) {
      boundaryParts = this._splitBlockAtBudget(boundary, boundaryBudget, measurer, lineHeightPx);
    }
    if (lastFitting < 0 && !boundaryParts) return null;

    const cont = sdt.cloneNode(false) as HTMLElement;
    cont.setAttribute('data-split-para', 'cont');
    cont.style.marginTop = '0';
    if (boundaryParts) {
      cont.appendChild(boundaryParts[1]);
      if (boundary.parentNode !== sdt) sdt.appendChild(boundaryParts[0]);
    } else {
      cont.appendChild(boundary);
    }
    for (let i = lastFitting + 2; i < children.length; i++) cont.appendChild(children[i]);
    return [sdt, cont];
  }

  private _findLineSplitPoint(block: HTMLElement, limitY: number): { node: Node; offset: number } | null {
    const EPS = 0.5;
    const probe = document.createRange();
    if (typeof probe.getClientRects !== 'function') return null;
    const walker = document.createTreeWalker(block, NodeFilter.SHOW_TEXT | NodeFilter.SHOW_ELEMENT, {
      acceptNode: (n: Node) => {
        if (n.nodeType === Node.TEXT_NODE) return NodeFilter.FILTER_ACCEPT;
        const el = n as HTMLElement;
        if (el.tagName === 'IMG' || el.classList.contains('editor-image-wrapper')) {
          return NodeFilter.FILTER_ACCEPT;
        }
        return NodeFilter.FILTER_SKIP;
      },
    });

    let node: Node | null;
    while ((node = walker.nextNode())) {
      if (node.nodeType === Node.ELEMENT_NODE) {
        const rect = (node as HTMLElement).getBoundingClientRect();
        if (rect.height > 0 && rect.bottom > limitY + EPS) {
          const parent = node.parentNode;
          if (!parent) return null;
          return { node: parent, offset: Array.prototype.indexOf.call(parent.childNodes, node) };
        }
        continue;
      }
      const text = node as Text;
      const len = text.data.length;
      if (len === 0) continue;
      probe.setStart(text, 0);
      probe.setEnd(text, len);
      let nodeBottom = 0;
      const nodeRects = probe.getClientRects();
      for (let i = 0; i < nodeRects.length; i++) nodeBottom = Math.max(nodeBottom, nodeRects[i].bottom);
      if (nodeRects.length === 0 || nodeBottom <= limitY + EPS) continue;

      for (let i = 0; i < len; i++) {
        probe.setStart(text, i);
        probe.setEnd(text, i + 1);
        const rects = probe.getClientRects();
        let bottom = 0;
        for (let k = 0; k < rects.length; k++) bottom = Math.max(bottom, rects[k].bottom);
        if (bottom > limitY + EPS) return { node: text, offset: i };
      }
    }
    return null;
  }

  private _tableDirectRows(table: HTMLTableElement): HTMLTableRowElement[] {
    return Array.from(table.rows);
  }

  private _tableSourceRows(table: HTMLTableElement): HTMLTableRowElement[] {
    return this._tableDirectRows(table).filter(tr => !tr.hasAttribute('data-repeated-header'));
  }

  private _repeatingHeaderRows(rows: HTMLTableRowElement[]): HTMLTableRowElement[] {
    const out: HTMLTableRowElement[] = [];
    for (const r of rows) {
      if (r.getAttribute('data-tbl-header') !== '1' || r.hasAttribute('data-repeated-header')) break;
      out.push(r);
    }
    return out;
  }

  private _rowCanSplit(table: HTMLTableElement, row: HTMLTableRowElement): boolean {
    if (row.getAttribute('data-cant-split') === '1') return false;
    if (row.getAttribute('data-cant-split-eff') === '1') return false;
    if (row.getAttribute('data-tbl-header') === '1') return false;
    if (row.getAttribute('data-row-hrule') === 'exact') return false;
    const rows = this._tableDirectRows(table);
    const idx = rows.indexOf(row);
    if (idx < 0) return true;
    for (let r = 0; r < rows.length; r++) {
      for (const cell of Array.from(rows[r].cells)) {
        if (cell.rowSpan > 1 && idx >= r && idx <= r + cell.rowSpan - 1) return false;
      }
    }
    return true;
  }

  private _measureRowLayout(
    table: HTMLTableElement,
    row: HTMLTableRowElement,
    measurer: HTMLElement
  ): { contentWidthPx: number; blockTops: number[]; blockBottoms: number[]; bottomExtraPx?: number }[] {
    const t = table.cloneNode(false) as HTMLTableElement;
    const colgroup = table.querySelector(':scope > colgroup');
    if (colgroup) t.appendChild(colgroup.cloneNode(true));
    const tbody = document.createElement('tbody');
    const rowClone = row.cloneNode(true) as HTMLTableRowElement;
    rowClone.style.height = '';
    tbody.appendChild(rowClone);
    t.appendChild(tbody);
    measurer.innerHTML = '';
    measurer.appendChild(t);
    const rowTop = rowClone.getBoundingClientRect().top;
    const out = Array.from(rowClone.cells).map(cell => {
      const cs = getComputedStyle(cell);
      const contentWidthPx = Math.max(
        2, cell.clientWidth - (parseFloat(cs.paddingLeft) || 0) - (parseFloat(cs.paddingRight) || 0));
      const blocks = Array.from(cell.children) as HTMLElement[];
      return {
        contentWidthPx,
        blockTops: blocks.map(b => b.getBoundingClientRect().top - rowTop),
        blockBottoms: blocks.map(b => b.getBoundingClientRect().bottom - rowTop),
        bottomExtraPx: (parseFloat(cs.paddingBottom) || 0) + (parseFloat(cs.borderBottomWidth) || 0),
      };
    });
    measurer.innerHTML = '';
    return out;
  }

  private _tableMeasureCache = new Map<string, number>();

  private _blockRunMeasureCache = new Map<string, number[]>();

  private _measureTableRowsHeight(
    table: HTMLTableElement,
    subset: HTMLTableRowElement[],
    measurer: HTMLElement
  ): number {
    const t = table.cloneNode(false) as HTMLTableElement;
    const colgroup = table.querySelector(':scope > colgroup');
    if (colgroup) t.appendChild(colgroup.cloneNode(true));
    let key = measurer.style.cssText + '|' + t.outerHTML;
    for (const r of subset) key += r.outerHTML;
    const cached = this._tableMeasureCache.get(key);
    if (cached !== undefined) return cached;
    const tbody = document.createElement('tbody');
    subset.forEach(r => tbody.appendChild(r.cloneNode(true)));
    t.appendChild(tbody);
    measurer.innerHTML = '';
    measurer.appendChild(t);
    const h = t.getBoundingClientRect().height;
    if (this._tableMeasureCache.size >= 2000) this._tableMeasureCache.clear();
    this._tableMeasureCache.set(key, h);
    return h;
  }

  private _measureTableVerticalMargins(table: HTMLTableElement, measurer: HTMLElement): number {
    const shell = table.cloneNode(false) as HTMLTableElement;
    const key = 'tblMargins|' + measurer.style.cssText + '|' + shell.outerHTML;
    const cached = this._tableMeasureCache.get(key);
    if (cached !== undefined) return cached;
    measurer.innerHTML = '';
    measurer.appendChild(shell);
    const cs = getComputedStyle(shell);
    const m = (parseFloat(cs.marginTop) || 0) + (parseFloat(cs.marginBottom) || 0);
    measurer.innerHTML = '';
    if (this._tableMeasureCache.size >= 2000) this._tableMeasureCache.clear();
    this._tableMeasureCache.set(key, m);
    return m;
  }

  private _splitRowSeq = 0;

  private _splitRowAtBudget(
    table: HTMLTableElement,
    row: HTMLTableRowElement,
    budgetPx: number,
    measurer: HTMLElement,
    lineHeightPx: number
  ): [HTMLTableRowElement, HTMLTableRowElement] | null {
    if (budgetPx < 2 * lineHeightPx) return null;
    const cells = Array.from(row.cells);
    if (cells.length === 0) return null;
    for (const cell of cells) {
      for (const n of Array.from(cell.childNodes)) {
        if (n.nodeType === Node.TEXT_NODE && (n.textContent ?? '').trim()) return null;
      }
    }

    const layout = this._measureRowLayout(table, row, measurer);
    if (layout.length !== cells.length) return null;

    const EPS = 0.5;
    const cs = getComputedStyle(measurer);
    const contentBudgetPx = budgetPx - Math.max(0, ...layout.map(l => l.bottomExtraPx ?? 0));
    if (contentBudgetPx < lineHeightPx) return null;
    const headCells: HTMLElement[][] = [];
    const tailCells: HTMLElement[][] = [];
    let anyHeadContent = false;
    let anyTailContent = false;

    for (let i = 0; i < cells.length; i++) {
      const blocks = Array.from(cells[i].children) as HTMLElement[];
      const info = layout[i];
      const head: HTMLElement[] = [];
      const tail: HTMLElement[] = [];
      for (let j = 0; j < blocks.length; j++) {
        const top = info.blockTops[j] ?? 0;
        const bottom = info.blockBottoms[j] ?? 0;
        const blk = blocks[j].cloneNode(true) as HTMLElement;
        if (bottom <= contentBudgetPx + EPS) {
          head.push(blk);
        } else if (top >= contentBudgetPx - EPS) {
          tail.push(blk);
        } else if (this._keepsLinesTogether(blk)) {
          tail.push(blk);
        } else {
          const cellMeasurer = this._createBlockMeasurer(cs, info.contentWidthPx);
          document.body.appendChild(cellMeasurer);
          let parts: [HTMLElement, HTMLElement] | null;
          try {
            parts = this._splitBlockAtBudget(blk, contentBudgetPx - top, cellMeasurer, lineHeightPx);
          } finally {
            cellMeasurer.remove();
          }
          if (parts) {
            head.push(parts[0]);
            tail.push(parts[1]);
          } else {
            tail.push(blk);
          }
        }
      }
      headCells.push(head);
      tailCells.push(tail);
      if (head.length) anyHeadContent = true;
      if (tail.length) anyTailContent = true;
    }

    if (!anyHeadContent || !anyTailContent) return null;

    const splitRowId = row.getAttribute('data-split-row-id') ?? `sr-${++this._splitRowSeq}`;
    const buildFragment = (cellBlocks: HTMLElement[][], isHead: boolean): HTMLTableRowElement => {
      const tr = row.cloneNode(false) as HTMLTableRowElement;
      tr.style.height = '';
      if (!tr.getAttribute('style')) tr.removeAttribute('style');
      if (!isHead) {
        tr.removeAttribute('data-row-height-tw');
        tr.removeAttribute('data-row-hrule');
        tr.setAttribute('data-split-row', 'cont');
      }
      tr.setAttribute('data-split-row-id', splitRowId);
      for (let i = 0; i < cells.length; i++) {
        const td = cells[i].cloneNode(false) as HTMLTableCellElement;
        td.style.height = '';
        cellBlocks[i].forEach(b => td.appendChild(b));
        tr.appendChild(td);
      }
      return tr;
    };
    return [buildFragment(headCells, true), buildFragment(tailCells, false)];
  }

  private _splitTableForPagination(
    table: HTMLTableElement,
    firstAvail: number,
    fullAvail: number,
    measurer: HTMLElement,
    lineHeightPx = 16
  ): HTMLTableElement[] {
    const rows = this._tableDirectRows(table);
    if (rows.length === 0) return [table];

    const measureRows = (subset: HTMLTableRowElement[]): number =>
      this._measureTableRowsHeight(table, subset, measurer);

    const headerRows = this._repeatingHeaderRows(rows);
    const headerH = headerRows.length > 0 && headerRows.length < rows.length ? measureRows(headerRows) : 0;
    const contAvail = Math.max(80, fullAvail - headerH);

    const chunks: HTMLTableRowElement[][] = [];
    let bucket: HTMLTableRowElement[] = [];
    let avail = firstAvail;
    for (let ri = 0; ri < rows.length; ri++) {
      let row = rows[ri];
      for (let guard = 0; guard < 100; guard++) {
        const tentative = [...bucket, row];
        const h = measureRows(tentative);
        if (h <= avail) {
          bucket = tentative;
          break;
        }
        const bucketH = bucket.length > 0 ? measureRows(bucket) : 0;
        const parts = this._rowCanSplit(table, row)
          ? this._splitRowAtBudget(table, row, avail - bucketH - 3, measurer, lineHeightPx)
          : null;
        if (parts) {
          chunks.push([...bucket, parts[0]]);
          bucket = [];
          avail = contAvail;
          row = parts[1];
          continue;
        }
        if (bucket.length > 0) {
          chunks.push(bucket);
          bucket = [];
          avail = contAvail;
          continue;
        }
        bucket = [row];
        break;
      }
    }
    if (bucket.length > 0) chunks.push(bucket);

    const existingId = table.getAttribute('data-split-table-id');
    const splitId = chunks.length > 1 ? (existingId ?? `st-${++this._splitTableSeq}`) : existingId;
    const colgroup = table.querySelector(':scope > colgroup');

    return chunks.map((subset, ci) => {
      const t = table.cloneNode(false) as HTMLTableElement;
      if (colgroup) t.appendChild(colgroup.cloneNode(true));
      const tbody = document.createElement('tbody');
      if (ci > 0 && headerH > 0) {
        for (const hr of headerRows) {
          const copy = hr.cloneNode(true) as HTMLTableRowElement;
          copy.setAttribute('data-repeated-header', '1');
          copy.setAttribute('contenteditable', 'false');
          copy.removeAttribute('data-split-row-id');
          tbody.appendChild(copy);
        }
      }
      subset.forEach(r => tbody.appendChild(r.cloneNode(true)));
      t.appendChild(tbody);
      if (splitId) t.setAttribute('data-split-table-id', splitId);
      return t;
    });
  }

  private _splitTableSeq = 0;

  private _staleGeometryRerun = false;

  private _keepsWithNext(el: HTMLElement): boolean {
    return /(?:page-)?break-after\s*:\s*avoid\b/i.test(el.getAttribute?.('style') ?? '');
  }

  private _keepsLinesTogether(el: HTMLElement): boolean {
    return /(?:page-)?break-inside\s*:\s*avoid\b/i.test(el.getAttribute?.('style') ?? '');
  }

  private _hasPageBreakBeforeStyle(el: HTMLElement): boolean {
    const style = el.getAttribute?.('style') ?? '';
    return /(?:page-)?break-before\s*:\s*(always|page)\b/i.test(style);
  }

  private _isPageBreakBlock(el: HTMLElement): boolean {
    if (!el || el.nodeType !== 1) return false;
    if (el.classList?.contains('page-break')) return true;
    const nested = el.querySelector?.('.page-break');
    return !!nested && (el.textContent ?? '').trim().length === 0;
  }

  private _isColumnBreakBlock(el: HTMLElement): boolean {
    return !!el && el.nodeType === 1 && !!el.classList?.contains('docx-column-break');
  }

  private _mergeSplitTables(html: string): string {
    if (!html.includes('data-split-table-id') && !html.includes('data-split-row-id')) return html;

    const tmp = document.createElement('div');
    tmp.innerHTML = html;

    const handled = new Set<Element>();
    tmp.querySelectorAll('table[data-split-table-id]').forEach(el => {
      const first = el as HTMLTableElement;
      if (handled.has(first)) return;
      const id = first.getAttribute('data-split-table-id');
      const targetBody = first.querySelector('tbody') ?? first;

      let next = first.nextElementSibling;
      while (next && next.tagName === 'TABLE' && next.getAttribute('data-split-table-id') === id) {
        handled.add(next);
        this._tableSourceRows(next as HTMLTableElement).forEach(tr => targetBody.appendChild(tr));
        const toRemove = next;
        next = next.nextElementSibling;
        toRemove.remove();
      }
      first.removeAttribute('data-split-table-id');
    });

    tmp.querySelectorAll('table').forEach(t => this._mergeSplitRowsIn(t as HTMLTableElement));

    return tmp.innerHTML;
  }

  private _tryMoveCaretAcrossPages(dir: 'down' | 'up'): boolean {
    const refs = this.pageEditorRefs?.toArray() ?? [];
    if (refs.length < 2) return false;
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0 || !sel.isCollapsed || !sel.anchorNode) return false;

    const node = sel.anchorNode;
    const curIdx = refs.findIndex(r => r.nativeElement === node || r.nativeElement.contains(node));
    if (curIdx < 0) return false;
    const cur = refs[curIdx].nativeElement;

    if (dir === 'down') {
      if (curIdx >= refs.length - 1 || !this._isCaretOnEdgeLine(cur, 'bottom')) return false;
      this._placeCaretAtEditorEdge(refs[curIdx + 1].nativeElement, 'start', curIdx + 1);
      return true;
    }
    if (curIdx <= 0 || !this._isCaretOnEdgeLine(cur, 'top')) return false;
    this._placeCaretAtEditorEdge(refs[curIdx - 1].nativeElement, 'end', curIdx - 1);
    return true;
  }

  private _tryDeletePageBreakBackwards(): boolean {
    const refs = this.pageEditorRefs?.toArray() ?? [];
    if (refs.length < 2) return false;
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0 || !sel.isCollapsed || !sel.anchorNode) return false;

    const idx = refs.findIndex(r => r.nativeElement.contains(sel.anchorNode!) || r.nativeElement === sel.anchorNode);
    if (idx <= 0) return false;
    if (!this._isCaretAtEditorStart(refs[idx].nativeElement)) return false;

    if (!this._removeTrailingPageBreak(refs[idx - 1].nativeElement)) return false;

    this._isDirty = true;
    this._schedulePaginate('backspace-pagebreak');
    this._schedulePersist();
    this.updateState();
    return true;
  }

  private _isCaretAtEditorStart(editor: HTMLElement): boolean {
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0 || !sel.isCollapsed) return false;
    const r = sel.getRangeAt(0);
    const probe = document.createRange();
    probe.selectNodeContents(editor);
    probe.setEnd(r.startContainer, r.startOffset);
    return probe.toString().length === 0;
  }

  private _removeTrailingPageBreak(editor: HTMLElement): boolean {
    let last = editor.lastChild;
    while (last && last.nodeType === Node.TEXT_NODE && (last.textContent ?? '').trim() === '') {
      const prev = last.previousSibling;
      last.parentNode?.removeChild(last);
      last = prev;
    }
    if (last && last.nodeType === Node.ELEMENT_NODE && this._isPageBreakBlock(last as HTMLElement)) {
      editor.removeChild(last);
      return true;
    }
    return false;
  }

  private _tryMergeAcrossPageBackwards(): boolean {
    const refs = this.pageEditorRefs?.toArray() ?? [];
    if (refs.length < 2) return false;
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0 || !sel.isCollapsed || !sel.anchorNode) return false;

    const idx = refs.findIndex(r => r.nativeElement.contains(sel.anchorNode!) || r.nativeElement === sel.anchorNode);
    if (idx <= 0) return false;
    const curEd = refs[idx].nativeElement;
    const prevEd = refs[idx - 1].nativeElement;
    if (!this._isCaretAtEditorStart(curEd)) return false;

    const firstBlock = curEd.firstElementChild as HTMLElement | null;
    const lastBlock = prevEd.lastElementChild as HTMLElement | null;
    if (!firstBlock || !lastBlock) return false;

    const MERGEABLE = ['P', 'DIV', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'LI'];
    const isEmptyTextBlock = firstBlock.tagName !== 'TABLE'
      && (firstBlock.textContent ?? '').trim() === ''
      && !firstBlock.querySelector('img, table');
    const canMerge = MERGEABLE.includes(firstBlock.tagName) && MERGEABLE.includes(lastBlock.tagName);

    const range = document.createRange();
    if (isEmptyTextBlock) {
      firstBlock.remove();
      range.selectNodeContents(lastBlock);
      range.collapse(false);
    } else if (canMerge) {
      const joinNode = lastBlock.lastChild;
      while (firstBlock.firstChild) lastBlock.appendChild(firstBlock.firstChild);
      firstBlock.remove();
      if (joinNode && joinNode.parentNode === lastBlock) {
        range.setStartAfter(joinNode);
        range.collapse(true);
      } else {
        range.selectNodeContents(lastBlock);
        range.collapse(true);
      }
    } else {
      this._placeCaretAtEditorEdge(prevEd, 'end', idx - 1);
      return true;
    }

    prevEd.focus();
    sel.removeAllRanges();
    sel.addRange(range);
    if (refs[idx - 1]) this.editorContent = refs[idx - 1];
    this.activePageIndex.set(idx - 1);

    this._isDirty = true;
    this._schedulePaginate('backspace-merge');
    this._schedulePersist();
    this.updateState();
    return true;
  }

  private _isCaretOnEdgeLine(editor: HTMLElement, edge: 'top' | 'bottom'): boolean {
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0) return false;
    const caret = sel.getRangeAt(0).cloneRange();
    caret.collapse(true);
    const cr = caret.getClientRects()[0] ?? caret.getBoundingClientRect();

    const bound = document.createRange();
    bound.selectNodeContents(editor);
    bound.collapse(edge === 'top');
    const rects = bound.getClientRects();
    const br = rects.length ? rects[edge === 'top' ? 0 : rects.length - 1] : bound.getBoundingClientRect();

    return edge === 'bottom' ? cr.bottom >= br.bottom - 2 : cr.top <= br.top + 2;
  }

  private _placeCaretAtEditorEdge(editor: HTMLElement, edge: 'start' | 'end', index: number): void {
    editor.focus();
    const range = document.createRange();
    range.selectNodeContents(editor);
    range.collapse(edge === 'start');
    const sel = window.getSelection();
    sel?.removeAllRanges();
    sel?.addRange(range);

    const refs = this.pageEditorRefs?.toArray() ?? [];
    if (refs[index]) this.editorContent = refs[index];
    this.activePageIndex.set(index);
  }

  private _flattenTopBlocks(el: HTMLElement, depth = 0): HTMLElement[] {
    const kids = (Array.from(el.children) as HTMLElement[]).flatMap(c =>
      c.classList.contains('docx-col-band') ? (Array.from(c.children) as HTMLElement[]) : [c]
    );
    if (depth >= 3) return kids;
    if (kids.length === 1) {
      const c = kids[0];
      if (
        (c.tagName === 'DIV' || c.tagName === 'SECTION' || c.tagName === 'ARTICLE') &&
        !c.classList.contains('page-break') &&
        !c.classList.contains('docx-section-break') &&
        !c.classList.contains('docx-column-break')
      ) {
        return this._flattenTopBlocks(c, depth + 1);
      }
    }
    return kids;
  }

  private _saveGlobalCaret(refs: ElementRef<HTMLDivElement>[]): { block: number; offset: number } | null {
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0) return null;
    return this._globalCaretFromRange(sel.getRangeAt(0), refs);
  }

  private _globalCaretFromRange(range: Range, refs: ElementRef<HTMLDivElement>[]): { block: number; offset: number } | null {
    const all: HTMLElement[] = [];
    let hit = -1;
    let offset = 0;
    for (let i = 0; i < refs.length; i++) {
      const editor = refs[i].nativeElement;
      const blocks = this._flattenTopBlocks(editor);
      if (hit === -1 && editor.contains(range.endContainer)) {
        let found = false;
        for (let b = 0; b < blocks.length; b++) {
          if (blocks[b] === range.endContainer || blocks[b].contains(range.endContainer)) {
            const pre = range.cloneRange();
            pre.selectNodeContents(blocks[b]);
            pre.setEnd(range.endContainer, range.endOffset);
            hit = all.length + b;
            offset = pre.toString().length;
            found = true;
            break;
          }
        }
        if (!found) {
          hit = all.length;
          offset = 0;
        }
      }
      all.push(...blocks);
    }
    if (hit === -1) return null;
    if (hit >= all.length) {
      return all.length === 0 ? { block: 0, offset: 0 } : null;
    }
    let gi = hit;
    while (gi > 0 && this._isContinuationBlock(all[gi - 1], all[gi])) {
      gi--;
      offset += (all[gi].textContent ?? '').length;
    }
    let logical = 0;
    for (let k = 0; k < gi; k++) {
      if (k === 0 || !this._isContinuationBlock(all[k - 1], all[k])) logical++;
    }
    return { block: logical, offset };
  }

  private _isContinuationBlock(prev: HTMLElement | null, el: HTMLElement): boolean {
    if (el.getAttribute('data-split-para') === 'cont') return true;
    const tid = el.getAttribute('data-split-table-id');
    return !!tid && !!prev && prev.getAttribute('data-split-table-id') === tid;
  }

  private _syncPageEditorDom(pageContents: string[]): void {
    const refs = this.pageEditorRefs?.toArray() ?? [];
    const n = Math.min(refs.length, pageContents.length);
    for (let i = 0; i < n; i++) {
      const el = refs[i].nativeElement;
      const desired = pageContents[i];
      if (el.innerHTML !== desired) {
        el.innerHTML = desired;
        this.wrapExistingImages(el);
      }
    }
  }

  private _restoreGlobalCaret(caret: { block: number; offset: number } | null): void {
    if (!caret) return;
    const refs = this.pageEditorRefs?.toArray() ?? [];
    const all: { el: HTMLElement; page: number }[] = [];
    for (let i = 0; i < refs.length; i++) {
      for (const el of this._flattenTopBlocks(refs[i].nativeElement)) {
        all.push({ el, page: i });
      }
    }
    let logical = -1;
    for (let g = 0; g < all.length; g++) {
      if (g > 0 && this._isContinuationBlock(all[g - 1].el, all[g].el)) continue;
      logical++;
      if (logical !== caret.block) continue;
      let idx = g;
      let off = caret.offset;
      while (
        idx + 1 < all.length &&
        this._isContinuationBlock(all[idx].el, all[idx + 1].el) &&
        off > (all[idx].el.textContent ?? '').length
      ) {
        off -= (all[idx].el.textContent ?? '').length;
        idx++;
      }
      const target = all[idx].el;
      refs[all[idx].page].nativeElement.focus();
      this._placeCaretAtTextOffset(target, off);
      target.scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
      this.editorContent = refs[all[idx].page];
      this.activePageIndex.set(all[idx].page);
      return;
    }
    for (let i = refs.length - 1; i >= 0; i--) {
      const blocks = this._flattenTopBlocks(refs[i].nativeElement);
      if (blocks.length === 0) continue;
      const target = blocks[blocks.length - 1];
      refs[i].nativeElement.focus();
      this._placeCaretAtTextOffset(target, Number.MAX_SAFE_INTEGER);
      target.scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
      this.editorContent = refs[i];
      this.activePageIndex.set(i);
      return;
    }
  }

  private _placeCaretAtTextOffset(block: HTMLElement, offset: number): void {
    const sel = window.getSelection();
    if (!sel) return;
    const range = document.createRange();
    const walker = document.createTreeWalker(block, NodeFilter.SHOW_TEXT);
    let node: Node | null;
    let r = offset;
    let lastNode: Node | null = null;
    while ((node = walker.nextNode())) {
      const nl = node.textContent?.length ?? 0;
      if (r <= nl) {
        const atom = (node.parentElement ?? null)?.closest?.('[contenteditable="false"]');
        if (atom && block.contains(atom) && atom !== block) {
          if (r <= 0) range.setStartBefore(atom);
          else range.setStartAfter(atom);
        } else {
          range.setStart(node, r);
        }
        range.collapse(true);
        sel.removeAllRanges();
        sel.addRange(range);
        return;
      }
      r -= nl;
      lastNode = node;
    }
    if (lastNode) {
      const atom = (lastNode.parentElement ?? null)?.closest?.('[contenteditable="false"]');
      if (atom && block.contains(atom) && atom !== block) {
        range.setStartAfter(atom);
      } else {
        range.setStart(lastNode, lastNode.textContent?.length ?? 0);
      }
    } else {
      range.setStart(block, 0);
    }
    range.collapse(true);
    sel.removeAllRanges();
    sel.addRange(range);
  }

  getContent(): string {
    const refs = this.pageEditorRefs?.toArray() ?? [];
    if (refs.length === 0) {
      const fallback = this.editorContent?.nativeElement;
      return fallback ? this._wrapWithDocumentContainer(this._serializeSingleEditor(fallback)) : '';
    }

    const parts = refs.map(r => this._serializeSingleEditor(r.nativeElement));
    const merged = parts.filter(p => p && p.trim().length > 0).join('');
    return this._wrapWithDocumentContainer(this._mergeSplitParagraphs(this._mergeSplitTables(merged)));
  }

  private _mergeSplitParagraphs(html: string): string {
    if (!html.includes('data-split-para')) return html;
    const tmp = document.createElement('div');
    tmp.innerHTML = html;
    this._mergeSplitParasWithin(tmp);
    return tmp.innerHTML;
  }

  private _mergeSplitParasWithin(root: ParentNode): void {
    root.querySelectorAll('[data-split-para="cont"]').forEach(cont => {
      const el = cont as HTMLElement;
      const prev = el.previousElementSibling;
      const sdtSafe = !el.classList.contains('sdt-block') || !!prev?.classList.contains('sdt-block');
      if (prev && prev.tagName === el.tagName && sdtSafe) {
        while (el.firstChild) prev.appendChild(el.firstChild);
        el.remove();
      } else {
        el.removeAttribute('data-split-para');
        el.style.marginTop = '';
        if (!el.getAttribute('style')) el.removeAttribute('style');
      }
    });
  }

  private _mergeSplitRowsIn(table: HTMLTableElement, keepIds = false): void {
    const rows = Array.from(table.querySelectorAll('tr')) as HTMLTableRowElement[];
    for (const tr of rows) {
      if (tr.getAttribute('data-split-row') !== 'cont') continue;
      const id = tr.getAttribute('data-split-row-id');
      const prev = tr.previousElementSibling as HTMLTableRowElement | null;
      if (prev && prev.tagName === 'TR' && id && prev.getAttribute('data-split-row-id') === id) {
        const n = Math.min(prev.cells.length, tr.cells.length);
        for (let i = 0; i < n; i++) {
          const target = prev.cells[i];
          const source = tr.cells[i];
          while (source.firstChild) target.appendChild(source.firstChild);
          this._mergeSplitParasWithin(target);
        }
        tr.remove();
      } else {
        tr.removeAttribute('data-split-row');
        tr.removeAttribute('data-split-row-id');
      }
    }
    if (!keepIds) {
      table.querySelectorAll('tr[data-split-row-id]').forEach(tr => {
        tr.removeAttribute('data-split-row-id');
        tr.removeAttribute('data-split-row');
      });
    }
  }

  private _serializeSingleEditor(editor: HTMLDivElement): string {
    const clone = editor.cloneNode(true) as HTMLDivElement;

    clone.querySelectorAll('.docx-col-band').forEach(band => {
      const parent = band.parentNode;
      if (!parent) return;
      while (band.firstChild) parent.insertBefore(band.firstChild, band);
      band.remove();
    });

    clone.querySelectorAll('.docx-textbox').forEach(tb => {
      tb.classList.remove('tb-selected', 'tb-dragging', 'tb-edge');
    });
    clone.querySelectorAll('.anchor-badge').forEach(b => b.remove());

    stripListLabelAttributes(clone);

    this._stripFmtTrailingBr(clone);
    this._stripFmtPageTop(clone);

    this._unwrapImageWrappers(clone);

    return clone.innerHTML;
  }

  private _unwrapImageWrappers(root: HTMLElement): void {
    root.querySelectorAll('.shape-resize-handle').forEach(h => h.remove());
    root.querySelectorAll('.shape-selected').forEach(s => (s as HTMLElement).classList.remove('shape-selected'));
    root.querySelectorAll('.editor-image-wrapper').forEach(wrapperEl => {
      const wrapper = wrapperEl as HTMLElement;
      wrapper.classList.remove('selected');
      wrapper.removeAttribute('contenteditable');
      wrapper.removeAttribute('draggable');

      wrapper.querySelectorAll('.image-resize-handle').forEach(h => h.remove());

      const img = wrapper.querySelector('img');
      if (img) {
        const imgEl = img as HTMLImageElement;
        const wrapperWidth = wrapper.style.width;
        if (wrapperWidth && wrapperWidth !== 'auto') {
          imgEl.style.width = wrapperWidth;
        }
        imgEl.style.maxWidth = '100%';
        if (!imgEl.style.height || imgEl.style.height === 'auto') {
          imgEl.style.height = 'auto';
        }
        imgEl.removeAttribute('draggable');
        imgEl.style.removeProperty('position');
        imgEl.style.removeProperty('left');
        imgEl.style.removeProperty('top');
        imgEl.style.removeProperty('z-index');
        wrapper.replaceWith(imgEl);
      }
    });
  }

  setContent(html: string): void {
    this._tableMeasureCache.clear();
    this._blockRunMeasureCache.clear();
    const unwrapped = this._captureDocumentDefaults(html);
    const pages = this._splitHtmlIntoPages(unwrapped ?? html ?? '<p></p>');
    this.pageContents.set(pages);
    this._content.set(pages.join(''));
    this._isDirty = false;
    setTimeout(() => {
      this.wrapExistingImages();
      this.refreshListLabels();
      const merged = this.getContent();
      this.lastSavedContent = merged;
      this.undoStack = [merged];
      this.redoStack = [];
      this.updateState();
      this._schedulePaginate('setContent');
      this._syncInitialFormatting();
      this._repaginateAfterResources();
    }, 0);
  }

  private wrapExistingImages(container?: HTMLElement | null): void {
    const editor = container ?? this.editorContent?.nativeElement;
    if (!editor) return;

    const images = editor.querySelectorAll('img');
    images.forEach((img: HTMLImageElement) => {
      if (img.parentElement?.classList.contains('editor-image-wrapper')) {
        return;
      }

      const imageId = `img-${Date.now()}-${Math.floor(Math.random() * 10000)}`;

      const wrapper = document.createElement('span');
      wrapper.className = 'editor-image-wrapper';
      wrapper.setAttribute('data-image-id', imageId);
      wrapper.setAttribute('contenteditable', 'false');
      wrapper.setAttribute('draggable', 'true');

      wrapper.style.maxWidth = '100%';
      img.style.maxWidth = '100%';
      img.setAttribute('draggable', 'false');

      img.parentNode?.insertBefore(wrapper, img);
      wrapper.appendChild(img);

      const posMode = img.dataset['posMode'];
      if (posMode === 'front' || posMode === 'behind') {
        const xPx = Math.round((Number(img.getAttribute('data-x-emu') ?? 0)) / 9525);
        const yPx = Math.round((Number(img.getAttribute('data-y-emu') ?? 0)) / 9525);
        const bandGeo = this._bandGeoForElement(img);
        if (bandGeo) {
          const { leftPx, topPx } = contractToBand(xPx, yPx, bandGeo);
          this.applyFloatingPosition(wrapper, img, posMode,
            Math.round(leftPx), Math.round(topPx), { xPx, yPx });
        } else {
          this.applyFloatingPosition(wrapper, img, posMode, xPx, yPx);
        }
      } else if (posMode === 'square') {
        wrapper.dataset['posMode'] = 'square';
        wrapper.style.float = 'left';
        wrapper.style.margin = '0 12px 8px 0';
      } else if (posMode === 'topBottom') {
        wrapper.dataset['posMode'] = 'topBottom';
        wrapper.style.display = 'block';
        wrapper.style.clear = 'both';
        wrapper.style.margin = '8px auto';
      }

      const bw = parseInt(img.dataset['borderWidth'] ?? '0', 10);
      if (bw > 0) {
        const bc = img.dataset['borderColor'] || '#000000';
        const bs = (img.dataset['borderStyle'] as 'solid' | 'dashed' | 'dotted') ?? 'solid';
        img.style.border = `${bw}px ${bs} ${bc}`;
      }
      const cl = parseFloat(img.dataset['cropL'] ?? '0') || 0;
      const cr = parseFloat(img.dataset['cropR'] ?? '0') || 0;
      const ct = parseFloat(img.dataset['cropT'] ?? '0') || 0;
      const cb = parseFloat(img.dataset['cropB'] ?? '0') || 0;
      if (cl > 0 || cr > 0 || ct > 0 || cb > 0) {
        img.style.clipPath = `inset(${ct}% ${cr}% ${cb}% ${cl}%)`;
      }

      ['right', 'bottom', 'corner'].forEach(type => {
        const h = document.createElement('span');
        h.className = `image-resize-handle resize-handle-${type}`;
        h.title = type === 'right' ? 'Zmień szerokość' : type === 'bottom' ? 'Zmień wysokość' : 'Zmień rozmiar';
        wrapper.appendChild(h);
      });
    });
  }

  markAsSaved(): void {
    this.lastSavedContent = this.getContent();
    this._isDirty = false;
    this.updateState();
  }

  getCurrentFormatting(): any {
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) {
      return null;
    }

    let element = selection.anchorNode as HTMLElement;
    if (element?.nodeType === Node.TEXT_NODE) {
      element = element.parentElement!;
    }

    if (!element) return null;

    const computedStyle = window.getComputedStyle(element);

    return {
      bold: document.queryCommandState('bold'),
      italic: document.queryCommandState('italic'),
      underline: document.queryCommandState('underline'),
      strikethrough: document.queryCommandState('strikeThrough'),
      subscript: document.queryCommandState('subscript'),
      superscript: document.queryCommandState('superscript'),
      fontFamily: computedStyle.fontFamily.replace(/['"]/g, '').split(',')[0].trim(),
      fontSize: Math.round(parseFloat(computedStyle.fontSize) * 0.75),
      textColor: this.rgbToHex(computedStyle.color),
      backgroundColor: computedStyle.backgroundColor === 'rgba(0, 0, 0, 0)' ? '' : this.rgbToHex(computedStyle.backgroundColor)
    };
  }

  applyFormatting(format: any): void {
    if (!format) return;

    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0 || selection.isCollapsed) {
      return;
    }

    const setMark = (state: boolean, queryName: string, command: EditorCommand) => {
      if (document.queryCommandState(queryName) !== state) this.executeCommand(command);
    };
    setMark(!!format.bold, 'bold', 'bold');
    setMark(!!format.italic, 'italic', 'italic');
    setMark(!!format.underline, 'underline', 'underline');
    setMark(!!format.strikethrough, 'strikeThrough', 'strikethrough');
    setMark(!!format.subscript, 'subscript', 'subscript');
    setMark(!!format.superscript, 'superscript', 'superscript');

    if (format.fontFamily) this.setFontFamily(format.fontFamily);
    if (format.fontSize) this.setFontSize(format.fontSize);
    if (format.textColor) this.setTextColor(format.textColor);
    if (format.backgroundColor) this.setBackgroundColor(format.backgroundColor);

    const html = this.editorContent?.nativeElement?.innerHTML || '';
    this._content.set(html);
    this.contentChange.emit(html);
    this.updateFormattingState();
  }

  private rgbToHex(rgb: string): string {
    if (rgb.startsWith('#')) return rgb;
    if (rgb === 'transparent' || rgb === 'rgba(0, 0, 0, 0)') return '';
    
    const match = rgb.match(/^rgba?\((\d+),\s*(\d+),\s*(\d+)/);
    if (!match) return '#000000';
    
    const r = parseInt(match[1]).toString(16).padStart(2, '0');
    const g = parseInt(match[2]).toString(16).padStart(2, '0');
    const b = parseInt(match[3]).toString(16).padStart(2, '0');
    
    return `#${r}${g}${b}`;
  }

  startEditingHeader(event?: MouseEvent): void {
    if (this.readOnly) return;
    if (this.editingSection() === 'header') return;
    const clickX = event?.clientX;
    const clickY = event?.clientY;
    this.editingHfPageIndex.set(this._pageIndexFromEvent(event));
    this.editingSection.set('header');
    this.editingSectionChange.emit('header');
    setTimeout(() => {
      const el = this.headerContentEl?.nativeElement;
      if (el) {
        el.innerHTML = this._editableHeaderHtml(this.editingHfPageIndex());
        this.wrapExistingImages(el);
        this._positionBandShapesForEditing(el, this.editingHfPageIndex(), 'header');
        this.attachEditorListeners(el);
        el.focus();
        this.placeCaretAtPoint(el, clickX, clickY);
        this.observeActiveSectionGeometry();
      }
    }, 0);
  }

  startEditingFooter(event?: MouseEvent): void {
    if (this.readOnly) return;
    if (this.editingSection() === 'footer') return;
    const clickX = event?.clientX;
    const clickY = event?.clientY;
    this.editingHfPageIndex.set(this._pageIndexFromEvent(event));
    this.editingSection.set('footer');
    this.editingSectionChange.emit('footer');
    setTimeout(() => {
      const el = this.footerContentEl?.nativeElement;
      if (el) {
        el.innerHTML = this._editableFooterHtml(this.editingHfPageIndex());
        this.wrapExistingImages(el);
        this._positionBandShapesForEditing(el, this.editingHfPageIndex(), 'footer');
        this.attachEditorListeners(el);
        el.focus();
        this.placeCaretAtPoint(el, clickX, clickY);
        this.observeActiveSectionGeometry();
      }
    }, 0);
  }

  private _pageIndexFromEvent(event?: MouseEvent): number {
    const page = (event?.target as HTMLElement | null)?.closest?.('.page') as HTMLElement | null;
    const n = Number(page?.getAttribute('data-page-number') ?? '1');
    return Number.isFinite(n) && n >= 1 ? n - 1 : 0;
  }

  private _editableHeaderHtml(pageIndex: number): string {
    return this._resolveHfVariant(pageIndex, 'header').html;
  }

  private _editableFooterHtml(pageIndex: number): string {
    return this._resolveHfVariant(pageIndex, 'footer').html;
  }

  private _updateSectionEntry(sectionIndex: number, kind: 'header' | 'footer', variant: 'default' | 'first' | 'even', html: string): void {
    const updated = this._sectionHF().map(e => {
      if (e.sectionIndex !== sectionIndex) return e;
      const current = kind === 'header' ? e.header : e.footer;
      const next: HeaderFooterContent = { ...(current ?? { html: '', height: 1.27 }) };
      if (variant === 'first') next.firstPageHtml = html;
      else if (variant === 'even') next.evenHtml = html;
      else next.html = html;
      return kind === 'header' ? { ...e, header: next } : { ...e, footer: next };
    });
    this._sectionHF.set(updated);
    this.sectionHeadersFootersChange.emit(updated);
  }

  private placeCaretAtPoint(el: HTMLElement, x?: number, y?: number): void {
    if (typeof x === 'number' && typeof y === 'number') {
      const range = this.getRangeFromPoint(x, y);
      if (range && el.contains(range.startContainer)) {
        const sel = window.getSelection();
        sel?.removeAllRanges();
        sel?.addRange(range);
        return;
      }
    }
    this.placeCaretAtEnd(el);
  }

  private placeCaretAtEnd(el: HTMLElement): void {
    const range = document.createRange();
    range.selectNodeContents(el);
    range.collapse(false);
    const sel = window.getSelection();
    sel?.removeAllRanges();
    sel?.addRange(range);
  }

  stopEditingHeaderFooter(): void {
    if (this.editingSection() !== 'body') {
      this.editingSection.set('body');
      this.editingSectionChange.emit('body');
      this.stopObservingSectionGeometry();
    }
  }

  onHeaderBlur(): void {
    const content = this.headerContentEl?.nativeElement?.innerHTML || '';
    this._applyEditedHeaderHtml(content);
    this.emitHeaderFooterChanges();
  }

  private _applyEditedHeaderHtml(content: string): void {
    this._applyEditedHfHtml(content, 'header');
  }

  private _applyEditedFooterHtml(content: string): void {
    this._applyEditedHfHtml(content, 'footer');
  }

  private _applyEditedHfHtml(content: string, kind: 'header' | 'footer'): void {
    content = this._cleanBandHtml(content);
    const pageIndex = this.editingHfPageIndex();
    const { variant, ownerEntry } = this._resolveHfVariant(pageIndex, kind);
    if (ownerEntry) {
      this._updateSectionEntry(ownerEntry.sectionIndex, kind, variant, content);
      return;
    }
    if (variant === 'first') {
      (kind === 'header' ? this._headerFirstPageHtml : this._footerFirstPageHtml).set(content);
    } else if (variant === 'even') {
      (kind === 'header' ? this._headerEvenHtml : this._footerEvenHtml).set(content);
    } else {
      (kind === 'header' ? this._headerHtml : this._footerHtml).set(content);
    }
  }

  private _cleanBandHtml(html: string): string {
    if (!html || (!html.includes('editor-image-wrapper') && !html.includes('data-band-orig-left')
        && !html.includes('shape-resize-handle') && !html.includes('shape-selected')
        && !html.includes('fmt-trailing-br'))) {
      return html;
    }
    const tmp = document.createElement('div');
    tmp.innerHTML = html;
    this._unwrapImageWrappers(tmp);
    this._stripFmtTrailingBr(tmp);
    tmp.querySelectorAll<HTMLElement>('[data-band-orig-left]').forEach(el => {
      el.style.left = el.getAttribute('data-band-orig-left') ?? el.style.left;
      el.style.top = el.getAttribute('data-band-orig-top') ?? el.style.top;
      el.removeAttribute('data-band-orig-left');
      el.removeAttribute('data-band-orig-top');
    });
    return tmp.innerHTML;
  }

  private _positionBandShapesForEditing(el: HTMLElement, pageIndex: number, band: 'header' | 'footer'): void {
    const floated = Array.from(
      el.querySelectorAll<HTMLElement>('.docx-shape, .docx-textbox'),
    ).filter(s => s.style.position === 'absolute' && !s.hasAttribute('data-band-orig-left'));
    if (floated.length === 0) return;
    const geo = this._bandGeoFor(pageIndex, band);
    floated.forEach(shape => {
      shape.setAttribute('data-band-orig-left', shape.style.left);
      shape.setAttribute('data-band-orig-top', shape.style.top);
      const { leftPx, topPx } = contractToBand(
        parseFloat(shape.style.left) || 0, parseFloat(shape.style.top) || 0, geo);
      shape.style.left = `${Math.round(leftPx)}px`;
      shape.style.top = `${Math.round(topPx)}px`;
    });
  }

  onHeaderInput(event: Event): void {
    const content = (event.target as HTMLDivElement).innerHTML;
    this._applyEditedHeaderHtml(content);
    this.invalidateHeaderFooterCache();
    this.emitHeaderFooterChanges();
  }

  onFooterBlur(): void {
    const content = this.footerContentEl?.nativeElement?.innerHTML || '';
    this._applyEditedFooterHtml(content);
    this.emitHeaderFooterChanges();
  }

  onFooterInput(event: Event): void {
    const content = (event.target as HTMLDivElement).innerHTML;
    this._applyEditedFooterHtml(content);
    this.invalidateHeaderFooterCache();
    this.emitHeaderFooterChanges();
  }

  private _isSectionFirstPage(pageIndex: number): boolean {
    if (pageIndex === 0) return true;
    const sections = this.pageSectionIndexes();
    const s = sections[pageIndex];
    return s !== undefined && s !== sections[pageIndex - 1];
  }

  private _bandContent(entry: SectionHeaderFooter, kind: 'header' | 'footer'): HeaderFooterContent | undefined {
    return kind === 'header' ? entry.header : entry.footer;
  }

  private _resolveHfVariant(pageIndex: number, kind: 'header' | 'footer'): {
    variant: 'default' | 'first' | 'even';
    ownerEntry: SectionHeaderFooter | null;
    html: string;
  } {
    const pageSection = this.pageSectionIndexes()[pageIndex] ?? 0;
    const candidates = this._sectionHF()
      .filter(e => e.sectionIndex <= pageSection && this._bandContent(e, kind))
      .sort((a, b) => b.sectionIndex - a.sectionIndex);

    const ownContent = candidates.length && candidates[0].sectionIndex === pageSection
      ? this._bandContent(candidates[0], kind)!
      : null;
    const differentFirstPage = pageSection === 0
      ? (kind === 'header' ? this._headerDifferentFirstPage() : this._footerDifferentFirstPage())
      : ownContent?.differentFirstPage === true;

    const nearestContent = candidates.length ? this._bandContent(candidates[0], kind)! : null;
    const differentOddEven = nearestContent
      ? nearestContent.differentOddEven === true
      : (kind === 'header' ? this._headerDifferentOddEven() : this._footerDifferentOddEven());

    const variant: 'default' | 'first' | 'even' =
      differentFirstPage && this._isSectionFirstPage(pageIndex) ? 'first'
        : differentOddEven && (pageIndex + 1) % 2 === 0 ? 'even'
          : 'default';

    for (const entry of candidates) {
      const c = this._bandContent(entry, kind)!;
      const value = variant === 'first' ? (c.firstPageHtml ?? null)
        : variant === 'even' ? (c.evenHtml ?? null)
          : (c.html || null);
      if (value !== null) return { variant, ownerEntry: entry, html: value };
    }

    const base = variant === 'first'
      ? (kind === 'header' ? this._headerFirstPageHtml() : this._footerFirstPageHtml())
      : variant === 'even'
        ? (kind === 'header' ? this._headerEvenHtml() : this._footerEvenHtml())
        : (kind === 'header' ? this._headerHtml() : this._footerHtml());
    return { variant, ownerEntry: null, html: base };
  }

  private _computeHeaderContent(pageIndex: number): string {
    return this._substitutePageFields(this._resolveHfVariant(pageIndex, 'header').html, pageIndex);
  }

  private _substitutePageFields(html: string, pageIndex: number): string {
    return html
      .replace(/\{page\}/gi, String(pageIndex + 1))
      .replace(/\{pages\}/gi, String(this.pageContents().length));
  }

  getHeaderContent(pageIndex: number): string {
    const contents = this.headerContents();
    return contents[pageIndex] ?? this._headerHtml();
  }

  private _computeFooterContent(pageIndex: number): string {
    let content = this._resolveHfVariant(pageIndex, 'footer').html;
    content = content.replace(/\{page\}/gi, String(pageIndex + 1));
    content = content.replace(/\{pages\}/gi, String(this.pageContents().length));
    return content;
  }

  getFooterContent(pageIndex: number): string {
    const contents = this.footerContents();
    return contents[pageIndex] ?? this._footerHtml();
  }

  getContentAreaHeight(): number {
    const pageHeight = this.baseGeometry().heightCm * CSS_PX_PER_CM;
    const headerHeightPx = this._headerHeight() * CSS_PX_PER_CM;
    const footerHeightPx = this._footerHeight() * CSS_PX_PER_CM;
    return pageHeight - headerHeightPx - footerHeightPx;
  }

  setHeaderHeight(heightCm: number): void {
    this._headerHeight.set(Math.max(0.5, Math.min(5, heightCm)));
    this.headerChange.emit({
      html: this._headerHtml(),
      height: this._headerHeight()
    });
  }

  setFooterHeight(heightCm: number): void {
    this._footerHeight.set(Math.max(0.5, Math.min(5, heightCm)));
    this.footerChange.emit({
      html: this._footerHtml(),
      height: this._footerHeight()
    });
  }

  getFullDocumentContent(): { body: string; header: HeaderFooterContent; footer: HeaderFooterContent } {
    return {
      body: this.editorContent?.nativeElement?.innerHTML || '',
      header: {
        html: this._headerHtml(),
        height: this._headerHeight(),
        differentFirstPage: this._headerDifferentFirstPage(),
        firstPageHtml: this._headerFirstPageHtml()
      },
      footer: {
        html: this._footerHtml(),
        height: this._footerHeight(),
        differentFirstPage: this._footerDifferentFirstPage(),
        firstPageHtml: this._footerFirstPageHtml()
      }
    };
  }

  onHeaderFooterToolbarMouseDown(event: MouseEvent): void {
    const target = event.target as HTMLElement;
    if (target.tagName !== 'INPUT' && target.tagName !== 'SELECT' && target.tagName !== 'TEXTAREA') {
      event.preventDefault();
    }
  }

  toggleHeaderOptionsMenu(event: Event): void {
    event.stopPropagation();
    this.showHeaderOptionsMenu.update(v => !v);
    this.showFooterOptionsMenu.set(false);
    
    if (this.showHeaderOptionsMenu()) {
      setTimeout(() => {
        const closeHandler = () => {
          this.showHeaderOptionsMenu.set(false);
          document.removeEventListener('click', closeHandler);
        };
        document.addEventListener('click', closeHandler);
      }, 0);
    }
  }

  toggleFooterOptionsMenu(event: Event): void {
    event.stopPropagation();
    this.showFooterOptionsMenu.update(v => !v);
    this.showHeaderOptionsMenu.set(false);
    
    if (this.showFooterOptionsMenu()) {
      setTimeout(() => {
        const closeHandler = () => {
          this.showFooterOptionsMenu.set(false);
          document.removeEventListener('click', closeHandler);
        };
        document.addEventListener('click', closeHandler);
      }, 0);
    }
  }

  toggleDifferentFirstPage(): void {
    const next = !this.differentFirstPage();
    this._headerDifferentFirstPage.set(next);
    this._footerDifferentFirstPage.set(next);
    this.invalidateHeaderFooterCache();
    this.emitHeaderFooterChanges();
  }

  openHeaderFormatDialog(): void {
    this.showHeaderOptionsMenu.set(false);
    this.openHeaderFooterFormatDialog();
  }

  openFooterFormatDialog(): void {
    this.showFooterOptionsMenu.set(false);
    this.openHeaderFooterFormatDialog();
  }

  openHeaderFooterFormatDialog(): void {
    this.openHeaderFooterSettings.emit({
      headerMargin: this._headerHeight(),
      footerMargin: this._footerHeight(),
      differentFirstPage: this.differentFirstPage(),
      differentOddEven: this.differentOddEven()
    });
  }

  applyHeaderFooterSettings(settings: {
    headerMargin: number;
    footerMargin: number;
    differentFirstPage: boolean;
    differentOddEven: boolean;
  }): void {
    this._headerHeight.set(settings.headerMargin);
    this._footerHeight.set(settings.footerMargin);
    this._headerDifferentFirstPage.set(settings.differentFirstPage);
    this._footerDifferentFirstPage.set(settings.differentFirstPage);
    this._headerDifferentOddEven.set(settings.differentOddEven);
    this._footerDifferentOddEven.set(settings.differentOddEven);
    this.invalidateHeaderFooterCache();
    this.emitHeaderFooterChanges();
  }

  insertImageIntoActive(): void {
    this.showHeaderOptionsMenu.set(false);
    this.showFooterOptionsMenu.set(false);

    const editor = this.getActiveEditor();
    if (editor) {
      editor.focus();
      this.saveSelection();
    }

    const input = document.createElement('input');
    input.type = 'file';
    input.accept = 'image/*';
    input.onchange = (e) => {
      const file = (e.target as HTMLInputElement).files?.[0];
      if (!file) return;
      const reader = new FileReader();
      reader.onload = (ev) => {
        const base64 = ev.target?.result as string;
        if (!base64) return;
        const editor2 = this.getActiveEditor();
        if (editor2) {
          editor2.focus();
          this.restoreSelection();
        }
        this.insertImage(base64, file.name);
      };
      reader.readAsDataURL(file);
    };
    input.click();
  }

  insertPageNumbers(): void {
    this.showHeaderOptionsMenu.set(false);
    const el = this.headerContentEl?.nativeElement;
    if (!el) return;
    el.focus();
    document.execCommand('insertHTML', false, this._pageNumberHtml(el));
    this.onHeaderInput({ target: el } as any);
  }

  insertPageNumbersFooter(): void {
    this.showFooterOptionsMenu.set(false);
    const el = this.footerContentEl?.nativeElement;
    if (!el) return;
    el.focus();
    document.execCommand('insertHTML', false, this._pageNumberHtml(el));
    this.onFooterInput({ target: el } as any);
  }

  private _pageNumberHtml(editor: HTMLElement): string {
    const size = this._inlineFieldFontSize(editor);
    const style = size ? ` style="font-size:${size};"` : '';
    return `<span class="page-number"${style}>{page}</span>`;
  }

  private _inlineFieldFontSize(editor: HTMLElement): string | null {
    const span = editor.querySelector('span[style*="font-size"]') as HTMLElement | null;
    if (span?.style.fontSize) return span.style.fontSize;
    const cs = getComputedStyle(span ?? editor).fontSize;
    return cs && cs !== '0px' ? cs : null;
  }

  removeHeader(): void {
    this.showHeaderOptionsMenu.set(false);
    this._headerHtml.set('');
    this._headerFirstPageHtml.set('');
    if (this.headerContentEl?.nativeElement) {
      this.headerContentEl.nativeElement.innerHTML = '';
    }
    this.emitHeaderFooterChanges();
    this.stopEditingHeaderFooter();
  }

  removeFooter(): void {
    this.showFooterOptionsMenu.set(false);
    this._footerHtml.set('');
    this._footerFirstPageHtml.set('');
    if (this.footerContentEl?.nativeElement) {
      this.footerContentEl.nativeElement.innerHTML = '';
    }
    this.emitHeaderFooterChanges();
    this.stopEditingHeaderFooter();
  }

  private emitHeaderFooterChanges(): void {
    this.headerChange.emit({
      html: this._headerHtml(),
      height: this._headerHeight(),
      differentFirstPage: this._headerDifferentFirstPage(),
      firstPageHtml: this._headerFirstPageHtml(),
      differentOddEven: this._headerDifferentOddEven(),
      oddHtml: this._headerOddHtml(),
      evenHtml: this._headerEvenHtml()
    });
    this.footerChange.emit({
      html: this._footerHtml(),
      height: this._footerHeight(),
      differentFirstPage: this._footerDifferentFirstPage(),
      firstPageHtml: this._footerFirstPageHtml(),
      differentOddEven: this._footerDifferentOddEven(),
      oddHtml: this._footerOddHtml(),
      evenHtml: this._footerEvenHtml()
    });
  }

  selectAllContent(): void {
    const sel = window.getSelection();
    if (!sel) return;

    const refs = this.pageEditorRefs?.toArray() ?? [];
    const editors = refs.length > 0
      ? refs.map(r => r.nativeElement)
      : (this.editorContent?.nativeElement ? [this.editorContent.nativeElement] : []);
    if (editors.length === 0) return;

    const first = editors[0];
    const last = editors[editors.length - 1];
    const range = document.createRange();
    range.setStart(first, 0);
    range.setEnd(last, last.childNodes.length);

    sel.removeAllRanges();
    sel.addRange(range);
  }

  getPageLayout(): { top: number; height: number }[] {
    const refs = this.pageEditorRefs?.toArray() ?? [];
    const pages = refs
      .map(r => r.nativeElement.closest('.page') as HTMLElement | null)
      .filter((p): p is HTMLElement => !!p);
    if (pages.length === 0) return [];
    const scrollEl = pages[0].closest('.editor-scroll-container') as HTMLElement | null;
    if (!scrollEl) return [];
    const base = scrollEl.getBoundingClientRect().top - scrollEl.scrollTop;
    return pages.map(p => {
      const r = p.getBoundingClientRect();
      return { top: r.top - base, height: r.height };
    });
  }

  private searchHighlights: HTMLElement[] = [];
  private currentHighlightIndex = -1;

  searchText(text: string, direction: 'next' | 'previous'): { count: number; currentIndex: number } {
    this.clearSearchHighlights();

    const editors = (this.pageEditorRefs?.toArray() ?? []).map(r => r.nativeElement);
    if (editors.length === 0 && this.editorContent?.nativeElement) {
      editors.push(this.editorContent.nativeElement);
    }
    if (editors.length === 0 || !text) return { count: 0, currentIndex: -1 };

    const searchLower = text.toLowerCase();
    const matches: { node: Text; index: number }[] = [];

    for (const editor of editors) {
      const treeWalker = document.createTreeWalker(editor, NodeFilter.SHOW_TEXT, null);
      while (treeWalker.nextNode()) {
        const node = treeWalker.currentNode as Text;
        const content = node.textContent || '';
        let idx = content.toLowerCase().indexOf(searchLower);
        while (idx !== -1) {
          matches.push({ node, index: idx });
          idx = content.toLowerCase().indexOf(searchLower, idx + 1);
        }
      }
    }

    if (matches.length === 0) return { count: 0, currentIndex: -1 };

    for (let i = matches.length - 1; i >= 0; i--) {
      const { node, index } = matches[i];
      const range = document.createRange();
      range.setStart(node, index);
      range.setEnd(node, index + text.length);

      const highlight = document.createElement('mark');
      highlight.className = 'search-highlight';
      highlight.style.backgroundColor = '#fff3a8';
      highlight.style.color = 'inherit';
      highlight.style.padding = '0';
      highlight.dataset['searchHighlight'] = 'true';

      try {
        range.surroundContents(highlight);
        this.searchHighlights.unshift(highlight);
      } catch {
      }
    }

    if (this.searchHighlights.length > 0) {
      this.currentHighlightIndex = 0;
      if (direction === 'previous') {
        this.currentHighlightIndex = this.searchHighlights.length - 1;
      }
      this.highlightCurrent();
    }

    return { count: this.searchHighlights.length, currentIndex: this.currentHighlightIndex };
  }

  getSearchSnippets(ctx = 32): { before: string; match: string; after: string }[] {
    return this.searchHighlights.map(mark => {
      const block = (mark.closest('p,li,h1,h2,h3,h4,h5,h6,td,th,blockquote') as HTMLElement | null)
        ?? mark.parentElement;
      const full = block?.textContent ?? mark.textContent ?? '';
      const match = mark.textContent ?? '';
      let prefixLen = 0;
      if (block) {
        const r = document.createRange();
        r.setStart(block, 0);
        r.setEndBefore(mark);
        prefixLen = r.toString().length;
      }
      const start = Math.max(0, prefixLen - ctx);
      const before = (start > 0 ? '…' : '') + full.substring(start, prefixLen);
      const afterEnd = prefixLen + match.length + ctx;
      const after = full.substring(prefixLen + match.length, afterEnd) + (afterEnd < full.length ? '…' : '');
      return { before, match, after };
    });
  }

  goToMatch(index: number): { count: number; currentIndex: number } {
    if (index < 0 || index >= this.searchHighlights.length) {
      return { count: this.searchHighlights.length, currentIndex: this.currentHighlightIndex };
    }
    this.currentHighlightIndex = index;
    this.highlightCurrent();
    return { count: this.searchHighlights.length, currentIndex: index };
  }

  findNext(): { count: number; currentIndex: number } {
    if (this.searchHighlights.length === 0) return { count: 0, currentIndex: -1 };
    this.currentHighlightIndex = (this.currentHighlightIndex + 1) % this.searchHighlights.length;
    this.highlightCurrent();
    return { count: this.searchHighlights.length, currentIndex: this.currentHighlightIndex };
  }

  findPrevious(): { count: number; currentIndex: number } {
    if (this.searchHighlights.length === 0) return { count: 0, currentIndex: -1 };
    this.currentHighlightIndex = (this.currentHighlightIndex - 1 + this.searchHighlights.length) % this.searchHighlights.length;
    this.highlightCurrent();
    return { count: this.searchHighlights.length, currentIndex: this.currentHighlightIndex };
  }

  private highlightCurrent(): void {
    this.searchHighlights.forEach((el, i) => {
      if (i === this.currentHighlightIndex) {
        el.style.backgroundColor = '#ff9632';
        el.scrollIntoView({ behavior: 'smooth', block: 'center' });
      } else {
        el.style.backgroundColor = '#fff3a8';
      }
    });
  }

  replaceCurrentMatch(replaceText: string): { count: number; currentIndex: number } {
    if (this.searchHighlights.length === 0 || this.currentHighlightIndex < 0) {
      return { count: 0, currentIndex: -1 };
    }

    const highlight = this.searchHighlights[this.currentHighlightIndex];
    const textNode = document.createTextNode(replaceText);
    highlight.parentNode?.replaceChild(textNode, highlight);
    this.searchHighlights.splice(this.currentHighlightIndex, 1);

    if (this.currentHighlightIndex >= this.searchHighlights.length) {
      this.currentHighlightIndex = 0;
    }
    if (this.searchHighlights.length > 0) {
      this.highlightCurrent();
    }

    this.emitContentChange();
    return { count: this.searchHighlights.length, currentIndex: this.currentHighlightIndex };
  }

  replaceAllMatches(replaceText: string): { count: number; currentIndex: number } {
    for (const highlight of this.searchHighlights) {
      const textNode = document.createTextNode(replaceText);
      highlight.parentNode?.replaceChild(textNode, highlight);
    }
    this.searchHighlights = [];
    this.currentHighlightIndex = -1;
    this.emitContentChange();
    return { count: 0, currentIndex: -1 };
  }

  clearSearchHighlights(): void {
    for (const highlight of this.searchHighlights) {
      const parent = highlight.parentNode;
      if (parent) {
        while (highlight.firstChild) {
          parent.insertBefore(highlight.firstChild, highlight);
        }
        parent.removeChild(highlight);
        parent.normalize();
      }
    }
    this.searchHighlights = [];
    this.currentHighlightIndex = -1;
  }

  private emitContentChange(): void {
    const html = this.getContent();
    if (!html && !this.editorContent?.nativeElement) return;
    this._isInternalUpdate = true;
    this._content.set(html);
    this.contentChange.emit(html);
    this._isInternalUpdate = false;
  }
}
