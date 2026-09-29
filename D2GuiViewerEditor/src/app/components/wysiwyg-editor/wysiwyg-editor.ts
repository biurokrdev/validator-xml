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
  input,
  ChangeDetectionStrategy
} from '@angular/core';
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
import { convertWordListParagraphs } from '../../core/utils/word-clipboard-lists.util';
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
import { CheckboxMarkService } from '../../services/checkbox-mark.service';
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

/** Ikona kotwicy (Material Symbols „anchor", Apache 2.0) — znacznik akapitu-kotwicy jak w Wordzie. */
const ANCHOR_BADGE_SVG =
  '<svg viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg" focusable="false" aria-hidden="true">'
  + '<path d="M17 15l1.55 1.55c-.96 1.69-3.33 3.04-5.55 3.37V11h3V9h-3V7.82C14.16 7.4 15 6.3 15 5'
  + 'c0-1.65-1.35-3-3-3S9 3.35 9 5c0 1.3.84 2.4 2 2.82V9H8v2h3v8.92c-2.22-.33-4.59-1.68-5.55-3.37'
  + 'L7 15l-4-3v3c0 3.88 4.92 7 9 7s9-3.12 9-7v-3l-4 3zM12 4c.55 0 1 .45 1 1s-.45 1-1 1-1-.45-1-1'
  + ' .45-1 1-1z"/></svg>';

/**
 * Geometria pojedynczej strony w edytorze (cm). Sekcja 1 pochodzi z inputów
 * (`pageSize`/`pageMargins`/`pageOrientation`); kolejne sekcje z markerów
 * `div.docx-section-break` (data-* z readera, ADR-0023). Dzięki temu dokumenty
 * mieszające orientacje/rozmiary stron renderują każdą stronę we właściwej geometrii.
 */
/**
 * Układ kolumn sekcji w geometrii strony (ADR-0039). spaceCm = odstęp między kolumnami (px→cm
 * przez CSS_PX_PER_CM). widthsCm/spacesCm tylko dla kolumn nierównych (render przybliżony
 * równymi kolumnami — dane round-tripują wiernie przez data-*).
 */
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
  /** w:pgMar header — odległość GÓRY nagłówka od góry strony (cm). Brak → wyliczana z pasma. */
  headerDistanceCm?: number;
  /** w:pgMar footer — odległość DOŁU stopki od dołu strony (cm). Brak → wyliczana z pasma. */
  footerDistanceCm?: number;
  /** Układ kolumn sekcji. Brak/1 kolumna = jednokolumnowy (ADR-0039). */
  columns?: ColumnLayoutGeo;
}

/**
 * Region przypisów końcowych na konkretnej stronie (jak w MS Word: zaraz po ostatnim
 * bloku treści, z separatorem; nadmiar przelewa się na kolejne strony). Liczony w
 * `_repaginateNow` tym samym measurerem co bloki treści; `topPx` = offset od góry `.page`.
 */
export interface EndnotePageRegion {
  pageIndex: number;
  topPx: number;
  ids: string[];
  /** Kontynuacja z poprzedniej strony — separator na całą szerokość (jak w Wordzie). */
  continuation: boolean;
}

/**
 * Region przypisów DOLNYCH na konkretnej stronie (jak w MS Word: na dole strony, na której
 * jest odwołanie, tuż nad stopką; rezerwuje miejsce w body — treść spływa niżej). Liczony w
 * `_repaginateNow` tym samym measurerem co bloki treści; `bottomPx` = offset od dołu `.page`
 * (pasmo stopki + dystans stopki), region rośnie w GÓRĘ. `ids` = przypisy, których odwołania
 * wylądowały na tej stronie; nadmiar przelewa się na kolejną stronę (`continuation`).
 */
export interface FootnotePageRegion {
  pageIndex: number;
  bottomPx: number;
  ids: string[];
  /** Kontynuacja z poprzedniej strony — separator na całą szerokość (jak w Wordzie). */
  continuation: boolean;
}

/** Twipy → cm (1440 twipów = 1 cal = 2.54 cm). Centralne przeliczenie dla data-col-*-tw. */
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

/**
 * Pasmo kolumnowe strony PRZEJŚCIOWEJ (ciągła zmiana sekcji 1 kolumna → N kolumn w środku
 * strony, jak w Wordzie): bloki od `start` (indeks w obrębie strony, ZA markerem sekcji)
 * renderują się w zagnieżdżonym kontenerze multicol `div.docx-col-band` o jawnej wysokości
 * `heightPx` = pozostała część body strony. Twór czysto prezentacyjny paginacji — flatten
 * i serializacja zapisu rozwijają go do bloków (kolumny do writera niesie marker sekcji).
 */
interface PageColumnBand {
  start: number;
  columns: ColumnLayoutGeo;
  heightPx: number;
}

/**
 * Komponent edytora WYSIWYG
 * Własna implementacja edytora contenteditable
 */
@Component({
  selector: 'd2-wysiwyg-editor',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './wysiwyg-editor.html',
  styleUrl: './wysiwyg-editor.scss',
  changeDetection: ChangeDetectionStrategy.Eager,
  encapsulation: ViewEncapsulation.None
})
export class WysiwygEditorComponent implements AfterViewInit, OnDestroy {
  /**
   * Aktywny edytor strony — ustawiany ręcznie przy `focusin` na konkretnej stronie.
   * Cała istniejąca logika (toolbar, paste, undo, table edit, image drag, search)
   * pracuje na tym refie. Dzięki temu MVP multi-page nie wymaga zmian
   * w setkach miejsc kodu.
   */
  editorContent!: ElementRef<HTMLDivElement>;

  @ViewChildren('pageEditor') pageEditorRefs!: QueryList<ElementRef<HTMLDivElement>>;
  @ViewChild('headerContent') headerContentEl?: ElementRef<HTMLDivElement>;
  @ViewChild('footerContent') footerContentEl?: ElementRef<HTMLDivElement>;

  /**
   * Zwraca AKTUALNIE edytowany contenteditable: header / footer / aktywna strona body.
   * Toolbar musi kierować komendy (B/I/U/align/color/font/size/insertImage) tu,
   * a nie zawsze na body — inaczej formatowanie w header/footer nie zadziała.
   */
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
    // Nie aktualizuj innerHTML jeśli wartość pochodzi z tego samego edytora
    // (zapobiega resetowaniu kursora podczas pisania)
    if (this._content() === value) {
      return;
    }

    this._content.set(value);
    if (!this._isInternalUpdate) {
      const unwrapped = this._captureDocumentDefaults(value);
      // Rozbij na strony po znacznikach <div class="page-break">
      const splitPages = this._splitHtmlIntoPages(unwrapped ?? value ?? '<p></p>');
      const pages = splitPages.length ? splitPages : ['<p></p>'];
      this.pageContents.set(pages);
      this.pageGeometries.set(this._deriveGeometriesForPages(pages));
      // Po Angular re-render zaktualizuj aktywny edytor i zrepaginuj
      this._schedulePaginate('content-input');
      // Etykiety list DOCX liczy silnik (nie przeglądarka) — po renderze stron; przy okazji
      // zsynchronizuj toolbar z pierwszym fragmentem świeżo załadowanego dokumentu (rozmiar/
      // krój czcionki itd.) — bez tego pole pokazuje domyślne 11 do 1. kliknięcia użytkownika.
      setTimeout(() => {
        this.refreshListLabels();
        this._syncInitialFormatting();
        // Renumber note references right after load so endnotes show Roman (i, ii, iii…)
        // and footnotes Arabic immediately — not only after the first edit.
        this.syncFootnotesWithBody();
        this.syncEndnotesWithBody();
      }, 0);
    }
  }
  
  pageMargins = input<PageMargins>({ top: 2.5, bottom: 2.5, left: 2.5, right: 2.5 });
  /** Orientacja jako sygnał (mirror inputu), by geometria bazowa reagowała na zmianę z toolbara. */
  private readonly _orientationSig = signal<'portrait' | 'landscape'>('portrait');
  @Input() set pageOrientation(value: 'portrait' | 'landscape') {
    this._orientationSig.set(value === 'landscape' ? 'landscape' : 'portrait');
  }
  get pageOrientation(): 'portrait' | 'landscape' {
    return this._orientationSig();
  }
  /** Rzeczywisty rozmiar strony dokumentu (cm; sekcja 1 z readera). Brak → A4. */
  private readonly _pageSizeSig = signal<PageSize | null>(null);
  @Input() set pageSize(value: PageSize | undefined) {
    this._pageSizeSig.set(value ?? null);
  }

  /**
   * Geometria strony sekcji 1 — źródło prawdy dla stron bez markera sekcji.
   * Wymiary z `pageSize` (fallback A4); orientacja z inputu wygrywa (toolbar może ją
   * przełączyć bez aktualizacji wymiarów) — wtedy wymiary są obracane.
   */
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

  /** Kolumny sekcji bazowej (0) z kontenera .document-content — do renderu (ADR-0039). */
  private readonly _baseColumns = signal<ColumnLayoutGeo | null>(null);

  /** Geometria per strona (indeks = strona). Wypełniana przy split/repaginacji; brak → baza. */
  readonly pageGeometries = signal<PageGeometry[]>([]);

  /**
   * Wysokość body per strona (px) policzona w `_repaginateNow` (ta sama co `availableFor`,
   * z realnie zmierzonymi pasmami nagłówka/stopki). Używana TYLKO dla stron wielokolumnowych:
   * fragmentacja CSS multicol (column-fill:auto) wymaga JEDNOZNACZNEJ wysokości kontenera —
   * wysokość nadana przez flex:1 jest dla niej nieokreślona i Chrome zostawia całą treść
   * w pierwszej, przepełnionej kolumnie (kolumna 2 pusta, treść przycięta przy dole strony).
   */
  readonly pageBodyHeights = signal<number[]>([]);

  /** Jawna wysokość body strony wielokolumnowej; null = strona jednokolumnowa (flex jak dotąd). */
  pageBodyHeightPx(index: number): number | null {
    if (this.pageColumnCount(index) === null) return null;
    return this.pageBodyHeights()[index] ?? null;
  }

  /** Strona wielokolumnowa wyłącza flex body (jawna wysokość musi wygrać z flex-basis). */
  pageBodyFlex(index: number): string | null {
    return this.pageBodyHeightPx(index) !== null ? 'none' : null;
  }

  /** Indeks sekcji (0-based) per strona — do doboru nagłówka/stopki sekcji. Brak → sekcja 0. */
  readonly pageSectionIndexes = signal<number[]>([]);

  geometryFor(index: number): PageGeometry {
    return this.pageGeometries()[index] ?? this.baseGeometry();
  }

  // Template helpers — px at 96 DPI via the shared CSS_PX_PER_CM constant.
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
  /**
   * Liczba kolumn treści strony (ADR-0039). null = układ jednokolumnowy — binding zdejmuje
   * własność, więc strona NIE jest kontenerem multicol (column-count:1 tworzyłby kontekst
   * fragmentacji: zabłąkany docx-column-break wypychałby treść do przyciętej kolumny overflow).
   */
  pageColumnCount(index: number): number | null {
    const c = this.geometryFor(index).columns;
    return c && c.count > 1 ? c.count : null;
  }
  /** Odstęp między kolumnami w px (column-gap); null = strona jednokolumnowa. */
  pageColumnGapPx(index: number): number | null {
    const c = this.geometryFor(index).columns;
    return c && c.count > 1 ? Math.max(0, c.spaceCm) * CSS_PX_PER_CM : null;
  }
  /** Separator kolumn (column-rule) — cienka linia jak w Wordzie, albo brak. */
  pageColumnRule(index: number): string | null {
    const c = this.geometryFor(index).columns;
    return c && c.count > 1 && c.separator ? '1px solid #bbb' : null;
  }
  /**
   * Geometria pasma nagłówka/stopki jak w Wordzie: pasmo zaczyna się `headerDistance`
   * (w:pgMar header) od krawędzi strony i ma MINIMALNĄ wysokość (margines − dystans) —
   * treść wyższa niż pasmo spycha body (flex + min-height), zamiast się przycinać.
   * Sekcja 1 nie niesie dystansu w kontrakcie → odtwarzamy go z pasma (margines − band),
   * czyli dokładnie odwrotność wzoru readera.
   */
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
  /** Tryb tylko-do-odczytu (Krok 2) — blokuje edycję contenteditable. */
  @Input() readOnly = false;
  @Input() showMarginGuides = false;
  
  // Nagłówek i stopka. Każde pasmo trzyma WŁASNE flagi wariantów: inputy headerContent
  // i footerContent są aplikowane jeden po drugim, więc wspólna flaga była nadpisywana
  // przez binding wykonany później (dokument z nagłówkiem first bez stopki first tracił
  // titlePg nagłówka — strona 1 renderowała wariant default).
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
  
  /**
   * Własne nagłówki/stopki sekcji ≥ 1 (dokumenty wielosekcyjne, ADR-0023). Sekcja bez
   * wpisu dziedziczy z poprzedniej; sekcja 0 = headerContent/footerContent.
   */
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
  /** Emituje aktualnie edytowaną sekcję (treść / nagłówek / stopka) — używane przez pionową linijkę. */
  @Output() editingSectionChange = new EventEmitter<'header' | 'footer' | 'body'>();
  /**
   * Emits a snapshot of the currently selected image (or null when nothing is selected).
   * Drives d2-image-properties-panel — the parent owns the signal and the visibility logic.
   */
  @Output() imageSelectionChange = new EventEmitter<{
    widthPx: number;
    heightPx: number;
    aspectRatio: number;
    alignment: 'left' | 'center' | 'right' | null;
    positionMode: 'inline' | 'square' | 'topBottom' | 'front' | 'behind';
    border: { enabled: boolean; color: string; widthPx: number; style: 'solid' | 'dashed' | 'dotted' };
    crop: { left: number; right: number; top: number; bottom: number };
  } | null>();
  /**
   * Emituje ZMIERZONĄ geometrię edytowanego pasma nagłówka/stopki (cm od górnej krawędzi
   * strony 1). Pasmo ma `min-height` i rośnie z treścią (np. obraz), więc pionowa linijka
   * musi odzwierciedlać faktyczne położenie, a nie wyliczone z marginesów cm.
   */
  @Output() sectionGeometryChange = new EventEmitter<{ section: 'header' | 'footer'; topCm: number; bottomCm: number }>();

  /** Obserwator rozmiaru aktywnego pasma nagłówka/stopki (re-emisja geometrii przy zmianie wysokości). */
  private _sectionResizeObserver?: ResizeObserver;
  @Output() openHeaderFooterSettings = new EventEmitter<{
    headerMargin: number;
    footerMargin: number;
    differentFirstPage: boolean;
    differentOddEven: boolean;
  }>();

  private _content = signal<string>('');
  private _isInternalUpdate = false;
  /** Flaga ustawiana na input, czyszczona przy save — pozwala uniknąć ciężkiego getContent() w updateState. */
  private _isDirty = false;
  /** Debouncer dla saveToUndoStack + emitContent — nie wykonujemy ich na każde naciśnięcie klawisza. */
  private _persistTimer: ReturnType<typeof setTimeout> | null = null;
  private undoStack: string[] = [];
  // Each redo entry carries the caret that was active when the state was left via undo —
  // i.e. the position AFTER that edit's inserted text — so redo restores it there (Word-like),
  // instead of the pre-redo caret which sits before the re-inserted content.
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

  // Zaznaczone pole tekstowe (div.docx-textbox) — klasa tb-selected steruje obramowaniem
  // edycyjnym (SCSS), a zaznaczenie pokazuje znacznik kotwicy przy akapicie-kotwicy.
  private selectedTextBox: HTMLElement | null = null;
  /** Textbox z klasą tb-edge (kursor „move" na pasie krawędzi) — do sprzątania. */
  private edgeCursorTextBox: HTMLElement | null = null;
  /** Znacznik kotwicy (overlay w .page — poza contenteditable, nie serializuje się). */
  private anchorBadge: HTMLElement | null = null;
  /** Element pływający, dla którego znacznik jest widoczny. */
  private anchorBadgeTarget: HTMLElement | null = null;
  private _anchorBadgeRafHandle: number | null = null;

  // Stan resize tabeli
  private tableResizeState: {
    type: 'col' | 'row' | 'table';
    table: HTMLTableElement;
    startX: number;
    startY: number;
    // Logical column left of the dragged boundary (col resize). See table-grid.util.
    boundaryCol: number;
    rowIndex: number;
    grid: TableGrid;
    // Per-logical-column widths captured at drag start (immutable reference).
    startColumnWidths: number[];
    // Per-column widths as of the latest pointer move (mutated during the drag).
    columnWidths: number[];
    startHeight: number;
    startTableWidth: number;
    // Czy wskaźnik faktycznie się poruszył — sam klik w strefie krawędzi (bez ruchu)
    // nie może commitować (onContentChange = dirty/undo/persist za darmo).
    moved: boolean;
  } | null = null;

  // Multi-page MVP (Wariant A):
  // pełna treść HTML per strona; każda strona renderuje własny contenteditable
  pageContents = signal<string[]>(['<p></p>']);
  // która strona ma aktualnie focus (do operacji toolbar/undo/paste)
  activePageIndex = signal<number>(0);

  private _sanitizer = inject(DomSanitizer);
  private _hostRef = inject(ElementRef<HTMLElement>);
  private _cdr = inject(ChangeDetectorRef);
  /** Wybrany znak zaznaczenia pól wyboru (krzyżyk domyślnie) — wspólny z menu toolbara. */
  private _checkboxMarks = inject(CheckboxMarkService);
  /** Cache trusted-HTML per strona — KLUCZOWE dla wydajności i contenteditable.
   *  Bez tego każde change detection tworzy nowy obiekt SafeHtml, Angular widzi
   *  „zmianę" i rebinduje innerHTML co kasuje kursor + uniemożliwia pisanie. */
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

  /** Cache SafeHtml dla nagłówków/stopek — żeby preview zachował formatowanie
   *  inline (color/font-size/text-align/img style="width:..."). Bez tego Angular
   *  strippuje atrybuty `style` i obraz/rozmiar/kolor "ginie" w trybie podglądu. */
  private _safeHeaderCache = new Map<number, { html: string; safe: SafeHtml }>();
  private _safeFooterCache = new Map<number, { html: string; safe: SafeHtml }>();

  getHeaderContentSafe(pageIndex: number): SafeHtml {
    // Transformacja PRZED cache (klucz = HTML po transformacji): zmiana geometrii strony
    // (marginesy/dystanse) zmienia wynik i naturalnie unieważnia wpis.
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

  /**
   * Ostatni pomiar realnych wysokości pasm z paginacji (_measureBandHeightsPx w _repaginateNow).
   * Kotwice pasm muszą używać TEJ SAMEJ wysokości stopki co paginacja: treść stopki wyższa niż
   * min-height (np. baner firmowy) podnosi górę kontenera pasma, a statyczny wzór zostawiał
   * kotwice o tę różnicę ZA WYSOKO (logo malowało się nad banerem, w obszarze treści).
   * null = jeszcze bez pomiaru (start, jsdom w testach) → statyczny wzór jak dotąd.
   */
  private _measuredBandHeights:
    { headerFirst: number; headerRest: number; footerFirst: number; footerRest: number } | null = null;

  /**
   * Geometria pasma nagłówka/stopki dla przeliczeń kotwic (kontrakt ↔ pasmo).
   * bandTopPx stopki = góra kontenera pasma od góry strony: max(min-height pasma, realny pomiar
   * z paginacji) — kontener flex rośnie treścią W GÓRĘ strony. Nagłówka pomiar nie dotyczy
   * (góra pasma = dystans nagłówka, niezależnie od wysokości treści). Zero odczytów DOM tutaj
   * (gettery pasm lecą w każdym cyklu CD — pułapka ADR-0075).
   */
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

  /**
   * Geometria pasma dla elementu żyjącego w EDYTOWANYM nagłówku/stopce (null = element w body).
   * Strona edytowanego pasma = editingHfPageIndex() (edycja pasma zawsze przypięta do strony).
   */
  private _bandGeoForElement(el: HTMLElement): HfBandGeometry | null {
    if (el.closest('.header-editor-content')) return this._bandGeoFor(this.editingHfPageIndex(), 'header');
    if (el.closest('.footer-editor-content')) return this._bandGeoFor(this.editingHfPageIndex(), 'footer');
    return null;
  }

  /**
   * Tryb WYŚWIETLANIA pasma ([innerHTML] bez wrapowania JS): pozycjonuje obrazy kotwiczone
   * (data-pos-mode front/behind) inline stylem na <img> — absolut w układzie pasma przeliczony
   * z kontraktu (contractToBand), jak w MS Word (obiekt może wystawać poza pasmo). Transformacja
   * jednokierunkowa: model źródłowy pasma pozostaje nietknięty (nic nie wraca do zapisu).
   */
  private _positionBandAnchors(html: string, pageIndex: number, band: 'header' | 'footer'): string {
    if (!html) return html;
    const mayHaveAnchors = html.includes('data-pos-mode')
      || html.includes('docx-shape') || html.includes('docx-textbox');
    if (!mayHaveAnchors) return html;
    const tpl = document.createElement('template');
    tpl.innerHTML = html;
    const anchored = tpl.content.querySelectorAll<HTMLElement>(
      'img[data-pos-mode="front"], img[data-pos-mode="behind"]');
    // Kotwiczone kształty wektorowe i pola tekstowe (div.docx-shape / div.docx-textbox) niosą
    // z readera inline absolut we współrzędnych KONTRAKTU (X od lewej krawędzi strony, Y od góry
    // obszaru treści) — w paśmie wymaga tego samego przeliczenia originu co obrazy; kształty
    // statyczne (inline-block, bez kotwicy) przechodzą nietknięte.
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

  /** Inwalidacja cache nagłówka/stopki — wołać po każdej edycji */
  private invalidateHeaderFooterCache(): void {
    this._safeHeaderCache.clear();
    this._safeFooterCache.clear();
  }

  // ── Przypisy dolne ────────────────────────────────────────────────────────
  // Treść przypisów (jedno źródło prawdy) trzymana w sygnale, renderowana w panelu
  // POZA contenteditable body. Odwołania (<sup class="footnote-ref">) żyją w treści
  // strony; numer widoczny wynika z kolejności pierwszych odwołań i jest przeliczany
  // po każdej operacji edycyjnej (add/remove/reorder).

  private readonly _footnotes = signal<Footnote[]>([]);
  readonly footnoteList = computed(() => this._footnotes());

  // Format numeracji przypisów z dokumentu (w:numFmt) — tylko WYŚWIETLANIE etykiet. undefined =
  // dokument nie ustala → domyślna Worda (dolne = cyfry, końcowe = małe rzymskie). Nie round-tripuje
  // przez zapis (żyje w zachowanym settings.xml pakietu).
  private readonly _footnoteNumberFormat = signal<string | undefined>(undefined);
  private readonly _endnoteNumberFormat = signal<string | undefined>(undefined);

  @Input() set footnoteNumberFormat(value: string | undefined) {
    this._footnoteNumberFormat.set(value || undefined);
  }
  @Input() set endnoteNumberFormat(value: string | undefined) {
    this._endnoteNumberFormat.set(value || undefined);
  }

  /**
   * Formatuje numer przypisu wg formatu z dokumentu (w:numFmt). Fallback (brak/nieznany format) =
   * domyślna Worda: dolne = cyfry (1,2,3), końcowe = małe rzymskie (i,ii,iii).
   */
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

  /** Litery jak w Wordzie: a, b, …, z, aa, bb, cc (POWTÓRZONA litera, nie bijektywne aa/ab). */
  private _toWordLetters(n: number): string {
    if (!Number.isFinite(n) || n <= 0) return String(n);
    const k = Math.floor(n);
    const letter = String.fromCharCode(97 + (k - 1) % 26);
    const count = Math.floor((k - 1) / 26) + 1;
    return letter.repeat(count);
  }

  /** Rozkład regionów przypisów dolnych na strony — liczony w `_repaginateNow`. */
  private readonly _footnoteLayout = signal<FootnotePageRegion[]>([]);

  /** Region przypisów dolnych danej strony (null = strona bez przypisów). */
  footnoteRegionFor(pageIndex: number): FootnotePageRegion | null {
    const region = this._footnoteLayout().find(r => r.pageIndex === pageIndex);
    if (!region) return null;
    // Układ może być chwilowo przestarzały względem modelu (repaginacja jest
    // debounce'owana) — pokazuj tylko wpisy nadal obecne w modelu.
    return this.footnoteEntriesFor(region).length > 0 ? region : null;
  }

  /** Wpisy regionu z numeracją GLOBALNĄ (pozycja w modelu, ciągła przez strony). */
  footnoteEntriesFor(region: FootnotePageRegion): { fn: Footnote; number: number; label: string }[] {
    const list = this.footnoteList();
    const indexById = new Map(list.map((f, i) => [f.id, i]));
    const entries: { fn: Footnote; number: number; label: string }[] = [];
    for (const id of region.ids) {
      const idx = indexById.get(id);
      if (idx === undefined) continue;
      // label = dziesiętny (1, 2, 3…) — jak odwołanie w treści przypisu dolnego.
      entries.push({ fn: list[idx], number: idx + 1, label: this._formatNoteLabel(idx + 1, this._footnoteNumberFormat(), false) });
    }
    return entries;
  }

  @Input() set footnotes(value: Footnote[] | undefined) {
    // Kopia obronna — nie mutujemy tablicy wejściowej rodzica.
    this._footnotes.set(value ? value.map(f => ({ ...f })) : []);
    // Region przypisów jest częścią układu strony — przelicz rozkład.
    this._schedulePaginate('footnotes-input');
  }

  @Output() footnotesChange = new EventEmitter<Footnote[]>();

  /** Cache SafeHtml treści przypisu (klucz = id + treść) — bez tego rebinding kasuje kursor. */
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

  /** Zwraca aktualny model przypisów (dla zapisu / testów). */
  getFootnotes(): Footnote[] {
    return this._footnotes().map(f => ({ ...f }));
  }

  /**
   * Zamienia „szumowe" twarde spacje z contenteditable na zwykłe — TYLKO te przylegające
   * do znaku nie-białego (międzywyrazowe). Chrome przy edycji wstawia &nbsp; zamiast spacji;
   * takie U+00A0 szło do w:t i Word nie łamał wierszy (tekst przypisu wychodził poza margines).
   * Samotne &nbsp; (placeholder pustych bloków) zostaje; działa na text node'ach, więc
   * atrybuty (np. data-docx-xml) są nietknięte. Wołane wyłącznie dla realnie edytowanych
   * wpisów — nieedytowane zachowują oryginalne twarde spacje z importu 1:1.
   */
  private _normalizeEditedNbsp(root: HTMLElement): void {
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const value = node.nodeValue ?? '';
      if (!value.includes('\u00A0')) continue;
      const cleaned = value.replace(/(?<=\S)\u00A0|\u00A0(?=\S)/g, ' ');
      if (cleaned !== value) node.nodeValue = cleaned;
    }
  }

  /**
   * Czy przechowywany HTML wpisu jest treściowo tożsamy z żywym DOM-em. Proste porównanie
   * stringów pada na serializacji (np. U+00A0 w modelu vs &nbsp; z innerHTML), przez co blur
   * BEZ edycji commitował wpis — a commit niesie normalizację twardych spacji, więc fałszywy
   * commit naruszałby wierność nieedytowanych przypisów. Kanonizacja: oba przez innerHTML.
   */
  private _isSameRenderedHtml(stored: string, live: HTMLElement): boolean {
    if (stored === live.innerHTML) return true;
    // Overlay „Pokaż wszystko" mógł nadać blokom wpisu prezentacyjną klasę .fmt-trailing-br
    // — porównujemy treść BEZ niej, inaczej sam blur (bez edycji) commitowałby wpis.
    const liveClone = live.cloneNode(true) as HTMLElement;
    this._stripFmtTrailingBr(liveClone);
    if (stored === liveClone.innerHTML) return true;
    const probe = document.createElement('div');
    probe.innerHTML = stored;
    return probe.innerHTML === liveClone.innerHTML;
  }

  /** Commit treści przypisu po edycji w panelu (blur) → aktualizacja modelu + emisja. */
  commitFootnoteContent(id: string, event: Event): void {
    if (this.readOnly) return;
    const el = event.target as HTMLElement | null;
    if (!el) return;
    const current = this._footnotes();
    const idx = current.findIndex(f => f.id === id);
    if (idx < 0 || this._isSameRenderedHtml(current[idx].html, el)) return;

    this._normalizeEditedNbsp(el);
    // Do modelu idzie treść BEZ prezentacyjnej klasy overlay'a (żywy DOM ją zachowuje —
    // overlay uzgodni ją przy następnym przebiegu).
    const clean = el.cloneNode(true) as HTMLElement;
    this._stripFmtTrailingBr(clean);
    const html = clean.innerHTML;
    const updated = current.map(f => (f.id === id ? { ...f, html } : f));
    this._footnotes.set(updated);
    this.footnotesChange.emit(this.getFootnotes());
    // Zmiana treści zmienia wysokość wpisu — region może przelać się inaczej / zmienić rezerwę.
    this._schedulePaginate('footnote-edit');
  }

  /** Id przypisów dolnych, do których odwołuje się dany blok (kolejność DOM). */
  private _footnoteIdsIn(block: HTMLElement): string[] {
    const ids: string[] = [];
    // Blok MOŻE sam być odwołaniem (goły <sup> wstawiony bez akapitu — np. addFootnoteAtCursor
    // bez żywej selekcji) — querySelectorAll nie dopasowuje samego elementu, więc sprawdzamy go osobno.
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

  /** Wszystkie odwołania w treści stron, w kolejności dokumentu (DOM). */
  private _footnoteReferenceElements(): HTMLElement[] {
    const refs: HTMLElement[] = [];
    for (const page of this.pageEditorRefs?.toArray() ?? []) {
      page.nativeElement
        .querySelectorAll<HTMLElement>('sup.footnote-ref[data-footnote-id]')
        .forEach(el => refs.push(el));
    }
    return refs;
  }

  /**
   * Uzgadnia model przypisów z odwołaniami w treści: numeruje odwołania wg kolejności
   * pierwszego wystąpienia, porządkuje listę treści tak samo, usuwa treści bez odwołania
   * (brak osieroconych) i emituje zmianę. Woływane po każdej operacji edycyjnej.
   */
  syncFootnotesWithBody(): void {
    const refEls = this._footnoteReferenceElements();

    // Kolejność pierwszych wystąpień + mapa id → numer (współdzielony przez powtórzone odwołania).
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

    // Odśwież numer + etykietę na KAŻDYM odwołaniu (także powtórzonych).
    for (const el of refEls) {
      const id = el.getAttribute('data-footnote-id') ?? '';
      const number = numberById.get(id);
      if (!number) continue;
      // Etykieta wg formatu z dokumentu (w:numFmt); domyślnie cyfry dla przypisów dolnych.
      const label = this._formatNoteLabel(number, this._footnoteNumberFormat(), false);
      if (el.textContent !== label) el.textContent = label;
      el.setAttribute('aria-label', `Przypis ${label}`);
    }

    // Uporządkuj listę treści wg kolejności odwołań; treści bez odwołania są usuwane.
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

  /**
   * Wstawia nowy przypis w bieżącej pozycji kursora: odwołanie <sup> w treści + pusty
   * wpis treści w modelu, po czym przelicza numerację. Zwraca id nowego przypisu.
   */
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

    // Dodaj wpis treści; kolejność/numer ustali syncFootnotesWithBody wg pozycji w DOM.
    this._footnotes.set([...this._footnotes(), { id, html: '<p></p>' }]);
    this.syncFootnotesWithBody();
    this.contentChange.emit(this.getContent());
    return id;
  }

  /**
   * Usuwa przypis: kasuje WSZYSTKIE jego odwołania z treści oraz wpis treści, po czym
   * przelicza numerację. Bez osieroconych odwołań ani nieużywanych przypisów.
   */
  removeFootnote(id: string): void {
    if (this.readOnly) return;

    let removedFromDom = false;
    for (const page of this.pageEditorRefs?.toArray() ?? []) {
      page.nativeElement
        .querySelectorAll<HTMLElement>(`sup.footnote-ref[data-footnote-id="${id}"]`)
        .forEach(el => { el.remove(); removedFromDom = true; });
    }

    // Nie usuwamy z modelu ręcznie — syncFootnotesWithBody przytnie treść bez odwołania,
    // przenumeruje pozostałe i wyemituje footnotesChange. Przypis bez odwołania w DOM też
    // zostanie przycięty (brak nieużywanych przypisów).
    this.syncFootnotesWithBody();
    if (removedFromDom) this.contentChange.emit(this.getContent());
  }

  // ── Przypisy końcowe ──────────────────────────────────────────────────────
  // Osobny model od dolnych (inna semantyka: koniec dokumentu). Odwołania w treści =
  // <sup class="endnote-ref">; treść renderowana WEWNĄTRZ ostatniej strony, zaraz po
  // ostatnim bloku treści (jak w MS Word), z przelewaniem na kolejne strony. Elementy
  // regionu żyją POZA contenteditable body — nie wchodzą do getContent().

  private readonly _endnotes = signal<Endnote[]>([]);
  readonly endnoteList = computed(() => this._endnotes());

  /** Rozkład regionów przypisów końcowych na strony — liczony w `_repaginateNow`. */
  private readonly _endnoteLayout = signal<EndnotePageRegion[]>([]);

  /** Region przypisów końcowych danej strony (null = strona bez przypisów). */
  endnoteRegionFor(pageIndex: number): EndnotePageRegion | null {
    const region = this._endnoteLayout().find(r => r.pageIndex === pageIndex);
    if (!region) return null;
    // Układ może być chwilowo przestarzały względem modelu (repaginacja jest
    // debounce'owana) — pokazuj tylko wpisy nadal obecne w modelu.
    return this.endnoteEntriesFor(region).length > 0 ? region : null;
  }

  /** Wpisy regionu z numeracją GLOBALNĄ (pozycja w modelu, ciągła przez strony). */
  endnoteEntriesFor(region: EndnotePageRegion): { en: Endnote; number: number; label: string }[] {
    const list = this.endnoteList();
    const indexById = new Map(list.map((e, i) => [e.id, i]));
    const entries: { en: Endnote; number: number; label: string }[] = [];
    for (const id of region.ids) {
      const idx = indexById.get(id);
      if (idx === undefined) continue;
      // label = lowercase Roman (i, ii, iii…) to match the in-text endnote reference marker.
      entries.push({ en: list[idx], number: idx + 1, label: this._formatNoteLabel(idx + 1, this._endnoteNumberFormat(), true) });
    }
    return entries;
  }

  @Input() set endnotes(value: Endnote[] | undefined) {
    this._endnotes.set(value ? value.map(e => ({ ...e })) : []);
    // Region przypisów jest częścią układu strony — przelicz rozkład.
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
    // Jak w commitFootnoteContent: treść do modelu bez prezentacyjnej klasy overlay'a.
    const clean = el.cloneNode(true) as HTMLElement;
    this._stripFmtTrailingBr(clean);
    const html = clean.innerHTML;
    const updated = current.map(e => (e.id === id ? { ...e, html } : e));
    this._endnotes.set(updated);
    this.endnotesChange.emit(this.getEndnotes());
    // Zmiana treści zmienia wysokość wpisu — region może przelać się inaczej.
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

  /**
   * Uzgadnia model przypisów końcowych z odwołaniami w treści (numeracja wg kolejności
   * pierwszego wystąpienia, porządkowanie listy, usunięcie osieroconych treści). Analogicznie
   * do <see cref="syncFootnotesWithBody"/>, ale na osobnym modelu/klasie odwołania.
   */
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
      // Etykieta wg formatu z dokumentu (w:numFmt); domyślnie małe rzymskie (i, ii, iii…) jak MS Word,
      // co odróżnia przypisy końcowe od dolnych (cyfry) gdy dokument nie ustala własnego formatu.
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

  /** Lowercase Roman numeral (endnotes are labelled i, ii, iii… like MS Word). */
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

  // ── Nawigacja odnośnik ↔ treść przypisu (jak w MS Word) ──────────────────

  /**
   * Klik w treści strony: jeżeli trafił w odnośnik przypisu (dolnego lub końcowego),
   * przenieś do jego wpisu (scroll + fokus + podświetlenie). Niezależnie od tego klik
   * w body kończy edycję nagłówka/stopki (dotychczasowe zachowanie).
   */
  onEditorClick(ev: MouseEvent): void {
    if (this._toggleCheckboxFromClick(ev)) return;
    this._navigateToNoteFromRef(ev);
    this._navigateFromInternalAnchor(ev);
    this.stopEditingHeaderFooter();
  }

  /**
   * Ctrl+klik (lub Cmd na macOS) w hyperlink WEWNĘTRZNY (`a[data-anchor]` — np. wpis spisu
   * treści) przenosi do celu-zakładki w treści, jak w MS Word („Ctrl+klik, aby przejść").
   * Zwykły klik zostaje edycją (karetka) — również zachowanie Worda. Cel to niewidoczny
   * marker `span.docx-bookmark[data-bm-name]` (display:none), więc scrollujemy jego BLOK.
   */
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

  /** Klik w numer wpisu przypisu: powrót do odwołania w treści (scroll + karetka za nim). */
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

  /** Chwilowe podświetlenie wpisu po skoku z odwołania (odpowiednik wyróżnienia w Wordzie). */
  private _flashNoteItem(item: HTMLElement): void {
    item.classList.remove('note-item-flash');
    // Restart animacji, gdy użytkownik klika ten sam odnośnik ponownie.
    void item.offsetWidth;
    item.classList.add('note-item-flash');
    setTimeout(() => item.classList.remove('note-item-flash'), 1300);
  }

  // Paginator: debounce + safety flag
  private _paginateTimer: ReturnType<typeof setTimeout> | null = null;
  private _paginateRafHandle: number | null = null;
  private _isRepaginating = false;
  // Guards the corrective repagination that runs once fonts/images finish loading.
  // The generation token lets a newer import cancel an in-flight resource wait so a
  // stale document never repaginates over a fresh one.
  private _isDestroyed = false;
  private _resourceRepaginateGen = 0;

  // Bieżący rozmiar czcionki (dla nowego tekstu gdy nie ma zaznaczenia)
  private currentFontSize = 11;
  private currentFontFamily = 'Calibri';

  // ── Sticky formatting (Problem 10) ──
  /**
   * Style picked at a collapsed caret that must survive DOM churn. The ZWS span created by
   * the font pickers is the only DOM carrier of the choice; when a deletion empties it the
   * browser silently removes the span and the caret falls back into the paragraph — without
   * this record the toolbar and the next typed character revert to the document default
   * font. Values are CSS-ready ('Arial', '11pt').
   */
  private _pendingInlineStyle: { fontFamily?: string; fontSize?: string } | null = null;

  /**
   * Stała referencja handlera `selectionchange` tej instancji — pozwala przepiąć jedyny
   * listener na document z poprzedniej instancji na bieżącą i zdjąć go w ngOnDestroy.
   */
  private readonly _onDocumentSelectionChange = (): void => {
    if (this._isDestroyed) return;
    this.onSelectionChange();
  };
  /** Caret position the pending style is valid at; any other caret invalidates it. */
  private _pendingStyleAnchor: { node: Node; offset: number } | null = null;
  /** Style captured in beforeinput (delete) — re-anchored after the input event mutates the DOM. */
  private _pendingDeleteCapture: { fontFamily?: string; fontSize?: string } | null = null;
  /** Guard window: our own programmatic re-anchor must not be treated as the user moving away. */
  private _pendingStyleHoldUntil = 0;

  // Nagłówek i stopka - stan
  private _headerHtml = signal<string>('');
  private _footerHtml = signal<string>('');
  private _headerFirstPageHtml = signal<string>('');
  private _footerFirstPageHtml = signal<string>('');
  private _headerOddHtml = signal<string>('');
  private _headerEvenHtml = signal<string>('');
  private _footerOddHtml = signal<string>('');
  private _footerEvenHtml = signal<string>('');
  private _headerHeight = signal<number>(1.27); // domyślnie 1.27 cm (jak w Google Docs)
  private _footerHeight = signal<number>(1.27); // domyślnie 1.27 cm
  /**
   * Domyślny rozmiar/krój czcionki dokumentu odczytany z wrappera `.document-content` (reader
   * umieszcza tam default z docDefaults/stylu Normal). Stosujemy je na contenteditable strony,
   * bo paginacja (`_flattenTopBlocks`) ROZWIJA ten wrapper — bez tego default ginie i tekst
   * wraca do rozmiaru domyślnego edytora (Issue: „14pt z Worda ładuje się jako ~10pt").
   * Runy/akapity z własnym rozmiarem nadal wygrywają (kaskada CSS).
   */
  documentDefaultFontSize = signal<string | null>(null);
  documentDefaultFontFamily = signal<string | null>(null);
  /** Domyślna interlinia dokumentu (line-height kontenera z docDefaults readera). */
  documentDefaultLineHeight = signal<string | null>(null);
  /** Domyślny odstęp PO akapicie (data-default-after-tw kontenera) — CSS var --doc-par-margin. */
  documentDefaultParagraphSpacing = signal<string | null>(null);
  /** Domyślny odstęp PRZED akapitem (data-default-before-tw kontenera) — CSS var
   *  --doc-par-margin-top. Word renderuje odstęp „przed" każdego akapitu (także na górze
   *  strony), a wcześniej wartość jechała tylko w round-tripie atrybutów i nie była widoczna. */
  documentDefaultParagraphSpacingBefore = signal<string | null>(null);
  /** Model SUMY odstępów akapitów (data-para-spacing-sum kontenera; ADR-0107): dokument
   *  z flagą zgodności doNotUseHTMLParagraphAutoSpacing — Word SUMUJE after+before, więc
   *  strony dostają klasę `para-spacing-sum` (nośnik „po" = padding). Domyślnie false =
   *  MAX Worda przez kolaps marginesów CSS. */
  documentParagraphSpacingSum = signal<boolean>(false);
  /** Domyślna interlinia auto w 240-tych (data-default-line kontenera) — marker --w-line-tw
   *  na .editor-content; dziedziczy na akapity, więc dialog akapitu czyta mnożnik Worda,
   *  a nie skalibrowaną wartość renderową (PG-09). */
  documentDefaultLineTw = signal<string | null>(null);

  /**
   * Single line spacing of the document font in em (font metrics, PG-09 table) — CSS var
   * `--w-line-single` on `.page`. ADR-0108 r.4 (koryguje ADR-0085 po pomiarze glifów w PDF
   * z Worda): for lineRule=AUTO Word anchors the ink at the TOP of the slot (first-line
   * baseline is constant regardless of the multiplier; excess goes BELOW each line), for
   * EXACT/AT LEAST at the BOTTOM (excess ABOVE; "Exactly" smaller than the font clips the
   * TOP). CSS splits leading half above / half below, so the SCSS shifts paragraph ink UP by
   * half the leading by default (`top: calc((var(--w-line-single) - 1lh) / 2)`) and DOWN for
   * paragraphs carrying the `--w-line-rule:atLeast|exact` marker. The em token resolves at
   * USE time (against the paragraph font-size), not at declaration.
   */
  documentDefaultLineSingle(): string {
    return `${wordSingleFactor(this.documentDefaultFontFamily())}em`;
  }

  /**
   * Atrybuty wrappera .document-content przechwycone przy setContent. Paginacja rozwija
   * wrapper, więc getContent() owija nimi scaloną treść z powrotem — bez tego zapis gubił
   * domyślny font i data-default-* (writer regenerował pakiet z hardkodowanymi 11pt/259).
   */
  private _documentContainerAttrs: { name: string; value: string }[] | null = null;
  // Flagi wariantów PER PASMO (patrz settery headerContent/footerContent) — w DOCX
  // titlePg jest właściwością sekcji wspólną dla nagłówka i stopki, ale kontrakt
  // HeaderFooterContent raportuje ją per pasmo (nagłówek może mieć wariant first,
  // a stopka nie), więc wspólna flaga gubiła stan jednego z pasm.
  private _headerDifferentFirstPage = signal<boolean>(false);
  private _footerDifferentFirstPage = signal<boolean>(false);
  private _headerDifferentOddEven = signal<boolean>(false);
  private _footerDifferentOddEven = signal<boolean>(false);
  editingSection = signal<'header' | 'footer' | 'body'>('body');
  /** Strona, na której trwa edycja nagłówka/stopki — edycja pasma sekcji ≥ 1 odbywa się
   *  na stronie tej sekcji (właściciel treści = wpis sekcyjny albo baza). */
  editingHfPageIndex = signal<number>(0);
  
  // Menu opcji nagłówka/stopki
  showHeaderOptionsMenu = signal<boolean>(false);
  showFooterOptionsMenu = signal<boolean>(false);
  
  // Publiczne gettery dla template
  headerHeight = computed(() => this._headerHeight());
  footerHeight = computed(() => this._footerHeight());
  differentFirstPage = computed(() => this._headerDifferentFirstPage() || this._footerDifferentFirstPage());
  differentOddEven = computed(() => this._headerDifferentOddEven() || this._footerDifferentOddEven());

  // Computed: zawartość nagłówka/stopki per strona (reaktywna na zmiany sygnałów).
  // Iterujemy pageContents() — RZECZYWISTĄ listę stron. Wcześniej używano martwego
  // sygnału `pages` (zawsze długości 1), więc nagłówek/stopka liczyły się tylko dla
  // strony 0, a strony 2+ dostawały surowy fallback z nierozwiniętymi {page}/{pages}.
  headerContents = computed(() => {
    const pagesArr = this.pageContents();
    return pagesArr.map((_, i) => this._computeHeaderContent(i));
  });
  footerContents = computed(() => {
    const pagesArr = this.pageContents();
    return pagesArr.map((_, i) => this._computeFooterContent(i));
  });

  // Efektywne paddingi treści (margines minus wysokość nagłówka/stopki) liczone są per strona
  // w contentPadTopPx/contentPadBottomPx — w MS Word nagłówek/stopka zajmują CZĘŚĆ marginesu.

  // Stan edytora
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

  // Ostatnia policzona liczba stron (do emisji pagesChange bez rerenderu DOM)
  private lastEmittedPageCount = 1;

  ngAfterViewInit(): void {
    // Ustaw editorContent na pierwszej (aktywnej) stronie i obserwuj zmiany
    // (np. po repaginacji liczba stron się zmienia).
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
      // Po repaginacji ponownie podpinamy listenery do nowych edytorów
      this.setupEventListeners();
      // Nowe/usunięte strony mogą przecinać listy — przelicz etykiety w kolejności dokumentu.
      this.refreshListLabels();
    });

    this.initializeEditor();
    this.setupEventListeners();
    
    // Oblicz strony przy starcie
    setTimeout(() => {
      this.calculatePages();
      this._schedulePaginate('init');
      // Pomiar wstępny leci na metrykach dostępnych „teraz"; po doładowaniu web-fontów/obrazów
      // skoryguj liczbę stron jednym przebiegiem (patrz _repaginateAfterResources).
      this._repaginateAfterResources();
    }, 100);
    
    // Sprawdzaj podział na strony co 500ms
    this.pageCheckInterval = setInterval(() => {
      this.calculatePages();
    }, 500);
  }

  ngOnDestroy(): void {
    this._isDestroyed = true;
    if (this.pageCheckInterval) {
      clearInterval(this.pageCheckInterval);
    }
    // Wiszący debounce persist po zniszczeniu komponentu sięgałby po zdjęty DOM
    // (w testach: „document is not defined" po teardownie środowiska).
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
    const doc = document as Document & { __wysiwygSelListener?: () => void };
    if (doc.__wysiwygSelListener === this._onDocumentSelectionChange) {
      document.removeEventListener('selectionchange', this._onDocumentSelectionChange);
      delete doc.__wysiwygSelListener;
    }
  }

  /**
   * Zaczyna obserwować pasmo aktywnej sekcji (header/footer) i emituje jego zmierzoną
   * geometrię w cm od górnej krawędzi strony 1. Re-emituje przy zmianie wysokości pasma
   * (np. po załadowaniu obrazu w nagłówku).
   */
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

  /** Mierzy pasmo względem strony (uwzględnia skalę zoomu) i emituje cm od góry strony. */
  private emitSectionGeometry(section: 'header' | 'footer', band: HTMLElement): void {
    const page = band.closest('.page') as HTMLElement | null;
    if (!page) return;
    const pr = page.getBoundingClientRect();
    const br = band.getBoundingClientRect();
    // Skala niezależna od wzrostu pasma: z szerokości strony wg geometrii TEJ strony
    // (per sekcja; wcześniej stała A4).
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

  /**
   * Emituje aktualną liczbę stron (= długość `pageContents`, czyli zgodną
   * z wizualnym podziałem po repaginacji Wariantu A).
   */
  private calculatePages(): void {
    const pageCount = Math.max(1, this.pageContents().length);
    if (pageCount !== this.lastEmittedPageCount) {
      this.lastEmittedPageCount = pageCount;
      this.pagesChange.emit(pageCount);
    }
  }

  /** Zwraca innerHTML edytora (legacy helper – wcześniej usuwał wstrzykiwane separatory stron). */
  private _getCleanEditorHtml(editor: HTMLElement): string {
    return editor.innerHTML;
  }

  /**
   * Inicjalizuje edytor
   */
  private initializeEditor(): void {
    // pageContents jest już zainicjalizowany (np. przez setter content/setContent).
    // Po renderze Angular nakłada [innerHTML] na każdą stronę.
    // Tu opakowujemy obrazki i robimy snapshot dla isModified.
    const editor = this.editorContent?.nativeElement;
    if (!editor) return;

    this.wrapExistingImages();
    this.lastSavedContent = this._getCleanEditorHtml(editor);
    this.saveToUndoStack();
    this.updateState();
  }

  /**
   * Konfiguruje nasłuchiwanie zdarzeń
   */
  private setupEventListeners(): void {
    const refs = this.pageEditorRefs?.toArray() ?? [];
    // Globalne nasłuchiwanie selekcji — JEDEN listener na document, ale zawsze bieżącej
    // instancji. Dawny flag boolean wiązał `this` pierwszego edytora na zawsze: po nawigacji
    // w SPA (admin → edytor, inny dokument) nowa instancja nie dostawała selectionchange,
    // więc toolbar tkwił w fallbacku „pierwszy run dokumentu" (Calibri 28 + bold z tytułu)
    // niezależnie od tego, gdzie stała karetka (2026-09-27).
    const doc = document as Document & { __wysiwygSelListener?: () => void };
    if (doc.__wysiwygSelListener !== this._onDocumentSelectionChange) {
      if (doc.__wysiwygSelListener) {
        document.removeEventListener('selectionchange', doc.__wysiwygSelListener);
      }
      document.addEventListener('selectionchange', this._onDocumentSelectionChange);
      doc.__wysiwygSelListener = this._onDocumentSelectionChange;
    }

    for (const ref of refs) {
      this.attachEditorListeners(ref.nativeElement);
    }
  }

  /**
   * Rejestruje pełen zestaw listenerów (input/paste/keydown/click/mousedown/drag/resize)
   * dla danego contenteditable. Używane dla każdej strony body oraz dla header/footer
   * (po pierwszym wejściu w edycję). Idempotentne — flag `__wysiwygBound`.
   */
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
    // Własna serializacja kopiowania: domyślna (przeglądarkowa) zapieka computed style
    // w spany i gubi kontekst częściowo zaznaczonych list/tabel; do schowka wchodziły też
    // wewnętrzne markery podziału paginacji i zakładki (duplikaty po wklejeniu).
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
      // Sticky formatting (Problem 10): leaving the editor ends the pending-style intent.
      // (The pickers set it AFTER the blur their toolbar click causes, so this is safe.)
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
      // Kliknięcie na wrapper - nie rób nic, selekcja jest obsługiwana w mousedown
      return;
    }

    this.clearSelectedImage();

    // Klik poza polem tekstowym (puste miejsce edytora) — schowaj obramowanie
    // edycyjne i znacznik kotwicy poprzedniego zaznaczenia.
    if (!target.closest('.docx-textbox')) {
      this.clearSelectedTextBox();
    }
  }

  private handleEditorMouseDown(event: MouseEvent): void {
    const target = event.target as HTMLElement;

    // --- Resize tabeli ---
    // Dwuklik/trójklik = intencja ZAZNACZENIA tekstu (słowo/akapit) — strefa resize nie
    // może go kraść: 6px od krawędzi komórki pokrywa dolną połowę glifów przy tcMar≈0,
    // więc dwuklik w słowo w ciasnej tabeli bywał połykany (preventDefault) do skutku.
    let tableHit = event.detail >= 2 ? null : this.detectTableResizeHit(event);
    if (tableHit) {
      // Empty cells have no glyphs, so the _pointOverText escape hatch never fires and the
      // 6px band claims most of a small cell's interior (freshly inserted tables have
      // <br>-only cells) — the click never placed a caret, so no selectionchange fired and
      // the table panel never appeared. Re-run the hit-test with a tight threshold: resize
      // stays possible on the exact border, the interior falls through to caret placement.
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

    // Kształt pass-through (ADR-0063): uchwyt resize / drag / selekcja.
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

    // Sprawdź czy kliknięto na wrapper obrazu lub jego zawartość
    const wrapper = target.closest('.editor-image-wrapper') as HTMLElement | null;
    if (!wrapper) {
      this.handleTextBoxMouseDown(event, target);
      return;
    }

    // Resize handle
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

        // KONTENER = aktywny edytor (body / header / footer) — wrapper.closest
        const editor = wrapper.closest('.editor-content, .header-editor-content, .footer-editor-content') as HTMLElement | null;
        const editorMaxWidth = (editor?.clientWidth || 900) - 30;
        const st = this.imageResizeState;
        const currentImg = st.wrapper.querySelector('img') as HTMLImageElement | null;
        if (!currentImg) return;

        if (st.axis === 'x') {
          // Rozciąganie tylko szerokości — wysokość zostaje stała
          const deltaX = moveEvent.clientX - st.startX;
          const newWidth = Math.max(60, Math.min(editorMaxWidth, st.startWidth + deltaX));
          st.wrapper.style.width = `${newWidth}px`;
          st.wrapper.style.maxWidth = '100%';
          currentImg.style.width = '100%';
          currentImg.style.height = `${st.startHeight}px`;
        } else if (st.axis === 'y') {
          // Rozciąganie tylko wysokości — szerokość zostaje stała
          const deltaY = moveEvent.clientY - st.startY;
          const newHeight = Math.max(30, st.startHeight + deltaY);
          st.wrapper.style.width = `${st.startWidth}px`;
          st.wrapper.style.maxWidth = '100%';
          currentImg.style.width = '100%';
          currentImg.style.height = `${newHeight}px`;
        } else {
          // Proporcjonalne skalowanie z narożnika
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

          // Po zakończeniu skalowania zapisujemy realny rozmiar na <img>
          // i aktualizujemy `data-width-emu` / `data-height-emu`, których
          // używa eksporter DOCX. Inaczej eksport używa ORYGINALNYCH wymiarów
          // EMU (z importu), ignorując zmianę w edytorze — i obraz w pliku
          // .docx jest dużo większy niż widać w edytorze.
          const finalWrapper = this.imageResizeState.wrapper;
          const finalImg = finalWrapper.querySelector('img') as HTMLImageElement | null;
          if (finalImg) {
            const rect = finalImg.getBoundingClientRect();
            const widthPx = Math.round(rect.width);
            const heightPx = Math.round(rect.height);
            if (widthPx > 0 && heightPx > 0) {
              finalImg.style.width = `${widthPx}px`;
              finalImg.style.height = `${heightPx}px`;
              // 1 px = 9525 EMU (przybliżenie używane też po stronie API)
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
        // Snapshot the new dimensions so the side panel reflects the post-resize state.
        this.emitImageSelectionState();
      };

      document.addEventListener('mousemove', onMouseMove);
      document.addEventListener('mouseup', onMouseUp);
      return;
    }

    // Kliknięcie / przeciąganie na wrapper obrazu (nie resize handle)
    event.preventDefault();
    this.selectImageWrapper(wrapper);

    const startX = event.clientX;
    const startY = event.clientY;
    const isFloating = wrapper.dataset['posMode'] === 'front' || wrapper.dataset['posMode'] === 'behind';
    this.imageMoveState = { wrapper, startX, startY, isDragging: false };

    // Floating drag — image stays absolutely positioned, drag updates left/top.
    if (isFloating) {
      const startLeft = parseInt(wrapper.style.left || '0', 10);
      const startTop = parseInt(wrapper.style.top || '0', 10);
      // Delty kursora są w px viewportu — przy zoomie (transform: scale) trzeba je
      // sprowadzić do px układu strony, inaczej obraz ucieka szybciej/wolniej niż kursor.
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
            // Drag w edytowanym paśmie nagłówka/stopki: left/top są w układzie PASMA —
            // data-emu (kontrakt: X od lewej strony, Y od góry obszaru treści) wymaga
            // przeliczenia, inaczej zapis DOCX przesuwa obraz o origin pasma.
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
        // Wyłącz pointer-events na wrapperze żeby getRangeFromPoint trafiał w tekst pod grafiką
        wrapper.style.pointerEvents = 'none';
        // Utwórz element wskazujący miejsce upuszczenia (kursor edytora)
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
        // Przywróć pointer-events i usuń kursor upuszczenia
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
            // Snapshot in case alignment changed because the drop landed in a paragraph
            // with different text-align.
            this.emitImageSelectionState();
          }
        }
      }
      // Sprzątanie po każdym mouse-up (także gdy nie było faktycznego drag)
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

  // ======= RESIZE TABEL =======

  private readonly TABLE_EDGE_THRESHOLD = 6; // px od krawędzi
  // Tight band used for cells with no visible text: without glyphs the _pointOverText guard
  // cannot fire, so the full 6px band would swallow most of the cell (see handleEditorMouseDown).
  private readonly EMPTY_CELL_EDGE_THRESHOLD = 2; // px od krawędzi

  /**
   * Wykrywa czy kursor jest nad krawędzią kolumny, wiersza lub narożnikiem tabeli
   */
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

    // Sprawdź narożnik tabeli (prawy dolny)
    const tableRect = table.getBoundingClientRect();
    if (
      Math.abs(event.clientX - tableRect.right) < t + 2 &&
      Math.abs(event.clientY - tableRect.bottom) < t + 2
    ) {
      return { type: 'table', table, colIndex: -1, rowIndex: -1 };
    }

    if (!td) return null;

    // Wskaźnik NAD glifami tekstu = intencja pracy z tekstem (karetka/zaznaczenie),
    // nie resize. Przy tcMar≈0 tekst kończy się tuż przy krawędzi komórki i 6px strefa
    // pokrywała dolną połowę glifów — klik/dwuklik w słowo był połykany do skutku.
    if (this._pointOverText(event.clientX, event.clientY)) return null;

    const cellRect = td.getBoundingClientRect();
    const rowIndex = (td.parentElement as HTMLTableRowElement).rowIndex;

    // Column boundaries are resolved in logical columns (td.cellIndex is wrong once the
    // row has colspan/rowspan gaps/spacers). columnBoundaryForCell returns the column
    // left of the boundary, or null when there is none (left edge of the first column).
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

    // Bottom edge = row boundary (row height is unambiguous, keep the DOM row index).
    if (Math.abs(event.clientY - cellRect.bottom) < t) {
      return { type: 'row', table, colIndex: td.cellIndex, rowIndex };
    }

    return null;
  }

  /**
   * Czy punkt (viewport px) leży w prostokącie glifów tekstu — sąsiedztwo pozycji
   * z caretRangeFromPoint rozszerzone o 1 znak w obie strony. Czyste odczyty layoutu
   * (bez zapisów — pułapka ADR-0075 nie dotyczy).
   */
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

  /**
   * Zmienia kursor nad krawędziami tabeli
   */
  private handleTableResizeCursor(event: MouseEvent): void {
    // Nie zmieniaj kursora podczas aktywnego resize / przenoszenia obrazu
    if (this.tableResizeState || this.imageResizeState || this.imageMoveState) return;

    const target = event.target as HTMLElement;
    const td = target.closest('td, th') as HTMLTableCellElement | null;
    const table = target.closest('table') as HTMLTableElement | null;

    if (!table || !td) {
      // Reset kursora na komórkach które miały zmieniony kursor
      if (this._lastCursorCell) {
        this._lastCursorCell.style.cursor = '';
        this._lastCursorCell = null;
      }
      return;
    }

    const hit = this.detectTableResizeHit(event);

    // Wyczyść poprzednią komórkę
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

  /**
   * Normalizuje tabele - ustawia stałe szerokości kolumn jeśli brak
   */
  private ensureTableColWidths(table: HTMLTableElement): void {
    const firstRow = table.rows[0];
    if (!firstRow) return;

    // Sprawdź czy kolumny mają już ustawione szerokości
    const hasWidths = Array.from(firstRow.cells).every(c => !!c.style.width);
    if (hasWidths) return;

    // Zmierz aktualne i ustaw px
    const cells = Array.from(firstRow.cells);
    const widths = cells.map(c => c.getBoundingClientRect().width);
    cells.forEach((c, i) => {
      c.style.width = `${widths[i]}px`;
    });

    // Ustaw table-layout: fixed
    table.style.tableLayout = 'fixed';
  }

  /**
   * Rozpoczyna resize tabeli
   */
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

    // Zablokuj zaznaczanie tekstu i wymusz kursor na całym dokumencie
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
      // Po zmianie szerokości kolumn/tabeli przelicz <colgroup> — to z niego eksport
      // odtwarza w:tblGrid; bez synchronizacji zapis wracał ze starą geometrią kolumn.
      // Widths come from the logical grid, so merged/spacer tables stay correct even
      // when no "clean" row exists to measure.
      // Sam klik w strefie krawędzi (zero ruchu) nie jest edycją — bez commitu
      // dokument nie brudzi się (undo/persist/repaginacja) od nieudanych kliknięć.
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

  /**
   * Resize kolumny - przesuwa granicę między kolumnami
   */
  private resizeTableColumn(
    st: NonNullable<typeof this.tableResizeState>,
    moveEvent: MouseEvent
  ): void {
    const deltaX = moveEvent.clientX - st.startX;
    const b = st.boundaryCol;
    const columnCount = st.grid.columnCount;
    const minW = 40;

    // Recompute from the immutable start widths (not the last frame) to avoid drift.
    const widths = [...st.startColumnWidths];

    if (b < columnCount - 1) {
      // Internal boundary: transfer width between column b and b+1, conserving their sum.
      const available = st.startColumnWidths[b] + st.startColumnWidths[b + 1];
      const newLeft = Math.min(Math.max(minW, st.startColumnWidths[b] + deltaX), available - minW);
      widths[b] = newLeft;
      widths[b + 1] = available - newLeft;
    } else {
      // Last column: grow/shrink the column and the whole table with it.
      widths[b] = Math.max(minW, st.startColumnWidths[b] + deltaX);
    }

    st.columnWidths = widths;
    // Each cell's width = sum of the logical columns it spans, so every row stays aligned.
    applyColumnWidths(st.table, st.grid, widths);
  }

  /**
   * Resize wiersza - zmienia wysokość
   */
  private resizeTableRow(
    st: NonNullable<typeof this.tableResizeState>,
    moveEvent: MouseEvent
  ): void {
    const deltaY = moveEvent.clientY - st.startY;
    const newHeight = Math.max(20, Math.round(st.startHeight + deltaY));
    const row = st.table.rows[st.rowIndex];
    if (row) {
      row.style.height = `${newHeight}px`;
      // Ręczna zmiana wysokości unieważnia dokładne twips/regułę z importu — inaczej
      // eksport użyłby STAREJ wartości z data-* zamiast nowej wysokości px (atLeast).
      row.removeAttribute('data-row-height-tw');
      row.removeAttribute('data-row-hrule');
    }
  }

  /**
   * Resize całej tabeli - skaluje proporcjonalnie
   */
  private resizeWholeTable(
    st: NonNullable<typeof this.tableResizeState>,
    moveEvent: MouseEvent
  ): void {
    const deltaX = moveEvent.clientX - st.startX;
    const editor = this.editorContent?.nativeElement;
    const maxW = (editor?.clientWidth || 900) - 20;
    const newTableWidth = Math.max(200, Math.min(maxW, st.startTableWidth + deltaX));
    const ratio = st.startTableWidth > 0 ? newTableWidth / st.startTableWidth : 1;

    // Scale every logical column proportionally; cell widths follow via the grid.
    const widths = st.startColumnWidths.map(w => (w > 0 ? w * ratio : 0));
    st.columnWidths = widths;

    if (widths.some(w => w > 0)) {
      applyColumnWidths(st.table, st.grid, widths);
    } else {
      // No known column widths (e.g. percentage table not yet measured) — fall back
      // to sizing the table alone; the browser distributes across columns.
      st.table.style.width = `${newTableWidth}px`;
    }
  }

  // ======= KONIEC RESIZE TABEL =======

  private handleEditorDragStart(event: DragEvent): void {
    const rawTarget = event.target as Node | null;
    // event.target może być węzłem tekstowym (np. podczas drag-zaznaczenia tekstu)
    // — wtedy nie ma metody closest(). Bierzemy najbliższy element nadrzędny.
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

    // Kotwica jak w Wordzie: widoczna tylko dla elementu PŁYWAJĄCEGO (front/behind);
    // obraz inline płynie z tekstem i kotwicy nie ma.
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

  // ======= POLA TEKSTOWE (div.docx-textbox) =======

  /**
   * Zaznaczenie / start przeciągania pola tekstowego. Wnętrze pola pozostaje zwykłą
   * treścią contenteditable (klik = edycja tekstu, jak w Wordzie); drag rusza wyłącznie
   * z pasa krawędzi pływającego pola, żeby nie kraść kliknięć przeznaczonych dla tekstu.
   */
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

  // ── Kształty pass-through (ADR-0063): selekcja / drag / resize ────────────
  // Podgląd kształtu jest pochodną data-docx-xml (writer odtwarza grafikę z XML, nie z DOM),
  // więc KAŻDA zmiana pozycji/rozmiaru musi aktualizować i style (widok), i XML markera —
  // inaczej ginie przy pierwszym zapisie.

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

  /** Drag pływającego kształtu — delty przez skalę zoomu, jak textboxy (ADR-0030). */
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

  /** Proporcjonalny resize z narożnika SE; podgląd i XML skalowane tym samym faktorem. */
  private startShapeResize(event: MouseEvent, shape: HTMLElement): void {
    const page = shape.closest('.page') as HTMLElement | null;
    const scale = page ? this._pageVisualScale(page) : 1;
    const startX = event.clientX;
    const startW = parseFloat(shape.style.width || '0') || shape.getBoundingClientRect().width / scale;
    const startH = parseFloat(shape.style.height || '0') || shape.getBoundingClientRect().height / scale;
    if (startW <= 0 || startH <= 0) return;
    // Snapshot geometrii — każdy ruch liczy ŚWIEŻY faktor od oryginału (bez kumulacji
    // błędów zaokrągleń przy skalowaniu przyrostowym).
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

  /** Kontekst kształtu: edytowane pasmo (header/footer) albo body. */
  private _shapeContext(shape: HTMLElement): { band: 'header' | 'footer' | null; pageIndex: number } {
    if (shape.closest('.header-editor-content')) return { band: 'header', pageIndex: this.editingHfPageIndex() };
    if (shape.closest('.footer-editor-content')) return { band: 'footer', pageIndex: this.editingHfPageIndex() };
    const page = shape.closest('.page') as HTMLElement | null;
    const n = Number(page?.getAttribute('data-page-number') ?? '1');
    return { band: null, pageIndex: Number.isFinite(n) && n >= 1 ? n - 1 : this.activePageIndex() };
  }

  /** Po dragu: styl → współrzędne KONTRAKTU → wp:anchor (page-relative EMU) w data-docx-xml. */
  private _syncShapeXmlPosition(shape: HTMLElement): void {
    const { band, pageIndex } = this._shapeContext(shape);
    let contractLeft = parseFloat(shape.style.left || '0');
    let contractTop = parseFloat(shape.style.top || '0');
    if (band) {
      const c = bandToContract(contractLeft, contractTop, this._bandGeoFor(pageIndex, band));
      contractLeft = c.xPx;
      contractTop = c.yPx;
      // Stash edycji pasma musi nieść NOWY kontrakt — commit przywraca z niego oryginał.
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

  /** Po resize: skala → wp:extent + root a:xfrm/a:ext (+ VML fallback) w data-docx-xml. */
  private _syncShapeXmlScale(shape: HTMLElement, fx: number, fy: number): void {
    const b64 = shape.getAttribute('data-docx-xml');
    const doc = b64 ? decodeShapeXml(b64) : null;
    if (!doc) return;
    if (scaleShapeExtent(doc, fx, fy)) {
      shape.setAttribute('data-docx-xml', encodeShapeXml(doc));
    }
  }

  /** Zmiana kształtu = zmiana treści właściciela (pasmo → model pasma, body → persist). */
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

  /**
   * Przeciąganie pływającego pola tekstowego. Delty kursora (px viewportu) są dzielone
   * przez aktualną skalę zoomu strony — bez tego przy zoomie ≠ 100% pole „uciekało"
   * szybciej/wolniej niż kursor. Kotwica pozostaje przy TYM SAMYM akapicie (pozycja divu
   * w DOM się nie zmienia) — drag aktualizuje wyłącznie offsety; to przewidywalna reguła
   * bez skoków pozycji przy zmianie kotwicy.
   */
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

  /**
   * Kursor „move" na pasie krawędzi pływającego pola tekstowego (klasa tb-edge — usuwana
   * przy serializacji). Wnętrze zachowuje kursor tekstowy.
   */
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

  // ======= ZNACZNIK KOTWICY =======

  /** Aktualna skala wizualna strony (transform zoomu): szerokość rect / szerokość layoutu. */
  private _pageVisualScale(page: HTMLElement): number {
    const rect = page.getBoundingClientRect();
    return page.offsetWidth > 0 && rect.width > 0 ? rect.width / page.offsetWidth : 1;
  }

  /**
   * Pokazuje znacznik kotwicy przy akapicie-kotwicy zaznaczonego elementu pływającego.
   * Znacznik jest dzieckiem .page (POZA contenteditable → nigdy nie trafia do getContent),
   * pozycjonowanym w px układu strony — transform zoomu i scroll przesuwają go razem
   * z treścią bez przeliczeń. `pointer-events:none` + `aria-label`: element czysto
   * informacyjny, nie przechwytuje kliknięć ani fokusu.
   */
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

  /** Repozycjonowanie znacznika po zmianach treści/układu (koalescencja przez rAF). */
  private _scheduleAnchorBadgeRefresh(): void {
    if (this._anchorBadgeRafHandle !== null || !this.anchorBadgeTarget) return;
    this._anchorBadgeRafHandle = requestAnimationFrame(() => {
      this._anchorBadgeRafHandle = null;
      this.refreshAnchorBadge();
    });
  }

  /** Po edycji/repaginacji: element usunięty → znacznik znika; przesunięty → jedzie za akapitem. */
  private refreshAnchorBadge(): void {
    const target = this.anchorBadgeTarget;
    if (!target) return;
    if (!target.isConnected) {
      this.hideAnchorBadge();
      return;
    }
    this.showAnchorBadgeFor(target);
  }

  /**
   * Snapshots the currently selected image wrapper into the wire shape consumed by
   * d2-image-properties-panel. Reads dimensions from the rendered &lt;img&gt;'s bounding
   * rect (covers both inline width/height and zoom). Alignment is detected by inspecting
   * the parent paragraph's text-align (Word-like alignment lives on the paragraph, not
   * the image element).
   */
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

  /**
   * Switches the selected image between in-text (inline) and floating (front/behind).
   * Word-like semantics — floating uses position: absolute within the closest .page
   * container, with z-index controlling the front/behind layering. The mode is mirrored
   * to both the wrapper (CSS hook) and the inner &lt;img&gt; (so the DOCX exporter sees it
   * on the round-trippable element) so import / export can reproduce it.
   */
  setSelectedImagePositionMode(mode: 'inline' | 'square' | 'topBottom' | 'front' | 'behind'): void {
    const wrapper = this.selectedImageWrapper;
    if (!wrapper) return;
    const img = wrapper.querySelector('img') as HTMLImageElement | null;
    if (!img) return;

    // Always clear previous floating-only state, then re-apply per the new mode.
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
      // Float-based wrap — text fills around the rectangular bounding box.
      wrapper.dataset['posMode'] = 'square';
      img.dataset['posMode'] = 'square';
      wrapper.style.float = 'left';
      wrapper.style.margin = '0 12px 8px 0';
    } else if (mode === 'topBottom') {
      // Block + clear:both — text sits above and below the image, not beside it.
      wrapper.dataset['posMode'] = 'topBottom';
      img.dataset['posMode'] = 'topBottom';
      wrapper.style.display = 'block';
      wrapper.style.clear = 'both';
      wrapper.style.margin = '8px auto';
    } else {
      // Anchor the wrapper at its current rendered position so the switch is visually
      // stable — no jump to (0,0). Coords are relative to the nearest paginated page.
      const page = wrapper.closest('.page, .editor-content, .header-editor-content, .footer-editor-content') as HTMLElement | null;
      const pageRect = page?.getBoundingClientRect();
      const rect = wrapper.getBoundingClientRect();
      const xPx = Math.max(0, Math.round(rect.left - (pageRect?.left ?? 0)));
      const yPx = Math.max(0, Math.round(rect.top - (pageRect?.top ?? 0)));
      // W edytowanym paśmie współrzędne rect są w układzie PASMA — kontrakt (data-emu)
      // wymaga przeliczenia, inaczej zapis DOCX przesunie obraz o origin pasma.
      const bandGeo = this._bandGeoForElement(img);
      const contract = bandGeo ? bandToContract(xPx, yPx, bandGeo) : undefined;
      this.applyFloatingPosition(wrapper, img, mode as 'front' | 'behind', xPx, yPx,
        contract ? { xPx: Math.round(contract.xPx), yPx: Math.round(contract.yPx) } : undefined);
    }
    this.onContentChange();
    this.emitImageSelectionState();
  }

  /**
   * Border state — when enabled, applied as inline CSS on the img so it survives the
   * HTML round-trip; mirrored to data-border-* attributes for the DOCX exporter, which
   * maps them to a:ln in pic:spPr.
   */
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

  /**
   * Crop state — % from each side. Rendered with CSS clip-path: inset() so the
   * original raster stays intact. Persisted via data-crop-* attrs the DOCX exporter
   * maps to a:srcRect (Word's "trim from each side" model).
   */
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

  /**
   * `xPx/yPx` = pozycja STYLU (left/top w układzie kontenera absolutu). `contract` — pozycja w
   * układzie KONTRAKTU (data-x/y-emu: X od lewej krawędzi strony, Y od góry obszaru treści);
   * w body oba układy są tożsame (brak param), w pasmach nagłówka/stopki różnią się o origin
   * pasma i MUSZĄ być podane osobno — inaczej zapis DOCX przesunie obraz.
   */
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
      // Allow text clicks to land through the wrapper when it sits visually behind.
      wrapper.style.pointerEvents = 'auto';
    } else {
      wrapper.style.zIndex = '10';
      wrapper.style.pointerEvents = 'auto';
    }
  }

  /** Apply a new width (px) to the selected image; height follows aspect when locked. */
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

  /** Apply a new height (px); width follows aspect when locked. */
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

  /** Re-stretches the image to its intrinsic aspect ratio at the current width. */
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

  /** Align the paragraph containing the selected image (null = clear text-align). */
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

  /** Removes the currently selected image from the DOM and notifies the editor. */
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
    // Keep the EMU data-attributes in sync so the DOCX exporter re-emits the new size.
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

  /**
   * Publiczny trigger zmiany zawartości — używany przez parenta po edycji DOM,
   * której wysywig nie obserwuje (np. linijka modyfikuje margin-left bloku).
   */
  triggerContentChange(): void {
    this.onContentChange();
  }

  /**
   * Obsługa zmiany zawartości — automatycznie kieruje na body lub header/footer
   * zależnie od aktualnie edytowanej sekcji.
   */
  private onContentChange(): void {
    const section = this.editingSection();

    // Header / footer — zapis do WŁAŚCIWEGO wariantu (sekcja/first-page/default) + emit
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

    // Body
    const editor = this.editorContent?.nativeElement;
    if (!editor) return;

    const html = editor.innerHTML;
    this._isInternalUpdate = true;
    this._content.set(html);
    this.contentChange.emit(html);
    this._isInternalUpdate = false;
    this.saveToUndoStack();
    this.updateState();
    this.updateFormattingState(); // Aktualizuj też stan formatowania
    // Znacznik kotwicy podąża za akapitem-kotwicą po każdej zmianie treści
    // (usunięcie elementu → znacznik znika; zmiana układu → repozycjonowanie).
    this._scheduleAnchorBadgeRefresh();
  }

  /**
   * Obsługa zmiany selekcji
   */
  private onSelectionChange(): void {
    const selection = window.getSelection();

    if (selection && this.isSelectionInEditor(selection)) {
      // Sticky formatting (Problem 10): the pending style is tied to ONE caret position —
      // clicking or arrowing away means the intent is gone. Our own programmatic re-anchor
      // is protected by a short hold window (see _consumePendingDeleteCapture).
      if (
        this._pendingInlineStyle &&
        Date.now() >= this._pendingStyleHoldUntil &&
        !this._caretMatchesPendingAnchor(selection)
      ) {
        this._clearPendingInlineStyle();
      }
      // Zapisuj selekcję NA BIEŻĄCO, nie tylko na `blur`. Klik w pole toolbara (np. ręczny
      // input rozmiaru czcionki) przenosi fokus poza edytor — zdarzenie `blur` bywa zbyt późne
      // (selekcja contenteditable już znika), więc `saveSelection()` na blur nic nie zapisywał
      // i `setFontSize` nie miał czego odtworzyć → „utrata zaznaczenia / tekst znika". Ciągły
      // zapis gwarantuje świeżą `savedSelection` z ostatniej realnej pozycji w edytorze.
      this.saveSelection();
      this.updateFormattingState();
      this.selectionChange.emit(selection);
    }
  }

  /**
   * Sprawdza czy selekcja jest w edytorze (body lub header/footer).
   * Sprawdzamy WSZYSTKIE strony (multi-page) — nie tylko aktywną, bo
   * `editorContent` ref aktualizuje się dopiero na focusin, a `selectionchange`
   * fire'uje też dla strony, na której kursor już jest.
   */
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

  /**
   * Returns the page `.editor-content` element hosting the given node (multi-page aware),
   * or null when the node lives outside every page. Callers that resolved only the ACTIVE
   * page ref (`editorContent`) treated carets on other pages as "outside the editor" —
   * e.g. the table panel never appeared for tables on page 2+ (Problem 11).
   */
  findEditorContentContaining(node: Node | null | undefined): HTMLElement | null {
    if (!node) return null;
    for (const ref of this.pageEditorRefs?.toArray() ?? []) {
      if (ref.nativeElement.contains(node)) return ref.nativeElement;
    }
    return null;
  }

  /**
   * Znacznik czasu, do którego najbliższe zdarzenie `paste` ma zostać potraktowane
   * jako „Wklej tylko tekst" (ustawiany skrótem Ctrl/Cmd+Shift+V w handleKeyboard).
   * Używamy okna czasowego zamiast bool, żeby nieużyty skrót nie „zatruł" kolejnego
   * zwykłego Ctrl+V.
   */
  private plainTextPasteUntil = 0;

  /** Zwraca true i konsumuje żądanie, jeśli bieżące wklejenie ma być czystym tekstem. */
  private consumePlainTextPasteRequest(): boolean {
    const requested = Date.now() < this.plainTextPasteUntil;
    this.plainTextPasteUntil = 0;
    return requested;
  }

  /**
   * Obsługa wklejania.
   *
   * Ctrl/Cmd+Shift+V → zawsze czysty tekst (preferuj text/plain, fallback z HTML).
   * Ctrl/Cmd+V → zachowaj formatowanie po sanitizacji; gdy brak HTML, zwykły tekst.
   */
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

  // ═════════════ Schowek — deterministyczny pipeline (copy/cut/paste) ═════════════
  // Objawy sprzed zmiany: „wytnij/kopiuj/wklej zmienia formatowanie, rozjazdy w tekście,
  // listach i tabelach". Przyczyny: (1) domyślna serializacja kopiowania Chrome zapieka
  // computed style i nie niesie tożsamości list (data-num-*)/kolumn tabel; (2) gołe
  // execCommand('insertHTML') pozwalało wkleić wewnętrzne markery podziału paginacji
  // (getContent SKLEJA tabele po data-split-table-id → korupcja!), duplikaty zakładek
  // i śmieci Worda (mso-*, o:p); (3) blok wklejony w środek akapitu lądował W <p>
  // (np. table-in-p), czego writer nie umie odczytać.

  /** Kopiuj/Wytnij: własny payload text/html + text/plain. Puste zaznaczenie → default. */
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

  /**
   * Buduje HTML schowka z zaznaczenia: klon fragmentu + odtworzone kontenery częściowo
   * zaznaczonych list/tabel (z ich atrybutami i colgroup — tożsamość i szerokości przeżywają),
   * bez wewnętrznych artefaktów (markery podziału, zakładki). Wrapper `data-d2-clip`
   * pozwala odróżnić wklejkę własną (zaufaną) od zewnętrznej.
   */
  private buildClipboardHtml(range: Range): string {
    const holder = document.createElement('div');
    holder.appendChild(range.cloneContents());

    this.rewrapClipboardOrphans(holder, range);
    this.rewrapInlineFormatting(holder, range);
    this.stripClipboardArtifacts(holder);

    const wrapper = document.createElement('div');
    wrapper.setAttribute('data-d2-clip', '1');
    while (holder.firstChild) wrapper.appendChild(holder.firstChild);
    return wrapper.outerHTML;
  }

  /**
   * Częściowe zaznaczenie listy/tabeli klonuje się bez kontenera (gołe LI / TR / TD) —
   * wklejone tak, jak jest, gubi tożsamość listy (numeracja od 1, zły format) i strukturę
   * tabeli. Odtwarzamy powłoki z ORYGINAŁU (shallow clone z atrybutami; tabela dostaje
   * klon colgroup — szerokości kolumn). Kolejność przebiegów: TD→TR zanim TR→TABLE.
   */
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
      // Zaznaczenie od akapitu w głąb listy: commonAncestor leży NAD listą — powłokę
      // odzyskujemy z końców zakresu (gołe <ul> bez kontraktu/stylów wklejałoby się
      // z domyślnym wcięciem SCSS zamiast wcięcia źródła).
      const list = anchor?.closest('ul,ol')
        ?? this._closestList(range.startContainer)
        ?? this._closestList(range.endContainer);
      return list ? (list.cloneNode(false) as HTMLElement) : document.createElement('ul');
    });
  }

  /**
   * Zaznaczenie W CAŁOŚCI wewnątrz sformatowanego fragmentu (typowy run z DOCX:
   * `<span style="font-family…;font-size…;color…">`, `<strong>`, link) klonuje się BEZ tych
   * opakowań — `cloneContents` oddaje tylko to, co leży POD wspólnym przodkiem. Skopiowane
   * zdanie traciło font/rozmiar/kolor i po wklejeniu przyjmowało formatowanie celu. Odtwarzamy
   * łańcuch przodków inline (płytkie klony) od wspólnego przodka do bloku. Spany strukturalne
   * (z klasą: segmenty tabulatorów, formanty, pola) nie są formatowaniem — pomijane.
   */
  private rewrapInlineFormatting(holder: HTMLElement, range: Range): void {
    const INLINE_FORMAT_TAGS = new Set(['SPAN', 'B', 'STRONG', 'I', 'EM', 'U', 'S', 'STRIKE', 'SUB', 'SUP', 'A', 'FONT']);
    const common = range.commonAncestorContainer;
    let el = common instanceof Element ? common : common.parentElement;
    while (el && INLINE_FORMAT_TAGS.has(el.tagName)) {
      if (el.tagName !== 'SPAN' || el.classList.length === 0) {
        const shell = el.cloneNode(false) as HTMLElement;
        shell.removeAttribute('id');
        while (holder.firstChild) shell.appendChild(holder.firstChild);
        holder.appendChild(shell);
      }
      el = el.parentElement;
    }
  }

  /** Najbliższy kontener listy nad węzłem (tekstowym lub elementem). */
  private _closestList(node: Node): HTMLElement | null {
    const el = node instanceof Element ? node : node.parentElement;
    return el?.closest('ul,ol') ?? null;
  }

  /** Artefakty, które NIGDY nie mogą wejść do schowka ani wrócić z wklejką. */
  private stripClipboardArtifacts(root: ParentNode): void {
    // Markery podziału paginacji: getContent scala tabele/wiersze po tych id — wklejona
    // kopia z tym samym id zostałaby SKLEJONA z oryginałem przy zapisie.
    root.querySelectorAll('tr[data-repeated-header]').forEach(el => el.remove());
    root.querySelectorAll('[data-split-table-id],[data-split-row-id],[data-split-cont]').forEach(el => {
      el.removeAttribute('data-split-table-id');
      el.removeAttribute('data-split-row-id');
      el.removeAttribute('data-split-cont');
    });
    // Zakładki: duplikat data-bm-name → writer emituje drugi bookmarkStart o tej samej
    // nazwie (ryzyko „naprawy" pliku w Wordzie). Cel pozostaje w miejscu oryginału.
    root.querySelectorAll('.docx-bookmark').forEach(el => el.remove());
  }

  /**
   * Parsuje i czyści HTML ze schowka. Wklejka własna (marker data-d2-clip) przechodzi
   * lekką ścieżką (tylko artefakty); zewnętrzna (Word/przeglądarka) — pełną sanitizację
   * z allowlistami tagów i stylów. Zwraca null, gdy po czyszczeniu nic nie zostało
   * (wtedy wklejamy czysty tekst).
   */
  private prepareClipboardFragment(html: string): DocumentFragment | null {
    const tpl = document.createElement('template');
    tpl.innerHTML = html; // template nie wykonuje skryptów ani nie ładuje zasobów

    tpl.content.querySelectorAll('script,style,link,meta,title,iframe,object,embed,base,form,input,button,textarea,select')
      .forEach(n => n.remove());

    const internal = tpl.content.querySelector('[data-d2-clip]');
    if (!internal) {
      this.sanitizeExternalClipboardHtml(tpl.content, html);
    }
    // Pas bezpieczeństwa także dla wklejek wewnętrznych (i starych kopii sprzed tej zmiany).
    this.stripClipboardArtifacts(tpl.content);

    const source: ParentNode = internal ?? tpl.content;
    const out = document.createDocumentFragment();
    while (source.firstChild) out.appendChild(source.firstChild);
    return out.childNodes.length > 0 ? out : null;
  }

  /**
   * Sanitizacja HTML spoza edytora (Word, strony WWW): allowlista tagów (reszta odpakowana,
   * treść zostaje), allowlista właściwości inline (mso-*, position, marginesy Worda — precz;
   * szerokości/obramowania zostają na elementach tabel), zrzucone klasy/id/handlery,
   * obrazki tylko data: (CSP i tak blokuje zdalne), międzywyrazowe twarde spacje → zwykłe
   * (Word HTML jest nimi upstrzony — bez tego „rozjazdy w tekście", ADR-0083).
   */
  private sanitizeExternalClipboardHtml(root: DocumentFragment, rawHtml = ''): void {
    // Listy Worda to akapity `mso-list` z literalnym punktorem w treści — zamień je na ul/ol
    // ZANIM allowlista zdejmie `mso-*` (potem nie da się ich już rozpoznać).
    const trustedListNodes = convertWordListParagraphs(root, rawHtml, () => this._uniqueListInstanceId());

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
      // Odpakowany/usunięty przez wcześniejszą iterację. UWAGA: nie isConnected —
      // w DocumentFragment jest zawsze false i pomijałby WSZYSTKO.
      if (!root.contains(el)) continue;
      // Kontenery/znaczniki list zbudowane przez konwerter Worda niosą kontrakt data-* — nietykalne.
      if (trustedListNodes.has(el)) continue;

      if (!ALLOWED_TAGS.has(el.tagName)) {
        // Odpakuj (o:p, font, section, article…) — treść przechodzi dalej.
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

      // Atrybuty: zostaje wyłącznie przefiltrowany style (+ src/alt obrazka, href linku,
      // colspan/rowspan/span komórek). Klasy (Mso*), id, contenteditable, data-* — precz.
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

    // Puste spany bez atrybutów — odpakuj (Chrome/Word potrafią ich nawrzucać setki).
    root.querySelectorAll('span').forEach(span => {
      if (span.attributes.length === 0) span.replaceWith(...Array.from(span.childNodes));
    });

    // Białe znaki ŹRÓDŁA HTML: Word łamie kod co ~80 znaków (także W ŚRODKU zdań) i rozdziela
    // akapity znakami nowej linii; kontenery treści edytora mają `white-space: pre-wrap`, więc
    // każdy taki znak stawał się widocznym wierszem (dziura na pół strony po wklejce, zdania
    // łamane w losowych miejscach). Normalizujemy jak parser HTML w trybie `normal`:
    // ciąg białych znaków z nową linią → jedna spacja; węzły z samych białych znaków przy
    // blokach (i komentarze StartFragment/EndFragment) — precz.
    const BLOCKISH = new Set(['P', 'DIV', 'UL', 'OL', 'LI', 'TABLE', 'THEAD', 'TBODY', 'TFOOT', 'TR', 'TD', 'TH',
      'COLGROUP', 'COL', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'BLOCKQUOTE']);
    const isBlockish = (n: Node | null) => n instanceof Element && BLOCKISH.has(n.tagName);
    const junk: Node[] = [];
    const sourceWalker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT | NodeFilter.SHOW_COMMENT);
    for (let node = sourceWalker.nextNode(); node; node = sourceWalker.nextNode()) {
      if (node.nodeType === Node.COMMENT_NODE) { junk.push(node); continue; }
      const value = node.nodeValue ?? '';
      if (/^[ \t\r\n]*$/.test(value)) {
        const parent = node.parentNode;
        const structural = parent === root || (isBlockish(parent) && !/^(P|LI|TD|TH|H[1-6]|DIV|BLOCKQUOTE)$/.test((parent as Element).tagName));
        if (/[\r\n]/.test(value) && (structural || isBlockish(node.previousSibling) || isBlockish(node.nextSibling)
          || (isBlockish(parent) && (!node.previousSibling || !node.nextSibling)))) {
          junk.push(node);
          continue;
        }
      }
      if (/[\r\n]/.test(value)) node.nodeValue = value.replace(/[ \t]*[\r\n]+[ \t\r\n]*/g, ' ');
    }
    junk.forEach(n => n.parentNode?.removeChild(n));

    // Międzywyrazowe twarde spacje → zwykłe (jak commit przypisów, ADR-0083).
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const value = node.nodeValue ?? '';
      if (!value.includes('\u00A0')) continue;
      const cleaned = value.replace(/(?<=\S)\u00A0|\u00A0(?=\S)/g, ' ');
      if (cleaned !== value) node.nodeValue = cleaned;
    }
  }

  /**
   * Wstawia fragment w kursor Z ROZRÓŻNIENIEM inline/blok. Fragment czysto inline idzie
   * wprost w miejsce kursora. Fragment z blokami dzieli bieżący akapit na pół (jak Word):
   * wiodące inline'y doklejają się do pierwszej połówki, bloki wchodzą MIĘDZY połówki na
   * poziomie bloków (koniec z <table> wewnątrz <p>, których writer nie czyta), końcowe
   * inline'y otwierają drugą połówkę. Puste połówki są sprzątane.
   */
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

  /** Wstawianie blokowe: split akapitu-gospodarza + dystrybucja inline/blok (patrz wyżej). */
  private insertBlockNodesAtCaret(
    range: Range,
    nodes: Node[],
    isBlock: (n: Node) => boolean,
    editor: HTMLElement,
  ): Node | null {
    const block = this._nearestBlockElement(range.startContainer, editor);
    let last: Node | null = null;

    // Wklejka listy z kursorem w elemencie listy: elementy scalamy do listy-gospodarza
    // jako rodzeństwo (jak Word). Zagnieżdżenie ul/ol wewnątrz li sumowałoby wcięcia
    // (padding-left kontraktu + wcięcie gospodarza) — punktor odjeżdżał od krawędzi,
    // a numeracja zaczynała się od nowa.
    if (block?.tagName === 'LI' && block.parentElement
        && (block.parentElement.tagName === 'UL' || block.parentElement.tagName === 'OL')
        && nodes.filter(isBlock).every(n => (n as Element).tagName === 'UL' || (n as Element).tagName === 'OL')) {
      return this.mergeListNodesIntoHostList(range, nodes, block, isBlock);
    }

    // Rozcinamy wyłącznie bloki „akapitowe". TD/TH rozcięte klonem = dodatkowa komórka
    // w wierszu; DIV bywa sdt-blockiem (klon = zduplikowany formant przy zapisie);
    // LI rozcięte wpuściłoby blok jako dziecko UL/OL. Te kontenery legalnie mieszczą
    // bloki w środku — wstawiamy sekwencyjnie w miejscu kursora.
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

    // Sprzątanie pustych połówek — bez osieroconych pustych akapitów wokół wklejki.
    const isEmpty = (el: HTMLElement) => (el.textContent ?? '').length === 0 && el.children.length === 0;
    if (isEmpty(after)) after.remove();
    if (isEmpty(block)) block.remove();

    return last;
  }

  /**
   * Scala wklejane listy do listy-gospodarza: element pod kursorem dzielimy jak akapit,
   * li wklejanych ul/ol wchodzą MIĘDZY połówki jako rodzeństwo (powłoka wklejanej listy
   * odpada — wcięcie i numerację nadaje kontrakt gospodarza; etykiety przeliczy
   * applyListLabels po onContentChange). Wiodące/końcowe inline'y trafiają do połówek.
   */
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
        // Przejmujemy dzieci-elementy wklejanej listy (li; ewentualne inne elementy też —
        // lepsza nadmiarowość niż utrata treści). Białe znaki między li pomijamy.
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

    // Puste połówki precz; li z samym znacznikiem punktora (span.list-marker,
    // prezentacyjny) też jest puste — bez osieroconych pustych punktów wokół wklejki.
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

  /**
   * Obsługa skrótów klawiszowych
   */
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

    // Backspace at the very start of a page deletes the PREVIOUS page's manual page-break
    // (pages are separate contenteditable, so the browser cannot merge across them). We remove
    // the trailing page-break marker and repaginate — content reflows up, the hard break is gone.
    // It must NOT delete the page wrapper itself (a layout element). Any other Backspace is the
    // browser's normal char/selection delete.
    if (e.key === 'Backspace' && !e.shiftKey && !e.ctrlKey && !e.metaKey && !e.altKey
        && (this._tryDeletePageBreakBackwards() || this._tryMergeAcrossPageBackwards())) {
      e.preventDefault();
      return;
    }

    // Backspace/Delete przy znaczniku punktora (span.list-marker, contenteditable=false):
    // domyślnie Chrome kasuje SAM znacznik, a ensureBulletMarkers po chwili go odtwarza —
    // „punkt wraca", a szybkie powtórzenia sklejają punkt z poprzednim i rwą listę.
    if ((e.key === 'Backspace' || e.key === 'Delete') && !e.shiftKey && !e.ctrlKey && !e.metaKey && !e.altKey
        && !this.readOnly && this._handleListMarkerDeletion(e.key === 'Delete')) {
      e.preventDefault();
      this._flushPaginateSoon();
      return;
    }

    // Cross-page caret navigation: pages are separate contenteditable elements, so the browser
    // cannot move the caret to the next/previous page with Arrow keys. At a page boundary (last/
    // first line, collapsed caret, no modifiers) move it ourselves; otherwise let the browser
    // handle normal in-page navigation and selections (Shift/Ctrl/Alt untouched).
    if ((e.key === 'ArrowDown' || e.key === 'ArrowUp')
        && !e.shiftKey && !e.ctrlKey && !e.metaKey && !e.altKey
        && this._tryMoveCaretAcrossPages(e.key === 'ArrowDown' ? 'down' : 'up')) {
      e.preventDefault();
      return;
    }

    if (e.ctrlKey || e.metaKey) {
      // Ctrl/Cmd+Shift+V — oznacz najbliższe wklejenie jako „tylko tekst".
      // Nie wołamy preventDefault: pozwalamy przeglądarce wywołać zdarzenie `paste`,
      // które obsłuży handlePaste z uwzględnieniem tej flagi.
      if (e.shiftKey && e.key.toLowerCase() === 'v') {
        this.plainTextPasteUntil = Date.now() + 1000;
        return;
      }

      // Ctrl+Shift+8 — „Pokaż wszystko" (znaczniki formatowania), jak w Wordzie.
      // e.code zamiast e.key: z Shiftem '8' staje się '*' (układ US/PL programisty).
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
          // Emituj zdarzenie zapisu (obsługiwane przez komponent nadrzędny)
          break;
      }
    }

    // Tab: w tabeli nawigacja po komórkach jak w MS Word (13259982) — indent robił
    // „nieoczekiwany efekt wizualny" i nie ruszał kursora. Poza tabelą jak Word:
    // Tab z kolapsowaną karetką W ŚRODKU bloku wstawia znak tabulacji; wcięcie tylko
    // na początku bloku lub przy zaznaczeniu. Shift+Tab — outdent jak dotąd.
    if ((e.key === 'ArrowLeft' || e.key === 'ArrowRight') && !e.shiftKey) {
      const dir = e.key === 'ArrowRight' ? 'right' : 'left';
      setTimeout(() => this._escapeTabCarrier(dir), 0);
    }

    if (e.key === 'Tab') {
      e.preventDefault();
      if (this._handleTableTab(e.shiftKey)) return;
      if (!this.readOnly && this._handleChecklistTab(e.shiftKey)) return;
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

    // ENTER przy dolnej krawędzi strony rozciągał kartkę na czas debounce'a (~250 ms), zanim
    // repaginacja przelała treść na kolejną stronę — użytkownik widział „wydłużoną" stronę i
    // musiał ręcznie scrollować, żeby zobaczyć drugą kartkę. Po wstawieniu akapitu przez
    // przeglądarkę wymuszamy repaginację NATYCHMIAST (z pominięciem debounce'a). NIE wołamy
    // preventDefault — domyślny insertParagraph ma się wykonać; rAF czeka na mutację DOM.
    // Zwykłe pisanie nadal korzysta z debounce'a (_schedulePaginate) dla wydajności.
    if (e.key === 'Enter' && !e.ctrlKey && !e.metaKey && !e.altKey) {
      // Lista pól wyboru: pusty punkt = wyjście z listy (obsłużone w całości tutaj).
      if (!e.shiftKey && !this.readOnly && this._handleChecklistEnter()) {
        e.preventDefault();
        this._flushPaginateSoon();
        return;
      }
      // Capture the explicit inline font at the caret BEFORE the native paragraph split. The
      // new block the browser creates does not carry the inline font-family span, so typing on
      // the next line reverts to the document default (bug 13263086). Re-establish it once
      // pagination has settled (double rAF = after _flushPaginateSoon's repaginate + caret restore).
      const carry = this.readOnly ? null : this._captureInlineTextStyleAtCaret();
      this._flushPaginateSoon();
      if (carry) {
        requestAnimationFrame(() =>
          requestAnimationFrame(() => this._continueInlineStyleAfterEnter(carry))
        );
      }
    }
  }

  /**
   * Tab w tabeli = nawigacja po komórkach jak w MS Word (13259982): następna/poprzednia
   * komórka, z końca wiersza do pierwszej komórki kolejnego wiersza, a w OSTATNIEJ komórce
   * tabeli Tab DOKŁADA nowy wiersz (jak Word). Shift+Tab w pierwszej komórce nie robi nic.
   * Tabele dzielone między strony (paginacja): fragmenty łączy data-split-table-id — z końca
   * fragmentu przechodzimy do kolejnego fragmentu zamiast dokładać wiersz w środku logicznej
   * tabeli. Zwraca true, gdy Tab został skonsumowany przez tabelę (bez indent/outdent).
   */
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
      // Bug 14185486: Tab jest obsługiwany w keydown z preventDefault — nie ma zdarzenia
      // `input`, więc nic nie planowało repaginacji; nowe wiersze przy dolnej krawędzi rozciągały
      // stronę (tabela i stopka poza kartką) aż do następnej edycji. Jak ENTER: repaginacja
      // na najbliższą klatkę.
      this._flushPaginateSoon();
      return true;
    }

    if (idx > 0) { this._focusTableCell(cells[idx - 1]); return true; }
    const prevRow = table.rows[row.rowIndex - 1];
    if (prevRow?.cells.length) { this._focusTableCell(prevRow.cells[prevRow.cells.length - 1]); return true; }
    const prevFragment = this._siblingTableFragment(table, -1);
    const lastRow = prevFragment?.rows[prevFragment.rows.length - 1];
    if (lastRow?.cells.length) { this._focusTableCell(lastRow.cells[lastRow.cells.length - 1]); return true; }
    return true; // pierwsza komórka tabeli: jak Word — nic, ale bez outdentu
  }

  /**
   * True when the collapsed caret sits at the very start of its containing block
   * (P/H1-H6/LI or the contenteditable root): only zero-width/empty nodes precede it.
   * Word indents on Tab only in that position — elsewhere Tab inserts a tab character.
   */
  private _isCaretAtBlockStart(ignoreListMarker = false): boolean {
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0 || !sel.isCollapsed) return false;
    const range = sel.getRangeAt(0);
    const isMarker = (n: Node) =>
      ignoreListMarker && n instanceof Element && n.classList.contains('list-marker');
    const isZeroWidthOnly = (text: string | null) =>
      (text ?? '').replace(/[​‌‍﻿]/g, '') === '';
    const editor = this.getActiveEditor();
    const isBlock = (el: HTMLElement) =>
      /^(P|H[1-6]|LI)$/.test(el.tagName) || el === editor || el.getAttribute('contenteditable') === 'true';

    let node: Node = range.startContainer;

    // Content before the caret inside the start node itself.
    if (node.nodeType === Node.TEXT_NODE) {
      if (!isZeroWidthOnly((node.textContent ?? '').slice(0, range.startOffset))) return false;
    } else if (node.nodeType === Node.ELEMENT_NODE) {
      const el = node as HTMLElement;
      for (let i = 0; i < range.startOffset; i++) {
        const child = el.childNodes[i];
        if (isMarker(child)) continue;
        if (child.nodeName === 'BR' || child.nodeName === 'IMG' || !isZeroWidthOnly(child.textContent)) return false;
      }
      if (isBlock(el)) return true;
    }

    // Walk up to the block, checking that every left sibling on the way is empty.
    while (node.parentNode) {
      for (let sib = node.previousSibling; sib; sib = sib.previousSibling) {
        if (isMarker(sib)) continue;
        if (sib.nodeName === 'BR' || sib.nodeName === 'IMG' || !isZeroWidthOnly(sib.textContent)) return false;
      }
      const parent = node.parentNode;
      if (parent.nodeType === Node.ELEMENT_NODE && isBlock(parent as HTMLElement)) return true;
      node = parent;
    }
    return true;
  }

  /**
   * Inserts the same tab carrier the DOCX reader emits (inline-block span with a
   * literal \t and min-width:2em) at the collapsed caret. The export pipeline
   * already converts both the carrier span and the literal \t to w:tab.
   */
  private _insertTabAtCaret(): void {
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0) return;
    const range = sel.getRangeAt(0);
    const span = document.createElement('span');
    // Exact reader attribute string — stylesheet rules match [style*='min-width:2em'].
    // white-space:pre + tab-size:2em (bug 14182165): glif \t wypełnia cały nośnik, więc
    // podświetlenie zaznaczenia maluje się na nim jak w Wordzie (bez tego \t zapadał się do
    // zera i w zaznaczeniu była 2-emowa przerwa).
    span.setAttribute('style', 'display:inline-block;min-width:2em;white-space:pre;tab-size:2em');
    // Atomic like a Word tab: without this, a TRAILING tab's \t is collapsible whitespace
    // and Chrome canonicalizes the caret to BEFORE the span — typing after Tab at the end
    // of a paragraph landed before the tab ("Tab does nothing"). contenteditable=false makes
    // the position after the span real and Backspace removes the whole tab in one step.
    span.setAttribute('contenteditable', 'false');
    span.textContent = '\t';
    range.insertNode(span);

    // Caret AFTER the carrier (insertNode does not move the selection).
    const caret = document.createRange();
    caret.setStartAfter(span);
    caret.collapse(true);
    sel.removeAllRanges();
    sel.addRange(caret);
    this.savedSelection = caret.cloneRange();
    this.onContentChange();
    // Wstawienie węzła nie generuje `input` — akapit mógł się zawinąć, strona urosnąć.
    this._schedulePaginate('tab-insert');
  }

  /**
   * Bug 14178141: strzałki wchodziły DO ŚRODKA nośnika tabulatora (contenteditable=false,
   * tekst \t) — reader owija nośnik edytowalnym spanem runu i Chrome tworzy w nim pozycję
   * karetki, dla której `getClientRects()` jest puste: karetka znikała aż do wpisania znaku.
   * Po ruchu strzałką (i po kliknięciu) karetka wewnątrz nośnika jest przenoszona ZA/PRZED niego.
   */
  private _escapeTabCarrier(direction: 'left' | 'right'): void {
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0 || !sel.isCollapsed) return;
    const range = sel.getRangeAt(0);
    const node = range.startContainer;
    const isCarrier = (n: Node | null | undefined): n is HTMLElement =>
      !!n && n.nodeType === Node.ELEMENT_NODE && (n as Element).tagName === 'SPAN'
      && (n as Element).getAttribute('contenteditable') === 'false' && /^\t?$/.test(n.textContent ?? '');
    let carrier: HTMLElement | null = null;
    if (node.nodeType === Node.TEXT_NODE) {
      const c = node.parentElement?.closest('span[contenteditable="false"]') ?? null;
      if (isCarrier(c)) carrier = c;
    } else {
      // Pozycja „(span runu, offset)” tuż ZA/PRZED nośnikiem: kontener jest edytowalny, ale Chrome
      // nie liczy dla niej prostokąta (karetka niewidoczna) — to właśnie przypadek z importu.
      const el = node as Element;
      const adjacent = direction === 'right' ? el.childNodes[range.startOffset - 1] : el.childNodes[range.startOffset];
      if (isCarrier(adjacent)) carrier = adjacent;
      else if (isCarrier(el)) carrier = el as HTMLElement;
    }
    if (!carrier) return;
    const editor = this.getActiveEditor();
    if (!editor || !editor.contains(carrier)) return;
    // Span runu zawierający TYLKO nośnik — wychodzimy poza niego: pozycja między spanami sąsiednich
    // runów ma prostokąt liczony z tekstu obok.
    let edge: Node = carrier;
    while (edge.parentElement && edge.parentElement !== editor && edge.parentElement.tagName === 'SPAN'
      && edge.parentElement.childNodes.length === 1) {
      edge = edge.parentElement;
    }
    // Pozycja „(akapit, indeks)” między spanami też nie ma prostokąta — celujemy w NAJBLIŻSZY
    // węzeł tekstowy za/przed nośnikiem (w obrębie bloku), z pominięciem treści nieedytowalnej.
    const block = (carrier.closest('p,li,td,th,h1,h2,h3,h4,h5,h6,div') as HTMLElement | null) ?? editor;
    const walker = document.createTreeWalker(block, NodeFilter.SHOW_TEXT, {
      acceptNode: (n) => (n.parentElement?.closest('[contenteditable="false"]') ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT),
    });
    let target: Text | null = null;
    if (direction === 'right') {
      walker.currentNode = edge;
      const n = walker.nextNode();
      if (n && !edge.contains(n)) target = n as Text;
    } else {
      walker.currentNode = edge;
      const n = walker.previousNode();
      if (n && !edge.contains(n)) target = n as Text;
    }
    const out = document.createRange();
    if (target) {
      out.setStart(target, direction === 'right' ? 0 : target.length);
    } else if (direction === 'right') {
      out.setStartAfter(edge);
    } else {
      out.setStartBefore(edge);
    }
    out.collapse(true);
    sel.removeAllRanges();
    sel.addRange(out);
    this.savedSelection = out.cloneRange();
  }

  /** Sąsiedni fragment tej samej logicznej tabeli (podział między strony), ±1 w kolejności DOM. */
  private _siblingTableFragment(table: HTMLTableElement, direction: 1 | -1): HTMLTableElement | null {
    const id = table.getAttribute('data-split-table-id');
    if (!id) return null;
    const fragments = Array.from(document.querySelectorAll<HTMLTableElement>(
      `table[data-split-table-id="${CSS.escape(id)}"]`));
    const i = fragments.indexOf(table);
    if (i < 0) return null;
    return fragments[i + direction] ?? null;
  }

  /**
   * Word: Tab w ostatniej komórce dokłada wiersz. Nowe komórki dziedziczą atrybuty wzorca
   * (inline border/padding, klasa markera siatki, colspan, data-grid-spacer) — goły <td>
   * spadałby na domyślne CSS i wiersz różniłby się od reszty tabeli (por. createCellLike).
   */
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

  /**
   * Karetka w komórce jak w Wordzie: zawartość zaznaczona (pisanie nadpisuje), pusta komórka
   * (sam <br>) — karetka na początku. Fokusuje contenteditable celu, bo docelowa komórka może
   * leżeć na innej stronie (fragmenty tabel z paginacji mają osobne contenteditable).
   */
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

  /**
   * Reads the explicit inline font (family/size) of the run to the LEFT of the caret — the run
   * a new paragraph should continue. Returns null when there is no explicit inline font (plain
   * default text), so Enter keeps the document default in that case. The editor element itself
   * is excluded, so the document-default font is never treated as an explicit run font.
   */
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
    // If the reference node is an element, drill into its deepest trailing element — that is
    // where the inline font span usually lives (e.g. a run wrapped by the editor).
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

  /**
   * After a native Enter, re-establishes the carried inline font on the fresh line by inserting a
   * zero-width-space span (same mechanism as a collapsed font pick) and placing the caret inside
   * it. Skips when the caret already sits in a matching font span (mid-paragraph split kept it) or
   * when the block already has visible text, to avoid stacking redundant empty spans.
   */
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

  /** Nearest block-level ancestor of a node within the editor (null if none). */
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

  /**
   * Obsługa upuszczania plików
   */
  private handleDrop(e: DragEvent): void {
    e.preventDefault();

    // Przenoszenie obrazu wewnątrz dokumentu
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

    // Obsłuż obrazy z systemu
    Array.from(files).forEach(file => {
      if (file.type.startsWith('image/')) {
        this.insertImageFromFile(file);
      }
    });
  }

  /**
   * Wstawia obraz z pliku
   */
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

  /**
   * Wykonuje komendę edytora
   */
  executeCommand(command: EditorCommand, value?: string): void {
    // Przełącznik WIDOKU, nie edycja treści: bez fokusu, bez onContentChange
    // (nie brudzi dokumentu, nie tworzy wpisu undo, nie rusza autosave).
    if (command === 'toggleFormattingMarks') {
      this.toggleFormattingMarks();
      return;
    }

    const editor = this.getActiveEditor();
    if (!editor) return;

    // Upewnij się, że edytor ma focus
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
        this._hoistListsOutOfParagraphs(editor);
        break;
      case 'toggleCheckboxBullet':
        this.toggleCheckboxBullet();
        break;
      case 'checkboxList':
        this.toggleCheckboxList();
        break;
      case 'insertCheckboxControl':
        this.insertCheckboxControl();
        break;
      case 'applyCheckboxMark':
        this.applyCheckboxMark();
        break;
      case 'numberedList':
      case 'insertOrderedList':
        document.execCommand('insertOrderedList', false);
        this._hoistListsOutOfParagraphs(editor);
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

  /** Krok wcięcia akapitu jak w Wordzie: 0,5 cala = 48px przy 96 dpi. */
  private static readonly INDENT_STEP_PX = 48;

  /**
   * Wcięcie/wysunięcie akapitów zaznaczenia przez inline `margin-left` (writer mapuje je
   * na w:ind, więc zmiana round-tripuje do DOCX). NIE używamy document.execCommand('indent'):
   * Chrome opakowuje akapit w <blockquote>, który dostawał szarą ramkę i kursywę z reguły
   * `.editor-content blockquote`, a każdy kolejny Tab zagnieżdżał następny blockquote bez
   * limitu — akapit dojeżdżał do prawej krawędzi i zapadał się w pionową kolumnę znaków
   * uciekającą poza dolną krawędź strony. Listy zostają przy execCommand — tam indent/outdent
   * to zmiana poziomu zagnieżdżenia, nie margines.
   */
  private _changeParagraphIndent(direction: 1 | -1): void {
    const editor = this.getActiveEditor();
    if (!editor) return;

    // Selekcja może żyć poza edytorem (klik w przycisk toolbara) — wtedy działamy
    // na zapamiętanym zakresie, jak pozostałe komendy formatujące.
    const live = window.getSelection();
    const range: Range | null = live && live.rangeCount > 0 && this.isSelectionInEditor(live)
      ? live.getRangeAt(0)
      : this.savedSelection;
    if (!range) return;

    // Lista pól wyboru: wcięcie = poziom listy (własny model — execCommand zostawia ul>ul bez li).
    const listItems = this._listItemsInRange(editor, range);
    if (listItems.length > 0) {
      this._changeChecklistLevel(listItems, direction);
      return;
    }

    const step = WysiwygEditorComponent.INDENT_STEP_PX;
    for (const block of this._indentableBlocksInRange(editor, range)) {
      const inline = parseFloat(block.style.marginLeft);
      const current = Number.isFinite(inline)
        ? inline
        : parseFloat(getComputedStyle(block).marginLeft) || 0;
      // Nie wypychaj akapitu poza kolumnę tekstu — zostaw co najmniej jeden krok
      // szerokości na treść. Szerokość kolumny = content-box rodzica (clientWidth
      // ZAWIERA padding, a padding .editor-content to marginesy strony — liczony
      // z paddingiem wypuszczał akapit poza prawy margines). W jsdom clientWidth=0
      // → bez górnego ograniczenia.
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

  /**
   * Bloki akapitowe objęte zakresem — tylko najgłębsze trafienia (blockquote z akapitem
   * w środku wciąłby się podwójnie). Dla kursora bez zaznaczenia: blok pod kursorem.
   */
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

  /**
   * Ustawia rozmiar czcionki.
   *
   * Wywoływane z toolbara (input + Enter / blur / +/-). Musi działać poprawnie
   * w dwóch scenariuszach:
   *  1. fokus jest w edytorze (klik na +/− z `preventDefault` na mousedown) — selekcja w edytorze istnieje,
   *  2. fokus przed chwilą był na inputcie toolbara — w `window.getSelection()` jest selekcja inputa
   *     (NIE w edytorze); musimy odtworzyć selekcję z `savedSelection` ZANIM zawołamy `editor.focus()`,
   *     bo `focus()` na contenteditable po utracie kursora ustawia caret na początku — i wstawienie
   *     spana lądowało na początku dokumentu.
   */
  setFontSize(size: number): void {
    const editor = this.getActiveEditor();
    if (!editor) return;

    // Guard the public API: a non-finite or out-of-range size would emit `0pt`/`NaNpt`,
    // which renders the text invisible (the reported "tekst znika"). Word's own range is
    // 1–1638pt; we cap at a sane 400. Reject silently — the toolbar keeps its prior value.
    if (!Number.isFinite(size) || size < 1 || size > 400) return;

    this.currentFontSize = size;

    // Odtwórz selekcję ZANIM dotkniemy fokusu — jeżeli live selection jest poza edytorem
    // (np. user kliknął w input rozmiaru czcionki), a mamy zapisaną ostatnią pozycję.
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
      // No caret to anchor the choice at — nothing to apply it to.
      return;
    }

    const range = selection.getRangeAt(0);

    if (range.collapsed) {
      // Jeśli kursor siedzi wewnątrz istniejącego ZWS-spana (wstawionego przez
      // poprzedni klik +/-), aktualizuj jego font-size zamiast zagnieżdżać nowy.
      // Dzięki temu nie powstają stosy spanów z różnymi rozmiarami, które utrzymują
      // duży line-height nawet po powrocie do małego fontu.
      const containerEl = range.startContainer.nodeType === Node.TEXT_NODE
        ? range.startContainer.parentElement
        : range.startContainer as HTMLElement;
      const isInZwsSpan = containerEl instanceof HTMLSpanElement
        && containerEl.textContent === '\u200B'
        && containerEl.style.fontSize !== '';

      if (isInZwsSpan && containerEl) {
        // Zaktualizuj rozmiar istniejącego ZWS-spana.
        containerEl.style.fontSize = `${size}pt`;
        // Umieść kursor za ZWS (pozycja 1) — bez zmiany.
        const newRange = document.createRange();
        newRange.setStart(containerEl.firstChild!, Math.min(1, containerEl.firstChild!.textContent!.length));
        newRange.setEnd(containerEl.firstChild!, Math.min(1, containerEl.firstChild!.textContent!.length));
        selection.removeAllRanges();
        selection.addRange(newRange);
        this.savedSelection = newRange.cloneRange();
        // Sticky formatting: remember the pick so it survives the span being dropped
        // by a later deletion (Problem 10).
        this._setPendingInlineStyle({ fontSize: `${size}pt` }, newRange);
        this.updateFormattingState();
        return;
      }

      // Wstaw nowy ZWS-span z żądanym rozmiarem.
      const span = document.createElement('span');
      span.style.fontSize = `${size}pt`;
      span.innerHTML = '\u200B'; // Zero-width space

      range.insertNode(span);

      // Usuń stale ZWS-spany w tym samym bloku (z poprzednich kliknięć +/-).
      // Zostawiamy tylko właśnie wstawiony i ewentualnie spany z prawdziwą treścią.
      this.removeStaleZwsSpans(span);

      // Ustaw kursor wewnątrz spana
      const newRange = document.createRange();
      newRange.setStart(span.firstChild!, 1);
      newRange.setEnd(span.firstChild!, 1);
      selection.removeAllRanges();
      selection.addRange(newRange);

      // Zapisz nową pozycję karetki, żeby kolejne klik +/- znalazły żywą selekcję
      // a nie zdezaktualizowaną z poprzedniego zapisu.
      this.savedSelection = newRange.cloneRange();
      this._setPendingInlineStyle({ fontSize: `${size}pt` }, newRange);
      this.updateFormattingState();
      return;
    }

    // Jest zaznaczenie - zastosuj rozmiar do zaznaczonego tekstu
    this.applyFontSizeToSelection(size, selection, range);
    this.onContentChange();

    // Po zmianie selekcji w applyFontSizeToSelection — zaktualizuj zapisaną
    // selekcję, żeby kolejne kliknięcie +/− trafiało dokładnie na ten sam zakres.
    const after = window.getSelection();
    if (after && after.rangeCount > 0 && this.isSelectionInEditor(after)) {
      this.savedSelection = after.getRangeAt(0).cloneRange();
    }
    this.updateFormattingState();
  }

  /**
   * Aplikuje rozmiar czcionki do zaznaczenia - bez execCommand
   */
  private applyFontSizeToSelection(size: number, selection: Selection, range: Range): void {
    this._applyInlineStyleToRange(range, span => {
      span.style.fontSize = `${size}pt`;
    });
  }

  /**
   * Wraps every text node intersecting `range` in an inline <span> styled by `apply`,
   * IN PLACE — deliberately without Range.extractContents()/insertNode(). The old
   * extract/reinsert approach split the boundary blocks (leaving empty <p> shells at
   * both ends) and, for ranges spanning page boundaries (Ctrl+A → selectAllContent
   * covers the .editor-content of EVERY page), it ripped the page scaffolding apart
   * and re-inserted its clones as content — the "new phantom page appears after
   * Ctrl+A + color change" bug. Wrapping the innermost text keeps all structure
   * intact and still wins the CSS cascade over any styled ancestor span. Only
   * document text surfaces are touched; page chrome between editors and
   * non-editable islands (markers, labels) are skipped.
   */
  private _applyInlineStyleToRange(range: Range, apply: (span: HTMLElement) => void): void {
    if (range.collapsed) return;

    let startContainer: Node = range.startContainer;
    let startOffset = range.startOffset;
    let endContainer: Node = range.endContainer;
    const endOffset = range.endOffset;

    // Split the boundary text nodes (end first, so the start offset stays valid).
    if (endContainer.nodeType === Node.TEXT_NODE && endOffset < (endContainer as Text).length) {
      (endContainer as Text).splitText(endOffset);
    }
    if (startContainer.nodeType === Node.TEXT_NODE && startOffset > 0) {
      const right = (startContainer as Text).splitText(startOffset);
      if (endContainer === startContainer) endContainer = right;
      startContainer = right;
      startOffset = 0;
    }

    // Element-level range: the split halves OUTSIDE the selection touch it at a
    // boundary and must not count as intersecting.
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
      // A cross-page range (Ctrl+A → selectAllContent) walks through page chrome:
      // idle band displays and note regions between the page editors. That is not
      // document text — skip it, but keep the band/footnote EDITORS editable text.
      if (el.closest('.page-header, .page-footer, .footnotes-region, .endnotes-region')
        && !el.closest('.header-editor-content, .footer-editor-content, .footnote-item-content')) continue;
      targets.push(n as Text);
    }

    for (const n of targets) {
      const parent = n.parentElement;
      if (!parent) continue;
      if (parent.tagName === 'SPAN' && parent.childNodes.length === 1) {
        // Reuse the sole-child wrapper — repeated applications stay one layer deep
        // (each extra span layer used to contribute its own line-height).
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

  /**
   * Ustawia rodzinę czcionki
   */
  setFontFamily(fontFamily: string): void {
    const editor = this.getActiveEditor();
    if (!editor) return;

    this.currentFontFamily = fontFamily;

    // Odtwórz selekcję ZANIM dotkniemy fokusu — otwarcie natywnego <select> czcionki
    // w toolbarze zabiera fokus i czyści selekcję contenteditable. Bez tego `editor.focus()`
    // przywracał karetkę na początek dokumentu (rangeCount > 0, więc strażnik niżej nie
    // wyzwalał restore), a ZWS-span z czcionką lądował w złym miejscu — tekst wpisywany w
    // realnej pozycji karetki dalej dziedziczył domyślną czcionkę (zgłoszony bug). To samo
    // zabezpieczenie ma już `setFontSize`.
    const live = window.getSelection();
    const liveInEditor = !!live && live.rangeCount > 0 && this.isSelectionInEditor(live);
    if (!liveInEditor && this.savedSelection) {
      this.restoreSelection();
    }

    editor.focus();

    // Jeśli selekcja zaginęła (np. klik w select toolbara) – przywróć zapisaną
    let selection = window.getSelection();
    if ((!selection || selection.rangeCount === 0) && this.savedSelection) {
      this.restoreSelection();
      selection = window.getSelection();
    }

    if (!selection || selection.rangeCount === 0) {
      // No caret to anchor the choice at — nothing to apply it to.
      return;
    }

    const range = selection.getRangeAt(0);

    if (range.collapsed) {
      // Jeśli kursor już siedzi w ZWS-spanie (z poprzedniego wyboru czcionki), zaktualizuj
      // jego font-family zamiast zagnieżdżać kolejny pusty span (analogicznie do `setFontSize`).
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
        // Sticky formatting: remember the pick so it survives the span being dropped
        // by a later deletion (Problem 10).
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
      // Keep the saved caret and toolbar state in sync with the fresh span (parity with
      // setFontSize): a follow-up combobox pick restores INTO the span instead of the
      // pre-span caret, and the selector reflects the chosen font before any typing.
      this.savedSelection = newRange.cloneRange();
      this._setPendingInlineStyle({ fontFamily }, newRange);
      this.updateFormattingState();

      return;
    }

    // Użyj tej samej logiki co dla font-size
    this.applyFontFamilyToSelection(fontFamily, selection, range);
    this.onContentChange();

    // Po zmianie selekcji w applyFontFamilyToSelection — zaktualizuj zapisaną selekcję,
    // żeby kolejny wybór czcionki trafiał na ten sam, żywy zakres (jak w `setFontSize`).
    const after = window.getSelection();
    if (after && after.rangeCount > 0 && this.isSelectionInEditor(after)) {
      this.savedSelection = after.getRangeAt(0).cloneRange();
    }
    this.updateFormattingState();
  }

  /**
   * Usuwa "stale" ZWS-spany (zero-width space, font-size ustawiony, bez innej treści)
   * z tego samego bloku co `keepSpan`. Takie spany powstają przy klikaniu +/− bez
   * zaznaczenia — każde kliknięcie wstawiało nowy span, stare pozostawały w DOM
   * i utrzymywały line-height linii na poziomie największego fontu.
   */
  private removeStaleZwsSpans(keepSpan: HTMLElement): void {
    // Znajdź blok nadrzędny (p, li, div itp.)
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

  /**
   * Aplikuje rodzinę czcionki do zaznaczenia
   */
  private applyFontFamilyToSelection(fontFamily: string, selection: Selection, range: Range): void {
    this._applyInlineStyleToRange(range, span => {
      span.style.fontFamily = fontFamily;
    });
  }

  /**
   * Ustawia kolor tekstu
   */
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

  /**
   * Aplikuje kolor do zaznaczenia
   */
  private applyColorToSelection(color: string, selection: Selection, range: Range): void {
    this._applyInlineStyleToRange(range, span => {
      span.style.color = color;
    });
  }

  /**
   * Ustawia kolor tła
   */
  setBackgroundColor(color: string): void {
    const editor = this.getActiveEditor();
    if (!editor) return;

    editor.focus();
    document.execCommand('hiliteColor', false, color);
    this.onContentChange();
  }

  // Zapisana selekcja (do użycia gdy selekcja jest tracona przez kliknięcie na toolbar)
  private savedSelection: Range | null = null;

  /**
   * Ustawia fokus na edytorze (header/footer jeśli edytowany, inaczej body).
   */
  focus(): void {
    const editor = this.getActiveEditor();
    editor?.focus();
  }

  /**
   * Zapisuje aktualną selekcję - wywoływane przed focusout.
   * Akceptuje selekcje z body lub header/footer.
   */
  saveSelection(): void {
    const selection = window.getSelection();
    if (selection && selection.rangeCount > 0) {
      const range = selection.getRangeAt(0);
      // Zapisuj TYLKO gdy edytor naprawdę ma fokus. Klik w pole toolbara (np. input rozmiaru
      // czcionki) przenosi fokus i zwija selekcję contenteditable do karetki — `blur`/`selectionchange`
      // odpalają się WTEDY z karetką wciąż „w edytorze", więc bez tego strażnika nadpisywaliśmy
      // realne zaznaczenie pustą karetką → `setFontSize` nie miał czego sformatować (Doc2-FMT-004).
      if (this.isSelectionInEditor(selection) && this.editorHasFocus()) {
        this.savedSelection = range.cloneRange();
      }
    }
  }

  /** True, gdy aktywny element to jeden z edytowalnych obszarów (strona/nagłówek/stopka). */
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

  /**
   * Przywraca zapisaną selekcję
   */
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

  /**
   * Aplikuje styl dokumentu TYLKO do zaznaczonego fragmentu tekstu.
   * Jeśli nic nie jest zaznaczone, nie robi nic.
   * Działa jak formatowanie w Word - aplikuje czcionkę, rozmiar, kolor, bold, italic, underline do selekcji.
   */
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

    // Debug - sprawdź co przychodzi w stylu
    console.log('[applyDocumentStyle] Otrzymany styl:', JSON.stringify(style, null, 2));

    // Najpierw spróbuj przywrócić zapisaną selekcję (bo mogła być utracona przez kliknięcie na toolbar)
    let selection = window.getSelection();
    let range: Range | null = null;
    
    if (selection && selection.rangeCount > 0) {
      range = selection.getRangeAt(0);
      // Sprawdź czy selekcja jest w edytorze i nie jest pusta
      if (!editor.contains(range.commonAncestorContainer) || range.collapsed) {
        range = null;
      }
    }
    
    // Jeśli nie ma aktualnej selekcji, spróbuj użyć zapisanej
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

    // Sprawdź czy selekcja jest wewnątrz edytora
    if (!editor.contains(range.commonAncestorContainer)) {
      console.warn('[applyDocumentStyle] Selekcja poza edytorem');
      return;
    }

    editor.focus();

    // Pobierz zaznaczony tekst
    const selectedText = range.toString();
    if (!selectedText || selectedText.trim().length === 0) {
      console.warn('[applyDocumentStyle] Pusty zaznaczony tekst');
      return;
    }

    console.log('[applyDocumentStyle] Zaznaczony tekst:', selectedText);

    // Wyodrębnij zawartość zaznaczenia
    const fragment = range.extractContents();
    
    // Spłaszcz zagnieżdżone elementy - wyciągnij tylko tekst
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
    
    // Tworzę element SPAN z wszystkimi stylami
    const styledSpan = document.createElement('span');
    
    // Buduj style inline
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
    
    // Zastosuj style do span
    if (styles.length > 0) {
      styledSpan.setAttribute('style', styles.join('; '));
      console.log('[applyDocumentStyle] Finalne style:', styles.join('; '));
    }
    
    // Wstaw czysty tekst do span (bez zagnieżdżonych elementów)
    styledSpan.textContent = plainText;
    
    // Wstaw span w miejsce zaznaczenia
    range.insertNode(styledSpan);
    
    // Ustaw kursor na końcu wstawionego elementu
    const newRange = document.createRange();
    newRange.selectNodeContents(styledSpan);
    newRange.collapse(false);
    selection!.removeAllRanges();
    selection!.addRange(newRange);
    
    // Wyczyść zapisaną selekcję
    this.savedSelection = null;

    console.log('[applyDocumentStyle] Styl został zastosowany');
    this.onContentChange();
  }

  /**
   * Wstawia tekst
   */
  insertText(text: string): void {
    // Restore the editor selection first: when invoked from a menu ("Wklej bez formatowania"),
    // clicking the menu item moved focus off the contenteditable, so execCommand('insertText')
    // would have no caret and silently do nothing (root cause of "wklej bez formatowania nie
    // działa"). During a real paste event the selection is already live, so this is a no-op.
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

  /**
   * Snapshots the current editor selection as a bookmark for a deferred paste.
   *
   * Captured synchronously at command-invocation time (e.g. the moment a menu item
   * is clicked) so that a later async clipboard read, menu teardown or focus change
   * cannot move the paste target. Falls back to the continuously-tracked
   * `savedSelection` when the live selection has already left the editor.
   */
  captureSelectionBookmark(): Range | null {
    const sel = window.getSelection();
    if (sel && sel.rangeCount > 0 && this.isSelectionInEditor(sel)) {
      return sel.getRangeAt(0).cloneRange();
    }
    return this.savedSelection ? this.savedSelection.cloneRange() : null;
  }

  /**
   * Pastes plain text at a previously captured bookmark ("Wklej bez formatowania").
   *
   * Unlike insertText() this NEVER trusts the transient live selection: after a menu
   * click plus an async clipboard read the live selection is unreliable and used to
   * point back at the source range, which is why the pasted text both landed in the
   * wrong place AND kept the source formatting (it inherited the source run's style).
   * We insert a bare text node at the target instead, so the run inherits the target
   * context formatting and no source marks survive. Newlines become soft <br> breaks.
   * The whole insertion emits a single content change → a single undo entry, and the
   * caret is left directly after the inserted text.
   */
  pastePlainTextAt(bookmark: Range | null, rawText: string): void {
    const text = normalizeWhitespace(rawText);
    if (!text) return;

    const editor = this.getActiveEditor();
    // The bookmark must still live inside the current editor DOM. Async clipboard
    // reads, a document swap or an unmount can invalidate it — abort rather than
    // paste into the wrong place (or throw on a detached range).
    if (!editor || !bookmark || !editor.contains(bookmark.startContainer)) return;

    const range = bookmark.cloneRange();
    range.deleteContents(); // replace the target selection when it was non-empty

    // Word's "Keep Text Only" turns every newline into a PARAGRAPH boundary (¶),
    // not a soft line break — a multi-line paste must split the host paragraph.
    const lines = text.split('\n');
    let lastNode: Node | null;
    if (lines.length > 1) {
      lastNode = this._pastePlainLinesAsParagraphs(range, lines);
    } else {
      const fragment = this.buildPlainTextFragment(text);
      lastNode = fragment.lastChild;
      // Insert at BLOCK level, escaping any inline formatting wrappers (b/i/u/font/
      // span[style]) around the caret. Otherwise a bare text node inherits the run it
      // sits in — e.g. right after copying colored text, when the source selection is
      // still the paste target, "bez formatowania" would re-emit the source color/bold.
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

  /**
   * Builds a DOM fragment for a SINGLE-LINE plain-text paste (multi-line pastes go
   * through _pastePlainLinesAsParagraphs — Word models newlines as paragraph marks).
   * Text nodes (not innerHTML) guarantee that `<`, `>`, `&` stay literal — the
   * pasted string is never parsed as HTML.
   */
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

  /**
   * Inserts `fragment` at the caret but lifted OUT of any inline character-formatting
   * wrappers (b/strong, i/em, u, s, sub, sup, font, span[style], mark, a, …) so the
   * pasted plain text inherits ONLY the destination paragraph's formatting — never the
   * bold/colored run it happened to land in. This is what makes "Wklej bez formatowania"
   * actually drop character formatting even when the caret sits inside (or replaced) a
   * formatted run, which is the common case right after copying colored text.
   *
   * Each inline ancestor between the caret and its block container is split at the caret
   * (left part stays, right part moves into a clone after it) and empty halves are pruned,
   * leaving the caret directly inside the block. If we cannot resolve a block container we
   * fall back to a plain insert at the caret.
   */
  private insertFragmentOutsideInlineFormatting(range: Range, fragment: DocumentFragment): void {
    const { container, offset } = this._liftRangeToBlockLevel(range);
    const insertAt = document.createRange();
    insertAt.setStart(container, Math.max(0, offset));
    insertAt.collapse(true);
    insertAt.insertNode(fragment);
  }

  /**
   * Resolves the caret to a (block container, child index) position, splitting every
   * inline formatting wrapper (b/i/u/font/span[style], …) between the caret and its
   * block on the way up. Left halves stay, right halves move into clones after them,
   * empty halves are pruned — the returned position sits directly inside the block.
   */
  private _liftRangeToBlockLevel(range: Range): { container: Node; offset: number } {
    const editor = this.getActiveEditor();
    const BLOCK_TAGS = ['P', 'DIV', 'LI', 'TD', 'TH', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'BLOCKQUOTE', 'PRE'];
    const isBlock = (node: Node | null): boolean =>
      !!node && (node === editor ||
        (node.nodeType === Node.ELEMENT_NODE && BLOCK_TAGS.includes((node as Element).tagName)));

    let container: Node = range.startContainer;
    let offset = range.startOffset;

    // Work in terms of a child index so splitting elements is a simple node move: if the
    // caret is inside a text node, split it so its right half becomes a sibling boundary.
    if (container.nodeType === Node.TEXT_NODE) {
      const text = container as Text;
      const right = text.splitText(offset);
      container = text.parentNode!;
      offset = Array.prototype.indexOf.call(container.childNodes, right);
    }

    // Climb out of every inline wrapper up to the block container (guarded against loops).
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

  /**
   * Multi-line "Wklej bez formatowania": every newline is a PARAGRAPH boundary, like
   * Word's Keep Text Only. The host paragraph is split at the caret — line 1 joins the
   * head, the last line joins the tail (keeping the text after the caret), middle lines
   * become sibling blocks cloned from the host shell (destination paragraph formatting,
   * matching Word). Returns the node the caret should land after (end of pasted text).
   */
  private _pastePlainLinesAsParagraphs(range: Range, lines: string[]): Node | null {
    const editor = this.getActiveEditor();
    if (!editor) return null;
    const { container, offset } = this._liftRangeToBlockLevel(range);

    const PARA_TAGS = ['P', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'LI', 'BLOCKQUOTE', 'PRE'];
    const host = container !== editor && container.nodeType === Node.ELEMENT_NODE
      && PARA_TAGS.includes((container as Element).tagName) ? container as Element : null;

    // New blocks inherit the destination paragraph's shell, but never its identity or
    // pagination/presentation markers (a cloned data-split-* id would make the merge
    // pass weld unrelated paragraphs together).
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
      // Caret sits directly in the editor / a cell / a wrapper div — emit one block per line.
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

    // Empty text node keeps a caret anchor even for an empty last line; contenteditable
    // normalization may drop it later, which is fine once the caret has been placed.
    const caretNode = document.createTextNode(lines[lines.length - 1]);
    tail.insertBefore(caretNode, tail.firstChild);
    if (!tail.textContent && !tail.querySelector('*')) tail.appendChild(document.createElement('br'));
    return caretNode;
  }

  /**
   * Wstawia HTML
   */
  insertHtml(html: string): void {
    document.execCommand('insertHTML', false, html);
    this.onContentChange();
  }

  /**
   * Wstawia pole daty AUTO-aktualizowane (ADR-0084) w miejscu kursora — jak wordowe
   * „Wstaw datę i godzinę" z zaznaczonym „Aktualizuj automatycznie" (pole TIME).
   * Span jest atomowy (contenteditable=false — wartości pola nie edytuje się inline);
   * writer odtwarza z niego żywe pole DOCX (w:fldSimple `TIME \@ "dd-MM-yyyy"`),
   * a reader przy każdym kolejnym otwarciu odświeża datę wg tego obrazu.
   */
  insertDateField(): void {
    const editor = this.getActiveEditor();
    if (!editor) return;

    // Selekcja: żywa w edytorze, inaczej zapisana (klik w toolbar zwija selekcję).
    // Wstawka przez Range, NIE execCommand('insertHTML'): Chrome przy karetce na KOŃCU
    // bloku wyrzuca nieedytowalny element ZA akapit (span lądował jako brat <p>,
    // a writer gubiłby go poza akapitem). Range.insertNode wstawia zawsze w miejscu kursora.
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

    // Karetka ZA polem (insertNode nie przesuwa selekcji).
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

  /**
   * Wstawia podział kolumny (div.docx-column-break) w pozycji kursora — dalsza treść przechodzi
   * do następnej kolumny (writer odtwarza w:br type=column). Sensowne tylko w sekcji wielokolumnowej,
   * ale wstawienie w jednokolumnowej jest nieszkodliwe (ADR-0039).
   */
  insertColumnBreak(): void {
    this.insertHtml('<div class="docx-column-break"></div>');
    this._schedulePaginate('columns-change');
  }

  /**
   * Ustawia układ kolumn sekcji bazowej (całego dokumentu) — count=1 znosi kolumny.
   * Kolumny sekcji bazowej żyją na kontenerze .document-content (data-col-*), więc modyfikujemy
   * przechwycone atrybuty kontenera (round-trip do writera) oraz sygnał renderu (ADR-0039).
   * Edycja per-sekcja (marker docx-section-break) — kolejny etap.
   */
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

  /** Aktualny układ kolumn sekcji bazowej (do stanu UI panelu). */
  getBaseColumnCount(): number {
    return this._baseColumns()?.count ?? 1;
  }

  /**
   * Zapisuje data-col-* na przechwyconych atrybutach kontenera .document-content, tworząc wpis
   * kontenera, gdy dokument nie miał wrappera (dokument jednokolumnowy z importu). Bez tego zmiana
   * kolumn nie trafiłaby do zapisu (writer czyta kolumny z kontenera).
   */
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

  /**
   * Wstawia obraz
   */
  insertImage(src: string, alt: string = ''): void {
    const editor = this.getActiveEditor();
    if (!editor) return;

    const imageId = `img-${Date.now()}-${Math.floor(Math.random() * 10000)}`;

    // Buduj DOM bezpośrednio zamiast insertHTML (które nie działa po utracie focusu)
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

    // Uchwyty resize: prawy (szerokość), dolny (wysokość), narożnik (proporcjonalnie)
    ['right', 'bottom', 'corner'].forEach(type => {
      const h = document.createElement('span');
      h.className = `image-resize-handle resize-handle-${type}`;
      h.title = type === 'right' ? 'Zmień szerokość' : type === 'bottom' ? 'Zmień wysokość' : 'Zmień rozmiar';
      wrapper.appendChild(h);
    });

    // Spróbuj wstawić w miejsce kursora / zapisanej selekcji
    let inserted = false;

    // Najpierw próbuj przywrócić selekcję
    const selection = window.getSelection();
    if (selection && selection.rangeCount > 0) {
      const range = selection.getRangeAt(0);
      if (editor.contains(range.commonAncestorContainer)) {
        range.deleteContents();
        range.insertNode(wrapper);
        // Ustaw kursor za wstawionym obrazem
        range.setStartAfter(wrapper);
        range.collapse(true);
        selection.removeAllRanges();
        selection.addRange(range);
        inserted = true;
      }
    }

    // Jeśli nie udało się wstawić w selekcji, próbuj savedSelection
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

    // Fallback: dołącz na koniec edytora
    if (!inserted) {
      editor.focus();
      const p = document.createElement('p');
      p.appendChild(wrapper);
      editor.appendChild(p);
    }

    this.selectImageWrapper(wrapper);
    this.onContentChange();
  }

  /**
   * Wstawia kod kreskowy z wartością tekstową pod spodem
   */
  insertBarcodeWithValue(src: string, valueText: string): void {
    const editor = this.editorContent?.nativeElement;
    if (!editor) return;

    const imageId = `img-${Date.now()}-${Math.floor(Math.random() * 10000)}`;

    // Kontener na kod kreskowy z wartością
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

    // Uchwyty resize
    ['right', 'bottom', 'corner'].forEach(type => {
      const h = document.createElement('span');
      h.className = `image-resize-handle resize-handle-${type}`;
      h.title = type === 'right' ? 'Zmień szerokość' : type === 'bottom' ? 'Zmień wysokość' : 'Zmień rozmiar';
      wrapper.appendChild(h);
    });

    container.appendChild(wrapper);

    // Tekst wartości pod kodem
    const valueDiv = document.createElement('div');
    valueDiv.className = 'barcode-value-text';
    valueDiv.style.cssText = 'font-size: 12px; font-family: monospace; color: #333; margin-top: 4px; text-align: center; word-break: break-all;';
    valueDiv.textContent = valueText;
    container.appendChild(valueDiv);

    // Wstaw w miejsce kursora
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

  /**
   * Wstawia tabelę
   */
  insertTable(config: string): void {
    const [rows, cols] = config.split('x').map(Number);
    const editor = this.getActiveEditor();
    if (!editor || rows <= 0 || cols <= 0) return;

    const colWidth = Math.floor(100 / cols);

    // Buduj tabelę jako element DOM (zamiast execCommand insertHTML, który nie działa bez focusu)
    const table = document.createElement('table');
    table.style.cssText = 'border-collapse:collapse;width:100%;margin:10px 0;table-layout:fixed;position:relative;';

    for (let i = 0; i < rows; i++) {
      const tr = document.createElement('tr');
      for (let j = 0; j < cols; j++) {
        const td = document.createElement('td');
        td.style.cssText = `border:1px solid #ccc;padding:8px;min-width:30px;width:${colWidth}%;`;
        // <br> a nie &nbsp; — twarda spacja zostawała przed wpisanym tekstem i szła do DOCX
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

    // Próbuj wstawić w miejsce kursora / selekcji
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

    // Fallback: użyj zapisanej selekcji (utraconej przez kliknięcie dialogu)
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

    // Fallback: dołącz na koniec edytora
    if (!inserted) {
      editor.focus();
      editor.appendChild(table);
      editor.appendChild(afterParagraph);
    }

    this.savedSelection = null;
    // Word: po wstawieniu tabeli kursor ląduje od razu w PIERWSZEJ komórce (13568651) —
    // użytkownik może natychmiast pisać, bez klikania w tabelę.
    const firstCell = table.rows[0]?.cells[0];
    if (firstCell) this._focusTableCell(firstCell);
    this.onContentChange();
  }

  /**
   * Wstawia link
   */
  insertLink(url: string, text?: string): void {
    const editor = this.getActiveEditor();
    if (!editor) return;

    const normalized = this.normalizeLinkUrl(url);
    if (!normalized) return; // pusty/niepoprawny URL — nie rób nic

    // The link dialog's URL input stole focus and collapsed the editor selection. Restore the
    // selection saved on the last editor mouseup/keyup so createLink lands on the user's text
    // instead of nothing (root cause of "wstawianie linku nie działa").
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
      // Apply to the selected text — keeps its existing run formatting.
      document.execCommand('createLink', false, normalized);
    } else {
      // Collapsed caret: insert a new anchor using the provided label, or the URL itself.
      const label = (text && text.trim().length > 0) ? text : normalized;
      const safeUrl = normalized.replace(/"/g, '&quot;');
      this.insertHtml(`<a href="${safeUrl}" target="_blank" rel="noopener">${this.escapeHtml(label)}</a>`);
    }

    this.onContentChange();
  }

  /**
   * Normalizuje URL linku. Zwraca null dla pustej wartości. Jawne schematy (http/https/mailto/
   * tel), kotwice (#) i ścieżki (/) zostają; „goła" domena (np. www.x.pl) dostaje https://.
   */
  private normalizeLinkUrl(url: string): string | null {
    const trimmed = (url ?? '').trim();
    if (!trimmed) return null;
    if (/^(https?:|mailto:|tel:|#|\/)/i.test(trimmed)) return trimmed;
    if (/^[\w.-]+\.[a-z]{2,}([\/?#]|$)/i.test(trimmed)) return `https://${trimmed}`;
    return trimmed;
  }

  /** Escapuje tekst etykiety linku, by user-input nie wstrzyknął HTML. */
  private escapeHtml(value: string): string {
    return value
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  /**
   * Wstawia poziomą linię
   */
  insertHorizontalRule(): void {
    document.execCommand('insertHorizontalRule', false);
    this.onContentChange();
  }

  /**
   * Inserts a manual page break as a SEMANTIC, non-printing marker.
   *
   * The marker carries no inline graphic styling on purpose: the writer maps
   * `div.page-break` to a real OOXML page break (`w:br w:type="page"`), never a
   * drawing/picture/shape, and the paginator splits pages on this marker. Any
   * visible hint is opt-in via the "formatting marks" mode (see SCSS), so the
   * break is never presented as an image the user could focus or edit.
   */
  insertPageBreak(): void {
    this.insertHtml('<div class="page-break" contenteditable="false"></div>');
  }

  /**
   * When on, page-break markers show a subtle non-printing hint (Word-like
   * "formatting marks"). Off by default so breaks render as a real page
   * boundary rather than a graphic in the content flow.
   *
   * Word's "Pokaż wszystko" (Ctrl+Shift+8). All marks are CSS pseudo-elements
   * keyed off the `show-formatting-marks` class (see SCSS) — nothing is added
   * to the DOM, so saved content and pagination measurements stay identical.
   */
  readonly showFormattingMarks = signal(false);

  toggleFormattingMarks(force?: boolean): void {
    this.showFormattingMarks.update(v => (force === undefined ? !v : force));
    // Odbij stan w EditorState — toolbar podświetla ¶ na tej podstawie.
    this.editorState.update(s => ({ ...s, formattingMarks: this.showFormattingMarks() }));
    this.stateChange.emit(this.editorState());
    // Overlay znaków (spacje/taby/br) rysuje się na żądanie i znika przy wyłączeniu.
    this._scheduleFormattingMarksRender();
  }

  /**
   * Undo
   */
  undo(): void {
    // Sticky formatting (Problem 10): the restored DOM invalidates the pending-style anchor.
    this._clearPendingInlineStyle();
    // Bug 13184834: bez flusha klik w oknie debounce'a (500 ms od ostatniej edycji) nie miał
    // czego cofnąć — ostatnia porcja pisania nie była jeszcze na stosie.
    this._flushPendingPersist();
    if (this.undoStack.length > 1) {
      // Kotwica kursora PRZED podmianą treści: rebind [innerHTML] przebudowuje DOM stron
      // i kasuje selekcję, więc bez przywrócenia karetka lądowała na początku dokumentu.
      const caret = this._captureCaretForHistory();
      const current = this.undoStack.pop()!;
      // Remember the live (pre-undo) caret with the redo entry: it marks the spot right after
      // this state's edit, which is where redo must place the caret.
      this.redoStack.push({ html: current, caret });

      const previous = this.undoStack[this.undoStack.length - 1];
      const pages = this._splitHtmlIntoPages(previous);
      this.pageContents.set(pages);
      this._content.set(previous);
      this.contentChange.emit(previous);
      this._schedulePaginate('undo');

      this.updateState();
      // Snapshot undo jest bez atrybutów etykiet (strip przy serializacji) — przelicz po renderze.
      setTimeout(() => {
        this.refreshListLabels();
        // Brak kotwicy (klik toolbara zabrał selekcję, savedSelection pusty) → domknij do
        // KOŃCA dokumentu; null gubił selekcję całkiem i kursor lądował na początku.
        this._restoreGlobalCaret(
          caret ?? { block: Number.MAX_SAFE_INTEGER, offset: Number.MAX_SAFE_INTEGER });
        this.updateFormattingState();
      }, 0);
    }
  }

  /**
   * Redo
   */
  redo(): void {
    // Sticky formatting (Problem 10): the restored DOM invalidates the pending-style anchor.
    this._clearPendingInlineStyle();
    // Flush jak w undo(): jeżeli po cofnięciu użytkownik COŚ dopisał, wiszący snapshot
    // unieważnia redo (nowa edycja czyści redoStack) — dokładnie jak w Wordzie.
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
        // Caret goes AFTER the re-inserted text (the position captured when this state was
        // left via undo), not the pre-redo caret which would land before it.
        this._restoreGlobalCaret(
          entry.caret ?? { block: Number.MAX_SAFE_INTEGER, offset: Number.MAX_SAFE_INTEGER });
        this.updateFormattingState();
      }, 0);
    }
  }

  /**
   * Kotwica karetki dla undo/redo. Preferuje żywą selekcję; gdy klik w przycisk toolbara
   * zdążył ją zabrać z edytora, mapuje ostatnią realną pozycję z `savedSelection`.
   * Kotwica { blok, offset } przeżywa przebudowę DOM stron (patrz `_saveGlobalCaret`),
   * a offset spoza przywróconej treści jest domykany do końca bloku/dokumentu.
   */
  private _captureCaretForHistory(): { block: number; offset: number } | null {
    const refs = this.pageEditorRefs?.toArray() ?? [];
    const sel = window.getSelection();
    if (sel && sel.rangeCount > 0) {
      const fromLive = this._globalCaretFromRange(sel.getRangeAt(0), refs);
      if (fromLive) return fromLive;
    }
    return this.savedSelection ? this._globalCaretFromRange(this.savedSelection, refs) : null;
  }

  /**
   * Zapisuje do stosu undo
   */
  private saveToUndoStack(): void {
    const html = this.getContent();
    if (!html) return;
    
    // Nie zapisuj jeśli to samo co ostatni wpis
    if (this.undoStack.length > 0 && this.undoStack[this.undoStack.length - 1] === html) {
      return;
    }

    this.undoStack.push(html);
    
    // Ogranicz rozmiar stosu
    if (this.undoStack.length > 100) {
      this.undoStack.shift();
    }

    // Wyczyść redo po nowej akcji
    this.redoStack = [];
  }

  /**
   * Aktualizuje stan formatowania
   */
  private updateFormattingState(): void {
    // Pobierz rzeczywisty rozmiar i czcionkę z computed styles
    const selection = window.getSelection();
    let fontSize = 11;
    let fontFamily = 'Calibri';
    let textColor = '#000000';
    let currentBlockFormat = 'p';
    // Wyrównanie/listy liczone z DOM (nie queryCommandState) — komendy justify* zapisują
    // inline text-align na bloku, a import DOCX niesie je i inline, i przez style;
    // computed style pokrywa oba źródła. Domyślne 'start' = lewa (jak w Wordzie).
    let alignment: 'left' | 'center' | 'right' | 'justify' = 'left';
    let bulletList = false;
    let numberedList = false;
    let checkboxList = false;
    let checkboxState: 'checked' | 'unchecked' | null = null;

    // Read from the live selection when it exists and is either inside the editor OR there
    // is no mounted editor to fall back to (the latter keeps direct-call unit tests working).
    // Otherwise — e.g. right after a document loads, before the user clicks — resolve the
    // formatting context from the first editable run so the toolbar reflects the real
    // document (9pt) instead of the hard-coded default (11pt). The fallback never touches
    // focus, selection, dirty state or the undo stack; it only reads computed styles.
    const refs = this.pageEditorRefs?.toArray() ?? [];
    const hasEditors = refs.length > 0;
    const selInEditor =
      !!selection && selection.rangeCount > 0 && this.isSelectionInEditor(selection);
    const fromSelection =
      !!selection && selection.rangeCount > 0 && (selInEditor || !hasEditors);

    let element: HTMLElement | null = null;
    if (fromSelection) {
      // Wyznacz „element pod karetką" tak, żeby na granicach spanów
      // (np. selection.anchorNode wskazuje na sam <h1> z offsetem dziecka)
      // wejść w głąb do faktycznego text-node/span — inaczej odczyt computed
      // font-family/size wraca z <h1>/<p> zamiast z konkretnego runa.
      const range = selection!.getRangeAt(0);
      let node: Node | null = range.startContainer;
      if (node && node.nodeType === Node.ELEMENT_NODE) {
        const el = node as HTMLElement;
        // Jeżeli zaznaczenie jest niezwinięte, weź dziecko z prawej strony granicy
        // (start zaznaczenia), żeby trafić w pierwszy zaznaczony span.
        // Dla zwiniętej karetki też wolimy „następne" dziecko — odpowiada wpisywaniu.
        const idx = Math.min(range.startOffset, el.childNodes.length - 1);
        node = el.childNodes[Math.max(idx, 0)] ?? el.lastChild ?? el;
        // Zejdź do pierwszego liścia (tekst lub element bez dzieci)
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

      // Rozmiar czcionki - konwersja px na pt
      const fontSizePx = parseFloat(computedStyle.fontSize);
      fontSize = Math.round(fontSizePx * 0.75); // px to pt (96dpi / 72pt)

      // Czcionka - usuń cudzysłowy i weź pierwszą
      fontFamily = computedStyle.fontFamily.replace(/['"]/g, '').split(',')[0].trim();

      // Kolor tekstu
      textColor = this.rgbToHex(computedStyle.color);

      // Znajdź blok nadrzędny (p, h1, h2, etc.)
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
      // Lista pól wyboru podświetla WŁASNY przycisk (nie „Lista punktowana") — jeden aktywny.
      const listEl = li?.parentElement;
      const checkboxBullet = listEl instanceof HTMLElement && listEl.hasAttribute('data-num-id')
        ? this._checkboxBulletState(listEl)
        : null;
      checkboxList = checkboxBullet !== null;
      bulletList = listTag === 'UL' && !checkboxList;
      numberedList = listTag === 'OL' && !checkboxList;

      // Stan pola, na którym zadziała „Zaznacz / Odznacz pole” — te same reguły co toggleCheckboxBullet:
      // punkt listy kontrolnej pod karetką, a poza listą pierwszy formant pola wyboru w bieżącym bloku.
      if (checkboxBullet) {
        checkboxState = checkboxBullet.checked ? 'checked' : 'unchecked';
      } else {
        const block = element.closest('p, li, td, th, h1, h2, h3, h4, h5, h6, div');
        const box = block?.querySelector<HTMLElement>('.sdt-checkbox') ?? null;
        if (box) checkboxState = box.getAttribute('data-checked') === '1' ? 'checked' : 'unchecked';
      }
    }

    // Sticky formatting (Problem 10): after deleting the last character of a styled span the
    // browser drops the span, so the DOM no longer carries the picked font. While the caret
    // still sits at the remembered anchor, report the pending style instead of the value
    // re-derived from the paragraph's computed style (the document default).
    if (this._pendingInlineStyle && selection && this._caretMatchesPendingAnchor(selection)) {
      if (this._pendingInlineStyle.fontFamily) {
        fontFamily = this._pendingInlineStyle.fontFamily;
      }
      const pendingPt = parseFloat(this._pendingInlineStyle.fontSize ?? '');
      if (Number.isFinite(pendingPt)) {
        fontSize = Math.round(pendingPt);
      }
    }

    // Bold/italic/underline/… come from queryCommandState when a real selection drives the
    // read; on the load-time fallback there is no selection, so derive them from the first
    // run's computed style to keep the whole toolbar consistent with the shown font size.
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
      numberedList,
      checkboxList,
      checkboxState
    };

    // Detect a selection that spans more than one font family so the toolbar can
    // show a mixed (blank) state instead of an arbitrary single font (item 6).
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

  /**
   * Resolves the element whose formatting the toolbar should reflect when there is no
   * selection inside the editor (initial load / document switch). Anchors to the FIRST
   * block like Word's caret: prefers the first real text run in that block (its inline
   * run carries the size), falling back to the block itself for an empty first paragraph
   * so the toolbar shows the inherited style/docDefault size rather than the editor default.
   */
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
    // Prefer the first real text run; else the first block (empty paragraph \u2192 inherited size).
    // A structureless, empty editor yields null so the caller keeps the neutral default.
    return (textNode?.parentElement as HTMLElement | null) ?? firstBlock;
  }

  /**
   * Derives bold/italic/underline/strike/sub/sup from an element's computed style. Used on
   * the load-time fallback where `document.queryCommandState` has no selection to report on.
   */
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

  /**
   * Populates the toolbar from the freshly loaded document, without focus, selection, dirty
   * flag, undo entry or scrolling. Must be called AFTER the content has rendered (the callers
   * invoke it from the same post-render `setTimeout` that already reads the live DOM). The
   * `updateFormattingState` fallback is a no-op once the user has placed the caret, so this is
   * safe to run on every external content load (including document switches).
   */
  private _syncInitialFormatting(): void {
    if (this._isDestroyed) return;
    this.updateFormattingState();
  }

  /**
   * True when a non-collapsed selection covers text runs with more than one
   * distinct font family. Bounded scan; any failure degrades to "not mixed".
   */
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

  /**
   * Aktualizuje ogólny stan edytora (LEKKI — bez getContent()).
   * isModified ustalamy na podstawie flagi `_isDirty`, którą czyścimy przy save/setContent.
   */
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
    // Word liczy te\u017c zawarto\u015b\u0107 nag\u0142\u00f3wka i stopki (raz, nie razy liczba stron),
    // dlatego do\u0142\u0105czamy je tu jednorazowo.
    const headerText = this.headerContentEl?.nativeElement?.innerText || '';
    const footerText = this.footerContentEl?.nativeElement?.innerText || '';
    if (headerText) text += '\n' + headerText;
    if (footerText) text += '\n' + footerText;

    // Algorytm zliczania s\u0142\u00f3w zbli\u017cony do MS Word:
    // - traktuje l\u0105czniki (-), apostrofy (', \u2019) i podkre\u015blniki wewn\u0105trz wyrazu jako spoiwo
    //   (np. "e-mail", "don\u2019t" \u2192 1 s\u0142owo)
    // - kropki i przecinki mi\u0119dzy cyframi traktuje jako cz\u0119\u015b\u0107 liczby ("3.14", "1,000" \u2192 1)
    // - separatorami s\u0105 m.in. spacje, tabulatory, my\u015blniki en/em (\u2013, \u2014), uko\u015bniki, znaki interpunkcyjne
    // - usuwa znaki o zerowej szeroko\u015bci oraz nasze sztuczne placeholdery p\u00f3l ({page}, {pages})
    const normalized = text
      .replace(/[\u200B-\u200D\uFEFF]/g, '')
      .replace(/\{page\}|\{pages\}/g, ' ');
    const matches = normalized.match(/[\p{L}\p{N}]+(?:[\-_'\u2019][\p{L}\p{N}]+|[.,]\p{N}+)*/gu);
    const wordCount = matches ? matches.length : 0;

    // Diagnostyka dostępna na żądanie z konsoli: window.__wcDebug() / window.__wcCopy().
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
    } catch { /* ignore */ }

    this.editorState.update(state => ({
      ...state,
      isModified: this._isDirty,
      // Wiszący snapshot (debounce 500 ms po edycji) liczy się jako stan do cofnięcia —
      // bez tego przycisk „Cofnij" wyglądał na martwy tuż po pisaniu (bug 13184834).
      canUndo: this.undoStack.length > 1 || (this._persistTimer !== null && this.undoStack.length >= 1),
      canRedo: this.redoStack.length > 0,
      wordCount
    }));

    this.stateChange.emit(this.editorState());
  }

  // ===== MULTI-PAGE PAGINATION (MVP - Wariant A) =====

  /** Aktywacja strony przy focusin — przełącza editorContent ref i indeks aktywnej strony. */
  setActivePage(index: number, _ev: Event): void {
    const refs = this.pageEditorRefs?.toArray() ?? [];
    if (refs[index]) {
      this.editorContent = refs[index];
      this.activePageIndex.set(index);
    }
  }

  /** Input na konkretnej stronie — NIE ustawia pageContents (bo to rebinduje innerHTML wszystkich stron
   *  i kasuje kursor). Zmienione DOM żyje samo do czasu repaginacji. */
  onPageInput(index: number, _ev: Event): void {
    if (this._isRepaginating) return;
    // Sticky formatting (Problem 10): the deletion has mutated the DOM by now — re-anchor
    // the style captured in beforeinput at the post-delete caret.
    this._consumePendingDeleteCapture();
    if ((_ev as InputEvent).inputType === 'insertParagraph') this._uncheckNewChecklistItemAfterEnter();
    else this._checklistEnterOrigin = null;
    this._isDirty = true;
    this._schedulePaginate('input');
    this._schedulePersist();
    // lekki update stanu (bez getContent)
    this.updateState();
    this.updateFormattingState();
  }

  /** Placeholder &nbsp; pustych bloków (komórki tabel, akapity z importu DOCX) musi
   *  zniknąć w chwili rozpoczęcia pisania — inaczej tekst zaczyna się od twardej
   *  spacji, której nie ma w Wordzie. Zamiast usuwać węzeł (kursor straciłby kotwicę),
   *  zaznaczamy nbsp, więc domyślne wstawienie tekstu go zastępuje. */
  onEditorBeforeInput(event: InputEvent): void {
    if (event.inputType === 'deleteContentBackward' || event.inputType === 'deleteContentForward') {
      // Sticky formatting (Problem 10): snapshot the style BEFORE the browser deletes —
      // once the deletion empties a styled span the browser drops it and the style is gone.
      this._captureStyleBeforeDelete();
      return;
    }
    if (event.inputType !== 'insertText' && event.inputType !== 'insertFromPaste') return;
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0 || !sel.isCollapsed) return;
    // Sticky formatting write path (Problem 10): re-create the styled span BEFORE the browser
    // inserts the character, so the character lands inside it and inherits the pending style.
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

  // ═════════════ Sticky formatting (Problem 10) ═════════════

  /**
   * Records the style picked at a collapsed caret. The ZWS span is the DOM carrier, but the
   * browser removes it once a deletion empties it — this record lets the toolbar read-back
   * and the next insertText survive that removal. A pick at the same anchor merges with the
   * previous one (family + size can coexist); a new anchor starts fresh.
   */
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

  /** True when the collapsed caret sits exactly at the remembered anchor (same live node). */
  private _caretMatchesPendingAnchor(selection: Selection): boolean {
    const anchor = this._pendingStyleAnchor;
    if (!anchor || !anchor.node.isConnected) return false;
    if (selection.rangeCount === 0 || !selection.isCollapsed) return false;
    const range = selection.getRangeAt(0);
    return range.startContainer === anchor.node && range.startOffset === anchor.offset;
  }

  /**
   * Deletion capture: when Backspace/Delete is about to remove the LAST visible character of
   * an inline-styled span, the browser will drop the span and the caret will fall back into
   * the paragraph — so the effective font would silently reset to the document default.
   * Snapshot the effective style now; `_consumePendingDeleteCapture` re-anchors it at the
   * post-delete caret. The last-character check keeps ordinary backspaces free of this cost.
   */
  private _captureStyleBeforeDelete(): void {
    this._pendingDeleteCapture = null;
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0 || !sel.isCollapsed) return;
    if (!this.isSelectionInEditor(sel)) return;
    const node = sel.anchorNode;
    const el = node instanceof Element ? node : node?.parentElement;
    const span = (el?.closest?.('span') as HTMLElement | null) ?? null;
    if (!span || (span.style.fontFamily === '' && span.style.fontSize === '')) return;
    // Only when the deletion is about to empty the span (ZWS carriers excluded from length).
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
      // Degrade silently: no capture, the delete behaves as before.
    }
  }

  /** After the deletion has mutated the DOM (input event), re-anchor the captured style. */
  private _consumePendingDeleteCapture(): void {
    const capture = this._pendingDeleteCapture;
    if (!capture) return;
    this._pendingDeleteCapture = null;
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0 || !sel.isCollapsed || !sel.anchorNode) return;
    if (!this.isSelectionInEditor(sel)) return;
    this._pendingInlineStyle = capture;
    this._pendingStyleAnchor = { node: sel.anchorNode, offset: sel.anchorOffset };
    // The browser fires selectionchange asynchronously after this input event; hold the
    // pending style through that programmatic churn so it is not mistaken for the user
    // clicking away.
    this._pendingStyleHoldUntil = Date.now() + 150;
  }

  /**
   * Write path: the user types while a pending style is armed at the caret — put the styled
   * span back (same pattern as the pickers' collapsed branch) so the character being
   * inserted lands inside it. Runs in beforeinput, i.e. before the browser mutation.
   */
  private _materializePendingStyleSpan(sel: Selection): void {
    const pending = this._pendingInlineStyle;
    if (!pending) return;
    const range = sel.getRangeAt(0);
    const zws = '\u200B';
    const containerEl = range.startContainer.nodeType === Node.TEXT_NODE
      ? range.startContainer.parentElement
      : range.startContainer as HTMLElement;
    // Caret still inside a live ZWS carrier span — restyle it instead of nesting another.
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
    // The span now carries the style; the pending record has served its purpose.
    this._clearPendingInlineStyle();
  }

  // ═════════════ Punktory-checkboxy: ☐/❑ ↔ ☑ (zgłoszenie „nie da się odhaczyć") ═════════════

  /** Młodsze bajty Wingdings (PUA) punktorów „pustych" i „odhaczonych". */
  private static readonly UNCHECKED_BULLET_CODES = new Set([0x71, 0xa8, 0x6f, 0x72]);
  private static readonly CHECKED_BULLET_CODES = new Set([0xfe, 0xfd, 0xfc]);
  private static readonly UNCHECKED_BULLET_GLYPHS = new Set(['☐', '❑', '□', '❒', '◻']);
  private static readonly CHECKED_BULLET_GLYPHS = new Set(['☑', '☒', '✔', '✓']);

  private _checkboxListSeq = 0;

  /**
   * Przełącza punktor-checkbox bieżącego elementu listy: pusty (☐/❑) ↔ odhaczony (☑).
   * Zmiana dotyczy TYLKO tego punktu: li jest wydzielany do własnego fragmentu listy
   * (nowy data-num-id + data-lvl-override="1"), więc writer emituje dla niego osobną
   * instancję numeracji z pełnym w:lvlOverride — reszta listy zostaje przy oryginalnym
   * punktorze, a definicja wraca do DOCX w oryginalnym kodowaniu (PUA + font Wingdings).
   */
  toggleCheckboxBullet(): void {
    if (this.readOnly) return;
    const editor = this.getActiveEditor();
    if (!editor) return;

    const sel = window.getSelection();
    const range = sel && sel.rangeCount > 0 && this.isSelectionInEditor(sel)
      ? sel.getRangeAt(0)
      : this.savedSelection;
    if (!range) return;

    // Zaznaczenie obejmujące kilka punktów przełącza je RAZEM — do stanu przeciwnego
    // niż pierwszy punkt (jak grupowe odhaczanie w Wordzie/To Do).
    const items = this._checkboxItemsInRange(editor, range);
    if (items.length === 0) {
      // Poza listą pól wyboru: przełącz formant(y) pola wyboru w zaznaczeniu / bieżącym bloku.
      if (this._toggleSdtCheckboxesInRange(editor, range)) this.onContentChange();
      return;
    }

    const first = this._checkboxBulletState(items[0].parentElement as HTMLElement);
    const target = !(first?.checked ?? false);
    items.forEach(li => this._setCheckboxBulletState(li, target));

    // Kursor z powrotem do przełączanego punktu (przenosiny li unieważniają selekcję).
    const caret = document.createRange();
    caret.selectNodeContents(items[items.length - 1]);
    caret.collapse(false);
    sel?.removeAllRanges();
    sel?.addRange(caret);
    this.savedSelection = caret.cloneRange();

    this.onContentChange();
  }

  /** Elementy list PÓL WYBORU objęte zakresem (kolejność dokumentu). */
  private _checkboxItemsInRange(editor: HTMLElement, range: Range): HTMLLIElement[] {
    const closestLi = (n: Node | null): HTMLLIElement | null =>
      ((n instanceof Element ? n : n?.parentElement)?.closest('li') ?? null) as HTMLLIElement | null;
    const isCheckboxItem = (li: HTMLLIElement | null): li is HTMLLIElement => {
      const container = li?.parentElement;
      return !!li && container instanceof HTMLElement && editor.contains(li)
        && /^(ul|ol)$/i.test(container.tagName) && container.hasAttribute('data-num-id')
        && this._checkboxBulletState(container) !== null;
    };

    const startLi = closestLi(range.startContainer);
    if (range.collapsed) return isCheckboxItem(startLi) ? [startLi] : [];

    const found = Array.from(editor.querySelectorAll<HTMLLIElement>('li'))
      .filter(li => isCheckboxItem(li) && range.intersectsNode(li));
    return found.length > 0 ? found : isCheckboxItem(startLi) ? [startLi] : [];
  }

  /**
   * Ustawia stan punktora-checkboxa JEDNEGO li (no-op, gdy już taki jest). Sąsiednie
   * wydzielone fragmenty o identycznym nadpisaniu są sklejane w jeden kontener — 20
   * odhaczonych punktów to jedna instancja numeracji, nie 20.
   */
  private _setCheckboxBulletState(li: HTMLLIElement, checked: boolean, force = false): void {
    const container = li.parentElement;
    if (!(container instanceof HTMLElement)) return;
    const state = this._checkboxBulletState(container);
    if (!state) return; // punktor nie jest checkboxem
    // `force` = zmiana ZNAKU zaznaczenia na już odhaczonym punkcie (stan ten sam, inny glif).
    if (!force && state.checked === checked) return;
    if (force && container.getAttribute('data-lvl-text') === (checked ? state.checkedLvlText : state.uncheckedLvlText)) return;

    const solo = this._isolateListItem(container, li);
    const targetLvlText = checked ? state.checkedLvlText : state.uncheckedLvlText;
    solo.setAttribute('data-lvl-text', targetLvlText);
    if (state.bulletFont) solo.setAttribute('data-bullet-font', state.bulletFont);
    solo.setAttribute('data-lvl-override', '1');
    // Odhaczenie ma wracać do ORYGINALNEGO pustego znaku tej listy (❑ vs ☐).
    solo.setAttribute('data-unchecked-lvl-text', state.uncheckedLvlText);

    const glyph = bulletGlyphFromContract(targetLvlText, state.bulletFont);
    const marker = li.querySelector<HTMLElement>(':scope > span.list-marker');
    if (marker) {
      marker.textContent = glyph;
    } else {
      const synthesized = synthesizeBulletMarker(solo);
      if (synthesized) li.insertBefore(synthesized, li.firstChild);
    }

    this._mergeAdjacentCheckboxOverrides(solo);
  }

  /**
   * Skleja `solo` z sąsiednim fragmentem listy o TYM SAMYM punktorze (ten sam rodzic, poziom
   * i abstrakt). Punkt wraca do sąsiada — także do oryginalnej listy po odznaczeniu — więc
   * zapis nie mnoży instancji numeracji ponad potrzebę.
   */
  private _mergeAdjacentCheckboxOverrides(solo: HTMLElement): void {
    const sameBullet = (a: HTMLElement, other: Element | null): other is HTMLElement =>
      other instanceof HTMLElement && other.tagName === a.tagName
      && other.hasAttribute('data-num-id')
      // Inna instancja listy (np. lista „rozpoczęta od nowa") NIE jest tym samym punktorem —
      // wyjątek: fragmenty nadpisań pól wyboru, które z definicji mają własne data-num-id.
      && (other.getAttribute('data-num-id') === a.getAttribute('data-num-id')
        || other.getAttribute('data-lvl-override') === '1' || a.getAttribute('data-lvl-override') === '1')
      && ['data-num-fmt', 'data-lvl-text', 'data-bullet-font', 'data-ilvl', 'data-abstract-num-id']
        .every(attr => other.getAttribute(attr) === a.getAttribute(attr));

    const prev = solo.previousElementSibling;
    if (sameBullet(solo, prev)) {
      Array.from(solo.children).forEach(k => prev.appendChild(k));
      solo.remove();
      solo = prev;
    }
    const next = solo.nextElementSibling;
    if (sameBullet(solo, next)) {
      if (solo.getAttribute('data-lvl-override') === '1' && next.getAttribute('data-lvl-override') !== '1') {
        // Zostaje kontener BEZ nadpisania (oryginalna tożsamość listy).
        Array.from(solo.children).reverse().forEach(k => next.insertBefore(k, next.firstChild));
        solo.remove();
      } else {
        Array.from(next.children).forEach(k => solo.appendChild(k));
        next.remove();
      }
    }
  }

  // ═════════════ Poziomy listy pól wyboru (Tab / Shift+Tab / przyciski wcięć) ═════════════

  /**
   * Tab w liście pól wyboru jak w Wordzie: na początku punktu (lub z zaznaczeniem) obniża
   * poziom, w środku tekstu wstawia tabulator (false → wołający idzie dalej); Shift+Tab
   * podnosi poziom. Zwraca true, gdy klawisz został skonsumowany.
   */
  private _handleChecklistTab(backwards: boolean): boolean {
    const editor = this.getActiveEditor();
    const sel = window.getSelection();
    if (!editor || !sel || sel.rangeCount === 0 || !this.isSelectionInEditor(sel)) return false;
    const range = sel.getRangeAt(0);
    const items = this._listItemsInRange(editor, range);
    if (items.length === 0) return false;
    if (!backwards && range.collapsed && !this._isCaretAtBlockStart(true)) return false;

    this._changeChecklistLevel(items, backwards ? -1 : 1);
    this.onContentChange();
    this._flushPaginateSoon();
    return true;
  }

  /** Zmienia poziom punktów (kolejność dokumentu), zachowując selekcję mimo przenosin li. */
  private _changeChecklistLevel(items: HTMLLIElement[], direction: 1 | -1): void {
    const editor = this.getActiveEditor();
    if (!editor) return;
    this._preservingSelection(editor, () => {
      // Punkt zagnieżdżony w innym przenoszonym punkcie jedzie razem z gospodarzem.
      const roots = items.filter(li => !items.some(other => other !== li && other.contains(li)));
      roots.forEach(li => this._relevelChecklistItem(li, direction));
    });
    // Numeracja zależy od poziomu („2." po Tab → „1.3.") — przelicz od razu, nie po debounce.
    this.refreshListLabels();
  }

  /**
   * Model PŁASKI: punkt na innym poziomie = osobny fragment listy z `data-ilvl` (kontrakt
   * readera — fragment może zaczynać się na głębszym poziomie), wspólny `data-abstract-num-id`
   * niesie definicje poziomów do jednego w:abstractNum. Bez zagnieżdżania ul w li: natywny
   * `indent` Chrome'a zostawia `ul > ul` bez li, a klik-toggle/Enter działają na kontenerze.
   * Definicja poziomu docelowego: z dokumentu (inny fragment tego abstraktu — jak Word, poziom
   * bierze format z definicji listy), a gdy go nie ma — ten sam punktor z wcięciem +720 tw.
   */
  private _relevelChecklistItem(li: HTMLLIElement, delta: 1 | -1): void {
    const container = li.parentElement;
    if (!(container instanceof HTMLElement)) return;
    const state = this._checkboxBulletState(container);
    if (!state) {
      this._relevelRegularListItem(li, container, delta);
      return;
    }

    // Struktura zagnieżdżona z importu (li > ul > li): podniesienie = wyjście do listy gospodarza;
    // kolejne punkty zostają dziećmi podnoszonego (jak w Wordzie).
    const hostLi = container.parentElement;
    if (delta < 0 && hostLi instanceof HTMLLIElement && hostLi.parentElement) {
      const kids = Array.from(container.children);
      const after = kids.slice(kids.indexOf(li) + 1);
      hostLi.parentElement.insertBefore(li, hostLi.nextSibling);
      if (after.length > 0) {
        const tail = container.cloneNode(false) as HTMLElement;
        after.forEach(k => tail.appendChild(k));
        li.appendChild(tail);
      }
      if (!container.querySelector(':scope > li')) container.remove();
      this._syncChecklistItemWithContainer(li, state.checked);
      return;
    }

    const level = this._listLevelOf(container);
    const target = Math.min(8, Math.max(0, level + delta));
    if (target === level) return;

    if (!container.hasAttribute('data-abstract-num-id')) {
      container.setAttribute('data-abstract-num-id', `chk-abs-${++this._checkboxListSeq}`);
    }
    const solo = this._isolateListItem(container, li);
    const template = this._findListLevelTemplate(solo, target);
    if (template) {
      // Wzorzec z importu bywa zagnieżdżony (padding WZGLĘDNY) — fragment płaski potrzebuje pozycji
      // bezwzględnej pomniejszonej o własnych przodków.
      const absolutePad = this._absoluteListPaddingPx(template);
      Array.from(solo.attributes).forEach(a => solo.removeAttribute(a.name));
      Array.from(template.attributes).forEach(a => solo.setAttribute(a.name, a.value));
      solo.removeAttribute('data-split-para');
      solo.style.paddingLeft = `${Math.max(0, absolutePad - this._ancestorListPaddingPx(solo))}px`;
    } else {
      const step = 2 * WysiwygEditorComponent.CHECKLIST_HANGING_PX;
      const pad = parseFloat(solo.style.paddingLeft);
      solo.style.paddingLeft = `${Math.max(0, (Number.isFinite(pad) ? pad : step) + delta * step)}px`;
      const indLeft = parseInt(solo.getAttribute('data-ind-left-tw') ?? '', 10);
      if (Number.isFinite(indLeft)) {
        const hangingTw = parseInt(solo.getAttribute('data-ind-hanging-tw') ?? '', 10) || 0;
        solo.setAttribute('data-ind-left-tw',
          String(Math.max(hangingTw, indLeft + delta * WysiwygEditorComponent.CHECKLIST_INDENT_TW)));
      }
      solo.setAttribute('data-ilvl', String(target));
    }
    this._syncChecklistItemWithContainer(li, state.checked);
    if (li.parentElement instanceof HTMLElement) this._mergeAdjacentCheckboxOverrides(li.parentElement);
  }

  /** Punkty DOWOLNEJ listy objęte zakresem (kolejność dokumentu). */
  private _listItemsInRange(editor: HTMLElement, range: Range): HTMLLIElement[] {
    const closestLi = (n: Node | null): HTMLLIElement | null =>
      ((n instanceof Element ? n : n?.parentElement)?.closest('li') ?? null) as HTMLLIElement | null;
    const inEditorList = (li: HTMLLIElement | null): li is HTMLLIElement =>
      !!li && editor.contains(li) && !!li.parentElement && /^(UL|OL)$/.test(li.parentElement.tagName);
    const startLi = closestLi(range.startContainer);
    if (range.collapsed) return inEditorList(startLi) ? [startLi] : [];
    const found = Array.from(editor.querySelectorAll<HTMLLIElement>('li'))
      .filter(li => inEditorList(li) && range.intersectsNode(li));
    return found.length > 0 ? found : inEditorList(startLi) ? [startLi] : [];
  }

  /**
   * Zmiana poziomu punktu ZWYKŁEJ listy (numerowanej/punktowanej) — jak Tab/Shift+Tab w Wordzie.
   *
   * Lista z kontraktem DOCX (data-num-id): model płaski jak dla pól wyboru, ale punkt ZOSTAJE w tej
   * samej instancji listy (ten sam data-num-id, inne data-ilvl — dokładnie to, co emituje reader),
   * więc numeracja liczy się dalej („2." po Tab → „1.3.", dawne „2.1." → „1.4."). Definicja poziomu
   * docelowego: (1) inny fragment tej listy na tym poziomie, (2) pełna definicja listy z importu
   * (data-list-abstracts — poziomy nieużyte w treści), (3) przesunięcie bieżącej definicji.
   * Listy zagnieżdżone w przenoszonym punkcie wychodzą ZA niego jako fragmenty (Word zmienia poziom
   * tylko wskazanego akapitu — „dzieci" zostają na swoim poziomie).
   *
   * Lista bez kontraktu (utworzona w edytorze): standardowe zagnieżdżenie HTML (li > ul/ol > li),
   * które writer mapuje na poziomy; pierwszy punkt listy nie ma gospodarza — bez zmian.
   */
  private _relevelRegularListItem(li: HTMLLIElement, container: HTMLElement, delta: 1 | -1): void {
    const hostLi = container.parentElement instanceof HTMLLIElement ? container.parentElement : null;

    // Podniesienie punktu ZAGNIEŻDŻONEGO (import i listy z edytora): wyjście za gospodarza;
    // kolejne punkty zostają dziećmi podnoszonego (jak w Wordzie).
    if (delta < 0 && hostLi?.parentElement) {
      const kids = Array.from(container.children);
      const after = kids.slice(kids.indexOf(li) + 1);
      hostLi.parentElement.insertBefore(li, hostLi.nextSibling);
      if (after.length > 0) {
        const tail = container.cloneNode(false) as HTMLElement;
        tail.removeAttribute('start');
        after.forEach(k => tail.appendChild(k));
        li.appendChild(tail);
      }
      if (!container.querySelector(':scope > li')) container.remove();
      this._syncListItemMarker(li);
      this._fixChildListPadding(li);
      return;
    }

    if (!container.hasAttribute('data-num-id')) {
      if (delta < 0) return; // poziom 0 listy z edytora
      const host = li.previousElementSibling;
      if (!(host instanceof HTMLLIElement)) return;
      const last = host.lastElementChild;
      const nested = last instanceof HTMLElement && last.tagName === container.tagName && !last.hasAttribute('data-num-id')
        ? last
        : host.appendChild(document.createElement(container.tagName.toLowerCase()));
      nested.appendChild(li);
      return;
    }

    const level = this._listLevelOf(container);
    const target = Math.min(8, Math.max(0, level + delta));
    if (target === level) return;

    // „Dzieci" punktu zostają na swoim poziomie: wychodzą za punkt jako fragmenty tej samej listy.
    const childLists = Array.from(li.children).filter(
      (c): c is HTMLElement => c instanceof HTMLElement && /^(UL|OL)$/.test(c.tagName));

    const numId = container.getAttribute('data-num-id')!;
    const isolated = this._isolateListItem(container, li);
    isolated.setAttribute('data-num-id', numId); // ta sama instancja listy, inny poziom
    const solo = this._applyListLevelDefinition(isolated, target, delta);

    let anchor: Element = solo;
    for (const child of childLists) {
      const absolutePad = this._absoluteListPaddingPx(child);
      solo.parentNode!.insertBefore(child, anchor.nextSibling);
      child.style.paddingLeft = `${Math.max(0, absolutePad - this._ancestorListPaddingPx(child))}px`;
      anchor = child;
    }
    this._syncListItemMarker(li);
    this._mergeAdjacentCheckboxOverrides(solo);
  }

  /** Nadaje fragmentowi definicję poziomu docelowego (patrz _relevelRegularListItem). Zwraca kontener. */
  private _applyListLevelDefinition(solo: HTMLElement, target: number, delta: 1 | -1): HTMLElement {
    const numId = solo.getAttribute('data-num-id')!;
    const base = this._ancestorListPaddingPx(solo);
    ['start', 'data-start-override', 'data-split-para'].forEach(a => solo.removeAttribute(a));

    const template = this._findListLevelTemplate(solo, target);
    if (template) {
      const absolutePad = this._absoluteListPaddingPx(template);
      const replaced = this._retagList(solo, template.tagName);
      Array.from(replaced.attributes).forEach(a => replaced.removeAttribute(a.name));
      Array.from(template.attributes).forEach(a => replaced.setAttribute(a.name, a.value));
      ['start', 'data-start-override', 'data-split-para'].forEach(a => replaced.removeAttribute(a));
      replaced.setAttribute('data-num-id', numId);
      replaced.style.paddingLeft = `${Math.max(0, absolutePad - base)}px`;
      return replaced;
    }

    const def = this._sourceListLevelDefinition(solo.getAttribute('data-abstract-num-id'), target);
    if (def) {
      const replaced = this._retagList(solo, def['tag'] === 'ol' ? 'OL' : 'UL');
      const keep = new Set(['data-num-id', 'data-abstract-num-id']);
      Array.from(replaced.attributes).forEach(a => { if (!keep.has(a.name)) replaced.removeAttribute(a.name); });
      replaced.setAttribute('data-ilvl', String(target));
      for (const key of ['num-fmt', 'start', 'lvl-text', 'bullet-font', 'suffix', 'is-legal', 'lvl-restart',
        'ind-left-tw', 'ind-hanging-tw', 'ind-first-line-tw', 'marker-color', 'marker-size']) {
        if (def[key] !== undefined) replaced.setAttribute(`data-${key}`, def[key]);
      }
      const twToPx = (tw: string | undefined) => (tw && Number.isFinite(parseInt(tw, 10)) ? parseInt(tw, 10) / 15 : null);
      const leftPx = twToPx(def['ind-left-tw']);
      const hangingPx = twToPx(def['ind-hanging-tw']);
      replaced.style.margin = '0';
      replaced.style.paddingLeft = `${Math.max(0, (leftPx ?? 36 * (target + 1)) - base)}px`;
      replaced.style.listStyleType = def['list-style'] ?? (def['tag'] === 'ol' ? 'decimal' : 'disc');
      if (hangingPx !== null && hangingPx > 0) replaced.style.setProperty('--ind-hanging', `${hangingPx}px`);
      if (def['marker-color-css']) replaced.style.setProperty('--marker-color', def['marker-color-css']);
      return replaced;
    }

    // Brak definicji poziomu (lista spoza importu): ta sama definicja przesunięta o poziom.
    const step = 2 * WysiwygEditorComponent.CHECKLIST_HANGING_PX;
    const pad = parseFloat(solo.style.paddingLeft);
    solo.style.paddingLeft = `${Math.max(0, (Number.isFinite(pad) ? pad : step) + delta * step)}px`;
    const indLeft = parseInt(solo.getAttribute('data-ind-left-tw') ?? '', 10);
    if (Number.isFinite(indLeft)) {
      solo.setAttribute('data-ind-left-tw', String(Math.max(0, indLeft + delta * WysiwygEditorComponent.CHECKLIST_INDENT_TW)));
    }
    const lvlText = solo.getAttribute('data-lvl-text');
    if (lvlText && lvlText.includes('%')) {
      // „%1." poziomu 0 na poziomie 1 musi wskazywać licznik SWOJEGO poziomu („%2.").
      solo.setAttribute('data-lvl-text', lvlText.replace(/%(\d)(?!.*%\d)/, `%${target + 1}`));
    }
    solo.setAttribute('data-ilvl', String(target));
    return solo;
  }

  /** Zamienia znacznik kontenera (ul ↔ ol), zachowując atrybuty i dzieci; zwraca właściwy element. */
  private _retagList(list: HTMLElement, tagName: string): HTMLElement {
    if (list.tagName === tagName.toUpperCase()) return list;
    const fresh = document.createElement(tagName.toLowerCase());
    Array.from(list.attributes).forEach(a => fresh.setAttribute(a.name, a.value));
    while (list.firstChild) fresh.appendChild(list.firstChild);
    list.replaceWith(fresh);
    return fresh;
  }

  /** Definicja poziomu z data-list-abstracts kontenera dokumentu (reader: pełne definicje list). */
  private _sourceListLevelDefinition(abstractId: string | null, level: number): Record<string, string> | null {
    if (!abstractId) return null;
    const raw = this._documentContainerAttrs?.find(a => a.name === 'data-list-abstracts')?.value;
    if (!raw) return null;
    try {
      const parsed = JSON.parse(raw) as Record<string, { levels?: Array<Record<string, string>> }>;
      return parsed[abstractId]?.levels?.find(l => l['ilvl'] === String(level)) ?? null;
    } catch {
      return null;
    }
  }

  /** Suma padding-left list-PRZODKÓW (px) — baza, od której liczony jest padding fragmentu. */
  private _ancestorListPaddingPx(list: HTMLElement): number {
    let sum = 0;
    for (let el = list.parentElement; el; el = el.parentElement) {
      if (el.tagName === 'UL' || el.tagName === 'OL') sum += parseFloat(el.style.paddingLeft) || 0;
      if (el.classList.contains('editor-content')) break;
    }
    return sum;
  }

  /** Pozycja tekstu listy od krawędzi treści (px): własny padding + paddingi przodków. */
  private _absoluteListPaddingPx(list: HTMLElement): number {
    return (parseFloat(list.style.paddingLeft) || 0) + this._ancestorListPaddingPx(list);
  }

  /** Po zmianie gospodarza: listy-dzieci punktu wracają na pozycję wynikającą z ICH definicji poziomu. */
  private _fixChildListPadding(li: HTMLLIElement): void {
    for (const child of Array.from(li.children)) {
      if (!(child instanceof HTMLElement) || !/^(UL|OL)$/.test(child.tagName)) continue;
      const leftTw = parseInt(child.getAttribute('data-ind-left-tw') ?? '', 10);
      if (!Number.isFinite(leftTw)) continue;
      child.style.paddingLeft = `${Math.max(0, leftTw / 15 - this._ancestorListPaddingPx(child))}px`;
    }
  }

  /**
   * Znacznik punktu zgodny z NOWYM kontenerem: span.list-marker tylko tam, gdzie kontener sam nie
   * rysuje punktora (list-style none + punktor z kontraktu); numeracja i natywne punktory — bez spana
   * (inaczej podwójny punktor / punktor przy numerze).
   */
  private _syncListItemMarker(li: HTMLLIElement): void {
    const container = li.parentElement;
    if (!(container instanceof HTMLElement)) return;
    li.querySelectorAll(':scope > span.list-marker').forEach(m => m.remove());
    li.removeAttribute('data-list-label');
    li.removeAttribute('data-list-suffix');
    if (container.style.listStyleType !== 'none') return;
    const marker = synthesizeBulletMarker(container);
    if (marker) li.insertBefore(marker, li.firstChild);
  }

  /** Poziom kontenera listy: data-ilvl, a bez niego głębokość zagnieżdżenia w HTML. */
  private _listLevelOf(container: HTMLElement): number {
    const raw = parseInt(container.getAttribute('data-ilvl') ?? '', 10);
    if (Number.isFinite(raw)) return Math.min(8, Math.max(0, raw));
    let depth = 0;
    for (let el = container.parentElement; el; el = el.parentElement) {
      if (el.tagName === 'UL' || el.tagName === 'OL') depth++;
    }
    return Math.min(8, depth);
  }

  /** Fragment tej samej definicji listy (abstrakt) na poziomie docelowym — bez nadpisań per punkt. */
  private _findListLevelTemplate(solo: HTMLElement, target: number): HTMLElement | null {
    const abstractId = solo.getAttribute('data-abstract-num-id');
    if (!abstractId) return null;
    const roots = (this.pageEditorRefs?.toArray() ?? []).map(r => r.nativeElement);
    const active = this.getActiveEditor();
    if (active && !roots.includes(active)) roots.push(active);
    for (const root of roots) {
      for (const el of Array.from(root.querySelectorAll<HTMLElement>('ul[data-num-id], ol[data-num-id]'))) {
        if (el !== solo && el.getAttribute('data-abstract-num-id') === abstractId
          && el.getAttribute('data-lvl-override') !== '1' && this._listLevelOf(el) === target) {
          return el;
        }
      }
    }
    return null;
  }

  /**
   * Po przenosinach punktu: marker zgodny z kontraktem NOWEGO kontenera (glif/szerokość albo
   * brak markera dla listy numerowanej), a stan ☑/☐ punktu odtworzony, gdy kontener to lista
   * pól wyboru o innym stanie.
   */
  private _syncChecklistItemWithContainer(li: HTMLLIElement, checked: boolean): void {
    const container = li.parentElement;
    if (!(container instanceof HTMLElement)) return;
    li.querySelectorAll(':scope > span.list-marker').forEach(m => m.remove());
    const marker = synthesizeBulletMarker(container);
    if (marker) li.insertBefore(marker, li.firstChild);
    const state = this._checkboxBulletState(container);
    if (state && state.checked !== checked) this._setCheckboxBulletState(li, checked);
  }

  /** Wykonuje mutację DOM przenoszącą węzły i odtwarza selekcję (węzły w li zachowują tożsamość). */
  private _preservingSelection(editor: HTMLElement, mutate: () => void): void {
    const sel = window.getSelection();
    const live = sel && sel.rangeCount > 0 && this.isSelectionInEditor(sel) ? sel.getRangeAt(0) : null;
    const saved = live && {
      sc: live.startContainer, so: live.startOffset, ec: live.endContainer, eo: live.endOffset,
    };

    mutate();

    if (!saved || !sel || !editor.contains(saved.sc) || !editor.contains(saved.ec)) return;
    const length = (n: Node) =>
      n.nodeType === Node.TEXT_NODE ? (n.textContent ?? '').length : n.childNodes.length;
    try {
      const restored = document.createRange();
      restored.setStart(saved.sc, Math.min(saved.so, length(saved.sc)));
      restored.setEnd(saved.ec, Math.min(saved.eo, length(saved.ec)));
      sel.removeAllRanges();
      sel.addRange(restored);
      this.savedSelection = restored.cloneRange();
    } catch {
      // Granica w usuniętym węźle — selekcję odtworzy kolejny klik/komenda.
    }
  }

  // ═════════════ Lista pól wyboru (checklista) — tworzenie/zdejmowanie z toolbara ═════════════

  /** Kontrakt nowej listy pól wyboru: ☐ = Wingdings 0xA8 w PUA (tak zapisuje ją sam Word). */
  private static readonly CHECKLIST_INDENT_TW = 720;
  private static readonly CHECKLIST_HANGING_TW = 360;
  private static readonly CHECKLIST_HANGING_PX = 24;

  /**
   * Przełącza bieżący blok/zaznaczenie w LISTĘ PÓL WYBORU (punktor ☐, klik w punktor
   * odhacza) i z powrotem w zwykłe akapity. Lista numerowana/punktowana pod kursorem jest
   * KONWERTOWANA (nowa tożsamość data-num-id — definicja punktora nie może przeciec do
   * pozostałych fragmentów oryginalnej listy; semantyka „pierwsze wystąpienie wygrywa").
   */
  toggleCheckboxList(): void {
    if (this.readOnly) return;
    const editor = this.getActiveEditor();
    if (!editor) return;

    let containers = this._listContainersInSelection(editor);
    if (containers.length > 0 && containers.every(c => this._checkboxBulletState(c) !== null)) {
      // Już lista pól wyboru → zdejmij ją z zaznaczonych punktów. Własna implementacja:
      // execCommand zostawia marker (contenteditable=false) w treści i gołe węzły z <br>.
      const sel = window.getSelection();
      const range = sel && sel.rangeCount > 0 && this.isSelectionInEditor(sel)
        ? sel.getRangeAt(0)
        : this.savedSelection;
      const items = range ? this._checkboxItemsInRange(editor, range) : [];
      const paragraphs = items.map(li => this._unlistChecklistItem(li));
      const last = paragraphs[paragraphs.length - 1];
      if (last) {
        const caret = document.createRange();
        caret.selectNodeContents(last);
        caret.collapse(false);
        sel?.removeAllRanges();
        sel?.addRange(caret);
        this.savedSelection = caret.cloneRange();
      }
      this.onContentChange();
      return;
    }

    if (containers.length === 0 || containers.some(c => c.tagName !== 'UL')) {
      document.execCommand('insertUnorderedList', false);
      this._hoistListsOutOfParagraphs(editor);
      containers = this._listContainersInSelection(editor);
    }
    containers.forEach(c => this._applyChecklistContract(c));
    this.refreshListLabels();
    this.onContentChange();
  }

  /**
   * Chrome (insertUnorderedList na akapitach `<p>`) potrafi zostawić listę WEWNĄTRZ `<p>` —
   * niepoprawny HTML (parser zapisu zamyka `<p>` przed `<ul>`, a kolejne bloki tworzone
   * Enterem lądują w tym samym akapicie). Lista wychodzi na poziom bloku; treść akapitu
   * przed/za listą zostaje w jego kopiach, pusty akapit znika.
   */
  private _hoistListsOutOfParagraphs(editor: HTMLElement): void {
    const lists = editor.querySelectorAll<HTMLElement>('p > ul, p > ol');
    if (lists.length === 0) return;

    // Przenosiny węzła zwijają selekcję do usuwanego akapitu — stąd _preservingSelection.
    this._preservingSelection(editor, () => lists.forEach(list => {
      const p = list.parentElement!;
      const parent = p.parentNode;
      if (!parent) return;
      const hasContent = (el: Element) =>
        (el.textContent ?? '').replace(/[​\s]/g, '') !== '' || !!el.querySelector('img, table, .sdt-inline');

      const tail = p.cloneNode(false) as HTMLElement;
      tail.removeAttribute('id');
      while (list.nextSibling) tail.appendChild(list.nextSibling);
      parent.insertBefore(list, p.nextSibling);
      if (hasContent(tail) || tail.querySelector('ul, ol')) parent.insertBefore(tail, list.nextSibling);
      if (!hasContent(p)) p.remove();
    }));
  }

  /** Zamienia punkt listy pól wyboru w zwykły akapit (rozcina kontener wokół punktu). */
  private _unlistChecklistItem(li: HTMLLIElement): HTMLElement {
    const container = li.parentElement as HTMLElement;
    const parent = container.parentNode!;
    const kids = Array.from(container.children);
    const after = kids.slice(kids.indexOf(li) + 1);
    if (after.length > 0) {
      const tail = container.cloneNode(false) as HTMLElement;
      after.forEach(k => tail.appendChild(k));
      parent.insertBefore(tail, container.nextSibling);
    }

    const p = document.createElement('p');
    // Formatowanie akapitu punktu (odstępy/wyrównanie/styl z readera) zostaje przy treści.
    for (const attr of ['style', 'data-style-id', 'data-tab-stops']) {
      const value = li.getAttribute(attr);
      if (value) p.setAttribute(attr, value);
    }
    p.style.removeProperty('text-indent');
    if (!p.getAttribute('style')) p.removeAttribute('style');

    li.querySelectorAll(':scope > span.list-marker').forEach(m => m.remove());
    // Listy zagnieżdżone w punkcie nie mogą wejść do <p> — zostają blokami ZA akapitem.
    const nestedLists = Array.from(li.children).filter(c => c.tagName === 'UL' || c.tagName === 'OL');
    nestedLists.forEach(n => n.remove());
    while (li.firstChild) p.appendChild(li.firstChild);
    if (!p.firstChild) p.appendChild(document.createElement('br'));
    parent.insertBefore(p, container.nextSibling);
    nestedLists.reverse().forEach(n => parent.insertBefore(n, p.nextSibling));
    li.remove();
    if (!container.querySelector(':scope > li')) container.remove();
    return p;
  }

  /**
   * Backspace na początku punktu ze znacznikiem = zdjęcie punktora (punkt → akapit, jak w
   * Wordzie); lista ZA punktem zachowuje tożsamość (data-num-id), więc trwa dalej. Kolejny
   * Backspace to już zwykłe scalenie akapitu z poprzednim blokiem. Delete na końcu punktu,
   * gdy następny punkt ma znacznik = dociągnięcie jego treści (bez kasowania samego znacznika).
   * Zwraca true, gdy klawisz został obsłużony.
   */
  private _handleListMarkerDeletion(forward: boolean): boolean {
    const editor = this.getActiveEditor();
    const sel = window.getSelection();
    if (!editor || !sel || sel.rangeCount === 0 || !sel.isCollapsed || !this.isSelectionInEditor(sel)) return false;
    const start = sel.getRangeAt(0).startContainer;
    const li = ((start instanceof Element ? start : start.parentElement)?.closest('li') ?? null) as HTMLLIElement | null;
    if (!li || !editor.contains(li) || !(li.parentElement instanceof HTMLElement)) return false;
    const hasMarker = (item: Element | null) => !!item?.querySelector(':scope > span.list-marker');

    if (!forward) {
      if (!hasMarker(li) || !this._isCaretAtBlockStart(true)) return false;
      const p = this._unlistChecklistItem(li);
      const caret = document.createRange();
      const first = p.firstChild;
      if (first?.nodeType === Node.TEXT_NODE) caret.setStart(first, 0);
      else caret.setStart(p, 0);
      caret.collapse(true);
      sel.removeAllRanges();
      sel.addRange(caret);
      this.savedSelection = caret.cloneRange();
      this.onContentChange();
      return true;
    }

    // Delete: tylko gdy karetka stoi na KOŃCU treści punktu, a następny punkt ma znacznik.
    // Następny punkt bywa w SĄSIEDNIM fragmencie listy (odhaczony punkt, inny poziom).
    const nextList = li.parentElement.nextElementSibling;
    const next = li.nextElementSibling
      ?? (nextList && /^(UL|OL)$/.test(nextList.tagName) ? nextList.querySelector(':scope > li') : null);
    if (!next || next.tagName !== 'LI' || !hasMarker(next)) return false;
    const nextContainer = next.parentElement;
    const tail = document.createRange();
    tail.selectNodeContents(li);
    tail.setStart(sel.getRangeAt(0).endContainer, sel.getRangeAt(0).endOffset);
    const rest = tail.cloneContents();
    rest.querySelectorAll('ul, ol').forEach(n => n.remove());
    if ((rest.textContent ?? '').replace(/[​]/g, '') !== '' || rest.querySelector('img, .sdt-inline')) {
      return false;
    }
    // <br> za karetką: w PUSTYM punkcie to wypełniacz (znika po dociągnięciu treści),
    // w punkcie z tekstem — miękki podział wiersza, który ma skasować przeglądarka.
    const filler = Array.from(li.children).find(c => c.tagName === 'BR') ?? null;
    if (rest.querySelector('br')) {
      if (!this._isEmptyChecklistItem(li)) return false;
      filler?.remove();
    }

    const caretAnchor = document.createTextNode('');
    const nestedInLi = Array.from(li.children).find(c => c.tagName === 'UL' || c.tagName === 'OL') ?? null;
    li.insertBefore(caretAnchor, nestedInLi);
    next.querySelectorAll(':scope > span.list-marker').forEach(m => m.remove());
    while (next.firstChild) li.insertBefore(next.firstChild, nestedInLi);
    next.remove();
    if (nextContainer && nextContainer !== li.parentElement && !nextContainer.querySelector(':scope > li')) {
      nextContainer.remove();
    }
    const caret = document.createRange();
    caret.setStartAfter(caretAnchor);
    caret.collapse(true);
    caretAnchor.remove();
    sel.removeAllRanges();
    sel.addRange(caret);
    this.savedSelection = caret.cloneRange();
    this.onContentChange();
    return true;
  }

  /** Kontenery list (ul/ol) elementów objętych bieżącą selekcją, bez duplikatów. */
  private _listContainersInSelection(editor: HTMLElement): HTMLElement[] {
    const sel = window.getSelection();
    const range = sel && sel.rangeCount > 0 && this.isSelectionInEditor(sel)
      ? sel.getRangeAt(0)
      : this.savedSelection;
    if (!range) return [];
    const closestLi = (n: Node | null): Element | null =>
      (n instanceof Element ? n : n?.parentElement)?.closest('li') ?? null;
    const items = range.collapsed
      ? [closestLi(range.startContainer)]
      : [closestLi(range.startContainer),
         ...Array.from(editor.querySelectorAll('li')).filter(li => range.intersectsNode(li))];
    const result: HTMLElement[] = [];
    for (const li of items) {
      const c = li?.parentElement;
      if (c instanceof HTMLElement && /^(ul|ol)$/i.test(c.tagName) && editor.contains(c) && !result.includes(c)) {
        result.push(c);
      }
    }
    return result;
  }

  /** Nadaje kontenerowi kontrakt listy pól wyboru (writer buduje z niego definicję poziomu). */
  private _applyChecklistContract(container: HTMLElement): void {
    const hadContract = container.hasAttribute('data-num-id');
    container.setAttribute('data-num-id', this._uniqueListInstanceId());
    container.setAttribute('data-abstract-num-id', `chk-abs-${++this._checkboxListSeq}`);
    ['data-lvl-override', 'data-unchecked-lvl-text', 'data-pic-bullet',
      'data-start', 'data-start-override', 'data-suffix', 'data-is-legal', 'data-lvl-restart']
      .forEach(a => container.removeAttribute(a));
    container.setAttribute('data-num-fmt', 'bullet');
    container.setAttribute('data-lvl-text', String.fromCharCode(0xf0a8));
    container.setAttribute('data-bullet-font', 'Wingdings');
    container.style.listStyleType = 'none';
    if (!hadContract) {
      // Lista świeżo utworzona w edytorze: geometria domyślnego poziomu 0 Worda (720/360 twips).
      container.setAttribute('data-ilvl', '0');
      container.setAttribute('data-ind-left-tw', String(WysiwygEditorComponent.CHECKLIST_INDENT_TW));
      container.setAttribute('data-ind-hanging-tw', String(WysiwygEditorComponent.CHECKLIST_HANGING_TW));
      container.style.margin = '0';
      container.style.paddingLeft = `${2 * WysiwygEditorComponent.CHECKLIST_HANGING_PX}px`;
      container.style.setProperty('--ind-hanging', `${WysiwygEditorComponent.CHECKLIST_HANGING_PX}px`);
    }

    const hanging = container.style.getPropertyValue('--ind-hanging').trim();
    Array.from(container.children).forEach(li => {
      if (li.tagName !== 'LI') return;
      li.removeAttribute('data-list-label');
      li.removeAttribute('data-list-suffix');
      let marker = li.querySelector<HTMLElement>(':scope > span.list-marker');
      if (!marker) {
        marker = synthesizeBulletMarker(container);
        if (!marker) return;
        li.insertBefore(marker, li.firstChild);
      }
      marker.replaceChildren('☐'); // także podmiana punktora obrazkowego (img)
      if (hanging) {
        marker.style.minWidth = hanging;
        marker.style.marginRight = '0';
      }
    });
  }

  // ═════════════ Formant „pole wyboru" (w14:checkbox) — wstawianie i przełączanie ═════════════

  /** Buduje formant pola wyboru wg kontraktu readera (span.sdt-checkbox, atomowy). */
  private _createSdtCheckbox(checked: boolean): HTMLElement {
    const box = document.createElement('span');
    box.className = 'sdt-inline sdt-checkbox';
    box.setAttribute('contenteditable', 'false');
    box.setAttribute('data-sdt-checkbox', '1');
    box.setAttribute('data-checked', checked ? '1' : '0');
    const mark = this._checkboxMarks.definition();
    box.setAttribute('data-checked-glyph', mark.glyph);
    // Jawny wybór znaku — writer buduje z niego w14:checkedState (kod + font), zamiast domyślnej definicji.
    box.setAttribute('data-checked-mark', mark.id);
    box.setAttribute('data-unchecked-glyph', '☐');
    box.textContent = checked ? mark.glyph : '☐';
    return box;
  }

  /**
   * Wstawia w miejscu kursora formant pola wyboru (Word: Deweloper → „Pole wyboru —
   * formant zawartości"). Range.insertNode, nie insertHTML — Chrome wyrzuca element
   * contenteditable=false za akapit (ADR-0084).
   */
  insertCheckboxControl(): void {
    if (this.readOnly) return;
    const editor = this.getActiveEditor();
    if (!editor) return;
    const sel = window.getSelection();
    const range = sel && sel.rangeCount > 0 && this.isSelectionInEditor(sel)
      ? sel.getRangeAt(0)
      : this.savedSelection?.cloneRange() ?? null;
    if (!range || !editor.contains(range.startContainer)) return;

    // Kursor wewnątrz elementu nieedytowalnego (inny formant/pole/marker) → wstaw ZA nim.
    const host = (range.startContainer instanceof Element ? range.startContainer : range.startContainer.parentElement)
      ?.closest('[contenteditable="false"]');
    if (host && editor.contains(host)) {
      range.setStartAfter(host);
      range.collapse(true);
    } else {
      range.deleteContents();
    }

    const box = this._createSdtCheckbox(false);
    // Spacja ZA formantem: odstęp od etykiety i węzeł tekstowy dla karetki (pozycja
    // „za elementem nieedytowalnym" nie ma w Chrome prostokąta — ADR-0108 r.13).
    const space = document.createTextNode(' ');
    range.insertNode(space);
    range.insertNode(box);

    const caret = document.createRange();
    caret.setStart(space, space.length);
    caret.collapse(true);
    sel?.removeAllRanges();
    sel?.addRange(caret);
    this.savedSelection = caret.cloneRange();
    this.onContentChange();
  }

  /** Przełącza stan formantu pola wyboru (atrybut + glif); zapis odtwarza w14:checked. */
  private _toggleSdtCheckbox(box: HTMLElement): void {
    const checked = box.getAttribute('data-checked') !== '1';
    box.setAttribute('data-checked', checked ? '1' : '0');
    box.textContent = checked
      ? box.getAttribute('data-checked-glyph') || '☒'
      : box.getAttribute('data-unchecked-glyph') || '☐';
  }

  /** Formanty w zaznaczeniu, a przy kursorze — pierwszy formant bieżącego bloku. */
  private _toggleSdtCheckboxesInRange(editor: HTMLElement, range: Range): boolean {
    const boxes = this._sdtCheckboxesInRange(editor, range);
    boxes.forEach(b => this._toggleSdtCheckbox(b));
    return boxes.length > 0;
  }

  private _sdtCheckboxesInRange(editor: HTMLElement, range: Range): HTMLElement[] {
    if (range.collapsed) {
      const at = range.startContainer instanceof Element ? range.startContainer : range.startContainer.parentElement;
      const block = at?.closest('p, li, td, th, h1, h2, h3, h4, h5, h6, div');
      const first = block && editor.contains(block) ? block.querySelector<HTMLElement>('.sdt-checkbox') : null;
      return first ? [first] : [];
    }
    return Array.from(editor.querySelectorAll<HTMLElement>('.sdt-checkbox'))
      .filter(b => range.intersectsNode(b));
  }

  /**
   * Stosuje WYBRANY znak zaznaczenia (menu „Pola wyboru" → „Znak zaznaczenia") do pól pod kursorem / w zaznaczeniu:
   * odhaczone punkty listy kontrolnej dostają nowy punktor, a pojedyncze pola nową definicję stanu „zaznaczone"
   * (także puste — po kliknięciu pokażą wybrany znak). Pola poza zaznaczeniem zostają bez zmian; nowy wybór
   * obowiązuje dla każdego kolejnego odhaczenia i wstawienia.
   */
  applyCheckboxMark(): void {
    if (this.readOnly) return;
    const editor = this.getActiveEditor();
    if (!editor) return;

    const sel = window.getSelection();
    const range = sel && sel.rangeCount > 0 && this.isSelectionInEditor(sel)
      ? sel.getRangeAt(0)
      : this.savedSelection;
    if (!range) return;

    const mark = this._checkboxMarks.definition();
    let changed = false;

    for (const box of this._sdtCheckboxesInRange(editor, range)) {
      if (box.getAttribute('data-checked-mark') === mark.id && box.getAttribute('data-checked-glyph') === mark.glyph) continue;
      box.setAttribute('data-checked-glyph', mark.glyph);
      box.setAttribute('data-checked-mark', mark.id);
      if (box.getAttribute('data-checked') === '1') box.textContent = mark.glyph;
      changed = true;
    }

    const items = this._checkboxItemsInRange(editor, range)
      .filter(li => this._checkboxBulletState(li.parentElement as HTMLElement)?.checked === true);
    const before = items.map(li => li.parentElement?.getAttribute('data-lvl-text'));
    items.forEach(li => this._setCheckboxBulletState(li, true, true));
    if (items.some((li, index) => li.parentElement?.getAttribute('data-lvl-text') !== before[index])) {
      changed = true;
      // Kursor z powrotem do punktu (przenosiny li unieważniają selekcję).
      const caret = document.createRange();
      caret.selectNodeContents(items[items.length - 1]);
      caret.collapse(false);
      sel?.removeAllRanges();
      sel?.addRange(caret);
      this.savedSelection = caret.cloneRange();
    }

    if (changed) this.onContentChange();
  }

  /**
   * Klik w pole wyboru: formant (span.sdt-checkbox) albo punktor listy pól wyboru.
   * Zwraca true, gdy klik został skonsumowany przełączeniem.
   */
  private _toggleCheckboxFromClick(ev: MouseEvent): boolean {
    if (this.readOnly) return false;
    const target = ev.target instanceof Element ? ev.target : null;
    if (!target) return false;

    const box = target.closest<HTMLElement>('.sdt-checkbox');
    if (box) {
      ev.preventDefault();
      this._toggleSdtCheckbox(box);
      this.onContentChange();
      return true;
    }

    const marker = target.closest<HTMLElement>('li > span.list-marker');
    const li = marker?.parentElement as HTMLLIElement | null;
    const container = li?.parentElement;
    if (!marker || !li || !(container instanceof HTMLElement)) return false;
    const state = this._checkboxBulletState(container);
    if (!state || !container.hasAttribute('data-num-id')) return false;

    ev.preventDefault();
    this._setCheckboxBulletState(li, !state.checked);
    this.onContentChange();
    return true;
  }

  /**
   * Punkt bez WŁASNEJ treści: znacznik punktora i listy zagnieżdżone się nie liczą (pusty punkt,
   * który po cofnięciu poziomu przejął „dzieci", nadal jest pusty — Enter ma go wyprowadzić z listy).
   */
  private _isEmptyChecklistItem(li: Element): boolean {
    const clone = li.cloneNode(true) as Element;
    clone.querySelectorAll('span.list-marker, ul, ol').forEach(m => m.remove());
    return (clone.textContent ?? '').replace(/[\u200b\u00a0\s]/g, '') === ''
      && !clone.querySelector('img, table, .sdt-inline');
  }

  /**
   * Enter w liście pól wyboru. Pusty punkt → wyjście z listy (jak Word; marker
   * contenteditable=false sprawia, że Chrome nie uznaje li za pusty i dokładałby punkty
   * w nieskończoność). Zwraca true, gdy Enter został obsłużony (wołający robi preventDefault).
   */
  private _handleChecklistEnter(): boolean {
    const editor = this.getActiveEditor();
    const sel = window.getSelection();
    if (!editor || !sel || sel.rangeCount === 0 || !sel.isCollapsed) return false;
    const start = sel.getRangeAt(0).startContainer;
    const li = (start instanceof Element ? start : start.parentElement)?.closest('li') ?? null;
    const container = li?.parentElement;
    if (!li || !(container instanceof HTMLElement) || !editor.contains(li)) return false;
    // Listy pól wyboru — zawsze; zwykłe listy — tylko z kontraktem DOCX (model płaski poziomów:
    // natywny Enter na pustym punkcie poziomu 2 WYCHODZIŁ z listy zamiast cofnąć o poziom).
    const isChecklist = this._checkboxBulletState(container) !== null;
    if (!isChecklist && !container.hasAttribute('data-num-id')) return false;

    // Niepusty punkt: domyślny podział przeglądarki; nowy punkt po ODHACZONYM ma być pusty ☐.
    if (isChecklist) this._checklistEnterOrigin = li;
    if (!this._isEmptyChecklistItem(li)) return false;
    this._checklistEnterOrigin = null;

    // Pusty punkt na GŁĘBSZYM poziomie: Enter cofa go o poziom (jak Word); dopiero pusty punkt
    // poziomu 1 opuszcza listę.
    if (this._listLevelOf(container) > 0 || container.parentElement instanceof HTMLLIElement) {
      this._changeChecklistLevel([li as HTMLLIElement], -1);
      this.onContentChange();
      return true;
    }

    const p = this._unlistChecklistItem(li as HTMLLIElement);
    p.replaceChildren(document.createElement('br'));

    const caret = document.createRange();
    caret.setStart(p, 0);
    caret.collapse(true);
    sel.removeAllRanges();
    sel.addRange(caret);
    this.savedSelection = caret.cloneRange();
    this.onContentChange();
    return true;
  }

  /** li, w którym wciśnięto Enter (lista pól wyboru) — konsumowane w zdarzeniu input. */
  private _checklistEnterOrigin: Element | null = null;

  /** Po podziale punktu Enterem: nowy punkt po odhaczonym startuje jako NIEodhaczony. */
  private _uncheckNewChecklistItemAfterEnter(): void {
    const origin = this._checklistEnterOrigin;
    this._checklistEnterOrigin = null;
    if (!origin) return;
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0) return;
    const start = sel.getRangeAt(0).startContainer;
    const li = ((start instanceof Element ? start : start.parentElement)?.closest('li') ?? null) as HTMLLIElement | null;
    const container = li?.parentElement;
    if (!li || li === origin || !(container instanceof HTMLElement)) return;
    if (!this._checkboxBulletState(container)?.checked) return;

    this._setCheckboxBulletState(li, false);

    // Przenosiny li unieważniają selekcję — karetka na początek treści nowego punktu.
    const marker = li.querySelector(':scope > span.list-marker');
    const caret = document.createRange();
    const firstContent = marker?.nextSibling ?? null;
    if (firstContent?.nodeType === Node.TEXT_NODE) caret.setStart(firstContent, 0);
    else if (firstContent) caret.setStartBefore(firstContent);
    else { caret.selectNodeContents(li); caret.collapse(false); }
    caret.collapse(true);
    sel.removeAllRanges();
    sel.addRange(caret);
    this.savedSelection = caret.cloneRange();
  }

  /**
   * Rozpoznaje stan checkboxa z kontraktu kontenera (data-lvl-text + data-bullet-font).
   * Zwraca null, gdy punktor nie jest checkboxem. Docelowe lvlText trzymają kodowanie
   * źródła: lista z PUA (Wingdings) dostaje PUA, lista ze zwykłym glifem — glif.
   */
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
      // Znak zaznaczenia z wyboru użytkownika (domyślnie krzyżyk), w kodowaniu źródła listy.
      checkedLvlText: isPua
        ? String.fromCharCode(0xf000 | this._checkboxMarks.definition().wingdingsCode)
        : this._checkboxMarks.definition().glyph,
      uncheckedLvlText: isUnchecked
        ? lvlText
        : storedUnchecked ?? (isPua ? String.fromCharCode(0xf0a8) : String.fromCharCode(0x2610)),
      bulletFont: isPua ? (font ?? 'Wingdings') : font,
    };
  }

  /**
   * Wydziela li do własnego kontenera listy (klon atrybutów + świeży data-num-id).
   * Elementy ZA li idą do osobnego fragmentu z ORYGINALNĄ tożsamością (data-num-id
   * bez zmian = writer skleja je z głową listy w jedną instancję numeracji).
   */
  private _isolateListItem(container: HTMLElement, li: HTMLElement): HTMLElement {
    const kids = Array.from(container.children);
    const otherItems = kids.filter(k => k !== li && k.tagName === 'LI');
    if (otherItems.length === 0) {
      // Jednoelementowy fragment — wystarczy nowa tożsamość instancji.
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

  /** Unikalny data-num-id nowego fragmentu (nie koliduje z żadnym w dokumencie). */
  private _uniqueListInstanceId(): string {
    let id: string;
    do {
      id = `chk-${++this._checkboxListSeq}`;
    } while (document.querySelector(`[data-num-id="${id}"]`));
    return id;
  }

  /**
   * Przelicza etykiety list DOCX silnikiem z `list-label.util` na wszystkich stronach
   * W KOLEJNOŚCI DOKUMENTU (kontynuacja działa przez granice stron i fragmentów listy).
   * Render robi CSS ::before z data-list-label — poza edytowalnym tekstem; serializacja
   * zapisu zdejmuje atrybuty (stripListLabelAttributes w _serializeSingleEditor).
   */
  refreshListLabels(): void {
    const roots = (this.pageEditorRefs?.toArray() ?? []).map(r => r.nativeElement);
    if (roots.length === 0 && this.editorContent?.nativeElement) {
      roots.push(this.editorContent.nativeElement);
    }
    if (roots.length === 0) return;
    applyListLabels(roots);
    // li utworzony Enterem nie dziedziczy marker spana (własny symbol / "TODO:" / obraz
    // punktatora) — uzupełnij klonem z rodzeństwa, żeby nowy element miał znacznik.
    ensureBulletMarkers(roots);
  }

  /** Debounce ciężkich operacji (undo snapshot + emit contentChange) — 500 ms. */
  private _schedulePersist(): void {
    if (this._persistTimer) clearTimeout(this._persistTimer);
    this._persistTimer = setTimeout(() => {
      this._persistTimer = null;
      this._persistNow();
    }, 500);
  }

  /**
   * Wisący (niewykonany) snapshot wykonaj TERAZ. Bug 13184834: klik „Cofnij" w oknie
   * debounce'a trafiał na stos BEZ ostatniej porcji pisania — undo() nie miało czego
   * cofnąć (pierwsze kliknięcie „nic nie robiło", a wiszący timer chwilę później dopisywał
   * stan i dopiero drugi klik cofał). Flush przed undo/redo zamyka to okno.
   */
  private _flushPendingPersist(): void {
    if (!this._persistTimer) return;
    clearTimeout(this._persistTimer);
    this._persistTimer = null;
    this._persistNow();
  }

  private _persistNow(): void {
    // Renumeruj przypisy i przytnij osierocone treści PRZED serializacją — edycja treści
    // (usunięcie/przeniesienie odwołania) musi uaktualnić numerację i model przypisów.
    this.syncFootnotesWithBody();
    this.syncEndnotesWithBody();
    // Przelicz etykiety list (dodanie/usunięcie li zmienia numery dalszych elementów,
    // także w innych fragmentach tej samej listy — natywny <ol start> tego nie umie).
    this.refreshListLabels();
    const html = this.getContent();
    this._isInternalUpdate = true;
    this._content.set(html);
    this.contentChange.emit(html);
    this._isInternalUpdate = false;
    // undo snapshot — tylko jeśli różni się od ostatniego
    if (this.undoStack.length === 0 || this.undoStack[this.undoStack.length - 1] !== html) {
      this.undoStack.push(html);
      if (this.undoStack.length > 100) this.undoStack.shift();
      this.redoStack = [];
    }
  }

  /**
   * Dzieli HTML wejściowy na strony po znacznikach page-break, ale ZACHOWUJE marker na końcu
   * każdej strony (poza ostatnią). Bez tego `_repaginateNow` (re-paginacja wg wysokości) nie
   * widziała już bloku page-break i scalała treść z powrotem — manualny podział ginął wizualnie
   * (np. „PROTOKÓŁ…" lądował pod podpisami). Marker przeżywa też zapis (getContent → writer → w:br).
   */
  /**
   * Odczytuje domyślny rozmiar/krój czcionki z wrappera `.document-content` (jeśli reader go dodał)
   * i zapamiętuje, by zastosować na contenteditable strony — wrapper jest rozwijany przy paginacji,
   * więc inaczej default ginie. Brak wrappera/stylu → bez zmian (null = CSS edytora).
   */
  private _captureDocumentDefaults(html: string): string | null {
    if (!html) return null;
    const tmp = document.createElement('div');
    tmp.innerHTML = html;
    const container = tmp.querySelector('.document-content') as HTMLElement | null;
    if (!container) {
      this._documentContainerAttrs = null;
      // Nowy dokument bez wrappera w pełni nadpisuje stan — kolumny poprzedniego pliku
      // nie mogą wyciekać na jednokolumnowy dokument (analogicznie do resetu nagłówka/stopki).
      this._baseColumns.set(null);
      // Defaulty poprzedniego dokumentu też nie mogą wyciekać (null = CSS edytora) —
      // bez resetu treść bez wrappera renderowała się z fontem/odstępami POPRZEDNIEGO pliku.
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
    // Marker mnożnika Worda tylko dla reguły auto — exact/atLeast idzie w pt przez
    // line-height kontenera i nie jest mnożnikiem.
    const lineTw = parseInt(container.getAttribute('data-default-line') ?? '', 10);
    const lineRule = container.getAttribute('data-default-line-rule');
    this.documentDefaultLineTw.set(
      Number.isFinite(lineTw) && lineTw > 0 && (!lineRule || lineRule === 'auto')
        ? String(lineTw)
        : null
    );
    // Kolumny sekcji bazowej (0) z kontenera → render CSS; round-trip atrybutów zapewnia
    // _documentContainerAttrs (poniżej) + _wrapWithDocumentContainer (ADR-0039).
    this._baseColumns.set(parseColumnDataAttributes(container) ?? null);

    // Zapamiętaj atrybuty wrappera i ROZWIŃ go od razu: strony nigdy nie niosą kontenera
    // (paginacja i tak by go rozwinęła), a getContent() owija scaloną treść z powrotem —
    // symetryczny kontrakt z readerem/writerem. Rozwijamy tylko, gdy wrapper faktycznie
    // opakowuje całą treść (jedyne dziecko-element).
    this._documentContainerAttrs = Array.from(container.attributes).map(a => ({
      name: a.name,
      value: a.value,
    }));
    if (container.parentElement === tmp && tmp.children.length === 1) {
      return container.innerHTML;
    }
    return null;
  }

  /** Owija HTML zapisu przechwyconym wrapperem .document-content (jeśli był w źródle). */
  private _wrapWithDocumentContainer(html: string): string {
    if (!this._documentContainerAttrs || !html) return html;
    const div = document.createElement('div');
    for (const { name, value } of this._documentContainerAttrs) {
      try {
        div.setAttribute(name, value);
      } catch {
        /* nieprawidłowa nazwa atrybutu — pomiń */
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

  /**
   * Zmierzone wysokości pasm nagłówka/stopki z DOM (px layoutu; transform zoomu nie wpływa
   * na offsetHeight). Strona 1 może mieć inny wariant (titlePg) niż pozostałe, stąd osobno
   * first/rest. Brak DOM (start, jsdom w testach) → 0, czyli zostaje samo min-height pasma
   * z geometrii — bez regresji względem statycznego wzoru.
   */
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

  /** Marker przerwy sekcji DOCX (niewidoczny div z geometrią następnej sekcji w data-*). */
  private _isSectionBreakMarker(el: Element | null): boolean {
    return !!el && el.nodeType === 1 && el.classList?.contains('docx-section-break');
  }

  /**
   * Geometria sekcji z data-* markera; brakujące atrybuty dziedziczą z geometrii bieżącej
   * (reader emituje tylko to, co sekcja jawnie deklaruje).
   */
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
      // Kolumny NIE dziedziczą z poprzedniej sekcji — brak data-col-* na markerze = jednokolumnowa.
      columns: parseColumnDataAttributes(el),
    };
  }

  /**
   * Geometria per strona dla podanych stron HTML. Marker OTWIERAJĄCY stronę (pierwszy element —
   * tak układa go split: reader emituje page-break przed markerem nextPage) zmienia geometrię
   * od TEJ strony; marker w środku strony (przerwa continuous) — od następnej. Treść przed
   * markerem należy do kończonej sekcji, więc strona zachowuje jej geometrię.
   */
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

  /**
   * Schedule paginacji z krótkim debouncingiem. 250 ms to kompromis: wystarczająco długo,
   * by nie repaginować na każdym wciśnięciu klawisza (IME/wydajność), ale na tyle krótko, że
   * strona nie zdąży widocznie urosnąć poza format A4 zanim treść spłynie na kolejną stronę
   * (Issue: „Enter wydłuża stronę do niestandardowych rozmiarów"). Wcześniej 600 ms — przy tym
   * oknie strona z `min-height:1122px; overflow:visible` rozciągała się zauważalnie przed reflow.
   *
   * Max-wait: czysty debounce resetuje się przy KAŻDYM evencie `input`, więc przytrzymany
   * ENTER (auto-repeat ~30 ms) odsuwał repaginację w nieskończoność — strona rosła, dopóki
   * użytkownik nie puścił klawisza. Przy ciągłym wpisywaniu repaginacja odpala najpóźniej
   * po PAGINATE_MAX_WAIT_MS od pierwszego zaplanowania.
   */
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

  /**
   * Planuje NATYCHMIASTOWĄ repaginację na najbliższą klatkę (po mutacji DOM), koalescując
   * wiele wywołań w jedną — przytrzymany ENTER nie kolejkuje repaginacji na każdy keydown.
   * Używane przez obsługę ENTER, żeby strona nie została wizualnie rozciągnięta do końca
   * okna debounce'a.
   */
  private _flushPaginateSoon(): void {
    if (this._paginateRafHandle !== null) return;
    this._paginateRafHandle = requestAnimationFrame(() => {
      this._paginateRafHandle = null;
      this._flushPaginateNow();
    });
  }

  /** Wymusza repaginację od razu, kasując oczekujący debounce (_schedulePaginate). */
  private _flushPaginateNow(): void {
    if (this._paginateTimer) {
      clearTimeout(this._paginateTimer);
      this._paginateTimer = null;
    }
    this._paginateFirstScheduledAt = null;
    this._repaginateNow();
  }

  /**
   * Jednorazowa KOREKCYJNA repaginacja po ustabilizowaniu zasobów wpływających na wysokość
   * bloków. Pierwsza paginacja (init/setContent) mierzy bloki, zanim doładują się web-fonty
   * i obrazy, więc korzysta z metryk fallbacku (inne wznoszenie/opadanie/interlinia niż
   * docelowa czcionka) i potrafi policzyć INNĄ liczbę stron niż finalny układ — a to właśnie
   * finalny układ ma odpowiadać Wordowi. Czekamy na `document.fonts.ready` oraz doładowanie
   * obrazów, a potem robimy JEDEN przebieg (`_flushPaginateNow`). `_repaginateNow` i tak
   * rebinduje DOM tylko, gdy rozkład bloków REALNIE się zmienił, więc brak zmian metryk =
   * brak przeliczeń (bez wyścigów i migotania). Token generacji anuluje oczekiwanie, gdy w
   * międzyczasie wjechał nowy dokument.
   */
  private _repaginateAfterResources(): void {
    const gen = ++this._resourceRepaginateGen;
    // jsdom (Vitest) nie ma FontFaceSet — traktuj brak jako „gotowe".
    const fontSet = (document as unknown as { fonts?: { ready?: Promise<unknown> } }).fonts;
    const fontsReady: Promise<unknown> =
      fontSet && typeof fontSet.ready?.then === 'function' ? fontSet.ready : Promise.resolve();
    Promise.all([fontsReady, this._pendingImagesSettled()])
      // Makrotask, nie mikrotask (ADR-0108 r.7): gdy `fonts.ready` jest już spełnione, `then`
      // odpalał się PRZED change detection Angulara — strony w DOM miały jeszcze rozmiar i
      // marginesy POPRZEDNIEGO dokumentu (A4 2,5 cm), a `_repaginateNow` bierze szerokość
      // pomiaru z DOM strony 1: measurer 605 px zamiast 687 px → węższe łamanie → +1 strona
      // (PB02 fixture'a table-text-layout), korygowana dopiero przy pierwszej edycji.
      .then(() => new Promise<void>(resolve => setTimeout(resolve, 0)))
      .then(() => {
        if (this._isDestroyed || gen !== this._resourceRepaginateGen) return;
        // Doładowane fonty/obrazy zmieniają metryki bez zmiany HTML — pomiary sprzed
        // załadowania są nieaktualne.
        this._tableMeasureCache.clear();
        this._blockRunMeasureCache.clear();
        this._flushPaginateNow();
      })
      .catch(() => {
        /* Oczekiwanie na zasoby nie może wywrócić edytora — najwyżej zostaje pomiar wstępny. */
      });
  }

  /**
   * Rozwiązuje się, gdy wszystkie jeszcze-ładujące się obrazy w edytorach stron załadują się
   * (lub zwrócą błąd). Safety-timeout gwarantuje, że zawieszony/uszkodzony obraz nigdy nie
   * zablokuje przebiegu korekcyjnego.
   */
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

  /**
   * Główna paginacja: bierze zawartość każdej strony, łączy, dzieli na kartki A4
   * z zachowaniem reguły "block-atomic" (paragraf w całości na 1 stronie),
   * z wyjątkiem tabel — te dzielimy między wierszami.
   */
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
      // Serializacja ŻYWEGO DOM per strona (tym samym mechanizmem co newPageContents niżej)
      // — potrzebna do decyzji, czy rebind [innerHTML] jest w ogóle konieczny. Celowo BEZ
      // fallbacku '<p></p>': pusta strona w DOM musi różnić się od syntetycznego akapitu,
      // żeby rebind przywrócił edytowalny <p>.
      const livePageContents: string[] = [];
      const liveTmp = document.createElement('div');
      for (const ref of refs) {
        // allBlocks: bloki logiczne (flatten rozwija wrapper importu ORAZ pasmo kolumnowe
        // docx-col-band). livePageContents: serializacja DOSŁOWNA dzieci strony (z pasmem) —
        // porównuje się z newPageContents, które pasmo odtwarza, więc niezmieniony układ
        // nie może wymuszać rebindu [innerHTML] (utrata kursora przy pisaniu).
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
      // Fragmenty akapitów z poprzedniej paginacji (data-split-para="cont", ADR-0046)
      // scalamy z powrotem PRZED układaniem — paginacja zawsze startuje od akapitów
      // logicznych i wyznacza punkt podziału od nowa (edycje przesuwają linie);
      // fragmentacja nigdy nie utrwala się w treści.
      const mergedInput: HTMLElement[] = [];
      for (const b of allBlocks) {
        // Prezentacyjna klasa szczytu strony z poprzedniej paginacji (ADR-0108) — zdejmij przed
        // pomiarem: układ liczy się od nowa, a measurer musi widzieć realny margin-top bloku.
        this._stripFmtPageTop(b);
        const prev = mergedInput[mergedInput.length - 1];
        if (b.getAttribute('data-split-para') === 'cont' && prev && prev.tagName === b.tagName) {
          while (b.firstChild) prev.appendChild(b.firstChild);
          continue;
        }
        if (b.getAttribute('data-split-para') === 'cont') {
          // Osierocony fragment (poprzednik usunięty w edycji) — zostaje zwykłym akapitem.
          b.removeAttribute('data-split-para');
          b.style.marginTop = '';
        }
        mergedInput.push(b);
      }
      // Fragmenty TABEL (data-split-table-id) i CIĘTYCH WIERSZY (data-split-row-id) także
      // scalamy przed układaniem — paginacja startuje od logicznych tabel/wierszy i wyznacza
      // punkty cięcia od nowa (edycja w komórce przepływa między fragmentami; fragmentacja
      // nie utrwala się). Id zostają (keepIds) — świeże cięcie ich reuse'uje, więc HTML stron
      // jest stabilny między przebiegami (bez rebindu [innerHTML] = bez utraty kursora).
      const premerged: HTMLElement[] = [];
      for (const b of mergedInput) {
        const prev = premerged[premerged.length - 1];
        if (
          b.tagName === 'TABLE' && prev && prev.tagName === 'TABLE' &&
          b.getAttribute('data-split-table-id') &&
          b.getAttribute('data-split-table-id') === prev.getAttribute('data-split-table-id')
        ) {
          const targetBody = prev.querySelector('tbody') ?? prev;
          // Tylko wiersze WŁASNE fragmentu (RC4): querySelectorAll('tr') łapał też wiersze
          // tabel zagnieżdżonych i wyrywał je do tbody tabeli zewnętrznej.
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

      // Wysokość dostępna dla treści zależy od geometrii BIEŻĄCEJ sekcji (per strona)
      // ORAZ od realnie wyrenderowanego pasma nagłówka/stopki: pasmo z treścią wyższą niż
      // (margines − dystans) SPYCHA body jak w Wordzie, więc body dostaje mniej miejsca
      // i treść spływa na kolejną stronę zamiast rozciągać format strony.
      const measuredBands = this._measureBandHeightsPx(refs);
      // Ten sam pomiar zasila geometrię kotwic pasm (_bandGeoFor) — realna stopka wyższa niż
      // min-height przesuwa górę kontenera. Zmiana wysokości stopki wymusza przeliczenie
      // pozycji kotwic w następnym CD (cache getterów pasm kluczuje po HTML PO transformacji,
      // więc unieważnia się sam); markForCheck domyka cykl. Zero nowych odczytów DOM.
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

      // Kolumny sekcji (ADR-0039): render (CSS multicol) układa bloki w kolumnach o szerokości
      // (szerokość treści − gap×(n−1))/n i wypełnia je sekwencyjnie (column-fill:auto), więc
      // strona mieści n kolumn długości treści. Pomiar bloków i pojemność strony MUSZĄ używać
      // tych samych wielkości — pomiar na pełnej szerokości zaniża wysokości ~n-krotnie i
      // paginator przepełnia/niedopełnia strony względem tego, co przeglądarka faktycznie ułoży.
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
      // Szerokość pomiaru: realna szerokość treści strony 1 z DOM (uwzględnia zoom itp.);
      // dla kolejnych sekcji skalowana proporcjonalnie do ich szerokości treści w cm.
      const innerW = probeEd.clientWidth - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight);
      const baseContentPx = contentWidthPx(baseGeo);
      // Strona 1 w DOM może jeszcze nieść geometrię POPRZEDNIEGO dokumentu (przebieg przed
      // change detection po setContent — ADR-0108 r.7): wtedy skala z DOM fałszuje szerokość
      // pomiaru (605 px zamiast 687 px → +1 strona). Rozjazd szerokości strony z geometrią
      // bazową = DOM nieaktualny → mierz wg geometrii i dołóż jeden przebieg po renderze.
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
      // Linia nie dzieli się na granicy kolumny — każda kolumna poza ostatnią może zostawić
      // przy dole do jednej niepełnej linii. Pojemność strony wielokolumnowej dostaje więc
      // zapas (n−1) linii; bez niego ostatnia linia strony bywa przycinana przez overflow:hidden.
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

      // Pomiar wsadowy: jeden append + jeden layout-flush na ciągły przebieg zwykłych bloków
      // (zamiast reflow per blok) — istotne przy dużych dokumentach.
      const measureRun = (blocks: HTMLElement[]): number[] =>
        this._measureBlockRunHeights(measurer, blocks);

      const pages: HTMLElement[][] = [[]];
      const pageGeos: PageGeometry[] = [curGeo];
      let curSection = 0;
      const pageSections: number[] = [0];
      // Pasmo kolumnowe strony przejściowej (continuous 1→N kolumn w środku strony);
      // null = strona bez pasma (cała w jednej sekcji — render per strona jak dotąd).
      const pageBands: (PageColumnBand | null)[] = [null];
      // currentHeight = skonsumowana DŁUGOŚĆ treści strony (suma wysokości bloków przy
      // szerokości kolumny); availableHeight = pojemność całej strony (n kolumn),
      // columnHeight = wysokość jednej kolumny (obszar body). Dla 1 kolumny obie równe.
      // columnBase = offset początku bieżącego pasma kolumnowego w skonsumowanej długości
      // (0 poza stroną przejściową) — skoki kolumn liczą się względem pasma, nie strony.
      let currentHeight = 0;
      let columnBase = 0;
      let columnHeight = availableFor(curGeo, 0);
      let availableHeight = capacityFor(curGeo, 0);

      // ── Przypisy dolne: pomiar wysokości wpisów + separatora (rezerwacja miejsca w body,
      // jak w MS Word). Wpisy mierzymy klasą `.footnote-item.footnote-entry` (style 10pt/padding
      // NA KLASIE wpisu — measurer mierzy bez kontenera regionu, style muszą być identyczne).
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

      // Stan przypisów BIEŻĄCEJ strony (resetowany w openPage) + rozkład per strona dla regionów.
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

      // Rezerwa, jaką wprowadziłoby dołożenie tych przypisów do BIEŻĄCEJ strony:
      // separator (gdy strona nie miała jeszcze przypisu) + suma wysokości NOWYCH wpisów.
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

      // Pojemność body pomniejszona o rezerwę przypisów (extra = rezerwa dokładanego bloku).
      // Multi-kolumny (przybliżenie v1): region pełnej szerokości pod kolumnami → każda kolumna
      // traci `reserve`, więc pojemność n kolumn maleje o n·reserve.
      const fnEffAvail = (extra: number): number =>
        availableHeight - columnCountFor(curGeo) * (fnReservePx + extra);

      // Zatwierdza przypisy fragmentu na BIEŻĄCĄ stronę: aktualizuje rezerwę i listę regionu.
      const commitFootnotes = (frag: HTMLElement): void => {
        for (const id of this._footnoteIdsIn(frag)) {
          if (fnIdsOnPage.has(id) || !fnItemHById.has(id)) continue;
          if (fnIdsOnPage.size === 0) fnReservePx += fnSepH;
          fnReservePx += fnItemHById.get(id)!;
          fnIdsOnPage.add(id);
          pageFnIds[pages.length - 1].push(id);
        }
      };

      // ADR-0108: Word IGNORUJE odstęp „przed" pierwszego akapitu na stronie — zarówno po
      // twardym łamaniu strony, jak i przy naturalnym przelaniu (pomiar COM: nagłówek
      // z before=24pt siedzi dokładnie na górnym marginesie). Odstęp ZOSTAJE na pierwszej
      // stronie dokumentu i na pierwszej stronie nowej sekcji (marker docx-section-break leży
      // już na tej stronie — reader emituje [page-break][docx-section-break][akapit]).
      // Akapit z pageBreakBefore bez zmian (brak pomiaru — zachowanie jak dotąd).
      const suppressTopMarginHere = (blk: HTMLElement): boolean => {
        if (pages.length === 1) return false;
        const page = pages[pages.length - 1];
        if (page.some(b => this._isSectionBreakMarker(b))) return false;
        if (page.some(b => !this._isSectionBreakMarker(b))) return false; // nie pierwszy blok treści
        return this._isPageTopSpacingBlock(blk) && !this._hasPageBreakBeforeStyle(blk);
      };

      // ADR-0108 r.3: w:keepNext — Word trzyma akapit z PIERWSZĄ LINIĄ następnego. Gdy następny
      // blok w całości spada na nową stronę, poprzedzające go bloki z `break-after:avoid` jadą
      // razem z nim (łańcuch, np. etykieta + próbka); podział następnego (jego 1. linia zostaje)
      // łańcucha nie rusza. Wysokości SKONSUMOWANE (po topCut) pamiętane per blok, żeby cofnąć
      // currentHeight bez ponownego pomiaru.
      const placedHeights = new Map<HTMLElement, number>();
      const pullKeepNextChain = (): HTMLElement[] => {
        const page = pages[pages.length - 1];
        const pulled: HTMLElement[] = [];
        // ≥1 blok zostaje — Word też przerywa łańcuch, gdy nie mieści się na pustej stronie.
        // Blok z przypisem nie wraca (rezerwa przypisów jest już policzona na tej stronie).
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
        // Akapit niemieszczący się w reszcie pojemności strony dzielimy na granicy LINII
        // (jak Word) — dopasowany fragment zostaje na stronie, kontynuacja
        // (data-split-para="cont") płynie dalej i może dzielić się wielokrotnie (ADR-0046).
        // Bloki niedzielne (nie-akapity, akapity z abspos, resztka < 1 linii) — jak dotąd:
        // w całości na kolejną stronę; blok większy niż PUSTA strona zostaje przycięty.
        let blk = block;
        let bh = h;
        // Margin-top zdjęty ze szczytu strony (ADR-0108): pomiar `h` liczy go w wysokości
        // bloku, render zeruje go klasą .fmt-page-top — budżet strony musi widzieć to samo.
        let topCut = 0;
        const applyPageTop = () => {
          topCut = 0;
          if (!suppressTopMarginHere(blk)) return;
          topCut = this._blockMarginTopPx(blk, measurer);
          if (topCut > 0) bh = Math.max(0, bh - topCut);
        };
        applyPageTop();
        // Rezerwa przypisów tego bloku (odwołania w treści) skraca pojemność body — treść
        // spływa niżej, żeby zrobić miejsce na przypis na dole strony (jak w MS Word). Liczona
        // z CAŁEGO bloku (górne oszacowanie dla fragmentu na stronie = bezpiecznie, bez nachodzenia).
        let blkExtra = fnProspectiveReserve(this._footnoteIdsIn(blk));
        while (currentHeight + bh > fnEffAvail(blkExtra)) {
          const pageHasContent = pages[pages.length - 1].length > 0;
          // Budżet splittera liczy blok „łącznie z marginesem górnym" — na szczycie strony
          // ten margines nie jest renderowany, więc oddajemy go do budżetu.
          // w:keepLines (break-inside:avoid) = akapit niedzielny — w całości na kolejną stronę.
          const parts = this._keepsLinesTogether(blk) ? null : this._splitBlockAtBudget(
            blk, fnEffAvail(blkExtra) - currentHeight + topCut, measurer, lineHeightPx);
          if (!parts) {
            if (!pageHasContent) break;
            // Blok jedzie w całości → poprzedzające bloki „zachowaj z następnym" jadą z nim.
            const pulled = pullKeepNextChain();
            openPage();
            for (const pb of pulled) pushMeasured(pb, measureBlock(pb));
            blkExtra = fnProspectiveReserve(this._footnoteIdsIn(blk));
            applyPageTop();
            continue;
          }
          // parts[0] zostaje na stronie → jego przypisy rezerwują się TU; parts[1] płynie dalej
          // i zabiera swoje przypisy na kolejną stronę (przypisanie per fragment).
          if (topCut > 0) parts[0].classList.add('fmt-page-top');
          commitFootnotes(parts[0]);
          pages[pages.length - 1].push(parts[0]);
          placedHeights.set(parts[0], Math.max(0, fnEffAvail(blkExtra) - currentHeight));
          openPage();
          blk = parts[1];
          bh = measureBlock(blk);
          topCut = 0; // kontynuacja ma już wyzerowany margin-top (ADR-0046)
          blkExtra = fnProspectiveReserve(this._footnoteIdsIn(blk));
        }
        if (topCut > 0) blk.classList.add('fmt-page-top');
        commitFootnotes(blk);
        pages[pages.length - 1].push(blk);
        placedHeights.set(blk, bh);
        currentHeight += bh;
      };

      // Tabela w przebiegu: dotąd mierzona OSOBNO (własny przebieg), więc jej margines górny i
      // dolny sumowały się z marginesami sąsiadów zamiast kolapsować (4 + 13.3 zamiast 13.3 —
      // ok. 8 px na tabelę). Strona z 7 tabelami traciła ok. 56 px i nagłówek+nota PS01 uciekały
      // na kolejną stronę mimo miejsca (Word trzymał je na poprzedniej). Teraz tabela jest
      // mierzona W przebiegu z sąsiadami (runHeightHint), a tu tylko układana (ADR-0108 r.8).
      const placeTable = (tableBlock: HTMLElement, runHeightHint: number | null) => {
          // Tabela mieści się w jednej KOLUMNIE (nie w całej szerokości strony wielokolumnowej),
          // więc fragmenty tnie wysokość kolumny; kolejny fragment idzie do następnej kolumny,
          // a na nową stronę dopiero, gdy kolumny się skończą.
          // Problem 7 / RC1: splitter mierzy SAME wiersze, a kontrola pojemności fragmentu
          // (measureBlock = _measureBlockRunHeights) liczy blok z pionowymi MARGINESAMI tabeli
          // (`.editor-content table { margin: 15px 0 }` / inline z importu). Budżety splittera
          // pomniejszamy o te marginesy — inaczej świeżo dopasowany fragment mierzył się na
          // avail+marginesy, oblewał kontrolę i CAŁY jechał dalej mimo wolnego miejsca.
          // Dolny margines tabeli to render (`margin:4px 0`; Word nie ma marginesów tabeli) —
          // może wisieć poza dołem strony, więc budżety cięcia pomniejszamy TYLKO o górny
          // (PG04: 0.5 pt zabrakło i wysoki wiersz szedł na 2 strony, w Wordzie 1; r.9).
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
            // Wysokość CAŁEJ tabeli z pomiaru przebiegu (kolaps marginesów z sąsiadami jak na
            // stronie, ADR-0108 r.8); fragmenty po cięciu — pomiar osobny jak dotąd.
            let h = i === 0 && split.length === 1 && runHeightHint !== null
              ? runHeightHint
              : measureBlock(frag);
            if (i > 0) {
              advanceColumnOrPage();
            } else {
              // Bug 13902621: PIERWSZY fragment też musi przejść kontrolę pojemności —
              // splitter dostaje budżet min. 80px, więc tabela krótsza niż 80px (albo
              // niedzielna) wracała w całości i była kładziona w resztce strony/kolumny,
              // w której się NIE mieściła — wizualnie wjeżdżała pod stopkę. Przy braku
              // miejsca przechodzimy do następnej kolumny/strony (raz — świeża kolumna
              // to maksimum, które możemy dać; guard pustej strony jak w pushMeasured).
              const used = columnHeight > 0 ? (currentHeight - columnBase) % columnHeight : 0;
              const remaining = columnHeight - used;
              // Dolny margines tabeli (render `margin:4px 0`, Word go nie ma) może wisieć poza
              // dołem strony — nie decyduje o dopasowaniu (PG04: 0.5 pt za dużo = +1 strona).
              if (h > remaining + 0.5 + marginBottomPx && pages[pages.length - 1].length > 0) {
                advanceColumnOrPage();
                // Problem 7 / RC2: fragmenty były skrojone pod STARĄ resztkę strony — po
                // przejściu na świeżą kolumnę/stronę potnij tabelę od nowa pełnym budżetem
                // (dokładnie raz; każdy nowy fragment mieści się w pełnej kolumnie
                // z konstrukcji, więc bez ryzyka pętli). Bez tego chunk 0 skrojony pod małą
                // resztkę siedział sam na świeżej stronie, a chunk 1 otwierał następną —
                // niemal puste strony w środku tabeli.
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
          // Marker sekcji: od tego miejsca obowiązuje geometria następnej sekcji. Marker zostaje
          // w treści (przeżywa zapis → writer odtwarza paragraph-level sectPr). Gdy otwiera stronę
          // (typowo: po page-breaku emitowanym przez reader), geometria dotyczy TEJ strony.
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
            // Marker W ŚRODKU strony (typowo w:type=continuous). Word kontynuuje na tej samej
            // stronie — trzy przypadki:
            const newCols = columnCountFor(curGeo);
            const prevCols = columnCountFor(pageGeos[pageGeos.length - 1]);
            const remainingPx = columnHeight - (currentHeight - columnBase);
            if (
              newCols > 1 && prevCols === 1 && !pageBands[pageBands.length - 1] &&
              remainingPx >= 2 * lineHeightPx
            ) {
              // (1) 1 kolumna → N kolumn: pasmo kolumnowe w POZOSTAŁEJ części strony (jak
              // w Wordzie). Dalsza treść płynie w N kolumnach o wysokości `remainingPx`;
              // render owinie bloki za markerem w div.docx-col-band o tej wysokości.
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
              // (2) Zmiana liczby kolumn nieodwzorowywalna w obrębie strony (N→1, N→M,
              // drugi marker na stronie, resztka < 2 linii): sekcja od ŚWIEŻEJ strony
              // z własną geometrią — przybliżenie, ale render spójny z pomiarem.
              openPage();
              pages[pages.length - 1].push(block);
            } else {
              // (3) Ta sama liczba kolumn (np. continuous ze zmianą marginesów) — jak dotąd:
              // marker zostaje, pełna geometria obowiązuje od następnej strony.
              pages[pages.length - 1].push(block);
            }
          }
          measurer.style.width = `${measurerWidthFor(curGeo)}px`;
          bi++;
          continue;
        }
        if (this._isPageBreakBlock(block)) {
          // Manualny page break: wymuś nową stronę. Marker zostaje na końcu bieżącej strony,
          // żeby przeżył zapis (getContent → writer → w:br type=page).
          pages[pages.length - 1].push(block);
          openPage();
          bi++;
          continue;
        }
        if (this._hasPageBreakBeforeStyle(block)) {
          // Właściwość „podział strony przed" (w:pageBreakBefore — checkbox dialogu akapitu,
          // style nagłówków; reader emituje `page-break-before:always` w stylu inline bloku).
          // W odróżnieniu od markera div.page-break blok NIE jest znacznikiem — sam zaczyna
          // nową stronę i jedzie na nią razem ze swoim stylem (zapis odtwarza w:pageBreakBefore
          // w pPr). Jak w Wordzie: akapit i tak otwierający stronę nie tworzy pustej kartki.
          if (pages[pages.length - 1].length > 0 || currentHeight > 0) {
            openPage();
          }
          pushMeasured(block, measureBlock(block));
          bi++;
          continue;
        }
        if (this._isColumnBreakBlock(block)) {
          // Podział kolumny (w:br type=column, render: break-before:column na markerze):
          // dalsza treść zaczyna się od góry NASTĘPNEJ kolumny, więc paginator konsumuje
          // resztę bieżącej. Skok poza ostatnią kolumnę = nowa strona (marker jedzie z dalszą
          // treścią; forsowany break na początku świeżego kontenera jest przez CSS ignorowany).
          // columnBase: na stronie przejściowej kolumny liczą się od początku PASMA.
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
        // Ciągły przebieg zwykłych bloków — zmierz wsadowo, potem rozłóż na strony.
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

      // ── Przypisy końcowe: rozkład jak w MS Word — region zaczyna się zaraz po
      // ostatnim bloku treści na ostatniej stronie (separator + wpisy); wpisy, które
      // się nie mieszczą, przelewają się na kolejne strony (kontynuacja z separatorem
      // na całą szerokość). Strony tylko-przypisowe mają PUSTE body ('' w pageContents)
      // — getContent() je odfiltrowuje, więc nie wchodzą do zapisu dokumentu.
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

        // Offset początku regionu od góry `.page`: dystans nagłówka + realnie zajęte
        // pasmo nagłówka (jak w availableFor) + wysokość treści body na stronie.
        const regionTopFor = (geo: PageGeometry, pageIdx: number, usedPx: number): number => {
          const headerBand = Math.max(this._bandCmFor(geo, 'header') * CSS_PX_PER_CM,
            pageIdx === 0 ? measuredBands.headerFirst : measuredBands.headerRest);
          return this._distanceCmFor(geo, 'header') * CSS_PX_PER_CM + headerBand + usedPx;
        };

        let enPageIdx = pages.length - 1;
        // Region przypisów renderuje się na PEŁNEJ szerokości pod treścią. Na stronie
        // wielokolumnowej kolumna 1 wypełnia się pierwsza (column-fill:auto), więc region
        // zaczyna się pod najgłębszą kolumną: min(zużyta długość, wysokość kolumny);
        // wolne miejsce dla wpisów to reszta wysokości KOLUMNY (nie pojemności n kolumn).
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
          // Wpis atomowy: nie mieści się → nowa strona. Na ŚWIEŻEJ stronie przypisów
          // (kontynuacja bez wpisów) kładziemy mimo przepełnienia — inaczej pętla.
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

      // Przypisy z modelu, których odwołania nie zostały przypisane do żadnej strony (np. goły
      // <sup> poza akapitem, albo chwilowa niespójność modelu z DOM) NIE mogą zniknąć — dokładamy
      // je na ostatnią stronę treści (jak fallback regionu, zanim sync przytnie osierocone).
      if (footnotesForLayout.length > 0) {
        const assigned = new Set<string>(pageFnIds.flat());
        const lastContentPage = pages.length - 1;
        for (const fn of footnotesForLayout) {
          if (!assigned.has(fn.id)) pageFnIds[lastContentPage].push(fn.id);
        }
      }

      // ── Przypisy dolne: region na dole KAŻDEJ strony, na której wylądowały odwołania.
      // Kotwica dolna = pasmo stopki + dystans stopki (region rośnie w GÓRĘ, tuż nad stopką);
      // pojemność body została już zmniejszona o rezerwę w pętli, więc treść nie nachodzi.
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
          // Strona przejściowa: bloki za markerem sekcji idą w zagnieżdżony kontener multicol
          // o JAWNEJ wysokości (column-fill:auto wymaga jednoznacznej wysokości — jak
          // pageBodyHeights dla stron w całości wielokolumnowych, ADR-0039).
          const wrap = document.createElement('div');
          wrap.className = 'docx-col-band';
          const round2 = (v: number) => Math.round(v * 100) / 100;
          const gapPx = round2(Math.max(0, band.columns.spaceCm) * CSS_PX_PER_CM);
          // Styl jako string (nie CSSOM): serializacja deterministyczna — porównanie
          // live vs new nie może dawać fałszywych różnic (rebind = utrata kursora).
          wrap.setAttribute('style',
            `column-count:${band.columns.count};column-gap:${gapPx}px;` +
            (band.columns.separator ? 'column-rule:1px solid #bbb;' : '') +
            `height:${round2(band.heightPx)}px;`);
          blocks.slice(bandStart).forEach(b => wrap.appendChild(b));
          tmp.appendChild(wrap);
        }
        return tmp.innerHTML || '<p></p>';
      });
      // Strony wyłącznie na przelane przypisy końcowe: puste body (patrz komentarz wyżej).
      for (let i = 0; i < endnoteExtraPages; i++) newPageContents.push('');

      this._endnoteLayout.set(endnoteRegions);
      this._footnoteLayout.set(footnoteRegions);
      this.pageGeometries.set(pageGeos);
      this.pageBodyHeights.set(pageGeos.map((g, i) => availableFor(g, i)));
      this.pageSectionIndexes.set(pageSections);
      // Rebind [innerHTML] tylko gdy rozkład bloków na strony REALNIE się zmienił — porównanie
      // z ŻYWYM DOM, nie z sygnałem pageContents. Sygnał jest celowo przestarzały między
      // repaginacjami (onPageInput go nie aktualizuje), więc przy pisaniu różnił się ZAWSZE
      // i każda repaginacja (max-wait 600 ms) wymieniała DOM stron: selekcja ginęła, kursor
      // na ułamek sekundy spadał na początek dokumentu, a znaki wpisane przed odtworzeniem
      // karetki lądowały w złym miejscu / ginęły. Zgodność liczby stron z sygnałem jest
      // wymagana, bo to sygnał steruje @for stron (żywy DOM opisuje tylko wyrenderowane).
      const domAlreadyCorrect = newPageContents.length === this.pageContents().length
        && newPageContents.length === livePageContents.length
        && newPageContents.every((v, i) => v === livePageContents[i]);
      if (!domAlreadyCorrect) {
        this.pageContents.set(newPageContents);
        // KRYTYCZNE: rebind [innerHTML], sync DOM i przywrócenie karetki MUSZĄ zajść w jednym
        // tasku. Wcześniej sync+restore szły przez setTimeout(0): między renderem [innerHTML]
        // (selekcja skasowana — kursor „mrugał" na początku dokumentu) a odtworzeniem karetki
        // istniało okno (~30 ms, rosnące z dokumentem), w którym obsłużony keystroke wstawiał
        // tekst do żywego DOM, po czym _syncPageEditorDom nadpisywał go treścią policzoną BEZ
        // tego znaku — litery ginęły przy pisaniu („Ala" → „Aa"). detectChanges() renderuje
        // nowy rozkład synchronicznie, więc żaden event wejścia nie może się wcisnąć.
        this._cdr.detectChanges();
        // Angular NIE nadpisuje `[innerHTML]`, gdy obliczona treść strony jest wartościowo
        // taka sama jak ostatnio zbindowana (SafeHtml cache, patrz `_safeHtmlCache`). Ale
        // strona, w którą użytkownik właśnie pisze, ma w DOM ŻYWE edycje (dodane akapity),
        // których sygnał nie zna — więc jej DOM rozjeżdża się z paginacją: strona rośnie w pion
        // (przepełnienie nie schodzi na kolejną), a nadmiar jest DODATKOWO duplikowany na dalsze
        // strony (i trafia do zapisu przez getContent). Dlatego po repaginacji WYMUSZAMY zgodność
        // DOM edytorów z obliczonym rozkładem, zanim przywrócimy kursor.
        this._syncPageEditorDom(newPageContents);
        this._restoreGlobalCaret(caret);
      }
      this.calculatePages();
      // Repaginacja przenosi bloki między stronami — znacznik kotwicy musi pojechać
      // za akapitem-kotwicą (albo zniknąć, jeśli element wypadł z DOM).
      this._scheduleAnchorBadgeRefresh();
    } finally {
      this._isRepaginating = false;
      // „Pokaż wszystko": znaki spacji/tabów/br żyją w overlayu pozycjonowanym pomiarami —
      // po każdej repaginacji (nowy DOM stron) trzeba je przeliczyć. rAF = po malowaniu.
      this._scheduleFormattingMarksRender();
    }
  }

  // ═══════════ „Pokaż wszystko" — overlay znaków formatowania (spacje/taby/br) ═══════════
  // CSS-owe pseudo-elementy (¶ za blokiem, strzałki tab-segów, hinty podziałów) zostają;
  // overlay dorysowuje to, czego CSS nie umie bez mutacji treści: kropkę · za KAŻDĄ spacją,
  // ° za twardą spacją, → dla literalnych \t w nośnikach inline i ↵ dla <br>. Warstwa jest
  // dzieckiem .page POZA contenteditable (nie serializuje się do getContent, nie łapie
  // kliknięć), pozycje liczone Range.getBoundingClientRect i sprowadzane do px układu
  // strony przez _pageVisualScale (zoom). Przeliczenie TYLKO gdy tryb włączony, po
  // repaginacji — zero kosztu w zwykłej pracy (pułapka ADR-0075 nie dotyczy).
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
    // Prezentacyjna klasa .fmt-trailing-br z poprzedniego przebiegu — zdejmij i nadaj
    // od nowa (treść mogła się zmienić); po wyłączeniu trybu nie może zostać w DOM.
    this._stripFmtTrailingBr(host);
    if (!this.showFormattingMarks()) return;

    const pages = Array.from(host.querySelectorAll<HTMLElement>('.page'));
    let budget = 20000; // twardy limit znaczników — patologiczne dokumenty nie zamrożą UI

    // Word pokazuje znaki WSZĘDZIE: body + nagłówek/stopka (podgląd i edycja) + wpisy przypisów.
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

      // Kolor i rozmiar znaczników jak w Wordzie: z RUNU, przy którym stoją (computed
      // z rodzica text node'a; cache per element — computed w pętli po znakach byłby drogi).
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
        // computed font-size jest w px UKŁADU (transform nie zmienia computed) — bez skali.
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
          // Ukryte nośniki (markery display:none) — Range da zerowe recty i addMark je odrzuci.
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
          // <br> ZAMYKAJĄCY blok: CSS-owe ¶ (::after) wpadłoby ZA złamanie — do nowej
          // linii, rozpychając akapit względem pomiaru paginacji (znaczniki muszą mieć
          // zerowy wpływ na układ). Gasimy pseudo-element klasą i malujemy ¶ tutaj,
          // w TEJ SAMEJ linii zaraz za ↵. Offset w px ekranu = px układu × skala.
          const block = this._trailingBrBlock(br);
          if (block) {
            block.classList.add('fmt-trailing-br');
            addMark(new DOMRect(rect.left + 9 * scale, rect.top, rect.width, rect.height), '¶', 'fmt-pilcrow', br.parentElement);
          }
        });

        // Znaczniki końca komórki (w komórce) i końca wiersza (za tabelą) — ¤ jak w Wordzie.
        container.querySelectorAll<HTMLTableCellElement>('td, th').forEach(cell => {
          if (budget <= 0 || cell.hasAttribute('data-grid-spacer')) return;
          // Collapsed range na końcu komórki daje w Chrome zerowy rect — bierzemy OSTATNI
          // rect zawartości i stawiamy ¤ przy jego prawej krawędzi; pusta komórka = lewy górny róg.
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

        // Kotwica obiektu pływającego (⚓) przy jego lewym-górnym rogu — jak w Wordzie.
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

  /**
   * Blok (p/h1–h6/li), który dany <br> ZAMYKA — tzn. za tym <br> nie ma już w bloku żadnej
   * widocznej treści (biały znak/ZWSP się nie liczy). Dla takiego bloku pseudo-element ¶
   * renderowałby się ZA złamaniem, w nowej linii — overlay przejmuje rysowanie ¶,
   * a klasa .fmt-trailing-br gasi CSS-owy ::after (patrz SCSS).
   */
  private _trailingBrBlock(br: HTMLElement): HTMLElement | null {
    // Every paragraph-like block that gets the CSS ::after pilcrow must be listed here,
    // or a trailing <br> in it would show ↵ with no ¶ at all (blockquote/pre arrive
    // from the clipboard pipeline's splittable-block set).
    const block = br.closest<HTMLElement>('p, h1, h2, h3, h4, h5, h6, li, blockquote, pre');
    if (!block) return null;
    const tail = document.createRange();
    tail.selectNodeContents(block);
    tail.setStartAfter(br);
    if (tail.toString().replace(/[\s\u200B\uFEFF]/g, '').length > 0) return null;
    // Elementy bez tekstu, ale widoczne (kolejne <br>, obraz, tabela, odwołanie przypisu,
    // nośniki tabów, obiekty pływające) także oznaczają „jest treść za złamaniem".
    const frag = tail.cloneContents();
    return frag.querySelector(
      'br, img, svg, table, sup, .docx-tab-seg, .docx-tab-leader, .docx-textbox, .docx-shape, .editor-image-wrapper',
    ) ? null : block;
  }

  /** Zdejmuje prezentacyjną klasę .fmt-trailing-br (nadawaną przez overlay „Pokaż wszystko"). */
  private _stripFmtTrailingBr(root: ParentNode): void {
    root.querySelectorAll('.fmt-trailing-br').forEach(el => {
      el.classList.remove('fmt-trailing-br');
      if (!el.getAttribute('class')) el.removeAttribute('class');
    });
  }

  /**
   * Measurer do paginacji. MUSI renderować bloki w tym samym kontekście stylów co realna
   * strona: klasa `editor-content` (style globalne — ViewEncapsulation.None) nadaje blokom
   * marginesy akapitów/nagłówków/tabel, paddingi komórek oraz wysokość pustego akapitu
   * (`.editor-content p:empty::before { content:'\00a0' }`). Goły <div> zaniżał pomiar
   * (pusty <p> = 0 px, brak marginesów bloków) → paginacja nie widziała przepełnienia
   * i strona rosła w pion zamiast przelać treść na nową kartkę.
   */
  private _createBlockMeasurer(cs: CSSStyleDeclaration, widthPx: number): HTMLElement {
    const measurer = document.createElement('div');
    // Klasa modelu odstępów jak na realnej stronie (ADR-0107) — inaczej measurer liczyłby
    // odstępy innym nośnikiem niż render i paginacja rozjeżdżałaby się z ekranem.
    measurer.className = this.documentParagraphSpacingSum()
      ? 'editor-content para-spacing-sum'
      : 'editor-content';
    // ADR-0108: interlinia measurera = wartość AUTORSKA kontenera, nie wyliczona. Strona ma
    // bezjednostkowe `line-height:1.404` (mnożnik dziedziczony przez każdy blok względem JEGO
    // font-size), a `cs.lineHeight` zwraca px policzone dla font-size kontenera — dziedziczone
    // jako stała wysokość, bloki mniejszym fontem (etykiety 9pt, noty 8.5pt) mierzyły się
    // ~2pt wyżej niż render; przy 30 takich blokach strona „traciła" ~50pt i przelewała
    // treść, która na ekranie się mieściła. Bez domyślnej z dokumentu — jak dotąd.
    const lineHeight = this.documentDefaultLineHeight() || cs.lineHeight;
    // Zmienne CSS strony (ADR-0108): measurer wisi w <body>, poza `.page`/`.editor-content`,
    // więc bez nich akapit bez inline margin-bottom mierzył się z FALLBACKIEM SCSS
    // (`--doc-par-margin` = 10px) zamiast z domyślnym odstępem dokumentu (tu 0) — ~7.5pt
    // nadwyżki na akapit, kolejne dziesiątki pt „zjedzone" z pojemności strony. Te same
    // źródła co bindingi [style.--…] w szablonie; null = binding nic nie pisze = fallback po
    // obu stronach (spójnie).
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

  /**
   * Wysokość KONSUMOWANA przez każdy blok, liczona deltami pozycji kolejnych bloków
   * (+ sentinel o zerowej wysokości na końcu). W odróżnieniu od
   * `getBoundingClientRect().height` uwzględnia pionowe marginesy bloków wraz z ich
   * realnym kolapsem między sąsiadami — dokładnie tak, jak bloki ułożą się na stronie
   * (`.editor-content` ma `overflow:hidden`, więc marginesy nie wyciekają z kontenera).
   * Margin-top pierwszego bloku jest doliczany do niego. Jeden append + jeden
   * layout-flush na przebieg (wydajność jak dotychczasowy pomiar wsadowy).
   */
  private _measureBlockRunHeights(measurer: HTMLElement, blocks: HTMLElement[]): number[] {
    // Cache CAŁEGO przebiegu (nie per blok — wysokości liczone deltami niosą realny kolaps
    // marginesów między sąsiadami, więc wynik zależy od całej sekwencji). Przy pisaniu
    // zmienia się jeden przebieg (ten z edytowanym akapitem) — pozostałe trafiają w cache
    // zamiast klonować się i wymuszać layout-flush przy każdej repaginacji (≤600 ms).
    // Klucz = inline style measurera (szerokość kolumny/fonty) + HTML sekwencji;
    // inwalidacja jak _tableMeasureCache (setContent + doładowanie zasobów).
    let key = measurer.style.cssText + '|';
    for (const b of blocks) key += b.outerHTML;
    const cached = this._blockRunMeasureCache.get(key);
    if (cached !== undefined) return cached;
    measurer.innerHTML = '';
    for (const b of blocks) measurer.appendChild(b.cloneNode(true));
    const sentinel = document.createElement('div');
    // clear:both — tabela pływająca (float, ADR-0108 r.6) wyższa niż bloki obok niej musi
    // doliczyć się do ostatniego bloku przebiegu; bez clear sentinel stawał obok floata
    // i strona przelewała się pod nim.
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

  /**
   * Margin-top bloku w px w kontekście measurera (te same fonty/szerokość co strona).
   * Cache współdzielony z pomiarem przebiegów (klucz z prefiksem) — ADR-0075: repaginacja
   * leci po każdym keystroke, więc bez cache każdy szczyt strony kosztowałby reflow.
   */
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

  /** Bloki, którym Word zdejmuje odstęp „przed" na szczycie strony (ADR-0108): akapity
   *  i nagłówki. Listy/tabele/formanty świadomie poza zakresem (inny nośnik odstępu). */
  private _isPageTopSpacingBlock(el: HTMLElement): boolean {
    return /^(P|H[1-6]|BLOCKQUOTE|PRE)$/.test(el.tagName);
  }

  /** Zdejmuje prezentacyjną klasę .fmt-page-top (szczyt strony, ADR-0108) — z bloku
   *  lub ze wszystkich potomków korzenia. Nigdy nie może wejść do zapisu. */
  private _stripFmtPageTop(root: Element | ParentNode): void {
    const strip = (el: Element) => {
      el.classList.remove('fmt-page-top');
      if (!el.getAttribute('class')) el.removeAttribute('class');
    };
    if ((root as Element).classList?.contains('fmt-page-top')) strip(root as Element);
    root.querySelectorAll('.fmt-page-top').forEach(strip);
  }

  /**
   * Dzieli akapit na granicy LINII tak, by pierwszy fragment zmieścił się w `budgetPx`
   * (skonsumowana wysokość łącznie z marginesem górnym) — jak Word, który kontynuuje
   * akapit na następnej stronie zamiast przenosić go w całości (ADR-0046).
   * Zwraca `[dopasowany, kontynuacja]` albo null, gdy blok jest niedzielny: nie-akapit,
   * elementy pozycjonowane w środku (pływające obrazy/textboxy, pozycyjne tab-segi —
   * podział zmieniłby ich stronę-kontener), nie mieści się ani jedna linia, albo punkt
   * podziału wypada na początku/końcu treści.
   * Kontynuacja dostaje `data-split-para="cont"` + wyzerowany margin-top (odstęp akapitu
   * tylko na realnych końcach, jak w Wordzie). Fragmentacja jest CZYSTO prezentacyjna:
   * scala ją z powrotem `_mergeSplitParagraphs` (zapis) i pre-merge w `_repaginateNow`.
   */
  private _splitBlockAtBudget(
    block: HTMLElement,
    budgetPx: number,
    measurer: HTMLElement,
    lineHeightPx: number
  ): [HTMLElement, HTMLElement] | null {
    if (budgetPx < lineHeightPx) return null;
    // Listy dzielą się MIĘDZY punktami (jak Word; tabele mają własną ścieżkę po wierszach) —
    // bez tego lista dłuższa niż reszta strony jechała W CAŁOŚCI dalej, zostawiając pustkę.
    // Gałąź PRZED guardem tab-segów: tab-seg/textbox w punkcie blokuje cięcie LINII, ale
    // podział między nietkniętymi punktami jest bezpieczny (bankowe numeracje „1) ⇥ tekst").
    if (block.tagName === 'UL' || block.tagName === 'OL') {
      return this._splitListBetweenItems(block, budgetPx, measurer);
    }
    // Formant blokowy (content control) to kontener wielu akapitów — jako całość robił
    // z sekcji dokumentu jeden niepodzielny mega-blok (dziura na pół strony, gdy nie
    // mieścił się w resztce). Dzielimy między jego dziećmi jak Word.
    if (block.tagName === 'DIV' && block.classList.contains('sdt-block')) {
      return this._splitSdtBetweenChildren(block, budgetPx, measurer, lineHeightPx);
    }
    // .docx-tab-leader = linia flex z wypełniaczem tabulatora (wpis spisu treści) —
    // jednoliniowa, cięcie w środku rozerwałoby układ segmentów flex.
    if (block.querySelector('.docx-tab-seg, .docx-tab-leader, .docx-textbox, [data-pos-mode], [data-docx-xml]')) return null;
    if (block.tagName !== 'P' && !/^H[1-6]$/.test(block.tagName)) return null;

    measurer.innerHTML = '';
    measurer.appendChild(block);
    try {
      const limitY = measurer.getBoundingClientRect().top + budgetPx;
      const split = this._findLineSplitPoint(block, limitY);
      if (!split) return null;

      // Obie strony podziału muszą mieć realną treść — inaczej to zwykłe „cały blok dalej".
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
        // Punkt podziału na końcu treści — przywróć wyjęte resztki (białe znaki) i nie dziel.
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

  /**
   * Skumulowane dolne krawędzie elementów listy (px od zewnętrznego początku bloku listy,
   * z marginesem górnym) — pomiar w measurerze. Wydzielone, żeby testy jsdom (bez layoutu)
   * mogły wstrzyknąć deterministyczne wysokości.
   */
  private _measureListItemBottoms(list: HTMLElement, measurer: HTMLElement): number[] {
    measurer.innerHTML = '';
    measurer.appendChild(list);
    const base = measurer.getBoundingClientRect().top;
    const bottoms = (Array.from(list.children) as HTMLElement[])
      .map(li => li.getBoundingClientRect().bottom - base);
    measurer.removeChild(list);
    return bottoms;
  }

  /**
   * Dzieli listę MIĘDZY punktami tak, by dopasowane `li` zmieściły się w `budgetPx`.
   * Kontynuacja = klon kontenera (te same data-* — silnik etykiet liczy numerację przez
   * granice stron/fragmentów) + `data-split-para="cont"`; natywne `<ol>` bez data-num-id
   * dostaje `start`, żeby numeracja przeglądarki nie restartowała. Null = nic nie mieści
   * się / wszystko się mieści / lista jednopunktowa (li pozostaje atomowe).
   */
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

  /**
   * Górne/dolne krawędzie bezpośrednich dzieci kontenera (px od zewnętrznego początku
   * bloku, pomiar w measurerze). Wydzielone, żeby testy jsdom mogły wstrzyknąć
   * deterministyczną geometrię.
   */
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

  /**
   * Dzieli formant blokowy (`div.sdt-block`) MIĘDZY jego dziećmi-blokami; dziecko graniczne
   * próbuje dodatkowo ciąć po liniach rekurencją przez `_splitBlockAtBudget` (akapit w SDT
   * łamie się przez granicę strony dokładnie jak poza formantem — tak robi Word; obejmuje
   * to też SDT zagnieżdżone). Kontynuacja = klon kontenera (te same data-sdt-props —
   * scalanie `_mergeSplitParasWithin` skleja fragmenty przed zapisem, więc writer widzi
   * JEDEN formant) + `data-split-para="cont"`. Null = nic nie da się odciąć.
   */
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

    // Dziecko graniczne: resztka budżetu liczona od jego górnej krawędzi w kontenerze.
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
      // Head fragmentu granicznego zostaje w SDT (boundary wciąż jest jego dzieckiem
      // po podziale in-place), tail otwiera kontynuację.
      cont.appendChild(boundaryParts[1]);
      if (boundary.parentNode !== sdt) sdt.appendChild(boundaryParts[0]);
    } else {
      cont.appendChild(boundary);
    }
    for (let i = lastFitting + 2; i < children.length; i++) cont.appendChild(children[i]);
    return [sdt, cont];
  }

  /**
   * Pierwszy punkt w treści bloku (wyrenderowanego w measurerze), którego LINIA wystaje
   * poza `limitY` (viewport-owe px). Zwraca pozycję na początku tej linii — łamanie linii
   * kończy się na granicy słowa, więc podział nie tnie słów. Zwinięte znaki (spacja
   * skonsumowana przez łamanie — zero rectów) traktowane jak mieszczące się.
   */
  private _findLineSplitPoint(block: HTMLElement, limitY: number): { node: Node; offset: number } | null {
    const EPS = 0.5;
    const probe = document.createRange();
    // Środowiska bez layoutu (jsdom w testach) nie implementują Range.getClientRects —
    // degradacja do braku podziału (stare zachowanie: cały blok na kolejną stronę).
    if (typeof probe.getClientRects !== 'function') return null;
    const walker = document.createTreeWalker(block, NodeFilter.SHOW_TEXT | NodeFilter.SHOW_ELEMENT, {
      acceptNode: (n: Node) => {
        if (n.nodeType === Node.TEXT_NODE) return NodeFilter.FILTER_ACCEPT;
        const el = n as HTMLElement;
        // Atomowe elementy inline traktujemy jak pojedynczy „znak" — bez schodzenia w głąb.
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
      if (nodeRects.length === 0 || nodeBottom <= limitY + EPS) continue; // cały węzeł się mieści

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

  /**
   * Wiersze NALEŻĄCE do tej tabeli (thead/tbody/tfoot + bezpośrednie `tr`) — `table.rows`
   * per spec pomija wiersze tabel ZAGNIEŻDŻONYCH w komórkach (RC4: `querySelectorAll('tr')`
   * schodził w głąb i psuł chunking / skan rowSpan).
   */
  private _tableDirectRows(table: HTMLTableElement): HTMLTableRowElement[] {
    return Array.from(table.rows);
  }

  /** Wiersze ŹRÓDŁOWE fragmentu — bez prezentacyjnych kopii nagłówka (data-repeated-header). */
  private _tableSourceRows(table: HTMLTableElement): HTMLTableRowElement[] {
    return this._tableDirectRows(table).filter(tr => !tr.hasAttribute('data-repeated-header'));
  }

  /**
   * Wiersze nagłówka powtarzane na kolejnych stronach (w:tblHeader → data-tbl-header):
   * tylko ciągły prefiks od pierwszego wiersza (ECMA-376 §17.4.49; fixture HDR04).
   */
  private _repeatingHeaderRows(rows: HTMLTableRowElement[]): HTMLTableRowElement[] {
    const out: HTMLTableRowElement[] = [];
    for (const r of rows) {
      if (r.getAttribute('data-tbl-header') !== '1' || r.hasAttribute('data-repeated-header')) break;
      out.push(r);
    }
    return out;
  }

  /**
   * Czy zawartość wiersza może być dzielona między strony (Word: „Zezwalaj na dzielenie
   * wierszy między strony"). Nie dzielimy: jawny zakaz `w:cantSplit`, wiersz nagłówkowy,
   * sztywna wysokość (`hRule=exact` — Word przycina treść, nie łamie) oraz wiersze OBJĘTE
   * scaleniem pionowym (rowspan/vMerge — koordynacja scalenia przez granicę cięcia poza
   * zakresem). Jak Word: atomowe są TYLKO wiersze w zasięgu scalenia, nie cała tabela
   * (RC3 — jeden rowspan gdziekolwiek blokował dzielenie wszystkich wysokich wierszy).
   */
  private _rowCanSplit(table: HTMLTableElement, row: HTMLTableRowElement): boolean {
    if (row.getAttribute('data-cant-split') === '1') return false;
    // cantSplit odziedziczone ze stylu tabeli (reader: data-cant-split-eff, ADR-0108 r.9).
    if (row.getAttribute('data-cant-split-eff') === '1') return false;
    if (row.getAttribute('data-tbl-header') === '1') return false;
    if (row.getAttribute('data-row-hrule') === 'exact') return false;
    // Tylko wiersze WŁASNE tabeli i ich WŁASNE komórki (row.cells) — komórki tabel
    // zagnieżdżonych nie wpływają na scalenia pionowe tabeli zewnętrznej (RC4).
    const rows = this._tableDirectRows(table);
    const idx = rows.indexOf(row);
    // Tail poprzedniego cięcia (wiersz odłączony od tabeli) — cięty dalej jak dotąd;
    // wiersz objęty scaleniem nigdy nie zostaje tailem, bo nie jest cięty wcale.
    if (idx < 0) return true;
    for (let r = 0; r < rows.length; r++) {
      for (const cell of Array.from(rows[r].cells)) {
        if (cell.rowSpan > 1 && idx >= r && idx <= r + cell.rowSpan - 1) return false;
      }
    }
    return true;
  }

  /**
   * Realny layout wiersza w kontekście tabeli (klon: shell + colgroup + sam wiersz, w measurerze
   * o szerokości kolumny strony): per komórka szerokość TREŚCI (do measurera cięcia bloków)
   * oraz top/bottom każdego bloku względem GÓRY wiersza (bordery/padding wliczone naturalnie).
   * Inline height wiersza jest zdejmowany na czas pomiaru — interesuje nas treść, nie minimum.
   * Wydzielone jako metoda instancji, żeby testy jsdom mogły wstrzyknąć deterministyczny layout.
   */
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
        // Dolny padding + ramka komórki: fragment-głowa kończy się nimi PO ostatnim bloku, więc
        // budżet na bloki jest o tyle mniejszy (MF02: tcMar bottom 25 pt → głowa 33 px za
        // wysoka → cięcie odrzucane, cała tabela jechała na nową stronę; ADR-0108 r.9).
        bottomExtraPx: (parseFloat(cs.paddingBottom) || 0) + (parseFloat(cs.borderBottomWidth) || 0),
      };
    });
    measurer.innerHTML = '';
    return out;
  }

  /**
   * Cache pomiarów wysokości podzbiorów wierszy tabel. `_splitTableForPagination` mierzy
   * podzbiory NARASTAJĄCO (O(wierszy²) klonów + layout-flush per tabela) przy KAŻDEJ
   * repaginacji, a podczas pisania tabele się nie zmieniają — profil CDP na ~34-stronicowym
   * dokumencie: ~70% czasu repaginacji szło w te ponowne pomiary (repaginacja odpala się
   * co ≤600 ms przy pisaniu = wyczuwalne cięcie pod klawiszami). Klucz niesie pełny kontekst
   * wyniku: inline style measurera (szerokość kolumny/fonty — pokrywa zoom i zmianę sekcji),
   * shell tabeli i HTML wierszy. Czyszczony przy nowym dokumencie (`setContent`) i po
   * doładowaniu zasobów (`_repaginateAfterResources` — fonty/obrazy zmieniają metryki bez
   * zmiany HTML); twardy limit rozmiaru chroni przed rozrostem przy długiej edycji tabel.
   */
  private _tableMeasureCache = new Map<string, number>();

  /** Cache wsadowego pomiaru wysokości bloków — patrz `_measureBlockRunHeights`.
   *  UWAGA: zwracane tablice są współdzielone (czytane, nigdy nie mutowane przez callerów). */
  private _blockRunMeasureCache = new Map<string, number[]>();

  /**
   * Wysokość podzbioru wierszy tabeli w measurerze (klon: shell + wiersze). Wydzielone jako
   * metoda instancji, żeby testy jsdom (bez layoutu) mogły wstrzyknąć deterministyczne wysokości.
   */
  private _measureTableRowsHeight(
    table: HTMLTableElement,
    subset: HTMLTableRowElement[],
    measurer: HTMLElement
  ): number {
    const t = table.cloneNode(false) as HTMLTableElement;
    // Symetria z budową fragmentu w _splitTableForPagination: bez colgroup szerokości kolumn
    // degradują do auto → inne łamanie tekstu → inna wysokość niż realny fragment na stronie
    // (RC1 — fragment „mieszczący się" w budżecie mierzył się potem wyżej i uciekał na
    // następną stronę). `:scope >` — nie podbieraj colgroup tabeli zagnieżdżonej.
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

  /**
   * Pionowe marginesy tabeli (margin-top + margin-bottom) w kontekście strony — mierzone
   * na klonie shellu w measurerze (klasa `.editor-content` → działa reguła
   * `.editor-content table { margin: 15px 0 }` oraz inline margin z importu). Budżety dla
   * `_splitTableForPagination` muszą być o nie pomniejszone: splitter mierzy SAME wiersze,
   * a kontrola pojemności fragmentu (`_measureBlockRunHeights`) liczy blok z marginesami.
   * Cache współdzielony z `_tableMeasureCache` (gorąca ścieżka repaginacji — ADR-0075:
   * bez cache to dodatkowy layout-flush per tabela przy każdym keystroke).
   */
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

  /** Sekwencja id fragmentów jednego logicznie podzielonego WIERSZA. */
  private _splitRowSeq = 0;

  /**
   * Dzieli WIERSZ tabeli na [head, tail] tak, by head zmieścił się w `budgetPx` (linia cięcia
   * = budgetPx od góry wiersza, wspólna dla wszystkich komórek — jak pozioma granica strony
   * w Wordzie). Per komórka: bloki nad linią → head, blok przecinający → `_splitBlockAtBudget`
   * na measurerze o szerokości TEJ komórki (akapity na granicy linii, listy między punktami),
   * niedzielne (zagnieżdżona tabela, abspos) → w całości do tail. Fragmenty są CZYSTO
   * prezentacyjne: `data-split-row-id` (+ `data-split-row="cont"` na tail) scala z powrotem
   * `_mergeSplitRowsIn` (zapis i pre-merge repaginacji). Inline `height` NIE przechodzi na
   * fragmenty (wymusiłby pełne minimum na każdym); `data-row-height-tw`/`data-row-hrule`
   * zostają tylko na head — po scaleniu wiersz odzyskuje jedną wysokość (kontrakt writera).
   * Null = nie da się sensownie ciąć (za mały budżet, nic nie zostaje po którejś stronie,
   * goły tekst bezpośrednio w komórce).
   */
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
    // Goły tekst bezpośrednio w <td> (bez bloku) nie ma jak być przydzielony do fragmentu.
    for (const cell of cells) {
      for (const n of Array.from(cell.childNodes)) {
        if (n.nodeType === Node.TEXT_NODE && (n.textContent ?? '').trim()) return null;
      }
    }

    const layout = this._measureRowLayout(table, row, measurer);
    if (layout.length !== cells.length) return null;

    const EPS = 0.5;
    const cs = getComputedStyle(measurer);
    // Linia cięcia dla BLOKÓW: budżet minus dolny padding/ramka komórki (największy w wierszu).
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
          // w:keepLines w komórce (ADR-0108 r.9): akapit niedzielny — w całości do kontynuacji.
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

    // Cięcie ma sens tylko, gdy COŚ zostaje na stronie i COŚ płynie dalej.
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

  /**
   * Dzieli tabelę między wierszami; wiersz, który sam nie mieści się w dostępnej wysokości,
   * jest dodatkowo cięty WEWNĄTRZ (`_splitRowAtBudget` — jak Word „Zezwalaj na dzielenie
   * wierszy między strony": część treści zostaje w resztce bieżącej strony, reszta płynie
   * dalej i może być cięta wielokrotnie). Zwraca array <table> dla kolejnych stron.
   */
  private _splitTableForPagination(
    table: HTMLTableElement,
    firstAvail: number,
    fullAvail: number,
    measurer: HTMLElement,
    lineHeightPx = 16
  ): HTMLTableElement[] {
    // Tylko wiersze WŁASNE tabeli — querySelectorAll('tr') schodził do tabel zagnieżdżonych
    // w komórkach i wciągał ich wiersze do chunkingu jak wiersze zewnętrzne (RC4).
    const rows = this._tableDirectRows(table);
    if (rows.length === 0) return [table];

    const measureRows = (subset: HTMLTableRowElement[]): number =>
      this._measureTableRowsHeight(table, subset, measurer);

    // w:tblHeader (ADR-0108 r.9): wiersze nagłówka powtarzane na KAŻDYM kolejnym fragmencie
    // tabeli — wyłącznie ciągłe od pierwszego wiersza (ECMA §17.4.49: wiersz 2 oznaczony bez
    // wiersza 1 NIE powtarza się — HDR04). Kopie są prezentacyjne (data-repeated-header,
    // contenteditable=false), znikają przy scalaniu fragmentów (pre-merge, getContent,
    // schowek) — model źródłowy trzyma 1 wiersz. Budżet kolejnych stron pomniejszony o ich
    // wysokość.
    const headerRows = this._repeatingHeaderRows(rows);
    const headerH = headerRows.length > 0 && headerRows.length < rows.length ? measureRows(headerRows) : 0;
    const contAvail = Math.max(80, fullAvail - headerH);

    const chunks: HTMLTableRowElement[][] = [];
    let bucket: HTMLTableRowElement[] = [];
    let avail = firstAvail;
    for (let ri = 0; ri < rows.length; ri++) {
      let row = rows[ri];
      // Wielokrotne cięcie tego samego wiersza (tail może przekraczać kolejne pełne strony);
      // twardy limit iteracji — obrona przed patologią pomiarów (np. measure stale 0/NaN).
      for (let guard = 0; guard < 100; guard++) {
        const tentative = [...bucket, row];
        const h = measureRows(tentative);
        if (h <= avail) {
          bucket = tentative;
          break;
        }
        // Wiersz nie mieści się: spróbuj rozciąć jego zawartość na granicy strony —
        // W RESZTCE bieżącej strony (jak Word), z budżetem pomniejszonym o wiersze bucketa.
        const bucketH = bucket.length > 0 ? measureRows(bucket) : 0;
        // Zapas 3 px (ADR-0108 r.9, MF01): fragment-głowa mierzył się ~1 px ponad budżet
        // (ramki 0.7 px + zaokrąglenia paddingu) → kontrola pojemności fragmentu 0 odrzucała
        // cięcie i CAŁA tabela jechała na świeżą stronę zamiast dzielić wiersz jak Word.
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
          // Nie da się ciąć w resztce — wiersz od świeżej strony (i ponowna próba cięcia tam).
          chunks.push(bucket);
          bucket = [];
          avail = contAvail;
          continue;
        }
        // Świeża strona i cięcie niemożliwe (cantSplit/exact/rowspan/za mało treści) —
        // połóż w całości (guard anty-pętla; nadmiar przycięty jak dotąd dla niedzielnych).
        bucket = [row];
        break;
      }
    }
    if (bucket.length > 0) chunks.push(bucket);

    // Tag every fragment of THIS split with a shared logical id so serialization can merge them
    // back into one table (R-17). The colgroup (column widths) is cloned into each fragment so a
    // fragment renders with correct columns and the merged result keeps them.
    // Id jest STABILNE między repaginacjami (reuse z pre-merge'owanej tabeli) — świeże id przy
    // każdym przebiegu zmieniałoby HTML stron i wymuszało rebind [innerHTML] (utrata kursora).
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

  /** Sekwencja id dla fragmentów jednej logicznie podzielonej tabeli (R-17). */
  private _splitTableSeq = 0;

  /** Jednorazowy przebieg po renderze, gdy strona 1 w DOM miała starą geometrię (ADR-0108 r.7). */
  private _staleGeometryRerun = false;

  /**
   * Czy blok żąda rozpoczęcia nowej strony WŁAŚCIWOŚCIĄ `page-break-before` (w:pageBreakBefore
   * z Worda / checkbox dialogu akapitu) — w odróżnieniu od markera `.page-break` blok sam
   * idzie na nową stronę. `break-before:column` (podział kolumny) celowo nie pasuje.
   */
  /** w:keepNext — reader emituje `break-after:avoid` (ADR-0108 r.3); jawne `auto` = wyłączone. */
  private _keepsWithNext(el: HTMLElement): boolean {
    return /(?:page-)?break-after\s*:\s*avoid\b/i.test(el.getAttribute?.('style') ?? '');
  }

  /** w:keepLines — reader emituje `break-inside:avoid`: akapit nie dzieli się między strony. */
  private _keepsLinesTogether(el: HTMLElement): boolean {
    return /(?:page-)?break-inside\s*:\s*avoid\b/i.test(el.getAttribute?.('style') ?? '');
  }

  private _hasPageBreakBeforeStyle(el: HTMLElement): boolean {
    // Z atrybutu style (nie przez CSSOM): działa identycznie w przeglądarce i jsdom,
    // niezależnie od tego, czy silnik implementuje akcesory legacy `page-break-before`.
    const style = el.getAttribute?.('style') ?? '';
    return /(?:page-)?break-before\s*:\s*(always|page)\b/i.test(style);
  }

  /** Czy blok to manualny page break (div.page-break albo akapit zawierający tylko page-break). */
  private _isPageBreakBlock(el: HTMLElement): boolean {
    if (!el || el.nodeType !== 1) return false;
    if (el.classList?.contains('page-break')) return true;
    const nested = el.querySelector?.('.page-break');
    return !!nested && (el.textContent ?? '').trim().length === 0;
  }

  /** Marker podziału kolumny (div.docx-column-break z w:br type=column, ADR-0039). */
  private _isColumnBreakBlock(el: HTMLElement): boolean {
    return !!el && el.nodeType === 1 && !!el.classList?.contains('docx-column-break');
  }

  /**
   * Scala sąsiednie fragmenty tej samej logicznej tabeli (te same data-split-table-id) w jedną
   * tabelę — paginacja widoku dzieli tabelę między strony, ale zapis ma zawierać jedną tabelę.
   * Niezależne sąsiednie tabele (bez wspólnego id) NIE są scalane (R-17 / reguła 11).
   */
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
        // Tylko wiersze WŁASNE fragmentu — nie wyrywaj wierszy tabel zagnieżdżonych (RC4).
        this._tableSourceRows(next as HTMLTableElement).forEach(tr => targetBody.appendChild(tr));
        const toRemove = next;
        next = next.nextElementSibling;
        toRemove.remove();
      }
      first.removeAttribute('data-split-table-id');
    });

    // Wiersze cięte WEWNĄTRZ (dzielenie wiersza między strony) — po sklejeniu tabel fragmenty
    // tego samego <tr> są sąsiadami; scal je z powrotem w jeden logiczny wiersz. Defensywnie
    // po WSZYSTKICH tabelach (fragment wiersza zawsze implikuje fragment tabeli, ale edycje
    // mogą przetasować strukturę).
    tmp.querySelectorAll('table').forEach(t => this._mergeSplitRowsIn(t as HTMLTableElement));

    return tmp.innerHTML;
  }

  /**
   * Przenosi kursor na sąsiednią stronę (osobny contenteditable) na granicy strony. Zwraca true
   * (i obsłużono nawigację) tylko gdy: jest 2+ stron, karetka jest zwinięta, leży na skrajnej
   * linii bieżącej strony i istnieje sąsiednia strona. Inaczej false → przeglądarka robi normalną
   * nawigację wewnątrz strony. Nie ingeruje w zaznaczenia ani w nawigację wewnątrz strony.
   */
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

  /**
   * Backspace na samym początku strony: usuń manualny page-break kończący POPRZEDNIĄ stronę.
   * Zwraca true, gdy coś usunięto (caller robi preventDefault). Nie rusza wrappera strony —
   * tylko marker `<div class="page-break">`; resztę scala repaginacja.
   */
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

  /** Czy zwinięta karetka stoi na samym początku treści edytora (brak tekstu przed nią). */
  private _isCaretAtEditorStart(editor: HTMLElement): boolean {
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0 || !sel.isCollapsed) return false;
    const r = sel.getRangeAt(0);
    const probe = document.createRange();
    probe.selectNodeContents(editor);
    probe.setEnd(r.startContainer, r.startOffset);
    return probe.toString().length === 0;
  }

  /** Usuwa końcowy marker page-break (i puste węzły po nim) z edytora. Zwraca true gdy usunięto. */
  private _removeTrailingPageBreak(editor: HTMLElement): boolean {
    let last = editor.lastChild;
    // Pomiń końcowe puste/whitespace węzły tekstowe.
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

  /**
   * Backspace na początku strony powstałej z AUTO-paginacji (bez manualnego page-break).
   * Strony to osobne `contenteditable`, więc przeglądarka nie scali bloków między nimi —
   * domyślny Backspace na początku 2. strony nic nie robi (objaw: „nie usuwa 2. strony /
   * nie przechodzi na 1."). Tu scalamy pierwszy blok bieżącej strony z ostatnim blokiem
   * poprzedniej (jak Word) albo usuwamy pusty blok wiodący, a następnie repaginujemy — treść
   * spływa w górę i nadmiarowa strona znika. Wołane TYLKO gdy `_tryDeletePageBreakBackwards`
   * nie znalazł manualnego break'a. Zwraca true, gdy obsłużono.
   */
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
      // Pusty blok wiodący (typowa nadmiarowa pusta strona) — usuń, karetka na koniec poprzedniego bloku.
      firstBlock.remove();
      range.selectNodeContents(lastBlock);
      range.collapse(false);
    } else if (canMerge) {
      // Scal treść pierwszego bloku z ostatnim blokiem poprzedniej strony; karetka na styku.
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
      // Niekompatybilne (np. tabela) — nie scalaj treści, tylko przenieś karetkę na koniec
      // poprzedniej strony. Nawigacja działa, treść nietknięta (brak utraty/uszkodzenia).
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

  /** Czy zwinięta karetka leży na górnej/dolnej skrajnej linii danego edytora strony. */
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

  /** Ustawia karetkę na początku/końcu wskazanego edytora strony i aktywuje tę stronę. */
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

  /**
   * Flatten: jeśli DOCX import wsadził treść w jeden wrapper &lt;div&gt;/&lt;section&gt;/&lt;article&gt;,
   * weź jego dzieci (max 3 poziomy). Współdzielone przez paginację i kotwicę karetki, żeby
   * indeksy bloków były identyczne po obu stronach (zapis/odtworzenie pozycji kursora).
   */
  private _flattenTopBlocks(el: HTMLElement, depth = 0): HTMLElement[] {
    // Pasmo kolumnowe strony przejściowej (div.docx-col-band) to twór PREZENTACYJNY
    // paginacji — bloki w środku są zwykłymi blokami body (rozwinięcie utrzymuje spójne
    // indeksy bloków między paginacją a kotwicą karetki; pasmo odtwarza się przy renderze).
    const kids = (Array.from(el.children) as HTMLElement[]).flatMap(c =>
      c.classList.contains('docx-col-band') ? (Array.from(c.children) as HTMLElement[]) : [c]
    );
    if (depth >= 3) return kids;
    if (kids.length === 1) {
      const c = kids[0];
      // Markerów page-break / docx-section-break nie rozwijamy — to atomiczne bloki-znaczniki;
      // rekursja do środka gubiłaby je przy repaginacji (strona z samym markerem).
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

  /**
   * Zapamiętuje pozycję kursora jako BLOK + offset tekstowy w bloku (nie globalny offset
   * tekstowy). Czysty offset tekstowy jest niejednoznaczny na granicy bloków: nowy PUSTY akapit
   * po ENTER ma 0 znaków, więc po repaginacji restore lądował na końcu poprzedniego akapitu
   * (objaw: „kursor wraca do poprzedniej linii"). Indeks bloku rozróżnia pusty akapit od końca
   * poprzedniego. Kolejność bloków jest stabilna między repaginacjami (zmienia się tylko ich
   * rozkład na strony), więc indeks przeżywa przebudowę DOM.
   */
  private _saveGlobalCaret(refs: ElementRef<HTMLDivElement>[]): { block: number; offset: number; item?: number; itemOffset?: number } | null {
    const sel = window.getSelection();
    if (!sel || sel.rangeCount === 0) return null;
    return this._globalCaretFromRange(sel.getRangeAt(0), refs);
  }

  /**
   * Mapuje dowolny Range (żywa selekcja lub `savedSelection`) na kotwicę { blok, offset }.
   * Indeksujemy akapity LOGICZNE: fragment kontynuacji podzielonego akapitu
   * (data-split-para="cont", ADR-0046) nie zwiększa indeksu, a offset liczy się od początku
   * CAŁEGO akapitu — kotwica przeżywa zmianę punktu podziału między repaginacjami.
   */
  private _globalCaretFromRange(range: Range, refs: ElementRef<HTMLDivElement>[]): { block: number; offset: number; item?: number; itemOffset?: number } | null {
    const all: HTMLElement[] = [];
    let hit = -1;
    let offset = 0;
    // Punkt listy pod karetką: cała lista to JEDEN blok, więc sam offset tekstowy nie odróżnia
    // nowego PUSTEGO punktu (po Enter) od końca poprzedniego — kursor wracał do poprzedniego punktu,
    // a wpisywany tekst doklejał się do niego (najczęściej w listach zagnieżdżonych).
    let item = -1;
    let itemOffset = 0;
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
            const endEl = range.endContainer instanceof Element ? range.endContainer : range.endContainer.parentElement;
            const li = endEl?.closest('li') ?? null;
            if (li && blocks[b].contains(li)) {
              item = Array.from(blocks[b].querySelectorAll('li')).indexOf(li as HTMLLIElement);
              const inItem = range.cloneRange();
              inItem.selectNodeContents(li);
              inItem.setEnd(range.endContainer, range.endOffset);
              itemOffset = inItem.toString().length;
            }
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
    // Cofnij do początku łańcucha fragmentów, dosumowując długości wcześniejszych fragmentów.
    let gi = hit;
    while (gi > 0 && this._isContinuationBlock(all[gi - 1], all[gi])) {
      gi--;
      offset += (all[gi].textContent ?? '').length;
      if (item >= 0) item += all[gi].querySelectorAll('li').length;
    }
    let logical = 0;
    for (let k = 0; k < gi; k++) {
      if (k === 0 || !this._isContinuationBlock(all[k - 1], all[k])) logical++;
    }
    return item >= 0 ? { block: logical, offset, item, itemOffset } : { block: logical, offset };
  }

  /**
   * Czy blok jest KONTYNUACJĄ poprzedniego bloku logicznego — fragmentem z paginacji:
   * ogon dzielonego akapitu (data-split-para="cont") albo kolejny fragment tej samej
   * logicznej tabeli (data-split-table-id). Snapshoty undo trzymają treść SCALONĄ
   * (getContent), więc indeks bloku liczony po żywym, podzielonym DOM musi składać
   * łańcuchy OBU rodzajów — bez tabel kursor po undo lądował o N bloków dalej w
   * dokumentach importowanych (tabele wielostronicowe przed miejscem edycji).
   */
  private _isContinuationBlock(prev: HTMLElement | null, el: HTMLElement): boolean {
    if (el.getAttribute('data-split-para') === 'cont') return true;
    const tid = el.getAttribute('data-split-table-id');
    return !!tid && !!prev && prev.getAttribute('data-split-table-id') === tid;
  }

  /**
   * Wymusza zgodność DOM edytorów stron z obliczonym rozkładem paginacji. Angular pomija
   * nadpisanie `[innerHTML]`, gdy wartość SafeHtml się nie zmieniła (cache) — więc strona,
   * w którą użytkownik pisze (żywe edycje w DOM, nieznane sygnałowi), NIE jest resetowana do
   * paginowanej treści: rośnie w pion i duplikuje nadmiar na dalsze strony. Nadpisujemy DOM
   * tylko tych stron, które faktycznie się rozjechały (bez zbędnego churnu i utraty kursora na
   * stronach już zgodnych), i re-opakowujemy obrazy w nadpisanej treści.
   */
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

  /**
   * Przywraca kursor wg kotwicy { blok, offset }. Numeracja bloków = akapity LOGICZNE
   * (fragmenty kontynuacji nie liczą się osobno); offset schodzi wzdłuż łańcucha
   * fragmentów do właściwego kawałka (ADR-0046).
   */
  private _restoreGlobalCaret(caret: { block: number; offset: number; item?: number; itemOffset?: number } | null): void {
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
      // Karetka w punkcie listy: celuj w TEN punkt (indeks w łańcuchu fragmentów listy).
      if (caret.item !== undefined && caret.item >= 0) {
        let remaining = caret.item;
        for (let f = g; f < all.length && (f === g || this._isContinuationBlock(all[f - 1].el, all[f].el)); f++) {
          const items = all[f].el.querySelectorAll<HTMLLIElement>('li');
          if (remaining < items.length) {
            refs[all[f].page].nativeElement.focus();
            this._placeCaretInListItem(items[remaining], caret.itemOffset ?? 0);
            items[remaining].scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
            this.editorContent = refs[all[f].page];
            this.activePageIndex.set(all[f].page);
            return;
          }
          remaining -= items.length;
        }
      }
      // Łańcuch fragmentów: zjedź offsetem do fragmentu, w którym wypada pozycja.
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
      // focus PRZED ustawieniem range: focus() na contenteditable potrafi zresetować
      // świeżo dodaną selekcję do początku kontenera (jsdom zawsze; przeglądarki przy
      // pierwszym fokusie) — objaw „kursor wraca na początek dokumentu" (bug 13184834).
      refs[all[idx].page].nativeElement.focus();
      this._placeCaretAtTextOffset(target, off);
      // Po repaginacji treść mogła przelać się na kolejną stronę POZA widokiem — bez tego
      // użytkownik musiał ręcznie scrollować, by ją zobaczyć. `block:'nearest'` nie rusza
      // widoku, gdy kursor jest już widoczny (brak skoków przy pisaniu w środku strony).
      target.scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
      this.editorContent = refs[all[idx].page];
      this.activePageIndex.set(all[idx].page);
      return;
    }
    // Kotwica poza zakresem — undo mogło usunąć bloki, w których stał kursor. Domknij do
    // KOŃCA dokumentu zamiast gubić selekcję (objaw: kursor skakał na początek dokumentu).
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

  /**
   * Ustawia zwiniętą karetkę na danym offsetcie tekstowym wewnątrz bloku. Pusty blok (np. nowy
   * akapit po ENTER, bez węzłów tekstowych) → karetka na początku bloku. Dzięki temu kursor
   * zostaje w nowej linii zamiast wracać do poprzedniej.
   */
  /**
   * Karetka w punkcie listy na offsetcie liczonym od początku punktu. Pusty punkt (sam znacznik
   * lub `<br>`) → tuż za znacznikiem / na początku punktu — NIGDY w liście zagnieżdżonej ani
   * w poprzednim punkcie.
   */
  private _placeCaretInListItem(li: HTMLLIElement, offset: number): void {
    const sel = window.getSelection();
    if (!sel) return;
    const ownText = document.createTreeWalker(li, NodeFilter.SHOW_TEXT, {
      acceptNode: node => {
        const host = node.parentElement?.closest('ul, ol, li');
        return host === li ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_REJECT;
      },
    });
    const range = document.createRange();
    let remaining = offset;
    let last: Node | null = null;
    for (let node = ownText.nextNode(); node; node = ownText.nextNode()) {
      const atom = node.parentElement?.closest('[contenteditable="false"]');
      const length = node.textContent?.length ?? 0;
      if (atom && li.contains(atom)) { remaining -= length; continue; } // znacznik punktora
      if (remaining <= length) {
        range.setStart(node, Math.max(0, remaining));
        range.collapse(true);
        sel.removeAllRanges();
        sel.addRange(range);
        return;
      }
      remaining -= length;
      last = node;
    }
    if (last) {
      range.setStart(last, last.textContent?.length ?? 0);
    } else {
      const marker = li.querySelector(':scope > span.list-marker');
      if (marker) range.setStartAfter(marker);
      else range.setStart(li, 0);
    }
    range.collapse(true);
    sel.removeAllRanges();
    sel.addRange(range);
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
        // Atomic inline (tab carrier, bullet marker): the caret must never sit INSIDE a
        // contenteditable=false element — Chrome would snap it out unpredictably. Land
        // before/after the wrapper instead, matching which edge the offset falls on.
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
    // Brak węzłów tekstowych (pusty akapit) lub offset poza zakresem — początek/koniec bloku.
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

  /**
   * Pobiera zawartość HTML do ZAPISU — scala strony BEZ wstawiania znaczników
   * <div class="page-break"> na granicach stron.
   *
   * Granice stron w edytorze pochodzą z auto-paginacji wg wysokości (_repaginateNow),
   * a nie z intencji użytkownika. Wstawianie tu page-breaków materializowało paginację
   * widoku jako twarde <w:br type=page> w DOCX — dokument rósł (np. 4 → 7 stron), a Word
   * i tak paginuje sam. Akapity są block-atomic (nie dzielone), więc czysta konkatenacja
   * odtwarza treść; jawne page-breaki użytkownika (insertPageBreak) przeżywają jako
   * <div class="page-break"> wewnątrz treści strony. Patrz analiza orginał_GOOD vs zapisany_BAD.
   */
  getContent(): string {
    const refs = this.pageEditorRefs?.toArray() ?? [];
    if (refs.length === 0) {
      const fallback = this.editorContent?.nativeElement;
      return fallback ? this._wrapWithDocumentContainer(this._serializeSingleEditor(fallback)) : '';
    }

    const parts = refs.map(r => this._serializeSingleEditor(r.nativeElement));
    const merged = parts.filter(p => p && p.trim().length > 0).join('');
    // Scal fragmenty tej samej logicznej tabeli (R-17) — wraz z fragmentami CIĘTYCH WIERSZY —
    // a dopiero POTEM fragmenty akapitów (ADR-0046): komórkowe `data-split-para="cont"` stają
    // się rodzeństwem swojej pierwszej połówki dopiero po scaleniu wierszy; odwrotna kolejność
    // zdejmowałaby im marker jako „osieroconym" i utrwalała fragmentację w DOCX. Na końcu
    // wrapper .document-content (domyślne wartości dokumentu do writera).
    return this._wrapWithDocumentContainer(this._mergeSplitParagraphs(this._mergeSplitTables(merged)));
  }

  /**
   * Scala fragmenty akapitu podzielonego przez paginację na granicy strony (ADR-0046):
   * `<p data-split-para="cont">` dokleja swoje dzieci do POPRZEDNIEGO bloku tego samego
   * tagu i znika — do zapisu/undo idzie zawsze akapit LOGICZNY (identyczny z oryginałem;
   * punkt podziału leży na łamaniu linii, więc czysta konkatenacja odtwarza tekst 1:1).
   * Osierocony fragment (poprzednik usunięty w edycji) zostaje zwykłym akapitem.
   */
  private _mergeSplitParagraphs(html: string): string {
    if (!html.includes('data-split-para')) return html;
    const tmp = document.createElement('div');
    tmp.innerHTML = html;
    this._mergeSplitParasWithin(tmp);
    return tmp.innerHTML;
  }

  /** Wnętrze scalania split-para na żywym elemencie — używane też przy scalaniu wierszy tabel. */
  private _mergeSplitParasWithin(root: ParentNode): void {
    root.querySelectorAll('[data-split-para="cont"]').forEach(cont => {
      const el = cont as HTMLElement;
      const prev = el.previousElementSibling;
      // Fragment kontenera SDT może skleić się wyłącznie z poprzednikiem-SDT — goły match
      // po tagu wlałby treść formantu np. w div-marker sekcji, gdy head usunięto w edycji.
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

  /**
   * Scala fragmenty CIĘTEGO WIERSZA (data-split-row-id, tail z data-split-row="cont") z powrotem
   * w jeden logiczny <tr>: per-kolumna przenosi dzieci komórek tail do komórek head, skleja
   * komórkowe fragmenty akapitów (stały się rodzeństwem) i zdejmuje markery. Wysokość wiersza
   * wraca z `data-row-height-tw` na head (fragmenty celowo nie niosą inline height).
   * Osierocony tail (head usunięty w edycji) zostaje zwykłym wierszem.
   * `keepIds=true` (pre-merge repaginacji) zostawia `data-split-row-id` na scalonym wierszu —
   * świeże cięcie reuse'uje id, więc HTML stron jest stabilny między przebiegami (bez rebindu).
   */
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

  /** Serializuje pojedynczy edytor strony do HTML (z odwijaniem image-wrapperów).
   *
   * UWAGA (R-18, zamknięte): wcześniej KAŻDY wiersz tabeli dostawał tu „zapieczoną"
   * wysokość zmierzoną z DOM (getBoundingClientRect) — render edytora nadpisywał
   * semantykę DOCX (wiersze bez trHeight dostawały sztuczne atLeast, exact rosło od
   * line-height edytora) i dokument puchł przy każdym autosave. Teraz serializujemy
   * wyłącznie to, co jest jawnie w inline style (import z DOCX / ręczny resize) —
   * wiersze bez wysokości wracają do Worda jako auto, jak w oryginale. */
  private _serializeSingleEditor(editor: HTMLDivElement): string {
    const clone = editor.cloneNode(true) as HTMLDivElement;

    // Pasmo kolumnowe strony przejściowej (docx-col-band) jest tworem paginacji — do zapisu
    // rozwijamy je do bloków (układ kolumn niesie marker sekcji + data-col-* dla writera).
    clone.querySelectorAll('.docx-col-band').forEach(band => {
      const parent = band.parentNode;
      if (!parent) return;
      while (band.firstChild) parent.insertBefore(band.firstChild, band);
      band.remove();
    });

    // Stany UI pól tekstowych (zaznaczenie/drag/kursor krawędzi) są edycyjne — nie mogą
    // trafić do zapisu. Sama klasa docx-textbox + data-* zostają (kontrakt writera).
    clone.querySelectorAll('.docx-textbox').forEach(tb => {
      tb.classList.remove('tb-selected', 'tb-dragging', 'tb-edge');
    });
    // Defensywnie: znacznik kotwicy nigdy nie powinien być w contenteditable, ale gdyby
    // trafił (np. przez wklejenie) — usuń przy serializacji.
    clone.querySelectorAll('.anchor-badge').forEach(b => b.remove());

    // Etykiety list (data-list-label/-suffix) są czysto prezentacyjne — liczy je silnik
    // przy każdym renderze; w zapisie byłyby szumem i groziłyby dryfem po edycjach.
    stripListLabelAttributes(clone);

    // Klasa .fmt-trailing-br („Pokaż wszystko": ¶ malowane w overlayu za ↵ przy <br>
    // zamykającym blok) jest czysto prezentacyjna — nie może wejść do zapisu.
    this._stripFmtTrailingBr(clone);
    // Klasa .fmt-page-top (szczyt strony, ADR-0108) — tak samo prezentacyjna: inline margin-top
    // z readera ZOSTAJE w treści, więc writer nadal widzi zadeklarowane w:before.
    this._stripFmtPageTop(clone);

    this._unwrapImageWrappers(clone);

    return clone.innerHTML;
  }

  /**
   * Zdejmuje edycyjne wrappery obrazów (span.editor-image-wrapper: contenteditable/draggable/
   * uchwyty resize/inline left-top) — do modelu/zapisu idzie goły <img> z data-* (pozycja
   * kotwicy żyje w data-x/y-emu na <img>, inline left/top wrappera są edycyjne i porzucane).
   * Wspólne dla serializacji body (_serializeSingleEditor) i commitu pasma nagłówka/stopki.
   */
  private _unwrapImageWrappers(root: HTMLElement): void {
    // Artefakty selekcji kształtów pass-through (ADR-0063) — czysto edycyjne, nie mogą
    // trafić do modelu/zapisu (writer odtwarza kształt z data-docx-xml, ale klasa/uchwyt
    // zaśmiecałyby HTML i cache SafeHtml).
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
        // Pozycję niesie kontrakt data-x/y-emu; edycyjny styl absolutu wrappera nie może
        // zostać na <img> (display mode pozycjonuje od nowa z kontraktu).
        imgEl.style.removeProperty('position');
        imgEl.style.removeProperty('left');
        imgEl.style.removeProperty('top');
        imgEl.style.removeProperty('z-index');
        wrapper.replaceWith(imgEl);
      }
    });
  }

  /**
   * Ustawia zawartość HTML — rozbija na strony po znacznikach <div class="page-break">.
   */
  setContent(html: string): void {
    this._tableMeasureCache.clear();
    this._blockRunMeasureCache.clear();
    const unwrapped = this._captureDocumentDefaults(html);
    const pages = this._splitHtmlIntoPages(unwrapped ?? html ?? '<p></p>');
    this.pageContents.set(pages);
    // Pages already carry their own page-break markers (see _splitHtmlIntoPages); plain join
    // avoids doubling them.
    this._content.set(pages.join(''));
    this._isDirty = false;
    // Po Angular re-render: opakuj obrazki, przelicz etykiety list, zapisz snapshot, repaginuj
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
      // Import zwykle wnosi nowe czcionki/obrazy — skoryguj paginację, gdy się doładują,
      // żeby liczba stron odpowiadała finalnemu układowi (a nie metrykom fallbacku).
      this._repaginateAfterResources();
    }, 0);
  }

  /**
   * Opakowuje istniejące elementy <img> (bez wrappera) w editor-image-wrapper.
   * Domyślnie operuje na aktywnym edytorze body; można podać kontener (header/footer/strona).
   */
  private wrapExistingImages(container?: HTMLElement | null): void {
    const editor = container ?? this.editorContent?.nativeElement;
    if (!editor) return;

    const images = editor.querySelectorAll('img');
    images.forEach((img: HTMLImageElement) => {
      // Pomiń obrazy które już mają wrapper
      if (img.parentElement?.classList.contains('editor-image-wrapper')) {
        return;
      }

      const imageId = `img-${Date.now()}-${Math.floor(Math.random() * 10000)}`;

      const wrapper = document.createElement('span');
      wrapper.className = 'editor-image-wrapper';
      wrapper.setAttribute('data-image-id', imageId);
      wrapper.setAttribute('contenteditable', 'false');
      wrapper.setAttribute('draggable', 'true');

      // Keep the img's inline width/height untouched so the editor renders the
      // image at the same size as the read-only display (rule 14 — no apparent
      // resize on entering edit mode). The wrapper is inline-block, so it
      // shrinks to fit the image; clamping to container width is delegated to
      // the img's own `max-width: 100%`.
      wrapper.style.maxWidth = '100%';
      img.style.maxWidth = '100%';
      img.setAttribute('draggable', 'false');

      img.parentNode?.insertBefore(wrapper, img);
      wrapper.appendChild(img);

      // Restore floating positioning from the img's data attributes (set by the DOCX
      // importer when the source had wp:anchor, or persisted from a previous editing
      // session). Inline images are the default and need no further setup.
      const posMode = img.dataset['posMode'];
      if (posMode === 'front' || posMode === 'behind') {
        const xPx = Math.round((Number(img.getAttribute('data-x-emu') ?? 0)) / 9525);
        const yPx = Math.round((Number(img.getAttribute('data-y-emu') ?? 0)) / 9525);
        // W edytowanym paśmie nagłówka/stopki origin absolutu ≠ origin kontraktu — styl
        // dostaje pozycję przeliczoną do układu pasma, data-emu zostają w kontrakcie.
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

      // Restore border + crop from data attributes set by the DOCX importer or a
      // previous edit. They're applied as inline CSS on the <img> for round-trip.
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

  /**
   * Oznacza dokument jako zapisany
   */
  markAsSaved(): void {
    this.lastSavedContent = this.getContent();
    this._isDirty = false;
    this.updateState();
  }

  /**
   * Pobiera aktualne formatowanie z zaznaczenia
   */
  getCurrentFormatting(): any {
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) {
      return null;
    }

    // Pobierz element z którego kopiujemy formatowanie
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

  /**
   * Aplikuje formatowanie do zaznaczenia
   */
  applyFormatting(format: any): void {
    if (!format) return;

    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0 || selection.isCollapsed) {
      return;
    }

    // Atrybuty znakowe USTAWIAMY na wartość źródła, nie przełączamy: `executeCommand`
    // działa jak toggle, więc bezwarunkowe wywołanie zdejmowałoby pogrubienie z tekstu,
    // który już był pogrubiony (malarz ma kopiować format, nie odwracać go).
    const setMark = (state: boolean, queryName: string, command: EditorCommand) => {
      if (document.queryCommandState(queryName) !== state) this.executeCommand(command);
    };
    setMark(!!format.bold, 'bold', 'bold');
    setMark(!!format.italic, 'italic', 'italic');
    setMark(!!format.underline, 'underline', 'underline');
    setMark(!!format.strikethrough, 'strikeThrough', 'strikethrough');
    setMark(!!format.subscript, 'subscript', 'subscript');
    setMark(!!format.superscript, 'superscript', 'superscript');

    // Krój, rozmiar i kolory idą przez te same appliery co toolbar (ADR-0102: owijanie
    // text-node'ów W MIEJSCU). Poprzednia implementacja robiła tu `extractContents()` +
    // `insertNode()` — wzorzec, który przy zaznaczeniu przez szkielet stron wyrywał
    // strukturę i produkował puste strony — oraz zapisywała rozmiar w `px`, przez co
    // eksport do DOCX dostawał inną wartość niż pokazywał toolbar.
    if (format.fontFamily) this.setFontFamily(format.fontFamily);
    if (format.fontSize) this.setFontSize(format.fontSize);
    if (format.textColor) this.setTextColor(format.textColor);
    if (format.backgroundColor) this.setBackgroundColor(format.backgroundColor);

    // Emituj zmiany
    const html = this.editorContent?.nativeElement?.innerHTML || '';
    this._content.set(html);
    this.contentChange.emit(html);
    this.updateFormattingState();
  }

  /**
   * Konwertuje RGB na HEX
   */
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

  // ================================
  // Nagłówek i Stopka
  // ================================

  /**
   * Rozpoczyna edycję nagłówka. Jeśli przekazano event — kursor zostanie ustawiony
   * w miejscu kliknięcia. W przeciwnym wypadku trafi na koniec zawartości.
   */
  startEditingHeader(event?: MouseEvent): void {
    // Dokument chroniony: nagłówek nie wchodzi w tryb edycji (bug 13677237 — klik
    // otwierał panel „Nagłówek i stopka" i pozwalał modyfikować chroniony dokument).
    if (this.readOnly) return;
    // Jeśli już edytujemy nagłówek, nie restartuj kursora (pozwól natywnemu klikowi go ustawić)
    if (this.editingSection() === 'header') return;
    const clickX = event?.clientX;
    const clickY = event?.clientY;
    this.editingHfPageIndex.set(this._pageIndexFromEvent(event));
    this.editingSection.set('header');
    this.editingSectionChange.emit('header');
    setTimeout(() => {
      const el = this.headerContentEl?.nativeElement;
      if (el) {
        // Load the variant shown on the CLICKED page so the editor matches the
        // displayed content (rule 10 — no apparent editing): wpis sekcyjny > warianty bazy.
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

  /**
   * Rozpoczyna edycję stopki
   */
  startEditingFooter(event?: MouseEvent): void {
    // Dokument chroniony: stopka nie wchodzi w tryb edycji (bug 13677237).
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

  /** Strona, na której kliknięto pasmo nagłówka/stopki (fallback: 0). */
  private _pageIndexFromEvent(event?: MouseEvent): number {
    const page = (event?.target as HTMLElement | null)?.closest?.('.page') as HTMLElement | null;
    const n = Number(page?.getAttribute('data-page-number') ?? '1');
    return Number.isFinite(n) && n >= 1 ? n - 1 : 0;
  }

  /** HTML wariantu edytowanego na danej stronie — TEN SAM resolver co rendering (rule 10:
   *  klikasz to, co widzisz; edytujesz dokładnie wyświetlany wariant). */
  private _editableHeaderHtml(pageIndex: number): string {
    return this._resolveHfVariant(pageIndex, 'header').html;
  }

  private _editableFooterHtml(pageIndex: number): string {
    return this._resolveHfVariant(pageIndex, 'footer').html;
  }

  /** Aktualizuje WSKAZANY wariant nagłówka/stopki wpisu sekcyjnego i emituje zmianę. */
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

  /**
   * Ustawia kursor w punkcie (x,y) jeśli trafia w content edytora; w przeciwnym
   * wypadku ustawia kursor na końcu.
   */
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

  /**
   * Ustawia kursor na końcu danego contenteditable.
   */
  private placeCaretAtEnd(el: HTMLElement): void {
    const range = document.createRange();
    range.selectNodeContents(el);
    range.collapse(false);
    const sel = window.getSelection();
    sel?.removeAllRanges();
    sel?.addRange(range);
  }

  /**
   * Kończy edycję nagłówka/stopki i wraca do głównej treści
   */
  stopEditingHeaderFooter(): void {
    if (this.editingSection() !== 'body') {
      this.editingSection.set('body');
      this.editingSectionChange.emit('body');
      this.stopObservingSectionGeometry();
    }
  }

  /**
   * Obsługa blur nagłówka
   */
  onHeaderBlur(): void {
    const content = this.headerContentEl?.nativeElement?.innerHTML || '';
    this._applyEditedHeaderHtml(content);
    // Emit the full header (incl. firstPage/even variants) — partial emit on blur
    // would drop the other variants in the parent's signal.
    this.emitHeaderFooterChanges();
    // Nie kończymy edycji od razu, pozwalamy na kliknięcie poza nagłówkiem
  }

  /**
   * Obsługa input nagłówka — emituje zmiany do parent (saveDocument używa headerContent)
   */
  /**
   * Zapis edytowanej treści nagłówka do WŁAŚCIWEGO wariantu (rule 10 — no apparent
   * editing): wpis sekcyjny strony edycji > wariant first-page (tylko strona 0) > default.
   */
  private _applyEditedHeaderHtml(content: string): void {
    this._applyEditedHfHtml(content, 'header');
  }

  private _applyEditedFooterHtml(content: string): void {
    this._applyEditedHfHtml(content, 'footer');
  }

  /** Zapis edytowanej treści do WŁAŚCICIELA wyświetlanego wariantu (wpis sekcyjny, który
   *  go definiuje — także dziedziczony, jak edycja połączonego nagłówka w Wordzie — albo
   *  odpowiedni sygnał bazowy sekcji 0). */
  private _applyEditedHfHtml(content: string, kind: 'header' | 'footer'): void {
    // Do modelu (i zapisu) idzie CZYSTY HTML — bez edycyjnych wrapperów obrazów
    // (contenteditable/draggable/uchwyty/inline absolut w układzie pasma), jak w body
    // (_serializeSingleEditor). Pozycja kotwicy przeżywa w data-x/y-emu na <img>.
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

  /** Czysty HTML pasma z surowego innerHTML edycji (unwrap wrapperów obrazów +
   *  przywrócenie KONTRAKTOWYCH współrzędnych kształtów ze stasha edycji pasma). */
  private _cleanBandHtml(html: string): string {
    if (!html || (!html.includes('editor-image-wrapper') && !html.includes('data-band-orig-left')
        && !html.includes('shape-resize-handle') && !html.includes('shape-selected')
        && !html.includes('fmt-trailing-br'))) {
      return html;
    }
    const tmp = document.createElement('div');
    tmp.innerHTML = html;
    this._unwrapImageWrappers(tmp);
    // Prezentacyjny znacznik trybu „Pokaż wszystko" — nie może wejść do modelu pasma.
    this._stripFmtTrailingBr(tmp);
    // Kształty przeliczone na układ pasma na czas edycji — do modelu wraca DOKŁADNY
    // oryginał z data-band-orig-* (bez dryfu zaokrągleń przy wielokrotnych edycjach).
    tmp.querySelectorAll<HTMLElement>('[data-band-orig-left]').forEach(el => {
      el.style.left = el.getAttribute('data-band-orig-left') ?? el.style.left;
      el.style.top = el.getAttribute('data-band-orig-top') ?? el.style.top;
      el.removeAttribute('data-band-orig-left');
      el.removeAttribute('data-band-orig-top');
    });
    return tmp.innerHTML;
  }

  /**
   * Tryb EDYCJI pasma: kotwiczone kształty (div.docx-shape/.docx-textbox z inline absolutem)
   * niosą współrzędne KONTRAKTU (X od lewej krawędzi strony, Y od góry obszaru treści) —
   * kontener pasma ma inny origin, więc bez przeliczenia logo „znikało" (np. top:927px
   * wypadał daleko poza pasmem). Przeliczenie jak w wyświetlaniu (_positionBandAnchors),
   * ale z zapamiętaniem oryginału w data-band-orig-* — commit (_cleanBandHtml) przywraca
   * kontrakt 1:1, więc model i zapis DOCX pozostają nietknięte.
   */
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

  /**
   * Obsługa blur stopki
   */
  onFooterBlur(): void {
    const content = this.footerContentEl?.nativeElement?.innerHTML || '';
    this._applyEditedFooterHtml(content);
    this.emitHeaderFooterChanges();
  }

  /**
   * Obsługa input stopki — emituje zmiany do parent
   */
  onFooterInput(event: Event): void {
    const content = (event.target as HTMLDivElement).innerHTML;
    this._applyEditedFooterHtml(content);
    this.invalidateHeaderFooterCache();
    this.emitHeaderFooterChanges();
  }

  /**
   * Strona otwierająca sekcję (pierwsza strona CAŁEGO dokumentu albo strona, na której
   * indeks sekcji różni się od poprzedniej strony). Wariant first (titlePg) dotyczy
   * pierwszej strony KAŻDEJ sekcji, nie tylko strony 0 dokumentu.
   */
  private _isSectionFirstPage(pageIndex: number): boolean {
    if (pageIndex === 0) return true;
    const sections = this.pageSectionIndexes();
    const s = sections[pageIndex];
    return s !== undefined && s !== sections[pageIndex - 1];
  }

  private _bandContent(entry: SectionHeaderFooter, kind: 'header' | 'footer'): HeaderFooterContent | undefined {
    return kind === 'header' ? entry.header : entry.footer;
  }

  /**
   * Rozwiązuje wariant nagłówka/stopki dla strony w semantyce DOCX:
   * 1. titlePg NIE dziedziczy się między sekcjami (sekcja bez własnego w:titlePg ma je
   *    WYŁĄCZONE, a reader emituje wpis dla każdej sekcji ≥ 1 z aktywnym titlePg — także
   *    bez własnych partów), więc flaga first pochodzi WYŁĄCZNIE z własnego wpisu sekcji
   *    strony (sekcja 0 = flagi bazowe pasma). evenAndOddHeaders jest globalne
   *    (settings.xml) — dziedziczy z najbliższego wpisu/bazy.
   * 2. Wariant: first na pierwszej stronie sekcji (wygrywa z even), even na stronach
   *    parzystych przy evenAndOddHeaders, inaczej default.
   * 3. Treść: najbliższy wpis, który DEFINIUJE wariant; null/'' (dla default) = dziedziczenie
   *    z wcześniejszej sekcji jak w Wordzie, ostatecznie sygnały bazowe. Pusty string
   *    zdefiniowanego wariantu first/even = celowo puste pasmo (NIE fallback do default).
   */
  private _resolveHfVariant(pageIndex: number, kind: 'header' | 'footer'): {
    variant: 'default' | 'first' | 'even';
    ownerEntry: SectionHeaderFooter | null;
    html: string;
  } {
    const pageSection = this.pageSectionIndexes()[pageIndex] ?? 0;
    const candidates = this._sectionHF()
      .filter(e => e.sectionIndex <= pageSection && this._bandContent(e, kind))
      .sort((a, b) => b.sectionIndex - a.sectionIndex);

    // Branie flagi first z POPRZEDNIEJ sekcji pokazywało nagłówek pierwszej strony także
    // na pierwszej stronie sekcji dziedziczącej (str. 2 dokumentu z przerwą sekcji po
    // stronie 1 — zgłoszenie „first widoczny na stronach 1 i 2").
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

  /**
   * Wylicza zawartość nagłówka dla danej strony (używane wewnętrznie przez computed)
   */
  private _computeHeaderContent(pageIndex: number): string {
    return this._substitutePageFields(this._resolveHfVariant(pageIndex, 'header').html, pageIndex);
  }

  private _substitutePageFields(html: string, pageIndex: number): string {
    return html
      .replace(/\{page\}/gi, String(pageIndex + 1))
      .replace(/\{pages\}/gi, String(this.pageContents().length));
  }

  /**
   * Pobiera zawartość nagłówka dla danej strony (używane w szablonie)
   */
  getHeaderContent(pageIndex: number): string {
    const contents = this.headerContents();
    return contents[pageIndex] ?? this._headerHtml();
  }

  /**
   * Wylicza zawartość stopki dla danej strony (używane wewnętrznie przez computed)
   */
  private _computeFooterContent(pageIndex: number): string {
    let content = this._resolveHfVariant(pageIndex, 'footer').html;
    // Zamień placeholder na numer strony
    content = content.replace(/\{page\}/gi, String(pageIndex + 1));
    content = content.replace(/\{pages\}/gi, String(this.pageContents().length));
    return content;
  }

  /**
   * Pobiera zawartość stopki dla danej strony (używane w szablonie)
   */
  getFooterContent(pageIndex: number): string {
    const contents = this.footerContents();
    return contents[pageIndex] ?? this._footerHtml();
  }

  /**
   * Oblicza dostępną wysokość dla treści głównej (bez nagłówka i stopki)
   */
  getContentAreaHeight(): number {
    const pageHeight = this.baseGeometry().heightCm * CSS_PX_PER_CM;
    const headerHeightPx = this._headerHeight() * CSS_PX_PER_CM;
    const footerHeightPx = this._footerHeight() * CSS_PX_PER_CM;
    return pageHeight - headerHeightPx - footerHeightPx;
  }

  /**
   * Ustawia wysokość nagłówka
   */
  setHeaderHeight(heightCm: number): void {
    this._headerHeight.set(Math.max(0.5, Math.min(5, heightCm)));
    this.headerChange.emit({
      html: this._headerHtml(),
      height: this._headerHeight()
    });
  }

  /**
   * Ustawia wysokość stopki
   */
  setFooterHeight(heightCm: number): void {
    this._footerHeight.set(Math.max(0.5, Math.min(5, heightCm)));
    this.footerChange.emit({
      html: this._footerHtml(),
      height: this._footerHeight()
    });
  }

  /**
   * Pobiera pełną zawartość dokumentu z nagłówkiem i stopką
   */
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

  // ================================
  // Menu Opcji Nagłówka/Stopki (styl Google Docs)
  // ================================

  /**
   * Zapobiega utracie zaznaczenia w nagłówku/stopce przy klikaniu w pasek narzędzi.
   * Pozwala na fokus tylko dla pól input/select (np. checkbox "Inna pierwsza strona").
   */
  onHeaderFooterToolbarMouseDown(event: MouseEvent): void {
    const target = event.target as HTMLElement;
    if (target.tagName !== 'INPUT' && target.tagName !== 'SELECT' && target.tagName !== 'TEXTAREA') {
      event.preventDefault();
    }
  }

  /**
   * Toggle menu opcji nagłówka
   */
  toggleHeaderOptionsMenu(event: Event): void {
    event.stopPropagation();
    this.showHeaderOptionsMenu.update(v => !v);
    this.showFooterOptionsMenu.set(false);
    
    if (this.showHeaderOptionsMenu()) {
      // Zamknij menu po kliknięciu poza nim
      setTimeout(() => {
        const closeHandler = () => {
          this.showHeaderOptionsMenu.set(false);
          document.removeEventListener('click', closeHandler);
        };
        document.addEventListener('click', closeHandler);
      }, 0);
    }
  }

  /**
   * Toggle menu opcji stopki
   */
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

  /**
   * Toggle "Inna pierwsza strona"
   */
  toggleDifferentFirstPage(): void {
    // Word's "Different first page" checkbox is a SECTION property shared by the header
    // and footer bands — toggling flips both flags to the same new value.
    const next = !this.differentFirstPage();
    this._headerDifferentFirstPage.set(next);
    this._footerDifferentFirstPage.set(next);
    this.invalidateHeaderFooterCache();
    this.emitHeaderFooterChanges();
  }

  /**
   * Otwiera dialog formatu nagłówka
   */
  openHeaderFormatDialog(): void {
    this.showHeaderOptionsMenu.set(false);
    this.openHeaderFooterFormatDialog();
  }

  /**
   * Otwiera dialog formatu stopki
   */
  openFooterFormatDialog(): void {
    this.showFooterOptionsMenu.set(false);
    this.openHeaderFooterFormatDialog();
  }

  /**
   * Otwiera dialog formatowania nagłówka i stopki
   */
  openHeaderFooterFormatDialog(): void {
    // Emituj event do rodzica - dialog zostanie wyświetlony w document-editor
    this.openHeaderFooterSettings.emit({
      headerMargin: this._headerHeight(),
      footerMargin: this._footerHeight(),
      differentFirstPage: this.differentFirstPage(),
      differentOddEven: this.differentOddEven()
    });
  }

  /**
   * Aplikuje ustawienia nagłówka/stopki z zewnątrz (z document-editor)
   */
  applyHeaderFooterSettings(settings: {
    headerMargin: number;
    footerMargin: number;
    differentFirstPage: boolean;
    differentOddEven: boolean;
  }): void {
    this._headerHeight.set(settings.headerMargin);
    this._footerHeight.set(settings.footerMargin);
    // Dialog exposes the section-wide setting — apply to both bands (like Word).
    this._headerDifferentFirstPage.set(settings.differentFirstPage);
    this._footerDifferentFirstPage.set(settings.differentFirstPage);
    this._headerDifferentOddEven.set(settings.differentOddEven);
    this._footerDifferentOddEven.set(settings.differentOddEven);
    this.invalidateHeaderFooterCache();
    this.emitHeaderFooterChanges();
  }

  /**
   * Otwiera file picker i wstawia wybrany obrazek do aktualnie edytowanej
   * sekcji (header/footer/body). Używane przez menu opcji header/footer.
   */
  insertImageIntoActive(): void {
    this.showHeaderOptionsMenu.set(false);
    this.showFooterOptionsMenu.set(false);

    // Upewnij się że focus jest w aktywnym edytorze przed otwarciem dialogu
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
        // Przywróć focus i selekcję — file dialog je gubi
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

  /**
   * Wstawia numer strony do nagłówka
   */
  insertPageNumbers(): void {
    this.showHeaderOptionsMenu.set(false);
    const el = this.headerContentEl?.nativeElement;
    if (!el) return;
    el.focus();
    document.execCommand('insertHTML', false, this._pageNumberHtml(el));
    this.onHeaderInput({ target: el } as any);
  }

  /**
   * Wstawia numer strony do stopki
   */
  insertPageNumbersFooter(): void {
    this.showFooterOptionsMenu.set(false);
    const el = this.footerContentEl?.nativeElement;
    if (!el) return;
    el.focus();
    document.execCommand('insertHTML', false, this._pageNumberHtml(el));
    this.onFooterInput({ target: el } as any);
  }

  /**
   * Buduje znacznik numeru strony dziedziczący rozmiar czcionki z treści nagłówka/stopki, zamiast
   * domyślnego rozmiaru edytora. Inaczej numer strony bywa większy niż reszta stopki (np. 10.5pt
   * kontenera vs 8pt runów stopki). Bez własnego rozmiaru ".page-number" dziedziczy przez CSS.
   */
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

  /**
   * Usuwa nagłówek
   */
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

  /**
   * Usuwa stopkę
   */
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

  /**
   * Emituje zmiany nagłówka i stopki
   */
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

  /**
   * Zaznacza WYŁĄCZNIE treść dokumentu (edytory stron), a nie całą stronę przeglądarki.
   * `document.execCommand('selectAll')` w trybie read-only (brak fokusu w contenteditable)
   * zaznaczał całe `body` — łącznie z menu, toolbarem i paskiem statusu. Tutaj tworzymy
   * jeden ciągły zakres od początku pierwszego do końca ostatniego edytora strony.
   */
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

  /**
   * Zwraca faktyczny układ stron (zmierzony z DOM, w px ze skalą zoomu): wysokość każdej
   * kartki i odstęp do następnej (separator). Strona z wysokim nagłówkiem rośnie ponad
   * min-height 1122px, więc do zrównania pionowej linijki per-strona potrzebne są realne
   * wymiary, nie stałe 1122.
   */
  getPageLayout(): { top: number; height: number }[] {
    const refs = this.pageEditorRefs?.toArray() ?? [];
    const pages = refs
      .map(r => r.nativeElement.closest('.page') as HTMLElement | null)
      .filter((p): p is HTMLElement => !!p);
    if (pages.length === 0) return [];
    const scrollEl = pages[0].closest('.editor-scroll-container') as HTMLElement | null;
    if (!scrollEl) return [];
    // Pozycja Y odpowiadająca offsetowi 0 w zawartości scrolla (niezależna od przewinięcia).
    const base = scrollEl.getBoundingClientRect().top - scrollEl.scrollTop;
    return pages.map(p => {
      const r = p.getBoundingClientRect();
      return { top: r.top - base, height: r.height };
    });
  }

  // ========== Wyszukiwanie i zamiana ==========

  private searchHighlights: HTMLElement[] = [];
  private currentHighlightIndex = -1;

  /**
   * Wyszukuje tekst w dokumencie i podświetla wyniki
   */
  searchText(text: string, direction: 'next' | 'previous'): { count: number; currentIndex: number } {
    this.clearSearchHighlights();

    // Przeszukujemy WSZYSTKIE strony (nie tylko aktywną) — w kolejności dokumentu.
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

    // Podświetl wszystkie wyniki (od końca żeby nie psuć indeksów)
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
        // Jeśli range obejmuje wiele elementów, pomiń
      }
    }

    // Ustaw aktualny indeks
    if (this.searchHighlights.length > 0) {
      this.currentHighlightIndex = 0;
      if (direction === 'previous') {
        this.currentHighlightIndex = this.searchHighlights.length - 1;
      }
      this.highlightCurrent();
    }

    return { count: this.searchHighlights.length, currentIndex: this.currentHighlightIndex };
  }

  /**
   * Zwraca listę wyników wyszukiwania jako fragmenty tekstu (kontekst wokół trafienia)
   * — do panelu „Wyszukiwanie". Kolejność zgodna z `searchHighlights` (kolejność dokumentu).
   */
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

  /**
   * Skacze do wyniku o danym indeksie (klik na liście w panelu) — podświetla i przewija.
   */
  goToMatch(index: number): { count: number; currentIndex: number } {
    if (index < 0 || index >= this.searchHighlights.length) {
      return { count: this.searchHighlights.length, currentIndex: this.currentHighlightIndex };
    }
    this.currentHighlightIndex = index;
    this.highlightCurrent();
    return { count: this.searchHighlights.length, currentIndex: index };
  }

  /**
   * Przechodzi do następnego wyniku wyszukiwania
   */
  findNext(): { count: number; currentIndex: number } {
    if (this.searchHighlights.length === 0) return { count: 0, currentIndex: -1 };
    this.currentHighlightIndex = (this.currentHighlightIndex + 1) % this.searchHighlights.length;
    this.highlightCurrent();
    return { count: this.searchHighlights.length, currentIndex: this.currentHighlightIndex };
  }

  /**
   * Przechodzi do poprzedniego wyniku wyszukiwania
   */
  findPrevious(): { count: number; currentIndex: number } {
    if (this.searchHighlights.length === 0) return { count: 0, currentIndex: -1 };
    this.currentHighlightIndex = (this.currentHighlightIndex - 1 + this.searchHighlights.length) % this.searchHighlights.length;
    this.highlightCurrent();
    return { count: this.searchHighlights.length, currentIndex: this.currentHighlightIndex };
  }

  /**
   * Podświetla aktualny wynik wyszukiwania
   */
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

  /**
   * Zamienia bieżący wynik wyszukiwania
   */
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

  /**
   * Zamienia wszystkie wyniki wyszukiwania
   */
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

  /**
   * Czyści podświetlenia wyszukiwania
   */
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
    // Agreguj WSZYSTKIE strony — zamiana może dotknąć innej strony niż aktywna.
    const html = this.getContent();
    if (!html && !this.editorContent?.nativeElement) return;
    this._isInternalUpdate = true;
    this._content.set(html);
    this.contentChange.emit(html);
    this._isInternalUpdate = false;
  }
}
