import {
  Component,
  ViewChild,
  ElementRef,
  inject,
  signal,
  computed,
  HostListener,
  OnInit,
  OnDestroy
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { switchMap, map, filter, distinctUntilChanged } from 'rxjs/operators';
import { from, Observable, Subscription, timer } from 'rxjs';
import { MsalService } from '@azure/msal-angular';
import { AccountInfo } from '@azure/msal-browser';
import { environment } from '../../../environments/environment';
import { WysiwygEditorComponent } from '../wysiwyg-editor/wysiwyg-editor';
import { EditorToolbarComponent } from '../editor-toolbar/editor-toolbar';
import { BarcodeDialogComponent } from '../barcode-dialog/barcode-dialog';
import { RulerComponent, RulerColumnSegment } from '../ruler/ruler';
import { DocumentService, OpenDocumentError } from '../../services/document.service';
import { documentDefectMessage } from '../../core/errors/document-error.util';
import { 
  DocumentContent, 
  DocumentMetadata, 
  EditorState,
  EditorCommand,
  DocumentTemplate,
  PageMargins,
  PageSettings,
  PageSize,
  MARGIN_PRESETS,
  DocumentStyle,
  HeaderFooterContent,
  SectionHeaderFooter,
  DigitalSignatureInfo,
  SignDocumentRequest,
  Footnote,
  Endnote
} from '../../models/document.model';
import { BuildInfoService } from '../../core/services/build-info.service';
import { DocumentNavigationService } from '../../core/services/document-navigation.service';
import { LastHttpErrorService } from '../../core/services/last-http-error.service';
import { NotificationService } from '../../core/services/notification.service';
import { FontProviderService } from '../../services/font-provider.service';
import { CSS_PX_PER_CM } from '../../core/utils/units.util';
import { DocumentStorageService, DeliveryStatus } from '../../services/document-storage.service';
import { DocumentClassificationBadgeComponent } from '../document-classification-badge/document-classification-badge';
import { TablePropertiesPanelComponent } from '../table-properties-panel/table-properties-panel';
import { HeaderFooterPanelComponent } from '../header-footer-panel/header-footer-panel';
import { ImagePropertiesPanelComponent, ImageSelectionState } from '../image-properties-panel/image-properties-panel';
import {
  TableBorderLineStyle,
  TableBorderScope,
  DEFAULT_TABLE_BORDER
} from '../../models/table-style.model';
import {
  applyBorderToCells,
  classifyBorderTarget,
  restoreDefaultTableBorders as restoreDefaultBordersUtil
} from '../../core/utils/table-style.util';
import { resolveTableContext } from '../../core/utils/table-context.util';
import { syncTableColgroup } from '../../core/utils/table-grid.util';
import { isValidReturnUrl } from '../../core/utils/return-url.util';
import {
  applyWordLineSpacing,
  applyExactLineSpacing,
  readWordLineMultiple,
} from '../../core/utils/word-line-spacing.util';

/**
 * Jeden komunikat dla dokumentu oznaczonego jako tylko do odczytu w pliku źródłowym
 * (Word „Ogranicz edycję" / hasło zapisu / „Oznacz jako ostateczny"). Wyświetlany zamiast
 * komunikatu sukcesu — nie sugerujemy, że dokument otwarto w standardowym trybie edycji.
 */
const READ_ONLY_PROTECTED_MESSAGE =
  'Ten dokument jest oznaczony jako tylko do odczytu i nie może być edytowany w DOC2 Editor.';

/**
 * Główny komponent edytora dokumentów Word Online
 */
@Component({
  selector: 'd2-document-editor',
  standalone: true,
  imports: [
    CommonModule,
    FormsModule,
    RouterLink,
    WysiwygEditorComponent,
    EditorToolbarComponent,
    BarcodeDialogComponent,
    RulerComponent,
    DocumentClassificationBadgeComponent,
    TablePropertiesPanelComponent,
    HeaderFooterPanelComponent,
    ImagePropertiesPanelComponent
  ],
  templateUrl: './document-editor.html',
  styleUrl: './document-editor.scss'
})
export class DocumentEditorComponent implements OnInit, OnDestroy {
  @ViewChild(WysiwygEditorComponent) editor!: WysiwygEditorComponent;
  @ViewChild(EditorToolbarComponent) toolbar!: EditorToolbarComponent;
  @ViewChild('verticalRulerBar') verticalRulerBar?: ElementRef<HTMLDivElement>;
  @ViewChild('horizontalRulerInner') horizontalRulerInner?: ElementRef<HTMLDivElement>;
  @ViewChild('editorScrollContainer') editorScrollContainer?: ElementRef<HTMLDivElement>;

  private documentService = inject(DocumentService);
  private documentStorageService = inject(DocumentStorageService);
  private route = inject(ActivatedRoute);
  private router = inject(Router);
  private documentNavigation = inject(DocumentNavigationService);
  readonly buildInfo = inject(BuildInfoService);
  private readonly lastHttpError = inject(LastHttpErrorService);
  private readonly notification = inject(NotificationService);
  private readonly msal = inject(MsalService);
  private readonly fontProvider = inject(FontProviderService);

  /** Pełna nazwa zalogowanego użytkownika (MSAL active account). Pusta w trybie bez auth
   *  (dev bypass) → powitanie ukrywane w szablonie. */
  readonly currentUserName = signal<string>('');
  /** Imię do powitania „Witaj, <imię>!" — nawias z `name` („… (Imię)"), potem `given_name`, fallback. */
  readonly firstName = signal<string>('');
  /** Inicjały — fallback awatara, gdy brak zdjęcia z Graph. */
  readonly initials = signal<string>('');
  /** URL awatara z Microsoft Graph (object URL). Null → pokazujemy inicjały. */
  readonly avatarUrl = signal<string | null>(null);
  /** Object URL awatara do zwolnienia przy destroy (uniknięcie wycieku pamięci). */
  private avatarObjectUrl: string | null = null;

  // Stan dokumentu
  documentContent = signal<string>('<p></p>');
  documentMasterId = signal<string | null>(null);
  /** GUID wersji edytowalnej (v2) — obecny tylko w trybie edycji (?versionId=...). Cel auto-save. */
  documentVersionId = signal<string | null>(null);
  /** Tryb tylko-do-odczytu (Krok 2): brak versionId → ładujemy wersję bazową i blokujemy edycję. */
  readOnly = signal<boolean>(false);

  /**
   * Dokument jest aktualnie edytowany przez kogoś innego (status `Editing` na liście).
   * HOOK: podłączyć pod backendowy `DocumentStatus`, gdy dotrze do edytora — wtedy ustawić
   * `true`, by ukryć narzędzia edycyjne tak samo jak w trybie tylko-do-odczytu.
   */
  lockedByOther = signal<boolean>(false);

  /**
   * Dokument źródłowy jest chroniony przed edycją (Word: „Ogranicz edycję" / hasło zapisu;
   * settings.xml: wymuszone w:documentProtection lub w:writeProtection). Ustawiane z flagi
   * `isReadOnlyProtected` konwersji przy KAŻDYM załadowaniu treści — resetuje się samo
   * przy otwarciu kolejnego, niechronionego dokumentu.
   */
  documentEditProtected = signal<boolean>(false);

  /**
   * Edycja zablokowana: tryb tylko-do-odczytu, dokument zajęty przez kogoś innego
   * LUB dokument chroniony przed edycją w pliku źródłowym.
   * Steruje ukrywaniem edycyjnych funkcji w toolbarze i menu.
   */
  editingDisabled = computed(() => this.readOnly() || this.lockedByOther() || this.documentEditProtected());

  // Auto-save (nadpisuje wersję edytowalną w miejscu)
  autoSaveEnabled = signal<boolean>(environment.autoSave?.enabled ?? true);
  /** Status ostatniego auto-save dla wskaźnika w UI. */
  autoSaveStatus = signal<'idle' | 'saving' | 'saved' | 'error'>('idle');
  lastAutoSaveAt = signal<Date | null>(null);
  private autoSaveSub?: Subscription;
  private isAutoSaving = false;

  // Klasyfikacja dokumentu z metadanych (prezentacyjna; C1..C4 lub wartość spoza słownika)
  documentClassification = signal<string | null>(null);

  // Zakończ i wyślij: synchroniczna pierwsza próba wysyłki na returnUrl. Sukces → „Wysłano";
  // błąd → wybór „Przerwij" / „Kontynuuj wysyłkę w tle" (worker dokańcza z retry/backoff).
  isFinishing = signal<boolean>(false);
  deliveryStatus = signal<DeliveryStatus | null>(null);
  deliveryId = signal<string | null>(null);
  /** Modal „Trwa wysyłanie dokumentu ..." — widoczny w trakcie pierwszej próby wysyłki. */
  showSendingModal = signal<boolean>(false);
  /**
   * Modal „Wystąpiły problemy z dostarczeniem dokumentu" — pokazywany po nieudanej pierwszej
   * próbie. Daje wybór: „Przerwij" (powrót do edytora) lub „Kontynuuj wysyłkę w tle".
   */
  showSendErrorModal = signal<boolean>(false);
  /**
   * Użytkownik przerwał trwającą wysyłkę („Przerwij wysyłkę"). To anulowanie, NIE błąd —
   * po przerwaniu requestu jego ewentualny błąd nie może wyskoczyć jako modal/komunikat błędu.
   */
  private sendCancelled = signal<boolean>(false);
  /**
   * Użytkownik zamknął okno trwającej wysyłki („Zamknij"). Wysyłka biegnie dalej (inline/worker),
   * a UI jest odpięte — spóźniony wynik lub błąd requestu nie pokazuje już żadnego modalu.
   */
  private sendDetachedFromUi = signal<boolean>(false);
  /**
   * Praca nad dokumentem zakończona (po „Zamknij"/odliczaniu). Gdy przeglądarka nie pozwoli
   * zamknąć karty (window.close() działa tylko dla okien otwartych skryptem), wyświetlamy
   * blokujący ekran końcowy — użytkownik NIE może dalej edytować zakończonego dokumentu.
   */
  workFinished = signal<boolean>(false);
  /**
   * Próba `window.close()` nie zamknęła karty (Chrome blokuje zamykanie kart nieotwartych skryptem)
   * → pokazujemy wskazówkę o ręcznym zamknięciu (Ctrl/⌘+W).
   */
  tabCloseBlocked = signal<boolean>(false);
  private tabCloseHintTimer?: ReturnType<typeof setTimeout>;
  /** Link zwrotny z metadanych dokumentu (źródło prawdy dla widoczności „Zakończ"). */
  returnUrl = signal<string | null>(null);
  /** „Zakończ" ma sens tylko, gdy istnieje poprawny link do zwrócenia pliku po edycji. */
  readonly canFinish = computed(() => isValidReturnUrl(this.returnUrl()));

  /**
   * Mirror of documents.metadata.userDownload (default false). Drives the visibility
   * of the "Pobierz dokument" menu item — backend additionally enforces on the
   * /user-download endpoint, so flipping the signal in DevTools doesn't bypass the rule.
   */
  userDownload = signal<boolean>(false);
  readonly canUserDownload = computed(() => this.userDownload());

  /**
   * True when the current document was opened from the local disk ("Plik → Otwórz").
   * Together with userDownload it drives the "Pobierz oryginał dokumentu" menu item.
   */
  loadedFromDisk = signal<boolean>(false);
  /** Original file kept in memory for disk-loaded documents, so we can hand back the untouched original. */
  private diskOriginalFile: File | null = null;

  /**
   * "Pobierz oryginał dokumentu" is available when the file was loaded from disk OR the source
   * app explicitly allowed downloads (userDownload === true). Always returns the original (v1),
   * never the edited working copy.
   */
  readonly canDownloadOriginal = computed(() => this.loadedFromDisk() || this.userDownload());

  /**
   * Mirror of documents.metadata.showSaveState (inverse-default true). When false, the
   * source app asked to hide the editor's save-state UI: the autosave switch + footer
   * status AND the manual "Zapisz" button. Missing metadata ⇒ true (unchanged behavior).
   */
  showSaveState = signal<boolean>(true);
  /** Subskrypcja łańcucha save→finishAndSend — anulowana przy destroy, by nie pisać po zniszczeniu. */
  private finishSendSub?: Subscription;
  documentMetadata = signal<DocumentMetadata>({
    title: 'Nowy dokument',
    created: new Date().toISOString(),
    modified: new Date().toISOString()
  });
  documentStyles = signal<DocumentStyle[]>([]);
  originalFileName = signal<string>('');
  
  // Nagłówek i stopka
  headerContent = signal<HeaderFooterContent>({ html: '', height: 1.25 });
  footerContent = signal<HeaderFooterContent>({ html: '', height: 1.25 });
  
  // Stan edytora
  editorState = signal<EditorState | null>(null);
  
  // Stan UI
  isLoading = signal(false);
  showMenu = signal(false);
  showEditMenu = signal(false);
  showFormatMenu = signal(false);
  showInsertMenu = signal(false);
  activeSubmenu = signal<string | null>(null);
  showTemplates = signal(false);
  showFindReplace = signal(false);
  showBarcodeDialog = signal(false);
  errorMessage = signal<string | null>(null);
  successMessage = signal<string | null>(null);
  /** Komunikat INFORMACYJNY (np. dokument tylko do odczytu) — stan, nie błąd (13865553). */
  infoMessage = signal<string | null>(null);
  documentNotFound = signal(false);

  // Menu kontekstowe
  showContextMenu = signal(false);
  contextMenuX = signal(0);
  contextMenuY = signal(0);
  contextSubmenu = signal<string | null>(null);
  contextMenuTargetCell = signal<HTMLElement | null>(null);
  contextMenuTargetImage = signal<HTMLImageElement | null>(null);
  /**
   * Widoczność „rozszerzonych" pozycji menu kontekstowego (wyrównanie/interlinia/wcięcia,
   * sekcje Tabela i Grafika). Na życzenie menu pokazuje tylko podstawowe operacje:
   * Cofnij, Ponów, Wytnij, Kopiuj, Wklej, Wklej bez formatowania, Zaznacz wszystko.
   * Markup pozostaje w szablonie (ukryty, nie usunięty) — wystarczy ustawić `true`, aby przywrócić.
   */
  contextMenuExtrasVisible = signal(false);

  // Mini toolbar nad zaznaczeniem
  showMiniToolbar = signal(false);
  miniToolbarX = signal(0);
  miniToolbarY = signal(0);

  /** Shared font list — identical to the main toolbar, incl. corporate font (item 7). */
  readonly commonFonts = this.fontProvider.displayNames;

  /** Font shown in the mini-toolbar — normalised via the shared provider so it
   *  matches exactly what the main toolbar shows for the same selection. */
  readonly miniToolbarFontFamily = computed(() =>
    this.fontProvider.normalize(this.editorState()?.currentStyle?.fontFamily),
  );

  /**
   * Options for the mini-toolbar font `<select>`. A native `<select>` cannot
   * display a value that has no matching `<option>`, so it silently falls back
   * to the first option (Calibri) whenever the selection uses a font outside the
   * shared list — e.g. a corporate/document font like "Qutas Me". The main toolbar
   * avoids this by using an `<input list=…>`; here we keep the `<select>` but make
   * the current font always selectable, so the control reflects the real
   * selection instead of misreporting Calibri.
   */
  readonly miniToolbarFontOptions = computed<readonly string[]>(() => {
    const current = this.miniToolbarFontFamily();
    const fonts = this.commonFonts();
    if (current && !fonts.some((f) => f.toLowerCase() === current.toLowerCase())) {
      return [current, ...fonts];
    }
    return fonts;
  });

  // Menu Narzędzia
  showToolsMenu = signal(false);

  // Dialog Akapit
  showParagraphDialog = signal(false);
  paragraphDialogTab = signal<'indents' | 'breaks'>('indents');
  paragraphData = {
    alignment: 'left' as string,
    outlineLevel: 'body' as string,
    indentLeft: 0,
    indentRight: 0,
    specialIndent: 'none' as string,
    specialIndentBy: 1.27,
    mirrorIndents: false,
    spaceBefore: 0,
    spaceAfter: 8,
    lineSpacingType: 'multiple' as string,
    lineSpacingValue: 1.08,
    dontAddSpaceBetweenSameStyle: false,
    widowOrphanControl: true,
    keepWithNext: false,
    keepLinesTogether: false,
    pageBreakBefore: false
  };

  /**
   * Domyślne ustawienia akapitu zapisane przez „Ustaw jako domyślne".
   * Model: default jest per-sesja edytora (resetuje się po odświeżeniu) — przechowuje
   * snapshot ustawień, którym seedujemy dialog, gdy nie ma aktywnej selekcji.
   * Inicjowane wartościami bazowymi (te same co startowe `paragraphData`).
   */
  private _paragraphDefaults: typeof DocumentEditorComponent.prototype.paragraphData = {
    alignment: 'left',
    outlineLevel: 'body',
    indentLeft: 0,
    indentRight: 0,
    specialIndent: 'none',
    specialIndentBy: 1.27,
    mirrorIndents: false,
    spaceBefore: 0,
    spaceAfter: 8,
    lineSpacingType: 'multiple',
    lineSpacingValue: 1.08,
    dontAddSpaceBetweenSameStyle: false,
    widowOrphanControl: true,
    keepWithNext: false,
    keepLinesTogether: false,
    pageBreakBefore: false
  };

  // Dialog Wstawianie tabeli
  showInsertTableDialog = signal(false);
  tableDialogData = {
    columns: 5,
    rows: 2,
    autoFitBehavior: 'fixed' as string,
    fixedWidth: 0, // 0 = Auto
    rememberDimensions: false
  };
  private savedTableDimensions: { columns: number; rows: number } | null = null;
  
  // Szablony
  templates = signal<DocumentTemplate[]>([]);

  // Zoom
  zoomLevel = signal(100);
  zoomLevels = [50, 75, 100, 125, 150, 200];

  // Toolbar tabeli
  isInTable = signal(false);
  activeTableCell = signal<HTMLTableCellElement | null>(null);
  activeTable = signal<HTMLTableElement | null>(null);

  /**
   * Boczny panel konfiguracji tabeli (dokowany po lewej, jak panel „Wyszukiwanie").
   * Otwiera się automatycznie po wejściu karetki w tabelę — chyba że użytkownik
   * zamknął go ręcznie (`tablePanelManuallyClosed`). Dok ma jeden aktywny tryb na
   * raz: ponowne zaznaczenie tabeli wypiera otwarte wyszukiwanie (zamyka je) i
   * pokazuje formatowanie tabeli — patrz `syncTablePanel`.
   */
  showTablePanel = signal(false);
  private tablePanelManuallyClosed = false;

  // Obramowania tabeli: bieżące ustawienia „pióra" (rodzaj/grubość/kolor),
  // cel zastosowania i ostatnio użyty zakres — do podświetlenia stanu w panelu.
  tableBorderColor = signal(DEFAULT_TABLE_BORDER.color);
  tableBorderWidth = signal(DEFAULT_TABLE_BORDER.width);
  tableBorderStyle = signal<TableBorderLineStyle>(DEFAULT_TABLE_BORDER.style);
  lastBorderScope = signal<TableBorderScope | null>(null);

  /**
   * Auto-zakres: opis celu wywnioskowany z bieżącego zaznaczenia (do podpisu
   * „Zastosowanie: …" w panelu). Zależy od `selectedCells`/`activeTableCell`,
   * więc aktualizuje się wraz ze zmianą zaznaczenia.
   */
  borderTargetInfo = computed(() => {
    const table = this.activeTable();
    if (!table) return null;
    return classifyBorderTarget(table, this.resolveAutoTargetCells(table));
  });

  // Zaznaczanie komórek tabeli (custom cell selection)
  selectedCells = signal<Set<HTMLTableCellElement>>(new Set());
  private cellSelectionStartCell: HTMLTableCellElement | null = null;
  private isCellSelecting = false;

  // Cieniowanie (shading) dropdown w toolbarze tabeli
  showShadingDropdown = signal(false);

  // Strony
  currentPage = signal(1);
  totalPages = signal(1);
  
  // Tooltip ze stronami przy scrollowaniu
  showPageIndicator = signal(false);
  private pageIndicatorTimeout?: ReturnType<typeof setTimeout>;

  // Ustawienia strony
  showPageSetup = signal(false);
  // Domyślnie ukryte — użytkownik może je pokazać przez menu Widok lub dialog Ustawienia strony.
  showMarginGuides = signal(false);
  showRuler = signal(true);

  /**
   * Wcięcie paragrafu/bloku dla aktualnie zaznaczonego fragmentu (cm względem marginesu strony).
   * Wczytywane przy każdej zmianie zaznaczenia z `margin-left`/`margin-right` bieżącego bloku
   * (P/H/UL/OL/LI/TABLE/FIGURE/IMG-wrapper). Używane przez poziomą linijkę — dragowanie
   * uchwytu modyfikuje TYLKO ten blok, jak w MS Word, a nie marginesy całego dokumentu.
   */
  currentBlockIndent = signal<{ start: number; end: number }>({ start: 0, end: 0 });

  /**
   * Geometria kolumn sekcji, w której stoi kursor — dla poziomej linijki (jak w MS Word:
   * linijka pokazuje obszar per kolumna, a uchwyty wcięć działają w kolumnie z kursorem).
   * Wyliczana z DOM przy każdej zmianie zaznaczenia: kontenerem multicol jest
   * `.editor-content` (kolumny całej strony) lub `.docx-col-band` (pasmo continuous
   * w środku strony). Null = układ jednokolumnowy.
   */
  currentColumnRuler = signal<{ segments: RulerColumnSegment[]; activeIndex: number } | null>(null);

  /**
   * Stan linii prowadzącej linijki (jak w MS Word). Renderowana nad kartką podczas
   * przeciągania uchwytu — w samej linijce (overflow:hidden, 22px) byłaby przycięta.
   * `offsetPx` to NIEZSKALOWANA odległość od krawędzi strony (lewej/górnej).
   */
  rulerGuide = signal<{ active: boolean; axis: 'horizontal' | 'vertical'; offsetPx: number }>({
    active: false,
    axis: 'horizontal',
    offsetPx: 0
  });

  /** Aktualnie edytowana sekcja (treść / nagłówek / stopka) — z d2-wysiwyg-editor. */
  editingSection = signal<'header' | 'footer' | 'body'>('body');

  /**
   * Header/footer side panel — replaces the old floating toolbar over the page.
   * Mutually exclusive with the Find / Table panels (single docked mode); when
   * Find or the Table panel is open it takes precedence and HF stays hidden but
   * the underlying edit mode keeps running so re-closing them brings it back.
   */
  showHeaderFooterPanel = computed(() =>
    this.editingSection() !== 'body'
    && !this.showFindReplace()
    && !this.showTablePanel()
    && !this.showImagePanel()
  );

  /**
   * Snapshot of the currently selected image (or null). Owned by the editor template
   * because the panel that consumes it lives in this component's view.
   */
  selectedImage = signal<ImageSelectionState | null>(null);
  imageLockAspect = signal<boolean>(true);

  /**
   * Image properties panel — single-mode dock. Shown when an image is selected and no
   * higher-priority panel is open. The header/footer panel yields to it so the user can
   * resize a logo while still in header-edit mode.
   */
  showImagePanel = computed(() =>
    this.selectedImage() !== null
    && !this.showFindReplace()
    && !this.showTablePanel()
  );

  onImageSelectionChange(state: ImageSelectionState | null): void {
    this.selectedImage.set(state);
  }

  /**
   * ZMIERZONA geometria edytowanego pasma nagłówka/stopki (cm od górnej krawędzi strony),
   * z d2-wysiwyg-editor. Pasmo rośnie z treścią (min-height + obraz), więc położenie na
   * pionowej linijce musi pochodzić z pomiaru DOM, nie z marginesów cm.
   */
  sectionGeometry = signal<{ section: 'header' | 'footer'; topCm: number; bottomCm: number } | null>(null);

  /**
   * Marginesy dla PIONOWEJ linijki. Gdy edytowany jest nagłówek/stopka, biały (aktywny)
   * obszar linijki odzwierciedla FAKTYCZNE pasmo nagłówka/stopki (z pomiaru DOM) — jak
   * w MS Word — zamiast globalnego marginesu treści.
   */
  verticalRulerMargins = computed<PageMargins>(() => {
    const m = this.pageSettings().margins;
    const pageH = this.pageSettings().orientation === 'portrait' ? 29.7 : 21;
    const section = this.editingSection();
    const geo = this.sectionGeometry();
    if ((section === 'header' || section === 'footer') && geo && geo.section === section) {
      // białe pasmo linijki = [topCm … bottomCm], reszta = szary margines
      return { ...m, top: Math.max(0, geo.topCm), bottom: Math.max(0, pageH - geo.bottomCm) };
    }
    return m;
  });

  /**
   * Lista indeksów stron — do renderowania OSOBNEJ pionowej linijki dla każdej strony,
   * żeby przy przewijaniu linijka restartowała się na granicy kartek (zamiast jednej
   * „zamrożonej"). Pasek linijki scrolluje 1:1 ze scroll-containerem, a segmenty mają tę
   * samą geometrię co kartki (wysokość strony + separator), więc idealnie się pokrywają.
   */
  pageList = computed(() => Array.from({ length: this.totalPages() }, (_, i) => i));

  /** Wysokość segmentu pionowej linijki = wysokość kartki (px @100%) * zoom (fallback). */
  vRulerSegmentHeightPx = computed(() =>
    (this.pageSettings().orientation === 'portrait' ? 1122 : 794) * (this.zoomLevel() / 100)
  );

  /** Odstęp między segmentami = wysokość separatora stron (8px) * zoom (fallback). */
  vRulerGapPx = computed(() => 8 * (this.zoomLevel() / 100));

  /**
   * Zmierzony z DOM układ stron (top + wysokość, w px ze skalą, względem zawartości scrolla).
   * Strony z wysokim nagłówkiem rosną ponad 1122px — pozycjonowanie absolutne wg zmierzonego
   * `top` eliminuje kumulację błędów i daje wierne wyrównanie pionowej linijki per strona.
   */
  vRulerSegments = signal<{ top: number; height: number }[]>([]);

  /**
   * Segmenty do renderu: zmierzone jeśli dostępne, inaczej fallback ze stałych.
   * `axisCm` = wysokość kartki w cm (niezależna od zoomu), by linijka wypełniła podziałką
   * całą stronę (strona z wysokim nagłówkiem bywa wyższa niż A4).
   */
  vRulerSegmentsView = computed(() => {
    const scale = this.zoomLevel() / 100;
    const toCm = (px: number) => px / (DocumentEditorComponent.CM_TO_PX * scale);
    const measured = this.vRulerSegments();
    if (measured.length === this.totalPages() && measured.length > 0) {
      return measured.map(s => ({ ...s, axisCm: toCm(s.height) }));
    }
    const h = this.vRulerSegmentHeightPx();
    const g = this.vRulerGapPx();
    const pad = 20; // padding-top editor-scroll-container
    const fallbackAxis = this.pageSettings().orientation === 'portrait' ? 29.7 : 21;
    return this.pageList().map((_, i) => ({ top: pad + i * (h + g), height: h, axisCm: fallbackAxis }));
  });

  /** Łączna wysokość zawartości paska pionowej linijki (do scrolla 1:1). */
  vRulerInnerHeight = computed(() => {
    const segs = this.vRulerSegmentsView();
    return segs.length ? segs[segs.length - 1].top + segs[segs.length - 1].height : 0;
  });

  /** Obserwator zmian rozmiaru zawartości — re-mierzy układ stron dla pionowej linijki. */
  private vRulerResizeObserver?: ResizeObserver;

  /**
   * Mierzy realny układ stron (top + wysokość każdej kartki) i aktualizuje segmenty linijki.
   * Wołane w `requestAnimationFrame` (po layoutcie), z guardem równości. Reaguje na faktyczne
   * zmiany (ładowanie obrazów, edycja) przez ResizeObserver, więc nie zgaduje momentu pomiaru.
   */
  private measureVRuler(): void {
    requestAnimationFrame(() => {
      const next = this.editor?.getPageLayout() ?? [];
      const cur = this.vRulerSegments();
      const same = cur.length === next.length
        && cur.every((s, i) => Math.abs(s.height - next[i].height) < 0.5 && Math.abs(s.top - next[i].top) < 0.5);
      if (!same) this.vRulerSegments.set(next);
    });
  }

  /** Podpina ResizeObserver na zawartości dokumentu (editor-wrapper), by linijka była zawsze aktualna. */
  private ensureVRulerObserver(): void {
    if (this.vRulerResizeObserver) return;
    const wrapper = this.editorScrollContainer?.nativeElement?.querySelector('.editor-wrapper') as HTMLElement | null;
    if (!wrapper) return;
    this.vRulerResizeObserver = new ResizeObserver(() => this.measureVRuler());
    this.vRulerResizeObserver.observe(wrapper);
  }

  /** Konwersja cm ↔ px (96 DPI). */
  private static readonly CM_TO_PX = CSS_PX_PER_CM;

  // Menu Widok
  showViewMenu = signal(false);
  showHelpMenu = signal(false);
  pageSettings = signal<PageSettings>({
    margins: { top: 2.5, bottom: 2.5, left: 2.5, right: 2.5 },
    orientation: 'portrait',
    paperSize: 'a4'
  });
  /** Page size/orientation (cm) from the imported DOCX; round-tripped verbatim on save. */
  documentPageSize = signal<PageSize | undefined>(undefined);
  /** Własne nagłówki/stopki sekcji ≥ 1 z importu (dokumenty wielosekcyjne, ADR-0023). */
  sectionHeadersFooters = signal<SectionHeaderFooter[] | null>(null);
  /** Przypisy dolne z importu (jedno źródło prawdy treści; round-trip przez zapis). */
  footnotes = signal<Footnote[] | null>(null);
  /** Przypisy końcowe z importu (osobny model; round-trip przez zapis). */
  endnotes = signal<Endnote[] | null>(null);
  /** Format numeracji przypisów z dokumentu (w:numFmt) — tylko wyświetlanie; null = domyślna Worda. */
  footnoteNumberFormat = signal<string | null>(null);
  endnoteNumberFormat = signal<string | null>(null);
  marginPresets = MARGIN_PRESETS;

  // Dialog nagłówka i stopki
  showHeaderFooterDialog = signal(false);
  headerFooterDialogData = signal<{
    headerMargin: number;
    footerMargin: number;
    differentFirstPage: boolean;
    differentOddEven: boolean;
  }>({
    headerMargin: 1.27,
    footerMargin: 1.27,
    differentFirstPage: false,
    differentOddEven: false
  });

  // Dialog Właściwości dokumentu
  showPropertiesDialog = signal(false);
  propertiesData = signal<DocumentMetadata>({});

  // Dialog Podpisów cyfrowych
  showSignatureDialog = signal(false);
  signatureDialogTab = signal<'list' | 'sign'>('list');
  signatureData = {
    signerName: '' as string,
    signerTitle: '' as string,
    signerEmail: '' as string,
    reason: '' as string,
    certificateBase64: '' as string,
    certificatePassword: '' as string,
    certificateFileName: '' as string
  };

  // Baner podpisów
  documentSignatures = signal<DigitalSignatureInfo[]>([]);

  // Dialog opuszczania edytora
  showLeaveDialog = signal(false);

  // Math dla template
  protected readonly Math = Math;

  constructor() {
    // Załaduj szablony
    this.loadTemplates();
  }

  ngOnInit(): void {
    // Czytamy masterId i versionId RAZEM: versionId decyduje o trybie (edycja vs read-only),
    // więc musi być znany zanim zdecydujemy, którą wersję załadować.
    this.route.queryParams.pipe(
      map(params => ({
        masterId: params['masterId'] as string | undefined,
        versionId: params['versionId'] as string | undefined
      })),
      filter(p => !!p.masterId),
      distinctUntilChanged((a, b) => a.masterId === b.masterId && a.versionId === b.versionId)
    ).subscribe(({ masterId, versionId }) => {
      this.documentVersionId.set(versionId ?? null);
      this.readOnly.set(!versionId);
      this.loadFromStorage(masterId!, versionId ?? null);
    });

    // Zalogowany użytkownik z MSAL (konto aktywne ustawiane w App po zakończeniu interakcji).
    const account = this.msal.instance.getActiveAccount() ?? this.msal.instance.getAllAccounts()[0] ?? null;
    const fullName = account?.name?.trim() || account?.username || '';
    this.currentUserName.set(fullName);

    // Imię ustalamy tak samo jak na Dashboardzie: `name` ma format „Nazwisko, X. (Imię)" —
    // najpierw bierzemy tekst z nawiasów; gdy go brak, claim `given_name`; w ostateczności fallback
    // z pełnej nazwy (Qutasator `given_name` bywa samym inicjałem, dlatego nawias ma priorytet).
    const parenthesized = fullName.match(/\(([^)]+)\)/)?.[1]?.trim();
    const givenName = (account?.idTokenClaims as Record<string, unknown> | undefined)?.['given_name'];
    const first = parenthesized
      ? parenthesized
      : typeof givenName === 'string' && givenName.trim()
        ? givenName.trim()
        : this.deriveFirstName(fullName);
    this.firstName.set(first);
    this.initials.set(this.deriveInitials(fullName || first));

    // Awatar z Microsoft Graph — best-effort: brak zgody/zdjęcia → zostają inicjały.
    if (account) {
      void this.loadAvatar(account);
    }

    this.startAutoSave();
  }

  ngOnDestroy(): void {
    this.stopAutoSave();
    this.finishSendSub?.unsubscribe();
    this.vRulerResizeObserver?.disconnect();
    clearTimeout(this.tabCloseHintTimer);
    if (this.avatarObjectUrl) {
      URL.revokeObjectURL(this.avatarObjectUrl);
      this.avatarObjectUrl = null;
    }
  }

  /**
   * Pobiera awatar użytkownika z Microsoft Graph (`/me/photo/$value`). Świadomie OMIJA
   * Angular HttpClient (acquireTokenSilent + fetch), żeby nie odpalać `httpErrorInterceptor`
   * (toast/redirect) przy 404 „brak zdjęcia" lub braku zgody na Graph. Każdy błąd → cicho
   * zostają inicjały. Nie wymusza interakcji (brak popupu logowania dla samego awatara).
   */
  private async loadAvatar(account: AccountInfo): Promise<void> {
    try {
      const result = await this.msal.instance.acquireTokenSilent({ scopes: ['User.Read'], account });
      const response = await fetch('https://graph.microsoft.com/v1.0/me/photo/$value', {
        headers: { Authorization: `Bearer ${result.accessToken}` },
      });
      if (!response.ok) return; // brak zdjęcia (404) / brak dostępu → fallback na inicjały
      const blob = await response.blob();
      this.avatarObjectUrl = URL.createObjectURL(blob);
      this.avatarUrl.set(this.avatarObjectUrl);
    } catch {
      // interaction_required / brak zgody / sieć → zostają inicjały
    }
  }

  /** Imię z pełnej nazwy: obsługuje „Nazwisko, Imię" i „Imię Nazwisko"; fallback z e-maila. */
  private deriveFirstName(fullName: string): string {
    if (!fullName) return '';
    if (fullName.includes(',')) {
      return fullName.split(',')[1]?.trim().split(/\s+/)[0] ?? '';
    }
    if (fullName.includes('@')) {
      return fullName.split('@')[0];
    }
    return fullName.split(/\s+/)[0];
  }

  /** Inicjały (max 2 znaki) do awatara zastępczego. */
  private deriveInitials(name: string): string {
    const parts = name.replace(',', ' ').split(/\s+/).filter(Boolean);
    if (parts.length === 0) return '?';
    if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
    return (parts[0][0] + parts[1][0]).toUpperCase();
  }

  /**
   * Uruchamia cykliczny auto-save. Interwał z konfiguracji (environment.autoSave.intervalSeconds).
   * Każdy tick nadpisuje wersję edytowalną tylko gdy: auto-save włączony, jest versionId (tryb edycji)
   * oraz w edytorze są niezapisane zmiany.
   */
  private startAutoSave(): void {
    this.stopAutoSave();
    const intervalMs = (environment.autoSave?.intervalSeconds ?? 30) * 1000;
    this.autoSaveSub = timer(intervalMs, intervalMs).subscribe(() => {
      if (!this.autoSaveEnabled()) return;
      if (this.isAutoSaving) return;
      if (this.editingDisabled()) return;
      if (!this.documentVersionId() || !this.documentMasterId()) return;
      if (!this.editorState()?.isModified) return;
      this.performAutoSave();
    });
  }

  private stopAutoSave(): void {
    this.autoSaveSub?.unsubscribe();
    this.autoSaveSub = undefined;
  }

  /**
   * Przełącza auto-save (switch w UI). Wyłączenie zatrzymuje cykliczne zapisy.
   */
  toggleAutoSave(): void {
    const next = !this.autoSaveEnabled();
    this.autoSaveEnabled.set(next);
    this.autoSaveStatus.set('idle');
  }

  /**
   * Buduje request zapisu z bieżącego stanu edytora (HTML + metadane + nagłówek/stopka + marginesy).
   */
  private buildSaveRequest() {
    const html = this.editor?.getContent() || this.documentContent();
    const fileName = this.originalFileName() || `${this.documentMetadata().title || 'dokument'}.docx`;
    return {
      html,
      originalFileName: fileName,
      metadata: this.documentMetadata(),
      header: this.headerContent(),
      footer: this.footerContent(),
      margins: this.pageSettings().margins,
      pageSize: this.documentPageSize(),
      sectionHeadersFooters: this.sectionHeadersFooters() ?? undefined,
      footnotes: this.footnotes() ?? undefined,
      endnotes: this.endnotes() ?? undefined,
      // Pass-through: backend zachowa style tabel/motyw/numerację oryginału (definicje,
      // nie tylko formatowanie bezpośrednie). Brak masterId (np. nowy dokument) → regeneracja.
      masterId: this.documentMasterId() ?? undefined,
      // Efektywny format POKAZYWANY w edytorze (defaulty jak _formatNoteLabel wysiwyg:
      // dolne = cyfry, końcowe = małe rzymskie) — zapisany plik ma wyglądać jak ekran.
      footnoteNumberFormat: this.footnoteNumberFormat() ?? 'decimal',
      endnoteNumberFormat: this.endnoteNumberFormat() ?? 'lowerRoman'
    };
  }

  /**
   * Serializuje zawartość do DOCX i utrwala przez API.
   * PUT (nadpisanie wersji edytowalnej v2 w miejscu) gdy jest versionId, inaczej POST (nowa wersja).
   */
  private persistDocument(): Observable<unknown> {
    const masterId = this.documentMasterId()!;
    const versionId = this.documentVersionId();
    return this.documentService.saveDocument(this.buildSaveRequest()).pipe(
      switchMap(blob => from(this.blobToBase64(blob))),
      switchMap(base64 => versionId
        ? this.documentStorageService.updateDocumentVersion(masterId, versionId, { content: base64 })
        : this.documentStorageService.saveDocumentVersion(masterId, { content: base64 })
      )
    );
  }

  /**
   * Auto-save: nadpisuje wersję edytowalną w tle (ta sama ścieżka co ręczny „Zapisz").
   */
  private performAutoSave(): void {
    this.isAutoSaving = true;
    this.autoSaveStatus.set('saving');

    this.persistDocument().subscribe({
      next: () => {
        this.editor?.markAsSaved();
        this.lastAutoSaveAt.set(new Date());
        this.autoSaveStatus.set('saved');
        this.isAutoSaving = false;
      },
      error: () => {
        this.autoSaveStatus.set('error');
        this.isAutoSaving = false;
      }
    });
  }

  /**
   * Loguje RAZ, przy otwarciu dokumentu, ten sam zestaw danych diagnostycznych co akcja „Zgłoś
   * problem" (id-ki dokumentu, wersja/build, przeglądarka, viewport, ostatni błąd HTTP itd.). Dzięki
   * temu w logach aplikacji (konsola przeglądarki — wsparcie zgrywa je z devtools / raportu
   * użytkownika) jest ślad kontekstu sesji edycji, bez zalewania logów przy każdym autozapisie.
   * Reużywa `collectDiagnosticRows()` — jedno źródło prawdy dla danych zgłoszenia.
   */
  private logOpenDiagnostics(): void {
    const block = this.formatDiagnostics(this.collectDiagnosticRows());
    console.info(`[open] ${new Date().toISOString()} — otwarto dokument\n${block}`);
  }

  private static readonly DOCX_MIME = 'application/vnd.openxmlformats-officedocument.wordprocessingml.document';
  private static readonly DOC_MIME = 'application/msword';
  private static readonly PDF_MIME = 'application/pdf';

  /**
   * Ładuje dokument z bazy.
   * - Tryb edycji (versionId): ładuje WSKAZANĄ wersję edytowalną (Krok 3).
   * - Tryb read-only (brak versionId): ładuje wersję BAZOWĄ — oryginał (Krok 2, bardzo ważne!).
   * PDF nie jest obsługiwany w edytorze DOCX → przekierowanie do /viewer.
   */
  private loadFromStorage(masterId: string, versionId: string | null): void {
    this.isLoading.set(true);
    this.errorMessage.set(null);
    this.documentMasterId.set(masterId);
    this.returnUrl.set(null);
    this.userDownload.set(false);
    this.showSaveState.set(true);
    // Loaded from storage (external app / dashboard), not from local disk.
    this.loadedFromDisk.set(false);
    this.diskOriginalFile = null;

    this.documentStorageService.getDocumentMetadata(masterId).pipe(
      switchMap(meta => {
        const mime = (meta.mimeType || '').toLowerCase();

        // Presentational classification from external metadata (does not gate access).
        this.documentClassification.set(meta.classification ?? null);
        // Source of truth for the "Zakończ" button visibility (Krok 4 return link).
        this.returnUrl.set(meta.returnUrl ?? null);
        // Drives "Pobierz dokument" menu visibility — backend also enforces.
        this.userDownload.set(meta.userDownload === true);
        // Inverse-default: hide save-state UI only when the source app sent explicit false.
        this.showSaveState.set(meta.showSaveState !== false);

        // PDF: edytor DOCX nie renderuje PDF — kieruj do PDFViewer (tryb podglądu).
        if (mime === DocumentEditorComponent.PDF_MIME) {
          this.router.navigate(['/viewer'], { queryParams: { masterId } });
          return from(Promise.reject({ handled: true } as const));
        }

        // Bajty: edycja → wskazana wersja; read-only → wersja bazowa (oryginał).
        const bytes$ = versionId
          ? this.documentStorageService.downloadVersion(masterId, versionId)
          : this.documentStorageService.downloadBaseVersion(masterId);

        const ext = mime === DocumentEditorComponent.DOC_MIME ? '.doc' : '.docx';
        const fileName = `dokument${ext}`;

        return bytes$.pipe(
          map(blob => ({
            file: new File([blob], fileName, { type: mime || DocumentEditorComponent.DOCX_MIME }),
            fileName
          }))
        );
      })
    ).subscribe({
      next: ({ file, fileName }) => {
        // Konwersja przez /open (z dekrypcją/detekcją .doc). Plik z hasłem → dialog hasła
        // pojawia się też w tej ścieżce (otwarcie z dashboardu / odświeżenie edytora).
        this._convertAndLoad(file, fileName);
      },
      error: (err) => {
        if (err?.handled) {
          // przekierowanie do /viewer — nic nie pokazujemy
          return;
        }
        if (err.status === 404) {
          this.documentNotFound.set(true);
        } else {
          this.showError(err.message || 'Nie udało się otworzyć dokumentu z bazy danych');
        }
        this.isLoading.set(false);
      }
    });
  }

  /**
   * Aktualizuje tytuł dokumentu
   */
  updateTitle(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.documentMetadata.update(m => ({ ...m, title: input.value }));
  }

  /**
   * Ładuje dostępne szablony
   */
  private loadTemplates(): void {
    this.documentService.getTemplates().subscribe({
      next: (templates) => this.templates.set(templates),
      error: (err) => console.error('Błąd ładowania szablonów:', err)
    });
  }

  /**
   * Wraca do dashboardu — pokazuje ładny dialog jeśli jest otwarty dokument lub niezapisane zmiany
   */
  goToDashboard(): void {
    const hasDocument = !!this.documentMasterId();
    const hasChanges = !!this.editorState()?.isModified;

    if (hasDocument || hasChanges) {
      this.showLeaveDialog.set(true);
    } else {
      this.router.navigate(['/']);
    }
  }

  confirmLeave(): void {
    this.showLeaveDialog.set(false);
    this.router.navigate(['/']);
  }

  cancelLeave(): void {
    this.showLeaveDialog.set(false);
  }

  /**
   * Tworzy nowy dokument — zapisuje pusty dokument do bazy, nawiguje do edytora z nowym masterId
   */
  newDocument(): void {
    if (this.editorState()?.isModified) {
      if (!confirm('Masz niezapisane zmiany. Czy na pewno chcesz utworzyć nowy dokument?')) {
        return;
      }
    }

    this.showMenu.set(false);
    this.isLoading.set(true);
    this.errorMessage.set(null);

    this.documentService.newDocument().pipe(
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
                  // Nowy dokument = zamiar edycji → twórz wersję edytowalną (v2).
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
        this.router.navigate(['/editor'], { queryParams: { masterId, versionId } });
      },
      error: () => {
        this.showError('Nie udało się utworzyć nowego dokumentu');
        this.isLoading.set(false);
      }
    });
  }

  /**
   * Otwiera dokument z pliku — DOCX: upload do bazy, nawiguje z nowym masterId; PDF: upload i podgląd (/viewer)
   */
  openDocument(): void {
    const input = document.createElement('input');
    input.type = 'file';
    // .doc dozwolone — backend wykrywa po zawartości: zwykły/zaszyfrowany DOCX oraz .doc będący
    // w istocie DOCX są otwierane; binarny .doc zwraca kontrolowany komunikat o konwersji.
    input.accept = '.docx,.doc,.pdf';

    input.onchange = (e) => {
      const file = (e.target as HTMLInputElement).files?.[0];
      if (!file) return;

      this.showMenu.set(false);

      if (file.name.toLowerCase().endsWith('.pdf')) {
        this.openPdfInViewer(file);
        return;
      }

      // Wczytanie przez /open (normalizer): obsługa DOCX, DOCX z hasłem (prompt) oraz detekcja .doc.
      // Treść trafia bezpośrednio do edytora (HTML).
      this.loadDocument(file);
    };

    input.click();
  }

  /**
   * PDF wybrany w „Plik → Otwórz": upload do bazy i przejście do podglądu (/viewer) —
   * ta sama ścieżka co na stronie startowej.
   */
  private async openPdfInViewer(file: File): Promise<void> {
    this.isLoading.set(true);
    this.errorMessage.set(null);
    try {
      const base64 = await this.documentStorageService.fileToBase64(file);
      this.documentStorageService.uploadDocument({
        name: file.name,
        mimeType: 'application/pdf',
        content: base64,
      }).subscribe({
        next: (result) => {
          this.documentNavigation.navigateToDocument(result.masterId, 'application/pdf');
        },
        error: (err) => {
          this.isLoading.set(false);
          this.showError(documentDefectMessage(err)
            ?? 'Błąd podczas wczytywania pliku PDF. Spróbuj ponownie.');
        },
      });
    } catch {
      this.isLoading.set(false);
      this.showError('Błąd podczas odczytu pliku PDF.');
    }
  }

  private blobToBase64(blob: Blob): Promise<string> {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.readAsDataURL(blob);
      reader.onload = () => resolve((reader.result as string).split(',')[1]);
      reader.onerror = reject;
    });
  }

  /** Ładuje dokument z pliku z dysku ("Plik → Otwórz"). */
  private loadDocument(file: File, password?: string): void {
    // Keep the untouched original so "Pobierz oryginał dokumentu" can hand it back verbatim.
    this.diskOriginalFile = file;
    this.loadedFromDisk.set(true);
    this._convertAndLoad(file, file.name, password, true);
  }

  /**
   * Konwertuje plik (DOCX / .doc-jako-DOCX / odszyfrowany) przez /open i ładuje treść do edytora.
   * WSPÓLNE dla otwierania z dysku oraz ładowania wersji z bazy (loadFromStorage) — dzięki temu
   * dialog hasła pojawia się w OBU ścieżkach (wcześniej tylko przy otwieraniu z dysku).
   * Plik zabezpieczony hasłem → dialog + ponowienie z tym samym plikiem i hasłem.
   */
  private _convertAndLoad(file: File, fileName: string, password?: string, announce = false): void {
    this.isLoading.set(true);
    this.errorMessage.set(null);

    this.documentService.openDocument(file, password).subscribe({
      next: (content) => {
        this._applyLoadedContent(content, fileName);
        // Jeden snapshot diagnostyczny na otwarcie dokumentu (te same dane co „Zgłoś problem":
        // id-ki, wersja/build, przeglądarka…) — kontekst sesji w logach, bez zalewania ich przy
        // każdym autozapisie. WSPÓLNE dla otwierania z dysku i wczytywania wersji z bazy.
        this.logOpenDiagnostics();
        // Jeden autorytatywny komunikat wyniku otwarcia — read-only wyklucza „sukces".
        // Dokument chroniony NIGDY nie jest prezentowany jako otwarty w trybie edycji.
        if (content.isReadOnlyProtected === true) {
          // INFORMACJA, nie błąd (13865553): dokument OTWORZYŁ się poprawnie, tylko bez
          // możliwości edycji — czerwony toast sugerował nieudaną operację przy każdym
          // (także ponownym) wczytaniu tego samego pliku.
          this.showInfo(READ_ONLY_PROTECTED_MESSAGE);
        } else if (announce) {
          this.showSuccess(`Otwarto dokument: ${fileName}`);
        }
        this.isLoading.set(false);
      },
      error: (err) => {
        this.isLoading.set(false);
        const code = err instanceof OpenDocumentError ? err.code : undefined;

        // Plik zabezpieczony hasłem — pokaż dialog i ponów (z tym samym plikiem + hasłem).
        if (code === 'PASSWORD_REQUIRED' || code === 'WRONG_PASSWORD') {
          this.openPasswordDialog(pwd => this._convertAndLoad(file, fileName, pwd, announce), code === 'WRONG_PASSWORD');
          return;
        }

        // Znany defekt pliku (pusty / zły format / uszkodzony — bug 13625398) — jednolity
        // komunikat, ten sam co na stronie startowej; nie surowa treść backendu.
        const defect = documentDefectMessage(err);
        if (defect) {
          this.showError(defect);
          return;
        }

        // Binarny .doc / inny błąd — komunikat z backendu wskazuje, co zrobić.
        if (err?.status === 404) {
          this.documentNotFound.set(true);
        } else {
          this.showError(err.message || 'Nie udało się otworzyć dokumentu');
        }
      }
    });
  }

  /** Ustawia treść/metadane/nagłówki/stopki/marginesy/podpisy z DocumentContent w edytorze. */
  private _applyLoadedContent(content: DocumentContent, fileName: string): void {
    // Ochrona przed edycją z pliku źródłowego (Word „Ogranicz edycję" / hasło zapisu /
    // „Oznacz jako ostateczny") — blokuje edycję niezależnie od trybu (versionId). Samo
    // USTAWIENIE stanu; komunikat emituje _convertAndLoad jako jeden wynik otwarcia, żeby
    // nie nakładał się na toast sukcesu (badge w nagłówku prezentuje stan trwale).
    this.documentEditProtected.set(content.isReadOnlyProtected === true);
    this.documentContent.set(content.html);
    this.documentMetadata.set(content.metadata);
    this.documentStyles.set(content.styles || []);
    this.originalFileName.set(fileName);
    // Nagłówek/stopka: KAŻDE pole wariantu podawane JAWNIE (spread nie wystarcza — brak pola
    // w nowym dokumencie zostawiał w edytorze wariant z POPRZEDNIEGO: setter wysiwyg-editora
    // aktualizuje sygnał tylko dla pól `!== undefined`, więc dokument bez nagłówka/stopki
    // pokazywał first-page/even z wcześniej wczytanego pliku). Reset = „wczytaj od zera".
    this.headerContent.set({
      html: content.header?.html || '',
      height: content.header?.height || 1.25,
      differentFirstPage: content.header?.differentFirstPage ?? false,
      firstPageHtml: content.header?.firstPageHtml ?? '',
      differentOddEven: content.header?.differentOddEven ?? false,
      oddHtml: content.header?.oddHtml ?? '',
      evenHtml: content.header?.evenHtml ?? ''
    });
    this.footerContent.set({
      html: content.footer?.html || '',
      height: content.footer?.height || 1.25,
      differentFirstPage: content.footer?.differentFirstPage ?? false,
      firstPageHtml: content.footer?.firstPageHtml ?? '',
      differentOddEven: content.footer?.differentOddEven ?? false,
      oddHtml: content.footer?.oddHtml ?? '',
      evenHtml: content.footer?.evenHtml ?? ''
    });
    if (content.margins) {
      this.pageSettings.update(s => ({ ...s, margins: content.margins! }));
    }
    if (content.pageSize) {
      this.documentPageSize.set(content.pageSize);
      this.pageSettings.update(s => ({ ...s, orientation: content.pageSize!.orientation }));
    }
    // Własne nagłówki/stopki sekcji ≥ 1 (dokumenty wielosekcyjne) — round-trip przez zapis.
    this.sectionHeadersFooters.set(content.sectionHeadersFooters ?? null);
    // Przypisy dolne — treść przekazywana do edytora i z powrotem w zapisie (jedno źródło prawdy).
    this.footnotes.set(content.footnotes ?? null);
    this.endnotes.set(content.endnotes ?? null);
    // Format numeracji przypisów z dokumentu (w:numFmt) — do wyświetlania etykiet w edytorze.
    this.footnoteNumberFormat.set(content.footnoteNumberFormat ?? null);
    this.endnoteNumberFormat.set(content.endnoteNumberFormat ?? null);
    if (this.editor) {
      this.editor.setContent(content.html);
    }
    this.documentSignatures.set(content.metadata.signatures || []);
  }

  // ── Dialog hasła do zaszyfrowanego dokumentu ─────────────────────────────────
  showPasswordDialog = signal(false);
  passwordDialogValue = '';
  passwordDialogError = signal<string | null>(null);
  /** Callback ponawiający otwarcie z podanym hasłem (różny dla pliku z dysku vs wersji z bazy). */
  private _passwordRetry: ((password: string) => void) | null = null;

  /** Otwiera dialog hasła. `retry(pwd)` ponawia otwarcie; `wrong` = poprzednia próba miała błędne hasło. */
  private openPasswordDialog(retry: (password: string) => void, wrong: boolean): void {
    this._passwordRetry = retry;
    this.passwordDialogValue = '';
    this.passwordDialogError.set(wrong ? 'Nieprawidłowe hasło. Spróbuj ponownie.' : null);
    this.showPasswordDialog.set(true);
    // Po wyrenderowaniu @if ustaw fokus w polu hasła.
    setTimeout(() => (document.querySelector('.password-dialog input') as HTMLInputElement | null)?.focus(), 50);
  }

  /** Zatwierdza hasło → ponawia otwarcie dokumentu z podanym hasłem. */
  confirmPasswordDialog(): void {
    const pwd = this.passwordDialogValue;
    if (!pwd) {
      this.passwordDialogError.set('Wpisz hasło.');
      return;
    }
    const retry = this._passwordRetry;
    this.showPasswordDialog.set(false);
    this.passwordDialogValue = '';
    this._passwordRetry = null;
    retry?.(pwd);
  }

  /**
   * Anuluje wprowadzanie hasła. Nie wolno zostawić użytkownika z pustym dokumentem (Problem 1):
   * - bez `returnUrl` (otwarcie z dashboardu / dysku) → powrót na dashboard,
   * - z `returnUrl` (link z aplikacji zewnętrznej) → przepływ powrotu jak po „Zakończ"
   *   (best-effort zamknięcie karty otwartej przez aplikację zewnętrzną).
   */
  cancelPasswordDialog(): void {
    this.showPasswordDialog.set(false);
    this.passwordDialogValue = '';
    this._passwordRetry = null;
    this.passwordDialogError.set(null);

    if (this.returnUrl()) {
      this.tryCloseBrowserTab();
    } else {
      this.router.navigate(['/']);
    }
  }

  /**
   * Zapisuje dokument przez API (ujednolicony zapis — ta sama ścieżka co auto-save).
   * Nadpisuje wersję edytowalną (v2) w miejscu gdy jest versionId; w przeciwnym razie tworzy nową wersję.
   */
  saveDocument(): void {
    if (this.editingDisabled()) {
      // Tryb podglądu (Krok 2), dokument zajęty albo chroniony przed edycją — zapis przez API zablokowany.
      this.showError(this.documentEditProtected()
        ? 'Dokument jest chroniony przed edycją — zapis jest zablokowany.'
        : 'Tryb podglądu — dokument jest tylko do odczytu. Użyj „Pobierz dokument", aby zapisać kopię lokalnie.');
      this.showMenu.set(false);
      return;
    }

    const masterId = this.documentMasterId();
    if (!masterId) {
      // Brak mastera (np. dokument z szablonu, jeszcze nie utrwalony) — pozwól pobrać plik zamiast cichego nic.
      this.showError('Dokument nie jest powiązany z bazą — użyj „Pobierz dokument".');
      this.showMenu.set(false);
      return;
    }

    this.showMenu.set(false);
    this.isLoading.set(true);
    this.autoSaveStatus.set('saving');

    this.persistDocument().subscribe({
      next: () => {
        this.editor?.markAsSaved();
        this.lastAutoSaveAt.set(new Date());
        this.autoSaveStatus.set('saved');
        this.showSuccess('Dokument został zapisany');
        this.isLoading.set(false);
      },
      error: () => {
        this.autoSaveStatus.set('error');
        this.showError('Nie udało się zapisać dokumentu');
        this.isLoading.set(false);
      }
    });
  }

  /**
   * Pobiera dokument jako plik DOCX do przeglądarki (dawne „Zapisz").
   * Nie utrwala w bazie — to lokalna kopia dla użytkownika.
   */
  downloadDocument(): void {
    const masterId = this.documentMasterId();
    if (!masterId || !this.canUserDownload()) {
      // Defensive: the menu item is hidden when canUserDownload() is false, but
      // anyone calling the method directly still hits the same gate the backend uses.
      this.showError('Pobieranie pliku na komputer nie jest dostępne dla tego dokumentu.');
      return;
    }

    const request = this.buildSaveRequest();
    const fileName = request.originalFileName;

    // === DIAGNOSTYKA (do debugowania zgubionych stylów / formatowania) ===
    try {
      const html = request.html;
      const tmp = document.createElement('div');
      tmp.innerHTML = html;
      const counts = {
        chars: html.length,
        h1: tmp.querySelectorAll('h1').length,
        h2: tmp.querySelectorAll('h2').length,
        h3: tmp.querySelectorAll('h3').length,
        p: tmp.querySelectorAll('p').length,
        b_strong: tmp.querySelectorAll('b,strong').length,
        i_em: tmp.querySelectorAll('i,em').length,
        u: tmp.querySelectorAll('u').length,
        img: tmp.querySelectorAll('img').length,
        table: tmp.querySelectorAll('table').length,
        tr: tmp.querySelectorAll('tr').length,
        td: tmp.querySelectorAll('td').length,
        pageBreaks: tmp.querySelectorAll('div.page-break').length,
        inlineStyles: tmp.querySelectorAll('[style]').length,
        fontSizeAttrs: Array.from(tmp.querySelectorAll('[style*="font-size"]')).slice(0, 5).map(e => (e as HTMLElement).style.fontSize),
        fontFamilyAttrs: Array.from(tmp.querySelectorAll('[style*="font-family"]')).slice(0, 5).map(e => (e as HTMLElement).style.fontFamily),
      };
      console.group('[downloadDocument] DIAGNOSTYKA HTML wysyłanego do API');
      console.log('fileName:', fileName);
      console.log('counts:', counts);
      console.log('first 2000 chars:', html.substring(0, 2000));
      console.log('header:', this.headerContent());
      console.log('footer:', this.footerContent());
      console.log('margins:', this.pageSettings().margins);
      (window as unknown as { __lastSaveHtml?: string }).__lastSaveHtml = html;
      console.log('Pełny HTML dostępny w window.__lastSaveHtml');
      console.groupEnd();
    } catch (e) {
      console.warn('[downloadDocument] diagnostyka failed', e);
    }

    this.isLoading.set(true);
    this.showMenu.set(false);
    this.documentStorageService.downloadEditedDocument(masterId, request).subscribe({
      next: (blob) => {
        this.saveBlobToDisk(blob, fileName.endsWith('.docx') ? fileName : `${fileName}.docx`);
        this.showSuccess('Pobrano dokument');
        this.isLoading.set(false);
      },
      error: (err) => {
        this.isLoading.set(false);
        // 403 from the gate → keep the document-level signal in sync with the backend
        // verdict (covers the race where metadata changed since load).
        if (err?.status === 403) {
          this.userDownload.set(false);
          this.showError('Pobieranie pliku na komputer nie jest dostępne dla tego dokumentu.');
          return;
        }
        this.showError('Nie udało się pobrać dokumentu.');
      }
    });
  }

  /**
   * "Pobierz oryginał dokumentu": zawsze zwraca nietknięty oryginał (v1), nigdy edytowanej
   * kopii roboczej. Dokument z dysku → plik wczytany lokalnie; dokument z aplikacji zewnętrznej
   * → wersja bazowa (v1) ze storage. Widoczność pozycji menu steruje `canDownloadOriginal()`.
   */
  downloadOriginalDocument(): void {
    this.showMenu.set(false);

    if (!this.canDownloadOriginal()) {
      // Defensive: the menu item is hidden when canDownloadOriginal() is false.
      this.showError('Pobieranie oryginału nie jest dostępne dla tego dokumentu.');
      return;
    }

    // Wczytany z dysku — oddaj dokładnie ten plik, który użytkownik otworzył.
    if (this.loadedFromDisk() && this.diskOriginalFile) {
      this.saveBlobToDisk(this.diskOriginalFile, this.diskOriginalFile.name);
      this.showSuccess('Pobrano oryginał dokumentu');
      return;
    }

    const masterId = this.documentMasterId();
    if (!masterId) {
      this.showError('Dokument nie jest powiązany z bazą — brak oryginału do pobrania.');
      return;
    }

    this.isLoading.set(true);
    this.documentStorageService.downloadBaseVersion(masterId).subscribe({
      next: (blob) => {
        const fileName = this.originalFileName() || `${this.documentMetadata().title || 'dokument'}.docx`;
        this.saveBlobToDisk(blob, fileName);
        this.showSuccess('Pobrano oryginał dokumentu');
        this.isLoading.set(false);
      },
      error: () => {
        this.isLoading.set(false);
        this.showError('Nie udało się pobrać oryginału dokumentu.');
      }
    });
  }

  /** Zapisuje Blob jako plik na dysku użytkownika (wspólne dla „Pobierz dokument"/„Pobierz oryginał"). */
  private saveBlobToDisk(blob: Blob, fileName: string): void {
    const objectUrl = window.URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = objectUrl;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    window.URL.revokeObjectURL(objectUrl);
  }

  /**
   * Otwiera szablon
   */
  openTemplate(templateId: string): void {
    this.isLoading.set(true);
    this.showTemplates.set(false);
    
    this.documentService.getTemplate(templateId).subscribe({
      next: (content) => {
        this.documentContent.set(content.html);
        this.documentMetadata.set(content.metadata);
        this.originalFileName.set('');
        
        if (this.editor) {
          this.editor.setContent(content.html);
        }
        
        this.isLoading.set(false);
      },
      error: (err) => {
        this.showError('Nie udało się załadować szablonu');
        this.isLoading.set(false);
      }
    });
  }

  /**
   * Obsługa komendy z toolbara
   */
  onCommand(event: { command: EditorCommand; value?: string }): void {
    this.editor?.executeCommand(event.command, event.value);
  }

  /**
   * Obsługa zmiany rozmiaru czcionki
   */
  onFontSizeChange(size: number): void {
    this.editor?.setFontSize(size);
  }

  /**
   * Obsługa zmiany rodziny czcionki
   */
  onFontFamilyChange(family: string): void {
    this.editor?.setFontFamily(family);
  }

  /**
   * Obsługa zmiany koloru tekstu
   */
  onTextColorChange(color: string): void {
    this.editor?.setTextColor(color);
  }

  /**
   * Obsługa zmiany koloru tła
   */
  onBackgroundColorChange(color: string): void {
    this.editor?.setBackgroundColor(color);
  }

  /**
   * Wstawia link
   */
  onInsertLink(event: { url: string; text?: string }): void {
    this.editor?.insertLink(event.url, event.text);
  }

  /**
   * Wstawia obraz
   */
  onInsertImage(): void {
    // Zapisz selekcję edytora przed otwarciem dialogu pliku (który zabiera focus)
    this.editor?.saveSelection();

    const input = document.createElement('input');
    input.type = 'file';
    input.accept = 'image/*';
    
    input.onchange = (e) => {
      const file = (e.target as HTMLInputElement).files?.[0];
      if (file) {
        this.uploadAndInsertImage(file);
      }
    };
    
    input.click();
  }

  /**
   * Wgrywa i wstawia obraz
   */
  private uploadAndInsertImage(file: File): void {
    // Konwertuj lokalnie do base64 (bez wysyłania na serwer)
    const reader = new FileReader();
    reader.onload = (e) => {
      const base64 = e.target?.result as string;
      if (base64 && this.editor) {
        // Przywróć fokus i selekcję w edytorze przed wstawieniem
        this.editor.focus();
        this.editor.restoreSelection();
        this.editor.insertImage(base64, file.name);
      }
    };
    reader.readAsDataURL(file);
  }

  /**
   * Wstawia tabelę (szybkie wstawianie z podmenu)
   */
  onInsertTable(config: string): void {
    if (this.editor) {
      this.editor.insertTable(config);
      this.applyTableAutoFit(this.tableDialogData.autoFitBehavior, this.tableDialogData.fixedWidth);
    }
  }

  /** Wstawia przypis dolny w pozycji kursora (odwołanie + wpis w modelu przypisów). */
  onInsertFootnote(): void {
    this.editor?.addFootnoteAtCursor();
  }

  /** Wstawia przypis końcowy w pozycji kursora. */
  onInsertEndnote(): void {
    this.editor?.addEndnoteAtCursor();
  }

  /**
   * Obsługa zmiany stylu dokumentu
   */
  onStyleChange(style: DocumentStyle): void {
    // Zastosuj pełny styl do zaznaczenia
    if (this.editor) {
      this.editor.applyDocumentStyle(style);
    }
  }

  // Przechowywane formatowanie do kopiowania
  private copiedFormat: any = null;

  /**
   * Kopiuje formatowanie z bieżącego zaznaczenia
   */
  onCopyFormat(): void {
    if (this.editor) {
      this.copiedFormat = this.editor.getCurrentFormatting();
    }
  }

  /**
   * Aplikuje skopiowane formatowanie do zaznaczenia
   */
  onPasteFormat(): void {
    if (this.editor && this.copiedFormat) {
      this.editor.applyFormatting(this.copiedFormat);
    }
  }

  /**
   * Wyszukiwanie tekstu w dokumencie
   */
  private lastSearchText = '';

  onSearchInDocument(event: { text: string; direction: 'next' | 'previous' }): void {
    if (!this.editor) return;

    let result: { count: number; currentIndex: number };

    if (event.text !== this.lastSearchText) {
      // Nowe wyszukiwanie
      this.lastSearchText = event.text;
      result = this.editor.searchText(event.text, event.direction);
    } else {
      // Nawigacja po istniejących wynikach
      result = event.direction === 'next' ? this.editor.findNext() : this.editor.findPrevious();
    }

    if (this.toolbar) {
      this.toolbar.updateSearchResults(result.count, result.currentIndex);
    }
  }

  onReplaceInDocument(event: { searchText: string; replaceText: string; all: boolean }): void {
    if (!this.editor) return;

    let result: { count: number; currentIndex: number };

    if (event.all) {
      result = this.editor.replaceAllMatches(event.replaceText);
    } else {
      result = this.editor.replaceCurrentMatch(event.replaceText);
    }

    if (this.toolbar) {
      this.toolbar.updateSearchResults(result.count, result.currentIndex);
    }
  }

  onClearSearch(): void {
    if (this.editor) {
      this.editor.clearSearchHighlights();
    }
    this.lastSearchText = '';
  }

  /**
   * Obsługa zmiany zawartości
   */
  onContentChange(html: string): void {
    this.documentContent.set(html);
    this.documentMetadata.update(m => ({
      ...m,
      modified: new Date().toISOString()
    }));
  }

  /**
   * Obsługa zmiany nagłówka
   */
  onHeaderChange(header: HeaderFooterContent): void {
    this.headerContent.set(header);
    this.documentMetadata.update(m => ({
      ...m,
      modified: new Date().toISOString()
    }));
  }

  /**
   * Obsługa zmiany stopki
   */
  onFooterChange(footer: HeaderFooterContent): void {
    this.footerContent.set(footer);
    this.documentMetadata.update(m => ({
      ...m,
      modified: new Date().toISOString()
    }));
  }

  /**
   * Obsługa zmiany stanu edytora
   */
  onStateChange(state: EditorState): void {
    this.editorState.set(state);
    this.detectTableContext();
  }

  /**
   * Wykrywa czy kursor jest wewnątrz tabeli
   */
  private detectTableContext(): void {
    const selection = window.getSelection();
    const editorEl = this.editor?.editorContent?.nativeElement;
    const ctx = resolveTableContext(selection?.anchorNode, editorEl);

    // Selekcja poza treścią edytora = interakcja z UI (panel boczny / toolbar).
    // Zachowujemy ostatni znany kontekst tabeli, żeby kliknięcia w panelu (np.
    // obramowania) nadal dotyczyły aktywnej tabeli i nie przełączały panelu na
    // „Wybierz tabelę". Czyszczenie tylko, gdy karetka jest realnie poza tabelą.
    if (ctx.placement === 'outside-editor') return;

    this.isInTable.set(ctx.placement === 'in-table');
    this.activeTableCell.set(ctx.cell);
    this.activeTable.set(ctx.table);
    if (ctx.placement === 'outside-table') {
      // Karetka w treści poza tabelą — wyczyść wizualne zaznaczenie komórek, żeby
      // tabela nie wyglądała na nadal zaznaczoną (spójność stanu z widokiem).
      this.clearCellSelection();
    }
    this.syncTablePanel();
  }

  /** Reaguje na zmianę selekcji w edytorze (ruch karetki/klik), nie tylko na zmianę treści. */
  onEditorSelectionChange(): void {
    this.detectTableContext();
  }

  /**
   * Steruje widocznością bocznego panelu tabeli na podstawie kontekstu karetki.
   * Reguły: panel pojawia się gdy karetka jest w tabeli (zastępuje dawny pasek
   * tabeli pojawiający się przy `isInTable`), znika po opuszczeniu tabeli, nie
   * wypiera panelu wyszukiwania i respektuje ręczne zamknięcie przez użytkownika.
   */
  private syncTablePanel(): void {
    if (!this.isInTable() || this.editingDisabled()) {
      this.showTablePanel.set(false);
      this.tablePanelManuallyClosed = false;
      return;
    }
    if (this.tablePanelManuallyClosed) {
      return;
    }
    // Re-selecting a table takes over the docked area: the user's context is now
    // the table, so close search (if open) and show table formatting. The dock has
    // a single active mode at a time — table selection wins over an open search panel.
    if (this.showFindReplace()) {
      this.closeFindReplace();
    }
    this.showTablePanel.set(true);
  }

  /** Ręczne zamknięcie panelu tabeli (przycisk ×) — nie otwieraj ponownie póki w tej tabeli. */
  closeTablePanel(): void {
    this.showTablePanel.set(false);
    this.tablePanelManuallyClosed = true;
  }

  /**
   * ESC zamyka aktywny boczny panel (Wyszukiwanie / właściwości tabeli) — przez tę
   * samą logikę co przycisk × (`closeFindReplace` / `closeTablePanel`). Reguły:
   *  - jeśli bardziej szczegółowy handler już obsłużył ESC (np. deselekcja obrazu
   *    w edytorze woła `preventDefault`) — nie ruszamy panelu (`defaultPrevented`),
   *  - otwarte dialogi/modale i menu kontekstowe mają pierwszeństwo (własne zamykanie),
   *  - gdy żaden panel nie jest otwarty, ESC nie robi nic (brak skutków ubocznych).
   * Listener jest na `document`, bo panel może nie mieć focusu (karetka w edytorze).
   */
  @HostListener('document:keydown.escape', ['$event'])
  onEscapeKeydown(event: KeyboardEvent): void {
    if (event.defaultPrevented) return;
    if (this.isAnyDialogOpen() || this.showContextMenu()) return;

    // Header/footer edit mode takes precedence over side panels — it's the most
    // recently entered context and the spec lists ESC as a way to leave it.
    if (this.editingSection() !== 'body') {
      event.preventDefault();
      this.editor?.stopEditingHeaderFooter();
      return;
    }

    if (!this.showFindReplace() && !this.showTablePanel()) return;

    event.preventDefault();
    this.closeActiveSidePanel();
  }

  /**
   * Zamyka aktualnie otwarty boczny panel tą samą drogą co przycisk ×.
   * Jeśli focus był wewnątrz panelu (np. pole wyszukiwania), przywraca go do
   * edytora — sensowne miejsce, bez tworzenia osobnego systemu focus-trap.
   */
  closeActiveSidePanel(): void {
    const focusInPanel = !!(document.activeElement as HTMLElement | null)
      ?.closest('.search-panel, d2-table-properties-panel');

    if (this.showFindReplace()) {
      this.closeFindReplace();
    } else if (this.showTablePanel()) {
      this.closeTablePanel();
    }

    if (focusInPanel) {
      this.editor?.focus();
    }
  }

  /**
   * Czy otwarty jest jakikolwiek modal/dialog edytora. Mają one własne zamykanie
   * (przycisk ×) i pierwszeństwo nad regułą ESC dla bocznego panelu — inaczej ESC
   * przy otwartym dialogu zamykałby panel w tle zamiast (przyszłościowo) dialog.
   */
  private isAnyDialogOpen(): boolean {
    return this.showTemplates()
      || this.showPageSetup()
      || this.showHeaderFooterDialog()
      || this.showBarcodeDialog()
      || this.showParagraphDialog()
      || this.showInsertTableDialog()
      || this.showPropertiesDialog()
      || this.showSignatureDialog()
      || this.showLeaveDialog();
  }

  /**
   * Zmienia zoom
   */
  setZoom(level: number): void {
    this.zoomLevel.set(level);
    this.measureVRuler();
  }

  /**
   * Obsługuje scroll aby aktualizować bieżącą stronę
   */
  /**
   * Kółko myszy nad pionową linijką nie ma jej przewijać samodzielnie (desync od dokumentu)
   * — przekierowujemy scroll na obszar dokumentu, który następnie zsynchronizuje linijkę.
   */
  onRulerWheel(e: WheelEvent): void {
    const container = this.editorScrollContainer?.nativeElement;
    if (!container) return;
    e.preventDefault();
    container.scrollTop += e.deltaY;
    container.scrollLeft += e.deltaX;
  }

  onEditorScroll(event: Event): void {
    const container = event.target as HTMLElement;
    const scrollTop = container.scrollTop;
    const scale = this.zoomLevel() / 100;

    // Synchronizuj pionową linijkę ze scrollem (pionowym)
    if (this.verticalRulerBar?.nativeElement) {
      this.verticalRulerBar.nativeElement.scrollTop = scrollTop;
    }

    // Synchronizuj poziomą linijkę ze scrollem (poziomym) — przesuwamy ją razem z kartką,
    // żeby podziałka pokrywała się z dokumentem także przy przewijaniu w bok / dużym zoomie.
    if (this.horizontalRulerInner?.nativeElement) {
      this.horizontalRulerInner.nativeElement.style.transform = `translateX(${-container.scrollLeft}px)`;
    }
    
    // Wysokość strony A4 w pikselach + margines
    const PAGE_HEIGHT = 1122;
    const PAGE_GAP = 40; // gap między stronami + separator
    const PADDING_TOP = 20; // padding containera
    
    // Oblicz wysokość strony z uwzględnieniem skali
    const scaledPageHeight = PAGE_HEIGHT * scale;
    const scaledGap = PAGE_GAP * scale;
    
    // Oblicz pozycję środka widocznego obszaru
    const viewportCenter = scrollTop + (container.clientHeight / 2) - (PADDING_TOP * scale);
    
    // Oblicz bieżącą stronę
    const currentPageNum = Math.floor(viewportCenter / (scaledPageHeight + scaledGap)) + 1;
    const maxPages = this.totalPages();
    
    this.currentPage.set(Math.min(Math.max(1, currentPageNum), maxPages));
    
    // Pokaż wskaźnik stron przy scrollowaniu (jeśli jest więcej niż 1 strona)
    if (maxPages > 1) {
      this.showPageIndicator.set(true);
      
      // Ukryj wskaźnik po 1.5 sekundy bez scrollowania
      if (this.pageIndicatorTimeout) {
        clearTimeout(this.pageIndicatorTimeout);
      }
      this.pageIndicatorTimeout = setTimeout(() => {
        this.showPageIndicator.set(false);
      }, 1500);
    }
  }

  /**
   * Obsługuje zmianę liczby stron
   */
  onPagesChange(pageCount: number): void {
    this.totalPages.set(pageCount);
    // Upewnij się, że currentPage nie jest większa niż totalPages
    if (this.currentPage() > pageCount) {
      this.currentPage.set(pageCount);
    }
    // Zmierz realny układ stron dla pionowej linijki + podepnij obserwator zmian rozmiaru.
    this.ensureVRulerObserver();
    this.measureVRuler();
  }

  /**
   * Drukuje dokument
   */
  printDocument(): void {
    window.print();
    this.showMenu.set(false);
  }

  /**
   * Pokazuje komunikat sukcesu
   */
  private showSuccess(message: string): void {
    // Sukces i błąd są wzajemnie wykluczające się — nigdy nie pokazujemy obu banerów naraz
    // (chroniony dokument nie może jednocześnie „otworzyć się poprawnie" i być read-only).
    this.errorMessage.set(null);
    this.infoMessage.set(null);
    this.successMessage.set(message);
    setTimeout(() => this.successMessage.set(null), 3000);
  }

  /**
   * Komunikat INFORMACYJNY (13865553): stan dokumentu (np. tylko do odczytu) to nie błąd —
   * czerwony toast błędu sugerował, że operacja się nie powiodła, choć dokument otwarto.
   */
  private showInfo(message: string): void {
    this.errorMessage.set(null);
    this.successMessage.set(null);
    this.infoMessage.set(message);
    setTimeout(() => this.infoMessage.set(null), 5000);
  }

  /**
   * Pokazuje komunikat błędu
   */
  private showError(message: string): void {
    this.successMessage.set(null);
    this.infoMessage.set(null);
    this.errorMessage.set(message);
    setTimeout(() => this.errorMessage.set(null), 5000);
  }

  /**
   * Toggle menu
   */
  toggleMenu(): void {
    const wasOpen = this.showMenu();
    this.closeAllMenus();
    this.showMenu.set(!wasOpen);
  }

  /**
   * Toggle menu Edytuj
   */
  toggleEditMenu(): void {
    const wasOpen = this.showEditMenu();
    this.closeAllMenus();
    this.showEditMenu.set(!wasOpen);
  }

  /**
   * Toggle menu Format
   */
  toggleFormatMenu(): void {
    const wasOpen = this.showFormatMenu();
    this.closeAllMenus();
    this.showFormatMenu.set(!wasOpen);
  }

  /**
   * Toggle menu Wstaw
   */
  toggleInsertMenu(): void {
    const wasOpen = this.showInsertMenu();
    this.closeAllMenus();
    this.showInsertMenu.set(!wasOpen);
  }

  /**
   * Zamyka menu po kliknięciu poza obszarem menu
   */
  @HostListener('document:click', ['$event'])
  onDocumentClick(event: MouseEvent): void {
    const target = event.target as HTMLElement;
    // Sprawdź czy kliknięto w obszarze menu
    const isMenuArea = target.closest('.menu-bar') || 
                       target.closest('.dropdown-menu');
    const isShadingArea = target.closest('.shading-dropdown') || target.closest('.table-toolbar-btn-shading');
    // Jeśli kliknięto poza menu i poza cieniowaniem - zamknij
    if (!isMenuArea && !isShadingArea) {
      this.closeAllMenus();
    }
    // Zamknij dropdown cieniowania jeśli kliknięto poza nim
    if (!isShadingArea) {
      this.showShadingDropdown.set(false);
    }
  }

  // =====================
  // ZAZNACZANIE KOMÓREK TABELI (MULTI-CELL SELECTION)
  // =====================

  @HostListener('mousedown', ['$event'])
  onCellMouseDown(event: MouseEvent): void {
    const target = event.target as HTMLElement;
    const editorEl = this.editor?.editorContent?.nativeElement;
    if (!editorEl) return;

    const cell = target.closest('td, th') as HTMLTableCellElement | null;
    if (cell && editorEl.contains(cell)) {
      this.cellSelectionStartCell = cell;
      this.isCellSelecting = false;
      // Wyczyść zaznaczenie jeśli nie trzymamy Shift
      if (!event.shiftKey) {
        this.clearCellSelection();
      }
    } else if (!target.closest('.table-toolbar') && !target.closest('.context-menu') && !target.closest('.shading-dropdown') && !target.closest('d2-table-properties-panel')) {
      this.cellSelectionStartCell = null;
      this.clearCellSelection();
    }
  }

  @HostListener('document:mousemove', ['$event'])
  onCellMouseMove(event: MouseEvent): void {
    if (!this.cellSelectionStartCell || !(event.buttons & 1)) return;

    const target = event.target as HTMLElement;
    const cell = target.closest('td, th') as HTMLTableCellElement | null;
    const editorEl = this.editor?.editorContent?.nativeElement;

    if (cell && editorEl && editorEl.contains(cell) && cell !== this.cellSelectionStartCell) {
      // Sprawdź czy obie komórki są w tej samej tabeli
      const startTable = this.cellSelectionStartCell.closest('table');
      const endTable = cell.closest('table');
      if (startTable && startTable === endTable) {
        this.isCellSelecting = true;
        event.preventDefault();
        // Wyczyść selekcję tekstową przeglądarki
        window.getSelection()?.removeAllRanges();
        this.selectCellRange(this.cellSelectionStartCell, cell);
      }
    }
  }

  @HostListener('document:mouseup', ['$event'])
  onCellMouseUp(event: MouseEvent): void {
    if (this.isCellSelecting) {
      this.isCellSelecting = false;
      // Wyczyść selekcję tekstową przeglądarki - zostawiamy custom cell selection
      window.getSelection()?.removeAllRanges();
    }
    this.cellSelectionStartCell = null;
  }

  /**
   * Zaznacza prostokątny zakres komórek od start do end
   */
  private selectCellRange(start: HTMLTableCellElement, end: HTMLTableCellElement): void {
    const table = start.closest('table') as HTMLTableElement;
    if (!table) return;

    const startPos = this.getCellPosition(start);
    const endPos = this.getCellPosition(end);
    if (!startPos || !endPos) return;

    const minRow = Math.min(startPos.rowIndex, endPos.rowIndex);
    const maxRow = Math.max(startPos.rowIndex, endPos.rowIndex);
    const minCol = Math.min(startPos.colIndex, endPos.colIndex);
    const maxCol = Math.max(startPos.colIndex, endPos.colIndex);

    const newSelection = new Set<HTMLTableCellElement>();
    for (let r = minRow; r <= maxRow; r++) {
      const row = table.rows[r];
      if (!row) continue;
      for (let c = minCol; c <= maxCol; c++) {
        if (c < row.cells.length) {
          newSelection.add(row.cells[c]);
        }
      }
    }
    this.applyCellSelection(newSelection);
  }

  /**
   * Stosuje wizualne zaznaczenie na podanych komórkach
   */
  private applyCellSelection(cells: Set<HTMLTableCellElement>): void {
    // Usuń stare zaznaczenie
    const prev = this.selectedCells();
    prev.forEach(c => c.classList.remove('table-cell-selected'));
    // Zaznacz nowe
    cells.forEach(c => c.classList.add('table-cell-selected'));
    this.selectedCells.set(cells);
  }

  /**
   * Czyści zaznaczenie komórek
   */
  clearCellSelection(): void {
    const prev = this.selectedCells();
    prev.forEach(c => c.classList.remove('table-cell-selected'));
    this.selectedCells.set(new Set());
  }

  /**
   * Obsługuje prawy przycisk myszy - menu kontekstowe
   */
  @HostListener('contextmenu', ['$event'])
  onContextMenu(event: MouseEvent): void {
    const target = event.target as HTMLElement;
    // Pokaż menu kontekstowe tylko w obszarze edytora
    const isEditorArea = target.closest('.editor-main') || 
                         target.closest('d2-wysiwyg-editor') ||
                         target.closest('.paper-container');
    if (isEditorArea) {
      event.preventDefault();
      this.closeAllMenus();
      this.contextSubmenu.set(null);

      // Wykryj czy kliknięto w komórkę tabeli
      const cellTarget = target.closest('td, th') as HTMLElement | null;
      this.contextMenuTargetCell.set(cellTarget);

      // Wykryj czy kliknięto w obraz
      const imgTarget = (target.tagName === 'IMG' ? target : target.closest('img')) as HTMLImageElement | null;
      this.contextMenuTargetImage.set(imgTarget);

      // Oblicz pozycję — menu zawsze w granicach viewportu (nie zasłania toolbara ani nie
      // wychodzi poza prawą/dolną krawędź). `Math.max(8, …)` jest kluczowe: bez dolnego
      // ograniczenia clamp dolnej krawędzi na niskim oknie dawał ujemne `y` i menu wjeżdżało
      // NAD viewport, zasłaniając toolbar (Issue: menu kontekstowe zasłania UI).
      const margin = 8;
      const menuWidth = 260;
      const menuHeight = 420;
      const maxX = Math.max(margin, window.innerWidth - menuWidth - margin);
      const maxY = Math.max(margin, window.innerHeight - menuHeight - margin);
      const x = Math.min(Math.max(margin, event.clientX), maxX);
      const y = Math.min(Math.max(margin, event.clientY), maxY);

      this.contextMenuX.set(x);
      this.contextMenuY.set(y);
      this.showContextMenu.set(true);
    }
  }

  /**
   * Zamyka wszystkie menu
   */
  closeAllMenus(): void {
    this.showMenu.set(false);
    this.showEditMenu.set(false);
    this.showFormatMenu.set(false);
    this.showInsertMenu.set(false);
    this.showToolsMenu.set(false);
    this.showViewMenu.set(false);
    this.showHelpMenu.set(false);
    this.activeSubmenu.set(null);
    this.showTemplates.set(false);
    this.showContextMenu.set(false);
    this.contextSubmenu.set(null);
    this.showShadingDropdown.set(false);
  }

  /**
   * "Zakończ": utrwala stan edytora i wykonuje SYNCHRONICZNĄ pierwszą próbę wysyłki na returnUrl,
   * pokazując komunikat „Trwa wysyłanie dokumentu ...". Sukces → status „Wysłano" + standardowe
   * zakończenie (zamknięcie karty). Błąd → modal „Wystąpiły problemy z dostarczeniem dokumentu"
   * z wyborem „Przerwij" / „Kontynuuj wysyłkę w tle". Guard `isFinishing` chroni przed dublami.
   */
  finishDocument(): void {
    this.showMenu.set(false);

    if (this.readOnly()) {
      this.showInfo('Tryb podglądu — dokument jest tylko do odczytu.');
      return;
    }

    const masterId = this.documentMasterId();
    const versionId = this.documentVersionId();
    if (!masterId || !versionId) {
      this.showError('Dokument nie jest powiązany z edytowalną wersją — nie można zakończyć i wysłać.');
      return;
    }

    if (this.isFinishing()) {
      return;
    }

    this.isFinishing.set(true);
    this.sendCancelled.set(false);
    this.sendDetachedFromUi.set(false);
    this.deliveryStatus.set('Sending');
    this.showSendErrorModal.set(false);
    this.showSendingModal.set(true); // „Trwa wysyłka pliku do aplikacji zewnętrznej"

    this.finishSendSub?.unsubscribe();
    this.finishSendSub = this.documentService.saveDocument(this.buildSaveRequest()).pipe(
      switchMap(blob => from(this.blobToBase64(blob))),
      switchMap(base64 => this.documentStorageService.finishAndSend(masterId, versionId, { content: base64 }))
    ).subscribe({
      next: result => {
        // Okno zamknięte przez użytkownika — wysyłka leci w tle, nie dotykamy już UI.
        if (this.sendDetachedFromUi()) return;

        this.deliveryId.set(result.deliveryId);
        this.deliveryStatus.set(result.status);

        if (result.delivered) {
          // „Wysłano" — kończymy jak dotychczas (zamknięcie karty / ekran końcowy).
          this.showSendingModal.set(false);
          this.enterFinishedAndCloseTab();
          return;
        }

        if (result.status === 'Sending') {
          // Wysyłka wciąż trwa (np. równoległa próba w toku) — poprawny stan PRZEJŚCIOWY, nie błąd.
          // Zostawiamy okno informacyjne; użytkownik może je zamknąć lub przerwać wysyłkę.
          return;
        }

        // Rzeczywisty błąd dostarczenia — poproś użytkownika o decyzję (Przerwij / Kontynuuj w tle).
        this.showSendingModal.set(false);
        this.showSendErrorModal.set(true);
      },
      error: () => {
        // Request przerwany przez użytkownika („Przerwij") albo okno zamknięte — to nie jest błąd.
        if (this.sendCancelled() || this.sendDetachedFromUi()) return;

        // Pierwsza próba nie powiodła się (sieć / błąd serwera) — ta sama ścieżka co błąd wysyłki.
        this.showSendingModal.set(false);
        this.deliveryStatus.set('RetryScheduled');
        this.showSendErrorModal.set(true);
      }
    });
  }

  /**
   * „Przerwij wysyłkę" w trakcie trwającej wysyłki: przerywa request w locie (HttpClient anuluje XHR
   * przy unsubscribe) i zgłasza anulowanie do backendu. Anulowanie NIE jest błędem — przechodzimy
   * w neutralny stan „anulowano" i wracamy do edytora. Działa także, gdy próba już trwa.
   */
  cancelSend(): void {
    this.sendCancelled.set(true);
    this.finishSendSub?.unsubscribe(); // anuluje request w locie — next/error się nie wywoła
    this.showSendingModal.set(false);

    const masterId = this.documentMasterId();
    if (!masterId) {
      this.afterSendCancelled();
      return;
    }

    this.documentStorageService.abortSend(masterId).subscribe({
      next: () => this.afterSendCancelled(),
      // Backend mógł nie móc anulować (np. zadanie przejęte przez workera) — dla użytkownika to nadal
      // anulowanie własnej wysyłki, a nie błąd techniczny. Pokazujemy neutralny stan anulowania.
      error: () => this.afterSendCancelled()
    });
  }

  /** Wspólne domknięcie anulowania: neutralny stan „anulowano", powrót do edytora (bez błędu). */
  private afterSendCancelled(): void {
    this.isFinishing.set(false);
    this.sendCancelled.set(false);
    this.deliveryStatus.set('Cancelled');
    this.showSuccess('Wysyłka anulowana. Możesz dalej edytować dokument.');
  }

  /**
   * „Zamknij" okno trwającej wysyłki: zamknięcie NIE jest błędem ani anulowaniem. Wysyłka biegnie
   * dalej (inline kończy się po stronie serwera / dokańcza worker), a UI przechodzi w neutralny stan
   * końcowy. `sendDetachedFromUi` pilnuje, by spóźniony wynik/błąd requestu nie wyskoczył jako modal.
   */
  closeSendingModal(): void {
    this.sendDetachedFromUi.set(true);
    this.showSendingModal.set(false);
    this.enterFinishedAndCloseTab();
  }

  /**
   * „Przerwij" (po nieudanej pierwszej próbie): anuluje zadanie wysyłki (status ANULOWANY),
   * ustawia dokument na „UzytkownikPrzerwałWysyłkę" i COFA użytkownika do edytora. Nie zamyka
   * karty i nie kontynuuje wysyłki w tle.
   */
  abortSend(): void {
    const masterId = this.documentMasterId();
    if (!masterId) return;

    this.documentStorageService.abortSend(masterId).subscribe({
      next: () => {
        this.showSendErrorModal.set(false);
        this.isFinishing.set(false); // powrót do edytora — „Zakończ" znów dostępne
        this.showSuccess('Wysyłka przerwana. Możesz dalej edytować dokument.');
      },
      error: () => {
        this.showError('Nie udało się przerwać wysyłki.');
      }
    });
  }

  /**
   * „Kontynuuj wysyłkę w tle" (po nieudanej pierwszej próbie): przekazuje zadanie do workera
   * (status „Zlecono do wysyłki"), zamyka kartę jak dotychczasowe zakończenie i NIE cofa do edytora.
   */
  continueSendInBackground(): void {
    const masterId = this.documentMasterId();
    if (!masterId) return;

    this.documentStorageService.continueDelivery(masterId).subscribe({
      next: result => {
        this.deliveryStatus.set(result.deliveryStatus);
        this.showSendErrorModal.set(false);
        this.enterFinishedAndCloseTab();
      },
      error: () => {
        this.showError('Nie udało się przekazać wysyłki do realizacji w tle.');
      }
    });
  }

  /**
   * Wspólne zakończenie pracy (sukces wysyłki / kontynuacja w tle): zatrzymuje auto-save, wchodzi
   * w blokujący stan końcowy (żeby nie wrócić do edycji zakończonego dokumentu) i próbuje zamknąć
   * kartę. Gdy przeglądarka odmówi (karta nieotwarta skryptem) — pokazujemy ekran z instrukcją.
   */
  private enterFinishedAndCloseTab(): void {
    this.isFinishing.set(false);
    this.stopAutoSave();
    this.autoSaveEnabled.set(false);
    this.workFinished.set(true);

    this.tryCloseBrowserTab();
  }

  /**
   * Best-effort zamknięcie karty. `window.close()` działa niezawodnie tylko dla okien
   * otwartych skryptem — gdy przeglądarka odmówi, NIE jest to błąd krytyczny: modal jest już
   * zamknięty, edytor zostaje w neutralnym stanie. Owijamy w try/catch, by nie wywrócić apki.
   */
  private tryCloseBrowserTab(): void {
    try {
      window.close();
    } catch {
      // Neutralny stan końcowy — brak dalszych akcji.
    }
  }

  /**
   * Ponowna próba zamknięcia karty z ekranu końcowego (przycisk „Zamknij kartę").
   * `window.close()` działa tylko dla kart otwartych skryptem (window.open) — gdy karta została
   * otwarta ręcznie/linkiem, Chrome odmawia. Jeśli po próbie strona nadal żyje, pokazujemy
   * wskazówkę o ręcznym zamknięciu (skrótem klawiaturowym).
   */
  closeFinishedTab(): void {
    this.tryCloseBrowserTab();
    clearTimeout(this.tabCloseHintTimer);
    this.tabCloseHintTimer = setTimeout(() => this.tabCloseBlocked.set(true), 300);
  }

  openReportEmail(): void {
    // Triggered from the Pomoc dropdown — close it like the other menu actions do.
    this.closeAllMenus();

    const subject = encodeURIComponent('[Qutas Editor] Zgłoszenie');
    const body = encodeURIComponent(
      `Dzień dobry,\n\n` +
      `proszę o opis problemu poniżej:\n\n` +
      `\n\n\n` +
      this.buildDiagnosticsBlock()
    );

    // Odbiorca z konfiguracji środowiska (pusty → klient poczty poprosi o adres).
    const to = environment.supportEmail ?? '';
    window.open(`mailto:${to}?subject=${subject}&body=${body}`, '_self');
  }

  /**
   * Kopiuje blok informacji diagnostycznych do schowka — siatka bezpieczeństwa, gdy
   * `mailto:` nie zadziała (brak skonfigurowanego klienta poczty, ucięcie długiej treści
   * przez przeglądarkę). Użytkownik może wkleić dane w dowolny kanał zgłoszenia.
   */
  copyDiagnostics(): void {
    this.closeAllMenus();
    const text = this.formatDiagnostics(this.collectDiagnosticRows());
    navigator.clipboard?.writeText(text)
      .then(() => this.notification.success('Skopiowano informacje diagnostyczne do schowka'))
      .catch(() => this.notification.error('Nie udało się skopiować informacji diagnostycznych'));
  }

  /**
   * Zbiera pary „etykieta : wartość" z danymi diagnostycznymi zgłoszenia.
   * Metody `buildInfo.*` czytane defensywnie (`?.()`) — część stubów testowych ich nie dostarcza.
   */
  private collectDiagnosticRows(): Array<[string, string]> {
    const masterId = this.documentMasterId() ?? '—';
    // VersionId aktualnie otwartej wersji edytowalnej (sygnał ustawiany z query param `versionId`).
    // Fallback „—" TYLKO gdy faktycznie brak wersji (tryb podglądu read-only bez versionId).
    const versionId = this.documentVersionId() ?? '—';

    const rows: Array<[string, string]> = [
      ['Data zgłoszenia',  new Date().toLocaleString('pl-PL')],
      ['Master ID',        masterId],
      ['Version ID',       versionId],
      ['Wersja aplikacji', this.buildInfo.buildNumber?.() ?? '—'],
      ['Data buildu',      this.buildInfo.buildDate?.() ?? '—'],
      ['Źródło wersji',    this.buildInfo.isApiData?.() ? 'API (health-check)' : 'front (fallback)'],
      ['Środowisko',       this.buildInfo.environment?.() ?? '—'],
      ['Połączenie',       navigator.onLine ? 'online' : 'offline'],
      ['URL',              window.location.href],
      ['Przeglądarka',     navigator.userAgent],
      ['Platforma',        (navigator as unknown as { platform?: string }).platform ?? '—'],
      ['Język',            navigator.language],
      ['Viewport',         `${window.innerWidth}×${window.innerHeight}`],
      ['Ekran',            `${window.screen.width}×${window.screen.height}`],
    ];

    // Ostatni błąd HTTP — najcenniejsza informacja diagnostyczna, gdy istnieje.
    const err = this.lastHttpError.lastError();
    if (err) {
      rows.push(['Ostatni błąd',  `HTTP ${err.status} ${err.method} ${err.url}`]);
      rows.push(['— czas',        err.at]);
      rows.push(['— szczegóły',   err.detail]);
    }

    return rows;
  }

  /** Formatuje pary diagnostyczne w wyrównaną kolumnę „etykieta : wartość". */
  private formatDiagnostics(rows: Array<[string, string]>): string {
    const labelWidth = Math.max(...rows.map(([k]) => k.length));
    return rows.map(([k, v]) => `  ${k.padEnd(labelWidth)} : ${v}`).join('\n');
  }

  /** Otoczony ramką blok diagnostyczny wklejany do treści maila. */
  private buildDiagnosticsBlock(): string {
    const separator = '────────────────────────────────────────────';
    return (
      `${separator}\n` +
      `  INFORMACJE DIAGNOSTYCZNE — proszę nie usuwać\n` +
      `${separator}\n` +
      `${this.formatDiagnostics(this.collectDiagnosticRows())}\n` +
      `${separator}\n`
    );
  }

  /**
   * Ustawia aktywne podmenu
   */
  setActiveSubmenu(submenu: string | null): void {
    this.activeSubmenu.set(submenu);
  }

  // =====================
  // MENU EDYTUJ
  // =====================

  /**
   * Cofnij
   */
  undo(): void {
    this.editor?.executeCommand('undo');
    this.closeAllMenus();
  }

  /**
   * Ponów
   */
  redo(): void {
    this.editor?.executeCommand('redo');
    this.closeAllMenus();
  }

  /**
   * Wytnij
   */
  cut(): void {
    document.execCommand('cut');
    this.closeAllMenus();
  }

  /**
   * Kopiuj
   */
  copy(): void {
    document.execCommand('copy');
    this.closeAllMenus();
  }

  /**
   * Wklej
   */
  paste(): void {
    // Capture the target selection synchronously, before the async clipboard read
    // and the menu teardown move it (see pasteWithoutFormatting for the details).
    const target = this.editor?.captureSelectionBookmark() ?? null;
    this.closeAllMenus();
    navigator.clipboard.readText()
      .then(text => this.editor?.pastePlainTextAt(target, text))
      .catch(() => { document.execCommand('paste'); });
  }

  /**
   * Wklej bez formatowania
   */
  pasteWithoutFormatting(): void {
    // Snapshot the target selection synchronously — BEFORE closing the menu and the
    // async clipboard read. Otherwise the paste lands at whatever selection is live
    // after the await (historically the source range), which also made the text keep
    // the source formatting. readText() yields text/plain only, so formatting is
    // dropped by construction. On denied clipboard we fail quietly (Ctrl+Shift+V works).
    const target = this.editor?.captureSelectionBookmark() ?? null;
    this.closeAllMenus();
    navigator.clipboard.readText()
      .then(text => this.editor?.pastePlainTextAt(target, text))
      .catch(() => { /* brak dostępu do schowka */ });
  }

  /**
   * Zaznacz wszystko
   */
  selectAll(): void {
    this.editor?.executeCommand('selectAll');
    this.closeAllMenus();
  }

  /**
   * Usuń zaznaczenie
   */
  deleteSelection(): void {
    document.execCommand('delete');
    this.closeAllMenus();
  }

  /**
   * Globalne skróty klawiszowe edycji: Ctrl/Cmd+F → Znajdź (oba tryby, w read-only bez
   * zamiany), Ctrl/Cmd+H → Znajdź i zamień, Ctrl/Cmd+A → zaznacz treść dokumentu oraz
   * Ctrl/Cmd+Z/Y/X/C/V → cofnij/ponów/wytnij/kopiuj/wklej. Skróty edycyjne działają tylko
   * poza polami formularzy i contenteditable: wewnątrz stron edytora Ctrl+Z/Y obsługuje
   * keydown wysiwyg-editora, a X/C/V natywny mechanizm przeglądarki (zdarzenia cut/copy/
   * paste, w tym handlePaste); tu domykamy przypadek fokusu poza kartką (toolbar, tło).
   */
  @HostListener('document:keydown', ['$event'])
  onGlobalKeydown(e: KeyboardEvent): void {
    // e.altKey odfiltrowuje AltGr (Ctrl+Alt na Windows) — polskie znaki ż/ź/ć nie mogą
    // wyzwalać skrótów.
    if (!(e.ctrlKey || e.metaKey) || e.altKey) return;
    const key = e.key.toLowerCase();
    if (key === 'f') {
      e.preventDefault();
      this.openFindReplace();
      return;
    }
    if (key === 'h' && !this.editingDisabled()) {
      e.preventDefault();
      this.openFindReplace();
      return;
    }
    if (key === 'a') {
      // Ctrl+A → zaznacz tylko treść dokumentu (nie całe body z menu/paskami).
      // Pomijamy pola formularzy, by nie psuć natywnego zaznaczania w inputach.
      const tag = (e.target as HTMLElement)?.tagName;
      if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;
      e.preventDefault();
      this.selectAll();
      return;
    }

    // Pozostałe skróty nie mogą przechwytywać natywnej edycji w polach formularzy ani
    // w contenteditable (strony dokumentu, edytory nagłówka/stopki). Obok
    // isContentEditable sprawdzamy atrybut przez closest — jsdom (testy) nie
    // implementuje isContentEditable.
    const target = e.target as HTMLElement | null;
    const tag = target?.tagName;
    const inContentEditable = !!target && (target.isContentEditable
      || !!target.closest?.('[contenteditable=""], [contenteditable="true"], [contenteditable="plaintext-only"]'));
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || inContentEditable) {
      return;
    }

    switch (key) {
      case 'z':
        if (this.editingDisabled()) return;
        e.preventDefault();
        if (e.shiftKey) {
          this.redo();
        } else {
          this.undo();
        }
        break;
      case 'y':
        if (this.editingDisabled()) return;
        e.preventDefault();
        this.redo();
        break;
      case 'x':
        if (this.editingDisabled()) return;
        e.preventDefault();
        this.cut();
        break;
      case 'c':
        e.preventDefault();
        this.copy();
        break;
      case 'v':
        if (this.editingDisabled()) return;
        e.preventDefault();
        this.paste();
        break;
    }
  }

  /**
   * Otwiera dialog Znajdź i zamień
   */
  openFindReplace(): void {
    this.showFindReplace.set(true);
    // Wyszukiwanie (jawnie wywołane) przejmuje dokowany obszar po lewej.
    this.showTablePanel.set(false);
    this.closeAllMenus();
  }

  // =====================
  // MENU FORMATUJ
  // =====================

  /**
   * Pogrubienie
   */
  toggleBold(): void {
    this.editor?.executeCommand('bold');
    this.closeAllMenus();
  }

  /**
   * Kursywa
   */
  toggleItalic(): void {
    this.editor?.executeCommand('italic');
    this.closeAllMenus();
  }

  /**
   * Podkreślenie
   */
  toggleUnderline(): void {
    this.editor?.executeCommand('underline');
    this.closeAllMenus();
  }

  /**
   * Przekreślenie
   */
  toggleStrikethrough(): void {
    this.editor?.executeCommand('strikethrough');
    this.closeAllMenus();
  }

  /**
   * Indeks górny
   */
  toggleSuperscript(): void {
    this.editor?.executeCommand('superscript');
    this.closeAllMenus();
  }

  /**
   * Indeks dolny
   */
  toggleSubscript(): void {
    this.editor?.executeCommand('subscript');
    this.closeAllMenus();
  }

  /**
   * Zwiększ rozmiar czcionki
   */
  increaseFontSize(): void {
    const currentSize = this.editorState()?.currentStyle?.fontSize || 11;
    this.editor?.setFontSize(currentSize + 1);
    this.closeAllMenus();
  }

  /**
   * Zmniejsz rozmiar czcionki
   */
  decreaseFontSize(): void {
    const currentSize = this.editorState()?.currentStyle?.fontSize || 11;
    if (currentSize > 1) {
      this.editor?.setFontSize(currentSize - 1);
    }
    this.closeAllMenus();
  }

  /**
   * Zmień na wielkie litery
   */
  toUpperCase(): void {
    const selection = window.getSelection();
    if (selection && selection.toString()) {
      const text = selection.toString().toUpperCase();
      document.execCommand('insertText', false, text);
    }
    this.closeAllMenus();
  }

  /**
   * Zmień na małe litery
   */
  toLowerCase(): void {
    const selection = window.getSelection();
    if (selection && selection.toString()) {
      const text = selection.toString().toLowerCase();
      document.execCommand('insertText', false, text);
    }
    this.closeAllMenus();
  }

  /**
   * Zmień na Kapitaliki (każde słowo z wielkiej litery)
   */
  toTitleCase(): void {
    const selection = window.getSelection();
    if (selection && selection.toString()) {
      const text = selection.toString().replace(/\b\w/g, l => l.toUpperCase());
      document.execCommand('insertText', false, text);
    }
    this.closeAllMenus();
  }

  /**
   * Wyrównaj do lewej
   */
  alignLeft(): void {
    this.editor?.executeCommand('justifyLeft');
    this.closeAllMenus();
  }

  /**
   * Wyrównaj do środka
   */
  alignCenter(): void {
    this.editor?.executeCommand('justifyCenter');
    this.closeAllMenus();
  }

  /**
   * Wyrównaj do prawej
   */
  alignRight(): void {
    this.editor?.executeCommand('justifyRight');
    this.closeAllMenus();
  }

  /**
   * Wyjustuj
   */
  alignJustify(): void {
    this.editor?.executeCommand('justifyFull');
    this.closeAllMenus();
  }

  /**
   * Zwiększ wcięcie
   */
  increaseIndent(): void {
    this.editor?.executeCommand('indent');
    this.closeAllMenus();
  }

  /**
   * Zmniejsz wcięcie
   */
  decreaseIndent(): void {
    this.editor?.executeCommand('outdent');
    this.closeAllMenus();
  }

  /**
   * Interlinia pojedyncza
   */
  setLineSpacingSingle(): void {
    this.setLineSpacing(1);
  }

  /**
   * Interlinia 1.15
   */
  setLineSpacing115(): void {
    this.setLineSpacing(1.15);
  }

  /**
   * Interlinia 1.5
   */
  setLineSpacing15(): void {
    this.setLineSpacing(1.5);
  }

  /**
   * Interlinia podwójna
   */
  setLineSpacingDouble(): void {
    this.setLineSpacing(2);
  }

  /**
   * Ustawia interlinię (mnożnik Worda). Wartość renderowa jest kalibrowana metrykami
   * fontu + marker --w-line-tw dla round-tripu (PG-09) — ta sama semantyka co reader,
   * dzięki czemu wygląd nie zmienia się po zapisie i ponownym otwarciu.
   */
  private setLineSpacing(value: number): void {
    const selection = window.getSelection();
    if (selection && selection.rangeCount > 0) {
      const range = selection.getRangeAt(0);
      let block = range.startContainer as Node;
      if (block.nodeType === Node.TEXT_NODE) {
        block = block.parentNode!;
      }
      // Znajdź blok
      while (block && !['P', 'DIV', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'LI'].includes((block as HTMLElement).tagName)) {
        block = block.parentNode!;
      }
      if (block) {
        applyWordLineSpacing(block as HTMLElement, value);
        // Zmiana interlinii zmienia układ i musi trafić do zapisu (persist + repaginacja).
        this.notifyEditorChange();
      }
    }
    this.closeAllMenus();
  }

  /**
   * Dodaj odstęp przed akapitem
   */
  addSpaceBefore(): void {
    this.setBlockSpacing('marginTop', '12pt');
  }

  /**
   * Usuń odstęp przed akapitem
   */
  removeSpaceBefore(): void {
    this.setBlockSpacing('marginTop', '0');
  }

  /**
   * Dodaj odstęp po akapicie
   */
  addSpaceAfter(): void {
    this.setBlockSpacing('paddingBottom', '12pt');
  }

  /**
   * Usuń odstęp po akapicie
   */
  removeSpaceAfter(): void {
    this.setBlockSpacing('paddingBottom', '0');
  }

  /**
   * Ustawia odstęp bloku. Odstęp PO akapicie = padding-bottom (ADR-0053: sumuje się
   * z margin-top następnego jak w Wordzie; marginesy CSS kolapsują do max).
   */
  private setBlockSpacing(property: 'marginTop' | 'paddingBottom', value: string): void {
    const selection = window.getSelection();
    if (selection && selection.rangeCount > 0) {
      const range = selection.getRangeAt(0);
      let block = range.startContainer as Node;
      if (block.nodeType === Node.TEXT_NODE) {
        block = block.parentNode!;
      }
      while (block && !['P', 'DIV', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'LI'].includes((block as HTMLElement).tagName)) {
        block = block.parentNode!;
      }
      if (block) {
        (block as HTMLElement).style[property] = value;
        // Odstęp „po" mógł dotąd siedzieć w margin-bottom (treść sprzed ADR-0053,
        // akapity z tłem) — czyścimy, żeby się nie dublował z padding-bottom.
        if (property === 'paddingBottom') {
          (block as HTMLElement).style.marginBottom = '';
        }
      }
    }
    this.closeAllMenus();
  }

  /**
   * Lista punktowana
   */
  insertBulletList(): void {
    this.editor?.executeCommand('insertUnorderedList');
    this.closeAllMenus();
  }

  /**
   * Lista numerowana
   */
  insertNumberedList(): void {
    this.editor?.executeCommand('insertOrderedList');
    this.closeAllMenus();
  }

  /**
   * Wyczyść formatowanie
   */
  clearFormatting(): void {
    this.editor?.executeCommand('removeFormat');
    this.closeAllMenus();
  }

  /**
   * Otwiera dialog wstawiania kodu kreskowego / QR
   */
  openBarcodeDialog(): void {
    // Zapisz selekcję przed otwarciem dialogu - dialog zabierze fokus z edytora
    this.editor?.saveSelection();
    this.showBarcodeDialog.set(true);
    this.closeAllMenus();
  }

  /**
   * Wstawia kod kreskowy / QR do edytora
   */
  onInsertBarcode(event: { base64Image: string; content: string; showValueBelow: boolean }): void {
    if (this.editor) {
      // Przywróć fokus i selekcję w edytorze przed wstawieniem
      this.editor.focus();
      this.editor.restoreSelection();
      if (event.showValueBelow) {
        this.editor.insertBarcodeWithValue(event.base64Image, event.content);
      } else {
        this.editor.insertImage(event.base64Image, 'barcode');
      }
    }
    this.showBarcodeDialog.set(false);
  }

  /**
   * Zamyka dialog kodu kreskowego
   */
  closeBarcodeDialog(): void {
    this.showBarcodeDialog.set(false);
  }

  /**
   * Wstawia linię poziomą
   */
  insertHorizontalLine(): void {
    this.editor?.insertHorizontalRule();
    this.closeAllMenus();
  }

  /**
   * Wstawia podział strony
   */
  insertPageBreak(): void {
    this.editor?.insertPageBreak();
    this.closeAllMenus();
  }

  /**
   * Ustawia liczbę kolumn układu dokumentu (sekcja bazowa). 1 = jedna kolumna (ADR-0039).
   */
  setColumns(count: number): void {
    this.editor?.setBaseColumns(count);
    this.closeAllMenus();
  }

  /** Aktualna liczba kolumn sekcji bazowej — do podświetlenia wyboru w menu. */
  currentColumnCount(): number {
    return this.editor?.getBaseColumnCount() ?? 1;
  }

  /**
   * Wstawia podział kolumny w pozycji kursora (dalsza treść → następna kolumna).
   */
  insertColumnBreak(): void {
    this.editor?.insertColumnBreak();
    this.closeAllMenus();
  }

  /**
   * Rozpoczyna edycję nagłówka
   */
  editHeader(): void {
    this.editor?.startEditingHeader();
  }

  /**
   * Rozpoczyna edycję stopki
   */
  editFooter(): void {
    this.editor?.startEditingFooter();
  }

  // =====================
  // ZNAJDŹ I ZAMIEŃ
  // =====================
  findText = signal('');
  replaceText = signal('');
  /** Liczba trafień i indeks bieżącego (do wyświetlenia „x z y" w panelu). */
  findResultCount = signal(0);
  findCurrentIndex = signal(-1);
  /** Lista wyników do panelu „Wyszukiwanie" — fragmenty tekstu z kontekstem wokół trafienia. */
  searchResults = signal<{ before: string; match: string; after: string }[]>([]);

  /** Odświeża licznik + listę wyników na podstawie aktualnych podświetleń edytora. */
  private refreshSearchResults(result: { count: number; currentIndex: number }): void {
    this.findResultCount.set(result.count);
    this.findCurrentIndex.set(result.currentIndex);
    this.searchResults.set(this.editor?.getSearchSnippets() ?? []);
  }

  /**
   * Wyszukiwanie na żywo podczas wpisywania — podświetla wszystkie trafienia i przewija
   * do pierwszego (przez prawdziwe API edytora, nie ułomne `window.find`).
   */
  onFindInput(value: string): void {
    this.findText.set(value);
    if (!value || !this.editor) {
      this.editor?.clearSearchHighlights();
      this.lastSearchText = '';
      this.findResultCount.set(0);
      this.findCurrentIndex.set(-1);
      this.searchResults.set([]);
      return;
    }
    this.lastSearchText = value;
    this.refreshSearchResults(this.editor.searchText(value, 'next'));
  }

  /** Następne trafienie (pierwsze wyszukanie, jeśli tekst się zmienił). */
  findNext(): void {
    const text = this.findText();
    if (!text || !this.editor) return;
    const result = text !== this.lastSearchText
      ? (this.lastSearchText = text, this.editor.searchText(text, 'next'))
      : this.editor.findNext();
    this.refreshSearchResults(result);
  }

  /** Poprzednie trafienie. */
  findPrev(): void {
    const text = this.findText();
    if (!text || !this.editor) return;
    const result = text !== this.lastSearchText
      ? (this.lastSearchText = text, this.editor.searchText(text, 'previous'))
      : this.editor.findPrevious();
    this.refreshSearchResults(result);
  }

  /** Klik na wyniku w panelu — skok do trafienia (podświetlenie + przewinięcie). */
  goToResult(index: number): void {
    const result = this.editor?.goToMatch(index);
    if (result) this.findCurrentIndex.set(result.currentIndex);
  }

  /** Zamknij panel wyszukiwania i wyczyść podświetlenia. */
  closeFindReplace(): void {
    this.showFindReplace.set(false);
    this.editor?.clearSearchHighlights();
    this.lastSearchText = '';
    this.findResultCount.set(0);
    this.findCurrentIndex.set(-1);
    this.searchResults.set([]);
    // Jeśli karetka nadal jest w tabeli, przywróć panel tabeli w zwolnionym doku.
    this.syncTablePanel();
  }

  /**
   * Zamienia bieżące trafienie (tylko gdy edycja dozwolona).
   */
  replaceOne(): void {
    if (this.editingDisabled() || !this.editor || !this.findText()) return;
    if (this.findText() !== this.lastSearchText) {
      this.findNext();
      return;
    }
    const result = this.editor.replaceCurrentMatch(this.replaceText());
    this.findResultCount.set(result.count);
    this.findCurrentIndex.set(result.currentIndex);
  }

  /**
   * Zamienia wszystkie trafienia (tylko gdy edycja dozwolona).
   */
  replaceAll(): void {
    if (this.editingDisabled() || !this.editor || !this.findText()) return;
    if (this.findText() !== this.lastSearchText) {
      this.lastSearchText = this.findText();
      this.editor.searchText(this.findText(), 'next');
    }
    const result = this.editor.replaceAllMatches(this.replaceText());
    this.findResultCount.set(result.count);
    this.findCurrentIndex.set(result.currentIndex);
    this.showSuccess(`Zamieniono wszystkie wystąpienia "${this.findText()}"`);
  }

  /**
   * Zamyka menu po kliknięciu poza
   */
  closeMenuOnOutsideClick(event: MouseEvent): void {
    this.showMenu.set(false);
    this.showTemplates.set(false);
  }

  /**
   * Otwiera dialog ustawień strony
   */
  openPageSetup(): void {
    this.showPageSetup.set(true);
    this.showMenu.set(false);
  }

  /**
   * Ustawia preset marginesów
   */
  applyMarginPreset(preset: { name: string; margins: PageMargins }): void {
    this.pageSettings.update(s => ({
      ...s,
      margins: { ...preset.margins }
    }));
  }

  /**
   * Aktualizuje pojedynczy margines
   */
  updateMargin(side: keyof PageMargins, value: number): void {
    this.pageSettings.update(s => ({
      ...s,
      margins: { ...s.margins, [side]: value }
    }));
  }

  /**
   * Pobiera style marginesów w pikselach
   */
  getMarginStyles(): { [key: string]: string } {
    const m = this.pageSettings().margins;
    const cmToPx = CSS_PX_PER_CM;
    return {
      'padding-top': `${m.top * cmToPx}px`,
      'padding-bottom': `${m.bottom * cmToPx}px`,
      'padding-left': `${m.left * cmToPx}px`,
      'padding-right': `${m.right * cmToPx}px`
    };
  }

  /**
   * Zmienia orientację strony
   */
  setOrientation(orientation: 'portrait' | 'landscape'): void {
    this.pageSettings.update(s => ({ ...s, orientation }));
  }

  /**
   * Sprawdza czy preset marginesów jest aktywny
   */
  isPresetActive(preset: { name: string; margins: PageMargins }): boolean {
    const current = this.pageSettings().margins;
    return current.top === preset.margins.top &&
           current.bottom === preset.margins.bottom &&
           current.left === preset.margins.left &&
           current.right === preset.margins.right;
  }

  /**
   * Pobiera style dla podglądu presetu
   */
  getPresetPreviewStyle(preset: { name: string; margins: PageMargins }): { [key: string]: string } {
    const m = preset.margins;
    const scale = 2; // Skala dla miniaturki
    return {
      'padding': `${m.top * scale}px ${m.right * scale}px ${m.bottom * scale}px ${m.left * scale}px`
    };
  }

  /**
   * Pobiera style dla podglądu strony
   */
  getPreviewStyle(): { [key: string]: string } {
    const settings = this.pageSettings();
    const isLandscape = settings.orientation === 'landscape';
    
    return {
      'width': isLandscape ? '140px' : '100px',
      'height': isLandscape ? '100px' : '140px'
    };
  }

  /**
   * Pobiera style dla obszaru zawartości w podglądzie
   */
  getContentPreviewStyle(): { [key: string]: string } {
    const m = this.pageSettings().margins;
    const scale = 4; // Skala dla podglądu
    return {
      'padding-top': `${m.top * scale}px`,
      'padding-bottom': `${m.bottom * scale}px`,
      'padding-left': `${m.left * scale}px`,
      'padding-right': `${m.right * scale}px`
    };
  }

  /**
   * Aplikuje ustawienia strony do edytora
   */
  applyPageSettings(): void {
    // Marginesy zostaną przekazane do edytora przez style
    this.showPageSetup.set(false);
    this.showSuccess('Zastosowano ustawienia strony');
  }

  // ================================
  // Dialog Nagłówka i Stopki
  // ================================

  /**
   * Otwiera dialog nagłówka i stopki
   */
  onOpenHeaderFooterSettings(data: {
    headerMargin: number;
    footerMargin: number;
    differentFirstPage: boolean;
    differentOddEven: boolean;
  }): void {
    this.headerFooterDialogData.set(data);
    this.showHeaderFooterDialog.set(true);
  }

  /**
   * Zamyka dialog nagłówka i stopki
   */
  closeHeaderFooterDialog(): void {
    this.showHeaderFooterDialog.set(false);
  }

  /**
   * Aktualizuje dane dialogu
   */
  updateHeaderFooterDialogData(field: string, value: number | boolean): void {
    this.headerFooterDialogData.update(data => ({
      ...data,
      [field]: value
    }));
  }

  /**
   * Zatwierdza ustawienia nagłówka i stopki
   */
  applyHeaderFooterSettings(): void {
    const data = this.headerFooterDialogData();
    this.editor?.applyHeaderFooterSettings(data);
    this.closeHeaderFooterDialog();
  }

  // =====================
  // MINI TOOLBAR
  // =====================

  onEditorMouseUp(event: MouseEvent): void {
    // Nie pokazuj jeśli otwarte jest menu kontekstowe
    if (this.showContextMenu()) return;

    setTimeout(() => {
      const selection = window.getSelection();
      if (!selection || selection.isCollapsed || selection.rangeCount === 0) {
        this.showMiniToolbar.set(false);
        return;
      }
      const range = selection.getRangeAt(0);
      const rect = range.getBoundingClientRect();
      if (rect.width === 0) {
        this.showMiniToolbar.set(false);
        return;
      }

      const toolbarWidth = 560;
      const toolbarHeight = 76;
      const margin = 8;

      let x = rect.left + rect.width / 2 - toolbarWidth / 2;
      let y = rect.top - toolbarHeight - margin;

      // Nie wychodź poza lewą/prawą krawędź ekranu
      x = Math.max(margin, Math.min(x, window.innerWidth - toolbarWidth - margin));
      // Jeśli nie mieści się nad — pokaż pod
      if (y < margin) {
        y = rect.bottom + margin;
      }

      this.miniToolbarX.set(x);
      this.miniToolbarY.set(y);
      this.showMiniToolbar.set(true);
    }, 10);
  }

  onEditorMouseDown(event: MouseEvent): void {
    const target = event.target as HTMLElement;
    if (!target.closest('.mini-toolbar')) {
      this.showMiniToolbar.set(false);
    }
  }

  /** Zapobiega utracie selekcji w edytorze przy klikaniu w mini-toolbar,
   *  ale pozwala INPUT i SELECT na normalne działanie.
   *  Dla INPUT/SELECT selekcja jest zapisywana PRZED przeniesieniem focusu
   *  przez przeglądarkę (mousedown odpala się przed blur edytora). */
  onMiniToolbarMouseDown(event: MouseEvent): void {
    const target = event.target as HTMLElement;
    if (target.tagName === 'INPUT' || target.tagName === 'SELECT') {
      // Zapisz selekcję zanim focus przejdzie do kontrolki i edytor ją wyczyści
      this.editor?.saveSelection();
    } else {
      event.preventDefault();
    }
  }

  /**
   * Stan przycisków wyrównania (mini-toolbar / menu kontekstowe) — jak w Wordzie
   * dokładnie jeden aktywny; brak jawnego text-align = „do lewej".
   */
  alignmentActive(align: 'left' | 'center' | 'right' | 'justify'): boolean {
    return (this.editorState()?.currentFormatting?.alignment ?? 'left') === align;
  }

  miniToolbarCommand(command: string): void {
    this.editor?.executeCommand(command as any);
    // Nie zamykaj — użytkownik może kliknąć kolejny przycisk
    setTimeout(() => {
      const selection = window.getSelection();
      if (!selection || selection.isCollapsed) {
        this.showMiniToolbar.set(false);
      }
    }, 50);
  }

  miniToolbarSetFontFamily(family: string): void {
    this.editor?.setFontFamily(family);
  }

  miniToolbarSetFontSize(event: Event): void {
    const val = parseInt((event.target as HTMLInputElement).value, 10);
    if (!isNaN(val) && val > 0) {
      this.editor?.setFontSize(val);
    }
  }

  miniToolbarIncreaseFontSize(): void {
    const current = this.editorState()?.currentStyle?.fontSize ?? 11;
    this.editor?.setFontSize(current + 1);
  }

  miniToolbarDecreaseFontSize(): void {
    const current = this.editorState()?.currentStyle?.fontSize ?? 11;
    if (current > 1) this.editor?.setFontSize(current - 1);
  }

  miniToolbarSetTextColor(color: string): void {
    this.editor?.setTextColor(color);
  }

  miniToolbarSetHighlightColor(color: string): void {
    this.editor?.setBackgroundColor(color);
  }

  miniToolbarCut(): void {
    document.execCommand('cut');
  }

  miniToolbarCopy(): void {
    document.execCommand('copy');
  }

  miniToolbarPaste(): void {
    const target = this.editor?.captureSelectionBookmark() ?? null;
    navigator.clipboard.readText()
      .then(text => this.editor?.pastePlainTextAt(target, text))
      .catch(() => document.execCommand('paste'));
  }

  miniToolbarIncreaseIndent(): void {
    this.editor?.executeCommand('indent');
  }

  miniToolbarDecreaseIndent(): void {
    this.editor?.executeCommand('outdent');
  }

  // =====================
  // MENU KONTEKSTOWE
  // =====================

  closeContextMenu(): void {
    this.showContextMenu.set(false);
    this.contextSubmenu.set(null);
  }

  contextMenuToggleBold(): void {
    this.editor?.executeCommand('bold');
    this.closeContextMenu();
  }

  contextMenuToggleItalic(): void {
    this.editor?.executeCommand('italic');
    this.closeContextMenu();
  }

  contextMenuToggleUnderline(): void {
    this.editor?.executeCommand('underline');
    this.closeContextMenu();
  }

  contextMenuAlignLeft(): void {
    this.editor?.executeCommand('justifyLeft');
    this.closeContextMenu();
  }

  contextMenuAlignCenter(): void {
    this.editor?.executeCommand('justifyCenter');
    this.closeContextMenu();
  }

  contextMenuAlignRight(): void {
    this.editor?.executeCommand('justifyRight');
    this.closeContextMenu();
  }

  contextMenuAlignJustify(): void {
    this.editor?.executeCommand('justifyFull');
    this.closeContextMenu();
  }

  contextMenuSetLineSpacing(value: number): void {
    this.setLineSpacing(value);
    this.closeContextMenu();
  }

  contextMenuIncreaseIndent(): void {
    this.editor?.executeCommand('indent');
    this.closeContextMenu();
  }

  contextMenuDecreaseIndent(): void {
    this.editor?.executeCommand('outdent');
    this.closeContextMenu();
  }

  /**
   * Ustawia kolor tła komórki tabeli (context menu + toolbar)
   */
  setCellColor(color: string): void {
    // Użyj custom zaznaczenia lub aktywnej/target komórki
    const customSelected = this.selectedCells();
    if (customSelected.size > 0) {
      customSelected.forEach(c => (c as HTMLElement).style.backgroundColor = color);
    } else {
      const cell = this.contextMenuTargetCell() || this.activeTableCell();
      if (cell) {
        (cell as HTMLElement).style.backgroundColor = color;
      }
    }
    this.closeContextMenu();
    this.showShadingDropdown.set(false);
    this.notifyEditorChange();
  }

  /**
   * Czyści kolor tła komórki tabeli
   */
  clearCellColor(): void {
    const customSelected = this.selectedCells();
    if (customSelected.size > 0) {
      customSelected.forEach(c => (c as HTMLElement).style.backgroundColor = '');
    } else {
      const cell = this.contextMenuTargetCell() || this.activeTableCell();
      if (cell) {
        (cell as HTMLElement).style.backgroundColor = '';
      }
    }
    this.closeContextMenu();
    this.showShadingDropdown.set(false);
    this.notifyEditorChange();
  }

  // =====================
  // TABELA – MENU KONTEKSTOWE
  // =====================

  private getContextCell(): HTMLElement | null {
    return this.contextMenuTargetCell() || this.activeTableCell();
  }

  /**
   * Nowa komórka przejmuje inline style komórki-wzorca (padding/border z DOCX) —
   * goły <td> spadał na domyślne CSS edytora i wiersz miał inne wcięcia niż reszta
   * tabeli w edytorze oraz inne tcMar po zapisie.
   */
  private createCellLike(reference: HTMLTableCellElement | undefined): HTMLTableCellElement {
    const td = document.createElement('td');
    const style = reference?.getAttribute('style');
    if (style) td.setAttribute('style', style);
    td.innerHTML = '<br>';
    return td;
  }

  contextMenuInsertRowAbove(): void {
    const cell = this.getContextCell();
    if (!cell) { this.closeContextMenu(); return; }
    const row = cell.closest('tr');
    if (!row) { this.closeContextMenu(); return; }
    const table = row.closest('table')!;
    const colspan = row.querySelectorAll('td, th').length;
    const newRow = row.cloneNode(false) as HTMLTableRowElement;
    for (let i = 0; i < colspan; i++) {
      newRow.appendChild(this.createCellLike(row.cells[i]));
    }
    table.querySelector('tbody')?.insertBefore(newRow, row) || row.parentNode?.insertBefore(newRow, row);
    this.notifyEditorChange();
    this.closeContextMenu();
  }

  contextMenuInsertRowBelow(): void {
    const cell = this.getContextCell();
    if (!cell) { this.closeContextMenu(); return; }
    const row = cell.closest('tr');
    if (!row) { this.closeContextMenu(); return; }
    const table = row.closest('table')!;
    const colspan = row.querySelectorAll('td, th').length;
    const newRow = row.cloneNode(false) as HTMLTableRowElement;
    for (let i = 0; i < colspan; i++) {
      newRow.appendChild(this.createCellLike(row.cells[i]));
    }
    const nextSibling = row.nextSibling;
    nextSibling ? row.parentNode?.insertBefore(newRow, nextSibling) : row.parentNode?.appendChild(newRow);
    this.notifyEditorChange();
    this.closeContextMenu();
  }

  contextMenuInsertColLeft(): void {
    const cell = this.getContextCell();
    if (!cell) { this.closeContextMenu(); return; }
    const table = cell.closest('table');
    if (!table) { this.closeContextMenu(); return; }
    const colIndex = (cell as HTMLTableCellElement).cellIndex;
    table.querySelectorAll('tr').forEach(row => {
      const ref = row.cells[colIndex];
      const newTd = this.createCellLike(ref);
      if (ref) row.insertBefore(newTd, ref);
      else row.appendChild(newTd);
    });
    this.notifyEditorChange();
    this.closeContextMenu();
  }

  contextMenuInsertColRight(): void {
    const cell = this.getContextCell();
    if (!cell) { this.closeContextMenu(); return; }
    const table = cell.closest('table');
    if (!table) { this.closeContextMenu(); return; }
    const colIndex = (cell as HTMLTableCellElement).cellIndex;
    table.querySelectorAll('tr').forEach(row => {
      const ref = row.cells[colIndex];
      const newTd = this.createCellLike(ref);
      if (ref?.nextSibling) row.insertBefore(newTd, ref.nextSibling);
      else row.appendChild(newTd);
    });
    this.notifyEditorChange();
    this.closeContextMenu();
  }

  contextMenuDeleteRow(): void {
    const cell = this.getContextCell();
    const row = cell?.closest('tr');
    row?.parentNode?.removeChild(row);
    this.notifyEditorChange();
    this.closeContextMenu();
  }

  contextMenuDeleteCol(): void {
    const cell = this.getContextCell();
    if (!cell) { this.closeContextMenu(); return; }
    const table = cell.closest('table');
    const colIndex = (cell as HTMLTableCellElement).cellIndex;
    table?.querySelectorAll('tr').forEach(row => {
      const td = row.cells[colIndex];
      if (td) row.removeChild(td);
    });
    this.notifyEditorChange();
    this.closeContextMenu();
  }

  contextMenuDeleteTable(): void {
    const cell = this.getContextCell();
    const table = cell?.closest('table');
    table?.parentNode?.removeChild(table);
    this.notifyEditorChange();
    this.closeContextMenu();
  }

  // =====================
  // GRAFIKA – MENU KONTEKSTOWE
  // =====================

  contextMenuAlignImageLeft(): void {
    const img = this.contextMenuTargetImage();
    if (img) {
      img.style.display = 'block';
      img.style.marginLeft = '0';
      img.style.marginRight = 'auto';
      img.style.float = '';
    }
    this.notifyEditorChange();
    this.closeContextMenu();
  }

  contextMenuAlignImageCenter(): void {
    const img = this.contextMenuTargetImage();
    if (img) {
      img.style.display = 'block';
      img.style.marginLeft = 'auto';
      img.style.marginRight = 'auto';
      img.style.float = '';
    }
    this.notifyEditorChange();
    this.closeContextMenu();
  }

  contextMenuAlignImageRight(): void {
    const img = this.contextMenuTargetImage();
    if (img) {
      img.style.display = 'block';
      img.style.marginLeft = 'auto';
      img.style.marginRight = '0';
      img.style.float = '';
    }
    this.notifyEditorChange();
    this.closeContextMenu();
  }

  /**
   * Pobiera zaznaczone komórki tabeli (z custom cell selection)
   */
  private getSelectedCells(selection: Selection, editor: HTMLElement): HTMLElement[] {
    // Użyj custom zaznaczenia komórek
    const customSelected = this.selectedCells();
    if (customSelected.size > 0) {
      return Array.from(customSelected);
    }

    // Fallback: aktywna komórka
    const cell = this.activeTableCell();
    return cell ? [cell] : [];
  }

  // =====================
  // MENU NARZĘDZIA
  // =====================

  toggleToolsMenu(): void {
    const wasOpen = this.showToolsMenu();
    this.closeAllMenus();
    this.showToolsMenu.set(!wasOpen);
  }

  // =====================
  // MENU WIDOK
  // =====================

  toggleViewMenu(): void {
    const wasOpen = this.showViewMenu();
    this.closeAllMenus();
    this.showViewMenu.set(!wasOpen);
  }

  toggleHelpMenu(): void {
    const wasOpen = this.showHelpMenu();
    this.closeAllMenus();
    this.showHelpMenu.set(!wasOpen);
  }

  toggleRuler(): void {
    this.showRuler.set(!this.showRuler());
    this.closeAllMenus();
  }

  toggleMarginGuides(): void {
    this.showMarginGuides.set(!this.showMarginGuides());
    this.closeAllMenus();
  }

  /**
   * Obsługuje zmianę marginesów z linijki (drag & drop)
   */
  onRulerMarginsChange(margins: PageMargins): void {
    this.pageSettings.update(s => ({
      ...s,
      margins: { ...margins }
    }));
  }

  /**
   * Aktualizuje stan linii prowadzącej linijki (kreska nad kartką podczas drag).
   */
  onRulerDragGuide(e: { active: boolean; axis: 'horizontal' | 'vertical'; offsetPx: number }): void {
    this.rulerGuide.set({ ...e });
  }

  /**
   * Reaguje na zmianę edytowanej sekcji (treść / nagłówek / stopka) — przełącza
   * obrazowanie pionowej linijki na pasmo nagłówka/stopki.
   */
  onEditingSectionChange(section: 'header' | 'footer' | 'body'): void {
    this.editingSection.set(section);
    if (section === 'body') {
      this.sectionGeometry.set(null);
    }
  }

  /** Przyjmuje zmierzoną geometrię pasma nagłówka/stopki do obrazowania pionowej linijki. */
  onSectionGeometryChange(geo: { section: 'header' | 'footer'; topCm: number; bottomCm: number }): void {
    this.sectionGeometry.set(geo);
  }

  /**
   * Zmiana z PIONOWEJ linijki.
   * - tryb `body`: zwykła zmiana górnego/dolnego marginesu strony.
   * - tryb `header`: dolny uchwyt = dolna krawędź pasma → nowa wysokość nagłówka.
   * - tryb `footer`: górny uchwyt = górna krawędź pasma → nowa wysokość stopki.
   * (jak „header/footer from edge" w MS Word). Wysokość spinamy przez setHeaderHeight/
   * setFooterHeight, co emituje headerChange/footerChange i odświeża pasmo + linijkę.
   */
  onVerticalRulerMarginsChange(margins: PageMargins): void {
    const section = this.editingSection();
    if (section === 'body') {
      this.onRulerMarginsChange(margins);
      return;
    }
    const pageH = this.pageSettings().orientation === 'portrait' ? 29.7 : 21;
    const geo = this.sectionGeometry();
    if (section === 'header') {
      const topCm = geo ? Math.max(0, geo.topCm) : 0;
      const newBottomCm = pageH - margins.bottom; // dolna krawędź pasma nagłówka
      const newHeight = Math.round((newBottomCm - topCm) * 100) / 100;
      this.editor?.setHeaderHeight(newHeight);
    } else if (section === 'footer') {
      const bottomCm = geo ? geo.bottomCm : pageH - margins.bottom; // dolna krawędź pasma stopki
      const newTopCm = margins.top; // górna krawędź pasma stopki
      const newHeight = Math.round((bottomCm - newTopCm) * 100) / 100;
      this.editor?.setFooterHeight(newHeight);
    }
  }

  /**
   * Obsługuje zmianę wcięcia paragrafu z poziomej linijki (drag & drop).
   * Zachowuje się jak w MS Word: zmiana dotyczy TYLKO zaznaczonych bloków
   * (paragraf, lista, tabela, obraz/figura), a NIE marginesów ca\u0142ego dokumentu.
   */
  onRulerBlockIndentChange(indent: { start?: number; end?: number }): void {
    const blocks = this.getSelectedBlocks();
    if (blocks.length === 0) return;

    for (const block of blocks) {
      if (indent.start !== undefined) {
        const cm = Math.max(-this.pageSettings().margins.left + 0.1, indent.start);
        if (cm === 0) {
          block.style.removeProperty('margin-left');
        } else {
          block.style.marginLeft = `${cm.toFixed(2)}cm`;
        }
      }
      if (indent.end !== undefined) {
        const cm = Math.max(-this.pageSettings().margins.right + 0.1, indent.end);
        if (cm === 0) {
          block.style.removeProperty('margin-right');
        } else {
          block.style.marginRight = `${cm.toFixed(2)}cm`;
        }
      }
    }

    // Zaktualizuj sygna\u0142 wci\u0119cia (uchwyty linijki natychmiast podskakuj\u0105 do nowej pozycji)
    this.currentBlockIndent.update(prev => ({
      start: indent.start !== undefined ? indent.start : prev.start,
      end: indent.end !== undefined ? indent.end : prev.end
    }));

    // Powiadom edytor o modyfikacji (auto-save / dirty flag)
    this.editor?.triggerContentChange();
  }

  /**
   * Nas\u0142uchuje zmian zaznaczenia, \u017ceby zaktualizowa\u0107 odczyt wci\u0119cia paragrafu
   * dla poziomej linijki.
   */
  @HostListener('document:selectionchange')
  onDocumentSelectionChange(): void {
    this.updateCurrentBlockIndent();
  }

  /**
   * Znajduje wszystkie unikalne bloki nadrz\u0119dne zawarte w aktualnym zaznaczeniu.
   * Blokiem jest: P, H1\u2013H6, UL, OL, LI, TABLE, FIGURE, BLOCKQUOTE, DIV (poza wrapperami).
   * Je\u015bli zaznaczona jest grafika \u2014 zwracamy paragraf w kt\u00f3rym jest osadzona (lub IMG).
   */
  private getSelectedBlocks(): HTMLElement[] {
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) return [];

    const range = selection.getRangeAt(0);
    const blocks: HTMLElement[] = [];
    const seen = new Set<HTMLElement>();
    const blockTags = new Set(['P', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'UL', 'OL', 'TABLE', 'FIGURE', 'BLOCKQUOTE', 'LI', 'IMG']);

    const findBlock = (node: Node | null): HTMLElement | null => {
      let n = node;
      while (n && n !== document) {
        if (n.nodeType === Node.ELEMENT_NODE) {
          const el = n as HTMLElement;
          if (blockTags.has(el.tagName)) return el;
        }
        n = n.parentNode;
      }
      return null;
    };

    if (range.collapsed) {
      const b = findBlock(range.startContainer);
      if (b) blocks.push(b);
    } else {
      // Iteruj po w\u0119z\u0142ach mi\u0119dzy startem a ko\u0144cem
      const walker = document.createTreeWalker(
        range.commonAncestorContainer,
        NodeFilter.SHOW_ELEMENT,
        {
          acceptNode: (n: Node) => {
            const el = n as HTMLElement;
            if (!blockTags.has(el.tagName)) return NodeFilter.FILTER_SKIP;
            return range.intersectsNode(el) ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_SKIP;
          }
        }
      );
      // Dodaj rodzic\u00f3w start/end na wypadek gdyby walker pomin\u0105\u0142
      const startBlock = findBlock(range.startContainer);
      if (startBlock) blocks.push(startBlock);
      let node = walker.nextNode();
      while (node) {
        const el = node as HTMLElement;
        // Pomi\u0144 LI je\u015bli ma rodzica UL/OL w blocks (bo wci\u0119cie aplikujemy do listy)
        blocks.push(el);
        node = walker.nextNode();
      }
      const endBlock = findBlock(range.endContainer);
      if (endBlock) blocks.push(endBlock);
    }

    // Deduplikacja + filtracja: je\u015bli mamy UL/OL i jego LI \u2014 zostaw UL/OL.
    // Je\u015bli mamy IMG i jego paragraf \u2014 zostaw paragraf (CSS margin na P dzia\u0142a lepiej).
    const result: HTMLElement[] = [];
    for (const b of blocks) {
      if (seen.has(b)) continue;
      seen.add(b);
      result.push(b);
    }
    // Usu\u0144 LI je\u015bli rodzic UL/OL te\u017c jest w secie
    const filtered = result.filter(el => {
      if (el.tagName === 'LI') {
        const parent = el.parentElement;
        if (parent && (parent.tagName === 'UL' || parent.tagName === 'OL') && seen.has(parent)) {
          return false;
        }
      }
      if (el.tagName === 'IMG') {
        // Zamie\u0144 na rodzica paragrafu
        let p = el.parentElement;
        while (p && !['P', 'DIV', 'FIGURE'].includes(p.tagName)) p = p.parentElement;
        if (p) {
          if (!seen.has(p)) {
            seen.add(p);
            result.push(p);
          }
          return false;
        }
      }
      return true;
    });

    return filtered;
  }

  /**
   * Odczytuje wci\u0119cie (margin-left/right) z pierwszego bloku w zaznaczeniu
   * i zapisuje do `currentBlockIndent`. Warto\u015bci w cm (px / CSS_PX_PER_CM).
   */
  private updateCurrentBlockIndent(): void {
    const blocks = this.getSelectedBlocks();
    if (blocks.length === 0) {
      this.currentBlockIndent.set({ start: 0, end: 0 });
      this.setColumnRuler(null);
      return;
    }
    const block = blocks[0];
    const style = window.getComputedStyle(block);
    const mlPx = parseFloat(style.marginLeft) || 0;
    const mrPx = parseFloat(style.marginRight) || 0;
    const start = Math.round((mlPx / DocumentEditorComponent.CM_TO_PX) * 100) / 100;
    const end = Math.round((mrPx / DocumentEditorComponent.CM_TO_PX) * 100) / 100;
    const prev = this.currentBlockIndent();
    if (prev.start !== start || prev.end !== end) {
      this.currentBlockIndent.set({ start, end });
    }
    this.updateColumnRulerContext(block);
  }

  /**
   * Wylicza dla poziomej linijki geometrię kolumn sekcji, w której stoi kursor.
   * Szerokości i pozycje bierzemy z computed style (px layoutu — transform zoomu ich
   * nie zmienia); recty służą tylko do wyznaczenia offsetu pasma i aktywnej kolumny,
   * dlatego dzielimy je przez faktyczną skalę (rect.width / offsetWidth).
   */
  private updateColumnRulerContext(block: HTMLElement): void {
    const band = block.closest<HTMLElement>('.docx-col-band');
    const editorContent = block.closest<HTMLElement>('.editor-content');
    const container = band ?? editorContent;
    if (!container || !editorContent) {
      this.setColumnRuler(null);
      return;
    }
    const cs = window.getComputedStyle(container);
    const count = parseInt(cs.columnCount, 10);
    if (!count || count <= 1) {
      this.setColumnRuler(null);
      return;
    }

    const gapPx = parseFloat(cs.columnGap) || 0;
    const padLeftPx = parseFloat(cs.paddingLeft) || 0;
    const padRightPx = parseFloat(cs.paddingRight) || 0;
    const contentWidthPx = container.clientWidth - padLeftPx - padRightPx;
    const colWidthPx = Math.max(1, (contentWidthPx - gapPx * (count - 1)) / count);

    const pageRect = editorContent.getBoundingClientRect();
    const scale = editorContent.offsetWidth > 0 ? pageRect.width / editorContent.offsetWidth : 1;
    const containerRect = container.getBoundingClientRect();
    // Lewa krawędź obszaru kolumn względem lewej krawędzi kartki (px layoutu):
    // .editor-content zaczyna się na krawędzi kartki (padding = margines strony)
    const contentLeftViewportPx = containerRect.left + padLeftPx * scale;
    const baseStartPx = (contentLeftViewportPx - pageRect.left) / scale;

    const cmPerPx = 1 / DocumentEditorComponent.CM_TO_PX;
    const round2 = (v: number) => Math.round(v * 100) / 100;
    const segments: RulerColumnSegment[] = Array.from({ length: count }, (_, i) => ({
      startCm: round2((baseStartPx + i * (colWidthPx + gapPx)) * cmPerPx),
      widthCm: round2(colWidthPx * cmPerPx)
    }));

    // Aktywna kolumna z pozycji X punktu skupienia zaznaczenia (fallback: rect bloku)
    const focusX = this.getSelectionFocusX() ?? block.getBoundingClientRect().left;
    const relPx = (focusX - contentLeftViewportPx) / scale;
    const activeIndex = Math.max(0, Math.min(count - 1, Math.floor(relPx / (colWidthPx + gapPx))));

    this.setColumnRuler({ segments, activeIndex });
  }

  /** Pozycja X (viewport px) punktu skupienia zaznaczenia; null gdy nie da się wyznaczyć. */
  private getSelectionFocusX(): number | null {
    const sel = window.getSelection();
    if (!sel || !sel.focusNode) return null;
    try {
      const r = document.createRange();
      r.setStart(sel.focusNode, sel.focusOffset);
      r.collapse(true);
      const rect = r.getClientRects()[0];
      if (rect) return rect.left;
      // Pusty akapit / pozycja bez rectów — rect najbliższego elementu
      const el = sel.focusNode.nodeType === Node.ELEMENT_NODE
        ? sel.focusNode as HTMLElement
        : sel.focusNode.parentElement;
      return el ? el.getBoundingClientRect().left : null;
    } catch {
      return null;
    }
  }

  /** Ustawia sygnał geometrii kolumn tylko przy faktycznej zmianie (selectionchange strzela często). */
  private setColumnRuler(next: { segments: RulerColumnSegment[]; activeIndex: number } | null): void {
    const prev = this.currentColumnRuler();
    if (prev === null && next === null) return;
    if (
      prev && next &&
      prev.activeIndex === next.activeIndex &&
      prev.segments.length === next.segments.length &&
      prev.segments.every((s, i) => s.startCm === next.segments[i].startCm && s.widthCm === next.segments[i].widthCm)
    ) {
      return;
    }
    this.currentColumnRuler.set(next);
  }

  // =====================
  // DIALOG AKAPIT
  // =====================

  openParagraphDialog(): void {
    this.closeAllMenus();
    this.readCurrentParagraphSettings();
    this.paragraphDialogTab.set('indents');
    this.showParagraphDialog.set(true);
  }

  closeParagraphDialog(): void {
    this.showParagraphDialog.set(false);
  }

  /**
   * Odczytuje bieżące ustawienia akapitu z zaznaczenia
   */
  private readCurrentParagraphSettings(): void {
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) {
      // Brak karetki w treści → seeduj formularz zapisanym defaultem, nie zostawiaj
      // poprzednich (mylących) wartości w polach.
      this.paragraphData = { ...this._paragraphDefaults };
      return;
    }

    const range = selection.getRangeAt(0);
    let block = range.startContainer as Node;
    if (block.nodeType === Node.TEXT_NODE) {
      block = block.parentNode!;
    }
    while (block && !['P', 'DIV', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'LI'].includes((block as HTMLElement).tagName)) {
      block = block.parentNode!;
    }

    if (block) {
      const el = block as HTMLElement;
      const style = window.getComputedStyle(el);

      // Wyrównanie
      const textAlign = style.textAlign;
      if (textAlign === 'center') this.paragraphData.alignment = 'center';
      else if (textAlign === 'right' || textAlign === 'end') this.paragraphData.alignment = 'right';
      else if (textAlign === 'justify') this.paragraphData.alignment = 'justify';
      else this.paragraphData.alignment = 'left';

      // Indents (px -> cm), rounded to 0.1 cm. KONTRAKT = margin-left/right (gap-analysis
      // pkt 2): reader emituje w:ind jako margin-*, writer czyta margin-* — dialog czytał
      // padding-left, więc wcięcia z Worda pokazywały się jako 0.
      const pxToCm = (px: number) => Math.round((px / CSS_PX_PER_CM) * 10) / 10;
      this.paragraphData.indentLeft = pxToCm(parseFloat(style.marginLeft) || 0);
      this.paragraphData.indentRight = pxToCm(parseFloat(style.marginRight) || 0);

      // Text-indent (wcięcie specjalne)
      const textIndent = parseFloat(style.textIndent) || 0;
      if (textIndent > 0) {
        this.paragraphData.specialIndent = 'firstLine';
        this.paragraphData.specialIndentBy = pxToCm(textIndent);
      } else if (textIndent < 0) {
        this.paragraphData.specialIndent = 'hanging';
        this.paragraphData.specialIndentBy = pxToCm(Math.abs(textIndent));
      } else {
        this.paragraphData.specialIndent = 'none';
      }

      // Odstępy (px -> pt, 1pt ≈ 1.333px). Odstęp „po" = padding-bottom (ADR-0053:
      // sumuje się z margin-top następnego jak w Wordzie); margin-bottom doliczamy
      // dla akapitów z tłem/obramowaniem i treści sprzed zmiany (jedno z dwóch = 0).
      // Dokładna relacja CSS: 1pt = 96/72 px (drift gap-analysis pkt 5 — 1.333 gubił ułamki).
      const pxToPt = (px: number) => Math.round((px * 72) / 96);
      this.paragraphData.spaceBefore = pxToPt(parseFloat(style.marginTop) || 0);
      this.paragraphData.spaceAfter = pxToPt(
        (parseFloat(style.marginBottom) || 0) + (parseFloat(style.paddingBottom) || 0));

      // Interlinia — mnożnik Worda z markera --w-line-tw (własnego lub odziedziczonego
      // z domyślnych dokumentu), nie ze skalibrowanej wartości renderowej (PG-09);
      // inline w pt = atLeast/exactly (rozróżnienie markerem --w-line-rule).
      const inlineLineHeight = el.style.lineHeight;
      const atLeastMax = /^max\(\s*([\d.]+)pt/.exec(inlineLineHeight);
      if (atLeastMax) {
        // Kontrakt PG-10: atLeast = max(Xpt, single) — wartość dialogu to pt z wnętrza max().
        this.paragraphData.lineSpacingType = 'atLeast';
        this.paragraphData.lineSpacingValue = parseFloat(atLeastMax[1]) || 12;
      } else if (inlineLineHeight.endsWith('pt')) {
        this.paragraphData.lineSpacingType =
          el.style.getPropertyValue('--w-line-rule').trim() === 'atLeast' ? 'atLeast' : 'exactly';
        this.paragraphData.lineSpacingValue = parseFloat(inlineLineHeight) || 12;
      } else {
        const multiple = readWordLineMultiple(el);
        if (multiple === null || multiple === 1) {
          this.paragraphData.lineSpacingType = 'single';
          this.paragraphData.lineSpacingValue = 1;
        } else {
          this.paragraphData.lineSpacingType = 'multiple';
          this.paragraphData.lineSpacingValue = multiple;
        }
      }

      // Podział strony przed — z inline stylu bloku (reader emituje `page-break-before:always`
      // dla w:pageBreakBefore; checkbox odzwierciedla stan jak dialog Worda).
      this.paragraphData.pageBreakBefore =
        /always|page/i.test(el.style.pageBreakBefore || el.style.breakBefore || '');
    }
  }

  /**
   * Stosuje ustawienia akapitu
   */
  applyParagraphSettings(): void {
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0) {
      this.closeParagraphDialog();
      return;
    }

    const range = selection.getRangeAt(0);
    let block = range.startContainer as Node;
    if (block.nodeType === Node.TEXT_NODE) {
      block = block.parentNode!;
    }
    while (block && !['P', 'DIV', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6', 'LI'].includes((block as HTMLElement).tagName)) {
      block = block.parentNode!;
    }

    if (block) {
      const el = block as HTMLElement;
      const cmToPx = (cm: number) => cm * CSS_PX_PER_CM;

      // Wyrównanie
      el.style.textAlign = this.paragraphData.alignment;

      // Wcięcia — KONTRAKT writera (gap-analysis pkt 2): margin-left/right → w:ind left/right,
      // ujemny text-indent → w:ind hanging (semantyka Worda: „Z lewej" = pozycja tekstu,
      // pierwsza linia wysunięta o `hanging` W LEWO od niej). Dialog pisał padding-left,
      // którego writer nie czyta — wcięcia z dialogu nie trafiały do DOCX.
      el.style.marginLeft = cmToPx(this.paragraphData.indentLeft) + 'px';
      el.style.marginRight = cmToPx(this.paragraphData.indentRight) + 'px';
      // Sprzątanie po starym kontrakcie (padding-* z poprzednich wersji dialogu) — inaczej
      // wcięcie liczyłoby się podwójnie wizualnie.
      el.style.removeProperty('padding-left');
      el.style.removeProperty('padding-right');

      // Wcięcie specjalne
      if (this.paragraphData.specialIndent === 'firstLine') {
        el.style.textIndent = cmToPx(this.paragraphData.specialIndentBy) + 'px';
      } else if (this.paragraphData.specialIndent === 'hanging') {
        el.style.textIndent = '-' + cmToPx(this.paragraphData.specialIndentBy) + 'px';
      } else {
        el.style.textIndent = '0';
      }

      // Odstępy — „po" idzie w padding-bottom (ADR-0053), ewentualny stary
      // margin-bottom czyścimy, żeby wartości się nie sumowały podwójnie.
      const ptToPx = (pt: number) => (pt * 96) / 72;
      el.style.marginTop = ptToPx(this.paragraphData.spaceBefore) + 'px';
      el.style.paddingBottom = ptToPx(this.paragraphData.spaceAfter) + 'px';
      el.style.marginBottom = '';

      // Interlinia — mnożniki w semantyce Worda (kalibracja + marker, PG-09);
      // atLeast dostaje marker reguły, bez którego writer zapisywał exact
      // (a exact przycina w Wordzie tekst wyższy niż linia).
      switch (this.paragraphData.lineSpacingType) {
        case 'single':
          applyWordLineSpacing(el, 1);
          break;
        case '1.5':
          applyWordLineSpacing(el, 1.5);
          break;
        case 'double':
          applyWordLineSpacing(el, 2);
          break;
        case 'multiple':
          applyWordLineSpacing(el, this.paragraphData.lineSpacingValue);
          break;
        case 'atLeast':
          applyExactLineSpacing(el, this.paragraphData.lineSpacingValue, true);
          break;
        case 'exactly':
          applyExactLineSpacing(el, this.paragraphData.lineSpacingValue, false);
          break;
      }

      // Podział strony przed — właściwość Worda (w:pageBreakBefore): paginacja łamie stronę
      // przed blokiem, writer odtwarza właściwość w pPr (nie ręczny w:br). Jawne `auto`
      // ustawiamy tylko przy ODZNACZANIU aktywnego podziału — writer mapuje je na
      // w:pageBreakBefore val=false, co nadpisuje ewentualny podział ze STYLU Worda.
      if (this.paragraphData.pageBreakBefore) {
        el.style.pageBreakBefore = 'always';
      } else if (/always|page/i.test(el.style.pageBreakBefore || el.style.breakBefore || '')) {
        el.style.pageBreakBefore = 'auto';
      }

      // Zmiany stylu akapitu (interlinia/odstępy/podział strony) zmieniają układ i muszą
      // trafić do zapisu — dispatch `input` jak operacje tabelowe (persist + repaginacja).
      this.notifyEditorChange();
    }

    this.closeParagraphDialog();
  }

  /**
   * „Ustaw jako domyślne" — zapisuje BIEŻĄCE ustawienia akapitu z dialogu jako domyślne
   * (per-sesja edytora) i od razu stosuje je do aktywnego akapitu.
   *
   * Wcześniej ten przycisk wołał reset do wartości bazowych (Qutas-PAR-007: „ustaw jako
   * domyślne resetuje zamiast zapisywać"). Teraz zachowuje się zgodnie z nazwą:
   *  1. zapamiętuje snapshot ustawień jako default sesji (`_paragraphDefaults`),
   *  2. stosuje ustawienia do bieżącego akapitu (jak OK) — nowe akapity tworzone Enterem
   *     dziedziczą styl po bieżącym bloku (contenteditable klonuje blok), więc default
   *     propaguje się naturalnie na kolejne akapity.
   *
   * Model trwałości: default jest per-sesja edytora (resetuje się po odświeżeniu strony).
   * Nie wprowadzamy localStorage (brak takiego wzorca w aplikacji) ani zmiany kontraktu API.
   */
  setParagraphAsDefault(): void {
    this._paragraphDefaults = { ...this.paragraphData };
    this.applyParagraphSettings();
  }

  /**
   * Jednostka interlinii
   */
  getLineSpacingUnit(): string {
    switch (this.paragraphData.lineSpacingType) {
      case 'atLeast':
      case 'exactly':
        return 'pkt';
      case 'multiple':
        return '';
      default:
        return '';
    }
  }

  /**
   * Interlinia dla podglądu
   */
  getPreviewLineHeight(): string {
    switch (this.paragraphData.lineSpacingType) {
      case 'single': return '1';
      case '1.5': return '1.5';
      case 'double': return '2';
      case 'multiple': return this.paragraphData.lineSpacingValue.toString();
      default: return '1.15';
    }
  }

  // =====================
  // DIALOG WSTAWIANIE TABELI
  // =====================

  openInsertTableDialog(): void {
    this.closeAllMenus();
    if (this.savedTableDimensions) {
      this.tableDialogData.columns = this.savedTableDimensions.columns;
      this.tableDialogData.rows = this.savedTableDimensions.rows;
    } else {
      this.tableDialogData.columns = 5;
      this.tableDialogData.rows = 2;
    }
    this.tableDialogData.autoFitBehavior = 'fixed';
    this.tableDialogData.fixedWidth = 0;
    this.showInsertTableDialog.set(true);
  }

  closeInsertTableDialog(): void {
    this.showInsertTableDialog.set(false);
  }

  // ===== Walidacja rozmiaru tabeli =====
  // type="number" + min/max nie blokuje wpisania ręcznie —0", „-5" czy 9999
  // — atrybuty te wpływają tylko na spinner i :invalid. Dlatego trzymamy
  // jawne sprawdzenie + clamp on blur + disabled na przycisku „Wstaw".
  private static readonly TABLE_MIN_COLS = 1;
  private static readonly TABLE_MAX_COLS = 63;   // limit Worda
  private static readonly TABLE_MIN_ROWS = 1;
  private static readonly TABLE_MAX_ROWS = 500;

  isTableColumnsValid(): boolean {
    const v = this.tableDialogData.columns;
    return Number.isFinite(v) && Number.isInteger(v)
      && v >= DocumentEditorComponent.TABLE_MIN_COLS && v <= DocumentEditorComponent.TABLE_MAX_COLS;
  }

  isTableRowsValid(): boolean {
    const v = this.tableDialogData.rows;
    return Number.isFinite(v) && Number.isInteger(v)
      && v >= DocumentEditorComponent.TABLE_MIN_ROWS && v <= DocumentEditorComponent.TABLE_MAX_ROWS;
  }

  insertTableValidationError(): string | null {
    if (!this.isTableColumnsValid()) {
      return `Liczba kolumn musi być liczbą całkowitą z zakresu ${DocumentEditorComponent.TABLE_MIN_COLS}–${DocumentEditorComponent.TABLE_MAX_COLS}.`;
    }
    if (!this.isTableRowsValid()) {
      return `Liczba wierszy musi być liczbą całkowitą z zakresu ${DocumentEditorComponent.TABLE_MIN_ROWS}–${DocumentEditorComponent.TABLE_MAX_ROWS}.`;
    }
    return null;
  }

  clampTableColumns(): void {
    const v = this.tableDialogData.columns;
    if (!Number.isFinite(v)) {
      this.tableDialogData.columns = DocumentEditorComponent.TABLE_MIN_COLS;
      return;
    }
    this.tableDialogData.columns = Math.max(
      DocumentEditorComponent.TABLE_MIN_COLS,
      Math.min(DocumentEditorComponent.TABLE_MAX_COLS, Math.floor(v))
    );
  }

  clampTableRows(): void {
    const v = this.tableDialogData.rows;
    if (!Number.isFinite(v)) {
      this.tableDialogData.rows = DocumentEditorComponent.TABLE_MIN_ROWS;
      return;
    }
    this.tableDialogData.rows = Math.max(
      DocumentEditorComponent.TABLE_MIN_ROWS,
      Math.min(DocumentEditorComponent.TABLE_MAX_ROWS, Math.floor(v))
    );
  }

  onFixedWidthChange(value: string): void {
    if (value.toLowerCase() === 'auto' || value === '') {
      this.tableDialogData.fixedWidth = 0;
    } else {
      const num = parseFloat(value);
      if (!isNaN(num)) {
        this.tableDialogData.fixedWidth = num;
      }
    }
  }

  applyInsertTable(): void {
    // Twarda bramka — nawet jeśli ktoś ominie disabled (np. enter), nic nie wstawimy.
    if (this.insertTableValidationError()) {
      return;
    }
    const cols = Math.max(1, Math.min(63, this.tableDialogData.columns));
    const rows = Math.max(1, Math.min(500, this.tableDialogData.rows));

    if (this.tableDialogData.rememberDimensions) {
      this.savedTableDimensions = { columns: cols, rows: rows };
    }

    const config = `${cols}x${rows}`;

    if (this.editor) {
      this.editor.insertTable(config);
      this.applyTableAutoFit(this.tableDialogData.autoFitBehavior, this.tableDialogData.fixedWidth);
    }

    this.closeInsertTableDialog();
  }

  // =====================
  // TOOLBAR TABELI - OPERACJE
  // =====================

  /**
   * Powiadamia edytor o zmianach w DOM (wywołuje contentChange)
   */
  private notifyEditorChange(): void {
    const el = this.editor?.editorContent?.nativeElement;
    if (el) {
      el.dispatchEvent(new Event('input', { bubbles: true }));
    }
  }

  /** Pobiera indeks wiersza i kolumny aktywnej komórki */
  private getCellPosition(cell: HTMLTableCellElement): { rowIndex: number; colIndex: number } | null {
    const row = cell.parentElement as HTMLTableRowElement;
    if (!row) return null;
    const table = row.closest('table');
    if (!table) return null;
    const rows = Array.from(table.rows);
    const rowIndex = rows.indexOf(row);
    const colIndex = Array.from(row.cells).indexOf(cell);
    return { rowIndex, colIndex };
  }

  /** Wstaw wiersz powyżej */
  tableInsertRowAbove(): void {
    const cell = this.activeTableCell();
    const table = this.activeTable();
    if (!cell || !table) return;
    const pos = this.getCellPosition(cell);
    if (!pos) return;
    const colCount = table.rows[pos.rowIndex]?.cells.length || 1;
    const newRow = table.insertRow(pos.rowIndex);
    for (let i = 0; i < colCount; i++) {
      const td = newRow.insertCell();
      td.innerHTML = '<br>';
      td.style.cssText = 'border:1px solid #ccc;padding:8px;min-width:30px;';
    }
    this.notifyEditorChange();
  }

  /** Wstaw wiersz poniżej */
  tableInsertRowBelow(): void {
    const cell = this.activeTableCell();
    const table = this.activeTable();
    if (!cell || !table) return;
    const pos = this.getCellPosition(cell);
    if (!pos) return;
    const colCount = table.rows[pos.rowIndex]?.cells.length || 1;
    const insertAt = pos.rowIndex + 1;
    const newRow = table.insertRow(insertAt < table.rows.length ? insertAt : -1);
    for (let i = 0; i < colCount; i++) {
      const td = newRow.insertCell();
      td.innerHTML = '<br>';
      td.style.cssText = 'border:1px solid #ccc;padding:8px;min-width:30px;';
    }
    this.notifyEditorChange();
  }

  /** Wstaw kolumnę z lewej */
  tableInsertColLeft(): void {
    const cell = this.activeTableCell();
    const table = this.activeTable();
    if (!cell || !table) return;
    const pos = this.getCellPosition(cell);
    if (!pos) return;
    Array.from(table.rows).forEach(row => {
      const td = row.insertCell(Math.min(pos.colIndex, row.cells.length));
      td.innerHTML = '<br>';
      td.style.cssText = 'border:1px solid #ccc;padding:8px;min-width:30px;';
    });
    syncTableColgroup(table);
    this.notifyEditorChange();
  }

  /** Wstaw kolumnę z prawej */
  tableInsertColRight(): void {
    const cell = this.activeTableCell();
    const table = this.activeTable();
    if (!cell || !table) return;
    const pos = this.getCellPosition(cell);
    if (!pos) return;
    const insertAt = pos.colIndex + 1;
    Array.from(table.rows).forEach(row => {
      const td = row.insertCell(Math.min(insertAt, row.cells.length));
      td.innerHTML = '<br>';
      td.style.cssText = 'border:1px solid #ccc;padding:8px;min-width:30px;';
    });
    syncTableColgroup(table);
    this.notifyEditorChange();
  }

  /** Usuń wiersz */
  tableDeleteRow(): void {
    const cell = this.activeTableCell();
    const table = this.activeTable();
    if (!cell || !table) return;
    const pos = this.getCellPosition(cell);
    if (!pos) return;
    if (table.rows.length <= 1) {
      this.tableDeleteTable();
      return;
    }
    table.deleteRow(pos.rowIndex);
    this.notifyEditorChange();
  }

  /** Usuń kolumnę */
  tableDeleteCol(): void {
    const cell = this.activeTableCell();
    const table = this.activeTable();
    if (!cell || !table) return;
    const pos = this.getCellPosition(cell);
    if (!pos) return;
    if (table.rows[0]?.cells.length <= 1) {
      this.tableDeleteTable();
      return;
    }
    Array.from(table.rows).forEach(row => {
      if (pos.colIndex < row.cells.length) {
        row.deleteCell(pos.colIndex);
      }
    });
    syncTableColgroup(table);
    this.notifyEditorChange();
  }

  /** Usuń tabelę */
  tableDeleteTable(): void {
    const table = this.activeTable();
    if (!table) return;
    table.parentNode?.removeChild(table);
    this.isInTable.set(false);
    this.activeTableCell.set(null);
    this.activeTable.set(null);
    this.showTablePanel.set(false);
    this.tablePanelManuallyClosed = false;
    this.notifyEditorChange();
  }

  // =====================
  // OBRAMOWANIA / LINIE TABELI (rodzaj, grubość, kolor, miejsce, cel)
  // =====================

  setTableBorderStyle(style: TableBorderLineStyle): void {
    this.tableBorderStyle.set(style);
  }

  setTableBorderColor(color: string): void {
    this.tableBorderColor.set(color);
  }

  setTableBorderWidth(width: number): void {
    if (width >= 1 && width <= 12) this.tableBorderWidth.set(width);
  }

  /** Resetuje ustawienia „pióra" (rodzaj/grubość/kolor) do wartości domyślnych. */
  resetTableBorderSettings(): void {
    this.tableBorderColor.set(DEFAULT_TABLE_BORDER.color);
    this.tableBorderWidth.set(DEFAULT_TABLE_BORDER.width);
    this.tableBorderStyle.set(DEFAULT_TABLE_BORDER.style);
  }

  /**
   * Stosuje linię w wybranym miejscu do **auto-wykrytego** celu (zaznaczenie /
   * aktywna komórka). Nie usuwa treści — zmienia tylko style obramowań.
   */
  applyTableBorderScope(scope: TableBorderScope): void {
    const table = this.activeTable();
    if (!table) return;
    const cells = this.resolveAutoTargetCells(table);
    if (cells.length === 0) return;
    applyBorderToCells(cells, scope, this.currentBorderSettings());
    this.lastBorderScope.set(scope);
    this.notifyEditorChange();
  }

  /** Usuwa wszystkie obramowania w bieżącym celu. */
  clearTableBorders(): void {
    this.applyTableBorderScope('none');
  }

  /** Przywraca domyślną pełną siatkę 1px na całej tabeli i resetuje ustawienia pióra. */
  restoreDefaultTableBorders(): void {
    const table = this.activeTable();
    if (!table) return;
    restoreDefaultBordersUtil(table);
    this.resetTableBorderSettings();
    this.lastBorderScope.set('all');
    this.notifyEditorChange();
  }

  /**
   * Auto-wykrycie celu obramowania z bieżącego zaznaczenia:
   * zaznaczone komórki → ten zbiór (geometria wiersz/kolumna/zakres/tabela liczona
   * przez `applyBorderToCells`/`classifyBorderTarget`); brak zaznaczenia → aktywna
   * komórka (najbezpieczniejszy, intuicyjny domyślny zakres — jak karetka w Word).
   */
  private resolveAutoTargetCells(table: HTMLTableElement): HTMLTableCellElement[] {
    const selected = Array.from(this.selectedCells());
    if (selected.length > 0) return selected;
    const cell = this.activeTableCell();
    if (cell) return [cell];
    return Array.from(table.querySelectorAll('td, th')) as HTMLTableCellElement[];
  }

  private currentBorderSettings() {
    return {
      color: this.tableBorderColor(),
      width: this.tableBorderWidth(),
      style: this.tableBorderStyle()
    };
  }

  /** Scal zaznaczone komórki */
  tableMergeCells(): void {
    const customSelected = this.selectedCells();
    const cells = customSelected.size > 0
      ? Array.from(customSelected)
      : (() => {
          const selection = window.getSelection();
          const editorEl = this.editor?.editorContent?.nativeElement;
          return (selection && editorEl) ? this.getSelectedCells(selection, editorEl) : [];
        })();
    if (cells.length < 2) return;

    // Zbierz treść i usuń komórki oprócz pierwszej
    const firstCell = cells[0] as HTMLTableCellElement;
    let mergedContent = '';
    let minRow = Infinity, maxRow = -1, minCol = Infinity, maxCol = -1;

    cells.forEach((c) => {
      const td = c as HTMLTableCellElement;
      const row = td.parentElement as HTMLTableRowElement;
      const table = row.closest('table')!;
      const ri = Array.from(table.rows).indexOf(row);
      const ci = Array.from(row.cells).indexOf(td);
      minRow = Math.min(minRow, ri);
      maxRow = Math.max(maxRow, ri);
      minCol = Math.min(minCol, ci);
      maxCol = Math.max(maxCol, ci + (td.colSpan || 1) - 1);
    });

    // Zbierz treści
    cells.forEach(c => {
      const txt = c.innerHTML.trim();
      if (txt && txt !== '&nbsp;' && txt !== '<br>') {
        mergedContent += (mergedContent ? ' ' : '') + txt;
      }
    });

    // Ustaw colspan/rowspan na pierwszej komórce
    const colSpan = maxCol - minCol + 1;
    const rowSpan = maxRow - minRow + 1;
    firstCell.colSpan = colSpan;
    firstCell.rowSpan = rowSpan;
    firstCell.innerHTML = mergedContent || '<br>';

    // Usuń nadmiarowe komórki
    const table = this.activeTable();
    if (!table) return;
    for (let r = minRow; r <= maxRow; r++) {
      const row = table.rows[r];
      if (!row) continue;
      for (let c = row.cells.length - 1; c >= 0; c--) {
        const cell = row.cells[c];
        if (cell !== firstCell && cells.includes(cell)) {
          row.removeChild(cell);
        }
      }
    }
    this.clearCellSelection();
    this.notifyEditorChange();
  }

  /** Podziel komórkę */
  tableSplitCell(): void {
    const cell = this.activeTableCell();
    if (!cell) return;
    const table = this.activeTable();
    if (!table) return;

    const cs = cell.colSpan || 1;
    const rs = cell.rowSpan || 1;

    if (cs <= 1 && rs <= 1) {
      // Komórka nie jest scalona - podziel na 2 kolumny
      const pos = this.getCellPosition(cell);
      if (!pos) return;
      cell.colSpan = 1;
      Array.from(table.rows).forEach((row, ri) => {
        if (ri === pos.rowIndex) {
          const newTd = row.insertCell(pos.colIndex + 1);
          newTd.innerHTML = '<br>';
          newTd.style.cssText = 'border:1px solid #ccc;padding:8px;min-width:30px;';
        } else {
          const newTd = row.insertCell(Math.min(pos.colIndex + 1, row.cells.length));
          newTd.innerHTML = '<br>';
          newTd.style.cssText = 'border:1px solid #ccc;padding:8px;min-width:30px;';
        }
      });
    } else {
      // Komórka jest scalona - cofnij scalenie
      const pos = this.getCellPosition(cell);
      if (!pos) return;
      cell.colSpan = 1;
      cell.rowSpan = 1;
      // Dodaj brakujące komórki w bieżącym wierszu
      const row = cell.parentElement as HTMLTableRowElement;
      for (let c = 1; c < cs; c++) {
        const newTd = row.insertCell(Array.from(row.cells).indexOf(cell) + 1);
        newTd.innerHTML = '<br>';
        newTd.style.cssText = 'border:1px solid #ccc;padding:8px;min-width:30px;';
      }
      // Dodaj brakujące komórki w kolejnych wierszach
      for (let r = 1; r < rs; r++) {
        const targetRow = table.rows[pos.rowIndex + r];
        if (!targetRow) continue;
        for (let c = 0; c < cs; c++) {
          const insertIdx = Math.min(pos.colIndex, targetRow.cells.length);
          const newTd = targetRow.insertCell(insertIdx);
          newTd.innerHTML = '<br>';
          newTd.style.cssText = 'border:1px solid #ccc;padding:8px;min-width:30px;';
        }
      }
    }
    this.notifyEditorChange();
  }

  /** Podziel tabelę (dzieli nad bieżącym wierszem) */
  tableSplitTable(): void {
    const cell = this.activeTableCell();
    const table = this.activeTable();
    if (!cell || !table) return;
    const pos = this.getCellPosition(cell);
    if (!pos || pos.rowIndex === 0) return;

    // Utwórz nową tabelę z wierszami od bieżącego w dół
    const newTable = document.createElement('table');
    newTable.style.cssText = table.style.cssText;
    const rowsToMove = Array.from(table.rows).slice(pos.rowIndex);
    rowsToMove.forEach(row => newTable.appendChild(row));

    // Wstaw paragraf separator i nową tabelę po starej
    const separator = document.createElement('p');
    separator.innerHTML = '&nbsp;';
    table.parentNode?.insertBefore(separator, table.nextSibling);
    separator.parentNode?.insertBefore(newTable, separator.nextSibling);
    this.notifyEditorChange();
  }

  /** Autodopasowanie - do zawartości */
  tableAutoFitContents(): void {
    const table = this.activeTable();
    if (!table) return;
    table.style.width = 'auto';
    table.style.tableLayout = 'auto';
    table.querySelectorAll('td, th').forEach(c => {
      (c as HTMLElement).style.width = '';
    });
    this.notifyEditorChange();
  }

  /** Autodopasowanie - do okna */
  tableAutoFitWindow(): void {
    const table = this.activeTable();
    if (!table) return;
    table.style.width = '100%';
    table.style.tableLayout = 'auto';
    table.querySelectorAll('td, th').forEach(c => {
      (c as HTMLElement).style.width = '';
    });
    this.notifyEditorChange();
  }

  /** Stała szerokość kolumn */
  tableFixedWidth(): void {
    const table = this.activeTable();
    if (!table) return;
    table.style.width = '100%';
    table.style.tableLayout = 'fixed';
    this.notifyEditorChange();
  }

  /** Rozłóż wiersze równomiernie */
  tableDistributeRows(): void {
    const table = this.activeTable();
    if (!table) return;
    Array.from(table.rows).forEach(row => {
      row.style.height = '';
      Array.from(row.cells).forEach(cell => {
        cell.style.height = '';
      });
    });
    this.notifyEditorChange();
  }

  /** Rozłóż kolumny równomiernie */
  tableDistributeCols(): void {
    const table = this.activeTable();
    if (!table) return;
    const colCount = table.rows[0]?.cells.length || 1;
    const w = Math.floor(100 / colCount);
    Array.from(table.rows).forEach(row => {
      Array.from(row.cells).forEach(cell => {
        cell.style.width = w + '%';
      });
    });
    this.notifyEditorChange();
  }

  /** Wyświetl/ukryj linie siatki */
  showTableGridLines = signal(true);
  tableToggleGridLines(): void {
    this.showTableGridLines.update(v => !v);
    const table = this.activeTable();
    if (!table) return;
    if (this.showTableGridLines()) {
      table.querySelectorAll('td, th').forEach(c => {
        (c as HTMLElement).style.borderColor = '#ccc';
      });
    } else {
      table.querySelectorAll('td, th').forEach(c => {
        (c as HTMLElement).style.borderColor = 'transparent';
      });
    }
  }

  /** Pozycja dropdown cieniowania */
  shadingDropdownX = signal(0);
  shadingDropdownY = signal(0);

  /** Toggle dropdown cieniowania w toolbarze tabeli */
  toggleShadingDropdown(event: MouseEvent): void {
    const btn = (event.target as HTMLElement).closest('.table-toolbar-btn-shading') as HTMLElement;
    if (btn) {
      const rect = btn.getBoundingClientRect();
      this.shadingDropdownX.set(rect.left);
      this.shadingDropdownY.set(rect.bottom + 4);
    }
    this.showShadingDropdown.update(v => !v);
  }

  /** Kolory do palety cieniowania */
  shadingColors = [
    '#FFFFFF', '#F2F2F2', '#D9D9D9', '#BFBFBF', '#A6A6A6', '#808080', '#595959', '#404040', '#262626', '#000000',
    '#FFF2CC', '#FFE599', '#FFD966', '#FFC000', '#BF9000', '#806000', '#FCE4D6', '#F8CBAD', '#F4B084', '#ED7D31',
    '#C55A11', '#833C0B', '#D6E4F0', '#B4C6E7', '#8DB4E2', '#4472C4', '#2F5597', '#1F3864', '#E2EFDA', '#C6EFCE',
    '#A9D18E', '#70AD47', '#548235', '#375623', '#F8D7DA', '#F5C6CB', '#E8A0A0', '#FF0000', '#C00000', '#800000',
    '#E6D5F5', '#D0B8E8', '#B490D0', '#7030A0', '#5B259A', '#3B1770'
  ];

  /**
   * Stosuje autodopasowanie do ostatnio wstawionej tabeli
   */
  private applyTableAutoFit(behavior: string, fixedWidth: number): void {
    setTimeout(() => {
      const editorEl = this.editor?.editorContent?.nativeElement;
      const tables = editorEl?.querySelectorAll('table');
      if (tables && tables.length > 0) {
        const lastTable = tables[tables.length - 1] as HTMLTableElement;
        switch (behavior) {
          case 'fixed':
            if (fixedWidth > 0) {
              lastTable.style.width = '';
              lastTable.style.tableLayout = 'fixed';
              const widthPx = fixedWidth * CSS_PX_PER_CM;
              lastTable.querySelectorAll('td, th').forEach((cell) => {
                (cell as HTMLElement).style.width = widthPx + 'px';
              });
            } else {
              lastTable.style.width = '100%';
              lastTable.style.tableLayout = 'fixed';
            }
            break;
          case 'contents':
            lastTable.style.width = 'auto';
            lastTable.style.tableLayout = 'auto';
            break;
          case 'window':
            lastTable.style.width = '100%';
            lastTable.style.tableLayout = 'auto';
            break;
        }
      }
    }, 50);
  }

  // ===== WŁAŚCIWOŚCI DOKUMENTU =====

  /**
   * Otwiera dialog właściwości dokumentu
   */
  openPropertiesDialog(): void {
    this.propertiesData.set({ ...this.documentMetadata() });
    this.showPropertiesDialog.set(true);
    this.closeAllMenus();
  }

  /**
   * Zapisuje właściwości dokumentu
   */
  saveProperties(): void {
    const props = this.propertiesData();
    this.documentMetadata.update(m => ({
      ...m,
      title: props.title,
      author: props.author,
      subject: props.subject,
      keywords: props.keywords,
      description: props.description,
      category: props.category,
      company: props.company,
      manager: props.manager,
      contentStatus: props.contentStatus,
      lastModifiedBy: props.lastModifiedBy,
      revision: props.revision,
      version: props.version,
      modified: new Date().toISOString()
    }));
    this.showPropertiesDialog.set(false);
    this.showSuccess('Właściwości dokumentu zostały zaktualizowane');
  }

  /**
   * Zamyka dialog właściwości
   */
  closePropertiesDialog(): void {
    this.showPropertiesDialog.set(false);
  }

  /**
   * Aktualizuje pojedynczą właściwość w propertiesData
   */
  updateProperty(key: string, value: string): void {
    this.propertiesData.update(p => ({ ...p, [key]: value }));
  }

  // ===== PODPISY CYFROWE =====

  /**
   * Otwiera dialog podpisów cyfrowych
   */
  openSignatureDialog(): void {
    this.signatureDialogTab.set(
      this.documentSignatures().length > 0 ? 'list' : 'sign'
    );
    this.signatureData.signerName = '';
    this.signatureData.signerTitle = '';
    this.signatureData.signerEmail = '';
    this.signatureData.reason = '';
    this.signatureData.certificateBase64 = '';
    this.signatureData.certificatePassword = '';
    this.signatureData.certificateFileName = '';
    this.showSignatureDialog.set(true);
    this.closeAllMenus();
  }

  /**
   * Zamyka dialog podpisów
   */
  closeSignatureDialog(): void {
    this.showSignatureDialog.set(false);
  }

  /**
   * Obsługuje wybranie pliku certyfikatu PFX
   */
  onCertificateFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;

    this.signatureData.certificateFileName = file.name;

    const reader = new FileReader();
    reader.onload = () => {
      const arrayBuffer = reader.result as ArrayBuffer;
      const bytes = new Uint8Array(arrayBuffer);
      let binary = '';
      bytes.forEach(b => binary += String.fromCharCode(b));
      this.signatureData.certificateBase64 = btoa(binary);
    };
    reader.readAsArrayBuffer(file);
  }

  /**
   * Podpisuje dokument
   */
  signDocument(): void {
    if (!this.signatureData.certificateBase64) {
      this.showError('Wybierz plik certyfikatu (.pfx/.p12)');
      return;
    }
    if (!this.signatureData.signerName.trim()) {
      this.showError('Podaj imię i nazwisko podpisującego');
      return;
    }
    if (!this.signatureData.certificatePassword) {
      this.showError('Podaj hasło do certyfikatu');
      return;
    }

    const html = this.editor?.getContent() || this.documentContent();
    const fileName = this.originalFileName() || `${this.documentMetadata().title || 'dokument'}.docx`;

    this.isLoading.set(true);

    const request: SignDocumentRequest = {
      html,
      originalFileName: fileName,
      metadata: this.documentMetadata(),
      header: this.headerContent(),
      footer: this.footerContent(),
      certificateBase64: this.signatureData.certificateBase64,
      certificatePassword: this.signatureData.certificatePassword,
      signerName: this.signatureData.signerName,
      signerTitle: this.signatureData.signerTitle || undefined,
      signerEmail: this.signatureData.signerEmail || undefined,
      signatureReason: this.signatureData.reason || undefined
    };

    this.documentService.signDocument(request).subscribe({
      next: (blob) => {
        const url = window.URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = fileName.endsWith('.docx') ? fileName : `${fileName}.docx`;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        window.URL.revokeObjectURL(url);

        this.showSignatureDialog.set(false);
        this.showSuccess('Dokument został podpisany i pobrany');
        this.isLoading.set(false);
      },
      error: (err) => {
        this.showError(err.message || 'Nie udało się podpisać dokumentu');
        this.isLoading.set(false);
      }
    });
  }

  /**
   * Wstawia wizualny blok podpisu do dokumentu
   */
  insertSignatureLine(): void {
    const name = this.signatureData.signerName || '________________________';
    const title = this.signatureData.signerTitle || '';
    const date = new Date().toLocaleDateString('pl-PL');

    const html = `
      <div style="margin: 24px 0; padding: 16px; border: 1px solid #999; width: 300px; font-family: Calibri, sans-serif;">
        <div style="border-bottom: 1px solid #333; padding-bottom: 40px; margin-bottom: 8px; font-size: 10px; color: #999;">
          ✕ Podpis
        </div>
        <div style="font-size: 12px; font-weight: bold;">${name}</div>
        ${title ? `<div style="font-size: 11px; color: #555;">${title}</div>` : ''}
        <div style="font-size: 10px; color: #888; margin-top: 4px;">Data: ${date}</div>
      </div>
    `;

    this.editor?.insertHtml(html);
    this.showSignatureDialog.set(false);
    this.notifyEditorChange();
  }
}
